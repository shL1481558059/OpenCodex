from __future__ import annotations

import json
import time
import urllib.error
import urllib.request
import uuid
from copy import deepcopy
from typing import Any


WEB_SEARCH_TOOL_NAME = "web_search"
DEFAULT_MAX_WEB_SEARCH_CALLS = 5
TAVILY_SEARCH_URL = "https://api.tavily.com/search"
TAVILY_TIMEOUT_SECONDS = 15


def request_declares_web_search(payload: dict[str, Any]) -> bool:
    for tool in payload.get("tools", []) or []:
        if isinstance(tool, dict) and tool.get("type") == WEB_SEARCH_TOOL_NAME:
            return True
    return False


def max_web_search_calls(payload: dict[str, Any]) -> int:
    value = payload.get("max_tool_calls")
    if isinstance(value, bool):
        return DEFAULT_MAX_WEB_SEARCH_CALLS
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        return DEFAULT_MAX_WEB_SEARCH_CALLS
    return max(0, parsed)


def extract_tool_calls(payload: dict[str, Any], protocol: str) -> list[dict[str, Any]]:
    if protocol == "chat":
        choice = (payload.get("choices") or [{}])[0] if isinstance(payload.get("choices"), list) else {}
        message = choice.get("message", {}) if isinstance(choice, dict) else {}
        result = []
        for index, tool_call in enumerate(message.get("tool_calls", []) or []):
            if not isinstance(tool_call, dict):
                continue
            function = tool_call.get("function") or {}
            result.append(
                {
                    "id": tool_call.get("id") or f"call_{uuid.uuid4().hex}",
                    "index": index,
                    "name": function.get("name"),
                    "arguments": function.get("arguments", "{}"),
                    "raw": deepcopy(tool_call),
                }
            )
        return result
    if protocol == "messages":
        result = []
        for index, block in enumerate(payload.get("content", []) or []):
            if not isinstance(block, dict) or block.get("type") != "tool_use":
                continue
            result.append(
                {
                    "id": block.get("id") or f"call_{uuid.uuid4().hex}",
                    "index": index,
                    "name": block.get("name"),
                    "arguments": json.dumps(block.get("input") or {}, ensure_ascii=False),
                    "raw": deepcopy(block),
                }
            )
        return result
    return []


def web_search_calls(tool_calls: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return [
        tool_call
        for tool_call in tool_calls
        if str(tool_call.get("name") or "") == WEB_SEARCH_TOOL_NAME
    ]


def non_web_search_calls(tool_calls: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return [
        tool_call
        for tool_call in tool_calls
        if str(tool_call.get("name") or "") != WEB_SEARCH_TOOL_NAME
    ]


def append_tool_results(
    upstream_request: dict[str, Any],
    upstream_response: dict[str, Any],
    protocol: str,
    results: list[dict[str, Any]],
) -> dict[str, Any]:
    request = deepcopy(upstream_request)
    if protocol == "chat":
        messages = request.setdefault("messages", [])
        choice = (upstream_response.get("choices") or [{}])[0]
        message = deepcopy(choice.get("message") or {})
        messages.append(
            {
                key: value
                for key, value in message.items()
                if key in {"role", "content", "tool_calls", "reasoning_content"}
            }
        )
        for result in results:
            messages.append(
                {
                    "role": "tool",
                    "tool_call_id": result["call_id"],
                    "content": result["tool_result"],
                }
            )
        return request

    if protocol == "messages":
        messages = request.setdefault("messages", [])
        content = deepcopy(upstream_response.get("content") or [])
        if content:
            messages.append({"role": "assistant", "content": content})
        for result in results:
            messages.append(
                {
                    "role": "user",
                    "content": [
                        {
                            "type": "tool_result",
                            "tool_use_id": result["call_id"],
                            "content": result["tool_result"],
                        }
                    ],
                }
            )
        return request

    return request


def parse_web_search_query(arguments: Any) -> tuple[str | None, str | None]:
    value = arguments
    if isinstance(value, str):
        try:
            value = json.loads(value or "{}")
        except json.JSONDecodeError:
            return None, "web_search arguments must be valid JSON"
    if not isinstance(value, dict):
        return None, "web_search arguments must be an object"
    extra_keys = sorted(str(key) for key in value if key != "query")
    if extra_keys:
        return None, "web_search only supports the query argument"
    query = str(value.get("query") or "").strip()
    if not query:
        return None, "web_search query is required"
    return query, None


def make_tool_result(
    *,
    call_id: str,
    query: str | None,
    status: str,
    answer: str = "",
    results: list[dict[str, Any]] | None = None,
    error: str | None = None,
    log_error: str | None = None,
    error_type: str | None = None,
    http_status: int | None = None,
    key: dict[str, Any] | None = None,
    raw: dict[str, Any] | None = None,
) -> dict[str, Any]:
    result_payload = {
        "answer": answer or "",
        "results": results or [],
        "error": error,
    }
    tool_result = json.dumps(result_payload, ensure_ascii=False)
    return {
        "call_id": call_id,
        "query": query or "",
        "status": status,
        "tool_result": tool_result,
        "opencodex_result": result_payload,
        "log_error": log_error if log_error is not None else error,
        "provider": key.get("provider") if key else None,
        "key_id": key.get("id") if key else None,
        "key_position": key.get("position") if key else None,
        "key_usage_count": key.get("usage_count") if key else None,
        "key_usage_limit": key.get("key_usage_limit") if key else None,
        "error_type": error_type,
        "http_status": http_status,
        "raw": raw,
    }


def tavily_search(api_key: str, query: str, timeout: int = TAVILY_TIMEOUT_SECONDS) -> dict[str, Any]:
    payload = {
        "query": query,
        "search_depth": "basic",
        "max_results": 5,
        "include_answer": "basic",
        "include_raw_content": False,
        "include_usage": True,
    }
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        TAVILY_SEARCH_URL,
        data=data,
        headers={
            "content-type": "application/json",
            "authorization": f"Bearer {api_key}",
        },
        method="POST",
    )
    started = time.time()
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            body = response.read().decode("utf-8")
            raw = json.loads(body) if body else {}
            return {
                "ok": True,
                "status_code": response.status,
                "duration_ms": int((time.time() - started) * 1000),
                "raw": raw,
                "summary": tavily_summary(raw),
            }
    except urllib.error.HTTPError as exc:
        body_text = exc.read().decode("utf-8", errors="replace")
        return {
            "ok": False,
            "status_code": exc.code,
            "duration_ms": int((time.time() - started) * 1000),
            "error_type": "http_error",
            "error": f"Tavily returned HTTP {exc.code}",
            "raw": _decode_json_or_text(body_text),
            "summary": {"answer": "", "results": [], "error": f"Tavily returned HTTP {exc.code}"},
        }
    except urllib.error.URLError as exc:
        error = f"failed to reach Tavily: {exc.reason}"
        return _tavily_error_result(started, error)
    except TimeoutError:
        return _tavily_error_result(started, "Tavily request timed out")
    except json.JSONDecodeError:
        return _tavily_error_result(started, "Tavily returned invalid JSON")


def tavily_summary(raw: dict[str, Any]) -> dict[str, Any]:
    results = []
    for item in raw.get("results", []) or []:
        if not isinstance(item, dict):
            continue
        results.append(
            {
                "title": str(item.get("title") or ""),
                "url": str(item.get("url") or ""),
                "content": str(item.get("content") or ""),
                "score": item.get("score"),
            }
        )
    return {
        "answer": str(raw.get("answer") or ""),
        "results": results,
        "error": None,
    }


def build_web_search_item(result: dict[str, Any], include_result: bool = False) -> dict[str, Any]:
    item = {
        "id": result["call_id"],
        "type": "web_search_call",
        "status": "completed" if result.get("status") == "completed" else "failed",
        "action": {"type": "search", "query": result.get("query") or ""},
    }
    if include_result:
        item["opencodex_result"] = deepcopy(result.get("opencodex_result") or {})
    return item


def replace_web_search_function_items(
    response_payload: dict[str, Any],
    web_results: list[dict[str, Any]],
    *,
    include_result: bool,
) -> dict[str, Any]:
    by_call_id = {str(result.get("call_id")): result for result in web_results}
    output = []
    inserted: set[str] = set()
    for item in response_payload.get("output", []) or []:
        if (
            isinstance(item, dict)
            and item.get("type") == "function_call"
            and item.get("name") == WEB_SEARCH_TOOL_NAME
            and str(item.get("call_id")) in by_call_id
        ):
            call_id = str(item.get("call_id"))
            output.append(build_web_search_item(by_call_id[call_id], include_result=include_result))
            inserted.add(call_id)
            continue
        output.append(item)
    for result in web_results:
        call_id = str(result.get("call_id"))
        if call_id not in inserted:
            output.insert(0, build_web_search_item(result, include_result=include_result))
    response_payload["output"] = output
    return response_payload


def prepend_web_search_items(
    response_payload: dict[str, Any],
    web_results: list[dict[str, Any]],
    *,
    include_result: bool,
) -> dict[str, Any]:
    items = [
        build_web_search_item(result, include_result=include_result)
        for result in web_results
    ]
    response_payload["output"] = [*items, *(response_payload.get("output", []) or [])]
    return response_payload


def add_source_annotations(response_payload: dict[str, Any], web_results: list[dict[str, Any]]) -> dict[str, Any]:
    sources = _all_sources(web_results)
    if not sources:
        return response_payload
    message = _first_message_item(response_payload)
    if message is None:
        return response_payload
    content = message.setdefault("content", [])
    if not content:
        content.append({"type": "output_text", "text": ""})
    text_block = content[0]
    text = str(text_block.get("text") or "")
    source_lines = [
        f"- {source['title'] or source['url']}: {source['url']}"
        for source in sources
        if source.get("url")
    ]
    if source_lines:
        separator = "\n\n" if text else ""
        source_text = "来源:\n" + "\n".join(source_lines)
        start_offset = len(text) + len(separator)
        text = f"{text}{separator}{source_text}"
        text_block["text"] = text
    annotations = list(text_block.get("annotations") or [])
    for source in sources:
        url = source.get("url") or ""
        if not url:
            continue
        line = f"- {source['title'] or url}: {url}"
        start_index = text.find(line)
        if start_index < 0:
            start_index = max(0, start_offset if source_lines else 0)
        annotations.append(
            {
                "type": "url_citation",
                "start_index": start_index,
                "end_index": start_index + len(line),
                "url": url,
                "title": source.get("title") or url,
            }
        )
    if annotations:
        text_block["annotations"] = annotations
    return response_payload


def web_search_log(results: list[dict[str, Any]], upstream_calls: list[dict[str, Any]]) -> dict[str, Any]:
    upstream_call_summary = [
        {
            "iteration": call.get("iteration"),
            "after_limit": call.get("after_limit") is True,
            "tool_call_count": len(call.get("tool_calls") or []),
            "tool_names": [
                item.get("name")
                for item in call.get("tool_calls") or []
                if isinstance(item, dict)
            ],
        }
        for call in upstream_calls
        if isinstance(call, dict)
    ]
    return {
        "calls": [
            {
                "call_id": result.get("call_id"),
                "query": result.get("query"),
                "status": result.get("status"),
                "error": result.get("log_error"),
                "error_type": result.get("error_type"),
                "http_status": result.get("http_status"),
                "provider": result.get("provider"),
                "key_id": result.get("key_id"),
                "key_position": result.get("key_position"),
                "key_usage_count": result.get("key_usage_count"),
                "key_usage_limit": result.get("key_usage_limit"),
                "raw": result.get("raw"),
            }
            for result in results
        ],
        "upstream_calls": upstream_calls,
        "upstream_call_summary": upstream_call_summary,
    }


def _first_message_item(response_payload: dict[str, Any]) -> dict[str, Any] | None:
    for item in response_payload.get("output", []) or []:
        if isinstance(item, dict) and item.get("type") == "message":
            return item
    return None


def _all_sources(web_results: list[dict[str, Any]]) -> list[dict[str, Any]]:
    sources = []
    for result in web_results:
        payload = result.get("opencodex_result") or {}
        for item in payload.get("results", []) or []:
            if isinstance(item, dict):
                sources.append(item)
    return sources


def _decode_json_or_text(text: str) -> Any:
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return text


def _tavily_error_result(started: float, error: str) -> dict[str, Any]:
    return {
        "ok": False,
        "status_code": None,
        "duration_ms": int((time.time() - started) * 1000),
        "error_type": "request_error",
        "error": error,
        "raw": None,
        "summary": {"answer": "", "results": [], "error": error},
    }
