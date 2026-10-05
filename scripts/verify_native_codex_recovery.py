#!/usr/bin/env python3
"""Real Codex 0.160 recovery and resource-boundary checks against loopback OpenCodex."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import time

from verify_native_codex_lifecycle import AppServer, Verification, DEFAULT_CODEX, MODEL, require


class Recovery(Verification):
    def __init__(self, args, home, work, output, schemas):
        self.args = args
        self.connection_number = 0
        super().__init__(None, home, work, output, schemas)
        self.connect()

    def connect(self):
        self.connection_number += 1
        connection = self.output / ('connection-' + str(self.connection_number))
        connection.mkdir()
        self.app = AppServer(self.args.codex_bin, self.home, self.work, connection,
                             self.args.api_key_env, self.args.timeout)
        self.rpc('initialize', 'InitializeParams', {'clientInfo': {'name': 'opencodex_recovery_acceptance', 'version': '1.0'},
                                                  'capabilities': {'experimentalApi': True}})
        self.app.send({'method': 'initialized'})

    def tools_for_turn(self, thread, turn):
        current, calls, outputs = None, [], {}
        for row in self.rollout(thread):
            value = row.get('payload', {})
            if row.get('type') == 'turn_context':
                current = value.get('turn_id')
            if row.get('type') != 'response_item' or current != turn:
                continue
            if value.get('type') in ('function_call', 'custom_tool_call'):
                calls.append(value)
            elif value.get('type') in ('function_call_output', 'custom_tool_call_output'):
                outputs.setdefault(value.get('call_id'), []).append(value.get('output'))
        return calls, outputs

    def cells(self):
        thread = self.new_thread()
        marker = 'CELLS_CHECKED_' + self.nonce
        source = '// @exec: {"yield_time_ms": 1, "max_output_tokens": 1000}\nawait new Promise(resolve => setTimeout(resolve, 30000)); text("UNEXPECTED_CELL_COMPLETION");'
        prompt = ('This is an authorized negative-path tool test. Use the real functions.wait with cell_id="999999999" once and observe its error. '
                  'Then call functions.exec exactly once with the exact JavaScript below. It must yield a running cell. '
                  'Call functions.wait with the returned real cell_id and terminate=true. Then call functions.wait again with that same cell_id '
                  'and observe that it no longer exists. Never retry exec, never call shell or other tools. '
                  'After all four real tool calls, reply exactly ' + marker + '.\n' + source)
        turn = self.complete(thread, prompt, marker, 'cell_terminate_invalid')
        calls, outputs = self.tools_for_turn(thread, turn)
        execs = [item for item in calls if item.get('name') == 'exec']
        waits = [item for item in calls if item.get('name') == 'wait']
        require(len(execs) == 1 and len(waits) == 3, 'Expected one real exec and three real wait calls')
        require(execs[0]['type'] == 'custom_tool_call', 'exec lost custom shape')
        match = re.search(r'Script running with cell ID ([0-9]+)', json.dumps(outputs.get(execs[0]['call_id'])))
        require(match is not None, 'exec did not yield a live cell')
        args = [json.loads(item['arguments']) for item in waits]
        require(str(args[0]['cell_id']) == '999999999', 'Missing invalid-cell call')
        require(str(args[1]['cell_id']) == match[1] and args[1].get('terminate') is True, 'Did not terminate actual cell')
        require(str(args[2]['cell_id']) == match[1] and not args[2].get('terminate'), 'Did not recheck terminated cell')
        rendered = [json.dumps(outputs.get(item['call_id'], [])) for item in waits]
        for value in (rendered[0], rendered[2]):
            require(re.search(r'not found|unknown|invalid|does not exist|no .*cell|not running', value, re.I), 'No actual missing-cell error: ' + value)
        require(re.search(r'terminat|cancel', rendered[1], re.I), 'No actual termination result: ' + rendered[1])
        require('UNEXPECTED_CELL_COMPLETION' not in ''.join(rendered), 'Terminated cell completed anyway')
        (self.output / 'cell-tools.json').write_text(json.dumps({'calls': calls, 'outputs': outputs}, indent=2))

    def cleanup(self):
        thread = self.new_thread()
        ready, late = self.work / 'cleanup.ready', self.work / 'cleanup.late'
        command = "python3 -c 'from pathlib import Path; import time; Path(\"cleanup.ready\").write_text(\"ready\"); time.sleep(20); Path(\"cleanup.late\").write_text(\"late\")'"
        source = 'text(await tools.exec_command(' + json.dumps({'cmd': command, 'workdir': str(self.work), 'yield_time_ms': 1000}) + '));\nawait new Promise(resolve => setTimeout(resolve, 30000));'
        turn, offset = self.start(thread, 'Call functions.exec exactly once with this JavaScript and await completion. No other tools.\n' + source)
        self.wait_file(ready, thread, turn, offset)
        started = time.monotonic()
        owned = []
        while time.monotonic() < started + 12:
            terminals = self.rpc('thread/backgroundTerminals/list', 'ThreadBackgroundTerminalsListParams', {'threadId': thread})
            owned = [item for item in terminals['data'] if 'cleanup.ready' in item['command']]
            if owned:
                break
            time.sleep(.2)
        self.rpc('turn/interrupt', 'TurnInterruptParams', {'threadId': thread, 'turnId': turn})
        self.finish(thread, turn, offset, status='interrupted', label='cleanup_interrupted')
        require(len(owned) == 1, 'Interrupted command must be listed as one actual background terminal: ' + json.dumps(terminals))
        result = self.rpc('thread/backgroundTerminals/terminate', 'ThreadBackgroundTerminalsTerminateParams',
                          {'threadId': thread, 'processId': owned[0]['processId']})
        require(result['terminated'] is True, 'Explicit terminal termination did not terminate actual process')
        after = self.rpc('thread/backgroundTerminals/list', 'ThreadBackgroundTerminalsListParams', {'threadId': thread})
        require(not any(item['processId'] == owned[0]['processId'] for item in after['data']), 'Terminated process remains listed')
        marker = 'AFTER_CLEANUP_' + self.nonce
        self.complete(thread, 'Do not resume old work or use tools. Reply exactly ' + marker, marker, 'after_cleanup')
        while time.monotonic() < started + 22:
            time.sleep(.2)
        require(not late.exists(), 'Process produced a late side effect after explicit termination')
        self.boundaries['cleanup'] = {'terminals_before': terminals, 'terminate_result': result, 'terminals_after': after,
                                      'late_side_effect_observed': late.exists(), 'checked_after_original_deadline': True}

    def reconnect(self):
        thread = self.new_thread()
        marker = 'REMEMBER_' + self.nonce
        first = self.complete(thread, 'Remember this value and answer it exactly: ' + marker, marker, 'before_disconnect')
        self.app.close()
        self.connect()
        resumed = self.rpc('thread/resume', 'ThreadResumeParams', {'threadId': thread, 'model': MODEL,
                           'modelProvider': 'local_e2e', 'cwd': str(self.work), 'approvalPolicy': 'never', 'sandbox': 'workspace-write'})
        require(resumed['thread']['id'] == thread, 'Resume changed thread identity')
        require(any(item['id'] == first for item in resumed['thread']['turns']), 'Resume lost completed history')
        expected = 'RESUMED|' + marker
        self.complete(thread, 'New question: return RESUMED| followed by the exact value I asked you to remember. Do not use tools.',
                      expected, 'after_disconnect')
        (self.output / 'resume-response.json').write_text(json.dumps(resumed, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-url', required=True)
    parser.add_argument('--output-dir', required=True, type=Path)
    parser.add_argument('--native-trace-log', required=True, type=Path)
    parser.add_argument('--transport', required=True, choices=['http', 'websocket'])
    parser.add_argument('--api-key-env', default='OCXP_E2E_API_KEY')
    parser.add_argument('--codex-bin', default=DEFAULT_CODEX)
    parser.add_argument('--timeout', type=int, default=240)
    args = parser.parse_args()
    require(os.environ.get(args.api_key_env), 'Missing API key environment variable')
    subprocess.run(['python3', str(Path(__file__).with_name('verify_native_codex_lifecycle.py')), '--prepare-only',
                    '--base-url', args.base_url, '--output-dir', str(args.output_dir), '--codex-bin', args.codex_bin], check=True)
    output = args.output_dir.resolve()
    home, work, schemas = output / 'home', output / 'work', output / 'schema'
    config = home / 'config.toml'
    config.write_text(config.read_text().replace('requires_openai_auth = false', 'requires_openai_auth = false\nsupports_websockets = '
                                                + str(args.transport == 'websocket').lower() + '\nwebsocket_connect_timeout_ms = 5000'))
    env = os.environ.copy()
    env['CODEX_HOME'] = str(home)
    probe = subprocess.run([args.codex_bin, 'app-server', '--strict-config', '--stdio'], input='', capture_output=True, text=True, env=env, timeout=15)
    require(probe.returncode == 0, 'Installed binary rejected transport config: ' + probe.stderr)
    verification = Recovery(args, home, work, output, schemas)
    try:
        for name, action in [('cell_terminate_and_invalid_id', verification.cells), ('interrupt_then_terminate_process', verification.cleanup),
                             ('client_disconnect_and_resume', verification.reconnect)]:
            verification.run_check(name, action)
    finally:
        verification.app.close()
        trace = args.native_trace_log.read_text()
        routes = [line.strip() for line in trace.splitlines() if 'Native client response: thread=' in line
                  and any('thread=' + thread + ' ' in line for thread in verification.threads)]
        missing = [thread for thread in verification.threads if not any('thread=' + thread + ' ' in line
                   and 'transport=' + args.transport in line for line in routes)]
        wrong = [line for line in routes if 'transport=' + args.transport not in line]
        passed = len(verification.checks) == 3 and all(item['passed'] for item in verification.checks) and not missing and not wrong
        summary = {'model': MODEL, 'transport': args.transport, 'checks': verification.checks, 'turns': verification.turns,
                   'threads': verification.threads, 'boundaries': verification.boundaries,
                   'native_route_audit': {'routes': routes, 'missing_threads': missing, 'unexpected_transport': wrong}, 'passed': passed}
        (output / 'summary.json').write_text(json.dumps(summary, indent=2))
        print(json.dumps(summary), flush=True)
    return 0 if passed else 1


if __name__ == '__main__':
    raise SystemExit(main())
