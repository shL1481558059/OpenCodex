from __future__ import annotations

import json
import os
import tempfile
import threading
from copy import deepcopy
from pathlib import Path
from typing import Any


CHANNEL_TYPES = {"responses", "chat", "messages"}
AUTH_MODES = {"pass_through_or_config", "pass_through", "config", "none"}

EMPTY_CONFIG: dict[str, Any] = {
    "channels": [],
    "routing": {"default_channel": "", "model_routes": []},
}


class ConfigError(ValueError):
    pass


class ConfigManager:
    def __init__(self, path: Path, default_timeout: int = 120):
        self.path = path
        self.default_timeout = default_timeout
        self._lock = threading.RLock()
        self._raw: dict[str, Any] = deepcopy(EMPTY_CONFIG)
        self._expanded: dict[str, Any] = deepcopy(EMPTY_CONFIG)
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
            raw = self._read_raw()
            expanded = expand_env(raw)
            validate_config(expanded, self.default_timeout)
            self._raw = raw
            self._expanded = expanded

    def save(self, candidate: dict[str, Any]) -> dict[str, Any]:
        if not isinstance(candidate, dict):
            raise ConfigError("config must be a JSON object")
        expanded = expand_env(candidate)
        validate_config(expanded, self.default_timeout)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        fd, tmp_name = tempfile.mkstemp(
            prefix=f".{self.path.name}.", suffix=".tmp", dir=str(self.path.parent)
        )
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as handle:
                json.dump(candidate, handle, indent=2, ensure_ascii=False)
                handle.write("\n")
            os.replace(tmp_name, self.path)
        finally:
            if os.path.exists(tmp_name):
                os.unlink(tmp_name)
        with self._lock:
            self._raw = deepcopy(candidate)
            self._expanded = expanded
        return self.raw

    def _read_raw(self) -> dict[str, Any]:
        if not self.path.exists():
            return deepcopy(EMPTY_CONFIG)
        try:
            with self.path.open("r", encoding="utf-8") as handle:
                data = json.load(handle)
        except json.JSONDecodeError as exc:
            raise ConfigError(f"invalid JSON config: {exc}") from exc
        if not isinstance(data, dict):
            raise ConfigError("config root must be a JSON object")
        return data


def expand_env(value: Any) -> Any:
    if isinstance(value, str):
        return os.path.expandvars(value)
    if isinstance(value, list):
        return [expand_env(item) for item in value]
    if isinstance(value, dict):
        return {key: expand_env(item) for key, item in value.items()}
    return value


def validate_config(config: dict[str, Any], default_timeout: int = 120) -> None:
    channels = config.get("channels", [])
    routing = config.get("routing", {})
    if not isinstance(channels, list):
        raise ConfigError("channels must be a list")
    if not isinstance(routing, dict):
        raise ConfigError("routing must be an object")

    ids: set[str] = set()
    for channel in channels:
        validate_channel(channel, default_timeout)
        channel_id = channel["id"]
        if channel_id in ids:
            raise ConfigError(f"duplicated channel id: {channel_id}")
        ids.add(channel_id)

    default_channel = routing.get("default_channel", "")
    if default_channel and default_channel not in ids:
        raise ConfigError(f"routing.default_channel does not exist: {default_channel}")

    model_routes = routing.get("model_routes", [])
    if not isinstance(model_routes, list):
        raise ConfigError("routing.model_routes must be a list")
    for index, route in enumerate(model_routes):
        if not isinstance(route, dict):
            raise ConfigError(f"routing.model_routes[{index}] must be an object")
        if not str(route.get("pattern", "")).strip():
            raise ConfigError(f"routing.model_routes[{index}].pattern is required")
        channel_id = str(route.get("channel", "")).strip()
        if not channel_id:
            raise ConfigError(f"routing.model_routes[{index}].channel is required")
        if channel_id not in ids:
            raise ConfigError(
                f"routing.model_routes[{index}].channel does not exist: {channel_id}"
            )


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

    headers = channel.get("headers", {})
    if not isinstance(headers, dict):
        raise ConfigError(f"channel {channel_id} headers must be an object")

    validate_compat(channel.get("compat", {}), channel_id)


def validate_compat(compat: Any, channel_id: str) -> None:
    if compat in (None, ""):
        return
    if not isinstance(compat, dict):
        raise ConfigError(f"channel {channel_id} compat must be an object")
    _validate_compat_fields(compat, f"channel {channel_id} compat")

    for field in ("force_protocol", "tool_request_protocol"):
        protocol = compat.get(field)
        if protocol in (None, ""):
            continue
        if protocol not in CHANNEL_TYPES:
            raise ConfigError(
                f"channel {channel_id} compat.{field} must be one of {sorted(CHANNEL_TYPES)}"
            )

    by_protocol = compat.get("by_protocol", {})
    if by_protocol in (None, ""):
        return
    if not isinstance(by_protocol, dict):
        raise ConfigError(f"channel {channel_id} compat.by_protocol must be an object")
    for protocol, protocol_compat in by_protocol.items():
        if protocol not in CHANNEL_TYPES:
            raise ConfigError(
                f"channel {channel_id} compat.by_protocol key must be one of {sorted(CHANNEL_TYPES)}"
            )
        if not isinstance(protocol_compat, dict):
            raise ConfigError(
                f"channel {channel_id} compat.by_protocol.{protocol} must be an object"
            )
        _validate_compat_fields(
            protocol_compat,
            f"channel {channel_id} compat.by_protocol.{protocol}",
        )


def _validate_compat_fields(compat: dict[str, Any], label: str) -> None:
    object_fields = ("rename_params", "force_params", "default_params")
    list_fields = ("drop_params", "unsupported_params")
    for field in object_fields:
        value = compat.get(field, {})
        if not isinstance(value, dict):
            raise ConfigError(f"{label}.{field} must be an object")
    for field in list_fields:
        value = compat.get(field, [])
        if not isinstance(value, list):
            raise ConfigError(f"{label}.{field} must be a list")
