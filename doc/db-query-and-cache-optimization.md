# OpenCodex 数据访问与缓存优化方案

> 状态：待评审，尚未实施。本文是《OpenCodex 业务运行时 SQL 清单》的后续行动方案。
>
> 现状判断来自 `opencodex_proxy/src` 的代码读取，参考模式来自 `Ylg.WebPortal`。
> 每条问题都标注了源文件与行号，实施前可逐条复核。
>
> 查询点级别的逐条治理清单（哪些查询可以删、哪些写法要改）另见
> [ef-query-cleanup.md](/home/shl/.codex/worktrees/e0a8/OpenCodex/doc/ef-query-cleanup.md)。

## 1. 结论先行

先澄清一个判断，否则后面的工作会走偏。

这个项目的问题不在 SQL 语句上。全仓只有两处手写原生 SQL：内容寻址的幂等插入
（`LogContentStore.cs:272`、`LogContentStore.cs:298`）和清空日志的批量删除
（`ObservabilityService.cs:257-267`）。其余全部已经是 EF LINQ。所以「非必要不允许
使用 SQL 语句」这条要求如果只按字面执行，能改的东西不到全部问题的 5%。

真正让查询与修改逻辑混乱的是四件事，按严重程度排列。

第一，该由数据库做的聚合被搬到进程内存做。时间序列、模型分布、错误分布三个统计
接口把时间窗内的日志全量 35 列取回内存再分组，且没有任何行数上限
（`ObservabilityService.cs:344`、`395`、`414`、`565`）。选「最近 30 天」就是把
30 天日志读进进程。

第二，同一业务规则有多份实现，而且已经不一致。「请求成功 / 失败」的判定有 4 份：
`ApplyRequestStatusFilter`（`ObservabilityService.cs:723-728`）、`IsSuccessfulLog`
（`ObservabilityService.cs:1161`）、`QueryRecentErrors` 的内联条件、以及 attempt
重试统计里的条件。attempt 统计那份缺少 `LifecycleStatus IS NULL` 前置条件，
语义与其他三份不同：一条 `LifecycleStatus='success'` 但 `StatusCode=500` 的
attempt 在它眼里是失败，在 recent-errors 眼里不是。

第三，写路径默认全列覆盖。仓储的 `Update(entity)` 走 `DbSet.Update`
（`EfRepository.cs:61`），EF 把整个实体标成 `Modified`。`RequestLogs` 因此是 34 列
全写，同一条日志被流式 TTFT 与最终 usage 两个路径先后更新时，后写者会用自己内存里
的旧值盖掉先写者刚落库的列。这是会真丢数据的。

第四，缓存没有统一契约。两套设施并存：`ICacheService`（L1 + L2 + 广播失效）和裸
`IMemoryCache`（`ConfigService.cs:776` 与 `ObservabilityService.cs:1015` 两份渠道
快照）。后者在多实例下各进程互不知情，
只靠 10 秒 TTL 兜底。TTL 长度散落在各服务里硬编码，失效点靠人肉记忆维护。

所以本方案的主线是：把聚合推回数据库、把规则收敛成单一定义、把写操作变成精确写、
把缓存收敛成一套有前缀失效能力的契约。「只用 LINQ」在这条主线上恰好是有效的执行
准则，因为聚合下推、原子自增、批量删除在 EF Core 10 里都有纯 LINQ 表达。

## 2. 现状诊断

### 2.1 数据访问层

| 事实 | 位置 | 影响 |
| --- | --- | --- |
| 每个 CRUD 方法内部各自 `SaveChanges()` | `EfRepository.cs:37-125` | 没有事务边界概念，一个业务动作产生多次提交 |
| `Update(entity)` 默认全列写 | `EfRepository.cs:61` | 写放大，并发互相覆盖 |
| 部分列更新重载已存在但几乎无人用 | `EfRepository.cs:73`、`85` | 全仓只有 `ProxyAccessService.cs:113` 一处使用 |
| 服务层绕过仓储直接注入 `IOpenCodexDbContext` | `ObservabilityService.cs:77`、`ProxyLogService.cs:42` | 数据访问边界被穿透，`LogContentStore` 直接 `new` 出来 |
| 查询一律取回整实体，没有投影 | 全仓 | `Channels` 19 列、`RequestLogs` 35 列全取，列表接口尤其浪费 |
| 禁用导航属性，关联靠手工二次查询 | `ProxyRouteService.cs:424`、`ConfigService.cs:794-800` | 每个列表接口都要补一次 `Users WHERE Id IN (...)` |
| 代码级联删除，无数据库外键 | `ConfigService.cs:1020`、`UserService.cs:270-272` | 漏调用就留孤儿数据 |

另有两处具体缺陷。

`WebSearchService.ReserveTavilyKeyById`（`WebSearchService.cs:359-371`）是「先 SELECT
再 `UsageCount += 1` 再全列写回」，没有乐观并发列。并发搜索会读到同一个 `UsageCount`
并写回同一个值，实际用量会超过 `UsageLimit`。

`ProxyLogService.ResolveOwnerUserId`（`ProxyLogService.cs:782-791`）每次写日志都按
`Username` 查一次 `Users`，无缓存。一次带三连重试的请求会调用它四次以上。

### 2.2 缓存层

现有 `ICacheService` 只有三个方法：`GetOrCreateAsync`、`RemoveAsync(key)`、
`RemoveAsync(keys)`。没有前缀失效能力，这一个缺口直接导致了两个连锁后果。

其一，定价缓存被迫用「版本号嵌进 key」模拟整片失效（`CacheKeys.cs` 的
`PricingContext`、`ModelCatalogService.cs:1102-1190`）。改一次价格就把 `redisVersion`
自增，所有旧 key 立刻失去命中机会，但仍然物理存在于 Redis 里，只能等 TTL 回收。

其二，需要整片失效的场景只能退化成「删一个能想到的 key」。
`ConfigService.InvalidateRouteCache`（`ConfigService.cs:263-270`）的注释自己写明了这个
妥协：超管改他人渠道时，他人的路由缓存靠 60 秒 TTL 兜底。

当前缓存覆盖情况（调用次数来自 SQL 清单那次全量捕获）：

| 数据 | 是否缓存 | TTL | 捕获中的执行次数 |
| --- | --- | --- | --- |
| `AccessApiKeys` by hash | 是（ICacheService） | 60s | 热路径 |
| `Users` by id（代理鉴权） | 是（ICacheService） | 60s | 热路径 |
| `Users` by id（管理台会话复核） | 否 | — | 178，最高 |
| `ModelProviders` 全表 | 否 | — | 188，第二高 |
| `Channels` by owner（路由） | 是（ICacheService） | 60s | 热路径 |
| `Channels` 全量（管理台） | 是（裸 IMemoryCache） | 10s | 每次列表 |
| 定价解析结果 | 是（ICacheService + 版本号） | 60s | 每次请求收尾 |
| `ModelPricingRules` | 否 | — | 每次计费 |
| `ChannelModelMappings` | 否 | — | 每次路由 |
| `WebSearchSettings` / `TavilyKeys` | 否 | — | 每次搜索 |
| `Users` by username（日志 owner 解析） | 否 | — | 每条日志至少一次 |

### 2.3 原生 SQL 的真实分布

只有两处，性质完全不同。

可以且应该改成 LINQ 的是清空日志（`ObservabilityService.cs:250-268`）。
`TRUNCATE ... CASCADE` 和 5 条 `DELETE FROM` 都能用 `ExecuteDelete()` 表达，还能顺带
拿到受影响行数，省掉前面那三次 `COUNT(*)`，同时消除 `DatabaseProvider` 字符串分叉。

属于「必要」但可以收敛的是内容寻址的幂等插入（`LogContentStore.cs:255-303`）。
`INSERT OR IGNORE` 与 `ON CONFLICT DO NOTHING` 在 EF Core 里没有 LINQ 对应表达。
这里的原生 SQL 不是随手写的，是为了让「同一个 SHA256 并发写入时只留一份」成立。
但当前实现按 `Database.ProviderName` 字符串分叉，加第三个 provider 会直接抛异常。
处理方式见 6.5。

### 2.4 问题分级

| 级别 | 问题 | 后果 |
| --- | --- | --- |
| P0 | 统计三接口无上限全表读 | 时间窗一大就打爆进程内存 |
| P0 | `RequestLogs` 全列 UPDATE 互相覆盖 | 已落库的 TTFT 与 usage 被旧值盖掉 |
| P0 | Tavily 配额并发超发 | 实际用量突破 `UsageLimit`，产生额外费用 |
| P1 | 成功 / 失败判定 4 份实现且不一致 | 同一条日志在不同接口里状态不同 |
| P1 | `SessionService.RequireUser` 每请求一次查询 | 管理台最热查询完全裸奔 |
| P1 | `ModelProviders` 全表查 188 次 | 极少变更的小字典表反复回源 |
| P1 | 模型目录列表 N+1 | 50 个模型等于 101 条 SQL |
| P2 | 缺前缀失效能力 | 定价版本号 hack、路由缓存靠 TTL 兜底 |
| P2 | 两套缓存设施并存 | 多实例下管理台数据不一致 |
| P2 | 代码级联删除 | 漏调用留孤儿数据 |
| P2 | 清空日志是全库破坏性操作 | 无 owner 与时间维度，无审计 |

## 3. 参考项目 Ylg.WebPortal 的做法

`Ylg.WebPortal` 的缓存是 nopCommerce 风格的成熟实现，有五个模式值得直接搬过来。

一是缓存键集中定义在按业务域划分的 `XxxDefaults` 静态类里，且同时定义「前缀」和
「具体键」。以 `AppClientDefaults` 为例：`PrefixCacheKey => "app:client:"`，
`ClientAllCacheKey => $"{PrefixCacheKey}all-{{0}}"`，`ClientByAppIdCacheKey =>
$"{PrefixCacheKey}aid-{{0}}"`。读缓存用具体键，失效时用 `RemoveByPrefixAsync(前缀)`
一次清掉整个域。写路径不需要知道这个域下到底有哪些键。

二是前缀失效是一等能力。`IStaticCacheManager.RemoveByPrefixAsync(prefix, params
object[])` 是接口的一部分，`AppClientService.ClearCacheAsync()` 就是一句
`RemoveByPrefixAsync(前缀)`。OpenCodex 缺的正是这个方法。

三是派生键从全集缓存派生，而不是各自查库。`AppClientService.GetByAppId` 不查数据库，
它调 `GetAllClients(loadCacheableCopy: true)` 拿到全集缓存后在内存里 `FirstOrDefault`。
一张小表因此只有一个真正的回源点，多个访问维度共享它。

四是缓存值是专门的可序列化副本，不是 EF 实体。`AppClientForCaching`、
`SettingForCaching` 都是为进 Redis 而定义的类型。OpenCodex 在鉴权路径上已经这么做了
（`CachedAccessKey`、`CachedAccessUser`），但路由缓存直接缓存了 `List<Channel>`
实体（`ProxyRouteService.cs:460`），应当统一。

五是缓存时长按数据变更频率分档，而不是全局一个值。`AppClientDefaults.CacheMinutes`
是 43200，也就是 30 天。这说明参考项目对「配置类、字典类数据长期驻留缓存」这件事
本来就是接受的，只是用超长 TTL 表达，而本次要求把它变成真正的无过期。

数据访问侧，`Ylg.Data.IDbContext` 有一个 OpenCodex 缺失的能力：`AutoSaveChanges` 开关
与 `DisableAutoSaveChanges()` / `ResetAutoSaveChanges()`（`IDbContext.cs:64-71`、
`EfRepository.cs:108`）。仓储的每个写方法都是 `AutoSaveChanges ? SaveChanges() : 0`，
于是「一个业务动作一次提交」有了统一表达，不需要为批量场景另写一套方法。

另外 `PROJECT_ARCHITECTURE.md` 第八节的四条数据访问规则可以直接作为本次的验收标准：
查询场景优先无跟踪查询、修改场景使用可跟踪实体或明确更新方法、查询返回尽量投影为
业务需要的数据、不把数据库异常直接返回给调用方。

有三处不建议照搬。`PerRequestCache` 用 `HttpContext.Items` 加 `ReaderWriterLockSlim`
做请求内缓存，而 OpenCodex 的 L1 本来就是进程内 `IMemoryCache`，覆盖面更大，再加一层
只增加复杂度。序列化不跟 Newtonsoft，继续用 `System.Text.Json`。最关键的是
`RemoveByPrefix` 的实现方式：参考项目用 `IServer.Keys(pattern)` 扫描全库，这在无过期
持久化模式下会随 key 总量线性变慢，而且生产 Redis 常常禁用或限流 `KEYS` / `SCAN`。
替代方案见 5.1。

## 4. 三条约束的落地口径

### 4.1 只用 EF LINQ 的边界在哪

准则：默认一律 LINQ；只有当 LINQ 无法表达，且行为正确性依赖数据库的原子语义时，
才允许原生 SQL，并且必须收在数据访问层一个命名清楚的方法里，不许出现在服务层。

需要说明的是，下面这些都属于 LINQ，不算「写 SQL」，可以放心用：

| 目的 | LINQ 写法 | 翻译结果 |
| --- | --- | --- |
| 聚合下推 | `GroupBy(...).Select(g => new { g.Key, C = g.Count() })` | `GROUP BY` |
| 条件计数 | `g.Count(x => 条件)` | SQLite `COUNT(CASE WHEN)`，PG `FILTER` |
| 原子自增 | `ExecuteUpdateAsync(s => s.SetProperty(x => x.UsageCount, x => x.UsageCount + 1))` | `SET c = c + 1` |
| 批量删除 | `Where(...).ExecuteDelete()` | 单条 `DELETE ... WHERE` |
| 部分列更新 | `UpdateAsync(entity, nameof(X), nameof(Y))` | 只含这两列的 `UPDATE` |
| 列投影 | `Select(x => new Dto(x.Id, x.Name))` | 只取两列 |

按这个口径，真正需要保留原生 SQL 的只剩幂等插入一个场景。

### 4.2 Redis 无过期持久化：前置条件与风险

这是本方案里风险最高的一条，需要把顺序讲清楚。

TTL 在当前设计里同时承担两个职责：回收内存，以及给「失效点写漏了」兜底。去掉 TTL
等于放弃第二个职责。所以正确顺序是先把失效完备性做成结构性保证，再去掉 TTL。
顺序颠倒会得到永久脏数据，而且没有任何自愈机会。

完备性靠五条保证：

1. 单一所有者。每个缓存域只由一个服务读写，该域涉及的写操作必须经过这个服务。
   现在 `Channels` 被 `ConfigService`、`ProxyRouteService`、`ObservabilityService`
   三处各自缓存，必须先收敛。
2. 前缀失效优先于点失效。写操作默认失效整个域，宁可多删不可漏删。回源代价远小于
   脏数据代价。
3. 键里带结构版本号，而不是自增版本号。缓存值的字段结构变更时，把域前缀里的
   `v1` 改成 `v2` 就整体作废；而自增版本号会让旧键永久留在 Redis 里。
4. Redis 重连后清空本地 L1。断连期间本实例收不到失效广播，重连时 L1 里可能存着已经被
   别人改掉的值，而且没有 TTL 能让它自己过期。挂 `ConnectionRestored` 事件清 L1。
5. 保留一个 superadmin 的清缓存入口，作为最后兜底。

有一个必须提前处理的副作用。`AccessApiKey.LastUsedAt` 的回写藏在缓存回源路径里
（`ProxyAccessService.cs:113`）：只有缓存未命中时才会写一次。TTL 是 60 秒时它至少每
分钟更新一次；改成无过期以后，只有改 Key 主动失效时才更新一次，这个字段等于停止工作。
必须先把它从回源路径里拿出来，否则「最近使用时间」会静默失真。

另有两点边界。定价缓存现在的自增版本号方案在无过期模式下会导致 Redis 键永久堆积，
必须同步换成前缀失效，这两件事得一起做。熔断、容量、渠道亲和这三类数据本身带时间
语义，不属于「配置缓存」，继续保留各自的过期策略，不在本次改造范围内。

最后一点运维前提：既然缓存要长期驻留，Redis 需要开启 AOF 或 RDB 持久化。即使没开也
不致命，重启后缓存全空会自然回源重建，而且前缀索引与缓存键一起丢失，两边是自洽的，
不会出现「索引在但键已丢」或反过来的漏删。

## 5. 目标架构

### 5.1 缓存契约

`ICacheService` 增加两个方法，其余保持不变：

```csharp
public interface ICacheService
{
    Task<T?> GetOrCreateAsync<T>(string key, Func<Task<T?>> factory, TimeSpan? ttl = null);
    Task RemoveAsync(string key);
    Task RemoveAsync(IEnumerable<string> keys);

    // 无过期写入。key 归属 prefix 域，写入时登记到该域索引。
    Task<T?> GetOrCreatePersistentAsync<T>(string prefix, string key, Func<Task<T?>> factory);

    // 按域失效：清 L1 中该域全部条目、清 Redis 中该域全部键、广播通知其它实例。
    Task RemoveByPrefixAsync(string prefix);
}
```

`TwoLevelCacheService` 的三处实现要点。

L1 的前缀失效不要自己维护 key 集合，用 `CancellationTokenSource` 配合
`MemoryCacheEntryOptions.AddExpirationToken`：每个前缀持有一个 CTS，写入条目时挂上它的
token，失效时 `Cancel()` 并换一个新 CTS。`IMemoryCache` 会自动逐出所有挂了该 token 的
条目，线程安全，且不需要枚举键。

L2 的前缀失效用 Redis Set 作为域索引，不要用 `KEYS` / `SCAN`。写入时把逻辑键 `SADD`
进 `{prefix}__index`，失效时 `SMEMBERS` 拿到键列表、批量 `KeyDelete`，最后删索引本身。
这样代价只与该域的键数量相关，与整库无关。索引 Set 本身也不设过期。

实现时注意一个容易踩的点：`RedisConnectionProvider.GetDatabase()` 返回的是
`WithKeyPrefix` 包装过的实例（`RedisConnectionProvider.cs:55-58`），所以索引 Set 的键和
成员对应的实际键都会自动带上全局前缀，而 Set 里存的成员应当是不带前缀的逻辑键。
两边保持一致就自洽，但如果中途改用 `IServer.Keys` 之类不走包装的 API，前缀不会自动补上。

失效广播消息要区分两种类型。当前格式是 `{instanceId}|{key}`，扩展为
`{instanceId}|K|{key}` 与 `{instanceId}|P|{prefix}`。滚动发布期间会出现新旧实例并存，
所以要先发一个「能同时识别新旧两种格式」的版本，下一个版本再改发送方，否则升级窗口内
会有失效消息被丢弃。

### 5.2 缓存域划分

键格式统一为 `{结构版本}:{域}:{维度}`，例如 `v1:user:id:{guid}`。域前缀就是
`v1:user:`，前缀失效以它为单位。结构版本只在缓存值的字段结构变化时手工递增。

| 域前缀 | 内容 | 所有者服务 | 失效触发点 | 过期 |
| --- | --- | --- | --- | --- |
| `v1:user:` | 用户快照，按 id 与 username 两个维度 | `UserService` | 建、改、删用户；启动播种超管 | 无 |
| `v1:apikey:` | API Key 快照，按 hash | `ApiKeyService` | 建、改、删 Key；删用户级联 | 无 |
| `v1:channel:` | 渠道集合，按 owner 与全量两个维度 | `ConfigService` | 渠道任意写；批量导入；删用户级联 | 无 |
| `v1:channel-model:` | 渠道模型映射，按 channelId | `ConfigService` | 渠道保存；渠道删除 | 无 |
| `v1:catalog:` | `ModelProviders` 全表、全局 `ModelInfos` | `ModelCatalogService` | 目录任意写；导入；官方目录同步 | 无 |
| `v1:pricing:` | 定价解析结果、方案、规则 | `ModelCatalogService` | 价格相关任意写 | 无 |
| `v1:websearch:` | `WebSearchSettings` 单行配置 | `WebSearchService` | 保存 Web Search 配置 | 无 |

三点说明。`TavilyKeys` 不进这个表：`UsageCount` 每次搜索都变，缓存它只会制造不一致，
应该让它每次现查并用原子自增写。渠道的两个维度共用一个域前缀，因为渠道的任何变更都会
同时影响两者，分开失效没有收益只增加漏删概率。熔断、容量、亲和不在此表内，保持现状。

### 5.3 数据访问约定

改造后所有新写的数据访问代码都要满足下面六条，评审时按此检查：

1. 只读查询用 `TableNoTracking`，并投影成 DTO 或匿名类型，不返回整实体。需要写回的
   查询才用 `Table`。
2. 更新一律走部分列更新 `UpdateAsync(entity, propNames)` 或 `ExecuteUpdateAsync`。
   全实体 `Update(entity)` 只在「确实要覆盖全部字段」的场景使用，并写注释说明。
3. 聚合、分组、分桶、取 TopN 一律下推数据库。内存里只做「数据库表达不了」的计算，
   例如定价的复合打分匹配。
4. 一个业务动作一次提交。仓储引入 `AutoSaveChanges` 开关，批量场景先关掉、最后统一
   `SaveChangesAsync()`。
5. 同一业务规则只有一份 `Expression` 定义，同时用于 SQL 下推与内存判断。
   「请求成功 / 失败」是第一个要收敛的对象。
6. 服务层不直接依赖 `IOpenCodexDbContext`。`ObservabilityService` 与 `LogContentStore`
   的直连要收进仓储或专门的数据访问类型。

第 5 条给一个具体形态，避免实施时又写出第五份判定：

```csharp
public static class RequestLogSpec
{
    public static Expression<Func<RequestLog, bool>> Successful() =>
        log => log.LifecycleStatus == ProxyRequestLifecycleStatus.Success
            || (log.LifecycleStatus == null
                && log.StatusCode < 400
                && (log.Error == null || log.Error == ""));

    public static Expression<Func<RequestLog, bool>> Failed() =>
        log => log.LifecycleStatus == ProxyRequestLifecycleStatus.Failed
            || (log.LifecycleStatus == null
                && (log.StatusCode >= 400 || (log.Error != null && log.Error != "")));

    public static Expression<Func<RequestLog, bool>> ExcludesInternalTypes() =>
        log => log.RequestType == null
            || (log.RequestType != ProxyRequestTypes.Attempt
                && log.RequestType != ProxyRequestTypes.Diagnostic);
}
```

落地时要注意：attempt 重试统计现在用的是「不带 `LifecycleStatus IS NULL` 前置条件」
的第四种写法，改成统一定义后它的统计结果会发生变化。这是修正而不是回归，但需要同步
更新对应的测试期望，并在发布说明里提一句。

## 6. 分阶段任务单元

每个单元控制在三个业务文件以内（测试文件不计），可以独立提交、独立回滚。阶段之间有
依赖顺序，阶段内的单元可以并行。

### 6.1 阶段 0：地基，不改任何业务行为

#### U0.1 缓存契约扩展

目标：给 `ICacheService` 增加 `RemoveByPrefixAsync` 与 `GetOrCreatePersistentAsync`，
在 `TwoLevelCacheService` 里按 5.1 的三个要点实现，并让广播消息同时识别新旧两种格式。

涉及文件：`OpenCodex.CoreBase/Caching/ICacheService.cs`、
`OpenCodex.Core/Services/Caching/TwoLevelCacheService.cs`、
`tests/OpenCodex.Api.Tests/TestCacheService.cs`。

风险：广播格式变更在滚动发布窗口内可能丢失失效消息。本单元只增加「识别」能力、
不改发送格式，把发送方切换留到 U3.3，规避这个窗口。

验收：新增单元测试覆盖「写入两个同域键、前缀失效后两者都不命中」、「Redis 不可用时
前缀失效只影响 L1 且不抛异常」、「域索引 Set 在失效后被删除」。

#### U0.2 分域缓存键定义

目标：在 `CacheKeys` 里按 5.2 的表新增分域键与域前缀常量。旧的键方法全部保留不动，
本单元不改任何调用方，只是把新定义准备好。

涉及文件：`OpenCodex.CoreBase/Caching/CacheKeys.cs`。

风险：几乎没有。唯一要注意的是新旧键不能撞名，新键统一带 `v1:` 前缀即可区分。

验收：编译通过；新增一个测试断言各域前缀互不为对方的前缀，避免 `v1:channel:` 误伤
`v1:channel-model:` 这类问题。

#### U0.3 仓储能力补齐

目标：给 `IRepository` 增加 `AutoSaveChanges` 开关与配套的 `SaveChangesAsync` 语义，
参考 `Ylg.Data.IDbContext:64-71` 的做法；同时暴露基于 `ExecuteUpdateAsync` 的原子更新
入口，供 U1.2 使用。

涉及文件：`OpenCodex.CoreBase/Data/IRepository.cs`、
`OpenCodex.CoreBase/Data/IOpenCodexDbContext.cs`、`OpenCodex.Data/EfRepository.cs`。

风险：`AutoSaveChanges` 是 `IOpenCodexDbContext` 上的状态，而 context 是 Scoped，
忘记 Reset 会让同一请求后续的写操作静默不落库。实现时用 `IDisposable` 作用域对象
而不是裸开关，让它出作用域自动恢复。

验收：默认行为与改造前完全一致（所有现有测试不改动即通过）；新增测试覆盖
「关闭自动提交后连续三次写入只产生一次提交」。

### 6.2 阶段 1：止血，先修会出错的地方

#### U1.1 RequestLogs 改为部分列更新

目标：`MarkProcessing`、`CompleteLog`、OCR 子日志认领三处写入改用
`UpdateAsync(entity, propNames)`，每处只声明自己真正负责的列。

涉及文件：`OpenCodex.Core/Services/Proxy/ProxyLogService.cs`。

风险：这是本阶段最需要小心的单元。漏声明一列，该列就再也不会被写入，而且不会报错。
必须逐个方法把「本方法负责哪些列」列成清单写进注释，并用测试覆盖每个生命周期阶段
写入后各列的值。

验收：新增测试模拟「流式请求先写 TTFT、后写最终 usage」，断言两者最终都在库里；
这个场景在改造前会失败，是本单元的核心回归防线。

#### U1.2 Tavily 配额原子自增

目标：`ReserveTavilyKeyById` 改成一条带条件的原子更新，用返回的受影响行数判断是否
抢到配额，抢不到就换下一把 Key。

涉及文件：`OpenCodex.Core/Services/WebSearchService.cs`。

参考写法：

```csharp
var affected = await _keyRepository.Table
    .Where(key => key.Id == keyId && key.UsageCount < key.UsageLimit)
    .ExecuteUpdateAsync(setters => setters
        .SetProperty(key => key.UsageCount, key => key.UsageCount + 1)
        .SetProperty(key => key.UpdatedAt, now));
if (affected == 0) { /* 配额已满或已被别人抢走，换下一把 */ }
```

风险：`ExecuteUpdate` 绕过 ChangeTracker，同一 Scope 内如果之前已经加载过这个实体，
内存里的 `UsageCount` 会是旧值。返回 DTO 时要么重新查一次，要么直接用「旧值 + 1」，
不能继续用被跟踪实体的字段。

验收：新增并发测试，10 个并发请求抢一把 `UsageLimit=5` 的 Key，断言成功次数恰好是 5。

#### U1.3 统计三接口聚合下推

目标：`ReadStatsTimeseries`、`ReadStatsModelDistribution`、`ReadStatsErrorDistribution`
以及 `QueryStats` 内部的分桶，全部改成数据库端 `GroupBy`，进程里只做格式化。
同时把成功 / 失败判定抽成 `RequestLogSpec`（见 5.3）。

涉及文件：`OpenCodex.Core/Services/ObservabilityService.cs`、
新增 `OpenCodex.Core/Services/Observability/RequestLogSpec.cs`。

参考写法：

```csharp
// 时间序列：桶号在数据库端算，不取回原始行
var buckets = query
    .GroupBy(log => (long)((log.CreatedAt - startTs) / bucketSeconds))
    .Select(g => new
    {
        Bucket = g.Key,
        Count = g.Count(),
        Cost = g.Sum(x => x.Cost),
        InputTokens = g.Sum(x => x.InputTokens),
        CachedTokens = g.Sum(x => x.CachedTokens),
        OutputTokens = g.Sum(x => x.OutputTokens),
        TtftSum = g.Sum(x => x.TtftMs > 0 ? x.TtftMs!.Value : 0),
        TtftCount = g.Count(x => x.TtftMs > 0)
    })
    .ToList();

// 模型分布：TopN 也下推
var models = query
    .GroupBy(log => log.Model == null || log.Model == "" ? "unknown" : log.Model)
    .Select(g => new { Model = g.Key, Count = g.Count() })
    .OrderByDescending(x => x.Count)
    .Take(20)
    .ToList();

// 错误分布：复合键分组
var errors = query
    .Where(RequestLogSpec.Failed())
    .GroupBy(log => new { log.ChannelId, log.StatusCode })
    .Select(g => new { g.Key.ChannelId, g.Key.StatusCode, Count = g.Count() })
    .OrderByDescending(x => x.Count)
    .Take(30)
    .ToList();
```

风险有三个。第一，`(long)((CreatedAt - startTs) / bucketSeconds)` 是 double 转整型，
SQLite 与 PostgreSQL 的翻译结果和边界舍入行为可能不同，必须用 `ToQueryString()` 在两个
provider 上各看一遍，再用同一批数据比对新旧实现的输出。第二，空桶在 `GroupBy` 里不会
出现，需要在内存里按桶号补齐零值，否则前端图表会缺点。第三，TTFT 平均值改成
`TtftSum / TtftCount` 后，浮点累加顺序变化可能让末位有 0.1 级别的差异，测试断言要留容差。

验收：同一批种子数据下，新旧实现的输出逐字段比对一致（除 TTFT 容差）；新增一个
「时间窗内 5 万条日志」的测试断言查询过程中进程内存增长在可接受范围。

#### U1.4 清空日志改用 LINQ

目标：去掉 `ExecuteSqlRaw` 与 `DatabaseProvider` 字符串分叉，五张表按依赖顺序各来一次
`ExecuteDelete()`，用返回的行数替代前面三次 `COUNT(*)`。

涉及文件：`OpenCodex.Core/Services/ObservabilityService.cs`。

风险：删除顺序不能颠倒，清单对块是 `RESTRICT`。改完后 PostgreSQL 不再走 `TRUNCATE`，
大表上耗时会变长、且会产生 WAL；如果生产日志量很大，需要评估是否加上分批删除。
SQLite 侧行为不变，`DELETE` 依旧不把空间还给操作系统，仍需 `VACUUM`。

验收：现有清空日志的测试断言的返回行数语义不变；补一个测试断言五张表在调用后都为空。

### 6.3 阶段 2：把查询逻辑收敛到唯一归属

#### U2.1 用户读取域统一

目标：新增一个 `UserLookup`（或直接放在 `UserService` 上）作为「按 id / 按 username 取
用户快照」的唯一入口，走 `v1:user:` 域缓存；`SessionService.RequireUser` 与
`ProxyLogService.ResolveOwnerUserId` 都改为调用它。

涉及文件：`OpenCodex.CoreBase/Services/ISessionService.cs`、
`OpenCodex.Core/Services/SessionService.cs`、
`OpenCodex.Core/Services/Proxy/ProxyLogService.cs`。

风险：`ISessionService` 现在是同步接口（`RequireUser` / `RequireSuperadmin`），而缓存是
异步的。有两个选择：把接口异步化，代价是波及所有 Controller 与 `IWorkContext`；或者给
`ICacheService` 加一个同步读入口。建议前者，理由是同步阻塞异步在 Redis 超时时会吃线程池；
但它会让这个单元超出三文件限制，所以要再拆一次：先加异步方法并保留同步方法，
再逐个 Controller 切换，最后删同步方法。这三步各自独立提交。

验收：管理台任意接口连续调用两次，第二次不再产生 `SELECT ... FROM "Users"`；
改用户后立即调用，能读到新值（验证失效链路）。

#### U2.2 模型目录字典表进缓存

目标：`ModelProviders` 全表与「全部启用的全局 `ModelInfos`」进 `v1:catalog:` 域，
目录任意写操作后整域失效。这两个查询在捕获里分别是 188 次和 98 次。

涉及文件：`OpenCodex.Core/Services/ModelCatalogService.cs`。

风险：目录导入与官方同步会在一个事务里做大量写，必须在事务提交之后才失效缓存，
不能在事务中间失效，否则并发请求会把未提交的中间状态读进缓存。

验收：连续两次调用定价解析只产生一次 providers 查询；导入后立即查询能读到新目录。

#### U2.3 模型目录列表消除 N+1

目标：`ToModelResponse` / `ToChannelModelResponse` 不再逐个模型查价格方案，
改成先一次性把当页模型的方案与规则批量取回，在内存里按 `ModelInfoId` 组装。

涉及文件：`OpenCodex.Core/Services/ModelCatalogService.cs`。

参考思路：方案表按 `ModelInfoId IN (...)` 一次取回后，用 `GroupBy(ModelInfoId)` 再
`OrderByDescending(UpdatedAt).First()` 在内存里选出每个模型的最新方案；规则表同理按
`PricingPlanId IN (...)` 一次取回。50 个模型从 101 条 SQL 降到 3 条。

风险：`IN (...)` 的参数个数等于当页模型数，SQLite 上限是 999，页大小要设上限。

验收：列 50 个模型时的 SQL 条数从 101 降到 3；响应内容与改造前逐字段一致。

#### U2.4 渠道映射同步与级联删除收敛

目标：`SyncChannelModelMappings` 的「先全删再全插」改成一个不自动提交的批次，
一次 `SaveChangesAsync`；`DeleteMappingsForChannels` 改用 `ExecuteDelete()`，
不再把旧映射全部加载进内存。同时把「删渠道必须删映射」「删用户必须删 Key 与渠道」
这两条级联关系收进各自服务的一个方法，禁止调用方自行拼装。

涉及文件：`OpenCodex.Core/Services/ConfigService.cs`、
`OpenCodex.Core/Services/UserService.cs`。

风险：`ExecuteDelete` 会立即执行、不参与外层 `SaveChanges` 的原子性。如果这一步之后的
插入失败，映射就被删掉了却没有新的。需要把删除与插入放进显式事务。

验收：改一次渠道的 SQL 条数明显下降；新增测试断言「删渠道后无孤儿映射」
「删用户后无孤儿 Key 与渠道」。

### 6.4 阶段 3：切换到无过期持久化

这一阶段的前提是阶段 0 到 2 全部完成。在失效链路收敛之前不要动 TTL。

#### U3.1 定价缓存换成前缀失效

目标：键从 `pricing:context:r{redis}:l{local}:{channelId}:{model}` 简化为
`v1:pricing:ctx:{channelId}:{model}`；删掉 Redis 版本号自增、`_localPricingVersion`、
`_lastKnownRedisPricingVersion`、`_pendingRedisPricingVersionBump` 这一整套机制；
`BumpPricingVersion()` 的十几个调用点改为调 `RemoveByPrefixAsync("v1:pricing:")`。
同时把定价规则与 provider 排序也纳入这个域，不再每次现查。

涉及文件：`OpenCodex.Core/Services/ModelCatalogService.cs`、
`OpenCodex.CoreBase/Caching/CacheKeys.cs`。

风险：现有版本号机制有一个隐含好处，就是「失效」等于「换键」，天然不需要跨实例通信。
改成前缀失效后，跨实例一致性完全依赖 Pub/Sub 广播。Redis 断连期间其它实例会继续用旧
定价，且没有 TTL 让它过期，只能靠 U0.1 里那条「重连后清空本地 L1」自愈。价格算错会
直接影响计费，所以这个单元要单独发布、单独观察，不要和其它单元一起上。

验收：改一次价格后，两个实例都能在广播到达后读到新价；杀掉 Redis 再恢复，实例重连后
不再返回旧价；Redis 里不再出现带版本号的历史键。

#### U3.2 渠道快照并入统一缓存

目标：`ConfigService` 与 `ObservabilityService` 里的裸 `IMemoryCache` 渠道快照改走
`ICacheService` 的 `v1:channel:` 域；`ProxyRouteService` 的路由缓存也归入同一域，
缓存值从 `List<Channel>` 实体换成专用快照类型。

涉及文件：`OpenCodex.Core/Services/ConfigService.cs`、
`OpenCodex.Core/Services/ObservabilityService.cs`、
`OpenCodex.Core/Services/Proxy/ProxyRouteService.cs`。

风险：三个地方对渠道的字段需求不同。路由要 `Priority`、`Capacity`、`CompatJson`、
`ModelsJson`；观测只要轻量字段。合并成一个快照类型会让观测侧多传字节，拆成两个键又要
两次回源。建议一个域内放两个键：`v1:channel:route:{owner}` 与 `v1:channel:all`，
失效时整域一起清，回源各取所需。

验收：多实例下改渠道，另一个实例的管理台列表在广播到达后立即变化（改造前要等 10 秒）。

#### U3.3 全域切换无过期，处理 LastUsedAt

目标：5.2 表里的七个域全部改用 `GetOrCreatePersistentAsync`；把
`AccessApiKey.LastUsedAt` 的回写从缓存回源路径里移出来；把失效广播的发送格式切到新格式；
新增一个 superadmin 的清缓存接口。

涉及文件：`OpenCodex.Core/Services/Proxy/ProxyAccessService.cs`、
`OpenCodex.Core/Services/Caching/TwoLevelCacheService.cs`、
`OpenCodex.Api` 下新增一个运维端点。

`LastUsedAt` 有三个可选处理方式，建议第二个：

| 方式 | 做法 | 代价 |
| --- | --- | --- |
| 放弃精度 | 只在缓存回源时写 | 该字段基本停止更新，等于废弃 |
| 节流异步写 | 鉴权成功后记进内存，后台每 N 秒批量 `ExecuteUpdate` 一次 | 需要一个后台任务，精度到 N 秒 |
| 每次都写 | 从缓存路径移到鉴权主路径，每请求一次 UPDATE | 热路径上每请求一次写，代价最高 |

风险：这是唯一一个「一旦漏了失效点就产生永久脏数据」的单元。上线前要把 5.2 表里每个域
的失效触发点逐条对照代码走查一遍，确认没有绕过所有者服务的写入路径。建议先在预发环境
跑一轮完整的管理台操作（建、改、删用户 / Key / 渠道 / 模型 / 价格），每步之后立即查询验证。

验收：Redis 里所有业务缓存键 `TTL` 返回 -1；上述完整管理台操作序列中每步之后的查询都
返回新值；清缓存接口调用后所有域被清空且服务正常回源。

### 6.5 阶段 4：收尾

#### U4.1 内容寻址的幂等插入

目标：去掉 `LogContentStore` 里按 `Database.ProviderName` 字符串分叉的两段原生 SQL。

涉及文件：`OpenCodex.Core/Services/Proxy/LogContentStore.cs`。

推荐做法是改成纯 LINQ 的「查、插缺失、冲突后重查」：先按 `Sha256 IN (...)` 查出已存在的
块与清单，只 `AddRange` 缺失的那些，捕获唯一索引冲突（`DbUpdateException`）后重查一次
并继续。之所以可行，是因为高度重复的内容（同一 system prompt）在第一步查询就命中了，
根本走不到插入；异常只会在「两个请求同时首次写入同一份全新内容」时发生，是罕见竞态。
这样既满足只用 LINQ，也不牺牲正确性。

备选做法是保留 `INSERT ... ON CONFLICT DO NOTHING`，但把 provider 分叉从服务层移到
`OpenCodex.Data`，做成一个 `ILogContentUpsert` 接口按 provider 注册实现。如果实测发现
竞态冲突频率超出预期，退回这个方案。

风险：捕获 `DbUpdateException` 后 EF 的 ChangeTracker 处于脏状态，必须把失败的条目
Detach 掉再重试，否则下一次 `SaveChanges` 会重复报错。这一点参考项目里有现成写法
（`Ylg.Data/EfRepository.cs` 的 `GetFullErrorTextAndRollbackEntityChanges`）。

验收：新增并发测试，两个线程同时写同一份全新内容，断言最终库里只有一份块与一份清单；
两个 provider 上分别跑一遍。

#### U4.2 模糊过滤与索引

目标：让日志列表的过滤真正吃到索引。

涉及文件：`OpenCodex.Core/Services/ObservabilityService.cs`，可能涉及一个新迁移。

现状是 `Contains` 翻译成 SQLite 的 `instr(...) > 0` 和 PostgreSQL 的前置通配 `LIKE`，
`IX_RequestLogs_Model` 这类单列索引完全用不上。三个可选方向，需要产品先做决策：
把「包含」改成「前缀匹配」（`StartsWith`，能吃到索引，但改变搜索行为）；PostgreSQL 上
加 `pg_trgm` 扩展与 GIN 索引（不改行为，但 SQLite 侧仍无解）；或者接受现状，只给日志
列表加时间范围必填约束，让时间索引先把结果集收窄。

风险：前两个方向都会让两个 provider 的行为进一步分叉。第三个方向最省事但会改变前端交互。

顺带处理 SQLite 的参数上限问题：重试统计的 `IN (@ids1...@idsN)` 的 N 等于当页条数，
页大小 500 时接近 SQLite 的 999 上限，需要给页大小设一个硬上限或对 ID 列表分批。

#### U4.3 清空日志加范围与审计

目标：给清空日志加上 owner 与时间范围参数，以及一条操作审计记录。

涉及文件：`OpenCodex.Core/Services/ObservabilityService.cs` 以及对应的 Controller。

风险：这是接口行为变更，前端需要同步改。如果暂时不做，至少要在接口上加二次确认参数，
避免误触清空全库所有用户的日志。

## 7. 测试策略

这次改造有一个天然优势：SQL 清单那次捕获已经证明「挂 `RelationalEventId.CommandExecuted`
钩子抓全部 SQL」这条路走得通。建议把它从一次性探针固化成测试基础设施。

具体做法是在测试用的 DbContext 工厂里挂一个收集器，提供
`AssertSqlCount(expected)` 与 `AssertNoTableScan("RequestLogs")` 之类的断言。有了它，
N+1 与全表读就变成可以被测试抓住的回归，而不是靠人工复审。这一项建议在阶段 1 之前先做，
它是 U1.3、U2.2、U2.3 的验收工具。

其余按层补：

| 层次 | 覆盖对象 | 关键用例 |
| --- | --- | --- |
| 缓存契约 | `TwoLevelCacheService` | 前缀失效、Redis 不可用降级、重连清 L1、广播新旧格式并存 |
| 业务规则 | `RequestLogSpec` | 同一批数据下 SQL 下推结果与内存判断结果一致 |
| 写路径 | `ProxyLogService` | 流式请求 TTFT 与最终 usage 先后写入互不覆盖 |
| 并发 | Tavily 配额、内容寻址插入 | 配额不超发、同内容只落一份 |
| 失效链路 | 各所有者服务 | 每个域「改完立即读到新值」，逐域一条 |
| 双 provider | 聚合下推 | SQLite 与 PostgreSQL 输出一致 |

最后一行是当前最大的测试缺口：测试套件全部硬编码 sqlite，PostgreSQL 侧零覆盖。聚合下推
改造以后，两个 provider 在 double 转整型、除法舍入、`NULL` 参与比较上的差异会直接影响
统计数字。建议引入 Testcontainers 起一个 `postgres:17-alpine`，至少让统计相关的用例
在两个 provider 上各跑一遍。

## 8. 已知取舍与未覆盖边界

有几件事本方案明确不做，或者做不彻底，先说清楚免得后面误解。

定价的模型匹配继续在内存做。「精确 / 前缀 / 通配 + 优先级 + 模式长度 + provider 排序」
的复合打分写不进 `WHERE`，现有代码显式 `AsEnumerable()` 后在内存排序取第一名，这是正确
选择，不改。要改的只是把参与打分的候选集合缓存起来，减少回源次数。

SQLite 上价格列是 `TEXT` 而 PostgreSQL 是 `numeric(18,8)`，这是唯一会导致结果不同而不只是
SQL 文本不同的差异。目前所有价格比较都在内存完成，所以还没踩到。本方案不改列类型
（涉及双 provider 迁移），但要立一条禁令：任何人不得写 `Where(rule => rule.UnitPrice > x)`
这类下推到数据库的价格比较，否则 SQLite 上会变成字符串比较。建议在评审清单里加这一条。

数据库外键不加。渠道与映射、用户与 Key / 渠道之间没有外键，加上需要双份迁移并处理历史
孤儿数据。本方案只做服务层的级联收敛（U2.4），把「必须一起删」的关系收进单一方法，
外键作为后续独立任务。

日志表的分区与归档不在范围内。`RequestLogs` 会持续增长，聚合下推能解决进程内存问题，
但解决不了表本身变大之后的查询变慢。这需要按时间分区或定期归档，是独立课题。

Redis 内存增长需要监控。无过期以后 `v1:pricing:` 域的键数量上限是渠道数乘以模型数，
当前 50 个渠道、120 个全局模型意味着最多 6000 个键，规模可接受；但如果将来渠道或模型
大幅增加，需要重新评估，必要时给这一个域单独恢复过期时间。这也是为什么 5.2 的表要按域
列出过期策略，而不是全局一个开关。

最后是本次的实施建议：阶段 1 的四个单元优先做，它们修的是正确性问题，不做的话后面所有
优化都建立在会丢数据的基础上。阶段 3 的 U3.1 与 U3.3 建议单独发布并观察，它们一个影响
计费、一个影响缓存一致性，混在其它改动里出问题不好定位。
