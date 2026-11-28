# 流式输出、审计快照与 Responses 续接资源约定

本文件合并原 pipeline 文档中的有效输入、写出、所有权与终态契约，统一说明当前实现。历史阶段的整轮缓冲、独立协议 JSON、进程总配额和旧配置表不再作为现行部署说明。

## 1. 配置来源与边界

协议转换使用 [环境样例](<../../.env.example>) 中的 **20 个 ProtocolConversion__* 必填键**，由进程环境或运行 .env 提供；进程内读取一次，修改后重启。缺失、非法或越界值按现行启动契约失败，不使用旧 JSON 或猜测默认值。配置读取见 [ProtocolConversionParameters](<../src/Libraries/OpenCodex.ProtocolConversion/ProtocolConversionParameters.cs>)。

当前没有快照/审计存储的进程级总量配额，也没有应用容器 mem_limit。不要恢复 SnapshotStoreMaxBytes、APP_MEMORY_LIMIT 或历史 MaxTotalBytes/MaxFiles 生产配置。**单次请求、单事件、捕获及队列预算仍然存在**；取消进程总配额不等于取消这些约束，也不是恒定内存或无限磁盘的保证。Redis 自身的内存策略是独立部署约定。

下表仅列环境样例的预置值，不是代码默认值、性能测量或建议最优值；实际生效值以部署环境为准。

| 环境键后缀（均加 ProtocolConversion__） | 样例值 | 作用 |
|---|---:|---|
| MaxRequestBodyBytes | 268435456 | 完整解码/还原后的逻辑请求共用上限 |
| StringSegmentCharacters | 4096 | 字符串编码/拼接分段 |
| FlushBytes | 16384 | 快照、证据等分片粒度 |
| GeneratedFrameCacheBytes | 32768 | 生成帧缓存 |
| CaptureCopyCharacters | 8192 | 捕获复制字符块 |
| SseReadBufferCharacters | 4096 | SSE 读取缓冲 |
| SseRetainedBufferCharacters | 65536 | SSE 保留缓冲 |
| SseMaxEventBytes | 16777216 | 客户端完整事件上限 |
| SseMaxCaptureBytes | 67108864 | 一次完整流捕获累计上限 |
| SseMaxCaptureItems | 4096 | 捕获条目数 |
| SseMaxCaptureFields | 8192 | 捕获字段数 |
| SseMaxBufferedRoundBytes | 33554432 | 需要延迟验证/发布的单轮缓冲预算 |
| SseMaxBufferedRoundFrames | 65536 | 单轮缓冲帧数 |
| SseMaxProbeBytes | 16777216 | 上游预内容探测字节预算 |
| SseMaxProbeFrames | 1024 | 上游探测帧数 |
| SseMaxWebSocketMessageBytes | 33554432 | 上游 Responses WS 单消息预算 |
| SseMaxBatchBytes | 16384 | SSE 批量写出阈值 |
| SseMaxFlushDelayMilliseconds | 10 | 完整事件批量写出等待时间 |
| SseMaxQueuedEvents | 8 | 输出队列事件数 |
| SseMaxQueuedBytes | 1048576 | 输出队列字节预算 |

MaxRequestBodyBytes 对 HTTP、客户端 WS 首条/后续消息、续接还原后的完整历史和最终上游请求生效，不是传输分块编码的线上字节数。上游 WS 消息预算与客户端请求预算不同，不能互换。完整捕获累计计费也不等于最终响应 JSON 长度，终态载荷可能重复早期增量。

## 2. 输入、转换与所有权

- HTTP SSE 和 Responses WS 进入共享的事件解码、探测、捕获与转换路径；不要为了观察事件再反向解析生成的 JSON 文本。
- [SseFrameReader](<../src/Libraries/OpenCodex.ProtocolConversion/Streaming/SseFrameReader.cs>) 的 ReadLines 接收物理行或完整帧，任意字符块使用 ReadChunks。行尾规范化、多行 data、注释与未知字段遵守既有线协议，不把不完整 EOF 帧标成已成功写出的终态。
- [SseFrame](<../src/Libraries/OpenCodex.ProtocolConversion/Streaming/SseFrame.cs>) 固定生成容器图的可见状态，避免生产者后续修改泄漏到已发布事件。借用的 JsonElement/JsonDocument 不能越过其所有者生命周期。
- 转换与捕获共享已接受的语义字段、租约和累积结果。字符串/工具参数增量保留空白、片段边界和跨片段代理对；不是按文本相等猜测历史身份。
- 现行普通转换输出可增量交付；需要整批校验的原生工具完成事件仍延迟到验证/提交后发布。不能把两者统一描述为整轮 typed+wire 队列，也不能声称所有路径零复制、零解析或恒定堆占用。

主编排见 [ProxyStreamService](<../src/Libraries/OpenCodex.Core/Services/Proxy/ProxyStreamService.cs>) 与 [转换循环](<../src/Libraries/OpenCodex.Core/Services/Proxy/ProxyStreamService.Conversion.cs>)；捕获见 [StreamResponseCapture](<../src/Libraries/OpenCodex.ProtocolConversion/Protocols/StreamResponseCapture.cs>)。

## 3. 完整事件写出与背压

- [HTTP 写出适配器](<../src/Presentation/OpenCodex.Api/Infrastructure/ProxyStreamResponseWriter.cs>) 按完整事件和类型元数据写出，不在任意正文中搜索终态名称。
- 首帧/首业务事件、心跳/注释、错误和终态按现行策略及时 flush；普通完整事件由字节阈值和可复用的流内定时器合并。
- 写出、flush、上游读取与取消生命周期相互衔接；等待上游时仍能 flush 已接受批次，不新增每个 delta 的定时器或无界生产队列。
- 一个事件可能超过批量阈值，但仍必须满足完整事件预算。超限、取消或 I/O 失败不是静默截断后成功。
- 客户端 WS 保留消息边界，分片只有最后一片标 EndOfMessage。完整默认转义后的大小与 SSE relaxed 编码大小可能不同；先核对完整消息预算，不能把部分消息补成虚假成功。
- Flush 成功不是 TCP 对端已收到的确认；反向代理/套接字缓冲仍可能影响用户看到首个 token 的时间。

原生/多代理 HTTP 的现有结束行为与普通代理的 DONE 行约定保持区分，不因共用 writer 自动增补新的终止标记。

## 4. 审计快照与共享存储

[ProxyAuditSpool](<../src/Libraries/OpenCodex.CoreBase/Domain/Proxy/ProxyAuditSpool.cs>) 和 [SnapshotStore](<../src/Libraries/OpenCodex.CoreBase/Domain/Proxy/SnapshotStore.cs>) 管理完整快照、引用、页、所有者与释放。当前全局不设存储总量上限；资源使用量统计不是内存预分配，也不是任意对象的精确 CLR 堆计量。

- 保留请求/响应的现行原始证据或规范化审计投影、内容寻址与哈希边界，不用诊断前缀冒充完整正文。
- 快照 sealing 后才提供稳定读取；引用及租约存活期间不能删除底层页。取消/释放回收请求所拥有的暂存资源，已有孤儿清理仅处理自身管理的过期文件。
- 大字段可分段编码/落盘，但显式请求最终 DTO、字符串或旧 WireText 时仍可能物化大对象；大属性名、扩展 DTO 与公共 API 也可能需要额外工作内存。
- 日志事务/内容持久化失败要保留真实失败，不发布成功终态；外部所有者的快照按现有事务与释放契约处理。
- 生产使用专用、受限权限的持久目录；按部署 SOP 检查磁盘空间、备份与留存，必要时采用操作系统管理约定。该存储不是跨进程/跨租户自动配额或合规留存系统。

请求级预算协调见 [RequestExecutionBudget](<../src/Libraries/OpenCodex.Core/Services/Proxy/RequestExecutionBudget.cs>)。这次说明整理不改变存储、额度、计费或数据库 Schema。

## 5. 语义提交、工具完成与终态

- 角色、response ID、文本、reasoning、工具开始或未知业务事件一旦交给下游 writer，按现行保守语义提交处理；不能透明整轮重试或切渠道重放已经交付的动作。
- 仅头、注释或心跳不等于模型业务提交。内部搜索在对应上游轮次接受、校验后执行；后续失败不能重复已经执行的工具。
- 可执行工具批次必须通过身份、名称、call ID、payload、批次一致性与续接提交验证，之后才发布可执行完成/成功；不完整工具保持不可执行。
- 顺序、片段和实际工具形状保持现有契约。磁盘回放可能重新解码，并不意味着零解析。
- 审计完成及终态写出按真实结果决定成功；已尝试但失败的终态不能被矛盾的第二个成功替换。已经发送的字节无法撤回，传输失败仍可能以中断连接结束。

这些是业务与协议边界，不是本次清理要移除的兜底实现。

## 6. Responses 历史与原生客户端生命周期

[ClientConnectionCache](<../src/Libraries/OpenCodex.Core/Services/MultiAgent/ClientConnectionCache.cs>)、[ClientHistoryVersion](<../src/Libraries/OpenCodex.Core/Services/MultiAgent/ClientHistoryVersion.cs>) 与共享快照保存可恢复的版本和已知响应身份。续接还原完整逻辑请求后仍受请求预算约束，不能只按客户端新传的 delta 判断大小。

- 连接、会话、owner/API Key 与 response 引用按现行身份契约隔离；缺失历史要求完整历史恢复，不凭相似内容猜测重试。
- 连接结束与租约释放回收关联的内存引用；上游连接池、客户端 WS 历史、原生线程持久状态和旧服务端 Runtime 是不同生命周期，不能套用同一张历史缓存限额表。
- [NativeClientSessionStore](<../src/Libraries/OpenCodex.Core/Services/MultiAgent/NativeClientSessionStore.cs>) 的持久线程状态位于配置状态目录的 native-client-v1 中。内存对象退役不等于删除 JSON/已提交 turn，也不等于可以重跑已执行工具。
- [NativeClientStateCache](<../src/Libraries/OpenCodex.Core/Services/MultiAgent/NativeClientStateCache.cs>) 是可选 Redis 协调缓存，组状态只在可信身份与磁盘 generation 对得上时参与恢复。Redis 不是线程/响应存在与否的权威来源，正文与回放不因该缓存改为 Redis 持久源。
- 请求与等待者都持有引用；只有无存活所有者/等待者的组才能退役，不能制造第二把保护同一会话的锁。
- 已提交 turn 的幂等返回沿用当前请求身份、规范化数据与持久记录。损坏/缺失记录不代表可安全重执行，memory-only 保护不跨进程重启。
- 当前持久状态没有统一自动保留期；原生线程目录、日志数据库、正文引用与快照目录一起纳入实例备份/留存 SOP，不当作编译缓存删除。

旧服务端 Runtime 与绑定注册表仍有业务调用，清理旧 helper 不取消它们；旧 raw Gate 兼容入口不具备完整等待者生命周期的限制也仍是兼容边界，不在文档整理中承诺已完成迁移。

## 7. 运维与证据使用 SOP

1. 升级前补齐当前20个协议环境键并确认实例使用的配置文件/注入环境；修改后重启。不要从历史 JSON 或本文件旧表推导部署默认值。
2. 为日志、密钥、原生线程及审计页使用授权访问与持久化目录；监测实际磁盘/内存，不把配置字节数当已预分配内存或性能最优值。
3. 请求日志可包含原始头、正文和凭据，没有统一脱敏保证。对外分享前人工脱敏，优先保留必要的身份、错误、时间与实际结果证据。
4. 按业务管理约定处理留存与容量，不直接删仍被存活会话/日志引用的文件，不在运行时清依赖或编译输出。
5. 缺失最终 usage 沿用现有数值计费兼容行为，不代表已确认零用量；usage 置信度提案未实施，本次不改变计费或添加新 Schema。

当前既有测试覆盖相应协议、工具、取消、审计与历史契约；维护时按改动需要复用，不为本文合并新增单元/回归测试或自动清理机制。
