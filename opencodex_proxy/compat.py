from __future__ import annotations

from copy import deepcopy
from typing import Any

from .errors import BadRequestError


def apply_compat(payload: dict[str, Any], compat: dict[str, Any] | None) -> tuple[dict[str, Any], list[str]]:
    result = deepcopy(payload)
    details: list[str] = []
    compat = compat or {}

    for key, value in (compat.get("default_params") or {}).items():
        if key not in result:
            result[key] = value
            details.append(f"default:{key}")

    for source, target in (compat.get("rename_params") or {}).items():
        if source in result:
            if target not in result:
                result[target] = result[source]
            del result[source]
            details.append(f"rename:{source}->{target}")

    for key in compat.get("drop_params") or []:
        if key in result:
            del result[key]
            details.append(f"drop:{key}")

    for key, value in (compat.get("force_params") or {}).items():
        result[key] = value
        details.append(f"force:{key}")

    unsupported = [key for key in compat.get("unsupported_params") or [] if key in result]
    if unsupported:
        joined = ", ".join(sorted(unsupported))
        raise BadRequestError(f"upstream does not support parameter(s): {joined}")

    return result, details
