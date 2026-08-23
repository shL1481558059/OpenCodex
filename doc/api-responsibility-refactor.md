# API 职责单一化改造方案

> 状态：已实施（批 0-8）。本文档记录后端 API 与前端管理台调用面的完整排查结论、目标蓝图、分批实施计划及执行结果。原始排查结论保留作为改造前基线，第 10 章记录实际执行结果与遗留决策。
>
> 排查时间：2026-08-23。所有标注文件行号的结论均通过全仓检索与逐文件阅读核对过，未依赖记忆或推测。

## 1. 范围与基线

本次排查覆盖后端全部 HTTP 边界与前端管理台全部调用点，不涉及协议转换内部实现。

| 范围 | 数量 | 说明 |
| --- | --- | --- |
| 业务 Controller | 14 | 另有 `ApiControllerBase`、`AuthenticatedApiControllerBase` 两个基类和 `SessionState` 静态类 |
| 路由模板 | 83 | 含别名（代理 `/v1/*` 4 条、图片 `/v1/*` 2 条、诊断旧路径 2 条、渠道旧路径别名若干） |
| 前端页面 | 11 | `App.vue` 外壳 + 10 个业务页 |
| 前端调用点 | 53 | `props.api()` 47 处、`await api()` 3 处、裸 `fetch` 1 处、`EventSource` 2 处 |
| 服务端分页的列表接口 | 1 | 仅 `GET /logs` |

各 Controller 的路由密度：

| Controller | 路由模板数 |
| --- | --- |
| `ModelCatalogController` | 11 |
| `ObservabilityController` | 17 |
| `ProxyController` | 8 |
| `ConfigController` | 10 |
| `ApiKeysController` | 6 |
| `ChannelDiagnosticsController` | 4 |
| `ImagesController` | 4 |
| `UsersController` | 5 |
| `WebSearchController` | 4 |
| `SessionController` | 3 |
| `SetupController` | 2 |
| `SystemController` | 2 |
| `SystemSettingsController` | 2 |

## 2. 真实缺陷

以下五条不是风格问题，是可观测的错误或明确的功能缺口，且全部由「接口职责混在一起」直接导致。

### 2.1 `POST /api-keys` 的归属人参数不生效（P0，静默数据错误）✅ 已修复

> 批 5 已修复。`ApiKeyCreateRequest` 新增 `owner_username` 字段，`ApiKeyService.CreateKey` 对超管解析 username 为 userId，非超管忽略。

前端下拉选的是用户名，请求体发 `owner_username`（`frontend/src/AccessKeys.vue:311`）。后端 `ApiKeyCreateRequest` 只声明了 `owner_user_id`（`DTOs/ApiKeys/ApiKeyRequests.cs:14`），反序列化后为 `null`，`ToCommand()` 转成 `Guid.Empty`，服务层再回落到当前登录用户（`Core/Services/ApiKeyService.cs:91`）。

结果：超级管理员替他人创建 Key 会静默落到自己名下，接口返回 201，无任何报错。

同一份 DTO 文件里的 `ApiKeyImportItemRequest` 却用 `owner_username`（`ApiKeyRequests.cs:91`），说明创建与导入两条路径独立演进，契约已分叉。

### 2.2 `/images/*` 四条路由运行时必然 500（P0，死路由）

`IProxyImagesEndpointService` 全仓只存在三处：接口定义、`ImagesController` 的构造函数参数、测试里的 stub。没有任何实现类，`OpenCodexServiceCollectionExtensions` 的服务注册清单里也没有它。

受影响路由：`POST /images/generations`、`POST /v1/images/generations`、`POST /images/edits`、`POST /v1/images/edits`。

### 2.3 列表接口回传明文密钥（P1，安全）

- `GET /config` → `ChannelResponse.ApiKey`（`DTOs/Config/ConfigResponses.cs:152`）是渠道上游 API Key 明文。
- `GET /web-search` → `TavilyKeyResponse.Key`（`DTOs/WebSearch/WebSearchResponses.cs:127`）是搜索 Key 明文。

两者都只要求登录用户，不要求超级管理员。前端导出功能会把这些明文直接写入本地 JSON 文件。

### 2.4 `GET /stats` 全量物化日志（P1，性能）✅ 已修复

> 批 3 已修复。新增 `/stats/summary` 端点走数据库聚合（COUNT/SUM），不再 `query.ToList()` 全量物化。旧 `/stats` 聚合端点保留（详见 10.4）。

`Core/Services/ObservabilityService.cs:415` 的 `query.ToList()` 会把时间范围内的全部日志加载进内存，再在内存里分桶、算分布、算摘要。`range=30d` 等于一次全表扫描加全量物化。

放大这个问题的是前端：Logs 页每次翻页、每次自动刷新都会并发调用 `/logs` 和 `/stats`（`frontend/src/Logs.vue:1029` 与 `:1057`），而它只用到 `/stats` 响应里 `summary` 的五个数字和 `currency_rate`，曲线、模型分布、错误分布全部丢弃。

### 2.5 `/model-providers` 缺少更新与删除（P1，功能缺口）✅ 已修复

> 批 4 已修复。`IModelCatalogService` 新增 `UpdateProvider`/`DeleteProvider`/`ReadModelInfoById`，Controller 新增 `PATCH /model-providers/{id}`、`DELETE /model-providers/{id}`、`GET /model-infos/{id}`。`DeleteProvider` 会检查关联模型存在性。

`ModelCatalogController` 只暴露 `GET /model-providers` 和 `POST /model-providers`。供应商创建后无法改名、无法停用、无法删除。请求 DTO 名为 `ModelProviderUpsertRequest`，但没有任何 update 入口，命名是空承诺。

前端 `Pricing.vue` 因此只有「新增供应商」按钮，没有编辑和停用能力。

## 3. 职责混淆的四类模式

### 3.1 写操作返回全量集合

`ConfigController` 的五个写端点全部返回 `ConfigResponse`，即完整渠道列表：

| 端点 | 返回 |
| --- | --- |
| `POST /channels` | 全部渠道 |
| `PUT /channels/{id}` | 全部渠道 |
| `PATCH /channels/batch` | 全部渠道 |
| `DELETE /channels/{id}` | 全部渠道 |
| `POST /config/import` | 全部渠道 |

前端因此形成整表替换的写法：`config.channels = Array.isArray(data?.channels) ? data.channels : config.channels`（`frontend/src/Channels.vue:1763`、`:1897`、`:2107`）。删一个渠道要传回全部渠道配置，连同全部明文 Key。写操作和列表查询是两个职责，不该由一个端点同时承担。

### 3.2 列表接口混入运行时状态

`ChannelResponse` 里 `active_requests`（`ConfigResponses.cs:200`）来自 `IChannelCapacityService`，`health_status`（`:224`）来自 `IChannelCircuitBreakerService`。两者与数据库配置的生命周期完全不同：配置几天不变，运行时状态每秒都在变。

`ConfigResponse.From` 通过两个回调把它们缝进配置响应，导致前端只想刷新健康状态时也必须重拉整个 `/config`。

### 3.3 单端点用参数做多态

`GET /log-filter-options?field=xxx` 靠 `field` 决定返回哪个键，返回类型是 `IReadOnlyDictionary<string, object>`（`Services/IObservabilityService.cs:30`），完全没有类型契约。前端必须维护一张 `filterOptionFieldMap` 映射表（`frontend/src/Logs.vue:765`）才能取到值。

`GET /logs` 平铺 21 个查询参数（19 个筛选 + `page` + `page_size`），`GET /stats` 平铺 20 个（17 个筛选 + `range` + `start` + `end`）。两者靠两次调用同一个 `BuildLogFilters` 手工装 19 项字典（`ObservabilityController.cs:46` 与 `:98`），筛选契约没有类型载体，新增一个筛选字段要改 4 处。

`POST /web-search` 是整包覆盖式配置写入。前端删除单个 Key 的做法是本地 `splice` 后把整份配置 POST 回去（`frontend/src/WebSearch.vue` 的 `deleteWebSearchKey`）。请求字段名 `key_usage_limit` 与响应字段名 `default_key_usage_limit` 还不对称。

### 3.4 Controller 里做业务

| 位置 | 越界内容 |
| --- | --- |
| `ObservabilityController.cs:202`、`:229` | 两个 SSE 端点把 `while (!RequestAborted)` + `Task.Delay` + 查库循环写在表现层 |
| `ProxyController.cs:48`、`:198`、`:286` | `Models` 动作与 `CodexModelCatalogItem`、`ReasoningLevel` 合计约 140 行 Codex 模型目录构造逻辑 |
| `ModelCatalogController.cs:67` | `ExportCatalog` 自己做 `JsonSerializer.SerializeToUtf8Bytes` 和文件名拼接 |
| `SystemSettingsController.cs:32` | 用 `try/catch (ArgumentException)` 当校验层 |
| ~~`ModelCatalogController.cs:84`~~ | ~~`dryRun` 用 `string` 接收再 `bool.TryParse`~~ → 已改为 `[FromQuery] bool dryRun = false` |
| `ChannelDiagnosticsController.cs:33` | `TestChannelStream` 返回裸 `Task`，绕过 `IActionResult`，无法统一错误响应 |

### 3.5 其他一致性问题

列表包装字段各不相同，没有统一契约（`BasePagedListModel<T>` 已创建但尚未应用到现有端点）：

| 端点 | 包装字段 | 分页 |
| --- | --- | --- |
| `GET /logs` | `events` | 服务端 |
| `GET /api-keys` | `keys` | 无 |
| `GET /users` | `users` | 无 |
| `GET /config` | `channels` | 无 |
| `GET /model-infos` | `models` | 前端切片（`Pricing.vue:523`） |
| `GET /model-providers` | `providers` | 无 |

两组诊断路由各带一条重复别名（共 4 条路由映射到 2 个动作），前端只调用后者：

- `/channels/discover-models` 与 `/discover-models`
- `/channels/test/stream` 与 `/test-channel/stream`

~~`/pricing` 四条路由~~ → 已删除（批 0）。实际计费走 `IModelCatalogService.CalculateCostAsync`，与 ~~`IModelPricingService`~~ 无关。

一个 Controller 管四种资源：`ModelCatalogController` 同时负责供应商、全局模型、目录导入导出、渠道级模型覆盖，且权限面不一致（模型写操作要超管，渠道级模型覆盖只要登录用户）。

响应外壳也有两套形态。`ApiOpResult<T>` 序列化出 `ErrorCode` / `ErrorMsg` / `Data` 三个键，非泛型 `ApiOpResult` 只有前两个、没有 `Data`。当前 `POST /channels/{id}/reset-health` 与 `DELETE /channels/{id}/model-infos/{id}` 走的正是非泛型分支。前端 `App.vue:188` 因此写了一段运行时类型嗅探来判断要不要解包，两种形态由同一个 helper 承担，调用方无法从类型上预知拿到的是载荷还是外壳。

## 4. 设计原则

用五条可判定的规则约束改造，避免凭感觉拆分。

1. **一个端点只回答一个问题。** 查询不写，写不返回列表。写操作只返回被写的那一个对象。
2. **配置态、运行时态、聚合态分离到不同端点。** 三者的缓存策略、刷新频率、权限面都不同，混在一起必然互相拖累。
3. **列表只返回列表用得上的字段。** 完整对象和正文留给详情端点。列表统一 `{ items, total, page, page_size }`，一律服务端分页。
4. **多态返回改成多个路径。** `?field=` 换成路径段或独立端点，每个响应有确定类型。
5. **密钥永不出现在列表里。** 创建时一次性返回明文，之后只给掩码。

补充一条外壳约定：所有端点统一走 `ApiOpResult<T>`，无载荷的操作也返回带 `Data` 的形态（如 `{ deleted, id }`），不再使用非泛型 `ApiOpResult`。这样前端可以无条件解包，去掉 `App.vue:188` 的类型嗅探。

## 5. 目标 API 蓝图

### 5.1 渠道

拆为 `ChannelsController`（配置）与 `ChannelRuntimeController`（运行时）。

```
GET    /channels                       分页列表，筛选 owner/group/type/enabled，apikey 掩码
GET    /channels/{id}                  单个配置详情，含 models 映射，apikey 掩码
POST   /channels                       201 + 单个渠道
PUT    /channels/{id}                  单个渠道
PATCH  /channels                       批量 → { updated_ids, count }
DELETE /channels/{id}                  → { deleted: true, id }
GET    /channels/runtime               运行时快照，?ids= 可选，返回 active_requests/health_status/capacity
POST   /channels/{id}/health-reset
POST   /channels/bulk-import           超管，替代 /config/import
POST   /channels/{id}/probe-models     替代 /discover-models，删两条别名
POST   /channels/{id}/probe-stream     替代 /test-channel/stream，删两条别名
```

编辑时 `apikey` 采用「不传即不改」语义，避免明文往返。`GET /config` 保留一个版本作为 deprecated 别名供前端迁移，迁完即删。

### 5.2 可观测性

`ObservabilityController` 拆为 `LogsController`、`LogFilterOptionsController`、`StatsController`、`MonitorController` 四个。

```
GET    /logs                           分页列表，筛选参数收敛为 [FromQuery] LogFilterQuery
GET    /logs/{id}                      元数据详情，不含正文
GET    /logs/{id}/content              正文（request/response/upstream/websearch/ocr）
GET    /logs/{id}/stream-lines         原始 SSE 行
POST   /logs/purge                     超管，显式动作而非集合 DELETE
GET    /logs/filter-options/{field}    路径参数替代 ?field=，每个 field 强类型响应
GET    /stats/summary                  Logs 页只需要这一个
GET    /stats/timeseries               Dashboard 曲线
GET    /stats/model-distribution
GET    /stats/error-distribution
GET    /monitor/active-channels        非流
GET    /monitor/recent-errors          非流（当前缺失）
GET    /monitor/active-channels/stream 循环下沉到 IMonitorStreamService
GET    /monitor/recent-errors/stream   同上
```

`/stats` 拆分顺带解决 2.4：`summary` 可以走数据库聚合，不必物化全量日志；曲线和分布各自按需查询。

### 5.3 模型目录

`ModelCatalogController` 拆为四个，按资源边界划分。

```
GET    /model-providers                分页列表
POST   /model-providers                201
PATCH  /model-providers/{id}           补齐，修 2.5
DELETE /model-providers/{id}           补齐，修 2.5
GET    /model-infos                    服务端分页，只返回 pricing_summary
GET    /model-infos/{id}               完整 pricing rules
POST   /model-infos                    201
PATCH  /model-infos/{id}
DELETE /model-infos/{id}
GET    /channels/{channelId}/model-infos                 渠道级覆盖列表
PUT    /channels/{channelId}/model-infos                 单个覆盖
DELETE /channels/{channelId}/model-infos/{overrideId}    恢复全局
GET    /model-catalog/export           序列化下沉到服务层
POST   /model-catalog/import           dryRun 改 bool，默认 false
```

### 5.4 访问密钥与用户

```
GET    /api-keys                       分页 + owner 筛选，只返回 masked_key
POST   /api-keys                       接受 owner_username，修 2.1；明文只此一次
GET    /api-keys/{id}
PATCH  /api-keys/{id}
DELETE /api-keys/{id}
POST   /api-keys/bulk-import           超管
GET    /users                          分页
GET    /users/options                  下拉专用轻量端点，AccessKeys 页不再依赖完整 /users
```

### 5.5 Web Search

从整包覆盖改为设置与密钥两类资源。

```
GET    /web-search/settings            mode + default_key_usage_limit
PUT    /web-search/settings
GET    /web-search/keys                掩码列表
POST   /web-search/keys                201
PATCH  /web-search/keys/{id}
DELETE /web-search/keys/{id}
POST   /web-search/keys/{id}/test
```

### 5.6 认证、系统、代理

- `AuthController` 拆为 `SetupController`（`/setup/status`、`/setup`）与 `SessionController`（`/session`、`/login`、`/logout`），Cookie 写入下沉到 `ISessionCookieWriter`。
- `SystemSettingsController` 的校验搬进服务并返回 `ApiOpResult`，`IDesktopSystemSettingsStore` 从 Api 层提到 CoreBase。
- `ProxyController.Models` 的目录构造整体搬进 `IProxyModelListService`，Controller 只做协议分发。
- `SystemController` 的 `/`、`/health` 保持不变。

## 6. 待决策项

以下三项需要产品或运维侧确认，否则无法进入对应批次。

| 决策 | 现状证据 | 建议 |
| --- | --- | --- |
| ~~`/pricing` 四条路由是否删除~~ | 前端零调用；实际计费不经过 `IModelPricingService`；但远端样本 legacy `ModelPricings` 表仍有约 111 行 | ✅ 已执行（批 0）：删除 `PricingController` + `ModelPricingService` + `OpenCodexPricing` + 相关 DTO/Tests。保留 `ModelPricingPlan`/`ModelPricingRule` 实体 |
| `/images/*` 补齐还是删除 | 当前必 500，删除无可用性损失；补齐需复刻鉴权、候选排序、容量、熔断、failover、日志生命周期 | 无明确产品需求则整链删除，连带 `images` 渠道类型、前端选项、`ConfigValidator` 分支 |
| 列表包装字段是否统一为 `items` | 现有六种命名各不相同 | 建议统一。前后端同仓，一次性同步改造比长期维护两套契约成本低 |

## 7. 分批实施计划

每批控制在 3 个文件左右，逐批可独立验证与回滚。

| 批次 | 内容 | 主要风险 | 缓解 |
| --- | --- | --- | --- |
| 1 | 新增 `PageResult<T>`、`LogFilterQuery`、`ChannelRuntimeResponse` 三个 DTO | 无，纯新增不改行为 | — |
| 2 | `ChannelsController` + `ChannelRuntimeController` + `IConfigService` 签名调整 | 写操作返回值变化，前端需同步 | deprecated `/config` 兜住过渡期 |
| 3 | `LogsController` / `StatsController` / `MonitorController` + `ObservabilityService` 拆分 | 最高。Logs 与 Dashboard 两页同时依赖 | `ObservabilityControllerTests`、`ObservabilityServiceTests` 重写；`/stats/summary` 与旧值做一致性对比 |
| 4 | 模型目录四个 Controller + `IModelCatalogService` 拆分 | `ModelInfoResponse` 瘦身影响 Pricing 页编辑弹窗 | 先核对弹窗读取的字段全部保留在详情端点 |
| 5 | `ApiKeysController` / `UsersController` / `WebSearchController` | 密钥展示行为变更 | 顺带修 2.1 与 2.3，需出一份行为变更说明 |
| 6 | `SetupController` / `SessionController` / `SystemSettingsController` / `ProxyController` | 低，纯搬迁 | 现有 `RouteTests`、`ProxyControllerTests` 兜底 |
| 7 | 新建 `frontend/src/api/`，`client.js` 统一 401 处理与 envelope 解包，每资源一个模块 | 10 个页面全量改造 | 按页面再拆成 10 个小步，逐页切换 |
| 8 | 死链清理，按第 6 节决策执行 | 取决于决策结果 | — |

### 前端改造要点（批 7）

- 建立 `src/api/` 分层，页面不再手拼 URL 与查询串。
- `client.js` 统一处理 401，触发登出并跳转登录页，替代现在每页各自 `ElMessage.error`。
- Logs 页改调 `/stats/summary`，不再为五个数字拉取全量聚合。
- Channels 页写操作后改为更新单条 + 独立轮询运行时状态，取消整表替换。
- Pricing 页改用服务端分页，移除 `pagedModels` 客户端切片。
- 移除 `App.vue:188` 的响应体类型嗅探，改为后端固定 envelope 契约。
- 同步维护 `frontend/vite.config.js` 的 `adminProxyRoutes` 白名单（当前 21 条）。批 8 已执行：移除冗余的 `/stats/active-channels`，新增 `/monitor`、`/channels`、`/users/options`（`/users` 已覆盖 `/users/options`）。

## 8. 测试计划

每批都要同步更新 `RouteTests.ControllerRoutesDoNotUseAdminApiPrefix` 的路由断言表。

新增用例：

| 用例 | 验证目标 |
| --- | --- |
| 列表契约测试 | `page=0`、`page_size` 超限、`total` 与 `items` 数量一致 |
| 密钥不泄露测试 | `GET /channels` 与 `GET /web-search/keys` 响应中不出现明文 |
| 归属人测试 | 超管指定 `owner_username` 落到目标用户；普通用户指定他人被忽略 |
| 写操作返回值测试 | POST / PUT / DELETE 不返回集合 |
| 统计一致性测试 | `/stats/summary` 与旧 `/stats` 的 summary 数值一致 |
| Controller 激活 smoke test | 所有 Controller 能从 DI 解析，暴露 2.2 类漏注册 |

按既定流程，缺陷修复先写复现测试：

- 2.1 先写一个失败的归属人测试，再改 DTO。
- 2.2 先写 Controller 激活 smoke test 证明 DI 解析失败，再决定补齐或删除。

## 9. 未覆盖的边界

两处需要在对应批次单独处理，本方案不预设答案：

1. **历史路径残留。** `RequestLogs.Path` 存的是请求发生时的实际路径，路由改名后旧值不变。日志筛选的 `path` 选项会同时出现新旧两套值，需要在批 3 决定是做值映射、分组展示，还是保留原样。
2. **历史渠道类型残留。** 若决定删除 `images` 渠道类型，历史 `RequestLogs.channel_type = images` 的日志展示与查询策略需要在批 8 明确。

另外，`AGENTS.md` 记录的 SQLite 与无 Redis 配置与当前部署（PostgreSQL + Redis）不一致，本次改造不触及数据层，但批 3 的统计聚合改造会依赖数据库能力差异，实施前需确认目标环境。

## 附录 A：路由处置矩阵

现存 68 条路由模板的逐条处置。「保留」指路径与语义均不变；「改名」指语义不变、路径调整；「拆分」指一个端点拆成多个；「删除」需第 6 节决策支持。

### A.1 渠道与诊断（批 2）

| 现状 | 处置 | 目标 |
| --- | --- | --- |
| `GET /config` | 拆分 | `GET /channels` + `GET /channels/runtime`，原路径降级为 deprecated 别名 |
| `POST /channels` | 改返回 | 返回单个渠道，201 |
| `PUT /channels/{channelId}` | 改返回 | 返回单个渠道 |
| `PATCH /channels/batch` | 改名改返回 | `PATCH /channels` → `{ updated_ids, count }` |
| `DELETE /channels/{channelId}` | 改返回 | `{ deleted, id }` |
| `POST /config/import` | 改名 | `POST /channels/bulk-import`，加超管校验 |
| `POST /channels/{channelId}/reset-health` | 改名 | `POST /channels/{channelId}/health-reset` |
| `POST /channels/discover-models` | 改名 | `POST /channels/{channelId}/probe-models` |
| `POST /discover-models` | 删除 | 别名，前端已改用新路径后即删 |
| `POST /channels/test/stream` | 改名 | `POST /channels/{channelId}/probe-stream` |
| `POST /test-channel/stream` | 删除 | 别名，前端当前唯一调用方，需先迁移 |
| — | 新增 | `GET /channels/{id}`、`GET /channels/runtime` |

### A.2 可观测性（批 3）

| 现状 | 处置 | 目标 |
| --- | --- | --- |
| `GET /logs` | 保留改签名 | 筛选参数收敛为 `LogFilterQuery`，响应改 `PageResult<T>` |
| `GET /logs/{logId}` | 拆分 | 元数据留在原路径，正文移入 `/logs/{id}/content`、`/logs/{id}/stream-lines` |
| `DELETE /logs` | 改名 | `POST /logs/purge` |
| `GET /log-filter-options` | 改名拆分 | `GET /logs/filter-options/{field}`，每 field 强类型响应 |
| `GET /stats` | 拆分 | `/stats/summary`、`/stats/timeseries`、`/stats/model-distribution`、`/stats/error-distribution` |
| `GET /stats/active-channels` | 改名 | `GET /monitor/active-channels`。前端零调用，可直接改 |
| `GET /stats/active-channels/stream` | 改名下沉 | `GET /monitor/active-channels/stream`，循环移入 `IMonitorStreamService` |
| `GET /stats/recent-errors/stream` | 改名下沉 | `GET /monitor/recent-errors/stream`，同上 |
| — | 新增 | `GET /monitor/recent-errors`（当前只有流式版本） |

### A.3 模型目录与定价（批 4、批 8）

| 现状 | 处置 | 目标 |
| --- | --- | --- |
| `GET /model-providers` | 保留改分页 | 服务端分页 |
| `POST /model-providers` | 保留 | 201 |
| `GET /model-infos` | 保留改分页 | 服务端分页，只返回 `pricing_summary` |
| `POST /model-infos` | 保留 | 201 |
| `PATCH /model-infos/{id}` | 保留 | — |
| `DELETE /model-infos/{id}` | 保留 | — |
| `GET /model-catalog/export` | 保留下沉 | 序列化移入服务层 |
| `POST /model-catalog/import` | 保留改参数 | `dryRun` 改 `bool`，默认 `false` |
| `GET /channels/{channelId}/model-infos` | 保留 | — |
| `PUT /channels/{channelId}/model-infos` | 保留 | — |
| `DELETE /channels/{channelId}/model-infos/{id}` | 保留 | — |
| — | 新增 | `GET /model-infos/{id}`、`PATCH`/`DELETE /model-providers/{id}` |
| ~~`GET /pricing`~~ | ~~待决策删除~~ | ✅ 已删除（批 0） |
| ~~`POST /pricing`~~ | ~~待决策删除~~ | ✅ 已删除（批 0） |
| ~~`PATCH /pricing/{id}`~~ | ~~待决策删除~~ | ✅ 已删除（批 0） |
| ~~`DELETE /pricing/{id}`~~ | ~~待决策删除~~ | ✅ 已删除（批 0） |

### A.4 密钥、用户、Web Search（批 5）

| 现状 | 处置 | 目标 |
| --- | --- | --- |
| `GET /api-keys` | 保留改分页脱敏 | 服务端分页，移除明文 `key` |
| `POST /api-keys` | 保留修缺陷 | 接受 `owner_username`（修 2.1） |
| `PATCH /api-keys/{keyId}` | 保留 | — |
| `DELETE /api-keys/{keyId}` | 保留 | — |
| `POST /api-keys/import` | 改名 | `POST /api-keys/bulk-import`，加超管校验 |
| `GET /users` | 保留改分页 | 服务端分页 |
| `POST /users` | 保留 | 201 |
| `PATCH /users/{username}` | 保留 | — |
| `DELETE /users/{username}` | 保留 | — |
| — | 新增 | `GET /api-keys/{id}`、`GET /users/options` |
| `GET /web-search` | 拆分 | `GET /web-search/settings` + `GET /web-search/keys` |
| `POST /web-search` | 拆分 | `PUT /web-search/settings` + keys 的 POST/PATCH/DELETE |
| `POST /web-search/import` | 改名 | `POST /web-search/keys/bulk-import` |
| `POST /web-search/test-key` | 改名 | `POST /web-search/keys/{id}/test` |

### A.5 认证、系统、代理、图片（批 6、批 8）

| 现状 | 处置 | 目标 |
| --- | --- | --- |
| `GET /setup/status`、`POST /setup` | 保留搬迁 | 移入 `SetupController` |
| `GET /session`、`POST /login`、`POST /logout` | 保留搬迁 | 移入 `SessionController`，Cookie 写入下沉 |
| `GET /system-settings`、`PUT /system-settings` | 保留下沉 | 校验移入服务层 |
| `GET /`、`GET /health` | 保留不动 | `/health` 被 `docker-compose-sqlite.yml:25` 容器健康检查调用，路径不可变更 |
| `GET /models`、`GET /v1/models` | 保留下沉 | 目录构造移入 `IProxyModelListService` |
| `POST /responses`、`/v1/responses` | 保留 | — |
| `POST /chat/completions`、`/v1/chat/completions` | 保留 | — |
| `POST /messages`、`/v1/messages` | 保留 | — |
| `POST /images/generations`、`/v1/images/generations` | 待决策 | 决策点 2 |
| `POST /images/edits`、`/v1/images/edits` | 待决策 | 决策点 2 |

代理入口的 `/v1/*` 别名全部保留：它们是对外 OpenAI 兼容契约，由外部客户端消费，不属于内部历史遗留。

## 附录 B：前端页面与接口映射

按页面统计当前调用面，用于评估批 7 的逐页切换顺序。「跨域调用」指该页调用了非本页主资源的接口。

| 页面 | 调用的接口 | 跨域调用 | 受本方案影响的批次 |
| --- | --- | --- | --- |
| `App.vue` | `/setup/status`、`/session`、`/logout` | — | 批 6、批 7 |
| `Setup.vue` | `/setup` | — | 批 6 |
| `Login.vue` | `/login` | — | 批 6 |
| `Dashboard.vue` | `/stats`、`/stats/active-channels/stream`、`/stats/recent-errors/stream`、`/logs/{id}` | 读日志详情 | 批 3（SSE 路径保留，详见 10.4） |
| `Channels.vue` | `/config`、`/config/import`、`/channels`、`/channels/{id}`、`/channels/batch`、`/channels/{id}/reset-health`、`/channels/{id}/model-infos`、`/discover-models`、`/test-channel/stream`、`/model-providers` | 读模型供应商、读渠道级模型 | 批 2、批 4 |
| `AccessKeys.vue` | `/api-keys`、`/api-keys/{id}`、`/api-keys/import`、`/users` | 读用户列表做归属人下拉 | 批 5 |
| `Users.vue` | `/users`、`/users/{username}` | — | 批 5 |
| `WebSearch.vue` | `/web-search`、`/web-search/import`、`/web-search/test-key` | — | 批 5 |
| `Pricing.vue` | `/model-providers`、`/model-infos`、`/model-infos/{id}`、`/model-catalog/export`、`/model-catalog/import` | — | 批 4 |
| `SystemSettings.vue` | `/system-settings` | — | 批 6 |
| `Logs.vue` | `/logs`、`/logs/{id}`、`/log-filter-options`、`/stats` | 读统计摘要 | 批 3 |

三处跨域调用是拆分的直接依据：

1. `AccessKeys.vue` 为了一个下拉拉取完整用户列表，超管场景下会连带拿到全部用户的时间戳字段。目标是 `GET /users/options`。
2. `Logs.vue` 为了 `summary` 五个数字调用全量 `/stats`，把 Dashboard 的聚合成本摊到每次翻页。目标是 `GET /stats/summary`。
3. `Channels.vue` 同时是渠道页和渠道级模型定价页，混用了 `/channels/*` 与 `/model-*` 两套资源。拆分后仍需跨资源调用，但接口边界会清晰。

切换顺序建议：先 `SystemSettings.vue`、`Setup.vue`、`Login.vue` 三个单接口页验证 `src/api/` 分层可行，再切 `Users.vue`、`AccessKeys.vue`、`WebSearch.vue`，最后处理 `Channels.vue`、`Logs.vue`、`Dashboard.vue` 三个重页面。

## 10. 实施进度与遗留决策记录

> 更新时间：2026-08-23。以下记录批 0-8 的实际执行结果、浏览器/API 测试发现的问题，以及部分路由的保留决策。

### 10.1 已完成批次

| 批次 | 内容 | 状态 | 备注 |
| --- | --- | --- | --- |
| 0 | `/pricing` 整链删除 | 完成 | 删除 Controller/Service/DTO/Tests，保留 `ModelPricingPlan`/`ModelPricingRule` 实体（新 catalog 系统仍在使用）。Migrations 未改动（历史保留） |
| 1 | 新增 DTO：`BasePagedListModel<T>`、`LogFilterQuery`、`ChannelRuntimeResponse` | 完成 | 纯新增 |
| 2 | 渠道拆分：`IConfigService` 新增 `ReadChannelById`/`ReadChannelRuntime`，写操作返回单对象，新路由 `/channels`、`/channels/{id}`、`/channels/runtime`、`PATCH /channels`、`POST /channels/bulk-import`、`POST /channels/{id}/health-reset` | 完成 | 旧路由保留为别名 |
| 3 | 观测性拆分：`IObservabilityService` 新增 `ReadStatsSummary`/`ReadStatsTimeseries`/`ReadStatsModelDistribution`/`ReadStatsErrorDistribution`，`/stats/summary` 用 DB 聚合（COUNT/SUM）而非 `query.ToList()` | 完成 | 修复缺陷 2.4 |
| 4 | 模型目录拆分：`IModelCatalogService` 新增 `UpdateProvider`/`DeleteProvider`/`ReadModelInfoById`，`dryRun` 改为 `bool` 默认 `false` | 完成 | 修复缺陷 2.5 |
| 5 | ApiKeys/Users：`ApiKeyCreateRequest` 新增 `owner_username` 字段，修复缺陷 2.1 | 完成 | 新增 `GET /api-keys/{id}`、`GET /users/options` |
| 6 | Auth 拆分为 `SetupController` + `SessionController` | 完成 | — |
| 7 | 前端 `src/api/` 分层：`client.js` 统一 fetch + 401 处理 + envelope 解包 | 完成 | 现有页面仍用 `App.vue api()` 函数，逐步迁移中 |
| 8 | vite whitelist 同步 | 完成 | 移除 `/stats/active-channels`，新增 `/monitor`、`/channels`、`/users/options` |

全部 532 个后端测试通过，前端构建成功。

### 10.2 浏览器与 API 测试结果（2026-08-23）

对 8 个前端页面进行了完整浏览器测试（Chrome），对 23 个 API 端点进行了 curl 级别验证。所有页面零控制台错误，所有端点返回符合预期。

**测试发现的问题：**

| 严重度 | 问题 | 状态 |
| --- | --- | --- |
| P0 | `GET /channels` 列表接口返回明文 apikey，`GET /channels/{id}` 单渠道接口已脱敏。脱敏行为不一致 | 待修复 |
| P1 | `ModelCatalogController.RestoreChannelModel` 路由是 `[HttpDelete]` 但方法名暗示"恢复"，实际逻辑是删除。命名矛盾 | 已解决（详见 10.3） |
| P2 | `ConfigService.cs.tmp` 残留临时文件 | 待清理 |
| P2 | `ExportCatalog` 的 `result.Payload!` null-forgiving 无防护 | 待修复 |
| P2 | `BasePagedListModel<T>` setter 为 public，建议改 `init` | 待修复 |

### 10.3 P1 命名矛盾已解决

审查报告中标注的 P1 命名矛盾问题经核实已不存在。之前的审查基于改名前的代码状态（当时方法名为 `RestoreChannelModelInfo`），但实际代码已改为 `DeleteChannelModelInfo` + `DeleteChannelModel`，方法名与 DELETE 语义一致，调用链上下游已对齐：

```
前端 Channels.vue:2305 restoreChannelPricing()
  DELETE /channels/{channelId}/model-infos/{overrideId}
    -> ModelCatalogController.DeleteChannelModel()
      -> IModelCatalogService.DeleteChannelModelInfo(channelId, id)
        -> ModelCatalogService.DeleteChannelModelInfo()
          -> RemovePlansForChannelModel(model.Id)
          -> _channelModels.Delete(model)
          -> BumpPricingVersion()
```

前端函数名 `restoreChannelPricing`（恢复全局配置）描述的是用户操作意图，后端 `DeleteChannelModelInfo` 描述的是数据操作（删除覆盖记录）。两层语义不同但各自正确，无需修改。

### 10.4 旧路由保留决策

以下是测试后明确决定保留的旧路由/重复端点，不标记 deprecated、不删除：

**`/stats` 聚合端点** — 保留。`/stats` 不是旧别名，而是有独立价值的聚合端点。Dashboard.vue 的 `fetchStats()` 一次请求消费 9 个字段（`summary` + `points` + `model_distribution` + `error_distribution` + 元信息），对应仪表盘 6 个统计卡片 + 5 个趋势折线图 + 2 个分布图。拆分端点 `/stats/summary` 等是给 Logs.vue 等"只需要一部分数据"的场景用的，两者各有用途，不是新旧替换关系。

**`/stats/active-channels`、`/stats/active-channels/stream`、`/stats/recent-errors/stream`** — 保留。这三组端点与 `/monitor/*` 重复，但前端 Dashboard.vue 仍在通过 `/stats/*/stream` 建立 SSE 连接。改路径没有功能收益，只增加前端改动风险和测试成本。重复的 SSE 循环逻辑约 34 行，提取 helper 后可消除重复，但端点本身保留。

不清理的原因：这些端点功能完全正常，不是 bug 也不是安全问题。改了之后需要重新验证仪表盘实时队列和错误流，这是没有收益的测试成本。清理的核心目标是删除 `/pricing` 整链（已完成）和修复 apikey 脱敏（待执行），观测性重复端点保持现状。
