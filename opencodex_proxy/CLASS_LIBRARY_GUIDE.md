# 类库项目设计指南

本文档说明 API 后端项目中类库的拆分、依赖、公共 API、配置、测试和发布规则。目标是让类库边界清晰、可复用、可测试，并避免随着业务增长变成互相依赖的大杂烩。

不包含范围：

- 前端、页面模板、JavaScript、CSS
- 认证、授权、Token、权限策略细节
- Factory 相关设计
- 某个具体项目、目录或业务模块的约定

## 一、类库设计目标

类库设计应优先满足：

- 职责单一，边界清晰。
- 依赖方向稳定，不循环依赖。
- 公共 API 小而明确。
- 实现细节不泄漏给调用方。
- 可以独立测试。
- 可以被多个应用复用。
- 可以在未来拆分、替换或发布为包。

## 二、推荐类库类型

常见类库可按职责拆分：

- Abstractions：接口、抽象类型、公共契约。
- Domain：实体、值对象、枚举、领域常量和领域行为。
- Application：业务服务、用例编排、业务结果。
- Infrastructure：基础设施实现，例如缓存、日志扩展、文件、时钟、ID 生成。
- DataAccess：DbContext、Repository、实体映射、数据库查询。
- ExternalIntegrations：第三方系统客户端、SDK 包装、外部响应转换。
- Contracts：跨进程或跨服务共享的请求/响应契约。
- SharedKernel：极少量真正跨业务域共享的基础类型。

不要一开始就把所有类型都放进 `Common`、`Core`、`Utils` 这类模糊类库。

## 三、依赖方向

推荐依赖方向：

```text
API / Host
    -> Application
        -> Domain
        -> Abstractions
    -> DataAccess
        -> Domain
        -> Abstractions
    -> Infrastructure
        -> Abstractions
    -> ExternalIntegrations
        -> Abstractions
```

规则：

- Domain 不依赖 Application、DataAccess、Infrastructure、API。
- Abstractions 不依赖具体实现类库。
- Application 不依赖 API。
- DataAccess 不依赖 API。
- Infrastructure 不依赖 API。
- ExternalIntegrations 不把第三方 SDK 类型泄漏到 Application。
- Host / API 负责组合依赖，不把组合逻辑下沉到领域层。

禁止：

- 循环引用。
- 领域层引用数据访问层。
- 类库为了读取请求信息直接依赖 Web 框架。
- 通用类库引用具体业务应用。

## 四、项目引用规则

新增项目引用前先确认：

- 这个依赖是否符合分层方向。
- 是否可以依赖接口而不是实现。
- 是否会引入循环依赖。
- 是否会把重型依赖传染给不需要它的调用方。
- 是否会让低层类库知道高层应用细节。

如果只是为了复用一个小工具，不应让整个类库依赖另一个庞大类库。可以考虑提取更小的抽象或工具类型。

## 五、公共 API 暴露原则

类库公共 API 应尽量少：

- 只把调用方真正需要的类型设为 `public`。
- 内部实现优先使用 `internal`。
- 不暴露第三方 SDK 类型，除非该类库本身就是对 SDK 的薄封装且调用方明确接受该耦合。
- 不暴露数据库实体给远程 API 调用方。
- 不把异常类型、内部状态机细节、数据库字段名作为公共契约。
- 公共 API 一旦被多个项目使用，修改时按兼容性处理。

公共类型命名要表达业务语义，避免 `Helper`、`Manager`、`Util` 泛滥。

## 六、Abstractions 规则

Abstractions 类库只放稳定契约：

- 接口。
- 抽象基类。
- 轻量契约模型。
- 枚举或常量。
- Result 类型或基础错误类型。

Abstractions 不应包含：

- 具体数据库访问。
- 具体外部 SDK 调用。
- 具体框架启动逻辑。
- 复杂业务流程实现。

如果接口只被一个实现和一个调用方使用，先不急着抽到 Abstractions。

## 七、Domain 规则

Domain 类库表达核心业务概念：

- 实体。
- 值对象。
- 领域枚举。
- 领域常量。
- 简单领域行为。

Domain 应保持纯粹：

- 不依赖 Web 框架。
- 不依赖 DbContext。
- 不依赖外部 SDK。
- 不依赖配置系统。
- 不依赖依赖注入容器。

领域对象可以包含不需要外部资源的业务判断。需要数据库、外部系统、缓存或当前用户上下文的流程，应放到 Application / Service。

## 八、Application 规则

Application 类库承载业务用例：

- 业务服务。
- 业务校验。
- 事务边界。
- 调用 Repository。
- 调用外部集成接口。
- 返回业务结果。

Application 不应：

- 返回 HTTP 响应对象。
- 直接依赖 Controller。
- 直接暴露第三方 SDK 类型。
- 直接读取配置 key。
- 直接持有容器对象。

Application 可以依赖 Abstractions、Domain，以及必要的数据访问抽象。

## 九、DataAccess 规则

DataAccess 类库封装数据库细节：

- DbContext。
- Repository。
- 实体映射。
- 查询扩展。
- 数据迁移。

规则：

- 查询场景优先无跟踪。
- 写入场景明确事务边界。
- 不把数据库异常直接抛给 API 调用方。
- 不在 DataAccess 中决定 HTTP 状态码。
- 不把 DataAccess 作为业务规则堆放地。

如果查询高度业务化，应由 Application 组织；DataAccess 提供必要查询能力。

## 十、Infrastructure 规则

Infrastructure 类库封装通用技术能力：

- 时钟。
- ID 生成。
- 文件访问。
- 缓存实现。
- 消息发送。
- 日志扩展。
- 配置绑定。

规则：

- 通过接口暴露可替换能力。
- 不把基础设施实现写进业务服务。
- 配置对象使用强类型。
- 基础设施类库不应反向依赖具体业务模块。

## 十一、ExternalIntegrations 规则

外部集成类库用于隔离第三方系统：

- 包装第三方 SDK。
- 统一超时、重试、日志和错误转换。
- 将外部响应转换为内部模型。
- 隐藏第三方认证、签名、序列化细节。

规则：

- 不让第三方 SDK 类型泄漏到业务服务层。
- 不让 Controller 直接调用第三方 SDK。
- 外部失败要转换为内部结果或明确异常。
- 对可能重复调用的外部动作设计幂等。

## 十二、DTO、Entity、Options、Constants 放置规则

推荐放置：

- Entity：Domain。
- ValueObject：Domain。
- API Request / Response DTO：API 或 Contracts。
- 跨进程契约 DTO：Contracts。
- Service 内部 DTO：Application。
- EF 映射对象：DataAccess。
- Options / Settings：使用方所在类库，或 Infrastructure。
- Constants / Defaults：靠近使用它们的领域或模块。

不要把所有 DTO、Options、Constants 放到一个全局共享类库里。

## 十三、配置和 Options

配置规则：

- 使用强类型 Options / Settings。
- 启动时绑定并校验关键配置。
- 类库不要自己到处读取配置 key。
- 密钥、连接串、证书不写入源码。
- 默认值来源要明确。

类库如果需要配置，应通过构造函数注入强类型配置对象或 Options 抽象。

## 十四、依赖注入扩展

类库可以提供注册扩展方法：

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddResourceModule(this IServiceCollection services)
    {
        services.AddScoped<IResourceService, ResourceService>();
        return services;
    }
}
```

或提供 Autofac Module：

```csharp
public class ResourceModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<ResourceService>()
            .As<IResourceService>()
            .InstancePerLifetimeScope();
    }
}
```

规则：

- 注册扩展应只注册本模块相关服务。
- 不在类库注册方法里读取不相关配置。
- 不在类库注册方法里启动后台任务或执行复杂业务逻辑。
- Host / API 负责选择哪些模块被启用。

## 十五、异常和结果

类库内部的错误表达要统一：

- 可预期业务失败返回业务结果对象。
- 不可预期错误抛异常。
- 不直接返回 API 响应对象。
- 不直接决定 HTTP 状态码。
- 不暴露内部异常消息给远程调用方。

底层类库可以抛出明确异常，由上层转换为统一响应。

## 十六、测试规则

每类类库应有对应测试：

- Domain：领域行为和边界值测试。
- Application：业务规则、成功失败路径、事务和幂等测试。
- DataAccess：关键查询、映射、分页和持久化测试。
- Infrastructure：配置绑定、缓存、文件、ID、时钟等行为测试。
- ExternalIntegrations：使用 mock / stub 测试超时、失败、错误转换。
- Abstractions：通常不需要单独测试，但要通过实现测试验证契约。

类库测试不应依赖真实外部系统，除非明确是集成测试。

## 十七、版本和发布

如果类库未来可能发布为包，应提前考虑：

- 包名。
- 语义化版本。
- 公共 API 兼容性。
- 变更日志。
- 依赖包版本范围。
- 是否包含源码生成、配置文件或静态资源。
- 是否需要 XML 注释文档。

公共 API 破坏性变更应升级主版本或提供迁移路径。

## 十八、常见反例

避免：

- 所有公共类型都放进一个 `Common` 类库。
- Domain 引用 DataAccess。
- Application 返回 HTTP 响应对象。
- 类库直接读取请求上下文。
- 类库直接解析 DI 容器。
- 外部 SDK 类型穿透到 Controller。
- DTO、Entity、数据库模型混用。
- 一个类库引用大量不相关包，导致依赖污染。
- 为了复用一个小方法引入一个庞大类库引用。

## 十九、新类库创建清单

新增类库前确认：

- [ ] 这个类库的职责一句话能说清。
- [ ] 它不和已有类库职责重叠。
- [ ] 它的依赖方向符合架构规则。
- [ ] 它不会造成循环引用。
- [ ] 它的公共 API 足够小。
- [ ] 它的实现细节不会泄漏给调用方。
- [ ] 它有必要的测试策略。
- [ ] 它是否可能未来发布为包已经被考虑。
- [ ] 它的配置、日志、异常和依赖注册方式清楚。
