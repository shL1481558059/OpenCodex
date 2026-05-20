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
        raise RoutingError("no channels configured")

    model = str(model or "").strip()
    for channel in channels:
        if _channel_enabled(channel):
            return RouteResult(
                channel=channel,
                original_model=model,
                upstream_model=model,
            )

    raise RoutingError("no enabled channels configured")


def _channel_enabled(channel: dict[str, Any]) -> bool:
    return channel.get("enabled", True) is not False
