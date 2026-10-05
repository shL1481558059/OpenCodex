#!/usr/bin/env python3
"""Exercise real Codex app-server lifecycle APIs against a loopback OpenCodex API."""
import argparse
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import threading
import time
import urllib.request
import uuid
from urllib.parse import urlparse, parse_qsl, urlencode, urlunparse


MODEL = "deepseek-v4.1-flash"
DEFAULT_CODEX = "/Applications/ChatGPT.app/Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex"
DOCS = "https://developers.openai.com/codex/app-server/"


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def preflight_model_catalog(binary, home, output):
    # These test drivers generate JSON-quoted TOML strings; no external parser is needed.
    config = (home / "config.toml").read_text()
    def configured_string(key):
        match = re.search(r'^' + re.escape(key) + r'\s*=\s*("(?:\\.|[^"\\])*")\s*$', config, re.MULTILINE)
        require(match is not None, "Missing generated config field " + key)
        return json.loads(match[1])
    model = configured_string("model")
    version = subprocess.check_output([binary, "--version"], text=True).strip().split()[-1]
    url = urlparse(configured_string("model_catalog_url"))
    require(url.scheme in ("http", "https") and url.hostname in ("127.0.0.1", "localhost", "::1")
            and not url.username and not url.password, "Catalog must use a credential-free loopback URL")
    query = dict(parse_qsl(url.query))
    query["client_version"] = version
    endpoint = urlunparse(url._replace(query=urlencode(query)))
    request = urllib.request.Request(endpoint, headers={"Authorization": "Bearer " + os.environ[configured_string("env_key")]})
    with urllib.request.urlopen(request, timeout=30) as response:
        payload = json.load(response)
    models = payload.get("models", [])
    entry = next((item for item in models if item.get("slug") == model), None)
    require(entry is not None, "Codex catalog omitted requested model " + model)
    require(isinstance(entry.get("apply_patch_tool_type"), str) and entry["apply_patch_tool_type"],
            "Codex catalog omitted apply_patch_tool_type")
    (output / "catalog-preflight.json").write_text(json.dumps({"url": endpoint, "client_version": version,
        "model": model, "apply_patch_tool_type": entry["apply_patch_tool_type"], "model_entry": entry}, indent=2))
    return entry


def verify_cached_model(home, model, output):
    path = home / "models_cache.json"
    require(path.exists(), "Client model discovery did not create models_cache.json; fallback metadata is not acceptance")
    cache = json.loads(path.read_text())
    entry = next((item for item in cache.get("models", []) if item.get("slug") == model), None)
    require(entry is not None, "Client model cache omitted requested model " + model)
    require(isinstance(entry.get("apply_patch_tool_type"), str) and entry["apply_patch_tool_type"],
            "Client model cache omitted apply_patch_tool_type")
    (output / "client-model-cache-evidence.json").write_text(json.dumps({"model": model, "model_entry": entry}, indent=2))
    return entry


class AppServer:
    def __init__(self, binary, home, work, output, key_env, timeout):
        preflight_model_catalog(binary, home, output)
        self.timeout = timeout
        self.output = output
        self.messages = []
        self.condition = threading.Condition()
        self.counter = 0
        self.error = None
        self.secret = os.environ[key_env]
        self.log = (output / "rpc.jsonl").open("w")
        self.log_lock = threading.Lock()
        env = os.environ.copy()
        env["CODEX_HOME"] = str(home)
        self.process = subprocess.Popen([binary, "app-server", "--stdio"], cwd=work, env=env,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                        text=True, bufsize=1, start_new_session=True)
        self.reader = threading.Thread(target=self.read_messages, daemon=True)
        self.stderr_reader = threading.Thread(target=self.read_stderr, daemon=True)
        self.reader.start()
        self.stderr_reader.start()

    def record(self, direction, value):
        line = json.dumps({"time": time.time(), "direction": direction, "message": value}, ensure_ascii=False)
        with self.log_lock:
            self.log.write(line.replace(self.secret, "[REDACTED]") + "\n")
            self.log.flush()

    def read_stderr(self):
        with (self.output / "stderr.log").open("w") as log:
            for line in self.process.stderr:
                log.write(line.replace(self.secret, "[REDACTED]"))
                log.flush()

    def read_messages(self):
        try:
            for line in self.process.stdout:
                value = json.loads(line)
                self.record("receive", value)
                with self.condition:
                    self.messages.append(value)
                    if "method" in value and "id" in value:
                        self.error = RuntimeError("Unexpected server request: " + value["method"])
                    self.condition.notify_all()
        except Exception as error:
            self.error = error
        finally:
            with self.condition:
                self.error = self.error or RuntimeError("app-server stdout closed")
                self.condition.notify_all()

    def send(self, value):
        self.record("send", value)
        self.process.stdin.write(json.dumps(value) + "\n")
        self.process.stdin.flush()

    def request(self, method, params, timeout=None):
        self.counter += 1
        identity = self.counter
        offset = len(self.messages)
        self.send({"id": identity, "method": method, "params": params})
        value = self.wait(lambda item: item.get("id") == identity and "method" not in item,
                          offset, timeout)
        if "error" in value:
            raise RuntimeError(method + ": " + json.dumps(value["error"]))
        return value["result"]

    def wait(self, predicate, offset=0, timeout=None):
        deadline = time.monotonic() + (timeout or self.timeout)
        cursor = offset
        with self.condition:
            while True:
                for item in self.messages[cursor:]:
                    if predicate(item):
                        return item
                cursor = len(self.messages)
                if self.error:
                    raise self.error
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("app-server event deadline exceeded")
                self.condition.wait(min(remaining, 1))

    def close(self):
        if self.process.poll() is None:
            os.killpg(self.process.pid, signal.SIGTERM)
            try:
                self.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                os.killpg(self.process.pid, signal.SIGKILL)
                self.process.wait(timeout=10)
        self.reader.join(timeout=5)
        self.stderr_reader.join(timeout=5)
        self.log.close()


class Verification:
    def __init__(self, app, home, work, output, schemas):
        self.app, self.home, self.work, self.output = app, home, work, output
        self.schemas = schemas
        self.nonce = uuid.uuid4().hex[:16]
        self.checks = []
        self.threads = []
        self.turns = {}
        self.active_turns = {}
        self.boundaries = {}

    def rpc(self, method, schema, params):
        document = json.loads(next(self.schemas.rglob(schema + ".json")).read_text())
        require(set(params) <= set(document.get("properties", {})), "Undeclared schema fields for " + method)
        require(set(document.get("required", [])) <= set(params), "Missing schema fields for " + method)
        return self.app.request(method, params)

    def new_thread(self):
        result = self.rpc("thread/start", "ThreadStartParams", {
            "model": MODEL, "modelProvider": "local_e2e", "cwd": str(self.work),
            "approvalPolicy": "never", "sandbox": "workspace-write", "ephemeral": False,
            "historyMode": "legacy", "experimentalRawEvents": True,
            "developerInstructions": "This is an authorized local protocol integration test. Follow each latest user request exactly. Do not delegate or use tools unless explicitly requested. Never access credentials or network services. Use only the assigned test working directory. A request for one exact line requires one final answer with that line."
        })
        verify_cached_model(self.home, MODEL, self.output)
        identity = result["thread"]["id"]
        self.threads.append(identity)
        return identity

    def read(self, thread, label):
        result = self.rpc("thread/read", "ThreadReadParams", {"threadId": thread, "includeTurns": True})
        (self.output / (label + ".thread.json")).write_text(json.dumps(result, indent=2, ensure_ascii=False))
        return result["thread"]

    def start(self, thread, text):
        offset = len(self.app.messages)
        result = self.rpc("turn/start", "TurnStartParams", {
            "threadId": thread, "model": MODEL, "effort": "low",
            "input": [{"type": "text", "text": text}], "clientUserMessageId": str(uuid.uuid4())
        })
        self.active_turns[thread] = result["turn"]["id"]
        return result["turn"]["id"], offset

    def finish(self, thread, turn, offset, expected=None, status="completed", label="turn"):
        event = self.app.wait(lambda item: item.get("method") == "turn/completed"
                              and item.get("params", {}).get("threadId") == thread
                              and item.get("params", {}).get("turn", {}).get("id") == turn, offset)
        self.active_turns.pop(thread, None)
        require(event["params"]["turn"]["status"] == status, label + " terminal status: " + json.dumps(event["params"]["turn"]))
        snapshot = self.read(thread, label)
        matches = [item for item in snapshot.get("turns", []) if item["id"] == turn]
        require(len(matches) == 1, label + " must occur once in thread/read")
        finals = [item for item in matches[0]["items"]
                  if item.get("type") == "agentMessage" and item.get("phase") in (None, "final_answer")]
        terminal_events = [item for item in self.app.messages[offset:]
                           if item.get("method") == "turn/completed"
                           and item.get("params", {}).get("threadId") == thread
                           and item.get("params", {}).get("turn", {}).get("id") == turn]
        require(len(terminal_events) == 1, label + " emitted duplicate turn/completed")
        if expected is not None:
            require(len(finals) == 1, label + " expected exactly one terminal non-commentary message; got " + str(finals))
            require(finals[0]["text"].strip() == expected, label + " unexpected final: " + finals[0]["text"])
        elif status == "interrupted":
            require(not finals, label + " emitted a final after interruption")
        self.turns[label] = {"thread_id": thread, "turn_id": turn, "status": status,
                             "final_count": len(finals), "final_text": [item["text"] for item in finals],
                             "raw_phases": [item.get("phase") for item in finals]}
        return matches[0]

    def complete(self, thread, prompt, expected, label):
        turn, offset = self.start(thread, prompt)
        self.finish(thread, turn, offset, expected, label=label)
        return turn

    def wait_file(self, path, thread, turn, offset):
        deadline = time.monotonic() + self.app.timeout
        while not path.exists():
            require(not any(item.get("method") == "turn/completed"
                            and item.get("params", {}).get("threadId") == thread
                            and item.get("params", {}).get("turn", {}).get("id") == turn
                            for item in self.app.messages[offset:]), "Turn ended before real command created " + path.name)
            if time.monotonic() >= deadline:
                raise TimeoutError("No real command side effect: " + path.name)
            if self.app.error:
                raise self.app.error
            time.sleep(0.1)

    def conversations(self):
        root = self.new_thread()
        marker = "ANSWER_" + self.nonce + "_42"
        prompt = "Reply with exactly one final line: " + marker + ". Do not use tools."
        first = self.complete(root, prompt, marker, "fresh")
        second = self.complete(root, prompt, marker, "same_text_new_turn")
        require(first != second, "Identical input must create a distinct explicit turn")
        snapshot = self.read(root, "before_fork")
        require(len(snapshot["turns"]) == 2, "Same-text continuation reused or replayed a turn")
        fork = self.rpc("thread/fork", "ThreadForkParams", {"threadId": root, "model": MODEL,
                        "modelProvider": "local_e2e", "cwd": str(self.work), "approvalPolicy": "never",
                        "sandbox": "workspace-write"})["thread"]["id"]
        require(fork != root, "Fork must have a new thread id")
        self.threads.append(fork)
        inherited = self.read(fork, "fork_inherited")
        require({first, second} <= {item["id"] for item in inherited["turns"]}, "Fork lost inherited turns")
        fork_marker = "FORK_" + self.nonce + "|" + marker
        self.complete(fork, "Only answer this new question: prefix the exact answer from the previous conversation with FORK_"
                      + self.nonce + "|. Do not repeat old instructions or use tools. Output one final line.", fork_marker, "fork_new_question")
        before = len(self.app.messages)
        self.rpc("thread/compact/start", "ThreadCompactStartParams", {"threadId": fork})
        compact = self.app.wait(lambda item: item.get("params", {}).get("threadId") == fork and
                                (item.get("method") == "thread/compacted" or
                                 item.get("method") == "item/completed" and
                                 item.get("params", {}).get("item", {}).get("type") == "contextCompaction"), before)
        compact_turn = compact["params"].get("turnId")
        if compact_turn:
            self.app.wait(lambda item: item.get("method") == "turn/completed"
                          and item.get("params", {}).get("threadId") == fork
                          and item.get("params", {}).get("turn", {}).get("id") == compact_turn, before)
        compacted = self.read(fork, "compacted")
        require(any(item.get("type") == "contextCompaction" for turn in compacted["turns"] for item in turn["items"]),
                "thread/read did not retain a contextCompaction item")
        next_marker = "AFTER_COMPACT_" + self.nonce
        self.complete(fork, "Ignore answered tasks. For this new question return exactly: " + next_marker + ". Do not use tools.",
                      next_marker, "after_compaction")

    def shell_prompt(self, command, final):
        return ("Use the real functions.exec code-mode tool and tools.exec_command to run exactly this shell command once in the current test directory. "
                "No preliminary checks, no additional commands or delegation. Await its completion, then output exactly the final line " + final
                + ". Shell command:\n" + command)

    def steering(self):
        thread = self.new_thread()
        ready = self.work / "steer.ready"
        command = "python3 -c 'from pathlib import Path; import time; Path(\"steer.ready\").write_text(\"ready\"); time.sleep(8); print(\"STEER_COMMAND_DONE\")'"
        turn, offset = self.start(thread, self.shell_prompt(command, "STALE_" + self.nonce))
        self.wait_file(ready, thread, turn, offset)
        corrected = "STEERED_" + self.nonce
        result = self.rpc("turn/steer", "TurnSteerParams", {"threadId": thread, "expectedTurnId": turn,
                          "input": [{"type": "text", "text": "Correction for this active turn: do not run another command. The final answer must be exactly " + corrected + ". Replace the old requested final."}]})
        require(result["turnId"] == turn, "steer changed the active turn id")
        history = self.finish(thread, turn, offset, corrected, label="steer")
        require(len([item for item in history["items"] if item.get("type") == "userMessage"]) >= 2,
                "Steered input was not persisted in the active turn")

    def interruption(self):
        thread = self.new_thread()
        ready = self.work / "interrupt.ready"
        command = "python3 -c 'from pathlib import Path; import time; Path(\"interrupt.ready\").write_text(\"ready\"); time.sleep(30); Path(\"interrupt.late\").write_text(\"unexpected\")'"
        turn, offset = self.start(thread, self.shell_prompt(command, "SHOULD_NOT_FINISH_" + self.nonce))
        self.wait_file(ready, thread, turn, offset)
        command_started = time.monotonic()
        self.rpc("turn/interrupt", "TurnInterruptParams", {"threadId": thread, "turnId": turn})
        self.finish(thread, turn, offset, status="interrupted", label="interrupted")
        marker = "AFTER_INTERRUPT_" + self.nonce
        self.complete(thread, "The previous task was cancelled. Do not resume it or run any tool. Reply exactly " + marker + ".",
                      marker, "after_interrupt")
        # Check after the original process's sleep would have ended, not just immediately after cancellation.
        while time.monotonic() < command_started + 32:
            time.sleep(0.2)
        self.boundaries["interrupt"] = {
            "late_side_effect_observed": (self.work / "interrupt.late").exists(),
            "checked_after_original_command_deadline": True,
            "verified_scope": "Codex turn interrupted without final; next turn answers new input",
            "os_process_termination_guaranteed": False,
            "note": "Codex 0.160 explicitly allows previously launched unified exec processes to remain in the background after turn interruption."
        }

    def code_mode(self):
        thread = self.new_thread()
        marker = "CELL_" + self.nonce
        command = "python3 -c 'from pathlib import Path; import time; time.sleep(2); Path(\"cell.txt\").open(\"a\").write(\"" + marker + "\\n\"); print(\"" + marker + "\")'"
        source = '// @exec: {"yield_time_ms": 1, "max_output_tokens": 1000}\ntext(await tools.exec_command(' + json.dumps({"cmd": command, "workdir": str(self.work), "yield_time_ms": 10000, "max_output_tokens": 1000}) + '));'
        prompt = ("Execute this exact JavaScript once with the real functions.exec custom tool. It intentionally yields a running cell. "
                  "When the tool returns Script running with cell ID, call functions.wait with that real cell_id until completion. "
                  "Do not rerun functions.exec, do not use other tools or simulate tool output. After observing the successful real output, reply exactly "
                  + marker + ".\n" + source)
        turn = self.complete(thread, prompt, marker, "code_mode")
        require((self.work / "cell.txt").read_text().splitlines() == [marker], "Code-mode command was missing or executed more than once")
        rows = self.rollout(thread)
        current, calls, outputs = None, [], {}
        for row in rows:
            payload = row.get("payload", {})
            if row.get("type") == "turn_context":
                current = payload.get("turn_id")
                require(payload.get("model") == MODEL, "Unexpected actual model in rollout")
            if row.get("type") != "response_item" or current != turn:
                continue
            if payload.get("type") in ("function_call", "custom_tool_call"):
                calls.append(payload)
            elif payload.get("type") in ("function_call_output", "custom_tool_call_output"):
                outputs.setdefault(payload.get("call_id"), []).append(payload.get("output"))
        # Codex's persisted default tool namespace may be omitted; retain the raw item as evidence.
        execs = [item for item in calls if item.get("namespace") in (None, "functions") and item.get("name") == "exec"]
        waits = [item for item in calls if item.get("namespace") in (None, "functions") and item.get("name") == "wait"]
        require(len(execs) == 1 and execs[0].get("type") == "custom_tool_call" and isinstance(execs[0].get("input"), str),
                "functions.exec did not retain its declared custom tool shape: " + json.dumps(calls))
        require(waits and all(item.get("type") == "function_call" for item in waits), "No actual functions.wait function call")
        serialized = json.dumps(outputs.get(execs[0]["call_id"], []))
        require("Script running with cell ID" in serialized, "functions.exec did not actually yield a live cell")
        cell = re.search(r"Script running with cell ID ([0-9]+)", serialized)
        require(cell is not None and all(str(json.loads(item["arguments"])["cell_id"]) == cell.group(1) for item in waits),
                "functions.wait did not use the cell id actually yielded by functions.exec")
        require(any(marker in json.dumps(outputs.get(item["call_id"], [])) for item in waits), "functions.wait did not deliver actual command result")
        (self.output / "tool-shapes.json").write_text(json.dumps({"calls": calls, "outputs": outputs}, indent=2))

    def rollout(self, thread):
        for path in (self.home / "sessions").rglob("*.jsonl"):
            rows = [json.loads(line) for line in path.read_text().splitlines() if line.strip()]
            if any(row.get("type") == "session_meta" and row.get("payload", {}).get("id") == thread for row in rows):
                return rows
        raise AssertionError("No persisted native rollout for " + thread)

    def run_check(self, name, action):
        print("START " + name, flush=True)
        started = time.time()
        try:
            action()
            result = {"name": name, "passed": True}
        except Exception as error:
            result = {"name": name, "passed": False, "error": str(error)}
            for thread, turn in list(self.active_turns.items()):
                try:
                    self.rpc("turn/interrupt", "TurnInterruptParams", {"threadId": thread, "turnId": turn})
                except Exception:
                    pass
                self.active_turns.pop(thread, None)
        result["duration_seconds"] = round(time.time() - started, 3)
        self.checks.append(result)
        print(json.dumps(result, ensure_ascii=False), flush=True)
        return result["passed"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--codex-bin", default=DEFAULT_CODEX)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--api-key-env", default="OCXP_E2E_API_KEY")
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--timeout", default=240, type=int, help="Deadline per RPC/turn, in seconds")
    parser.add_argument("--native-trace-log", type=Path, help="Local API Debug log proving each thread entered the native executor")
    parser.add_argument("--prepare-only", action="store_true", help="Generate exact-binary schemas/config without contacting any model")
    args = parser.parse_args()
    parsed = urlparse(args.base_url)
    if parsed.scheme not in ("http", "https") or parsed.hostname not in ("localhost", "127.0.0.1", "::1") or parsed.username or parsed.password:
        parser.error("--base-url must be a credential-free loopback HTTP(S) endpoint")
    if not args.prepare_only and not args.native_trace_log:
        parser.error("--native-trace-log is required to prove the native executor route")
    if not args.prepare_only and not os.environ.get(args.api_key_env):
        parser.error("Missing API key environment variable " + args.api_key_env)
    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=False, mode=0o700)
    home, work, schemas = output / "home", output / "work", output / "schema"
    home.mkdir(mode=0o700)
    work.mkdir()
    env = os.environ.copy()
    env["CODEX_HOME"] = str(home)
    version = subprocess.check_output([args.codex_bin, "--version"], text=True, env=env).strip()
    require("0.160." in version, "This acceptance contract requires Codex CLI 0.160.x; got " + version)
    subprocess.run([args.codex_bin, "app-server", "generate-json-schema", "--experimental", "--out", str(schemas)], check=True, env=env)
    required = ("InitializeParams", "ThreadStartParams", "ThreadForkParams", "ThreadReadParams", "ThreadCompactStartParams",
                "TurnStartParams", "TurnSteerParams", "TurnInterruptParams")
    for name in required:
        require(any(schemas.rglob(name + ".json")), "Installed binary lacks required explicit API schema " + name)
    config = f'''model = {json.dumps(MODEL)}
model_provider = "local_e2e"
model_reasoning_effort = "low"
approval_policy = "never"
sandbox_mode = "workspace-write"
[model_providers.local_e2e]
name = "Local native lifecycle verification"
base_url = {json.dumps(args.base_url.rstrip('/'))}
model_catalog_url = {json.dumps(args.base_url.rstrip('/') + '/models')}
wire_api = "responses"
env_key = {json.dumps(args.api_key_env)}
requires_openai_auth = false
[features]
code_mode = true
multi_agent = true
multi_agent_v2 = true
use_agent_identity = true
api_key_model_discovery = true
'''
    (home / "config.toml").write_text(config)
    (output / "contract.json").write_text(json.dumps({"codex_version": version, "model": MODEL, "base_url": args.base_url,
                                                    "official_docs": DOCS, "required_schemas": required}, indent=2))
    if args.prepare_only:
        print(json.dumps({"prepared": True, "output_dir": str(output), "model_calls": 0}))
        return 0
    app = AppServer(args.codex_bin, home, work, output, args.api_key_env, args.timeout)
    verification = Verification(app, home, work, output, schemas)
    try:
        verification.rpc("initialize", "InitializeParams", {"clientInfo": {"name": "opencodex_native_acceptance", "version": "1.0"},
                         "capabilities": {"experimentalApi": True}})
        app.send({"method": "initialized"})
        for name, action in (("conversation_fork_compaction", verification.conversations),
                             ("steer_active_turn", verification.steering),
                             ("interrupt_and_continue", verification.interruption),
                             ("code_mode_yield_wait", verification.code_mode)):
            verification.run_check(name, action)
    finally:
        app.close()
        trace = args.native_trace_log.read_text() if args.native_trace_log.exists() else ""
        missing = [thread for thread in verification.threads if "Native client response: thread=" + thread + " " not in trace]
        route_audit = {"expected_threads": verification.threads, "missing_threads": missing, "passed": not missing}
        summary = {"model": MODEL, "codex_version": version, "base_url": args.base_url,
                   "checks": verification.checks, "turns": verification.turns, "threads": verification.threads,
                   "boundaries": verification.boundaries, "native_route_audit": route_audit,
                   "passed": not missing and len(verification.checks) == 4 and all(check["passed"] for check in verification.checks)}
        (output / "summary.json").write_text(json.dumps(summary, indent=2, ensure_ascii=False))
        print(json.dumps(summary, ensure_ascii=False), flush=True)
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
