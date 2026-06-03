from __future__ import annotations

from collections.abc import Callable, Iterable

import atexit
import json
import sqlite3
import time
import uuid
from copy import deepcopy
from pathlib import Path
from typing import Any

from flask import (
    Flask,
    Response,
    current_app,
    jsonify,
    redirect,
    render_template,
    request,
    session,
    send_from_directory,
    stream_with_context,
    url_for,
)

from .compat import apply_compat
from .config import (
    ConfigError,
    ConfigManager,
    expand_env,
    normalize_config,
    validate_channel,
)
from .db import (
    AsyncDBWriter,
    authenticate_access_api_key,
    authenticate_user,
    calculate_cost,
    create_access_api_key,
    create_user,
    delete_access_api_key,
    delete_user,
    ensure_superadmin,
    extract_usage,
    get_user,
    list_access_api_keys,
    list_users,
    read_log_filter_options,
    read_log_by_id,
    read_logs_page,
    read_web_search_config,
    replace_web_search_config,
    reserve_tavily_key,
    reserve_tavily_key_by_id,
    reset_user_password,
    set_access_api_key_enabled,
    set_user_enabled,
    read_stats,
)
from .errors import BadRequestError, ProxyError
from .logging_utils import configure_logging, log_event, redact
from .protocols import convert_request, convert_response
from .reasoning_cache import ReasoningCache
from .routing import choose_channel
from .settings import Settings, SettingsError
from .streaming import (
    chat_sse_to_responses_events,
    messages_sse_to_responses_events,
    responses_sse_events,
)
from .upstream import list_upstream_models, post_upstream, stream_upstream
from .web_search import (
    add_source_annotations,
    append_tool_results,
    extract_tool_calls,
    max_web_search_calls,
    non_web_search_calls,
    parse_web_search_query,
    prepend_web_search_items,
    replace_web_search_function_items,
    request_declares_web_search,
    tavily_search,
    web_search_calls,
    web_search_log,
    make_tool_result,
    build_web_search_item,
)


ENTRY_PROTOCOLS = {
    "/v1/responses": "responses",
    "/v1/chat/completions": "chat",
    "/v1/messages": "messages",
}

DIRECT_CACHE_NAMESPACE_KEYS = ("thread_id", "conversation_id", "session_id")
METADATA_CACHE_NAMESPACE_KEYS = (
    "thread_id",
    "conversation_id",
    "session_id",
    "codex_thread_id",
    "codex_conversation_id",
    "x-codex-thread-id",
    "x-codex-conversation-id",
    "x-codex-session-id",
    "prompt_cache_key",
)
FALLBACK_REASONING_CONTENT = "Tool use continuation context unavailable after proxy restart."


def create_app(settings: Settings | None = None) -> Flask:
    settings = settings or Settings.from_env()
    logger = configure_logging(settings.log_path, settings.log_level)
    ensure_superadmin(settings.db_path, settings.admin_username, settings.admin_password)
    config_manager = ConfigManager(
        settings.db_path,
        settings.default_timeout,
        default_owner_username=settings.admin_username,
    )
    reasoning_cache = ReasoningCache()
    db_writer = AsyncDBWriter(settings.db_path, default_owner_username=settings.admin_username)
    db_writer.start()
    atexit.register(db_writer.stop)

    app = Flask(__name__)
    app.secret_key = settings.secret_key
    admin_static_dir = (Path(__file__).parent / "static" / "admin").resolve()
    app.config["OPENCODEX_SETTINGS"] = settings
    app.config["OPENCODEX_CONFIG_MANAGER"] = config_manager
    app.config["OPENCODEX_LOGGER"] = logger
    app.config["OPENCODEX_DB_WRITER"] = db_writer

    @app.teardown_appcontext
    def shutdown_db(exception=None):
        pass

    @app.errorhandler(ProxyError)
    def handle_proxy_error(exc: ProxyError):
        return jsonify(exc.to_response()), exc.status_code

    @app.get("/")
    def index():
        return redirect(url_for("admin"))

    @app.route("/admin", methods=["GET", "POST"])
    def admin():
        error = None
        if request.method == "POST":
            username = request.form.get("username", settings.admin_username)
            password = request.form.get("password", "")
            user = authenticate_user(settings.db_path, username, password)
            if user is not None:
                _set_session_user(user)
                return redirect(url_for("admin"))
            error = "用户名或密码错误"
            log_event(logger, "WARNING", "admin login failed", path="/admin", username=username)
        if request.method == "GET" and (admin_static_dir / "index.html").exists():
            return _spa_index_response(admin_static_dir)
        if not current_session_user():
            return render_template("login.html", error=error)
        return render_template(
            "admin.html",
            log_view_level=settings.log_view_level,
            log_levels=["BASIC", "DEBUG", "TRACE"],
        )

    @app.get("/admin/<path:asset_path>")
    def admin_assets(asset_path: str):
        if (admin_static_dir / asset_path).is_file():
            return send_from_directory(admin_static_dir, asset_path)
        if (admin_static_dir / "index.html").exists():
            return _spa_index_response(admin_static_dir)
        return redirect(url_for("admin"))

    @app.get("/admin/api/session")
    def admin_session():
        user = current_session_user()
        return jsonify({"authenticated": user is not None, "user": user})

    @app.post("/admin/api/login")
    def admin_login():
        body = request.get_json(silent=True)
        username = settings.admin_username
        password = ""
        if isinstance(body, dict):
            username = str(body.get("username") or username)
            password = str(body.get("password", ""))
        else:
            username = request.form.get("username", username)
            password = request.form.get("password", "")
        user = authenticate_user(settings.db_path, username, password)
        if user is not None:
            _set_session_user(user)
            return jsonify({"authenticated": True, "user": _session_user_payload(user)})
        log_event(
            logger,
            "WARNING",
            "admin login failed",
            path="/admin/api/login",
            username=username,
        )
        return jsonify({"error": "用户名或密码错误"}), 401

    @app.post("/admin/logout")
    def admin_logout():
        session.clear()
        return redirect(url_for("admin"))

    @app.post("/admin/api/logout")
    def admin_api_logout():
        session.clear()
        return jsonify({"authenticated": False})

    @app.get("/admin/api/users")
    def admin_list_users():
        require_superadmin()
        return jsonify({"users": list_users(settings.db_path)})

    @app.post("/admin/api/users")
    def admin_create_user():
        require_superadmin()
        body = request.get_json(silent=True)
        if not isinstance(body, dict):
            return jsonify({"error": "request body must be a JSON object"}), 400
        try:
            user = create_user(
                settings.db_path,
                str(body.get("username") or ""),
                str(body.get("password") or ""),
                role="user",
                enabled=body.get("enabled", True) is not False,
            )
        except ValueError as exc:
            return jsonify({"error": str(exc)}), 400
        return jsonify({"user": user}), 201

    @app.patch("/admin/api/users/<username>")
    def admin_update_user(username: str):
        require_superadmin()
        body = request.get_json(silent=True)
        if not isinstance(body, dict):
            return jsonify({"error": "request body must be a JSON object"}), 400
        try:
            if "enabled" in body:
                user = set_user_enabled(
                    settings.db_path,
                    username,
                    body.get("enabled") is True,
                    protected_username=settings.admin_username,
                )
            else:
                user = get_user(settings.db_path, username)
                if user is None:
                    return jsonify({"error": "user not found"}), 404
            if "password" in body:
                if username == settings.admin_username:
                    return jsonify({"error": "environment superadmin password is managed by env"}), 400
                user = reset_user_password(
                    settings.db_path,
                    username,
                    str(body.get("password") or ""),
                )
        except ValueError as exc:
            return jsonify({"error": str(exc)}), 400
        return jsonify({"user": user})

    @app.delete("/admin/api/users/<username>")
    def admin_delete_user(username: str):
        user = require_superadmin()
        try:
            deleted_user = delete_user(
                settings.db_path,
                username,
                protected_username=user["username"],
            )
        except ValueError as exc:
            status_code = 404 if str(exc) == "user not found" else 400
            return jsonify({"error": str(exc)}), status_code
        config_manager.reload()
        return jsonify({"deleted": True, "user": deleted_user})

    @app.get("/admin/api/api-keys")
    def admin_list_api_keys():
        user = require_user()
        owner = request.args.get("owner_username")
        if user["role"] != "superadmin":
            owner = user["username"]
        elif owner in (None, ""):
            owner = None
        return jsonify({"keys": list_access_api_keys(settings.db_path, owner)})

    @app.post("/admin/api/api-keys")
    def admin_create_api_key():
        user = require_user()
        body = request.get_json(silent=True)
        if not isinstance(body, dict):
            return jsonify({"error": "request body must be a JSON object"}), 400
        owner = str(body.get("owner_username") or user["username"])
        if user["role"] != "superadmin":
            owner = user["username"]
        try:
            key = create_access_api_key(
                settings.db_path,
                owner,
                str(body.get("name") or ""),
            )
        except ValueError as exc:
            return jsonify({"error": str(exc)}), 400
        return jsonify({"key": key}), 201

    @app.patch("/admin/api/api-keys/<int:key_id>")
    def admin_update_api_key(key_id: int):
        user = require_user()
        body = request.get_json(silent=True)
        if not isinstance(body, dict):
            return jsonify({"error": "request body must be a JSON object"}), 400
        owner = None if user["role"] == "superadmin" else user["username"]
        try:
            key = set_access_api_key_enabled(
                settings.db_path,
                key_id,
                body.get("enabled") is True,
                owner_username=owner,
            )
        except ValueError as exc:
            return jsonify({"error": str(exc)}), 404
        return jsonify({"key": key})

    @app.delete("/admin/api/api-keys/<int:key_id>")
    def admin_delete_api_key(key_id: int):
        user = require_user()
        owner = None if user["role"] == "superadmin" else user["username"]
        try:
            delete_access_api_key(settings.db_path, key_id, owner_username=owner)
        except ValueError as exc:
            return jsonify({"error": str(exc)}), 404
        return jsonify({"deleted": True})

    @app.get("/admin/api/config")
    def admin_get_config():
        user = require_user()
        return jsonify(_config_for_session_user(config_manager, user))

    @app.get("/admin/api/config/export")
    def admin_export_config():
        user = require_user()
        config = _config_for_session_user(config_manager, user)
        payload = json.dumps(
            {"channels": config.get("channels", [])},
            ensure_ascii=False,
            indent=2,
        ) + "\n"
        return Response(
            payload,
            mimetype="application/json",
            headers={
                "Content-Disposition": 'attachment; filename="opencodex-channels-config.json"'
            },
        )

    @app.post("/admin/api/config/import")
    def admin_import_config():
        user = require_user()
        candidate = request.get_json(silent=True)
        if not isinstance(candidate, dict):
            return jsonify({"error": "request body must be a JSON object"}), 400

        imported_channels = candidate.get("channels")
        if not isinstance(imported_channels, list):
            return jsonify({"error": "channels must be a list"}), 400

        current = _config_for_session_user(config_manager, user)
        current_channels = current.get("channels", [])
        current_ids = {
            (
                str(channel.get("owner_username") or user["username"]).strip(),
                str(channel.get("id", "")).strip(),
            )
            for channel in current_channels
            if isinstance(channel, dict)
        }
        merged_channels = list(current_channels)
        skipped_ids: list[str] = []

        for channel in imported_channels:
            if not isinstance(channel, dict):
                merged_channels.append(channel)
                continue
            channel_id = str(channel.get("id", "")).strip()
            owner_username = (
                str(channel.get("owner_username") or user["username"]).strip()
                if user["role"] == "superadmin"
                else user["username"]
            )
            channel_key = (owner_username, channel_id)
            if channel_key in current_ids:
                skipped_ids.append(channel_id)
                continue
            current_ids.add(channel_key)
            merged_channels.append(channel)

        try:
            saved = config_manager.save(
                {**current, "channels": merged_channels},
                owner_username=None if user["role"] == "superadmin" else user["username"],
            )
        except ConfigError as exc:
            log_event(logger, "WARNING", "config import rejected", error=str(exc))
            return jsonify({"error": str(exc)}), 400
        except (OSError, sqlite3.DatabaseError) as exc:
            log_event(logger, "ERROR", "config import failed", error=str(exc))
            return jsonify({"error": f"failed to import config: {exc}"}), 500

        imported_count = len(merged_channels) - len(current_channels)
        log_event(
            logger,
            "INFO",
            "config imported",
            imported=imported_count,
            skipped=len(skipped_ids),
            channels=len(saved.get("channels", [])),
        )
        return jsonify(
            {
                "config": saved,
                "imported": imported_count,
                "skipped": len(skipped_ids),
                "skipped_ids": skipped_ids,
            }
        )

    @app.post("/admin/api/config")
    def admin_save_config():
        user = require_user()
        candidate = request.get_json(silent=True)
        if not isinstance(candidate, dict):
            return jsonify({"error": "request body must be a JSON object"}), 400
        try:
            saved = config_manager.save(
                candidate,
                owner_username=None if user["role"] == "superadmin" else user["username"],
            )
        except ConfigError as exc:
            log_event(logger, "WARNING", "config save rejected", error=str(exc))
            return jsonify({"error": str(exc)}), 400
        except (OSError, sqlite3.DatabaseError) as exc:
            log_event(logger, "ERROR", "config save failed", error=str(exc))
            return jsonify({"error": f"failed to save config: {exc}"}), 500
        log_event(logger, "INFO", "config saved", channels=len(saved.get("channels", [])))
        return jsonify(saved)

    @app.get("/admin/api/web-search")
    def admin_get_web_search():
        require_superadmin()
        return jsonify(read_web_search_config(settings.db_path))

    @app.post("/admin/api/web-search")
    def admin_save_web_search():
        require_superadmin()
        candidate = request.get_json(silent=True)
        if not isinstance(candidate, dict):
            return jsonify({"error": "request body must be a JSON object"}), 400
        try:
            saved = replace_web_search_config(settings.db_path, candidate)
        except ValueError as exc:
            return jsonify({"error": str(exc)}), 400
        except sqlite3.DatabaseError as exc:
            log_event(logger, "ERROR", "web search config save failed", error=str(exc))
            return jsonify({"error": f"failed to save web search config: {exc}"}), 500
        log_event(
            logger,
            "INFO",
            "web search config saved",
            keys=len(saved.get("keys", [])),
            enabled=saved.get("enabled"),
        )
        return jsonify(saved)

    @app.post("/admin/api/web-search/test-key")
    def admin_test_web_search_key():
        require_superadmin()
        started = time.time()
        body = request.get_json(silent=True)
        if not isinstance(body, dict):
            return jsonify({"error": "request body must be a JSON object"}), 400
        try:
            key_id = int(body.get("id"))
        except (TypeError, ValueError):
            return jsonify({"error": "id is required"}), 400
        query = str(body.get("query") or "OpenAI").strip() or "OpenAI"
        reserved = reserve_tavily_key_by_id(settings.db_path, key_id)
        if reserved is None:
            return jsonify({"error": "Web Search key is unavailable or has reached its usage limit"}), 400
        result = _web_search_provider_search(reserved, query)
        return jsonify(
            {
                "ok": result.get("ok") is True,
                "duration_ms": int((time.time() - started) * 1000),
                "key": {
                    "id": reserved["id"],
                    "provider": reserved["provider"],
                    "usage_count": reserved["usage_count"],
                    "usage_limit": reserved["usage_limit"],
                    "key_usage_limit": reserved["key_usage_limit"],
                },
                "result": redact(result),
                "config": read_web_search_config(settings.db_path),
            }
        )

    @app.get("/admin/api/logs")
    def admin_logs():
        user = require_user()
        filters = {
            key: request.args.get(key)
            for key in (
                "request_id",
                "model",
                "upstream_model",
                "channel_id",
                "owner_username",
                "api_key_id",
                "path",
                "status_code",
                "is_stream",
                "client_ip",
                "error",
                "request_status",
                "created_from",
                "created_to",
            )
            if request.args.get(key) not in (None, "")
        }
        scope_filters: dict[str, Any] = {}
        if user["role"] != "superadmin":
            filters["owner_username"] = user["username"]
            scope_filters["owner_username"] = user["username"]
        page = read_logs_page(
            settings.db_path,
            page=request.args.get("page", "1"),
            page_size=request.args.get("page_size", "50"),
            filters=filters,
        )
        page["filter_options"] = read_log_filter_options(settings.db_path, scope_filters)
        return jsonify(page)

    @app.get("/admin/api/logs/<int:log_id>")
    def admin_log_detail(log_id: int):
        user = require_user()
        filters: dict[str, Any] = {}
        if user["role"] != "superadmin":
            filters["owner_username"] = user["username"]
        log = read_log_by_id(settings.db_path, log_id, filters=filters)
        if log is None:
            return jsonify({"error": "log not found"}), 404
        return jsonify(log)

    @app.get("/admin/api/stats")
    def admin_stats():
        user = require_user()
        range_key = str(request.args.get("range") or "1h").strip()
        owner_username = None if user["role"] == "superadmin" else user["username"]
        return jsonify(
            read_stats(
                settings.db_path,
                range_key=range_key,
                start_ts=request.args.get("start"),
                end_ts=request.args.get("end"),
                owner_username=owner_username,
            )
        )

    @app.post("/admin/api/channels/discover-models")
    @app.post("/admin/api/discover-models")
    def admin_discover_models():
        require_user()
        started = time.time()
        try:
            body = request.get_json(silent=True)
            if not isinstance(body, dict):
                return jsonify({"error": "request body must be a JSON object"}), 400
            channel = _draft_channel_from_body(body, settings.default_timeout)
            raw = list_upstream_models(
                channel,
                settings.default_timeout,
            )
        except ConfigError as exc:
            return jsonify({"error": str(exc)}), 400
        except ProxyError as exc:
            return jsonify(
                {
                    "error": str(exc),
                    "status_code": exc.status_code,
                    "duration_ms": int((time.time() - started) * 1000),
                    "body": redact(getattr(exc, "body", None)),
                }
            ), 502
        return jsonify(
            {
                "models": _extract_model_ids(raw),
                "raw": redact(raw),
                "duration_ms": int((time.time() - started) * 1000),
            }
        )

    @app.post("/admin/api/channels/test")
    @app.post("/admin/api/test-channel")
    def admin_test_channel():
        require_user()
        started = time.time()
        try:
            body = request.get_json(silent=True)
            if not isinstance(body, dict):
                return jsonify({"error": "request body must be a JSON object"}), 400
            channel, payload = _parse_test_channel_body(body, settings.default_timeout)
            original_model, upstream_model = _test_models(channel, payload.get("model"))
            upstream_request = convert_request(
                payload,
                channel["type"],
                channel["type"],
                upstream_model,
            )
            upstream_request, compat_details = apply_compat(
                upstream_request,
                _compat_rules(channel.get("compat", {})),
            )
            upstream_response = post_upstream(
                channel,
                upstream_request,
                settings.default_timeout,
            )
            response_payload = convert_response(
                upstream_response,
                channel["type"],
                channel["type"],
                original_model,
            )
        except ConfigError as exc:
            return jsonify({"error": str(exc)}), 400
        except ProxyError as exc:
            return jsonify(
                {
                    "ok": False,
                    "status_code": exc.status_code,
                    "duration_ms": int((time.time() - started) * 1000),
                    "error": str(exc),
                    "body": redact(getattr(exc, "body", None)),
                }
            )
        return jsonify(
            {
                "ok": True,
                "duration_ms": int((time.time() - started) * 1000),
                "model": original_model,
                "upstream_model": upstream_model,
                "compat": compat_details,
                "response": redact(response_payload),
            }
        )

    @app.post("/v1/responses")
    @app.post("/v1/chat/completions")
    @app.post("/v1/messages")
    def proxy():
        request_method = request.method
        request_path = request.path
        request_headers = json.dumps(redact(dict(request.headers)), ensure_ascii=False)
        client_ip = request.remote_addr
        entry_protocol = ENTRY_PROTOCOLS[request_path]
        started = time.time()
        request_id = uuid.uuid4().hex[:12]
        status_code = 200
        ttft_ms = None
        stream_requested = False
        defer_db_write = False
        payload: dict[str, Any] | None = None
        trace_payload: Any = None
        route = None
        channel: dict[str, Any] | None = None
        upstream_response: dict[str, Any] | None = None
        web_search_details: dict[str, Any] | None = None
        access_key: dict[str, Any] | None = None
        request_user: dict[str, Any] | None = None
        owner_username = settings.admin_username
        api_key_id: int | None = None
        log_payload: dict[str, Any] = {
            "request_id": request_id,
            "path": request_path,
            "entry_protocol": entry_protocol,
        }

        def write_db_record(
            *,
            record_status_code: int,
            record_duration_ms: int,
            record_ttft_ms: int | None,
            response_body: dict[str, Any] | None,
            response_protocol: str | None,
            error: str | None,
        ) -> None:
            db_record = {
                "request_id": request_id,
                "created_at": started,
                "method": request_method,
                "path": request_path,
                "client_ip": client_ip,
                "request_headers": request_headers,
                "request_body": json.dumps(trace_payload, ensure_ascii=False)
                if trace_payload is not None
                else None,
                "model": payload.get("model") if isinstance(payload, dict) else None,
                "upstream_model": route.upstream_model if route is not None else None,
                "channel_id": channel.get("id") if channel is not None else None,
                "is_stream": 1 if stream_requested else 0,
                "ttft_ms": record_ttft_ms,
                "duration_ms": record_duration_ms,
                "status_code": record_status_code,
                "response_body": None,
                "input_tokens": 0,
                "cached_tokens": 0,
                "output_tokens": 0,
                "cost": 0.0,
                "web_search_json": json.dumps(redact(web_search_details), ensure_ascii=False)
                if web_search_details is not None
                else None,
                "owner_username": owner_username,
                "api_key_id": api_key_id,
                "error": error,
            }

            if response_body is not None:
                if response_protocol is not None:
                    usage = extract_usage(response_body, response_protocol)
                    db_record.update(usage)
                    db_record["cost"] = _calculate_record_cost(db_record, usage, response_body)
                db_record["response_body"] = json.dumps(redact(response_body), ensure_ascii=False)

            db_writer.write(db_record)

        try:
            access_key = _authenticate_proxy_access_key(settings.db_path)
            request_user = access_key["user"]
            owner_username = request_user["username"]
            api_key_id = access_key["id"]
            log_payload["owner_username"] = owner_username
            log_payload["api_key_id"] = api_key_id

            payload = request.get_json(silent=True)
            if not isinstance(payload, dict):
                raise BadRequestError("request body must be a JSON object")
            stream_requested = payload.get("stream") is True
            log_payload["model"] = payload.get("model")
            log_payload["params"] = _visible_params(payload)
            trace_payload = redact(payload)
            cache_namespace = _request_cache_namespace(payload, request_id)

            route = choose_channel(
                config_manager.expanded_for_user(owner_username),
                payload.get("model"),
            )
            channel = route.channel
            log_payload.update(
                {
                    "channel_id": channel.get("id"),
                    "upstream_model": route.upstream_model,
                }
            )
            same_protocol = entry_protocol == channel["type"]
            if stream_requested and entry_protocol != "responses" and not same_protocol:
                raise BadRequestError("stream=true is only supported for /v1/responses")

            if same_protocol:
                upstream_request = deepcopy(payload)
                upstream_request["model"] = route.upstream_model
            else:
                upstream_request = convert_request(
                    payload, entry_protocol, channel["type"], route.upstream_model
                )
            upstream_request, compat_details = apply_compat(
                upstream_request,
                _compat_rules(channel.get("compat", {})),
            )
            if channel["type"] == "chat" and not same_protocol:
                injected_reasoning = reasoning_cache.inject_chat_request(
                    upstream_request, cache_namespace
                )
                if injected_reasoning:
                    compat_details.extend(
                        f"inject_reasoning_content:{tool_call_id}"
                        for tool_call_id in injected_reasoning
                    )
                if _compat_flag(
                    channel.get("compat", {}),
                    "fallback_thinking_on_tool_use",
                ):
                    fallback_reasoning = _inject_fallback_reasoning_content_on_tool_calls(
                        upstream_request
                    )
                    if fallback_reasoning:
                        compat_details.extend(
                            f"fallback_reasoning_content:{tool_call_id}"
                            for tool_call_id in fallback_reasoning
                        )
            if channel["type"] == "messages" and not same_protocol:
                injected_thinking = reasoning_cache.inject_messages_request(
                    upstream_request, cache_namespace
                )
                if injected_thinking:
                    compat_details.extend(
                        f"inject_thinking:{tool_call_id}"
                        for tool_call_id in injected_thinking
                    )
                if _compat_flag(
                    channel.get("compat", {}),
                    "fallback_thinking_on_tool_use",
                ):
                    fallback_thinking = _inject_fallback_thinking_on_tool_use(upstream_request)
                    if fallback_thinking:
                        compat_details.extend(
                            f"fallback_thinking:{tool_call_id}"
                            for tool_call_id in fallback_thinking
                        )
            log_payload["compat"] = compat_details
            log_payload["request_body"] = trace_payload
            log_payload["upstream_request_body"] = redact(upstream_request)
            can_simulate_web_search = (
                entry_protocol == "responses"
                and channel["type"] in {"chat", "messages"}
                and request_user is not None
                and request_user.get("role") == "superadmin"
                and request_declares_web_search(payload)
                and read_web_search_config(settings.db_path).get("enabled") is True
            )

            if can_simulate_web_search and not stream_requested:
                web_search_details: dict[str, Any] = {}
                # ── non-streaming path: block until complete ──
                try:
                    intermediate_rounds, final_upstream_request, final_nonstream_response, details = _run_web_search_simulation(
                        channel=channel,
                        upstream_request=upstream_request,
                        payload=payload,
                        original_model=route.original_model,
                        default_timeout=settings.default_timeout,
                        db_path=settings.db_path,
                    )
                    web_search_details = details
                except WebSearchSimulationUpstreamError as exc:
                    web_search_details = exc.details
                    log_payload["web_search"] = redact(web_search_details)
                    raise exc.proxy_error from exc
                log_payload["web_search"] = redact(web_search_details)
                log_payload["upstream_response_body"] = redact(final_nonstream_response) if final_nonstream_response is not None else None
                if final_nonstream_response is not None:
                    if channel["type"] == "chat":
                        reasoning_cache.remember_chat_response(final_nonstream_response, cache_namespace)
                    if channel["type"] == "messages":
                        reasoning_cache.remember_messages_response(final_nonstream_response, cache_namespace)
                response_payload = _build_web_search_final_response(
                    intermediate_rounds,
                    final_nonstream_response,
                    route.original_model,
                    channel["type"],
                )
                return jsonify(response_payload)

            if same_protocol and stream_requested:
                log_payload["upstream_request_body"] = redact(upstream_request)
                log_payload["streaming"] = True
                ttft_start = time.time()
                upstream_lines = stream_upstream(
                    channel,
                    upstream_request,
                    settings.default_timeout,
                )
                streamed_line_seen = False

                def stream_passthrough():
                    nonlocal streamed_line_seen, ttft_ms
                    stream_error = None
                    try:
                        for line in upstream_lines:
                            if not streamed_line_seen and line.strip():
                                streamed_line_seen = True
                                ttft_ms = int((time.time() - ttft_start) * 1000)
                            yield line
                    except Exception as exc:
                        stream_error = str(exc)
                        raise
                    finally:
                        write_db_record(
                            record_status_code=200,
                            record_duration_ms=int((time.time() - started) * 1000),
                            record_ttft_ms=ttft_ms,
                            response_body=None,
                            response_protocol=None,
                            error=stream_error,
                        )

                defer_db_write = True
                return Response(
                    stream_with_context(stream_passthrough()),
                    mimetype="text/event-stream",
                    headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
                )

            if stream_requested and channel["type"] in {"messages", "chat"}:
                upstream_request["stream"] = True
                log_payload["upstream_request_body"] = redact(upstream_request)
                log_payload["streaming"] = True
                ttft_start = time.time()
                upstream_lines = stream_upstream(
                    channel,
                    upstream_request,
                    settings.default_timeout,
                )
                ttft_recorded = False
                streamed_upstream_response = None

                def stream_with_ttft():
                    nonlocal ttft_recorded, streamed_upstream_response, ttft_ms, web_search_details
                    stream_error = None

                    def remember_streamed_response(response: dict[str, Any]) -> None:
                        nonlocal streamed_upstream_response
                        streamed_upstream_response = response
                        if channel["type"] == "messages":
                            reasoning_cache.remember_messages_response(response, cache_namespace)
                        if channel["type"] == "chat":
                            reasoning_cache.remember_chat_response(response, cache_namespace)

                    def record_ttft(line: str) -> None:
                        nonlocal ttft_recorded, ttft_ms
                        if not ttft_recorded and _counts_for_ttft(line):
                            ttft_recorded = True
                            ttft_ms = int((time.time() - ttft_start) * 1000)

                    def first_stream_events() -> Iterable[str]:
                        if channel["type"] == "messages":
                            return messages_sse_to_responses_events(
                                upstream_lines,
                                route.original_model or payload.get("model"),
                                remember_streamed_response,
                            )
                        return chat_sse_to_responses_events(
                            upstream_lines,
                            route.original_model or payload.get("model"),
                            remember_streamed_response,
                            skip_tool_names={"web_search"} if can_simulate_web_search else None,
                        )

                    try:
                        event_iter = first_stream_events()
                        if can_simulate_web_search:
                            pending_completed_line: str | None = None
                            prefix_output_items: dict[int, dict[str, Any]] = {}
                            next_output_index = 0
                            sequence_number_offset = 0
                            for line in event_iter:
                                record_ttft(line)
                                event_name, event_payload = _parse_sse_event(line)
                                if event_name == "response.completed":
                                    pending_completed_line = line
                                    continue
                                sequence_number_offset = max(
                                    sequence_number_offset,
                                    _next_sequence_number(event_payload),
                                )
                                next_output_index = max(
                                    next_output_index,
                                    _next_output_index(event_payload),
                                )
                                item_done = _output_item_done_payload(event_name, event_payload)
                                if item_done is not None:
                                    output_index, item = item_done
                                    prefix_output_items[output_index] = item
                                yield line

                            tool_calls = (
                                extract_tool_calls(streamed_upstream_response, channel["type"])
                                if streamed_upstream_response is not None
                                else []
                            )
                            if web_search_calls(tool_calls):
                                web_search_details = {}
                                model_for_sse = route.original_model or payload.get("model")
                                prefix_items = [
                                    prefix_output_items[index]
                                    for index in sorted(prefix_output_items)
                                ]
                                for line in _web_search_simulation_stream(
                                    channel=channel,
                                    upstream_request=upstream_request,
                                    payload=payload,
                                    original_model=route.original_model,
                                    default_timeout=settings.default_timeout,
                                    db_path=settings.db_path,
                                    model_for_sse=model_for_sse,
                                    cache_namespace=cache_namespace,
                                    reasoning_cache=reasoning_cache,
                                    web_search_details=web_search_details,
                                    initial_upstream_response=streamed_upstream_response,
                                    initial_output_index=next_output_index,
                                    on_response=remember_streamed_response,
                                ):
                                    record_ttft(line)
                                    if line.startswith("event: response.completed\n"):
                                        line = _prepend_completed_output_items(line, prefix_items)
                                    line = _patch_sse_sequence_numbers(line, sequence_number_offset)
                                    yield line
                                return

                            if pending_completed_line is not None:
                                yield pending_completed_line
                            return

                        for line in event_iter:
                            record_ttft(line)
                            yield line
                    except Exception as exc:
                        stream_error = str(exc)
                        raise
                    finally:
                        if web_search_details is not None:
                            log_payload["web_search"] = redact(web_search_details)
                        write_db_record(
                            record_status_code=200,
                            record_duration_ms=int((time.time() - started) * 1000),
                            record_ttft_ms=ttft_ms,
                            response_body=streamed_upstream_response,
                            response_protocol=channel["type"],
                            error=stream_error,
                        )

                defer_db_write = True
                return Response(
                    stream_with_context(stream_with_ttft()),
                    mimetype="text/event-stream",
                    headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
                )

            if stream_requested:
                upstream_request["stream"] = False

            upstream_response = post_upstream(
                channel,
                upstream_request,
                settings.default_timeout,
            )
            if channel["type"] == "chat":
                reasoning_cache.remember_chat_response(upstream_response, cache_namespace)
            if channel["type"] == "messages":
                reasoning_cache.remember_messages_response(upstream_response, cache_namespace)
            log_payload["upstream_response_body"] = redact(upstream_response)
            response_payload = (
                deepcopy(upstream_response)
                if same_protocol
                else convert_response(
                    upstream_response,
                    entry_protocol,
                    channel["type"],
                    route.original_model,
                )
            )
            if stream_requested:
                def stream_synthesized_response():
                    nonlocal ttft_ms
                    stream_error = None
                    ttft_recorded = False
                    try:
                        for line in responses_sse_events(response_payload):
                            if not ttft_recorded and _counts_for_ttft(line):
                                ttft_recorded = True
                                ttft_ms = int((time.time() - started) * 1000)
                            yield line
                    except Exception as exc:
                        stream_error = str(exc)
                        raise
                    finally:
                        write_db_record(
                            record_status_code=200,
                            record_duration_ms=int((time.time() - started) * 1000),
                            record_ttft_ms=ttft_ms,
                            response_body=upstream_response,
                            response_protocol=channel["type"] if channel is not None else None,
                            error=stream_error,
                        )

                defer_db_write = True
                return Response(
                    stream_with_context(stream_synthesized_response()),
                    mimetype="text/event-stream",
                    headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
                )
            return jsonify(response_payload)
        except ProxyError as exc:
            status_code = exc.status_code
            body = exc.to_response()
            log_payload["error"] = str(exc)
            if hasattr(exc, "body"):
                log_payload["upstream_error"] = redact(getattr(exc, "body"))
            return jsonify(body), status_code
        except ConfigError as exc:
            status_code = 400
            log_payload["error"] = str(exc)
            return jsonify({"error": {"message": str(exc), "type": "config_error"}}), 400
        except Exception as exc:  # pragma: no cover - defensive logging
            status_code = 500
            log_payload["error"] = str(exc)
            logger.exception("unhandled proxy error", extra={"extra": log_payload})
            return jsonify({"error": {"message": "internal server error"}}), 500
        finally:
            duration_ms = int((time.time() - started) * 1000)
            log_payload["status_code"] = status_code
            log_payload["duration_ms"] = duration_ms
            level = "ERROR" if status_code >= 500 else "WARNING" if status_code >= 400 else "INFO"
            log_event(logger, level, "proxy request", **log_payload)

            if not defer_db_write:
                write_db_record(
                    record_status_code=status_code,
                    record_duration_ms=duration_ms,
                    record_ttft_ms=ttft_ms,
                    response_body=upstream_response,
                    response_protocol=channel["type"] if channel is not None else None,
                    error=log_payload.get("error"),
                )

    return app


def _set_session_user(user: dict[str, Any]) -> None:
    session["user"] = _session_user_payload(user)
    session["admin_authenticated"] = True


def _session_user_payload(user: dict[str, Any]) -> dict[str, Any]:
    return {
        "username": user["username"],
        "role": user["role"],
        "enabled": user.get("enabled", True) is not False,
    }


def current_session_user() -> dict[str, Any] | None:
    user = session.get("user")
    if isinstance(user, dict) and user.get("username") and user.get("role"):
        return {
            "username": str(user.get("username")),
            "role": str(user.get("role")),
            "enabled": user.get("enabled", True) is not False,
        }
    if session.get("admin_authenticated"):
        settings = current_app.config.get("OPENCODEX_SETTINGS")
        if isinstance(settings, Settings):
            return {
                "username": settings.admin_username,
                "role": "superadmin",
                "enabled": True,
            }
    return None


def require_user() -> dict[str, Any]:
    user = current_session_user()
    if user is None:
        raise BadRequestError("admin authentication required", status_code=401)
    settings = current_app.config.get("OPENCODEX_SETTINGS")
    if isinstance(settings, Settings):
        stored_user = get_user(settings.db_path, user["username"])
        if stored_user is None or stored_user.get("enabled") is False:
            session.clear()
            raise BadRequestError("admin authentication required", status_code=401)
        _set_session_user(stored_user)
        return stored_user
    return user


def require_superadmin() -> dict[str, Any]:
    user = require_user()
    if user.get("role") != "superadmin":
        raise BadRequestError("superadmin required", status_code=403)
    return user


def _config_for_session_user(
    config_manager: ConfigManager,
    user: dict[str, Any],
) -> dict[str, Any]:
    if user.get("role") == "superadmin":
        return config_manager.raw
    return config_manager.raw_for_user(user["username"])


def _authenticate_proxy_access_key(db_path: Path) -> dict[str, Any]:
    token = _bearer_token(request.headers.get("Authorization"))
    if token is None:
        raise BadRequestError("valid bearer api key required", status_code=401)
    access_key = authenticate_access_api_key(db_path, token)
    if access_key is None:
        raise BadRequestError("valid bearer api key required", status_code=401)
    return access_key


def _bearer_token(authorization: str | None) -> str | None:
    value = str(authorization or "").strip()
    if not value.lower().startswith("bearer "):
        return None
    token = value.split(" ", 1)[1].strip()
    return token or None


def _spa_index_response(admin_static_dir: Path) -> Response:
    return Response(
        (admin_static_dir / "index.html").read_text(encoding="utf-8"),
        mimetype="text/html",
    )


def _web_search_provider_search(reserved_key: dict[str, Any], query: str) -> dict[str, Any]:
    provider = str(reserved_key.get("provider") or "tavily").strip().lower()
    if provider == "tavily":
        return tavily_search(reserved_key["key"], query)
    return {
        "ok": False,
        "status_code": None,
        "duration_ms": 0,
        "error_type": "unsupported_provider",
        "summary": {
            "answer": "",
            "results": [],
            "error": f"unsupported web search provider: {provider}",
        },
        "raw": None,
    }


def _run_web_search_simulation(
    *,
    channel: dict[str, Any],
    upstream_request: dict[str, Any],
    payload: dict[str, Any],
    original_model: str | None,
    default_timeout: int,
    db_path: Path,
) -> tuple[list[dict[str, Any]], dict[str, Any], dict[str, Any] | None, dict[str, Any]]:
    protocol = channel["type"]
    request_payload = deepcopy(upstream_request)
    request_payload["stream"] = False
    web_results: list[dict[str, Any]] = []
    upstream_calls: list[dict[str, Any]] = []
    intermediate_rounds: list[dict[str, Any]] = []
    web_limit = max_web_search_calls(payload)
    web_executed = 0
    max_iterations = max(web_limit + 3, 3)
    upstream_response: dict[str, Any] = {}

    for iteration in range(max_iterations):
        upstream_response = _web_search_post_upstream(
            channel=channel,
            request_payload=request_payload,
            default_timeout=default_timeout,
            web_results=web_results,
            upstream_calls=upstream_calls,
        )
        tool_calls = extract_tool_calls(upstream_response, protocol)
        web_calls = web_search_calls(tool_calls)
        other_calls = non_web_search_calls(tool_calls)
        upstream_calls.append(
            {
                "iteration": iteration + 1,
                "tool_calls": [
                    {
                        "id": tool_call.get("id"),
                        "name": tool_call.get("name"),
                    }
                    for tool_call in tool_calls
                ],
            }
        )

        if not web_calls:
            response_payload = convert_response(
                upstream_response,
                "responses",
                protocol,
                original_model,
            )
            if web_results:
                response_payload = prepend_web_search_items(
                    response_payload,
                    web_results,
                    include_result=False,
                )
                response_payload = add_source_annotations(response_payload, web_results)
            return intermediate_rounds, request_payload, upstream_response, web_search_log(web_results, upstream_calls)

        current_results: list[dict[str, Any]] = []
        current_requires_final_answer = False
        for tool_call in web_calls:
            if web_executed >= web_limit:
                result = make_tool_result(
                    call_id=str(tool_call.get("id")),
                    query="",
                    status="failed",
                    error="已达到 web_search 调用上限，不能继续搜索",
                )
                current_results.append(result)
                web_results.append(result)
                current_requires_final_answer = True
                continue

            query, parse_error = parse_web_search_query(tool_call.get("arguments"))
            if parse_error is not None:
                result = make_tool_result(
                    call_id=str(tool_call.get("id")),
                    query=query,
                    status="failed",
                    error=parse_error,
                )
                current_results.append(result)
                web_results.append(result)
                continue

            reserved = reserve_tavily_key(db_path)
            if reserved is None:
                result = make_tool_result(
                    call_id=str(tool_call.get("id")),
                    query=query,
                    status="failed",
                    error="搜索不可用",
                )
                current_results.append(result)
                web_results.append(result)
                current_requires_final_answer = True
                continue

            web_executed += 1
            tavily_result = _web_search_provider_search(reserved, query or "")
            summary = tavily_result.get("summary") or {}
            result = make_tool_result(
                call_id=str(tool_call.get("id")),
                query=query,
                status="completed" if tavily_result.get("ok") is True else "failed",
                answer=str(summary.get("answer") or ""),
                results=summary.get("results") if isinstance(summary.get("results"), list) else [],
                error=None if tavily_result.get("ok") is True else "搜索不可用",
                log_error=summary.get("error"),
                error_type=tavily_result.get("error_type"),
                http_status=tavily_result.get("status_code"),
                key=reserved,
                raw=tavily_result.get("raw") if isinstance(tavily_result, dict) else None,
            )
            current_results.append(result)
            web_results.append(result)
            if tavily_result.get("ok") is not True:
                current_requires_final_answer = True

        if other_calls:
            intermediate_rounds.append({
                "iteration": iteration + 1,
                "upstream_response": upstream_response,
                "current_web_results": list(current_results),
                "all_web_results": list(web_results),
                "queries": [
                    result.get("query") for result in current_results if result.get("query")
                ],
            })
            return intermediate_rounds, request_payload, upstream_response, web_search_log(web_results, upstream_calls)

        intermediate_rounds.append({
            "iteration": iteration + 1,
            "upstream_response": upstream_response,
            "current_web_results": list(current_results),
            "all_web_results": list(web_results),
            "queries": [
                result.get("query") for result in current_results if result.get("query")
            ],
        })

        request_payload = append_tool_results(
            request_payload,
            upstream_response,
            protocol,
            current_results,
        )
        if current_requires_final_answer:
            next_response = _web_search_post_upstream(
                channel=channel,
                request_payload=request_payload,
                default_timeout=default_timeout,
                web_results=web_results,
                upstream_calls=upstream_calls,
            )
            upstream_response = next_response
            upstream_calls.append(
                {
                    "iteration": iteration + 2,
                    "after_limit": True,
                    "tool_calls": [
                        {"id": item.get("id"), "name": item.get("name")}
                        for item in extract_tool_calls(next_response, protocol)
                    ],
                }
            )
            response_payload = convert_response(
                next_response,
                "responses",
                protocol,
                original_model,
            )
            response_payload = prepend_web_search_items(
                response_payload,
                web_results,
                include_result=False,
            )
            response_payload = add_source_annotations(response_payload, web_results)
            return intermediate_rounds, request_payload, upstream_response, web_search_log(web_results, upstream_calls)


    response_payload = convert_response(
        upstream_response,
        "responses",
        protocol,
        original_model,
    )
    response_payload = prepend_web_search_items(
        response_payload,
        web_results,
        include_result=False,
    )
    response_payload = add_source_annotations(response_payload, web_results)
    details = web_search_log(web_results, upstream_calls)
    details["error"] = "web_search simulation stopped after iteration guard"
    return intermediate_rounds, request_payload, upstream_response, details


class WebSearchSimulationUpstreamError(Exception):
    def __init__(self, proxy_error: ProxyError, details: dict[str, Any]) -> None:
        super().__init__(str(proxy_error))
        self.proxy_error = proxy_error
        self.details = details


def _web_search_post_upstream(
    *,
    channel: dict[str, Any],
    request_payload: dict[str, Any],
    default_timeout: int,
    web_results: list[dict[str, Any]],
    upstream_calls: list[dict[str, Any]],
) -> dict[str, Any]:
    try:
        return post_upstream(
            channel,
            request_payload,
            default_timeout,
        )
    except ProxyError as exc:
        details = web_search_log(web_results, upstream_calls)
        details["upstream_error"] = str(exc)
        raise WebSearchSimulationUpstreamError(exc, details) from exc


def _draft_channel_from_request(default_timeout: int) -> dict[str, Any]:
    body = request.get_json(silent=True)
    if not isinstance(body, dict):
        raise ConfigError("request body must be a JSON object")
    channel = body.get("channel")
    if not isinstance(channel, dict):
        raise ConfigError("channel must be a JSON object")
    normalized = normalize_config({"channels": [channel]})
    expanded_channel = expand_env(normalized)["channels"][0]
    validate_channel(expanded_channel, default_timeout)
    return expanded_channel

_CHANNEL_KEYS = frozenset({
    "id", "name", "type", "baseurl", "apikey", "auth_mode",
    "headers", "timeout_seconds", "retry_count", "compat",
    "models", "enabled",
})


def _draft_channel_from_body(body: dict[str, Any], default_timeout: int) -> dict[str, Any]:
    channel = body.get("channel")
    if isinstance(channel, dict):
        pass
    elif "baseurl" in body or "type" in body:
        channel = {k: v for k, v in body.items() if k in _CHANNEL_KEYS}
    else:
        raise ConfigError("channel must be a JSON object")
    normalized = normalize_config({"channels": [channel]})
    expanded_channel = expand_env(normalized)["channels"][0]
    validate_channel(expanded_channel, default_timeout)
    return expanded_channel


def _parse_test_channel_body(body: dict[str, Any], default_timeout: int) -> tuple[dict[str, Any], dict[str, Any]]:
    payload = body.get("payload")
    if isinstance(payload, dict):
        channel = _draft_channel_from_body(body, default_timeout)
    elif "baseurl" in body or "type" in body:
        channel = _draft_channel_from_body(body, default_timeout)
        payload = _build_payload_from_flat(body, channel["type"])
    else:
        raise ConfigError("payload must be a JSON object or a flat channel+payload body is required")
    return channel, payload


def _build_payload_from_flat(body: dict[str, Any], channel_type: str) -> dict[str, Any]:
    model = str(body.get("model") or "").strip()
    input_text = str(body.get("input") or "ping").strip()
    max_output_tokens = int(body.get("max_output_tokens") or 256)
    if channel_type == "chat":
        return {
            "model": model,
            "messages": [{"role": "user", "content": input_text}],
            "max_tokens": max_output_tokens,
        }
    elif channel_type == "messages":
        return {
            "model": model,
            "messages": [{"role": "user", "content": input_text}],
            "max_tokens": max_output_tokens,
        }
    else:  # responses
        return {
            "model": model,
            "input": input_text,
            "max_output_tokens": max_output_tokens,
        }


def _extract_model_ids(payload: dict[str, Any]) -> list[str]:
    data = payload.get("data")
    if not isinstance(data, list):
        return []
    ids: list[str] = []
    seen: set[str] = set()
    for item in data:
        if not isinstance(item, dict):
            continue
        model_id = str(item.get("id", "")).strip()
        if model_id and model_id not in seen:
            seen.add(model_id)
            ids.append(model_id)
    return ids


def _test_models(channel: dict[str, Any], model: Any) -> tuple[str, str]:
    original_model = str(model or "").strip()
    for mapping in channel.get("models", []):
        if not isinstance(mapping, dict):
            continue
        if mapping.get("model") == original_model:
            return original_model, str(mapping.get("upstream_model") or original_model)
    return original_model, original_model


def _visible_params(payload: dict[str, Any]) -> dict[str, Any]:
    hidden = {"messages", "input", "instructions", "system", "tools"}
    return {key: redact(value) for key, value in payload.items() if key not in hidden}


def _parse_sse_event(line: str) -> tuple[str | None, dict[str, Any] | None]:
    event_name: str | None = None
    data_lines: list[str] = []
    for raw_line in line.splitlines():
        if raw_line.startswith("event:"):
            event_name = raw_line.split(":", 1)[1].strip()
        elif raw_line.startswith("data:"):
            data_lines.append(raw_line.split(":", 1)[1].lstrip())
    if not data_lines:
        return event_name, None
    try:
        payload = json.loads("\n".join(data_lines))
    except json.JSONDecodeError:
        return event_name, None
    return event_name, payload if isinstance(payload, dict) else None


def _next_sequence_number(payload: dict[str, Any] | None) -> int:
    if not isinstance(payload, dict):
        return 0
    sequence_number = payload.get("sequence_number")
    if isinstance(sequence_number, int):
        return sequence_number + 1
    return 0


def _next_output_index(payload: dict[str, Any] | None) -> int:
    max_index = _max_output_index(payload)
    return max_index + 1 if max_index is not None else 0


def _max_output_index(obj: Any) -> int | None:
    if isinstance(obj, dict):
        result: int | None = None
        value = obj.get("output_index")
        if isinstance(value, int):
            result = value
        for child in obj.values():
            child_index = _max_output_index(child)
            if child_index is not None:
                result = child_index if result is None else max(result, child_index)
        return result
    if isinstance(obj, list):
        result: int | None = None
        for item in obj:
            child_index = _max_output_index(item)
            if child_index is not None:
                result = child_index if result is None else max(result, child_index)
        return result
    return None


def _output_item_done_payload(
    event_name: str | None,
    payload: dict[str, Any] | None,
) -> tuple[int, dict[str, Any]] | None:
    if event_name != "response.output_item.done" or not isinstance(payload, dict):
        return None
    output_index = payload.get("output_index")
    item = payload.get("item")
    if not isinstance(output_index, int) or not isinstance(item, dict):
        return None
    return output_index, item


def _patch_sse_sequence_numbers(line: str, offset: int) -> str:
    if offset <= 0:
        return line
    event_name, payload = _parse_sse_event(line)
    if event_name is None or payload is None:
        return line
    sequence_number = payload.get("sequence_number")
    if not isinstance(sequence_number, int):
        return line
    payload["sequence_number"] = sequence_number + offset
    new_data = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    return f"event: {event_name}\ndata: {new_data}\n\n"


def _prepend_completed_output_items(
    line: str,
    prefix_items: list[dict[str, Any]],
) -> str:
    if not prefix_items or not line.startswith("event: response.completed\n"):
        return line
    event_name, payload = _parse_sse_event(line)
    if event_name != "response.completed" or payload is None:
        return line
    response = payload.get("response")
    if not isinstance(response, dict):
        return line
    output = response.get("output")
    response["output"] = [*prefix_items, *(output if isinstance(output, list) else [])]
    payload["response"] = response
    new_data = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    return f"event: response.completed\ndata: {new_data}\n\n"


def _add_usage_totals(
    totals: dict[str, int],
    response: dict[str, Any],
    protocol: str,
) -> None:
    usage = extract_usage(response, protocol)
    totals["input_tokens"] += int(usage.get("input_tokens") or 0)
    totals["cached_tokens"] += int(usage.get("cached_tokens") or 0)
    totals["output_tokens"] += int(usage.get("output_tokens") or 0)


def _usage_totals_for_protocol(
    totals: dict[str, int],
    protocol: str,
) -> dict[str, Any]:
    input_tokens = int(totals.get("input_tokens") or 0)
    cached_tokens = int(totals.get("cached_tokens") or 0)
    output_tokens = int(totals.get("output_tokens") or 0)
    if protocol == "chat":
        usage: dict[str, Any] = {
            "prompt_tokens": input_tokens,
            "completion_tokens": output_tokens,
            "total_tokens": input_tokens + output_tokens,
        }
        if cached_tokens:
            usage["prompt_tokens_details"] = {"cached_tokens": cached_tokens}
        return usage
    if protocol == "messages":
        usage = {
            "input_tokens": input_tokens,
            "output_tokens": output_tokens,
        }
        if cached_tokens:
            usage["cache_read_input_tokens"] = cached_tokens
        return usage
    usage = {
        "input_tokens": input_tokens,
        "output_tokens": output_tokens,
        "total_tokens": input_tokens + output_tokens,
    }
    if cached_tokens:
        usage["input_tokens_details"] = {"cached_tokens": cached_tokens}
    return usage


def _response_with_usage_totals(
    response: dict[str, Any],
    protocol: str,
    totals: dict[str, int],
) -> dict[str, Any]:
    patched = deepcopy(response)
    patched["usage"] = _usage_totals_for_protocol(totals, protocol)
    return patched


def _patch_completed_usage(
    line: str,
    totals: dict[str, int],
) -> str:
    if not line.startswith("event: response.completed\n"):
        return line
    event_name, payload = _parse_sse_event(line)
    if event_name != "response.completed" or payload is None:
        return line
    response = payload.get("response")
    if not isinstance(response, dict):
        return line
    response["usage"] = _usage_totals_for_protocol(totals, "responses")
    payload["response"] = response
    new_data = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    return f"event: response.completed\ndata: {new_data}\n\n"


def _patch_sse_output_indices(line: str, offset: int) -> str:
    """Add *offset* to every output_index field in an SSE data payload."""
    if offset <= 0:
        return line
    if '\n' not in line:
        return line
    event_part, data_str = line.split('\n', 1)
    if not data_str.startswith('data: '):
        return line
    json_text = data_str[len('data: '):]
    try:
        payload = json.loads(json_text)
    except json.JSONDecodeError:
        return line
    _add_offset(payload, offset)
    new_data = json.dumps(payload, ensure_ascii=False, separators=(',', ':'))
    return f"{event_part}\ndata: {new_data}\n\n"


def _inject_web_search_into_completed_sse(
    line: str,
    web_results: list[dict[str, Any]],
    offset: int,
) -> str:
    """Modify a response.completed SSE line so its output array includes
    web_search_call items (at the front) and indices match the stream."""
    if not line.startswith('event: response.completed\n'):
        return line
    _, data_str = line.split('\n', 1)
    if not data_str.startswith('data: '):
        return line
    json_text = data_str[len('data: '):]
    try:
        payload = json.loads(json_text)
    except json.JSONDecodeError:
        return line
    response = payload.get('response', {})
    output = list(response.get('output', []) or [])
    web_items = [build_web_search_item(r, include_result=True) for r in web_results]
    merged = [*web_items, *output]
    response['output'] = merged
    payload['response'] = response
    # Re-apply offset since rebuild_would reset indices in completed payload
    _add_offset(payload, offset)
    new_data = json.dumps(payload, ensure_ascii=False, separators=(',', ':'))
    return f"event: response.completed\ndata: {new_data}\n\n"


def _add_offset(obj: Any, offset: int) -> None:
    """Recursively add *offset* to every 'output_index' integer in *obj*."""
    if isinstance(obj, dict):
        for key, value in obj.items():
            if key == 'output_index' and isinstance(value, int):
                obj[key] = value + offset
            else:
                _add_offset(value, offset)
    elif isinstance(obj, list):
        for item in obj:
            _add_offset(item, offset)



def _counts_for_ttft(sse_line: str) -> bool:
    if sse_line.startswith("event: patch.semantic_preview\n"):
        event_name, payload = _parse_sse_event(sse_line)
        return (
            event_name == "patch.semantic_preview"
            and isinstance(payload, dict)
            and payload.get("event") == "file_started"
        )
    return any(
        event_name in sse_line
        for event_name in (
            "response.output_text.delta",
            "response.reasoning_summary_text.delta",
            "response.function_call_arguments.delta",
            "response.output_item.done",
        )
    )


def _calculate_record_cost(
    db_record: dict[str, Any],
    usage: dict[str, int],
    response_body: dict[str, Any],
) -> float:
    # 优先用 upstream_model 算成本（反映真实支出），再回落到请求模型
    for model in (
        db_record.get("upstream_model"),
        db_record.get("model"),
        response_body.get("model"),
    ):
        cost = calculate_cost(
            str(model or ""),
            usage["input_tokens"],
            usage["cached_tokens"],
            usage["output_tokens"],
        )
        if cost:
            return cost
    return 0.0


def _request_cache_namespace(payload: dict[str, Any], request_id: str) -> str:
    direct = _first_namespace_value(payload, DIRECT_CACHE_NAMESPACE_KEYS)
    if direct:
        return direct

    for metadata_key in ("metadata", "client_metadata"):
        metadata = payload.get(metadata_key)
        if not isinstance(metadata, dict):
            continue
        nested = _first_namespace_value(metadata, METADATA_CACHE_NAMESPACE_KEYS)
        if nested:
            return f"{metadata_key}:{nested}"

    prompt_cache_key = _namespace_value(payload.get("prompt_cache_key"))
    if prompt_cache_key:
        return f"prompt_cache_key:{prompt_cache_key}"

    return f"request:{request_id}"


def _first_namespace_value(payload: dict[str, Any], keys: tuple[str, ...]) -> str | None:
    for key in keys:
        value = _namespace_value(payload.get(key))
        if value:
            return f"{key}:{value}"
    return None


def _namespace_value(value: Any) -> str | None:
    if isinstance(value, (str, int, float)):
        text = str(value).strip()
        return text or None
    return None


def _compat_rules(compat: dict[str, Any] | None) -> dict[str, Any]:
    compat = compat or {}
    fields = ("rename_params", "drop_params", "force_params", "default_params", "unsupported_params")
    return {field: compat.get(field) for field in fields if field in compat}


def _compat_flag(compat: dict[str, Any] | None, field: str) -> bool:
    compat = compat or {}
    return compat.get(field) is True


def _inject_fallback_reasoning_content_on_tool_calls(
    request_payload: dict[str, Any],
) -> list[str]:
    messages = request_payload.get("messages")
    if not isinstance(messages, list):
        return []
    injected: list[str] = []
    for message in messages:
        if not isinstance(message, dict) or message.get("role") != "assistant":
            continue
        if message.get("reasoning_content"):
            continue
        tool_calls = message.get("tool_calls")
        if not isinstance(tool_calls, list) or not tool_calls:
            continue
        tool_call_ids = [
            str(tool_call.get("id") or "").strip()
            for tool_call in tool_calls
            if isinstance(tool_call, dict)
        ]
        tool_call_ids = [tool_call_id for tool_call_id in tool_call_ids if tool_call_id]
        if not tool_call_ids:
            continue
        message["reasoning_content"] = FALLBACK_REASONING_CONTENT
        injected.extend(tool_call_ids)
    return injected


def _inject_fallback_thinking_on_tool_use(request_payload: dict[str, Any]) -> list[str]:
    messages = request_payload.get("messages")
    if not isinstance(messages, list):
        return []
    injected: list[str] = []
    for message in messages:
        if not isinstance(message, dict) or message.get("role") != "assistant":
            continue
        content = message.get("content")
        if not isinstance(content, list):
            continue
        if any(isinstance(block, dict) and block.get("type") == "thinking" for block in content):
            continue
        tool_use_ids = [
            str(block.get("id") or "").strip()
            for block in content
            if isinstance(block, dict) and block.get("type") == "tool_use"
        ]
        tool_use_ids = [tool_use_id for tool_use_id in tool_use_ids if tool_use_id]
        if not tool_use_ids:
            continue
        message["content"] = [
            {
                "type": "thinking",
                "thinking": FALLBACK_REASONING_CONTENT,
                "signature": "",
            },
            *content,
        ]
        injected.extend(tool_use_ids)
    return injected



def _web_search_simulation_stream(
    *,
    channel: dict[str, Any],
    upstream_request: dict[str, Any],
    payload: dict[str, Any],
    original_model: str | None,
    default_timeout: int,
    db_path: Path,
    model_for_sse: str | None,
    cache_namespace: str,
    reasoning_cache: ReasoningCache | None = None,
    web_search_details: dict[str, Any] | None = None,
    initial_upstream_response: dict[str, Any] | None = None,
    initial_output_index: int = 0,
    on_response: Callable[[dict[str, Any]], None] | None = None,
) -> Iterable[str]:
    """Generator: yields SSE events as web search simulation progresses.

    Unlike _run_web_search_simulation which collects everything and replays
    at the end, this generator emits web_search_call in_progress/completed
    items *as* each search happens, then streams the final model response
    from upstream.  This gives the client a typewriter-style experience
    instead of one big burst at the end.
    """
    protocol = channel["type"]
    request_payload = deepcopy(upstream_request)
    request_payload["stream"] = False
    web_results: list[dict[str, Any]] = []
    upstream_calls: list[dict[str, Any]] = []
    web_limit = max_web_search_calls(payload)
    web_executed = 0
    max_iterations = max(web_limit + 3, 3)
    sequence_number = 0
    next_output_index = max(0, int(initial_output_index or 0))
    usage_totals = {"input_tokens": 0, "cached_tokens": 0, "output_tokens": 0}
    pending_upstream_response = initial_upstream_response
    pending_usage_counted = False
    completed_prefix_output_items: dict[int, dict[str, Any]] = {}

    def emit(event: str, payload_data: dict[str, Any]) -> str:
        nonlocal sequence_number
        enriched = {"type": event, **payload_data, "sequence_number": sequence_number}
        sequence_number += 1
        return f"event: {event}\ndata: {json.dumps(enriched, ensure_ascii=False, separators=(',', ':'))}\n\n"

    def alloc_index() -> int:
        nonlocal next_output_index
        idx = next_output_index
        next_output_index += 1
        return idx

    for iteration in range(max_iterations):
        if pending_upstream_response is None:
            break
        upstream_response = pending_upstream_response
        usage_already_counted = pending_usage_counted
        pending_upstream_response = None
        pending_usage_counted = False
        if not usage_already_counted:
            _add_usage_totals(usage_totals, upstream_response, protocol)
        tool_calls = extract_tool_calls(upstream_response, protocol)
        web_calls = web_search_calls(tool_calls)
        other_calls = non_web_search_calls(tool_calls)
        upstream_calls.append({
            "iteration": iteration + 1,
            "tool_calls": [
                {"id": tc.get("id"), "name": tc.get("name")}
                for tc in tool_calls
            ],
        })

        if not web_calls:
            if web_search_details is not None:
                web_search_details.update(web_search_log(web_results, upstream_calls))
            response_payload = convert_response(
                upstream_response, "responses", protocol, original_model,
            )
            if web_results:
                response_payload = add_source_annotations(response_payload, web_results)
            if on_response is not None:
                on_response(_response_with_usage_totals(upstream_response, protocol, usage_totals))
            for line in responses_sse_events(response_payload, skip_response_created=True):
                line = _patch_sse_output_indices(line, next_output_index)
                if line.startswith("event: response.completed\n"):
                    line = _prepend_completed_output_items(
                        line,
                        [
                            completed_prefix_output_items[index]
                            for index in sorted(completed_prefix_output_items)
                        ],
                    )
                    line = _patch_completed_usage(line, usage_totals)
                line = _patch_sse_sequence_numbers(line, sequence_number)
                yield line
            return

        # Emit web_search_call items as each search happens
        current_results: list[dict[str, Any]] = []
        for tool_call in web_calls:
            call_id = str(tool_call.get("id"))
            item_idx = alloc_index()
            item_id = f"ws_{uuid.uuid4().hex}"

            query, parse_error = parse_web_search_query(tool_call.get("arguments"))

            # Emit in_progress so the client immediately shows "searching..."
            yield emit(
                "response.output_item.added",
                {
                    "output_index": item_idx,
                    "item": {
                        "id": item_id,
                        "type": "web_search_call",
                        "status": "in_progress",
                        "action": {"type": "search", "query": query or ""},
                    },
                },
            )

            # Execute search
            if parse_error is not None:
                result = make_tool_result(
                    call_id=call_id, query=query,
                    status="failed", error=parse_error,
                )
            elif web_executed >= web_limit:
                result = make_tool_result(
                    call_id=call_id, query="",
                    status="failed", error="已达到 web_search 调用上限，不能继续搜索",
                )
            else:
                reserved = reserve_tavily_key(db_path)
                if reserved is None:
                    result = make_tool_result(
                        call_id=call_id, query=query,
                        status="failed", error="搜索不可用",
                    )
                else:
                    web_executed += 1
                    tavily_result = _web_search_provider_search(reserved, query or "")
                    summary = tavily_result.get("summary") or {}
                    result = make_tool_result(
                        call_id=call_id, query=query,
                        status="completed" if tavily_result.get("ok") else "failed",
                        answer=str(summary.get("answer") or ""),
                        results=summary.get("results") if isinstance(summary.get("results"), list) else [],
                        error=None if tavily_result.get("ok") else "搜索不可用",
                        log_error=summary.get("error"),
                        error_type=tavily_result.get("error_type"),
                        http_status=tavily_result.get("status_code"),
                        key=reserved,
                        raw=tavily_result.get("raw") if isinstance(tavily_result, dict) else None,
                    )

            current_results.append(result)
            web_results.append(result)

            # Emit completed so the client shows the search results
            web_item = build_web_search_item(result, include_result=True)
            yield emit(
                "response.output_item.done",
                {"output_index": item_idx, "item": web_item},
            )
            completed_prefix_output_items[item_idx] = web_item

        if other_calls:
            # Model produced non-web-search tool calls - stream from upstream
            request_payload["stream"] = True
            if web_search_details is not None:
                web_search_details.update(web_search_log(web_results, upstream_calls))
            try:
                upstream_lines = stream_upstream(channel, request_payload, default_timeout)
            except ProxyError as exc:
                if web_search_details is not None:
                    web_search_details["stream_fallback_error"] = str(exc)
                response_payload = convert_response(
                    upstream_response, "responses", protocol, original_model,
                )
                response_payload = _replace_or_prepend_web_search_items(
                    response_payload, web_results
                )
                response_payload = add_source_annotations(response_payload, web_results)
                if on_response is not None:
                    on_response(_response_with_usage_totals(upstream_response, protocol, usage_totals))
                for line in responses_sse_events(response_payload, skip_response_created=True):
                    if line.startswith("event: response.completed\n"):
                        line = _patch_completed_usage(line, usage_totals)
                    line = _patch_sse_sequence_numbers(line, sequence_number)
                    yield line
                return

            def remember(resp: dict[str, Any]) -> None:
                _add_usage_totals(usage_totals, resp, protocol)
                if on_response is not None:
                    on_response(_response_with_usage_totals(resp, protocol, usage_totals))
                if reasoning_cache is not None:
                    if protocol == "messages":
                        reasoning_cache.remember_messages_response(resp, cache_namespace)
                    elif protocol == "chat":
                        reasoning_cache.remember_chat_response(resp, cache_namespace)

            if protocol == "chat":
                event_iter = chat_sse_to_responses_events(
                    upstream_lines, model_for_sse, remember,
                    skip_tool_names={"web_search"}, skip_response_created=True,
                )
            else:
                event_iter = messages_sse_to_responses_events(
                    upstream_lines, model_for_sse, remember,
                    skip_response_created=True,
                )
            for line in event_iter:
                line = _patch_sse_output_indices(line, next_output_index)
                if line.startswith("event: response.completed\n"):
                    line = _inject_web_search_into_completed_sse(
                        line, web_results, next_output_index,
                    )
                    line = _patch_completed_usage(line, usage_totals)
                line = _patch_sse_sequence_numbers(line, sequence_number)
                yield line
            return

        # Prepare for next iteration
        request_payload = append_tool_results(
            request_payload, upstream_response, protocol, current_results,
        )
        request_payload["stream"] = True
        if web_search_details is not None:
            web_search_details.update(web_search_log(web_results, upstream_calls))
        try:
            upstream_lines = stream_upstream(channel, request_payload, default_timeout)
        except ProxyError as exc:
            if web_search_details is not None:
                web_search_details["stream_fallback_error"] = str(exc)
            response_payload = convert_response(
                upstream_response, "responses", protocol, original_model,
            )
            response_payload = _replace_or_prepend_web_search_items(
                response_payload, web_results
            )
            response_payload = add_source_annotations(response_payload, web_results)
            if on_response is not None:
                on_response(_response_with_usage_totals(upstream_response, protocol, usage_totals))
            for line in responses_sse_events(response_payload, skip_response_created=True):
                if line.startswith("event: response.completed\n"):
                    line = _patch_completed_usage(line, usage_totals)
                line = _patch_sse_sequence_numbers(line, sequence_number)
                yield line
            return

        streamed_response: dict[str, Any] | None = None

        def remember(resp: dict[str, Any]) -> None:
            nonlocal streamed_response
            streamed_response = resp
            _add_usage_totals(usage_totals, resp, protocol)
            if on_response is not None:
                on_response(_response_with_usage_totals(resp, protocol, usage_totals))
            if reasoning_cache is not None:
                if protocol == "messages":
                    reasoning_cache.remember_messages_response(resp, cache_namespace)
                elif protocol == "chat":
                    reasoning_cache.remember_chat_response(resp, cache_namespace)

        if protocol == "chat":
            event_iter = chat_sse_to_responses_events(
                upstream_lines, model_for_sse, remember,
                skip_tool_names={"web_search"}, skip_response_created=True,
            )
        else:
            event_iter = messages_sse_to_responses_events(
                upstream_lines, model_for_sse, remember,
                skip_response_created=True,
            )
        pending_completed_line: str | None = None
        round_output_items: dict[int, dict[str, Any]] = {}
        round_sequence_offset = sequence_number
        round_output_offset = next_output_index
        for line in event_iter:
            line = _patch_sse_output_indices(line, round_output_offset)
            line = _patch_sse_sequence_numbers(line, round_sequence_offset)
            event_name, event_payload = _parse_sse_event(line)
            if event_name == "response.completed":
                pending_completed_line = line
                continue
            item_done = _output_item_done_payload(event_name, event_payload)
            if item_done is not None:
                output_index, item = item_done
                round_output_items[output_index] = item
            yield line
            sequence_number = max(
                sequence_number,
                _next_sequence_number(event_payload),
            )
            next_output_index = max(
                next_output_index,
                _next_output_index(event_payload),
            )
        streamed_tool_calls = (
            extract_tool_calls(streamed_response, protocol)
            if streamed_response is not None
            else []
        )
        if web_search_calls(streamed_tool_calls):
            completed_prefix_output_items.update(round_output_items)
            pending_upstream_response = streamed_response
            pending_usage_counted = True
            continue
        if pending_completed_line is not None:
            prefix_items = [
                completed_prefix_output_items[index]
                for index in sorted(completed_prefix_output_items)
            ]
            line = _prepend_completed_output_items(
                pending_completed_line,
                prefix_items,
            )
            line = _patch_completed_usage(line, usage_totals)
            yield line
        return

    # Max iterations guard — should not normally be reached
    response_payload = convert_response(
        upstream_response, "responses", protocol, original_model,
    )
    if web_results:
        response_payload = _replace_or_prepend_web_search_items(
            response_payload, web_results
        )
        response_payload = add_source_annotations(response_payload, web_results)
    if on_response is not None:
        on_response(_response_with_usage_totals(upstream_response, protocol, usage_totals))
    for line in responses_sse_events(response_payload, skip_response_created=True):
        if line.startswith("event: response.completed\n"):
            line = _patch_completed_usage(line, usage_totals)
        line = _patch_sse_sequence_numbers(line, sequence_number)
        yield line


def _build_web_search_final_response(
    intermediate_rounds: list[dict[str, Any]],
    final_nonstream_response: dict[str, Any] | None,
    original_model: str | None,
    protocol: str,
) -> dict[str, Any]:
    """Build the final response payload for non-streaming web search."""
    if final_nonstream_response is None:
        return {}

    response_payload = convert_response(
        final_nonstream_response,
        "responses",
        protocol,
        original_model,
    )

    # Collect all web search results from intermediate rounds
    all_web_results: list[dict[str, Any]] = []
    for intermediate in intermediate_rounds:
        for r in intermediate.get("current_web_results") or []:
            all_web_results.append(r)

    if all_web_results:
        response_payload = _replace_or_prepend_web_search_items(
            response_payload, all_web_results
        )
        response_payload = add_source_annotations(response_payload, all_web_results)

    return response_payload


def _replace_or_prepend_web_search_items(
    response_payload: dict[str, Any],
    web_results: list[dict[str, Any]],
) -> dict[str, Any]:
    output = response_payload.get("output", []) or []
    has_web_search_function = any(
        isinstance(item, dict)
        and item.get("type") == "function_call"
        and item.get("name") == "web_search"
        for item in output
    )
    if has_web_search_function:
        return replace_web_search_function_items(
            response_payload,
            web_results,
            include_result=True,
        )
    return prepend_web_search_items(
        response_payload,
        web_results,
        include_result=False,
    )


def main() -> None:
    try:
        settings = Settings.from_env()
    except SettingsError as exc:
        raise SystemExit(str(exc)) from exc
    app = create_app(settings)
    app.run(host=settings.host, port=settings.port)
