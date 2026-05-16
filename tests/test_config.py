import json
import os
import tempfile
import unittest
from pathlib import Path

from opencodex_proxy.config import ConfigError, ConfigManager, expand_env
from opencodex_proxy.routing import choose_channel


class ConfigTests(unittest.TestCase):
    def test_expand_env(self):
        os.environ["OPEN_CODEX_TEST_KEY"] = "secret-value"
        self.assertEqual(expand_env({"apikey": "${OPEN_CODEX_TEST_KEY}"})["apikey"], "secret-value")

    def test_save_validates_before_overwrite(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "config.json"
            manager = ConfigManager(path)
            valid = {
                "channels": [
                    {
                        "id": "chat",
                        "type": "chat",
                        "baseurl": "https://example.test/v1",
                        "timeout_seconds": 30,
                    }
                ],
                "routing": {"default_channel": "chat", "model_routes": []},
            }
            manager.save(valid)
            with self.assertRaises(ConfigError):
                manager.save({"channels": [{"id": "bad", "type": "chat"}]})
            self.assertEqual(json.loads(path.read_text())["channels"][0]["id"], "chat")

    def test_model_route_rewrites_model(self):
        config = {
            "channels": [
                {"id": "chat", "type": "chat", "baseurl": "https://example.test/v1"}
            ],
            "routing": {
                "default_channel": "chat",
                "model_routes": [
                    {
                        "pattern": "mimo-*",
                        "channel": "chat",
                        "upstream_model": "mimo-v2.5-pro",
                    }
                ],
            },
        }
        result = choose_channel(config, "mimo-local")
        self.assertEqual(result.channel["id"], "chat")
        self.assertEqual(result.upstream_model, "mimo-v2.5-pro")
        self.assertEqual(result.matched_pattern, "mimo-*")

    def test_compat_by_protocol_is_validated(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "config.json"
            manager = ConfigManager(path)
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
                        ],
                        "routing": {"default_channel": "chat", "model_routes": []},
                    }
                )


if __name__ == "__main__":
    unittest.main()
