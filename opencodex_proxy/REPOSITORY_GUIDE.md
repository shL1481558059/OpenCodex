# Repository 仓储规范与使用指南

本文档说明 API 后端项目中 Repository 的职责边界、设计方式和使用规则。目标是让数据访问集中、业务逻辑不泄漏到持久化层，同时避免 Repository 变成万能查询工具。

不包含范围：

- 前端、页面模板、JavaScript、CSS
- 认证、授权、Token、权限策略细节
- Factory 相关设计
- 某个具体项目、目录或业务模块的约定

## 一、设计目标

Repository 应解决：

- 隔离数据库访问细节。
- 避免 Controller 直接访问 DbContext。
- 让 Service 通过稳定抽象访问数据。
- 集中处理常见查询、写入、分页、软删除、审计和并发规则。
- 让数据访问可以被测试和替换。

Repository 不应成为：

- 业务规则堆放地。
- 任意 SQL / LINQ 拼装中心。
- 事务边界的唯一拥有者。
- API 返回对象构造器。

## 二、职责边界

推荐调用链：

```text
Controller -> Service -> Repository / DbContext -> Database
```

Controller：

- 不直接访问 Repository。
- 不直接访问 DbContext。
- 不拼写数据库查询。

Service：

- 组织业务规则。
- 决定调用哪些 Repository。
- 控制事务边界。
- 处理业务失败。

Repository：

- 封装基础数据访问。
- 提供明确查询方法。
- 提供新增、更新、删除能力。
- 隔离数据库和 ORM 细节。

DbContext / Unit of Work：

- 管理实体跟踪。
- 管理 SaveChanges。
- 管理事务或暴露事务能力。

## 三、Repository 类型

常见类型：

- 泛型 Repository：提供基础 CRUD 能力。
- 专用 Repository：提供特定聚合或实体的复杂查询。
- 只读 Repository：只暴露查询能力。
- 写 Repository：暴露新增、更新、删除能力。
- Query Service：当查询跨多个聚合且与业务展示强相关时，可以独立成查询服务。

建议：

- 简单实体可以使用泛型 Repository。
- 复杂查询使用专用 Repository 或 Query Service。
- 不要为了所有实体都创建空的专用 Repository。

## 四、接口设计

推荐接口保持小而明确：

```csharp
public interface IRepository<TEntity>
    where TEntity : class
{
    Task<TEntity> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task AddAsync(TEntity entity, CancellationToken cancellationToken = default);
    void Update(TEntity entity);
    void Delete(TEntity entity);
}
```

专用查询接口：

```csharp
public interface IResourceRepository
{
    Task<Resource> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Resource>> GetActiveResourcesAsync(CancellationToken cancellationToken = default);
}
```

规则：

- 方法名表达查询意图。
- 不使用模糊方法名，例如 `QueryData`、`Handle`、`Process`。
- 查询条件复杂时使用查询对象。
- 返回集合优先使用 `IReadOnlyList<T>`。
- 异步方法使用 `Async` 后缀。
- IO 方法传递 `CancellationToken`。

## 五、IQueryable 暴露规则

是否暴露 `IQueryable` 要谨慎。

推荐：

- Repository 内部可以使用 `IQueryable` 组合查询。
- 对外优先暴露明确方法或查询对象。
- 只有在团队明确采用可组合查询模式时，才暴露 `IQueryable`。

暴露 `IQueryable` 的风险：

- 查询逻辑散落到 Service 或 Controller。
- 延迟执行导致异常位置不清晰。
- 跟踪 / 无跟踪规则难以统一。
- Include、分页、排序被调用方随意组合。
- 数据访问细节泄漏到上层。

如果必须暴露，应限制在 Service 层使用，不允许 Controller 使用。

## 六、查询规则

只读查询：

- 默认使用无跟踪查询。
- 只返回业务需要字段。
- 列表查询必须分页或明确限制数量。
- 排序规则必须稳定。
- 避免 N+1 查询。
- 大列表避免一次性加载全部数据。

详情查询：

- 根据业务唯一标识查询。
- 资源不存在时返回 null 或明确结果，由 Service 转换业务失败。
- 是否加载关联数据要由查询方法明确表达。

复杂查询：

- 使用查询对象封装条件。
- 查询对象不应包含 HTTP 请求对象。
- 查询对象字段应是业务语义，而不是数据库字段泄漏。

## 七、写入规则

新增：

- Repository 负责添加实体。
- Service 负责业务校验和默认值决策。
- ID 生成策略要统一。

更新：

- Service 决定允许修改哪些字段。
- Repository 不应盲目覆盖所有字段。
- 并发字段应被正确处理。

删除：

- 优先明确物理删除还是软删除。
- 软删除应统一过滤规则。
- 删除前的业务约束由 Service 判断。

保存：

- 是否由 Repository 调用 SaveChanges 要统一。
- 推荐由 Unit of Work 或 Service 事务边界统一保存。
- 不要在一次业务流程中多个 Repository 各自随意 SaveChanges。

## 八、事务和 Unit of Work

事务边界通常属于 Service 或 Unit of Work。

规则：

- 单个业务用例内多个写操作应在同一事务中。
- Controller 不控制事务。
- Repository 不随意开启独立事务。
- 外部系统调用和数据库事务混用时，要考虑补偿。
- 事务范围不应包含长时间外部调用，除非明确需要。

推荐抽象：

```csharp
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default);
}
```

## 九、分页、排序和过滤

分页规则：

- 页码默认从 1 开始，除非项目明确采用从 0 开始。
- 页大小必须有默认值和最大值。
- Repository 或 Query Service 返回分页对象或分页数据加总数。
- 大数据量列表要避免昂贵总数查询，必要时使用游标分页。

排序规则：

- 默认排序必须稳定。
- 排序字段白名单控制。
- 不直接把客户端传入字段拼接到 SQL。

过滤规则：

- 过滤条件使用明确字段。
- 字符串搜索要考虑大小写、模糊匹配和索引。
- 日期范围要明确闭区间或半开区间。

## 十、软删除和全局过滤

如果项目使用软删除：

- 软删除字段统一命名。
- 默认查询排除已删除数据。
- 需要查询已删除数据时使用明确方法。
- 删除操作更新软删除字段，而不是物理删除。
- 唯一约束要考虑软删除数据。

不要在每个查询里手写软删除条件。应通过全局过滤或统一查询扩展处理。

## 十一、审计字段

常见审计字段：

- 创建时间。
- 修改时间。
- 创建人。
- 修改人。
- 删除时间。
- 删除人。

规则：

- 审计字段由统一机制写入。
- Service 不应在每个方法里重复设置相同审计字段。
- 时间来源应统一，避免直接散落使用系统时间。
- 当前用户信息通过抽象上下文传入，不直接依赖 HTTP 上下文。

## 十二、并发控制

需要考虑并发的场景：

- 库存扣减。
- 余额变更。
- 订单状态流转。
- 审批状态变更。
- 重复提交。

常见方式：

- 乐观并发字段。
- 数据库唯一约束。
- 状态机校验。
- 分布式锁。
- 幂等键。

Repository 负责正确持久化并发字段；Service 负责处理并发失败后的业务结果。

## 十三、异常处理

Repository 可以抛出基础设施异常，但不应返回 API 结果。

规则：

- 数据库异常不要直接暴露给远程调用方。
- 唯一约束冲突应由 Service 转换为业务失败。
- 连接失败、超时等基础设施错误交给全局异常处理或上层转换。
- Repository 不决定 HTTP 状态码。
- Repository 日志应避免记录敏感数据。

## 十四、异步和 CancellationToken

数据库 IO 优先使用异步：

- `GetByIdAsync`
- `ListAsync`
- `AddAsync`
- `SaveChangesAsync`

规则：

- 异步方法使用 `Async` 后缀。
- 传递 `CancellationToken`。
- 避免 `.Result` 和 `.Wait()`。
- 不为了纯内存操作强行异步。

## 十五、测试规则

Repository 测试应覆盖：

- 根据 ID 查询。
- 条件查询。
- 分页和排序。
- 新增、更新、删除。
- 软删除过滤。
- 审计字段写入。
- 并发冲突。
- 唯一约束冲突。

测试方式：

- 关键查询优先使用真实数据库或接近生产的测试数据库。
- 简单业务 Service 测试可以 mock Repository。
- 不要只依赖内存数据库验证复杂 SQL 行为。
- 测试数据准备应清晰可重复。

## 十六、常见反例

避免：

- Controller 直接使用 DbContext。
- Controller 直接使用 Repository。
- Repository 返回 API Result。
- Repository 包含复杂业务规则。
- Repository 每个方法都 SaveChanges。
- Repository 对外暴露任意 IQueryable，被 Controller 随意拼接。
- 查询方法无分页返回大集合。
- 字符串拼接 SQL 且没有参数化。
- 软删除条件散落在每个查询里。
- 用 Repository 掩盖糟糕的模块边界。

## 十七、新项目落地清单

新项目使用 Repository 前确认：

- [ ] 是否真的需要 Repository，还是 DbContext 已足够。
- [ ] 泛型 Repository 和专用 Repository 的边界。
- [ ] 是否允许暴露 IQueryable。
- [ ] 查询默认是否无跟踪。
- [ ] SaveChanges 由谁负责。
- [ ] 事务边界在哪里。
- [ ] 分页、排序、过滤规则。
- [ ] 软删除规则。
- [ ] 审计字段规则。
- [ ] 并发控制规则。
- [ ] 数据库异常转换规则。
- [ ] Repository 测试策略。
