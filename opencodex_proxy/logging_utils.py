from __future__ import annotations

import json
import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Any


SENSITIVE_KEYS = {
    "authorization",
    "api_key",
    "apikey",
    "x-api-key",
    "cookie",
    "set-cookie",
    "opencodex_admin_password",
    "password",
}


class JsonFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        payload = {
            "time": self.formatTime(record, "%Y-%m-%dT%H:%M:%S%z"),
            "level": record.levelname,
            "message": record.getMessage(),
        }
        extra = getattr(record, "extra", None)
        if isinstance(extra, dict):
            payload.update(redact(extra))
        if record.exc_info:
            payload["exception"] = self.formatException(record.exc_info)
        return json.dumps(payload, ensure_ascii=False)


def configure_logging(log_path: Path, log_level: str) -> logging.Logger:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    logger = logging.getLogger("opencodex_proxy")
    logger.setLevel(getattr(logging, log_level))
    for handler in logger.handlers:
        handler.close()
    logger.handlers.clear()
    logger.propagate = False

    handler = RotatingFileHandler(
        log_path, maxBytes=5 * 1024 * 1024, backupCount=3, encoding="utf-8"
    )
    handler.setFormatter(JsonFormatter())
    handler.setLevel(getattr(logging, log_level))
    logger.addHandler(handler)
    return logger


def log_event(logger: logging.Logger, level: str, message: str, **extra: Any) -> None:
    logger.log(getattr(logging, level), message, extra={"extra": extra})


def redact(value: Any) -> Any:
    if isinstance(value, dict):
        result = {}
        for key, item in value.items():
            if str(key).lower() in SENSITIVE_KEYS:
                result[key] = _mask(item)
            else:
                result[key] = redact(item)
        return result
    if isinstance(value, list):
        return [redact(item) for item in value]
    return value


def _mask(value: Any) -> str:
    text = "" if value is None else str(value)
    if not text:
        return "***"
    if len(text) <= 8:
        return "***"
    return f"{text[:4]}...{text[-4:]}"


def event_for_view(event: dict[str, Any], view_level: str) -> dict[str, Any]:
    allowed = {
        "BASIC": {
            "time",
            "level",
            "message",
            "request_id",
            "path",
            "entry_protocol",
            "model",
            "channel_id",
            "status_code",
            "duration_ms",
            "error",
        },
        "DEBUG": {
            "time",
            "level",
            "message",
            "request_id",
            "path",
            "entry_protocol",
            "model",
            "channel_id",
            "status_code",
            "duration_ms",
            "error",
            "route_pattern",
            "upstream_model",
            "compat",
            "params",
            "upstream_error",
        },
        "TRACE": set(event.keys()),
    }
    keys = allowed.get(view_level, allowed["BASIC"])
    return {key: event[key] for key in event.keys() if key in keys}


def read_log_events(log_path: Path, view_level: str, limit: int = 200) -> list[dict[str, Any]]:
    if not log_path.exists():
        return []
    limit = max(1, min(limit, 1000))
    lines = _tail_lines(log_path, limit * 3)
    events: list[dict[str, Any]] = []
    for line in lines:
        try:
            event = json.loads(line)
        except json.JSONDecodeError:
            continue
        if not isinstance(event, dict):
            continue
        events.append(event_for_view(redact(event), view_level))
    return events[-limit:]


def _tail_lines(path: Path, max_lines: int) -> list[str]:
    with path.open("rb") as handle:
        handle.seek(0, 2)
        end = handle.tell()
        block_size = 4096
        data = b""
        lines: list[bytes] = []
        while end > 0 and len(lines) <= max_lines:
            read_size = min(block_size, end)
            end -= read_size
            handle.seek(end)
            data = handle.read(read_size) + data
            lines = data.splitlines()
        return [line.decode("utf-8", errors="replace") for line in lines[-max_lines:]]
