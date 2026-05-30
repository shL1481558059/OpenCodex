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
