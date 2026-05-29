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
    calculate_cost,
    extract_usage,
    init_db,
    read_channels,
    read_logs,
    read_web_search_config,
    replace_channels,
    replace_web_search_config,
    reserve_tavily_key,
    reserve_tavily_key_by_id,
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
        self.assertEqual(channels[1]["auth_mode"], "pass_through_or_config")
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
                auth_mode TEXT NOT NULL DEFAULT 'pass_through_or_config',
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
                'pass_through_or_config', '{}', 30, '{}', 1, 1.0, 1.0
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
        conn.close()
        self.assertIn("models_json", columns)
        self.assertIn("retry_count", columns)
        self.assertEqual(retry_count, 3)

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
                    {"key": "tvly-first", "enabled": True},
                    {"key": "tvly-second", "enabled": False},
                ],
            },
        )

        self.assertTrue(saved["enabled"])
        self.assertEqual(saved["key_usage_limit"], TAVILY_KEY_USAGE_LIMIT)
        self.assertEqual([item["key"] for item in saved["keys"]], ["tvly-first", "tvly-second"])
        self.assertEqual([item["usage_count"] for item in saved["keys"]], [0, 0])
        self.assertFalse(saved["keys"][1]["enabled"])

        loaded = read_web_search_config(self.db_path)
        self.assertEqual([item["key"] for item in loaded["keys"]], ["tvly-first", "tvly-second"])

    def test_save_and_read_web_search_config_allows_custom_usage_limit(self):
        saved = replace_web_search_config(
            self.db_path,
            {
                "enabled": True,
                "key_usage_limit": 2,
                "keys": [{"key": "tvly-first", "enabled": True}],
            },
        )

        self.assertEqual(saved["key_usage_limit"], 2)
        self.assertEqual(saved["keys"][0]["key_usage_limit"], 2)

        loaded = read_web_search_config(self.db_path)
        self.assertEqual(loaded["key_usage_limit"], 2)
        self.assertEqual(loaded["keys"][0]["key_usage_limit"], 2)

    def test_web_search_usage_limit_must_be_positive_integer(self):
        with self.assertRaises(ValueError):
            replace_web_search_config(
                self.db_path,
                {
                    "enabled": True,
                    "key_usage_limit": 0,
                    "keys": [{"key": "tvly-first", "enabled": True}],
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
                    {"key": "first", "enabled": True},
                    {"key": "second", "enabled": True},
                ],
            },
        )
        first_id = saved["keys"][0]["id"]
        conn = sqlite3.connect(str(self.db_path))
        conn.execute(
            "UPDATE tavily_keys SET usage_count = ? WHERE id = ?",
            (TAVILY_KEY_USAGE_LIMIT, first_id),
        )
        conn.commit()
        conn.close()

        reserved = reserve_tavily_key(self.db_path)

        self.assertIsNotNone(reserved)
        self.assertEqual(reserved["key"], "second")
        self.assertEqual(reserved["usage_count"], 1)
        self.assertEqual(reserved["key_usage_limit"], TAVILY_KEY_USAGE_LIMIT)

    def test_reserve_switches_to_next_key_after_configured_usage_limit(self):
        saved = replace_web_search_config(
            self.db_path,
            {
                "enabled": True,
                "key_usage_limit": 2,
                "keys": [
                    {"key": "first", "enabled": True},
                    {"key": "second", "enabled": True},
                ],
            },
        )

        first = reserve_tavily_key(self.db_path)
        second = reserve_tavily_key(self.db_path)
        third = reserve_tavily_key(self.db_path)

        self.assertEqual(saved["key_usage_limit"], 2)
        self.assertEqual(first["key"], "first")
        self.assertEqual(first["key_usage_limit"], 2)
        self.assertEqual(second["key"], "first")
        self.assertEqual(second["usage_count"], 2)
        self.assertEqual(third["key"], "second")
        self.assertEqual(third["key_usage_limit"], 2)

    def test_test_reserve_can_use_disabled_key_but_not_exhausted_key(self):
        saved = replace_web_search_config(
            self.db_path,
            {
                "enabled": True,
                "keys": [
                    {"key": "disabled", "enabled": False},
                    {"key": "exhausted", "enabled": True},
                ],
            },
        )
        disabled_id = saved["keys"][0]["id"]
        exhausted_id = saved["keys"][1]["id"]
        conn = sqlite3.connect(str(self.db_path))
        conn.execute(
            "UPDATE tavily_keys SET usage_count = ? WHERE id = ?",
            (TAVILY_KEY_USAGE_LIMIT, exhausted_id),
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
            {"enabled": True, "keys": [{"key": "same", "enabled": True}]},
        )
        key_id = saved["keys"][0]["id"]
        reserve_tavily_key(self.db_path)

        toggled = replace_web_search_config(
            self.db_path,
            {"enabled": True, "keys": [{"id": key_id, "key": "same", "enabled": False}]},
        )
        changed = replace_web_search_config(
            self.db_path,
            {"enabled": True, "keys": [{"id": key_id, "key": "changed", "enabled": True}]},
        )

        self.assertEqual(toggled["keys"][0]["usage_count"], 1)
        self.assertEqual(changed["keys"][0]["usage_count"], 0)

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
