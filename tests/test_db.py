from __future__ import annotations

import json
import sqlite3
import tempfile
import time
import unittest
from pathlib import Path

from opencodex_proxy.db import (
    AsyncDBWriter,
    TAVILY_KEY_USAGE_LIMIT,
    authenticate_access_api_key,
    authenticate_user,
    calculate_cost,
    create_access_api_key,
    create_user,
    delete_access_api_key,
    ensure_superadmin,
    extract_usage,
    init_db,
    list_access_api_keys,
    read_log_filter_options,
    read_channels,
    read_logs,
    read_web_search_config,
    replace_channels,
    replace_web_search_config,
    reserve_tavily_key,
    reserve_tavily_key_by_id,
    set_access_api_key_enabled,
    set_user_enabled,
    delete_user,
)


class TestExtractUsage(unittest.TestCase):
    def test_responses_protocol(self):
        response = {
            "usage": {
                "input_tokens": 100,
                "input_tokens_details": {"cached_tokens": 30},
                "output_tokens": 50,
            }
        }
        result = extract_usage(response, "responses")
        self.assertEqual(result["input_tokens"], 100)
        self.assertEqual(result["cached_tokens"], 30)
        self.assertEqual(result["output_tokens"], 50)

    def test_messages_protocol(self):
        response = {
            "usage": {
                "input_tokens": 100,
                "cache_creation_input_tokens": 10,
                "cache_read_input_tokens": 20,
                "output_tokens": 50,
            }
        }
        result = extract_usage(response, "messages")
        self.assertEqual(result["input_tokens"], 100)
        self.assertEqual(result["cached_tokens"], 30)
        self.assertEqual(result["output_tokens"], 50)

    def test_chat_protocol(self):
        response = {
            "usage": {
                "prompt_tokens": 100,
                "prompt_tokens_details": {"cached_tokens": 25},
                "completion_tokens": 50,
            }
        }
        result = extract_usage(response, "chat")
        self.assertEqual(result["input_tokens"], 100)
        self.assertEqual(result["cached_tokens"], 25)
        self.assertEqual(result["output_tokens"], 50)

    def test_missing_usage(self):
        response = {}
        result = extract_usage(response, "responses")
        self.assertEqual(result["input_tokens"], 0)
        self.assertEqual(result["cached_tokens"], 0)
        self.assertEqual(result["output_tokens"], 0)


class TestCalculateCost(unittest.TestCase):
    def test_gpt4o(self):
        cost = calculate_cost("gpt-4o", 1000, 500, 500)
        expected = (500 * 2.5 + 500 * 1.25 + 500 * 10.0) / 1_000_000
        self.assertAlmostEqual(cost, expected)

    def test_gpt4o_mini(self):
        cost = calculate_cost("gpt-4o-mini", 1000, 0, 500)
        expected = (1000 * 0.15 + 500 * 0.6) / 1_000_000
        self.assertAlmostEqual(cost, expected)

    def test_claude(self):
        cost = calculate_cost("claude-3-5-sonnet", 1000, 200, 800)
        expected = (800 * 3.0 + 200 * 0.3 + 800 * 15.0) / 1_000_000
        self.assertAlmostEqual(cost, expected)

    def test_unknown_model(self):
        cost = calculate_cost("unknown-model", 1000, 0, 500)
        self.assertEqual(cost, 0.0)


class TestAsyncDBWriter(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db_path = Path(self.tmp.name) / "test.db"

    def tearDown(self):
        self.tmp.cleanup()

    def test_write_and_read(self):
        writer = AsyncDBWriter(self.db_path)
        writer.start()
        record = {
            "request_id": "test123",
            "created_at": time.time(),
            "method": "POST",
            "path": "/v1/responses",
            "client_ip": "127.0.0.1",
            "request_headers": "{}",
            "request_body": "{}",
            "model": "gpt-4o",
            "upstream_model": "gpt-4o",
            "channel_id": "openai",
            "is_stream": 0,
            "ttft_ms": None,
            "duration_ms": 100,
            "status_code": 200,
            "response_body": "{}",
            "input_tokens": 100,
            "cached_tokens": 0,
            "output_tokens": 50,
            "cost": 0.001,
            "error": None,
        }
        writer.write(record)
        time.sleep(0.1)
        writer.stop()
        logs = read_logs(self.db_path)
        self.assertEqual(len(logs), 1)
        self.assertEqual(logs[0]["request_id"], "test123")
        self.assertEqual(logs[0]["model"], "gpt-4o")
        self.assertEqual(logs[0]["input_tokens"], 100)

    def test_multiple_writes(self):
        writer = AsyncDBWriter(self.db_path)
        writer.start()
        for i in range(5):
            record = {
                "request_id": f"req_{i}",
                "created_at": time.time(),
                "method": "POST",
                "path": "/v1/responses",
                "client_ip": "127.0.0.1",
                "request_headers": "{}",
                "request_body": "{}",
                "model": "gpt-4o",
                "upstream_model": "gpt-4o",
                "channel_id": "openai",
                "is_stream": 0,
                "ttft_ms": None,
                "duration_ms": 100,
                "status_code": 200,
                "response_body": "{}",
                "input_tokens": 100,
                "cached_tokens": 0,
                "output_tokens": 50,
                "cost": 0.001,
                "error": None,
            }
            writer.write(record)
        time.sleep(0.2)
        writer.stop()
        logs = read_logs(self.db_path, limit=10)
        self.assertEqual(len(logs), 5)

    def test_limit(self):
        writer = AsyncDBWriter(self.db_path)
        writer.start()
        for i in range(10):
            record = {
                "request_id": f"req_{i}",
                "created_at": time.time(),
                "method": "POST",
                "path": "/v1/responses",
                "client_ip": "127.0.0.1",
                "request_headers": "{}",
                "request_body": "{}",
                "model": "gpt-4o",
                "upstream_model": "gpt-4o",
                "channel_id": "openai",
                "is_stream": 0,
                "ttft_ms": None,
                "duration_ms": 100,
                "status_code": 200,
                "response_body": "{}",
                "input_tokens": 100,
                "cached_tokens": 0,
                "output_tokens": 50,
                "cost": 0.001,
                "error": None,
            }
            writer.write(record)
        time.sleep(0.3)
        writer.stop()
        logs = read_logs(self.db_path, limit=3)
        self.assertEqual(len(logs), 3)

    def test_paginated_logs_include_total_and_request_status(self):
        writer = AsyncDBWriter(self.db_path)
        writer.start()
        for i in range(5):
            record = {
                "request_id": f"req_{i}",
                "created_at": time.time(),
                "method": "POST",
                "path": "/v1/responses",
                "client_ip": "127.0.0.1",
                "request_headers": "{}",
                "request_body": "{}",
                "model": "gpt-4o",
                "upstream_model": "gpt-4o",
                "channel_id": "openai",
                "is_stream": 0,
                "ttft_ms": None,
                "duration_ms": 100,
                "status_code": 500 if i == 0 else 200,
                "response_body": "{}",
                "input_tokens": 100,
                "cached_tokens": 0,
                "output_tokens": 50,
                "cost": 0.001,
                "error": "boom" if i == 0 else None,
            }
            writer.write(record)
        time.sleep(0.2)
        writer.stop()

        from opencodex_proxy.db import read_logs_page

        page = read_logs_page(self.db_path, page=2, page_size=2)

        self.assertEqual(page["total"], 5)
        self.assertEqual(page["page"], 2)
        self.assertEqual(page["page_size"], 2)
        self.assertEqual(len(page["events"]), 2)
        self.assertIn(page["events"][0]["request_status"], {"success", "failed"})

    def test_log_filter_options_are_loaded_from_existing_logs(self):
        writer = AsyncDBWriter(self.db_path)
        writer.start()
        for status_code, model in ((200, "gpt-4o"), (502, "claude-3-5-sonnet")):
            writer.write(
                {
                    "request_id": f"req_{status_code}",
                    "created_at": time.time(),
                    "method": "POST",
                    "path": "/v1/responses",
                    "client_ip": "127.0.0.1",
                    "request_headers": "{}",
                    "request_body": "{}",
                    "model": model,
                    "upstream_model": model,
                    "channel_id": "openai",
                    "is_stream": 0,
                    "ttft_ms": None,
                    "duration_ms": 100,
                    "status_code": status_code,
                    "response_body": "{}",
                    "input_tokens": 100,
                    "cached_tokens": 0,
                    "output_tokens": 50,
                    "cost": 0.001,
                    "error": None,
                }
            )
        time.sleep(0.2)
        writer.stop()

        from opencodex_proxy.db import read_log_filter_options

        options = read_log_filter_options(self.db_path)

        self.assertEqual(options["models"], ["claude-3-5-sonnet", "gpt-4o"])
        self.assertEqual(options["status_codes"], [200, 502])

    def test_filters_logs_by_common_fields(self):
        writer = AsyncDBWriter(self.db_path)
        writer.start()
        now = time.time()
        records = [
            {
                "request_id": "req_ok",
                "created_at": now - 60,
                "method": "POST",
                "path": "/v1/responses",
                "client_ip": "127.0.0.1",
                "request_headers": "{}",
                "request_body": "{}",
                "model": "gpt-4o",
                "upstream_model": "gpt-4o",
                "channel_id": "openai",
                "is_stream": 0,
                "ttft_ms": None,
                "duration_ms": 100,
                "status_code": 200,
                "response_body": "{}",
                "input_tokens": 100,
                "cached_tokens": 0,
                "output_tokens": 50,
                "cost": 0.001,
                "error": None,
            },
            {
                "request_id": "req_error",
                "created_at": now,
                "method": "POST",
                "path": "/v1/chat/completions",
                "client_ip": "10.0.0.8",
                "request_headers": "{}",
                "request_body": "{}",
                "model": "claude-3-5-sonnet",
                "upstream_model": "claude-3-5-sonnet",
                "channel_id": "anthropic",
                "is_stream": 1,
                "ttft_ms": 20,
                "duration_ms": 250,
                "status_code": 502,
                "response_body": "{}",
                "input_tokens": 200,
                "cached_tokens": 0,
                "output_tokens": 0,
                "cost": 0.0,
                "error": "upstream timeout",
            },
        ]
        for record in records:
            writer.write(record)
        time.sleep(0.2)
        writer.stop()

        logs = read_logs(
            self.db_path,
            filters={
                "channel_id": "anthropic",
                "model": "claude",
                "path": "/v1/chat/completions",
                "status_code": 502,
                "is_stream": 1,
                "error": "timeout",
                "created_from": now - 1,
            },
        )

        self.assertEqual(len(logs), 1)
        self.assertEqual(logs[0]["request_id"], "req_error")

    def test_filters_logs_by_owner_and_access_key(self):
        writer = AsyncDBWriter(self.db_path)
        writer.start()
        now = time.time()
        for owner_username, api_key_id in (("alice", 7), ("bob", 8)):
            writer.write(
                {
                    "request_id": f"req_{owner_username}",
                    "created_at": now,
                    "method": "POST",
                    "path": "/v1/responses",
                    "client_ip": "127.0.0.1",
                    "request_headers": "{}",
                    "request_body": "{}",
                    "model": "gpt-4o",
                    "upstream_model": "gpt-4o",
                    "channel_id": "openai",
                    "is_stream": 0,
                    "ttft_ms": None,
                    "duration_ms": 100,
                    "status_code": 200,
                    "response_body": "{}",
                    "input_tokens": 100,
                    "cached_tokens": 0,
                    "output_tokens": 50,
                    "cost": 0.001,
                    "owner_username": owner_username,
                    "api_key_id": api_key_id,
                    "error": None,
                }
            )
        writer.stop()

        logs = read_logs(
            self.db_path,
            filters={"owner_username": "alice", "api_key_id": 7},
        )
        options = read_log_filter_options(
            self.db_path,
            filters={"owner_username": "alice"},
        )

        self.assertEqual(len(logs), 1)
        self.assertEqual(logs[0]["request_id"], "req_alice")
        self.assertEqual(options["owner_usernames"], ["alice"])
        self.assertEqual(options["api_key_ids"], [7])

    def test_stop_flushes_queued_records(self):
        writer = AsyncDBWriter(self.db_path)
        writer.start()
        for i in range(50):
            record = {
                "request_id": f"req_{i}",
                "created_at": time.time(),
                "method": "POST",
                "path": "/v1/responses",
                "client_ip": "127.0.0.1",
                "request_headers": "{}",
                "request_body": "{}",
                "model": "gpt-4o",
                "upstream_model": "gpt-4o",
                "channel_id": "openai",
                "is_stream": 0,
                "ttft_ms": None,
                "duration_ms": 100,
                "status_code": 200,
                "response_body": "{}",
                "input_tokens": 100,
                "cached_tokens": 0,
                "output_tokens": 50,
                "cost": 0.001,
                "error": None,
            }
            writer.write(record)
        writer.stop()
        logs = read_logs(self.db_path, limit=100)
        self.assertEqual(len(logs), 50)


class TestChannelStore(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db_path = Path(self.tmp.name) / "test.db"

    def tearDown(self):
        self.tmp.cleanup()

    def test_replace_and_read_channels_preserves_order_and_json_fields(self):
        replace_channels(
            self.db_path,
            [
                {
                    "id": "first",
                    "name": "First",
                    "type": "chat",
                    "baseurl": "https://first.example.test/v1",
                    "apikey": "${FIRST_KEY}",
                    "auth_mode": "config",
                    "headers": {"X-Test": "yes"},
                    "timeout_seconds": 45,
                    "retry_count": 2,
                    "compat": {"drop_params": ["store"]},
                    "models": [{"model": "gpt-5", "upstream_model": "gpt-4"}],
                    "enabled": False,
                },
                {
                    "id": "second",
                    "type": "messages",
                    "baseurl": "https://second.example.test/v1",
                },
            ],
            default_timeout=30,
        )

        channels = read_channels(self.db_path)

        self.assertEqual([channel["id"] for channel in channels], ["first", "second"])
        self.assertEqual(channels[0]["headers"], {"X-Test": "yes"})
        self.assertEqual(channels[0]["retry_count"], 2)
        self.assertEqual(channels[0]["compat"], {"drop_params": ["store"]})
        self.assertEqual(channels[0]["models"], [{"model": "gpt-5", "upstream_model": "gpt-4"}])
        self.assertFalse(channels[0]["enabled"])
        self.assertEqual(channels[1]["timeout_seconds"], 30)
        self.assertEqual(channels[1]["retry_count"], 3)
        self.assertEqual(channels[1]["auth_mode"], "config")
        self.assertTrue(channels[1]["enabled"])

    def test_replace_channels_removes_deleted_rows(self):
        replace_channels(
            self.db_path,
            [
                {"id": "old", "type": "chat", "baseurl": "https://old.example.test/v1"},
                {"id": "keep", "type": "chat", "baseurl": "https://keep.example.test/v1"},
            ],
        )
        replace_channels(
            self.db_path,
            [
                {"id": "keep", "type": "chat", "baseurl": "https://keep.example.test/v1"},
            ],
        )

        channels = read_channels(self.db_path)

        self.assertEqual([channel["id"] for channel in channels], ["keep"])

    def test_replace_channels_scopes_rows_by_owner(self):
        replace_channels(
            self.db_path,
            [
                {
                    "id": "chat",
                    "type": "chat",
                    "baseurl": "https://alice.example.test/v1",
                }
            ],
            owner_username="alice",
        )
        replace_channels(
            self.db_path,
            [
                {
                    "id": "chat",
                    "type": "chat",
                    "baseurl": "https://bob.example.test/v1",
                }
            ],
            owner_username="bob",
        )

        all_channels = read_channels(self.db_path)
        alice_channels = read_channels(self.db_path, owner_username="alice")
        bob_channels = read_channels(self.db_path, owner_username="bob")

        self.assertEqual(
            [(channel["owner_username"], channel["id"]) for channel in all_channels],
            [("alice", "chat"), ("bob", "chat")],
        )
        self.assertEqual(alice_channels[0]["baseurl"], "https://alice.example.test/v1")
        self.assertEqual(bob_channels[0]["baseurl"], "https://bob.example.test/v1")

    def test_init_db_migrates_channels_defaults_columns(self):
        conn = sqlite3.connect(str(self.db_path))
        conn.executescript(
            """
            CREATE TABLE channels (
                id TEXT PRIMARY KEY,
                position INTEGER NOT NULL,
                name TEXT NOT NULL DEFAULT '',
                type TEXT NOT NULL,
                baseurl TEXT NOT NULL,
                apikey TEXT NOT NULL DEFAULT '',
                auth_mode TEXT NOT NULL DEFAULT 'removed_auth_mode',
                headers_json TEXT NOT NULL DEFAULT '{}',
                timeout_seconds INTEGER NOT NULL,
                compat_json TEXT NOT NULL DEFAULT '{}',
                enabled INTEGER NOT NULL DEFAULT 1,
                created_at REAL NOT NULL,
                updated_at REAL NOT NULL
            );

            INSERT INTO channels (
                id, position, name, type, baseurl, apikey, auth_mode,
                headers_json, timeout_seconds, compat_json, enabled,
                created_at, updated_at
            ) VALUES (
                'legacy', 0, '', 'chat', 'https://legacy.example.test/v1', '',
                'removed_auth_mode', '{}', 30, '{}', 1, 1.0, 1.0
            );
            """
        )
        conn.commit()
        conn.close()

        init_db(self.db_path)

        conn = sqlite3.connect(str(self.db_path))
        columns = {row[1] for row in conn.execute("PRAGMA table_info(channels)").fetchall()}
        retry_count = conn.execute(
            "SELECT retry_count FROM channels WHERE id = 'legacy'"
        ).fetchone()[0]
        auth_mode = conn.execute(
            "SELECT auth_mode FROM channels WHERE id = 'legacy'"
        ).fetchone()[0]
        conn.close()
        self.assertIn("models_json", columns)
        self.assertIn("retry_count", columns)
        self.assertEqual(retry_count, 3)
        self.assertEqual(auth_mode, "config")

    def test_init_db_migrates_legacy_owner_fields(self):
        conn = sqlite3.connect(str(self.db_path))
        conn.executescript(
            """
            CREATE TABLE channels (
                id TEXT PRIMARY KEY,
                position INTEGER NOT NULL,
                name TEXT NOT NULL DEFAULT '',
                type TEXT NOT NULL,
                baseurl TEXT NOT NULL,
                apikey TEXT NOT NULL DEFAULT '',
                auth_mode TEXT NOT NULL DEFAULT 'removed_auth_mode',
                headers_json TEXT NOT NULL DEFAULT '{}',
                timeout_seconds INTEGER NOT NULL,
                compat_json TEXT NOT NULL DEFAULT '{}',
                enabled INTEGER NOT NULL DEFAULT 1,
                created_at REAL NOT NULL,
                updated_at REAL NOT NULL
            );

            INSERT INTO channels (
                id, position, name, type, baseurl, apikey, auth_mode,
                headers_json, timeout_seconds, compat_json, enabled,
                created_at, updated_at
            ) VALUES (
                'legacy', 0, '', 'chat', 'https://legacy.example.test/v1', '',
                'removed_auth_mode', '{}', 30, '{}', 1, 1.0, 1.0
            );

            CREATE TABLE request_logs (
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

            INSERT INTO request_logs (
                request_id, created_at, method, path, client_ip, request_headers,
                request_body, model, upstream_model, channel_id, is_stream,
                ttft_ms, duration_ms, status_code, response_body, input_tokens,
                cached_tokens, output_tokens, cost, error
            ) VALUES (
                'legacy_req', 1.0, 'POST', '/v1/responses', '127.0.0.1', '{}',
                '{}', 'gpt-4o', 'gpt-4o', 'legacy', 0,
                NULL, 10, 200, '{}', 1, 0, 1, 0.0, NULL
            );
            """
        )
        conn.commit()
        conn.close()

        init_db(self.db_path, default_owner_username="root")

        conn = sqlite3.connect(str(self.db_path))
        channel_columns = {
            row[1]
            for row in conn.execute("PRAGMA table_info(channels)").fetchall()
        }
        log_columns = {
            row[1]
            for row in conn.execute("PRAGMA table_info(request_logs)").fetchall()
        }
        channel_pk = [
            row[1]
            for row in sorted(
                conn.execute("PRAGMA table_info(channels)").fetchall(),
                key=lambda row: row[5],
            )
            if row[5]
        ]
        conn.close()
        channels = read_channels(self.db_path, default_owner_username="root")
        logs = read_logs(self.db_path)

        self.assertIn("owner_username", channel_columns)
        self.assertIn("owner_username", log_columns)
        self.assertIn("api_key_id", log_columns)
        self.assertIn("web_search_json", log_columns)
        self.assertEqual(channel_pk, ["owner_username", "id"])
        self.assertEqual(channels[0]["owner_username"], "root")
        self.assertEqual(channels[0]["auth_mode"], "config")
        self.assertEqual(logs[0]["owner_username"], "root")
        self.assertIsNone(logs[0]["api_key_id"])

    def test_init_db_migrates_web_search_usage_limit(self):
        conn = sqlite3.connect(str(self.db_path))
        conn.executescript(
            """
            CREATE TABLE web_search_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                enabled INTEGER NOT NULL DEFAULT 0,
                created_at REAL NOT NULL,
                updated_at REAL NOT NULL
            );

            INSERT INTO web_search_settings (id, enabled, created_at, updated_at)
            VALUES (1, 1, 1.0, 1.0);
            """
        )
        conn.commit()
        conn.close()

        init_db(self.db_path)

        conn = sqlite3.connect(str(self.db_path))
        columns = {
            row[1]
            for row in conn.execute("PRAGMA table_info(web_search_settings)").fetchall()
        }
        key_usage_limit = conn.execute(
            "SELECT key_usage_limit FROM web_search_settings WHERE id = 1"
        ).fetchone()[0]
        conn.close()
        self.assertIn("key_usage_limit", columns)
        self.assertEqual(key_usage_limit, TAVILY_KEY_USAGE_LIMIT)

    def test_init_db_migrates_web_search_key_provider_and_usage_limit(self):
        conn = sqlite3.connect(str(self.db_path))
        conn.executescript(
            """
            CREATE TABLE web_search_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                enabled INTEGER NOT NULL DEFAULT 0,
                key_usage_limit INTEGER NOT NULL DEFAULT 1000,
                created_at REAL NOT NULL,
                updated_at REAL NOT NULL
            );

            CREATE TABLE tavily_keys (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                position INTEGER NOT NULL,
                api_key TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                usage_count INTEGER NOT NULL DEFAULT 0,
                created_at REAL NOT NULL,
                updated_at REAL NOT NULL
            );

            INSERT INTO web_search_settings (id, enabled, key_usage_limit, created_at, updated_at)
            VALUES (1, 1, 1000, 1.0, 1.0);
            INSERT INTO tavily_keys (position, api_key, enabled, usage_count, created_at, updated_at)
            VALUES (0, 'tvly-old', 1, 2, 1.0, 1.0);
            """
        )
        conn.commit()
        conn.close()

        init_db(self.db_path)

        config = read_web_search_config(self.db_path)
        self.assertEqual(config["keys"][0]["provider"], "tavily")
        self.assertEqual(config["keys"][0]["usage_limit"], TAVILY_KEY_USAGE_LIMIT)


class TestUserAndAccessKeyStore(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db_path = Path(self.tmp.name) / "test.db"

    def tearDown(self):
        self.tmp.cleanup()

    def test_superadmin_is_env_authoritative(self):
        ensure_superadmin(self.db_path, "root", "first")
        self.assertIsNotNone(authenticate_user(self.db_path, "root", "first"))

        ensure_superadmin(self.db_path, "root", "second")

        user = authenticate_user(self.db_path, "root", "second")
        self.assertIsNotNone(user)
        self.assertEqual(user["role"], "superadmin")
        self.assertIsNone(authenticate_user(self.db_path, "root", "first"))

    def test_access_api_key_plaintext_is_stored_for_copying_and_hash_is_kept(self):
        ensure_superadmin(self.db_path, "root", "pw")
        create_user(self.db_path, "alice", "alice-pw")

        created = create_access_api_key(self.db_path, "alice", "Laptop")
        raw_key = created["key"]

        self.assertTrue(raw_key.startswith("ocx_"))
        self.assertEqual(created["owner_username"], "alice")
        listed = list_access_api_keys(self.db_path, "alice")
        self.assertEqual(len(listed), 1)
        self.assertEqual(listed[0]["key"], raw_key)
        self.assertEqual(listed[0]["masked_key"], created["masked_key"])

        conn = sqlite3.connect(str(self.db_path))
        row = conn.execute(
            "SELECT key_hash, key_prefix, key_suffix, key_plaintext FROM access_api_keys WHERE id = ?",
            (created["id"],),
        ).fetchone()
        conn.close()

        self.assertEqual(len(row[0]), 64)
        self.assertNotEqual(row[0], raw_key)
        self.assertEqual(row[1], raw_key[:12])
        self.assertEqual(row[2], raw_key[-6:])
        self.assertEqual(row[3], raw_key)

        authenticated = authenticate_access_api_key(self.db_path, raw_key)
        self.assertIsNotNone(authenticated)
        self.assertEqual(authenticated["user"]["username"], "alice")
        self.assertIsNotNone(authenticated["last_used_at"])
        self.assertNotIn("key", authenticated)

    def test_access_api_key_legacy_rows_without_plaintext_are_listed_without_copy_value(self):
        ensure_superadmin(self.db_path, "root", "pw")
        create_user(self.db_path, "alice", "alice-pw")
        now = time.time()
        raw_key = "ocx_legacy-secret"
        conn = sqlite3.connect(str(self.db_path))
        with conn:
            conn.execute(
                """
                INSERT INTO access_api_keys (
                    owner_username, name, key_hash, key_prefix, key_suffix,
                    enabled, created_at, updated_at
                ) VALUES (?, ?, ?, ?, ?, 1, ?, ?)
                """,
                (
                    "alice",
                    "Legacy",
                    "0" * 64,
                    raw_key[:12],
                    raw_key[-6:],
                    now,
                    now,
                ),
            )
        conn.close()

        listed = list_access_api_keys(self.db_path, "alice")

        self.assertEqual(len(listed), 1)
        self.assertIsNone(listed[0]["key"])

    def test_disabled_or_deleted_access_api_key_is_rejected(self):
        ensure_superadmin(self.db_path, "root", "pw")
        create_user(self.db_path, "alice", "alice-pw")
        created = create_access_api_key(self.db_path, "alice", "Laptop")

        set_access_api_key_enabled(self.db_path, created["id"], False)
        self.assertIsNone(authenticate_access_api_key(self.db_path, created["key"]))

        set_access_api_key_enabled(self.db_path, created["id"], True)
        self.assertIsNotNone(authenticate_access_api_key(self.db_path, created["key"]))

        delete_access_api_key(self.db_path, created["id"])
        self.assertIsNone(authenticate_access_api_key(self.db_path, created["key"]))

    def test_disabled_user_access_api_key_is_rejected(self):
        ensure_superadmin(self.db_path, "root", "pw")
        create_user(self.db_path, "alice", "alice-pw")
        created = create_access_api_key(self.db_path, "alice", "Laptop")

        set_user_enabled(self.db_path, "alice", False)

        self.assertIsNone(authenticate_user(self.db_path, "alice", "alice-pw"))
        self.assertIsNone(authenticate_access_api_key(self.db_path, created["key"]))

    def test_delete_user_removes_owned_api_keys_and_channels_but_not_current_user(self):
        ensure_superadmin(self.db_path, "root", "pw")
        create_user(self.db_path, "alice", "alice-pw")
        created = create_access_api_key(self.db_path, "alice", "Laptop")
        replace_channels(
            self.db_path,
            [{"id": "chat", "type": "chat", "baseurl": "https://alice.example.test/v1"}],
            owner_username="alice",
            default_owner_username="root",
        )

        deleted = delete_user(self.db_path, "alice", protected_username="root")

        self.assertEqual(deleted["username"], "alice")
        self.assertIsNone(authenticate_user(self.db_path, "alice", "alice-pw"))
        self.assertIsNone(authenticate_access_api_key(self.db_path, created["key"]))
        self.assertEqual(list_access_api_keys(self.db_path, "alice"), [])
        self.assertEqual(read_channels(self.db_path, owner_username="alice"), [])
        with self.assertRaisesRegex(ValueError, "cannot delete current user"):
            delete_user(self.db_path, "root", protected_username="root")


class TestWebSearchStore(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db_path = Path(self.tmp.name) / "test.db"

    def tearDown(self):
        self.tmp.cleanup()

    def test_save_and_read_web_search_config_keeps_full_keys_and_order(self):
        saved = replace_web_search_config(
            self.db_path,
            {
                "enabled": True,
                "keys": [
                    {"provider": "tavily", "key": "tvly-first", "enabled": True, "usage_limit": 2},
                    {"provider": "tavily", "key": "tvly-second", "enabled": False, "usage_limit": 3},
                ],
            },
        )

        self.assertTrue(saved["enabled"])
        self.assertEqual(saved["providers"], ["tavily"])
        self.assertEqual(saved["default_key_usage_limit"], TAVILY_KEY_USAGE_LIMIT)
        self.assertNotIn("key_usage_limit", saved)
        self.assertEqual([item["key"] for item in saved["keys"]], ["tvly-first", "tvly-second"])
        self.assertEqual([item["provider"] for item in saved["keys"]], ["tavily", "tavily"])
        self.assertEqual([item["usage_limit"] for item in saved["keys"]], [2, 3])
        self.assertEqual([item["usage_count"] for item in saved["keys"]], [0, 0])
        self.assertFalse(saved["keys"][1]["enabled"])

        loaded = read_web_search_config(self.db_path)
        self.assertEqual([item["key"] for item in loaded["keys"]], ["tvly-first", "tvly-second"])
        self.assertEqual([item["usage_limit"] for item in loaded["keys"]], [2, 3])

    def test_save_and_read_web_search_config_allows_legacy_default_usage_limit(self):
        saved = replace_web_search_config(
            self.db_path,
            {
                "enabled": True,
                "key_usage_limit": 2,
                "keys": [{"key": "tvly-first", "enabled": True}],
            },
        )

        self.assertNotIn("key_usage_limit", saved)
        self.assertEqual(saved["keys"][0]["key_usage_limit"], 2)
        self.assertEqual(saved["keys"][0]["usage_limit"], 2)

        loaded = read_web_search_config(self.db_path)
        self.assertNotIn("key_usage_limit", loaded)
        self.assertEqual(loaded["keys"][0]["key_usage_limit"], 2)
        self.assertEqual(loaded["keys"][0]["usage_limit"], 2)

    def test_web_search_usage_limit_must_be_positive_integer(self):
        with self.assertRaises(ValueError):
            replace_web_search_config(
                self.db_path,
                {
                    "enabled": True,
                    "keys": [{"key": "tvly-first", "enabled": True, "usage_limit": 0}],
                },
            )

    def test_web_search_provider_must_be_supported(self):
        with self.assertRaises(ValueError):
            replace_web_search_config(
                self.db_path,
                {
                    "enabled": True,
                    "keys": [{"provider": "other", "key": "search-key", "enabled": True}],
                },
            )

    def test_save_and_read_web_search_config_allows_custom_usage_count(self):
        saved = replace_web_search_config(
            self.db_path,
            {
                "enabled": True,
                "keys": [{"key": "tvly-first", "enabled": True, "usage_count": 7}],
            },
        )

        self.assertEqual(saved["keys"][0]["usage_count"], 7)

        loaded = read_web_search_config(self.db_path)
        self.assertEqual(loaded["keys"][0]["usage_count"], 7)

    def test_web_search_usage_count_must_be_non_negative_integer(self):
        invalid_values = [-1, "1.5", True]
        for value in invalid_values:
            with self.subTest(value=value):
                with self.assertRaises(ValueError):
                    replace_web_search_config(
                        self.db_path,
                        {
                            "enabled": True,
                            "keys": [
                                {
                                    "key": "tvly-first",
                                    "enabled": True,
                                    "usage_count": value,
                                }
                            ],
                        },
                    )

    def test_reserve_uses_enabled_keys_by_position_and_counts_on_request_start(self):
        replace_web_search_config(
            self.db_path,
            {
                "enabled": True,
                "keys": [
                    {"key": "disabled", "enabled": False},
                    {"key": "first", "enabled": True},
                    {"key": "second", "enabled": True},
                ],
            },
        )

        reserved = reserve_tavily_key(self.db_path)

        self.assertIsNotNone(reserved)
        self.assertEqual(reserved["key"], "first")
        self.assertEqual(reserved["position"], 1)
        self.assertEqual(reserved["usage_count"], 1)
        config = read_web_search_config(self.db_path)
        self.assertEqual([item["usage_count"] for item in config["keys"]], [0, 1, 0])

    def test_reserve_switches_to_next_key_after_usage_limit(self):
        saved = replace_web_search_config(
            self.db_path,
            {
                "enabled": True,
                "keys": [
                    {"key": "first", "enabled": True, "usage_limit": 1},
                    {"key": "second", "enabled": True},
                ],
            },
        )
        first_id = saved["keys"][0]["id"]
        conn = sqlite3.connect(str(self.db_path))
        conn.execute(
            "UPDATE tavily_keys SET usage_count = ? WHERE id = ?",
            (1, first_id),
        )
        conn.commit()
        conn.close()

        reserved = reserve_tavily_key(self.db_path)

        self.assertIsNotNone(reserved)
        self.assertEqual(reserved["key"], "second")
        self.assertEqual(reserved["usage_count"], 1)
        self.assertEqual(reserved["key_usage_limit"], TAVILY_KEY_USAGE_LIMIT)

    def test_reserve_switches_to_next_key_after_per_key_usage_limit(self):
        saved = replace_web_search_config(
            self.db_path,
            {
                "enabled": True,
                "keys": [
                    {"key": "first", "enabled": True, "usage_limit": 2},
                    {"key": "second", "enabled": True, "usage_limit": 5},
                ],
            },
        )

        first = reserve_tavily_key(self.db_path)
        second = reserve_tavily_key(self.db_path)
        third = reserve_tavily_key(self.db_path)

        self.assertEqual(saved["keys"][0]["usage_limit"], 2)
        self.assertEqual(first["key"], "first")
        self.assertEqual(first["key_usage_limit"], 2)
        self.assertEqual(second["key"], "first")
        self.assertEqual(second["usage_count"], 2)
        self.assertEqual(third["key"], "second")
        self.assertEqual(third["key_usage_limit"], 5)

    def test_test_reserve_can_use_disabled_key_but_not_exhausted_key(self):
        saved = replace_web_search_config(
            self.db_path,
            {
                "enabled": True,
                "keys": [
                    {"key": "disabled", "enabled": False},
                    {"key": "exhausted", "enabled": True, "usage_limit": 1},
                ],
            },
        )
        disabled_id = saved["keys"][0]["id"]
        exhausted_id = saved["keys"][1]["id"]
        conn = sqlite3.connect(str(self.db_path))
        conn.execute(
            "UPDATE tavily_keys SET usage_count = ? WHERE id = ?",
            (1, exhausted_id),
        )
        conn.commit()
        conn.close()

        disabled = reserve_tavily_key_by_id(self.db_path, disabled_id)
        exhausted = reserve_tavily_key_by_id(self.db_path, exhausted_id)

        self.assertIsNotNone(disabled)
        self.assertEqual(disabled["key"], "disabled")
        self.assertIsNone(exhausted)

    def test_key_string_change_resets_usage_but_enabled_change_preserves_usage(self):
        saved = replace_web_search_config(
            self.db_path,
            {"enabled": True, "keys": [{"provider": "tavily", "key": "same", "enabled": True, "usage_limit": 10}]},
        )
        key_id = saved["keys"][0]["id"]
        reserve_tavily_key(self.db_path)

        toggled = replace_web_search_config(
            self.db_path,
            {"enabled": True, "keys": [{"id": key_id, "provider": "tavily", "key": "same", "enabled": False, "usage_limit": 20}]},
        )
        changed = replace_web_search_config(
            self.db_path,
            {"enabled": True, "keys": [{"id": key_id, "provider": "tavily", "key": "changed", "enabled": True, "usage_limit": 20}]},
        )

        self.assertEqual(toggled["keys"][0]["usage_count"], 1)
        self.assertEqual(toggled["keys"][0]["usage_limit"], 20)
        self.assertEqual(changed["keys"][0]["usage_count"], 0)

    def test_key_provider_case_normalizes_and_preserves_usage(self):
        saved = replace_web_search_config(
            self.db_path,
            {"enabled": True, "keys": [{"provider": "tavily", "key": "same", "enabled": True}]},
        )
        key_id = saved["keys"][0]["id"]
        reserve_tavily_key(self.db_path)

        changed = replace_web_search_config(
            self.db_path,
            {"enabled": True, "keys": [{"id": key_id, "provider": "TAVILY", "key": "same", "enabled": True}]},
        )

        self.assertEqual(changed["keys"][0]["provider"], "tavily")
        self.assertEqual(changed["keys"][0]["usage_count"], 1)

    def test_request_log_can_store_web_search_json(self):
        writer = AsyncDBWriter(self.db_path)
        writer.start()
        writer.write(
            {
                "request_id": "req_web",
                "created_at": time.time(),
                "method": "POST",
                "path": "/v1/responses",
                "client_ip": "127.0.0.1",
                "request_headers": "{}",
                "request_body": "{}",
                "model": "gpt-4o",
                "upstream_model": "gpt-4o",
                "channel_id": "openai",
                "is_stream": 0,
                "ttft_ms": None,
                "duration_ms": 100,
                "status_code": 200,
                "response_body": "{}",
                "input_tokens": 100,
                "cached_tokens": 0,
                "output_tokens": 50,
                "cost": 0.001,
                "web_search_json": json.dumps({"calls": [{"query": "OpenAI"}]}),
                "error": None,
            }
        )
        writer.stop()

        logs = read_logs(self.db_path)

        self.assertEqual(json.loads(logs[0]["web_search_json"])["calls"][0]["query"], "OpenAI")


if __name__ == "__main__":
    unittest.main()
