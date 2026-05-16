from __future__ import annotations

from dataclasses import dataclass
from fnmatch import fnmatchcase
from typing import Any

from .errors import RoutingError


@dataclass(frozen=True)
class RouteResult:
    channel: dict[str, Any]
    original_model: str
    upstream_model: str
    matched_pattern: str | None


def choose_channel(config: dict[str, Any], model: str | None) -> RouteResult:
    channels = {channel["id"]: channel for channel in config.get("channels", [])}
    if not channels:
        raise RoutingError("no channels configured")

    model = str(model or "").strip()
    routing = config.get("routing", {})
    for route in routing.get("model_routes", []) or []:
        pattern = str(route.get("pattern", "")).strip()
        if model and pattern and fnmatchcase(model, pattern):
            channel_id = route.get("channel")
            channel = channels.get(channel_id)
            if not channel:
                raise RoutingError(f"route channel does not exist: {channel_id}")
            return RouteResult(
                channel=channel,
                original_model=model,
                upstream_model=str(route.get("upstream_model") or model),
                matched_pattern=pattern,
            )

    default_channel = routing.get("default_channel")
    if default_channel:
        channel = channels.get(default_channel)
        if not channel:
            raise RoutingError(f"default channel does not exist: {default_channel}")
        return RouteResult(
            channel=channel,
            original_model=model,
            upstream_model=model,
            matched_pattern=None,
        )

    if len(channels) == 1:
        channel = next(iter(channels.values()))
        return RouteResult(
            channel=channel,
            original_model=model,
            upstream_model=model,
            matched_pattern=None,
        )

    raise RoutingError("no model route matched and no default channel configured")
