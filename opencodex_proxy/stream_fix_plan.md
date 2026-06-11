# 流式转换修复方案

## 问题诊断

deepseek (type=chat)、100xlabs (type=messages) 通道的 TTFT ≈ DurationMs，差值仅 10-270ms。
bh (type=responses) 等直通通道差值 15-69 秒，流式正常。

根因：Chat→Responses 转换路径中，IAsyncEnumerable 状态机可能未正确惰性求值，
导致 `response.created` 事件未在开始上游读取前 yield。

## 方案 A：消除外层包装（最小改动）

### 改动
`ProxyStreamService.StreamAsync.cs` — 将调用点从外层透传重载改为直接调内部重载。

```csharp
// 改前
var convertedLines = context.ChannelType == ProtocolConverter.Chat
    ? SseStreamConverter.ChatToResponsesEvents(streamLines, visibleModel, converted, ct)
    : SseStreamConverter.MessagesToResponsesEvents(streamLines, visibleModel, converted, ct);

// 改后
var convertedLines = context.ChannelType == ProtocolConverter.Chat
    ? SseStreamConverter.ChatToResponsesEvents(streamLines, visibleModel, converted,
        SkipToolNames: null, SkipResponseCreated: false,
        InitialSequenceNumber: 0, InitialOutputIndex: 0, ct)
    : SseStreamConverter.MessagesToResponsesEvents(streamLines, visibleModel, converted,
        SkipToolNames: null, SkipResponseCreated: false,
        InitialSequenceNumber: 0, InitialOutputIndex: 0, ct);
```

### 添加日志
在 `StreamAsync` 各阶段打印时间戳，便于排查。

### 优点
1 文件、零风险。

---

## 方案 B：预发送 response.created + Task.Yield

### 改动
`ProxyStreamService.StreamAsync.cs` + `SseStreamConverter.cs`（2 文件）

手动构造 `response.created` + `response.in_progress`，在进入转换管道前直接写入 HttpResponse。
转换器设置 `SkipResponseCreated: true`。

```csharp
// 预发送
var earlyEvents = new[] {
    SseStreamConverter.EmitStandalone("response.created", ...),
    SseStreamConverter.EmitStandalone("response.in_progress", ...)
};
foreach (var evt in earlyEvents)
    await context.StreamWriter.WriteRawLineAsync(evt, ct);

// 转换器跳过
var convertedLines = SseStreamConverter.ChatToResponsesEvents(
    ..., SkipResponseCreated: true, InitialSequenceNumber: 2, ct);
```

### 需要新增
- `SseStreamConverter.EmitStandalone` 静态方法
- `IProxyStreamWriter.WriteRawLineAsync` 方法

### 优点
确定性强，绕过整个 IAsyncEnumerable 管道。

### 缺点
需共享 responseId，改动稍多。

---

## 方案 C：Channel<T> 管道（根治）

### 思路
放弃 IAsyncEnumerable 惰性求值，改用 `System.Threading.Channels.Channel<string>`。

```
[上游读取] → Channel(256) → [转换器] → Channel(256) → [HTTP写入]
```

### 优点
彻底消除状态机不确定性，天然支持背压。

### 缺点
改动量大（~3 文件），需引入 Task.Run 和异常传播。

