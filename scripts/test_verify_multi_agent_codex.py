import unittest
import json
import tempfile
from pathlib import Path

import verify_multi_agent_codex as verifier


class CommandExecutionAuditTests(unittest.TestCase):
    def test_rollout_fixture_execution_does_not_require_equal_inner_outer_ids(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory); home = base / 'home'; sessions = home / 'sessions'
            sessions.mkdir(parents=True); (base / 'work').mkdir()
            (base / 'work' / 'alpha.txt').write_text('SAFE_OUTPUT')
            def row(at, kind, payload):
                return {'timestamp': '2026-10-06T00:00:0' + str(at) + 'Z', 'type': kind, 'payload': payload}
            root = [row(0, 'session_meta', {'id': 'root'}), row(0, 'turn_context', {'turn_id': 'root-turn', 'model': verifier.MODEL})]
            child = [row(0, 'session_meta', {'id': 'child', 'source': {'subagent': {'thread_spawn': {'parent_thread_id': 'root', 'agent_path': '/root/alpha'}}}}),
                     row(0, 'turn_context', {'turn_id': 'turn-a', 'model': verifier.MODEL}),
                     row(1, 'response_item', {'type': 'custom_tool_call', 'call_id': 'call-outer', 'name': 'exec', 'namespace': 'functions'}),
                     row(2, 'event_msg', {'type': 'item_completed', 'turn_id': 'turn-a', 'item': {'type': 'CommandExecution', 'id': 'exec-inner', 'status': 'completed', 'exit_code': 0, 'aggregated_output': 'SAFE_OUTPUT'}}),
                     row(3, 'response_item', {'type': 'custom_tool_call_output', 'call_id': 'call-outer', 'output': [{'type': 'input_text', 'text': 'SAFE_OUTPUT'}]}),
                     row(4, 'event_msg', {'type': 'task_complete', 'turn_id': 'turn-a', 'last_agent_message': 'ALPHA_OK_63'})]
            for name, rows in [('root', root), ('child', child)]:
                (sessions / (name + '.jsonl')).write_text('\n'.join(json.dumps(item) for item in rows))
            _, failures = verifier.audit_native_sessions(home, ['root'])
            self.assertNotIn('missing successful fixture tool execution and result in /root/alpha', failures)
            # A real exit 0 remains a process fact, but a failed outer wrapper cannot prove the fixture chain.
            child[4]['payload']['output'] = {'output': 'SAFE_OUTPUT', 'isError': True}
            (sessions / 'child.jsonl').write_text('\n'.join(json.dumps(item) for item in child))
            audit, failures = verifier.audit_native_sessions(home, ['root'])
            self.assertEqual(audit['command_execution_audit']['/root/alpha']['exit_code_counts'], {'0': 1})
            self.assertIn('missing successful fixture tool execution and result in /root/alpha', failures)

    def actor(self, exit_code=0, output='SAFE_OUTPUT', turn='turn-a'):
        return {
            'commands': [{'id': 'exec-internal', 'turn': turn, 'time': 2.0,
                          'status': 'completed', 'exit_code': exit_code,
                          'aggregated_output': output}],
            'calls': {'call-outer': {'name': 'exec', 'namespace': 'functions',
                                   'turn': 'turn-a', 'time': 1.0,
                                   'results': [{'time': 3.0, 'output': [
                                       {'type': 'input_text', 'text': 'Script completed\nOutput:'},
                                       {'type': 'input_text', 'text': output}]}]}}}

    def test_different_inner_outer_ids_match_only_with_actual_success(self):
        result = verifier.audit_command_executions(self.actor())
        command = result['commands'][0]
        self.assertEqual(command['matched_call_id'], 'call-outer')
        self.assertEqual(command['association'], 'matched')
        self.assertTrue(command['verified_success'])
        self.assertEqual(result['verified_success_count'], 1)

    def test_outer_script_completed_does_not_override_exit_seven(self):
        result = verifier.audit_command_executions(self.actor(exit_code=7))
        self.assertEqual(result['exit_code_counts'], {'7': 1})
        self.assertFalse(result['commands'][0]['verified_success'])
        self.assertEqual(result['nonzero_exit_count'], 1)

    def test_missing_and_boolean_exit_codes_are_not_success(self):
        for code in (None, False, True, '0'):
            with self.subTest(code=code):
                result = verifier.audit_command_executions(self.actor(exit_code=code))
                self.assertFalse(result['commands'][0]['verified_success'])
                self.assertEqual(result['missing_or_invalid_exit_count'], 1)

    def test_missing_or_wrong_status_is_not_success(self):
        for status in (None, 'failed', 'in_progress'):
            actor = self.actor(); actor['commands'][0]['status'] = status
            self.assertFalse(verifier.audit_command_executions(actor)['commands'][0]['verified_success'])

    def test_other_turn_outside_interval_and_missing_output_do_not_match(self):
        actors = [self.actor(turn='other'), self.actor(output='')]
        actor = self.actor(); actor['commands'][0]['time'] = 4.0; actors.append(actor)
        actor = self.actor(); actor['commands'][0]['turn'] = None; actors.append(actor)
        actor = self.actor(); actor['calls']['call-outer']['results'] = []; actors.append(actor)
        actor = self.actor(); actor['calls']['call-outer']['results'][0]['output'][1]['text'] = 'PREFIX SAFE_OUTPUT SUFFIX'; actors.append(actor)
        for actor in actors:
            result = verifier.audit_command_executions(actor)
            self.assertEqual(result['commands'][0]['association'], 'unmatched')
            self.assertFalse(result['commands'][0]['verified_success'])

    def test_ambiguous_same_turn_matching_calls_fail_closed(self):
        actor = self.actor(); actor['calls']['call-other'] = dict(actor['calls']['call-outer'])
        result = verifier.audit_command_executions(actor)
        self.assertEqual(result['commands'][0]['association'], 'ambiguous')
        self.assertIsNone(result['commands'][0]['matched_call_id'])
        self.assertFalse(result['commands'][0]['verified_success'])

    def test_structured_printed_result_preserves_match(self):
        actor = self.actor()
        actor['calls']['call-outer']['results'][0]['output'] = [
            {'type': 'input_text', 'text': '{"output":"SAFE_OUTPUT","exit_code":0}'}]
        self.assertTrue(verifier.audit_command_executions(actor)['commands'][0]['verified_success'])

    def test_only_execution_or_polling_wrappers_can_match(self):
        for name, namespace in [('wait_agent', 'collaboration'), ('exec', 'other'), ('lookup', None)]:
            actor = self.actor(); actor['calls']['call-outer'].update(name=name, namespace=namespace)
            self.assertEqual(verifier.audit_command_executions(actor)['commands'][0]['association'], 'unmatched')
        for name in ('wait', 'exec_command', 'write_stdin'):
            actor = self.actor(); actor['calls']['call-outer']['name'] = name
            self.assertEqual(verifier.audit_command_executions(actor)['commands'][0]['association'], 'matched')


if __name__ == '__main__':
    unittest.main()
