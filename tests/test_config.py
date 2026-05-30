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
            self.assertEqual(manager.raw["channels"][0]["retry_count"], 3)
            with self.assertRaises(ConfigError):
                manager.save({"channels": [{"id": "bad", "type": "chat"}]})
            self.assertEqual(manager.raw["channels"][0]["id"], "chat")

    def test_retry_count_allows_zero_and_rejects_invalid_values(self):
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
                            "retry_count": 0,
                        }
                    ]
                }
            )
            self.assertEqual(saved["channels"][0]["retry_count"], 0)

            for retry_count in (-1, 1.5, "3", True):
                with self.subTest(retry_count=retry_count):
                    with self.assertRaises(ConfigError):
                        manager.save(
                            {
                                "channels": [
                                    {
                                        "id": "chat",
                                        "type": "chat",
                                        "baseurl": "https://example.test/v1",
                                        "retry_count": retry_count,
                                    }
                                ]
                            }
                        )

    def test_unknown_top_level_config_fields_are_rejected(self):
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
                            }
                        ],
                        "routing": {"default_channel": "chat", "model_routes": []},
                    }
                )

    def test_unknown_compat_fields_are_rejected(self):
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
                                    "drop_params": ["metadata"],
                                    "force_protocol": "messages",
                                },
                            }
                        ]
                    }
                )

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

    def test_model_mapping_string_array_is_rejected(self):
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
                                "models": ["gpt-4"],
                            }
                        ]
                    }
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

    def test_removed_auth_modes_are_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            db_path = Path(tmp) / "config.db"
            manager = ConfigManager(db_path)
            for auth_mode in ("pass_through_or_config", "pass_through"):
                with self.subTest(auth_mode=auth_mode):
                    with self.assertRaises(ConfigError):
                        manager.save(
                            {
                                "channels": [
                                    {
                                        "id": "chat",
                                        "type": "chat",
                                        "baseurl": "https://example.test/v1",
                                        "auth_mode": auth_mode,
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
