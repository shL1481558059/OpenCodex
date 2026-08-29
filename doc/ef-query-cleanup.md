# OpenCodex EF 查询治理清单

> 状态：待评审。本文只做一件事：把业务代码里的每个 EF 查询点过一遍，判断它该不该存在、
> 写法是不是最好的。缓存架构层面的改造见
> [db-query-and-cache-optimization.md](/home/shl/.codex/worktrees/e0a8/OpenCodex/doc/db-query-and-cache-optimization.md)。

## 1. 治理口径

全仓共 171 个查询点（`rg -c 'TableNoTracking|\.Table\b|_dbContext\.[A-Z]|_context\.[A-Z]'`
统计，已排除仓储自身与迁移）。分布集中在六个文件：

| 文件 | 查询点 |
| --- | --- |
| `ModelCatalogService.cs` | 52 |
| `ObservabilityService.cs` | 31 |
| `LogContentStore.cs` | 22 |
| `ConfigService.cs` | 17 |
| `ApiKeyService.cs` | 13 |
| `WebSearchService.cs` | 10 |

每个查询点问三个问题，按这个顺序：

1. 这个查询能不能删掉？答案是「能」的时候，优化写法就没有意义了。
2. 取回的列能不能减少？只用一个字段却取回整实体是最常见的浪费。
3. 多次往返能不能合并？同一个方法里查两遍同一张表是纯粹的重复。

按这三问筛下来，得到三类问题：A 类可以直接删掉，B 类写法应改进，C 类内存计算要下推。
下面每条都给了位置、当前写法、建议写法。

先说一个总体观察：这个代码库里同一个模式经常有两种写法，一种是对的，一种不是。
`ObservabilityService.ReadChannelNames`（`:954`）写的是
`.Select(c => new { c.Id, c.Name }).ToDictionary(...)`，只取两列；同一个文件的
`BuildOwnerMap`（`:424`）写的是 `.ToDictionary(u => u.Id, u => u.Username)`，取回整个
`User` 实体包括 `PasswordHash`。两处需求完全一样。所以治理的主要工作不是发明新写法，
而是把已经存在的正确写法推广到其余地方。

## 2. A 类：可以直接删掉的查询

### A1 加载实体只为了删除

`UserService.DeleteUser`（`UserService.cs:270-271`）：

```csharp
_apiKeyRepository.Delete(_apiKeyRepository.Table.Where(key => key.OwnerUserId == user.Id).ToList());
_channelRepository.Delete(_channelRepository.Table.Where(channel => channel.OwnerUserId == user.Id).ToList());
```

一个持有 200 个 Key、50 个渠道的用户被删时，这两行会把 250 个实体（11 列 + 19 列）
全部读进内存，再产生 250 条逐行 `DELETE`。实体本身一个字段都没用到。改成：

```csharp
_apiKeyRepository.Table.Where(key => key.OwnerUserId == user.Id).ExecuteDelete();
_channelRepository.Table.Where(channel => channel.OwnerUserId == user.Id).ExecuteDelete();
```

两个 SELECT 消失，250 条 DELETE 变成 2 条。同样的模式还有两处：
`ConfigService.DeleteMappingsForChannels`（`ConfigService.cs:1028-1034`）和
`WebSearchService` 的 Key 整体替换（`WebSearchService.cs:158-162`）。

注意 `ExecuteDelete` 立即执行、不参与外层 `SaveChanges`，删除与后续插入需要放进同一个
显式事务，否则中途失败会只删不插。

### A2 同一方法里查两遍同一张表

`WebSearchService.SaveConfig`（`WebSearchService.cs:127` 与 `:142`）：

```csharp
var currentDefaultKeyUsageLimit = _settingsRepository.TableNoTracking.FirstOrDefault()
    ?.KeyUsageLimit ?? DefaultWebSearchKeyUsageLimit;      // 第一次
// ...
var settings = _settingsRepository.Table.FirstOrDefault();  // 第二次，同一行数据
```

`WebSearchSettings` 是单行表，第二次查回来的 tracking 实体上就有 `KeyUsageLimit`，
第一次查询可以整条删掉。`:183` 与 `:189` 是同一段代码的另一个副本，同样处理。

同一方法内 `TavilyKeys` 也查了两遍：`:132` 用 `TableNoTracking` 建字典，`:158` 用
`Table` 取列表准备删除。字典可以从第二次的列表构建，第一次查询同样可以删掉。

`ApiKeyService.CreateKey`（`ApiKeyService.cs:120` 与 `:138`）：超管指定 `owner_username`
时先按 username 查一次用户拿到 Id，随后又按这个 Id 把同一个用户查了一遍，只为了拿
`Username` 填进返回值。第一次的结果直接复用即可。

### A3 当前用户信息已在手上却回库查

`ApiKeyService.ReadKey`（`:103`）、`UpdateKey`（`:198`）、`CreateKey`（`:138`）都有：

```csharp
var owner = _userRepository.TableNoTracking.FirstOrDefault(u => u.Id == existing.OwnerUserId);
// 只用到 owner?.Username
```

非超管场景下 `existing.OwnerUserId` 必然等于 `currentUser.UserId`，而
`_workContext.RequireUser()` 返回的 `SessionUser` 里已经带着 `Username`。这一整条查询在
非超管路径上可以直接跳过；超管路径才需要查，并且应该只投影 `Username`。

### A4 为了拿受影响行数而先 COUNT

`ObservabilityService.ClearLogs`（`:247-249`）连做三次 `COUNT(*)`，注释写的理由是
「TRUNCATE 不返回受影响行数」。改用 `ExecuteDelete()` 之后返回值就是行数，三次 COUNT
全部可以删掉。这一条和把原生 SQL 换成 LINQ 是同一个改动。

### A5 插入之后把刚插进去的数据查回来

`LogContentStore.EnsureBlocks`（`:174-179`）与 `EnsureManifests`（`:232-237`）：

```csharp
foreach (var chunk in chunks) { InsertBlockIfMissing(chunk); }   // 原生 INSERT，Id 被丢弃
var blocks = _context.LogContentBlocks
    .Where(block => hashes.Contains(block.Sha256))                // 再查回来拿 Id
    .ToList()
    .ToDictionary(block => block.Sha256, StringComparer.Ordinal);
```

这次回查是被原生 SQL 逼出来的：`InsertBlockIfMissing` 自己 `Guid.NewGuid()` 生成了 Id，
但因为走 `ExecuteSqlRaw`，调用方不知道这次插入到底成功还是被 `ON CONFLICT` 忽略，
所以只能重新查一遍。

改成 EF 写法之后这个问题自动消失：先按 `Sha256 IN (...)` 查出已存在的，只 `Add` 缺失的，
新增实体的 Id 就在手上，不需要回查。这条路径每次请求要走 8 次以上，省掉的是每次
至少 2 条查询。

顺带修掉两个问题：`EnsureManifests` 的 `SaveChanges()` 写在 `foreach` 里面（`:230`），
每个 manifest 提交一次；以及 provider 字符串分叉（`ProviderKind()`）可以整段删除。

并发正确性不受影响：高度重复的内容在第一步查询就命中了，走不到插入；异常只会在
「两个请求同时首次写入同一份全新内容」时发生，捕获 `DbUpdateException` 后 Detach 冲突
条目、重查一次即可。

### A6 表达式树里藏着的查询

`ObservabilityService.ApplyLogFilter`（`:673`）：

```csharp
"owner_username" when text.Length > 0
    => query.Where(log => log.OwnerUserId == ResolveOwnerUserIdFilter(text)),
```

`ResolveOwnerUserIdFilter` 是一个会查数据库的实例方法，写在 lambda 内部。EF 在参数提取
阶段会把这个不依赖 `log` 的子表达式在客户端求值，也就是每次执行查询时都会触发一次
`SELECT ... FROM "Users"`。`GET /logs?owner_username=x` 一次请求要构建两个查询
（`Count()` 和分页），于是这条 SELECT 执行两次；统计接口按 owner 过滤时同理。

更麻烦的是它在 SQL 日志里看起来像凭空多出来的查询，排查时很难和这行代码对上。
改法是在进入 `ApplyLogFilter` 之前把 username 解析成局部变量，再带进 lambda：

```csharp
var ownerUserId = ResolveOwnerUserIdFilter(text);   // 明确的一次查询
query = query.Where(log => log.OwnerUserId == ownerUserId);
```

这条已经用 `SqlCapture` 实测过（见 `ObservabilityServiceTests` 的
`LogsPage_OwnerUsernameFilter_ResolvesUserOnce`）。治理前把解析塞回 lambda 时，
`GET /logs?owner_username=x` 一次请求里 `FROM "Users"` 共执行 3 条，其中按 username
解析的 `SELECT ... WHERE "u"."Username"` 是 2 条（`Count()` 与分页各触发一次），另 1 条是
`BuildOwnerMap` 按 Id 取 Username。治理后解析提到 lambda 之外，按 username 解析降为 1 条，
总 `FROM "Users"` 从 3 条降为 2 条（剩下 1 条是 `BuildOwnerMap`，与 A6 无关）。倍数为 2 比 1。

### A7 缓存里已有的数据又去查库

`ModelCatalogService.CalculatePricing`（`:1051`）：

```csharp
var provider = _providers.TableNoTracking.FirstOrDefault(item => item.Id == providerId);
// 只用到 provider?.Code
```

`providerId` 是从定价缓存 `CachedPricingResolution` 里取出来的。既然这个 DTO 已经缓存了
`ProviderId`，把 `ProviderCode` 一起缓存进去就行，每次请求收尾的这条查询可以整条消失。
同一个方法里 `:1012` 的 rules 查询也是同样道理：rules 属于 plan，失效时机与 plan 完全
一致，没有理由每次现查。

### A8 同一请求内反复解析同一个用户

`ProxyLogService.ResolveOwnerUserId`（`:782-791`）按 username 查 `Users`。它被
`CreateQueuedLog`、`MarkProcessing`、`CompleteLog` 各调一次，加上每次上游尝试的 attempt
日志还要各调一次。一次带三连重试的请求会重复执行同一条查询七八遍，参数完全相同。

`ProxyLogService` 是 Scoped，加一个 `Dictionary<string, Guid>` 字段做请求内记忆化就够，
不需要动 Redis。`SessionService.RequireUser` 是同一类问题的更严重版本（捕获里 178 次，
全仓最热），处理方式见缓存方案文档 U2.1。

## 3. B 类：写法应改进的查询

### B1 取回整实体只为拿一个字段

这是全仓最多的一类。`Users` 表 7 列，其中包含 `PasswordHash`，而调用方通常只要 `Id` 或
`Username`：

```csharp
// 当前（8 处）
var ownerUser = _userRepository.TableNoTracking.FirstOrDefault(u => u.Username == name);
var id = ownerUser?.Id ?? Guid.Empty;

// 建议
var id = _userRepository.TableNoTracking
    .Where(u => u.Username == name)
    .Select(u => u.Id)
    .FirstOrDefault();
```

位置：`ObservabilityService.cs:437`、`ProxyRouteService.cs:434`、`ConfigService.cs:535`、
`:647`、`:656`、`:684`、`:827`、`ApiKeyService.cs:50`。只要 `Username` 的另有
`ApiKeyService.cs:103`、`:138`、`:198`、`ObservabilityService.cs:501`。

这个库里已经有两处正确写法可以照着抄：`ObservabilityService.cs:194` 和
`ConfigService.cs:862`。

把 `PasswordHash` 从这些路径上摘掉还有一个附带好处：它不会再进入进程内存，也不会因为
将来某个 DTO 映射写漏而外泄。

### B2 建字典时忘了投影

```csharp
// 当前
_userRepository.TableNoTracking.Where(u => ownerIds.Contains(u.Id))
    .ToDictionary(u => u.Id, u => u.Username);      // 取 7 列，用 2 列

// 建议
_userRepository.TableNoTracking.Where(u => ownerIds.Contains(u.Id))
    .Select(u => new { u.Id, u.Username })
    .ToDictionary(x => x.Id, x => x.Username);
```

`ToDictionary` 不是 `IQueryable` 的方法，所以当前写法会先把整实体拉回内存再构字典。
位置：`ObservabilityService.cs:424`、`:1042`、`ProxyRouteService.cs:450`、
`ApiKeyService.cs:70`、`ConfigService.cs:799`。

`ModelCatalogService` 里有两处同样的问题，而且更值得改，因为它在热路径上：
`:1285` 的 `_providers.TableNoTracking.ToDictionary(p => p.Id, p => p.SortOrder)` 取回
`ModelProvider` 全部 8 列却只用 2 列，且这个查询在 SQL 捕获里执行了 188 次，是全仓第二
热的业务查询。`:1731` 同理。

正确范例：`ObservabilityService.cs:954`（`ReadChannelNames`）和 `:1035`
（`ReadApiKeyNames`）都已经投影了。

### B3 循环体里的查询

`ConfigService.MergeChannels`（`ConfigService.cs:683-685`）在 `foreach (var channel in
channels)` 内部按 username 查 `Users`。批量导入 100 个渠道就是 100 条查询，而导入场景里
这些 username 通常还是同一个。改法是进循环前把涉及的 username 一次查出来建字典。

`ModelCatalogService.ToModelResponse`（`:1745`）对每个模型查一次价格方案，
`ToPlanResponse`（`:1773`）对每个方案查一次规则。列 50 个模型就是 101 条 SQL。改法是
按 `ModelInfoId IN (...)` 和 `PricingPlanId IN (...)` 各批量取一次，在内存里按 Id 分组
组装，50 个模型从 101 条降到 3 条。注意 SQLite 的参数上限是 999，页大小要设上限。

### B4 列表接口取回用不到的大字段

`RequestLog` 有 35 列，其中 `PricingSnapshotJson` 是一份完整的计费明细 JSON，单行体积可能
比其余所有列加起来还大。

`QueryLogsPage`（`:454-462`）取全部 35 列，`MapRequestLogEvent` 实际只用 28 列，
`PricingSnapshotJson` 不在其中。分页 500 行时这是白读 500 份 JSON。

`QueryRecentErrors`（`:202-208`）更极端：取 35 列，只用 `Id`、`CreatedAt`、`Model`、
`UpstreamModel`、`ChannelId`、`StatusCode`、`Error` 七列。

两处都应该 `Select` 成专门的投影类型。这个改动风险低、收益直接，建议优先做。

### B5 先 SELECT 再全列 UPDATE

```csharp
// UserService.cs:218-222
var user = _userRepository.Table.FirstOrDefault(item => item.Username == username);
user.Enabled = enabled;
user.UpdatedAt = UnixTimeSeconds();
_userRepository.Update(user);        // 6 列全写
```

这里 SELECT 是必要的（返回值要完整 DTO），但 UPDATE 应该只写两列：
`_userRepository.Update(user, nameof(User.Enabled), nameof(User.UpdatedAt))`。仓储的这个
重载早就有了（`EfRepository.cs:73`），全仓只有 `ProxyAccessService.cs:113` 一处在用。

同类位置：`UserService.cs:239`（改密码）、`WebSearchService.cs:155`、`:202`、
`ProxyLogService.cs:103`。`ProxyLogService` 那处最严重，因为 `RequestLogs` 的全列覆盖会
让两个写入路径互相盖掉对方刚落库的值，细节见缓存方案文档 U1.1。

### B6 两次查重可以合并成一次

`ConfigService.SaveSingleChannel`（`:378` 与 `:386`）建渠道前查两次 `Channels`：一次查
Id 重复，一次查名字重复，两次都取回整个 19 列实体只为判断存在。合并成一次往返：

```csharp
var conflicts = _channelRepository.TableNoTracking
    .Where(c => c.OwnerUserId == ownerId && (c.Id == parsedId || c.Name == channelName))
    .Select(c => new { c.Id, c.Name })
    .ToList();
```

再在内存里分辨是 Id 冲突还是名字冲突，错误消息的区分度不受影响。

## 4. C 类：应该下推数据库的内存计算

### C1 统计接口的分组与分桶

`ReadStatsTimeseries`（`:344`）、`ReadStatsModelDistribution`（`:395`）、
`ReadStatsErrorDistribution`（`:414`）、`QueryStats`（`:565`）都是同一个形状：

```csharp
var logs = query.ToList();       // 时间窗内全部日志，35 列，无 LIMIT
// 然后在内存里 GroupBy / 分桶 / Sum / Take
```

返回行数完全由用户选的时间范围决定。这是全部业务查询里唯一能打爆进程的一条。
三个接口的分组逻辑都能完整下推：

```csharp
// 分桶：桶号在数据库端算
query.GroupBy(log => (long)((log.CreatedAt - startTs) / bucketSeconds))
     .Select(g => new { g.Key, Count = g.Count(), Cost = g.Sum(x => x.Cost) })

// 模型分布：TopN 也下推
query.GroupBy(log => log.Model == null || log.Model == "" ? "unknown" : log.Model)
     .Select(g => new { Model = g.Key, Count = g.Count() })
     .OrderByDescending(x => x.Count).Take(20)

// 错误分布：复合键
query.Where(失败条件).GroupBy(log => new { log.ChannelId, log.StatusCode })
     .Select(g => new { g.Key.ChannelId, g.Key.StatusCode, Count = g.Count() })
     .OrderByDescending(x => x.Count).Take(30)
```

三个注意点。空桶在 `GroupBy` 里不会出现，要在内存按桶号补零，否则前端图表缺点。
`(long)(double / double)` 的翻译和舍入行为在 SQLite 与 PostgreSQL 上可能不同，必须用
`ToQueryString()` 在两个 provider 上各看一遍再比对输出。TTFT 平均值从内存 `Average` 改成
`Sum / Count` 后末位可能有 0.1 级差异，测试断言要留容差。

### C2 精确匹配不需要 AsEnumerable

`ModelCatalogService` 里有两个看起来一样、实际不同的方法。

`ResolveGlobalModel`（`:1286`）的 `.AsEnumerable()` 是正确的：它后面是「精确 / 前缀 /
通配 + 优先级 + 模式长度 + provider 排序」的复合打分，写不进 `WHERE`，必须在内存做。
这一处不要改。

`ResolveChannelModel`（`:1268`）的 `.AsEnumerable()` 是可以去掉的：

```csharp
return _channelModels.TableNoTracking
    .Where(model => model.ChannelId == channelId && model.Enabled)
    .AsEnumerable()
    .FirstOrDefault(model => string.Equals(
        model.UpstreamModel, normalized, StringComparison.OrdinalIgnoreCase));
```

匹配条件只是一个大小写不敏感的字符串相等，不是复合打分。可以先下推一个精确相等作为快
路径（`Where(m => m.UpstreamModel == normalized)`），未命中再退回当前的内存比较。这样常
见情况下只取回一行，而不是该渠道的全部模型。

要注意 `OrdinalIgnoreCase` 与数据库 collation 的语义不完全等价，所以是「下推快路径加内存
兜底」，不是直接替换成 `ToLower()` 比较。

### C3 不要改的地方

为免过度治理，把几处「看着可疑但其实合理」的写法也记下来。

`ConfigService.ReadChannels` 的内存排序：前面有缓存，排序作用在缓存副本上，不产生查询。

`WebSearchService.cs:106` 的 `.AsEnumerable().Select(MapToDto)`：`MapToDto` 是普通方法，
不能翻译成 SQL，这里显式切到内存是对的写法。

`AuthService.cs:39`、`:67` 用 `Any()` 判断存在：已经是最优，翻译成 `SELECT EXISTS(...)`，
不要改成 `Count() > 0`。

`AuthService.cs:156` 登录时取回整个 `User`：需要 `PasswordHash` 做校验，必须全取。

`QueryLogsPage` 的 `Count()` 加分页两条查询：分页接口要返回总数，两条是必要的。

## 5. 一次代理请求的查询总账

把上面的结论套回一次流式 `POST /v1/chat/completions`（无重试、缓存全未命中）的查询时序，
看每一步的处置：

| 步骤 | 查询 | 处置 |
| --- | --- | --- |
| 1 | `AccessApiKeys` by hash | 保留，已有缓存 |
| 2 | `UPDATE LastUsedAt` | 移出缓存回源路径 |
| 3 | `Users` by id | 保留，已有缓存 |
| 4 | `Channels` by owner | 保留；owner 解析改投影（B1） |
| 5 | `Users IN (ownerIds)` | 改投影（B2） |
| 6 | `ChannelModelMappings` by channel | 可进缓存 |
| 7 | `Users` by username（日志 owner） | 删除，请求内记忆化（A8） |
| 8 | `INSERT RequestLogs` | 保留 |
| 9 | 内容寻址：headers 与 body | 省掉插入后的回查（A5） |
| 10 | `SELECT RequestLogs` by id 加 34 列 `UPDATE` | UPDATE 改部分列（B5） |
| 11 | 内容寻址：上游请求体 | 同 A5 |
| 12 | `ChannelModelInfos` by channel | 下推精确匹配快路径（C2） |
| 13 | `ModelInfos` 全局启用 | 保留，复合打分必须在内存 |
| 14 | `ModelPricingPlans` | 保留 |
| 15 | `ModelPricingRules` | 删除，随定价结果一起缓存（A7） |
| 16 | `ModelProviders` 全表 | 改投影并进缓存（B2） |
| 17 | `UPDATE RequestLogs` 落账 | 改部分列（B5） |
| 18 | 内容寻址：响应体等槽位 | 同 A5 |
| 19 | `ModelProviders` by id 取 Code | 删除，Code 进定价缓存（A7） |

可以直接消除的是第 7、15、19 三条，加上第 9、11、18 三组内容寻址各自的回查。带三连重试
时第 7 条原本要重复四次，全部消除。

具体省下多少条 SQL 建议用捕获实测，不要采信估算：本表是按代码路径推出来的，实际条数受
缓存命中率和重试次数影响很大。

## 6. 落地顺序

按「风险低、收益明确」排序分四批，每批可以独立提交。

第一批，纯投影改造：B1、B2、B4。这批只改取哪些列，不改结果集内容，也不改查询条数，
回归风险最低。做完之后 `Users` 表上的 `PasswordHash` 不再被无谓读取，日志列表不再白读
`PricingSnapshotJson`。建议先做这批，它能把后面几批的 SQL 日志噪音降下来。

第二批，删掉冗余查询：A1、A2、A3、A4、A8、B3、B6。这批会改变查询条数，但每条的语义都是
「原本就不该存在」，行为不变。A1 与 A2 要注意 `ExecuteDelete` 的事务边界。

第三批，正确性相关：B5 与 C1。B5 的部分列更新要逐个方法把「本方法负责哪些列」列清楚，
漏一列就是该列再也不写入且不报错。C1 的聚合下推要在两个 provider 上比对输出。这两个都
需要新增测试，不要和前两批混在一起提交。

第四批，结构改动：A5、A6、A7、C2。A5 会顺带删掉全仓最后两处原生 SQL 和 provider 字符串
分叉；A7 需要改动定价缓存的 DTO 结构，会和缓存方案文档 U3.1 相互影响，两者放在一起做
更省事。

## 7. 验证手段

这次治理的每一条都是「SQL 条数或列数应该变少」，所以验证方式应该直接盯 SQL 本身，
而不是只看接口返回值。

SQL 清单那次捕获已经证明「在 `OpenCodexDbContextFactory.ConfigureWarnings` 挂
`RelationalEventId.CommandExecuted` 钩子」这条路走得通。建议把它固化成测试基础设施：
在测试用的 context 工厂里挂一个收集器，提供 `AssertSqlCount(n)`、
`AssertNoColumn("PricingSnapshotJson")` 之类的断言。

有了它，这份清单里的每一条都能写成一个测试：

| 治理项 | 断言 |
| --- | --- |
| A1 | 删用户时 `SELECT` 条数为 1，`DELETE` 条数为 4 |
| A2 | 保存 Web Search 配置时 `WebSearchSettings` 查 2 次（目标 1 次，见第 8 章待办） |
| A8 | 一次代理请求中 `Users` 查询条数不超过 1 |
| B2 | providers 查询的 SQL 文本只含 `Id` 与 `SortOrder` |
| B3 | `ModelPricingPlans` 查询条数不随模型数线性增长（10 个模型 1 条、1000 个模型按 900 一页分 2 条），对应 `ListModels_PlansQueryDoesNotGrowWithModelCount`。原建议值 3 是按当时代码路径估的，落地后按分页实现改为增长性断言，比固定条数更能拦 N+1 回归 |
| B4 | 日志列表 SQL 不含 `PricingSnapshotJson` |
| C1 | 统计接口 SQL 含 `GROUP BY`，且不出现无 `LIMIT` 的全列查询 |

这个基础设施建议在第一批之前就搭好，它是后面所有批次的验收工具，也是防止 N+1 悄悄回归的
长期防线。

## 8. 落地情况

本章是施工清单的落地记录：每条治理项的实际状态、改动落点与验收测试名，均以当前代码与测试为准。
表里「部分完成」表示该条只做了一部分，剩余部分记录在本章「仍然待办」。

| 治理项 | 状态 | 实际落点 | 验收测试 |
| --- | --- | --- | --- |
| A1 | 已完成 | `UserService.DeleteUser`（改用 `DeleteWhere` 删 api key / channel / vision transfer settings，再用 `Delete(user)` 删 user 自身）；`ConfigService.DeleteMappingsForChannels`、`WebSearchService.ReplaceWebSearchConfig` 改用 `DeleteWhere`，新增 `IRepository.DeleteWhere` | `DeleteUser_IssuesNoSelectAndBatchDeletesRelatedRows` |
| A2 | 部分完成 | `WebSearchService.ReplaceWebSearchConfig` 入口的重复 settings 查询已合并；收尾 `ReadWebSearchConfig` 仍会再查一次 | `SaveConfig_QueriesWebSearchSettingsTwiceAndDeletesKeysByPredicate` |
| A3 | 已完成 | `ApiKeyService` 非超管路径直接复用 `_workContext.RequireUser()` 的 username，不再回库查 owner；超管路径（`OwnerUsername` / `OwnerUserId`）一次投影 `Id`/`Username` 后直接复用，消除按 Id 回查 Username 的二次查询 | `CreateKey_NonSuperadmin_UsesWorkContextOwnerWithoutUserQuery`、`CreateKey_SuperadminByUsername_ResolvesOwnerOnceAndProjectsFields`、`CreateKey_SuperadminByOwnerUserId_ResolvesOwnerOnce` |
| A4 | 已完成 | `ObservabilityService.ClearLogs` 三次 `COUNT` 已删，改用 `ExecuteDeleteAll`（新增 `IRepository.ExecuteDeleteAll`），返回值即真实行数，删除放同一个显式事务 | `ClearLogs_RemovesContentRefsManifestsBlocksAndLogs` |
| A5 | 已完成 | `LogContentStore.EnsureBlocks` / `EnsureManifests` 先查已存在、只插入缺失，不再回查；顺带把 `EnsureManifests` 的 `SaveChanges()` 移出循环、删除 provider 字符串分叉 | `IdenticalContentWrittenTwice_DoesNotAddBlocksAndSharesManifest`、`ReplacingLastReference_RemovesOrphanedManifestAndBlocks` 等 |
| A6 | 已完成 | `ObservabilityService.ApplyOwnerUsernameFilter` 把用户解析提到 lambda 之外，先解析为局部变量再进 `Where` | `ObservabilityServiceTests` 的 `LogsPage_OwnerUsernameFilter_ResolvesUserOnce`（按 username 解析恰 1 条） |
| A7 | 已完成 | `ModelCatalogService` 的 `CachedPricingResolution` 新增 `ProviderCode` 与 `Rules`，`ToCached` 时一并装入，收尾不再现查 provider / rules | `ModelCatalogService` 相关定价快照用例 |
| A8 | 已完成 | `ProxyLogService.ResolveOwnerUserId` 增加请求内 `Dictionary<string, Guid>` 记忆化，重复解析只查一次库；不缓存 `Guid.Empty`，同名新用户后续可解析到新 Id | `ProxyLog_CreateQueuedLog_ResolvesOwnerOnceWithinRequest`、`ProxyLog_ResolveOwnerUserId_DoesNotCacheMissingUser` |
| B1 | 已完成 | `ObservabilityService`、`ConfigService`、`ApiKeyService`、`ProxyRouteService` 的 owner 解析改 `Select` 投影，只取 `Id` 或 `Username` | `CreateKey_SuperadminByUsername_ProjectsOnlyIdAndUsername`（校验 `FROM "Users"` 的语句不含 `PasswordHash`） |
| B2 | 已完成 | 各处 owner 字典改 `Select(u => new { u.Id, u.Username })` 后 `ToDictionary`；`ModelCatalogService` 的 providers 字典只投影 `Id` / `SortOrder` | `CreateKey_SuperadminByUsername_ProjectsOnlyIdAndUsername` |
| B3 | 已完成 | `ModelCatalogService` 新增 `PlansByModelId` / `RulesByPlanIds` 批量取 plans / rules 并分组，`Pages` 按 900 一页规避 SQLite 参数上限 | `ListModels_PlansQueryDoesNotGrowWithModelCount`（10 个模型 1 条 plan 查询，1000 个模型 2 条，不随模型数线性增长） |
| B4 | 已完成 | `ObservabilityService` 的 `RequestLogRow` / `RecentErrorRow` 投影避开 `PricingSnapshotJson`；额外发现并修掉 `LogContentStore.EnsureBlocks` 把 `LogContentBlock.Data` 整行读回的问题，改为 `AsNoTracking` + 只投影 `Id` / `Sha256` / `RawLength` | `LogsPage_ProjectionDoesNotReadPricingSnapshotJson`、`DeduplicatedBlockLookup_SqlDoesNotSelectDataColumn` |
| B5 | 已完成 | `UserService`（`SetUserEnabled` / `ResetUserPassword`）、`ApiKeyService.UpdateKeyAsync`、`WebSearchService`、`ProxyLogService` 改部分列更新 | `SetUserEnabled_UpdatesOnlyEnabledAndUpdatedAt`、`ResetUserPassword_UpdatesOnlyPasswordHashAndUpdatedAt`、`UpdateKey_NonSuperadmin_UpdateWritesOnlyEnabledAndUpdatedAt` |
| B6 | 已完成 | `ConfigService.SaveSingleChannel` 的 id / name 两次查重合并成一次 `Where(c => ... && (c.Id == parsedId || c.Name == channelName))` | `ServiceQueryGovernanceTests` 内渠道重复名相关用例 |
| C1 | 已完成 | 四个统计接口的聚合下推为数据库 `GroupBy` / `Sum` / `Count`；额外修了两个问题：分桶键从 `(long)` 强转改为 `Math.Floor`（PostgreSQL 的 float 到 bigint 是四舍五入，与 SQLite 截断不一致，会错桶），`QueryStatsSummary` 从 13 条 SQL 合并成 2 条 | `ObservabilityAggregationSqlTests` 的 `TimeseriesBucketBoundary_RoundsDownToBucket1`、`PostgresBucketQuery_TranslatesFloorAndDoesNotCastToBigint`、`ReadStatsSummary_NonEmptyTableAggregatesInTwoSql`，以及 `ObservabilityServiceTests` 的 `StatsAggregations_ArePushedToDatabaseAndPadEmptyBuckets` |
| C2 | 已完成（`ResolveChannelModel`） | `ModelCatalogService.ResolveChannelModel` 加数据库精确相等快路径 + 内存兜底；`ResolveGlobalModel` 按 C3 保留不改。`ListChannelModelInfos` 新增 `ResolveGlobalModels` 批量解析，一次加载 provider 排序与 enabled 全局模型后在内存打分，全局匹配不再随 upstream 模型数线性触发全表查询 | `ModelCatalogService` 模型解析相关用例、`ListChannelModelInfos_GlobalModelLookupDoesNotGrowWithModelCount` |

> 注：A1 删用户时的 1 条 `SELECT` 是必要的——`UserService.DeleteUserAsync` 要先读出被删用户才能
> 返回完整 DTO；4 条 `DELETE` 依次是 api key、channel、vision transfer settings、user 自身。
> A2 实测为 2 次 `WebSearchSettings` 查询，第二次属尚未消除的残留，见「仍然待办」。

### 8.1 仍然待办

- A2 残留的第二次 `WebSearchSettings` 查询：`ReplaceWebSearchConfig` 收尾调 `ReadWebSearchConfig` 构造
  响应时又查一次 settings，目标是降到 1 次。
- `ClearLogs` 在 PostgreSQL 上从 `TRUNCATE ... CASCADE` 换成 5 条 `DELETE FROM`，符合「非必要不用裸 SQL」
  的约束，但大表清空会明显变慢且产生大量 WAL，需要用户确认这个取舍。
- `BuildAttemptStats` 的失败判定缺 `LifecycleStatus == null` 前置，与
  `IsSuccessfulPredicate` / `ApplyRequestStatusFilter` 的口径不一致（既有逻辑，本轮未改）。
- PostgreSQL 只验证了 `ToQueryString()` 生成的 SQL 文本，没有连真实 PG 实例跑端到端。
- Redis 无过期持久化属 `doc/db-query-and-cache-optimization.md` 的阶段 3，本轮未实施。

### 8.2 验证入口

SQL 级验收测试的共享设施在
`tests/OpenCodex.Api.Tests/Infrastructure/SqlCapture.cs`：它拦截
`RelationalEventId.CommandExecuted` 同类事件，覆盖 Reader / NonQuery / Scalar 三类命令，提供
`SelectCount` / `DeleteCount` / `UpdateCount` / `CountMatching` / `AssertNoColumn` /
`AssertContains` / `StatementsStartingWith` 等断言，并附 SQLite 捕获 context 的工厂方法。

验收用例分布：

- `ServiceQueryGovernanceTests.cs`：A1 / A2 / A3 / A8 / B2 / B3 / B5 等服务级 SQL 断言。
- `ObservabilityAggregationSqlTests.cs`：C1 的分桶边界、跨 provider 翻译、摘要聚合 SQL 条数。
- `ObservabilityServiceTests.cs`：B4 / C1 的接口行为与 SQL 断言、A4 的 `ClearLogs` 清理。
- `LogContentStoreTests.cs`：A5 / B4 的内容寻址与 `LogContentBlock.Data` 不整行读回。

当前全量结果：`dotnet test` 652 个测试全绿。
