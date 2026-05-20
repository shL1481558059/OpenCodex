from __future__ import annotations

import json
import tempfile
import time
import unittest
from pathlib import Path

from opencodex_proxy.db import (
    AsyncDBWriter,
    calculate_cost,
    extract_usage,
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
                "completion_tokens": 50,
            }
        }
        result = extract_usage(response, "chat")
        self.assertEqual(result["input_tokens"], 100)
        self.assertEqual(result["cached_tokens"], 0)
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


if __name__ == "__main__":
    unittest.main()
