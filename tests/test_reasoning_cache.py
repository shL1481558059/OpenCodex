import unittest

from opencodex_proxy.reasoning_cache import ReasoningCache


class ReasoningCacheTests(unittest.TestCase):
    def test_injects_reasoning_content_for_followup_tool_result(self):
        cache = ReasoningCache()
        cache.remember_chat_response(
            {
                "choices": [
                    {
                        "message": {
                            "role": "assistant",
                            "content": "",
                            "reasoning_content": "need to run a command",
                            "tool_calls": [
                                {
                                    "id": "call_1",
                                    "type": "function",
                                    "function": {"name": "exec_command", "arguments": "{}"},
                                }
                            ],
                        }
                    }
                ]
            }
        )
        request = {
            "messages": [
                {
                    "role": "assistant",
                    "content": "",
                    "tool_calls": [
                        {
                            "id": "call_1",
                            "type": "function",
                            "function": {"name": "exec_command", "arguments": "{}"},
                        }
                    ],
                }
            ]
        }
        injected = cache.inject_chat_request(request)
        self.assertEqual(injected, ["call_1"])
        self.assertEqual(request["messages"][0]["reasoning_content"], "need to run a command")

    def test_injects_thinking_block_for_messages_followup(self):
        cache = ReasoningCache()
        cache.remember_messages_response(
            {
                "content": [
                    {"type": "thinking", "thinking": "need a command", "signature": ""},
                    {
                        "type": "tool_use",
                        "id": "call_1",
                        "name": "exec_command",
                        "input": {"cmd": "printf OK"},
                    },
                ]
            }
        )
        request = {
            "messages": [
                {
                    "role": "assistant",
                    "content": [
                        {"type": "text", "text": ""},
                        {
                            "type": "tool_use",
                            "id": "call_1",
                            "name": "exec_command",
                            "input": {"cmd": "printf OK"},
                        },
                    ],
                }
            ]
        }
        injected = cache.inject_messages_request(request)
        self.assertEqual(injected, ["call_1"])
        self.assertEqual(request["messages"][0]["content"][0]["type"], "thinking")
        self.assertEqual(request["messages"][0]["content"][1]["type"], "tool_use")


if __name__ == "__main__":
    unittest.main()
