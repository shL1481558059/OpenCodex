#!/bin/bash
# 捕获真实的SSE响应用于测试

set -e

OUTPUT_DIR="opencodex_proxy/tests/OpenCodex.Api.Tests/TestData"
mkdir -p "$OUTPUT_DIR"

echo "📡 捕获真实SSE响应..."
echo ""
echo "请确保后端已启动在 https://localhost:8443"
echo "用户名: admin"
echo "密码: change-me"
echo ""

# 获取访问令牌
echo "🔑 获取访问令牌..."
TOKEN_RESPONSE=$(curl -s -k -X POST https://localhost:8443/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"change-me"}')

TOKEN=$(echo "$TOKEN_RESPONSE" | python3 -c "import sys, json; print(json.load(sys.stdin).get('token', ''))")

if [ -z "$TOKEN" ]; then
    echo "❌ 获取令牌失败"
    echo "$TOKEN_RESPONSE"
    exit 1
fi

echo "✅ 令牌: ${TOKEN:0:20}..."

# 场景1: Chat协议 - 简单文本
echo ""
echo "📝 场景1: Chat协议 - 简单文本"
curl -s -k -N https://localhost:8443/v1/chat/completions \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-4o-mini",
    "messages": [{"role": "user", "content": "Say hello"}],
    "stream": true
  }' > "$OUTPUT_DIR/chat_simple_text_raw.sse" 2>&1

echo "✅ 保存到: chat_simple_text_raw.sse"

# 场景2: Messages协议 - 简单文本
echo ""
echo "📝 场景2: Messages协议 - 简单文本"
curl -s -k -N https://localhost:8443/v1/messages \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "anthropic-version: 2023-06-01" \
  -d '{
    "model": "claude-3-5-sonnet-20241022",
    "messages": [{"role": "user", "content": "Say hello in Chinese"}],
    "max_tokens": 100,
    "stream": true
  }' > "$OUTPUT_DIR/messages_simple_text_raw.sse" 2>&1

echo "✅ 保存到: messages_simple_text_raw.sse"

# 场景3: Responses协议 - 简单文本  
echo ""
echo "📝 场景3: Responses协议 - 简单文本"
curl -s -k -N https://localhost:8443/v1/responses \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-4o-mini",
    "input": [{"type": "message", "role": "user", "content": [{"type": "input_text", "text": "Count to 3"}]}]
  }' > "$OUTPUT_DIR/responses_simple_text_raw.sse" 2>&1

echo "✅ 保存到: responses_simple_text_raw.sse"

# 场景4: Chat协议 - Tool Use (web_search)
echo ""
echo "📝 场景4: Chat协议 - Tool Use"
curl -s -k -N https://localhost:8443/v1/chat/completions \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-4o-mini",
    "messages": [{"role": "user", "content": "What is the weather today?"}],
    "tools": [{
      "type": "function",
      "function": {
        "name": "web_search",
        "description": "Search the web",
        "parameters": {"type": "object", "properties": {"query": {"type": "string"}}}
      }
    }],
    "stream": true
  }' > "$OUTPUT_DIR/chat_tool_use_raw.sse" 2>&1

echo "✅ 保存到: chat_tool_use_raw.sse"

echo ""
echo "✨ 完成！捕获的SSE响应保存在:"
ls -lh "$OUTPUT_DIR"/*.sse

echo ""
echo "💡 提示: 可以使用这些真实数据更新StreamingIntegrationTests.cs"

