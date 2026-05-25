import os
import tempfile
import unittest
from pathlib import Path

from opencodex_proxy.errors import RoutingError
from opencodex_proxy.config import ConfigError, ConfigManager, expand_env
from opencodex_proxy.routing import choose_channel


class ConfigTests(unittest.TestCase):
    def test_expand_env(self):
        os.environ["OPEN_CODEX_TEST_KEY"] = "secret-value"
        self.assertEqual(expand_env({"apikey": "${OPEN_CODEX_TEST_KEY}"})["apikey"], "secret-value")

    def test_save_validates_before_overwrite(self):
        with tempfile.TemporaryDirectory() as tmp:
            db_path = Path(tmp) / "config.db"
            manager = ConfigManager(db_path)
            valid = {
                "channels": [
                    {
                        "id": "chat",
                        "type": "chat",
                        "baseurl": "https://example.test/v1",
                        "timeout_seconds": 30,
                    }
                ]
            }
            manager.save(valid)
            with self.assertRaises(ConfigError):
                manager.save({"channels": [{"id": "bad", "type": "chat"}]})
            self.assertEqual(manager.raw["channels"][0]["id"], "chat")

    def test_legacy_routing_is_removed_on_save(self):
        with tempfile.TemporaryDirectory() as tmp:
            db_path = Path(tmp) / "config.db"
            manager = ConfigManager(db_path)
            saved = manager.save(
                {
                    "channels": [
                        {
                            "id": "chat",
                            "type": "chat",
                            "baseurl": "https://example.test/v1",
                        }
                    ],
                    "routing": {"default_channel": "chat", "model_routes": []},
                }
            )

            self.assertNotIn("routing", saved)
            self.assertNotIn("routing", manager.raw)

    def test_legacy_protocol_override_compat_fields_are_removed_on_save(self):
        with tempfile.TemporaryDirectory() as tmp:
            db_path = Path(tmp) / "config.db"
            manager = ConfigManager(db_path)
            saved = manager.save(
                {
                    "channels": [
                        {
                            "id": "chat",
                            "type": "chat",
                            "baseurl": "https://example.test/v1",
                            "compat": {
                                "force_protocol": "messages",
                                "tool_request_protocol": "messages",
                                "drop_params": ["metadata"],
                                "by_protocol": {
                                    "messages": {
                                        "force_protocol": "chat",
                                        "tool_request_protocol": "responses",
                                        "drop_params": ["parallel_tool_calls"],
                                    }
                                },
                            },
                        }
                    ]
                }
            )

            compat = saved["channels"][0]["compat"]
            self.assertNotIn("force_protocol", compat)
            self.assertNotIn("tool_request_protocol", compat)
            self.assertNotIn("by_protocol", compat)
            self.assertEqual(compat["drop_params"], ["metadata"])

    def test_first_enabled_channel_is_used(self):
        config = {
            "channels": [
                {"id": "first", "type": "chat", "baseurl": "https://example.test/v1"},
                {"id": "second", "type": "messages", "baseurl": "https://example.test/v1"},
            ]
        }
        result = choose_channel(config, "mimo-local")
        self.assertEqual(result.channel["id"], "first")
        self.assertEqual(result.upstream_model, "mimo-local")

    def test_disabled_channels_are_skipped(self):
        config = {
            "channels": [
                {
                    "id": "disabled",
                    "type": "chat",
                    "baseurl": "https://example.test/v1",
                    "enabled": False,
                },
                {
                    "id": "enabled",
                    "type": "messages",
                    "baseurl": "https://example.test/v1",
                },
            ]
        }
        result = choose_channel(config, "gpt-4o")
        self.assertEqual(result.channel["id"], "enabled")

    def test_model_mapping_routes_and_rewrites_upstream_model(self):
        config = {
            "channels": [
                {
                    "id": "first",
                    "type": "chat",
                    "baseurl": "https://example.test/v1",
                    "models": [{"model": "gpt-5", "upstream_model": "gpt-4"}],
                }
            ]
        }
        result = choose_channel(config, "gpt-5")
        self.assertEqual(result.channel["id"], "first")
        self.assertEqual(result.original_model, "gpt-5")
        self.assertEqual(result.upstream_model, "gpt-4")

    def test_model_mapping_prefers_first_enabled_channel(self):
        config = {
            "channels": [
                {
                    "id": "first",
                    "type": "chat",
                    "baseurl": "https://example.test/v1",
                    "models": [{"model": "gpt-5", "upstream_model": "gpt-4"}],
                },
                {
                    "id": "second",
                    "type": "chat",
                    "baseurl": "https://second.example.test/v1",
                    "models": [{"model": "gpt-5", "upstream_model": "gpt-4o"}],
                },
            ]
        }
        result = choose_channel(config, "gpt-5")
        self.assertEqual(result.channel["id"], "first")
        self.assertEqual(result.upstream_model, "gpt-4")

    def test_model_mapping_requires_match_when_any_mapping_exists(self):
        config = {
            "channels": [
                {
                    "id": "first",
                    "type": "chat",
                    "baseurl": "https://example.test/v1",
                    "models": [{"model": "gpt-5", "upstream_model": "gpt-4"}],
                },
                {"id": "fallback", "type": "chat", "baseurl": "https://fallback.test/v1"},
            ]
        }
        with self.assertRaises(RoutingError):
            choose_channel(config, "gpt-4o")

    def test_model_mapping_string_array_is_normalized(self):
        with tempfile.TemporaryDirectory() as tmp:
            db_path = Path(tmp) / "config.db"
            manager = ConfigManager(db_path)
            saved = manager.save(
                {
                    "channels": [
                        {
                            "id": "chat",
                            "type": "chat",
                            "baseurl": "https://example.test/v1",
                            "models": ["gpt-4"],
                        }
                    ]
                }
            )
            self.assertEqual(
                saved["channels"][0]["models"],
                [{"model": "gpt-4", "upstream_model": "gpt-4"}],
            )

    def test_model_mapping_empty_upstream_defaults_to_model(self):
        with tempfile.TemporaryDirectory() as tmp:
            db_path = Path(tmp) / "config.db"
            manager = ConfigManager(db_path)
            saved = manager.save(
                {
                    "channels": [
                        {
                            "id": "chat",
                            "type": "chat",
                            "baseurl": "https://example.test/v1",
                            "models": [{"model": "gpt-5", "upstream_model": ""}],
                        }
                    ]
                }
            )
            self.assertEqual(saved["channels"][0]["models"][0]["upstream_model"], "gpt-5")

    def test_model_mapping_rejects_duplicate_downstream_model(self):
        with tempfile.TemporaryDirectory() as tmp:
            db_path = Path(tmp) / "config.db"
            manager = ConfigManager(db_path)
            with self.assertRaises(ConfigError):
                manager.save(
                    {
                        "channels": [
                            {
                                "id": "chat",
                                "type": "chat",
                                "baseurl": "https://example.test/v1",
                                "models": [
                                    {"model": "gpt-5", "upstream_model": "gpt-4"},
                                    {"model": "gpt-5", "upstream_model": "gpt-4o"},
                                ],
                            }
                        ]
                    }
                )

    def test_channel_enabled_must_be_boolean(self):
        with tempfile.TemporaryDirectory() as tmp:
            db_path = Path(tmp) / "config.db"
            manager = ConfigManager(db_path)
            with self.assertRaises(ConfigError):
                manager.save(
                    {
                        "channels": [
                            {
                                "id": "chat",
                                "type": "chat",
                                "baseurl": "https://example.test/v1",
                                "enabled": "false",
                            }
                        ]
                    }
                )

    def test_all_disabled_channels_are_not_routed(self):
        config = {
            "channels": [
                {
                    "id": "disabled",
                    "type": "chat",
                    "baseurl": "https://example.test/v1",
                    "enabled": False,
                },
            ]
        }
        with self.assertRaises(RoutingError):
            choose_channel(config, "mimo-local")

    def test_compat_fallback_thinking_flag_must_be_boolean(self):
        with tempfile.TemporaryDirectory() as tmp:
            db_path = Path(tmp) / "config.db"
            manager = ConfigManager(db_path)
            with self.assertRaises(ConfigError):
                manager.save(
                    {
                        "channels": [
                            {
                                "id": "chat",
                                "type": "chat",
                                "baseurl": "https://example.test/v1",
                                "compat": {
                                    "fallback_thinking_on_tool_use": "true",
                                },
                            }
                        ]
                    }
                )


if __name__ == "__main__":
    unittest.main()
