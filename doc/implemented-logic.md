# OpenCodex 已实现逻辑

> 基线：提交 `87587d47`（2026-09-30）。本文由原 `doc/`、`docs/` 全部文档精简合并而成，以当前代码为准；本文与代码冲突时以代码为准。
> 未实施事项、待决策项与已接受的边界见 [unimplemented-plans.md](unimplemented-plans.md)。

## 1. 系统定位与分层

OpenCodex 是多协议 LLM 代理与配套管理台：接收客户端 Responses、Chat Completions、Anthropic Messages 请求，按 owner 的渠道配置完成路由，必要时在协议之间双向转换，并统一处理鉴权、容量、熔断、重试、故障转移、计费与日志。

主要分层（`opencodex_proxy/`）：

| 层 | 路径 | 职责 |
|---|---|---|
| Presentation | `src/Presentation/OpenCodex.Api` | HTTP 路由、请求体与元数据读取、SSE 写出、末端异常处理 |
| Core 编排 | `src/Libraries/OpenCodex.Core/Services/Proxy` | `ProxyEndpointService` 主编排、非流式/流式服务、日志生命周期 |
| Core 协议 | `src/Libraries/OpenCodex.Core/Protocols` | `ProtocolConverter` 请求/响应规范化、`SseStreamConverter` 六向转换、响应累积器 |
| 路由与可靠性 | `ProxyRouteService`、`ChannelAffinityService`、`ChannelCapacityService`、`ChannelCircuitBreakerService` | 候选生成、排序、亲和、容量、熔断 |
| External | `src/Libraries/OpenCodex.Core/ExternalIntegrations` | 上游 HTTP 调用、重试、SSE 读取、Tavily/Keenable 搜索客户端 |
| Domain/Data | `src/Libraries/OpenCodex.Domain`、`OpenCodex.Data` | 渠道、用户、密钥、模型目录、日志实体与 SQLite/PostgreSQL 双 provider 持久化 |

管理台在 `frontend/`（Vue 3 + Element Plus），桌面端在 `src-tauri/`（Tauri）。独立图片生成/编辑路由（`/images/generations`、`/images/edits`）已注册，但 `IProxyImagesEndpointService` 没有实现类、相关服务未注册到 DI，当前运行时不可用（GAP），详见 [unimplemented-plans.md](unimplemented-plans.md)；它不属于三协议转换矩阵。

## 2. 端到端流程

三协议入口：

| HTTP 路径 | 入口协议 | 控制器动作 |
|---|---|---|
| `POST /responses`、`/v1/responses` | `responses` | `ProxyController.Responses` |
| `POST /chat/completions`、`/v1/chat/completions` | `chat` | `ProxyController.ChatCompletions` |
| `POST /messages`、`/v1/messages` | `messages` | `ProxyController.Messages` |

一次代理请求的执行顺序：

1. 控制器把请求体解析为 JSON 根对象；解析失败或根节点不是对象时，在鉴权成功后返回 400。同时采集方法、路径、客户端 IP 与原始请求头，构造 `ProxyEndpointContext`。
2. Bearer 鉴权：API Key 按 SHA-256 查缓存/库，并校验所属用户有效性；通过后写入 owner、api key id、owner user id 与 role。
3. 创建 queued 主日志，提取 `model`、`stream`、`prompt_cache_key` 与图片检测结果。`stream` 必须运行时严格为布尔 `true` 才走流式，字符串 `"true"` 按非流式处理。
4. 路由：按 owner 读取启用渠道，先做类型过滤，再做模型精确匹配，形成候选并按 `priority`/`position`/`id` 初始排序。
5. 运行时排序：亲和渠道 → `priority` 升序 → 本实例活跃请求数升序 → 初始顺序。
6. 逐候选准入：熔断检查（Open 跳过、HalfOpen 抢探测名额）→ 容量租约（Redis 信号量或进程内计数）。
7. 有效载荷重写：图片 OCR 降级 → Web Search 模式 → 渠道 compat（`default_params`、`rename_params`、`drop_params`、`force_params`、`drop_tool_types`、`unsupported_params`、`preserve_thinking_history`、`enable_apply_patch_prompt_compat`）。
8. 协议转换：同协议深拷贝并清洗工具 Schema；跨协议经规范化中间结构（canonical）完成请求转换。
9. 上游调用：非流式 JSON 或流式 SSE；流式场景延迟准备下游 SSE 响应，确认上游可产出首行后才写出响应头。
10. 响应处理：非流式转换回入口协议；流式同协议走透传并旁路捕获，跨协议走对应状态机。
11. 收尾：记录 usage、缓存 token、费用快照（含峰谷时段）、TTFT 与耗时；写入 attempt 子日志与主日志终态，并通过 SSE 推送管理台。

## 3. 鉴权与请求状态

- Bearer 解析：缺失、未以 `Bearer ` 开头、或前缀后只有空白 → 401；前缀大小写不敏感；token 去首尾空白后使用。
- Key 与 User 任一不存在、或 `Enabled=false` → 401。鉴权缓存分 `auth:apikey:<SHA256>` 与 `auth:user:<id>` 两级，读 L1/L2、失效走广播；null 不写缓存，避免新建用户被长期拒绝。
- `requestId` 为 12 字节随机十六进制串，用于串联主请求、attempt 与 OCR 子日志；默认超时来自运行时设置，渠道未配置有效超时时使用它兜底。
- 管理接口使用 Cookie 会话，与代理端点的 Bearer Key 是两套认证体系，互不通用。

## 4. 路由与可靠性

### 4.1 候选生成与模型映射

- 显式映射模式：任一启用渠道存在对象型模型映射时，主请求必须命中某个 `mapping.model`，否则返回 400；不会回退到无映射渠道。
- 无映射兼容模式：所有启用渠道都没有对象型映射时，取第一个启用渠道，请求模型原样作为上游模型（`MatchedModelMapping=false`）。
- `upstream_model` 为空时回退为对外模型；响应转换必须恢复客户端可见模型，不泄露上游模型名。
- 图片能力判定顺序：渠道级模型覆盖 → 全局模型目录；未命中时按上游模型做兼容回退。

### 4.2 排序与亲和

- 亲和键为请求顶层 `prompt_cache_key`，按 `(owner, stickyKey)` 记忆渠道；默认 TTL 30 分钟，滑动过期；Redis 可用时跨实例共享。
- 最终排序为：亲和候选最前 → `priority` 升序（数值越小越优先）→ 活跃请求数升序 → 初始候选顺序。
- 亲和只决定顺序，不绕过熔断与容量检查。

### 4.3 熔断与容量

- 熔断默认值：连续失败阈值 3、开路 60 秒、半开最大 1 个探测；渠道可覆盖；有效时长小于等于 0 表示该渠道不熔断。
- 计入失败的响应：上游 400、403、429、500、502、503、504。不计入：401、本地 `BadRequestException`、`RoutingException`、一般异常、客户端取消。
- 状态机为 Healthy（Closed）/ Open / HalfOpen：打开期间跳过；到期后半开只放一个探测；探测成功清状态，失败重新打开。
- Redis 可用时熔断状态跨实例；Redis 锁失败时降级为不放行半开探测、失败计数回退本进程。
- 容量：`capacity > 0` 为并发硬上限，0 表示不限流；Redis 信号量租约 TTL 600 秒、锁 TTL 5 秒；Redis 不可用时降级为进程内限制。本实例活跃数始终维护，用于排序与展示，不承诺等于全局并发数。

### 4.4 重试、故障转移与超时

- 同渠道重试：`retry_count = N` 表示首请求之外最多再发 N 次（共 N+1 次 HTTP 请求）。
- 退避：`Retry-After` 优先，否则按 base 2 秒 × 2^attempt 指数退避，叠加向上抖动 20%，并夹在 [2 秒, 30 秒] 区间，不出现零间隔重试。
- 跨渠道故障转移：非流式与流式首字节前都可在满足策略时切换下一个候选；只要已向下游写出至少一行，就不再更换渠道，也不能改写为 JSON 错误。
- HTTP 200 + JSON body 的 rate-limit 错误不做同渠道重试；HTTP 200 + SSE 首个 data 的同类错误做同渠道重试。
- 渠道超时主要约束收到响应头之前，不覆盖整个 SSE 生命周期。
- 客户端可见错误统一为：上游失败 → HTTP 502 + 泛化消息；真实状态码与错误体只进入日志和内部策略。

## 5. 协议转换

### 5.1 支持矩阵

请求、非流式响应与流式 SSE 都覆盖 3×3 组合：三个同协议方向走透传/深拷贝，六个跨协议方向各有一个专用 SSE 状态机（`ChatToResponsesEvents`、`MessagesToResponsesEvents`、`MessagesToChatEvents`、`ResponsesToChatEvents`、`ResponsesToMessagesEvents`、`ChatToMessagesEvents`）。

同协议不等于逐字节透传：请求仍会深拷贝、替换上游模型并清洗工具 Schema；响应仍会恢复客户端可见模型并做结构化输出后处理。未知协议标识与未登记的流式组合在进入流式前返回 400。

### 5.2 canonical 与参数处理

- canonical 是 `Dictionary<string, object?>` 约定，不是持久化格式。请求经 `ToCanonicalRequest` 归一，再由 `FromCanonicalRequest` 生成目标协议字段。
- 参数语义按目标协议映射：`max_output_tokens`/`max_tokens`/`max_completion_tokens` 互转、`reasoning.effort`/`reasoning_effort` 互转、`text.format`/`response_format`/`output_config.format` 互转、`stop`/`stop_sequences` 互转、system/developer/instructions 归一。
- 目标协议使用白名单过滤字段；无等价语义的字段要么进语义拒绝表，要么明确接受静默删除并补测试。
- Messages 目标在过滤后若无有效 `max_tokens`，兜底 `4096`。

### 5.3 语义拒绝

发送上游前直接 400 的已知组合：Responses → Chat/Messages 的 `background`、`context_management`、`conversation`、`previous_response_id`、`prompt`；Responses → Messages 另拒 `parallel_tool_calls` 与 `reasoning`；Messages → Responses 拒 `container` 与 `thinking`；Messages → Chat 拒 `container`（`thinking` 作为兼容扩展透传）；Chat → Messages 拒 `parallel_tool_calls` 与 `reasoning_effort`。原生远程 MCP 转向 Chat 也被拒绝。

### 5.4 工具

- 工具契约归一为 name、description、parameters、native_type、namespace、raw、compat；Responses 原生工具保留 `raw` 以便无损还原。
- namespace 工具按展平名称与最后合法切点解析/恢复；重复工具按作用域与名称去重，只保留第一个定义。
- 工具 JSON Schema 在发往 Chat/Messages 前递归清洗（含 `$ref`/`$defs` 展开，带环检测与节点预算）；无 Schema 的原生工具按类型生成通用 `{input:string}` 或 `{cmd:string}` 形态。
- `tool_choice` 在三种协议间按表映射；`apply_patch` 支持 function/custom/freeform/grammar 形态与增量参数、结果回传。
- 工具调用与结果按调用映射配对；缺失输出会被补偿，未保存映射时原生工具可能退化为普通 `function_call`。

### 5.5 Reasoning、结束原因与 Usage

- Responses reasoning 按 summary 与 encrypted content 规则读取；Anthropic thinking 以签名块保留并在往返时编码/解码。
- 结束原因三向归一：Responses `incomplete(max_output_tokens)`、Chat `length`、Messages `max_tokens` 都映射为 canonical `length`；`tool_calls`/`tool_use`、`content_filter`/`refusal` 同理。
- Usage 提取始终按渠道协议：Responses 读 `input_tokens`/`output_tokens` 与 `input_tokens_details.cached_tokens`；Chat 读 `prompt_tokens`/`completion_tokens`，缓存偏好 `prompt_tokens_details.cached_tokens`；Messages 的输入为 `input_tokens + cache_creation + cache_read`。
- 跨协议只保留 input/output/total/cached 总量；cache creation/read 拆分、reasoning/audio/prediction 明细等在跨协议方向丢失，同协议透传保留。

### 5.6 流式

- SSE 解析按行处理：`event:` 记事件名，多行 `data:` 合并为一个事件，空行触发产出，`:` 注释忽略，EOF 时残留 data 仍产出。
- 同协议透传：原样写下游，同时用 `StreamResponseCapture` 旁路重建响应写入日志；捕获预算为 1 MiB、集合 256 项、单条 data 256 KiB/1024 行，超限截断并标记。
- 跨协议转换：先确认上游流可读，再交给对应状态机；三个协议各有一个响应累积器，按事件类型维护文本、reasoning、工具参数、item/output 索引与 usage。
- 终止信号：Responses 为 `response.completed`/`response.incomplete`（失败 `response.failed`）、Chat 为 `[DONE]`、Messages 为 `message_stop`。
- TTFT 是协议感知的首个有效内容/推理/工具增量时间，不等同第一条非空 SSE 行；Responses 的 `response.created` 早于真实 token。
- 已知取舍：Chat/Responses 转 Messages 时，`message_start.usage.input_tokens` 保持 0（真实输入 token 通常只在终止事件出现），避免整流缓存把 TTFT 推迟到响应结束。SSE 只保存规范化逻辑行，不保存 TCP/HTTP chunk 边界。

## 6. 特殊链路

### 6.1 图片 OCR 降级与视觉转移

- 触发条件三条同时成立：检测到图片、当前主路由不支持图片、路由来自显式模型映射；无映射兼容回退不触发 OCR。
- 图片检测覆盖三种入口：Responses 的 `input_image`（message 与 function_call_output）、Chat 的 `image_url`、Messages 的 `image`。
- 重写时用户图片被移除并排队 OCR；assistant/developer/system 消息与工具结果中的图片替换为占位文本。图片来源支持 data URL 与 http(s) URL，Messages 支持 base64/url source。
- 视觉路由按 owner 显式配置：主 + 兜底，无配置不做自动发现；OCR 缓存键包含 channelId 与 upstreamModel；主失败时按请求级记忆切兜底。未配置或路由失效时返回明确 400 文案。
- 独立 Images API（GAP）：`/images/generations` 仅 JSON、拒绝 `stream=true`，`/images/edits` 为 multipart，代码中存在 `openai` 与 `xai` 两种 dialect 与失败不重试语义；但 `IProxyImagesEndpointService` 无生产实现、`IImagesProxyService`/`ImagesProxyService`/`IImagesUpstreamClient` 未注册，当前请求会因依赖解析失败而不可用。

### 6.2 Web Search 内置工具

- 模式值为 `simulate`/`convert`/`disabled`：`simulate` 由代理注册内置搜索函数并实际执行（provider 按 Key 路由到 Tavily 或 Keenable）；`convert` 只做协议转换，由上游执行；`disabled` 删除原生搜索声明与关联选择，但保留同名普通函数。
- 查询契约：只接受字符串 `query`，去空白后非空，上限 2048 字符；参数 JSON 上限 16 KiB，拒绝重复/多余字段与类型转换。
- 预算：`max_tool_calls` 默认 15、允许 0–64，只约束内置搜索；重复 call ID 也消费轮次预算；达到守卫后移除搜索工具只允许收尾。
- 去重：同 call ID 且同参数复用结果，同 ID 改参数明确失败；连续两次参数错误后关闭后续搜索；provider 不可用直接关闭本批及后续搜索。
- 结果：最多 5 个来源，各字段有字符上限；Key 用数据库原子预留，搜索失败不回滚已预留额度；搜索与续轮共享请求时限与输出预算；搜索开始后不因渠道切换重放请求。

### 6.3 上游请求头与 URL

- Codex 请求头转发只发生在 Responses → Responses：白名单包含 `OpenAI-Beta`、`User-Agent`、`x-oai-attestation`、`x-codex-turn-metadata`、`x-codex-window-id`、`x-client-request-id`、`originator`、`session-id`、`thread-id`、`x-codex-beta-features`；缺失时生成默认值（其中 attestation/session/turn 等当前为测试占位值）。渠道自定义头优先，且不修改共享路由对象。
- URL 拼接：`baseurl` 以 `/` 结尾视为完整 API 根，不补 `/v1`；否则自动补 `/v1`，再拼协议 endpoint（`/responses`、`/chat/completions`、`/messages`）。
- 上游认证、MCP beta 头与 JSON body 由 `HttpUpstreamClient` 统一构造；渠道可配置附加头与会话 ID 占位符。

## 7. 日志、观测与计费

- 日志分四类：`main`（一次客户端请求）、`attempt`（每个渠道尝试）、`ocr`（视觉识别子请求）、`diagnostic`（渠道连接测试）。attempt/OCR 通过父日志关联主请求。
- 生命周期为 queued → processing → success/failed；支持清除全部日志。
- 正文采用内容寻址存储：按内容定义分块、SHA-256 标识、Brotli 压缩、manifest 顺序引用，日志通过槽位引用 manifest；相同块与 manifest 只存一份；日志删除后回收无引用数据。
- 会话元数据独立索引：`ConversationKey`、`ConversationTurnId`、`ConversationWindowId`、`PreviousResponseId`，使列表/统计/补全不必解压正文。会话键回退支持 `x-conversation-id`、Claude Code 的 `X-Claude-Code-Session-Id` 等来源。
- 细粒度 SSE 逐行持久化已删除：失败时只保留最后一行 data 与终止原因（写入 `RequestLog.Error`，上限 2000 字符）。当前日志完整保存请求头、请求体、上游请求/响应、下游响应与 OCR/Web Search 正文，没有脱敏层；这是按审计需求的明确取舍。
- 管理台通过 SSE 实时刷新渠道运行状态与日志；日志展示 TPS、重试状态、渠道尝试次数、上游原始错误（连接测试）。
- 计费：价格来自模型目录（全局 + 渠道级覆盖），支持阶梯计费与按上下文窗口档位；峰谷分时定价按「请求进入时刻」选峰/谷绝对单价，历史成本按快照保存不被追溯改写；费用按币种分组双币种展示。

## 8. 其他已实现子系统

### 8.1 模型目录

- 全局模型目录（`ModelInfo` + 供应商 + 定价计划/规则）与渠道级覆盖统一为动态目录；`/models` 与 `/v1/models` 按当前用户返回可路由模型及渠道覆盖后的字段。
- 目录导入/导出为超管能力，单事务、失败整批回滚；支持远端 JSON 同步的增量与覆盖两种模式（详见边界文档）。
- 渠道模型映射支持匹配键数组；请求模型映射与渠道定价覆盖以渠道覆盖为主键统一。

### 8.2 缓存与数据访问

- 两级缓存：L1 进程内存 + Redis L2 + 广播失效；Redis 不可用时快速降级，不阻塞请求；重连后清理本地 L1。
- 已落地的数据访问治理：删除式/批量操作改为 `DeleteWhere`/`ExecuteDeleteAll` 等谓词操作；owner 解析改为投影并做请求内记忆化；统计聚合下推数据库 `GroupBy`/`Sum`/`Count`；定价解析走快照缓存并按前缀/版本失效；列表接口避免读取大字段（如 `PricingSnapshotJson`）。
- SQL 级验收测试以 `SqlCapture` 捕获 `CommandExecuted` 事件，覆盖 Select/Delete/Update 计数与列断言。

### 8.3 多代理模拟（v2）

- 由模型能力开关 `capabilities.v2_agent_simulation` 控制，默认关闭；开启后 Responses 请求进入服务端多代理运行器，HTTP 请求可传 `multi_agent.enabled: false` 交回普通管线；Chat/Messages 入口不受影响。
- 入口为 responses 且首个路由候选渠道本身支持 responses 直通（官方 OpenAI/ChatGPT 域名，或渠道显式配置 `compat.multi_agent_v2_mode=passthrough`）时跳过运行器：保持 `responses -> responses` 透传，不注入 `ocxp_ma_*` 协作工具。判定只看首个路由候选，亲和、容量与熔断导致的渠道切换仍由普通管线按渠道策略处理；第三方 responses 渠道默认继续运行模拟器。
- 服务端提供 `ocxp_ma_spawn_agent`、`send_message`、`followup_task`、`wait_agent`、`interrupt_agent`、`list_agents`；客户端工具（命令、文件等）仍由 Codex 执行，服务端分配独立 `call_id` 并维护归属。
- 每次模型调用独立 DI 作用域，复用流式管线的路由/认证/转换/日志/计量；增量文本、推理、工具参数即时转发，统一分配代理归属、item ID、output index 与事件序号。
- 会话按 API Key + 会话标识隔离，`previous_response_id` 同域查询；会话标识依次取 `client_metadata.session_id`、`thread_id`、`session-id`/`X-OpenCodex-Multi-Agent-Session` 请求头、`prompt_cache_key`，未提供时生成并通过响应头返回。
- `store: false` 仅内存保存；否则写入 `logs/multi-agent-runs` JSON 快照（`MultiAgent:StateDirectory` 可改）。`multi_agent.max_concurrent_subagents` 默认 3，`MaxModelTurns` 默认 128（整运行共享），上下文阈值默认 64000，超阈值时在推理前请求独立摘要。
- 当前为单实例实现，不包含多实例协调或 Redis 状态方案。

### 8.4 管理台与 API

- API 职责单一化改造已完成（批 0–8）：写操作返回单对象、列表分页、统计拆分、`/pricing` 整链删除、Auth 拆分为 Setup/Session、前端 `src/api/` 分层。
- 保留的重复端点：`/stats` 聚合与 `/stats/*/stream` 与 `/monitor/*` 并存，属于有意保留；代理入口 `/v1/*` 别名是对外兼容契约，不视为历史遗留。
- 渠道诊断（草稿渠道连接测试）保留：以 SSE 返回 `channel_test.*` 事件，完成事件含 `channel_test.completed`，错误事件保留真实上游状态与响应原文，普通代理则对客户端统一 502。

## 9. 测试与维护

- 后端测试项目：`opencodex_proxy/tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj`；最近记录为 1029 个测试全绿（含多代理用例），另有实时流集成测试与 `ProtocolConversionMatrixTests` 覆盖 3×3 非流/SSE 组合。
- 主题入口：主编排/容量/亲和/熔断/故障转移在 `ProxyEndpointServiceTests.cs`；路由与图片能力在 `ProxyVisionRoutingTests.cs`；协议结构在 `ProtocolStructuralCompatibilityTests.cs`；流式在 `SseStreamConverterTests.cs`、三组跨协议流式专项测试与 `StreamResponseCaptureTests.cs`；SQL 治理在 `ServiceQueryGovernanceTests.cs`、`ObservabilityAggregationSqlTests.cs`；多代理用 `--filter 'FullyQualifiedName~MultiAgent'`。
- 修改协议转换器时至少运行完整 3×3 非流/SSE 矩阵；修改可靠性策略时同时核对同渠道重试、故障转移、熔断三个状态集合；修改源码后同步更新本文与边界文档。
