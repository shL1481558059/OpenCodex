from __future__ import annotations

import json
import queue
import sqlite3
import threading
import time
from pathlib import Path
from typing import Any


SCHEMA = """
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


class AsyncDBWriter:
    def __init__(self, db_path: Path) -> None:
        self.db_path = db_path
        self._queue: queue.Queue[dict[str, Any] | None] = queue.Queue()
        self._thread: threading.Thread | None = None
        self._running = False
        self._init_db()

    def _init_db(self) -> None:
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        conn = sqlite3.connect(str(self.db_path))
        conn.execute(SCHEMA)
        conn.commit()
        conn.close()

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


def read_logs(db_path: Path, limit: int = 200) -> list[dict[str, Any]]:
    if not db_path.exists():
        return []
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    cursor = conn.execute(
        "SELECT * FROM request_logs ORDER BY id DESC LIMIT ?", (limit,)
    )
    rows = cursor.fetchall()
    conn.close()
    return [dict(row) for row in rows]
