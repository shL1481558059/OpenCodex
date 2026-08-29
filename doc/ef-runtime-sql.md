# OpenCodex 业务运行时 SQL 清单

## 1. 本文范围

本文只回答一件事：**服务跑起来以后，业务代码通过 EF Core 实际发给数据库的 SQL 是哪些，分别由谁触发、干什么用、有什么代价。**

不在范围内：迁移 DDL、`__EFMigrationsHistory` / `__EFMigrationsLock` 等 EF 自管语句、`PRAGMA journal_mode` 这类连接初始化语句、测试代码自己写的断言查询。下文出现的每条 SQL 都能定位到 `OpenCodex.Core` 里的一个业务方法。

两个 provider 都覆盖：SQLite（桌面与轻量单实例默认）和 PostgreSQL（当前生产默认）。同一段 LINQ 在两边翻译结果不同的地方会成对给出。

## 2. 取证方法

SQL 文本不是从代码推断的，是抓下来的。

1. 在 `OpenCodexDbContextFactory.ConfigureWarnings` 临时挂一个 `LogTo(RelationalEventId.CommandExecuted)`（这个位置能同时覆盖 DI 创建的 context、手动 `new` 的 context 和后台任务），跑完整测试套件，得到 24705 条命令执行记录。
2. 去掉 DDL、EF 自管语句和迁移期数据搬迁语句后，业务表上剩 **215 个去重 SQL 形状**（其中少数来自测试自己的断言查询，正文只收录能定位到业务方法的那些）。形状数远大于查询点数，因为同一段 LINQ 会随过滤条件组合产生多个变体。
3. 测试没覆盖到的管理台写路径（改 Key、删用户级联、Web Search 整体替换、目录清理）另用一个直接调用 `EfRepository` 的探针补跑，SQL 与服务层完全同源。
4. PostgreSQL 侧测试套件全部硬编码 sqlite，所以另起 `postgres:17-alpine` 容器，用同一个探针程序按服务层调用顺序跑一遍，抓到 45 条真实 Npgsql SQL。
5. 少数两边差异用 `ToQueryString()` 在同一段 LINQ 上做逐条对照（不需要数据库连接）。

抓取完成后临时代码已还原，仓库里没有留下探针。

## 3. 一次代理请求的完整 SQL 时序

这是最值得先看懂的一节。以 `POST /v1/chat/completions`（流式）为例，一次请求从鉴权到落账，按顺序执行下面这些 SQL。标注「缓存」的步骤在命中缓存时整段跳过。

| # | 阶段 | SQL | 缓存 |
|---:|---|---|---|
| 1 | Bearer 鉴权取 Key | `SELECT ... FROM "AccessApiKeys" WHERE "KeyHash" = @hash LIMIT 1` | L1+L2，60s |
| 2 | 回写最后使用时间 | `UPDATE "AccessApiKeys" SET "LastUsedAt" = @p0 WHERE "Id" = @p1` | 随 1 |
| 3 | 取 Key 的 owner | `SELECT ... FROM "Users" WHERE "Id" = @userId LIMIT 1` | L1+L2，60s |
| 4 | 取 owner 的渠道集合 | `SELECT ... FROM "Channels" WHERE "OwnerUserId" = @ownerUser_Id ORDER BY "OwnerUserId", "Position", "Id"` | 有 |
| 5 | 补渠道 owner 名 | `SELECT ... FROM "Users" WHERE "Id" IN (@ownerIds1, ...)` | 随 4 |
| 6 | 请求模型 → 上游模型 | `SELECT "UpstreamModel" FROM "ChannelModelMappings" WHERE "ChannelId" = @channel_Id AND "Enabled" ORDER BY "Position"` | 无 |
| 7 | 日志 owner 解析 | `SELECT ... FROM "Users" WHERE "Username" = @normalized LIMIT 1` | 无 |
| 8 | 写入队日志 | `INSERT INTO "RequestLogs" (35 列) VALUES (...)` | 无 |
| 9 | 存请求头与请求体 | 内容寻址事务，见 5.7 | 无 |
| 10 | 标记处理中 | `SELECT ... FROM "RequestLogs" WHERE "Id" = @requestLogId LIMIT 1` + `UPDATE "RequestLogs" SET (34 列)` | 无 |
| 11 | 存上游请求体 | 内容寻址事务 | 无 |
| 12 | 每次上游尝试 | 再走一遍 8/9（`RequestType = 'attempt'`） | 无 |
| 13 | 完成时取回日志 | `SELECT ... FROM "RequestLogs" WHERE "Id" = @requestLogId LIMIT 1` | 无 |
| 14 | 计费：渠道级模型 | `SELECT ... FROM "ChannelModelInfos" WHERE "ChannelId" = @channelId AND "Enabled"` | 定价结果有缓存 |
| 15 | 计费：全局模型 | `SELECT ... FROM "ModelInfos" WHERE "Enabled" AND "Scope" = 'global' AND "ChannelId" IS NULL` | 同上 |
| 16 | 计费：价格方案 | `SELECT ... FROM "ModelPricingPlans" WHERE "ModelInfoId" = @modelInfoId AND ... ORDER BY "UpdatedAt" DESC LIMIT 1` | 同上 |
| 17 | 计费：规则 | `SELECT ... FROM "ModelPricingRules" WHERE "PricingPlanId" = @planId AND "Enabled"` | 无，每次现查 |
| 18 | 计费：provider 排序 | `SELECT ... FROM "ModelProviders"`（全表） | 无 |
| 19 | 落账 | `UPDATE "RequestLogs" SET (34 列) WHERE "Id" = @p34` | 无 |
| 20 | 存响应体等槽位 | 内容寻址事务，一次写多个槽位 | 无 |
| 21 | 认领 OCR 子日志 | `SELECT ... WHERE "RequestType" = 'ocr' AND "RequestId" = @ AND "ParentRequestLogId" IS NULL` + `UPDATE "RequestLogs" SET "ParentRequestLogId" = @p0` | 无 |

两点提醒：第 12 步意味着**重试次数直接放大写入量**，一次三连重试的请求会写 4 条日志、至少 8 次内容寻址事务；第 18 步的 providers 全表查询在捕获里执行了 188 次，是执行次数第二高的业务查询。

管理台侧还有一个公共前置：每个带 Cookie 的请求都会执行一次

```sql
SELECT "u"."Id", "u"."CreatedAt", "u"."Enabled", "u"."PasswordHash", "u"."Role", "u"."UpdatedAt", "u"."Username"
FROM "Users" AS "u"
WHERE "u"."Id" = @currentUser_UserId
LIMIT 1
```

来自 [SessionService.cs](path/to/repo/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/SessionService.cs:27) 的 `RequireUser`，用于复核用户是否仍存在且启用。捕获里执行 178 次，是执行次数最高的业务查询，且**没有缓存**。

## 4. 按服务逐条清单

### 4.1 会话与登录

源码：[SessionService.cs](path/to/repo/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/SessionService.cs:27)、[AuthService.cs](path/to/repo/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/AuthService.cs:39)

| 入口 | SQL | 作用 |
|---|---|---|
| 所有带 Cookie 的接口 | `SELECT ... FROM "Users" WHERE "Id" = @currentUser_UserId LIMIT 1` | 复核登录态 |
| `GET /setup/status` | `SELECT EXISTS (SELECT 1 FROM "Users" AS "u")` | 判断是否需要引导初始化 |
| `POST /setup` | `SELECT EXISTS (SELECT 1 FROM "Users" AS "u")` | 已有用户则拒绝重复初始化 |
| `POST /login` | `SELECT ... FROM "Users" WHERE "Username" = @username LIMIT 1` | 取密码哈希校验 |
| 启动播种超管 | `SELECT EXISTS (SELECT 1 FROM "Users" WHERE "Username" = 'admin')` + `INSERT INTO "Users" (7 列)` | 环境变量里的管理员落库 |

`Username` 上有唯一索引，这几条都是索引点查。

### 4.2 代理 Bearer 鉴权

源码：[ProxyAccessService.cs](path/to/repo/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/Proxy/ProxyAccessService.cs:104)

```sql
-- 1. KeyHash 唯一索引点查
SELECT "a"."Id", "a"."CreatedAt", "a"."Enabled", "a"."KeyHash", "a"."KeyPlaintext",
       "a"."KeyPrefix", "a"."KeySuffix", "a"."LastUsedAt", "a"."Name", "a"."OwnerUserId", "a"."UpdatedAt"
FROM "AccessApiKeys" AS "a"
WHERE "a"."KeyHash" = @hash
LIMIT 1

-- 2. 只更新一列：全项目唯一使用部分列更新的写操作
UPDATE "AccessApiKeys" SET "LastUsedAt" = @p0
WHERE "Id" = @p1
RETURNING 1;          -- PostgreSQL 侧没有 RETURNING

-- 3. owner 校验
SELECT ... FROM "Users" AS "u" WHERE "u"."Id" = @userId LIMIT 1
```

背景：这三条在热路径上，所以 apikey 和 user 分成两个缓存键，TTL 60 秒，改 Key 或改用户时各自精准失效。作用上要注意第 2 条是**读路径里的隐藏写**，每次缓存未命中都会产生一次 UPDATE。

### 4.3 渠道路由

源码：[ProxyRouteService.cs](path/to/repo/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/Proxy/ProxyRouteService.cs:424)

```sql
-- 指定 owner 时先解析用户
SELECT ... FROM "Users" AS "u" WHERE "u"."Username" = @normalizedOwnerUsername LIMIT 1

-- 渠道集合，命中 IX_Channels_OwnerUserId_Position
SELECT "c"."Id", "c"."ApiKey", "c"."AuthMode", "c"."BaseUrl", "c"."Capacity",
       "c"."CircuitBreakDurationSeconds", "c"."CompatJson", "c"."CreatedAt", "c"."Enabled",
       "c"."GroupName", "c"."HeadersJson", "c"."ModelsJson", "c"."Name", "c"."OwnerUserId",
       "c"."Position", "c"."Priority", "c"."RetryCount", "c"."TimeoutSeconds", "c"."Type", "c"."UpdatedAt"
FROM "Channels" AS "c"
WHERE "c"."OwnerUserId" = @ownerUser_Id
ORDER BY "c"."OwnerUserId", "c"."Position", "c"."Id"

-- 手动补 owner 名（项目禁用导航属性，不生成 JOIN）
SELECT ... FROM "Users" AS "u" WHERE "u"."Id" IN (@ownerIds1, @ownerIds2)
```

说明：渠道全字段取回是必要的，路由要用到 `Priority`、`Capacity`、`CompatJson`、`ModelsJson`。owner 不存在时源码故意**不写缓存**，避免新建用户后读到陈旧空集。熔断、容量、亲和这三个能力全部走内存或 Redis，不产生 SQL。

### 4.4 渠道配置管理

源码：[ConfigService.cs](path/to/repo/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/ConfigService.cs:323)

| 入口 | SQL | 作用 |
|---|---|---|
| `GET /channels` | `SELECT ... FROM "Channels"` 全表 + `SELECT ... FROM "Users" WHERE "Id" IN (...)` | 列出全部渠道（10 秒缓存），排序在内存里做 |
| `GET /channels/{id}` | `SELECT ... FROM "Channels" WHERE "Id" = @channelId LIMIT 1`，非超管再加 `AND "OwnerUserId" = @ownerUser_Id` | 越权隔离靠这个条件 |
| `POST /channels` | `SELECT ... WHERE "OwnerUserId" = @ AND "Id" = @parsedId LIMIT 1`；`SELECT ... WHERE "OwnerUserId" = @ AND "Name" = @channelName LIMIT 1` | 建前查重（ID 与名字各一次） |
| `PUT /channels/{id}` | `SELECT ... WHERE "OwnerUserId" = @ AND "Id" <> @existing_Id AND "Name" = @nextName LIMIT 1` | 改名查重，排除自身 |
| 同上 | `UPDATE "Channels" SET (19 列) WHERE "Id" = @p19` | 全列写回 |
| `PATCH /channels/batch` | `SELECT ... FROM "Channels" WHERE "Id" IN (@ids1, ...)` + 每行一条 `UPDATE` | 批量改分组/启用/优先级 |
| `POST /channels/bulk-import` | `SELECT ... FROM "Channels"`（或按 owner 过滤）+ 批量 `INSERT` | 导入 |
| `DELETE /channels/{id}` | `DELETE FROM "Channels" WHERE "Id" = @p0` + `SELECT ... FROM "ChannelModelMappings" WHERE "ChannelId" IN (...)` + 逐条 `DELETE` | 无数据库外键，映射靠代码级联 |
| 渠道保存后同步映射 | `SELECT ... FROM "ChannelModelMappings" WHERE "ChannelId" = @ids1` + 逐条 `DELETE` + 批量 `INSERT` | 用 `ModelsJson` 重建映射表 |

两处值得注意。第一，`UPDATE "Channels"` 是 19 列全写，意味着每次改任何字段都会重写 `ApiKey` 和 `HeadersJson`，做字段级审计或加密时要考虑这一点。第二，渠道与映射之间没有数据库外键，删除依赖代码里显式 `DeleteMappingsForChannels`，漏调用就会留下孤儿映射。

### 4.5 模型目录管理

源码：[ModelCatalogService.cs](path/to/repo/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/ModelCatalogService.cs:85)

```sql
-- provider 列表 / provider 字典：捕获里 188 次，全表无过滤
SELECT "m"."Id", "m"."Code", "m"."CreatedAt", "m"."Enabled", "m"."Name", "m"."SortOrder",
       "m"."Source", "m"."UpdatedAt"
FROM "ModelProviders" AS "m"

-- 新建 provider 前查 code 唯一
SELECT EXISTS (SELECT 1 FROM "ModelProviders" AS "m" WHERE "m"."Code" = @code)

-- 新 provider 的排序值
SELECT MAX("m"."SortOrder") FROM "ModelProviders" AS "m"

-- 删 provider 前确认没有挂模型
SELECT EXISTS (SELECT 1 FROM "ModelInfos" AS "m" WHERE "m"."ProviderId" = @id)

-- 全局模型列表（管理台）
SELECT ... FROM "ModelInfos" AS "m" WHERE "m"."Scope" = 'global' AND "m"."ChannelId" IS NULL

-- 模型 key 查重
SELECT EXISTS (SELECT 1 FROM "ModelInfos" AS "m"
  WHERE "m"."Scope" = @scope AND "m"."ChannelId" IS NULL AND "m"."ModelKey" = @modelKey)

-- 渠道模型列表
SELECT ... FROM "ChannelModelInfos" AS "c" WHERE "c"."ChannelId" = @channel_Id ORDER BY "c"."UpdatedAt" DESC
```

列表接口有 N+1：`ToModelResponse` / `ToChannelModelResponse` 对**每个模型**再查一次价格方案，`ToPlanResponse` 再对每个方案查一次规则。

```sql
-- 每个模型一次
SELECT ... FROM "ModelPricingPlans" AS "m"
WHERE "m"."ModelInfoId" = @model_Id AND "m"."ChannelId" IS NULL
ORDER BY "m"."UpdatedAt" DESC
LIMIT 1

-- 每个方案一次
SELECT ... FROM "ModelPricingRules" AS "m" WHERE "m"."PricingPlanId" = @plan_Id ORDER BY "m"."BillingItem"
```

导入导出与官方目录同步（`POST /model-catalog/import`、`/model-catalog/sync`）会包在一个显式事务里：先全量读 `ModelProviders`、`ModelInfos`、`ModelPricingPlans`、`ModelPricingRules` 建索引字典，再按需 `INSERT` / `UPDATE` / `DELETE`，最后一次 `SaveChanges` 提交。这是唯一会出现"批量插入几十上百行"的业务路径。

### 4.6 计费定价解析

源码：[ModelCatalogService.cs](path/to/repo/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/ModelCatalogService.cs:1260)

每次请求收尾都要算钱，链路是"渠道级模型 → 全局模型 → 价格方案 → 计费规则"。

```sql
-- 1. 渠道级模型：只按 channel 过滤，模型名匹配在内存做
SELECT ... FROM "ChannelModelInfos" AS "c" WHERE "c"."ChannelId" = @channelId AND "c"."Enabled"

-- 2. 全局模型：把全部启用的全局模型取回内存打分
SELECT ... FROM "ModelInfos" AS "m"
WHERE "m"."Enabled" AND "m"."Scope" = 'global' AND "m"."ChannelId" IS NULL

-- 3. 方案：同一模型可能有多份，取最近更新
SELECT ... FROM "ModelPricingPlans" AS "m"
WHERE "m"."ModelInfoId" = @modelInfoId AND "m"."ChannelModelInfoId" IS NULL
  AND "m"."ChannelId" IS NULL AND "m"."Enabled"
ORDER BY "m"."UpdatedAt" DESC
LIMIT 1

-- 渠道级方案走另一分支
SELECT ... FROM "ModelPricingPlans" AS "m"
WHERE "m"."ChannelModelInfoId" = @channelModelInfoId AND "m"."ChannelId" = @channelId AND "m"."Enabled"
ORDER BY "m"."UpdatedAt" DESC
LIMIT 1

-- 4. 规则：input / output / cache_read / cache_write 各一条
SELECT "m"."Id", "m"."BillingItem", "m"."BillingMode", "m"."Enabled", "m"."OffPeakEnabled",
       "m"."OffPeakTiersJson", "m"."OffPeakUnitPrice", "m"."PricingPlanId", "m"."TiersJson", "m"."UnitPrice"
FROM "ModelPricingRules" AS "m"
WHERE "m"."PricingPlanId" = @planId AND "m"."Enabled"
```

为什么第 1、2 步不在 SQL 里比模型名：匹配规则是"精确 / 前缀 / 通配 + 优先级 + 模式长度 + provider 排序"的复合打分，写不进 `WHERE`，所以代码显式 `AsEnumerable()` 之后在内存排序取第一名。第 1 步在捕获里 55 次、第 2 步 98 次，是这个域里最热的两条。定价解析结果按 `channelId + upstreamModel` 缓存，规则和 provider 因为"是小表索引查询"而每次现查（源码注释写明了这个取舍）。

峰谷分时的判断（`OffPeakEnabled`、`OffPeakWindowsJson`、`TimeZoneId`）全部在内存完成，不产生额外 SQL。

### 4.7 请求日志写入

源码：[ProxyLogService.cs](path/to/repo/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/Proxy/ProxyLogService.cs:47)

```sql
-- 入队：35 列 INSERT
INSERT INTO "RequestLogs" ("Id", "ApiKeyId", "CacheReadTokens", "CacheWriteTokens", "CachedTokens",
  "ChannelId", "ClientIp", "CompletedAt", "ConversationKey", "ConversationTurnId", "ConversationWindowId",
  "Cost", "CostCurrency", "CreatedAt", "DurationMs", "Error", "InputTokens", "IsStream", "LifecycleStatus",
  "Method", "Model", "OutputTokens", "OwnerUserId", "ParentRequestLogId", "Path", "PreviousResponseId",
  "PricingModelInfoId", "PricingPlanId", "PricingSnapshotJson", "ProcessingStartedAt", "RequestId",
  "RequestType", "StatusCode", "TtftMs", "UpstreamModel")
VALUES (@p0, ..., @p34);

-- 标记处理中 / 落账：先按主键取回（tracking 查询）
SELECT ... FROM "RequestLogs" AS "r" WHERE "r"."Id" = @requestLogId LIMIT 1

-- 再 34 列全写
UPDATE "RequestLogs" SET "ApiKeyId" = @p0, "CacheReadTokens" = @p1, ..., "UpstreamModel" = @p33
WHERE "Id" = @p34
RETURNING 1;
```

为什么是全列 UPDATE：仓储的 `Update` 走 `DbSet.Update(entity)`（[EfRepository.cs](path/to/repo/opencodex_proxy/src/Libraries/OpenCodex.Data/EfRepository.cs:61)），EF 把整个实体标成 `Modified`，即使只改了一个 `LifecycleStatus`。后果有两个：写放大（每条日志至少 1 次 35 列 INSERT + 2 次 34 列 UPDATE），以及**同一条日志被两个路径先后更新时，后写者会用自己内存里的旧值盖掉先写者刚落库的列**。流式请求的 TTFT 与最终 usage 分别在不同时机写入，这个风险是真实的。

OCR 子日志认领（主请求完成时把先前独立记录的 OCR 日志挂到自己名下）：

```sql
SELECT ... FROM "RequestLogs" AS "r"
WHERE "r"."RequestType" = 'ocr' AND "r"."RequestId" = @context_RequestId AND "r"."ParentRequestLogId" IS NULL

UPDATE "RequestLogs" SET "ParentRequestLogId" = @p0 WHERE "Id" = @p1 RETURNING 1;
```

### 4.8 日志正文的内容寻址存储

源码：[LogContentStore.cs](path/to/repo/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/Proxy/LogContentStore.cs:17)

背景：请求体、上游请求/响应、SSE 原始行体积大且高度重复（同一 system prompt 会出现在成千上万条日志里）。方案是正文按 SHA256 切块去重存 `LogContentBlocks`，`LogContentManifests` 描述一份完整正文，`LogContentManifestChunks` 记顺序，`RequestLogContentRefs` 把某条日志的某个槽位指向某份清单。**这是全项目唯一手写原生 SQL 的模块。**

写一次正文（SQLite）：

```sql
BEGIN TRANSACTION;

-- 1. 记下这些槽位原来指向的清单，留给后面判断孤儿
SELECT DISTINCT "r"."ManifestId" FROM "RequestLogContentRefs" AS "r"
WHERE "r"."RequestLogId" = @requestLogId AND "r"."Slot" = @slots1

-- 2. 物理块去重写入：靠 Sha256 唯一索引实现"存在就跳过"
INSERT OR IGNORE INTO "LogContentBlocks"
  ("Id", "Sha256", "RawLength", "StoredLength", "Compression", "Data", "CreatedAt")
VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6);
SELECT ... FROM "LogContentBlocks" AS "l" WHERE "l"."Sha256" IN (@hashes1, ...)

-- 3. 清单同理，插入成功才写 chunk
INSERT OR IGNORE INTO "LogContentManifests" ("Id", "Sha256", "RawLength", "ChunkCount", "Encoding")
VALUES (@p0, @p1, @p2, @p3, @p4);
INSERT INTO "LogContentManifestChunks" ("Id", "BlockId", "ManifestId", "Ordinal", "RawLength") VALUES (...);
SELECT ... FROM "LogContentManifests" AS "l" WHERE "l"."Sha256" IN (@hashes1, ...)

-- 4. 覆盖旧引用
DELETE FROM "RequestLogContentRefs" AS "r"
WHERE "r"."RequestLogId" = @requestLogId AND "r"."Slot" = @slots1
INSERT INTO "RequestLogContentRefs" ("Id", "ManifestId", "RequestLogId", "Slot") VALUES (@p0, @p1, @p2, @p3);

-- 5. 回收：先删没人引用的清单，再删没有任何 chunk 指向的块
SELECT "l"."Id" FROM "LogContentManifests" AS "l"
WHERE "l"."Id" = @orphanIds1
  AND NOT EXISTS (SELECT 1 FROM "RequestLogContentRefs" AS "r" WHERE "r"."ManifestId" = "l"."Id")

DELETE FROM "LogContentManifests" AS "l" WHERE "l"."Id" = @orphanIds1

DELETE FROM "LogContentBlocks" AS "l"
WHERE "l"."Id" = @orphanIds1
  AND NOT EXISTS (SELECT 1 FROM "LogContentManifestChunks" AS "l0" WHERE "l0"."BlockId" = "l"."Id")

COMMIT;
```

PostgreSQL 侧同一逻辑只有三处不同，但都在关键位置：

```sql
INSERT INTO "LogContentBlocks" (...) VALUES (@p0, ..., @p6) ON CONFLICT ("Sha256") DO NOTHING;
INSERT INTO "LogContentManifests" (...) VALUES (@p0, ..., @p4) ON CONFLICT ("Sha256") DO NOTHING;

DELETE FROM "RequestLogContentRefs" AS "r"
WHERE "r"."RequestLogId" = @mainLogId AND "r"."Slot" = ANY (@slots)
```

分叉靠 `Database.ProviderName` 字符串判断，加第三个 provider 时这里会直接抛 `Unsupported log database provider`。

读一份正文是 4 条 `AsNoTracking` 查询，然后在内存拼回字符串并校验 chunk 数量：

```sql
SELECT ... FROM "RequestLogContentRefs" AS "r" WHERE "r"."RequestLogId" = @requestLogId
SELECT ... FROM "LogContentManifests" AS "l" WHERE "l"."Id" IN (@manifestIds1, ...)
SELECT ... FROM "LogContentManifestChunks" AS "l" WHERE "l"."ManifestId" IN (...)
  ORDER BY "l"."ManifestId", "l"."Ordinal"
SELECT ... FROM "LogContentBlocks" AS "l" WHERE "l"."Id" IN (@blockIds1, ...)
```

`LogContentBlocks.Data` 是 BLOB，日志详情接口每次展开都要把这些块读回来，是这个域里最重的 I/O。

### 4.9 可观测读取

源码：[ObservabilityService.cs](path/to/repo/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/ObservabilityService.cs:657)

这是 SQL 形状最多的一块（`RequestLogs` 上 28 个形状），因为过滤条件动态拼接。基线永远带一条排除内部日志的条件：

```sql
WHERE "r"."RequestType" NOT IN ('attempt', 'diagnostic')
```

它来自一个刻意写成 `!=` 链的表达式（注释说明是为了保证两个 provider 都能翻译），EF 最终优化成 `NOT IN`。用户显式按 `request_type` 过滤时这个条件让位。

**`GET /logs` 列表**：一条计数 + 一条分页。

```sql
SELECT COUNT(*) FROM "RequestLogs" AS "r" WHERE "r"."RequestType" NOT IN ('attempt', 'diagnostic')

SELECT ... FROM "RequestLogs" AS "r"
WHERE "r"."RequestType" NOT IN ('attempt', 'diagnostic')
  AND "r"."Model" IS NOT NULL AND instr("r"."Model", @text) > 0
ORDER BY "r"."CreatedAt" DESC
LIMIT @p1 OFFSET @p
```

PostgreSQL 上后者是 `r."Model" LIKE @text_contains`（参数值形如 `%gpt%`，通配符包在参数里，执行计划可复用）。两种写法都用不上 `IX_RequestLogs_Model`。

可用的过滤字段（`ApplyLogFilter` 全量）：`request_id`、`conversation_key`、`conversation_turn_id`、`conversation_window_id`、`previous_response_id`、`model`、`upstream_model`、`path`、`client_ip`、`error` 走 `Contains`；`channel_id`、`api_key_id`、`request_type`、`status_code`、`is_stream`、`parent_request_log_id` 走等值；`owner_username` 先查 `Users` 换 ID 再等值；`created_from` / `created_to` 是数值区间；`request_status` 展开成生命周期与状态码的组合条件，例如

```sql
AND ("r"."LifecycleStatus" = 'success'
     OR ("r"."LifecycleStatus" IS NULL AND "r"."StatusCode" < 400
         AND ("r"."Error" IS NULL OR "r"."Error" = '')))
```

**列表附带的重试统计**：每页一条 GROUP BY。

```sql
-- SQLite
SELECT "r"."ParentRequestLogId" AS "ParentId", COUNT(*) AS "AttemptCount", COUNT(CASE
    WHEN "r"."LifecycleStatus" = 'failed'
      OR ("r"."StatusCode" IS NOT NULL AND "r"."StatusCode" >= 400)
      OR ("r"."Error" IS NOT NULL AND "r"."Error" <> '') THEN 1
END) AS "FailedAttemptCount"
FROM "RequestLogs" AS "r"
WHERE "r"."RequestType" = 'attempt'
  AND "r"."ParentRequestLogId" IN (@parentIds1, @parentIds2, @parentIds3, @parentIds4)
GROUP BY "r"."ParentRequestLogId"

-- PostgreSQL
SELECT r."ParentRequestLogId" AS "ParentId", count(*)::int AS "AttemptCount",
       count(*) FILTER (WHERE r."LifecycleStatus" = 'failed'
         OR (r."StatusCode" IS NOT NULL AND r."StatusCode" >= 400)
         OR (r."Error" IS NOT NULL AND r."Error" <> ''))::int AS "FailedAttemptCount"
FROM "RequestLogs" AS r
WHERE r."RequestType" = 'attempt'
  AND (r."ParentRequestLogId" = ANY (@parentIds)
       OR (r."ParentRequestLogId" IS NULL AND array_position(@parentIds, NULL) IS NOT NULL))
GROUP BY r."ParentRequestLogId"
```

SQLite 侧参数个数等于当页主日志条数，每页 500 条就是 500 个参数（SQLite 默认参数上限 999）。

**`GET /logs/{id}` 详情**：一条日志 + 一次 owner 名 + 一次内容寻址读取（4 条）+ 一条重试统计。

```sql
SELECT ... FROM "RequestLogs" AS "r" WHERE "r"."Id" = @guidId LIMIT 1
SELECT ... FROM "Users" AS "u" WHERE "u"."Id" = @log_OwnerUserId LIMIT 1
```

**`GET /log-filter-options` 过滤下拉**：数据库 `DISTINCT` + `LIMIT 200`。

```sql
SELECT "r0"."Model" FROM (
    SELECT DISTINCT "r"."Model" FROM "RequestLogs" AS "r"
    WHERE "r"."Model" IS NOT NULL AND "r"."Model" <> ''
) AS "r0"
ORDER BY "r0"."Model"
LIMIT @p
```

按 API Key 名或渠道名搜索时会生成嵌套子查询：

```sql
SELECT "r0"."ApiKeyId" FROM (
    SELECT DISTINCT "r"."ApiKeyId" FROM "RequestLogs" AS "r"
    WHERE "r"."RequestType" NOT IN ('attempt', 'diagnostic') AND "r"."ApiKeyId" IS NOT NULL
      AND "r"."ApiKeyId" IN (SELECT "a"."Id" FROM "AccessApiKeys" AS "a" WHERE instr("a"."Name", @queryText) > 0)
) AS "r0"
ORDER BY "r0"."ApiKeyId"
LIMIT @p
```

拿到 ID 列表后再补名字：`SELECT "a"."Id", "a"."Name" FROM "AccessApiKeys" WHERE "a"."Id" IN (...)`、`SELECT "c"."Id", "c"."Name" FROM "Channels" WHERE "c"."Id" IN (...)`。

**统计概览**走数据库聚合，这部分实现是对的：

```sql
-- SQLite
SELECT COUNT(*) FROM "RequestLogs" AS "r" WHERE "CreatedAt" >= @p AND "CreatedAt" < @p1 AND ...
SELECT COALESCE(SUM("r"."InputTokens"), 0) FROM "RequestLogs" AS "r" WHERE ...
SELECT COALESCE(SUM("r"."Cost"), 0.0) FROM "RequestLogs" AS "r" WHERE ...

-- PostgreSQL
SELECT count(*)::int FROM "RequestLogs" AS r WHERE ...
SELECT COALESCE(sum(r."InputTokens"), 0)::int FROM "RequestLogs" AS r WHERE ...
SELECT COALESCE(sum(r."Cost"), 0.0) FROM "RequestLogs" AS r WHERE ...
```

**但模型分布、错误分布、时间序列三个统计不走聚合。** 它们把时间窗内符合条件的日志全量取回，再在内存里分组分桶：

```sql
SELECT "r"."Id", "r"."ApiKeyId", ... /* 35 列全取 */
FROM "RequestLogs" AS "r"
WHERE "r"."CreatedAt" >= @resolved_StartTs AND "r"."CreatedAt" < @resolved_EndTs
  AND "r"."RequestType" NOT IN ('attempt', 'diagnostic')
-- 没有 LIMIT
```

这是全部业务 SQL 里最需要盯的一条：返回行数完全由用户选的时间范围决定，选"最近 30 天"就把 30 天日志读进进程内存。

**最近错误流**（`GET /monitor/recent-errors/stream`）：

```sql
SELECT ... FROM "RequestLogs" AS "r"
WHERE "r"."RequestType" NOT IN ('attempt', 'diagnostic')
  AND ("r"."LifecycleStatus" = 'failed'
       OR ("r"."LifecycleStatus" IS NULL AND ("r"."StatusCode" >= 400
           OR ("r"."Error" IS NOT NULL AND "r"."Error" <> ''))))
ORDER BY "r"."CreatedAt" DESC
LIMIT @p
```

非超管会先 `SELECT "u"."Id" FROM "Users" WHERE "u"."Username" = @currentUsername LIMIT 1` 再加 `OwnerUserId` 条件做数据隔离。

**清空日志**（superadmin）：先数三次行数（因为 TRUNCATE 不返回受影响行数），再按依赖顺序清空。

```sql
SELECT COUNT(*) FROM "RequestLogContentRefs" AS "r"
SELECT COUNT(*) FROM "LogContentBlocks" AS "l"
SELECT COUNT(*) FROM "RequestLogs" AS "r"

-- SQLite：5 条无 WHERE 的 DELETE
DELETE FROM "RequestLogContentRefs";
DELETE FROM "LogContentManifestChunks";
DELETE FROM "LogContentManifests";
DELETE FROM "LogContentBlocks";
DELETE FROM "RequestLogs";

-- PostgreSQL：一条 TRUNCATE
TRUNCATE TABLE "RequestLogContentRefs", "LogContentManifestChunks",
  "LogContentManifests", "LogContentBlocks", "RequestLogs" RESTART IDENTITY CASCADE;
```

顺序不能颠倒（清单对块是 `RESTRICT`）。作用范围是全库所有用户的日志，没有按 owner 或时间过滤。SQLite 的 `DELETE` 不把空间还给操作系统，清完 `.db` 文件不会变小，需要 `VACUUM`。

### 4.10 用户与 API Key 管理

源码：[UserService.cs](path/to/repo/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/UserService.cs:48)、[ApiKeyService.cs](path/to/repo/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/ApiKeyService.cs:36)

| 入口 | SQL | 说明 |
|---|---|---|
| `GET /users` | `SELECT ... FROM "Users" ORDER BY "Role", "Username"` | 无分页 |
| `GET /users/options` | `SELECT "u"."Username" FROM "Users" ORDER BY "Username"` | 只投影一列 |
| `POST /users` | `SELECT EXISTS (SELECT 1 FROM "Users" WHERE "Username" = @username)` + `INSERT` | 查重后插入 |
| `PATCH /users/{username}` | `SELECT ... WHERE "Username" = @username LIMIT 1` + `UPDATE "Users" SET (6 列)` | 全列写回 |
| `DELETE /users/{username}` | `SELECT ... WHERE "Username" = @username LIMIT 1`；`SELECT ... FROM "AccessApiKeys" WHERE "OwnerUserId" = @user_Id`；`SELECT ... FROM "Channels" WHERE "OwnerUserId" = @user_Id`；然后**逐行** `DELETE` | 代码级联，行数越多语句越多 |
| `GET /api-keys` | `SELECT ... FROM "AccessApiKeys"`（超管全量 / 否则按 owner）`ORDER BY ...` + `SELECT ... FROM "Users" WHERE "Id" IN (...)` | 补 owner 名 |
| `POST /api-keys` | `SELECT ... FROM "Users" WHERE "Username" = @ LIMIT 1` + `INSERT INTO "AccessApiKeys" (11 列)` | 明文列 `KeyPlaintext` 也会落库 |
| `PATCH /api-keys/{id}` | `SELECT ... WHERE "Id" = @keyId`（非超管加 `AND "OwnerUserId" = @`）+ `UPDATE "AccessApiKeys" SET (10 列)` | 改完主动失效鉴权缓存 |
| `DELETE /api-keys/{id}` | 同上查询 + `DELETE FROM "AccessApiKeys" WHERE "Id" = @p0` | 同样失效缓存 |

删用户的级联是逐行 DELETE：一个持有 200 个 Key、50 个渠道的用户被删时会产生 250 条 `DELETE`，全部在一个 `SaveChanges` 里提交。

顺带说明一个全局规律：这个项目所有实体删除都走 `DbSet.Remove` / `RemoveRange`，生成的 SQL 一律是按主键逐行删除，形如 `DELETE FROM "T" WHERE "Id" = @p0`（SQLite 结尾带 `RETURNING 1`）。集合条件删除只出现在内容寻址回收和清空日志两处。

### 4.11 Web Search 配置与 Key 轮换

源码：[WebSearchService.cs](path/to/repo/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/WebSearchService.cs:102)

```sql
-- 读配置：设置是单行表
SELECT "w"."Id", "w"."CreatedAt", "w"."KeyUsageLimit", "w"."Mode", "w"."UpdatedAt"
FROM "WebSearchSettings" AS "w" LIMIT 1

SELECT ... FROM "TavilyKeys" AS "t" ORDER BY "t"."Position", "t"."Id"

-- 整体替换（POST /web-search）：先全删再全插，源码注明"接受非原子"
SELECT ... FROM "TavilyKeys" AS "t"
DELETE FROM "TavilyKeys" WHERE "Id" = @p0 RETURNING 1;   -- 每行一条
INSERT INTO "TavilyKeys" ("Id", "ApiKey", ..., "UsageLimit") VALUES (...);
UPDATE "WebSearchSettings" SET "CreatedAt" = @p0, "KeyUsageLimit" = @p1, "Mode" = @p2, "UpdatedAt" = @p3
WHERE "Id" = @p4 RETURNING 1;

-- 运行时挑一把可用 key
SELECT ... FROM "TavilyKeys" AS "t"
WHERE "t"."Enabled" AND "t"."UsageCount" < "t"."UsageLimit"
ORDER BY "t"."Position", "t"."Id"
LIMIT 1

-- 用掉一次配额：读出来加一再全列写回
UPDATE "TavilyKeys" SET "ApiKey" = @p0, "CreatedAt" = @p1, "Enabled" = @p2, "Position" = @p3,
  "Provider" = @p4, "UpdatedAt" = @p5, "UsageCount" = @p6, "UsageLimit" = @p7
WHERE "Id" = @p8
RETURNING 1;
```

配额自增是"先 SELECT 再 UPDATE 绝对值"，不是 `SET UsageCount = UsageCount + 1`，也没有乐观并发列。并发搜索请求会读到同一个 `UsageCount` 并写回同一个值，实际用量会**超过** `UsageLimit`。

## 5. 两个 provider 的业务 SQL 差异

| 语义 | SQLite | PostgreSQL |
|---|---|---|
| `string.Contains` | `instr("c"."X", @text) > 0` | `c."X" LIKE @text_contains`（参数值 `%v%`）|
| 集合 `Contains` | `IN (@ids1, @ids2, ...)` 逐值展开 | `= ANY (@ids)` 数组参数 |
| `Count()` | `SELECT COUNT(*)` | `SELECT count(*)::int` |
| `Sum()` | `COALESCE(SUM(x), 0)` | `COALESCE(sum(x), 0)::int` |
| 条件计数 | `COUNT(CASE WHEN ... THEN 1 END)` | `count(*) FILTER (WHERE ...)` |
| `UPDATE` / `DELETE` 行数确认 | 结尾 `RETURNING 1;` | 无 |
| 幂等插入 | `INSERT OR IGNORE INTO` | `INSERT ... ON CONFLICT (...) DO NOTHING` |
| 批量清空 | 5 条 `DELETE FROM` | `TRUNCATE ... RESTART IDENTITY CASCADE` |
| `ExecuteDelete` 别名 | `DELETE FROM "T" AS "t" WHERE ...` | `DELETE FROM "T" AS t WHERE ...` |
| `bool` 条件 | `AND "c"."Enabled"`（列是 INTEGER） | `AND c."Enabled"`（列是 boolean） |
| 价格列比较 | 列是 `TEXT`，下推比较会变字符串比较 | 列是 `numeric(18,8)`，语义正确 |

最后一行是唯一会导致**结果不同**而不只是文本不同的差异。目前价格比较全部在内存完成，所以还没有踩到；任何人写出 `Where(rule => rule.UnitPrice > x)` 就会在 SQLite 上得到错误结果。

## 6. 值得优先处理的问题

1. **统计接口无上限全表读**（4.9 末尾）。模型分布、错误分布、时间序列三个接口按时间窗全量取回 35 列。建议改成数据库端 `GROUP BY`，或至少加服务端行数上限。
2. **RequestLogs 全列 UPDATE 导致字段互相覆盖**（4.7）。建议改用已有的部分列重载 `UpdateAsync(entity, propNames)`，鉴权路径已经这么做了。
3. **Tavily Key 配额并发超发**（4.11）。改成数据库端自增或加乐观并发列。
4. **`SessionService.RequireUser` 每请求一次查询无缓存**（第 3 节末）。鉴权侧同样的用户查询已经有 60 秒缓存，这里可以复用同一套失效机制。
5. **`ModelProviders` 全表查询 188 次**（4.5）。这是极少变更的小字典表，适合进缓存。
6. **模型目录列表 N+1**（4.5）。plan 与 rules 逐行查，列 50 个模型就是 101 条 SQL。
7. **模糊过滤吃不到索引**（4.9）。`RequestLogs` 为 `Model`、`Path`、`UpstreamModel` 建了单列索引，但实际是 `instr` / 前置通配 `LIKE`，只有等值过滤（`ChannelId`、`ApiKeyId`、`StatusCode`）用得上。PostgreSQL 可以考虑 `pg_trgm`。
8. **SQLite 集合参数线性增长**（4.9）。`IN (@ids1...@idsN)` 的 N 等于当页条数，大分页接近 SQLite 999 参数上限。
9. **清空日志是全库破坏性操作**（4.9）。没有 owner 或时间维度，也没有软删除，建议加二次确认与审计。
10. **代码级联删除**（4.4、4.10）。渠道与映射、用户与 Key/渠道之间没有数据库外键，删除靠服务层逐行 DELETE，漏调用就留孤儿数据。

## 7. 复现方法

```csharp
// 1. 临时在 OpenCodexDbContextFactory.ConfigureWarnings 末尾加钩子
builder.LogTo(
    line => File.AppendAllText(Environment.GetEnvironmentVariable("OCXP_SQL_TRACE_FILE")!, line + "\n"),
    new[] { RelationalEventId.CommandExecuted },
    LogLevel.Information);
```

```bash
# 2. SQLite 侧：跑测试套件即可覆盖绝大多数业务路径
cd opencodex_proxy
OCXP_SQL_TRACE_FILE=/tmp/sqlite-runtime.txt dotnet test tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj

# 3. PostgreSQL 侧：测试硬编码 sqlite，需要真实实例 + 探针程序
docker run -d --name ocxp-sqlprobe-pg -e POSTGRES_USER=admin -e POSTGRES_PASSWORD=123456 \
  -e POSTGRES_DB=opencodex -p 55432:5432 postgres:17-alpine
```

探针程序的最小形态：新建一个引用 `OpenCodex.Data` 的控制台项目，用 `OpenCodexDbContextFactory.CreatePostgres(...)` 建 context，`Migrate()` 之后用 `EfRepository<T>` 按第 3 节的时序调用一遍。想只看某段 LINQ 的翻译结果而不落库时，用 `ToQueryString()`，它不需要数据库连接。

抓完记得删掉第 1 步的临时代码，并清理容器。迁移 DDL 不在本文范围，需要时用 `dotnet ef migrations script` 单独生成。
