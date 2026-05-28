from __future__ import annotations

import os
import threading
from copy import deepcopy
from pathlib import Path
from typing import Any

from .db import init_db, read_channels, replace_channels
from .defaults import DEFAULT_RETRY_COUNT


CHANNEL_TYPES = {"responses", "chat", "messages"}
AUTH_MODES = {"pass_through_or_config", "pass_through", "config", "none"}
REMOVED_COMPAT_FIELDS = {"force_protocol", "tool_request_protocol", "by_protocol"}

EMPTY_CONFIG: dict[str, Any] = {
    "channels": [],
}


class ConfigError(ValueError):
    pass


class ConfigManager:
    def __init__(self, db_path: Path, default_timeout: int = 120):
        self.db_path = db_path
        self.default_timeout = default_timeout
        self._lock = threading.RLock()
        self._raw: dict[str, Any] = deepcopy(EMPTY_CONFIG)
        self._expanded: dict[str, Any] = deepcopy(EMPTY_CONFIG)
        init_db(self.db_path)
        self.reload()

    @property
    def raw(self) -> dict[str, Any]:
        with self._lock:
            return deepcopy(self._raw)

    @property
    def expanded(self) -> dict[str, Any]:
        with self._lock:
            return deepcopy(self._expanded)

    def reload(self) -> None:
        with self._lock:
            raw = strip_removed_config_fields({"channels": read_channels(self.db_path)})
            expanded = expand_env(raw)
            validate_config(expanded, self.default_timeout)
            self._raw = raw
            self._expanded = expanded

    def save(self, candidate: dict[str, Any]) -> dict[str, Any]:
        if not isinstance(candidate, dict):
            raise ConfigError("config must be a JSON object")
        candidate = strip_removed_config_fields(candidate)
        expanded = expand_env(candidate)
        validate_config(expanded, self.default_timeout)
        replace_channels(
            self.db_path,
            candidate.get("channels", []),
            default_timeout=self.default_timeout,
        )
        with self._lock:
            raw = strip_removed_config_fields({"channels": read_channels(self.db_path)})
            self._raw = raw
            self._expanded = expand_env(raw)
        return self.raw


def expand_env(value: Any) -> Any:
    if isinstance(value, str):
        return os.path.expandvars(value)
    if isinstance(value, list):
        return [expand_env(item) for item in value]
    if isinstance(value, dict):
        return {key: expand_env(item) for key, item in value.items()}
    return value


def strip_removed_config_fields(config: dict[str, Any]) -> dict[str, Any]:
    candidate = deepcopy(config)
    candidate.pop("routing", None)
    channels = candidate.get("channels")
    if isinstance(channels, list):
        for channel in channels:
            if not isinstance(channel, dict):
                continue
            compat = channel.get("compat")
            if isinstance(compat, dict):
                _strip_removed_compat_fields(compat)
            channel["models"] = normalize_model_mappings(channel.get("models", []))
    return candidate


def normalize_model_mappings(models: Any) -> list[dict[str, str]]:
    if models in (None, ""):
        return []
    if not isinstance(models, list):
        return []

    normalized: list[dict[str, str]] = []
    for item in models:
        if isinstance(item, str):
            model = item.strip()
            upstream_model = model
        elif isinstance(item, dict):
            model = str(item.get("model", "")).strip()
            upstream_model = str(item.get("upstream_model", "")).strip() or model
        else:
            continue
        if model:
            normalized.append({"model": model, "upstream_model": upstream_model})
    return normalized


def _strip_removed_compat_fields(compat: dict[str, Any]) -> None:
    for field in REMOVED_COMPAT_FIELDS:
        compat.pop(field, None)


def validate_config(config: dict[str, Any], default_timeout: int = 120) -> None:
    channels = config.get("channels", [])
    if not isinstance(channels, list):
        raise ConfigError("channels must be a list")

    ids: set[str] = set()
    for channel in channels:
        validate_channel(channel, default_timeout)
        channel_id = channel["id"]
        if channel_id in ids:
            raise ConfigError(f"duplicated channel id: {channel_id}")
        ids.add(channel_id)


def validate_channel(channel: Any, default_timeout: int) -> None:
    if not isinstance(channel, dict):
        raise ConfigError("each channel must be an object")
    channel_id = str(channel.get("id", "")).strip()
    if not channel_id:
        raise ConfigError("channel.id is required")
    channel_type = str(channel.get("type", "")).strip()
    if channel_type not in CHANNEL_TYPES:
        raise ConfigError(
            f"channel {channel_id} type must be one of {sorted(CHANNEL_TYPES)}"
        )
    baseurl = str(channel.get("baseurl", "")).strip()
    if not baseurl:
        raise ConfigError(f"channel {channel_id} baseurl is required")
    if not baseurl.startswith(("http://", "https://")):
        raise ConfigError(f"channel {channel_id} baseurl must start with http(s)://")

    auth_mode = str(channel.get("auth_mode", "pass_through_or_config")).strip()
    if auth_mode not in AUTH_MODES:
        raise ConfigError(f"channel {channel_id} auth_mode is invalid")

    timeout = channel.get("timeout_seconds", default_timeout)
    if not isinstance(timeout, int) or timeout <= 0:
        raise ConfigError(f"channel {channel_id} timeout_seconds must be positive")

    retry_count = channel.get("retry_count", DEFAULT_RETRY_COUNT)
    if (
        isinstance(retry_count, bool)
        or not isinstance(retry_count, int)
        or retry_count < 0
    ):
        raise ConfigError(f"channel {channel_id} retry_count must be a non-negative integer")

    headers = channel.get("headers", {})
    if not isinstance(headers, dict):
        raise ConfigError(f"channel {channel_id} headers must be an object")

    enabled = channel.get("enabled", True)
    if not isinstance(enabled, bool):
        raise ConfigError(f"channel {channel_id} enabled must be a boolean")

    validate_model_mappings(channel.get("models", []), channel_id)
    validate_compat(channel.get("compat", {}), channel_id)


def validate_model_mappings(models: Any, channel_id: str) -> None:
    if models in (None, ""):
        return
    if not isinstance(models, list):
        raise ConfigError(f"channel {channel_id} models must be a list")

    seen: set[str] = set()
    for index, mapping in enumerate(models, start=1):
        if not isinstance(mapping, dict):
            raise ConfigError(f"channel {channel_id} models[{index}] must be an object")
        model = str(mapping.get("model", "")).strip()
        upstream_model = str(mapping.get("upstream_model", "")).strip()
        if not model:
            raise ConfigError(f"channel {channel_id} models[{index}].model is required")
        if not upstream_model:
            mapping["upstream_model"] = model
        if model in seen:
            raise ConfigError(f"channel {channel_id} duplicated model mapping: {model}")
        seen.add(model)


def validate_compat(compat: Any, channel_id: str) -> None:
    if compat in (None, ""):
        return
    if not isinstance(compat, dict):
        raise ConfigError(f"channel {channel_id} compat must be an object")
    _validate_compat_fields(compat, f"channel {channel_id} compat")


def _validate_compat_fields(compat: dict[str, Any], label: str) -> None:
    object_fields = ("rename_params", "force_params", "default_params")
    list_fields = ("drop_params", "unsupported_params")
    bool_fields = ("fallback_thinking_on_tool_use",)
    for field in object_fields:
        value = compat.get(field, {})
        if not isinstance(value, dict):
            raise ConfigError(f"{label}.{field} must be an object")
    for field in list_fields:
        value = compat.get(field, [])
        if not isinstance(value, list):
            raise ConfigError(f"{label}.{field} must be a list")
    for field in bool_fields:
        if field in compat and not isinstance(compat.get(field), bool):
            raise ConfigError(f"{label}.{field} must be a boolean")
