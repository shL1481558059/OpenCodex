#!/bin/bash

# 测试流式输出是否真的存在延迟/阻塞问题
# 用法: ./scripts/test_streaming.sh

API_URL="${API_URL:-https://localhost:8443}"
API_KEY="${API_KEY:-change-me}"

echo "=== 测试流式输出响应延迟 ==="
echo "API地址: $API_URL"
echo ""

# 使用curl的 --no-buffer 和时间戳来验证是否实时输出
echo "发送流式请求，观察每行的时间戳..."
echo ""

curl -N --no-buffer \
  -H "Authorization: Bearer $API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-4o",
    "messages": [{"role": "user", "content": "Count from 1 to 5, one number per line."}],
    "stream": true
  }' \
  "$API_URL/v1/chat/completions" \
  2>/dev/null | while IFS= read -r line; do
    # 为每行添加时间戳（毫秒级）
    timestamp=$(date +"%H:%M:%S.%3N")
    echo "[$timestamp] $line"
  done

echo ""
echo "=== 测试完成 ==="
echo ""
echo "观察要点："
echo "1. 首行（data: {\"id\":...）应该在请求后立即出现（几百毫秒内）"
echo "2. 后续的 data: 行应该陆续到达，时间戳间隔不应该有突然的大跳跃"
echo "3. 如果所有行的时间戳几乎一样，说明存在缓冲问题"
echo "4. 如果时间戳逐渐递增，说明是真正的流式输出"

