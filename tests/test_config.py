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

    def test_compat_by_protocol_is_validated(self):
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
                                    "force_protocol": "messages",
                                    "by_protocol": {
                                        "messages": {"drop_params": "not-a-list"}
                                    },
                                },
                            }
                        ]
                    }
                )

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
                                    "by_protocol": {
                                        "messages": {
                                            "fallback_thinking_on_tool_use": "false",
                                        }
                                    },
                                },
                            }
                        ]
                    }
                )


if __name__ == "__main__":
    unittest.main()
