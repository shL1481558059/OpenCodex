# OpenCodex 代码清理与重构改造清单

> 状态：待实施。本文档用于记录后端代码清理、业务逻辑整理和维护性改进的分阶段方案。

## 前置决策点

1. 生产环境是否真的会用到图片 OCR 回退？
2. Web Search 是否还需要“模拟”模式，还是只保留“转换/关闭”？
3. 管理台的“测试渠道/发现模型”是否高频使用？
4. 桌面端是否依赖 `intercept_probe_requests` 特殊拦截？
5. 生产数据库是 SQLite 还是 Postgres？Redis 二级缓存是否必须？

## Phase 0：基线

- 运行 `dotnet test opencodex_proxy/OpenCodex.sln`，确认当前测试全绿。
- 记录当前 `git status` 和未提交改动，避免覆盖已有修改。
- 备份生产数据库和当前镜像，便于回滚。
- 为对外 API 的 JSON 结构建立契约测试，防止清理时破坏前端兼容。

## 1. 删除低收益高复杂度功能

### 1.1 OCR 图片回退

- 目标：去掉图片 OCR 回退链路，收到不支持图片的请求时直接返回清晰错误，或按渠道能力透传。
- 涉及范围：
  - `opencodex_proxy/src/Libraries/OpenCodex.Core/Services/Proxy/ProxyOcrService.cs`
  - `opencodex_proxy/src/Libraries/OpenCodex.Core/Services/Proxy/ProxyImageFallbackService.cs`
  - `opencodex_proxy/src/Libraries/OpenCodex.Core/Services/Proxy/ProxyImagePayloadRewriter.cs`
  - `opencodex_proxy/src/Libraries/OpenCodex.Core/Services/Proxy/ImageLogSanitizer.cs`
  - `OpenCodex.CoreBase/Services/Proxy` 下相关接口
  - `OpenCodex.CoreBase/Domain/Proxy` 下相关模型
  - `OpenCodex.Api/Hosting/OpenCodexServiceCollectionExtensions.cs` 里的 DI 注册
  - OCR 缓存、本地 OCR 模型等环境变量配置
  - `frontend/src/Logs.vue` 的 OCR 展示
  - `tests/ProxyImageFallbackTests.cs`
- 风险：如果客户端依赖图片 OCR 文本注入，删除会造成功能回退。
- 验证：图片请求返回预期错误；日志不再出现 `request_type=ocr`；非图片代理路径测试全绿。

### 1.2 Web Search 模拟模式

- 目标：只保留 `convert` 和 `disabled`，删掉整套 `simulate` 状态机。
- 涉及范围：
  - `OpenCodex.Core/Services/WebSearch/WebSearchSimulator.cs`
  - `WebSearchSimulator.NonStream.cs`
  - `WebSearchSimulator.Streaming.cs`
  - `WebSearchContinuationRequest.cs`
  - `WebSearchRequestPolicy.cs`
  - `WebSearchResponsePayload.cs`
  - `WebSearchSimulationLog.cs`
  - `WebSearchSimulationUpstreamException.cs`
  - `WebSearchStreamEventState.cs`
  - `WebSearchToolCallParser.cs`
  - `WebSearchToolResult.cs`
  - `OpenCodex.CoreBase/Services/WebSearch/IWebSearchSimulator.cs`
  - `OpenCodex.CoreBase/Domain/WebSearch/WebSearchModes.cs`
  - `WebSearchController.cs`
  - `WebSearchService.cs`
  - `frontend/src/WebSearch.vue`
  - 相关测试
- 风险：如果 Tavily 调用目前依赖模拟器，删除后 Web Search 会失效。
- 验证：`simulate` 模式不再被接受；`convert` 和 `disabled` 行为不变。

### 1.3 渠道诊断/测试渠道

- 目标：删除 `discover-models` 和 `test-channel` 调试链路，或只保留极简 discover。
- 涉及范围：
  - `OpenCodex.Core/Services/ChannelDiagnosticsService.cs` 及 partial 文件
  - `OpenCodex.CoreBase/Services/IChannelDiagnosticsService.cs`
  - `OpenCodex.Api/Controllers/ChannelDiagnosticsController.cs`
  - `OpenCodex.CoreBase/DTOs/ChannelDiagnostics/`
  - DI 注册
  - `frontend/src/Channels.vue`
  - `frontend/src/channelTestState.js`
  - `tests/ChannelDiagnosticsLogTests.cs`
- 风险：失去管理台手动验证渠道的能力。
- 验证：删除路由后前端不再调用；渠道 CRUD 和代理路径测试全绿。

### 1.4 Probe 拦截

- 目标：删除 `ProbeRequestInterceptor` 和桌面端 `intercept_probe_requests` 设置。
- 涉及范围：
  - `OpenCodex.Core/Services/Proxy/ProbeRequestInterceptor.cs`
  - `OpenCodex.Api/Controllers/ProxyController.cs`
  - `OpenCodex.Api/Configuration/DesktopSystemSettingsStore.cs`
  - `OpenCodex.Api/Configuration/IDesktopSystemSettingsStore.cs`
  - `SystemSettingsDtos.cs`
  - `SystemSettingsController.cs`
  - `frontend/src/SystemSettings.vue`
  - `frontend/src/Setup.vue`
  - 相关测试
- 风险：桌面客户端如果依赖该拦截逻辑，行为会变。
- 验证：设置页不再出现该开关；普通代理请求不受影响。

## 2. 去掉 Mapster

- 目标：删除运行时表达式映射，改用显式映射方法或 DTO 构造器。
- 涉及文件：
  - `OpenCodex.Core/Services/Mapping/OpenCodexMappingConfig.cs`
  - `OpenCodex.Core/Services/AuthService.cs`
  - `OpenCodex.Core/Services/UserService.cs`
  - `OpenCodex.Core/Services/ModelPricingService.cs`
  - `OpenCodex.Core/OpenCodex.Core.csproj`
- 做法：
  - 为 `User -> UserDto`、`ModelPricing -> ModelPricingDto` 写静态映射方法。
  - 删除 `TypeAdapterConfig` 注册和 `OpenCodexMappingConfig.Register()`。
  - 删除所有 `using Mapster` 和 `.Adapt<T>()`。
- 验证：用户、渠道、价格接口的 JSON 输出与改造前保持一致。

## 3. DTO 文件瘦身

- 目标：拆开超大 DTO 文件，删除前端和后端都不再使用的字段。
- 重点文件：
  - `ObservabilityResponses.cs`（1522 行）
  - `RequestLogDtos.cs`（809 行）
  - `ModelCatalogDtos.cs`
  - `StatsDtos.cs`
  - `WebSearchResponses.cs`
- 做法：
  - 扫描后端构造位置、Controller 返回和前端渲染字段。
  - 按功能拆成一个 DTO 一个文件。
  - 只删除确认无引用的字段，并用契约测试锁住 JSON 形状。
- 风险：前端用了旧字段名，删除后页面空白或读取 undefined。
- 验证：前端所有请求路径回归；后端无引用错误。

## 4. 协议层按方向拆分 Codec

- 目标：把 `ProtocolConverter` / `SseStreamConverter` 拆成明确的 `IProtocolCodec`。
- 涉及范围：
  - `OpenCodex.Core/Protocols/ProtocolConverter.cs` 及所有 partial 文件
  - `OpenCodex.Core/Protocols/SseStreamConverter.cs` 及所有 partial 文件
  - `StreamResponseCapture.cs`
  - 各协议 Accumulator
  - `ApplyPatchJsonDeltaDecoder.cs`
  - 相关协议测试
- 做法：
  - 先定义支持矩阵，只保留真实使用的方向。
  - 为每个方向定义 codec，例如 `ResponsesToChatCodec`、`ChatToResponsesCodec`。
  - 公共工具抽成独立 helper。
  - 每个 codec 只依赖自己的状态机。
- 风险：协议兼容是系统核心，拆错会导致 Codex CLI 或上游请求失败。
- 验证：现有协议测试全部保留并作为回归契约。

## 5. 内部强类型化

- 目标：内部不再到处使用 `Dictionary<string, object?>`。
- 涉及区域：
  - `OpenCodex.Core/Protocols/`
  - `ProxyStreamService.cs`
  - `ProxyNonStreamService.cs`
  - `HttpUpstreamClient.*`
  - `StreamResponseCapture.cs`
  - `SseStreamConverter.Parsing.cs`
- 做法：
  - 定义强类型模型：`ProtocolRequest`、`ProtocolMessage`、`ProtocolToolCall`、`ProtocolToolResult`、`ProtocolUsage`、`ProtocolStreamEvent`。
  - 在 API 边界做一次解码，内部转换全部走强类型。
  - 输出时再序列化回 wire format，保持对外 JSON 不变。
  - 逐方向迁移，不一次性替换全部。
- 风险：改动面最大，容易引入协议差异。
- 验证：用现有兼容性测试作为主回归。

## 6. 模型目录与计费收敛

- 目标：把模型目录相关七套实体收敛成“渠道模型能力 + 价格表”两层。
- 涉及范围：
  - `ModelProvider.cs`
  - `ModelInfo.cs`
  - `ChannelModelInfo.cs`
  - `ModelPricingPlan.cs`
  - `ModelPricingRule.cs`
  - `ChannelModelMapping.cs`
  - `ModelPricing.cs`
  - `OpenCodex.Core/Services/ModelCatalogService.cs`
  - 模型目录 DTO
  - `frontend/src/Pricing.vue`
  - `frontend/src/Channels.vue`
  - SQLite/Postgres 两套迁移
- 做法：
  - 保留 `Channel` 作为渠道配置。
  - 保留 `ModelPricing` 作为全局价格表。
  - 如渠道级模型覆盖确实需要，只保留 `ChannelModelInfo`。
  - 删除 `ModelProvider`、`ModelPricingPlan`、`ModelPricingRule`、`ChannelModelMapping`。
  - 写数据迁移，把旧数据收敛到新结构。
  - 前端不再编辑任意 `Catalog JSON`。
- 风险：生产已有模型目录和价格数据，迁移错误会丢数据。
- 验证：先备份数据，再跑迁移测试；价格计算结果与旧数据抽样一致。

## 收尾验收

- 全量 `dotnet test` 通过。
- 前端 `npm --prefix frontend run build` 通过。
- 清理 README/DEPLOYMENT 中已删除功能的描述。
- 删除不再使用的 DTO、配置项、测试桩和前端死代码。
- 本地起一次完整后端，跑登录、渠道配置、代理请求、日志查看。
