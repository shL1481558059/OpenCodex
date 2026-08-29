# 18. 需求追踪索引

> 文档类型：PRD 需求—页面—接口—数据—源码—测试追踪矩阵  
> 代码基线：`main@3827590eb33acb67dd063054c4a36d2b87b09002`  
> 生成日期：2026-08-17  
> 适用范围：`prd/README.md` 与 `prd/01`～`17`  
> 状态说明：本索引证明需求已被文档化和定位；标记为 GAP/TBD 的需求仍需实现或决策，不代表当前代码已经满足

## 1. 追踪规则

每条需求至少应能回答：

1. 需求在哪篇 PRD 中定义；
2. 哪个用户角色、页面或 API 能观察到该需求；
3. 哪个数据实体或运行时状态承载该需求；
4. 哪个源码模块是当前事实来源；
5. 哪类测试或运行证据可以证明验收；
6. 当前是已实现、部分实现、缺口还是待决策。

证据强度从高到低为：运行态/E2E、真实集成测试、单元/组件测试、当前源码与迁移、管理台实现、说明文档。只存在 PRD 描述而无源码或测试的项目必须视为 GAP/TBD。

## 2. 需求族覆盖总览

| 需求族 | 文档 | 数量 | 主要可观察面 | 主要实现/测试证据 |
|---|---|---:|---|---|
| `REQ-OV` | [产品总览](01-product-overview.md) | 7 | 全局产品边界与高层验收 | Program.cs；README.md；全部专题测试 |
| `REQ-USR` | [用户与权限](02-users-and-permissions.md) | 30 | /users、/api-keys、/channels、/logs；App/Users/AccessKeys | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-SYS` | [系统边界](03-system-boundary.md) | 15 | /、/health、管理 API、代理 API；Web/Docker/Tauri | Program/Hosting；SystemController；src-tauri；Compose；启动测试 |
| `REQ-DAT` | [领域模型](04-domain-model.md) | 5 | User、Channel、Model、Pricing、RequestLog、LogContent | Domain；OpenCodexDbContextBase；Migrations；数据/日志测试 |
| `REQ-AUTH` | [初始化与认证](05-initialization-and-auth.md) | 36 | /setup/status、/setup、/session、/login、/logout、/api-keys | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-CH` | [渠道管理](06-channel-management.md) | 20 | /channels、discover-models、test-channel；Channels 页面 | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-RTE` | [路由与可靠性](07-routing-and-reliability.md) | 25 | /models 与三协议代理入口 | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-PRT` | [协议转换](08-protocol-conversion.md) | 27 | Responses、Chat、Messages 的 3×3 请求/响应/SSE | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-SPC` | [特殊流程](09-tools-multimodal-and-special-flows.md) | 22 | /web-search、/images/*、三协议代理、system-settings | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-UI` | [管理台](10-admin-console.md) | 46 | /admin/ 全部页面与管理 API | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-OBS` | [可观测性与计费](11-observability-and-billing.md) | 20 | /logs、/stats、实时 SSE；Dashboard/Logs/Pricing | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-CFG` | [配置](12-configuration.md) | 26 | 环境变量、system-settings、渠道配置、Compose/Tauri | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-NFR` | [非功能要求](13-non-functional-requirements.md) | 32 | 性能、可靠性、安全、兼容性、可维护性 | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-MIG` | [数据与迁移](14-data-and-migrations.md) | 34 | SQLite/PostgreSQL、启动迁移、备份恢复 | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-REL` | [部署与发布](15-deployment-and-release.md) | 20 | Docker、远程部署、Tauri、GitHub Actions | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-TST` | [测试与验收](16-testing-and-acceptance.md) | 18 | 单元、集成、E2E、性能、发布门禁 | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-RSK` | [风险与决策](17-known-limitations-and-risks.md) | 17 | 安全、数据、功能、运维、文档漂移 | 风险证据文件；修复验证或书面接受记录 |

当前索引共收录 **400** 条专题需求。数量用于完整性检查，不等同“已实现需求数”。

## 3. 身份、角色与资源追踪

| 身份/角色 | 认证载体 | 可访问产品面 | 核心实体 | 主要源码 | 必测证据 |
|---|---|---|---|---|---|
| 未初始化操作者 | 无 | setup status、首次 setup | User、桌面设置文件 | AuthController、AuthService、DesktopSystemSettingsStore | SetupRoutes、首次启动 E2E |
| 未登录访客 | 无 | 根路径、浅 health、登录、会话查询 | 无/会话 | SystemController、AuthController | 公开接口与拒绝矩阵 |
| 普通用户 | 管理 Cookie | 自己的渠道、Key、日志、统计 | User、Channel、AccessApiKey、RequestLog | WorkContext、各管理 Service | 跨用户 ID 越权测试 |
| 超级管理员 | 管理 Cookie | 全部用户资源和全局配置 | 全部管理实体 | RequireSuperadmin、管理 Service | 管理权限和保护规则测试 |
| API 调用方 | Bearer `ocx_...` | models、Responses、Chat、Messages、Images | AccessApiKey、Channel、RequestLog | ProxyAccessService、ProxyController | 有效/无效/停用 Key测试 |
| AI 上游 | 渠道认证 | 接收转换后请求并返回响应 | Channel、ChannelModelMapping | HttpUpstreamClient、ProtocolConverter | 上游集成/协议矩阵 |
| Tavily | Tavily Key | simulate Web Search | WebSearchSettings、TavilyKey | TavilyWebSearchClient、WebSearchSimulator | 搜索模式与故障测试 |

## 4. HTTP 接口追踪矩阵

| 接口 | 身份 | 产品专题 | 主要实现 | 主要验收证据 |
|---|---|---|---|---|
| `GET /` | 公开 | 系统边界 | SystemController | 服务启动冒烟 |
| `GET /health` | 公开 | 系统边界、NFR、风险 | SystemController | 存活测试；readiness 当前为 GAP |
| `GET /setup/status` | 公开 | 初始化认证 | AuthController、AuthService | SetupRoutes/首次启动 E2E |
| `POST /setup` | 仅未初始化可成功 | 初始化认证 | AuthController、AuthService、DesktopSystemSettingsStore | 重复初始化、原子性、Tauri 重启测试 |
| `GET /session` | Cookie 可选 | 初始化认证、管理台 | AuthController、SessionService | 有效/失效/停用用户会话测试 |
| `POST /login` | 用户名密码 | 初始化认证 | AuthController、AuthService | 密码、停用、环境管理员、限流缺口 |
| `POST /logout` | Cookie 可选 | 初始化认证 | AuthController、SessionState | Cookie 清除和失败恢复 |
| `GET/POST/PATCH/DELETE /users*` | 超级管理员 | 用户权限、管理台 | UsersController、UserService | 保护管理员、当前用户、级联资源测试 |
| `GET/POST/PATCH/DELETE /api-keys*` | 管理 Cookie | 用户权限、认证、管理台 | ApiKeysController、ApiKeyService | Owner、明文策略、停用失效测试 |
| `GET /channels` | 管理 Cookie | 渠道管理 | ChannelController、ChannelService | 用户范围与敏感字段测试 |
| `POST/PUT/DELETE /channels*` | 管理 Cookie | 渠道管理 | ChannelController、ChannelService | CRUD、Owner、校验、缓存失效 |
| `PATCH /channels/batch` | 管理 Cookie | 渠道管理、管理台 | ChannelController、ChannelService | 部分字段、Images 限制、原子性 |
| `POST /channels/bulk-import` | 管理 Cookie | 渠道管理、风险 | ChannelController、ChannelService | 合并键、冲突、秘密导入测试 |
| `POST /channels/{id}/reset-health` | 管理 Cookie | 渠道、可靠性 | ChannelController、CircuitBreaker | Owner 与状态重置测试 |
| `POST /channels/discover-models` | 管理 Cookie | 渠道管理 | ChannelDiagnosticsController/Service | 临时草稿、脱敏、上游错误 |
| `POST /channels/test/stream` | 管理 Cookie | 渠道、协议、UI | ChannelDiagnosticsService | SSE 成功/空事件/失败/取消 |
| `GET/POST /model-providers*` | 读用户/写超管 | 模型、权限、UI | ModelCatalogController/Service | Provider 校验与权限 |
| `GET/POST/PATCH/DELETE /model-infos*` | 读用户/写超管 | 模型、价格 | ModelCatalogController/Service | 匹配、Catalog、停用、播种 |
| `GET/PUT/DELETE /channels/{id}/model-infos*` | 渠道 Owner/超管 | 渠道模型覆盖 | ModelCatalogController/Service | Owner、覆盖、恢复全局 |
| `GET/POST/PATCH/DELETE /pricing*` | 超级管理员 | 计费 | PricingController、ModelPricingService | 规则公式、播种、历史快照 |
| `GET /model-catalog/export` | 超级管理员 | 模型目录、备份迁移 | ModelCatalogController/Service | 全量导出、不含渠道覆盖 |
| `POST /model-catalog/import` | 超级管理员 | 模型目录、备份迁移 | ModelCatalogController/Service | dryRun 预检、事务导入、重复键拒绝 |
| `GET/POST /web-search*` | 超级管理员 | 特殊流程、UI | WebSearchController/Service | 三模式、Key 用量、导入测试 |
| `POST /web-search/test-key` | 超级管理员 | 特殊流程 | WebSearchService、TavilyClient | 成功、超时、无效 Key |
| `GET /logs` | 管理 Cookie | 可观测性 | ObservabilityController/Service | Owner、过滤、分页、大范围 |
| `GET /log-filter-options` | 管理 Cookie | 可观测性、UI | ObservabilityController/Service | 联想、防抖、Owner |
| `GET /logs/{id}` | 管理 Cookie | 可观测性 | ObservabilityController/Service、LogContentStore | 跨用户 ID、内容损坏、父子导航 |
| `DELETE /logs` | 超级管理员 | 可观测性、风险 | ObservabilityController/Service | 清理事务、共享块、二次确认 |
| `GET /stats` | 管理 Cookie | 仪表盘、计费 | ObservabilityService | 当前默认排除 attempt 但包含 OCR；时间桶、Token/成本口径测试 |
| `GET /stats/active-channels*` | 管理 Cookie | 仪表盘、可靠性 | ObservabilityController/Service | SSE 断开、Owner、数据新鲜度 |
| `GET /stats/recent-errors/stream` | 管理 Cookie | 仪表盘 | ObservabilityController/Service | SSE 重连、详情权限 |
| `GET/PUT /system-settings` | 超级管理员 | 配置、桌面、Probe | SystemSettingsController、DesktopSystemSettingsStore | 端口、LAN、重启、字段往返 |
| `GET /models`、`GET /v1/models` | Bearer Key | 路由、模型目录 | ProxyController、RouteService、CodexCatalogFactory | Codex/普通客户端目录测试 |
| `POST /responses`、`/v1/responses` | Bearer Key | 协议、工具、路由 | ProxyController、ProxyEndpointService | 3×3 非流式/流式矩阵 |
| `POST /chat/completions`、`/v1/chat/completions` | Bearer Key | 协议、工具、路由 | ProxyController、ProxyEndpointService | 3×3 非流式/流式矩阵 |
| `POST /messages`、`/v1/messages` | Bearer Key | 协议、工具、路由 | ProxyController、ProxyEndpointService | 3×3 非流式/流式矩阵 |
| `POST /images/generations*` | Bearer Key | 特殊流程 | ImagesController、Images Service 接口 | JSON、stream 拒绝、生产 DI GAP |
| `POST /images/edits*` | Bearer Key | 特殊流程 | ImagesController、ImageEditRequestReader | multipart、大小/数量、生产 DI GAP |

### 4.1 控制器精确路由目录

以下逐项保留当前 Controller Attribute 中的 HTTP 方法、路径参数、兼容别名、认证面和动作名。任何新增、删除或改名都必须同步接口测试和本索引。

| 方法 | 精确路径 | 身份/权限 | 控制器动作 |
|---|---|---|---|
| `GET` | `/api-keys` | 管理 Cookie，服务层限定资源范围 | `ApiKeysController.ApiKeys` |
| `POST` | `/api-keys` | 管理 Cookie，服务层限定资源范围 | `ApiKeysController.CreateApiKey` |
| `DELETE` | `/api-keys/{keyId:guid}` | 管理 Cookie，服务层限定资源范围 | `ApiKeysController.DeleteApiKey` |
| `POST` | `/api-keys/import` | 管理 Cookie，服务层限定资源范围 | `ApiKeysController.ImportApiKeys` |
| `PATCH` | `/api-keys/{keyId:guid}` | 管理 Cookie，服务层限定资源范围 | `ApiKeysController.UpdateApiKey` |
| `POST` | `/login` | 公开或 Cookie 可选 | `AuthController.Login` |
| `POST` | `/logout` | 公开或 Cookie 可选 | `AuthController.Logout` |
| `GET` | `/session` | 公开或 Cookie 可选 | `AuthController.Session` |
| `POST` | `/setup` | 公开或 Cookie 可选 | `AuthController.Setup` |
| `GET` | `/setup/status` | 公开或 Cookie 可选 | `AuthController.SetupStatus` |
| `POST` | `/channels/discover-models` | 管理 Cookie，服务层限定资源范围 | `ChannelDiagnosticsController.DiscoverModels` |
| `POST` | `/discover-models` | 管理 Cookie，服务层限定资源范围 | `ChannelDiagnosticsController.DiscoverModels` |
| `POST` | `/channels/test/stream` | 管理 Cookie，服务层限定资源范围 | `ChannelDiagnosticsController.TestChannelStream` |
| `POST` | `/test-channel/stream` | 管理 Cookie，服务层限定资源范围 | `ChannelDiagnosticsController.TestChannelStream` |
| `PATCH` | `/channels/batch` | 管理 Cookie，服务层限定资源范围 | `ChannelController.BatchUpdateChannels` |
| `GET` | `/channels` | 管理 Cookie，服务层限定资源范围 | `ChannelController.Channels` |
| `POST` | `/channels` | 管理 Cookie，服务层限定资源范围 | `ChannelController.CreateChannel` |
| `DELETE` | `/channels/{channelId:guid}` | 管理 Cookie，服务层限定资源范围 | `ChannelController.DeleteChannel` |
| `POST` | `/channels/bulk-import` | 管理 Cookie，服务层限定资源范围 | `ChannelController.BulkImportChannels` |
| `POST` | `/channels/{channelId:guid}/reset-health` | 管理 Cookie，服务层限定资源范围 | `ChannelController.ResetChannelHealth` |
| `PUT` | `/channels/{channelId:guid}` | 管理 Cookie，服务层限定资源范围 | `ChannelController.UpdateChannel` |
| `POST` | `/images/edits` | Bearer 访问 Key | `ImagesController.Edits` |
| `POST` | `/v1/images/edits` | Bearer 访问 Key | `ImagesController.Edits` |
| `POST` | `/images/generations` | Bearer 访问 Key | `ImagesController.Generations` |
| `POST` | `/v1/images/generations` | Bearer 访问 Key | `ImagesController.Generations` |
| `GET` | `/channels/{channelId:guid}/model-infos` | 管理 Cookie，服务层限定资源范围 | `ModelCatalogController.ChannelModels` |
| `POST` | `/model-infos` | 超级管理员 Cookie | `ModelCatalogController.CreateModel` |
| `POST` | `/model-providers` | 超级管理员 Cookie | `ModelCatalogController.CreateProvider` |
| `DELETE` | `/model-infos/{id:guid}` | 超级管理员 Cookie | `ModelCatalogController.DeleteModel` |
| `GET` | `/model-infos` | 管理 Cookie，服务层限定资源范围 | `ModelCatalogController.Models` |
| `GET` | `/model-providers` | 管理 Cookie，服务层限定资源范围 | `ModelCatalogController.Providers` |
| `DELETE` | `/channels/{channelId:guid}/model-infos/{id:guid}` | 管理 Cookie，服务层限定资源范围 | `ModelCatalogController.RestoreChannelModel` |
| `PATCH` | `/model-infos/{id:guid}` | 超级管理员 Cookie | `ModelCatalogController.UpdateModel` |
| `PUT` | `/channels/{channelId:guid}/model-infos` | 管理 Cookie，服务层限定资源范围 | `ModelCatalogController.UpsertChannelModel` |
| `GET` | `/model-catalog/export` | 超级管理员 Cookie | `ModelCatalogController.ExportCatalog` |
| `POST` | `/model-catalog/import` | 超级管理员 Cookie | `ModelCatalogController.ImportCatalog` |
| `GET` | `/stats/active-channels` | 管理 Cookie，服务层限定资源范围 | `ObservabilityController.ActiveChannels` |
| `GET` | `/stats/active-channels/stream` | 管理 Cookie，服务层限定资源范围 | `ObservabilityController.ActiveChannelsStream` |
| `DELETE` | `/logs` | 超级管理员 Cookie | `ObservabilityController.ClearLogs` |
| `GET` | `/logs/{logId:guid}` | 管理 Cookie，服务层限定资源范围 | `ObservabilityController.LogDetail` |
| `GET` | `/log-filter-options` | 管理 Cookie，服务层限定资源范围 | `ObservabilityController.LogFilterOptions` |
| `GET` | `/logs` | 管理 Cookie，服务层限定资源范围 | `ObservabilityController.Logs` |
| `GET` | `/stats/recent-errors/stream` | 管理 Cookie，服务层限定资源范围 | `ObservabilityController.RecentErrorsStream` |
| `GET` | `/stats` | 管理 Cookie，服务层限定资源范围 | `ObservabilityController.Stats` |
| `POST` | `/pricing` | 超级管理员 Cookie | `PricingController.CreatePrice` |
| `DELETE` | `/pricing/{id:guid}` | 超级管理员 Cookie | `PricingController.DeletePrice` |
| `GET` | `/pricing` | 超级管理员 Cookie | `PricingController.Prices` |
| `PATCH` | `/pricing/{id:guid}` | 超级管理员 Cookie | `PricingController.UpdatePrice` |
| `POST` | `/chat/completions` | Bearer 访问 Key | `ProxyController.ChatCompletions` |
| `POST` | `/v1/chat/completions` | Bearer 访问 Key | `ProxyController.ChatCompletions` |
| `POST` | `/messages` | Bearer 访问 Key | `ProxyController.Messages` |
| `POST` | `/v1/messages` | Bearer 访问 Key | `ProxyController.Messages` |
| `GET` | `/models` | Bearer 访问 Key | `ProxyController.Models` |
| `GET` | `/v1/models` | Bearer 访问 Key | `ProxyController.Models` |
| `POST` | `/responses` | Bearer 访问 Key | `ProxyController.Responses` |
| `POST` | `/v1/responses` | Bearer 访问 Key | `ProxyController.Responses` |
| `GET` | `/health` | 公开或 Cookie 可选 | `SystemController.Health` |
| `GET` | `/` | 公开或 Cookie 可选 | `SystemController.Root` |
| `GET` | `/system-settings` | 超级管理员 Cookie | `SystemSettingsController.GetSettings` |
| `PUT` | `/system-settings` | 超级管理员 Cookie | `SystemSettingsController.UpdateSettings` |
| `POST` | `/users` | 超级管理员 Cookie | `UsersController.CreateUser` |
| `DELETE` | `/users/{username}` | 超级管理员 Cookie | `UsersController.DeleteUser` |
| `PATCH` | `/users/{username}` | 超级管理员 Cookie | `UsersController.UpdateUser` |
| `GET` | `/users` | 超级管理员 Cookie | `UsersController.Users` |
| `POST` | `/web-search/import` | 超级管理员 Cookie | `WebSearchController.ImportWebSearch` |
| `POST` | `/web-search` | 超级管理员 Cookie | `WebSearchController.SaveWebSearch` |
| `POST` | `/web-search/test-key` | 超级管理员 Cookie | `WebSearchController.TestWebSearchKey` |
| `GET` | `/web-search` | 超级管理员 Cookie | `WebSearchController.WebSearch` |

## 5. 管理台页面追踪

| 页面/状态 | 角色 | API | 主要源码 | 核心需求/测试 |
|---|---|---|---|---|
| 加载与初始化失败 | 全部 | `/setup/status`、`/session` | App.vue | REQ-UI/REQ-AUTH；重试和错误态 E2E |
| 首次初始化 | 未初始化 | `/setup` | Setup.vue | 账号、访问模式、端口、Probe、Tauri 重启 |
| 登录 | 用户 | `/login` | Login.vue | 表单、停用、失败、会话恢复 |
| 全局框架/导航 | 登录用户 | `/session`、`/logout` | App.vue、style.css | 角色菜单、无 Router、移动 Drawer |
| 仪表盘 | 普通/超管 | `/stats`、实时 SSE、日志详情 | Dashboard.vue | 图表、时间、自动刷新、实时错误 |
| 渠道配置 | 普通/超管 | `/channels*`、诊断、模型覆盖 | Channels.vue | CRUD、归并、批量、测试、定价 |
| API Key | 普通/超管 | `/api-keys*`、`/users` | AccessKeys.vue | 创建、复制、启停、导入导出和秘密冲突 |
| 用户管理 | 超管 | `/users*` | Users.vue | 创建、停用、重置、删除和保护规则 |
| Web Search | 超管 | `/web-search*` | WebSearch.vue | 模式、Key、用量、测试和失败回滚 |
| 模型信息 | 超管 | `/model-providers*`、`/model-infos*`、`/model-catalog/export`、`/model-catalog/import` | Pricing.vue | 筛选、供应商、模型、定价、停用、导入导出 |
| 系统设置 | 超管 | `/system-settings` | SystemSettings.vue、tauriBackend.js | LAN、端口、Probe、重启 |
| 请求日志 | 普通/超管 | `/logs*`、`/stats` | Logs.vue | 快捷/高级筛选、分页、详情、SSE、清空 |

## 6. 数据实体追踪

| 实体/状态 | 产品专题 | 写入者 | 读取者 | 关键测试/风险 |
|---|---|---|---|---|
| User | 用户、认证 | Auth/User Service | Session/WorkContext/管理台 | 唯一用户名、保护管理员、级联 |
| AccessApiKey | 用户、认证 | ApiKeyService | ProxyAccess、管理台 | SHA-256、明文冲突、缓存失效 |
| Channel | 渠道、路由 | ChannelService | Route/Diagnostics/UI | Owner、排序、秘密、环境展开 |
| ChannelModelMapping | 渠道、路由 | Config/ModelCatalog | Route/ImageFallback | 显式映射全局语义 |
| ModelProvider | 模型目录 | ModelCatalogService | UI/Codex 目录 | code 唯一、启停 |
| ModelInfo | 模型目录/计费 | ModelCatalogService | Route/Catalog/Pricing | 匹配方式、Catalog、能力 |
| ChannelModelInfo | 渠道覆盖 | ModelCatalogService | Pricing/Route/UI | 覆盖和恢复 |
| ModelPricing（旧扁平价格） | 计费兼容 | ModelPricingService | 旧价格查询/播种 | 与新 Plan/Rule 的边界和迁移风险 |
| ModelPricingPlan / ModelPricingRule | 模型目录计费 | ModelCatalogService | ModelPricingService/ProxyLog/Stats | 公式、阶梯、渠道覆盖、历史快照 |
| WebSearchSettings | 特殊流程 | WebSearchService | Proxy 处理 | 三模式和权限 |
| TavilyKey | 特殊流程 | WebSearchService | TavilyClient | 明文、用量、上限 |
| RequestLog | 可观测性 | ProxyLogService | Observability/UI | 生命周期、父子、Owner |
| LogContentBlock | 日志存储 | LogContentStore | LogContentStore | 分块、压缩、哈希、共享删除 |
| LogContentManifest | 日志存储 | LogContentStore | LogContentStore | 完整正文哈希与重组 |
| ManifestChunk | 日志存储 | LogContentStore | LogContentStore | Ordinal 和缺块错误 |
| RequestLogContentRef | 日志存储 | ProxyLogService/Store | 日志详情 | 槽位、事务、孤立清理 |
| 亲和状态 | 路由运行时 | ChannelAffinityService | RouteService | Redis/内存、TTL |
| 容量租约 | 路由运行时 | ChannelCapacityService | RouteService/Stats | 租约、释放、崩溃过期 |
| 熔断状态 | 路由运行时 | CircuitBreakerService | Route/UI | Closed/Open/Half-open |
| 桌面设置文件 | 配置 | DesktopSystemSettingsStore/Tauri | API/Tauri | LAN、端口、Probe 字段往返 |

## 7. 协议与特殊能力追踪

| 能力 | 入口/方向 | 主要实现 | 主要测试 | 当前缺口 |
|---|---|---|---|---|
| 3×3 请求转换 | Responses/Chat/Messages → 三渠道 | ProtocolConverter.Requests | ProtocolConversionMatrixTests | 新字段需持续补矩阵 |
| 非流式响应转换 | 三渠道 → 原入口 | ProtocolConverter.Responses | ProxyCompatibility/StructuralTests | 供应商私有字段非全量 |
| 六向 SSE 转换 | 跨协议 | SseStreamConverter.* | Sse/Streaming/MatrixTests | 不支持分支需明确 |
| 同协议流 | 同协议 | ProxyStreamService + capture | StreamService/CaptureTests | 模型恢复/捕获语义持续回归 |
| 普通工具 | 三协议 | ToolContracts/Tools/History | Structural/CompatibilityTests | 超大 Schema 限制 TBD |
| Apply Patch | 三协议/多方言 | ApplyPatchTools | Compatibility/Streaming | 不等价方言需明确错误 |
| MCP | Responses/Chat/Messages | Mcp/ResponsesInput/Headers | NativeMcp*Tests | Enricher TODO |
| Web Search convert | 三协议 | WebSearchTools/Policy | 协议测试 | 上游能力依赖 |
| Web Search simulate | Responses 入口 → Chat/Messages 渠道，多轮 | WebSearchSimulator | 部分 Stream tests | 仅超级管理员 Key Owner；完整集成测试 GAP |
| 图片检测/OCR | 三协议图片输入 | ImageDetector/Fallback/Ocr | Vision/FallbackTests | 通用无映射路径不一致 |
| Images API | Images | ImagesController/Reader | Controller/Core Contract | 生产 DI/真实上游 GAP |
| Probe | 三协议 | ProbeRequestInterceptor | Probe/ProxyControllerTests | Rust 设置往返风险 |

## 8. 部署形态追踪

| 形态 | 数据/缓存 | 入口 | 主要文件 | 必须验证 |
|---|---|---|---|---|
| 本地开发 | SQLite/可选 Redis | HTTPS 后端 + Vite | README、launch profile、vite.config | Cookie origin、迁移、代理冒烟 |
| Docker SQLite | SQLite、无/可选 Redis | 127.0.0.1 映射 | Dockerfile、sqlite Compose | 卷、key ring、备份、重启 |
| Docker PostgreSQL + Redis | PostgreSQL + Redis | 反向代理 | pgsql Compose、部署脚本 | Secret、readiness、多实例一致性 |
| Tauri | 应用目录 SQLite、文件设置 | 本地 sidecar | src-tauri、prepare script | 安装、端口、重启、托盘、签名 |
| GitHub Release | 构建矩阵 | 安装包 | desktop-release.yml | 后端/前端/Rust 门禁、签名、哈希 |

## 9. 当前明确缺口追踪

| 缺口 | 关联需求族 | 需要的完成证据 |
|---|---|---|
| 访问 Key 明文策略冲突 | USR、AUTH、DAT、RSK | 统一决策、迁移、API/UI/文档和安全测试 |
| 渠道/Tavily Key明文 | CH、SPC、MIG、RSK | 应用层加密或批准方案、备份同级保护 |
| LAN 明文 HTTP | SYS、AUTH、CFG、NFR、RSK | TLS/受信网络策略和 E2E |
| 登录限流/CSRF | AUTH、NFR、RSK、TST | 中间件、策略和攻击负向测试 |
| Images 生产 DI | SPC、REL、TST、RSK | 真实容器启动和上游集成测试 |
| 浅层 health | SYS、NFR、REL、RSK | 独立 readiness、部署等待和故障测试 |
| 自动迁移回滚 | MIG、REL、RSK | 备份恢复、兼容窗口、唯一迁移执行者 |
| 日志保留和配额 | DAT、OBS、NFR、MIG、RSK | 配置、清理、告警、恢复和合规验收 |
| PR CI 门禁 | NFR、REL、TST、RSK | PR workflow 全部质量步骤 |
| 桌面签名/CSP/DevTools | UI、NFR、REL、RSK | 正式配置、签名、公证、安装测试 |
| WebSearchSimulator 集成 | SPC、TST | 真实 HTTP/Tavily fake server 多轮测试 |
| 前端标准测试与 E2E | UI、NFR、TST | npm test、浏览器矩阵和移动 E2E |
| SLA/RPO/RTO/容量 | OV、SYS、NFR、MIG、REL | 批准指标和可复现基准/演练 |
| 日志原样持久化认证与图片秘密 | OBS、NFR、MIG、RSK | 安全日志副本、受保护原始槽位、脱敏回归、访问审计和加密策略 |

## 10. 完整需求目录

以下目录从各专题中的唯一需求编号抽取。每行给出定义标题或摘要和该需求族的主要证据位置；最终验收仍以原专题中的完整规则和验收标准为准。

| 需求 ID | 级别 | 定义摘要 | 定义文档 | 主要证据族 |
|---|---|---|---|---|
| `REQ-OV-001` | MUST | 系统必须把管理台 Cookie 身份与代理 Bearer Key 身份视为两套独立认证体系，不得用管理台登录态替代代理访问 Key，也不得用访问 Key获得管理权限。 | [产品总览](01-product-overview.md) | Program.cs；README.md；全部专题测试 |
| `REQ-OV-002` | MUST | 普通用户没有可用渠道时，系统必须返回明确错误，不得自动使用超级管理员或其他用户渠道。 | [产品总览](01-product-overview.md) | Program.cs；README.md；全部专题测试 |
| `REQ-OV-003` | MUST | 协议转换必须优先保持客户端可观察语义；确实无法等价表达的字段应按已定义的降级或错误规则处理，不得静默生成结构无效的请求。 | [产品总览](01-product-overview.md) | Program.cs；README.md；全部专题测试 |
| `REQ-OV-004` | MUST | 渠道选择、跳过、重试和故障转移必须能够通过日志或诊断信息解释，不得只返回最终失败而丢失尝试链路。 | [产品总览](01-product-overview.md) | Program.cs；README.md；全部专题测试 |
| `REQ-OV-005` | MUST | 管理台、API、日志和导出功能必须遵循统一的敏感信息展示策略。 | [产品总览](01-product-overview.md) | Program.cs；README.md；全部专题测试 |
| `REQ-OV-006` | MUST | PRD、管理台文案和发布说明必须区分已实现能力、实验性能力、已知限制和未来计划。 | [产品总览](01-product-overview.md) | Program.cs；README.md；全部专题测试 |
| `REQ-OV-007` | MUST | 正式发布前必须为所选部署形态确认适用的可用性、容量、日志保留和恢复目标；未确认指标不得被宣传为产品保证。 | [产品总览](01-product-overview.md) | Program.cs；README.md；全部专题测试 |
| `REQ-USR-001` | MUST | 管理台身份与代理身份隔离 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-002` | MUST | 上游凭证隔离 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-003` | MUST | 服务端权限为最终裁决 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-004` | MUST | 固定基础角色 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-005` | MUST | 普通用户数据隔离 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-006` | MUST | 超级管理员全局视图 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-007` | MUST | owner不可被普通用户伪造 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-008` | MUST | 禁止删除当前用户 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-009` | MUST | 保护环境变量超级管理员 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-010` | MUST | 至少保留一个可登录超级管理员 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-011` | SHOULD | 超级管理员敏感操作重新认证 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-012` | MUST | 创建用户校验 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-013` | MUST | 用户停用立即影响 Cookie与Bearer | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-014` | MUST | 重新启用不恢复旧管理台会话 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-015` | MUST | 密码重置 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-016` | MUST | 用户删除影响清单 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-017` | MUST | 创建 Key归属字段统一 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-018` | MUST | Key名称非空 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-019` | MUST | 安全生成与哈希鉴权 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-020` | MUST | Key停用和删除即时失效 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-021` | MUST | Key使用归属 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-022` | MUST | 解决明文策略冲突 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-023` | MUST | 导入预览与覆盖确认 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-024` | SHOULD | Key轮换 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-025` | MUST | 统一401处理 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-026` | MUST | 统一403处理 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-027` | MUST | 敏感操作审计 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-028` | SHOULD | 并发冲突提示 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-029` | MUST | 移动端功能等价 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-USR-030` | MUST | 键盘与读屏支持 | [用户与权限](02-users-and-permissions.md) | UsersController；UserService；ApiKeyService；权限与路由测试 |
| `REQ-SYS-001` | MUST | 同一路径别名必须具有一致的鉴权、路由、转换和错误语义。 | [系统边界](03-system-boundary.md) | Program/Hosting；SystemController；src-tauri；Compose；启动测试 |
| `REQ-SYS-002` | MUST | 生产部署必须在 OpenCodex 前提供 TLS 终止，或仅允许受信任本机网络访问；LAN 模式不得被默认宣传为安全公网入口。 | [系统边界](03-system-boundary.md) | Program/Hosting；SystemController；src-tauri；Compose；启动测试 |
| `REQ-SYS-003` | MUST | 所有外部输入、上游输出和持久化内容必须有明确大小、超时或资源边界；未定义的边界必须在非功能需求中标为 TBD 并进入压力测试。 | [系统边界](03-system-boundary.md) | Program/Hosting；SystemController；src-tauri；Compose；启动测试 |
| `REQ-SYS-004` | MUST | Redis 故障时系统可以继续以单实例状态运行，但必须避免宣称仍具备跨实例一致的容量、亲和或熔断语义。 | [系统边界](03-system-boundary.md) | Program/Hosting；SystemController；src-tauri；Compose；启动测试 |
| `REQ-SYS-005` | SHOULD | 系统应提供能够反映数据库、迁移和关键写路径状态的 readiness 接口，与现有浅层 `/health` 分离。 | [系统边界](03-system-boundary.md) | Program/Hosting；SystemController；src-tauri；Compose；启动测试 |
| `REQ-SYS-006` | MUST | 管理台静态资源和管理 API 必须使用一致的基础路径规则 | [系统边界](03-system-boundary.md) | Program/Hosting；SystemController；src-tauri；Compose；启动测试 |
| `REQ-SYS-007` | MUST | 代理入口必须在路由前完成访问 Key 鉴权 | [系统边界](03-system-boundary.md) | Program/Hosting；SystemController；src-tauri；Compose；启动测试 |
| `REQ-SYS-008` | MUST | 管理资源访问必须在服务层继续验证归属，不能只依赖前端隐藏 | [系统边界](03-system-boundary.md) | Program/Hosting；SystemController；src-tauri；Compose；启动测试 |
| `REQ-SYS-009` | MUST | 上游 Key与客户端访问 Key必须分离 | [系统边界](03-system-boundary.md) | Program/Hosting；SystemController；src-tauri；Compose；启动测试 |
| `REQ-SYS-010` | MUST | 多实例部署必须共享数据库；若需要一致亲和/容量/熔断，还必须共享 Redis | [系统边界](03-system-boundary.md) | Program/Hosting；SystemController；src-tauri；Compose；启动测试 |
| `REQ-SYS-011` | MUST | 任何失败不得导致请求路由到其他用户渠道 | [系统边界](03-system-boundary.md) | Program/Hosting；SystemController；src-tauri；Compose；启动测试 |
| `REQ-SYS-012` | SHOULD | 管理接口权限应采用集中式声明或自动化覆盖检查，降低遗漏风险 | [系统边界](03-system-boundary.md) | Program/Hosting；SystemController；src-tauri；Compose；启动测试 |
| `REQ-SYS-013` | SHOULD | 所有运行形态应公开版本、构建提交和就绪状态 | [系统边界](03-system-boundary.md) | Program/Hosting；SystemController；src-tauri；Compose；启动测试 |
| `REQ-SYS-014` | MUST | 系统配置、数据迁移和文档必须使用同一组有效环境变量 | [系统边界](03-system-boundary.md) | Program/Hosting；SystemController；src-tauri；Compose；启动测试 |
| `REQ-SYS-015` | MUST | 正文内容存储损坏或哈希不一致时不得返回伪造内容 | [系统边界](03-system-boundary.md) | Program/Hosting；SystemController；src-tauri；Compose；启动测试 |
| `REQ-DAT-001` | MUST | 访问 Key 的持久化、展示、导出和轮换策略必须在产品和安全评审中统一；不得同时宣称“仅创建时可见”和“数据库保留可恢复明文”而没有标注差异。 | [领域模型](04-domain-model.md) | Domain；OpenCodexDbContextBase；Migrations；数据/日志测试 |
| `REQ-DAT-002` | MUST | 所有租户资源查询必须在数据库查询或服务层使用 Owner/User 约束，不能只在前端隐藏记录。 | [领域模型](04-domain-model.md) | Domain；OpenCodexDbContextBase；Migrations；数据/日志测试 |
| `REQ-DAT-003` | MUST | 历史请求成本必须使用完成请求时的价格快照，不得因后续修改模型价格而改变历史账单。 | [领域模型](04-domain-model.md) | Domain；OpenCodexDbContextBase；Migrations；数据/日志测试 |
| `REQ-DAT-004` | MUST | 删除或停用操作必须明确区分软删除、硬删除和恢复语义；模型“删除”若实际是停用，产品文案和验收必须统一使用“停用”。 | [领域模型](04-domain-model.md) | Domain；OpenCodexDbContextBase；Migrations；数据/日志测试 |
| `REQ-DAT-005` | SHOULD | 凭证和日志正文应支持加密存储或外部密钥管理；当前明文字段和导出能力必须进入安全风险评审。 | [领域模型](04-domain-model.md) | Domain；OpenCodexDbContextBase；Migrations；数据/日志测试 |
| `REQ-AUTH-001` | MUST | 启动时先判定初始化状态 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-002` | MUST | 初始化资格公式唯一且由服务端裁决 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-003` | MUST | 首次初始化只能成功一次 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-004` | MUST | 初始化状态可恢复 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-005` | MUST | 管理员用户名校验 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-006` | MUST | 管理员密码策略 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-007` | MUST | 系统设置字段校验 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-008` | MUST | LAN 风险确认 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-009` | MUST | 防止重复提交 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-010` | MUST | 初始化成功自动建立会话 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-011` | MUST | 监听变更时可靠重启 Tauri 后端 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-012` | MUST | 桌面设置字段无损持久化 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-013` | SHOULD | 重启使用应用就绪检查 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-014` | MUST | 统一登录契约 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-015` | MUST | 环境变量超级管理员同步 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-016` | MUST | 登录防暴力破解 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-017` | SHOULD | 风险登录通知与近期认证 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-018` | MUST | Cookie 安全属性 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-019` | MUST | 同时限制空闲时间与绝对寿命 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-020` | MUST | Data Protection 密钥持久化 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-021` | MUST | 禁止默认共享 session secret | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-022` | MUST | 多实例会话一致性 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-023` | MUST | 防止会话固定 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-024` | MUST | CSRF 防护 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-025` | MUST | `/session` 返回稳定会话快照 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-026` | MUST | 停用或删除用户撤销会话 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-027` | MUST | 密码重置撤销旧会话 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-028` | SHOULD | 会话管理与退出全部设备 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-029` | MUST | 退出幂等且本地状态必清理 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-030` | MUST | 全局 401处理 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-031` | MUST | 全局 403处理 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-032` | SHOULD | 恢复原目标页面 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-033` | MUST | 后端不可达与重启态可区分 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-034` | MUST | 认证页面可访问性 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-035` | MUST | 认证安全审计 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-AUTH-036` | MUST | 管理台与 Bearer 身份隔离 | [初始化与认证](05-initialization-and-auth.md) | AuthController；Auth/Session/ApiKey/ProxyAccess Service；Setup/Auth 测试 |
| `REQ-CH-001` | MUST | 租户隔离 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-002` | MUST | 渠道列表 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-003` | MUST | 创建校验 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-004` | MUST | 名称唯一性 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-005` | MUST | 更新目标稳定性 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-006` | MUST | 批量更新原子性 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-007` | MUST | 删除及关联清理 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-008` | MUST | 配置导入为合并操作 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-009` | MUST | 模型映射标准化 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-010` | MUST | Images 渠道约束 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-011` | MUST | Compat 执行契约 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-012` | MUST | 上游凭证保护 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-013` | SHOULD | 环境变量引用 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-014` | MUST | 实时健康展示 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-015` | MUST | 健康重置 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-016` | SHOULD | 模型发现 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-017` | SHOULD | 流式渠道测试 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-018` | MUST | 缓存一致性 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-019` | MUST | 审计记录 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-CH-020` | SHOULD | 并发修改保护 | [渠道管理](06-channel-management.md) | ChannelController；ChannelService；ChannelDiagnosticsService；渠道/路由测试 |
| `REQ-RTE-001` | MUST | 租户路由隔离 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-002` | MUST | 映射模式判定 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-003` | MUST | 无映射兜底 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-004` | MUST | 渠道类型过滤 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-005` | MUST | 确定性排序 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-006` | SHOULD | 最少连接选择 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-007` | MUST | 会话亲和 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-008` | SHOULD | 亲和写入时机 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-009` | MUST | 渠道容量硬限制 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-010` | MUST | 分布式容量 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-011` | MUST | 降级语义 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-012` | MUST | 单渠道重试 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-013` | MUST | 退避与 Retry-After | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-014` | MUST | 流内错误探测 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-015` | MUST | 跨渠道故障转移 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-016` | MUST | 流式首字节保护 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-017` | MUST | 熔断状态机 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-018` | MUST | 熔断失败分类 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-019` | MUST | 容量耗尽响应 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-020` | MUST | 上游错误隔离 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-021` | MUST | 请求取消 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-022` | MUST | 路由缓存一致性 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-023` | SHOULD | 图片能力路由 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-024` | MUST | 尝试级日志 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-RTE-025` | MUST | 可靠性指标 | [路由与可靠性](07-routing-and-reliability.md) | ProxyRoute/Capacity/CircuitBreaker/Affinity/Failover；相关单元与集成测试 |
| `REQ-PRT-001` | MUST | 完整协议矩阵 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-002` | MUST | 原始请求不可变 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-003` | MUST | 模型名隔离 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-004` | MUST | 同协议安全标准化 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-005` | MUST | 统一内部模型 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-006` | MUST | 显式语义保护 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-007` | MUST | 角色与系统指令 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-008` | MUST | 文本与多段内容 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-009` | MUST | 图片内容 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-010` | MUST | Reasoning 转换 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-011` | MUST | 普通工具闭环 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-012` | MUST | 工具命名空间恢复 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-013` | MUST | apply_patch | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-014` | MUST | Native MCP | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-015` | MUST | Web Search 与 Tool Search | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-016` | MUST | Compat 顺序 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-017` | MUST | JSON Schema 输出 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-018` | MUST | Usage 保真 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-019` | MUST | 结束原因映射 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-020` | MUST | SSE 事件顺序 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-021` | MUST | 流式错误终止 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-022` | MUST | SSE 延迟准备 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-023` | SHOULD | Codex headers | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-024` | MUST | Tool Schema 清理 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-025` | MUST | 未知新字段策略 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-026` | MUST | 可观测转换记录 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-PRT-027` | MUST | 协议回归矩阵 | [协议转换](08-protocol-conversion.md) | ProtocolConverter；SseStreamConverter；ProtocolConversionMatrixTests |
| `REQ-SPC-001` | MUST | 跨协议发送工具 Schema 前必须执行结构清洗，确保目标协议接受的类型、必填字段和嵌套结构合法。 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-002` | MUST | 工具调用结果必须与正确的调用 ID、工具名称和历史位置配对；缺失或冲突时不得静默拼接到其他调用。 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-003` | MUST | 当目标协议无法无损表达 `apply_patch` 时，系统必须执行已记录的兼容策略；如果兼容策略会改变调用语义，必须拒绝请求并说明受影响参数。 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-004` | MUST | MCP 调用、结果和历史必须在三种入口协议之间保持可追踪的调用 ID 和错误状态。 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-005` | MUST | 只有超级管理员配置并允许的场景才能启用 `simulate`；普通用户不得通过请求字段绕过全局模式或触发未授权 Tavily 调用。 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-006` | MUST | 图片检测、视觉路由和 OCR 降级必须在普通请求、工具结果续轮和三种入口协议中保持一致的能力判断。 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-007` | MUST | Images 接口不得把不支持的流式请求当作普通非流式请求静默执行，必须返回明确的客户端错误。 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-008` | MUST | Images 接口的生产 DI、渠道校验、真实上游调用和错误路径必须有启动态与集成测试证据；仅控制器 fake 测试不足以证明正式可用。 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-009` | MUST | Probe 拦截只能由系统级配置控制，并在日志中标记“未调用上游”的原因。 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-010` | MUST | 特殊流程错误必须携带可关联的 request ID，并在日志中记录触发条件、是否调用上游、是否产生子请求和最终入口协议。 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-011` | MUST | 工具声明、调用、结果和续轮必须保持调用关联 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-012` | MUST | Apply Patch 跨方言必须保留补丁语义或明确拒绝 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-013` | MUST | MCP 原生调用和结果不得误转普通工具 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-014` | MUST | Web Search 三种模式行为互斥且可观测 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-015` | MUST | Web Search 模拟有轮数和 Key 用量上限 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-016` | MUST | 图片检测覆盖三种入口和工具结果 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-017` | MUST | OCR 降级生成子日志并可回到主请求 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-018` | MUST | Images 接口拒绝不支持的流式请求 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-019` | MUST | Probe 不调用上游但仍鉴权和记日志 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-020` | MUST | 特殊流程不得泄露客户端 Bearer Key | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-021` | SHOULD | 大型 Schema、SSE、图片和搜索结果有容量保护 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-SPC-022` | SHOULD | 产品界面说明实验性/降级语义 | [特殊流程](09-tools-multimodal-and-special-flows.md) | Tool/MCP/WebSearch/Image/OCR/Probe 源码；专项测试 |
| `REQ-UI-001` | MUST | 管理台入口稳定 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-002` | MUST | 启动状态互斥且无闪屏 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-003` | MUST | 每个业务页面拥有稳定路由 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-004` | MUST | 路由级权限保护 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-005` | MUST | 安全恢复页面状态 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-006` | MUST | 未保存变更保护 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-007` | MUST | 应用外壳角色与身份清晰 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-008` | SHOULD | 侧栏偏好持久化 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-009` | MUST | 移动菜单可靠 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-010` | MUST | 异步页面有加载和错误占位 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-011` | MUST | 统一API错误对象 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-012` | MUST | 全局401处理 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-013` | MUST | 全局403处理 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-014` | MUST | 请求取消与竞态保护 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-015` | MUST | 加载态不破坏上下文 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-016` | MUST | 空态与错误态分离 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-017` | SHOULD | 数据新鲜度可见 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-018` | MUST | 字段级校验 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-019` | MUST | 并发冲突保护 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-020` | MUST | 仪表盘核心指标完整 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-021` | MUST | 实时区域独立降级 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-022` | MUST | 仪表盘图表可访问 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-023` | MUST | 渠道列表功能等价 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-024` | MUST | 渠道编辑草稿安全 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-025` | MUST | 渠道批量与测试流程可控 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-026` | MUST | API Key页面使用单一明文策略 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-027` | MUST | 用户生命周期交互说明影响 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-028` | MUST | Web Search即时变更可恢复 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-029` | MUST | 模型信息编辑可验证 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-030` | MUST | 系统设置保存和重启闭环 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-031` | MUST | 日志筛选可分享且可回滚 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-032` | MUST | 日志列表桌面移动等价 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-033` | MUST | 日志详情安全展示 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-034` | MUST | 关联日志导航可返回 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-035` | MUST | 导入前预览和冲突确认 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-036` | MUST | 敏感导出受控 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-037` | MUST | 危险操作分级确认 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-038` | MUST | 统一响应式策略 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-039` | MUST | 移动端核心功能等价 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-040` | MUST | 键盘与焦点管理 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-041` | MUST | 动态状态可被辅助技术感知 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-042` | MUST | 图表与复杂数据等价表达 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-043` | SHOULD | 减少动态效果 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-044` | MUST | 页面性能预算 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-045` | MUST | 前端自动化测试门禁 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-UI-046` | SHOULD | 前端可观测性 | [管理台](10-admin-console.md) | frontend/src；管理控制器；前端状态测试与 E2E 缺口 |
| `REQ-OBS-001` | MUST | 当上游提供可解析 Usage 时，系统必须在主请求日志中保存统一字段；无法解析时必须标明缺失原因，不得用 0 冒充真实零用量。 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-002` | MUST | 成本计算必须基于请求完成时的定价快照，且能追溯到计费项、模式、单位价格和输入用量。 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-003` | MUST | 详细正文的保存、读取、删除和重组必须保持引用完整性，任何损坏不得静默返回错误正文。 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-004` | MUST | 读取日志详情必须在服务端验证日志 Owner 或超级管理员权限，不能只凭前端传入日志 ID。 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-005` | MUST | 主请求详情必须能导航到所有关联 attempt 和 OCR 子日志，子日志也能返回主请求。 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-006` | MUST | 实时 SSE 断开时管理台必须显示未连接状态，并允许通过自动重连或刷新恢复；不得将旧数据伪装为实时数据。 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-007` | MUST | 成本展示必须同时展示币种和“已计算/未知/部分 Usage”等状态，不能把缺少价格或 Usage 的金额显示成确定账单。 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-008` | MUST | 日志和统计查询必须有分页、时间范围或容量边界，不能因用户传入极大范围导致无界内存加载。 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-009` | SHOULD | 系统应提供日志存储使用量、正文块去重率、孤立块数量和最近清理时间等运维指标。 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-010` | MUST | 主请求、attempt、OCR 形成可导航链路 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-011` | MUST | 流式请求记录 TTFT、结束状态和关键时序 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-012` | MUST | 原始、上游、客户端正文槽位按权限读取 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-013` | MUST | 内容寻址正文可重组并校验 SHA-256 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-014` | MUST | 统计不重复计算 attempt 为客户端请求 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-015` | MUST | 成本保留价格快照 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-016` | MUST | 实时 SSE 断开显示未连接 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-017` | MUST | 日志过滤始终受 Owner 约束 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-018` | MUST | 清空日志有权限和二次确认 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-019` | SHOULD | 支持结构化导出和受控正文导出 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-OBS-020` | SHOULD | readiness 与观测指标可区分 | [可观测性与计费](11-observability-and-billing.md) | Observability/ProxyLog/LogContent/ModelPricing；日志与统计测试 |
| `REQ-CFG-001` | MUST | 系统必须维护唯一、可机器检查的配置目录，列出名称、类型、默认值、敏感性、作用域、是否需重启和适用模式。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-002` | MUST | 配置优先级必须遵循“显式 `OpenCodex:*` > 对应 `OPENCODEX_*` > `.env` 默认 > 代码默认”。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-003` | MUST | `.env` 不得覆盖已有非空 ASP.NET Core 配置。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-004` | MUST | 数据库配置必须只使用 `OPENCODEX_DB_PROVIDER` 与 `OPENCODEX_DB_CONNECTION_STRING`；陈旧 `OPENCODEX_DB_PATH` 必须从正式文档移除或启动时明确告警。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-005` | MUST | 数据库 provider 在服务监听前必须验证为 `sqlite` 或 `postgres`（接受规范化别名时需文档化）。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-006` | MUST | 生产配置不得使用示例 Cookie secret、示例数据库密码或空管理员密码。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-007` | MUST | Data Protection key 目录必须可写且在持久化部署中可跨重启保留。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-008` | MUST | 正整数配置的非法值处理必须统一，不得无提示地在部分组件失败、部分组件回退。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-009` | MUST | 桌面端必须固定支持 `localhost` 与 `lan` 两种模式，并把绑定地址规范为 `127.0.0.1` 与 `0.0.0.0`。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-010` | MUST | 桌面端口必须限制为 1024–65535，非法端口不得启动 sidecar。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-011` | MUST | `intercept_probe_requests` 必须在 .NET 与 Rust 设置模型间无损保留。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-012` | MUST | 只有超级管理员可以读取或修改系统设置。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-013` | MUST | 影响监听地址或端口的变更必须返回 `restart_required=true`；仅动态字段变化不得错误要求重启。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-014` | MUST | LAN 模式必须明确标记当前为明文 HTTP，并在启用前展示安全影响。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-015` | SHOULD | 系统应提供脱敏的“有效配置诊断”，显示值来源而非秘密值。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-016` | MUST | 渠道配置必须按白名单拒绝未知顶层字段和未知 compat 字段。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-017` | MUST | 渠道 `capacity` 必须为正整数；新建渠道不得产生零容量或缺失容量记录。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-018` | MUST | Images 渠道必须执行 dialect、模型映射和 `retry_count=0` 三项约束。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-019` | MUST | 环境变量占位符只能在保存或执行前按既定语法递归展开；缺失变量不得被静默替换为空。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-020` | MUST | 所有秘密字段在读取、日志、诊断、导入导出和错误中必须脱敏。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-021` | MUST | Redis 为空或不可用时服务必须保持主数据正确，并明确退化为进程内缓存与单实例共享状态。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-022` | SHOULD | Redis 在依赖恢复后应自动重新加入，无需重启应用。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-023` | MUST | Docker Compose 中数据库、Redis和应用秘密不得使用仓库内固定生产值。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-024` | MUST | 配置变更必须产生不含秘密的审计记录。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-025` | SHOULD | 桌面设置文件损坏时应保留损坏副本并以可见方式恢复默认，而非静默覆盖。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-CFG-026` | MUST | README、DEPLOYMENT、`.env.example` 与配置目录必须在发布门禁中保持一致。 | [配置](12-configuration.md) | RuntimeSettingsProvider；DesktopSystemSettingsStore；配置/设置测试 |
| `REQ-NFR-001` | MUST | 发布前必须建立正式指标字典，统一 TTFT、总时长、成功率、错误率、吞吐和并发的计算口径。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-002` | MUST | 生产 SLO 数值必须来自可复现基准测试，不得直接引用内存单测阈值。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-003` | MUST | 流式路径必须逐增量写出并 flush，不得等待完整上游响应。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-004` | MUST | 反向代理必须关闭 SSE 响应缓冲，并保留取消传播。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-005` | MUST | 捕获预算超限不得截断真实客户端响应，只能截断观测副本并标记。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-006` | MUST | 服务必须为请求体、图片、工具 schema 和 SSE 事件设置可配置且可测试的资源边界。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-007` | MUST | 上游连接池、超时和重试必须可观测，且总最坏等待时间可计算。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-008` | MUST | 首次下游写出后不得执行跨渠道故障转移。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-009` | MUST | 熔断必须覆盖 closed/open/half-open，默认阈值、开放期和 probe 并发必须可测试。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-010` | MUST | Redis 不可用时主数据和鉴权真值不得丢失；多实例一致性降级必须显式可见。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-011` | SHOULD | Redis 恢复后应用应自动恢复共享缓存和状态。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-012` | MUST | 必须区分 liveness、readiness 与 degraded health。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-013` | MUST | 自动迁移和默认数据播种完成前实例不得进入 ready。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-014` | MUST | 生产网络必须使用 TLS；LAN HTTP 模式不得被描述为安全远程访问。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-015` | MUST | 管理登录、访问 API Key 和高成本代理接口必须具备速率限制与防爆破策略。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-016` | MUST | 生产秘密不得硬编码在 Compose、镜像、日志或前端资源。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-017` | MUST | 管理 Cookie 必须保持 HttpOnly，并在 HTTPS 部署中为 Secure；Cookie key 必须持久化。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-018` | MUST | 日志脱敏不得修改真实业务 payload，且必须覆盖已知认证、图片和嵌套秘密。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-019` | MUST | 日志正文损坏必须被检测，不得静默返回错误内容。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-020` | MUST | 数据库请求日志必须具有保留、配额、清理和归档策略。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-021` | MUST | SQLite 与 PostgreSQL 必须对相同业务契约保持迁移和查询兼容。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-022` | MUST | 普通 PR 和主分支 push 必须执行后端测试、前端单测与生产构建。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-023` | SHOULD | CI 应执行 Rust fmt/clippy/test 与桌面端最小冒烟。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-024` | MUST | 发布产物必须可复现并锁定依赖。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-025` | MUST | 桌面正式产物必须具备平台适用的签名和完整性验证。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-026` | SHOULD | 发布流程应生成 SBOM、依赖漏洞报告和产物校验和。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-027` | MUST | 必须定义并测试支持的服务器架构、桌面 OS 和浏览器最低版本。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-028` | MUST | 多实例容量限制的精确值必须来自 Redis，全局近似指标不得伪装成精确值。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-029` | MUST | 客户端取消、异常和进程退出必须释放本地计数，Redis租约即使丢失也必须最终过期。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-030` | SHOULD | 管理台核心流程应满足批准的无障碍目标。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-031` | MUST | 移动端必须覆盖初始化、登录、渠道、Key 和日志关键流程，不只验证视觉断点。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-NFR-032` | MUST | 任何 SLA/SLO 变更必须版本化并关联监控、报警和容量测试。 | [非功能要求](13-non-functional-requirements.md) | 跨模块源码；压测/故障注入/安全测试与当前缺口 |
| `REQ-MIG-001` | MUST | SQLite/PostgreSQL 必须是用户、渠道、Key、模型、价格和请求日志的唯一业务主数据源。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-002` | MUST | provider 只允许 `sqlite` 与规范化后的 `postgres`。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-003` | MUST | SQLite 与 PostgreSQL 必须维护逻辑等价的 schema、索引和业务约束。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-004` | MUST | 每次模型变更必须同时提交两套 migration 与 snapshot。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-005` | MUST | 应用进入 ready 前必须完成 `Database.Migrate()` 和关键 seed。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-006` | MUST | migration 失败必须阻断启动，并提供不泄露连接串的诊断。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-007` | MUST | 生产 schema 变更前必须创建并验证可恢复备份。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-008` | MUST | 破坏性 migration 必须被自动或人工门禁识别。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-009` | MUST | `ContentAddressedLogs` 当前升级不得被描述为无损；是否允许丢弃旧日志正文必须显式决策。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-010` | MUST | 新的破坏性变更必须使用 expand-migrate-contract 或等价兼容策略。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-011` | MUST | migration Down 必须标记“无损、有限损失或不可逆”，不得仅因存在 Down 方法就宣称可回滚。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-012` | MUST | 多实例部署必须避免多个实例无协调地同时执行生产 migration。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-013` | MUST | 默认价格和模型目录 seed 必须幂等，不得覆盖管理员明确修改的数据。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-014` | MUST | SQLite 备份必须是含 WAL 语义的一致快照。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-015` | MUST | PostgreSQL 必须具备自动备份、加密、校验和隔离恢复演练。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-016` | MUST | Data Protection key ring 必须与数据库恢复计划绑定。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-017` | MUST | Redis 备份不得替代主数据库备份。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-018` | MUST | `RequestLogContentSlot` 已发布数值只能追加，禁止重排和复用。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-019` | MUST | 日志内容块和 manifest 必须执行完整 SHA-256 与长度校验。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-020` | MUST | 内容寻址写入必须原子，不得产生 ref 指向缺失内容。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-021` | MUST | 内容去重必须处理并发插入，不因相同 hash 产生重复块或 manifest。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-022` | MUST | hash 冲突或相同 hash 不同长度必须 fail-closed。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-023` | MUST | 清空日志必须跨 RequestLogs、refs、manifests、chunks、blocks 原子执行。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-024` | SHOULD | 系统应提供可重复运行的全库孤立日志内容检查与垃圾回收。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-025` | MUST | 请求日志必须定义保留期、容量上限、清理批次和归档策略。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-026` | MUST | 备份和日志正文必须按高敏数据保护，去重/压缩不得被视为加密。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-027` | MUST | owner 关联数据必须在数据库或应用层保持引用完整性和删除契约。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-028` | SHOULD | 可编辑配置实体应定义并发更新冲突策略。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-029` | MUST | 时间字段必须统一记录 UTC 和单位；金额必须定义精度与舍入。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-030` | MUST | migration CI 必须覆盖空库到最新、上一正式版到最新和恢复备份到最新。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-031` | MUST | 应用版本与 schema migration 范围必须有兼容矩阵。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-032` | SHOULD | 生产应支持只运行 migration/preflight 而不启动 API。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-033` | MUST | 渠道和 Tavily 明文秘密的数据库保护策略必须在生产发布前确定。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-MIG-034` | MUST | 数据恢复必须在隔离环境通过完整性和业务冒烟后才切换流量。 | [数据与迁移](14-data-and-migrations.md) | DbContext；Migrations；DatabaseInitializer；双库迁移测试 |
| `REQ-REL-001` | MUST | 一次正式发布必须有唯一语义版本，并同步到桌面配置、后端版本接口、镜像标签、安装包和发布说明。 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-002` | MUST | Docker 构建必须从干净工作区使用锁文件恢复依赖，并记录 Git commit、构建时间、版本和基础镜像摘要。 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-003` | SHOULD | 生产镜像应使用非 root 用户、只读根文件系统兼容路径、最小 Linux capabilities 和显式资源限制。 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-004` | MUST | 多实例部署不得把流量同时分发到数据源、Redis Prefix、密钥或配置不一致的实例。 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-005` | MUST | 远程发布脚本必须在切换流量前完成目录准备、配置校验、备份、镜像固定、迁移验证和健康冒烟；任何一步失败应停止并保留旧实例。 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-006` | MUST | SSE 长连接发布时必须有连接排空或可接受中断策略，不能假设普通 HTTP 切流对流式请求无影响。 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-007` | MUST | 未经正式签名和平台安全验证的安装包必须标记为测试构建，不能作为正式生产发布。 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-008` | MUST | 任何包含数据库 Schema 变化的发布都必须同时给出应用回滚兼容性结论和数据恢复步骤。 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-009` | MUST | 正式发布使用不可变版本或镜像 digest | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-010` | MUST | 部署成功必须由 readiness 和业务冒烟证明 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-011` | MUST | 数据变更发布前有可恢复备份 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-012` | MUST | SQLite/PostgreSQL 双迁移同步交付 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-013` | MUST | PR 级验证覆盖后端、前端和迁移 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-014` | SHOULD | 发布生成 SBOM 和签名校验 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-015` | SHOULD | 服务端支持滚动或蓝绿发布 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-016` | MUST | 桌面正式包有平台签名和安装冒烟 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-017` | MUST | 发布说明列明所有配置变化和兼容性影响 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-018` | MUST | 失败发布不会删除唯一可用的旧版本和数据恢复点 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-019` | MUST | `ContentAddressedLogs` 升级必须迁移并校验旧日志正文，或在明确审批后执行可恢复的数据丢弃方案 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-REL-020` | MUST | 服务端 Docker 镜像由可审计 CI 产出并以不可变 digest 发布，禁止把本地工作站作为唯一发布链 | [部署与发布](15-deployment-and-release.md) | Dockerfile；Compose；scripts；workflow；发布冒烟 |
| `REQ-TST-001` | MUST | 每个管理接口必须至少有未登录、普通用户和超级管理员三类授权测试；每个租户资源接口必须有跨用户 ID 越权测试。 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-002` | MUST | 协议矩阵测试必须明确区分同协议透传和六个跨协议方向，不能用单一 happy path 代表全部兼容性。 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-003` | MUST | 每个数据库迁移必须在 SQLite 和 PostgreSQL 中从上一正式版本真实升级，并验证应用启动、读写和回滚/恢复路径。 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-004` | MUST | 可靠性需求必须通过故障注入证明，而不是只用 Fake 返回预设状态码。 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-005` | MUST | 前端测试提供标准 npm script 并在 CI 执行 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-006` | MUST | 普通 PR 自动运行质量门禁 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-007` | MUST | 九个协议方向覆盖流式和非流式 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-008` | MUST | Redis 可用/不可用和多实例行为均测试 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-009` | MUST | Images 真实生产依赖通过启动集成测试 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-010` | MUST | WebSearchSimulator 具备完整集成测试 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-011` | MUST | 移动端关键管理流程具备 E2E | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-012` | MUST | 桌面三平台完成安装冒烟 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-013` | MUST | 所有安全边界有负向测试 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-014` | SHOULD | 采集代码覆盖率并设关键模块门槛 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-015` | SHOULD | 建立性能基线和自动回归比较 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-016` | MUST | 每个 MUST 需求在追踪索引中有测试证据或缺口状态 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-017` | MUST | `ContentAddressedLogs` 必须以非空旧库验证 Up/Down 数据影响，任何预期数据丢弃都需显式验收 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-TST-018` | MUST | 手工测试和数据采集脚本必须与当前认证、数据库 Schema、部署形态和依赖声明同步，否则从验收证据中排除 | [测试与验收](16-testing-and-acceptance.md) | OpenCodex.Api.Tests；前端测试；CI；测试缺口 |
| `REQ-RSK-001` | MUST | 生产配置必须从 Secret 注入随机数据库凭据； | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |
| `REQ-RSK-002` | MUST | Redis 必须启用认证或置于不可被非受信主体访问的隔离网络； | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |
| `REQ-RSK-003` | MUST | LAN 模式必须展示明确风险和生效地址；正式公网场景必须使用 TLS。 | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |
| `REQ-RSK-004` | MUST | 正式发布前必须选择统一凭证策略，并同步实体、API、管理台、导出和 README。 | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |
| `REQ-RSK-005` | MUST | 正式多人或网络部署应增加登录速率限制、失败审计和 CSRF 防护，并定义会话撤销机制。 | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |
| `REQ-RSK-006` | MUST | 部署前必须确认日志数据分类、保留、删除、备份和访问策略。 | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |
| `REQ-RSK-007` | MUST | 生产数据库迁移必须有唯一执行者、备份、时长评估、兼容窗口和恢复验证。 | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |
| `REQ-RSK-008` | MUST | 在将 Images 标为正式能力前，必须补齐 `IProxyImagesEndpointService` 生产实现与 DI 注册，并通过真实容器启动、依赖解析、OpenAI/xAI 上游集成和错误路径测试。 | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |
| `REQ-RSK-009` | MUST | 增加 readiness，并让部署脚本等待其成功。 | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |
| `REQ-RSK-010` | SHOULD | 锁定工具链和依赖，生成 SBOM、哈希和签名。 | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |
| `REQ-RSK-011` | SHOULD | 管理台应建立全局错误、会话、表单、路由和无障碍规范。 | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |
| `REQ-RSK-012` | MUST | 正式发布文档必须由当前配置源和测试验证，历史方案不得混入现行操作说明。 | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |
| `REQ-RSK-013` | MUST | 没有书面接受记录的 P0/P1 风险不得被默认视为可接受。 | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |
| `REQ-RSK-014` | MUST | 在替换为可保留数据的迁移或完成明确的数据丢弃审批、导出和恢复验证前，发布门禁必须阻止该迁移进入含历史日志的环境。 | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |
| `REQ-RSK-015` | MUST | 服务端镜像必须由受控 CI 从干净提交构建、扫描、记录 digest/provenance 后发布； | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |
| `REQ-RSK-016` | MUST | 正式发布必须通过真实 PostgreSQL、Redis、多实例和迁移备份恢复集成测试。 | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |
| `REQ-RSK-017` | MUST | 手工测试、数据采集和运维脚本必须声明依赖、使用当前认证/API/Schema，并移除个人路径和示例凭据默认值；否则必须标记停用且不得作为验收证据。 | [风险与决策](17-known-limitations-and-risks.md) | 风险证据文件；修复验证或书面接受记录 |

## 11. 维护与完成判定

### 11.1 新需求

新增需求时必须：

1. 在对应专题使用下一个连续编号；
2. 提供级别、规则和可执行验收标准；
3. 在本索引增加接口、页面、实体、源码或测试映射；
4. 若为 MUST，提供测试或明确标为 GAP；
5. 更新文档基线提交。

### 11.2 完成判定

一条需求只有同时满足以下条件才能标记为“已完成”：

- 当前代码或配置具备该行为；
- 权限和异常边界已实现；
- 自动化测试或运行证据覆盖验收标准；
- 数据迁移和兼容影响已处理；
- 用户可见文案与真实行为一致；
- 相关风险已关闭或有正式接受记录。

仅有 PRD、源码但无测试，或仅有单元测试但无法证明真实启动/部署的情况，不足以支持广义“完成”声明。
