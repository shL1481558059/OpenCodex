from __future__ import annotations

import atexit
import json
import sqlite3
import time
import uuid
from typing import Any

from flask import (
    Flask,
    Response,
    jsonify,
    redirect,
    render_template,
    request,
    session,
    stream_with_context,
    url_for,
)

from .compat import apply_compat
from .config import ConfigError, ConfigManager
from .db import AsyncDBWriter, calculate_cost, extract_usage, read_logs
from .errors import BadRequestError, ProxyError
from .logging_utils import configure_logging, log_event, read_log_events, redact
from .protocols import convert_request, convert_response
from .reasoning_cache import ReasoningCache
from .routing import choose_channel
from .settings import Settings, SettingsError
from .streaming import (
    chat_sse_to_responses_events,
    messages_sse_to_responses_events,
    responses_sse_events,
)
from .upstream import post_upstream, stream_upstream


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
        if not is_admin_authenticated():
            return render_template("login.html", error=error)
        return render_template(
            "admin.html",
            log_view_level=settings.log_view_level,
            log_levels=["BASIC", "DEBUG", "TRACE"],
        )

    @app.post("/admin/logout")
    def admin_logout():
        session.clear()
        return redirect(url_for("admin"))

    @app.get("/admin/api/config")
    def admin_get_config():
        require_admin()
        return jsonify(config_manager.raw)

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
                "created_from",
                "created_to",
            )
            if request.args.get(key) not in (None, "")
        }
        events = read_logs(settings.db_path, request.args.get("limit", "200"), filters)
        return jsonify({"events": events})

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
                "error": error,
            }

            if response_body is not None:
                if response_protocol is not None:
                    usage = extract_usage(response_body, response_protocol)
                    db_record.update(usage)
                    db_record["cost"] = calculate_cost(
                        db_record["model"] or "",
                        usage["input_tokens"],
                        usage["cached_tokens"],
                        usage["output_tokens"],
                    )
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
                first_token_recorded = False
                streamed_upstream_response = None

                def stream_with_ttft():
                    nonlocal first_token_recorded, streamed_upstream_response, ttft_ms
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
                            if not first_token_recorded and "response.output_text.delta" in line:
                                first_token_recorded = True
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
                    stream_error = None
                    try:
                        yield from responses_sse_events(response_payload)
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


def _visible_params(payload: dict[str, Any]) -> dict[str, Any]:
    hidden = {"messages", "input", "instructions", "system", "tools"}
    return {key: redact(value) for key, value in payload.items() if key not in hidden}


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
                "thinking": "Tool use continuation context unavailable after proxy restart.",
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
