from __future__ import annotations

import json
import sqlite3
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from opencodex_proxy.app import create_app, _spa_index_response
from opencodex_proxy.db import (
    authenticate_access_api_key,
    authenticate_user,
    create_access_api_key,
    create_user,
    list_access_api_keys,
    read_channels,
    read_logs,
    read_stats,
    read_web_search_config,
    replace_web_search_config,
)
from opencodex_proxy.errors import UpstreamError
from opencodex_proxy.settings import Settings


def chat_text_response(content: str, *, model: str = "m", response_id: str = "chatcmpl_text"):
    return {
        "id": response_id,
        "model": model,
        "choices": [
            {
                "message": {"role": "assistant", "content": content},
                "finish_reason": "stop",
            }
        ],
        "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
    }


def chat_tool_response(
    *tool_calls: dict,
    model: str = "m",
    response_id: str = "chatcmpl_tool",
):
    return {
        "id": response_id,
        "model": model,
        "choices": [
            {
                "message": {
                    "role": "assistant",
                    "content": "",
                    "tool_calls": list(tool_calls),
                },
                "finish_reason": "tool_calls",
            }
        ],
        "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
    }


def function_tool_call(call_id: str, name: str, arguments: dict):
    return {
        "id": call_id,
        "type": "function",
        "function": {
            "name": name,
            "arguments": json.dumps(arguments, ensure_ascii=False),
        },
    }


def tavily_success(answer: str = "answer", url: str = "https://example.test/result"):
    return {
        "ok": True,
        "status_code": 200,
        "duration_ms": 12,
        "raw": {
            "answer": answer,
            "results": [
                {
                    "title": "Example Result",
                    "url": url,
                    "content": "source content",
                    "score": 0.9,
                }
            ],
            "usage": {"searches": 1},
        },
        "summary": {
            "answer": answer,
            "results": [
                {
                    "title": "Example Result",
                    "url": url,
                    "content": "source content",
                    "score": 0.9,
                }
            ],
            "error": None,
        },
    }


def tavily_http_error():
    return {
        "ok": False,
        "status_code": 429,
        "duration_ms": 7,
        "error_type": "http_error",
        "raw": {"error": "rate limited"},
        "summary": {"answer": "", "results": [], "error": "Tavily returned HTTP 429"},
    }


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
        self.raw_client = self.client
        self.access_key = create_access_api_key(self.db_path, "admin", "test")["key"]
        self.auth_headers = {"Authorization": f"Bearer {self.access_key}"}
        self._install_default_proxy_auth()

    def tearDown(self):
        db_writer = self.app.config.get("OPENCODEX_DB_WRITER")
        if db_writer:
            db_writer.stop()
        self.tmp.cleanup()

    def login(self):
        return self.client.post("/admin", data={"password": "pw"})

    def login_api(self, username: str = "admin", password: str = "pw"):
        return self.client.post(
            "/admin/api/login",
            json={"username": username, "password": password},
        )

    def _install_default_proxy_auth(self):
        original_open = self.client.open
        self._client_open_without_default_proxy_auth = original_open

        def authed_open(*args, **kwargs):
            path = ""
            if args:
                path = str(args[0])
            if not path:
                path = str(kwargs.get("path") or kwargs.get("full_path") or "")
            if path.startswith("/v1"):
                headers = dict(kwargs.get("headers") or {})
                headers.setdefault("Authorization", self.auth_headers["Authorization"])
                kwargs["headers"] = headers
            return original_open(*args, **kwargs)

        self.client.open = authed_open

    def enable_web_search(self, keys=None, enabled=True, key_usage_limit=None):
        config = {
            "enabled": enabled,
            "keys": keys if keys is not None else [{"key": "tvly-test", "enabled": True}],
        }
        if key_usage_limit is not None:
            config["key_usage_limit"] = key_usage_limit
        return replace_web_search_config(self.db_path, config)

    def parse_sse_events(self, body: str) -> list[tuple[str, dict]]:
        events = []
        for chunk in body.strip().split("\n\n"):
            event_name = ""
            data = None
            for line in chunk.splitlines():
                if line.startswith("event:"):
                    event_name = line.split(":", 1)[1].strip()
                if line.startswith("data:"):
                    data = json.loads(line.split(":", 1)[1].strip())
            if event_name and isinstance(data, dict):
                events.append((event_name, data))
        return events

    def test_admin_requires_login(self):
        response = self.client.get("/admin/api/config")
        self.assertEqual(response.status_code, 401)

    def test_admin_api_login_and_session(self):
        response = self.client.get("/admin/api/session")
        self.assertEqual(response.status_code, 200)
        self.assertFalse(response.get_json()["authenticated"])

        response = self.login_api()
        self.assertEqual(response.status_code, 200)
        self.assertTrue(response.get_json()["authenticated"])
        self.assertEqual(
            response.get_json()["user"],
            {"username": "admin", "role": "superadmin", "enabled": True},
        )

        response = self.client.get("/admin/api/session")
        self.assertTrue(response.get_json()["authenticated"])
        self.assertEqual(response.get_json()["user"]["username"], "admin")

        response = self.client.post("/admin/api/logout")
        self.assertEqual(response.status_code, 200)
        self.assertFalse(response.get_json()["authenticated"])

    def test_regular_user_login_and_superadmin_apis_are_forbidden(self):
        create_user(self.db_path, "alice", "alice-pw")

        response = self.login_api("alice", "alice-pw")

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.get_json()["user"]["role"], "user")
        self.assertEqual(self.client.get("/admin/api/users").status_code, 403)
        self.assertEqual(self.client.get("/admin/api/web-search").status_code, 403)
        self.assertEqual(
            self.client.post("/admin/api/web-search", json={"enabled": True}).status_code,
            403,
        )

    def test_regular_user_cannot_delete_users(self):
        create_user(self.db_path, "alice", "alice-pw")
        create_user(self.db_path, "bob", "bob-pw")
        self.login_api("alice", "alice-pw")

        response = self.client.delete("/admin/api/users/bob")

        self.assertEqual(response.status_code, 403)
        self.assertIsNotNone(authenticate_user(self.db_path, "bob", "bob-pw"))

    def test_superadmin_cannot_delete_current_user(self):
        self.login_api()

        response = self.client.delete("/admin/api/users/admin")

        self.assertEqual(response.status_code, 400)
        self.assertEqual(response.get_json()["error"], "cannot delete current user")
        self.assertIsNotNone(authenticate_user(self.db_path, "admin", "pw"))

    def test_superadmin_can_delete_other_users_including_superadmins(self):
        create_user(self.db_path, "alice", "alice-pw")
        create_user(self.db_path, "ops", "ops-pw", role="superadmin")
        alice_key = create_access_api_key(self.db_path, "alice", "Laptop")["key"]
        ops_key = create_access_api_key(self.db_path, "ops", "Ops")["key"]
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
            {
                "channels": [
                    {
                        "id": "chat",
                        "type": "chat",
                        "baseurl": "https://alice.example.test/v1",
                        "apikey": "alice-key",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                    }
                ]
            },
            owner_username="alice",
        )
        manager.save(
            {
                "channels": [
                    {
                        "id": "messages",
                        "type": "messages",
                        "baseurl": "https://ops.example.test/v1",
                        "apikey": "ops-key",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                    }
                ]
            },
            owner_username="ops",
        )
        self.login_api()

        alice_response = self.client.delete("/admin/api/users/alice")
        ops_response = self.client.delete("/admin/api/users/ops")

        self.assertEqual(alice_response.status_code, 200)
        self.assertTrue(alice_response.get_json()["deleted"])
        self.assertEqual(alice_response.get_json()["user"]["username"], "alice")
        self.assertEqual(ops_response.status_code, 200)
        self.assertEqual(ops_response.get_json()["user"]["role"], "superadmin")
        self.assertIsNone(authenticate_user(self.db_path, "alice", "alice-pw"))
        self.assertIsNone(authenticate_user(self.db_path, "ops", "ops-pw"))
        self.assertIsNone(authenticate_access_api_key(self.db_path, alice_key))
        self.assertIsNone(authenticate_access_api_key(self.db_path, ops_key))
        self.assertEqual(list_access_api_keys(self.db_path, "alice"), [])
        self.assertEqual(list_access_api_keys(self.db_path, "ops"), [])
        self.assertEqual(read_channels(self.db_path, owner_username="alice"), [])
        self.assertEqual(read_channels(self.db_path, owner_username="ops"), [])
        self.assertEqual(manager.raw_for_user("alice")["channels"], [])
        self.assertEqual(manager.raw_for_user("ops")["channels"], [])

    def test_regular_user_api_key_management_returns_plaintext_for_copying(self):
        create_user(self.db_path, "alice", "alice-pw")
        self.login_api("alice", "alice-pw")

        response = self.client.post("/admin/api/api-keys", json={"name": "Laptop"})

        self.assertEqual(response.status_code, 201)
        created = response.get_json()["key"]
        self.assertEqual(created["owner_username"], "alice")
        self.assertTrue(created["key"].startswith("ocx_"))
        self.assertEqual(authenticate_access_api_key(self.db_path, created["key"])["user"]["username"], "alice")

        response = self.client.get("/admin/api/api-keys")

        self.assertEqual(response.status_code, 200)
        listed = response.get_json()["keys"]
        self.assertEqual(len(listed), 1)
        self.assertEqual(listed[0]["masked_key"], created["masked_key"])
        self.assertEqual(listed[0]["key"], created["key"])
        self.assertEqual(list_access_api_keys(self.db_path, "alice")[0]["key"], created["key"])

    def test_regular_user_cannot_manage_other_users_api_keys(self):
        create_user(self.db_path, "alice", "alice-pw")
        create_user(self.db_path, "bob", "bob-pw")
        bob_key = create_access_api_key(self.db_path, "bob", "bob")
        self.login_api("alice", "alice-pw")

        response = self.client.get("/admin/api/api-keys?owner_username=bob")
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.get_json()["keys"], [])

        self.assertEqual(
            self.client.patch(
                f"/admin/api/api-keys/{bob_key['id']}",
                json={"enabled": False},
            ).status_code,
            404,
        )
        self.assertEqual(
            self.client.delete(f"/admin/api/api-keys/{bob_key['id']}").status_code,
            404,
        )

    def test_spa_index_response_serves_built_index(self):
        admin_static_dir = self.root / "static" / "admin"
        admin_static_dir.mkdir(parents=True)
        (admin_static_dir / "index.html").write_text(
            '<!doctype html><div id="app"></div>',
            encoding="utf-8",
        )

        response = _spa_index_response(admin_static_dir)

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.mimetype, "text/html")
        self.assertIn('id="app"', response.get_data(as_text=True))

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
        self.assertEqual(manager.expanded["channels"][0]["auth_mode"], "config")

    def test_admin_export_config_includes_full_apikey(self):
        self.login()
        self.enable_web_search(keys=[{"key": "tvly-secret", "enabled": True}])

        response = self.client.get("/admin/api/config/export")

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.mimetype, "application/json")
        self.assertIn(
            "opencodex-channels-config.json",
            response.headers.get("Content-Disposition", ""),
        )
        exported = json.loads(response.get_data(as_text=True))
        self.assertEqual(sorted(exported), ["channels"])
        self.assertEqual(exported["channels"][0]["id"], "chat")
        self.assertEqual(exported["channels"][0]["apikey"], "secret")
        self.assertNotIn("web_search", exported)
        self.assertNotIn("tavily_keys", exported)

    def test_admin_can_get_and_save_web_search_config(self):
        self.login()

        response = self.client.get("/admin/api/web-search")

        self.assertEqual(response.status_code, 200)
        initial = response.get_json()
        self.assertFalse(initial["enabled"])
        self.assertEqual(initial["providers"], ["tavily"])
        self.assertEqual(initial["default_key_usage_limit"], 1000)
        self.assertNotIn("key_usage_limit", initial)
        self.assertEqual(initial["keys"], [])

        response = self.client.post(
            "/admin/api/web-search",
            json={
                "enabled": True,
                "keys": [
                    {"provider": "tavily", "key": "tvly-a", "enabled": True, "usage_count": 3, "usage_limit": 250},
                    {"provider": "tavily", "key": "tvly-b", "enabled": False, "usage_count": 8, "usage_limit": 500},
                ],
            },
        )

        self.assertEqual(response.status_code, 200)
        saved = response.get_json()
        self.assertTrue(saved["enabled"])
        self.assertNotIn("key_usage_limit", saved)
        self.assertEqual(saved["keys"][0]["provider"], "tavily")
        self.assertEqual(saved["keys"][0]["usage_limit"], 250)
        self.assertEqual(saved["keys"][0]["key_usage_limit"], 250)
        self.assertEqual([item["key"] for item in saved["keys"]], ["tvly-a", "tvly-b"])
        self.assertEqual([item["usage_limit"] for item in saved["keys"]], [250, 500])
        self.assertEqual([item["usage_count"] for item in saved["keys"]], [3, 8])
        stored = read_web_search_config(self.db_path)
        self.assertNotIn("key_usage_limit", stored)
        self.assertEqual([item["key"] for item in stored["keys"]], ["tvly-a", "tvly-b"])
        self.assertEqual([item["usage_count"] for item in stored["keys"]], [3, 8])

    @patch("opencodex_proxy.app.tavily_search")
    def test_admin_web_search_test_key_allows_disabled_key_and_counts_usage(
        self, mock_tavily
    ):
        self.login()
        saved = self.enable_web_search(keys=[{"key": "tvly-disabled", "enabled": False}])
        key_id = saved["keys"][0]["id"]
        mock_tavily.return_value = tavily_success("ok")

        response = self.client.post(
            "/admin/api/web-search/test-key",
            json={"id": key_id, "query": "OpenAI"},
        )

        self.assertEqual(response.status_code, 200)
        data = response.get_json()
        self.assertTrue(data["ok"])
        self.assertEqual(data["key"]["provider"], "tavily")
        self.assertEqual(data["key"]["usage_count"], 1)
        self.assertEqual(data["key"]["usage_limit"], 1000)
        self.assertEqual(data["config"]["keys"][0]["usage_count"], 1)
        self.assertFalse(data["config"]["keys"][0]["enabled"])
        self.assertEqual(mock_tavily.call_args.args, ("tvly-disabled", "OpenAI"))

    @patch("opencodex_proxy.app.tavily_search")
    def test_admin_web_search_test_key_uses_configured_usage_limit_message(
        self, mock_tavily
    ):
        self.login()
        saved = self.enable_web_search(
            keys=[{"key": "tvly-limited", "enabled": True, "usage_limit": 1}],
        )
        key_id = saved["keys"][0]["id"]
        mock_tavily.return_value = tavily_success("ok")

        first = self.client.post(
            "/admin/api/web-search/test-key",
            json={"id": key_id, "query": "OpenAI"},
        )
        second = self.client.post(
            "/admin/api/web-search/test-key",
            json={"id": key_id, "query": "OpenAI"},
        )

        self.assertEqual(first.status_code, 200)
        self.assertEqual(second.status_code, 400)
        self.assertIn("usage limit", second.get_json()["error"])

    def test_admin_import_config_appends_without_overwriting_existing_ids(self):
        self.login()
        imported = {
            "channels": [
                {
                    "id": "chat",
                    "type": "chat",
                    "baseurl": "https://duplicate.example.test/v1",
                    "apikey": "changed",
                    "auth_mode": "config",
                    "timeout_seconds": 30,
                },
                {
                    "id": "messages",
                    "type": "messages",
                    "baseurl": "https://messages.example.test/v1",
                    "apikey": "new-secret",
                    "auth_mode": "config",
                    "timeout_seconds": 45,
                },
            ]
        }

        response = self.client.post("/admin/api/config/import", json=imported)

        self.assertEqual(response.status_code, 200)
        result = response.get_json()
        self.assertEqual(result["imported"], 1)
        self.assertEqual(result["skipped"], 1)
        self.assertEqual(result["skipped_ids"], ["chat"])
        channels = read_channels(self.db_path)
        self.assertEqual([channel["id"] for channel in channels], ["chat", "messages"])
        self.assertEqual(channels[0]["apikey"], "secret")
        self.assertEqual(channels[1]["apikey"], "new-secret")
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        self.assertEqual([channel["id"] for channel in manager.raw["channels"]], ["chat", "messages"])

    def test_admin_import_rejects_invalid_config_without_changes(self):
        self.login()

        response = self.client.post(
            "/admin/api/config/import",
            json={"channels": [{"id": "bad", "type": "chat"}]},
        )

        self.assertEqual(response.status_code, 400)
        channels = read_channels(self.db_path)
        self.assertEqual([channel["id"] for channel in channels], ["chat"])

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

    @patch("opencodex_proxy.app.list_upstream_models")
    def test_admin_can_discover_models(self, mock_models):
        self.login()
        mock_models.return_value = {
            "object": "list",
            "data": [
                {"id": "gpt-4"},
                {"id": "gpt-4"},
                {"id": "gpt-4o"},
                {"object": "model"},
            ],
        }
        response = self.client.post(
            "/admin/api/channels/discover-models",
            json={
                "channel": {
                    "id": "chat",
                    "type": "chat",
                    "baseurl": "https://example.test/v1",
                    "apikey": "secret",
                    "auth_mode": "config",
                    "timeout_seconds": 30,
                }
            },
        )

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.get_json()["models"], ["gpt-4", "gpt-4o"])
        upstream_channel = mock_models.call_args.args[0]
        self.assertEqual(upstream_channel["id"], "chat")

    @patch("opencodex_proxy.app.post_upstream")
    def test_admin_channel_test_rewrites_model_mapping(self, mock_post):
        self.login()
        mock_post.return_value = {
            "id": "chatcmpl_1",
            "model": "gpt-4",
            "choices": [{"message": {"role": "assistant", "content": "pong"}}],
        }
        response = self.client.post(
            "/admin/api/channels/test",
            json={
                "channel": {
                    "id": "chat",
                    "type": "chat",
                    "baseurl": "https://example.test/v1",
                    "apikey": "secret",
                    "auth_mode": "config",
                    "timeout_seconds": 30,
                    "models": [{"model": "gpt-5", "upstream_model": "gpt-4"}],
                },
                "payload": {
                    "model": "gpt-5",
                    "messages": [{"role": "user", "content": "ping"}],
                },
            },
        )

        self.assertEqual(response.status_code, 200)
        data = response.get_json()
        self.assertTrue(data["ok"])
        self.assertEqual(data["model"], "gpt-5")
        self.assertEqual(data["upstream_model"], "gpt-4")
        self.assertEqual(data["response"]["model"], "gpt-5")
        upstream_payload = mock_post.call_args.args[1]
        self.assertEqual(upstream_payload["model"], "gpt-4")

    @patch("opencodex_proxy.app.post_upstream")
    def test_admin_channel_test_returns_upstream_error_body(self, mock_post):
        self.login()
        mock_post.side_effect = UpstreamError(
            "upstream returned HTTP 400",
            status_code=400,
            body={"error": "bad model"},
            channel_id="chat",
        )
        response = self.client.post(
            "/admin/api/channels/test",
            json={
                "channel": {
                    "id": "chat",
                    "type": "chat",
                    "baseurl": "https://example.test/v1",
                    "timeout_seconds": 30,
                },
                "payload": {
                    "model": "gpt-5",
                    "messages": [{"role": "user", "content": "ping"}],
                },
            },
        )

        self.assertEqual(response.status_code, 200)
        data = response.get_json()
        self.assertFalse(data["ok"])
        self.assertEqual(data["status_code"], 400)
        self.assertEqual(data["body"], {"error": "bad model"})

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

    @patch("opencodex_proxy.app.tavily_search")
    @patch("opencodex_proxy.app.post_upstream")
    def test_responses_web_search_simulation_runs_only_after_model_tool_call(
        self, mock_post, mock_tavily
    ):
        self.enable_web_search()
        mock_tavily.return_value = tavily_success("OpenAI was founded in 2015.")
        mock_post.side_effect = [
            chat_tool_response(function_tool_call("call_web", "web_search", {"query": "OpenAI"})),
            chat_text_response("OpenAI was founded in 2015."),
        ]

        response = self.client.post(
            "/v1/responses",
            json={"model": "m", "input": "search", "tools": [{"type": "web_search"}]},
        )

        self.assertEqual(response.status_code, 200)
        payload = response.get_json()
        self.assertEqual([item["type"] for item in payload["output"]], ["web_search_call", "message"])
        self.assertEqual(payload["output"][0]["action"], {"type": "search", "query": "OpenAI"})
        message_text = payload["output"][1]["content"][0]["text"]
        self.assertIn("OpenAI was founded in 2015.", message_text)
        self.assertIn("来源:", message_text)
        self.assertEqual(payload["output"][1]["content"][0]["annotations"][0]["type"], "url_citation")
        self.assertEqual(mock_tavily.call_args.args, ("tvly-test", "OpenAI"))
        self.assertEqual(mock_post.call_count, 2)
        second_upstream_payload = mock_post.call_args_list[1].args[1]
        self.assertEqual(second_upstream_payload["messages"][-1]["role"], "tool")
        self.assertIn("OpenAI was founded", second_upstream_payload["messages"][-1]["content"])

        db_writer = self.app.config["OPENCODEX_DB_WRITER"]
        db_writer.stop()
        logs = read_logs(self.db_path)
        web_log = json.loads(logs[0]["web_search_json"])
        self.assertEqual(web_log["calls"][0]["query"], "OpenAI")
        self.assertEqual(web_log["calls"][0]["key_position"], 0)
        self.assertEqual(web_log["calls"][0]["key_usage_count"], 1)
        self.assertEqual(read_web_search_config(self.db_path)["keys"][0]["usage_count"], 1)

    @patch("opencodex_proxy.app.tavily_search")
    @patch("opencodex_proxy.app.post_upstream")
    def test_responses_web_search_declared_but_model_does_not_call_tool_skips_tavily(
        self, mock_post, mock_tavily
    ):
        self.enable_web_search()
        mock_post.return_value = chat_text_response("no search needed")

        response = self.client.post(
            "/v1/responses",
            json={"model": "m", "input": "hello", "tools": [{"type": "web_search"}]},
        )

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.get_json()["output"][0]["content"][0]["text"], "no search needed")
        mock_tavily.assert_not_called()
        self.assertEqual(mock_post.call_count, 1)

    @patch("opencodex_proxy.app.tavily_search")
    @patch("opencodex_proxy.app.post_upstream")
    def test_responses_web_search_disabled_returns_model_tool_call_without_tavily(
        self, mock_post, mock_tavily
    ):
        self.enable_web_search(enabled=False)
        mock_post.return_value = chat_tool_response(
            function_tool_call("call_web", "web_search", {"query": "OpenAI"})
        )

        response = self.client.post(
            "/v1/responses",
            json={"model": "m", "input": "search", "tools": [{"type": "web_search"}]},
        )

        self.assertEqual(response.status_code, 200)
        item = response.get_json()["output"][0]
        self.assertEqual(item["type"], "function_call")
        self.assertEqual(item["name"], "web_search")
        mock_tavily.assert_not_called()

    @patch("opencodex_proxy.app.post_upstream")
    def test_responses_web_search_no_key_is_fed_back_as_tool_result(self, mock_post):
        self.enable_web_search(keys=[])
        mock_post.side_effect = [
            chat_tool_response(function_tool_call("call_web", "web_search", {"query": "OpenAI"})),
            chat_text_response("search was unavailable"),
        ]

        response = self.client.post(
            "/v1/responses",
            json={"model": "m", "input": "search", "tools": [{"type": "web_search"}]},
        )

        self.assertEqual(response.status_code, 200)
        payload = response.get_json()
        self.assertEqual(payload["output"][0]["type"], "web_search_call")
        tool_message = mock_post.call_args_list[1].args[1]["messages"][-1]
        self.assertEqual(json.loads(tool_message["content"])["error"], "搜索不可用")

    @patch("opencodex_proxy.app.tavily_search")
    @patch("opencodex_proxy.app.post_upstream")
    def test_responses_web_search_tavily_failure_is_logged_and_fed_back(
        self, mock_post, mock_tavily
    ):
        self.enable_web_search()
        mock_tavily.return_value = tavily_http_error()
        mock_post.side_effect = [
            chat_tool_response(function_tool_call("call_web", "web_search", {"query": "OpenAI"})),
            chat_text_response("I could not search."),
        ]

        response = self.client.post(
            "/v1/responses",
            json={"model": "m", "input": "search", "tools": [{"type": "web_search"}]},
        )

        self.assertEqual(response.status_code, 200)
        tool_message = mock_post.call_args_list[1].args[1]["messages"][-1]
        self.assertEqual(json.loads(tool_message["content"])["error"], "搜索不可用")
        db_writer = self.app.config["OPENCODEX_DB_WRITER"]
        db_writer.stop()
        web_log = json.loads(read_logs(self.db_path)[0]["web_search_json"])
        self.assertEqual(web_log["calls"][0]["error_type"], "http_error")
        self.assertEqual(web_log["calls"][0]["http_status"], 429)
        self.assertEqual(web_log["calls"][0]["key_position"], 0)
        self.assertEqual(web_log["upstream_call_summary"][0]["tool_names"], ["web_search"])

    @patch("opencodex_proxy.app.tavily_search")
    @patch("opencodex_proxy.app.post_upstream")
    def test_responses_web_search_final_upstream_error_keeps_tavily_log(
        self, mock_post, mock_tavily
    ):
        self.enable_web_search()
        mock_tavily.return_value = tavily_success("OpenAI was founded in 2015.")
        mock_post.side_effect = [
            chat_tool_response(function_tool_call("call_web", "web_search", {"query": "OpenAI"})),
            UpstreamError(
                "upstream returned HTTP 502",
                status_code=502,
                body={"error": "bad gateway"},
                channel_id="chat",
            ),
        ]

        response = self.client.post(
            "/v1/responses",
            json={"model": "m", "input": "search", "tools": [{"type": "web_search"}]},
        )

        self.assertEqual(response.status_code, 502)
        payload = response.get_json()
        self.assertEqual(payload["error"]["type"], "upstream_error")
        self.assertEqual(payload["error"]["upstream"], {"error": "bad gateway"})
        db_writer = self.app.config["OPENCODEX_DB_WRITER"]
        db_writer.stop()
        web_log = json.loads(read_logs(self.db_path)[0]["web_search_json"])
        self.assertEqual(web_log["calls"][0]["query"], "OpenAI")
        self.assertEqual(web_log["calls"][0]["status"], "completed")
        self.assertEqual(web_log["calls"][0]["key_usage_count"], 1)
        self.assertEqual(web_log["upstream_error"], "upstream returned HTTP 502")
        self.assertEqual(web_log["upstream_call_summary"][0]["tool_names"], ["web_search"])

    @patch("opencodex_proxy.app.tavily_search")
    @patch("opencodex_proxy.app.post_upstream")
    def test_responses_web_search_mixed_tool_calls_return_client_visible_placeholder(
        self, mock_post, mock_tavily
    ):
        self.enable_web_search()
        mock_tavily.return_value = tavily_success()
        mock_post.return_value = chat_tool_response(
            function_tool_call("call_web", "web_search", {"query": "OpenAI"}),
            function_tool_call("call_lookup", "lookup", {"id": "1"}),
        )

        response = self.client.post(
            "/v1/responses",
            json={
                "model": "m",
                "input": "search and lookup",
                "tools": [
                    {"type": "web_search"},
                    {"type": "function", "name": "lookup", "parameters": {"type": "object"}},
                ],
            },
        )

        self.assertEqual(response.status_code, 200)
        output = response.get_json()["output"]
        self.assertEqual(output[0]["type"], "web_search_call")
        self.assertIn("opencodex_result", output[0])
        self.assertEqual(output[1]["type"], "function_call")
        self.assertEqual(output[1]["name"], "lookup")
        self.assertEqual(mock_post.call_count, 1)

    @patch("opencodex_proxy.app.tavily_search")
    @patch("opencodex_proxy.app.post_upstream")
    @patch("opencodex_proxy.app.stream_upstream")
    def test_responses_web_search_stream_declared_but_model_does_not_call_tool_uses_normal_stream(
        self, mock_stream, mock_post, mock_tavily
    ):
        self.enable_web_search()
        mock_stream.return_value = iter(
            [
                'data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"po"},"finish_reason":null}]}\n',
                "\n",
                'data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"ng"},"finish_reason":null}]}\n',
                "\n",
                'data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}\n',
                "\n",
                "data: [DONE]\n",
                "\n",
            ]
        )

        response = self.client.post(
            "/v1/responses",
            json={
                "model": "m",
                "input": "hello",
                "tools": [{"type": "web_search"}],
                "stream": True,
            },
        )

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.mimetype, "text/event-stream")
        body = response.get_data(as_text=True)
        self.assertIn("pong", body)
        self.assertIn("event: response.completed", body)
        self.assertNotIn("\"type\":\"web_search_call\"", body)
        mock_stream.assert_called_once()
        self.assertIs(mock_stream.call_args.args[1]["stream"], True)
        mock_post.assert_not_called()
        mock_tavily.assert_not_called()

        db_writer = self.app.config["OPENCODEX_DB_WRITER"]
        db_writer.stop()
        logs = read_logs(self.db_path)
        self.assertEqual(len(logs), 1)
        log = logs[0]
        self.assertEqual(log["status_code"], 200)
        self.assertEqual(log["is_stream"], 1)
        self.assertEqual(log["input_tokens"], 3)
        self.assertEqual(log["output_tokens"], 2)
        self.assertIsNone(log["web_search_json"])
        self.assertIsNotNone(log["response_body"])

    @patch("opencodex_proxy.app.tavily_search")
    @patch("opencodex_proxy.app.post_upstream")
    @patch("opencodex_proxy.app.stream_upstream")
    def test_responses_web_search_stream_preserves_prior_output_items(
        self, mock_stream, mock_post, mock_tavily
    ):
        self.enable_web_search()
        mock_tavily.return_value = tavily_success()
        mock_stream.side_effect = [
            iter(
                [
                    'data: {"id":"chatcmpl_tool","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"checking first"},"finish_reason":null}]}\n',
                    "\n",
                    'data: {"id":"chatcmpl_tool","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_web","type":"function","function":{"name":"web_search","arguments":"{\\"query\\":\\"OpenAI\\"}"}}]},"finish_reason":null}]}\n',
                    "\n",
                    'data: {"id":"chatcmpl_tool","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}\n',
                    "\n",
                    "data: [DONE]\n",
                    "\n",
                ]
            ),
            iter(
                [
                    'data: {"id":"chatcmpl_answer","object":"chat.completion.chunk","created":2,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"final answer"},"finish_reason":null}]}\n',
                    "\n",
                    'data: {"id":"chatcmpl_answer","object":"chat.completion.chunk","created":2,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":4,"completion_tokens":2,"total_tokens":6}}\n',
                    "\n",
                    "data: [DONE]\n",
                    "\n",
                ]
            ),
        ]
        mock_post.return_value = chat_text_response("final answer")

        response = self.client.post(
            "/v1/responses",
            json={
                "model": "m",
                "input": "search",
                "tools": [{"type": "web_search"}],
                "stream": True,
            },
        )

        self.assertEqual(response.status_code, 200)
        events = self.parse_sse_events(response.get_data(as_text=True))
        completed = next(
            data
            for event, data in events
            if event == "response.completed"
        )
        output = completed["response"]["output"]
        self.assertEqual([item["type"] for item in output], ["message", "web_search_call", "message"])
        self.assertEqual(output[0]["content"][0]["text"], "checking first")
        done_indexes = [
            data["output_index"]
            for event, data in events
            if event == "response.output_item.done"
        ]
        self.assertEqual(done_indexes, [0, 1, 2])

    @patch("opencodex_proxy.app.tavily_search")
    @patch("opencodex_proxy.app.post_upstream")
    @patch("opencodex_proxy.app.stream_upstream")
    def test_responses_web_search_stream_is_synthesized_with_web_search_call(
        self, mock_stream, mock_post, mock_tavily
    ):
        self.enable_web_search()
        mock_tavily.return_value = tavily_success()
        mock_stream.side_effect = [
            iter(
                [
                    'data: {"id":"chatcmpl_tool","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_web","type":"function","function":{"name":"web_search","arguments":"{\\"query\\":\\"Open"}}]},"finish_reason":null}]}\n',
                    "\n",
                    'data: {"id":"chatcmpl_tool","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"AI\\"}"}}]},"finish_reason":null}]}\n',
                    "\n",
                    'data: {"id":"chatcmpl_tool","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}\n',
                    "\n",
                    "data: [DONE]\n",
                    "\n",
                ]
            ),
            iter(
                [
                    'data: {"id":"chatcmpl_answer","object":"chat.completion.chunk","created":2,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"answer"},"finish_reason":null}]}\n',
                    "\n",
                    'data: {"id":"chatcmpl_answer","object":"chat.completion.chunk","created":2,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":4,"completion_tokens":2,"total_tokens":6}}\n',
                    "\n",
                    "data: [DONE]\n",
                    "\n",
                ]
            ),
        ]
        mock_post.return_value = chat_text_response("answer")

        response = self.client.post(
            "/v1/responses",
            json={
                "model": "m",
                "input": "search",
                "tools": [{"type": "web_search"}],
                "stream": True,
            },
        )

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.mimetype, "text/event-stream")
        body = response.get_data(as_text=True)
        self.assertIn("event: response.output_item.added", body)
        self.assertIn("\"type\":\"web_search_call\"", body)
        events = self.parse_sse_events(body)
        self.assertEqual(
            sum(1 for event, _ in events if event == "response.created"),
            1,
        )
        sequence_numbers = [
            data["sequence_number"]
            for _, data in events
            if isinstance(data.get("sequence_number"), int)
        ]
        self.assertEqual(sequence_numbers, sorted(sequence_numbers))
        self.assertEqual(len(sequence_numbers), len(set(sequence_numbers)))
        done_indexes = [
            data["output_index"]
            for event, data in events
            if event == "response.output_item.done"
        ]
        self.assertEqual(done_indexes, sorted(set(done_indexes)))
        completed = next(
            data
            for event, data in events
            if event == "response.completed"
        )
        self.assertEqual(completed["response"]["output"][0]["type"], "web_search_call")
        self.assertFalse(
            any(
                item.get("type") == "function_call" and item.get("name") == "web_search"
                for item in completed["response"]["output"]
            )
        )
        self.assertEqual(completed["response"]["usage"]["input_tokens"], 6)
        self.assertEqual(completed["response"]["usage"]["output_tokens"], 5)
        self.assertEqual(completed["response"]["usage"]["total_tokens"], 11)
        self.assertEqual(mock_stream.call_count, 2)
        self.assertEqual(mock_post.call_count, 0)
        self.assertEqual(mock_tavily.call_args.args, ("tvly-test", "OpenAI"))

        db_writer = self.app.config["OPENCODEX_DB_WRITER"]
        db_writer.stop()
        logs = read_logs(self.db_path)
        self.assertEqual(len(logs), 1)
        log = logs[0]
        self.assertEqual(log["input_tokens"], 6)
        self.assertEqual(log["output_tokens"], 5)
        self.assertIsNotNone(log["response_body"])
        web_log = json.loads(log["web_search_json"])
        self.assertEqual(web_log["calls"][0]["query"], "OpenAI")

    @patch("opencodex_proxy.app.tavily_search")
    @patch("opencodex_proxy.app.post_upstream")
    @patch("opencodex_proxy.app.stream_upstream")
    def test_responses_web_search_stream_continues_when_stream_requests_more_search(
        self, mock_stream, mock_post, mock_tavily
    ):
        self.enable_web_search()
        mock_tavily.side_effect = [
            tavily_success("first search"),
            tavily_success("second search"),
        ]
        mock_stream.side_effect = [
            iter(
                [
                    'data: {"id":"chatcmpl_tool","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_web_1","type":"function","function":{"name":"web_search","arguments":"{\\"query\\":\\"OpenAI\\"}"}}]},"finish_reason":null}]}\n',
                    "\n",
                    'data: {"id":"chatcmpl_tool","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}\n',
                    "\n",
                    "data: [DONE]\n",
                    "\n",
                ]
            ),
            iter(
                [
                    'data: {"id":"chatcmpl_more_tool","object":"chat.completion.chunk","created":2,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_web_2","type":"function","function":{"name":"web_search","arguments":"{\\"query\\":\\"OpenAI May\\"}"}}]},"finish_reason":null}]}\n',
                    "\n",
                    'data: {"id":"chatcmpl_more_tool","object":"chat.completion.chunk","created":2,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":4,"completion_tokens":2,"total_tokens":6}}\n',
                    "\n",
                    "data: [DONE]\n",
                    "\n",
                ]
            ),
            iter(
                [
                    'data: {"id":"chatcmpl_answer","object":"chat.completion.chunk","created":3,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"final answer"},"finish_reason":null}]}\n',
                    "\n",
                    'data: {"id":"chatcmpl_answer","object":"chat.completion.chunk","created":3,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":6,"completion_tokens":2,"total_tokens":8}}\n',
                    "\n",
                    "data: [DONE]\n",
                    "\n",
                ]
            ),
        ]
        mock_post.side_effect = [
            chat_text_response("probe answer"),
            chat_text_response("probe final"),
        ]

        response = self.client.post(
            "/v1/responses",
            json={
                "model": "m",
                "input": "search",
                "tools": [{"type": "web_search"}],
                "stream": True,
            },
        )

        self.assertEqual(response.status_code, 200)
        body = response.get_data(as_text=True)
        self.assertIn("final answer", body)
        events = self.parse_sse_events(body)
        completed = next(
            data
            for event, data in events
            if event == "response.completed"
        )
        self.assertEqual(
            [item["type"] for item in completed["response"]["output"]],
            ["web_search_call", "web_search_call", "message"],
        )
        self.assertEqual(mock_tavily.call_count, 2)
        self.assertEqual(
            [call.args[1] for call in mock_tavily.call_args_list],
            ["OpenAI", "OpenAI May"],
        )
        mock_post.assert_not_called()

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
    def test_responses_chat_apply_patch_tool_calls_fall_back_to_exec_command(
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
        self.assertEqual(item["type"], "function_call")
        self.assertEqual(item["call_id"], "call_patch")
        self.assertEqual(item["name"], "exec_command")
        self.assertIn("subprocess.run(['apply_patch']", json.loads(item["arguments"])["cmd"])

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
                        "compat": {"fallback_thinking_on_tool_use": True},
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
    def test_responses_chat_can_fallback_reasoning_content_for_tool_history(self, mock_post):
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
                        "compat": {"fallback_thinking_on_tool_use": True},
                    }
                ]
            }
        )
        mock_post.return_value = {
            "id": "chatcmpl_2",
            "model": "m",
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
                "model": "m",
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
        upstream_payload = mock_post.call_args.args[1]
        self.assertEqual(
            upstream_payload["messages"][0]["reasoning_content"],
            "Tool use continuation context unavailable after proxy restart.",
        )
        self.assertEqual(upstream_payload["messages"][0]["tool_calls"][0]["id"], "call_1")

    @patch("opencodex_proxy.app.post_upstream")
    def test_responses_chat_groups_parallel_tool_calls_before_tool_outputs(self, mock_post):
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
            {
                "channels": [
                    {
                        "id": "deepseek",
                        "type": "chat",
                        "baseurl": "https://example.test/v1",
                        "apikey": "secret",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                        "compat": {"fallback_thinking_on_tool_use": True},
                        "models": [{"model": "gpt-5.5", "upstream_model": "deepseek-v4-pro"}],
                    }
                ]
            }
        )
        mock_post.return_value = {
            "id": "chatcmpl_2",
            "model": "deepseek-v4-pro",
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
                "model": "gpt-5.5",
                "input": [
                    {"role": "user", "content": [{"type": "input_text", "text": "run checks"}]},
                    {
                        "type": "function_call",
                        "call_id": "call_a",
                        "name": "exec_command",
                        "arguments": "{\"cmd\":\"pwd\"}",
                    },
                    {
                        "type": "function_call",
                        "call_id": "call_b",
                        "name": "exec_command",
                        "arguments": "{\"cmd\":\"ls\"}",
                    },
                    {
                        "type": "function_call",
                        "call_id": "call_c",
                        "name": "exec_command",
                        "arguments": "{\"cmd\":\"date\"}",
                    },
                    {"type": "function_call_output", "call_id": "call_a", "output": "/tmp"},
                    {"type": "function_call_output", "call_id": "call_b", "output": "file.txt"},
                    {"type": "function_call_output", "call_id": "call_c", "output": "today"},
                ],
            },
        )

        self.assertEqual(response.status_code, 200)
        upstream_payload = mock_post.call_args.args[1]
        self.assertEqual(
            [message["role"] for message in upstream_payload["messages"]],
            ["user", "assistant", "tool", "tool", "tool"],
        )
        assistant = upstream_payload["messages"][1]
        self.assertEqual(
            [tool_call["id"] for tool_call in assistant["tool_calls"]],
            ["call_a", "call_b", "call_c"],
        )
        self.assertEqual(
            [message["tool_call_id"] for message in upstream_payload["messages"][2:]],
            ["call_a", "call_b", "call_c"],
        )
        self.assertEqual(assistant["reasoning_content"], "Tool use continuation context unavailable after proxy restart.")

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
                        "compat": {},
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

    def test_stream_rejected_when_chat_entry_requires_protocol_conversion(self):
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
        response = self.client.post(
            "/v1/chat/completions",
            json={"model": "m", "messages": [{"role": "user", "content": "hi"}], "stream": True},
        )
        self.assertEqual(response.status_code, 400)

    @patch("opencodex_proxy.app.stream_upstream")
    def test_chat_stream_passthrough_rewrites_model_mapping(self, mock_stream):
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
                        "models": [{"model": "public-model", "upstream_model": "upstream-model"}],
                    }
                ]
            }
        )
        stream_lines = [
            'data: {"id":"chatcmpl_1","choices":[{"delta":{"content":"po"}}]}\n',
            "\n",
            'data: {"id":"chatcmpl_1","choices":[{"delta":{"content":"ng"}}]}\n',
            "\n",
            "data: [DONE]\n",
            "\n",
        ]

        def stream_side_effect(channel, payload, default_timeout):
            return iter(stream_lines)

        mock_stream.side_effect = stream_side_effect

        payload = {
            "model": "public-model",
            "messages": [{"role": "user", "content": "ping"}],
            "stream": True,
            "temperature": 0.2,
            "custom_param": {"keep": True},
        }
        response = self.client.post("/v1/chat/completions", json=payload)

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.mimetype, "text/event-stream")
        body = response.get_data(as_text=True)
        self.assertIn('"content":"po"', body)
        self.assertIn("data: [DONE]", body)
        self.assertNotIn("event: response.completed", body)

        upstream_payload = mock_stream.call_args.args[1]
        self.assertEqual(upstream_payload, {**payload, "model": "upstream-model"})

    @patch("opencodex_proxy.app.post_upstream")
    def test_chat_non_stream_passthrough_rewrites_model_mapping(self, mock_post):
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
                        "models": [{"model": "public-model", "upstream_model": "upstream-model"}],
                    }
                ]
            }
        )
        upstream_response = chat_text_response(
            "pong", model="upstream-model", response_id="chatcmpl_passthrough"
        )
        mock_post.return_value = upstream_response

        payload = {
            "model": "public-model",
            "messages": [{"role": "user", "content": "ping"}],
            "temperature": 0.2,
            "custom_param": {"keep": True},
        }
        response = self.client.post("/v1/chat/completions", json=payload)

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.get_json(), upstream_response)
        upstream_payload = mock_post.call_args.args[1]
        self.assertEqual(upstream_payload, {**payload, "model": "upstream-model"})

    @patch("opencodex_proxy.app.stream_upstream")
    def test_responses_stream_passthrough_for_same_protocol(self, mock_stream):
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
        mock_stream.return_value = iter(
            [
                "event: response.output_text.delta\n",
                'data: {"delta":"pong"}\n',
                "\n",
                "event: response.completed\n",
                'data: {"id":"resp_1","status":"completed"}\n',
                "\n",
            ]
        )
        response = self.client.post(
            "/v1/responses", json={"model": "m", "input": "ping", "stream": True}
        )
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.mimetype, "text/event-stream")
        body = response.get_data(as_text=True)
        self.assertIn('data: {"delta":"pong"}', body)
        self.assertIn("event: response.completed", body)
        upstream_payload = mock_stream.call_args.args[1]
        self.assertIs(upstream_payload["stream"], True)

    @patch("opencodex_proxy.app.stream_upstream")
    def test_same_protocol_responses_stream_records_ttft(self, mock_stream):
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
        mock_stream.return_value = iter(
            [
                "event: response.output_text.delta\n",
                'data: {"delta":"pong"}\n',
                "\n",
                "event: response.completed\n",
                'data: {"id":"resp_1","status":"completed"}\n',
                "\n",
            ]
        )

        response = self.client.post(
            "/v1/responses", json={"model": "gpt-4o", "input": "ping", "stream": True}
        )

        self.assertEqual(response.status_code, 200)
        body = response.get_data(as_text=True)
        self.assertIn("event: response.completed", body)

        db_writer = self.app.config["OPENCODEX_DB_WRITER"]
        db_writer.stop()
        logs = read_logs(self.db_path)
        self.assertEqual(len(logs), 1)
        self.assertIsNotNone(logs[0]["ttft_ms"])

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
        upstream_response_body = json.loads(log["upstream_response_body"])
        self.assertEqual(upstream_response_body["content"][0]["text"], "pong")
        response_body = json.loads(log["response_body"])
        self.assertEqual(response_body["output"][0]["content"][0]["text"], "pong")

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
        self.assertIn("event: response.function_call_arguments.delta", body)
        self.assertIn("event: response.function_call_arguments.done", body)
        self.assertIn("event: response.output_item.done", body)
        self.assertIn("function_call", body)
        self.assertIn("\"call_id\":\"call_1\"", body)
        self.assertIn("\"name\":\"exec_command\"", body)
        self.assertIn("\"arguments\":\"{\\\"cmd\\\":\\\"pwd\\\"}\"", body)
        self.assertIn("event: response.completed", body)
        upstream_payload = mock_stream.call_args.args[1]
        self.assertIs(upstream_payload["stream"], True)

    @patch("opencodex_proxy.app.stream_upstream")
    def test_responses_stream_to_chat_streams_reasoning_annotations_and_sequence(self, mock_stream):
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
                'data: {"id":"chatcmpl_reason","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"think "},"finish_reason":null}]}\n',
                "\n",
                'data: {"id":"chatcmpl_reason","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{"content":"answer","annotations":[{"type":"url_citation","url":"https://example.test/a","title":"Example","summary":"snippet"}]},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}\n',
                "\n",
                "data: [DONE]\n",
                "\n",
            ]
        )

        response = self.client.post(
            "/v1/responses", json={"model": "deepseek-v4-pro", "input": "run", "stream": True}
        )

        self.assertEqual(response.status_code, 200)
        events = self.parse_sse_events(response.get_data(as_text=True))
        names = [event for event, _ in events]
        self.assertIn("response.reasoning_summary_text.delta", names)
        self.assertIn("response.reasoning_summary_text.done", names)
        self.assertIn("response.output_text.annotation.added", names)
        self.assertEqual(
            [data["sequence_number"] for _, data in events],
            list(range(len(events))),
        )
        completed = next(data for event, data in events if event == "response.completed")
        output = completed["response"]["output"]
        self.assertEqual(output[0]["type"], "reasoning")
        self.assertEqual(output[0]["encrypted_content"], "think ")
        self.assertEqual(output[1]["content"][0]["annotations"][0]["snippet"], "snippet")

    @patch("opencodex_proxy.app.stream_upstream")
    def test_responses_stream_to_chat_maps_apply_patch_to_exec_command(self, mock_stream):
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
        self.assertIn("\"type\":\"function_call\"", body)
        self.assertIn("\"name\":\"exec_command\"", body)
        self.assertIn("subprocess.run(['apply_patch']", body)
        self.assertNotIn("\"type\":\"custom_tool_call\"", body)

    @patch("opencodex_proxy.app.stream_upstream")
    def test_responses_stream_to_chat_maps_apply_patch_proxy_to_custom_tool_call(
        self, mock_stream
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
        arguments = json.dumps(
            {
                "path": "data.json",
                "hunks": [
                    {
                        "lines": [
                            {"op": "remove", "text": '  "old": true'},
                            {"op": "add", "text": '  "old": false'},
                        ]
                    }
                ],
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
                                "id": "call_patch_proxy",
                                "type": "function",
                                "function": {
                                    "name": "apply_patch_update_file",
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
        self.assertIn("\"call_id\":\"call_patch_proxy\"", body)
        self.assertIn("*** Update File: data.json", body)
        self.assertIn("-  \\\"old\\\": true", body)
        self.assertIn("+  \\\"old\\\": false", body)
        self.assertNotIn("response.function_call_arguments.delta", body)
        self.assertNotIn("\"type\":\"function_call\"", body)

    @patch("opencodex_proxy.app.stream_upstream")
    def test_responses_stream_to_chat_emits_apply_patch_semantic_preview(
        self, mock_stream
    ):
        arguments = json.dumps(
            {
                "path": "data.json",
                "hunks": [
                    {
                        "lines": [
                            {"op": "remove", "text": '  "old": true'},
                            {"op": "add", "text": '  "old": false'},
                        ]
                    }
                ],
            },
            ensure_ascii=False,
        )
        split_at = arguments.index('"hunks"')
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
                                "id": "call_patch_preview",
                                "type": "function",
                                "function": {
                                    "name": "apply_patch_update_file",
                                    "arguments": arguments[:split_at],
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
                                "function": {"arguments": arguments[split_at:]},
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
            "/v1/responses",
            json={"model": "deepseek-v4-pro", "input": "patch", "stream": True},
        )

        self.assertEqual(response.status_code, 200)
        events = self.parse_sse_events(response.get_data(as_text=True))
        preview_events = [
            data for event, data in events if event == "patch.semantic_preview"
        ]
        self.assertGreaterEqual(len(preview_events), 2)
        self.assertEqual(preview_events[0]["event"], "file_started")
        self.assertEqual(preview_events[0]["path"], "data.json")
        self.assertEqual(preview_events[0]["op"], "update")
        self.assertNotIn("source", preview_events[0])
        self.assertNotIn("preview_id", preview_events[0])
        self.assertEqual(preview_events[-1]["event"], "file_finished")
        self.assertEqual(
            [data["sequence_number"] for _, data in events],
            list(range(len(events))),
        )

    @patch("opencodex_proxy.app.stream_upstream")
    def test_responses_stream_to_chat_apply_patch_content_preview_is_progress_only(
        self, mock_stream
    ):
        arguments = json.dumps(
            {"path": "large.txt", "content": "partial\nfinal"},
            ensure_ascii=False,
        )
        split_at = arguments.index("partial") + len("partial")
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
                                "id": "call_patch_content_preview",
                                "type": "function",
                                "function": {
                                    "name": "apply_patch_replace_file",
                                    "arguments": arguments[:split_at],
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
                                "function": {"arguments": arguments[split_at:]},
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
            "/v1/responses",
            json={"model": "deepseek-v4-pro", "input": "patch", "stream": True},
        )

        self.assertEqual(response.status_code, 200)
        body = response.get_data(as_text=True)
        events = self.parse_sse_events(body)
        preview_events = [
            data for event, data in events if event == "patch.semantic_preview"
        ]
        progress_events = [
            data for data in preview_events if data["event"] == "content_progress"
        ]
        self.assertEqual(len(progress_events), 1)
        self.assertEqual(progress_events[0]["chars"], len("partial\nfinal"))
        self.assertNotIn(
            "partial",
            json.dumps(preview_events, ensure_ascii=False),
        )
        self.assertNotIn("response.custom_tool_call_input.delta", body)
        self.assertIn("+partial\\n+final", body)

    @patch("opencodex_proxy.app.stream_upstream")
    def test_responses_stream_to_chat_apply_patch_content_progress_is_throttled(
        self, mock_stream
    ):
        content = "x" * 1100
        arguments = json.dumps(
            {"path": "large.txt", "content": content},
            ensure_ascii=False,
        )
        chunks = [
            arguments[:200],
            arguments[200:700],
            arguments[700:1050],
            arguments[1050:],
        ]
        stream_chunks = []
        for index, chunk in enumerate(chunks):
            delta: dict[str, object] = {
                "tool_calls": [
                    {
                        "index": 0,
                        "function": {"arguments": chunk},
                    }
                ]
            }
            if index == 0:
                delta["role"] = "assistant"
                delta["tool_calls"][0]["id"] = "call_patch_throttle"  # type: ignore[index]
                delta["tool_calls"][0]["type"] = "function"  # type: ignore[index]
                delta["tool_calls"][0]["function"]["name"] = (  # type: ignore[index]
                    "apply_patch_replace_file"
                )
            stream_chunks.extend(
                [
                    f"data: {json.dumps({'id':'chatcmpl_tool','object':'chat.completion.chunk','created':1,'model':'deepseek-v4-pro','choices':[{'index':0,'delta':delta,'finish_reason':None}]}, ensure_ascii=False)}\n",
                    "\n",
                ]
            )
        stream_chunks.extend(
            [
                'data: {"id":"chatcmpl_tool","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}\n',
                "\n",
                "data: [DONE]\n",
                "\n",
            ]
        )
        mock_stream.return_value = iter(stream_chunks)

        response = self.client.post(
            "/v1/responses",
            json={"model": "deepseek-v4-pro", "input": "patch", "stream": True},
        )

        self.assertEqual(response.status_code, 200)
        events = self.parse_sse_events(response.get_data(as_text=True))
        progress = [
            data["chars"]
            for event, data in events
            if event == "patch.semantic_preview" and data["event"] == "content_progress"
        ]
        self.assertEqual(len(progress), 2)
        self.assertGreaterEqual(progress[0], 512)
        self.assertLess(progress[0], len(content))
        self.assertEqual(progress[-1], len(content))

    @patch("opencodex_proxy.app.stream_upstream")
    def test_responses_stream_to_chat_apply_patch_batch_preview_waits_for_operation_boundary(
        self, mock_stream
    ):
        arguments = json.dumps(
            {
                "operations": [
                    {
                        "type": "add_file",
                        "path": "created.txt",
                        "content": "hello",
                    }
                ]
            },
            ensure_ascii=False,
        )
        split_at = arguments.index('"content"')
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
                                "id": "call_patch_batch_preview",
                                "type": "function",
                                "function": {
                                    "name": "apply_patch_batch",
                                    "arguments": arguments[:split_at],
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
                                "function": {"arguments": arguments[split_at:]},
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
            "/v1/responses",
            json={"model": "deepseek-v4-pro", "input": "patch", "stream": True},
        )

        self.assertEqual(response.status_code, 200)
        events = self.parse_sse_events(response.get_data(as_text=True))
        preview_events = [
            data for event, data in events if event == "patch.semantic_preview"
        ]
        self.assertEqual(
            [data["event"] for data in preview_events],
            [
                "file_started",
                "content_progress",
                "file_finished",
            ],
        )
        self.assertEqual(preview_events[0]["path"], "created.txt")
        self.assertEqual(preview_events[0]["op"], "add")

    @patch("opencodex_proxy.app.stream_upstream")
    def test_responses_stream_to_chat_maps_newline_apply_patch_proxy_to_exec_command(
        self, mock_stream
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
        arguments = json.dumps(
            {"path": "bad\nname.txt", "content": "content"},
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
                                "id": "call_patch_newline_proxy",
                                "type": "function",
                                "function": {
                                    "name": "apply_patch_add_file",
                                    "arguments": arguments[:30],
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
                                "function": {"arguments": arguments[30:]},
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
        self.assertIn("\"type\":\"function_call\"", body)
        self.assertIn("\"name\":\"exec_command\"", body)
        self.assertIn("\"call_id\":\"call_patch_newline_proxy\"", body)
        self.assertIn("PYTHON_BIN=python3", body)
        self.assertIn("event: response.function_call_arguments.done", body)
        self.assertNotIn("patch.semantic_preview", body)
        self.assertNotIn("\"type\":\"custom_tool_call\"", body)

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
        upstream_response_body = json.loads(log["upstream_response_body"])
        self.assertEqual(
            upstream_response_body["choices"][0]["message"]["content"],
            "pong",
        )
        response_body = json.loads(log["response_body"])
        self.assertEqual(
            response_body["output"][0]["content"][0]["text"],
            "pong",
        )

    @patch("opencodex_proxy.app.stream_upstream")
    def test_chat_stream_tool_call_records_ttft(self, mock_stream):
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
                'data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"exec_command","arguments":"{\\"cmd\\":"}}]},"finish_reason":null}]}\n',
                "\n",
                'data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\\"pwd\\"}"}}]},"finish_reason":null}]}\n',
                "\n",
                'data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}\n',
                "\n",
                "data: [DONE]\n",
                "\n",
            ]
        )

        response = self.client.post(
            "/v1/responses", json={"model": "deepseek-v4-pro", "input": "run", "stream": True}
        )

        self.assertEqual(response.status_code, 200)
        body = response.get_data(as_text=True)
        self.assertIn("event: response.output_item.added", body)
        self.assertIn("event: response.function_call_arguments.delta", body)

        db_writer = self.app.config["OPENCODEX_DB_WRITER"]
        db_writer.stop()
        logs = read_logs(self.db_path)
        self.assertEqual(len(logs), 1)
        self.assertIsNotNone(logs[0]["ttft_ms"])

    @patch("opencodex_proxy.app.post_upstream")
    def test_logs_are_available_and_basic(self, mock_post):
        mock_post.return_value = {
            "id": "chatcmpl_1",
            "model": "gpt-5.4",
            "choices": [{"message": {"role": "assistant", "content": "pong"}}],
            "usage": {
                "prompt_tokens": 100,
                "prompt_tokens_details": {"cached_tokens": 20},
                "completion_tokens": 50,
            },
        }
        self.client.post("/v1/responses", json={"model": "m", "input": "ping"})
        db_writer = self.app.config["OPENCODEX_DB_WRITER"]
        db_writer.stop()
        self.login()
        response = self.client.get("/admin/api/logs?page=1&page_size=10")
        self.assertEqual(response.status_code, 200)
        payload = response.get_json()
        self.assertEqual(payload["page"], 1)
        self.assertEqual(payload["page_size"], 10)
        self.assertEqual(payload["total"], 1)
        self.assertNotIn("filter_options", payload)
        events = payload["events"]
        self.assertTrue(events)
        self.assertIn("status_code", events[0])
        self.assertEqual(events[0]["request_status"], "success")
        self.assertEqual(events[0]["cached_tokens"], 20)
        self.assertGreater(events[0]["cost"], 0)
        for field in (
            "request_headers",
            "request_body",
            "upstream_request_body",
            "upstream_response_body",
            "response_body",
            "web_search_json",
        ):
            self.assertNotIn(field, events[0])

        options_response = self.client.get("/admin/api/log-filter-options?field=model&q=m")
        self.assertEqual(options_response.status_code, 200)
        self.assertEqual(options_response.get_json()["models"], ["m"])

        detail_response = self.client.get(f"/admin/api/logs/{events[0]['id']}")
        self.assertEqual(detail_response.status_code, 200)
        detail = detail_response.get_json()
        request_headers = json.loads(detail["request_headers"])
        self.assertNotEqual(request_headers["Authorization"], self.auth_headers["Authorization"])
        self.assertIn("...", request_headers["Authorization"])
        self.assertIn("ping", detail["request_body"])
        self.assertIn("ping", detail["upstream_request_body"])
        self.assertIn("pong", detail["upstream_response_body"])
        self.assertIn("pong", detail["response_body"])

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
    def test_admin_logs_mark_failed_requests(self, mock_post):
        mock_post.side_effect = UpstreamError(
            "upstream returned HTTP 502",
            status_code=502,
            body={"error": {"message": "bad gateway"}},
            channel_id="chat",
        )
        self.client.post("/v1/responses", json={"model": "failed-model", "input": "ping"})
        db_writer = self.app.config["OPENCODEX_DB_WRITER"]
        db_writer.stop()
        self.login()

        response = self.client.get("/admin/api/logs?request_status=failed")

        self.assertEqual(response.status_code, 200)
        events = response.get_json()["events"]
        self.assertEqual(len(events), 1)
        self.assertEqual(events[0]["request_status"], "failed")
        self.assertEqual(events[0]["status_code"], 502)
        self.assertIn("upstream returned HTTP 502", events[0]["error"])

    def test_proxy_requires_user_access_api_key(self):
        response = self._client_open_without_default_proxy_auth(
            "/v1/responses",
            method="POST",
            json={"model": "m", "input": "ping"},
        )

        self.assertEqual(response.status_code, 401)
        self.assertIn("valid bearer api key required", response.get_json()["error"]["message"])

    @patch("opencodex_proxy.app.post_upstream")
    def test_proxy_routes_same_channel_id_by_access_key_owner(self, mock_post):
        create_user(self.db_path, "alice", "alice-pw")
        alice_key = create_access_api_key(self.db_path, "alice", "alice")["key"]
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
            {
                "channels": [
                    {
                        "id": "chat",
                        "type": "chat",
                        "baseurl": "https://admin.example.test/v1",
                        "apikey": "admin-upstream-key",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                    },
                    {
                        "owner_username": "alice",
                        "id": "chat",
                        "type": "chat",
                        "baseurl": "https://alice.example.test/v1",
                        "apikey": "alice-upstream-key",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                    },
                ]
            },
            owner_username=None,
        )
        mock_post.return_value = chat_text_response("pong")

        admin_response = self.client.post(
            "/v1/responses",
            json={"model": "m", "input": "admin"},
        )
        alice_response = self.client.post(
            "/v1/responses",
            headers={"Authorization": f"Bearer {alice_key}"},
            json={"model": "m", "input": "alice"},
        )

        self.assertEqual(admin_response.status_code, 200)
        self.assertEqual(alice_response.status_code, 200)
        self.assertEqual(mock_post.call_args_list[0].args[0]["baseurl"], "https://admin.example.test/v1")
        self.assertEqual(mock_post.call_args_list[0].args[2], 30)
        self.assertEqual(mock_post.call_args_list[1].args[0]["baseurl"], "https://alice.example.test/v1")
        self.assertEqual(mock_post.call_args_list[1].args[2], 30)

    def test_proxy_rejects_disabled_or_deleted_access_key(self):
        response = self.client.patch(
            "/admin/api/api-keys/1",
            json={"enabled": False},
        )
        self.assertEqual(response.status_code, 401)

        self.login_api()
        response = self.client.patch(
            "/admin/api/api-keys/1",
            json={"enabled": False},
        )
        self.assertEqual(response.status_code, 200)

        response = self.client.post("/v1/responses", json={"model": "m", "input": "ping"})
        self.assertEqual(response.status_code, 401)

        self.client.delete("/admin/api/api-keys/1")
        response = self.client.post("/v1/responses", json={"model": "m", "input": "ping"})
        self.assertEqual(response.status_code, 401)

    def test_disabled_user_access_key_is_rejected(self):
        create_user(self.db_path, "alice", "alice-pw")
        alice_key = create_access_api_key(self.db_path, "alice", "alice")["key"]
        self.login_api()
        self.client.patch("/admin/api/users/alice", json={"enabled": False})

        response = self.client.post(
            "/v1/responses",
            headers={"Authorization": f"Bearer {alice_key}"},
            json={"model": "m", "input": "ping"},
        )

        self.assertEqual(response.status_code, 401)

    def test_regular_user_without_channels_does_not_fall_back_to_admin_channels(self):
        create_user(self.db_path, "alice", "alice-pw")
        alice_key = create_access_api_key(self.db_path, "alice", "alice")["key"]

        response = self.client.post(
            "/v1/responses",
            headers={"Authorization": f"Bearer {alice_key}"},
            json={"model": "m", "input": "ping"},
        )

        self.assertEqual(response.status_code, 400)
        self.assertIn("no enabled channels configured", response.get_json()["error"]["message"])

    @patch("opencodex_proxy.app.post_upstream")
    def test_logs_are_isolated_by_user(self, mock_post):
        create_user(self.db_path, "alice", "alice-pw")
        alice_key = create_access_api_key(self.db_path, "alice", "alice")["key"]
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
            {
                "channels": [
                    {
                        "id": "chat",
                        "type": "chat",
                        "baseurl": "https://admin.example.test/v1",
                        "apikey": "admin-upstream-key",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                    },
                    {
                        "owner_username": "alice",
                        "id": "chat",
                        "type": "chat",
                        "baseurl": "https://alice.example.test/v1",
                        "apikey": "alice-upstream-key",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                    },
                ]
            },
            owner_username=None,
        )
        mock_post.return_value = chat_text_response("pong")
        self.client.post("/v1/responses", json={"model": "admin-model", "input": "ping"})
        self.client.post(
            "/v1/responses",
            headers={"Authorization": f"Bearer {alice_key}"},
            json={"model": "alice-model", "input": "ping"},
        )
        db_writer = self.app.config["OPENCODEX_DB_WRITER"]
        db_writer.stop()

        self.login_api("alice", "alice-pw")
        response = self.client.get("/admin/api/logs?page=1&page_size=10")
        self.assertEqual(response.status_code, 200)
        payload = response.get_json()
        self.assertEqual(payload["total"], 1)
        self.assertEqual(payload["events"][0]["owner_username"], "alice")
        self.assertEqual(payload["events"][0]["model"], "alice-model")

        options_response = self.client.get("/admin/api/log-filter-options?field=owner_username")
        self.assertEqual(options_response.status_code, 200)
        self.assertEqual(options_response.get_json()["owner_usernames"], ["alice"])

        self.login_api()
        response = self.client.get("/admin/api/logs?page=1&page_size=10&owner_username=alice")
        self.assertEqual(response.status_code, 200)
        payload = response.get_json()
        self.assertEqual(payload["total"], 1)
        self.assertEqual(payload["events"][0]["owner_username"], "alice")

        options_response = self.client.get("/admin/api/log-filter-options?field=owner_username&q=alice")
        self.assertEqual(options_response.status_code, 200)
        self.assertEqual(options_response.get_json()["owner_usernames"], ["alice"])

        response = self.client.get("/admin/api/logs?page=1&page_size=10")
        self.assertEqual(response.get_json()["total"], 2)

    def test_regular_user_config_save_forces_own_owner(self):
        create_user(self.db_path, "alice", "alice-pw")
        self.login_api("alice", "alice-pw")

        response = self.client.post(
            "/admin/api/config",
            json={
                "channels": [
                    {
                        "owner_username": "admin",
                        "id": "chat",
                        "type": "chat",
                        "baseurl": "https://alice.example.test/v1",
                        "apikey": "alice-key",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                    }
                ]
            },
        )

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.get_json()["channels"][0]["owner_username"], "alice")
        alice_channels = read_channels(self.db_path, owner_username="alice")
        admin_channels = read_channels(self.db_path, owner_username="admin")
        self.assertEqual(alice_channels[0]["baseurl"], "https://alice.example.test/v1")
        self.assertEqual(admin_channels[0]["baseurl"], "https://example.test/v1")

    @patch("opencodex_proxy.app.tavily_search")
    @patch("opencodex_proxy.app.post_upstream")
    def test_regular_user_web_search_declaration_does_not_run_local_simulation(
        self, mock_post, mock_tavily
    ):
        create_user(self.db_path, "alice", "alice-pw")
        alice_key = create_access_api_key(self.db_path, "alice", "alice")["key"]
        manager = self.app.config["OPENCODEX_CONFIG_MANAGER"]
        manager.save(
            {
                "channels": [
                    {
                        "id": "chat",
                        "type": "chat",
                        "baseurl": "https://alice.example.test/v1",
                        "apikey": "alice-upstream-key",
                        "auth_mode": "config",
                        "timeout_seconds": 30,
                    }
                ]
            },
            owner_username="alice",
        )
        self.enable_web_search()
        mock_post.return_value = chat_tool_response(
            function_tool_call("call_web", "web_search", {"query": "OpenAI"})
        )

        response = self.client.post(
            "/v1/responses",
            headers={"Authorization": f"Bearer {alice_key}"},
            json={"model": "m", "input": "search", "tools": [{"type": "web_search"}]},
        )

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.get_json()["output"][0]["type"], "function_call")
        mock_tavily.assert_not_called()
        self.assertEqual(mock_post.call_count, 1)

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

    def test_stats_api_default_range(self):
        self.login_api()
        resp = self.client.get("/admin/api/stats")
        self.assertEqual(resp.status_code, 200)
        data = resp.get_json()
        self.assertEqual(data["range"], "1h")
        self.assertNotIn("window", data)
        self.assertIn("currency_rate", data)
        self.assertIn("summary", data)
        self.assertIsInstance(data["points"], list)
        self.assertIsInstance(data["model_distribution"], list)

    def test_stats_api_explicit_range(self):
        self.login_api()
        expected_granularity = {"1h": 1, "6h": 5, "24h": 15, "7d": 120, "30d": 720}
        for range_key, granularity in expected_granularity.items():
            resp = self.client.get(f"/admin/api/stats?range={range_key}")
            self.assertEqual(resp.status_code, 200)
            data = resp.get_json()
            self.assertEqual(data["range"], range_key)
            self.assertEqual(data["granularity_minutes"], granularity)

    def test_stats_api_custom_range(self):
        self.login_api()
        resp = self.client.get("/admin/api/stats?range=custom&start=1700000000&end=1700003600")
        self.assertEqual(resp.status_code, 200)
        data = resp.get_json()
        self.assertEqual(data["range"], "custom")
        self.assertEqual(data["granularity_minutes"], 1)
        self.assertEqual(data["start"], "2023-11-15T06:13:20")
        self.assertEqual(data["end"], "2023-11-15T07:13:20")

    def test_stats_api_summary(self):
        now = 1_700_003_600
        conn = sqlite3.connect(str(self.db_path))
        try:
            conn.executemany(
                """
                INSERT INTO request_logs (
                    request_id, created_at, method, path, client_ip,
                    model, upstream_model, channel_id, is_stream,
                    ttft_ms, duration_ms, status_code, input_tokens,
                    cached_tokens, output_tokens, cost, owner_username, error
                ) VALUES (?, ?, 'POST', '/v1/responses', '127.0.0.1',
                    ?, ?, 'chat', 0, ?, 100, ?, ?, ?, ?, ?, ?, ?
                )
                """,
                [
                    ("req1", now - 60, "m1", "m1", 100, 200, 30, 10, 20, 7.25, "admin", None),
                    ("req2", now - 120, "m1", "m1", 0, 200, 40, 20, 10, 14.5, "admin", ""),
                    ("req3", now - 4000, "m2", "m2", 0, 500, 1, 1, 1, 10.0, "admin", "boom"),
                ],
            )
            conn.commit()
        finally:
            conn.close()

        self.login_api()
        resp = self.client.get(
            f"/admin/api/stats?range=custom&start={now - 3600}&end={now}"
        )
        self.assertEqual(resp.status_code, 200)
        summary = resp.get_json()["summary"]
        self.assertEqual(summary["request_count"], 2)
        self.assertEqual(summary["success_count"], 2)
        self.assertEqual(summary["recent_1h_request_count"], 2)
        self.assertEqual(summary["input_tokens"], 70)
        self.assertEqual(summary["cached_tokens"], 30)
        self.assertEqual(summary["output_tokens"], 30)
        self.assertEqual(summary["total_tokens"], 130)
        self.assertEqual(summary["cost"], 21.75)
        self.assertEqual(summary["recent_1h_cost"], 21.75)
        self.assertEqual(summary["rpm"], 1)
        self.assertEqual(summary["tpm"], 60)
        self.assertEqual(resp.get_json()["model_distribution"], [{"model": "m1", "count": 2}])

    def test_stats_api_invalid_range_defaults_to_1h(self):
        self.login_api()
        resp = self.client.get("/admin/api/stats?range=invalid")
        data = resp.get_json()
        self.assertEqual(data["range"], "1h")

    def test_stats_api_ignores_legacy_window_param(self):
        self.login_api()
        resp = self.client.get("/admin/api/stats?window=6h")
        data = resp.get_json()
        self.assertEqual(data["range"], "1h")

    def test_stats_api_regular_user_scope(self):
        create_user(self.db_path, "user1", "pass1")
        self.login_api("user1", "pass1")
        resp = self.client.get("/admin/api/stats?range=1h")
        self.assertEqual(resp.status_code, 200)
        self.assertIn("points", resp.get_json())

    def test_stats_api_empty_data(self):
        self.login_api()
        resp = self.client.get("/admin/api/stats?range=1h")
        data = resp.get_json()
        self.assertIsInstance(data["points"], list)
        self.assertGreater(len(data["points"]), 0)
        for p in data["points"]:
            self.assertEqual(p["cost"], 0)
            self.assertEqual(p["input_tokens"], 0)
            self.assertEqual(p["cached_tokens"], 0)
            self.assertEqual(p["output_tokens"], 0)
            self.assertIsNone(p["avg_ttft_ms"])
            self.assertIsNone(p["cache_hit_rate"])
            self.assertEqual(p["rpm"], 0)
        self.assertEqual(data["model_distribution"], [])
        self.assertEqual(data["summary"]["request_count"], 0)
        self.assertEqual(data["summary"]["total_tokens"], 0)

    def test_stats_api_requires_auth(self):
        resp = self.client.get("/admin/api/stats")
        self.assertIn(resp.status_code, (401, 403))


if __name__ == "__main__":
    unittest.main()
