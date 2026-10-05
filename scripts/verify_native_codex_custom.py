#!/usr/bin/env python3
"""Real custom-tool fidelity, error recovery and parallel execution checks."""
import argparse
import json
import os
from pathlib import Path
import shlex
import subprocess

from verify_native_codex_lifecycle import DEFAULT_CODEX, MODEL, require, verify_cached_model
from verify_native_codex_recovery import Recovery


def output_text(value):
    if isinstance(value, str):
        return value
    if isinstance(value, list):
        return '\n'.join(output_text(item) for item in value)
    if isinstance(value, dict):
        return output_text(value.get('text', value.get('output', '')))
    return ''


class CustomVerification(Recovery):
    def capture(self, thread, turn, label, expected_sources):
        calls, outputs = self.tools_for_turn(thread, turn)
        (self.output / (label + '-tools.json')).write_text(json.dumps({'calls': calls, 'outputs': outputs}, indent=2, ensure_ascii=False))
        require(len(calls) == len(expected_sources), label + ': unexpected tool count ' + str(len(calls)))
        for call, source in zip(calls, expected_sources):
            require(call.get('name') == 'exec' and call.get('type') == 'custom_tool_call', label + ': exec custom shape lost')
            require(call.get('input') == source, label + ': raw custom input changed')
        evidence = {'calls': calls, 'outputs': outputs}
        (self.output / (label + '-tools.json')).write_text(json.dumps(evidence, indent=2, ensure_ascii=False))
        return [output_text(outputs.get(call['call_id'], [])) for call in calls]

    def fidelity(self):
        thread = self.new_thread()
        marker = 'FIDELITY_' + self.nonce
        # Outer whitespace and inner escape spelling are part of the wire contract.
        source = '  \nconst message = "空 格 😀\\n第二行\\tTAB\\\\路径\\\"引用";\n  text(message);\ntext("' + marker + '");\n  \n'
        prompt = ('Call the real functions.exec exactly once. Its required raw JavaScript input is the JSON string below: decode the JSON string once and submit the exact decoded text as custom tool input, including every leading/trailing space and newline. '
                  'Do not use markdown fences or other tools. Do not narrate or emit any intermediate assistant messages. '
                  'After observing output reply exactly ' + marker + '. JSON-encoded required input:\n' + json.dumps(source, ensure_ascii=False))
        turn = self.complete(thread, prompt, marker, 'fidelity')
        outputs = self.capture(thread, turn, 'fidelity', [source])
        require('空 格 😀\n第二行\tTAB\\路径"引用' in outputs[0], 'Unicode or escaped output changed: ' + outputs[0])
        require(marker in outputs[0], 'Missing real fidelity marker')

    def error_recovery(self, syntax=False):
        thread = self.new_thread()
        label = 'syntax_recovery' if syntax else 'throw_recovery'
        marker = label.upper() + '_' + self.nonce
        bad = 'const broken = ;' if syntax else 'throw new Error("EXPECTED_THROW_' + self.nonce + '");'
        good = 'text("' + marker + '");'
        prompt = ('This is an authorized negative-path tool test. In this same turn call functions.exec with exactly this input first:\n'
                  + bad + '\nObserve its actual error, then call functions.exec again with exactly this valid input:\n' + good
                  + '\nDo not retry the bad input or use other tools. After observing successful recovery reply exactly ' + marker + '.')
        turn = self.complete(thread, prompt, marker, label)
        outputs = self.capture(thread, turn, label, [bad, good])
        expected = 'SyntaxError' if syntax else 'EXPECTED_THROW_' + self.nonce
        require(expected in outputs[0], label + ': no actual expected error: ' + outputs[0])
        require(marker in outputs[1], label + ': no successful real recovery output')

    def parallel(self):
        thread = self.new_thread()
        marker = 'PARALLEL_' + self.nonce
        commands = []
        for index in range(3):
            name = 'parallel-' + str(index) + '.txt'
            value = marker + '_' + str(index)
            code = 'from pathlib import Path; Path(' + repr(name) + ').open("a").write(' + repr(value + '\n') + '); print(' + repr(value) + ')'
            commands.append({'cmd': 'python3 -c ' + shlex.quote(code), 'workdir': str(self.work), 'yield_time_ms': 10000, 'max_output_tokens': 1000})
        source = 'const results = await Promise.all([\n' + ',\n'.join('  tools.exec_command(' + json.dumps(command) + ')' for command in commands) + '\n]);\nfor (const result of results) text(result);'
        prompt = ('Call functions.exec exactly once with this exact JavaScript, which invokes three real tools in parallel. '
                  'Do not rerun it or invoke other tools. After all three real outputs succeed reply exactly ' + marker + '.\n' + source)
        turn = self.complete(thread, prompt, marker, 'parallel_tools')
        outputs = self.capture(thread, turn, 'parallel', [source])
        for index in range(3):
            expected = marker + '_' + str(index)
            require((self.work / ('parallel-' + str(index) + '.txt')).read_text().splitlines() == [expected], 'Parallel tool executed zero or multiple times')
            require(expected in outputs[0], 'Missing actual parallel output ' + expected)



class PatchVerification(Recovery):
    def patch_turn(self, label, patches, expected_contents, expect_error=False):
        marker = "PATCH_" + label.upper() + "_" + self.nonce
        prompt = ("Use only the real apply_patch custom tool. Do not use shell, exec or other tools and do not narrate. "
                  "The following JSON array contains exact raw patch inputs; decode each string and submit it unchanged, in order. "
                  + ("The first patch intentionally has invalid context: observe its failure, then apply the second valid patch in this same turn. " if expect_error else "")
                  + "After successful completion reply exactly " + marker + ".\n" + json.dumps(patches, ensure_ascii=False))
        turn = self.complete(self.patch_thread, prompt, marker, label)
        calls, outputs = self.tools_for_turn(self.patch_thread, turn)
        contents = self.patch_path.read_text() if self.patch_path.exists() else None
        (self.output / (label + "-tools.json")).write_text(json.dumps({"calls": calls, "outputs": outputs,
            "file_contents": contents}, indent=2, ensure_ascii=False))
        require(len(calls) == len(patches), "Wrong number of native apply_patch calls")
        for call, patch in zip(calls, patches):
            require(call.get("name") == "apply_patch" and call.get("type") == "custom_tool_call", "Not native custom apply_patch")
            require(call.get("input") == patch, "Raw native patch input changed")
        if expect_error:
            failure = output_text(outputs.get(calls[0]["call_id"], []))
            require("failed" in failure.lower() or "error" in failure.lower(), "Expected actual patch context error")
        require("Success" in output_text(outputs.get(calls[-1]["call_id"], [])), "No real successful patch result")
        require(contents == expected_contents, "Native patch file result mismatch")

    def create(self):
        self.patch_thread = self.new_thread()
        entry = verify_cached_model(self.home, MODEL, self.output)
        require(entry["apply_patch_tool_type"] == "freeform", "Actual client catalog did not declare freeform patch")
        self.patch_path = self.work / "native-patch.txt"
        patch = "*** Begin Patch\n*** Add File: " + str(self.patch_path) + "\n+原生 patch 😀\n*** End Patch"
        self.patch_turn("patch_add", [patch], "原生 patch 😀\n")

    def update(self):
        patch = "*** Begin Patch\n*** Update File: " + str(self.patch_path) + "\n@@\n-原生 patch 😀\n+更新 patch 中文\n*** End Patch"
        self.patch_turn("patch_update", [patch], "更新 patch 中文\n")

    def recover(self):
        prefix = "*** Begin Patch\n*** Update File: " + str(self.patch_path) + "\n@@\n"
        invalid = prefix + "-THIS_CONTEXT_DOES_NOT_EXIST\n+不应写入\n*** End Patch"
        valid = prefix + "-更新 patch 中文\n+恢复 patch 😀\n*** End Patch"
        self.patch_turn("patch_error_recovery", [invalid, valid], "恢复 patch 😀\n", True)

    def delete(self):
        self.patch_turn("patch_delete", ["*** Begin Patch\n*** Delete File: " + str(self.patch_path) + "\n*** End Patch"], None)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-url', required=True)
    parser.add_argument('--output-dir', required=True, type=Path)
    parser.add_argument('--native-trace-log', required=True, type=Path)
    parser.add_argument('--transport', required=True, choices=['http', 'websocket'])
    parser.add_argument('--api-key-env', default='OCXP_E2E_API_KEY')
    parser.add_argument('--codex-bin', default=DEFAULT_CODEX)
    parser.add_argument('--timeout', type=int, default=240)
    parser.add_argument('--tool', choices=['exec', 'apply_patch'], default='exec')
    args = parser.parse_args()
    require(os.environ.get(args.api_key_env), 'Missing API key environment variable')
    subprocess.run(['python3', str(Path(__file__).with_name('verify_native_codex_lifecycle.py')), '--prepare-only', '--base-url', args.base_url,
                    '--output-dir', str(args.output_dir), '--codex-bin', args.codex_bin], check=True)
    output = args.output_dir.resolve()
    home, work, schemas = output / 'home', output / 'work', output / 'schema'
    config = home / 'config.toml'
    config.write_text(config.read_text().replace('requires_openai_auth = false', 'requires_openai_auth = false\nsupports_websockets = '
                                                + str(args.transport == 'websocket').lower() + '\nwebsocket_connect_timeout_ms = 5000'))
    if args.tool == 'apply_patch':
        config.write_text(config.read_text().replace('code_mode = true', 'code_mode = false'))
    env = os.environ.copy()
    env['CODEX_HOME'] = str(home)
    probe = subprocess.run([args.codex_bin, 'app-server', '--strict-config', '--stdio'], input='', capture_output=True, text=True, env=env, timeout=15)
    require(probe.returncode == 0, 'Installed binary rejected transport config: ' + probe.stderr)
    verification = (PatchVerification if args.tool == 'apply_patch' else CustomVerification)(args, home, work, output, schemas)
    checks = ([('patch_add', verification.create), ('patch_update', verification.update), ('patch_error_recovery', verification.recover),
               ('patch_delete', verification.delete)] if args.tool == 'apply_patch' else
              [('multiline_unicode_escapes', verification.fidelity), ('throw_then_recover_same_turn', verification.error_recovery),
               ('syntax_error_then_recover_same_turn', lambda: verification.error_recovery(True)), ('parallel_real_tools_once', verification.parallel)])
    try:
        for name, action in checks:
            verification.run_check(name, action)
    finally:
        verification.app.close()
        trace = args.native_trace_log.read_text()
        routes = [line.strip() for line in trace.splitlines() if 'Native client response: thread=' in line
                  and any('thread=' + thread + ' ' in line for thread in verification.threads)]
        missing = [thread for thread in verification.threads if not any('thread=' + thread + ' ' in line and 'transport=' + args.transport in line for line in routes)]
        wrong = [line for line in routes if 'transport=' + args.transport not in line]
        passed = len(verification.checks) == 4 and all(item['passed'] for item in verification.checks) and not missing and not wrong
        summary = {'model': MODEL, 'tool': args.tool, 'transport': args.transport, 'checks': verification.checks, 'turns': verification.turns,
                   'threads': verification.threads, 'native_route_audit': {'routes': routes, 'missing_threads': missing, 'unexpected_transport': wrong}, 'passed': passed}
        (output / 'summary.json').write_text(json.dumps(summary, indent=2, ensure_ascii=False))
        print(json.dumps(summary, ensure_ascii=False), flush=True)
    return 0 if passed else 1


if __name__ == '__main__':
    raise SystemExit(main())
