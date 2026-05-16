from __future__ import annotations

import threading
from collections import OrderedDict
from typing import Any


class ReasoningCache:
    def __init__(self, max_entries: int = 1000):
        self.max_entries = max_entries
        self._lock = threading.RLock()
        self._by_tool_call_id: OrderedDict[tuple[str, str], str] = OrderedDict()
        self._thinking_by_tool_call_id: OrderedDict[
            tuple[str, str], list[dict[str, Any]]
        ] = OrderedDict()

    def remember_chat_response(
        self, response: dict[str, Any], namespace: str | None = None
    ) -> None:
        choices = response.get("choices")
        if not isinstance(choices, list):
            return
        cache_namespace = _normalize_namespace(namespace)
        with self._lock:
            for choice in choices:
                if not isinstance(choice, dict):
                    continue
                message = choice.get("message")
                if not isinstance(message, dict):
                    continue
                reasoning_content = message.get("reasoning_content")
                tool_calls = message.get("tool_calls")
                if not reasoning_content or not isinstance(tool_calls, list):
                    continue
                for tool_call in tool_calls:
                    if not isinstance(tool_call, dict):
                        continue
                    tool_call_id = str(tool_call.get("id") or "").strip()
                    if tool_call_id:
                        cache_key = (cache_namespace, tool_call_id)
                        self._by_tool_call_id[cache_key] = str(reasoning_content)
                        self._by_tool_call_id.move_to_end(cache_key)
            self._trim()

    def remember_messages_response(
        self, response: dict[str, Any], namespace: str | None = None
    ) -> None:
        content = response.get("content")
        if not isinstance(content, list):
            return
        thinking_blocks: list[dict[str, Any]] = []
        cache_namespace = _normalize_namespace(namespace)
        with self._lock:
            for block in content:
                if not isinstance(block, dict):
                    continue
                block_type = block.get("type")
                if block_type == "thinking":
                    thinking_blocks.append(dict(block))
                    continue
                if block_type == "tool_use" and thinking_blocks:
                    tool_use_id = str(block.get("id") or "").strip()
                    if tool_use_id:
                        cache_key = (cache_namespace, tool_use_id)
                        self._thinking_by_tool_call_id[cache_key] = [
                            dict(item) for item in thinking_blocks
                        ]
                        self._thinking_by_tool_call_id.move_to_end(cache_key)
            self._trim()

    def inject_chat_request(
        self, request_payload: dict[str, Any], namespace: str | None = None
    ) -> list[str]:
        messages = request_payload.get("messages")
        if not isinstance(messages, list):
            return []
        injected: list[str] = []
        cache_namespace = _normalize_namespace(namespace)
        with self._lock:
            for message in messages:
                if not isinstance(message, dict):
                    continue
                if message.get("role") != "assistant" or message.get("reasoning_content"):
                    continue
                tool_calls = message.get("tool_calls")
                if not isinstance(tool_calls, list):
                    continue
                reasoning_content = None
                for tool_call in tool_calls:
                    if not isinstance(tool_call, dict):
                        continue
                    tool_call_id = str(tool_call.get("id") or "").strip()
                    cache_key = (cache_namespace, tool_call_id)
                    if tool_call_id and cache_key in self._by_tool_call_id:
                        reasoning_content = self._by_tool_call_id[cache_key]
                        injected.append(tool_call_id)
                        break
                if reasoning_content:
                    message["reasoning_content"] = reasoning_content
        return injected

    def inject_messages_request(
        self, request_payload: dict[str, Any], namespace: str | None = None
    ) -> list[str]:
        messages = request_payload.get("messages")
        if not isinstance(messages, list):
            return []
        injected: list[str] = []
        cache_namespace = _normalize_namespace(namespace)
        with self._lock:
            for message in messages:
                if not isinstance(message, dict) or message.get("role") != "assistant":
                    continue
                content = message.get("content")
                if not isinstance(content, list):
                    continue
                if any(isinstance(block, dict) and block.get("type") == "thinking" for block in content):
                    continue
                for index, block in enumerate(content):
                    if not isinstance(block, dict) or block.get("type") != "tool_use":
                        continue
                    tool_use_id = str(block.get("id") or "").strip()
                    cache_key = (cache_namespace, tool_use_id)
                    thinking_blocks = self._thinking_by_tool_call_id.get(cache_key)
                    if thinking_blocks:
                        message["content"] = (
                            [dict(item) for item in thinking_blocks]
                            + [item for item in content if not _is_empty_text_block(item)]
                        )
                        injected.append(tool_use_id)
                        break
        return injected

    def _trim(self) -> None:
        while len(self._by_tool_call_id) > self.max_entries:
            self._by_tool_call_id.popitem(last=False)
        while len(self._thinking_by_tool_call_id) > self.max_entries:
            self._thinking_by_tool_call_id.popitem(last=False)


def _is_empty_text_block(value: Any) -> bool:
    return isinstance(value, dict) and value.get("type") == "text" and not value.get("text")


def _normalize_namespace(namespace: str | None) -> str:
    return str(namespace or "").strip()
