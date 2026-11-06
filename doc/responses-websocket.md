# Responses WebSocket 与上下文池方案

## 1. 目标

Codex 的普通 HTTP 请求在多轮对话和工具续轮时，会把历史 input、工具结果和助手输出再次放进请求正文。请求越大，客户端到 OpenCodex 的上传越慢，反向代理与日志链路也需要重复处理同一份历史。

本方案用 Responses WebSocket 建立两类增量链路：

1. `Codex -> OpenCodex`：同一 WebSocket 连接保存已完成响应，后续 `response.create` 只上传新增 input。
2. `OpenCodex -> Responses 上游`：渠道选择上下文池时，代理保存上游请求与响应快照；当前 input 与历史一致时，仅发送 delta 和 `previous_response_id`。

WebSocket 减少的是传输正文和重复序列化，不会减少模型实际看到的上下文，也不会自动降低按输入 token 计费的成本。放入上下文的系统指令、工具定义和历史仍会构成模型输入。

## 2. 端到端链路

```text
Codex
  |  GET /v1/responses (Upgrade: websocket)
  v
OpenCodex 入站会话
  |-- 首轮：完整 response.create
  |-- 续轮：previous_response_id + 新增 input
  |
  |  统一鉴权、路由、协议转换、日志与计费
  v
Responses upstream transport
  |-- off: HTTP /responses
  |-- auto: WebSocket 成功则 context_pool，否则 HTTP
  |-- context_pool: WebSocket + 代理侧历史快照
  |-- passthrough: WebSocket + 客户端 previous_response_id/delta
  v
Responses upstream
```

Chat 或 Messages 上游仍是 HTTP 有状态代理。入站可以只接收 delta，但转换到无状态 Chat/Messages 前，OpenCodex 必须重建完整历史再发上游，因此第二项目标只对 Responses 上游生效。

## 3. 入站协议

### 3.1 支持的帧

- `response.create`：执行一次 Responses 响应。
- `generate=false`：创建连接内预热响应，不调用模型；返回合成的 `response.created` 与 `response.completed`。
- 工具调用后的续轮继续使用 `response.create`，input 通常只包含 `function_call_output` 或 `custom_tool_call_output`。

同一连接同时只允许一个活动响应。当前默认 lane 不支持命名 `stream_id`、`response.steer` 或并行响应流。

### 3.2 连接内状态

- 每个连接按 `previous_response_id` 保存已完成的请求 input 与响应 output。
- 后续 input 若以历史为前缀，直接采用客户端重放；若只是新增项，则拼接“历史 input + 历史 output + 新增 input”。
- input 与历史不兼容时，按完整历史重建；未知父响应返回 `previous_response_not_found`，要求客户端去掉该 ID 后重发完整历史。
- 状态不跨连接、不跨代理实例。重连后旧 `previous_response_id` 不可用。

### 3.3 与多代理的关系

WebSocket 入口优先识别已有 native 多代理线程，以及模型中显式开启的 `v2_agent_simulation`。已有 native 线程或模型显式开启多代理时继续走原 native/legacy 多代理路径；未开启多代理且没有既有 native 线程的普通请求进入通用 Responses WebSocket 会话。

## 4. 上游 WS 模式

只有 `type=responses` 渠道允许配置 `compat.ws_mode`。Chat、Messages、Images 渠道显式配置该字段会被校验拒绝。

| 模式 | 行为 | WebSocket 不可用时 |
|---|---|---|
| `auto`（默认） | 先探测上游 Responses WebSocket，成功后使用上下文池 | 回退 HTTP，并按渠道与模型缓存探测失败 5 分钟 |
| `context_pool` | 强制使用上游 WebSocket 和代理侧快照 | 返回 502，不静默降级 |
| `passthrough` | 仅当入站是 Responses WebSocket 时使用；保留客户端 `previous_response_id` 与增量 input | 返回 502 |
| `off` | 固定 HTTP | 不尝试 WebSocket |

`passthrough` 收到普通 HTTP 请求时按 HTTP 处理；它不会把 HTTP 请求强行升级为 WebSocket。

### 4.1 上下文池 delta 条件

代理仅在下列条件同时满足时发送 delta：

1. 同一客户端访问 Key、同一会话、同一渠道、同一上游模型。
2. 渠道 base URL、认证模式和凭据指纹一致。
3. 当前 input 的前缀等于上一次请求 input 加响应 output。
4. 除 input、`previous_response_id`、stream 相关字段外的关键请求属性一致，例如 instructions、tools、tool choice、模型与推理参数。

任一条件不满足就发送完整历史，避免把旧上下文错误套到新请求上。

### 4.2 失效恢复

- 上游返回 `previous_response_not_found` 或连接上限错误时，上下文池会用完整历史在同一渠道重放一次。
- 流式响应一旦已经向客户端发送首个事件，就不切换渠道或重放，避免产生重复输出。
- 连接在未收到终态事件时中断、超时或客户端提前断开，连接会被废弃，下一次请求重新建连，避免读到上一轮残留事件。

## 5. 隔离与容量

- 入站状态以访问 API Key 和会话为边界，不与其他 Key 共享。
- 上游连接键包含访问 Key、会话、渠道、上游模型和渠道凭据指纹。
- 上游快照按相同业务边界隔离；跨 Key、跨渠道或跨模型不会复用 `previous_response_id`。
- 单实例最多保留 64 个上游 WebSocket 连接、2000 个响应快照；空闲连接 5 分钟后清理。
- 入站单条 WebSocket 文本消息上限为 16 MiB，上游单条文本消息上限为 32 MiB。

## 6. 渠道配置

Responses 渠道示例：

```json
{
  "name": "responses-upstream",
  "type": "responses",
  "baseurl": "https://provider.example/v1",
  "apikey": "${UPSTREAM_API_KEY}",
  "auth_mode": "config",
  "headers": {},
  "capacity": 20,
  "compat": {
    "ws_mode": "auto"
  },
  "models": [
    {
      "model": "gpt-6-luna",
      "upstream_model": "provider-model"
    }
  ],
  "enabled": true
}
```

管理台在 Responses 渠道中显示“Responses WS 模式”，选项与上表一致。

## 7. Codex 配置

```toml
model = "gpt-6-luna"
model_provider = "opencodex"

[model_providers.opencodex]
name = "OpenCodex"
base_url = "https://ocxp.example.com/v1"
env_key = "OPENCODEX_ACCESS_API_KEY"
wire_api = "responses"
supports_websockets = true
requires_openai_auth = false
```

`OPENCODEX_ACCESS_API_KEY` 是 OpenCodex 访问 Key，不是上游服务 Key。官方 Codex 配置参考将 `supports_websockets` 列为 Responses provider 的可选能力开关。

## 8. 观测

上游传输选择日志包含：

- `ws_mode_requested`
- `effective_mode`
- `transport`
- `channel_id`
- `model`
- `input_item_count`
- `previous_response_id`
- `fallback_reason`

入站投影日志包含：

- `create_index`
- `previous_response_id`
- `client_input_item_count`
- `reused_history_item_count`
- `projected_input_item_count`
- `generate`

日志不记录 WebSocket 帧正文。请求正文仍按现有请求日志策略存储，因此 Trace 级别仍可能包含敏感内容，应按原有日志权限和清理策略管理。

## 9. 已知边界

- `stream_id`、`response.steer`、多活动响应和并行 lane 尚未实现。
- 入站与上游状态都在进程内，当前不提供跨实例上下文池。
- 显式 `context_pool` 不自动回退 HTTP；需要自动降级时使用 `auto`。
- 上游 WebSocket 仅实现对 Responses 渠道；Chat/Messages 上游仍走 HTTP。
- HTTP 回退会重放完整历史，但不会减少模型输入 token 计费。
- 上游是否支持 WebSocket、代理握手和单条消息大小限制仍受反向代理、TLS、渠道超时和上游实现约束。

## 10. 验证

- `ResponsesWebSocketClientServiceTests`：预热不调用模型、连接内续接、完整历史重建、缺失父响应错误、客户端原始增量载荷保留。
- `ResponsesUpstreamTransportTests`：上下文池 delta、passthrough、off、auto 回退与探测缓存、`context_pool` 严格失败、跨 API Key 与跨渠道隔离。
- `MultiAgentClientWebSocketTests`：原生多代理工具续轮与通用入站路径分流回归。
- `ConfigValidatorCompatTests`：四种模式、大小写归一、非 Responses 渠道拒绝。
- 本机 Codex `0.158.0` 实测：`GET /v1/responses` WebSocket 成功；工具调用后的第二轮在同一连接完成，服务端未出现 HTTP POST 回退。
