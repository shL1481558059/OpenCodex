from __future__ import annotations

import json
import queue
import sqlite3
import threading
import time
from pathlib import Path
from typing import Any


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
    compat_json TEXT NOT NULL DEFAULT '{}',
    models_json TEXT NOT NULL DEFAULT '[]',
    enabled INTEGER NOT NULL DEFAULT 1,
    created_at REAL NOT NULL,
    updated_at REAL NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_channels_position ON channels(position);
"""

SCHEMA = REQUEST_LOGS_SCHEMA + "\n" + CHANNELS_SCHEMA


def init_db(db_path: Path) -> None:
    db_path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(str(db_path))
    conn.executescript(SCHEMA)
    _migrate_channels(conn)
    conn.commit()
    conn.close()


def _migrate_channels(conn: sqlite3.Connection) -> None:
    columns = {
        row[1]
        for row in conn.execute("PRAGMA table_info(channels)").fetchall()
    }
    if "models_json" not in columns:
        conn.execute("ALTER TABLE channels ADD COLUMN models_json TEXT NOT NULL DEFAULT '[]'")


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
            cost, error
        ) VALUES (
            :request_id, :created_at, :method, :path, :client_ip,
            :request_headers, :request_body, :model, :upstream_model,
            :channel_id, :is_stream, :ttft_ms, :duration_ms, :status_code,
            :response_body, :input_tokens, :cached_tokens, :output_tokens,
            :cost, :error
        )
        """
        conn.execute(sql, record)
        conn.commit()


def read_channels(db_path: Path) -> list[dict[str, Any]]:
    init_db(db_path)
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    rows = conn.execute(
        """
        SELECT id, position, name, type, baseurl, apikey, auth_mode,
               headers_json, timeout_seconds, compat_json, models_json, enabled
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
                        headers_json, timeout_seconds, compat_json, models_json, enabled,
                        created_at, updated_at
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
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
        "compat": _parse_json_object(row["compat_json"]),
        "models": _parse_json_list(row["models_json"]),
        "enabled": bool(row["enabled"]),
    }


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
            "cached_tokens": 0,
            "output_tokens": usage.get("completion_tokens", 0),
        }
    return {"input_tokens": 0, "cached_tokens": 0, "output_tokens": 0}


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


def read_logs(
    db_path: Path,
    limit: int = 200,
    filters: dict[str, Any] | None = None,
) -> list[dict[str, Any]]:
    if not db_path.exists():
        return []
    filters = filters or {}
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

    try:
        parsed_limit = int(limit)
    except (TypeError, ValueError):
        parsed_limit = 200
    parsed_limit = max(1, min(parsed_limit, 1000))

    where_clause = f"WHERE {' AND '.join(conditions)}" if conditions else ""
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    cursor = conn.execute(
        f"SELECT * FROM request_logs {where_clause} ORDER BY id DESC LIMIT ?",
        (*params, parsed_limit),
    )
    rows = cursor.fetchall()
    conn.close()
    return [dict(row) for row in rows]
