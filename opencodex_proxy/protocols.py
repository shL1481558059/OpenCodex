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
    messages = _normalize_chat_tool_history(
        _merge_consecutive_assistant_tool_call_messages(messages)
    )

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
                if key
                in {
                    "role",
                    "content",
                    "tool_calls",
                    "tool_call_id",
                    "name",
                    "reasoning_content",
                }
            }
        )
        result["messages"][-1]["role"] = role
    tools = _canonical_tools_to_chat(canonical.get("tools", []))
    if tools:
        result["tools"] = tools
    if canonical.get("tool_choice") is not None:
        result["tool_choice"] = _tool_choice_to_chat(canonical["tool_choice"])
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
        reasoning_content = _stringify_content(message.get("reasoning_content", "")).strip()
        if reasoning_content:
            content = [{"type": "text", "text": f"Reasoning content:\n{reasoning_content}"}] + content
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


def _normalize_chat_tool_history(messages: list[dict[str, Any]]) -> list[dict[str, Any]]:
    messages = _fold_reasoning_into_tool_call_messages(messages)
    messages = _merge_consecutive_assistant_tool_call_messages(messages)
    _remove_orphan_tool_messages(messages)
    _ensure_tool_calls_have_outputs(messages)
    return messages


def _fold_reasoning_into_tool_call_messages(
    messages: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    folded: list[dict[str, Any]] = []
    pending_reasoning: dict[str, Any] | None = None
    for message in messages:
        if _is_reasoning_only_message(message):
            if folded and _is_assistant_with_tool_calls(folded[-1]):
                _append_reasoning_content(folded[-1], message.get("reasoning_content", ""))
            else:
                if pending_reasoning is None:
                    pending_reasoning = deepcopy(message)
                else:
                    _append_reasoning_content(
                        pending_reasoning, message.get("reasoning_content", "")
                    )
            continue

        if _is_assistant_with_tool_calls(message) and pending_reasoning is not None:
            message = deepcopy(message)
            _append_reasoning_content(message, pending_reasoning.get("reasoning_content", ""))
            pending_reasoning = None
        elif pending_reasoning is not None:
            folded.append(pending_reasoning)
            pending_reasoning = None
        folded.append(message)

    if pending_reasoning is not None:
        folded.append(pending_reasoning)
    return folded


def _is_reasoning_only_message(message: Any) -> bool:
    return (
        isinstance(message, dict)
        and message.get("role") == "assistant"
        and bool(message.get("reasoning_content"))
        and _is_empty_chat_content(message.get("content"))
        and not message.get("tool_calls")
    )


def _is_assistant_with_tool_calls(message: Any) -> bool:
    return (
        isinstance(message, dict)
        and message.get("role") == "assistant"
        and isinstance(message.get("tool_calls"), list)
        and bool(message.get("tool_calls"))
    )


def _append_reasoning_content(message: dict[str, Any], reasoning_content: Any) -> None:
    text = _stringify_content(reasoning_content)
    if not text:
        return
    existing = _stringify_content(message.get("reasoning_content", ""))
    message["reasoning_content"] = existing + text if existing else text


def _remove_orphan_tool_messages(messages: list[dict[str, Any]]) -> None:
    valid_ids: set[str] | None = None
    index = 0
    while index < len(messages):
        message = messages[index]
        role = message.get("role") if isinstance(message, dict) else None
        if role == "assistant":
            tool_calls = message.get("tool_calls")
            valid_ids = (
                {
                    str(tool_call.get("id"))
                    for tool_call in tool_calls
                    if isinstance(tool_call, dict) and tool_call.get("id")
                }
                if isinstance(tool_calls, list) and tool_calls
                else None
            )
            index += 1
            continue
        if role == "tool":
            tool_call_id = str(message.get("tool_call_id") or "")
            if valid_ids and tool_call_id in valid_ids:
                index += 1
                continue
            del messages[index]
            continue
        valid_ids = None
        index += 1


def _ensure_tool_calls_have_outputs(messages: list[dict[str, Any]]) -> None:
    index = 0
    while index < len(messages):
        message = messages[index]
        if not _is_assistant_with_tool_calls(message):
            index += 1
            continue
        tool_calls = message.get("tool_calls", [])
        seen: set[str] = set()
        insert_at = index + 1
        while insert_at < len(messages) and messages[insert_at].get("role") == "tool":
            tool_call_id = messages[insert_at].get("tool_call_id")
            if tool_call_id:
                seen.add(str(tool_call_id))
            insert_at += 1
        missing = [
            str(tool_call.get("id"))
            for tool_call in tool_calls
            if isinstance(tool_call, dict)
            and tool_call.get("id")
            and str(tool_call.get("id")) not in seen
        ]
        if missing:
            placeholders = [
                {
                    "role": "tool",
                    "tool_call_id": tool_call_id,
                    "content": "[tool output missing - no function_call_output was provided for this call_id]",
                }
                for tool_call_id in missing
            ]
            messages[insert_at:insert_at] = placeholders
            index = insert_at + len(placeholders)
            continue
        index += 1


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
        name, arguments = _normalize_apply_patch_tool_call(item_type, name, arguments)
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
        call_id = item.get("call_id") or item.get("tool_call_id") or item.get("tool_use_id")
        if not call_id:
            return []
        output = item.get("output")
        if output is None:
            output = item.get("content", "")
        return [
            {
                "role": "tool",
                "tool_call_id": call_id,
                "content": _stringify_content(output),
            }
        ]
    if item_type == "reasoning":
        text = _responses_reasoning_to_text(item)
        return [{"role": "assistant", "content": "", "reasoning_content": text}] if text else []
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
        reasoning_content = _stringify_content(message.get("reasoning_content", "")).strip()
        if role == "assistant" and reasoning_content:
            input_items.append(_responses_reasoning_item(reasoning_content))
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
        result.extend(_responses_tool_to_canonical_items(tool))
    return _dedupe_canonical_tools(result)


def _responses_tool_to_canonical_items(tool: Any) -> list[dict[str, Any]]:
    if not isinstance(tool, dict):
        return []
    tool_type = tool.get("type", "function")
    if tool_type == "namespace":
        nested = tool.get("tools")
        if not isinstance(nested, list):
            return []
        result: list[dict[str, Any]] = []
        for inner in nested:
            result.extend(_responses_tool_to_canonical_items(inner))
        return result
    if tool_type == "function":
        return [
            {
                "name": tool.get("name"),
                "description": tool.get("description", ""),
                "parameters": tool.get("parameters") or {},
                "native_type": "function",
            }
        ]
    return [_wrap_native_tool(tool_type, tool)]


def _dedupe_canonical_tools(tools: list[dict[str, Any]]) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    seen: set[tuple[str, str]] = set()
    for tool in tools:
        native_type = str(tool.get("native_type") or "function")
        name = str(tool.get("name") or "")
        if not name:
            continue
        namespace = "function" if native_type == "function" else native_type
        key = (namespace, name)
        if key in seen:
            continue
        seen.add(key)
        result.append(tool)
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
        for tool in _expand_apply_patch_proxy_tools(tools)
        if tool.get("name")
    ]


def _canonical_tools_to_anthropic(tools: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return [
        {
            "name": tool.get("name"),
            "description": tool.get("description", ""),
            "input_schema": tool.get("parameters") or {},
        }
        for tool in _expand_apply_patch_proxy_tools(tools)
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


def _expand_apply_patch_proxy_tools(tools: list[dict[str, Any]]) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for tool in tools:
        if _is_apply_patch_canonical_tool(tool):
            result.extend(_apply_patch_proxy_tools(tool))
        else:
            result.append(tool)
    return _dedupe_canonical_tools(result)


def _is_apply_patch_canonical_tool(tool: dict[str, Any]) -> bool:
    native_type = str(tool.get("native_type") or "function")
    name = str(tool.get("name") or "")
    if native_type == "apply_patch":
        return True
    if _is_apply_patch_name(name) and native_type in {"custom", "apply_patch"}:
        return True
    raw = tool.get("raw")
    if isinstance(raw, dict) and raw.get("type") == "custom" and _is_apply_patch_name(name):
        return True
    return False


def _apply_patch_proxy_tools(tool: dict[str, Any]) -> list[dict[str, Any]]:
    description = tool.get("description") or "Apply a patch."
    return [
        {
            "name": "apply_patch_add_file",
            "description": f"{description} Create one new file with structured JSON.",
            "parameters": _apply_patch_single_op_schema("add_file"),
            "native_type": "function",
        },
        {
            "name": "apply_patch_delete_file",
            "description": f"{description} Delete one file with structured JSON.",
            "parameters": _apply_patch_single_op_schema("delete_file"),
            "native_type": "function",
        },
        {
            "name": "apply_patch_update_file",
            "description": f"{description} Edit one existing file with structured hunks.",
            "parameters": _apply_patch_single_op_schema("update_file"),
            "native_type": "function",
        },
        {
            "name": "apply_patch_replace_file",
            "description": f"{description} Replace one file entirely with structured JSON.",
            "parameters": _apply_patch_single_op_schema("replace_file"),
            "native_type": "function",
        },
        {
            "name": "apply_patch_batch",
            "description": f"{description} Apply multiple structured patch operations.",
            "parameters": _apply_patch_batch_schema(),
            "native_type": "function",
        },
    ]


def _apply_patch_single_op_schema(action: str) -> dict[str, Any]:
    properties: dict[str, Any] = {
        "path": {"type": "string", "description": "Target file path."}
    }
    required = ["path"]
    if action in {"add_file", "replace_file"}:
        properties["content"] = {"type": "string", "description": "Full file content."}
        required.append("content")
    elif action == "update_file":
        properties["move_to"] = {
            "type": "string",
            "description": "Optional destination path for a file move.",
        }
        properties["hunks"] = _apply_patch_hunks_schema()
        required.append("hunks")
    return {
        "type": "object",
        "additionalProperties": False,
        "properties": properties,
        "required": required,
    }


def _apply_patch_batch_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "additionalProperties": False,
        "properties": {
            "operations": {
                "type": "array",
                "description": "Structured patch operations.",
                "minItems": 1,
                "items": {
                    "type": "object",
                    "additionalProperties": False,
                    "properties": {
                        "type": {
                            "type": "string",
                            "enum": [
                                "add_file",
                                "delete_file",
                                "update_file",
                                "replace_file",
                            ],
                        },
                        "path": {"type": "string", "description": "Target file path."},
                        "move_to": {
                            "type": "string",
                            "description": "Optional destination path for a file move.",
                        },
                        "content": {"type": "string", "description": "File content."},
                        "hunks": _apply_patch_hunks_schema(),
                    },
                    "required": ["type", "path"],
                },
            }
        },
        "required": ["operations"],
    }


def _apply_patch_hunks_schema() -> dict[str, Any]:
    return {
        "type": "array",
        "description": "Structured update hunks.",
        "minItems": 1,
        "items": {
            "type": "object",
            "additionalProperties": False,
            "properties": {
                "lines": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "additionalProperties": False,
                        "properties": {
                            "op": {
                                "type": "string",
                                "enum": ["context", "add", "remove", "eof"],
                            },
                            "text": {"type": "string"},
                        },
                        "required": ["op", "text"],
                    },
                },
            },
            "required": ["lines"],
        },
    }


def _responses_response_to_canonical(
    payload: dict[str, Any], original_model: str | None
) -> dict[str, Any]:
    text_parts: list[str] = []
    reasoning_parts: list[str] = []
    annotations: list[dict[str, Any]] = []
    tool_calls: list[dict[str, Any]] = []
    for item in payload.get("output", []) or []:
        if not isinstance(item, dict):
            continue
        if item.get("type") == "message":
            for block in item.get("content", []) or []:
                if isinstance(block, dict) and block.get("type") in {"output_text", "text"}:
                    text_parts.append(str(block.get("text", "")))
                    annotations.extend(_normalize_annotations(block.get("annotations")))
        elif item.get("type") == "reasoning":
            reasoning = _responses_reasoning_to_text(item)
            if reasoning:
                reasoning_parts.append(reasoning)
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
        "reasoning": "".join(reasoning_parts),
        "annotations": annotations,
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
        "reasoning": _stringify_content(message.get("reasoning_content", "")),
        "annotations": _normalize_annotations(message.get("annotations")),
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
    if canonical.get("reasoning"):
        reasoning = canonical.get("reasoning", "")
        output.append(
            {
                "id": f"rs_{uuid.uuid4().hex}",
                "type": "reasoning",
                "status": "completed",
                "summary": [{"type": "summary_text", "text": reasoning}],
                "encrypted_content": reasoning,
            }
        )
    if canonical.get("text"):
        output_text = {"type": "output_text", "text": canonical.get("text", "")}
        if canonical.get("annotations"):
            output_text["annotations"] = deepcopy(canonical.get("annotations"))
        output.append(
            {
                "id": f"msg_{uuid.uuid4().hex}",
                "type": "message",
                "status": "completed",
                "role": "assistant",
                "content": [output_text],
            }
        )
    for tool_call in canonical.get("tool_calls", []):
        tool_name = str(tool_call.get("name") or "")
        if _is_apply_patch_tool_name(tool_name):
            output.append(
                {
                    "id": f"ctc_{uuid.uuid4().hex}",
                    "type": "custom_tool_call",
                    "status": "completed",
                    "call_id": tool_call.get("id"),
                    "name": "apply_patch",
                    "input": _apply_patch_input_from_tool_call(
                        tool_name, tool_call.get("arguments", "{}")
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
    finish_reason = canonical.get("finish_reason") or "stop"
    incomplete = finish_reason == "length"
    response = {
        "id": canonical.get("id") or f"resp_{uuid.uuid4().hex}",
        "object": "response",
        "created_at": canonical.get("created") or int(time.time()),
        "status": "incomplete" if incomplete else "completed",
        "model": canonical.get("model"),
        "output": output,
        "usage": _canonical_usage_to_responses(canonical.get("usage", {})),
    }
    if incomplete:
        response["incomplete_details"] = {"reason": "max_output_tokens"}
    return response


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
    text = _responses_reasoning_to_text(item)
    if text:
        return f"Reasoning content:\n{text}"
    return ""


def _responses_reasoning_to_text(item: dict[str, Any]) -> str:
    encrypted_content = _stringify_content(item.get("encrypted_content", "")).strip()
    if encrypted_content:
        return encrypted_content
    summary = _stringify_content(item.get("summary", "")).strip()
    if summary:
        return summary
    content = _stringify_content(item.get("content", "")).strip()
    if content:
        return content
    return ""


def _responses_reasoning_item(text: str) -> dict[str, Any]:
    return {
        "type": "reasoning",
        "summary": [{"type": "summary_text", "text": text}],
        "encrypted_content": text,
        "status": "completed",
    }


def _normalize_annotations(value: Any) -> list[dict[str, Any]]:
    if not isinstance(value, list):
        return []
    result: list[dict[str, Any]] = []
    for item in value:
        if not isinstance(item, dict):
            continue
        annotation = {
            "type": item.get("type") or "url_citation",
            "url": item.get("url") or "",
            "title": item.get("title") or "",
        }
        snippet = item.get("snippet", item.get("summary"))
        if snippet is not None:
            annotation["snippet"] = snippet
        result.append(annotation)
    return result


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


def _tool_choice_to_chat(tool_choice: Any) -> Any:
    if isinstance(tool_choice, str):
        return tool_choice
    if isinstance(tool_choice, dict):
        function = tool_choice.get("function") or {}
        if function.get("name"):
            return tool_choice
        tool_choice_type = tool_choice.get("type")
        if tool_choice_type in {"auto", "none"}:
            return tool_choice_type
        if tool_choice_type in {"required", "tool", "any", "function"}:
            return "required"
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


def _is_apply_patch_tool_name(name: str) -> bool:
    normalized = name.replace("-", "_")
    return normalized == "apply_patch" or normalized.startswith("apply_patch_")


def _normalize_apply_patch_tool_call(
    item_type: str, name: Any, arguments: Any
) -> tuple[str, Any]:
    tool_name = str(name or "")
    arguments = _normalize_apply_patch_arguments(item_type, tool_name, arguments)
    if not (_is_apply_patch_name(tool_name) or item_type == "apply_patch_call"):
        return tool_name, arguments
    structured = _structured_apply_patch_arguments_from_legacy(arguments)
    if structured is None:
        return tool_name, arguments
    return "apply_patch_batch", structured


def _apply_patch_input_from_tool_call(name: str, arguments: Any) -> str:
    normalized = name.replace("-", "_")
    if normalized.startswith("apply_patch_") and normalized != "apply_patch":
        rebuilt = _rebuild_apply_patch_grammar(normalized, arguments)
        if rebuilt is not None:
            return rebuilt
    return _apply_patch_input_from_arguments(arguments)


def _apply_patch_input_from_arguments(arguments: Any) -> str:
    value = _decode_json_value(arguments)
    patch = _extract_patch_value(value)
    if patch is None:
        return arguments if isinstance(arguments, str) else _json_dumps(arguments)
    return patch if isinstance(patch, str) else _json_dumps(patch)


def _rebuild_apply_patch_grammar(name: str, arguments: Any) -> str | None:
    value = _decode_json_value(arguments)
    if not isinstance(value, dict):
        return None
    action = name.removeprefix("apply_patch_")
    if action == "batch":
        operations = value.get("operations")
        if not isinstance(operations, list):
            return None
    else:
        operation = dict(value)
        operation["type"] = action
        operations = [operation]

    body: list[str] = []
    for operation in operations:
        if not isinstance(operation, dict):
            continue
        body.extend(_apply_patch_operation_lines(operation))
    if not body:
        return None
    return "\n".join(["*** Begin Patch", *body, "*** End Patch"])


def _apply_patch_operation_lines(operation: dict[str, Any]) -> list[str]:
    op_type = str(operation.get("type") or "")
    path = _apply_patch_path(operation.get("path"))
    if not path:
        return []
    if op_type == "add_file":
        return [
            f"*** Add File: {path}",
            *_prefixed_content_lines(operation.get("content", ""), "+"),
        ]
    if op_type == "delete_file":
        return [f"*** Delete File: {path}"]
    if op_type == "replace_file":
        return [
            f"*** Delete File: {path}",
            f"*** Add File: {path}",
            *_prefixed_content_lines(operation.get("content", ""), "+"),
        ]
    if op_type == "update_file":
        lines = [f"*** Update File: {path}"]
        raw_move_to = operation.get("move_to")
        if str(raw_move_to or "").strip():
            move_to = _apply_patch_path(raw_move_to)
            if not move_to:
                return []
            lines.append(f"*** Move to: {move_to}")
        hunks = operation.get("hunks")
        if not isinstance(hunks, list):
            return []
        for hunk in hunks:
            if not isinstance(hunk, dict):
                continue
            lines.append("@@")
            hunk_lines = hunk.get("lines")
            if not isinstance(hunk_lines, list):
                continue
            for line in hunk_lines:
                if not isinstance(line, dict):
                    continue
                text = str(line.get("text") or "")
                op = line.get("op")
                if op == "add":
                    lines.append(f"+{text}")
                elif op == "remove":
                    lines.append(f"-{text}")
                elif op == "eof":
                    lines.append("*** End of File")
                else:
                    lines.append(f" {text}")
        return lines
    return []


def _apply_patch_path(value: Any) -> str:
    path = str(value or "").strip()
    if "\n" in path or "\r" in path:
        return ""
    return path


def _prefixed_content_lines(content: Any, prefix: str) -> list[str]:
    return [f"{prefix}{line}" for line in str(content or "").split("\n")]


def _structured_apply_patch_arguments_from_legacy(arguments: Any) -> dict[str, Any] | None:
    value = _decode_json_value(arguments)
    if isinstance(value, dict):
        patch_keys = [key for key in ("patch", "input") if key in value]
        if len(value) != 1 or not patch_keys:
            return None
    patch = _apply_patch_input_from_arguments(arguments)
    if not isinstance(patch, str) or not _looks_like_patch(patch):
        return None
    operations = _parse_apply_patch_operations(patch)
    if not operations:
        return None
    return {"operations": operations}


def _parse_apply_patch_operations(patch: str) -> list[dict[str, Any]] | None:
    lines = patch.strip("\n").split("\n")
    if len(lines) < 2 or lines[0] != "*** Begin Patch" or lines[-1] != "*** End Patch":
        return None
    operations: list[dict[str, Any]] = []
    index = 1
    end = len(lines) - 1
    while index < end:
        line = lines[index]
        if line.startswith("*** Add File: "):
            path = line.removeprefix("*** Add File: ").strip()
            index += 1
            content: list[str] = []
            while index < end and not lines[index].startswith("*** "):
                if not lines[index].startswith("+"):
                    return None
                content.append(lines[index][1:])
                index += 1
            if not path:
                return None
            operations.append(
                {"type": "add_file", "path": path, "content": "\n".join(content)}
            )
            continue
        if line.startswith("*** Delete File: "):
            path = line.removeprefix("*** Delete File: ").strip()
            if not path:
                return None
            operations.append({"type": "delete_file", "path": path})
            index += 1
            continue
        if line.startswith("*** Update File: "):
            operation, index = _parse_apply_patch_update_operation(lines, index, end)
            if operation is None:
                return None
            operations.append(operation)
            continue
        return None
    return operations


def _parse_apply_patch_update_operation(
    lines: list[str], index: int, end: int
) -> tuple[dict[str, Any] | None, int]:
    path = lines[index].removeprefix("*** Update File: ").strip()
    if not path:
        return None, index
    operation: dict[str, Any] = {"type": "update_file", "path": path}
    index += 1
    if index < end and lines[index].startswith("*** Move to: "):
        move_to = lines[index].removeprefix("*** Move to: ").strip()
        if move_to:
            operation["move_to"] = move_to
        index += 1
    hunks: list[dict[str, Any]] = []
    while index < end and not lines[index].startswith("*** "):
        if not lines[index].startswith("@@"):
            return None, index
        index += 1
        hunk_lines: list[dict[str, str]] = []
        while (
            index < end
            and not lines[index].startswith("@@")
            and not lines[index].startswith("*** ")
        ):
            line = lines[index]
            if not line:
                return None, index
            op = line[0]
            text = line[1:]
            if op == " ":
                hunk_lines.append({"op": "context", "text": text})
            elif op == "+":
                hunk_lines.append({"op": "add", "text": text})
            elif op == "-":
                hunk_lines.append({"op": "remove", "text": text})
            else:
                return None, index
            index += 1
        if index < end and lines[index] == "*** End of File":
            hunk_lines.append({"op": "eof", "text": ""})
            index += 1
        if not hunk_lines:
            return None, index
        hunks.append({"lines": hunk_lines})
    if not hunks:
        return None, index
    operation["hunks"] = hunks
    return operation, index


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
