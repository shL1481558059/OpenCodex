# 管理台 SSE 实时刷新断线修复方案

> 状态：单元 A/B/C 全部已落地，前后端测试与构建通过。
> 排查时间：2026-08-26。所有带行号的结论均来自当前工作区逐文件核对，未依赖记忆。

## 1. 问题与现象

管理台三个页面共四条 SSE 流「总是断开」：

| 前端页面 | 端点 | 事件名 | 现象 |
| --- | --- | --- | --- |
| 渠道（容量状态列） | `GET /channels/runtime/stream` | `runtime` | 空闲约 30 秒后彻底停更，切页重挂载才恢复 |
| 请求日志 | `GET /logs/stream` | `logs` | 同上 |
| 仪表盘（处理队列） | `GET /monitor/active-channels/stream` | `queue` | 长期显示「未连接」，但连接其实还活着 |
| 仪表盘（近期错误） | `GET /monitor/recent-errors/stream` | `errors` | 同上 |

## 2. 根因

### 2.1 心跳客户端收不到，30 秒保鲜计时器变成自杀计时器（主因）

服务端心跳原本写的是 SSE 注释行 `: heartbeat`。按 HTML 规范，`EventSource` 静默丢弃以 `:` 开头的行，不触发任何 JS 事件，所以前端完全无法据此判断连接活性。

前端的保鲜计时器只在收到业务事件时重置，且超时回调直接关流：

- 渠道页：`RUNTIME_STALE_TIMEOUT_MS = 30000`（`frontend/src/Channels.vue:1533`），超时回调是 `stopRuntimeStream`（`Channels.vue:3248`）。
- 日志页：`LOG_SSE_STALE_TIMEOUT_MS = 30000`（`frontend/src/Logs.vue:955`），超时回调是 `stopLogSseStream`（`Logs.vue:1088`）。

于是链路变成：连上 → 收到初始快照 → 计时器排到 30 秒后 → 30 秒内没有真实流量（渠道容量无变化、无新日志）→ 计时器到点主动 `close()`。`close()` 是客户端主动关闭，浏览器不会自动重连，代码里也没有任何重连逻辑，连接就永久死掉。

旧心跳间隔恰好也是 30 秒（与前端超时同值），即使心跳能被看见也是竞态。

### 2.2 `onerror` 只丢引用不关连接，僵尸连接泄漏

`Channels.vue:3286` 与 `Logs.vue:1104` 的 `onerror` 只把 `runtimeEventSource` / `logEventSource` 置 `null`，没有 `close()`。原生 `EventSource` 在传输层出错后会自行反复重连，于是被弃养的实例仍在后台重连，而应用层已认为流停了，`stopXxxStream()` 也再拿不到它。切几次页面就攒出多条僵尸连接。

叠加 nginx 只监听 80（HTTP/1.1，`switch_backend.sh:53`），浏览器同源 6 连接上限会被僵尸流吃满，之后普通接口也开始排队卡住，看起来像「到处都在断」。

### 2.3 心跳任务与快照推送并发写同一个 `HttpResponse`

改动前 `SseEventWriter` 用 `Task.Run` 起后台心跳循环写 `response`，主循环也写同一个 `response`，两者无互斥。`HttpResponse.WriteAsync` / `Body.FlushAsync` 不是线程安全的。

已用一个独立的最小 net10 web 项目复刻该结构（心跳 1ms、快照高频、单帧 8KB）验证：客户端收到明显错帧，如 `event: rundata: {"i":2130}`、`aa: {"i":231\r:: {"i":2923}`，并夹入 `\0` 填充；服务端两侧同时抛 `System.ArgumentOutOfRangeException`，连接立即断。**注意：该复现是在独立最小项目中完成的，不是在本仓库内跑出来的。** 临时目录已清理。

30 秒心跳间隔下碰撞概率低，但渠道容量事件在每次请求 acquire/release 都会发布（`ChannelCapacityService.cs:95` 与 `:189`），去抖后约 3.3 帧/秒，有流量时撞上心跳只是时间问题。

### 2.4 仪表盘两条流语义与另两页不一致（次要）

`QUEUE_STALE_TIMEOUT_MS = 5000`、`ERRORS_STALE_TIMEOUT_MS = 15000`（`Dashboard.vue:430`）超时只翻转「未连接」标签、不关流。所以仪表盘的坏法与渠道/日志页相反：连接还活着，标签长期显示未连接。四条流三种语义。

### 2.5 快照回查占用连接级 `DbContext`（次要）

`ReadChannelRuntime` 每帧全量查渠道表并 join 用户表（`ConfigService.cs:85`），`ResolveActiveRequests` / `ResolveHealthStatus` 读单例容量与熔断服务（`ConfigService.cs:233`、`:238`）。控制器是 scoped，`IConfigService` → `IRepository<>` → `IOpenCodexDbContext` 在控制器构造时就解析出来，整条 SSE 连接期间（可能数小时）持有同一个 `DbContext`。

这一条影响有限，需要说清楚边界：查询全部走 `TableNoTracking`，不存在变更跟踪膨胀或一级缓存脏读；EF Core 默认按操作开闭连接，也不会长期占着数据库连接。真实收益是把资源生命周期收敛到「一帧」而不是「一条连接」。

## 3. 目标与约束

- 空闲连接不再自我关闭；断线后自动恢复，无需用户切页。
- 心跳必须是客户端可感知的信号，且心跳间隔与前端超时窗口保持明确倍数关系。
- 任何路径下 `EventSource` 都必须 `close()` 后才丢引用，杜绝僵尸连接。
- 四条流的连接状态语义统一，由一处实现负责。
- 服务端对同一条响应的写入必须串行。
- 不改变现有事件名与 payload 结构（`runtime` / `logs` / `queue` / `errors`），保持前后端可分别灰度。

## 4. 实施单元拆分

按 AGENTS.md「单任务不超过 3 个文件」拆成 4 个单元，每个单元独立可验证。

| 单元 | 涉及文件 | 状态 |
| --- | --- | --- |
| A. 服务端串行写入 + 具名心跳 | `SseEventWriter.cs`、新增 `SseEventWriterTests.cs` | 已落地，测试通过 |
| B1. 前端 SSE 公共客户端 | 新增 `frontend/src/api/sseClient.js` 及其单测 | 已落地，测试通过 |
| B2. 渠道页与日志页接入 | `Channels.vue`、`Logs.vue` | 已落地 |
| C. 仪表盘接入 + 每帧独立 scope | `Dashboard.vue`、`ConfigController.cs`、`ObservabilityController.cs` | 已落地 |

## 5. 单元 A：服务端（已落地）

改动文件：`opencodex_proxy/src/Presentation/OpenCodex.Api/Infrastructure/SseEventWriter.cs`。

已完成的四点：

1. 删掉 `Task.Run` 后台心跳任务，心跳与快照并入同一个 `while` 循环串行写出。等待事件时用 `CancellationTokenSource.CreateLinkedTokenSource` + `CancelAfter(heartbeatInterval)` 给 `WaitToReadAsync` 加超时，超时即写一帧心跳后继续等（`SseEventWriter.cs:88` 的 `WaitForEventAsync`）。
2. 心跳从注释行改为具名事件 `event: heartbeat` + `data: {}`（`SseEventWriter.cs:108`），前端可监听。
3. 心跳间隔 30s → 15s（`SseEventWriter.cs:15`），与计划中的前端 45s 超时形成 3 次容错余量。
4. `WaitForEventAsync` 用三态返回区分三种情形：`true` 有事件、`false` 心跳到点、`null` 事件流已完成应结束推送；`OperationCanceledException` 统一由最外层 `catch` 收敛，客户端断开不再向上抛。

行为变化需要留意：初始快照过去在 `try` 之外，客户端立即断开会把 `OperationCanceledException` 抛给 MVC；现在被吞掉，不再产生噪音日志。

`dotnet build` 通过（仅 2 个既有的 `ModelCatalogService` nullable 警告，与本次无关）。

待补测试（`opencodex_proxy/tests/OpenCodex.Api.Tests/SseEventWriterTests.cs`）：

- 空闲时写出 `event: heartbeat` 帧。
- 帧完整性回归：给一个短心跳间隔 + 高频事件源，把响应体按 `\n\n` 切分，断言每帧都严格匹配 `event: <name>\ndata: <json>\n\n`，没有交错错帧。这是 2.3 的回归防线。
- 收到事件后去抖并推出第二帧快照。
- `ChannelReader` 完成后循环退出。
- 取消令牌触发后正常返回，不抛异常。

## 6. 单元 B1：`frontend/src/api/sseClient.js`（未开始）

### 6.1 对外形态

```js
import { createSseStream } from "./api/sseClient.js";

const stream = createSseStream({
  path: "/channels/runtime/stream",
  events: { runtime: (data) => applyRuntimePayload(data) },
  onStatus: (status) => { /* "connecting" | "live" | "disconnected" | "idle" */ }
});

stream.start();
stream.stop();
```

### 6.2 内部行为

1. URL 前缀复用 `client.js` 的规则：`import.meta.env.DEV ? BASE_URL.replace(/\/$/, "") : ""`，三处重复的 `buildXxxStreamUrl` 一并收敛。
2. 每次 `start()` 先 `stop()`，再新建 `EventSource(url, { withCredentials: true })`，状态置 `connecting`。
3. 注册 `events` 里的所有事件名，额外注册 `heartbeat`。任一事件到达即：状态置 `live`、重置退避基数、重置保鲜计时器。`heartbeat` 只做保鲜，不回调业务。
4. 保鲜计时器默认 45000ms（服务端心跳 15s 的 3 倍）。到点判定为连接已死：`close()` → 状态 `disconnected` → 安排重连。**不再直接停流。**
5. `onerror`：先 `close()` 再丢引用 → 状态 `disconnected` → 安排重连。
6. 重连退避 1s → 2s → 4s → 8s → 16s → 30s 上限，叠加 ±20% 抖动，避免多页签同时打爆后端。
7. `stop()` 清计时器、`close()`、状态 `idle`，并把代次标记自增，此后旧实例的任何回调都被忽略。
8. `document.visibilitychange`：页面重新可见且当前 `disconnected` 时，重置退避并立即重连（后台页签的 `setTimeout` 会被浏览器节流，这一步保证切回来立刻活）。

### 6.3 可测试性

`createSseStream` 接受可注入的 `eventSourceFactory` / `setTimeout` / `clearTimeout`，默认取全局。这样单测能在 `node:test` 下用 FakeEventSource + 假计时器驱动，不需要浏览器环境。

单测（`frontend/src/api/sseClient.test.js`，跑法 `node --test 'src/**/*.test.js'`）：

- 只有心跳、没有业务事件时，跨过 2 个心跳周期不触发重连（2.1 的回归）。
- 完全静默超过保鲜窗口 → 断言旧实例 `close()` 被调用且安排了重连（2.1 的另一半）。
- `onerror` 后断言 `closed === true`，且重连延迟序列为 1s / 2s / 4s（2.2 的回归）。
- 重连成功收到一帧后，退避回落到基数。
- `stop()` 后不再重连；旧实例事后回调不改变状态。

## 7. 单元 B2：渠道页与日志页接入（未开始）

`Channels.vue`：删除 `runtimeEventSource` / `runtimeStaleTimer` / `RUNTIME_STALE_TIMEOUT_MS`（`:1531`-`:1533`）、`buildRuntimeStreamUrl`（`:3234`）、两个 stale 计时器函数（`:3239`、`:3246`）、`startRuntimeStream` / `stopRuntimeStream`（`:3272`、`:3294`）。保留 `applyRuntimePayload`（`:3251`）作为 `events.runtime` 回调。`onMounted`（`:3307`）改为 `stream.start()`，`onBeforeUnmount`（`:3315`）改为 `stream.stop()`。

`Logs.vue`：同样删除自建流管理（`:952`-`:956`、`:1079`-`:1118`），`events.logs` 回调保留原有的 `refreshLogPageData()` 与 `logsLoading/statsLoading` 竞态防护。`setLogSseMode`（`:1120`）与 `watch(() => props.active)`（`:1742`）改为调用 `stream.start()` / `stream.stop()`，「已暂停」开关的语义不变。

## 8. 单元 C：仪表盘接入与每帧独立 scope（已落地）

`Dashboard.vue`：两条流都换成 `createSseStream`，`onStatus` 驱动 `queueConnected` / `errorsConnected`。删除 `QUEUE_STALE_TIMEOUT_MS` / `ERRORS_STALE_TIMEOUT_MS`（`:430`-`:431`），统一用 sseClient 的 45s 窗口。

这里有一处**可见的 UX 变化**要确认：原来 5 秒无队列更新就翻「未连接」，改后只要连接活着就一直显示「实时更新中」。这才是标签的本意（连接状态而非数据新鲜度），但和你现在看到的行为不同。

服务端每帧独立 scope：四个流端点迁到新增的 `RealtimeStreamController`（`Controllers/RealtimeStreamController.cs`），构造只注入 `IWorkContext` / `IEventBus` / `IServiceScopeFactory`，**不注入** `IConfigService` / `IObservabilityService`（连带不构造 scoped `DbContext`）。`pushSnapshot` 回调里 `using var scope = factory.CreateScope()` 后解析 `IConfigService` / `IObservabilityService`，用完即释放。`IHttpContextAccessor` 是单例且基于 `AsyncLocal`，子 scope 里的 `WebWorkContext` 仍能拿到同一个 `HttpContext`，权限判定不受影响。

原 `ConfigController.WriteChannelRuntimeStream` 与 `ObservabilityController` 的三个 `WriteXxxStream` 及其 `_eventBus` / `_scopeFactory` 依赖一并删除，两个控制器构造参数相应回退。此前「控制器自身仍持有连接级 `DbContext`」的残留项已消除。

## 9. 验证计划

自动化：

- `dotnet test opencodex_proxy/tests/OpenCodex.Api.Tests` 全绿，新增 `SseEventWriterTests` 覆盖第 5 节五条。
- `cd frontend && node --test 'src/**/*.test.js'` 全绿，新增 `sseClient.test.js` 覆盖第 6.3 节五条。

手工（本地 `https://localhost:8443` + `http://127.0.0.1:5173/admin/`）：

1. 打开渠道页，全程零流量静置 5 分钟。DevTools Network 里该连接持续存在、每 15 秒一帧 `heartbeat`，容量列不停更。这是主因的验收点。
2. 静置后发一次请求，容量状态列应在 300ms 去抖后跳动。
3. 重启后端（`Ctrl+C` 再起）。前端应在 1-2 秒内自动重连并恢复，无需刷新页面。
4. 在渠道页与日志页之间来回切换 10 次，Network 面板里 `stream` 类型连接数应始终为 1，不累积。
5. 切到后台页签 2 分钟再切回，应立即恢复 `live`。
6. 四页同开（渠道、日志、仪表盘），确认同源连接数不超过浏览器上限，普通接口不排队。
7. 用非超管账号登录，确认只收到自己 owner 的事件，权限过滤未被 scope 改动破坏。

## 10. 风险与未覆盖边界

- 心跳事件名 `heartbeat` 是新增的服务端输出。旧前端不监听它，只会忽略，因此前后端可分别发布；但若先发前端后发后端，前端会因收不到心跳而按 45s 窗口反复重连（每 45s 一次，可用），需要注意发布顺序。
- 45s 保鲜窗口与 15s 心跳是硬编码常量分居两端，没有协商机制。改动其中一个必须同步另一个。
- nginx 侧 `proxy_read_timeout 480s`（`switch_backend.sh:61`）远大于 15s 心跳，不会误杀；但如果将来有人把它降到 15s 以下，连接会被网关周期性切断，届时靠客户端重连兜底、体验降级。
- 移动端浏览器在后台可能直接冻结页签，`visibilitychange` 恢复路径已覆盖；但 iOS Safari 长时间后台后可能连 JS 计时器一起冻结，恢复延迟取决于系统，无法在应用层保证。
- 单元 C 的 scope 改动不改变任何权限判定路径，但确实换了 `IWorkContext` 实例来源，第 9 节手工步骤 7 是专门为它设的验收点。
- 未覆盖：多实例部署下 Redis pub/sub 广播链路（`EventBus.DispatchRedisEvent`）本次不动，跨实例事件投递行为保持原样。
- **单元 D+E（已落地）**：`ConfigService.ReadChannels` 与 `ObservabilityService.ReadScopedChannels` 的全量渠道配置查询改为走 `IMemoryCache` 进程内缓存（键 `CacheKeys.ChannelConfig`，TTL 10s）。`ConfigService` 所有写操作（Create/Update/Delete/BatchUpdate/Import/ResetHealth）后通过 `InvalidateRouteCache` 统一失效缓存。SSE 每帧回查不再落库：配置字段从缓存取、运行时字段（`ActiveRequests`、`HealthStatus`、`ModelUsages`）从进程内 `IChannelCapacityService` / `IChannelCircuitBreakerService` 读。多管理页签并发的数据库压力消除。
- `ReadRecentErrors` 仍每帧查 log 表（按时间倒序取最近 5 条错误日志），此路径数据本身在变，不适合缓存。它只在 `RequestLogWrittenEvent` 触发时回查，非心跳驱动，频率与实际错误发生节奏一致，可接受。
- 缓存以全量 `IReadOnlyList<ChannelDto>` 存储，非超管在内存按 `OwnerUsername` 过滤。多实例下各实例各缓存一份，写操作仅失效本实例缓存 + TTL 10s 兜底，跨实例一致性靠 TTL 保证。
