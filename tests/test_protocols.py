import json
import unittest

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
        self.assertEqual(result["messages"][3]["content"], "")
        self.assertEqual(result["messages"][3]["reasoning_content"], "checked")
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
        self.assertEqual(result["tools"][2]["function"]["name"], "apply_patch_add_file")
        self.assertEqual(result["tool_choice"], {"type": "function", "function": {"name": "lookup"}})
        self.assertEqual(result["max_tokens"], 64)
        self.assertEqual(result["temperature"], 0.2)

    def test_responses_apply_patch_tool_is_expanded_for_chat(self):
        payload = {
            "model": "local",
            "input": "patch",
            "tools": [{"type": "apply_patch", "description": "Apply a patch"}],
        }

        result = convert_request(payload, "responses", "chat", "upstream")

        names = [tool["function"]["name"] for tool in result["tools"]]
        self.assertEqual(
            names,
            [
                "apply_patch_add_file",
                "apply_patch_delete_file",
                "apply_patch_update_file",
                "apply_patch_replace_file",
                "apply_patch_batch",
            ],
        )
        update_schema = result["tools"][2]["function"]["parameters"]
        self.assertEqual(update_schema["required"], ["path", "hunks"])
        self.assertIn("hunks", update_schema["properties"])

    def test_responses_parallel_function_calls_are_grouped_for_chat(self):
        payload = {
            "model": "local",
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
        }

        result = convert_request(payload, "responses", "chat", "upstream")

        self.assertEqual([message["role"] for message in result["messages"]], ["user", "assistant", "tool", "tool", "tool"])
        assistant = result["messages"][1]
        self.assertEqual(
            [tool_call["id"] for tool_call in assistant["tool_calls"]],
            ["call_a", "call_b", "call_c"],
        )
        self.assertEqual(
            [message["tool_call_id"] for message in result["messages"][2:]],
            ["call_a", "call_b", "call_c"],
        )

    def test_responses_tool_output_without_call_id_is_skipped(self):
        payload = {
            "model": "local",
            "input": [
                {"role": "user", "content": [{"type": "input_text", "text": "run"}]},
                {"type": "function_call_output", "output": "orphan result"},
                {"type": "tool_result", "content": "orphan anthropic result"},
            ],
        }

        result = convert_request(payload, "responses", "chat", "upstream")

        self.assertEqual(result["messages"], [{"role": "user", "content": "run"}])

    def test_responses_reasoning_uses_encrypted_content_for_chat_roundtrip(self):
        payload = {
            "model": "local",
            "input": [
                {"role": "user", "content": [{"type": "input_text", "text": "search"}]},
                {
                    "type": "reasoning",
                    "summary": [{"type": "summary_text", "text": "short summary"}],
                    "encrypted_content": "full private reasoning",
                },
                {
                    "type": "function_call",
                    "call_id": "call_1",
                    "name": "lookup",
                    "arguments": "{\"q\":\"cats\"}",
                },
                {"type": "function_call_output", "call_id": "call_1", "output": "5 results"},
            ],
        }

        result = convert_request(payload, "responses", "chat", "upstream")

        assistant = result["messages"][1]
        self.assertEqual(assistant["role"], "assistant")
        self.assertEqual(assistant["reasoning_content"], "full private reasoning")
        self.assertEqual(assistant["tool_calls"][0]["id"], "call_1")

    def test_responses_orphan_tool_outputs_are_scrubbed_and_missing_outputs_are_filled(self):
        payload = {
            "model": "local",
            "input": [
                {"role": "user", "content": "first"},
                {"type": "function_call_output", "call_id": "call_orphan", "output": "stale"},
                {"type": "function_call", "call_id": "call_a", "name": "lookup", "arguments": "{}"},
                {"type": "function_call_output", "call_id": "call_a", "output": "ok"},
                {"role": "user", "content": "second"},
                {"type": "function_call", "call_id": "call_b", "name": "lookup", "arguments": "{}"},
            ],
        }

        result = convert_request(payload, "responses", "chat", "upstream")

        tool_messages = [message for message in result["messages"] if message["role"] == "tool"]
        self.assertEqual([message["tool_call_id"] for message in tool_messages], ["call_a", "call_b"])
        self.assertEqual(tool_messages[0]["content"], "ok")
        self.assertIn("tool output missing", tool_messages[1]["content"])

    def test_responses_tool_choice_dict_is_normalized_for_chat(self):
        cases = [
            ({"type": "auto"}, "auto"),
            ({"type": "none"}, "none"),
            ({"type": "tool"}, "required"),
            ({"type": "any"}, "required"),
            ({"type": "function"}, "required"),
            ({"type": "required"}, "required"),
            (
                {"type": "function", "function": {"name": "lookup"}},
                {"type": "function", "function": {"name": "lookup"}},
            ),
        ]

        for tool_choice, expected in cases:
            payload = {"model": "local", "input": "run", "tool_choice": tool_choice}
            result = convert_request(payload, "responses", "chat", "upstream")
            self.assertEqual(result["tool_choice"], expected)

    def test_responses_namespace_tools_are_flattened_and_deduped_for_chat(self):
        payload = {
            "model": "local",
            "input": "run",
            "tools": [
                {
                    "type": "function",
                    "name": "lookup",
                    "description": "top-level",
                    "parameters": {"type": "object"},
                },
                {
                    "type": "namespace",
                    "name": "mcp",
                    "tools": [
                        {
                            "type": "function",
                            "name": "lookup",
                            "description": "duplicate",
                            "parameters": {"type": "object"},
                        },
                        {
                            "type": "function",
                            "name": "fetch",
                            "description": "fetch",
                            "parameters": {"type": "object"},
                        },
                    ],
                },
            ],
        }

        result = convert_request(payload, "responses", "chat", "upstream")

        self.assertEqual(
            [tool["function"]["name"] for tool in result["tools"]],
            ["lookup", "fetch"],
        )

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

    def test_chat_apply_patch_proxy_response_rebuilds_custom_tool_call(self):
        arguments = json.dumps(
            {
                "path": "sample.txt",
                "hunks": [
                    {
                        "lines": [
                            {"op": "context", "text": "alpha"},
                            {"op": "remove", "text": "old"},
                            {"op": "add", "text": "new"},
                        ]
                    }
                ],
            },
            ensure_ascii=False,
        )
        payload = {
            "id": "chatcmpl_patch_proxy",
            "model": "upstream",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": "",
                        "tool_calls": [
                            {
                                "id": "call_patch_proxy",
                                "type": "function",
                                "function": {
                                    "name": "apply_patch_update_file",
                                    "arguments": arguments,
                                },
                            }
                        ],
                    },
                    "finish_reason": "tool_calls",
                }
            ],
        }

        result = convert_response(payload, "responses", "chat", "local")

        item = result["output"][0]
        self.assertEqual(item["type"], "custom_tool_call")
        self.assertEqual(item["call_id"], "call_patch_proxy")
        self.assertEqual(item["name"], "apply_patch")
        self.assertEqual(
            item["input"],
            "\n".join(
                [
                    "*** Begin Patch",
                    "*** Update File: sample.txt",
                    "@@",
                    " alpha",
                    "-old",
                    "+new",
                    "*** End Patch",
                ]
            ),
        )

    def test_chat_apply_patch_batch_proxy_response_rebuilds_one_patch(self):
        arguments = json.dumps(
            {
                "operations": [
                    {"type": "add_file", "path": "a.txt", "content": "hello"},
                    {"type": "delete_file", "path": "b.txt"},
                ]
            }
        )
        payload = {
            "id": "chatcmpl_patch_batch",
            "model": "upstream",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": "",
                        "tool_calls": [
                            {
                                "id": "call_patch_batch",
                                "type": "function",
                                "function": {
                                    "name": "apply_patch_batch",
                                    "arguments": arguments,
                                },
                            }
                        ],
                    },
                    "finish_reason": "tool_calls",
                }
            ],
        }

        result = convert_response(payload, "responses", "chat", "local")

        self.assertEqual(
            result["output"][0]["input"],
            "\n".join(
                [
                    "*** Begin Patch",
                    "*** Add File: a.txt",
                    "+hello",
                    "*** Delete File: b.txt",
                    "*** End Patch",
                ]
            ),
        )

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
        self.assertEqual(len(result["output"]), 2)
        self.assertEqual(result["output"][0]["type"], "reasoning")
        self.assertEqual(result["output"][0]["summary"][0]["text"], "need tool")
        self.assertEqual(result["output"][0]["encrypted_content"], "need tool")
        self.assertEqual(result["output"][1]["type"], "function_call")
        self.assertEqual(result["output"][1]["call_id"], "call_2")
        self.assertEqual(result["output"][1]["name"], "exec_command")
        self.assertEqual(result["usage"]["total_tokens"], 5)

    def test_chat_response_reasoning_uses_responses_summary_and_encrypted_content(self):
        payload = {
            "id": "chatcmpl_3",
            "model": "upstream",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": "answer",
                        "reasoning_content": "private chain",
                    },
                    "finish_reason": "stop",
                }
            ],
        }

        result = convert_response(payload, "responses", "chat", "local")

        reasoning = result["output"][0]
        self.assertEqual(reasoning["type"], "reasoning")
        self.assertEqual(reasoning["summary"], [{"type": "summary_text", "text": "private chain"}])
        self.assertEqual(reasoning["encrypted_content"], "private chain")

    def test_chat_response_to_responses_maps_length_and_annotations(self):
        payload = {
            "id": "chatcmpl_4",
            "model": "upstream",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": "truncated",
                        "annotations": [
                            {
                                "type": "url_citation",
                                "url": "https://example.test/a",
                                "title": "Example",
                                "summary": "snippet",
                            }
                        ],
                    },
                    "finish_reason": "length",
                }
            ],
        }

        result = convert_response(payload, "responses", "chat", "local")

        self.assertEqual(result["status"], "incomplete")
        self.assertEqual(result["incomplete_details"], {"reason": "max_output_tokens"})
        annotations = result["output"][0]["content"][0]["annotations"]
        self.assertEqual(
            annotations,
            [
                {
                    "type": "url_citation",
                    "url": "https://example.test/a",
                    "title": "Example",
                    "snippet": "snippet",
                }
            ],
        )

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
