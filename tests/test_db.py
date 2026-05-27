from __future__ import annotations

import json
import sqlite3
import tempfile
import time
import unittest
from pathlib import Path

from opencodex_proxy.db import (
    AsyncDBWriter,
    calculate_cost,
    extract_usage,
    init_db,
    read_channels,
    read_logs,
    replace_channels,
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
        self.assertEqual(channels[0]["compat"], {"drop_params": ["store"]})
        self.assertEqual(channels[0]["models"], [{"model": "gpt-5", "upstream_model": "gpt-4"}])
        self.assertFalse(channels[0]["enabled"])
        self.assertEqual(channels[1]["timeout_seconds"], 30)
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

    def test_init_db_migrates_channels_models_column(self):
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
            """
        )
        conn.commit()
        conn.close()

        init_db(self.db_path)

        conn = sqlite3.connect(str(self.db_path))
        columns = {row[1] for row in conn.execute("PRAGMA table_info(channels)").fetchall()}
        conn.close()
        self.assertIn("models_json", columns)


if __name__ == "__main__":
    unittest.main()
