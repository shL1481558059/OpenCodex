from __future__ import annotations

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
    strip_removed_config_fields,
    validate_channel,
)
from .db import (
    AsyncDBWriter,
    TAVILY_KEY_USAGE_LIMIT,
    calculate_cost,
    extract_usage,
    read_log_filter_options,
    read_logs_page,
    read_web_search_config,
    replace_web_search_config,
    reserve_tavily_key,
    reserve_tavily_key_by_id,
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
    config_manager = ConfigManager(settings.db_path, settings.default_timeout)
    reasoning_cache = ReasoningCache()
    db_writer = AsyncDBWriter(settings.db_path)
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
            password = request.form.get("password", "")
            if password == settings.admin_password:
                session["admin_authenticated"] = True
                return redirect(url_for("admin"))
            error = "密码错误"
            log_event(logger, "WARNING", "admin login failed", path="/admin")
        if request.method == "GET" and (admin_static_dir / "index.html").exists():
            return _spa_index_response(admin_static_dir)
        if not is_admin_authenticated():
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
        return jsonify({"authenticated": is_admin_authenticated()})

    @app.post("/admin/api/login")
    def admin_login():
        body = request.get_json(silent=True)
        password = ""
        if isinstance(body, dict):
            password = str(body.get("password", ""))
        else:
            password = request.form.get("password", "")
        if password == settings.admin_password:
            session["admin_authenticated"] = True
            return jsonify({"authenticated": True})
        log_event(logger, "WARNING", "admin login failed", path="/admin/api/login")
        return jsonify({"error": "密码错误"}), 401

    @app.post("/admin/logout")
    def admin_logout():
        session.clear()
        return redirect(url_for("admin"))

    @app.post("/admin/api/logout")
    def admin_api_logout():
        session.clear()
        return jsonify({"authenticated": False})

    @app.get("/admin/api/config")
    def admin_get_config():
        require_admin()
        return jsonify(config_manager.raw)

    @app.get("/admin/api/config/export")
    def admin_export_config():
        require_admin()
        payload = json.dumps(
            {"channels": config_manager.raw.get("channels", [])},
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
        require_admin()
        candidate = request.get_json(silent=True)
        if not isinstance(candidate, dict):
            return jsonify({"error": "request body must be a JSON object"}), 400

        imported_channels = candidate.get("channels")
        if not isinstance(imported_channels, list):
            return jsonify({"error": "channels must be a list"}), 400

        current = config_manager.raw
        current_channels = current.get("channels", [])
        current_ids = {
            str(channel.get("id", "")).strip()
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
            if channel_id in current_ids:
                skipped_ids.append(channel_id)
                continue
            current_ids.add(channel_id)
            merged_channels.append(channel)

        try:
            saved = config_manager.save({**current, "channels": merged_channels})
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
        require_admin()
        candidate = request.get_json(silent=True)
        if not isinstance(candidate, dict):
            return jsonify({"error": "request body must be a JSON object"}), 400
        try:
            saved = config_manager.save(candidate)
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
        require_admin()
        return jsonify(read_web_search_config(settings.db_path))

    @app.post("/admin/api/web-search")
    def admin_save_web_search():
        require_admin()
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
        require_admin()
        started = time.time()
        body = request.get_json(silent=True)
        if not isinstance(body, dict):
            return jsonify({"error": "request body must be a JSON object"}), 400
        try:
            key_id = int(body.get("id"))
        except (TypeError, ValueError):
            return jsonify({"error": "id is required"}), 400
        query = str(body.get("query") or "OpenAI").strip() or "OpenAI"
        web_search_config = read_web_search_config(settings.db_path)
        reserved = reserve_tavily_key_by_id(settings.db_path, key_id)
        if reserved is None:
            limit = web_search_config.get("key_usage_limit", TAVILY_KEY_USAGE_LIMIT)
            return jsonify({"error": f"Tavily key is unavailable or has reached {limit} uses"}), 400
        result = tavily_search(reserved["key"], query)
        return jsonify(
            {
                "ok": result.get("ok") is True,
                "duration_ms": int((time.time() - started) * 1000),
                "key": {
                    "id": reserved["id"],
                    "usage_count": reserved["usage_count"],
                    "key_usage_limit": reserved["key_usage_limit"],
                },
                "result": redact(result),
                "config": read_web_search_config(settings.db_path),
            }
        )

    @app.get("/admin/api/logs")
    def admin_logs():
        require_admin()
        filters = {
            key: request.args.get(key)
            for key in (
                "request_id",
                "model",
                "upstream_model",
                "channel_id",
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
        page = read_logs_page(
            settings.db_path,
            page=request.args.get("page", "1"),
            page_size=request.args.get("page_size", "50"),
            filters=filters,
        )
        page["filter_options"] = read_log_filter_options(settings.db_path)
        return jsonify(page)

    @app.post("/admin/api/channels/discover-models")
    def admin_discover_models():
        require_admin()
        started = time.time()
        try:
            channel = _draft_channel_from_request(settings.default_timeout)
            raw = list_upstream_models(
                channel,
                request.headers.get("Authorization"),
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
    def admin_test_channel():
        require_admin()
        started = time.time()
        try:
            body = request.get_json(silent=True)
            if not isinstance(body, dict):
                return jsonify({"error": "request body must be a JSON object"}), 400
            payload = body.get("payload")
            if not isinstance(payload, dict):
                return jsonify({"error": "payload must be a JSON object"}), 400
            channel = _draft_channel_from_request(settings.default_timeout)
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
                request.headers.get("Authorization"),
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
        request_headers = json.dumps(dict(request.headers), ensure_ascii=False)
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
            payload = request.get_json(silent=True)
            if not isinstance(payload, dict):
                raise BadRequestError("request body must be a JSON object")
            stream_requested = payload.get("stream") is True
            if stream_requested and entry_protocol != "responses":
                raise BadRequestError("stream=true is only supported for /v1/responses")
            log_payload["model"] = payload.get("model")
            log_payload["params"] = _visible_params(payload)
            trace_payload = redact(payload)
            cache_namespace = _request_cache_namespace(payload, request_id)

            route = choose_channel(config_manager.expanded, payload.get("model"))
            channel = route.channel
            log_payload.update(
                {
                    "channel_id": channel.get("id"),
                    "upstream_model": route.upstream_model,
                }
            )

            upstream_request = convert_request(
                payload, entry_protocol, channel["type"], route.upstream_model
            )
            upstream_request, compat_details = apply_compat(
                upstream_request,
                _compat_rules(channel.get("compat", {})),
            )
            if channel["type"] == "chat":
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
            if channel["type"] == "messages":
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
            should_simulate_web_search = (
                entry_protocol == "responses"
                and channel["type"] in {"chat", "messages"}
                and request_declares_web_search(payload)
                and read_web_search_config(settings.db_path).get("enabled") is True
            )

            if should_simulate_web_search:
                try:
                    response_payload, upstream_response, web_search_details = _run_web_search_simulation(
                        channel=channel,
                        upstream_request=upstream_request,
                        payload=payload,
                        original_model=route.original_model,
                        client_authorization=request.headers.get("Authorization"),
                        default_timeout=settings.default_timeout,
                        db_path=settings.db_path,
                    )
                except WebSearchSimulationUpstreamError as exc:
                    web_search_details = exc.details
                    log_payload["web_search"] = redact(web_search_details)
                    raise exc.proxy_error from exc
                log_payload["web_search"] = redact(web_search_details)
                log_payload["upstream_response_body"] = redact(upstream_response)
                if channel["type"] == "chat":
                    reasoning_cache.remember_chat_response(upstream_response, cache_namespace)
                if channel["type"] == "messages":
                    reasoning_cache.remember_messages_response(upstream_response, cache_namespace)
                if stream_requested:
                    def stream_web_search_response():
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
                        stream_with_context(stream_web_search_response()),
                        mimetype="text/event-stream",
                        headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
                    )
                return jsonify(response_payload)

            if stream_requested and channel["type"] in {"messages", "chat"}:
                upstream_request["stream"] = True
                log_payload["upstream_request_body"] = redact(upstream_request)
                log_payload["streaming"] = True
                ttft_start = time.time()
                upstream_lines = stream_upstream(
                    channel,
                    upstream_request,
                    request.headers.get("Authorization"),
                    settings.default_timeout,
                )
                ttft_recorded = False
                streamed_upstream_response = None

                def stream_with_ttft():
                    nonlocal ttft_recorded, streamed_upstream_response, ttft_ms
                    stream_error = None

                    def remember_streamed_response(response: dict[str, Any]) -> None:
                        nonlocal streamed_upstream_response
                        streamed_upstream_response = response
                        if channel["type"] == "messages":
                            reasoning_cache.remember_messages_response(response, cache_namespace)
                        if channel["type"] == "chat":
                            reasoning_cache.remember_chat_response(response, cache_namespace)

                    try:
                        event_iter = (
                            messages_sse_to_responses_events(
                                upstream_lines,
                                route.original_model or payload.get("model"),
                                remember_streamed_response,
                            )
                            if channel["type"] == "messages"
                            else chat_sse_to_responses_events(
                                upstream_lines,
                                route.original_model or payload.get("model"),
                                remember_streamed_response,
                            )
                        )
                        for line in event_iter:
                            if not ttft_recorded and _counts_for_ttft(line):
                                ttft_recorded = True
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
                request.headers.get("Authorization"),
                settings.default_timeout,
            )
            if channel["type"] == "chat":
                reasoning_cache.remember_chat_response(upstream_response, cache_namespace)
            if channel["type"] == "messages":
                reasoning_cache.remember_messages_response(upstream_response, cache_namespace)
            log_payload["upstream_response_body"] = redact(upstream_response)
            response_payload = convert_response(
                upstream_response,
                entry_protocol,
                channel["type"],
                route.original_model,
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


def is_admin_authenticated() -> bool:
    return bool(session.get("admin_authenticated"))


def require_admin() -> None:
    if not is_admin_authenticated():
        raise BadRequestError("admin authentication required", status_code=401)


def _spa_index_response(admin_static_dir: Path) -> Response:
    return Response(
        (admin_static_dir / "index.html").read_text(encoding="utf-8"),
        mimetype="text/html",
    )


def _run_web_search_simulation(
    *,
    channel: dict[str, Any],
    upstream_request: dict[str, Any],
    payload: dict[str, Any],
    original_model: str | None,
    client_authorization: str | None,
    default_timeout: int,
    db_path: Path,
) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any]]:
    protocol = channel["type"]
    request_payload = deepcopy(upstream_request)
    request_payload["stream"] = False
    web_results: list[dict[str, Any]] = []
    upstream_calls: list[dict[str, Any]] = []
    web_limit = max_web_search_calls(payload)
    web_executed = 0
    max_iterations = max(web_limit + 3, 3)
    upstream_response: dict[str, Any] = {}

    for iteration in range(max_iterations):
        upstream_response = _web_search_post_upstream(
            channel=channel,
            request_payload=request_payload,
            client_authorization=client_authorization,
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
            return response_payload, upstream_response, web_search_log(web_results, upstream_calls)

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
            tavily_result = tavily_search(reserved["key"], query or "")
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
            response_payload = convert_response(
                upstream_response,
                "responses",
                protocol,
                original_model,
            )
            response_payload = replace_web_search_function_items(
                response_payload,
                current_results,
                include_result=True,
            )
            return response_payload, upstream_response, web_search_log(web_results, upstream_calls)

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
                client_authorization=client_authorization,
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
            return response_payload, upstream_response, web_search_log(web_results, upstream_calls)

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
    return response_payload, upstream_response, details


class WebSearchSimulationUpstreamError(Exception):
    def __init__(self, proxy_error: ProxyError, details: dict[str, Any]) -> None:
        super().__init__(str(proxy_error))
        self.proxy_error = proxy_error
        self.details = details


def _web_search_post_upstream(
    *,
    channel: dict[str, Any],
    request_payload: dict[str, Any],
    client_authorization: str | None,
    default_timeout: int,
    web_results: list[dict[str, Any]],
    upstream_calls: list[dict[str, Any]],
) -> dict[str, Any]:
    try:
        return post_upstream(
            channel,
            request_payload,
            client_authorization,
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
    normalized = strip_removed_config_fields({"channels": [channel]})
    expanded_channel = expand_env(normalized)["channels"][0]
    validate_channel(expanded_channel, default_timeout)
    return expanded_channel


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


def _counts_for_ttft(sse_line: str) -> bool:
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
    for model in (
        db_record.get("model"),
        db_record.get("upstream_model"),
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


def main() -> None:
    try:
        settings = Settings.from_env()
    except SettingsError as exc:
        raise SystemExit(str(exc)) from exc
    app = create_app(settings)
    app.run(host=settings.host, port=settings.port)
