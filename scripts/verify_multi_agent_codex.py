#!/usr/bin/env python3
"""Run real Codex + DeepSeek against an explicitly selected local OpenCodex instance."""
import argparse
import json
import os
from pathlib import Path
import signal
import sqlite3
import subprocess
import time
from urllib.parse import urlparse


MODEL = "deepseek-v4.1-flash"

COLLABORATION = """This is an authorized local integration test. You MUST use the native
collaboration tools, not simulate delegation in text. All agents must use the current
deepseek-v4.1-flash model. Do not use web search or modify project source.
1. Spawn alpha and beta with fork_turns=none. Alpha must read alpha.txt with a shell
tool, calculate 7*9, and report ALPHA_OK_63. Beta must read beta.txt with a shell tool,
calculate 20+22, and report BETA_OK_42.
2. Send beta a message asking it to acknowledge MESSAGE_OK. Use list_agents and
wait_agent to collect actual reports. Do not invent results.
3. After alpha is finished, use followup_task on alpha: calculate 64+1 and return
FOLLOWUP_OK_65. Wait for its real result.
4. Spawn sleeper with fork_turns=none and ask it to run a shell sleep for 20 seconds.
Interrupt sleeper using interrupt_agent. Use list_agents to verify its state.
5. Your final answer must include COLLABORATION_OK, ALPHA_OK_63, BETA_OK_42,
FOLLOWUP_OK_65 and the observed interruption result. Only declare success after
the actual native operations succeed. Never wait indefinitely; report any failure.
"""

RECOVERY = """This is an authorized local failure-recovery integration test.
Execute exactly this single shell command once, without any preliminary checks:
python3 -c 'from pathlib import Path; p=Path("marker.txt"); p.open("a").write("RECOVERY_SIDE_EFFECT\\n"); print(len(p.read_text().splitlines()))'
The test directory is fresh. The command must print 1. Never execute it again,
even if an upstream model or summary fails and is retried. Then immediately finish
with exactly RECOVERY_OK 1 (no headings or other text). Do not use additional tools, delegate, search
the web, or modify other files. An external test proxy may fail one context summary;
continue from the already received tool result after automatic recovery.
"""


def audit_native_sessions(home, root_threads):
    """Verify native calls, their results and the receiving threads' actual history."""
    from datetime import datetime

    def object_value(value):
        if isinstance(value, str):
            try:
                value = json.loads(value)
            except ValueError:
                return {}
        return value if isinstance(value, dict) else {}

    def timestamp(row):
        return datetime.fromisoformat(row["timestamp"].replace("Z", "+00:00")).timestamp()

    def message_text(payload):
        content = payload.get("content", [])
        if isinstance(content, str):
            return content
        return "".join(block.get("text", block.get("encrypted_content", ""))
                       for block in content if isinstance(block, dict))

    actors, models, failures = {}, set(), []
    for path in (home / "sessions").rglob("*.jsonl"):
        rows = [json.loads(line) for line in path.read_text().splitlines() if line.strip()]
        metadata = next((row["payload"] for row in rows if row.get("type") == "session_meta"), {})
        source = object_value(metadata.get("source"))
        spawn = object_value(object_value(source.get("subagent")).get("thread_spawn"))
        is_root = metadata.get("id") in root_threads
        if not is_root and spawn.get("parent_thread_id") not in root_threads:
            continue
        name = "/root" if is_root else spawn.get("agent_path")
        if not name or name in actors:
            failures.append("missing or duplicate native agent path: " + str(name))
            continue
        actor = {"thread_id": metadata.get("id"), "parent_thread_id": spawn.get("parent_thread_id"),
                 "calls": {}, "commands": [], "messages": [], "completed": [], "aborted": []}
        actors[name] = actor
        turn = None
        for row in rows:
            payload = row.get("payload", {})
            kind, at = payload.get("type"), timestamp(row)
            if row.get("type") == "turn_context":
                turn = payload.get("turn_id") or turn
                if payload.get("model"):
                    models.add(payload["model"])
            if row.get("type") == "event_msg":
                if kind == "task_started":
                    turn = payload.get("turn_id")
                elif kind == "task_complete":
                    actor["completed"].append({"time": at, "turn": payload.get("turn_id"),
                                               "text": payload.get("last_agent_message") or ""})
                elif kind == "turn_aborted" and payload.get("reason") == "interrupted":
                    actor["aborted"].append({"time": at, "turn": payload.get("turn_id")})
                elif kind == "item_completed" and payload.get("item", {}).get("type") == "CommandExecution":
                    actor["commands"].append({"time": at, "turn": payload.get("turn_id"), **payload["item"]})
            if row.get("type") != "response_item":
                continue
            if kind in ("function_call", "custom_tool_call") and payload.get("call_id"):
                actor["calls"][payload["call_id"]] = {"time": at, "turn": turn,
                    "name": payload.get("name"), "namespace": payload.get("namespace"),
                    "args": object_value(payload.get("arguments")), "results": []}
            elif kind in ("function_call_output", "custom_tool_call_output"):
                call = actor["calls"].get(payload.get("call_id"))
                if call is not None:
                    call["results"].append({"time": at, "output": payload.get("output")})
            elif kind == "agent_message":
                actor["messages"].append({"time": at, "turn": turn, "payload": payload})

    root = actors.get("/root", {"calls": {}, "completed": []})
    calls = [dict(call, call_id=call_id) for call_id, call in root["calls"].items()
             if call["namespace"] == "collaboration"]
    actions = [call["name"] for call in calls]

    def successful(call):
        if not call["results"]:
            return False
        result = call["results"][-1]
        value = object_value(result["output"])
        if result["time"] < call["time"] or "error" in value or value.get("isError") is True:
            return False
        if call["namespace"] == "collaboration":
            if call["name"] in ("send_message", "followup_task"):
                return result["output"] == ""
            if call["name"] == "wait_agent":
                return isinstance(value.get("timed_out"), bool)
            if call["name"] == "list_agents":
                return isinstance(value.get("agents"), list)
            if call["name"] == "spawn_agent":
                return isinstance(value.get("task_name"), str)
            if call["name"] == "interrupt_agent":
                return isinstance(value.get("previous_status"), str)
        return True

    def targeting(call, name):
        target = call["args"].get("target", "")
        return target == name or "/root/" + target == name

    required = {"spawn_agent", "send_message", "followup_task", "wait_agent", "interrupt_agent", "list_agents"}
    succeeded = {call["name"] for call in calls if successful(call)}
    if required - succeeded:
        failures.append("missing successful native actions: " + ", ".join(sorted(required - succeeded)))
    if len(set(root_threads)) != 1 or "/root" not in actors:
        failures.append("expected one persisted native root thread")
    for name in ("/root/alpha", "/root/beta", "/root/sleeper"):
        actor = actors.get(name)
        if actor is None:
            failures.append("missing native child: " + name)
            continue
        if not any(call["name"] == "spawn_agent" and successful(call)
                   and "/root/" + call["args"].get("task_name", "") == name
                   and object_value(call["results"][-1]["output"]).get("task_name") == name for call in calls):
            failures.append("missing matched successful spawn result: " + name)

    def delivered(call, actor, kind):
        body = call["args"].get("message")
        return [message for message in actor["messages"]
                if body and message["time"] >= call["time"]
                and message["payload"].get("author") == "/root"
                and message["payload"].get("recipient") == call["target_path"]
                and ("Message Type: " + kind + "\n") in message_text(message["payload"])
                and body in message_text(message["payload"])]

    for name, marker in (("/root/alpha", "ALPHA_OK_63"), ("/root/beta", "BETA_OK_42")):
        actor = actors.get(name)
        if actor is None:
            continue
        fixture = home.parent / "work" / (name.rsplit("/", 1)[1] + ".txt")
        fixture_text = fixture.read_text().strip() if fixture.exists() else ""
        proven = any(command.get("exit_code") == 0 and command.get("status") == "completed"
                     and command.get("id") in actor["calls"] and successful(actor["calls"][command["id"]])
                     and fixture_text and fixture_text in command.get("aggregated_output", "")
                     and any(done["turn"] == command["turn"] and done["time"] >= command["time"]
                             and marker in done["text"] for done in actor["completed"])
                     for command in actor["commands"])
        if not proven:
            failures.append("missing successful fixture tool execution and result in " + name)

    alpha = actors.get("/root/alpha")
    followup_verified = False
    if alpha:
        for call in calls:
            if call["name"] != "followup_task" or not targeting(call, "/root/alpha") or not successful(call):
                continue
            initial = [done for done in alpha["completed"] if done["time"] < call["time"] and "ALPHA_OK_63" in done["text"]]
            messages = delivered(dict(call, target_path="/root/alpha"), alpha, "NEW_TASK")
            followup_verified |= bool(initial) and any(
                done["turn"] == message["turn"] and done["turn"] not in {item["turn"] for item in initial}
                and done["time"] >= message["time"] and "FOLLOWUP_OK_65" in done["text"]
                for message in messages for done in alpha["completed"])
    if not followup_verified:
        failures.append("alpha follow-up did not complete in a later turn of the same native thread")

    beta = actors.get("/root/beta")
    message_verified = bool(beta) and any(
        call["name"] == "send_message" and targeting(call, "/root/beta") and successful(call)
        and "MESSAGE_OK" in call["args"].get("message", "")
        and any(done["turn"] == message["turn"] and done["time"] >= message["time"] and "MESSAGE_OK" in done["text"]
                for message in delivered(dict(call, target_path="/root/beta"), beta, "MESSAGE")
                for done in beta["completed"]) for call in calls)
    if not message_verified:
        failures.append("beta has no matched native MESSAGE delivery and acknowledgment")

    sleeper = actors.get("/root/sleeper")
    interrupt_verified = False
    if sleeper:
        for call in calls:
            if (call["name"] != "interrupt_agent" or not targeting(call, "/root/sleeper") or not successful(call)
                    or object_value(call["results"][-1]["output"]).get("previous_status") != "running"):
                continue
            aborted = [event for event in sleeper["aborted"] if event["time"] >= call["time"]]
            pending = any(tool["time"] <= call["time"] and tool["turn"] == event["turn"]
                          and any(result["time"] >= call["time"] for result in tool["results"])
                          for event in aborted for tool in sleeper["calls"].values())
            observed = any(later["name"] == "list_agents" and successful(later)
                           and later["time"] >= call["results"][-1]["time"]
                           and any(agent.get("agent_name") == "/root/sleeper" and agent.get("agent_status") == "interrupted"
                                   for agent in object_value(later["results"][-1]["output"]).get("agents", []))
                           for later in calls)
            interrupt_verified |= bool(aborted) and pending and observed
    if not interrupt_verified:
        failures.append("sleeper lacks matched running interrupt, pending tool abortion and subsequent interrupted state")
    if models != {MODEL}:
        failures.append("unexpected or missing native model identities: " + repr(sorted(models)))
    children = [{"thread_id": actor["thread_id"], "parent_thread_id": actor["parent_thread_id"],
                 "agent_path": name, "tool_calls": len(actor["calls"]), "interrupted": bool(actor["aborted"])}
                for name, actor in actors.items() if name != "/root"]
    return {"actions": actions, "successful_actions": sorted(succeeded), "children": children,
            "models": sorted(models), "message_delivery_verified": message_verified,
            "same_thread_followup_verified": followup_verified, "running_interrupt_verified": interrupt_verified}, failures

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--codex-bin", default="codex")
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--api-key-env", default="OCXP_E2E_API_KEY")
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--scenario", choices=("collaboration", "recovery"), required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--fault-events", type=Path)
    parser.add_argument("--database", type=Path, help="Local SQLite request-log database")
    args = parser.parse_args()
    if urlparse(args.base_url).hostname not in ("localhost", "127.0.0.1", "::1"):
        parser.error("--base-url must point to the local instance under test")
    if not os.environ.get(args.api_key_env):
        parser.error(f"missing API key environment variable {args.api_key_env}")
    if args.scenario == "recovery" and (not args.fault_events or not args.database):
        parser.error("recovery requires --fault-events and --database to prove failure and retry")
    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=False, mode=0o700)
    home, work = output / "home", output / "work"
    home.mkdir(mode=0o700)
    work.mkdir()
    (work / "alpha.txt").write_text("Alpha fixture: seven times nine.\n")
    (work / "beta.txt").write_text("Beta fixture: twenty plus twenty-two.\n")
    # Separate CODEX_HOME prevents modifying the user's configuration or resuming real work.
    config = f'''model = {json.dumps(MODEL)}
model_provider = "local_e2e"
model_reasoning_effort = "low"
[model_providers.local_e2e]
name = "Local OpenCodex integration test"
base_url = {json.dumps(args.base_url.rstrip('/'))}
model_catalog_url = {json.dumps(args.base_url.rstrip('/') + '/models')}
wire_api = "responses"
env_key = {json.dumps(args.api_key_env)}
requires_openai_auth = false
[features]
multi_agent = true
multi_agent_v2 = true
use_agent_identity = true
api_key_model_discovery = true
'''
    (home / "config.toml").write_text(config)
    env = os.environ.copy()
    env["CODEX_HOME"] = str(home)
    prompt = COLLABORATION if args.scenario == "collaboration" else RECOVERY
    (output / "prompt.txt").write_text(prompt)
    version = subprocess.check_output([args.codex_bin, "--version"], text=True).strip()
    command = [args.codex_bin, "exec", "--json", "--skip-git-repo-check",
               "--sandbox", "workspace-write", "-C", str(work), "-m", MODEL,
               "-o", str(output / "final.txt"), prompt]
    with (output / "events.jsonl").open("w") as stdout, (output / "stderr.log").open("w") as stderr:
        started_at = time.time()
        process = subprocess.Popen(command, env=env, stdin=subprocess.DEVNULL,
                                   stdout=stdout, stderr=stderr, start_new_session=True)
        timed_out = False
        try:
            code = process.wait(timeout=args.timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            os.killpg(process.pid, signal.SIGTERM)
            try:
                code = process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                code = process.wait()
        ended_at = time.time()
    events = [json.loads(line) for line in (output / "events.jsonl").read_text().splitlines() if line.strip()]
    final = (output / "final.txt").read_text() if (output / "final.txt").exists() else ""
    completed = any(event.get("type") == "turn.completed" for event in events)
    threads = [event["thread_id"] for event in events if event.get("type") == "thread.started"]
    items = [event.get("item", {}) for event in events if event.get("type") == "item.completed"]
    collaboration = [item for item in items if "collab" in item.get("type", "")]
    failures = []
    native = None
    recovery = None
    if code != 0 or timed_out or not completed or not final.strip():
        failures.append("Codex did not complete with a nonempty final answer")
    if args.scenario == "collaboration":
        for marker in ("COLLABORATION_OK", "ALPHA_OK_63", "BETA_OK_42", "FOLLOWUP_OK_65"):
            if marker not in final:
                failures.append(f"missing verified result marker: {marker}")
        if not collaboration:
            failures.append("no native collaboration events; text claims are insufficient")
        native, native_failures = audit_native_sessions(home, threads)
        failures.extend(native_failures)
    else:
        marker = work / "marker.txt"
        if not marker.exists() or marker.read_text().splitlines() != ["RECOVERY_SIDE_EFFECT"]:
            failures.append("side effect was missing or executed more than once")
        if final.strip() != "RECOVERY_OK 1":
            failures.append("final must be the actual recovery result, not a summary quoting the marker")
        faults = [json.loads(line) for line in args.fault_events.read_text().splitlines() if line.strip()]
        faults = [event for event in faults if started_at <= event.get("time", 0) <= ended_at]
        injected = [event for event in faults if event.get("injected")]
        if len(injected) != 1 or not injected[0].get("summary"):
            failures.append("exactly one summary failure must actually be injected")
        elif not any(event.get("summary") and not event.get("injected")
                     and event.get("status") == 200 and not event.get("client_disconnected")
                     and event["time"] > injected[0]["time"]
                     for event in faults):
            failures.append("no subsequent real summary request after the injected failure")
        with sqlite3.connect(args.database.resolve().as_uri() + "?mode=ro", uri=True) as database:
            logs = []
            for thread in threads:
                logs.extend(database.execute(
                    'SELECT RequestId,RequestType,LifecycleStatus,StatusCode,CreatedAt,InputTokens,OutputTokens '
                    'FROM RequestLogs WHERE ConversationKey=? ORDER BY CreatedAt', ("thread:" + thread,)).fetchall())
        failed = [row for row in logs if row[1] == "main" and row[2] == "failed" and row[3] >= 400]
        if not failed or not any(row[1] == "attempt" and row[2] == "failed" for row in logs):
            failures.append("failure was not recorded in both main and channel-attempt logs")
        if not failed or not any(row[1] == "main" and row[2] == "success" and row[4] > failed[0][4] for row in logs):
            failures.append("no successful real model request after the logged failure")
        recovery = {"fault_events": faults, "request_logs": logs,
                    "marker_lines": marker.read_text().splitlines() if marker.exists() else []}
    summary = {"scenario": args.scenario, "model": MODEL, "codex_version": version,
               "base_url": args.base_url, "exit_code": code, "timed_out": timed_out,
               "started_at": started_at, "ended_at": ended_at,
               "root_threads": threads, "native_collaboration_events": collaboration,
               "native_session_audit": native,
               "recovery_audit": recovery,
               "completed": completed, "failures": failures,
               "passed": not failures}
    (output / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2))
    print(json.dumps({key: value for key, value in summary.items()
                      if key != "native_collaboration_events"}, ensure_ascii=False))
    raise SystemExit(1 if failures else 0)


if __name__ == "__main__":
    main()
