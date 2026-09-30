# 04. 领域模型与数据字典

> 需求前缀：`REQ-DAT`  
> 代码基线：`main@235da3f4`  
> 最后核对日期：2026-09-30  
> 持久化基线：SQLite 与 PostgreSQL 双迁移

## 1. 建模原则

OpenCodex 的持久化模型围绕“用户拥有资源、资源参与路由、请求产生观测和成本”组织。数据模型需要同时支持：

- 管理台配置和代理运行时查询；
- 用户级隔离与超级管理员全局视图；
- 一个主请求对应多个渠道尝试或 OCR 子请求；
- 大段正文去重、压缩、完整性校验和按需读取；
- 全局模型目录、渠道级覆盖和多层价格继承；
- SQLite 单机和 PostgreSQL 服务端的同一业务语义。

## 2. 实体关系总览

```mermaid
erDiagram
    USER ||--o{ ACCESS_API_KEY : owns
    USER ||--o{ CHANNEL : owns
    USER ||--o{ REQUEST_LOG : owns
    USER ||--o{ VISION_TRANSFER_SETTINGS : configures
    USER ||--o{ WEB_SEARCH_CONTINUATION_ENTRY : owns
    CHANNEL ||--o{ CHANNEL_MODEL_MAPPING : exposes
    CHANNEL ||--o{ CHANNEL_MODEL_INFO : overrides
    MODEL_PROVIDER ||--o{ MODEL_INFO : publishes
    MODEL_PROVIDER ||--o{ CHANNEL_MODEL_INFO : publishes
    MODEL_INFO ||--o{ MODEL_PRICING_PLAN : prices
    CHANNEL_MODEL_INFO ||--o{ MODEL_PRICING_PLAN : overrides
    CHANNEL ||--o{ MODEL_PRICING_PLAN : scopes
    MODEL_PRICING_PLAN ||--o{ MODEL_PRICING_RULE : contains
    REQUEST_LOG ||--o{ REQUEST_LOG : parent_of
    REQUEST_LOG ||--o{ REQUEST_LOG_CONTENT_REF : references
    LOG_CONTENT_MANIFEST ||--o{ REQUEST_LOG_CONTENT_REF : referenced_by
    LOG_CONTENT_MANIFEST ||--o{ LOG_CONTENT_MANIFEST_CHUNK : contains
    LOG_CONTENT_BLOCK ||--o{ LOG_CONTENT_MANIFEST_CHUNK : stores
    WEB_SEARCH_SETTINGS ||--o{ TAVILY_KEY : configures
```

## 3. 实体清单

| 实体 | 作用 | 归属/隔离 | 主要生命周期 |
|---|---|---|---|
| `User` | 管理台用户和代理租户主体 | 全局唯一用户名 | 创建、启停、删除 |
| `AccessApiKey` | 调用代理的 Bearer 凭证 | 一个用户 | 创建、启停、删除、轮换 |
| `Channel` | 上游服务连接与路由策略 | 一个用户 | 创建、编辑、启停、删除 |
| `ChannelModelMapping` | 请求模型到上游模型的映射（不含能力与价格字段） | 一个渠道 | 新增、编辑、停用、删除 |
| `ModelProvider` | 全局模型供应商 | 全局 | 创建、启停 |
| `ModelInfo` | 全局模型元数据与能力 | 全局/供应商 | 创建、编辑、停用 |
| `ChannelModelInfo` | 渠道级模型元数据覆盖 | 一个渠道 | 覆盖、恢复全局 |
| `VisionTransferSettings` | 图片识别转移的主/兜底渠道与模型 | 一个 owner（数据库唯一行） | 保存、覆盖、随渠道/用户删除清理 |
| `ModelPricingPlan` | 模型、渠道或渠道模型的价格计划 | 多层作用域 | 创建、启用/停用 |
| `ModelPricingRule` | 价格计划中的计费规则 | 一个价格计划 | 创建、编辑、启用/停用 |
| `WebSearchSettings` | Web Search 模式和全局限制 | 全局 | 单例更新 |
| `TavilyKey` | Tavily 搜索凭证和用量 | 全局 | 新增、编辑、启停、删除 |
| `WebSearchContinuationEntry` | Web Search 跨请求续传结果 | 一个用户（数据库外键级联） | 写入、按 owner 读取、清空日志时删除 |
| `ProxySetting` | 代理功能开关与系统设置的 key/value 存储 | 全局 | 新增、更新 |
| `RequestLog` | 主请求、渠道尝试或 OCR 子请求的元数据 | 一个用户 | 排队、处理、完成、清理 |
| `LogContentBlock` | 内容寻址压缩块 | 全局共享 | 写入、复用、孤立清理 |
| `LogContentManifest` | 一个完整正文的分块清单 | 全局共享 | 写入、引用、孤立清理 |
| `LogContentManifestChunk` | Manifest 到 Block 的顺序关系 | Manifest | 随 Manifest 创建/清理 |
| `RequestLogContentRef` | 日志正文槽位到 Manifest 的引用 | 一个请求日志 | 写入、替换、删除 |

历史实体 `ModelPricing` 及其表 `ModelPricings` 已由迁移 `DropChannelModelMappingDeadColumns` 删除，价格信息当前只存在于 `ModelPricingPlan` 与 `ModelPricingRule`。

## 4. 用户和凭证模型

### 4.1 User

| 字段 | 类型 | 规则 | 产品含义 |
|---|---|---|---|
| `Id` | UUID | 主键 | 用户内部标识 |
| `Username` | string | 全局唯一、非空 | 登录名和展示名 |
| `PasswordHash` | string | 非空，不保存明文密码 | PBKDF2-SHA256 哈希 |
| `Role` | string | 当前为 `superadmin` 或 `user` | 管理角色 |
| `Enabled` | bool | 默认 `true` | 停用后不能登录或调用 |
| `CreatedAt` | epoch/double | 创建时间 | 审计和展示 |
| `UpdatedAt` | epoch/double | 更新时间 | 审计和缓存失效 |

业务约束：

- 用户名大小写、空白和允许字符必须在服务端统一规范化；
- 环境超级管理员由运行时配置维护，不能通过普通用户 API 降级或删除；
- 当前登录用户不能删除自己；
- 删除用户时同步清理或明确处理其渠道、访问 Key 和日志；
- 停用用户应立即阻止新登录和代理调用；
- 已存在的 Cookie 不能绕过服务端用户启用状态校验。

### 4.2 AccessApiKey

| 字段 | 类型 | 规则 | 产品含义 |
|---|---|---|---|
| `Id` | UUID | 主键 | Key 管理对象 |
| `OwnerUserId` | UUID | 必须指向 User | 租户归属 |
| `Name` | string | 非空 | 调用用途标签 |
| `KeyHash` | string | 唯一 | Bearer 校验索引 |
| `KeyPlaintext` | string? | 当前实现存在 | 明文保存策略冲突点 |
| `KeyPrefix` | string | 用于掩码展示 | 识别 Key 类型 |
| `KeySuffix` | string | 用于掩码展示 | 末尾识别 |
| `Enabled` | bool | 默认 `true` | 即时启停 |
| `CreatedAt` | epoch/double | 必填 | 创建时间 |
| `UpdatedAt` | epoch/double | 必填 | 修改时间 |
| `LastUsedAt` | epoch/double? | 可空 | 最近成功/尝试使用时间 |

产品规则：

1. Key 前缀为 `ocx_`，随机部分由安全随机数生成；
2. 客户端使用 `Authorization: Bearer <key>`；
3. 列表默认只展示掩码和元数据；
4. 创建响应是否展示完整 Key必须与安全策略统一；
5. 停用或删除后，缓存失效必须在产品承诺的时间窗口内生效；
6. 超级管理员创建 Key时可选择启用用户作为归属；
7. 普通用户只能创建和管理自己的 Key；
8. Key不能被用于管理台 Cookie 认证。

`REQ-DAT-001`（MUST）：访问 Key 的持久化、展示、导出和轮换策略必须在产品和安全评审中统一；不得同时宣称“仅创建时可见”和“数据库保留可恢复明文”而没有标注差异。

### 4.3 凭证字段分类

| 凭证 | 当前存储/使用 | 最低产品要求 |
|---|---|---|
| 管理员密码 | PBKDF2 哈希 | 禁止明文日志和 API 返回 |
| 管理 Cookie | Data Protection 加密 Cookie | 持久化密钥目录，支持失效 |
| OpenCodex 访问 Key | 哈希 + 当前实体含明文字段 | 统一单次展示/加密存储策略 |
| 渠道上游 API Key | `Channel.ApiKey` | 管理台掩码，禁止日志透传 |
| 自定义 Header | JSON 字符串 | 按 Header 名称和日志级别脱敏 |
| Tavily Key | `TavilyKey.ApiKey` | 测试和导出需要显式权限 |
| Secret Key | 配置值，用于 Data Protection 应用名隔离 | 生产不得使用示例值 |

## 5. 渠道模型

### 5.1 Channel

| 字段 | 类型 | 规则/默认 | 作用 |
|---|---|---|---|
| `OwnerUserId` | UUID | 必填 | 资源隔离 |
| `Position` | int | 同用户内排序 | 最终稳定排序 |
| `Priority` | int | 数字越小越优先 | 候选排序主因素 |
| `Name` | string | 同用户内应唯一 | 管理台名称 |
| `GroupName` | string | 可为空/默认未分组 | 管理台归并视图 |
| `Type` | string | `responses/chat/messages/images` | 上游协议方言 |
| `BaseUrl` | string | 必填、合法 URL | 上游地址 |
| `ApiKey` | string | 由认证模式决定 | 上游凭证 |
| `AuthMode` | string | `config` 或 `none` | 是否注入上游认证 |
| `HeadersJson` | JSON | 默认 `{}` | 自定义请求头 |
| `TimeoutSeconds` | int | 正数或运行时默认 | 单次上游超时 |
| `CircuitBreakDurationSeconds` | int | 正数 | 熔断开放时间 |
| `RetryCount` | int | 非负 | 同渠道重试次数 |
| `Capacity` | int | 当前校验要求正数 | 并发槽位数 |
| `CompatJson` | JSON | 默认 `{}` | 参数、工具和历史兼容规则 |
| `ModelsJson` | JSON 数组 | 默认 `[]` | 渠道模型映射载荷：管理侧映射表的写入来源，网关路由候选也由该字段构建 |
| `Enabled` | bool | 默认 `true` | 是否参与路由 |
| `CreatedAt/UpdatedAt` | epoch/double | 必填 | 生命周期 |

渠道业务规则：

- 只有启用渠道才进入候选列表；
- `images` 渠道不参与聊天流测试；
- Images 渠道重试次数固定为 0；
- Images 渠道必须有模型映射和图片 API 方言；
- 渠道字段中的 `${ENV_NAME}`/`$ENV_NAME` 可按配置展开；
- 导入通常以用户和名称作为合并语义，不应误当作无条件全量替换；
- 删除渠道后不能再被路由、展示为活跃或写入新的 attempt；
- 熔断健康状态属于运行时状态，不应被误写成持久化启用状态。

### 5.2 ChannelModelMapping

| 字段 | 类型 | 规则 | 作用 |
|---|---|---|---|
| `ChannelId` | UUID | 必填 | 所属渠道 |
| `Position` | int | 渠道内排序 | 同模型多个映射时稳定排序 |
| `RequestModel` | string | 非空 | 客户端模型名 |
| `UpstreamModel` | string | 非空 | 上游模型名 |
| `Enabled` | bool | 默认 `true` | 是否命中 |
| `CreatedAt`/`UpdatedAt` | epoch/double | 必填 | 生命周期 |

模型映射规则：

1. 管理侧读取映射（`ModelCatalogService.ListChannelModelMappings`）优先使用 `ChannelModelMappings` 表（仅 `Enabled` 行、按 `Position` 排序）；只有当该渠道在表中没有任何启用行时，才回退解析 `Channel.ModelsJson` 中的 `model`/`upstream_model`；
2. 渠道保存与导入会按 `ModelsJson` 重写该渠道的映射表记录（`ChannelService.SyncChannelModelMappings`），网关路由候选仍从渠道行的 `ModelsJson` 快照构建；
3. 如果任一启用渠道存在模型映射，请求模型原则上必须精确命中启用映射；
4. 若所有启用渠道均无映射，系统可使用排序后的首个启用渠道并原样传递模型名；
5. 一旦进入映射模式，未配置映射的通用渠道不自动成为兜底；
6. 图片能力不再由映射表承载：`IModelCatalogService.SupportsImage` 先读渠道覆盖 `ChannelModelInfo.CapabilitiesJson.supports_image`，未声明时再读全局 `ModelInfo.CapabilitiesJson`；
7. 模型映射的请求名、上游名和能力状态必须在日志中可见。

> 旧字段 `SupportsImage`、`ModelInfoId`、`PricingMode`、`PricingPlanId` 已由迁移 `DropChannelModelMappingDeadColumns` 从映射表删除；能力、目录与价格覆盖统一由 `ChannelModelInfo` 承担，禁止回写映射表。

## 6. 模型目录和价格

### 6.1 ModelProvider

| 字段 | 类型 | 规则 |
|---|---|---|
| `Code` | string | 全局唯一，推荐小写字母、数字、点、下划线、连字符 |
| `Name` | string | 非空展示名 |
| `Enabled` | bool | 是否显示/参与目录 |
| `SortOrder` | int | 供应商展示顺序 |
| `Source` | string | 内置、用户或导入来源 |

### 6.2 ModelInfo

| 字段 | 类型 | 说明 |
|---|---|---|
| `Scope` | string | 当前仅 `global`（`ModelInfoScopes` 只定义 global；渠道级覆盖由 `ChannelModelInfo` 承担） |
| `ProviderId` | UUID | 供应商 |
| `ChannelId` | UUID? | 当前创建与导入始终写入 null（渠道级覆盖由 `ChannelModelInfo` 承担） |
| `ModelKey` | string | 对外模型标识 |
| `DisplayName` | string | 展示名 |
| `Description` | string | 描述 |
| `MatchType` | string | 服务层只接受 `exact/prefix/suffix/contains` |
| `MatchPattern` | string | 主匹配键；数组模式下取首项，兼容旧客户端 |
| `MatchPatternsJson` | JSON | 匹配键数组；仅 `exact` 允许包含多个值 |
| `CatalogJson` | JSON | Codex/客户端目录字段 |
| `CapabilitiesJson` | JSON | 图片等能力 |
| `Enabled` | bool | 删除操作实际通常是停用 |
| `Source` | string | `manual` 或 `sync`（`ModelCatalogSources`） |

### 6.3 ChannelModelInfo

渠道级模型信息按 `ChannelId + RequestModel` 唯一定位，用于覆盖请求模型在该渠道下的展示、能力、Catalog 和定价；`UpstreamModel` 只记录当前映射使用的上游模型，不作为覆盖主键。删除覆盖时恢复全局定义，而不是删除上游模型。

### 6.4 ModelPricingPlan 与 Rule

`ModelPricingPlan` 的实体字段允许携带 `ModelInfoId`、`ChannelModelInfoId` 和 `ChannelId`。当前服务实际创建和解析的组合只有：

- 全局模型计划：`ModelInfoId` 非空，`ChannelModelInfoId` 与 `ChannelId` 为空；
- 渠道模型覆盖计划：`ChannelModelInfoId` 与对应 `ChannelId` 非空，`ModelInfoId` 为空。

当前成本解析不读取 `ChannelModelMapping`（旧 `PricingMode`/`PricingPlanId` 列已删除），也没有独立的“仅绑定渠道”价格回退层。解析顺序为：

1. 按渠道与请求模型查找启用的 `ChannelModelInfo` 及其计划（渠道覆盖，`FindPlanForChannelModel`）；渠道覆盖未命中计划时继续走全局解析；
2. 按请求模型（未命中且与上游名不同时再按上游模型）查找启用的全局 `ModelInfo`，匹配顺序为 `exact → prefix → suffix → contains`，再取该模型的全局计划（`FindPlanForModel`），上游模型命中记为 `global_model_match_upstream_fallback`；
3. 命中渠道覆盖但覆盖没有启用计划时，当前实现回退全局模型计划（`ModelCatalogServiceTests.CalculateCostFallsBackToGlobalPricingWhenChannelModelInfoHasNoPricing` 覆盖该行为），不生成零成本快照；
4. 只有“无模型/无计划命中”（`model_not_matched`）或“计划存在但没有启用规则”（`pricing_plan_has_no_rules`）时，成本才落为 0，并写入对应 `Resolution` 原因。

每个计划包含多个计费规则，当前计费项包括：

- `input`；
- `output`；
- `cache_write`；
- `cache_read`。

计费模式包括：

- `per_request`：按请求计费；
- `per_million_tokens`：按百万 Token；
- `tiered_tokens`：按阶梯 Token。

峰谷计费由计划和规则共同定义：

- `ModelPricingPlan.TimeZoneId`（IANA 时区 ID，空串表示未启用）与 `ModelPricingPlan.OffPeakWindowsJson`（规范化后的谷段窗口，不跨午夜）；
- `ModelPricingRule.OffPeakEnabled`、`OffPeakUnitPrice`、`OffPeakTiersJson`；规则未启用峰谷时谷段沿用基础单价与阶梯。

时段判定由 `PricingWindowCalendar.Evaluate` 在每个请求按计费时刻现算（不得缓存时段结果），命中谷段且规则启用时使用谷段单价/阶梯。定价快照必须保存币种、单位价格、阶梯 JSON、启用状态、来源、相位与时段来源（`PricingPhases`/`PricingPhaseSources`），避免未来改价重算历史成本。

## 7. Web Search 模型

### 7.1 WebSearchSettings

| 字段 | 取值 | 说明 |
|---|---|---|
| `Mode` | `simulate` / `convert` / `disabled` | 全局 Web Search 处理策略；缺失或非法持久值在读取执行路径回退为 `convert` |
| `KeyUsageLimit` | 正整数 | 无设置行时 API 使用默认值 1000；保存/导入接口要求大于 0 |
| `CreatedAt/UpdatedAt` | 时间 | 配置生命周期 |

当前配置只允许超级管理员读取和修改。普通用户不能为自己选择模式。

### 7.2 TavilyKey

| 字段 | 说明 |
|---|---|
| `Position` | Key 选择顺序 |
| `Provider` | 当前主要为 `tavily` |
| `ApiKey` | 第三方搜索凭证 |
| `Enabled` | 是否可选 |
| `UsageCount` | 已使用次数 |
| `UsageLimit` | 单 Key 上限 |
| `CreatedAt/UpdatedAt` | 生命周期 |

可用 Key 定义为：启用且 `UsageCount < UsageLimit`。达到上限的 Key 不得继续用于模拟搜索。

### 7.3 WebSearchContinuationEntry

用于跨请求恢复代理内置 Web Search 的工具结果：

| 字段 | 说明 |
|---|---|
| `OwnerUserId` | 所属用户；用户删除时级联清理 |
| `EntryKey` | 搜索项 ID 或 `client:<call_id>` |
| `Kind` | `search` 或 `client-round` |
| `PayloadVersion` | 结果 JSON 版本 |
| `PayloadJson` | 完整搜索结果或客户端调用映射 |
| `CreatedAt` | 创建时间 |

该表以数据库为唯一真源，不使用 Web Search 专用内存或 Redis 缓存，也不运行后台过期清理。`OwnerUserId` 配置了指向 `Users` 的数据库外键并级联删除。生命周期跟随“清除全部日志”：`ObservabilityService.ClearLogs` 在同一显式事务中按序删除续传记录、内容引用、Manifest 分块、请求日志、Manifest 与内容块。

## 8. 请求日志模型

### 8.1 RequestLog

| 字段组 | 字段 | 产品含义 |
|---|---|---|
| 标识 | `Id`、`RequestId` | 数据库 ID 与调用链 ID |
| 时间 | `CreatedAt`、`ProcessingStartedAt`、`CompletedAt` | 请求生命周期 |
| HTTP | `Method`、`Path`、`ClientIp` | 入口信息 |
| 模型 | `Model`、`UpstreamModel` | 请求/上游模型 |
| 渠道 | `ChannelId` | 命中渠道 |
| 类型 | `RequestType` | `main`、`attempt`、`ocr`、`diagnostic` |
| 父子 | `ParentRequestLogId` | 主请求与子请求关联 |
| 会话 | `ConversationKey`、`ConversationTurnId`、`ConversationWindowId`、`PreviousResponseId` | Codex/会话链路 |
| 流式 | `IsStream`、`TtftMs` | 是否流式和首字延迟 |
| 结果 | `DurationMs`、`StatusCode`、`LifecycleStatus`、`Error` | 状态和错误 |
| Usage | `InputTokens`、`CachedTokens`、`CacheWriteTokens`、`CacheReadTokens`、`OutputTokens` | Token 统计 |
| 计费 | `Cost`、`CostCurrency`、`PricingModelInfoId`、`PricingPlanId`、`PricingSnapshotJson` | 成本和计算依据 |
| 归属 | `OwnerUserId`、`ApiKeyId` | 租户和调用凭证 |

主请求生命周期：

```mermaid
stateDiagram-v2
    [*] --> queued
    queued --> processing: 开始路由
    processing --> success: 获得可接受响应
    processing --> failed: 最终失败
    success --> [*]
    failed --> [*]
```

当前持久化状态常量只有 `queued`、`processing`、`success`、`failed`。客户端取消没有独立的 `cancelled` 状态；流式取消会留下错误文本，并按现有完成判定落为 `failed`。

请求类型语义：

- `main`：客户端可见的一次完整请求；
- `attempt`：主请求选择某个渠道的一次尝试，可有多个；
- `ocr`：为图片降级生成的内部视觉识别请求；
- `diagnostic`：管理台渠道测试/模型发现产生的日志，不计入业务统计；
- 子日志必须通过 `ParentRequestLogId` 可回到主请求；
- attempt 失败不等于主请求失败，最终状态以主请求为准。

### 8.2 内容寻址实体

| 实体 | 关键字段 | 规则 |
|---|---|---|
| `LogContentBlock` | `Sha256`、`RawLength`、`StoredLength`、`Compression`、`Data` | 相同哈希复用；压缩后更小时保存 Brotli |
| `LogContentManifest` | `Sha256`、`RawLength`、`ChunkCount`、`Encoding` | 描述一个完整正文 |
| `LogContentManifestChunk` | `ManifestId`、`Ordinal`、`BlockId`、`RawLength` | 按序重组正文 |
| `RequestLogContentRef` | `RequestLogId`、`Slot`、`ManifestId` | 将请求日志槽位映射到正文 |

当前持久化枚举定义 7 个槽位（`1`–`7`），枚举值属于数据库契约，只能追加：

| 枚举值 | 槽位 | 内容 |
|---:|---|---|
| 1 | `RequestHeaders` | 客户端请求头 |
| 2 | `RequestBody` | 原始客户端请求正文或序列化后的入口载荷 |
| 3 | `UpstreamRequestBody` | 转换后的上游请求正文 |
| 4 | `UpstreamResponseBody` | 上游响应正文 |
| 5 | `ResponseBody` | 客户端响应或错误响应正文 |
| 6 | `WebSearchJson` | Web Search 模拟详情 |
| 7 | `OcrJson` | OCR 元数据 |

> 历史槽位 `8`（`StreamLinesJson`）已废弃并从枚举移除：流式请求不再保存原始 SSE 行集合，
> 只在失败或断流时把最后一行数据行与终止原因写入 `RequestLog.Error`。
> 历史数据需用一次性命令 `--cleanup-legacy-stream-lines` 清理。

内容存储必须满足：

1. 写入与引用更新使用事务；
2. 读取时校验哈希和分块顺序；
3. 替换引用后清理不再被引用的 Manifest/Block；
4. 删除日志不能破坏仍被其他日志引用的共享块；
5. 内容损坏时返回可诊断错误，不返回静默截断正文。

## 9. 约束和索引

当前数据库索引重点包括：

- User.Username 唯一；
- AccessApiKey.KeyHash 唯一；
- Channel 按 Owner + Position、Owner + Priority + Position；
- ChannelModelMapping 按 Channel + Position、Channel + RequestModel、Enabled 建索引；
- ChannelModelInfo 按 Channel + RequestModel 唯一；Channel + UpstreamModel、ProviderId、Enabled、MatchPattern、MatchType 使用普通索引；
- ModelProvider.Code 唯一，Enabled、SortOrder 建索引；
- ModelInfo 按 Scope + ProviderId + ModelKey、Scope + ChannelId + ModelKey、ProviderId、ChannelId、Enabled、MatchPattern、MatchType 建索引；
- ModelPricingPlan 按 ModelInfoId、ChannelModelInfoId、ChannelId、Enabled 建索引；ModelPricingRule 按 PricingPlanId、BillingItem、Enabled 建索引，`UnitPrice` 与 `OffPeakUnitPrice` precision `(18,8)`；
- VisionTransferSettings.OwnerUserId 唯一；ProxySettings.Key 唯一；
- WebSearchContinuationEntries 按 Owner + EntryKey 唯一，并带指向 `Users` 的级联外键；
- RequestLog 按创建时间、模型、上游模型、渠道、定价模型、定价计划、类型、状态、父 ID、会话字段、路径、状态码、Key、Owner + Id；
- LogContentBlock.Sha256 唯一；
- LogContentManifest.Sha256 唯一。

数据库外键当前只覆盖内容寻址日志与 Web Search 续传记录：删除 `RequestLogs` 级联删除 `RequestLogContentRefs`，删除 `LogContentManifests` 级联删除 `LogContentManifestChunks`，`LogContentManifestChunks.BlockId` 与 `RequestLogContentRefs.ManifestId` 为 Restrict，`WebSearchContinuationEntries.OwnerUserId` 为级联。其余业务关系（渠道模型信息、价格计划、视觉转移设置等）没有数据库外键，引用清理依赖服务层：`ChannelService.DeleteChannelAsync` 删除该渠道的 `ChannelModelMappings` 并清理 `VisionTransferSettings` 引用，`UserService.DeleteUser` 批量删除访问 Key、渠道与视觉转移配置；渠道删除不清理 `ChannelModelInfos`/`ModelPricingPlans` 的渠道记录，其去留策略为 `TBD`。

`REQ-DAT-002`（MUST）：所有租户资源查询必须在数据库查询或服务层使用 Owner/User 约束，不能只在前端隐藏记录。

`REQ-DAT-003`（MUST）：历史请求成本必须使用完成请求时的价格快照，不得因后续修改模型价格而改变历史账单。

`REQ-DAT-004`（MUST）：删除或停用操作必须明确区分软删除、硬删除和恢复语义；模型“删除”若实际是停用，产品文案和验收必须统一使用“停用”。

`REQ-DAT-005`（SHOULD）：凭证和日志正文应支持加密存储或外部密钥管理；当前明文字段和导出能力必须进入安全风险评审。

`REQ-DAT-006`（MUST）：渠道模型映射只承载 `RequestModel → UpstreamModel` 的有序映射；`SupportsImage`、`ModelInfoId`、`PricingMode`、`PricingPlanId` 已由迁移 `DropChannelModelMappingDeadColumns` 删除，能力、目录与价格覆盖统一由 `ChannelModelInfo`/`ModelInfo` 承担，禁止回写映射表。

`REQ-DAT-007`（MUST）：成本解析必须先按渠道与请求模型命中 `ChannelModelInfo` 覆盖；命中覆盖但不存在启用价格计划时必须回退全局 `ModelInfo` 计划，不得静默生成零成本快照；只有“无模型/无计划命中”或“计划无启用规则”才允许零成本，并必须保留 `Resolution` 原因供账单解释。

`REQ-DAT-008`（MUST）：峰谷计费由 `ModelPricingPlan.TimeZoneId`/`OffPeakWindowsJson` 与 `ModelPricingRule.OffPeakEnabled`/`OffPeakUnitPrice`/`OffPeakTiersJson` 定义；时段必须按每请求的计费时刻现算（不得缓存时段结果），并把相位与时段来源写入定价快照。

`REQ-DAT-009`（MUST）：Web Search 跨请求续传以 `WebSearchContinuationEntries` 为唯一真源，`OwnerUserId` 通过数据库外键级联删除；清空全部日志必须在同一事务内删除续传记录与内容寻址日志六类表，`ObservabilityService.ClearLogs` 已按该契约实现。

`REQ-DAT-010`（MUST）：`VisionTransferSettings` 每个 owner 最多一行且 `PrimaryChannelId`/`PrimaryModel` 必填、兜底两列同时为空或同时非空（服务层不变式）；`ProxySetting` 以 `Key` 唯一存储全局开关；删除用户或渠道时必须清理相关视觉转移配置。

## 10. 数据生命周期

### 10.1 用户删除

当前实现（HTTP 入口 `UsersController.DeleteUser` 要求超级管理员；`UserService.DeleteUser` 额外禁止删除当前用户）：

- 访问 Key：按 owner 批量 `ExecuteDelete`；
- 渠道：按 owner 批量删除，但该路径不顺带清理渠道的 `ChannelModelMappings`、`ChannelModelInfos` 与 `ModelPricingPlans`；
- 视觉转移设置：按 owner 批量删除；
- Web Search 续传记录：由 `Users` 外键级联删除；
- 请求日志元数据与正文引用：当前不清理，历史统计继续可查；
- 会话 Cookie：用户被删除后立即失效。

仍待定义（`TBD`）：

- 日志元数据（可识别信息）的保留、匿名化或删除；
- 用户删除后遗留的渠道级模型信息与价格计划去留策略。

### 10.2 日志清理

当前提供超级管理员清空日志的入口（`ObservabilityService.ClearLogs` 在同一显式事务中原子清理 Web Search 续传记录、内容引用、Manifest 分块、请求日志、Manifest 与内容块），但没有产品化保留期。正式规则至少需要定义：

- 元数据和正文是否同一保留期；
- 是否支持按时间、用户、容量清理；
- 清理前是否需要导出或二次确认；
- 清理过程中如何避免共享块悬挂；
- 清理对统计结果的影响；
- 是否记录清理操作审计。

### 10.3 价格变更

1. 管理员新增或修改价格计划；
2. 新请求使用最新有效计划；
3. 完成请求生成定价快照；
4. 历史日志保留原快照；
5. 删除/停用旧计划不应使历史成本失去解释。

## 11. 数据验收标准

| 编号 | 验收 |
|---|---|
| `AC-DAT-01` | 创建用户后只能在其 Owner 范围内看到渠道、Key 和日志 |
| `AC-DAT-02` | 删除/停用 Key 后缓存和实际鉴权均在约定窗口内失效 |
| `AC-DAT-03` | 一个主请求可关联多个 attempt 和 OCR 子日志 |
| `AC-DAT-04` | 日志详情可从内容引用重建原始正文并校验哈希 |
| `AC-DAT-05` | 相同正文不会重复存储相同内容块 |
| `AC-DAT-06` | 删除日志不会删除仍被其他日志引用的共享内容 |
| `AC-DAT-07` | 价格变更不改变已有日志成本 |
| `AC-DAT-08` | SQLite 与 PostgreSQL 的实体约束和业务结果一致 |
| `AC-DAT-09` | 模型级覆盖删除后可恢复全局模型信息 |
| `AC-DAT-10` | 用户删除、停用和超级管理员保护规则均有测试 |

## 12. 源码和测试追溯

| 模型区域 | 源码 |
|---|---|
| 用户、Key、渠道 | `opencodex_proxy/src/Libraries/OpenCodex.Domain/Domain/`、`OpenCodex.Core/Services/UserService.cs`、`ChannelService.cs` |
| 模型和价格 | `ModelInfo.cs`、`ChannelModelInfo.cs`、`ModelPricingPlan.cs`、`ModelPricingRule.cs`、`ModelCatalogService.cs`、`ModelCatalogSyncService.cs` |
| 视觉转移与代理设置 | `VisionTransferSettings.cs`、`ProxySetting.cs`、`VisionTransferSettingsService.cs`、`ProxySettingsService.cs` |
| Web Search | `WebSearchSettings.cs`、`TavilyKey.cs`、`WebSearchContinuationEntry.cs`、`WebSearchContinuationStore.cs` |
| 日志实体 | `RequestLog.cs`、`LogContent.cs` |
| EF 模型 | `opencodex_proxy/src/Libraries/OpenCodex.Data/OpenCodexDbContextBase.cs` |
| 迁移 | `opencodex_proxy/src/Libraries/OpenCodex.Data/Migrations/` |
| 内容编码 | `OpenCodex.Core/Services/Proxy/LogContentCodec.cs` |
| 内容存储 | `OpenCodex.Core/Services/Proxy/LogContentStore.cs` |
| 日志写入与清理 | `OpenCodex.Core/Services/Proxy/ProxyLogService.cs`、`OpenCodex.Core/Services/ObservabilityService.cs`、`OpenCodex.Core/Services/LogMaintenance/StreamLineLogCleanupService.cs` |
| 相关测试 | `LogContentCodecTests.cs`、`LogContentStoreTests.cs`、`ObservabilityServiceTests.cs`、`ModelCatalogServiceTests.cs`、`VisionTransferSettingsServiceTests.cs`、`ProxySettingsServiceTests.cs`、`WebSearchContinuationStoreTests.cs` |
