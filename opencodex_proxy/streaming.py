from __future__ import annotations

import json
import time
import uuid
from collections.abc import Callable
from typing import Any, Iterable

from .protocols import _apply_patch_input_from_arguments, _is_apply_patch_name


def responses_sse_events(response_payload: dict[str, Any]) -> Iterable[str]:
    created = {
        "type": "response.created",
        "response": {
            "id": response_payload.get("id"),
            "object": "response",
            "created_at": response_payload.get("created_at"),
            "status": "in_progress",
            "model": response_payload.get("model"),
            "output": [],
        },
    }
    yield _sse("response.created", created)

    for item in response_payload.get("output", []) or []:
        yield _sse("response.output_item.done", {"type": "response.output_item.done", "item": item})

    completed_response = dict(response_payload)
    completed_response["end_turn"] = True
    completed_response["status"] = "completed"
    yield _sse(
        "response.completed",
        {"type": "response.completed", "response": completed_response},
    )


def messages_sse_to_responses_events(
    upstream_lines: Iterable[str],
    model: str | None = None,
    on_message: Callable[[dict[str, Any]], None] | None = None,
) -> Iterable[str]:
    response_id = f"resp_{uuid.uuid4().hex}"
    message_item_id = f"msg_{uuid.uuid4().hex}"
    created_at = int(time.time())
    response_model = model
    text_parts: list[str] = []
    content_blocks: dict[int, dict[str, Any]] = {}
    input_json_parts: dict[int, list[str]] = {}
    stop_reason = "stop"
    usage: dict[str, Any] = {}
    text_started = False

    yield _sse(
        "response.created",
        {
            "type": "response.created",
            "response": {
                "id": response_id,
                "object": "response",
                "created_at": created_at,
                "status": "in_progress",
                "model": response_model,
                "output": [],
            },
        },
    )

    for event in _iter_sse_events(upstream_lines):
        payload = event.get("data")
        if not isinstance(payload, dict):
            continue
        event_type = payload.get("type") or event.get("event")
        if event_type == "message_start":
            message = payload.get("message") or {}
            if isinstance(message, dict):
                response_model = model or message.get("model")
                usage = dict(message.get("usage") or {})
            continue
        if event_type == "content_block_start":
            index = int(payload.get("index") or 0)
            block = payload.get("content_block") or {}
            if isinstance(block, dict):
                content_blocks[index] = dict(block)
                if block.get("type") == "text" and block.get("text"):
                    text_parts.append(str(block.get("text")))
            continue
        if event_type == "content_block_delta":
            index = int(payload.get("index") or 0)
            delta = payload.get("delta") or {}
            if not isinstance(delta, dict):
                continue
            block = content_blocks.setdefault(index, {"type": "text", "text": ""})
            delta_type = delta.get("type")
            if delta_type == "text_delta":
                text = str(delta.get("text", ""))
                if not text:
                    continue
                if not text_started:
                    text_started = True
                    yield _sse(
                        "response.output_item.added",
                        {
                            "type": "response.output_item.added",
                            "output_index": 0,
                            "item": {
                                "id": message_item_id,
                                "type": "message",
                                "status": "in_progress",
                                "role": "assistant",
                                "content": [],
                            },
                        },
                    )
                    yield _sse(
                        "response.content_part.added",
                        {
                            "type": "response.content_part.added",
                            "item_id": message_item_id,
                            "output_index": 0,
                            "content_index": 0,
                            "part": {"type": "output_text", "text": ""},
                        },
                    )
                text_parts.append(text)
                block["text"] = str(block.get("text", "")) + text
                yield _sse(
                    "response.output_text.delta",
                    {
                        "type": "response.output_text.delta",
                        "item_id": message_item_id,
                        "output_index": 0,
                        "content_index": 0,
                        "delta": text,
                    },
                )
            elif delta_type == "input_json_delta":
                input_json_parts.setdefault(index, []).append(str(delta.get("partial_json", "")))
            elif delta_type in {"thinking_delta", "signature_delta"}:
                key = "thinking" if delta_type == "thinking_delta" else "signature"
                block["type"] = "thinking"
                block[key] = str(block.get(key, "")) + str(delta.get(key, ""))
            continue
        if event_type == "message_delta":
            delta = payload.get("delta") or {}
            if isinstance(delta, dict) and delta.get("stop_reason"):
                stop_reason = str(delta.get("stop_reason"))
            if isinstance(payload.get("usage"), dict):
                usage.update(payload["usage"])
            continue
        if event_type == "message_stop":
            break

    for index, parts in input_json_parts.items():
        block = content_blocks.setdefault(index, {"type": "tool_use"})
        block["input"] = _parse_json_object("".join(parts))

    ordered_blocks = [content_blocks[index] for index in sorted(content_blocks)]
    upstream_message = {
        "id": f"msg_{uuid.uuid4().hex}",
        "type": "message",
        "role": "assistant",
        "model": response_model,
        "content": ordered_blocks,
        "stop_reason": stop_reason,
        "usage": usage,
    }
    if on_message is not None:
        on_message(upstream_message)

    output = []
    text = "".join(text_parts)
    if text:
        message_item = {
            "id": message_item_id,
            "type": "message",
            "status": "completed",
            "role": "assistant",
            "content": [{"type": "output_text", "text": text}],
        }
        output.append(message_item)
        yield _sse(
            "response.output_text.done",
            {
                "type": "response.output_text.done",
                "item_id": message_item_id,
                "output_index": 0,
                "content_index": 0,
                "text": text,
            },
        )
        yield _sse(
            "response.content_part.done",
            {
                "type": "response.content_part.done",
                "item_id": message_item_id,
                "output_index": 0,
                "content_index": 0,
                "part": {"type": "output_text", "text": text},
            },
        )
        yield _sse(
            "response.output_item.done",
            {"type": "response.output_item.done", "output_index": 0, "item": message_item},
        )

    for block in ordered_blocks:
        if block.get("type") != "tool_use":
            continue
        item = _responses_tool_call_item(
            block.get("id"),
            block.get("name"),
            block.get("input") or {},
        )
        output.append(item)
        yield _sse(
            "response.output_item.done",
            {
                "type": "response.output_item.done",
                "output_index": len(output) - 1,
                "item": item,
            },
        )

    completed_response = {
        "id": response_id,
        "object": "response",
        "created_at": created_at,
        "status": "completed",
        "model": response_model,
        "output": output,
        "usage": _messages_usage_to_responses_usage(usage),
        "end_turn": True,
    }
    yield _sse(
        "response.completed",
        {"type": "response.completed", "response": completed_response},
    )


def chat_sse_to_responses_events(
    upstream_lines: Iterable[str],
    model: str | None = None,
    on_response: Callable[[dict[str, Any]], None] | None = None,
) -> Iterable[str]:
    response_id = f"resp_{uuid.uuid4().hex}"
    message_item_id = f"msg_{uuid.uuid4().hex}"
    created_at = int(time.time())
    response_model = model
    completion_id = None
    completion_created = None
    text_parts: list[str] = []
    text_started = False
    usage: dict[str, Any] = {}
    finish_reason = "stop"
    tool_calls: dict[int, dict[str, Any]] = {}

    yield _sse(
        "response.created",
        {
            "type": "response.created",
            "response": {
                "id": response_id,
                "object": "response",
                "created_at": created_at,
                "status": "in_progress",
                "model": response_model,
                "output": [],
            },
        },
    )

    for event in _iter_sse_events(upstream_lines):
        payload = event.get("data")
        if payload == "[DONE]":
            break
        if not isinstance(payload, dict):
            continue
        completion_id = payload.get("id") or completion_id
        completion_created = payload.get("created") or completion_created
        response_model = model or payload.get("model") or response_model
        if isinstance(payload.get("usage"), dict):
            usage.update(payload["usage"])

        choices = payload.get("choices")
        if not isinstance(choices, list):
            continue
        for choice in choices:
            if not isinstance(choice, dict):
                continue
            delta = choice.get("delta") or {}
            if not isinstance(delta, dict):
                delta = {}
            if choice.get("finish_reason"):
                finish_reason = str(choice["finish_reason"])

            text = delta.get("content")
            if isinstance(text, str) and text:
                if not text_started:
                    text_started = True
                    yield _sse(
                        "response.output_item.added",
                        {
                            "type": "response.output_item.added",
                            "output_index": 0,
                            "item": {
                                "id": message_item_id,
                                "type": "message",
                                "status": "in_progress",
                                "role": "assistant",
                                "content": [],
                            },
                        },
                    )
                    yield _sse(
                        "response.content_part.added",
                        {
                            "type": "response.content_part.added",
                            "item_id": message_item_id,
                            "output_index": 0,
                            "content_index": 0,
                            "part": {"type": "output_text", "text": ""},
                        },
                    )
                text_parts.append(text)
                yield _sse(
                    "response.output_text.delta",
                    {
                        "type": "response.output_text.delta",
                        "item_id": message_item_id,
                        "output_index": 0,
                        "content_index": 0,
                        "delta": text,
                    },
                )

            for tool_call in delta.get("tool_calls", []) or []:
                if not isinstance(tool_call, dict):
                    continue
                index = int(tool_call.get("index") or 0)
                target = tool_calls.setdefault(
                    index,
                    {
                        "id": None,
                        "type": "function",
                        "function": {"name": None, "arguments": ""},
                    },
                )
                if tool_call.get("id"):
                    target["id"] = tool_call["id"]
                if tool_call.get("type"):
                    target["type"] = tool_call["type"]
                function = tool_call.get("function") or {}
                if isinstance(function, dict):
                    if function.get("name"):
                        target["function"]["name"] = function["name"]
                    if function.get("arguments"):
                        target["function"]["arguments"] += str(function["arguments"])

    message = {
        "role": "assistant",
        "content": "".join(text_parts),
        "tool_calls": [],
    }
    for index in sorted(tool_calls):
        tool_call = tool_calls[index]
        message["tool_calls"].append(
            {
                "id": tool_call.get("id") or f"call_{uuid.uuid4().hex}",
                "type": tool_call.get("type") or "function",
                "function": {
                    "name": tool_call.get("function", {}).get("name"),
                    "arguments": tool_call.get("function", {}).get("arguments", "{}"),
                },
            }
        )

    upstream_response = {
        "id": completion_id or f"chatcmpl_{uuid.uuid4().hex}",
        "object": "chat.completion",
        "created": completion_created or int(time.time()),
        "model": response_model,
        "choices": [
            {
                "index": 0,
                "message": message,
                "finish_reason": finish_reason,
            }
        ],
        "usage": usage,
    }
    if on_response is not None:
        on_response(upstream_response)

    output = []
    text = "".join(text_parts)
    if text:
        message_item = {
            "id": message_item_id,
            "type": "message",
            "status": "completed",
            "role": "assistant",
            "content": [{"type": "output_text", "text": text}],
        }
        output.append(message_item)
        yield _sse(
            "response.output_text.done",
            {
                "type": "response.output_text.done",
                "item_id": message_item_id,
                "output_index": 0,
                "content_index": 0,
                "text": text,
            },
        )
        yield _sse(
            "response.content_part.done",
            {
                "type": "response.content_part.done",
                "item_id": message_item_id,
                "output_index": 0,
                "content_index": 0,
                "part": {"type": "output_text", "text": text},
            },
        )
        yield _sse(
            "response.output_item.done",
            {"type": "response.output_item.done", "output_index": 0, "item": message_item},
        )

    for tool_call in message["tool_calls"]:
        item = _responses_tool_call_item(
            tool_call["id"],
            tool_call["function"].get("name"),
            tool_call["function"].get("arguments", "{}"),
        )
        output.append(item)
        yield _sse(
            "response.output_item.done",
            {
                "type": "response.output_item.done",
                "output_index": len(output) - 1,
                "item": item,
            },
        )

    completed_response = {
        "id": response_id,
        "object": "response",
        "created_at": created_at,
        "status": "completed",
        "model": response_model,
        "output": output,
        "usage": _chat_usage_to_responses_usage(usage),
        "end_turn": True,
    }
    yield _sse(
        "response.completed",
        {"type": "response.completed", "response": completed_response},
    )


def _sse(event: str, payload: dict[str, Any]) -> str:
    data = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    return f"event: {event}\ndata: {data}\n\n"


def _iter_sse_events(lines: Iterable[str]) -> Iterable[dict[str, Any]]:
    event_name = "message"
    data_lines: list[str] = []
    for raw_line in lines:
        line = raw_line.rstrip("\r\n")
        if not line:
            if data_lines:
                data_text = "\n".join(data_lines)
                try:
                    data: Any = json.loads(data_text)
                except json.JSONDecodeError:
                    data = data_text
                yield {"event": event_name, "data": data}
            event_name = "message"
            data_lines = []
            continue
        if line.startswith(":"):
            continue
        if line.startswith("event:"):
            event_name = line.split(":", 1)[1].strip()
        elif line.startswith("data:"):
            data_lines.append(line.split(":", 1)[1].lstrip())
    if data_lines:
        data_text = "\n".join(data_lines)
        try:
            data = json.loads(data_text)
        except json.JSONDecodeError:
            data = data_text
        yield {"event": event_name, "data": data}


def _parse_json_object(value: Any) -> dict[str, Any]:
    if isinstance(value, dict):
        return value
    if not isinstance(value, str):
        return {}
    try:
        parsed = json.loads(value)
    except json.JSONDecodeError:
        return {"input": value}
    return parsed if isinstance(parsed, dict) else {"input": parsed}


def _json_dumps(value: Any) -> str:
    if isinstance(value, str):
        return value
    return json.dumps(value, ensure_ascii=False)


def _responses_tool_call_item(call_id: Any, name: Any, arguments: Any) -> dict[str, Any]:
    tool_name = str(name or "")
    if _is_apply_patch_name(tool_name):
        return {
            "id": f"ctc_{uuid.uuid4().hex}",
            "type": "custom_tool_call",
            "status": "completed",
            "call_id": call_id,
            "name": "apply_patch",
            "input": _apply_patch_input_from_arguments(arguments),
        }
    return {
        "id": f"fc_{uuid.uuid4().hex}",
        "type": "function_call",
        "status": "completed",
        "call_id": call_id,
        "name": tool_name,
        "arguments": _json_dumps(arguments),
    }


def _messages_usage_to_responses_usage(usage: dict[str, Any]) -> dict[str, int]:
    input_tokens = int(usage.get("input_tokens") or 0)
    output_tokens = int(usage.get("output_tokens") or 0)
    return {
        "input_tokens": input_tokens,
        "output_tokens": output_tokens,
        "total_tokens": input_tokens + output_tokens,
    }


def _chat_usage_to_responses_usage(usage: dict[str, Any]) -> dict[str, int]:
    input_tokens = int(usage.get("prompt_tokens") or 0)
    output_tokens = int(usage.get("completion_tokens") or 0)
    return {
        "input_tokens": input_tokens,
        "output_tokens": output_tokens,
        "total_tokens": int(usage.get("total_tokens") or (input_tokens + output_tokens)),
    }
