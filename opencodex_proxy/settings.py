from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

from dotenv import load_dotenv


LOG_LEVELS = {"DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"}
LOG_VIEW_LEVELS = {"BASIC", "DEBUG", "TRACE"}


class SettingsError(ValueError):
    pass


@dataclass(frozen=True)
class Settings:
    host: str
    port: int
    admin_password: str
    db_path: Path
    log_path: Path
    log_level: str
    log_view_level: str
    default_timeout: int
    secret_key: str
    admin_username: str = "admin"

    @classmethod
    def from_env(cls) -> "Settings":
        load_dotenv()

        admin_password = os.getenv("OPENCODEX_ADMIN_PASSWORD", "").strip()
        if not admin_password:
            raise SettingsError("OPENCODEX_ADMIN_PASSWORD is required")
        admin_username = os.getenv("OPENCODEX_ADMIN_USERNAME", "admin").strip() or "admin"

        log_level = os.getenv("OPENCODEX_LOG_LEVEL", "INFO").strip().upper()
        if log_level not in LOG_LEVELS:
            raise SettingsError(
                f"OPENCODEX_LOG_LEVEL must be one of {sorted(LOG_LEVELS)}"
            )

        log_view_level = os.getenv("OPENCODEX_LOG_VIEW_LEVEL", "BASIC").strip().upper()
        if log_view_level not in LOG_VIEW_LEVELS:
            raise SettingsError(
                f"OPENCODEX_LOG_VIEW_LEVEL must be one of {sorted(LOG_VIEW_LEVELS)}"
            )

        return cls(
            host=os.getenv("OPENCODEX_HOST", "0.0.0.0").strip() or "0.0.0.0",
            port=_parse_positive_int("OPENCODEX_PORT", 8000),
            admin_password=admin_password,
            db_path=Path(os.getenv("OPENCODEX_DB_PATH", "logs/opencodex.db")),
            log_path=Path(os.getenv("OPENCODEX_LOG_PATH", "logs/opencodex.log")),
            log_level=log_level,
            log_view_level=log_view_level,
            default_timeout=_parse_positive_int("OPENCODEX_DEFAULT_TIMEOUT", 120),
            secret_key=os.getenv(
                "OPENCODEX_SECRET_KEY", "change-me-session-secret"
            ),
            admin_username=admin_username,
        )


def _parse_positive_int(name: str, default: int) -> int:
    raw = os.getenv(name)
    if raw is None or raw.strip() == "":
        return default
    try:
        value = int(raw)
    except ValueError as exc:
        raise SettingsError(f"{name} must be an integer") from exc
    if value <= 0:
        raise SettingsError(f"{name} must be greater than zero")
    return value
