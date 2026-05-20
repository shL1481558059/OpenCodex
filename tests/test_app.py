from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from opencodex_proxy.app import create_app
from opencodex_proxy.db import read_channels, read_logs
from opencodex_proxy.settings import Settings


class AppTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.log_path = self.root / "opencodex.log"
        self.db_path = self.root / "opencodex.db"
        settings = Settings(
            host="127.0.0.1",
            port=5000,
            admin_password="pw",
            db_path=self.db_path,
            log_path=self.log_path,
            log_level="DEBUG",
            log_view_level="BASIC",
            default_timeout=30,
            secret_key="test-secret",
        )
        self.app = create_app(settings)
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
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
                ]
            }
        )
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
            ]
        }
        response = self.client.post("/admin/api/config", json=candidate)
        self.assertEqual(response.status_code, 200)
        self.assertEqual(read_channels(self.db_path)[0]["id"], "messages")
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        self.assertEqual(manager.expanded["channels"][0]["id"], "messages")
        self.assertNotIn("routing", manager.expanded)

    def test_admin_can_add_channel(self):
        self.login()
        compat = {
            "fallback_thinking_on_tool_use": True,
            "rename_params": {"max_output_tokens": "max_tokens"},
            "drop_params": ["parallel_tool_calls"],
            "force_params": {"max_tokens": 4096},
            "default_params": {"temperature": 0.2, "metadata": {"source": "admin"}},
            "unsupported_params": ["stream"],
        }
        candidate = {
            "channels": [
                {
                    "id": "chat",
                    "type": "chat",
                    "baseurl": "https://example.test/v1",
                    "timeout_seconds": 30,
                },
                {
                    "id": "messages",
                    "type": "messages",
                    "baseurl": "https://messages.example.test/v1",
                    "timeout_seconds": 45,
                    "compat": compat,
                },
            ]
        }

        response = self.client.post("/admin/api/config", json=candidate)

        self.assertEqual(response.status_code, 200)
        saved = response.get_json()
        self.assertEqual(len(saved["channels"]), 2)
        self.assertEqual(saved["channels"][1]["id"], "messages")
        self.assertEqual(saved["channels"][1]["compat"], compat)
        self.assertEqual(read_channels(self.db_path)[1]["compat"], compat)
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        self.assertEqual(len(manager.expanded["channels"]), 2)

    def test_admin_can_edit_channel(self):
        self.login()
        compat = {
            "fallback_thinking_on_tool_use": True,
            "rename_params": {"max_output_tokens": "max_tokens"},
            "force_params": {"max_tokens": 2048, "temperature": 0},
            "default_params": {"metadata": {"source": "edited"}},
        }
        candidate = {
            "channels": [
                {
                    "id": "chat",
                    "name": "Edited Chat",
                    "type": "messages",
                    "baseurl": "https://edited.example.test/v1",
                    "apikey": "new-secret",
                    "auth_mode": "config",
                    "timeout_seconds": 45,
                    "headers": {"X-Test": "yes"},
                    "compat": compat,
                    "enabled": False,
                }
            ]
        }

        response = self.client.post("/admin/api/config", json=candidate)

        self.assertEqual(response.status_code, 200)
        channel = response.get_json()["channels"][0]
        self.assertEqual(channel["name"], "Edited Chat")
        self.assertEqual(channel["type"], "messages")
        self.assertEqual(channel["baseurl"], "https://edited.example.test/v1")
        self.assertEqual(channel["headers"]["X-Test"], "yes")
        self.assertEqual(channel["compat"], compat)
        self.assertFalse(channel["enabled"])
        self.assertEqual(read_channels(self.db_path)[0]["compat"], compat)
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        self.assertFalse(manager.expanded["channels"][0]["enabled"])

    def test_admin_can_delete_channel(self):
        self.login()
        candidate = {
            "channels": [
                {
                    "id": "messages",
                    "type": "messages",
                    "baseurl": "https://messages.example.test/v1",
                    "timeout_seconds": 45,
                }
            ]
        }

        response = self.client.post("/admin/api/config", json=candidate)

        self.assertEqual(response.status_code, 200)
        saved = response.get_json()
        self.assertEqual(len(saved["channels"]), 1)
        self.assertEqual(saved["channels"][0]["id"], "messages")
        self.assertNotIn("chat", [channel["id"] for channel in saved["channels"]])
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        self.assertEqual(manager.expanded["channels"][0]["id"], "messages")

    @patch("opencodex_proxy.app.post_upstream")
    def test_agentrouter_chat_channel_converts_responses_request(self, mock_post):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
            {
                "channels": [
                    {
                        "id": "agentrouter-deepseek-v4-pro",
                        "name": "AgentRouter DeepSeek V4 Pro",
                        "type": "chat",
                        "baseurl": "https://agentrouter.org/v1",
                        "apikey": "${AGENT_ROUTER_TOKEN}",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                        "compat": {},
                    }
                ]
            }
        )
        mock_post.return_value = {
            "id": "chatcmpl_1",
            "model": "deepseek-v4-pro",
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
            json={"model": "deepseek-v4-pro", "input": "ping"},
        )
        self.assertEqual(response.status_code, 200)
        data = response.get_json()
        self.assertEqual(data["model"], "deepseek-v4-pro")
        self.assertEqual(data["output"][0]["content"][0]["text"], "pong")
        upstream_channel = mock_post.call_args.args[0]
        upstream_payload = mock_post.call_args.args[1]
        self.assertEqual(upstream_channel["id"], "agentrouter-deepseek-v4-pro")
        self.assertEqual(upstream_channel["type"], "chat")
        self.assertEqual(upstream_channel["baseurl"], "https://agentrouter.org/v1")
        self.assertEqual(upstream_payload["model"], "deepseek-v4-pro")
        self.assertEqual(upstream_payload["messages"][0]["content"], "ping")

    @patch("opencodex_proxy.app.post_upstream")
    def test_responses_to_messages_converts_without_protocol_specific_config(
        self, mock_post
    ):
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
                ]
            }
        )
        mock_post.return_value = {
            "id": "msg_1",
            "type": "message",
            "role": "assistant",
            "model": "m",
            "content": [{"type": "text", "text": "pong"}],
            "stop_reason": "end_turn",
            "usage": {"input_tokens": 1, "output_tokens": 1},
        }

        response = self.client.post(
            "/v1/responses",
            json={
                "model": "m",
                "instructions": "be brief",
                "input": "ping",
                "max_output_tokens": 32,
            },
        )

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.get_json()["output"][0]["content"][0]["text"], "pong")
        upstream_channel = mock_post.call_args.args[0]
        upstream_payload = mock_post.call_args.args[1]
        self.assertEqual(upstream_channel["type"], "messages")
        self.assertEqual(upstream_payload["system"], "be brief")
        self.assertEqual(upstream_payload["messages"][0]["content"][0]["text"], "ping")
        self.assertEqual(upstream_payload["max_tokens"], 32)

    @patch("opencodex_proxy.app.post_upstream")
    def test_responses_to_chat_applies_compat_rules(self, mock_post):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
            {
                "channels": [
                    {
                        "id": "chat-compat",
                        "type": "chat",
                        "baseurl": "https://example.test/v1",
                        "apikey": "secret",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                        "compat": {
                            "default_params": {"presence_penalty": 0},
                            "rename_params": {"temperature": "top_p"},
                            "drop_params": ["metadata"],
                            "force_params": {"response_format": {"type": "text"}},
                        },
                    }
                ]
            }
        )
        mock_post.return_value = {
            "id": "chatcmpl_compat",
            "model": "m",
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
            json={
                "model": "m",
                "input": "ping",
                "temperature": 0.7,
                "metadata": {"source": "test"},
            },
        )

        self.assertEqual(response.status_code, 200)
        upstream_payload = mock_post.call_args.args[1]
        self.assertEqual(upstream_payload["presence_penalty"], 0)
        self.assertEqual(upstream_payload["top_p"], 0.7)
        self.assertNotIn("temperature", upstream_payload)
        self.assertNotIn("metadata", upstream_payload)
        self.assertEqual(upstream_payload["response_format"], {"type": "text"})

    def test_responses_to_chat_rejects_unsupported_params(self):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
            {
                "channels": [
                    {
                        "id": "chat-compat",
                        "type": "chat",
                        "baseurl": "https://example.test/v1",
                        "compat": {
                            "unsupported_params": ["reasoning"],
                        },
                    }
                ]
            }
        )

        response = self.client.post(
            "/v1/responses",
            json={
                "model": "m",
                "input": "ping",
                "reasoning": {"effort": "medium"},
            },
        )

        self.assertEqual(response.status_code, 400)
        self.assertIn("upstream does not support parameter(s): reasoning", response.get_json()["error"]["message"])

    @patch("opencodex_proxy.app.post_upstream")
    def test_responses_chat_tool_calls_are_returned_as_function_call_items(self, mock_post):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
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
                ]
            }
        )
        mock_post.return_value = {
            "id": "chatcmpl_tool",
            "model": "m",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": "",
                        "tool_calls": [
                            {
                                "id": "call_1",
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
        }

        response = self.client.post("/v1/responses", json={"model": "m", "input": "run"})

        self.assertEqual(response.status_code, 200)
        payload = response.get_json()
        self.assertEqual(len(payload["output"]), 1)
        self.assertEqual(payload["output"][0]["type"], "function_call")
        self.assertEqual(payload["output"][0]["call_id"], "call_1")
        self.assertEqual(payload["output"][0]["name"], "exec_command")

    @patch("opencodex_proxy.app.post_upstream")
    def test_responses_chat_apply_patch_tool_calls_are_returned_as_custom_tool_calls(
        self, mock_post
    ):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
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
                ]
            }
        )
        patch_text = "*** Begin Patch\n*** Update File: sample.txt\n@@\n-alpha\n+beta\n*** End Patch"
        arguments = json.dumps(
            {
                "command": json.dumps(["apply_patch", patch_text], ensure_ascii=False),
                "metadata": {"paths": ["sample.txt"], "notes": [{"text": "保持"}]},
            },
            ensure_ascii=False,
        )
        mock_post.return_value = {
            "id": "chatcmpl_patch",
            "model": "deepseek-v4-pro",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": "",
                        "tool_calls": [
                            {
                                "id": "call_patch",
                                "type": "function",
                                "function": {
                                    "name": "apply_patch",
                                    "arguments": arguments,
                                },
                            }
                        ],
                    },
                    "finish_reason": "tool_calls",
                }
            ],
            "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
        }

        response = self.client.post(
            "/v1/responses",
            json={"model": "deepseek-v4-pro", "input": "patch"},
        )

        self.assertEqual(response.status_code, 200)
        item = response.get_json()["output"][0]
        self.assertEqual(item["type"], "custom_tool_call")
        self.assertEqual(item["call_id"], "call_patch")
        self.assertEqual(item["name"], "apply_patch")
        self.assertEqual(item["input"], patch_text)
        self.assertNotIn("arguments", item)

    @patch("opencodex_proxy.app.post_upstream")
    def test_responses_chat_followup_injects_reasoning_content(self, mock_post):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
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
                ]
            }
        )
        mock_post.side_effect = [
            {
                "id": "chatcmpl_1",
                "model": "m",
                "choices": [
                    {
                        "message": {
                            "role": "assistant",
                            "content": "",
                            "reasoning_content": "need to inspect the current directory",
                            "tool_calls": [
                                {
                                    "id": "call_1",
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
            "/v1/responses",
            json={
                "model": "m",
                "prompt_cache_key": "thread-a",
                "input": "run",
            },
        )
        self.assertEqual(first.status_code, 200)

        second = self.client.post(
            "/v1/responses",
            json={
                "model": "m",
                "prompt_cache_key": "thread-a",
                "input": [
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

        self.assertEqual(second.status_code, 200)
        second_upstream_payload = mock_post.call_args_list[1].args[1]
        self.assertEqual(
            second_upstream_payload["messages"][0]["reasoning_content"],
            "need to inspect the current directory",
        )
        self.assertEqual(second_upstream_payload["messages"][0]["tool_calls"][0]["id"], "call_1")

    @patch("opencodex_proxy.app.post_upstream")
    def test_chat_channel_type_is_used_even_for_tool_requests(self, mock_post):
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
                            "tool_request_protocol": "messages",
                            "by_protocol": {
                                "messages": {
                                    "default_params": {"max_tokens": 4096},
                                    "drop_params": ["parallel_tool_calls"],
                                }
                            },
                        },
                    }
                ]
            }
        )
        mock_post.return_value = {
            "id": "chatcmpl_1",
            "model": "mimo-v2.5-pro",
            "choices": [
                {
                    "message": {"role": "assistant", "content": "done"},
                    "finish_reason": "stop",
                }
            ],
            "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
        }
        response = self.client.post(
            "/v1/responses",
            json={
                "model": "mimo-v2.5-pro",
                "input": [
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
        self.assertEqual(response.get_json()["output"][0]["content"][0]["text"], "done")
        self.assertNotIn("force_protocol", manager.raw["channels"][0]["compat"])
        self.assertNotIn("tool_request_protocol", manager.raw["channels"][0]["compat"])
        self.assertNotIn("by_protocol", manager.raw["channels"][0]["compat"])
        upstream_channel = mock_post.call_args.args[0]
        upstream_payload = mock_post.call_args.args[1]
        self.assertEqual(upstream_channel["type"], "chat")
        self.assertEqual(upstream_payload["messages"][0]["tool_calls"][0]["id"], "call_1")
        self.assertEqual(upstream_payload["messages"][1]["role"], "tool")
        self.assertNotIn("max_tokens", upstream_payload)

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
                ]
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
                ]
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
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
            {
                "channels": [
                    {
                        "id": "responses",
                        "type": "responses",
                        "baseurl": "https://example.test/v1",
                        "apikey": "secret",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                        "compat": {},
                    }
                ]
            }
        )
        mock_post.return_value = {
            "id": "resp_1",
            "model": "upstream",
            "output": [
                {
                    "id": "msg_1",
                    "type": "message",
                    "status": "completed",
                    "role": "assistant",
                    "content": [{"type": "output_text", "text": "pong"}],
                }
            ],
            "status": "completed",
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
                ]
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
                ]
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

    @patch("opencodex_proxy.app.stream_upstream")
    def test_responses_stream_to_chat_streams_upstream_text(self, mock_stream):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
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
                ]
            }
        )
        mock_stream.return_value = iter(
            [
                'data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{"role":"assistant","content":"po"},"finish_reason":null}]}\n',
                "\n",
                'data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{"content":"ng"},"finish_reason":null}]}\n',
                "\n",
                'data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}\n',
                "\n",
                "data: [DONE]\n",
                "\n",
            ]
        )

        response = self.client.post(
            "/v1/responses", json={"model": "deepseek-v4-pro", "input": "ping", "stream": True}
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
    def test_responses_stream_to_chat_streams_tool_calls(self, mock_stream):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
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
                ]
            }
        )
        mock_stream.return_value = iter(
            [
                'data: {"id":"chatcmpl_tool","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"exec_command","arguments":"{\\"cmd\\":\\"p"}}]},"finish_reason":null}]}\n',
                "\n",
                'data: {"id":"chatcmpl_tool","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"wd\\"}"}}]},"finish_reason":null}]}\n',
                "\n",
                'data: {"id":"chatcmpl_tool","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}\n',
                "\n",
                "data: [DONE]\n",
                "\n",
            ]
        )

        response = self.client.post(
            "/v1/responses", json={"model": "deepseek-v4-pro", "input": "run", "stream": True}
        )

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.mimetype, "text/event-stream")
        body = response.get_data(as_text=True)
        self.assertIn("event: response.output_item.done", body)
        self.assertIn("function_call", body)
        self.assertIn("\"call_id\":\"call_1\"", body)
        self.assertIn("\"name\":\"exec_command\"", body)
        self.assertIn("event: response.completed", body)
        upstream_payload = mock_stream.call_args.args[1]
        self.assertIs(upstream_payload["stream"], True)

    @patch("opencodex_proxy.app.stream_upstream")
    def test_responses_stream_to_chat_maps_apply_patch_to_custom_tool_call(self, mock_stream):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
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
                ]
            }
        )
        patch_text = "*** Begin Patch\n*** Update File: data.json\n@@\n-  \"old\": true\n+  \"old\": false\n*** End Patch"
        arguments = json.dumps(
            {
                "command": [
                    "apply_patch",
                    patch_text,
                ],
                "metadata": {
                    "paths": ["data.json"],
                    "notes": [{"kind": "unicode", "text": "保持"}],
                },
            },
            ensure_ascii=False,
        )
        chunk_1 = {
            "id": "chatcmpl_tool",
            "object": "chat.completion.chunk",
            "created": 1,
            "model": "deepseek-v4-pro",
            "choices": [
                {
                    "index": 0,
                    "delta": {
                        "role": "assistant",
                        "tool_calls": [
                            {
                                "index": 0,
                                "id": "call_patch",
                                "type": "function",
                                "function": {
                                    "name": "apply_patch",
                                    "arguments": arguments[:40],
                                },
                            }
                        ],
                    },
                    "finish_reason": None,
                }
            ],
        }
        chunk_2 = {
            "id": "chatcmpl_tool",
            "object": "chat.completion.chunk",
            "created": 1,
            "model": "deepseek-v4-pro",
            "choices": [
                {
                    "index": 0,
                    "delta": {
                        "tool_calls": [
                            {
                                "index": 0,
                                "function": {"arguments": arguments[40:]},
                            }
                        ]
                    },
                    "finish_reason": None,
                }
            ],
        }
        mock_stream.return_value = iter(
            [
                f"data: {json.dumps(chunk_1, ensure_ascii=False)}\n",
                "\n",
                f"data: {json.dumps(chunk_2, ensure_ascii=False)}\n",
                "\n",
                'data: {"id":"chatcmpl_tool","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}\n',
                "\n",
                "data: [DONE]\n",
                "\n",
            ]
        )

        response = self.client.post(
            "/v1/responses", json={"model": "deepseek-v4-pro", "input": "patch", "stream": True}
        )

        self.assertEqual(response.status_code, 200)
        body = response.get_data(as_text=True)
        self.assertIn("\"type\":\"custom_tool_call\"", body)
        self.assertIn("\"name\":\"apply_patch\"", body)
        self.assertIn(json.dumps(patch_text, ensure_ascii=False)[1:-1], body)
        self.assertNotIn("\"type\":\"function_call\"", body)

    @patch("opencodex_proxy.app.stream_upstream")
    def test_chat_stream_is_written_to_db_after_completion(self, mock_stream):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
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
                ]
            }
        )
        mock_stream.return_value = iter(
            [
                'data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{"role":"assistant","content":"po"},"finish_reason":null}]}\n',
                "\n",
                'data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{"content":"ng"},"finish_reason":null}]}\n',
                "\n",
                'data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}\n',
                "\n",
                "data: [DONE]\n",
                "\n",
            ]
        )

        response = self.client.post(
            "/v1/responses", json={"model": "deepseek-v4-pro", "input": "ping", "stream": True}
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
        self.assertEqual(
            response_body["choices"][0]["message"]["content"],
            "pong",
        )

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

    @patch("opencodex_proxy.app.post_upstream")
    def test_admin_logs_can_filter_common_fields(self, mock_post):
        mock_post.return_value = {
            "id": "chatcmpl_1",
            "model": "upstream",
            "choices": [{"message": {"role": "assistant", "content": "pong"}}],
        }
        self.client.post("/v1/responses", json={"model": "visible-model", "input": "ping"})
        db_writer = self.app.config["OPENCODEX_DB_WRITER"]
        db_writer.stop()
        self.login()

        response = self.client.get(
            "/admin/api/logs?model=visible&channel_id=chat&path=/v1/responses&status_code=200&is_stream=0"
        )

        self.assertEqual(response.status_code, 200)
        events = response.get_json()["events"]
        self.assertEqual(len(events), 1)
        self.assertEqual(events[0]["model"], "visible-model")

    @patch("opencodex_proxy.app.post_upstream")
    def test_proxy_uses_first_enabled_channel(self, mock_post):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
            {
                "channels": [
                    {
                        "id": "disabled",
                        "type": "messages",
                        "baseurl": "https://disabled.example.test/v1",
                        "enabled": False,
                    },
                    {
                        "id": "chat",
                        "type": "chat",
                        "baseurl": "https://example.test/v1",
                    },
                ]
            }
        )
        mock_post.return_value = {
            "id": "chatcmpl_1",
            "model": "upstream",
            "choices": [{"message": {"role": "assistant", "content": "pong"}}],
        }

        response = self.client.post("/v1/responses", json={"model": "m", "input": "ping"})

        self.assertEqual(response.status_code, 200)
        upstream_channel = mock_post.call_args.args[0]
        self.assertEqual(upstream_channel["id"], "chat")

    def test_disabled_channel_returns_routing_error(self):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
            {
                "channels": [
                    {
                        "id": "chat",
                        "type": "chat",
                        "baseurl": "https://example.test/v1",
                        "enabled": False,
                    }
                ]
            }
        )

        response = self.client.post("/v1/responses", json={"model": "m", "input": "ping"})

        self.assertEqual(response.status_code, 400)
        self.assertIn("no enabled channels", response.get_json()["error"]["message"])


if __name__ == "__main__":
    unittest.main()
