from __future__ import annotations

import hashlib
import hmac
import json
import queue
import secrets
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
    owner_username TEXT NOT NULL DEFAULT 'admin',
    api_key_id INTEGER,
    error TEXT
);
"""

CHANNELS_SCHEMA = """
CREATE TABLE IF NOT EXISTS channels (
    owner_username TEXT NOT NULL DEFAULT 'admin',
    id TEXT NOT NULL,
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
    updated_at REAL NOT NULL,
    PRIMARY KEY (owner_username, id)
);
"""

USERS_SCHEMA = """
CREATE TABLE IF NOT EXISTS users (
    username TEXT PRIMARY KEY,
    password_hash TEXT NOT NULL,
    role TEXT NOT NULL DEFAULT 'user',
    enabled INTEGER NOT NULL DEFAULT 1,
    created_at REAL NOT NULL,
    updated_at REAL NOT NULL
);

CREATE TABLE IF NOT EXISTS access_api_keys (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    owner_username TEXT NOT NULL,
    name TEXT NOT NULL DEFAULT '',
    key_hash TEXT NOT NULL UNIQUE,
    key_plaintext TEXT,
    key_prefix TEXT NOT NULL,
    key_suffix TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    created_at REAL NOT NULL,
    updated_at REAL NOT NULL,
    last_used_at REAL,
    FOREIGN KEY (owner_username) REFERENCES users(username)
);

CREATE INDEX IF NOT EXISTS idx_access_api_keys_owner ON access_api_keys(owner_username, id);
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
    provider TEXT NOT NULL DEFAULT 'tavily',
    api_key TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    usage_count INTEGER NOT NULL DEFAULT 0,
    usage_limit INTEGER NOT NULL DEFAULT 1000,
    created_at REAL NOT NULL,
    updated_at REAL NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_tavily_keys_position ON tavily_keys(position);
"""

DEFAULT_WEB_SEARCH_KEY_USAGE_LIMIT = 1000
WEB_SEARCH_PROVIDERS = {"tavily"}
TAVILY_KEY_USAGE_LIMIT = DEFAULT_WEB_SEARCH_KEY_USAGE_LIMIT
PASSWORD_HASH_ITERATIONS = 200_000
ACCESS_KEY_PREFIX = "ocx_"
USER_ROLES = {"superadmin", "user"}

SCHEMA = (
    REQUEST_LOGS_SCHEMA
    + "\n"
    + CHANNELS_SCHEMA
    + "\n"
    + USERS_SCHEMA
    + "\n"
    + WEB_SEARCH_SCHEMA
)


def init_db(db_path: Path, default_owner_username: str = "admin") -> None:
    db_path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(str(db_path))
    conn.executescript(SCHEMA)
    _migrate_channels(conn, default_owner_username)
    conn.commit()
    conn.close()


def _migrate_channels(conn: sqlite3.Connection, default_owner_username: str = "admin") -> None:
    default_owner_username = _normalize_username(default_owner_username) or "admin"
    log_columns = {
        row[1]
        for row in conn.execute("PRAGMA table_info(request_logs)").fetchall()
    }
    if "web_search_json" not in log_columns:
        conn.execute("ALTER TABLE request_logs ADD COLUMN web_search_json TEXT")
    if "owner_username" not in log_columns:
        conn.execute("ALTER TABLE request_logs ADD COLUMN owner_username TEXT")
    if "api_key_id" not in log_columns:
        conn.execute("ALTER TABLE request_logs ADD COLUMN api_key_id INTEGER")
    conn.execute(
        """
        UPDATE request_logs
        SET owner_username = ?
        WHERE owner_username IS NULL OR owner_username = ''
        """,
        (default_owner_username,),
    )

    columns = {
        row[1]
        for row in conn.execute("PRAGMA table_info(channels)").fetchall()
    }
    if "models_json" not in columns:
        conn.execute("ALTER TABLE channels ADD COLUMN models_json TEXT NOT NULL DEFAULT '[]'")
    if "retry_count" not in columns:
        conn.execute("ALTER TABLE channels ADD COLUMN retry_count INTEGER NOT NULL DEFAULT 3")
    columns = {
        row[1]
        for row in conn.execute("PRAGMA table_info(channels)").fetchall()
    }
    if "owner_username" not in columns:
        conn.execute("ALTER TABLE channels ADD COLUMN owner_username TEXT")
    conn.execute(
        """
        UPDATE channels
        SET owner_username = ?
        WHERE owner_username IS NULL OR owner_username = ''
        """,
        (default_owner_username,),
    )
    if _channel_primary_key(conn) != ["owner_username", "id"]:
        _rebuild_channels_with_owner_primary_key(conn, default_owner_username)
    conn.execute(
        "CREATE INDEX IF NOT EXISTS idx_channels_owner_position ON channels(owner_username, position)"
    )

    web_search_columns = {
        row[1]
        for row in conn.execute("PRAGMA table_info(web_search_settings)").fetchall()
    }
    if "key_usage_limit" not in web_search_columns:
        conn.execute(
            "ALTER TABLE web_search_settings ADD COLUMN key_usage_limit INTEGER NOT NULL DEFAULT 1000"
        )
    web_key_columns = {
        row[1]
        for row in conn.execute("PRAGMA table_info(tavily_keys)").fetchall()
    }
    if "provider" not in web_key_columns:
        conn.execute("ALTER TABLE tavily_keys ADD COLUMN provider TEXT NOT NULL DEFAULT 'tavily'")
    if "usage_limit" not in web_key_columns:
        conn.execute(
            "ALTER TABLE tavily_keys ADD COLUMN usage_limit INTEGER NOT NULL DEFAULT 1000"
        )
    access_key_columns = {
        row[1]
        for row in conn.execute("PRAGMA table_info(access_api_keys)").fetchall()
    }
    if "key_plaintext" not in access_key_columns:
        conn.execute("ALTER TABLE access_api_keys ADD COLUMN key_plaintext TEXT")


def _channel_primary_key(conn: sqlite3.Connection) -> list[str]:
    rows = conn.execute("PRAGMA table_info(channels)").fetchall()
    keyed = [(int(row[5] or 0), row[1]) for row in rows if int(row[5] or 0) > 0]
    return [name for _, name in sorted(keyed)]


def _rebuild_channels_with_owner_primary_key(
    conn: sqlite3.Connection, default_owner_username: str
) -> None:
    conn.execute("ALTER TABLE channels RENAME TO channels_legacy")
    conn.execute(
        """
        CREATE TABLE channels (
            owner_username TEXT NOT NULL DEFAULT 'admin',
            id TEXT NOT NULL,
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
            updated_at REAL NOT NULL,
            PRIMARY KEY (owner_username, id)
        )
        """
    )
    conn.execute(
        """
        INSERT INTO channels (
            owner_username, id, position, name, type, baseurl, apikey, auth_mode,
            headers_json, timeout_seconds, retry_count, compat_json, models_json,
            enabled, created_at, updated_at
        )
        SELECT
            COALESCE(NULLIF(owner_username, ''), ?), id, position, name, type,
            baseurl, apikey, auth_mode, headers_json, timeout_seconds,
            retry_count, compat_json, models_json, enabled, created_at, updated_at
        FROM channels_legacy
        """,
        (default_owner_username,),
    )
    conn.execute("DROP TABLE channels_legacy")
    conn.execute(
        "CREATE INDEX IF NOT EXISTS idx_channels_owner_position ON channels(owner_username, position)"
    )


def ensure_superadmin(db_path: Path, username: str, password: str) -> dict[str, Any]:
    init_db(db_path, username)
    username = _normalize_username(username) or "admin"
    now = time.time()
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        with conn:
            row = conn.execute(
                "SELECT username FROM users WHERE username = ?",
                (username,),
            ).fetchone()
            if row is None:
                conn.execute(
                    """
                    INSERT INTO users (
                        username, password_hash, role, enabled, created_at, updated_at
                    ) VALUES (?, ?, 'superadmin', 1, ?, ?)
                    """,
                    (username, hash_password(password), now, now),
                )
            else:
                conn.execute(
                    """
                    UPDATE users
                    SET password_hash = ?, role = 'superadmin', enabled = 1, updated_at = ?
                    WHERE username = ?
                    """,
                    (hash_password(password), now, username),
                )
    finally:
        conn.close()
    user = get_user(db_path, username)
    if user is None:  # pragma: no cover - defensive guard
        raise RuntimeError("failed to ensure superadmin user")
    return user


def create_user(
    db_path: Path,
    username: str,
    password: str,
    *,
    role: str = "user",
    enabled: bool = True,
) -> dict[str, Any]:
    init_db(db_path)
    username = _normalize_username(username)
    if not username:
        raise ValueError("username is required")
    if role not in USER_ROLES:
        raise ValueError("role is invalid")
    if not str(password or ""):
        raise ValueError("password is required")
    now = time.time()
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        with conn:
            conn.execute(
                """
                INSERT INTO users (
                    username, password_hash, role, enabled, created_at, updated_at
                ) VALUES (?, ?, ?, ?, ?, ?)
                """,
                (
                    username,
                    hash_password(password),
                    role,
                    1 if enabled else 0,
                    now,
                    now,
                ),
            )
    except sqlite3.IntegrityError as exc:
        raise ValueError("username already exists") from exc
    finally:
        conn.close()
    user = get_user(db_path, username)
    if user is None:  # pragma: no cover - defensive guard
        raise RuntimeError("failed to create user")
    return user


def list_users(db_path: Path) -> list[dict[str, Any]]:
    init_db(db_path)
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        rows = conn.execute(
            """
            SELECT username, role, enabled, created_at, updated_at
            FROM users
            ORDER BY role ASC, username ASC
            """
        ).fetchall()
    finally:
        conn.close()
    return [_row_to_user(row) for row in rows]


def get_user(db_path: Path, username: str) -> dict[str, Any] | None:
    init_db(db_path, username)
    username = _normalize_username(username)
    if not username:
        return None
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        row = conn.execute(
            """
            SELECT username, role, enabled, created_at, updated_at
            FROM users
            WHERE username = ?
            """,
            (username,),
        ).fetchone()
    finally:
        conn.close()
    return _row_to_user(row) if row else None


def authenticate_user(db_path: Path, username: str, password: str) -> dict[str, Any] | None:
    init_db(db_path, username)
    username = _normalize_username(username)
    if not username:
        return None
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        row = conn.execute(
            """
            SELECT username, password_hash, role, enabled, created_at, updated_at
            FROM users
            WHERE username = ?
            """,
            (username,),
        ).fetchone()
    finally:
        conn.close()
    if row is None or not bool(row["enabled"]):
        return None
    if not verify_password(password, row["password_hash"]):
        return None
    return _row_to_user(row)


def set_user_enabled(
    db_path: Path,
    username: str,
    enabled: bool,
    *,
    protected_username: str | None = None,
) -> dict[str, Any]:
    init_db(db_path, protected_username or username)
    username = _normalize_username(username)
    protected_username = _normalize_username(protected_username)
    if not username:
        raise ValueError("username is required")
    if protected_username and username == protected_username and not enabled:
        raise ValueError("cannot disable the environment superadmin")
    conn = sqlite3.connect(str(db_path))
    try:
        with conn:
            cursor = conn.execute(
                """
                UPDATE users
                SET enabled = ?, updated_at = ?
                WHERE username = ?
                """,
                (1 if enabled else 0, time.time(), username),
            )
            if cursor.rowcount == 0:
                raise ValueError("user not found")
    finally:
        conn.close()
    user = get_user(db_path, username)
    if user is None:  # pragma: no cover - defensive guard
        raise RuntimeError("failed to update user")
    return user


def reset_user_password(
    db_path: Path,
    username: str,
    password: str,
) -> dict[str, Any]:
    init_db(db_path, username)
    username = _normalize_username(username)
    if not username:
        raise ValueError("username is required")
    if not str(password or ""):
        raise ValueError("password is required")
    conn = sqlite3.connect(str(db_path))
    try:
        with conn:
            cursor = conn.execute(
                """
                UPDATE users
                SET password_hash = ?, updated_at = ?
                WHERE username = ?
                """,
                (hash_password(password), time.time(), username),
            )
            if cursor.rowcount == 0:
                raise ValueError("user not found")
    finally:
        conn.close()
    user = get_user(db_path, username)
    if user is None:  # pragma: no cover - defensive guard
        raise RuntimeError("failed to reset user password")
    return user


def delete_user(
    db_path: Path,
    username: str,
    *,
    protected_username: str,
) -> dict[str, Any]:
    init_db(db_path, protected_username)
    username = _normalize_username(username)
    protected_username = _normalize_username(protected_username)
    if not username:
        raise ValueError("username is required")
    if not protected_username:
        raise ValueError("protected_username is required")
    if protected_username and username == protected_username:
        raise ValueError("cannot delete current user")

    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        with conn:
            row = conn.execute(
                """
                SELECT username, role, enabled, created_at, updated_at
                FROM users
                WHERE username = ?
                """,
                (username,),
            ).fetchone()
            if row is None:
                raise ValueError("user not found")
            deleted_user = _row_to_user(row)
            conn.execute(
                "DELETE FROM access_api_keys WHERE owner_username = ?",
                (username,),
            )
            conn.execute(
                "DELETE FROM channels WHERE owner_username = ?",
                (username,),
            )
            conn.execute("DELETE FROM users WHERE username = ?", (username,))
    finally:
        conn.close()
    return deleted_user


def create_access_api_key(
    db_path: Path,
    owner_username: str,
    name: str = "",
) -> dict[str, Any]:
    init_db(db_path, owner_username)
    owner_username = _normalize_username(owner_username)
    if not owner_username:
        raise ValueError("owner_username is required")
    if get_user(db_path, owner_username) is None:
        raise ValueError("user not found")
    raw_key = generate_access_api_key()
    now = time.time()
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        with conn:
            cursor = conn.execute(
                """
                INSERT INTO access_api_keys (
                    owner_username, name, key_hash, key_plaintext, key_prefix, key_suffix,
                    enabled, created_at, updated_at
                ) VALUES (?, ?, ?, ?, ?, ?, 1, ?, ?)
                """,
                (
                    owner_username,
                    str(name or "").strip(),
                    hash_access_api_key(raw_key),
                    raw_key,
                    raw_key[:12],
                    raw_key[-6:],
                    now,
                    now,
                ),
            )
            key_id = int(cursor.lastrowid)
    finally:
        conn.close()
    metadata = get_access_api_key(db_path, key_id)
    if metadata is None:  # pragma: no cover - defensive guard
        raise RuntimeError("failed to create access api key")
    metadata["key"] = raw_key
    return metadata


def list_access_api_keys(
    db_path: Path,
    owner_username: str | None = None,
) -> list[dict[str, Any]]:
    init_db(db_path, owner_username or "admin")
    owner_username = _normalize_username(owner_username) if owner_username is not None else None
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        if owner_username:
            rows = conn.execute(
                """
                SELECT id, owner_username, name, key_plaintext, key_prefix, key_suffix, enabled,
                       created_at, updated_at, last_used_at
                FROM access_api_keys
                WHERE owner_username = ?
                ORDER BY id DESC
                """,
                (owner_username,),
            ).fetchall()
        else:
            rows = conn.execute(
                """
                SELECT id, owner_username, name, key_plaintext, key_prefix, key_suffix, enabled,
                       created_at, updated_at, last_used_at
                FROM access_api_keys
                ORDER BY owner_username ASC, id DESC
                """
            ).fetchall()
    finally:
        conn.close()
    return [_row_to_access_api_key(row) for row in rows]


def get_access_api_key(db_path: Path, key_id: int) -> dict[str, Any] | None:
    init_db(db_path)
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        row = conn.execute(
            """
            SELECT id, owner_username, name, key_plaintext, key_prefix, key_suffix, enabled,
                   created_at, updated_at, last_used_at
            FROM access_api_keys
            WHERE id = ?
            """,
            (key_id,),
        ).fetchone()
    finally:
        conn.close()
    return _row_to_access_api_key(row) if row else None


def set_access_api_key_enabled(
    db_path: Path,
    key_id: int,
    enabled: bool,
    *,
    owner_username: str | None = None,
) -> dict[str, Any]:
    init_db(db_path, owner_username or "admin")
    owner_username = _normalize_username(owner_username) if owner_username is not None else None
    conn = sqlite3.connect(str(db_path))
    try:
        with conn:
            if owner_username:
                cursor = conn.execute(
                    """
                    UPDATE access_api_keys
                    SET enabled = ?, updated_at = ?
                    WHERE id = ? AND owner_username = ?
                    """,
                    (1 if enabled else 0, time.time(), key_id, owner_username),
                )
            else:
                cursor = conn.execute(
                    """
                    UPDATE access_api_keys
                    SET enabled = ?, updated_at = ?
                    WHERE id = ?
                    """,
                    (1 if enabled else 0, time.time(), key_id),
                )
            if cursor.rowcount == 0:
                raise ValueError("api key not found")
    finally:
        conn.close()
    key = get_access_api_key(db_path, key_id)
    if key is None:  # pragma: no cover - defensive guard
        raise RuntimeError("failed to update api key")
    return key


def delete_access_api_key(
    db_path: Path,
    key_id: int,
    *,
    owner_username: str | None = None,
) -> None:
    init_db(db_path, owner_username or "admin")
    owner_username = _normalize_username(owner_username) if owner_username is not None else None
    conn = sqlite3.connect(str(db_path))
    try:
        with conn:
            if owner_username:
                cursor = conn.execute(
                    "DELETE FROM access_api_keys WHERE id = ? AND owner_username = ?",
                    (key_id, owner_username),
                )
            else:
                cursor = conn.execute(
                    "DELETE FROM access_api_keys WHERE id = ?",
                    (key_id,),
                )
            if cursor.rowcount == 0:
                raise ValueError("api key not found")
    finally:
        conn.close()


def authenticate_access_api_key(db_path: Path, raw_key: str) -> dict[str, Any] | None:
    raw_key = str(raw_key or "").strip()
    if not raw_key:
        return None
    init_db(db_path)
    conn = sqlite3.connect(str(db_path), timeout=30)
    conn.row_factory = sqlite3.Row
    try:
        conn.execute("BEGIN IMMEDIATE")
        row = conn.execute(
            """
            SELECT
                k.id, k.owner_username, k.name, k.key_prefix, k.key_suffix,
                k.enabled, k.created_at, k.updated_at, k.last_used_at,
                u.role AS user_role, u.enabled AS user_enabled
            FROM access_api_keys k
            JOIN users u ON u.username = k.owner_username
            WHERE k.key_hash = ?
            """,
            (hash_access_api_key(raw_key),),
        ).fetchone()
        if row is None or not bool(row["enabled"]) or not bool(row["user_enabled"]):
            conn.rollback()
            return None
        now = time.time()
        conn.execute(
            "UPDATE access_api_keys SET last_used_at = ?, updated_at = ? WHERE id = ?",
            (now, now, row["id"]),
        )
        conn.commit()
        key = _row_to_access_api_key(row, include_plaintext=False)
        key["user"] = {
            "username": row["owner_username"],
            "role": row["user_role"],
            "enabled": bool(row["user_enabled"]),
        }
        key["last_used_at"] = now
        return key
    except Exception:
        conn.rollback()
        raise
    finally:
        conn.close()


def generate_access_api_key() -> str:
    return ACCESS_KEY_PREFIX + secrets.token_urlsafe(32)


def hash_access_api_key(raw_key: str) -> str:
    return hashlib.sha256(str(raw_key or "").encode("utf-8")).hexdigest()


def hash_password(password: str) -> str:
    salt = secrets.token_hex(16)
    digest = hashlib.pbkdf2_hmac(
        "sha256",
        str(password or "").encode("utf-8"),
        bytes.fromhex(salt),
        PASSWORD_HASH_ITERATIONS,
    ).hex()
    return f"pbkdf2_sha256${PASSWORD_HASH_ITERATIONS}${salt}${digest}"


def verify_password(password: str, stored_hash: str) -> bool:
    try:
        algorithm, iterations, salt, digest = str(stored_hash or "").split("$", 3)
        if algorithm != "pbkdf2_sha256":
            return False
        candidate = hashlib.pbkdf2_hmac(
            "sha256",
            str(password or "").encode("utf-8"),
            bytes.fromhex(salt),
            int(iterations),
        ).hex()
    except (TypeError, ValueError):
        return False
    return hmac.compare_digest(candidate, digest)


def _row_to_user(row: sqlite3.Row) -> dict[str, Any]:
    return {
        "username": row["username"],
        "role": row["role"],
        "enabled": bool(row["enabled"]),
        "created_at": row["created_at"],
        "updated_at": row["updated_at"],
    }


def _row_to_access_api_key(
    row: sqlite3.Row,
    *,
    include_plaintext: bool = True,
) -> dict[str, Any]:
    key = {
        "id": int(row["id"]),
        "owner_username": row["owner_username"],
        "name": row["name"],
        "key_prefix": row["key_prefix"],
        "key_suffix": row["key_suffix"],
        "masked_key": f"{row['key_prefix']}...{row['key_suffix']}",
        "enabled": bool(row["enabled"]),
        "created_at": row["created_at"],
        "updated_at": row["updated_at"],
        "last_used_at": row["last_used_at"],
    }
    if include_plaintext:
        key["key"] = row["key_plaintext"] if "key_plaintext" in row.keys() else None
    return key


def _normalize_username(value: Any) -> str:
    return str(value or "").strip()


class AsyncDBWriter:
    def __init__(self, db_path: Path, default_owner_username: str = "admin") -> None:
        self.db_path = db_path
        self.default_owner_username = _normalize_username(default_owner_username) or "admin"
        self._queue: queue.Queue[dict[str, Any] | None] = queue.Queue()
        self._thread: threading.Thread | None = None
        self._running = False
        self._init_db()

    def _init_db(self) -> None:
        init_db(self.db_path, self.default_owner_username)

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
            cost, web_search_json, owner_username, api_key_id, error
        ) VALUES (
            :request_id, :created_at, :method, :path, :client_ip,
            :request_headers, :request_body, :model, :upstream_model,
            :channel_id, :is_stream, :ttft_ms, :duration_ms, :status_code,
            :response_body, :input_tokens, :cached_tokens, :output_tokens,
            :cost, :web_search_json, :owner_username, :api_key_id, :error
        )
        """
        record.setdefault("web_search_json", None)
        record.setdefault("owner_username", self.default_owner_username)
        record.setdefault("api_key_id", None)
        conn.execute(sql, record)
        conn.commit()


def read_channels(
    db_path: Path,
    owner_username: str | None = None,
    default_owner_username: str = "admin",
) -> list[dict[str, Any]]:
    init_db(db_path, default_owner_username)
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    owner_username = _normalize_username(owner_username) if owner_username is not None else None
    if owner_username:
        rows = conn.execute(
            """
            SELECT owner_username, id, position, name, type, baseurl, apikey, auth_mode,
                   headers_json, timeout_seconds, retry_count, compat_json, models_json, enabled
            FROM channels
            WHERE owner_username = ?
            ORDER BY position ASC, id ASC
            """,
            (owner_username,),
        ).fetchall()
    else:
        rows = conn.execute(
            """
            SELECT owner_username, id, position, name, type, baseurl, apikey, auth_mode,
                   headers_json, timeout_seconds, retry_count, compat_json, models_json, enabled
            FROM channels
            ORDER BY owner_username ASC, position ASC, id ASC
            """
        ).fetchall()
    conn.close()
    return [_row_to_channel(row) for row in rows]


def replace_channels(
    db_path: Path,
    channels: list[dict[str, Any]],
    default_timeout: int = 120,
    owner_username: str | None = "admin",
    default_owner_username: str = "admin",
) -> None:
    default_owner_username = _normalize_username(default_owner_username) or "admin"
    owner_username = _normalize_username(owner_username) if owner_username is not None else None
    init_db(db_path, default_owner_username)
    now = time.time()
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        existing_created = {
            (row["owner_username"], row["id"]): row["created_at"]
            for row in conn.execute(
                "SELECT owner_username, id, created_at FROM channels"
            ).fetchall()
        }
        with conn:
            if owner_username is None:
                conn.execute("DELETE FROM channels")
            else:
                conn.execute(
                    "DELETE FROM channels WHERE owner_username = ?",
                    (owner_username,),
                )
            for position, channel in enumerate(channels):
                channel_owner = owner_username or _normalize_username(
                    channel.get("owner_username")
                ) or default_owner_username
                conn.execute(
                    """
                    INSERT INTO channels (
                        owner_username, id, position, name, type, baseurl, apikey, auth_mode,
                        headers_json, timeout_seconds, retry_count, compat_json,
                        models_json, enabled,
                        created_at, updated_at
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        channel_owner,
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
                        existing_created.get((channel_owner, channel["id"]), now),
                        now,
                    ),
                )
    finally:
        conn.close()


def _row_to_channel(row: sqlite3.Row) -> dict[str, Any]:
    return {
        "owner_username": row["owner_username"],
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
            "SELECT enabled FROM web_search_settings WHERE id = 1"
        ).fetchone()
        rows = conn.execute(
            """
            SELECT id, position, provider, api_key, enabled, usage_count, usage_limit
            FROM tavily_keys
            ORDER BY position ASC, id ASC
            """
        ).fetchall()
    finally:
        conn.close()
    return {
        "enabled": bool(settings["enabled"]) if settings else False,
        "providers": sorted(WEB_SEARCH_PROVIDERS),
        "default_key_usage_limit": DEFAULT_WEB_SEARCH_KEY_USAGE_LIMIT,
        "keys": [_row_to_tavily_key(row) for row in rows],
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
        current_default_key_usage_limit = (
            int(settings["key_usage_limit"] or TAVILY_KEY_USAGE_LIMIT)
            if settings
            else TAVILY_KEY_USAGE_LIMIT
        )
        default_key_usage_limit = _parse_required_positive_int(
            config.get("key_usage_limit", current_default_key_usage_limit),
            "web search key_usage_limit",
        )
        existing = {
            int(row["id"]): {
                "api_key": row["api_key"],
                "provider": row["provider"],
                "usage_count": int(row["usage_count"] or 0),
                "usage_limit": int(row["usage_limit"] or DEFAULT_WEB_SEARCH_KEY_USAGE_LIMIT),
                "created_at": float(row["created_at"] or now),
            }
            for row in conn.execute(
                "SELECT id, provider, api_key, usage_count, usage_limit, created_at FROM tavily_keys"
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
                (1 if config.get("enabled") is True else 0, default_key_usage_limit, now, now),
            )
            conn.execute("DELETE FROM tavily_keys")
            for position, item in enumerate(keys):
                if not isinstance(item, dict):
                    raise ValueError(f"web search keys[{position + 1}] must be an object")
                provider = _normalize_web_search_provider(item.get("provider"))
                api_key = str(item.get("key") or item.get("api_key") or "").strip()
                if not api_key:
                    raise ValueError(f"web search keys[{position + 1}].key is required")
                existing_id = _parse_positive_int(item.get("id"))
                old = existing.get(existing_id) if existing_id is not None else None
                usage_limit_source = item.get("usage_limit", item.get("key_usage_limit"))
                if usage_limit_source is None and old and old["api_key"] == api_key and old["provider"] == provider:
                    usage_limit = old["usage_limit"]
                else:
                    usage_limit = _parse_required_positive_int(
                        usage_limit_source if usage_limit_source is not None else default_key_usage_limit,
                        f"web search keys[{position + 1}].usage_limit",
                    )
                same_key = old is not None and old["api_key"] == api_key and old["provider"] == provider
                if "usage_count" in item:
                    usage_count = _parse_required_non_negative_int(
                        item.get("usage_count"),
                        f"web search keys[{position + 1}].usage_count",
                    )
                    created_at = old["created_at"] if same_key else now
                elif same_key:
                    usage_count = old["usage_count"]
                    created_at = old["created_at"]
                else:
                    usage_count = 0
                    created_at = now
                conn.execute(
                    """
                    INSERT INTO tavily_keys (
                        id, position, provider, api_key, enabled, usage_count,
                        usage_limit, created_at, updated_at
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        existing_id,
                        position,
                        provider,
                        api_key,
                        1 if item.get("enabled", True) is not False else 0,
                        usage_count,
                        usage_limit,
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
        if key_id is None:
            row = conn.execute(
                """
                SELECT id, position, provider, api_key, enabled, usage_count, usage_limit
                FROM tavily_keys
                WHERE enabled = 1 AND usage_count < usage_limit
                ORDER BY position ASC, id ASC
                LIMIT 1
                """
            ).fetchone()
        else:
            enabled_clause = "" if allow_disabled else "AND enabled = 1"
            row = conn.execute(
                f"""
                SELECT id, position, provider, api_key, enabled, usage_count, usage_limit
                FROM tavily_keys
                WHERE id = ? {enabled_clause} AND usage_count < usage_limit
                """,
                (key_id,),
            ).fetchone()
        if row is None:
            conn.rollback()
            return None
        next_usage = int(row["usage_count"] or 0) + 1
        usage_limit = int(row["usage_limit"] or DEFAULT_WEB_SEARCH_KEY_USAGE_LIMIT)
        conn.execute(
            "UPDATE tavily_keys SET usage_count = ?, updated_at = ? WHERE id = ?",
            (next_usage, time.time(), row["id"]),
        )
        conn.commit()
        return {
            "id": int(row["id"]),
            "position": int(row["position"]),
            "provider": row["provider"],
            "key": row["api_key"],
            "enabled": bool(row["enabled"]),
            "usage_count": next_usage,
            "usage_limit": usage_limit,
            "key_usage_limit": usage_limit,
        }
    except Exception:
        conn.rollback()
        raise
    finally:
        conn.close()


def _row_to_tavily_key(row: sqlite3.Row) -> dict[str, Any]:
    usage_limit = int(row["usage_limit"] or DEFAULT_WEB_SEARCH_KEY_USAGE_LIMIT)
    return {
        "id": int(row["id"]),
        "provider": row["provider"],
        "key": row["api_key"],
        "enabled": bool(row["enabled"]),
        "usage_count": int(row["usage_count"] or 0),
        "usage_limit": usage_limit,
        "key_usage_limit": usage_limit,
    }


def _normalize_web_search_provider(value: Any) -> str:
    provider = str(value or "tavily").strip().lower()
    if provider not in WEB_SEARCH_PROVIDERS:
        raise ValueError(f"unsupported web search provider: {provider}")
    return provider


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
    "owner_username",
    "path",
    "client_ip",
    "error",
}

INTEGER_FILTER_FIELDS = {"status_code", "is_stream", "api_key_id"}
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


def read_log_filter_options(
    db_path: Path,
    filters: dict[str, Any] | None = None,
) -> dict[str, list[Any]]:
    if not db_path.exists():
        return _empty_log_filter_options()
    where_clause, params = _log_where_clause(filters or {})
    conn = sqlite3.connect(str(db_path))
    try:
        return {
            "request_ids": _distinct_text_values(conn, "request_id", where_clause, params),
            "models": _distinct_text_values(conn, "model", where_clause, params),
            "upstream_models": _distinct_text_values(
                conn, "upstream_model", where_clause, params
            ),
            "channel_ids": _distinct_text_values(conn, "channel_id", where_clause, params),
            "owner_usernames": _distinct_text_values(
                conn, "owner_username", where_clause, params
            ),
            "paths": _distinct_text_values(conn, "path", where_clause, params),
            "status_codes": _distinct_int_values(conn, "status_code", where_clause, params),
            "api_key_ids": _distinct_int_values(conn, "api_key_id", where_clause, params),
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
        "owner_usernames": [],
        "paths": [],
        "status_codes": [],
        "api_key_ids": [],
        "request_statuses": ["success", "failed"],
    }


def _distinct_text_values(
    conn: sqlite3.Connection,
    field: str,
    where_clause: str = "",
    params: tuple[Any, ...] = (),
) -> list[str]:
    scoped_where = _append_where_condition(
        where_clause, f"{field} IS NOT NULL AND {field} != ''"
    )
    rows = conn.execute(
        f"""
        SELECT DISTINCT {field}
        FROM request_logs
        {scoped_where}
        ORDER BY {field} ASC
        LIMIT 200
        """,
        params,
    ).fetchall()
    return [str(row[0]) for row in rows]


def _distinct_int_values(
    conn: sqlite3.Connection,
    field: str,
    where_clause: str = "",
    params: tuple[Any, ...] = (),
) -> list[int]:
    scoped_where = _append_where_condition(where_clause, f"{field} IS NOT NULL")
    rows = conn.execute(
        f"""
        SELECT DISTINCT {field}
        FROM request_logs
        {scoped_where}
        ORDER BY {field} ASC
        LIMIT 200
        """,
        params,
    ).fetchall()
    values: list[int] = []
    for row in rows:
        try:
            values.append(int(row[0]))
        except (TypeError, ValueError):
            continue
    return values


def _append_where_condition(where_clause: str, condition: str) -> str:
    if where_clause:
        return f"{where_clause} AND {condition}"
    return f"WHERE {condition}"
