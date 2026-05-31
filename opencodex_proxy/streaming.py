from __future__ import annotations

import json
import time
import uuid
from collections.abc import Callable
from typing import Any, Iterable

from .patch_semantics import (
    ContentProgress,
    FileFinished,
    FileStarted,
    PatchFinished,
    PatchOp,
    PatchSemanticEvent,
    action_from_tool_name,
    semantic_events_from_operation,
    valid_preview_path,
)
from .protocols import (
    _is_apply_patch_tool_name,
    _normalize_annotations,
    _responses_apply_patch_item_from_tool_call,
)


def responses_sse_events(
    response_payload: dict[str, Any],
    skip_response_created: bool = False,
) -> Iterable[str]:
    sequence_number = 0

    def emit(event: str, payload: dict[str, Any]) -> str:
        nonlocal sequence_number
        enriched = {"type": event, **payload, "sequence_number": sequence_number}
        sequence_number += 1
        return _sse(event, enriched)

    created = {
        "response": {
            "id": response_payload.get("id"),
            "object": "response",
            "created_at": response_payload.get("created_at"),
            "status": "in_progress",
            "model": response_payload.get("model"),
            "output": [],
        },
    }
    if not skip_response_created:
        yield emit("response.created", created)

    for output_index, item in enumerate(response_payload.get("output", []) or []):
        in_progress = dict(item)
        in_progress["status"] = "in_progress"
        yield emit(
            "response.output_item.added",
            {"output_index": output_index, "item": in_progress},
        )
        yield emit(
            "response.output_item.done",
            {"output_index": output_index, "item": item},
        )

    completed_response = dict(response_payload)
    completed_response["end_turn"] = True
    completed_response["status"] = response_payload.get("status") or "completed"
    yield emit(
        "response.completed",
        {"response": completed_response},
    )


def messages_sse_to_responses_events(
    upstream_lines: Iterable[str],
    model: str | None = None,
    on_message: Callable[[dict[str, Any]], None] | None = None,
    skip_response_created: bool = False,
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
    sequence_number = 0

    def emit(event: str, payload: dict[str, Any]) -> str:
        nonlocal sequence_number
        enriched = {"type": event, **payload, "sequence_number": sequence_number}
        sequence_number += 1
        return _sse(event, enriched)

    if not skip_response_created:
        yield emit(
            "response.created",
            {
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
                    yield emit(
                        "response.output_item.added",
                        {
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
                    yield emit(
                        "response.content_part.added",
                        {
                            "item_id": message_item_id,
                            "output_index": 0,
                            "content_index": 0,
                            "part": {"type": "output_text", "text": ""},
                        },
                    )
                text_parts.append(text)
                block["text"] = str(block.get("text", "")) + text
                yield emit(
                    "response.output_text.delta",
                    {
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
        yield emit(
            "response.output_text.done",
            {
                "item_id": message_item_id,
                "output_index": 0,
                "content_index": 0,
                "text": text,
            },
        )
        yield emit(
            "response.content_part.done",
            {
                "item_id": message_item_id,
                "output_index": 0,
                "content_index": 0,
                "part": {"type": "output_text", "text": text},
            },
        )
        yield emit(
            "response.output_item.done",
            {"output_index": 0, "item": message_item},
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
        yield emit(
            "response.output_item.done",
            {
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
    yield emit(
        "response.completed",
        {"response": completed_response},
    )


def chat_sse_to_responses_events(
    upstream_lines: Iterable[str],
    model: str | None = None,
    on_response: Callable[[dict[str, Any]], None] | None = None,
    skip_tool_names: set[str] | None = None,
    skip_response_created: bool = False,
) -> Iterable[str]:
    response_id = f"resp_{uuid.uuid4().hex}"
    message_item_id = f"msg_{uuid.uuid4().hex}"
    reasoning_item_id = f"rs_{uuid.uuid4().hex}"
    created_at = int(time.time())
    response_model = model
    completion_id = None
    completion_created = None
    text_parts: list[str] = []
    reasoning_parts: list[str] = []
    annotations: list[dict[str, Any]] = []
    text_started = False
    reasoning_started = False
    reasoning_done = False
    usage: dict[str, Any] = {}
    finish_reason = "stop"
    tool_calls: dict[int, dict[str, Any]] = {}
    tool_stream_meta: dict[int, dict[str, Any]] = {}
    message_output_index: int | None = None
    reasoning_output_index: int | None = None
    next_output_index = 0
    sequence_number = 0
    output_by_index: dict[int, dict[str, Any]] = {}

    def emit(event: str, payload: dict[str, Any]) -> str:
        nonlocal sequence_number
        enriched = {"type": event, **payload, "sequence_number": sequence_number}
        sequence_number += 1
        return _sse(event, enriched)

    def allocate_output_index() -> int:
        nonlocal next_output_index
        output_index = next_output_index
        next_output_index += 1
        return output_index

    def ensure_reasoning_started() -> list[str]:
        nonlocal reasoning_started, reasoning_output_index
        if reasoning_started:
            return []
        reasoning_started = True
        reasoning_output_index = allocate_output_index()
        return [
            emit(
                "response.output_item.added",
                {
                    "output_index": reasoning_output_index,
                    "item": {
                        "id": reasoning_item_id,
                        "type": "reasoning",
                        "summary": [],
                        "encrypted_content": None,
                        "status": "in_progress",
                    },
                },
            ),
            emit(
                "response.reasoning_summary_part.added",
                {
                    "item_id": reasoning_item_id,
                    "output_index": reasoning_output_index,
                    "summary_index": 0,
                    "part": {"type": "summary_text", "text": ""},
                },
            ),
        ]

    def finalize_reasoning() -> list[str]:
        nonlocal reasoning_done
        if not reasoning_started or reasoning_done or reasoning_output_index is None:
            return []
        reasoning_done = True
        reasoning_text = "".join(reasoning_parts)
        reasoning_item = {
            "id": reasoning_item_id,
            "type": "reasoning",
            "status": "completed",
            "summary": [{"type": "summary_text", "text": reasoning_text}],
            "encrypted_content": reasoning_text,
        }
        output_by_index[reasoning_output_index] = reasoning_item
        return [
            emit(
                "response.reasoning_summary_text.done",
                {
                    "item_id": reasoning_item_id,
                    "output_index": reasoning_output_index,
                    "summary_index": 0,
                    "text": reasoning_text,
                },
            ),
            emit(
                "response.reasoning_summary_part.done",
                {
                    "item_id": reasoning_item_id,
                    "output_index": reasoning_output_index,
                    "summary_index": 0,
                    "part": {"type": "summary_text", "text": reasoning_text},
                },
            ),
            emit(
                "response.output_item.done",
                {"output_index": reasoning_output_index, "item": reasoning_item},
            ),
        ]

    def ensure_message_started() -> list[str]:
        nonlocal text_started, message_output_index
        if text_started:
            return []
        events = finalize_reasoning()
        text_started = True
        message_output_index = allocate_output_index()
        events.extend(
            [
                emit(
                    "response.output_item.added",
                    {
                        "output_index": message_output_index,
                        "item": {
                            "id": message_item_id,
                            "type": "message",
                            "status": "in_progress",
                            "role": "assistant",
                            "content": [],
                        },
                    },
                ),
                emit(
                    "response.content_part.added",
                    {
                        "item_id": message_item_id,
                        "output_index": message_output_index,
                        "content_index": 0,
                        "part": {"type": "output_text", "text": "", "annotations": []},
                    },
                ),
            ]
        )
        return events

    def ensure_patch_preview(index: int, call_id: Any) -> dict[str, Any]:
        meta = tool_stream_meta.setdefault(index, {})
        if "patch_preview_id" not in meta:
            meta["patch_preview_id"] = f"patchprev_{uuid.uuid4().hex}"
        if "patch_preview_source" not in meta:
            meta["patch_preview_source"] = {
                "type": "tool",
                "id": str(call_id or ""),
            }
        return meta

    def emit_patch_preview(
        index: int,
        call_id: Any,
        semantic_event: PatchSemanticEvent,
    ) -> str:
        meta = ensure_patch_preview(index, call_id)
        payload: dict[str, Any] = {
            "preview_id": meta["patch_preview_id"],
            "source": meta["patch_preview_source"],
        }
        if isinstance(semantic_event, FileStarted):
            payload.update(
                {
                    "event": "file_started",
                    "path": semantic_event.path,
                    "op": semantic_event.op.type,
                }
            )
        elif isinstance(semantic_event, ContentProgress):
            payload.update(
                {
                    "event": "content_progress",
                    "path": semantic_event.path,
                    "chars": semantic_event.chars,
                }
            )
        elif isinstance(semantic_event, FileFinished):
            payload.update({"event": "file_finished", "path": semantic_event.path})
        elif isinstance(semantic_event, PatchFinished):
            payload.update({"event": "patch_finished"})
        return emit("patch.semantic_preview", payload)

    def patch_preview_events_for_tool_call(
        index: int,
        call_id: Any,
        tool_name: str,
        arguments: str,
    ) -> list[str]:
        normalized = tool_name.replace("-", "_")
        meta = tool_stream_meta.setdefault(index, {})
        if normalized == "apply_patch":
            return []
        if normalized == "apply_patch_batch":
            return _batch_patch_preview_events(index, call_id, arguments, meta)
        action = action_from_tool_name(normalized)
        if action is None:
            return []
        path = _complete_json_string_field(arguments, "path")
        path = valid_preview_path(path)
        if path is None:
            return []
        meta["patch_preview_path"] = path
        events: list[str] = []
        if not meta.get("patch_preview_file_started"):
            meta["patch_preview_file_started"] = True
            events.append(
                emit_patch_preview(index, call_id, FileStarted(path, PatchOp(action, path)))
            )
        if action in {"add", "replace"}:
            chars = _partial_json_string_field_length(arguments, "content")
            streamed_chars = int(meta.get("patch_preview_content_chars") or 0)
            if chars is not None and chars > streamed_chars:
                meta["patch_preview_content_chars"] = chars
                events.append(
                    emit_patch_preview(index, call_id, ContentProgress(path, chars))
                )
        return events

    def _batch_patch_preview_events(
        index: int,
        call_id: Any,
        arguments: str,
        meta: dict[str, Any],
    ) -> list[str]:
        objects = _complete_operation_objects(arguments)
        emitted = int(meta.get("patch_preview_batch_emitted") or 0)
        events: list[str] = []
        for operation in objects[emitted:]:
            for semantic_event in semantic_events_from_operation(operation):
                events.append(emit_patch_preview(index, call_id, semantic_event))
        if len(objects) > emitted:
            meta["patch_preview_batch_emitted"] = len(objects)
        return events

    if not skip_response_created:
        yield emit(
            "response.created",
            {
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

            reasoning_text = delta.get("reasoning_content")
            if isinstance(reasoning_text, str) and reasoning_text:
                for line in ensure_reasoning_started():
                    yield line
                reasoning_parts.append(reasoning_text)
                yield emit(
                    "response.reasoning_summary_text.delta",
                    {
                        "item_id": reasoning_item_id,
                        "output_index": reasoning_output_index,
                        "summary_index": 0,
                        "delta": reasoning_text,
                    },
                )

            text = delta.get("content")
            if isinstance(text, str) and text:
                for line in ensure_message_started():
                    yield line
                text_parts.append(text)
                yield emit(
                    "response.output_text.delta",
                    {
                        "item_id": message_item_id,
                        "output_index": message_output_index,
                        "content_index": 0,
                        "delta": text,
                    },
                )

            new_annotations = _normalize_annotations(delta.get("annotations"))
            if new_annotations:
                for line in ensure_message_started():
                    yield line
                for annotation in new_annotations:
                    annotation_index = len(annotations)
                    annotations.append(annotation)
                    yield emit(
                        "response.output_text.annotation.added",
                        {
                            "item_id": message_item_id,
                            "output_index": message_output_index,
                            "content_index": 0,
                            "annotation_index": annotation_index,
                            "annotation": annotation,
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
                tool_name = target.get("function", {}).get("name")
                if (
                    target.get("id")
                    and tool_name
                    and _is_apply_patch_tool_name(str(tool_name))
                ):
                    arguments = str(target.get("function", {}).get("arguments") or "")
                    for line in patch_preview_events_for_tool_call(
                        index,
                        target["id"],
                        str(tool_name),
                        arguments,
                    ):
                        yield line
                    continue
                if not target.get("id") or not tool_name:
                    continue
                if skip_tool_names and str(tool_name) in skip_tool_names:
                    continue
                meta = tool_stream_meta.setdefault(index, {})
                if "output_index" not in meta:
                    meta["output_index"] = allocate_output_index()
                if "item_id" not in meta:
                    meta["item_id"] = f"fc_{uuid.uuid4().hex}"
                if not meta.get("item_added"):
                    meta["item_added"] = True
                    for line in finalize_reasoning():
                        yield line
                    yield emit(
                        "response.output_item.added",
                        {
                            "output_index": meta["output_index"],
                            "item": {
                                "id": meta["item_id"],
                                "type": "function_call",
                                "status": "in_progress",
                                "call_id": target["id"],
                                "name": tool_name,
                                "arguments": "",
                            },
                        },
                    )
                arguments = str(target.get("function", {}).get("arguments") or "")
                streamed_length = int(meta.get("streamed_arguments_length") or 0)
                if len(arguments) > streamed_length:
                    delta_text = arguments[streamed_length:]
                    meta["streamed_arguments_length"] = len(arguments)
                    yield emit(
                        "response.function_call_arguments.delta",
                        {
                            "item_id": meta["item_id"],
                            "output_index": meta["output_index"],
                            "delta": delta_text,
                        },
                    )

    for line in finalize_reasoning():
        yield line

    reasoning_text = "".join(reasoning_parts)
    message = {
        "role": "assistant",
        "content": "".join(text_parts),
        "tool_calls": [],
    }
    if reasoning_text:
        message["reasoning_content"] = reasoning_text
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

    text = "".join(text_parts)
    if text or annotations:
        if message_output_index is None:
            message_output_index = allocate_output_index()
        output_text = {"type": "output_text", "text": text}
        if annotations:
            output_text["annotations"] = annotations
        message_item = {
            "id": message_item_id,
            "type": "message",
            "status": "completed",
            "role": "assistant",
            "content": [output_text],
        }
        yield emit(
            "response.output_text.done",
            {
                "item_id": message_item_id,
                "output_index": message_output_index,
                "content_index": 0,
                "text": text,
            },
        )
        yield emit(
            "response.content_part.done",
            {
                "item_id": message_item_id,
                "output_index": message_output_index,
                "content_index": 0,
                "part": output_text,
            },
        )
        output_by_index[message_output_index] = message_item
        yield emit(
            "response.output_item.done",
            {
                "output_index": message_output_index,
                "item": message_item,
            },
        )

    for index, tool_call in zip(sorted(tool_calls), message["tool_calls"]):
        tool_name = tool_call["function"].get("name")
        if skip_tool_names and str(tool_name) in skip_tool_names:
            continue
        meta = tool_stream_meta.get(index, {})
        item = _responses_tool_call_item(
            tool_call["id"],
            tool_name,
            tool_call["function"].get("arguments", "{}"),
            meta.get("item_id"),
        )
        if "output_index" in meta:
            output_index = int(meta["output_index"])
        else:
            while next_output_index in output_by_index:
                next_output_index += 1
            output_index = allocate_output_index()
        output_by_index[output_index] = item
        if not meta.get("item_added") and item.get("type") == "function_call":
            yield emit(
                "response.output_item.added",
                {
                    "output_index": output_index,
                    "item": {
                        "id": item["id"],
                        "type": "function_call",
                        "status": "in_progress",
                        "call_id": item.get("call_id"),
                        "name": item.get("name"),
                        "arguments": "",
                    },
                },
            )
        if item.get("type") == "function_call":
            yield emit(
                "response.function_call_arguments.done",
                {
                    "item_id": item["id"],
                    "output_index": output_index,
                    "arguments": item.get("arguments", ""),
                },
            )
        elif _is_apply_patch_tool_name(str(tool_name)) and meta.get("patch_preview_id"):
            if not meta.get("patch_preview_file_finished") and meta.get("patch_preview_path"):
                meta["patch_preview_file_finished"] = True
                yield emit_patch_preview(
                    index,
                    tool_call["id"],
                    FileFinished(str(meta["patch_preview_path"])),
                )
            yield emit_patch_preview(index, tool_call["id"], PatchFinished())
        yield emit(
            "response.output_item.done",
            {
                "output_index": output_index,
                "item": item,
            },
        )

    incomplete = finish_reason == "length"
    completed_response = {
        "id": response_id,
        "object": "response",
        "created_at": created_at,
        "status": "incomplete" if incomplete else "completed",
        "model": response_model,
        "output": [output_by_index[index] for index in sorted(output_by_index)],
        "usage": _chat_usage_to_responses_usage(usage),
        "end_turn": True,
    }
    if incomplete:
        completed_response["incomplete_details"] = {"reason": "max_output_tokens"}
    yield emit(
        "response.completed",
        {"response": completed_response},
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


def _complete_json_string_field(source: str, key: str) -> str | None:
    parsed = _json_string_field_state(source, key)
    if parsed is None:
        return None
    value, complete = parsed
    return value if complete else None


def _partial_json_string_field_length(source: str, key: str) -> int | None:
    parsed = _json_string_field_state(source, key)
    if parsed is None:
        return None
    value, _complete = parsed
    return len(value)


def _json_string_field_state(source: str, key: str) -> tuple[str, bool] | None:
    pattern = json.dumps(key, ensure_ascii=False)
    index = source.find(pattern)
    if index < 0:
        return None
    index += len(pattern)
    while index < len(source) and source[index].isspace():
        index += 1
    if index >= len(source) or source[index] != ":":
        return None
    index += 1
    while index < len(source) and source[index].isspace():
        index += 1
    if index >= len(source) or source[index] != '"':
        return None
    return _scan_json_string_value(source, index)


def _scan_json_string_value(source: str, quote_index: int) -> tuple[str, bool]:
    result: list[str] = []
    index = quote_index + 1
    while index < len(source):
        char = source[index]
        if char == '"':
            return "".join(result), True
        if char == "\\":
            if index + 1 >= len(source):
                return "".join(result), False
            escape = source[index + 1]
            if escape == "u":
                digits = source[index + 2 : index + 6]
                if len(digits) < 4 or any(c not in "0123456789abcdefABCDEF" for c in digits):
                    return "".join(result), False
                result.append(chr(int(digits, 16)))
                index += 6
                continue
            result.append(
                {
                    '"': '"',
                    "\\": "\\",
                    "/": "/",
                    "b": "\b",
                    "f": "\f",
                    "n": "\n",
                    "r": "\r",
                    "t": "\t",
                }.get(escape, escape)
            )
            index += 2
            continue
        result.append(char)
        index += 1
    return "".join(result), False


def _complete_operation_objects(source: str) -> list[dict[str, Any]]:
    array_start = source.find(json.dumps("operations"))
    if array_start < 0:
        return []
    bracket = source.find("[", array_start)
    if bracket < 0:
        return []
    objects: list[dict[str, Any]] = []
    index = bracket + 1
    while index < len(source):
        while index < len(source) and source[index] in " \t\r\n,":
            index += 1
        if index >= len(source) or source[index] != "{":
            break
        end = _json_object_end(source, index)
        if end is None:
            break
        try:
            value = json.loads(source[index:end])
        except json.JSONDecodeError:
            break
        if isinstance(value, dict):
            objects.append(value)
        index = end
    return objects


def _json_object_end(source: str, start: int) -> int | None:
    depth = 0
    in_string = False
    escape = False
    for index in range(start, len(source)):
        char = source[index]
        if in_string:
            if escape:
                escape = False
            elif char == "\\":
                escape = True
            elif char == '"':
                in_string = False
            continue
        if char == '"':
            in_string = True
        elif char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                return index + 1
    return None


def _json_dumps(value: Any) -> str:
    if isinstance(value, str):
        return value
    return json.dumps(value, ensure_ascii=False)


def _responses_tool_call_item(
    call_id: Any,
    name: Any,
    arguments: Any,
    item_id: Any | None = None,
) -> dict[str, Any]:
    tool_name = str(name or "")
    if _is_apply_patch_tool_name(tool_name):
        return _responses_apply_patch_item_from_tool_call(call_id, tool_name, arguments, item_id)
    return {
        "id": item_id or f"fc_{uuid.uuid4().hex}",
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
