import json
import unittest

from opencodex_proxy.app import _has_tool_material
from opencodex_proxy.protocols import convert_request, convert_response


class ProtocolTests(unittest.TestCase):
    def test_responses_string_input_to_chat_user_message(self):
        payload = {
            "model": "local",
            "input": "ping",
            "temperature": 0.3,
        }

        result = convert_request(payload, "responses", "chat", "upstream")

        self.assertEqual(result["model"], "upstream")
        self.assertEqual(result["messages"], [{"role": "user", "content": "ping"}])
        self.assertEqual(result["temperature"], 0.3)

    def test_responses_request_to_chat(self):
        payload = {
            "model": "local",
            "instructions": "be brief",
            "input": [{"role": "user", "content": [{"type": "input_text", "text": "hi"}]}],
            "tools": [
                {
                    "type": "function",
                    "name": "lookup",
                    "description": "Lookup data",
                    "parameters": {"type": "object", "properties": {"q": {"type": "string"}}},
                }
            ],
            "max_output_tokens": 32,
        }
        result = convert_request(payload, "responses", "chat", "upstream")
        self.assertEqual(result["model"], "upstream")
        self.assertEqual(result["messages"][0]["role"], "system")
        self.assertEqual(result["messages"][1]["content"], "hi")
        self.assertEqual(result["tools"][0]["function"]["name"], "lookup")
        self.assertEqual(result["max_tokens"], 32)

    def test_responses_request_to_chat_preserves_mixed_history_and_native_tools(self):
        payload = {
            "model": "local",
            "instructions": "system rule",
            "input": [
                {"role": "developer", "content": [{"type": "input_text", "text": "dev hint"}]},
                {"role": "user", "content": [{"type": "input_text", "text": "hi"}]},
                {"type": "reasoning", "summary": [{"type": "summary_text", "text": "checked"}]},
                {
                    "type": "web_search_call",
                    "status": "completed",
                    "action": {"type": "search", "query": "weather"},
                },
                {
                    "type": "custom_tool_call",
                    "call_id": "call_patch",
                    "name": "apply_patch",
                    "input": "*** Begin Patch\n*** End Patch",
                },
                {
                    "type": "custom_tool_call_output",
                    "call_id": "call_patch",
                    "output": "patched",
                },
                {
                    "role": "assistant",
                    "content": [{"type": "output_text", "text": "done"}],
                },
            ],
            "tools": [
                {
                    "type": "function",
                    "name": "lookup",
                    "description": "Lookup data",
                    "parameters": {"type": "object", "properties": {"q": {"type": "string"}}},
                },
                {"type": "local_shell", "description": "Run shell"},
                {"type": "apply_patch", "description": "Apply a patch"},
            ],
            "tool_choice": {"type": "function", "function": {"name": "lookup"}},
            "max_output_tokens": 64,
            "temperature": 0.2,
        }

        result = convert_request(payload, "responses", "chat", "upstream")

        self.assertEqual(result["model"], "upstream")
        self.assertEqual(result["messages"][0], {"role": "system", "content": "system rule"})
        self.assertEqual(result["messages"][1], {"role": "system", "content": "dev hint"})
        self.assertEqual(result["messages"][2], {"role": "user", "content": "hi"})
        self.assertIn("checked", result["messages"][3]["content"])
        self.assertIn("web_search_call", result["messages"][4]["content"])
        self.assertEqual(result["messages"][5]["role"], "assistant")
        self.assertEqual(result["messages"][5]["tool_calls"][0]["id"], "call_patch")
        self.assertEqual(
            result["messages"][5]["tool_calls"][0]["function"]["arguments"],
            '{"patch": "*** Begin Patch\\n*** End Patch"}',
        )
        self.assertEqual(
            result["messages"][6],
            {"role": "tool", "tool_call_id": "call_patch", "content": "patched"},
        )
        self.assertEqual(result["messages"][7], {"role": "assistant", "content": "done"})
        self.assertEqual(result["tools"][0]["function"]["name"], "lookup")
        self.assertEqual(result["tools"][1]["function"]["name"], "local_shell")
        self.assertEqual(result["tools"][2]["function"]["name"], "apply_patch")
        self.assertEqual(result["tool_choice"], {"type": "function", "function": {"name": "lookup"}})
        self.assertEqual(result["max_tokens"], 64)
        self.assertEqual(result["temperature"], 0.2)

    def test_apply_patch_arguments_preserve_complex_json_objects(self):
        complex_arguments = (
            '{"patch":"*** Begin Patch\\n*** Update File: a.txt\\n@@\\n-old\\n+new\\n'
            '*** End Patch","metadata":{"paths":["a.txt","dir/b.json"],'
            '"flags":{"dry_run":false,"attempt":2},"notes":[{"kind":"unicode","text":"保持"}]}}'
        )

        cases = [
            (
                {
                    "type": "function_call",
                    "call_id": "call_patch_json",
                    "name": "apply_patch",
                    "arguments": complex_arguments,
                },
                complex_arguments,
            ),
            (
                {
                    "type": "custom_tool_call",
                    "call_id": "call_patch_input_json",
                    "name": "apply_patch",
                    "input": complex_arguments,
                },
                complex_arguments,
            ),
            (
                {
                    "type": "apply_patch_call",
                    "call_id": "call_patch_native_json",
                    "input": complex_arguments,
                },
                complex_arguments,
            ),
        ]

        for item, expected_arguments in cases:
            result = convert_request({"model": "local", "input": [item]}, "responses", "chat", "upstream")
            arguments = result["messages"][0]["tool_calls"][0]["function"]["arguments"]
            self.assertEqual(arguments, expected_arguments)

    def test_apply_patch_arguments_wrap_non_object_values(self):
        cases = [
            (
                {
                    "type": "custom_tool_call",
                    "call_id": "call_raw_patch",
                    "name": "apply_patch",
                    "input": "*** Begin Patch\n*** End Patch",
                },
                "*** Begin Patch\n*** End Patch",
            ),
            (
                {
                    "type": "function_call",
                    "call_id": "call_json_array",
                    "name": "apply_patch",
                    "arguments": '["not", "an", "object"]',
                },
                '["not", "an", "object"]',
            ),
            (
                {
                    "type": "apply_patch_call",
                    "call_id": "call_input_dict",
                    "input": {"input": "*** Begin Patch\n*** End Patch"},
                },
                "*** Begin Patch\n*** End Patch",
            ),
        ]

        for item, expected_patch in cases:
            result = convert_request({"model": "local", "input": [item]}, "responses", "chat", "upstream")
            arguments = result["messages"][0]["tool_calls"][0]["function"]["arguments"]
            self.assertEqual(json.loads(arguments), {"patch": expected_patch})

    def test_responses_request_to_messages_preserves_supported_history_items(self):
        payload = {
            "model": "local",
            "input": [
                {"role": "user", "content": [{"type": "input_text", "text": "hi"}]},
                {"type": "reasoning", "summary": [{"type": "summary_text", "text": "checked"}]},
                {"type": "reasoning", "summary": []},
                {
                    "type": "web_search_call",
                    "status": "completed",
                    "action": {"type": "search", "query": "weather"},
                },
                {"role": "assistant", "content": []},
                {
                    "type": "custom_tool_call",
                    "call_id": "call_patch",
                    "name": "apply_patch",
                    "input": "*** Begin Patch\n*** End Patch",
                },
                {
                    "type": "custom_tool_call_output",
                    "call_id": "call_patch",
                    "output": "patched",
                },
                {
                    "type": "function_call",
                    "call_id": "call_1",
                    "name": "lookup",
                    "arguments": "{\"q\":\"x\"}",
                },
                {
                    "type": "function_call_output",
                    "call_id": "call_1",
                    "output": "result",
                },
            ],
        }
        result = convert_request(payload, "responses", "messages", "upstream")

        self.assertEqual(result["model"], "upstream")
        self.assertEqual(len(result["messages"]), 7)
        self.assertEqual(result["messages"][0]["content"][0]["text"], "hi")
        self.assertIn("checked", result["messages"][1]["content"][0]["text"])
        self.assertIn("web_search_call", result["messages"][2]["content"][0]["text"])
        self.assertEqual(result["messages"][3]["content"][0]["type"], "tool_use")
        self.assertEqual(result["messages"][3]["content"][0]["name"], "apply_patch")
        self.assertEqual(result["messages"][4]["content"][0]["type"], "tool_result")
        self.assertEqual(result["messages"][5]["content"][0]["type"], "tool_use")
        self.assertEqual(result["messages"][6]["content"][0]["type"], "tool_result")
        self.assertFalse(
            any(message.get("content") in ("", [], None) for message in result["messages"])
        )

    def test_has_tool_material_recognizes_tool_outputs(self):
        for item_type in (
            "custom_tool_call_output",
            "local_shell_call_output",
            "shell_call_output",
            "apply_patch_call_output",
        ):
            self.assertTrue(
                _has_tool_material({"input": [{"type": item_type, "call_id": "call_1"}]}),
                item_type,
            )

    def test_chat_response_to_responses(self):
        payload = {
            "id": "chatcmpl_1",
            "model": "upstream",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": "hello",
                        "tool_calls": [
                            {
                                "id": "call_1",
                                "type": "function",
                                "function": {"name": "lookup", "arguments": "{\"q\":\"x\"}"},
                            }
                        ],
                    },
                    "finish_reason": "tool_calls",
                }
            ],
            "usage": {"prompt_tokens": 1, "completion_tokens": 2, "total_tokens": 3},
        }
        result = convert_response(payload, "responses", "chat", "local")
        self.assertEqual(result["model"], "local")
        self.assertEqual(result["output"][0]["content"][0]["text"], "hello")
        self.assertEqual(result["output"][1]["name"], "lookup")

    def test_chat_response_to_responses_with_tool_calls_only(self):
        payload = {
            "id": "chatcmpl_2",
            "model": "upstream",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": "",
                        "reasoning_content": "need tool",
                        "tool_calls": [
                            {
                                "id": "call_2",
                                "type": "function",
                                "function": {"name": "exec_command", "arguments": "{\"cmd\":\"pwd\"}"},
                            }
                        ],
                    },
                    "finish_reason": "tool_calls",
                }
            ],
            "usage": {"prompt_tokens": 2, "completion_tokens": 3, "total_tokens": 5},
        }

        result = convert_response(payload, "responses", "chat", "visible-model")

        self.assertEqual(result["model"], "visible-model")
        self.assertEqual(len(result["output"]), 1)
        self.assertEqual(result["output"][0]["type"], "function_call")
        self.assertEqual(result["output"][0]["call_id"], "call_2")
        self.assertEqual(result["output"][0]["name"], "exec_command")
        self.assertEqual(result["usage"]["total_tokens"], 5)

    def test_messages_response_to_chat(self):
        payload = {
            "id": "msg_1",
            "type": "message",
            "role": "assistant",
            "model": "claude",
            "content": [{"type": "text", "text": "ok"}],
            "stop_reason": "end_turn",
            "usage": {"input_tokens": 3, "output_tokens": 4},
        }
        result = convert_response(payload, "chat", "messages", "local")
        self.assertEqual(result["model"], "local")
        self.assertEqual(result["choices"][0]["message"]["content"], "ok")
        self.assertEqual(result["usage"]["total_tokens"], 7)


if __name__ == "__main__":
    unittest.main()
