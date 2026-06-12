#!/bin/bash
# 从服务器数据库提取真实SSE响应数据作为测试数据

set -e

SSH_KEY="${SSH_KEY:-/Users/w/.ssh/LightsailDefaultKey-ap-northeast-2.pem}"
SSH_HOST="${SSH_HOST:-admin@ssh.shldev.me}"
CONTAINER="${CONTAINER:-ocxp}"
DB_PATH="/app/logs/opencodex.db"
OUTPUT_DIR="opencodex_proxy/tests/OpenCodex.Api.Tests/TestData"

echo "📡 从服务器提取SSE测试数据..."

# 创建输出目录
mkdir -p "$OUTPUT_DIR"

# 创建临时C#查询脚本
cat > /tmp/query_sse.cs << 'CSHARP'
using System;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var connectionString = $"Data Source={args[0]};Mode=ReadOnly";
using var connection = new SqliteConnection(connectionString);
connection.Open();

// 查询最近的成功请求（包含上游响应）
var query = @"
SELECT 
    Id,
    CreatedAt,
    Path,
    UpstreamResponseBody,
    RequestBody,
    ChannelType
FROM ProxyLogs
WHERE StatusCode = 200 
  AND UpstreamResponseBody IS NOT NULL 
  AND UpstreamResponseBody != '{}'
  AND Path LIKE '%/v1/%'
ORDER BY CreatedAt DESC
LIMIT 20
";

using var command = new SqliteCommand(query, connection);
using var reader = command.ExecuteReader();

var results = new List<Dictionary<string, object?>>();
while (reader.Read())
{
    var record = new Dictionary<string, object?>
    {
        ["id"] = reader.GetInt64(0),
        ["created_at"] = reader.GetString(1),
        ["path"] = reader.GetString(2),
        ["upstream_response"] = reader.GetString(3),
        ["request_body"] = reader.GetString(4),
        ["channel_type"] = reader.IsDBNull(5) ? null : reader.GetString(5)
    };
    results.Add(record);
}

Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions 
{ 
    WriteIndented = false 
}));
CSHARP

# 从容器复制数据库到本地临时目录
echo "📥 下载数据库..."
TEMP_DB="/tmp/opencodex_temp_$(date +%s).db"
ssh -i "$SSH_KEY" "$SSH_HOST" "docker cp ${CONTAINER}:${DB_PATH} /tmp/temp.db && cat /tmp/temp.db && rm /tmp/temp.db" > "$TEMP_DB"

echo "🔍 查询SSE响应数据..."
# 使用dotnet script执行查询
dotnet script /tmp/query_sse.cs "$TEMP_DB" > /tmp/sse_records.json

# 解析JSON并生成测试数据文件
python3 << 'PYTHON'
import json
import sys
import os

with open('/tmp/sse_records.json', 'r') as f:
    records = json.load(f)

output_dir = os.environ.get('OUTPUT_DIR', 'opencodex_proxy/tests/OpenCodex.Api.Tests/TestData')
os.makedirs(output_dir, exist_ok=True)

print(f"📝 找到 {len(records)} 条记录")

for i, record in enumerate(records[:5]):  # 只取前5条
    record_id = record['id']
    path = record['path']
    channel_type = record.get('channel_type', 'unknown')
    
    # 解析上游响应
    try:
        upstream = json.loads(record['upstream_response'])
        request = json.loads(record['request_body'])
        
        # 判断类型
        if 'chat' in path:
            test_type = 'chat'
        elif 'responses' in path:
            test_type = 'responses'
        else:
            test_type = 'unknown'
        
        # 保存为测试数据
        filename = f"{test_type}_{channel_type}_{record_id}.json"
        output_path = os.path.join(output_dir, filename)
        
        with open(output_path, 'w') as out:
            json.dump({
                'id': record_id,
                'path': path,
                'created_at': record['created_at'],
                'channel_type': channel_type,
                'request': request,
                'upstream_response': upstream
            }, out, indent=2, ensure_ascii=False)
        
        print(f"✅ 保存: {filename}")
        
    except Exception as e:
        print(f"⚠️  跳过记录 {record_id}: {e}")

PYTHON

# 清理
rm -f "$TEMP_DB" /tmp/sse_records.json /tmp/query_sse.cs

echo "✨ 完成！测试数据已保存到 $OUTPUT_DIR"
ls -lh "$OUTPUT_DIR"

