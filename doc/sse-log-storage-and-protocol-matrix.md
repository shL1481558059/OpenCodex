# SSE 日志存储重构与协议矩阵验证记录

## 目标与约束

本次重构的首要目标是降低长对话日志的存储体积，同时保持审计内容完整：

- 完整保存请求头、原始请求、上游请求、上游响应、返回客户端的响应、OCR、Web Search 和 SSE 正文。
- 不脱敏、不截断，不减少业务请求的记录范围。
- 保留 SSE 响应内容，但移除细粒度“流式时序”持久化；TTFT 继续保留。
- 支持长会话、`previous_response_id`、编辑上一轮和新分支，不因历史上下文重复而重复存储相同内容块。
- 不迁移旧详情数据；迁移时直接删除旧详情表。

## 存储实现

日志正文采用内容寻址存储：

1. 对正文进行内容定义分块，使新增或编辑尾部内容时，未变化的历史块仍可复用。
2. 每个块以 SHA-256 唯一标识，并使用 Brotli 压缩。
3. manifest 按顺序引用内容块；日志通过槽位引用 manifest。
4. 数据库唯一索引保证相同块和相同 manifest 只存一份；写入冲突按幂等复用处理。
5. 日志删除后执行无引用 manifest、块的垃圾回收。

会话元数据在 `RequestLogs` 保持独立索引：

- `ConversationKey`
- `ConversationTurnId`
- `ConversationWindowId`
- `PreviousResponseId`

这样列表、统计、补全和前端会话筛选无需解压正文。

## 已移除内容与死代码

- 删除旧 `RequestLogDetails`、`RequestLogStreamLines` 实体和持久化链。
- 删除细粒度流时序采集与详情展示；SSE 逻辑行正文仍保存。
- SSE 行只保留顺序、来源和正文，不再写入或返回逐行时间戳。
- 删除无条件 SSE stderr 调试输出。
- 删除不再使用的图片日志清洗器；本需求明确要求不脱敏并完整记录。
- 清理不再使用的映射、服务注册和相关死代码。

## 协议转换测试矩阵

新增 `ProtocolConversionMatrixTests`，覆盖 `chat`、`messages`、`responses` 的全部 3×3 有向组合：

| 客户端协议 | 上游协议 | 非流 | SSE 流 |
| --- | --- | --- | --- |
| chat | chat | 覆盖 | 覆盖 |
| chat | messages | 覆盖 | 覆盖 |
| chat | responses | 覆盖 | 覆盖 |
| messages | chat | 覆盖 | 覆盖 |
| messages | messages | 覆盖 | 覆盖 |
| messages | responses | 覆盖 | 覆盖 |
| responses | chat | 覆盖 | 覆盖 |
| responses | messages | 覆盖 | 覆盖 |
| responses | responses | 覆盖 | 覆盖 |

每个组合验证：

- 请求模型从 `client-model` 映射为 `upstream-model`。
- system/instructions/messages/input 的协议形状正确。
- `max_tokens` 与 `max_output_tokens` 的 64 token 上限正确映射。
- 响应文本、模型、终止原因和协议对象形状正确。
- Chat usage：`prompt_tokens`、`completion_tokens`、`total_tokens`。
- Messages usage：`input_tokens`、`output_tokens`。
- Responses usage：`input_tokens`、`output_tokens`。
- Chat 和 Responses 的 `[DONE]` 终止行。
- 上游和下游 SSE 逻辑行均进入日志；同协议透传不伪造转换后正文。
- 日志中的转换前响应保留真实上游模型，转换后响应使用客户端可见模型。

## 测试发现并修复的问题

### 1. Responses 转 Chat/Messages 时，上游日志模型被覆盖

复现：上游 Responses SSE 返回 `upstream-model`，客户端请求模型为 `client-model`；转换后客户端事件正确显示 `client-model`，但 `UpstreamResponse.model` 也被写成了 `client-model`。

原因：转换器用同一个 `responseModel` 同时承担“客户端可见模型”和“原始上游模型”，构造日志快照时覆盖了捕获值。

修复：分别维护下游可见模型和捕获到的上游模型；生成客户端事件时使用前者，构造 `UpstreamResponse` 时优先使用后者。

影响评估：只改变日志中的转换前响应快照，不改变返回客户端的 SSE 事件、模型映射、工具调用或终止事件。

### 2. SSE 测试混淆逻辑行与网络块边界

复现：同协议 Chat 透传时，writer 接收的是 `data: [DONE]` 逻辑行；转换路径可能接收带 `\n\n` 的完整 SSE 块。测试统一要求以 `data: [DONE]\n\n` 结尾，导致误报。

修复：终止断言按逻辑内容 `Trim()` 后比较 `[DONE]`，不要求保存网络 chunk、CRLF 或空行字节边界。

影响评估：仅修正测试契约，不修改生产传输或日志正文。

### 3. Messages 首事件 input_tokens 的实时性取舍

矩阵扩展 usage 断言时发现：Chat/Responses 上游的输入 token 通常只在终止事件出现，而 Anthropic Messages 要求 `message_start` 位于内容事件之前。因此若要把真实 input_tokens 回填到 `message_start`，必须缓存完整上游流，会把 TTFT 推迟到响应结束。

最终处理：不采用整流缓存，保持实时 SSE 行为。上游为 Messages 时，首事件已有 input_tokens，继续精确透传；Chat/Responses 转 Messages 时，`message_start.usage.input_tokens` 保持 0，`message_delta.usage.output_tokens` 按终止 usage 完整透传。

影响评估：没有改变既有业务语义或 TTFT；测试明确编码这一协议边界，避免以后误把 0 当成丢日志或转换错误。

## 数据库迁移

SQLite 和 PostgreSQL 的迁移行为一致：

- `Up` 删除 `RequestLogDetails`、`RequestLogStreamLines`，新增内容块、manifest、manifest-chunk 和日志引用表。
- 新增会话键、Turn ID、窗口 ID、`previous_response_id` 列及索引。
- `Down` 可重建旧表结构，但不会恢复已删除的历史详情数据。

这是明确接受的数据迁移策略：项目不考虑历史详情数据。

## 已知边界

- 管理台打开超大 SSE 详情时，当前仍会一次性解压、解析和渲染全部内容，可能产生较高内存占用。
- SSE 保存的是规范化逻辑行，不保存原始 TCP/HTTP chunk、CRLF 与空行的逐字节边界。
- 请求头、Cookie、API Key、图片正文等敏感内容会按需求完整进入日志；没有脱敏层。
- 内容寻址降低重复正文的存储体积，但不会减少每次请求的日志写入流程；写放大仍需通过数据库指标持续观察。
- `Down` 迁移只恢复表结构，不恢复被 `Up` 删除的旧详情记录。

## 推荐持续回归

- 每次修改协议转换器时运行完整 3×3 非流/SSE 矩阵。
- 增加工具调用、拒绝、引用、reasoning、错误流、不完整响应和客户端取消的跨协议矩阵夹具。
- 使用真实 SQLite 和 PostgreSQL 并发写入相同正文，持续验证唯一冲突复用与 GC。
- 用长会话、编辑上一轮、新分支和 `previous_response_id` 构造存储体积基准，比较旧方案与内容寻址方案。
- 对超大 SSE 日志详情做内存和首屏耗时基准，后续可引入分段读取与虚拟列表。
