from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from opencodex_proxy.app import create_app
from opencodex_proxy.db import read_logs
from opencodex_proxy.settings import Settings


class AppTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.config_path = self.root / "config.json"
        self.log_path = self.root / "opencodex.log"
        self.db_path = self.root / "opencodex.db"
        self.config_path.write_text(
            json.dumps(
                {
                    "channels": [
                        {
                            "id": "chat",
                            "type": "chat",
                            "baseurl": "https://example.test/v1",
                            "apikey": "secret",
                            "auth_mode": "config",
                            "timeout_seconds": 30,
                            "compat": {},
                        }
                    ],
                    "routing": {"default_channel": "chat", "model_routes": []},
                }
            ),
            encoding="utf-8",
        )
        settings = Settings(
            host="127.0.0.1",
            port=5000,
            admin_password="pw",
            db_path=self.db_path,
            config_path=self.config_path,
            log_path=self.log_path,
            log_level="DEBUG",
            log_view_level="BASIC",
            default_timeout=30,
            secret_key="test-secret",
        )
        self.app = create_app(settings)
        self.client = self.app.test_client()

    def tearDown(self):
        db_writer = self.app.config.get("OPENCODEX_DB_WRITER")
        if db_writer:
            db_writer.stop()
        self.tmp.cleanup()

    def login(self):
        return self.client.post("/admin", data={"password": "pw"})

    def test_admin_requires_login(self):
        response = self.client.get("/admin/api/config")
        self.assertEqual(response.status_code, 401)

    def test_admin_save_hot_reloads(self):
        self.login()
        candidate = {
            "channels": [
                {
                    "id": "messages",
                    "type": "messages",
                    "baseurl": "https://example.test/v1",
                    "timeout_seconds": 30,
                }
            ],
            "routing": {"default_channel": "messages", "model_routes": []},
        }
        response = self.client.post("/admin/api/config", json=candidate)
        self.assertEqual(response.status_code, 200)
        self.assertEqual(json.loads(self.config_path.read_text())["channels"][0]["id"], "messages")
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        self.assertEqual(manager.expanded["channels"][0]["id"], "messages")

    @patch("opencodex_proxy.app.post_upstream")
    def test_proxy_chat_channel(self, mock_post):
        mock_post.return_value = {
            "id": "chatcmpl_1",
            "model": "upstream",
            "choices": [
                {
                    "message": {"role": "assistant", "content": "pong"},
                    "finish_reason": "stop",
                }
            ],
            "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
        }
        response = self.client.post(
            "/v1/responses",
            json={"model": "mimo-v2.5-pro", "input": "ping"},
            headers={"Authorization": "Bearer client"},
        )
        self.assertEqual(response.status_code, 200)
        data = response.get_json()
        self.assertEqual(data["output"][0]["content"][0]["text"], "pong")
        upstream_payload = mock_post.call_args.args[1]
        self.assertEqual(upstream_payload["messages"][0]["content"], "ping")

    @patch("opencodex_proxy.app.post_upstream")
    def test_mimo_chat_channel_can_force_all_requests_to_messages(self, mock_post):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
            {
                "channels": [
                    {
                        "id": "windhub-mimo",
                        "type": "chat",
                        "baseurl": "https://windhub.cc",
                        "apikey": "secret",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                        "compat": {
                            "force_protocol": "messages",
                            "by_protocol": {
                                "messages": {
                                    "default_params": {"max_tokens": 4096},
                                    "drop_params": ["parallel_tool_calls"],
                                }
                            },
                        },
                    }
                ],
                "routing": {
                    "default_channel": "windhub-mimo",
                    "model_routes": [
                        {"pattern": "mimo-*", "channel": "windhub-mimo"}
                    ],
                },
            }
        )
        mock_post.return_value = {
            "id": "msg_1",
            "type": "message",
            "role": "assistant",
            "model": "mimo-v2.5-pro",
            "content": [{"type": "text", "text": "pong"}],
            "stop_reason": "end_turn",
            "usage": {"input_tokens": 1, "output_tokens": 1},
        }
        response = self.client.post(
            "/v1/responses",
            json={"model": "mimo-v2.5-pro", "input": "ping"},
        )
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.get_json()["output"][0]["content"][0]["text"], "pong")
        upstream_channel = mock_post.call_args.args[0]
        upstream_payload = mock_post.call_args.args[1]
        self.assertEqual(upstream_channel["type"], "messages")
        self.assertEqual(upstream_payload["messages"][0]["content"][0]["text"], "ping")

    @patch("opencodex_proxy.app.post_upstream")
    def test_messages_channel_can_fallback_thinking_for_tool_history(self, mock_post):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
            {
                "channels": [
                    {
                        "id": "windhub-mimo",
                        "type": "messages",
                        "baseurl": "https://windhub.cc",
                        "apikey": "secret",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                        "compat": {
                            "fallback_thinking_on_tool_use": True,
                            "default_params": {"max_tokens": 4096},
                        },
                    }
                ],
                "routing": {"default_channel": "windhub-mimo", "model_routes": []},
            }
        )
        mock_post.return_value = {
            "id": "msg_1",
            "type": "message",
            "role": "assistant",
            "model": "mimo-v2.5-pro",
            "content": [{"type": "text", "text": "done"}],
            "stop_reason": "end_turn",
            "usage": {"input_tokens": 1, "output_tokens": 1},
        }

        response = self.client.post(
            "/v1/responses",
            json={
                "model": "mimo-v2.5-pro",
                "input": [
                    {"role": "user", "content": [{"type": "input_text", "text": "run"}]},
                    {
                        "type": "function_call",
                        "call_id": "call_1",
                        "name": "exec_command",
                        "arguments": "{\"cmd\":\"pwd\"}",
                    },
                    {
                        "type": "function_call_output",
                        "call_id": "call_1",
                        "output": "/tmp",
                    },
                ],
            },
        )

        self.assertEqual(response.status_code, 200)
        upstream_payload = mock_post.call_args.args[1]
        assistant_content = upstream_payload["messages"][1]["content"]
        self.assertEqual(assistant_content[0]["type"], "thinking")
        self.assertEqual(assistant_content[0]["signature"], "")
        self.assertEqual(assistant_content[1]["type"], "tool_use")

    @patch("opencodex_proxy.app.post_upstream")
    def test_reasoning_cache_does_not_cross_prompt_cache_keys(self, mock_post):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
            {
                "channels": [
                    {
                        "id": "messages",
                        "type": "messages",
                        "baseurl": "https://example.test/v1",
                        "apikey": "secret",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                        "compat": {},
                    }
                ],
                "routing": {"default_channel": "messages", "model_routes": []},
            }
        )
        mock_post.side_effect = [
            {
                "id": "msg_1",
                "type": "message",
                "role": "assistant",
                "model": "m",
                "content": [
                    {"type": "thinking", "thinking": "thread-a-thinking", "signature": "sig-a"},
                    {
                        "type": "tool_use",
                        "id": "call_same",
                        "name": "exec_command",
                        "input": {"cmd": "pwd"},
                    },
                ],
                "stop_reason": "tool_use",
                "usage": {"input_tokens": 1, "output_tokens": 1},
            },
            {
                "id": "msg_2",
                "type": "message",
                "role": "assistant",
                "model": "m",
                "content": [{"type": "text", "text": "done"}],
                "stop_reason": "end_turn",
                "usage": {"input_tokens": 1, "output_tokens": 1},
            },
        ]

        first = self.client.post(
            "/v1/messages",
            json={
                "model": "m",
                "prompt_cache_key": "thread-a",
                "messages": [
                    {"role": "user", "content": [{"type": "text", "text": "run"}]}
                ],
            },
        )
        self.assertEqual(first.status_code, 200)

        second = self.client.post(
            "/v1/messages",
            json={
                "model": "m",
                "prompt_cache_key": "thread-b",
                "messages": [
                    {
                        "role": "assistant",
                        "content": [
                            {
                                "type": "tool_use",
                                "id": "call_same",
                                "name": "exec_command",
                                "input": {"cmd": "pwd"},
                            }
                        ],
                    }
                ],
            },
        )

        self.assertEqual(second.status_code, 200)
        second_upstream_payload = mock_post.call_args_list[1].args[1]
        second_content = second_upstream_payload["messages"][0]["content"]
        self.assertEqual(second_content[0]["type"], "tool_use")

    @patch("opencodex_proxy.app.post_upstream")
    def test_chat_reasoning_cache_does_not_cross_prompt_cache_keys(self, mock_post):
        mock_post.side_effect = [
            {
                "id": "chatcmpl_1",
                "model": "m",
                "choices": [
                    {
                        "message": {
                            "role": "assistant",
                            "content": "",
                            "reasoning_content": "thread-a-reasoning",
                            "tool_calls": [
                                {
                                    "id": "call_same",
                                    "type": "function",
                                    "function": {
                                        "name": "exec_command",
                                        "arguments": "{\"cmd\":\"pwd\"}",
                                    },
                                }
                            ],
                        },
                        "finish_reason": "tool_calls",
                    }
                ],
                "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
            },
            {
                "id": "chatcmpl_2",
                "model": "m",
                "choices": [
                    {
                        "message": {"role": "assistant", "content": "done"},
                        "finish_reason": "stop",
                    }
                ],
                "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
            },
        ]

        first = self.client.post(
            "/v1/chat/completions",
            json={
                "model": "m",
                "prompt_cache_key": "thread-a",
                "messages": [{"role": "user", "content": "run"}],
            },
        )
        self.assertEqual(first.status_code, 200)

        second = self.client.post(
            "/v1/chat/completions",
            json={
                "model": "m",
                "prompt_cache_key": "thread-b",
                "messages": [
                    {
                        "role": "assistant",
                        "content": "",
                        "tool_calls": [
                            {
                                "id": "call_same",
                                "type": "function",
                                "function": {
                                    "name": "exec_command",
                                    "arguments": "{\"cmd\":\"pwd\"}",
                                },
                            }
                        ],
                    }
                ],
            },
        )

        self.assertEqual(second.status_code, 200)
        second_upstream_payload = mock_post.call_args_list[1].args[1]
        self.assertNotIn("reasoning_content", second_upstream_payload["messages"][0])

    def test_stream_rejected(self):
        response = self.client.post(
            "/v1/chat/completions",
            json={"model": "m", "messages": [{"role": "user", "content": "hi"}], "stream": True},
        )
        self.assertEqual(response.status_code, 400)

    @patch("opencodex_proxy.app.post_upstream")
    def test_responses_stream_is_synthesized(self, mock_post):
        mock_post.return_value = {
            "id": "chatcmpl_1",
            "model": "upstream",
            "choices": [
                {
                    "message": {"role": "assistant", "content": "pong"},
                    "finish_reason": "stop",
                }
            ],
        }
        response = self.client.post(
            "/v1/responses", json={"model": "m", "input": "ping", "stream": True}
        )
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.mimetype, "text/event-stream")
        body = response.get_data(as_text=True)
        self.assertIn("event: response.output_item.done", body)
        self.assertIn("event: response.completed", body)
        upstream_payload = mock_post.call_args.args[1]
        self.assertIs(upstream_payload["stream"], False)

    @patch("opencodex_proxy.app.stream_upstream")
    def test_responses_stream_to_messages_streams_upstream(self, mock_stream):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
            {
                "channels": [
                    {
                        "id": "messages",
                        "type": "messages",
                        "baseurl": "https://example.test/v1",
                        "apikey": "secret",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                        "compat": {"default_params": {"max_tokens": 4096}},
                    }
                ],
                "routing": {"default_channel": "messages", "model_routes": []},
            }
        )
        mock_stream.return_value = iter(
            [
                "event: message_start\n",
                'data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"mimo-v2.5-pro","content":[],"usage":{"input_tokens":1,"output_tokens":0}}}\n',
                "\n",
                "event: content_block_start\n",
                'data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}\n',
                "\n",
                "event: content_block_delta\n",
                'data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"pong"}}\n',
                "\n",
                "event: content_block_stop\n",
                'data: {"type":"content_block_stop","index":0}\n',
                "\n",
                "event: message_delta\n",
                'data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}\n',
                "\n",
                "event: message_stop\n",
                'data: {"type":"message_stop"}\n',
                "\n",
            ]
        )

        response = self.client.post(
            "/v1/responses", json={"model": "mimo-v2.5-pro", "input": "ping", "stream": True}
        )

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.mimetype, "text/event-stream")
        body = response.get_data(as_text=True)
        self.assertIn("event: response.output_text.delta", body)
        self.assertIn("pong", body)
        self.assertIn("event: response.completed", body)
        upstream_payload = mock_stream.call_args.args[1]
        self.assertIs(upstream_payload["stream"], True)

    @patch("opencodex_proxy.app.stream_upstream")
    def test_messages_stream_is_written_to_db_after_completion(self, mock_stream):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
            {
                "channels": [
                    {
                        "id": "messages",
                        "type": "messages",
                        "baseurl": "https://example.test/v1",
                        "apikey": "secret",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                        "compat": {"default_params": {"max_tokens": 4096}},
                    }
                ],
                "routing": {"default_channel": "messages", "model_routes": []},
            }
        )
        mock_stream.return_value = iter(
            [
                "event: message_start\n",
                'data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"mimo-v2.5-pro","content":[],"usage":{"input_tokens":3,"output_tokens":0}}}\n',
                "\n",
                "event: content_block_start\n",
                'data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}\n',
                "\n",
                "event: content_block_delta\n",
                'data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"po"}}\n',
                "\n",
                "event: content_block_delta\n",
                'data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ng"}}\n',
                "\n",
                "event: message_delta\n",
                'data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}\n',
                "\n",
                "event: message_stop\n",
                'data: {"type":"message_stop"}\n',
                "\n",
            ]
        )

        response = self.client.post(
            "/v1/responses", json={"model": "mimo-v2.5-pro", "input": "ping", "stream": True}
        )

        self.assertEqual(response.status_code, 200)
        body = response.get_data(as_text=True)
        self.assertIn("pong", body)
        self.assertIn("event: response.completed", body)

        db_writer = self.app.config["OPENCODEX_DB_WRITER"]
        db_writer.stop()
        logs = read_logs(self.db_path)
        self.assertEqual(len(logs), 1)
        log = logs[0]
        self.assertEqual(log["is_stream"], 1)
        self.assertEqual(log["status_code"], 200)
        self.assertIsNotNone(log["ttft_ms"])
        self.assertGreaterEqual(log["duration_ms"], log["ttft_ms"])
        self.assertEqual(log["input_tokens"], 3)
        self.assertEqual(log["output_tokens"], 2)
        response_body = json.loads(log["response_body"])
        self.assertEqual(response_body["content"][0]["text"], "pong")

    @patch("opencodex_proxy.app.post_upstream")
    def test_logs_are_available_and_basic(self, mock_post):
        mock_post.return_value = {
            "id": "chatcmpl_1",
            "model": "upstream",
            "choices": [{"message": {"role": "assistant", "content": "pong"}}],
        }
        self.client.post("/v1/responses", json={"model": "m", "input": "ping"})
        db_writer = self.app.config["OPENCODEX_DB_WRITER"]
        db_writer.stop()
        self.login()
        response = self.client.get("/admin/api/logs")
        self.assertEqual(response.status_code, 200)
        events = response.get_json()["events"]
        self.assertTrue(events)
        self.assertIn("status_code", events[-1])


if __name__ == "__main__":
    unittest.main()
