from __future__ import annotations

import datetime
import email.utils
import http.client
import json
import time
import urllib.error
import urllib.request
from collections.abc import Iterator
from typing import Any

from .defaults import DEFAULT_RETRY_COUNT
from .errors import UpstreamError


ENDPOINTS = {
    "responses": "/responses",
    "chat": "/chat/completions",
    "messages": "/messages",
}
BASE_RETRY_DELAY_SECONDS = 0.5
MAX_RETRY_DELAY_SECONDS = 8.0
MAX_RETRY_AFTER_SECONDS = 30.0


def post_upstream(
    channel: dict[str, Any],
    payload: dict[str, Any],
    default_timeout: int,
) -> dict[str, Any]:
    channel_type = channel["type"]
    url = _join_url(channel["baseurl"], ENDPOINTS[channel_type])
    headers = build_headers(channel)
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(url, data=data, headers=headers, method="POST")
    timeout = int(channel.get("timeout_seconds") or default_timeout)
    retry_count = _channel_retry_count(channel)
    try:
        with _urlopen_with_retries(request, timeout, retry_count) as response:
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
    except http.client.RemoteDisconnected as exc:
        raise UpstreamError(
            f"failed to reach upstream: {exc}",
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


def list_upstream_models(
    channel: dict[str, Any],
    default_timeout: int,
) -> dict[str, Any]:
    url = _join_url(channel["baseurl"], "/models")
    headers = build_headers(channel)
    request = urllib.request.Request(url, headers=headers, method="GET")
    timeout = int(channel.get("timeout_seconds") or default_timeout)
    retry_count = _channel_retry_count(channel)
    try:
        with _urlopen_with_retries(request, timeout, retry_count) as response:
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
    except http.client.RemoteDisconnected as exc:
        raise UpstreamError(
            f"failed to reach upstream: {exc}",
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
    default_timeout: int,
) -> Iterator[str]:
    channel_type = channel["type"]
    url = _join_url(channel["baseurl"], ENDPOINTS[channel_type])
    headers = build_headers(channel)
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(url, data=data, headers=headers, method="POST")
    timeout = int(channel.get("timeout_seconds") or default_timeout)
    retry_count = _channel_retry_count(channel)
    try:
        response = _urlopen_with_retries(request, timeout, retry_count)
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
    except http.client.RemoteDisconnected as exc:
        raise UpstreamError(
            f"failed to reach upstream: {exc}",
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


def build_headers(channel: dict[str, Any]) -> dict[str, str]:
    headers = {
        "content-type": "application/json",
        "user-agent": "OpenCodex-Proxy/0.1",
    }
    for key, value in (channel.get("headers") or {}).items():
        headers[str(key)] = str(value)

    auth_mode = channel.get("auth_mode") or "config"
    api_key = channel.get("apikey") or ""
    auth_value = None
    if auth_mode == "config":
        auth_value = f"Bearer {api_key}" if api_key else None

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


def _urlopen_with_retries(
    request: urllib.request.Request,
    timeout: int,
    retry_count: int,
):
    for retry_index in range(retry_count + 1):
        try:
            return urllib.request.urlopen(request, timeout=timeout)
        except (
            urllib.error.HTTPError,
            urllib.error.URLError,
            http.client.RemoteDisconnected,
            TimeoutError,
        ) as exc:
            if retry_index >= retry_count or not _is_retryable_exception(exc):
                raise
            delay = _retry_delay_seconds(retry_index, exc)
            _close_retry_exception(exc)
            time.sleep(delay)

    raise RuntimeError("unreachable retry state")


def _channel_retry_count(channel: dict[str, Any]) -> int:
    retry_count = channel.get("retry_count", DEFAULT_RETRY_COUNT)
    if (
        isinstance(retry_count, bool)
        or not isinstance(retry_count, int)
        or retry_count < 0
    ):
        return DEFAULT_RETRY_COUNT
    return retry_count


def _is_retryable_exception(exc: BaseException) -> bool:
    if isinstance(exc, urllib.error.HTTPError):
        return exc.code == 429 or 500 <= exc.code <= 599
    return isinstance(exc, (urllib.error.URLError, http.client.RemoteDisconnected, TimeoutError))


def _retry_delay_seconds(retry_index: int, exc: BaseException) -> float:
    retry_after = _retry_after_seconds(exc)
    if retry_after is not None:
        return min(retry_after, MAX_RETRY_AFTER_SECONDS)
    delay = BASE_RETRY_DELAY_SECONDS * (2 ** retry_index)
    return min(delay, MAX_RETRY_DELAY_SECONDS)


def _retry_after_seconds(exc: BaseException) -> float | None:
    if not isinstance(exc, urllib.error.HTTPError):
        return None
    raw = exc.headers.get("Retry-After") if exc.headers else None
    if raw is None:
        return None

    text = str(raw).strip()
    try:
        seconds = float(text)
    except ValueError:
        seconds = None
    if seconds is not None:
        return seconds if seconds >= 0 else None

    try:
        parsed = email.utils.parsedate_to_datetime(text)
    except (TypeError, ValueError, IndexError, OverflowError):
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=datetime.timezone.utc)
    now = datetime.datetime.now(datetime.timezone.utc)
    return max(0.0, (parsed - now).total_seconds())


def _close_retry_exception(exc: BaseException) -> None:
    close = getattr(exc, "close", None)
    if callable(close):
        close()


def _decode_body(body_text: str) -> object:
    if not body_text:
        return None
    try:
        return json.loads(body_text)
    except json.JSONDecodeError:
        return body_text[:2000]
