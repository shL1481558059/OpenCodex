# OpenCodex 未实施事项与待决策

> 基线：提交 `87587d47`（2026-09-30）。本文汇总当前仍未实施、待拍板或已明确接受的边界；每条待实施项都给出了代码证据、目标与验证方式。
> 已实现逻辑见 [implemented-logic.md](implemented-logic.md)。被本文替换的原方案文档可在 git 历史（提交 `87587d47` 及之前）中找回。

状态约定：

- **待实施**：方向已明确、只差施工与测试。
- **待决策**：需要产品/运维给出取舍后才能动代码。
- **已接受边界**：明确不做或暂时不覆盖，写下来是为了避免重复讨论与误判为缺陷。

## 1. 待实施：渠道熔断与错误语义

### 1.1 上游 400 计入渠道熔断（原 C.4，待决策后实施）

- 现状：`ChannelCircuitBreakerService.ShouldCountFailure` 把上游 `BadRequest` 与 `Forbidden`、429、5xx 同等计入失败；阈值默认 3。
- 问题案例：某 chat 渠道 349 次 200 中夹有 24 次上游 400（约 6%）；连续 3 次 400 即把该渠道开路 5000 秒，同模型再无其它渠道时客户端只能收到 429。
- 400 表示单次请求不合规，重试必然再失败，但渠道对其它请求是健康的。
- 两种可选方向：把 400 从熔断计数移除；或给 4xx 单独设置远小于 5xx 的开路时长，不复用渠道级 `CircuitBreakDurationSeconds`。
- 风险：若某上游用 400 表达整体不可用，移除计数会让失败请求持续打过去；可保留 `Forbidden` 计数作为折中（鉴权失效是渠道级问题）。
- 验证：`ChannelCircuitBreakerService` 针对 400 的计数断言，以及连续 400 后渠道仍在候选内的编排用例。

### 1.2 候选耗尽 429 文案不区分跳过原因（原 C.5，待实施）

- 现状：`ProxyEndpointService` 在候选耗尽时统一抛出 `all enabled channels for {model} are at capacity`，但候选被跳过的实际原因有三类：熔断开路、容量租约失败、无匹配/未启用。
- 问题：排查熔断事故时必须翻 Redis 状态才能确认原因；管理台已有 `/channels/{id}/health-reset` 可手工清除熔断，但没有提示。
- 方向：按跳过原因分别计数，错误文案区分「熔断中（附剩余开路时间）/容量已满/无匹配渠道」，并在熔断文案中提示重置入口。
- 风险：错误文案属于对外契约，改动前确认没有客户端按字符串匹配。
- 验证：三种跳过原因各自的路由异常用例。

## 2. 待实施：协议转换遗留问题

### 2.1 `anthropic_thinking_encrypted` 会随 Chat 上游请求外泄（原 C.2，待实施）

- 现状：`ProtocolConverter.Requests.cs:214-215` 在 `preserve_thinking_history=true` 时把 `reasoning_content` 与 `anthropic_thinking_encrypted` 一并拷进 chat 消息。后者是 OpenCodex 自有编码（`ocxp-thinking-v1:<base64>`），Chat Completions 协议没有该字段。
- 触发路径：Messages 客户端（如 Claude Code）请求 chat 渠道且渠道开启该开关；Codex 走 Responses 入口不受影响。
- 风险：宽容上游忽略未知字段；严格上游可能直接 400。影响面限于 messages→chat 且开启开关的渠道。
- 方向：评估 chat 出站只保留 `reasoning_content`；删除请求侧字段前先确认没有 OpenCodex 级联部署依赖响应侧的 `anthropic_thinking_encrypted` 做思考往返。
- 验证：messages→chat 转换断言上游消息不含该键，同时保留 chat 响应方向的既有用例。

### 2.2 同协议短路不清理 `_ocxp_*` 内部标记（原 C.3，待实施）

- 现状：`ProtocolConverter.ConvertRequest` 的 `sourceProtocol == targetProtocol` 分支只做工具 Schema 与 `tool_choice` 清洗就直接返回；跨协议路径则显式 `Remove` 内部标记（`ProtocolConverter.Requests.cs:196-197`、`279-280`、`395-396`）。
- 已实测：chat→chat 且开启 `preserve_thinking_history` 时，上游请求体携带 `_ocxp_preserve_thinking_history`；用户手工注入的 `_ocxp_thinking_budget_tokens` 同理。
- 方向：短路分支统一移除 `_ocxp_` 前缀键。同协议下这些标记本身没有语义（历史 `reasoning_content` 原样透传），移除不改变行为，也能覆盖未来新增标记。
- 风险：低。若将来需要在级联实例间传递内部标记，应改为显式白名单而不是依赖短路泄漏。
- 验证：chat→chat、responses→responses、messages→messages 三个方向断言上游请求体无 `_ocxp_` 前缀键。

## 3. 待实施：数据访问与缓存

### 3.1 EF 查询治理残留（原 `ef-query-cleanup` 第 8.1 节）

- `ReplaceWebSearchConfig` 收尾调用 `ReadWebSearchConfig` 时会对 `WebSearchSettings` 再查一次（`WebSearchService.cs` 第 176 行附近），目标是降到 1 次查询。
- `ClearLogs` 在 PostgreSQL 上已从 `TRUNCATE ... CASCADE` 改为多条 `DELETE FROM`（符合「非必要不用裸 SQL」），代价是大表清空明显变慢且产生大量 WAL；需要确认这个取舍是否接受，若接受不了需单独设计批量清理任务。
- `BuildAttemptStats` 的失败判定缺少 `LifecycleStatus == null` 前置，与 `IsSuccessfulPredicate`、`ApplyRequestStatusFilter` 口径不一致（既有逻辑，非本轮引入）。一条 `success` 但 `StatusCode=500` 的 attempt 在它眼里是失败，在 recent-errors 眼里不是。
- PostgreSQL 侧只验证过 `ToQueryString()` 生成的 SQL 文本，没有连真实 PG 实例跑端到端；聚合下推后 double 转整型、除法舍入、NULL 比较的 provider 差异会直接影响统计数字。
- 验证：`ServiceQueryGovernanceTests`、`ObservabilityAggregationSqlTests`、`ObservabilityServiceTests`、`LogContentStoreTests` 是现有 SQL 级验收入口；新增用例沿用 `Infrastructure/SqlCapture.cs` 设施。

### 3.2 数据访问与缓存优化阶段 3/4（原 `db-query-and-cache-optimization`，待实施）

已落地部分（阶段 0–2 的大部分）：谓词删除与批量删除、投影查询、统计聚合下推、plans/rules 批量取回、部分列更新、owner 解析记忆化、SQL 捕获测试基础设施。

未实施部分：

- 定价缓存仍是 `PricingCacheTtl = 60 秒` + Redis 版本号机制，未切换到 `v1:pricing:` 前缀失效与无过期持久化；`BumpPricingVersion()` 尚未改为 `RemoveByPrefixAsync`，接入配置的定价规则与 provider 排序也尚未整体进缓存域。
- `ConfigService`、`ObservabilityService` 中的裸 `IMemoryCache` 渠道快照尚未并入统一 `ICacheService` 域；多实例下仍靠 10 秒 TTL 兜底。
- `AccessApiKey.LastUsedAt` 回写仍挂在缓存回源路径；切无过期缓存前必须决定处理方式（建议节流异步批量写）。
- 尚无 superadmin 清缓存运维端点；失效广播的新旧格式并存切换未完成。
- 阶段 4 收尾（文档、清理、回归清单）未做。
- 为什么必须缓行：U3.1 影响计费、U3.3 影响缓存一致性，都属于「漏一个失效点就产生永久脏数据」的改动，必须单独发布并观察，不能混在其它改动里。
- 验证：改动前先补齐 `TwoLevelCacheService`（前缀失效、Redis 降级、重连清 L1）与「改完立即读到新值」的逐域用例；上线前对每个缓存域的失效触发点逐条走查。

### 3.3 请求日志留存与瘦身（原 B.5 待实施部分，待决策）

- 内容寻址存储已经完成（重复正文只存一份）；尚未实施的是 attempt 日志瘦身。
- 仍缺统一策略：请求详情、上游原始 SSE、attempt 日志的保留上限、TTL、失败请求是否留存。
- 当前失败流只保留最后一行 data 与终止原因（`RequestLog.Error`，2000 字符上限），不再逐行持久化 SSE。
- 风险：无上限的留存量会持续放大数据库体积；策略调整会影响审计与排障能力，需要运维输入。

## 4. 待实施：计费与目录

### 4.1 峰谷计费批 5（可选批次，待实施）

- 日志详情页不展示 `pricing_phase`；`prd/11` 与 `prd/18` 未补 `REQ-OBS` 条目；没有只读试算端点。
- 每请求的 `pricing_phase`/`phase_source`/`billing_instant`/`time_zone`/`matched_window` 已写入快照，展示与文档同步属于剩余工作。
- 注意：导出文档版本已升到 2，v1 文档覆盖导入会抹掉本地峰谷配置；跨版本互导需要按新版本说明执行。

### 4.2 模型目录同步的既定边界（已接受，暂不实施）

- 覆盖模式不可撤销：本地同名模型的名称、描述、匹配规则、能力、价格与 `source` 会被远端取代，唯一兜底是先导出当前目录。
- 增量模式下远端对存量模型的改价不会下发，必须显式走覆盖模式；两种模式都不清理远端已删除的模型。
- 覆盖模式不会因远端 `pricing: null` 删除本地价格，「远端改为不计费」只能人工处理。
- 无同步历史与上次同步时间（不建表），排障只能看 warning 日志；失败日志不截断，靠容器日志轮转兜底。
- 不做定时同步、私有源鉴权、签名校验；不同步渠道级覆盖；多实例并发同步无分布式锁（单事务保证不出半写状态）。

## 5. 待决策清单（需要产品/运维明确输入）

1. 是否增加 Controller 激活 smoke test；是否接受 `AddControllersAsServices()` + DI `ValidateOnBuild`（注意：默认 `AddControllers()` 下 `ValidateOnBuild` 不能发现 Controller 漏注册）。
2. `OPENCODEX_LOG_PATH`/`OPENCODEX_LOG_LEVEL`/`OPENCODEX_LOG_VIEW_LEVEL`：补实现日志落盘，还是从文档中删除宣传。当前项目没有对应日志框架，三者不生效。
3. 渠道批量运维便利层（批量测试、批量编辑、归并视图、诊断路由别名）的实际使用频率；低频功能可评估删除以降低维护面。
4. 是否引入 Testcontainers（`postgres:17-alpine`）让统计与聚合用例在两个 provider 各跑一遍；这是当前最大的测试缺口。
5. Dashboard 队列/错误卡片是否为刚需实时视图；当前保留重复端点与伪轮询遗留逻辑，若无刚需可评估删除而不必改造。
6. 管理台导入导出的增强范围：明文密钥保留为前提，可做的只有超管权限、schema 校验、冲突预览与审计记录。

已解决、无需再决策的历史问题（留档避免重复讨论）：`/images` 已补齐实现（含 channels `images` 类型与双 dialect）；`/pricing` 整链已删除；`intercept_probe_requests` 与渠道诊断确认在用；部署形态为 PostgreSQL + Redis；桌面设置跨语言覆盖 Bug 已修复。

## 6. 已接受的边界（明确不做或暂不覆盖）

- **日志不脱敏**：请求头、Cookie、API Key、图片正文按审计需求完整入库；日志表与导出文件的访问控制、磁盘加密由部署方负责，应用层不提供脱敏开关。
- **明文密钥保留**：`AccessApiKeys` 与 Web Search Key 的明文存储、导出为业务需要，不做移除；只做权限与审计增强。
- **数据库约束策略**：不加数据库外键，级联关系由服务层收敛；定价匹配留在内存做（精确/前缀/通配 + 优先级 + 模式长度 + provider 排序无法写进 `WHERE`）；SQLite 价格列是 `TEXT`、PostgreSQL 是 `numeric(18,8)`，禁止把价格比较下推到数据库，否则 SQLite 会变成字符串比较。
- **日志表规模**：不做时间分区或归档；聚合下推解决进程内存问题，表本身变大后的查询变慢需要独立课题。
- **SSE 详情渲染**：管理台打开超大 SSE 详情仍会一次性解压、解析与渲染，可能产生较高内存占用；后续要改应做分段读取与虚拟列表。
- **SSE 存储粒度**：只保存规范化逻辑行，不保存原始 TCP/HTTP chunk、CRLF 与空行逐字节边界。
- **多代理（v2）**：单实例实现，不含多实例协调或 Redis 共享状态；官方密文互通与外部 hosted 运行导入不在支持范围；客户端回传历史会丢弃 hosted 多代理事件与 `agent` 属性，因此必须保留服务器状态与调用映射；不宣称与官方 v2 全部行为一致，Codex UI 也不保证原生显示服务端代理树。
- **峰谷计费**：不做按用户/API Key/渠道的差异化峰谷，不做上游账单对账与历史成本重算，不做谷段配额。
- **独立 Images API**：不做上游重试，不产生 attempt 子日志语义；`stream=true` 明确拒绝。
- **渠道诊断**：与普通代理的行为差异（SSE 内暴露真实上游状态与响应原文、无 attempt 子日志、完成事件为 `channel_test.completed`）为有意保留，不拉平。
- **视觉转移前置**：每个有图片流量的 owner 必须显式配置主与兜底视觉路由；视觉模型必须先在模型信息中标注 `supports_image`，否则候选列表为空、配置无法生效。未配置时带图请求返回 400 是预期行为。
- **Redis 依赖度**：Redis 不可用时容量、熔断、亲和都会降级为本进程语义，不阻塞请求；多实例下这是有意的可用性优先取舍。
- **PostgreSQL 测试覆盖**：当前测试套件硬编码 SQLite，PG 侧端到端为零覆盖，属于已知边界（对应上方决策 4）。

## 7. 建议落地顺序

1. 低风险纯清理：C.3（同协议 `_ocxp_*`）、C.5（429 文案）、A2 二次查询、attempt 统计口径。
2. 需要先拍板的改动：C.4（400 计入熔断）、C.2（Chat 出站标记）、`ClearLogs` provider 取舍、日志留存策略。
3. 影响计费的改动（缓存阶段 3 的 U3.1/U3.3、峰谷批 5）单独发布、单独观察，不与其它改动混批。
4. 每项都先写复现测试或最小复现步骤，再改代码，最后确认全量 `dotnet test` 通过；涉及双 provider 的改动按 `SqlCapture` 断言补 SQL 级用例。
