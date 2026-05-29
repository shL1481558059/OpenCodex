from __future__ import annotations

import json
import queue
import sqlite3
import threading
import time
from pathlib import Path
from typing import Any

from .defaults import DEFAULT_RETRY_COUNT


REQUEST_LOGS_SCHEMA = """
CREATE TABLE IF NOT EXISTS request_logs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    request_id TEXT,
    created_at REAL,
    method TEXT,
    path TEXT,
    client_ip TEXT,
    request_headers TEXT,
    request_body TEXT,
    model TEXT,
    upstream_model TEXT,
    channel_id TEXT,
    is_stream INTEGER DEFAULT 0,
    ttft_ms INTEGER,
    duration_ms INTEGER,
    status_code INTEGER,
    response_body TEXT,
    input_tokens INTEGER,
    cached_tokens INTEGER,
    output_tokens INTEGER,
    cost REAL,
    web_search_json TEXT,
    error TEXT
);
"""

CHANNELS_SCHEMA = """
CREATE TABLE IF NOT EXISTS channels (
    id TEXT PRIMARY KEY,
    position INTEGER NOT NULL,
    name TEXT NOT NULL DEFAULT '',
    type TEXT NOT NULL,
    baseurl TEXT NOT NULL,
    apikey TEXT NOT NULL DEFAULT '',
    auth_mode TEXT NOT NULL DEFAULT 'pass_through_or_config',
    headers_json TEXT NOT NULL DEFAULT '{}',
    timeout_seconds INTEGER NOT NULL,
    retry_count INTEGER NOT NULL DEFAULT 3,
    compat_json TEXT NOT NULL DEFAULT '{}',
    models_json TEXT NOT NULL DEFAULT '[]',
    enabled INTEGER NOT NULL DEFAULT 1,
    created_at REAL NOT NULL,
    updated_at REAL NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_channels_position ON channels(position);
"""

WEB_SEARCH_SCHEMA = """
CREATE TABLE IF NOT EXISTS web_search_settings (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    enabled INTEGER NOT NULL DEFAULT 0,
    key_usage_limit INTEGER NOT NULL DEFAULT 1000,
    created_at REAL NOT NULL,
    updated_at REAL NOT NULL
);

CREATE TABLE IF NOT EXISTS tavily_keys (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    position INTEGER NOT NULL,
    api_key TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    usage_count INTEGER NOT NULL DEFAULT 0,
    created_at REAL NOT NULL,
    updated_at REAL NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_tavily_keys_position ON tavily_keys(position);
"""

TAVILY_KEY_USAGE_LIMIT = 1000

SCHEMA = REQUEST_LOGS_SCHEMA + "\n" + CHANNELS_SCHEMA + "\n" + WEB_SEARCH_SCHEMA


def init_db(db_path: Path) -> None:
    db_path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(str(db_path))
    conn.executescript(SCHEMA)
    _migrate_channels(conn)
    conn.commit()
    conn.close()


def _migrate_channels(conn: sqlite3.Connection) -> None:
    log_columns = {
        row[1]
        for row in conn.execute("PRAGMA table_info(request_logs)").fetchall()
    }
    if "web_search_json" not in log_columns:
        conn.execute("ALTER TABLE request_logs ADD COLUMN web_search_json TEXT")

    columns = {
        row[1]
        for row in conn.execute("PRAGMA table_info(channels)").fetchall()
    }
    if "models_json" not in columns:
        conn.execute("ALTER TABLE channels ADD COLUMN models_json TEXT NOT NULL DEFAULT '[]'")
    if "retry_count" not in columns:
        conn.execute("ALTER TABLE channels ADD COLUMN retry_count INTEGER NOT NULL DEFAULT 3")

    web_search_columns = {
        row[1]
        for row in conn.execute("PRAGMA table_info(web_search_settings)").fetchall()
    }
    if "key_usage_limit" not in web_search_columns:
        conn.execute(
            "ALTER TABLE web_search_settings ADD COLUMN key_usage_limit INTEGER NOT NULL DEFAULT 1000"
        )


class AsyncDBWriter:
    def __init__(self, db_path: Path) -> None:
        self.db_path = db_path
        self._queue: queue.Queue[dict[str, Any] | None] = queue.Queue()
        self._thread: threading.Thread | None = None
        self._running = False
        self._init_db()

    def _init_db(self) -> None:
        init_db(self.db_path)

    def start(self) -> None:
        if self._running:
            return
        self._running = True
        self._thread = threading.Thread(target=self._worker, daemon=True)
        self._thread.start()

    def stop(self) -> None:
        if not self._running:
            return
        self._running = False
        self._queue.put(None)
        if self._thread:
            self._thread.join(timeout=5)

    def write(self, record: dict[str, Any]) -> None:
        self._queue.put(record)

    def _worker(self) -> None:
        conn = sqlite3.connect(str(self.db_path))
        while True:
            try:
                record = self._queue.get(timeout=1)
            except queue.Empty:
                continue
            if record is None:
                break
            try:
                self._insert(conn, record)
            except Exception:
                pass
        conn.close()

    def _insert(self, conn: sqlite3.Connection, record: dict[str, Any]) -> None:
        sql = """
        INSERT INTO request_logs (
            request_id, created_at, method, path, client_ip,
            request_headers, request_body, model, upstream_model,
            channel_id, is_stream, ttft_ms, duration_ms, status_code,
            response_body, input_tokens, cached_tokens, output_tokens,
            cost, web_search_json, error
        ) VALUES (
            :request_id, :created_at, :method, :path, :client_ip,
            :request_headers, :request_body, :model, :upstream_model,
            :channel_id, :is_stream, :ttft_ms, :duration_ms, :status_code,
            :response_body, :input_tokens, :cached_tokens, :output_tokens,
            :cost, :web_search_json, :error
        )
        """
        record.setdefault("web_search_json", None)
        conn.execute(sql, record)
        conn.commit()


def read_channels(db_path: Path) -> list[dict[str, Any]]:
    init_db(db_path)
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    rows = conn.execute(
        """
        SELECT id, position, name, type, baseurl, apikey, auth_mode,
               headers_json, timeout_seconds, retry_count, compat_json, models_json, enabled
        FROM channels
        ORDER BY position ASC, id ASC
        """
    ).fetchall()
    conn.close()
    return [_row_to_channel(row) for row in rows]


def replace_channels(
    db_path: Path,
    channels: list[dict[str, Any]],
    default_timeout: int = 120,
) -> None:
    init_db(db_path)
    now = time.time()
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        existing_created = {
            row["id"]: row["created_at"]
            for row in conn.execute("SELECT id, created_at FROM channels").fetchall()
        }
        with conn:
            conn.execute("DELETE FROM channels")
            for position, channel in enumerate(channels):
                conn.execute(
                    """
                    INSERT INTO channels (
                        id, position, name, type, baseurl, apikey, auth_mode,
                        headers_json, timeout_seconds, retry_count, compat_json,
                        models_json, enabled,
                        created_at, updated_at
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        channel["id"],
                        position,
                        channel.get("name") or "",
                        channel["type"],
                        channel["baseurl"],
                        channel.get("apikey") or "",
                        channel.get("auth_mode") or "pass_through_or_config",
                        json.dumps(channel.get("headers") or {}, ensure_ascii=False),
                        int(channel.get("timeout_seconds") or default_timeout),
                        int(channel.get("retry_count", DEFAULT_RETRY_COUNT)),
                        json.dumps(channel.get("compat") or {}, ensure_ascii=False),
                        json.dumps(channel.get("models") or [], ensure_ascii=False),
                        1 if channel.get("enabled", True) is not False else 0,
                        existing_created.get(channel["id"], now),
                        now,
                    ),
                )
    finally:
        conn.close()


def _row_to_channel(row: sqlite3.Row) -> dict[str, Any]:
    return {
        "id": row["id"],
        "name": row["name"],
        "type": row["type"],
        "baseurl": row["baseurl"],
        "apikey": row["apikey"],
        "auth_mode": row["auth_mode"],
        "headers": _parse_json_object(row["headers_json"]),
        "timeout_seconds": row["timeout_seconds"],
        "retry_count": row["retry_count"],
        "compat": _parse_json_object(row["compat_json"]),
        "models": _parse_json_list(row["models_json"]),
        "enabled": bool(row["enabled"]),
    }


def read_web_search_config(db_path: Path) -> dict[str, Any]:
    init_db(db_path)
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        settings = conn.execute(
            "SELECT enabled, key_usage_limit FROM web_search_settings WHERE id = 1"
        ).fetchone()
        rows = conn.execute(
            """
            SELECT id, position, api_key, enabled, usage_count
            FROM tavily_keys
            ORDER BY position ASC, id ASC
            """
        ).fetchall()
    finally:
        conn.close()
    key_usage_limit = (
        int(settings["key_usage_limit"] or TAVILY_KEY_USAGE_LIMIT)
        if settings
        else TAVILY_KEY_USAGE_LIMIT
    )
    return {
        "enabled": bool(settings["enabled"]) if settings else False,
        "keys": [_row_to_tavily_key(row, key_usage_limit) for row in rows],
        "key_usage_limit": key_usage_limit,
    }


def replace_web_search_config(db_path: Path, config: dict[str, Any]) -> dict[str, Any]:
    init_db(db_path)
    if not isinstance(config, dict):
        raise ValueError("web search config must be a JSON object")
    keys = config.get("keys", [])
    if not isinstance(keys, list):
        raise ValueError("web search keys must be a list")

    now = time.time()
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        settings = conn.execute(
            "SELECT key_usage_limit FROM web_search_settings WHERE id = 1"
        ).fetchone()
        current_key_usage_limit = (
            int(settings["key_usage_limit"] or TAVILY_KEY_USAGE_LIMIT)
            if settings
            else TAVILY_KEY_USAGE_LIMIT
        )
        key_usage_limit = _parse_required_positive_int(
            config.get("key_usage_limit", current_key_usage_limit),
            "web search key_usage_limit",
        )
        existing = {
            int(row["id"]): {
                "api_key": row["api_key"],
                "usage_count": int(row["usage_count"] or 0),
                "created_at": float(row["created_at"] or now),
            }
            for row in conn.execute(
                "SELECT id, api_key, usage_count, created_at FROM tavily_keys"
            ).fetchall()
        }
        with conn:
            conn.execute(
                """
                INSERT INTO web_search_settings (id, enabled, key_usage_limit, created_at, updated_at)
                VALUES (1, ?, ?, ?, ?)
                ON CONFLICT(id) DO UPDATE SET
                    enabled = excluded.enabled,
                    key_usage_limit = excluded.key_usage_limit,
                    updated_at = excluded.updated_at
                """,
                (1 if config.get("enabled") is True else 0, key_usage_limit, now, now),
            )
            conn.execute("DELETE FROM tavily_keys")
            for position, item in enumerate(keys):
                if not isinstance(item, dict):
                    raise ValueError(f"web search keys[{position + 1}] must be an object")
                api_key = str(item.get("key") or item.get("api_key") or "").strip()
                if not api_key:
                    raise ValueError(f"web search keys[{position + 1}].key is required")
                existing_id = _parse_positive_int(item.get("id"))
                old = existing.get(existing_id) if existing_id is not None else None
                if "usage_count" in item:
                    usage_count = _parse_required_non_negative_int(
                        item.get("usage_count"),
                        f"web search keys[{position + 1}].usage_count",
                    )
                    created_at = old["created_at"] if old and old["api_key"] == api_key else now
                elif old and old["api_key"] == api_key:
                    usage_count = old["usage_count"]
                    created_at = old["created_at"]
                else:
                    usage_count = 0
                    created_at = now
                conn.execute(
                    """
                    INSERT INTO tavily_keys (
                        id, position, api_key, enabled, usage_count,
                        created_at, updated_at
                    ) VALUES (?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        existing_id,
                        position,
                        api_key,
                        1 if item.get("enabled", True) is not False else 0,
                        usage_count,
                        created_at,
                        now,
                    ),
                )
    finally:
        conn.close()
    return read_web_search_config(db_path)


def reserve_tavily_key(db_path: Path) -> dict[str, Any] | None:
    return _reserve_tavily_key(db_path, key_id=None, allow_disabled=False)


def reserve_tavily_key_by_id(db_path: Path, key_id: int) -> dict[str, Any] | None:
    return _reserve_tavily_key(db_path, key_id=key_id, allow_disabled=True)


def _reserve_tavily_key(
    db_path: Path,
    *,
    key_id: int | None,
    allow_disabled: bool,
) -> dict[str, Any] | None:
    init_db(db_path)
    conn = sqlite3.connect(str(db_path), timeout=30)
    conn.row_factory = sqlite3.Row
    try:
        conn.execute("BEGIN IMMEDIATE")
        settings = conn.execute(
            "SELECT key_usage_limit FROM web_search_settings WHERE id = 1"
        ).fetchone()
        key_usage_limit = (
            int(settings["key_usage_limit"] or TAVILY_KEY_USAGE_LIMIT)
            if settings
            else TAVILY_KEY_USAGE_LIMIT
        )
        if key_id is None:
            row = conn.execute(
                """
                SELECT id, position, api_key, enabled, usage_count
                FROM tavily_keys
                WHERE enabled = 1 AND usage_count < ?
                ORDER BY position ASC, id ASC
                LIMIT 1
                """,
                (key_usage_limit,),
            ).fetchone()
        else:
            enabled_clause = "" if allow_disabled else "AND enabled = 1"
            row = conn.execute(
                f"""
                SELECT id, position, api_key, enabled, usage_count
                FROM tavily_keys
                WHERE id = ? {enabled_clause} AND usage_count < ?
                """,
                (key_id, key_usage_limit),
            ).fetchone()
        if row is None:
            conn.rollback()
            return None
        next_usage = int(row["usage_count"] or 0) + 1
        conn.execute(
            "UPDATE tavily_keys SET usage_count = ?, updated_at = ? WHERE id = ?",
            (next_usage, time.time(), row["id"]),
        )
        conn.commit()
        return {
            "id": int(row["id"]),
            "position": int(row["position"]),
            "key": row["api_key"],
            "enabled": bool(row["enabled"]),
            "usage_count": next_usage,
            "key_usage_limit": key_usage_limit,
        }
    except Exception:
        conn.rollback()
        raise
    finally:
        conn.close()


def _row_to_tavily_key(row: sqlite3.Row, key_usage_limit: int) -> dict[str, Any]:
    return {
        "id": int(row["id"]),
        "key": row["api_key"],
        "enabled": bool(row["enabled"]),
        "usage_count": int(row["usage_count"] or 0),
        "key_usage_limit": key_usage_limit,
    }


def _parse_required_positive_int(value: Any, label: str) -> int:
    if isinstance(value, bool):
        raise ValueError(f"{label} must be a positive integer")
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        raise ValueError(f"{label} must be a positive integer") from None
    if parsed <= 0:
        raise ValueError(f"{label} must be a positive integer")
    return parsed


def _parse_required_non_negative_int(value: Any, label: str) -> int:
    if isinstance(value, bool):
        raise ValueError(f"{label} must be a non-negative integer")
    if isinstance(value, float) and not value.is_integer():
        raise ValueError(f"{label} must be a non-negative integer")
    try:
        parsed = int(value)
    except (TypeError, ValueError) as exc:
        raise ValueError(f"{label} must be a non-negative integer") from exc
    if parsed < 0:
        raise ValueError(f"{label} must be a non-negative integer")
    return parsed


def _parse_positive_int(value: Any) -> int | None:
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        return None
    return parsed if parsed > 0 else None


def _parse_json_object(raw: str | None) -> dict[str, Any]:
    if not raw:
        return {}
    try:
        value = json.loads(raw)
    except json.JSONDecodeError:
        return {}
    return value if isinstance(value, dict) else {}


def _parse_json_list(raw: str | None) -> list[Any]:
    if not raw:
        return []
    try:
        value = json.loads(raw)
    except json.JSONDecodeError:
        return []
    return value if isinstance(value, list) else []


def extract_usage(response: dict[str, Any], protocol: str) -> dict[str, int]:
    usage = response.get("usage") or {}
    if protocol == "responses":
        return {
            "input_tokens": usage.get("input_tokens", 0),
            "cached_tokens": usage.get("input_tokens_details", {}).get("cached_tokens", 0),
            "output_tokens": usage.get("output_tokens", 0),
        }
    if protocol == "messages":
        return {
            "input_tokens": usage.get("input_tokens", 0),
            "cached_tokens": usage.get("cache_creation_input_tokens", 0) + usage.get("cache_read_input_tokens", 0),
            "output_tokens": usage.get("output_tokens", 0),
        }
    if protocol == "chat":
        return {
            "input_tokens": usage.get("prompt_tokens", 0),
            "cached_tokens": _chat_cached_tokens(usage),
            "output_tokens": usage.get("completion_tokens", 0),
        }
    return {"input_tokens": 0, "cached_tokens": 0, "output_tokens": 0}


def _chat_cached_tokens(usage: dict[str, Any]) -> int:
    details = usage.get("prompt_tokens_details") or usage.get("input_tokens_details") or {}
    if not isinstance(details, dict):
        return 0
    try:
        return int(details.get("cached_tokens") or 0)
    except (TypeError, ValueError):
        return 0


def calculate_cost(
    model: str,
    input_tokens: int,
    cached_tokens: int,
    output_tokens: int,
) -> float:
    pricing: dict[str, dict[str, float]] = {
        "gpt-4o": {"input": 2.5, "cached_input": 1.25, "output": 10.0},
        "gpt-4o-mini": {"input": 0.15, "cached_input": 0.075, "output": 0.6},
        "claude-3-5-sonnet": {"input": 3.0, "cached_input": 0.3, "output": 15.0},
        "claude-3-opus": {"input": 15.0, "cached_input": 1.5, "output": 75.0},
    }
    model_lower = model.lower()
    matched = None
    best_len = 0
    for key in pricing:
        if key in model_lower and len(key) > best_len:
            matched = pricing[key]
            best_len = len(key)
    if not matched:
        return 0.0
    non_cached = max(0, input_tokens - cached_tokens)
    return (non_cached * matched["input"] + cached_tokens * matched["cached_input"] + output_tokens * matched["output"]) / 1_000_000


TEXT_FILTER_FIELDS = {
    "request_id",
    "model",
    "upstream_model",
    "channel_id",
    "path",
    "client_ip",
    "error",
}

INTEGER_FILTER_FIELDS = {"status_code", "is_stream"}
REQUEST_STATUS_VALUES = {"success", "failed"}


def read_logs(
    db_path: Path,
    limit: int = 200,
    filters: dict[str, Any] | None = None,
) -> list[dict[str, Any]]:
    if not db_path.exists():
        return []
    where_clause, params = _log_where_clause(filters or {})

    try:
        parsed_limit = int(limit)
    except (TypeError, ValueError):
        parsed_limit = 200
    parsed_limit = max(1, min(parsed_limit, 1000))

    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    cursor = conn.execute(
        f"SELECT * FROM request_logs {where_clause} ORDER BY id DESC LIMIT ?",
        (*params, parsed_limit),
    )
    rows = cursor.fetchall()
    conn.close()
    return [_row_to_log(row) for row in rows]


def read_logs_page(
    db_path: Path,
    page: int = 1,
    page_size: int = 50,
    filters: dict[str, Any] | None = None,
) -> dict[str, Any]:
    if not db_path.exists():
        return {"events": [], "total": 0, "page": 1, "page_size": _parse_page_size(page_size)}

    parsed_page = _parse_page(page)
    parsed_page_size = _parse_page_size(page_size)
    offset = (parsed_page - 1) * parsed_page_size
    where_clause, params = _log_where_clause(filters or {})

    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    total = conn.execute(
        f"SELECT COUNT(*) FROM request_logs {where_clause}",
        params,
    ).fetchone()[0]
    rows = conn.execute(
        f"SELECT * FROM request_logs {where_clause} ORDER BY id DESC LIMIT ? OFFSET ?",
        (*params, parsed_page_size, offset),
    ).fetchall()
    conn.close()
    return {
        "events": [_row_to_log(row) for row in rows],
        "total": int(total),
        "page": parsed_page,
        "page_size": parsed_page_size,
    }


def read_log_filter_options(db_path: Path) -> dict[str, list[Any]]:
    if not db_path.exists():
        return _empty_log_filter_options()
    conn = sqlite3.connect(str(db_path))
    try:
        return {
            "request_ids": _distinct_text_values(conn, "request_id"),
            "models": _distinct_text_values(conn, "model"),
            "upstream_models": _distinct_text_values(conn, "upstream_model"),
            "channel_ids": _distinct_text_values(conn, "channel_id"),
            "paths": _distinct_text_values(conn, "path"),
            "status_codes": _distinct_int_values(conn, "status_code"),
            "request_statuses": ["success", "failed"],
        }
    finally:
        conn.close()


def _log_where_clause(filters: dict[str, Any]) -> tuple[str, tuple[Any, ...]]:
    conditions: list[str] = []
    params: list[Any] = []

    for field in TEXT_FILTER_FIELDS:
        value = filters.get(field)
        if value in (None, ""):
            continue
        conditions.append(f"{field} LIKE ?")
        params.append(f"%{value}%")

    for field in INTEGER_FILTER_FIELDS:
        value = filters.get(field)
        if value in (None, ""):
            continue
        try:
            int_value = int(value)
        except (TypeError, ValueError):
            continue
        conditions.append(f"{field} = ?")
        params.append(int_value)

    request_status = str(filters.get("request_status") or "").strip()
    if request_status in REQUEST_STATUS_VALUES:
        if request_status == "success":
            conditions.append("status_code < 400 AND (error IS NULL OR error = '')")
        else:
            conditions.append("(status_code >= 400 OR (error IS NOT NULL AND error != ''))")

    for field, operator in (("created_from", ">="), ("created_to", "<=")):
        value = filters.get(field)
        if value in (None, ""):
            continue
        try:
            timestamp = float(value)
        except (TypeError, ValueError):
            continue
        conditions.append(f"created_at {operator} ?")
        params.append(timestamp)

    where_clause = f"WHERE {' AND '.join(conditions)}" if conditions else ""
    return where_clause, tuple(params)


def _row_to_log(row: sqlite3.Row) -> dict[str, Any]:
    log = dict(row)
    log["request_status"] = _request_status(log)
    return log


def _request_status(log: dict[str, Any]) -> str:
    status_code = log.get("status_code")
    try:
        status = int(status_code)
    except (TypeError, ValueError):
        status = 0
    error = str(log.get("error") or "").strip()
    return "failed" if status >= 400 or error else "success"


def _parse_page(page: Any) -> int:
    try:
        parsed = int(page)
    except (TypeError, ValueError):
        parsed = 1
    return max(1, parsed)


def _parse_page_size(page_size: Any) -> int:
    try:
        parsed = int(page_size)
    except (TypeError, ValueError):
        parsed = 50
    return max(1, min(parsed, 200))


def _empty_log_filter_options() -> dict[str, list[Any]]:
    return {
        "request_ids": [],
        "models": [],
        "upstream_models": [],
        "channel_ids": [],
        "paths": [],
        "status_codes": [],
        "request_statuses": ["success", "failed"],
    }


def _distinct_text_values(conn: sqlite3.Connection, field: str) -> list[str]:
    rows = conn.execute(
        f"""
        SELECT DISTINCT {field}
        FROM request_logs
        WHERE {field} IS NOT NULL AND {field} != ''
        ORDER BY {field} ASC
        LIMIT 200
        """
    ).fetchall()
    return [str(row[0]) for row in rows]


def _distinct_int_values(conn: sqlite3.Connection, field: str) -> list[int]:
    rows = conn.execute(
        f"""
        SELECT DISTINCT {field}
        FROM request_logs
        WHERE {field} IS NOT NULL
        ORDER BY {field} ASC
        LIMIT 200
        """
    ).fetchall()
    values: list[int] = []
    for row in rows:
        try:
            values.append(int(row[0]))
        except (TypeError, ValueError):
            continue
    return values
