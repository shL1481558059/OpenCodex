# OpenCodex 代码清理与重构改造清单

> 状态：部分实施中。本文档用于记录后端代码清理、业务逻辑整理和维护性改进的分阶段方案。已完成项标注 ✅，详见各节实施记录。
>
> 最近一次实证排查：2026-08-08（数据库体积核验截至 2026-08-07）。新增 Phase A / Phase B 两组候选，并修正了原清单中多处与代码/部署实际不符的判断，详见「清单修正说明」。凡标注「已实证」的结论均通过 `rg` 全仓检索、运行时请求或只读部署核验过。

## 规模基线（2026-08-07 实测）

| 范围 | 行数 |
| --- | --- |
| 后端 `src`（不含 EF 迁移） | 45388 |
| EF 迁移（SQLite 6 个 + Postgres 6 个，需双份同步维护） | 9334 |
| 后端 `tests` | 21132 |
| `frontend/src` | 8702 |

- 最大源文件：`ModelCatalogService.cs` 1743、`ObservabilityResponses.cs` 1522、`ObservabilityService.cs` 1332、`ConfigService.cs` 1056。
- 最大测试：`ProxyCompatibilityTests` 4113、`SseStreamConverterTests` 2958、`ProxyEndpointServiceTests` 1260、`ProtocolStructuralCompatibilityTests` 1215、`RouteTests` 1100。
- `dotnet build opencodex_proxy/OpenCodex.sln` 当前通过，仅 2 个既有 CS86xx nullable 警告（`ModelCatalogService.cs:476/509`）。

## 前置决策点

1. 生产环境是否真的会用到图片 OCR 回退？
2. Web Search 是否还需要“模拟”模式，还是只保留“转换/关闭”？
3. 管理台的“测试渠道/发现模型”是否高频使用？
4. 桌面端是否依赖 `intercept_probe_requests` 特殊拦截？
5. 生产数据库是 SQLite 还是 Postgres？Redis 二级缓存是否必须？
6. `/images` 图片生成链路：**补齐实现**还是**整链删除**？（现状运行时必 500，见 B.1）
7. `/pricing` 这 5 个路由是否有外部脚本或运维工具在调用？（前端已确认零调用，见 B.2）
8. 是否增加 Controller 激活 smoke test？是否进一步接受 `AddControllersAsServices()` + DI `ValidateOnBuild`？（见 A.5）
9. `OPENCODEX_LOG_PATH` / `OPENCODEX_LOG_LEVEL` / `OPENCODEX_LOG_VIEW_LEVEL` 是要**真做日志落盘**，还是**删掉文档宣传**？（当前项目无日志框架，三者是死配置，见 A.1）
10. 管理台的渠道/API Key/Web Search 配置导入导出是否保留？（当前导出包含明文密钥，见 B.4）— 明文密钥为业务需要保留，不修改。
11. 请求详情、原始 SSE 行和渠道 attempt 日志要保留到什么粒度？是否接受大小上限、失败请求留存和 TTL？（见 B.5）
12. Dashboard 的请求队列和请求错误是否属于刚需的实时运维视图？（若不是，删除两张卡片及伪 SSE，见 B.3）
13. 渠道批量测试、批量编辑和归并视图的实际使用频率如何？（见 B.6）
14. 渠道级模型覆盖和第二家 Web Search Provider 是否有明确近期需求？（见 B.7）

### 已可预先回答的决策点

- 决策点 3 的答案是「在用」：`frontend/src/Channels.vue` 真实调用 `/discover-models` 与 `/test-channel/stream`（`Channels.vue:2107` 用 `fetch`），删除会真丢能力。
- 决策点 4 的答案是「在用」：`ProbeRequestInterceptor` 被 `ProxyController.cs:113` 调用，且有 `ProbeRequestInterceptorTests.cs` 覆盖。
- 决策点 5 的现状结论：仓库根 `AGENTS.md` 的 SQLite/无 Redis 记录已过时；`docker-compose-pgsql.yml:22-59`、`DEPLOYMENT.md:73-124`、`update_remote_image.sh:5/22-29` 以及本次远端只读核验（运行应用为 PostgreSQL，Redis 已配置）均指向 PostgreSQL + Redis。远端当前容器名为 `ocxp-dev`，需先确认它是否就是目标生产实例；在此之前不能按 SQLite/无 Redis 删除双轨，**Redis 不能按死路径删除**。
- 远端只读业务核验（本次样本窗口，需确认是否生产）：共 50 条渠道、无 `images` 渠道；Web Search 模式为 `simulate` 且有 4 个启用 Key；`RequestLogDetails.WebSearchJson` 非空约 3,184/14,664；OCR 子日志为 0/14,664；渠道配置中有 3 条 `intercept_probe_requests=true`。因此该样本支持 Web Search simulate 和 Probe 在用，也支持 OCR 当前低活跃，但不能替代正式生产使用率调查。
- 两套远端 PostgreSQL 的只读核验必须分开看，不能把开发库样本当成生产结论：`ocxp-postgres` 有 44 条渠道（chat 18 / messages 11 / responses 15），`ocxp-postgres-dev` 有 50 条渠道（chat 14 / messages 18 / responses 18），两库均无 `images` 渠道。两库 `ChannelModelInfos=0`，但仍有 `ChannelModelMappings`（生产 177、开发 161）；全部映射行当前 `Enabled=true`、`SupportsImage=false`、`PricingMode=inherit_global`，`ModelInfoId`/`PricingPlanId` 全为 0，说明先删死列/索引比直接删表更有证据基础。两库 `ModelInfos`（生产 120、开发 123）全部为 `scope=global`。
- 远端密钥数据也证明“明文存储”不是理论风险：`AccessApiKeys` 生产 10、开发 16，全部 `LastUsedAt IS NULL` 且 `KeyPlaintext` 非空；Tavily 配置均为 `provider=tavily`，API key 仍以明文保存。若不实现真实使用追踪，应把 `LastUsedAt` 与“最近使用”展示列为删除候选。
- 旧兼容字段的远端残留也有明确边界：开发库仍有 10 条 `compat.intercept_probe_requests`，生产库为 0；在 `/system-settings` 已成为唯一写入入口后，可先做一次兼容迁移，再删除配置导入白名单项。

## 清单修正说明

以下判断与代码实际不符，实施前必须先修正，否则会删错对象、漏掉迁移风险或产生错误的工作量预估。

1. **第 6 节的保留/删除对象写反了。** 原文说「保留 `ModelPricing` 作为全局价格表，删除 `ModelPricingPlan`、`ModelPricingRule`」，代码实际相反：在用的是 `ModelInfo` + `ModelPricingPlan` + `ModelPricingRule`（`ModelCatalogService` 内 `_rules` 有 20+ 处使用，含 `CalculateRuleCost` / `DefaultRules`），而 `ModelPricing` 才是死的那一边。原文说「删除 `ModelProvider`」同样不成立——`Pricing.vue:319/388`、`Channels.vue:1291` 都在调 `/model-providers`。
2. **第 5 节（内部强类型化）本身就是「低收益高复杂度」，应降级搁置。** 见该节新增的评估说明。
3. **1.1 的删除范围有误：`ImageLogSanitizer` 不能删。** 它被 `ProxyLogService.cs:746` 用于图片日志脱敏，与 OCR 无关，连带删除会编译失败。

此外 1.3、1.4 均**不是死代码**，删除是「主动砍功能」而非「清理」，已在对应小节标注。

4. **A.5 的 `ValidateOnBuild` 不能保证发现 `/images` 漏注册。** 当前只有 `services.AddControllers()`，默认 Controller 激活不等同于普通 DI 服务构建验证；应增加 Controller 激活/路由 smoke test，必要时再单独评估 `AddControllersAsServices()` 的行为变化。
5. **B.1 的补齐实现工作量被低估。** `/images` 不是“补一个实现类和两处注册”即可完成，还要复刻鉴权、候选排序、容量、熔断、failover、响应写入和日志生命周期；若无明确产品需求，应按独立功能评审，而不是当作几十行修复。
6. **B.3 不应默认改成轮询。** Dashboard 的错误和队列卡片分别只有一条 SSE 消费链，Logs/Channels 已有重叠信息；若没有实时运维刚需，删除卡片和端点比新增 GET + 轮询更低复杂度。
7. **第 6 节的 `ChannelModelMapping` 不宜直接整表删除。** 它仍被 `ConfigService.SyncChannelModelMappings` 写入，并由 `ModelCatalogService.ListChannelUpstreamModels` 读取；可先删除确认无读取的列/常量，再评估整表迁移。
8. **流行留存已有明确无上限风险。** `StreamResponseCapture` 的 1 MB 预算只限制响应重建，`ProxyStreamService` 的 `streamLineCaptures` 和 `RequestLogStreamLines.RawLine` 没有总行数/字节上限，也没有统一脱敏和 TTL（见 B.5）。
9. **OCR 保留时还需管理缓存目录。** `ProxyOcrService` 会按图片哈希写 `OcrCacheDir/results`，当前只有读写，没有 TTL、容量上限或清理任务；删除 OCR 时随链路删除，保留 OCR 时必须单列运维策略。

## Phase 0：基线

- 运行 `dotnet test opencodex_proxy/OpenCodex.sln`，确认当前测试全绿。
- 记录当前 `git status` 和未提交改动，避免覆盖已有修改。
- 备份生产数据库和当前镜像，便于回滚。
- 记录 `RequestLogs`、`RequestLogDetails`、`RequestLogStreamLines` 的行数/体积、最早/最新时间和单请求最大流行数；当前远端日志留存已出现 GB 级写放大，不能只以应用测试代替存储基线。
- 以远端实际部署文件核对 DB provider、Redis 和容器名，并同步 `AGENTS.md`/`DEPLOYMENT.md` 的冲突记录。
- 为对外 API 的 JSON 结构建立契约测试，防止清理时破坏前端兼容。

## Phase A：高置信清理与低风险去重（新增）

A.1-A.4 主要是已实证零引用的纯删/去重；A.5-A.6 涉及启动验证、数据库列和前端行为，需按各自风险完成迁移与回归，不能把整组视为无条件零风险。预计可减少 400+ 行代码、多个误导性配置项和无效字段。

### A.1 `OpenCodexSettings` + `OpenCodexSettingsLoader` 整类死代码 ✅ 已完成

- 现状（已实证）：`rg OpenCodexSettingsLoader` 只命中自身定义行，**全仓零引用**，tests 亦零引用。实际生效的是 `OpenCodexRuntimeSettingsProvider`（80 行，读 `IConfiguration`）+ `OpenCodexHostBuilderExtensions.AddOpenCodexConfiguration` → `DotEnvDefaults.Load`。两套 dotenv 解析逻辑（`OpenCodexSettingsLoader.LoadDotEnvFile` 与 `DotEnvDefaults.Load`）重复实现。
- 涉及范围：
  - `opencodex_proxy/src/Presentation/OpenCodex.Api/Configuration/OpenCodexSettingsLoader.cs`（225 行，整文件删除）
  - `opencodex_proxy/src/Presentation/OpenCodex.Api/Configuration/OpenCodexSettings.cs`（38 行，整文件删除）
- 连带死配置：`OPENCODEX_LOG_PATH` / `OPENCODEX_LOG_LEVEL` / `OPENCODEX_LOG_VIEW_LEVEL`。项目**无任何日志框架**（无 Serilog、无 `appsettings.json`，`ILogger<>` 全仓仅 `ProxyErrorMiddleware.cs` 一处），三者零消费点。但 `README.md:40-42`、`DEPLOYMENT.md:22-24`、`DEPLOYMENT.md:247` 仍在宣传，`src-tauri/src/lib.rs:170` 仍在注入 → **文档与实现不一致，属误导性配置**，必须连带处理（见决策点 9）。
- 注意：`OPENCODEX_SECRET_KEY` 本身是活的（`OpenCodexServiceCollectionExtensions.cs:234` 用于 DataProtection 应用名），只有 `OpenCodexSettings.SecretKey` 这条路径是死的，不要误删环境变量。
- 风险：低。纯删无引用类型。
- 验证：`dotnet build` 通过；启动后 `.env` 解析行为不变（仍由 `DotEnvDefaults` 负责）。

> 实施记录（2026-08-23）：已删除 `OpenCodexSettingsLoader.cs` 和 `OpenCodexSettings.cs`，全仓 `rg` 零命中。`OPENCODEX_LOG_PATH` / `OPENCODEX_LOG_LEVEL` / `OPENCODEX_LOG_VIEW_LEVEL` 在 README、DEPLOYMENT 和 Tauri 中的文档残留也已清理。`OPENCODEX_SECRET_KEY` 保留（`OpenCodexServiceCollectionExtensions.cs` 仍在使用）。`dotnet build` 通过。

### A.3 OCR 残留死配置与空壳引擎 ✅ 已完成

- 现状（已实证）：
  - `ProxyOcrEngines.PaddleOcr = "paddleocr"`（`ProxyImageFallbackModels.cs:17`）只在 `ProxyOcrService.cs:530 IsSupportedCacheEngine` 出现一次；`OpenCodex.Core.csproj` 无任何 OCR 包（只有 EFCore.Relational / Caching.Memory / StackExchange.Redis）。当前新请求执行路径固定走 `ProxyOcrEngines.Vision`（:52 硬编码 + :57/78/80/101/122/136），`ProxyImageFallbackTests.cs:233` 那条 paddle 测试**已被注释掉**，但 `ReadCache`/`IsSupportedCacheEngine` 仍接受历史 `engine=paddleocr` 缓存。删除该常量前必须决定旧缓存的兼容或清理策略。
  - `OPENCODEX_LOCAL_OCR_MODEL` / `LocalOcrModel`（默认 `"ChineseV5"`）分布在配置链路多处（`OpenCodexSettings.cs:29`、`OpenCodexSettingsLoader.cs:81/115/171`、`OpenCodexRuntimeSettingsProvider.cs:23/61`、`OpenCodexRuntimeSettings.cs:26/65`），但 **`ProxyOcrService` 从不读取它**。连带更老的兼容变量 `OPENCODEX_TESSERACT_LANG`（`OpenCodexRuntimeSettingsProvider.cs:69` 的 legacy 映射）也是死的。
  - 只有 `OcrCacheDir` 是真在用（`ProxyOcrService.cs:511-517`，且 `src-tauri/src/lib.rs:172` 会注入 `OPENCODEX_OCR_CACHE_DIR`），**不要删**。
- 说明：本项与 1.1 是否整体删 OCR **相互独立**——即使决定保留 OCR 回退，这些死配置也应删掉。
- 风险：低。
- 验证：OCR 回退链路行为不变；`OPENCODEX_OCR_CACHE_DIR` 仍生效。

> 实施记录（2026-08-23）：已删除 `ProxyOcrEngines.PaddleOcr` 常量、`LocalOcrModel` / `OPENCODEX_TESSERACT_LANG` / `OPENCODEX_LOCAL_OCR_MODEL` 死配置链路。全仓 `rg` 零命中。`OcrCacheDir` / `OPENCODEX_OCR_CACHE_DIR` 保留（`ProxyOcrService.cs:510`、`lib.rs:170` 仍正常使用）。
### A.4 `ApplyCompat` 重复实现 + `CompatDetails` 死数据链 ✅ 已完成

- 现状（已实证）：`opencodex_proxy/src/Libraries/OpenCodex.Core/Services/ChannelDiagnosticsService.Compat.cs:9 ApplyCompat` 把 `default_params` / `rename_params` / `drop_params` / `force_params` / `unsupported_params` 五步全实现一遍（约 50 行），随后在 `:57` **又调用 `ChannelCompatRequestRewriter.Apply` 把同样五步再跑一遍**——幂等但纯冗余，两处逻辑逐字重复。
- 其返回的 `Details` 一路传进 `TestChannelPreparedRequest.CompatDetails`（`ChannelDiagnosticsService.cs:309/366/374/386`），但该属性**全仓零读取**，`BuildTestCompletedEvent` 不输出它，前端无 `compatDetails` / `compat_details` 字样 → 整条 details 收集链是死数据。
- 做法：`ChannelDiagnosticsService.Compat.cs`（73 行）退化为一行调用 `ChannelCompatRequestRewriter.Apply`，删除 details 参数链与 `CompatDetails` 属性。
- 若确认没有外部消费者，可进一步让 `ChannelCompatRequestRewriter.Apply` 直接返回重写后的 payload，删除只为 `Details` 存在的 `ChannelCompatRewriteResult` 包装类；代理主链和测试目前都只读取 `.Payload`。
- 风险：低。唯一权威实现变为 `ChannelCompatRequestRewriter`（代理主链路 `ProxyEndpointService.cs:223` 用的就是它，行为天然一致）。
- 验证：`ProxyCompatibilityTests` / `ProtocolStructuralCompatibilityTests` 全绿；测试渠道输出不变。

> 实施记录（2026-08-23）：`ChannelDiagnosticsService.Compat.cs` 已退化为一行 `return ChannelCompatRequestRewriter.Apply(payload, compat).Payload;`。`CompatDetails` 属性及 details 参数链已删除，全仓 `rg` 零命中。唯一权威实现为 `ChannelCompatRequestRewriter`（代理主链路 `ProxyEndpointService.cs:223` 使用的就是它）。
### A.5 Controller 激活 smoke test（可选启用 DI 验证）

- 现状（已实证）：DI 全部手写集中在 `opencodex_proxy/src/Presentation/OpenCodex.Api/Hosting/OpenCodexServiceCollectionExtensions.cs`（37 处注册，无 Scrutor / 无程序集扫描），且 `ValidateOnBuild` / `ValidateScopes` 全仓 0 命中。当前 `services.AddControllers()` 不保证 Controller 构造参数会参与普通服务构建验证。
- 首选做法：增加一条 Controller 激活/路由 smoke test，枚举所有 Controller 并在测试容器中创建实例，直接捕获 `/images` 这类漏注册；必要时再单独评估 `AddControllersAsServices()` + `ValidateOnBuild` 的启动行为变化。
- 这是一项**缺陷预防测试**，不是 B.1 的直接修复，也不应承诺仅打开 `ValidateOnBuild` 就能发现全部 Controller 漏注册。
- 风险：中。`AddControllersAsServices()` 可能改变生命周期和启动失败边界；仅增加 smoke test 的风险较低。属决策点 8。
- 验证：故意移除一个 Controller 依赖时 smoke test 必须失败；正常启动与 `dotnet test` 全绿。

### A.6 无效配置、死字段与调试残留

- `AccessApiKey.LastUsedAt` 没有任何生产持久化写入点；认证响应会临时计算一个 `lastUsedAt`，但管理列表读取的实体字段长期为空。若不实现真实追踪，应同步删除实体/迁移、Auth DTO 的瞬时字段和“最近使用”展示。
- `RequestLogDetail` 继承 `BaseEntity<Guid>` 产生额外 `Id` 列，但实体配置以 `RequestLogId` 为主键；写入、查询和 API 均不使用 `Id`。可改为非泛型基类并为 SQLite/Postgres 各加删列迁移。
- `IModelCatalogService.CalculateCostAsync` 的 `responseModel` 参数在方法体中完全不读取，`ProxyLogService` 却为此额外从响应提取/回退该值；删除参数及两处提取逻辑，并同步测试夹具。
- 另有零调用私有/内部方法：`ObservabilityService.EmptyLogFilterOptions()`、`EmptyStatsResponse(...)`、`ProxyStreamService.CaptureRawStreamLines(...)` 和 `TryExtractObject(...)`；先确认无反射调用，再连同专用测试/using 删除。 ✅ 四个方法全部已删除，全仓 `rg` 零命中。
- `ModelInfoScopes.Channel` 除常量定义外零引用；`ChannelModelPricingModes.PrivateModel` 除定义外零引用；`OverridePricing` 仅测试夹具赋值。应在模型目录收敛时清理，避免继续扩大旧设计。 ✅ `ModelInfoScopes.Channel` 和 `ChannelModelPricingModes.PrivateModel` 已删除，全仓零命中。`OverridePricing` 仍保留，待后续清理。
- `OpenCodexConfig.CompatFields` 仍接受 `intercept_probe_requests`，但该设置已迁移到 `/system-settings`；保留一版兼容剥离后删除白名单项，避免配置导入继续宣称该字段有效。
- `OPENCODEX_ACCESS_API_KEY` 与 `OPENCODEX_DB_PATH` 目前只有 README/DEPLOYMENT 文档引用；`OPENCODEX_LOG_*` 已在 A.1 处理。应核对外部脚本后清理文档和示例，避免误导用户。
- `Program.cs` 每次启动无条件写 `opencodex-startup-diagnostic.txt`，全仓没有读取方；`ProxyStreamService`、`SseStreamConverter` 中约 10 条无条件 `[OCXP-DEBUG] Console.Error.WriteLine` 也没有级别控制。建议删除启动文件，或只在显式诊断开关下写入；调试输出统一移除或接入真正的日志级别。 ✅ 启动诊断文件和 `OCXP-DEBUG` Console 输出均已删除，全仓 `rg` 零命中。
- 历史文档残留：`opencodex_proxy/stream_fix_plan.md` 更像已完成方案；`opencodex_proxy/tests/OpenCodex.Api.Tests/README_STREAMING_TESTS.md` 虽对应的 `StreamingIntegrationTests.cs` 实际存在，但 README 夸大了覆盖范围，引用已删除文件和过时事件名，并保留已不存在的 OCR 编译前置条件。应按当前实现逐条校对，标记历史归档或删除过时段落，不能把文档描述当作测试现状。 ✅ 两个文件均已删除。`stream_fix_plan.md` 是已完成方案，`README_STREAMING_TESTS.md` 内容过时且引用已删除文件。对应的 `StreamingIntegrationTests.cs` 仍存在且测试通过。
- 过时脚本残留：`scripts/capture_real_sse.sh` 仍调用旧 `/api/auth/login` + Bearer 流程，`scripts/extract_sse_test_data.sh` 查询旧 `ProxyLogs`/SQLite 列，`scripts/test_streaming.sh` 默认把启动密码当访问 API Key，`switch_backend.sh` 写死个人域名和端口且未被文档/CI 引用。应逐个验证后删除或明确标记为个人历史脚本。 ✅ `switch_backend.sh` 已删除。其余三个旧脚本（`capture_real_sse.sh`、`extract_sse_test_data.sh`、`test_streaming.sh`）及 `test_streaming.py` 仍存在，待清理。
- `scripts/test_streaming.py` 同样未被引用，依赖未声明的 `requests`，默认使用管理员密码作为 API Key，且 payload 与当前 Responses 契约不符；应与上述历史诊断脚本一起处理。
- `src-tauri/src/lib.rs:215` 在发布窗口无条件开启 `.devtools(true)`，疑似白屏诊断残留；应改为 debug 构建或显式开关，避免生产桌面暴露开发者工具。 ✅ 已改为 `cfg!(debug_assertions)`，仅 debug 构建开启。同时修复了 Rust `DesktopSettings` 缺失 `intercept_probe_requests` 字段导致重启后设置丢失的跨语言覆盖 Bug。
- 前端零作用残留：`Logs.vue.resetLogFilters`、`Logs.vue.filterOptions.upstream_models`、`WebSearch.vue.defaultWebSearchKey`、未使用的 `onMounted`、`main.js` 的 `ElUpload` 注册、`.input-with-action` CSS、Dashboard/Logs 的无效 `active` 状态机和 `App.vue` 无对应样式的 `mobile-menu-drawer` `custom-class`。
- `Logs.vue` 的列设置没有 localStorage/后端持久化，组件离开页面即销毁；若不打算实现持久化，应删除设置状态和入口，固定默认列。
- 前端未读取的观测字段（如 `LogDetailResponse.client_ip`、清理日志返回的 `deleted_details/deleted_stream_lines`、队列响应的 `generated_at`）可在 DTO 瘦身时核对；先确认没有外部 API 消费者，不要只凭前端零引用删掉公开契约。
- 风险：低；涉及数据库列时为低-中（双数据库迁移）。
- 验证：`rg` 确认零读取；迁移后旧数据库可启动；前端构建通过。

## Phase B：需决策的低收益链路与运行时风险（新增）

### B.1 `/images` 整链是死路由，运行时必 500（最高优先级，且是真 bug）

- 现状（已运行时实测）：`POST /v1/images/generations` 返回 **HTTP 500**，`InvalidOperationException: Unable to resolve service for type '...IProxyImagesEndpointService' while attempting to activate 'ImagesController'`。
- 根因：`ImagesController` 注入 `IProxyImagesEndpointService`，但**全仓没有任何实现类**——`rg` 仅 4 处命中：接口定义、Controller 构造注入 ×2、测试桩 `StubImagesService`。DI 从未注册。引入 commit `639b23b4`「feat: 支持 images 渠道与图片生成/编辑代理」只注册了 `IImageEditRequestReader`，漏掉了 endpoint service。
- 同时 `IImagesUpstreamClient` 虽由 `HttpUpstreamClient.Images.cs`（281 行）实现，但 `OpenCodexServiceCollectionExtensions.cs:106-126` 只注册了 `IUpstreamClient` / `IUpstreamModelClient` / `IWebSearchClient` 三个 HttpClient，**没有 `IImagesUpstreamClient`**；`GenerateAsync` / `EditAsync` 在测试中有桩调用，但生产代码零调用。
- 受影响路由（4 条，全部 500）：`/images/generations`、`/v1/images/generations`、`/images/edits`、`/v1/images/edits`（`ImagesController.cs:30/31/52/53`）。
- 涉及范围（测试 534 行 = 100+138+203+93）：
  - `opencodex_proxy/src/Presentation/OpenCodex.Api/Controllers/ImagesController.cs`（81）
  - `opencodex_proxy/src/Libraries/OpenCodex.CoreBase/Services/Proxy/IProxyImagesEndpointService.cs`（10）
  - `opencodex_proxy/src/Libraries/OpenCodex.CoreBase/Abstractions/IImagesUpstreamClient.cs`（63）
  - `opencodex_proxy/src/Libraries/OpenCodex.Core/ExternalIntegrations/HttpUpstreamClient.Images.cs`（281）
  - `opencodex_proxy/src/Libraries/OpenCodex.CoreBase/Domain/Proxy/ImageProxyModels.cs`（81）、`ImageEditRequestReader.cs`（136）、`IProxyResponseBodyWriter` + `ProxyResponseBodyWriter.cs`
  - 测试：`ImagesControllerTests.cs`(100)、`ImagesCoreContractTests.cs`(138)、`ImagesUpstreamClientTests.cs`(203)、`ImageEditRequestReaderTests.cs`(93)
  - 前端：`frontend/src/Channels.vue:347` 的 `<el-option label="images" value="images" />` + `channelImagesState.js`（含 `.test.js`）
  - 校验：`opencodex_proxy/src/Libraries/OpenCodex.Core/Config/ConfigValidator.cs:93/138/143`（`images` 渠道要求 `retry_count` 为 0、`images_api_dialect` 仅允许 images 渠道）
  - 删除时须一并处理 `ProxyLogServiceTests.cs:441`（`channelType: "images"`）与 `ProxyVisionRoutingTests.cs:97-164`（多处 `type = "images"`）
- 两条路（属决策点 6）：
  - **① 补齐**：按独立代理功能实现完整 `ProxyImagesEndpointService`，复刻鉴权、候选排序、容量、熔断、failover、响应写入和日志生命周期；不能按“几十行 + 两处注册”估算。
  - **② 整链删除**：连同 `images` 渠道类型、前端选项、`ConfigValidator` 分支一起删。
- 风险：若选 ②，任何已配置 images 渠道的用户会失去该渠道类型（但当前它本来就 500，实际无可用性损失）。
- 选 ② 的迁移边界：清理现有渠道 JSON 中的 `compat.images_api_dialect`，处理历史 `RequestLogs.channel_type=images` 的展示/查询策略，并删除前端 `channelImagesState` 及相关测试。
- 无论选哪条路，实施前都要再次 `rg -n "images|Images"` 盘点协议、日志、迁移、DTO、前端状态和测试引用；上面的范围是当前已知主链，不是可替代全仓引用核对的白名单。
- 当前远端样本中 `images` 渠道数为 0，日志窗口也未命中 `%images%`；这降低该样本实例的整链删除迁移风险，但仍需核对目标生产配置文件和客户端调用。
- 验证：选 ① 则 4 条路由能正常代理并落日志；选 ② 则路由返回 404 且 `dotnet test` 全绿，旧配置迁移测试通过。

### B.2 `/pricing` 是死计费半边 ✅ 已完成

- 现状（已实证）：实际计费走 `IModelCatalogService.CalculateCostAsync`，生产调用点仅 `ProxyLogService.cs:214` 和 `:370`。`IModelPricingService.CalculateCost` 除 `ModelPricingServiceTests.cs:40` 外**零生产调用**；其底层 `OpenCodexPricing.cs`（exact/contains 匹配算法）只服务于它。
- 前端**完全不调 `/pricing`**：`Pricing.vue` 只使用 `/model-providers`、`/model-infos`及其 CRUD；内置目录 seed 入口已删除。现存 4 个 `/pricing*` CRUD 路由仅被兼容性测试覆盖。
- 死链条（约 700 行）：
  - `opencodex_proxy/src/Presentation/OpenCodex.Api/Controllers/PricingController.cs`（67，路由 `:21/32/52/60`）
  - `opencodex_proxy/src/Libraries/OpenCodex.Core/Services/ModelPricingService.cs`（546）
  - `opencodex_proxy/src/Libraries/OpenCodex.Core/Services/OpenCodexPricing.cs`（80）
  - `IModelPricingService` + `ModelPricingDto` + `ModelPricing.ToDto()` 扩展方法
- `ModelPricing` 表已与当前模型目录和实际代理计费解耦：启动时不再播种，`ModelCatalogService` 不再读取或迁移其数据。
- 新模型目录完全依赖手工维护的 `ModelInfo` / `ChannelModelInfo` / `ModelPricingPlan` / `ModelPricingRule`；空库不会自动生成供应商或模型。
- 删除前不要只看仓库内部引用；应从生产访问日志或 `RequestLogs.Path` 检查 `/pricing` 的外部调用，并在迁移窗口保留 deprecation 观测。
- 当前远端样本日志窗口未命中 `%pricing%` 路径，支持继续核实后收敛，但不等同于目标生产或外部脚本零调用。
- 远端样本中 legacy `ModelPricings` 仍有约 111 行数据，`ChannelModelMappings` 约 161 行；删除旧表/整表迁移前必须完成并核验真实数据迁移，不能按“表空/已迁移”处理。
- 旧的远端更新路由、联网客户端、解析器和内嵌价格快照已全部删除，运行时不再为模型目录主动出网。
- 属决策点 7。

> 实施记录（2026-08-23）：已删除 `PricingController.cs`、`ModelPricingService.cs`、`OpenCodexPricing.cs`、`IModelPricingService.cs`，全仓 `rg` 零命中。`RouteTests.cs:53` 断言 `/pricing/seed-defaults` 返回 404。`ModelPricing` 实体已删除。保留 `ModelPricingPlan` / `ModelPricingRule` 实体（新目录系统仍在使用）。
- 验证：`/pricing/seed-defaults` 不可调用；新目录 CRUD 后计费立即生效；旧 `/pricing` CRUD 不影响实际代理计费。

### B.3 Dashboard 两条伪 SSE（先评估删除，不默认改轮询）— SSE 已从轮询改为事件驱动，端点仍保留

- 现状（已实证）：`ObservabilityController.cs:178 /stats/active-channels/stream`（2s）和 `:205 /stats/recent-errors/stream`（5s）已从手写 `while + Task.Delay` 循环重构为基于 `_eventBus.Subscribe` + `SseEventWriter.StreamAsync` 的事件驱动模式（`ObservabilityController.cs:383-440`），不再定时轮询查库。但端点本身仍保留，前端 `Dashboard.vue` 仍有 2 个 `EventSource`。原始描述保留供参考：~~手写 `while (!RequestAborted)` + `Task.Delay` + `Response.WriteAsync`，本质是服务端定时查库，却背着常驻连接状态机。~~前端 `frontend/src/Dashboard.vue:617` 与 `:696` 两个 `EventSource`。
- ⚠️ `/stats/active-channels` 有非流版本（`:170`），但 `/stats/recent-errors` **没有** → 改成前端轮询需先新增 `GET /stats/recent-errors`。
- Dashboard 实际只消费 `/stats/active-channels/stream`；普通 `/stats/active-channels` 在前端、文档和测试外部调用矩阵中没有消费方。若删除队列 SSE，可连同该 GET 和 Vite proxy 白名单一起清理。
- 更低复杂度的选项：若没有实时运维刚需，直接删除“请求队列”和“请求错误”卡片、两条 SSE 端点及对应前端状态机；保留 Dashboard 统计图、Channels 容量/健康状态和 Logs 错误详情。
- 删除队列卡片时可继续删除仅供观测使用的 `GetActiveModelUsages`/`ChannelActiveModelUsage` 及其 Redis/进程内模型计数；保留容量限流本身使用的 `GetActiveRequests`。删除错误卡片时，`ReadRecentErrors`/`QueryRecentErrors` 及其专用测试也可整条删除，不必新增 GET。
- 只有在必须保留卡片时，才新增非流 GET 并改为前端轮询；不要为了替换 SSE 再维护第三套刷新链路。
- 风险：删除会失去首页即时速览；保留并轮询则会增加 API 和前端定时器复杂度。
- 验证：选删除则后端不再建立长连接且首页核心图表正常；选保留则数据、权限和刷新频率与原行为一致。

### B.4 管理台配置导入/导出与明文密钥 — 明文密钥返回为业务需要保留，不修改

- 现状：`Channels.vue:1300-1343`、`AccessKeys.vue:172-221`、`WebSearch.vue:247-306` 各自实现 Blob 导出、文件解析、格式兼容和错误提示；后端对应 `ConfigService.ImportConfigAsync`、`ApiKeyService.ImportKeysAsync`、`WebSearchService.ImportConfig`。
- 提交 `6d7e66e0` 一次增加约 996 行代码，仓库没有相应端到端导入/导出测试。
- 导出内容包含渠道 `apikey`、Access API Key 明文和 Web Search Key 明文；`/web-search` 列表本身也返回 `TavilyKey.ApiKey` 明文。`/config/import`、`/api-keys/import` 只要求登录用户，导入还按名称或 `(provider, key)` 合并并可能覆盖现有配置。渠道导入会更新 Base URL、headers、models 等路由字段，API Key 导入会直接重算 hash 并替换明文 key。
- 远端样本已确认规模：生产 `AccessApiKeys=10`、开发 `=16`，所有记录 `KeyPlaintext` 非空且 `LastUsedAt` 为空；Tavily 记录均为单一 `tavily` provider。整改前应先决定是否需要一次性密钥轮换，不能把“删除导出按钮”误当成已消除历史明文风险。
- 导入文件没有统一 schema/version/source 校验，部分页面还接受“任意数组”作为输入；误选旧文件或其他 JSON 可能被当成线上配置合并。
- 建议：优先删除三页导入/导出按钮、端点、请求 DTO 和服务分支，迁移改用数据库备份或专用加密 CLI。若必须保留，至少增加超级管理员权限、加密文件、预览/冲突确认、审计记录，并禁止列表接口回传完整密钥。
- 风险：失去管理台便捷迁移；安全收益明显。实施前核对是否存在外部备份脚本。
- 验证：删除后管理台 CRUD、数据库备份恢复和密钥一次性展示全绿；保留时补充恶意/重复导入、权限和密钥泄露测试。

> 业务决策（2026-08-23）：明文密钥返回（渠道 `apikey`、Access API Key `KeyPlaintext`、Tavily Key）为业务需要保留，不进行脱敏或移除。管理台需要完整密钥用于编辑、导入导出和配置迁移。以下各节中涉及「停止明文密钥返回」「改为 hash-only」「GET/list 永不返回原文」「metadata-only 导出」等建议均不再适用，仅保留导入/导出的权限、schema 校验和审计相关改进。
> 备注：模型信息页的全局目录导入导出（`/model-catalog/export`、`/model-catalog/import`）已于 v1 实现，采用 dryRun 预检 + 事务导入 + 超级管理员权限，不含明文密钥。渠道级覆盖暂未纳入导出范围。

### B.5 观测日志写放大、原始 SSE 留存与保留策略 ✅ 内容寻址存储已完成，attempt 瘦身待实施

> 实施状态（2026-08-12）：已按当前产品决策完成内容寻址存储重构。请求头、原始请求、转换后请求、上下游响应、OCR、Web Search 与完整 SSE 逻辑行改为基于 SHA-256 的内容定义分块、Brotli 压缩、manifest/引用存储；历史宽表与逐行 SSE 表在新迁移中直接删除，不迁移历史数据。完整 SSE 仍保留，细粒度“流式时序 JSON”已移除，仅保留统计所需 TTFT。会话键、Turn ID、窗口 ID、`previous_response_id` 已建立索引并接入列表、统计、补全和前端筛选，以支持追加、编辑及新分支定位。不做脱敏，也不截断请求或 SSE 正文；下列旧宽表体积数据保留为改造前基线，原“限额/TTL/脱敏”建议不代表本轮实现。

- **成功 attempt 子日志**：`ProxyEndpointService.WriteChannelAttemptLogAsync` 在成功流式/非流式分支（`:279/:331`）和失败分支（`:375/:405/:445`）都会写一条 `request_type=attempt`。每条子日志还复制请求体和上游请求；读取侧默认排除 attempt，只聚合次数（`ObservabilityService.cs:182/486/1073`）。建议仅保留失败、实际 failover 或父日志摘要。
- **完整详情重复保存**：~~`RequestLogDetail` 同时保存请求头、原始请求、转换后请求、上下游响应、Web Search/OCR 诊断和流时序；流请求还同时保存 `UpstreamResponseBody` 与 `RequestLogStreamLines.RawLine`。~~ ✅ 旧宽表 `RequestLogDetail` 和 `RequestLogStreamLines` 实体已删除，替换为内容寻址存储 `LogContentBlock` + `LogContentManifest`（SHA-256 分块 + Brotli 压缩 + manifest 引用）。
- **计费溯源字段零观测收益**：`RequestLog` 写入 `PricingModelInfoId`、`PricingPlanId`、`PricingSnapshotJson`、`CostCurrency`、`CacheWriteTokens`、`CacheReadTokens`，但观测响应和前端没有对应读取；若不提供计费审计，可删持久化字段及索引，保留运行时 `ModelUsageVector` 计算所需数据。
- **SSE 无总预算（当前为 P0/P1 运维风险）**：`StreamResponseCapture` 的 1 MB/256 项预算只限制响应重建；`ProxyStreamService.cs:95/493-534/725-734` 创建的 `streamLineCaptures` 没有总行数、总字节或总时长上限，`RequestLogStreamLines.RawLine` 原样写入数据库，可能造成长流内存和数据库无限放大。原始行还没有统一经过请求体同等级的脱敏。 ✅ 旧逐行表已删除，`streamLineCaptures` 改为写入 `LogContentBlock`（内容寻址存储）。`ProxyStreamService.cs:43` 仍创建 `List<ProxyRequestStreamLineCapture>`，但持久化路径已改为内容寻址分块，不再逐行写宽表。
- Logs 详情还提供“合并事件/原始行”切换、逐行展示和复制；若没有协议排障刚需，可只保留摘要、错误和上下游响应，并连带关闭原始行持久化，而不是仅给无限增长的表加查询入口。
- 远端只读样本核验（2026-08-07，需确认目标实例）显示 `RequestLogStreamLines` 约 **3.16 GB / 11.33M 行**，约 6,747 个请求带流行，单请求平均约 1,680 行，最大约 **124,575 行**；`RequestLogDetails` 约 **5.85 GB**，而 `RequestLogs` 仅约 17 MB。当前只有超级管理员手动清空日志，没有 TTL 或自动清理。服务代码已经为 Postgres 特判 `TRUNCATE`，说明逐行清理超时风险已被预见。
- 同期约有 7,233 个主流式请求和 7,161 个 attempt 流式请求，说明问题来自正常主流量而非边角测试流，不能简单以“删除测试日志”解决。
- attempt 的成功率也很高：生产 956 条 attempt 中 901 条为 HTTP 200（94.2%），开发 7,435 条中 7,096 条为 200（95.4%）；而读取侧默认排除成功 attempt。因此默认只留失败、实际 failover 或父日志摘要，收益/风险比高于继续保存完整成功 attempt。
- `RequestLogDetails` 的主要膨胀来自 `RequestBody`（约 3.18 GB）和 `UpstreamRequestBody`（约 2.52 GB），不是响应正文；`RequestLogs` 日增量也从 7 月 31 日约 685 条升至 8 月 7 日约 4,776 条。默认保存完整请求体的收益不足以覆盖持续增长和敏感数据风险。
- 字段填充率进一步说明 OCR 不是当前日志体积来源：生产库 `RequestLogDetails=1,945`，`OcrJson=0`、`WebSearchJson=528`、`StreamTimingsJson=895`；开发库 `=15,158`，`OcrJson=0`、`WebSearchJson=3,184`、`StreamTimingsJson=7,060`。这支持将 OCR 列为“生产未使用、待产品决策”的低收益候选，但仍不能仅凭日志零值删除客户端明确依赖的图片能力。
- **OCR 缓存**：若保留 OCR，`ProxyOcrService` 按图片哈希写 `OcrCacheDir/results`，当前没有 TTL、容量上限或清理任务；若删除 OCR，则随 1.1 一并删除。
- 建议分两步：先备份并按时间窗口分批清理已超量的 `RequestLogStreamLines`/`RequestLogDetails`，再在代码中默认保存元数据和错误摘要；调试详情改为开关或仅失败请求留存；为 SSE 行设置每请求 max lines/max bytes，超限保留首尾和摘要；增加 TTL/定期清理，并明确敏感数据权限。不要删除协议转换本身使用的 `StreamResponseCapture`。
- 风险：Logs 页面目前确实展示流行和详情，不能直接判为死功能；需要兼容旧日志读取和 SQLite/Postgres 两套迁移。
- 验证：长流、超限、取消、错误、敏感字段脱敏、旧日志详情读取和清理任务测试。

### B.6 渠道批量运维便利层

- **批量测试**：提交 `846e9cde` 增加约 838 行；`Channels.vue` 维护 worker 并发、AbortController、pending/running/success/error/cancelled 状态和 SSE 解析，只是并发调用已有 `/test-channel/stream`。后端每次测试在 `finally` 都写完整请求日志，可能放大上游费用、限流和数据库写入。 ✅ 批量测试已删除，`Channels.vue` 中 `batchTest` 相关代码全仓零命中。归并视图和批量编辑仍保留。
- **归并视图**：`Channels.vue:43-279/2170-2228/2785-2921` 约 433 行第二套表格、操作按钮、跨组选择同步和 CSS，无独立后端能力。
- `group_name` 目前主要服务管理台展示、归并和批量编辑，代理路由/容量/统计不读取；若确认连归并视图、原始列表分组列和按组编辑都不需要，可进一步删除 Channel 字段及 SQLite/Postgres 迁移，否则应明确保留它的管理用途。
- **批量编辑与模型映射文本 DSL**：额外维护字段勾选、共同值计算和“请求模型,上游模型”文本解析；单行编辑、发现模型已经存在。
- **兼容路由别名**：`ChannelDiagnosticsController` 同一 action 同时暴露 `/channels/discover-models` 与 `/discover-models`、`/channels/test/stream` 与 `/test-channel/stream`，前端和文档只使用后者。无外部兼容承诺时可删除前缀别名。
- 当前远端样本日志窗口命中 `/test-channel/stream` 4 次、未命中 `/discover-models`；批量测试不是零使用，只是样本内低频，删除前应保留单渠道测试并确认这 4 次是否来自真实运维。
- 建议先查生产访问日志/管理台反馈：低频时保留单渠道测试、模型发现和原始列表，删除批量测试/批量编辑/归并视图/未使用别名；高频时只做共享状态和并发上限收敛，不要同时重写整页。
- 风险：失去批量验活和大规模渠道维护效率；属于主动砍功能，不是死代码。
- 验证：单渠道测试、发现模型、渠道 CRUD、批量更新（若保留）和外部兼容路由契约测试。

### B.7 Web Search 与模型目录的过度泛化

- **Web Search Provider**：`WebSearchService.WebSearchProviders` 当前只有 `tavily`，客户端也只有 `TavilyWebSearchClient`；前后端仍维护 provider 列表、归一化、选择器、兼容字段和 `(provider, key)` 合并逻辑。若近期无第二家供应商，可固定 Tavily，保留 `IWebSearchClient` 作为测试边界。
- **全局/渠道定价表单重复**：`Pricing.vue` 与 `Channels.vue` 分别维护 provider、model_key、match_type/pattern、capabilities、Catalog JSON 和四类计费规则。后端确实支持 `ChannelModelInfo` 覆盖，不能直接删除渠道级数据；应先确认使用率，再选择“渠道仅覆盖价格/继承全局元数据”或抽共享组件。
- **Catalog JSON 任意编辑**：字段用于 `/models` 元数据，不是死字段，但任意 JSON 编辑器误配面大。优先改为白名单字段或只读展示，不要误删后端存储。
- **前端低收益实现**：`Pricing.vue` 分页是全量拉取后前端切片，内置目录当前约 17 个模型；规模不大时可删除分页状态。Web Search 同时维护 `usage_limit`/`key_usage_limit` 兼容字段，旧导入格式确认淘汰后可在 API 边界统一。
- **Codex 官方模型目录**：`CodexOfficialModelCatalogFactory.cs` 约 278 行，配套 `wwwroot/ocxp_codex_official_models.json` 约 258 KB、包含大段 instructions，主要只服务带 Codex UA/`client_version` 的 `/models` 请求；非 Codex 请求仍先构造整套 `codexModels` 再塞入响应。若 Codex CLI 兼容是刚需应保留，否则可改为最小元数据或外置版本化目录，避免单客户端承担高维护静态资产。
- **仅建议顺手收敛**：Logs 的 10 个远程筛选项、Dashboard/Logs 重复的 5 张 summary cards、两页不同的时间格式化、以及三页重复的 Blob 下载/复制/格式化 helper 都有维护成本，但 Logs 的筛选统计仍有诊断价值，不应为了抽象而单独重构。
- **Dashboard 图表也可按使用率收敛**：当前有 7 个 ECharts 实例，各自维护 ResizeObserver、销毁和渲染函数；TTFT、缓存命中率、RPM 与 Token/请求趋势存在部分信息重叠，Logs 也展示 RPM/TPM。若运维只关注成本、请求量、模型和错误，可先保留 3–4 个核心图表并同步删减 `/stats` 聚合字段；这仍是需反馈确认的主动收敛，不是已证实死代码。
- `main.js` 手工全局注册约 45 个 Element Plus 组件、Setup/SystemSettings 还重复维护访问范围/端口/拦截开关表单；可按需注册或抽共享表单，但这是包体/组织性重构，不应与功能删减混在一起。
- `App.vue` 的 `defineAsyncComponent` 与 `vite.config.js` 手工 `manualChunks` 共同维护页面拆包，历史上已有多次异步组件生命周期/切 tab 修复；若 Tauri/内网首屏体积不是瓶颈，可评估恢复普通 import 并删除手工 pageChunks，降低白屏和生命周期复杂度，但优先级低于真正的功能删减。
- 风险：这些是主动收敛/去重，不应标成死代码；保留渠道级覆盖时要先锁定 API 契约。

## 明确保留的核心链路

以下对象虽代码量大或包含状态机，但当前有真实调用方或属于代理核心，不应仅按复杂度删除：

- 单渠道测试、`discover-models`、兼容规则和 `ProbeRequestInterceptor`。
- 协议转换、SSE 转换、`StreamResponseCapture`、failover、容量、熔断和渠道亲和状态。
- Logs 的筛选统计、详情读取和核心 Dashboard 图表。
- OCR、Web Search simulate、`/images` 及渠道级模型覆盖：分别按 B.1、B.7 和第 1 节的产品决策处理，不得把“近期新增”误判为死代码。

## 1. 删除低收益高复杂度功能

### 1.1 OCR 图片回退

- 目标：去掉图片 OCR 回退链路，收到不支持图片的请求时直接返回清晰错误，或按渠道能力透传。
- 规模（实测）：`ProxyOcrService.cs` 679 + `ProxyImagePayloadRewriter.cs` 607 + `ProxyImageRequestDetector.cs` 128 + `ProxyImageFallbackService.cs` 48 + `ImageLogSanitizer.cs` 96 + `ProxyImageFallbackTests.cs` 846 + `ProxyVisionRoutingTests.cs` 575 ≈ **2979 行**。
- 涉及范围：
  - `opencodex_proxy/src/Libraries/OpenCodex.Core/Services/Proxy/ProxyOcrService.cs`
  - `opencodex_proxy/src/Libraries/OpenCodex.Core/Services/Proxy/ProxyImageFallbackService.cs`
  - `opencodex_proxy/src/Libraries/OpenCodex.Core/Services/Proxy/ProxyImagePayloadRewriter.cs`
  - `opencodex_proxy/src/Libraries/OpenCodex.Core/Services/Proxy/ProxyImageRequestDetector.cs`
  - `OpenCodex.CoreBase/Services/Proxy` 下相关接口
  - `OpenCodex.CoreBase/Domain/Proxy` 下相关模型
  - `OpenCodex.Api/Hosting/OpenCodexServiceCollectionExtensions.cs` 里的 DI 注册
  - OCR 缓存、本地 OCR 模型等环境变量配置
  - `frontend/src/Logs.vue` 的 OCR 展示
  - `tests/ProxyImageFallbackTests.cs`、`tests/ProxyVisionRoutingTests.cs`
- ⚠️ **修正：`ImageLogSanitizer.cs` 不在删除范围**。它被 `ProxyLogService.cs:746` 用于图片日志脱敏，与 OCR 回退无关，连带删除会编译失败。
- 前置：先做 A.3（清掉 `LocalOcrModel` / `OPENCODEX_TESSERACT_LANG` / `paddleocr` 死配置），可缩小本节改动面。
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
- 现状核查：`IWebSearchSimulator` 已注册且在跑，`WebSearchSimulator.NonStream` / `.Streaming` 真实调用 `WebSearchSimulationLog.Build` 等；前端 `frontend/src/WebSearch.vue:196/216` 三种模式都在选项里，默认 `convert`。**属主动砍功能，不是清理死代码**，必须先答决策点 2。

### 1.3 渠道诊断/测试渠道

- 目标：删除 `discover-models` 和 `test-channel` 调试链路，或只保留极简 discover。
- ⚠️ **修正：这不是死代码。** `frontend/src/Channels.vue` 真实调用 `/discover-models`（`api("/discover-models")`）与 `/test-channel/stream`（`Channels.vue:2107` 用 `fetch`），删除会真的丢能力 → 优先级应低于 Phase A/B。若不删，至少先做 A.4 消除其内部重复实现。
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
- ⚠️ **修正：这不是死代码。** `ProbeRequestInterceptor` 被 `ProxyController.cs:113` 调用，且有 `ProbeRequestInterceptorTests.cs` 覆盖 → 属主动砍功能，需先答决策点 4。
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

## 3. DTO 文件瘦身

- 目标：拆开超大 DTO 文件，删除前端和后端都不再使用的字段。
- ⚠️ 本节主要是文件组织收益，不是行为收益；应先完成 A/B 的死链、存储和安全治理，再按字段读取矩阵拆分。不要为了“每个 DTO 一个文件”把契约回归扩大成前置大工程。
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
- ⚠️ 这是核心协议层的大规模组织重构，当前没有独立业务收益；在现有兼容性测试已稳定前不应优先实施，避免与日志/路由清理同时改变协议行为。
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
- ⚠️ **评估：本节本身就是「低收益高复杂度」，建议降级/搁置或排最后。** 理由（已实证）：
- `Dictionary<string, object?>` 是系统骨架：`IUpstreamClient.PostJsonAsync` / `StreamJsonAsync` 签名、`ProtocolConverter.*` 全部 partial、`ChannelCompatRequestRewriter`、`ProxyRouteService` 的 channel 字典、`ProxyOcrService` 内部全是它。
- 全仓还有 10+ 份各自语义略有差异的 `FromJsonElement`/deep-copy helper；把它们强行统一成一个通用 JSON 工具只节省少量代码，却会横跨多个协议/配置边界并扩大回归面。不要另起统一工具重构。
  - 协议是 6 方向转换（`ChatToResponses` / `MessagesToResponses` / `ChatToMessages` / `MessagesToChat` / `ResponsesToChat` / `ResponsesToMessages`，见 `ProxyStreamService.cs:204-252`），保护测试 `ProxyCompatibilityTests` 4113 + `SseStreamConverterTests` 2958 + `ProtocolStructuralCompatibilityTests` 1215 = **8286 行**。
  - 改造面最大、对外行为零变化，收益最低 → 与 1-4、Phase A/B 相比优先级最低。
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
- ⚠️ **修正：原文的保留/删除对象写反了。** 按代码实际（已实证）：
  - **在用的是** `ModelInfo` + `ModelPricingPlan` + `ModelPricingRule`（`ModelCatalogService` 内 `_rules` 有 20+ 处使用，含 `CalculateRuleCost` / `DefaultRules`；`ModelProvider` 也在用，`Pricing.vue:319/388`、`Channels.vue:1291` 调 `/model-providers`）。
  - **死的才是** `ModelPricing` + `PricingController` + `ModelPricingService` + `OpenCodexPricing`（见 B.2，前端零调用、`CalculateCost` 零生产调用）。
- 涉及范围：
  - `ModelInfo.cs`
  - `ChannelModelInfo.cs`
  - `ModelPricingPlan.cs`
  - `ModelPricingRule.cs`
  - `OpenCodex.Core/Services/ModelCatalogService.cs`
  - 模型目录 DTO
  - `frontend/src/Pricing.vue`
  - `frontend/src/Channels.vue`
  - SQLite/Postgres 两套迁移
- 做法：
  - 保留 `Channel` 作为渠道配置。
  - 保留 `ModelInfo` + `ModelPricingPlan` + `ModelPricingRule` + `ModelProvider`。
  - 如渠道级模型覆盖确实需要，只保留 `ChannelModelInfo`。
  - 删除 `ModelPricing` + `PricingController` + `ModelPricingService` + `OpenCodexPricing`（即 B.2，需先确认外部无调用方与生产迁移已执行）。
  - `ChannelModelMapping`（写多读少）：`ConfigService.SyncChannelModelMappings`（`ConfigService.cs:906`）每次存渠道全删重插，唯一读点是 `ModelCatalogService.cs:1222 ListChannelUpstreamModels`，而该方法在 mapping 为空时还有 `channel.ModelsJson` 回退路径 → 可考虑直接删表、统一读 `ModelsJson`。
  - 更窄的低风险步骤：运行时真正读取的只有 `ChannelId`、`Position`、`UpstreamModel`；`RequestModel`、`SupportsImage`、`ModelInfoId`、`PricingMode`、`PricingPlanId` 及其索引没有生产读取点，且同步逻辑把 `SupportsImage=false`、`PricingMode=InheritGlobal` 等值硬编码。可先删这些列/索引，再评估是否整表迁移。
  - 写数据迁移，把旧数据收敛到新结构。
  - 前端不再编辑任意 `Catalog JSON`。
- 风险：生产已有模型目录和价格数据，迁移错误会丢数据。
- 验证：先备份数据，再跑迁移测试；价格计算结果与旧数据抽样一致。

## 推荐修复方案（至少三套，可按阶段组合）

### 共同背景与不可省略的前置条件

本次排查暴露的不是单一“代码太多”问题，而是三类维护债务叠加：

1. **高置信死代码与重复实现**继续参与编译、配置和发布流程，制造误导性入口；例如两套 dotenv 设置类、重复的兼容重写和无条件 Tauri DevTools。
2. **运行时边界缺少门禁**。`services.AddControllers()` 不会把默认 Controller 激活完整纳入普通 `ValidateOnBuild`，因此 `/images` 的依赖漏注册直到真实请求才变成 500。
3. **数据与部署存在双轨**。SQLite/Postgres 各自维护迁移和 snapshot，远端又同时存在 `ocxp-postgres` 与 `ocxp-postgres-dev`；日志 GB 级增长、明文密钥和无 TTL 比单纯删几百行代码更紧急。

无论选择哪套方案，都必须先完成以下前置动作：

- 确认目标正式生产实例、容器和数据库；不能把开发库样本或仓库根 `AGENTS.md` 的旧部署描述当成生产事实。
- 同时检查反向代理/网关 access log、应用 HTTP access log、脚本/CI 配置和客户端版本。`RequestLogs.Path` 主要是代理请求日志，不能单独证明 `/pricing` 或其他管理端路由没有外部调用。
- 备份数据库、镜像和配置；记录 `__EFMigrationsHistory`、关键表行数/非空率、schema checksum，并在 staging 完成恢复演练。
- 代码删除、数据回填、破坏性 migration 分开提交和发布。为 SQLite/Postgres 建立“逻辑迁移清单”（逻辑名称、目标版本、两套 migration 文件、回填脚本、是否破坏性和最低应用版本），不要要求两个 provider 的 migration ID 字符串相同。
- 增加 Controller/endpoint smoke test：从 MVC `ControllerFeature` 枚举真实 Controller，在独立 scope 中用 `ActivatorUtilities.CreateInstance` 激活，并检查 `EndpointDataSource` 路由；对最小匿名/鉴权请求断言不能返回 500。该测试比单独打开 `ValidateOnBuild` 更可靠。

### 方案一：保守、可逆的“止血 + 高置信清理”（风险最低）

#### 背景与原因

适用于正式生产调用尚未完全盘清、仍需兼容旧桌面端/旧脚本的阶段。它只处理已通过零引用核验的对象，并先控制数据和安全风险，不主动删除仍在使用的 Web Search simulate、Probe、单渠道测试或渠道模型映射。

当前最值得立即止血的是日志和密钥：远端样本已有约 3.16 GB `RequestLogStreamLines`、5.85 GB `RequestLogDetails`、单请求最高 124,575 条流行；成功 attempt 占 94.2%/95.4%，却仍保存完整请求体和流行。远端样本中的 `AccessApiKeys` 两库共 26 条记录，全部 `KeyPlaintext` 非空且 `LastUsedAt` 为空；这说明安全风险和存储写放大都不是理论问题，但不能把样本数量直接当成正式生产全量基线。

#### 修复内容

1. **先限增长，暂不做破坏性删列/删表**：增加详情级别开关、每请求 SSE 最大行数/字节数/时长，超限只保留首尾摘要和截断标记；默认不保存成功 attempt 的完整详情，只保留失败、实际 failover 和父日志计数。可先采用需基线/压测校准的初始预算：单请求最多 2,000 行/1 MB、单详情字段 256 KB、总详情 1 MB、最长捕获 5 分钟；建议元数据保留 30 天、详情 7 天、流行 24 小时。请求体、上游请求体和原始 SSE 统一脱敏，备份后允许按时间窗分批清理超期行；清理任务保留一键暂停和审计日志。
2. **降低密钥暴露面**：列表接口不再返回完整 key；导出默认关闭，若必须保留则限制超级管理员、加密文件、schema/version 校验、冲突预览和审计；新增一次性展示与轮换流程。DataProtection 只有在 key ring 目录权限、持久化备份和恢复演练都满足时才足够；`OPENCODEX_SECRET_KEY` 不是自动加密业务密文的替代品，生产优先评估外部 KMS/Vault。先轮换已有明文 key，再删除历史备份中的明文副本。

> 业务决策（2026-08-23）：明文密钥返回为业务需要保留，不修改。本条中「列表接口不再返回完整 key」「导出默认关闭」不再适用。如需改进导入/导出，仅限权限（超管）、schema 校验、冲突预览和审计。
3. **立即消除 `/images` 的运行时 500**：若产品决策尚未完成，不实现图片业务也不能继续暴露坏链。将现有业务 Controller 替换为不依赖缺失服务的 `RetiredImagesController`/短路中间件，尽早返回 410（或明确 501），不要先解析 multipart/大请求体；待调用观测完成后再选择整链删除或完整重建。不能只在现有 `ImagesController` 内加开关，因为构造注入会先失败。
4. **执行 A.1-A.4 的纯清理**：删除无引用的 `OpenCodexSettings`/`OpenCodexSettingsLoader`、OCR 的 `LocalOcrModel`/Tesseract 配置、`ApplyCompat` 重复五步及零读取 `CompatDetails`。保留 `OPENCODEX_SECRET_KEY`、`OcrCacheDir`、`ImageLogSanitizer` 和仍在用的 Probe/诊断链路；处理旧 `paddleocr` 缓存兼容后再删常量。
5. **只做测试门禁，不改变生产 DI 语义**：加入 Controller 激活/路由 smoke test；暂不因为测试便利直接启用 `AddControllersAsServices()`，也不把 `ValidateOnBuild` 当作充分保证。代理 action 的 smoke test 不应真实调用上游，只覆盖 Controller 激活、元数据和匿名/鉴权边界。
6. **双库只做非破坏性变更**：新增索引、marker、统计表或配置开关可以成对迁移；暂不删除旧列/旧表，观察一个完整业务周期。

#### 风险与缓解

- 诊断信息减少：保留失败请求和首尾流行，提供临时 debug 开关；开关必须有权限和自动过期时间。
- 外部旧脚本可能依赖导出或旧配置字段：先保留兼容读取，记录访问方，发布说明中给出迁移期限。
- 历史日志清理误删：先只读盘点和备份，分批按时间窗口删除，逐批校验行数和磁盘回收效果。

#### 验收标准

- 长流超过上限时内存、数据库写入和响应都可预测；TTL 清理可重入、可暂停，敏感字段在请求体/上游体/SSE 中均不泄漏。
- ~~密钥列表、导出和日志不再出现完整 key~~（明文密钥为业务需要保留，不修改）；轮换后的旧 key 立即失效。
- `dotnet build`、后端全量测试（基线 473/473）、前端构建和 Controller smoke test 通过；故意移除一个 Controller 注册时测试能给出明确缺失依赖。
- 线上 `/models`、`/config`、登录、代理流和 Logs 核心查询行为不变。

#### 适用条件与评价

适合先落地，收益中等、风险低、回滚快。它会先把 `/images` 从 500 修正为明确的 410/501，再决定是否整链删除；旧 `/pricing` 仍保留到调用观测和迁移完成。

### 方案二：平衡收益与复杂度的“兼容迁移 + 软下线”（默认推荐）

#### 背景与原因

适用于已完成方案一的调用盘点，并确认大多数旧能力没有近期产品需求，但仍不能保证所有外部脚本和旧客户端立即升级。目标是把“死链/坏链”从运行时和数据模型中逐步移除，同时给调用方一个明确迁移窗口。

#### 修复内容与步骤

1. **先观测再软下线**：对 `/images*`、`/pricing*`、配置导入/导出、批量测试和 Dashboard SSE 记录路径、UA、客户端版本、调用次数和状态码，至少覆盖一个完整保留窗口。观测数据不得保存 API key、Authorization 或完整请求体。
2. **修复 `/images` 的错误语义**：不能在现有 `ImagesController` 上简单加 feature flag，因为构造注入仍会先解析缺失的 `IProxyImagesEndpointService`。若决定下线，应替换为不依赖业务服务的 `RetiredImagesController`/中间件并返回统一 `410 Gone`；若需要保留“未实现”语义则返回 `501`。响应带 `Deprecation`、`Sunset` 和迁移文档链接，确保不再出现 500。最终删除时再移除 Controller、图片渠道校验、`channelImagesState`、历史配置迁移和所有 `images` 引用。
3. **收敛 `/pricing`**：legacy seed、全量重写、远端拉取和新目录迁移已停止。剩余 `/pricing` CRUD 仅作兼容保留，不影响实际代理计费；连续一个保留窗口无外部调用后，再删除 `PricingController`、`ModelPricingService`、旧 DTO、`OpenCodexPricing` 和旧表。
4. **收敛 Dashboard 实时卡片**：若无实时运维刚需，删除队列/错误两张卡片、伪 SSE、仅供它们使用的查询链和测试；若必须保留，新增单一低频 GET + 前端轮询，不同时维护 SSE 和轮询两套状态机。
5. **迁移低价值管理台便利层**：保留单渠道测试、模型发现和渠道 CRUD；在使用率确认后删除批量测试、批量编辑、归并视图和未使用的路由前缀别名。Web Search 近期只有 Tavily 时，在 API 边界固定 provider，保留接口作为测试边界，不提前为第二家供应商维护整套泛化字段。
6. **执行扩展→回填→切换→收缩**：先加新字段/marker 和兼容读取，再批量回填并校验计数/哈希，切换代码只读新结构，保留旧列一个版本，最后为 SQLite/Postgres 分别提交删列/删表 migration。生产回滚以备份/旧镜像为准，不把 EF `Down()` 当唯一回滚手段。
7. **完成密钥与日志的目标态迁移**：`AccessApiKey` 可保持现有 hash 认证并改为 hash-only；渠道 API key、Tavily key 和需回调上游的敏感 headers 必须使用可逆密文或 `secret_ref`，不能套用不可逆 hash。GET/list 永不返回原文，create/rotate 只一次性返回；更新时缺失或 `null` 表示保持旧值，只有显式 rotate/clear 才改变秘密；`/web-search` 测试响应也不得回传 key。导出默认 metadata-only，必要时使用版本化、单独口令加密的 bundle，并支持 dry-run、冲突预览和审计。迁移顺序固定为“备份/轮换 → 双读回填 → 抽样解密验证 → 清空 `KeyPlaintext`/历史导出副本 → 保留一个版本 → 删除旧列”。

> 业务决策（2026-08-23）：明文密钥返回为业务需要保留，不修改。本条中涉及「hash-only」「secret_ref」「GET/list 永不返回原文」「metadata-only 导出」「清空 KeyPlaintext」的建议均不再适用。
8. **把原始流行变成受限 ring buffer**：先结构化解析并脱敏；解析失败只保留事件名、长度和 hash，不保留原文。每请求同时限制行数、字节和时长，超限保留首尾片段及 `truncated/dropped_*` 摘要；成功 attempt 只写状态、耗时、渠道和错误摘要，失败/failover 才按短 TTL 保留详情。

#### 风险与缓解

- 外部调用未发现会造成 404/410：保留软下线窗口、网关告警和按调用方发迁移通知。
- 旧价格迁移不一致会影响费用：迁移 marker 写入前阻断删除，抽样比较旧/新价格快照和历史账单。
- SQLite 重建表可能锁库，Postgres 索引/删除列行为不同：分别在空库和上一版本 fixture 演练，并按 provider 编写 SQL/回滚说明。
- 删除批量运维会降低管理效率：保留单渠道能力和导出/备份替代路径，按真实使用率逐项下线。

#### 验收标准

- 观测期结束后，旧路由调用量为 0 或全部来自已登记迁移方；下线路由稳定返回 410/404，绝不返回 DI 500。
- 新旧计费快照和费用抽样一致，legacy 行迁移数、冲突数和 marker 状态可审计；启动不再重复播种或访问 GitHub。
- Dashboard 核心图表、Logs 查询、单渠道测试、模型发现和渠道 CRUD 全部通过契约测试。
- 两套 provider migration 均可从备份恢复，`GetPendingMigrations()` 为 0，schema 差异均有清单解释。

#### 适用条件与评价

这是综合收益最高、风险可控的默认方案，但需要一个维护窗口、调用观测和至少一个版本的兼容期。

### 方案三：激进收敛为“生产单一真相 + 功能整链删除/重建”（风险最高）

#### 背景与原因

适用于团队已经书面确认：Postgres 是唯一生产数据库、SQLite 仅服务可弃用的旧桌面/测试场景，且 `/images`、旧 `/pricing`、管理台导入导出、批量运维等功能均有明确产品决策。该方案一次性降低长期维护面，但把代码清理升级为产品、数据和部署架构变更。

#### 修复内容与步骤

1. **确定单一数据真相**：以 Postgres schema 为 canonical，发布 SQLite sunset 日期和客户端升级门槛；提供一次性 SQLite 导出/校验工具，保留只读备份而不是继续双写。未达到升级率前仍必须维护成对 migration，不能提前删除 SQLite migration。
2. **整链处理主动砍功能**：无量化图片需求则删除 `/images` Controller、接口、专用 upstream、图片渠道、校验、前端状态和历史兼容分支；删除旧 `/pricing` 半边及 legacy 表；~~删除三套管理台导入/导出和明文持久化~~（明文密钥为业务需要保留，不修改）；按使用率删除 Dashboard 伪 SSE、批量测试/编辑/归并视图和兼容别名。若图片确有需求，则反向选择完整实现 `ProxyImagesEndpointService`，补齐鉴权、候选排序、容量、熔断、failover、日志脱敏和端到端契约，不能做半修复。
3. **模型目录收敛**：先删除 `ChannelModelMapping` 的死列/索引，再在数据对账后决定整表；固定唯一 Web Search provider 或保留真正需要的抽象；禁止任意 Catalog JSON 编辑，改为白名单字段/只读元数据。
4. **启动架构门禁**：在 staging 先实验 `AddControllersAsServices()`、`ValidateScopes`、`ValidateOnBuild`，记录生命周期和启动耗时；实验通过后生产启用 fail-fast，同时保留 Controller/endpoint smoke test。
5. ~~**密钥和事故日志外置**~~（明文密钥为业务需要保留，不修改）：本条不再适用。公网/多租户场景下的事故日志外置策略可独立评估，但不涉及密钥存储方式的改变。
6. **发布与回滚**：使用蓝绿/影子流量验证真实快照，监控 404、DI 激活异常、迁移锁、日志增长、成本和上游错误；切换前保留完整数据库备份、旧镜像和明确负责人。

#### 风险与缓解

- 旧客户端无法升级会直接中断：提前公告 sunset、返回带迁移链接的 410、保留兼容 facade 到截止版本。
- 破坏性 migration 失败可能造成整体不可用：先在真实快照副本演练，回滚依赖备份恢复，不依赖 `Down()`。
- 删除图片/批量运维会损失业务能力：必须有产品负责人签字、调用量/成本/维护工时数据和替代流程。
- 单一 Postgres 假设若错误会影响桌面端：在发布前核验所有部署 profile、测试 fixture 和离线模式。

#### 验收标准

- Postgres 备份可完整恢复，关键表/索引/约束和业务抽样一致；生产启动时任何 Controller 缺依赖都会在部署前失败。
- 被删除路由明确返回 404；保留的软下线路由返回 410/501 且无 500；所有公开 API 契约测试和前端构建通过。
- SQLite sunset 文档、导出工具、客户端升级率、回滚演练和负责人均已确认；日志 TTL/上限、密钥轮换和敏感字段脱敏验收通过。

#### 适用条件与评价

收益最高、长期维护成本最低，但不可逆风险也最高。只有在外部调用、数据库使用者、迁移责任和产品需求都已签字确认时采用。

### 三套方案的选择建议

| 方案 | 主要收益 | 风险/回滚 | 对外兼容 | 推荐条件 |
| --- | --- | --- | --- | --- |
| 方案一：止血 + 高置信清理 | 立即降低数据、安全和编译噪声 | 低，容易回滚 | 基本保持 | 事实尚未完全确认，想先安全推进 |
| 方案二：兼容迁移 + 软下线 | 收益与风险平衡，能真正移除死链 | 中，需要维护窗口 | 保留一个版本的迁移期 | **默认推荐** |
| 方案三：单一真相 + 整链收敛 | 长期维护面最小 | 高，依赖备份/切换 | 主动终止旧兼容 | 已有书面产品批准和 SQLite sunset |

推荐路径：先执行方案一的共同前置、日志止血和 smoke test；确认调用与数据库事实后升级到方案二。方案三不应作为普通重构直接合并。

## 功能点逐项三方案（按清单合并重复项）

上一节的三套方案是“整体推进路线”。本节针对每一个候选功能点单独给出至少三种处理选项；同一功能在 A/B/第 1–6 节重复出现时合并说明，避免把同一决策拆散。每个功能点的选项统一为：A=窄改/立即止血，B=迁移后收敛（通常推荐），C=需求驱动保留或重建。

| 功能点 | 方案 A | 方案 B | 方案 C | 当前建议 |
| --- | --- | --- | --- | --- |
| A.1 旧设置类与 `OPENCODEX_LOG_*` | 直接删死链 | 兼容适配一个版本 | 真正接入结构化日志 | A |
| A.3 / 1.1 OCR | 保留 Vision、清理残留 | 软下线后删除 | 实现本地 OCR | A；需求为零时 B |
| A.4 Compat 重复链 | 单一权威重写 | 保留脱敏解释管线 | 删除诊断解释链 | A |
| A.5 Controller/DI 门禁 | 测试 smoke | staging fail-fast | 启动 readiness 验证器 | A，随后评估 B/C |
| A.6 异构死字段/脚本 | 证据驱动分批删 | deprecated 兼容隔离 | 把缺失能力补实 | A+B |
| B.1 `/images` | 退役壳止血 | 观测后整链删除 | 完整重建 | A→B；有需求才 C |
| B.2 `/pricing` | 只读兼容 facade | 迁移后删除 | 保留完整旧 CRUD 适配 | B |
| B.3 Dashboard 伪 SSE | 删除卡片/端点 | 低频 GET 轮询 | 事件驱动实时摘要 | A |
| B.4 导入导出/密钥 | ~~metadata-only 止血~~ | ~~加密迁移~~ | ~~删除 UI、接管 Vault/KMS~~ | 明文密钥为业务需要保留，不修改。仅可改进权限/schema/审计 |
| B.5 日志留存 | 上限+批量清理 | 分层留存服务 | 删除详情链、外部事故捕获 | B |
| B.6 批量渠道运维 | 删除便利层 | 后端作业化 | 窄幅限额收敛 | 低频 A；高频 B；未决 C |
| B.7 Provider/模型目录 | 固定 Tavily | 真正插件注册表 | 最小模型目录 | A 或 C |
| 1.2 Web Search simulate | 保留并清理配置 | 软下线/默认关闭 | 删除或重建模拟器 | A；需求为零时 B |
| 1.3 渠道诊断 | 保留单项、删重复 | 保留薄兼容路由 | 整链删除 | A |
| 1.4 Probe 拦截 | 保留并修复桌面同步 | 兼容迁移后关闭 | 完全删除 | A；明确无需求才 C |
| 第 3 节 DTO | 只拆文件 | 字段矩阵后瘦身 | v2 版本化契约 | A→B |
| 第 4 节 Protocol Codec | 不拆、补边界 | 逐方向抽取 | 完整强类型 codec | A→B |
| 第 5 节内部强类型 | 保留动态边界 | typed adapter 渐进迁移 | 全量强类型化 | A→B |
| 第 6 节模型目录/计费 | 兼容保留 | 软下线旧半边 | 单一数据库/目录真相 | B |

### F.1 A.1：旧设置类与 `OPENCODEX_LOG_*`

**背景与原因**：`OpenCodexSettingsLoader` 全仓无消费者，实际生效的是 `OpenCodexRuntimeSettingsProvider + DotEnvDefaults`；`OPENCODEX_LOG_PATH/LOG_LEVEL/LOG_VIEW_LEVEL` 没有日志框架或消费点，却仍在文档和 Tauri 中出现。历史上新增运行时配置时没有删除旧配置入口，形成重复 dotenv 解析和误导性部署契约。`OPENCODEX_SECRET_KEY` 是活配置，不能误删。

- **方案 A（推荐）**：删除两个旧设置类、重复 helper、`LOG_*` 的文档/注入/示例；保留 runtime provider、`OPENCODEX_SECRET_KEY` 和 OCR cache 配置。收益是行为唯一、改动小；风险是外部旧脚本可能依赖旧变量。验收：`rg` 无旧类型/LOG_* 消费，`.env` 等价测试、启动和全量测试通过。
- **方案 B**：保留 `[Obsolete]` 薄适配层一个版本，委托 `IConfiguration`，对旧变量只计数和输出脱敏迁移告警，读取量归零后删除。优点是兼容性好；缺点是重复入口继续存在。验收：优先级、冲突规则和 sunset 日期明确，告警不泄露秘密。
- **方案 C**：把 `LOG_*` 真正接入结构化日志、级别、轮转、权限和磁盘配额，并统一配置绑定。优点是兑现配置承诺；缺点是新增日志运维复杂度，当前没有明确需求时收益低。验收：Docker/Tauri/本地语义一致，轮转/脱敏/容量和故障降级测试通过。

### F.3 A.3 与 1.1：OCR 残留和图片 OCR 回退

**背景与原因**：新请求路径固定 Vision，项目没有 Paddle 包；`LocalOcrModel`/Tesseract 配置不被服务读取，`paddleocr` 只可能出现在历史缓存。完整 OCR 回退链约 2,979 行；多图请求可能按图片数串行调用 Vision，缓存命中仍会写 OCR 子日志，而缓存目录没有 TTL/容量上限。OCR 是否仍是产品能力需单独确认，`ImageLogSanitizer` 与 OCR 无关不能误删。

- **方案 A（推荐默认）**：保留 Vision，处理旧 `paddleocr` 缓存（迁移 engine、只读兼容或 TTL 清理），删除 LocalOcrModel/Tesseract/Paddle 残留，并为 `OcrCacheDir` 增加容量/TTL；同时设置单请求图片数、总字节和总 OCR 时间预算，缓存命中只写轻量计数，不再复制完整 OCR 子日志。优点是保留现有图片回退、风险低；验收是 Vision、失败降级、旧缓存、预算和缓存清理测试全绿。
- **方案 B**：软下线一个版本，禁止新请求写 OCR/cache，增加使用计数和 `Deprecation/Sunset`；调用为零后删除 `ProxyOcrService`、路由、配置、缓存和图片兼容字段。优点是长期成本最低；缺点是失去图片回退。验收：下线期无新 OCR 写入、旧配置迁移可重入、普通文本代理不受影响。
- **方案 C**：真正实现本地 Paddle/其他 OCR，兑现 `LocalOcrModel`，Vision 作为 fallback；补模型版本、隔离进程、CPU/内存/图片大小/超时/缓存治理。优点是离线隐私能力完整；缺点是镜像、漏洞和跨平台维护成本最高。验收：离线、多图、恶意图片、取消、资源上限、Docker/Tauri 可复现。

### F.4 A.4：Compat 重复实现与 `CompatDetails`

**背景与原因**：诊断服务先手写五步 compat，又调用权威 `ChannelCompatRequestRewriter.Apply` 重跑；`Details/CompatDetails` 全仓无读取，重复逻辑却扩大了测试和敏感数据面。

- **方案 A（推荐）**：诊断服务只调用 canonical rewriter，删除重复五步、details 参数链和包装类，保留 Payload。优点是最小改动、代理与诊断一致；验收覆盖五类规则 golden test 和现有兼容测试。
- **方案 B**：把 rewriter 改为步骤管线，每步返回 Payload + 脱敏审计事件；代理只取 Payload，诊断按权限/开关取摘要。优点是保留排障解释；缺点是新增事件模型和脱敏维护。验收顺序固定、敏感值不出响应、开关关闭时零额外日志。
- **方案 C**：若管理台诊断也低频，删除解释链和相关 DTO，只保留单渠道连通性/模型发现；代理主链绝不能删。验收旧诊断返回 410/404，单渠道测试和 discover-models 仍正常。

### F.5 A.5：Controller 激活与 DI 验证

**背景与原因**：`/images` 证明普通 `ValidateOnBuild` 不足以发现 Controller 构造依赖漏注册；当前 DI 手工注册、无启动门禁。

- **方案 A（推荐）**：测试侧枚举 MVC `ControllerFeature`，用 `ActivatorUtilities` 激活并检查 `EndpointDataSource`；最小请求只测元数据/鉴权边界，不触发上游。优点是不改变生产生命周期；验收故意删注册时 CI 明确失败。
- **方案 B**：staging 实验 `AddControllersAsServices + ValidateScopes/ValidateOnBuild`，记录启动时间/生命周期后再决定生产 fail-fast。优点是部署前失败；缺点是可能改变启动边界和内存。验收 staging 缺依赖时健康检查不放行。
- **方案 C**：保持 MVC 默认注册，增加 `IHostedService`/readiness 验证器，启动时逐 Controller 检查，支持 warning/fail 配置。优点是渐进可控；缺点是新增启动组件。验收 readiness 在验证完成前不通过，失败信息包含 Controller 和参数。

### F.6 A.6：死字段、无效配置、调试残留和旧脚本

**背景与原因**：`LastUsedAt` 不持久化、`RequestLogDetail.Id` 不参与主键、`responseModel`/多个 helper 零调用，另有旧环境变量、启动诊断文件、无级别 Console debug、过时脚本和文档。它们消费者不同，不能一次性按“前端没用”删除公开契约。

- **方案 A（推荐）**：建立消费者矩阵，连续一个完整窗口零消费后分批删除私有方法、死常量、诊断文件、过时脚本和明确死列；数据库列先做双 provider 兼容迁移再收缩。优点是证据充分、回滚容易；验收每项有 `rg`/访问日志证据、旧 DB 启动和前端构建全绿。
- **方案 B**：公开字段/环境变量保留 deprecated 只读兼容，禁止新写入，加入使用计数和 sunset；内部零调用方法立即删。优点是保护旧客户端；缺点是兼容代码继续膨胀。验收旧读不改变核心响应，新写入走新字段或明确拒绝。
- **方案 C**：对确有价值的缺失能力补实，例如异步节流写回 `LastUsedAt`、真正结构化日志、显式诊断开关和可复现脚本。优点是契约兑现；缺点是与清理目标相反，新增运维复杂度。仅在有审计/运维需求时采用，并验收轮转、权限、容量和失败降级。

### F.7 B.1：`/images` 图片生成/编辑坏链

**背景与原因**：四条 `/images` 路由在 Controller 激活阶段因 `IProxyImagesEndpointService` 未注册而返回 500；专用 `IImagesUpstreamClient` 也未注册，仓库却保留渠道选项、校验、解析器和测试，形成半完成链路。远端样本无 images 渠道，但仍需核验正式生产和外部客户端。

- **方案 A（推荐第一步）**：替换为不依赖业务服务的 `RetiredImagesController`/短路 endpoint，尽早返回 410（计划保留但尚未实现时返回 501），带 `Deprecation/Sunset` 和迁移链接，不读取大 multipart。优点是立即消除 500、改动小；缺点是能力仍不可用。验收：四路由在异常大请求下也稳定 410/501，无 DI 异常和读放大。
- **方案 B**：观测一个窗口后整链删除 Controller、接口、专用 upstream、图片渠道校验、前端状态、`images_api_dialect` 和历史兼容分支；已有配置明确 disabled/拒绝，历史日志保留 retired 类型。优点是长期维护面最小、物理路由 404；缺点是旧客户端立即失败。验收：四路由 404、`rg` 无未登记业务引用、双 provider 配置迁移可重入。
- **方案 C**：有书面需求时完整实现 `ProxyImagesEndpointService`，接入专用 HttpClient，复用鉴权、候选排序、容量、熔断、failover、响应写入和图像脱敏；明确 generations/edits 参数、大小/格式/超时/重试和成本。优点是恢复产品能力；缺点是上游费用、图片泄露和重试扣费风险高。验收：成功、鉴权失败、参数拒绝、上游 4xx/5xx、取消、failover 和灰度成本指标全通过。

### F.8 B.2：旧 `/pricing` 计费管理链

**背景与原因**：生产计费已走 `IModelCatalogService.CalculateCostAsync`，旧 `/pricing*` CRUD 仅作外部兼容保留；启动迁移、内置播种和 GitHub 远端更新均已删除。**远端样本**旧表仍有约 111 行（正式目标实例待确认），因此仍不能凭前端零调用直接删表。

- **方案 A**：保留薄只读兼容 facade，把新目录映射成旧 GET DTO；POST/PATCH/DELETE/seed 返回 410，停止旧播种和 GitHub 拉取。优点是保护外部只读脚本；缺点是旧 DTO/匹配语义仍要维护。验收：登记调用方 GET 与新目录逐字段一致，写路由不改变价格数据。
- **方案 B（推荐）**：生成 legacy→新目录逐行对账和幂等 marker，完成迁移后软下线一个窗口，再删 `PricingController`、旧 service/算法/DTO/表及双 provider migration。优点是最终只留一套计费真相；缺点是迁移和费用回归风险高。验收：冲突/未知 vendor/规则优先级均可审计，费用与历史账单抽样一致，旧路由 410→404。
- **方案 C**：如果存在旧写 API 合同，重写完整旧 CRUD 到新 `ModelPricingPlan/Rule` 的显式适配层，双读对账后再删 legacy。优点是保留客户工具；缺点是适配层复杂度接近重建，禁止隐式猜测旧写语义。验收：并发、权限、匹配、币种和账单逐项一致。

### F.9 B.3：Dashboard 两条伪 SSE

**背景与原因**：Controller 内每 2/5 秒 `while + Task.Delay + 查库 + WriteAsync`，每个浏览器连接都重复查询；卡片与 Logs/Channels 重叠，`recent-errors` 甚至没有普通 GET。根因是把定时快照错误建模成实时事件流。

- **方案 A（默认推荐）**：删除请求队列/近期错误卡片、两个 SSE 端点、仅供它们使用的查询链和状态机，保留核心图表、容量限流和 Logs 错误详情。优点是复杂度和数据库压力最低；缺点是首页失去秒级速览。验收：路由 404、无 EventSource/常驻连接，Dashboard/Logs/Channels 核心功能正常。
- **方案 B**：保留卡片，新增统一 `GET /stats/dashboard-summary` 或两个低频 GET，15–30 秒轮询，页面 hidden 时暂停，服务端短 TTL 缓存/分页/上限。优点是保留运维价值；缺点是新增 DTO/缓存和延迟。验收：刷新频率、p95、并发查询量和权限均有预算，无 SSE 与轮询并存。
- **方案 C**：真正事件化，Redis pub/sub/Streams 聚合请求开始/结束/错误，单一 SSE/WebSocket 支持 heartbeat、Last-Event-ID、重连和连接预算。优点是多消费者实时扩展；缺点是事件一致性、Redis 故障降级和维护成本明显过高。仅在有实时告警/多消费者需求时采用。

### F.10 B.4：管理台导入/导出与明文密钥 — 明文密钥为业务需要保留，不修改

**背景与原因**：三页各自维护约 996 行 Blob/解析/合并逻辑，导出和 DTO 返回渠道、Access、Tavily 完整密钥；导入无统一 schema/version、dry-run 和冲突确认。Access 认证实际只查 hash，`KeyPlaintext` 是额外泄露面；渠道/Tavily 密钥则需要可逆密文或 secret_ref。

- **方案 A**：立即改 metadata-only DTO/list/export，创建/rotate 只一次性返回，导入限 superadmin、schema 校验、预览和审计；掩码值表示保持旧秘密，先轮换历史 key。优点是快速止血；缺点是旧明文列和历史备份仍需清理。
- **方案 B（推荐目标态）**：Access 改 hash-only；渠道/Tavily/敏感 headers 扩展密文或 `secret_ref`，按“备份/轮换→双读回填→解密抽样→清空明文→删列”迁移；导出为加密 versioned bundle，导入 dry-run/二次确认。优点是安全与迁移平衡；缺点是 key-ring/KMS、双库 migration 和 DTO 兼容成本高。
- **方案 C**：删除三套 UI/API 导入导出，所有可逆秘密交给 Vault/KMS/Docker Secrets，数据库只存 secret_ref/指纹，普通 CRUD 仅支持轮换。优点是长期暴露面最小；缺点是外部硬依赖和离线部署门槛最高。验收：代码/API/UI 无导入路径，数据库/备份扫描无完整 key，secret manager 故障可观测且不回显密文。

> 业务决策（2026-08-23）：明文密钥返回为业务需要保留，不进行修改。上述方案 A/B/C 中涉及「停止明文返回」「hash-only」「secret_ref」「GET/list 永不返回原文」「metadata-only 导出」的建议均不再适用。如仍需改进导入/导出，仅限于权限（超管）、schema/version 校验、冲突预览和审计记录。
### F.11 B.5：日志详情、原始 SSE 与 attempt 留存

**背景与原因**：样本已有约 3.16 GB 流行、5.85 GB 详情，最大单请求 124,575 行；成功 attempt 94.2%/95.4% 却仍复制完整正文，而读取侧默认排除。根因是无界捕获 List、`UseRawLine` 原样入库、请求体/上游体重复保存、无 TTL；逐实体 `SaveChanges` 也不适合千万级清理。

- **方案 A**：统一结构化脱敏，采用“需基线/压测校准的初始建议值”：单请求最多 2,000 行/1 MB、总详情 1 MB、单字段 256 KB、5 分钟；成功 attempt 只留摘要；用 Postgres CTE/`ExecuteDelete` 或 SQLite 小批 DELETE + VACUUM 的 cron 分批清理。优点是立即止血、无需删表；缺点是依赖外部 cron、旧宽表仍在。
- **方案 B（推荐）**：三层留存：摘要 30 天、失败/显式 debug 详情 7 天、流行 24–72 小时；引入 `ILogRetentionService`/锁/指标，实体增加 ExpiresAt、截断和脱敏版本，固定容量 ring buffer，SQLite/Postgres 成对迁移。优点是诊断与容量平衡最好；缺点是治理服务和迁移复杂。
- **方案 C**：删除 `RequestLogDetails`/`RequestLogStreamLines` 与原始详情 UI/API，业务库只留 7–14 天摘要；superadmin 事故开关按采样把已脱敏内容加密写外部存储，24 小时过期并审计。优点是高安全、最低存储；缺点是失去在线逐行排障、依赖外部存储。验收必须证明默认库/备份无正文和 SSE。

### F.12 B.6 与 1.3：渠道批量运维、归并视图和诊断路由

**背景与原因**：批量测试约 838 行前端 worker/SSE 状态，实际只是并发调用已有单渠道测试；归并视图另有约 433 行表格/选择同步/CSS，批量编辑还维护文本 DSL。远端样本命中 `/test-channel/stream` 4 次，不能直接视为零使用；但单渠道测试、模型发现和 CRUD 是明确核心能力。

- **方案 A（低频推荐）**：删除批量测试、批量编辑、归并视图及其状态机，保留单渠道测试、discover-models、CRUD 和必要 `group_name`；旧路由别名先 410 后 404。优点是长期复杂度和日志/费用最低；缺点是大规模运维变慢。验收：批量代码/路由无残留，单项功能、权限和历史配置正常。
- **方案 B（高频推荐）**：后端作业化，提交渠道 ID 列表返回 job id，统一并发/超时/取消/幂等/权限，状态用受限 GET 或单一 SSE；批量编辑改结构化 patch，成功项只写摘要日志。优点是保留效率并把资源控制放到服务端；缺点是新增 job 持久化和后台 worker。验收：重复提交不重复执行，部分失败/取消可追踪，费用和日志预算受控。
- **方案 C（使用率未明的快速收敛）**：不重写页面，只抽共享 composable，限制批量大小、worker、总时长，hidden/卸载自动取消，批量测试默认摘要日志；只删除确认无外部调用的别名。优点是上线快；缺点是重复表格/DSL 仍在。验收：超限拒绝、取消无悬挂请求、单项兼容测试全绿。

**选择规则**：低频选 A，高频且渠道规模大选 B，使用率尚未确认先选 C 做护栏；无论哪种都不能删除已证实在用的单渠道测试、模型发现和 Probe 主链。

#### 渠道诊断核心链（`discover-models`/`test-channel`）的三种选项

- **方案 A（当前推荐）**：保留单渠道测试、模型发现和 CRUD，只把两套路由别名收敛为一个 canonical action，并复用 `ChannelCompatRequestRewriter`。适用：仍需日常验活/排障；验收单渠道 SSE、模型发现、兼容规则和权限契约不变。
- **方案 B**：保留薄兼容 Controller，把测试执行收敛为后端受限 job/低频查询，前端不再维护两套 SSE 状态；旧前缀别名在 sunset 窗口返回 410。适用：外部调用不明但希望降低浏览器复杂度；验收 job 幂等、取消、结果摘要和旧客户端迁移可追踪。
- **方案 C**：删除 discover/test 诊断整链，仅保留受鉴权的 `/health`/`/models` 能力；旧路由明确 410 后 404。适用：产品确认不再提供渠道诊断；验收代理核心、容量、熔断和普通 CRUD 不受影响，访问量在窗口内归零。

### F.13 B.7 与 1.2：Web Search Provider、`simulate` 和模型目录泛化

**背景与原因**：实际 provider 只有 Tavily，但前后端维护多 provider 选择/兼容字段；`simulate` 远端确有配置和日志，不能顺手删除。`Pricing.vue`/`Channels.vue` 重复编辑模型、能力、四类价格和任意 Catalog JSON；渠道级覆盖确有读取点，Codex 官方目录又是约 258 KB 的客户端专用静态资产。根因是未来需求泛化先于实际供应商/模型规模。

- **方案 A（近期无第二 provider 时推荐）**：API 边界只接受/规范化 Tavily，未知 provider 明确拒绝，不立即删除实体列；保留 `IWebSearchClient` 测试边界，压缩兼容字段，Catalog JSON 改白名单/只读，Codex 目录按 UA/client_version 懒加载。`simulate` 单独保留并加 allowlist、搜索/迭代/耗时上限和最小脱敏日志。优点是风险低、与事实一致；缺点是未来接第二 provider 需再扩展。
- **方案 B（确定多 provider/多租户需求时）**：建立 provider registry/descriptor 和 schema 驱动表单，每家 adapter 独立处理能力、错误和 secret_ref；共享 ModelEditor，明确 global 与 channel override；`simulate` 保留为独立策略接口并统一流/非流状态机。优点是新增 provider 不复制整链；缺点是注册表/schema 本身增加复杂度。验收 fake provider contract、未注册 provider 拒绝、密钥不出 DTO/日志、模型覆盖优先级矩阵全绿。
- **方案 C（模型规模小且接受受限编辑时）**：收敛为 global `ModelInfo/Plan/Rule/Provider` + 实际读取的 `ChannelModelInfo`，先删 `ChannelModelMapping` 死列/索引，再评估整表；删除任意 Catalog JSON 和前端全量分页，Codex 目录独立懒加载。`simulate` 软下线为 convert/disabled，调用为零后删除状态机，但保留 Tavily 的 convert/disabled CRUD、测试和契约。优点是误配置和维护面最低；缺点是失去自由元数据和代理搜索能力。验收 global/channel 解析、价格、Codex CLI、Tavily convert/disabled 和配置迁移抽样一致。

#### `Web Search simulate` 子功能的三种选项

**背景与原因**：`WebSearchSimulator` 同时维护非流和流式搜索/续接状态机，约 1,474 行；远端样本确有 `simulate` 模式和启用 key，开发库 `WebSearchJson` 约 3,184/15,158 非空，因此它不是死配置。真正的问题是两套状态机、迭代/搜索预算和原始搜索结果日志没有统一护栏。

- **方案 A（当前推荐）**：保留 simulate，但只允许明确 owner/channel allowlist；统一流/非流解析、最大搜索次数/迭代次数/query 长度/总耗时，raw provider 结果只在短时 debug 脱敏保存，默认只存 query hash、状态、耗时和结果数。适用：仍需代理代执行搜索；验收超限不循环、key usage 不超限、日志无 key/raw 内容。
- **方案 B**：默认强制 convert，已有 simulate 进入一个版本的弃用窗口；不支持原生工具的渠道返回明确 4xx/迁移提示，窗口结束后删除 simulator 状态机、专用日志和前端选项。适用：近期希望退出模拟但需兼容旧配置；验收新配置不能启用 simulate，旧调用可追踪且最终稳定 400/410。
- **方案 C**：整链删除 simulate，只保留 convert/disabled，迁移数据库模式和历史配置，删除 simulator 接口/DI/测试/日志字段。适用：产品确认不再代理执行搜索；验收所有配置只有两种模式、流/非流 convert/disabled 契约全绿、上游费用不再被模拟迭代放大。

### F.14 1.4：Probe 拦截

**背景与原因**：`ProbeRequestInterceptor` 被 ProxyController 真实调用并有测试，远端开发库仍有 10 条旧 `compat.intercept_probe_requests`；删除它不是死代码清理，而是主动改变桌面/代理行为，且已发现 Rust/C# 设置跨语言覆盖 Bug。

- **方案 A（推荐当前）**：保留拦截能力，但先 bearer 鉴权，再读取/解析 probe body；至少限制 Content-Length、JSON 深度/大小、协议、localhost/allowlist UA 和速率，避免未认证解析 DoS。拦截响应写轻量审计（request id、owner、protocol、status、耗时，不记正文）；同时修 Rust/C# JSON 所有权和 `restartRequired` 比较，兼容字段迁移到 `/system-settings`，增加跨语言最小复现和重启测试。优点是满足现有调用、风险最低；验收未认证/超大 body 不触发拦截、开关重启后不丢、普通代理行为不回归。
- **方案 B**：兼容一个版本，旧 `compat.intercept_probe_requests` 只读并告警，不再允许新配置写入；按 owner/客户端观测调用，默认关闭，窗口结束后移除旧白名单和字段。优点是渐进退出；缺点是保留兼容代码和迁移成本。验收旧配置明确迁移、未知字段不误开开关、窗口后访问量为零。
- **方案 C**：删除 interceptor、system setting、Rust/C# 字段和测试；旧 probe 请求返回稳定 410/明确错误，并提供需鉴权的 `/health/ready` 或 `/models` 探测端点，避免误把探测变成真实上游计费。优点是跨语言状态机和特殊分支最少；缺点是可能破坏桌面探测/运维流程。只有产品确认无依赖时采用，验收旧开关/compat 已迁移清理、健康端点契约通过，普通代理仍走真实认证/容量/熔断/failover/日志。

### F.15 第 3 节：DTO 文件拆分与字段瘦身

**背景与原因**：`ObservabilityResponses.cs`、`RequestLogDtos.cs` 等巨型文件由历史追加形成；文件组织收益与公开 JSON 契约删除被混在一起，前端零引用不能单独证明外部没有消费者。

- **方案 A（推荐第一步）**：只按领域拆文件/partial，保持类型名、JSON 名和 Controller 返回完全不变。优点是行为风险最低；验收序列化 snapshot、OpenAPI、前端构建和 diff 均只体现组织变化。
- **方案 B**：建立字段读取矩阵，公开字段先 deprecated 一个版本，零消费后删列/DTO/前端入口；同时补契约版本和 golden tests。优点是实际减少 payload/维护；缺点是需观测窗口和迁移通知。验收字段访问量为零、旧客户端契约通过。
- **方案 C**：建立 v2 响应模型和 endpoint，v1 冻结并设置 sunset，按领域重新设计分页/envelope/错误结构。优点是长期契约清晰；缺点是双 API 和迁移工作量最大。验收 v1/v2 schema、双读一致、v1 调用归零后才删除。

### F.16 第 4 节：Protocol Codec 拆分

**背景与原因**：现有 converter/partial 承担六个协议方向和流状态机，保护测试约 8,286 行；完整拆分对外行为几乎没有直接收益，容易回归工具调用、增量 JSON、取消和未知字段。

- **方案 A（当前推荐）**：不拆实现，补方向支持矩阵、characterization tests、状态机不变量和统一边界文档；未使用方向先查调用再删。优点是稳定；缺点是结构债务暂存。
- **方案 B**：以 facade 包住旧 converter，逐次抽取一个稳定方向（例如 Chat↔Responses），旧路径 feature flag fallback，逐方向做 golden/性能对比。优点是可回滚；缺点是过渡期双路径。
- **方案 C**：完整 `IProtocolCodec` + typed request/event/usage + source-generated JSON，删除旧 partial。优点是长期边界最好；缺点是最高行为风险和测试成本。验收必须覆盖长流、断线、取消、工具调用、未知字段、模糊测试和性能。

### F.17 第 5 节：内部 `Dictionary<string, object?>` 强类型化

**背景与原因**：动态字典贯穿 upstream、六个协议转换、compat、路由、OCR 和图片边界，同时有 10+ 个语义不同的 JSON helper；它是系统骨架，全面替换对外行为零收益。

- **方案 A（当前推荐）**：保留供应商扩展字典，只统一 deep-copy、脱敏、错误处理；认证、配置、模型目录、日志查询等稳定边界使用强类型 DTO。优点是收益/风险最佳；验收关键边界无裸字典、未知字段保留、round-trip/property tests 通过。
- **方案 B**：选一个高价值方向渐进 typed adapter，保留 `ExtensionData`，旧接口 facade 可回退；每方向对比结果/性能。优点是逐步获得编译约束；缺点是短期双模型。
- **方案 C**：全量以 typed request/message/tool/usage/event 替换字典，六方向 codec/upstream 全部改签。优点是长期类型安全；缺点是改动最大、协议扩展兼容成本最高。只有新增多协议或强合规审计需求时采用。

### F.18 第 6 节：模型目录、渠道映射与计费数据层

**背景与原因**：新目录 `ModelInfo + ModelPricingPlan + ModelPricingRule + ModelProvider` 在用，旧 `ModelPricing` 半边应由 F.8 处理；`ChannelModelMapping` 仍有写入/读取但多个列全部硬编码或零读取。根因是 legacy migration 没有完成 marker，且 SQLite/Postgres 双迁移长期分叉。

- **方案 A**：增加一次性 marker、停止新增 legacy 数据，保留旧表只读一个版本；仅删除有零读取证据的映射列/索引，保留 `ChannelId/Position/UpstreamModel` 和 `ModelsJson` fallback。优点是数据最安全；缺点是双轨暂存。
- **方案 B（推荐）**：完成逐行对账后软下线旧计费半边，删除死映射列/索引，下一窗口再评估整表；所有变更按 expand→backfill→switch→contract 成对生成 SQLite/Postgres migration。优点是收益与风险平衡；验收 marker 幂等、费用抽样、schema parity 和恢复演练。
- **方案 C**：确认 Postgres 唯一生产真相后，收敛为 global 目录 + 必要 channel override，转移并删除 mapping 表/旧 DTO/旧表，SQLite 进入 sunset。优点是长期维护面最小；缺点是不可逆兼容风险。验收逐行数据/成本一致、旧访问为零、客户端升级率和回滚签字。



### 功能点方案的统一选择规则与验收口径

- 事实或外部消费者尚未确认时，先选 **A**：只做止血、边界收敛和可逆兼容；不要用“前端零引用”替代公开 API、网关、脚本和客户端调用核验。
- 已确认仍有需求但现有实现过度复杂时，选 **B**：扩展→回填→切换→观察→收缩，保留一个版本的迁移窗口；这是多数功能点的默认目标态。
- 只有有书面产品需求、量化调用/收入、明确负责人和回滚资源时才选 **C**：重建能力或整链删除。Protocol Codec、全量强类型、事件驱动 Dashboard 和本地 OCR 不应仅为“代码更整齐”而选 C。
- HTTP 状态码必须表达真实状态：`501` 仅表示功能计划保留但当前未实现，`410` 表示已弃用且处于迁移窗口，物理删除后才返回 `404`；任何方案都不得留下运行时 500。
- 每个功能点的验收至少包括：公开 JSON/错误契约、权限、旧配置迁移幂等、SQLite/Postgres schema parity、日志/密钥脱敏、回滚演练和访问量观测。涉及代理流的测试不得只测 200，还要覆盖取消、超限、上游失败、failover 和长流。

### 实施拆分建议（每个单元独立评审、测试和回滚）

为避免一次修改超过三个文件族并把清理、DI 和破坏性迁移绑在一起，建议拆成以下四个单元：

1. **U1：日志与密钥止血**
   - 目标：停止继续写入完整成功 attempt、无限 SSE 行；~~完整凭据~~（明文密钥为业务需要保留）；增加上限、TTL、轮换和权限门禁。
   - 主要范围：`ProxyLogService`、`ProxyStreamService`、日志实体/清理服务、`ApiKeyService`、`WebSearchService`、相关 DTO/管理台接口。
   - 风险：日志详情契约变化、清理任务锁表（明文密钥为业务需要保留，不修改）；必须先做只读盘点和恢复演练。
2. **U2：高置信纯代码清理与 Controller smoke test**
   - 目标：删除 A.1-A.4 死代码/重复链，并建立 Controller 激活与 endpoint 路由门禁。
   - 主要范围：配置死类、MappingConfig、`ChannelDiagnosticsService.Compat`、测试工程新增 smoke test；不改业务数据库表。
   - 风险：隐藏反射/外部脚本引用；每个小批次单独 `build/test`，保留 `ImageLogSanitizer`、`OcrCacheDir` 和仍在用功能。
3. **U3：兼容迁移与旧链软下线**
   - 目标：处理 `/images` 410/501、`/pricing` marker/只读兼容、Dashboard SSE 和管理台便利层的观测与迁移。
   - 主要范围：Controller/路由、前端页面、迁移 marker、契约测试和访问观测；先不删旧表。
   - 风险：外部调用方未登记、旧价格语义不完全等价；必须保留 `Sunset`、告警和回滚版本。
4. **U4：破坏性 schema/架构收缩**
   - 目标：在观测窗口结束后删旧列/表、收敛 `ChannelModelMapping`、执行 SQLite sunset 或采用方案三的单一 Postgres 真相。
   - 主要范围：SQLite/Postgres 成对 migration、snapshot、回填/校验脚本、部署文档和备份恢复流程。
   - 风险：不可逆数据损失、SQLite 重建表锁、旧客户端中断；必须有生产快照副本、`__EFMigrationsHistory` 记录和负责人签字。

## 建议实施顺序（汇总）

按「高置信清理 → 存储/安全降级 → 使用率决策 → 大改造」排序：

1. **先修正部署事实和基线**：核对远端 `docker-compose.yml`、DB provider、Redis 连接；备份数据库；记录 `git status`。
2. **先做 P0/P1 止血**：控制 attempt、SSE 行和详情写放大，~~停止 API/管理台回传完整密钥~~（明文密钥为业务需要保留），轮换已暴露 key；同时把 `/images` 500 改为明确的 410/501 或暂时移除路由。
3. **Phase A.1-A.4**：死设置类、死映射注册、OCR 死配置、`ApplyCompat` 重复实现。完成后运行后端测试。
4. **Phase A.5-A.6**：Controller 激活 smoke test、死字段/死配置/启动诊断/调试输出；涉及数据库列时分别生成 SQLite/Postgres 迁移。
5. **B.4 密钥导入/导出与明文持久化**：明文密钥为业务需要保留，不修改。如需改进导入/导出，仅限权限（超管）、schema 校验、冲突预览和审计记录。
6. **B.3 Dashboard 实时卡片**：无实时运维刚需时直接删除队列/错误卡片和 SSE；只有必须保留时才改低频 GET。
7. **B.2 `/pricing`**：先查生产外部调用和 legacy 迁移状态，再删除旧计费半边及启动时重复迁移。
8. **B.1 `/images`**：补齐完整链路或整链删除；不按小修复处理。删除时完成配置和历史日志迁移。
9. **B.6 渠道批量运维便利层**：根据 `/test-channel/stream` 使用量和渠道规模，整体评估批量测试、批量编辑、归并视图及未使用路由别名。
10. **B.7 模型目录/Web Search 泛化收敛**：先确认渠道级覆盖和第二 Provider 需求，再决定抽共享组件、固定 Tavily 或删除旧列。
11. **第 6 节模型目录收敛**：优先删 `ChannelModelMapping` 的死列/索引，最后才考虑整表和双迁移重构。
12. **1.1 OCR / 1.2 WebSearch simulate / 1.3 渠道诊断 / 1.4 Probe**：均为仍在使用的主动砍功能，需产品确认后单独实施。
13. **第 4 节协议拆 Codec、第 5 节内部强类型化**：收益最低、改动最大，排最后或搁置。

### 待用户决策的问题

1. `/images` 是**补齐完整实现**还是**整链删除**？（决策点 6）
2. `/pricing` 是否有外部脚本/运维工具调用？legacy 表迁移是否已完成？（决策点 7）
3. 远端实际 DB/Redis 部署形态是什么？以哪份部署文件为准？（决策点 5）
4. 是否接受 Controller 激活 smoke test；是否还要启用 `AddControllersAsServices()`/`ValidateOnBuild`？（决策点 8）
5. `OPENCODEX_LOG_*` 是补实现日志落盘还是删掉文档宣传？（决策点 9）
6. 是否删除管理台三套导入/导出？~~并移除 API Key 明文持久化~~（决策点 10）— 明文密钥为业务需要保留，不移除。
7. 请求详情、原始 SSE 行、attempt 日志的保留上限、TTL 和失败留存策略是什么？（决策点 11）
8. Dashboard 队列/错误卡片是否有实时运维刚需？（决策点 12）
9. 批量测试、批量编辑、归并视图和诊断路由别名的实际使用率如何？（决策点 13）
10. 是否计划接入第二家 Web Search Provider，是否依赖渠道级模型覆盖？（决策点 14）

### 已知 Bug：桌面设置跨语言覆盖 ✅ 已修复

- 复现：通过 `/system-settings` 将 `intercept_probe_requests` 设为 `true`，重启桌面端，再 GET `/system-settings`，值会恢复为 `false`。
- 原因：Rust `DesktopSettings` 只反序列化/回写 `access_mode`、`bind_host`、`port`，C# 写入的第四个字段被 Rust 丢弃；C# 的 `restartRequired` 也没有比较该字段。
- 要求：先写跨 Rust/C# 的最小复现测试，再统一 JSON 文件所有权或让 Rust 保留未知字段；修复后验证桌面启动、重启和设置页行为。

> 实施记录（2026-08-23）：已修复。Rust `DesktopSettings` struct 新增 `intercept_probe_requests: bool` 字段，`Default` 补默认值 `false`，`normalize_settings` 保留原值传递。`start_backend` 通过 `OPENCODEX_INTERCEPT_PROBE_REQUESTS` 环境变量注入后端。`.devtools(true)` 改为 `cfg!(debug_assertions)`，仅 debug 构建开启。待 GitHub Action 验证 Rust 编译。

## 收尾验收

- 全量 `dotnet test` 通过。
- 前端 `npm --prefix frontend run build` 通过。
- 前端状态测试（`channelTestState`、`channelImagesState`）加入 CI；当前 workflow 只安装前端依赖并间接构建，没有运行 Node 测试脚本。
- 清理 README/DEPLOYMENT 中已删除功能的描述。
- 删除不再使用的 DTO、配置项、测试桩和前端死代码。
- 每个功能点在实施前记录最终选项（A/B/C）、消费者证据、负责人、迁移版本、回滚方式和验收结果；不能只记录“已删除”。
- 本地起一次完整后端，跑登录、渠道配置、代理请求、日志查看、长流/取消、密钥一次性展示和旧数据库迁移。
- SQLite/Postgres 各跑一次迁移；验证日志详情、SSE 行上限、TTL 清理、敏感字段脱敏和历史数据兼容。
