from __future__ import annotations

import json
import time
import uuid
from copy import deepcopy
from typing import Any

from .errors import BadRequestError


PROTOCOL_RESPONSES = "responses"
PROTOCOL_CHAT = "chat"
PROTOCOL_MESSAGES = "messages"


RESPONSES_TOOL_CALL_TYPES = {
    "function_call",
    "custom_tool_call",
    "local_shell_call",
    "shell_call",
    "apply_patch_call",
}
RESPONSES_TOOL_OUTPUT_TYPES = {
    "function_call_output",
    "custom_tool_call_output",
    "local_shell_call_output",
    "shell_call_output",
    "apply_patch_call_output",
    "tool_result",
}


def convert_request(
    payload: dict[str, Any],
    source_protocol: str,
    target_protocol: str,
    upstream_model: str,
) -> dict[str, Any]:
    payload = deepcopy(payload)
    payload["model"] = upstream_model
    if source_protocol == target_protocol:
        return payload
    canonical = to_canonical_request(payload, source_protocol)
    return from_canonical_request(canonical, target_protocol)


def convert_response(
    payload: dict[str, Any],
    source_protocol: str,
    target_protocol: str,
    original_model: str | None,
) -> dict[str, Any]:
    if source_protocol == target_protocol:
        result = deepcopy(payload)
        if original_model:
            result["model"] = original_model
        return result
    canonical = to_canonical_response(payload, target_protocol, original_model)
    return from_canonical_response(canonical, source_protocol)


def to_canonical_request(payload: dict[str, Any], protocol: str) -> dict[str, Any]:
    if protocol == PROTOCOL_RESPONSES:
        return _responses_request_to_canonical(payload)
    if protocol == PROTOCOL_CHAT:
        return _chat_request_to_canonical(payload)
    if protocol == PROTOCOL_MESSAGES:
        return _messages_request_to_canonical(payload)
    raise BadRequestError(f"unsupported source protocol: {protocol}")


def from_canonical_request(canonical: dict[str, Any], protocol: str) -> dict[str, Any]:
    if protocol == PROTOCOL_RESPONSES:
        return _canonical_to_responses_request(canonical)
    if protocol == PROTOCOL_CHAT:
        return _canonical_to_chat_request(canonical)
    if protocol == PROTOCOL_MESSAGES:
        return _canonical_to_messages_request(canonical)
    raise BadRequestError(f"unsupported target protocol: {protocol}")


def to_canonical_response(
    payload: dict[str, Any], protocol: str, original_model: str | None
) -> dict[str, Any]:
    if protocol == PROTOCOL_RESPONSES:
        return _responses_response_to_canonical(payload, original_model)
    if protocol == PROTOCOL_CHAT:
        return _chat_response_to_canonical(payload, original_model)
    if protocol == PROTOCOL_MESSAGES:
        return _messages_response_to_canonical(payload, original_model)
    raise BadRequestError(f"unsupported upstream protocol: {protocol}")


def from_canonical_response(canonical: dict[str, Any], protocol: str) -> dict[str, Any]:
    if protocol == PROTOCOL_RESPONSES:
        return _canonical_to_responses_response(canonical)
    if protocol == PROTOCOL_CHAT:
        return _canonical_to_chat_response(canonical)
    if protocol == PROTOCOL_MESSAGES:
        return _canonical_to_messages_response(canonical)
    raise BadRequestError(f"unsupported response protocol: {protocol}")


def _responses_request_to_canonical(payload: dict[str, Any]) -> dict[str, Any]:
    messages: list[dict[str, Any]] = []
    instructions = payload.get("instructions")
    if instructions:
        messages.append({"role": "system", "content": _stringify_content(instructions)})

    raw_input = payload.get("input", [])
    if isinstance(raw_input, str):
        messages.append({"role": "user", "content": raw_input})
    elif isinstance(raw_input, list):
        for item in raw_input:
            messages.extend(_responses_input_item_to_messages(item))
    else:
        raise BadRequestError("responses input must be a string or list")
    messages = _merge_consecutive_assistant_tool_call_messages(messages)

    return {
        "model": payload.get("model"),
        "messages": messages,
        "tools": _responses_tools_to_canonical(payload.get("tools", [])),
        "tool_choice": payload.get("tool_choice"),
        "params": _copy_common_request_params(payload, "responses"),
    }


def _chat_request_to_canonical(payload: dict[str, Any]) -> dict[str, Any]:
    messages = []
    for item in payload.get("messages", []) or []:
        if not isinstance(item, dict):
            continue
        messages.append(deepcopy(item))
    return {
        "model": payload.get("model"),
        "messages": messages,
        "tools": _chat_tools_to_canonical(payload.get("tools", [])),
        "tool_choice": payload.get("tool_choice"),
        "params": _copy_common_request_params(payload, "chat"),
    }


def _messages_request_to_canonical(payload: dict[str, Any]) -> dict[str, Any]:
    messages: list[dict[str, Any]] = []
    system = payload.get("system")
    if system:
        messages.append({"role": "system", "content": _stringify_content(system)})
    for item in payload.get("messages", []) or []:
        if not isinstance(item, dict):
            continue
        role = item.get("role", "user")
        content = _anthropic_content_to_chat_content(item.get("content", ""))
        messages.append({"role": role, "content": content})
    return {
        "model": payload.get("model"),
        "messages": messages,
        "tools": _anthropic_tools_to_canonical(payload.get("tools", [])),
        "tool_choice": payload.get("tool_choice"),
        "params": _copy_common_request_params(payload, "messages"),
    }


def _canonical_to_responses_request(canonical: dict[str, Any]) -> dict[str, Any]:
    params = deepcopy(canonical.get("params", {}))
    result = {"model": canonical.get("model"), **params}
    instructions, input_items = _messages_to_responses_input(canonical.get("messages", []))
    if instructions:
        result["instructions"] = instructions
    result["input"] = input_items
    tools = _canonical_tools_to_responses(canonical.get("tools", []))
    if tools:
        result["tools"] = tools
    if canonical.get("tool_choice") is not None:
        result["tool_choice"] = canonical["tool_choice"]
    if "max_tokens" in result and "max_output_tokens" not in result:
        result["max_output_tokens"] = result.pop("max_tokens")
    return result


def _canonical_to_chat_request(canonical: dict[str, Any]) -> dict[str, Any]:
    params = deepcopy(canonical.get("params", {}))
    result = {"model": canonical.get("model"), "messages": [], **params}
    for message in canonical.get("messages", []):
        role = message.get("role", "user")
        if role == "developer":
            role = "system"
        result["messages"].append(
            {
                key: deepcopy(value)
                for key, value in message.items()
                if key in {"role", "content", "tool_calls", "tool_call_id", "name"}
            }
        )
        result["messages"][-1]["role"] = role
    tools = _canonical_tools_to_chat(canonical.get("tools", []))
    if tools:
        result["tools"] = tools
    if canonical.get("tool_choice") is not None:
        result["tool_choice"] = canonical["tool_choice"]
    if "max_output_tokens" in result and "max_tokens" not in result:
        result["max_tokens"] = result.pop("max_output_tokens")
    return result


def _canonical_to_messages_request(canonical: dict[str, Any]) -> dict[str, Any]:
    params = deepcopy(canonical.get("params", {}))
    result = {"model": canonical.get("model"), "messages": [], **params}
    system_parts: list[str] = []
    for message in canonical.get("messages", []):
        role = message.get("role", "user")
        if role in {"system", "developer"}:
            text = _stringify_content(message.get("content", ""))
            if text:
                system_parts.append(text)
            continue
        if role == "tool":
            result["messages"].append(
                {
                    "role": "user",
                    "content": [
                        {
                            "type": "tool_result",
                            "tool_use_id": message.get("tool_call_id", message.get("id", "")),
                            "content": _stringify_content(message.get("content", "")),
                        }
                    ],
                }
            )
            continue
        content = _chat_content_to_anthropic_content(message.get("content", ""))
        if message.get("tool_calls"):
            content = content if isinstance(content, list) else [{"type": "text", "text": str(content)}]
            content.extend(_chat_tool_calls_to_anthropic(message.get("tool_calls", [])))
        if _is_empty_anthropic_content(content):
            continue
        result["messages"].append({"role": role, "content": content})
    if system_parts:
        result["system"] = "\n\n".join(system_parts)
    tools = _canonical_tools_to_anthropic(canonical.get("tools", []))
    if tools:
        result["tools"] = tools
    tool_choice = canonical.get("tool_choice")
    if tool_choice is not None:
        result["tool_choice"] = _tool_choice_to_anthropic(tool_choice)
    if "max_output_tokens" in result and "max_tokens" not in result:
        result["max_tokens"] = result.pop("max_output_tokens")
    return result


def _merge_consecutive_assistant_tool_call_messages(
    messages: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    merged: list[dict[str, Any]] = []
    pending: dict[str, Any] | None = None

    for message in messages:
        if _is_assistant_tool_call_only_message(message):
            if pending is None:
                pending = deepcopy(message)
            else:
                pending["tool_calls"].extend(deepcopy(message.get("tool_calls", [])))
            continue
        if pending is not None:
            merged.append(pending)
            pending = None
        merged.append(message)

    if pending is not None:
        merged.append(pending)
    return merged


def _is_assistant_tool_call_only_message(message: Any) -> bool:
    return (
        isinstance(message, dict)
        and message.get("role") == "assistant"
        and _is_empty_chat_content(message.get("content"))
        and isinstance(message.get("tool_calls"), list)
        and bool(message.get("tool_calls"))
    )


def _responses_input_item_to_messages(item: Any) -> list[dict[str, Any]]:
    if isinstance(item, str):
        return [{"role": "user", "content": item}]
    if not isinstance(item, dict):
        return []
    item_type = item.get("type")
    if item_type in RESPONSES_TOOL_CALL_TYPES:
        name = item.get("name") or item_type.replace("_call", "")
        arguments = item.get("arguments")
        if arguments is None:
            arguments = item.get("input") or item.get("action") or {}
        arguments = _normalize_apply_patch_arguments(item_type, name, arguments)
        return [
            {
                "role": "assistant",
                "content": "",
                "tool_calls": [
                    {
                        "id": item.get("call_id") or item.get("id") or f"call_{uuid.uuid4().hex}",
                        "type": "function",
                        "function": {
                            "name": str(name),
                            "arguments": _json_dumps(arguments),
                        },
                    }
                ],
            }
        ]
    if item_type in RESPONSES_TOOL_OUTPUT_TYPES:
        output = item.get("output")
        if output is None:
            output = item.get("content", "")
        return [
            {
                "role": "tool",
                "tool_call_id": item.get("call_id") or item.get("tool_call_id"),
                "content": _stringify_content(output),
            }
        ]
    if item_type == "reasoning":
        text = _responses_reasoning_summary_to_text(item)
        return [{"role": "assistant", "content": text}] if text else []
    if item_type == "web_search_call":
        text = _responses_metadata_item_to_text(item)
        return [{"role": "assistant", "content": text}] if text else []
    if item_type and "role" not in item and "content" not in item:
        text = _responses_metadata_item_to_text(item)
        return [{"role": "assistant", "content": text}] if text else []
    role = item.get("role", "user")
    if role == "developer":
        role = "system"
    content = _responses_content_to_chat_content(item.get("content", ""))
    if _is_empty_chat_content(content):
        return []
    return [{"role": role, "content": content}]


def _messages_to_responses_input(messages: list[dict[str, Any]]) -> tuple[str, list[dict[str, Any]]]:
    instructions: list[str] = []
    input_items: list[dict[str, Any]] = []
    for message in messages:
        role = message.get("role", "user")
        if role in {"system", "developer"}:
            text = _stringify_content(message.get("content", ""))
            if text:
                instructions.append(text)
            continue
        if role == "tool":
            input_items.append(
                {
                    "type": "function_call_output",
                    "call_id": message.get("tool_call_id"),
                    "output": _stringify_content(message.get("content", "")),
                }
            )
            continue
        input_items.append(
            {
                "type": "message",
                "role": role,
                "content": _chat_content_to_responses_content(message.get("content", ""), role),
            }
        )
        if message.get("tool_calls"):
            for tool_call in message.get("tool_calls", []):
                function = tool_call.get("function", {})
                input_items.append(
                    {
                        "type": "function_call",
                        "call_id": tool_call.get("id"),
                        "name": function.get("name"),
                        "arguments": function.get("arguments", "{}"),
                    }
                )
    return "\n\n".join(instructions), input_items


def _copy_common_request_params(payload: dict[str, Any], protocol: str) -> dict[str, Any]:
    ignored = {
        "model",
        "messages",
        "input",
        "instructions",
        "system",
        "tools",
        "tool_choice",
    }
    params = {key: deepcopy(value) for key, value in payload.items() if key not in ignored}
    if protocol == "responses" and "max_output_tokens" in params:
        params["max_tokens"] = params.pop("max_output_tokens")
    return params


def _responses_tools_to_canonical(tools: Any) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for tool in tools or []:
        if not isinstance(tool, dict):
            continue
        tool_type = tool.get("type", "function")
        if tool_type == "function":
            result.append(
                {
                    "name": tool.get("name"),
                    "description": tool.get("description", ""),
                    "parameters": tool.get("parameters") or {},
                    "native_type": "function",
                }
            )
        else:
            result.append(_wrap_native_tool(tool_type, tool))
    return result


def _chat_tools_to_canonical(tools: Any) -> list[dict[str, Any]]:
    result = []
    for tool in tools or []:
        if not isinstance(tool, dict):
            continue
        function = tool.get("function", {}) if tool.get("type") == "function" else tool
        result.append(
            {
                "name": function.get("name"),
                "description": function.get("description", ""),
                "parameters": function.get("parameters") or {},
                "native_type": "function",
            }
        )
    return result


def _anthropic_tools_to_canonical(tools: Any) -> list[dict[str, Any]]:
    result = []
    for tool in tools or []:
        if not isinstance(tool, dict):
            continue
        result.append(
            {
                "name": tool.get("name"),
                "description": tool.get("description", ""),
                "parameters": tool.get("input_schema") or {},
                "native_type": "function",
            }
        )
    return result


def _canonical_tools_to_responses(tools: list[dict[str, Any]]) -> list[dict[str, Any]]:
    result = []
    for tool in tools:
        native_type = tool.get("native_type", "function")
        if native_type != "function":
            raw = deepcopy(tool.get("raw") or {})
            if raw:
                result.append(raw)
                continue
        result.append(
            {
                "type": "function",
                "name": tool.get("name"),
                "description": tool.get("description", ""),
                "parameters": tool.get("parameters") or {},
            }
        )
    return result


def _canonical_tools_to_chat(tools: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return [
        {
            "type": "function",
            "function": {
                "name": tool.get("name"),
                "description": tool.get("description", ""),
                "parameters": tool.get("parameters") or {},
            },
        }
        for tool in tools
        if tool.get("name")
    ]


def _canonical_tools_to_anthropic(tools: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return [
        {
            "name": tool.get("name"),
            "description": tool.get("description", ""),
            "input_schema": tool.get("parameters") or {},
        }
        for tool in tools
        if tool.get("name")
    ]


def _wrap_native_tool(tool_type: str, tool: dict[str, Any]) -> dict[str, Any]:
    name = str(tool.get("name") or tool_type).replace("-", "_")
    if tool_type in {"local_shell", "shell"}:
        parameters = {
            "type": "object",
            "properties": {"cmd": {"type": "string"}},
            "required": ["cmd"],
        }
    elif tool_type == "apply_patch":
        parameters = {
            "type": "object",
            "properties": {"patch": {"type": "string"}},
            "required": ["patch"],
        }
    else:
        parameters = {
            "type": "object",
            "properties": {"input": {"type": "string"}},
        }
    return {
        "name": name,
        "description": tool.get("description") or f"Wrapped Responses tool: {tool_type}",
        "parameters": tool.get("parameters") or parameters,
        "native_type": tool_type,
        "raw": deepcopy(tool),
    }


def _responses_response_to_canonical(
    payload: dict[str, Any], original_model: str | None
) -> dict[str, Any]:
    text_parts: list[str] = []
    tool_calls: list[dict[str, Any]] = []
    for item in payload.get("output", []) or []:
        if not isinstance(item, dict):
            continue
        if item.get("type") == "message":
            for block in item.get("content", []) or []:
                if isinstance(block, dict) and block.get("type") in {"output_text", "text"}:
                    text_parts.append(str(block.get("text", "")))
        elif item.get("type") in {"function_call", "custom_tool_call", "local_shell_call", "shell_call", "apply_patch_call"}:
            name = item.get("name") or item.get("type", "tool").replace("_call", "")
            arguments = item.get("arguments") or item.get("input") or {}
            arguments = _normalize_apply_patch_arguments(item.get("type", ""), name, arguments)
            tool_calls.append(
                {
                    "id": item.get("call_id") or item.get("id") or f"call_{uuid.uuid4().hex}",
                    "name": name,
                    "arguments": _json_dumps(arguments),
                }
            )
    return {
        "id": payload.get("id") or f"resp_{uuid.uuid4().hex}",
        "model": original_model or payload.get("model"),
        "created": payload.get("created_at") or int(time.time()),
        "text": "".join(text_parts),
        "tool_calls": tool_calls,
        "finish_reason": payload.get("status") or "stop",
        "usage": _responses_usage_to_canonical(payload.get("usage", {})),
        "raw": deepcopy(payload),
    }


def _chat_response_to_canonical(
    payload: dict[str, Any], original_model: str | None
) -> dict[str, Any]:
    choice = (payload.get("choices") or [{}])[0] if isinstance(payload.get("choices"), list) else {}
    message = choice.get("message", {}) if isinstance(choice, dict) else {}
    tool_calls = []
    for tool_call in message.get("tool_calls", []) or []:
        function = tool_call.get("function", {})
        tool_calls.append(
            {
                "id": tool_call.get("id") or f"call_{uuid.uuid4().hex}",
                "name": function.get("name"),
                "arguments": function.get("arguments", "{}"),
            }
        )
    return {
        "id": payload.get("id") or f"chatcmpl_{uuid.uuid4().hex}",
        "model": original_model or payload.get("model"),
        "created": payload.get("created") or int(time.time()),
        "text": _stringify_content(message.get("content", "")),
        "tool_calls": tool_calls,
        "finish_reason": choice.get("finish_reason", "stop") if isinstance(choice, dict) else "stop",
        "usage": _chat_usage_to_canonical(payload.get("usage", {})),
        "raw": deepcopy(payload),
    }


def _messages_response_to_canonical(
    payload: dict[str, Any], original_model: str | None
) -> dict[str, Any]:
    text_parts: list[str] = []
    tool_calls: list[dict[str, Any]] = []
    for block in payload.get("content", []) or []:
        if not isinstance(block, dict):
            continue
        if block.get("type") == "text":
            text_parts.append(str(block.get("text", "")))
        elif block.get("type") == "tool_use":
            tool_calls.append(
                {
                    "id": block.get("id") or f"call_{uuid.uuid4().hex}",
                    "name": block.get("name"),
                    "arguments": _json_dumps(block.get("input") or {}),
                }
            )
    return {
        "id": payload.get("id") or f"msg_{uuid.uuid4().hex}",
        "model": original_model or payload.get("model"),
        "created": int(time.time()),
        "text": "".join(text_parts),
        "tool_calls": tool_calls,
        "finish_reason": payload.get("stop_reason") or "stop",
        "usage": _messages_usage_to_canonical(payload.get("usage", {})),
        "raw": deepcopy(payload),
    }


def _canonical_to_responses_response(canonical: dict[str, Any]) -> dict[str, Any]:
    output = []
    if canonical.get("text"):
        output.append(
            {
                "id": f"msg_{uuid.uuid4().hex}",
                "type": "message",
                "status": "completed",
                "role": "assistant",
                "content": [{"type": "output_text", "text": canonical.get("text", "")}],
            }
        )
    for tool_call in canonical.get("tool_calls", []):
        tool_name = str(tool_call.get("name") or "")
        if _is_apply_patch_name(tool_name):
            output.append(
                {
                    "id": f"ctc_{uuid.uuid4().hex}",
                    "type": "custom_tool_call",
                    "status": "completed",
                    "call_id": tool_call.get("id"),
                    "name": "apply_patch",
                    "input": _apply_patch_input_from_arguments(
                        tool_call.get("arguments", "{}")
                    ),
                }
            )
            continue
        output.append(
            {
                "id": f"fc_{uuid.uuid4().hex}",
                "type": "function_call",
                "status": "completed",
                "call_id": tool_call.get("id"),
                "name": tool_name,
                "arguments": tool_call.get("arguments", "{}"),
            }
        )
    return {
        "id": canonical.get("id") or f"resp_{uuid.uuid4().hex}",
        "object": "response",
        "created_at": canonical.get("created") or int(time.time()),
        "status": "completed",
        "model": canonical.get("model"),
        "output": output,
        "usage": _canonical_usage_to_responses(canonical.get("usage", {})),
    }


def _canonical_to_chat_response(canonical: dict[str, Any]) -> dict[str, Any]:
    message: dict[str, Any] = {
        "role": "assistant",
        "content": canonical.get("text") or None,
    }
    if canonical.get("tool_calls"):
        message["tool_calls"] = [
            {
                "id": tool_call.get("id"),
                "type": "function",
                "function": {
                    "name": tool_call.get("name"),
                    "arguments": tool_call.get("arguments", "{}"),
                },
            }
            for tool_call in canonical.get("tool_calls", [])
        ]
    return {
        "id": canonical.get("id") or f"chatcmpl_{uuid.uuid4().hex}",
        "object": "chat.completion",
        "created": canonical.get("created") or int(time.time()),
        "model": canonical.get("model"),
        "choices": [
            {
                "index": 0,
                "message": message,
                "finish_reason": canonical.get("finish_reason") or "stop",
            }
        ],
        "usage": _canonical_usage_to_chat(canonical.get("usage", {})),
    }


def _canonical_to_messages_response(canonical: dict[str, Any]) -> dict[str, Any]:
    content = []
    if canonical.get("text"):
        content.append({"type": "text", "text": canonical.get("text", "")})
    for tool_call in canonical.get("tool_calls", []):
        content.append(
            {
                "type": "tool_use",
                "id": tool_call.get("id"),
                "name": tool_call.get("name"),
                "input": _parse_json_object(tool_call.get("arguments", "{}")),
            }
        )
    return {
        "id": canonical.get("id") or f"msg_{uuid.uuid4().hex}",
        "type": "message",
        "role": "assistant",
        "model": canonical.get("model"),
        "content": content,
        "stop_reason": canonical.get("finish_reason") or "end_turn",
        "stop_sequence": None,
        "usage": _canonical_usage_to_messages(canonical.get("usage", {})),
    }


def _responses_reasoning_summary_to_text(item: dict[str, Any]) -> str:
    summary = _stringify_content(item.get("summary", "")).strip()
    if summary:
        return f"Reasoning summary:\n{summary}"
    content = _stringify_content(item.get("content", "")).strip()
    if content:
        return f"Reasoning content:\n{content}"
    return ""


def _responses_metadata_item_to_text(item: dict[str, Any]) -> str:
    exported = {}
    for key, value in item.items():
        if key in {"content", "encrypted_content"}:
            continue
        if key == "summary" and not value:
            continue
        exported[key] = deepcopy(value)
    if not exported or set(exported) <= {"type"}:
        return ""
    return f"Responses {item.get('type', 'item')}: {_json_dumps(exported)}"


def _is_empty_chat_content(content: Any) -> bool:
    if content is None:
        return True
    if isinstance(content, str):
        return content == ""
    if isinstance(content, list):
        return all(_is_empty_content_block(block) for block in content)
    return False


def _is_empty_anthropic_content(content: Any) -> bool:
    return _is_empty_chat_content(content)


def _is_empty_content_block(block: Any) -> bool:
    if not isinstance(block, dict):
        return False
    block_type = block.get("type")
    if block_type in {"text", "input_text", "output_text"}:
        return not block.get("text")
    if block_type == "thinking":
        return not block.get("thinking")
    if block_type in {"tool_use", "tool_result"}:
        return False
    if "content" in block:
        return _is_empty_chat_content(block.get("content"))
    if "text" in block:
        return not block.get("text")
    return False


def _responses_content_to_chat_content(content: Any) -> Any:
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        result = []
        for block in content:
            if not isinstance(block, dict):
                continue
            block_type = block.get("type")
            if block_type in {"input_text", "output_text", "text"}:
                result.append({"type": "text", "text": block.get("text", "")})
            else:
                result.append(deepcopy(block))
        if len(result) == 1 and result[0].get("type") == "text":
            return result[0].get("text", "")
        return result
    return _stringify_content(content)


def _chat_content_to_responses_content(content: Any, role: str) -> list[dict[str, Any]]:
    text_type = "output_text" if role == "assistant" else "input_text"
    if isinstance(content, str):
        return [{"type": text_type, "text": content}]
    if isinstance(content, list):
        result = []
        for block in content:
            if isinstance(block, dict) and block.get("type") in {"text", "input_text", "output_text"}:
                result.append({"type": text_type, "text": block.get("text", "")})
            else:
                result.append(deepcopy(block))
        return result
    return [{"type": text_type, "text": _stringify_content(content)}]


def _anthropic_content_to_chat_content(content: Any) -> Any:
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        result = []
        for block in content:
            if not isinstance(block, dict):
                continue
            if block.get("type") == "text":
                result.append({"type": "text", "text": block.get("text", "")})
            elif block.get("type") == "tool_use":
                result.append(deepcopy(block))
            elif block.get("type") == "tool_result":
                result.append({"type": "text", "text": _stringify_content(block.get("content", ""))})
            else:
                result.append(deepcopy(block))
        if len(result) == 1 and result[0].get("type") == "text":
            return result[0].get("text", "")
        return result
    return _stringify_content(content)


def _chat_content_to_anthropic_content(content: Any) -> list[dict[str, Any]]:
    if isinstance(content, str):
        if content == "":
            return []
        return [{"type": "text", "text": content}]
    if isinstance(content, list):
        result = []
        for block in content:
            if not isinstance(block, dict):
                continue
            if block.get("type") in {"text", "input_text", "output_text"}:
                text = block.get("text", "")
                if text:
                    result.append({"type": "text", "text": text})
            elif block.get("type") == "tool_use":
                result.append(deepcopy(block))
            else:
                result.append(deepcopy(block))
        return result
    return [{"type": "text", "text": _stringify_content(content)}]


def _chat_tool_calls_to_anthropic(tool_calls: list[dict[str, Any]]) -> list[dict[str, Any]]:
    result = []
    for tool_call in tool_calls:
        function = tool_call.get("function", {})
        result.append(
            {
                "type": "tool_use",
                "id": tool_call.get("id") or f"call_{uuid.uuid4().hex}",
                "name": function.get("name"),
                "input": _parse_json_object(function.get("arguments", "{}")),
            }
        )
    return result


def _tool_choice_to_anthropic(tool_choice: Any) -> Any:
    if isinstance(tool_choice, str):
        if tool_choice in {"auto", "none"}:
            return {"type": "auto"}
        if tool_choice == "required":
            return {"type": "any"}
    if isinstance(tool_choice, dict):
        function = tool_choice.get("function") or {}
        name = function.get("name") or tool_choice.get("name")
        if name:
            return {"type": "tool", "name": name}
    return tool_choice


def _stringify_content(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, str):
        return value
    if isinstance(value, list):
        parts = []
        for item in value:
            if isinstance(item, dict):
                if "text" in item:
                    parts.append(str(item.get("text", "")))
                elif "content" in item:
                    parts.append(_stringify_content(item.get("content")))
            else:
                parts.append(str(item))
        return "".join(parts)
    if isinstance(value, (dict, tuple)):
        return _json_dumps(value)
    return str(value)


def _json_dumps(value: Any) -> str:
    if isinstance(value, str):
        return value
    return json.dumps(value, ensure_ascii=False)


def _is_apply_patch_name(name: str) -> bool:
    return name.replace("-", "_") == "apply_patch"


def _apply_patch_input_from_arguments(arguments: Any) -> str:
    value = _decode_json_value(arguments)
    patch = _extract_patch_value(value)
    if patch is None:
        return arguments if isinstance(arguments, str) else _json_dumps(arguments)
    return patch if isinstance(patch, str) else _json_dumps(patch)


def _extract_patch_value(value: Any) -> Any:
    if isinstance(value, dict):
        for key in ("patch", "input"):
            if key in value:
                return value[key]
        if "command" in value:
            return _extract_patch_command(value["command"])
    if isinstance(value, (list, tuple)):
        return _extract_patch_command(value)
    return None


def _extract_patch_command(command: Any) -> Any:
    command = _decode_json_value(command)
    if isinstance(command, dict):
        return _extract_patch_value(command)
    if isinstance(command, (list, tuple)):
        parts = list(command)
        if len(parts) >= 2 and _is_apply_patch_executable(parts[0]):
            return _extract_patch_value(parts[1]) or parts[1]
        for part in parts:
            if isinstance(part, str) and _looks_like_patch(part):
                return part
    if isinstance(command, str) and _looks_like_patch(command):
        return command
    return None


def _decode_json_value(value: Any) -> Any:
    if not isinstance(value, str):
        return value
    try:
        return json.loads(value)
    except json.JSONDecodeError:
        return value


def _is_apply_patch_executable(value: Any) -> bool:
    executable = str(value or "").rsplit("/", 1)[-1]
    return _is_apply_patch_name(executable)


def _looks_like_patch(value: str) -> bool:
    return value.lstrip().startswith("*** Begin Patch")


def _normalize_apply_patch_arguments(item_type: str, name: str, arguments: Any) -> Any:
    normalized_name = str(name or "").replace("-", "_")
    if normalized_name != "apply_patch" and item_type != "apply_patch_call":
        return arguments
    if isinstance(arguments, str):
        if _is_json_object_string(arguments):
            return arguments
        return {"patch": arguments}
    if isinstance(arguments, dict):
        if "patch" in arguments:
            return arguments
        if set(arguments) == {"input"}:
            return {"patch": arguments["input"]}
    return arguments


def _is_json_object_string(value: str) -> bool:
    try:
        parsed = json.loads(value)
    except json.JSONDecodeError:
        return False
    return isinstance(parsed, dict)


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


def _responses_usage_to_canonical(usage: dict[str, Any]) -> dict[str, int]:
    return {
        "input_tokens": int(usage.get("input_tokens") or usage.get("prompt_tokens") or 0),
        "output_tokens": int(usage.get("output_tokens") or usage.get("completion_tokens") or 0),
        "total_tokens": int(usage.get("total_tokens") or 0),
    }


def _chat_usage_to_canonical(usage: dict[str, Any]) -> dict[str, int]:
    return {
        "input_tokens": int(usage.get("prompt_tokens") or usage.get("input_tokens") or 0),
        "output_tokens": int(usage.get("completion_tokens") or usage.get("output_tokens") or 0),
        "total_tokens": int(usage.get("total_tokens") or 0),
    }


def _messages_usage_to_canonical(usage: dict[str, Any]) -> dict[str, int]:
    input_tokens = int(usage.get("input_tokens") or 0)
    output_tokens = int(usage.get("output_tokens") or 0)
    return {
        "input_tokens": input_tokens,
        "output_tokens": output_tokens,
        "total_tokens": input_tokens + output_tokens,
    }


def _canonical_usage_to_responses(usage: dict[str, int]) -> dict[str, int]:
    total = usage.get("total_tokens") or usage.get("input_tokens", 0) + usage.get("output_tokens", 0)
    return {
        "input_tokens": usage.get("input_tokens", 0),
        "output_tokens": usage.get("output_tokens", 0),
        "total_tokens": total,
    }


def _canonical_usage_to_chat(usage: dict[str, int]) -> dict[str, int]:
    total = usage.get("total_tokens") or usage.get("input_tokens", 0) + usage.get("output_tokens", 0)
    return {
        "prompt_tokens": usage.get("input_tokens", 0),
        "completion_tokens": usage.get("output_tokens", 0),
        "total_tokens": total,
    }


def _canonical_usage_to_messages(usage: dict[str, int]) -> dict[str, int]:
    return {
        "input_tokens": usage.get("input_tokens", 0),
        "output_tokens": usage.get("output_tokens", 0),
    }
