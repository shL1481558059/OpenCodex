import unittest

from opencodex_proxy.protocols import convert_request, convert_response


class ProtocolTests(unittest.TestCase):
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
