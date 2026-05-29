from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from .errors import RoutingError


@dataclass(frozen=True)
class RouteResult:
    channel: dict[str, Any]
    original_model: str
    upstream_model: str


def choose_channel(config: dict[str, Any], model: str | None) -> RouteResult:
    channels = config.get("channels", [])
    if not channels:
        raise RoutingError("no enabled channels configured")

    model = str(model or "").strip()
    enabled_channels = [channel for channel in channels if _channel_enabled(channel)]
    if not enabled_channels:
        raise RoutingError("no enabled channels configured")

    has_model_mappings = any(_model_mappings(channel) for channel in enabled_channels)
    if has_model_mappings:
        for channel in enabled_channels:
            for mapping in _model_mappings(channel):
                if mapping.get("model") == model:
                    return RouteResult(
                        channel=channel,
                        original_model=model,
                        upstream_model=mapping.get("upstream_model") or model,
                    )
        raise RoutingError(f"no enabled channel configured for model: {model}")

    channel = enabled_channels[0]
    return RouteResult(
        channel=channel,
        original_model=model,
        upstream_model=model,
    )


def _model_mappings(channel: dict[str, Any]) -> list[dict[str, str]]:
    mappings = channel.get("models", [])
    if not isinstance(mappings, list):
        return []
    return [mapping for mapping in mappings if isinstance(mapping, dict)]


def _channel_enabled(channel: dict[str, Any]) -> bool:
    return channel.get("enabled", True) is not False
