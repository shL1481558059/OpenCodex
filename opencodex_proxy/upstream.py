from __future__ import annotations

import json
import urllib.error
import urllib.request
from collections.abc import Iterator
from typing import Any

from .errors import UpstreamError


ENDPOINTS = {
    "responses": "/responses",
    "chat": "/chat/completions",
    "messages": "/messages",
}


def post_upstream(
    channel: dict[str, Any],
    payload: dict[str, Any],
    client_authorization: str | None,
    default_timeout: int,
) -> dict[str, Any]:
    channel_type = channel["type"]
    url = _join_url(channel["baseurl"], ENDPOINTS[channel_type])
    headers = build_headers(channel, client_authorization)
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(url, data=data, headers=headers, method="POST")
    timeout = int(channel.get("timeout_seconds") or default_timeout)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            body = response.read().decode("utf-8")
            return json.loads(body) if body else {}
    except urllib.error.HTTPError as exc:
        body_text = exc.read().decode("utf-8", errors="replace")
        body = _decode_body(body_text)
        raise UpstreamError(
            f"upstream returned HTTP {exc.code}",
            status_code=exc.code,
            body=body,
            channel_id=channel.get("id"),
        ) from exc
    except urllib.error.URLError as exc:
        raise UpstreamError(
            f"failed to reach upstream: {exc.reason}",
            status_code=502,
            channel_id=channel.get("id"),
        ) from exc
    except TimeoutError as exc:
        raise UpstreamError(
            "upstream request timed out",
            status_code=504,
            channel_id=channel.get("id"),
        ) from exc
    except json.JSONDecodeError as exc:
        raise UpstreamError(
            "upstream returned invalid JSON",
            status_code=502,
            channel_id=channel.get("id"),
        ) from exc


def stream_upstream(
    channel: dict[str, Any],
    payload: dict[str, Any],
    client_authorization: str | None,
    default_timeout: int,
) -> Iterator[str]:
    channel_type = channel["type"]
    url = _join_url(channel["baseurl"], ENDPOINTS[channel_type])
    headers = build_headers(channel, client_authorization)
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(url, data=data, headers=headers, method="POST")
    timeout = int(channel.get("timeout_seconds") or default_timeout)
    try:
        response = urllib.request.urlopen(request, timeout=timeout)
    except urllib.error.HTTPError as exc:
        body_text = exc.read().decode("utf-8", errors="replace")
        body = _decode_body(body_text)
        raise UpstreamError(
            f"upstream returned HTTP {exc.code}",
            status_code=exc.code,
            body=body,
            channel_id=channel.get("id"),
        ) from exc
    except urllib.error.URLError as exc:
        raise UpstreamError(
            f"failed to reach upstream: {exc.reason}",
            status_code=502,
            channel_id=channel.get("id"),
        ) from exc
    except TimeoutError as exc:
        raise UpstreamError(
            "upstream request timed out",
            status_code=504,
            channel_id=channel.get("id"),
        ) from exc

    def lines() -> Iterator[str]:
        with response:
            for raw_line in response:
                yield raw_line.decode("utf-8", errors="replace")

    return lines()


def build_headers(
    channel: dict[str, Any], client_authorization: str | None
) -> dict[str, str]:
    headers = {
        "content-type": "application/json",
        "user-agent": "OpenCodex-Proxy/0.1",
    }
    for key, value in (channel.get("headers") or {}).items():
        headers[str(key)] = str(value)

    auth_mode = channel.get("auth_mode") or "pass_through_or_config"
    api_key = channel.get("apikey") or ""
    auth_value = None
    if auth_mode == "pass_through":
        auth_value = client_authorization
    elif auth_mode == "config":
        auth_value = f"Bearer {api_key}" if api_key else None
    elif auth_mode == "pass_through_or_config":
        auth_value = client_authorization or (f"Bearer {api_key}" if api_key else None)

    if channel.get("type") == "messages":
        if auth_value and auth_value.lower().startswith("bearer "):
            headers["x-api-key"] = auth_value.split(" ", 1)[1]
        elif api_key:
            headers["x-api-key"] = api_key
        if "anthropic-version" not in {key.lower(): value for key, value in headers.items()}:
            headers["anthropic-version"] = "2023-06-01"
    elif auth_value:
        headers["authorization"] = auth_value

    return headers


def _join_url(baseurl: str, endpoint: str) -> str:
    base = baseurl.rstrip("/")
    if base.endswith("/v1"):
        return f"{base}{endpoint}"
    return f"{base}/v1{endpoint}"


def _decode_body(body_text: str) -> object:
    if not body_text:
        return None
    try:
        return json.loads(body_text)
    except json.JSONDecodeError:
        return body_text[:2000]
