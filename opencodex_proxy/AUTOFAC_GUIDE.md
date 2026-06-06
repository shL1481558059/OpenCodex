# Autofac 注册及使用指南

本文档说明在 API 后端项目中如何使用 Autofac 做依赖注入。目标是让注册集中、生命周期清晰、依赖关系可测试，并避免容器被业务代码滥用。

## 一、基本原则

- 依赖通过构造函数注入，优先使用主构造函数或简写构造函数。
- 注册集中在应用启动或基础设施层。
- 业务代码不直接持有 `IContainer`、`ILifetimeScope` 或容器构建器。
- 优先依赖接口，不依赖具体实现。
- 生命周期必须和对象职责匹配。
- 不在 Controller、Service 或 Repository 中手动 new 复杂依赖。

## 二、接入方式

典型 API 应用可以在启动阶段接入 Autofac：

```csharp
Host.CreateDefaultBuilder(args)
    .UseServiceProviderFactory(new AutofacServiceProviderFactory())
    .ConfigureWebHostDefaults(webBuilder =>
    {
        webBuilder.UseStartup<Startup>();
    });
```

在 `Startup` 中注册：

```csharp
public void ConfigureContainer(ContainerBuilder builder)
{
    builder.RegisterModule(new ApplicationModule());
}
```

如果项目使用其他启动方式，也应保持同一原则：框架服务先注册，Autofac 接管服务提供者，业务依赖通过模块或统一入口注册。

## 三、模块化注册

推荐使用 `Module` 组织注册：

```csharp
public class ApplicationModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<ResourceService>()
            .As<IResourceService>()
            .InstancePerLifetimeScope();
    }
}
```

模块划分建议：

- `ApplicationModule`：业务服务注册。
- `DataModule`：数据访问注册。
- `InfrastructureModule`：基础设施注册。
- `ExternalServiceModule`：外部系统客户端注册。

模块数量不宜过多。按职责划分即可，不要每个类一个模块。

## 四、常用注册方式

接口到实现：

```csharp
builder.RegisterType<ResourceService>()
    .As<IResourceService>()
    .InstancePerLifetimeScope();
```

具体类型注册：

```csharp
builder.RegisterType<ResourceLocalService>()
    .InstancePerLifetimeScope();
```

实例注册：

```csharp
builder.RegisterInstance(settings)
    .As<ResourceSettings>()
    .SingleInstance();
```

委托注册：

```csharp
builder.Register(context =>
    {
        var settings = context.Resolve<ResourceSettings>();
        return new ResourceClient(settings.Endpoint, settings.ApiKey);
    })
    .As<IResourceClient>()
    .SingleInstance();
```

开放泛型注册：

```csharp
builder.RegisterGeneric(typeof(Repository<>))
    .As(typeof(IRepository<>))
    .InstancePerLifetimeScope();
```

## 五、生命周期

Autofac 常见生命周期：

- `InstancePerDependency()`：每次解析都创建新实例，适合轻量、无状态对象。
- `InstancePerLifetimeScope()`：同一个生命周期作用域内复用，Web 请求中通常等价于每请求一个实例。
- `SingleInstance()`：全应用单例，适合不可变配置、线程安全客户端、全局缓存协调器。

推荐使用：

- Controller：由框架创建，依赖通过构造函数注入。
- Service：通常使用 `InstancePerLifetimeScope()`。
- Repository / DbContext：通常使用 `InstancePerLifetimeScope()`。
- 配置对象：通常使用 `SingleInstance()`。
- 线程安全外部客户端：可使用 `SingleInstance()`。
- 非线程安全对象：不要注册为单例。

## 六、生命周期风险

避免以下问题：

- 单例依赖 scoped 服务。
- 单例持有 DbContext。
- 单例持有请求上下文。
- scoped 服务被后台线程长期持有。
- disposable 服务被手动 new 后没有释放。
- 在循环中频繁创建生命周期作用域。

判断规则：

- 生命周期长的对象不应依赖生命周期短的对象。
- 涉及请求、用户、事务、DbContext 的对象不要做单例。
- 不确定线程安全时，不要做单例。

## 七、简写构造函数注入

推荐写法：

```csharp
public class ResourceController(IResourceService resourceService) : ApiControllerBase
{
    public IActionResult GetResource(string id)
    {
        var result = resourceService.GetResource(id);
        return Ok(result);
    }
}
```

规则：

- 类型构造参数只接收当前类真正需要的依赖。
- 简单依赖注入直接写在类型声明中，不额外声明字段和展开构造函数。
- 只有需要额外初始化、参数校验、兼容旧语言版本或复杂构造逻辑时，才展开传统构造函数。
- 不传入容器对象。
- 不传入过多依赖。依赖过多通常说明类职责过大。
- 不在构造阶段执行耗时业务逻辑。

## 八、批量扫描注册

当项目服务较多时，可以使用程序集扫描：

```csharp
builder.RegisterAssemblyTypes(typeof(ApplicationModule).Assembly)
    .Where(type => type.Name.EndsWith("Service"))
    .AsImplementedInterfaces()
    .InstancePerLifetimeScope();
```

使用扫描注册时要谨慎：

- 命名规则必须稳定。
- 不应把不该注册的类误注册。
- 生命周期不能被粗暴统一到不合适的范围。
- 对特殊生命周期的服务应单独注册。

新项目早期可以先显式注册，服务数量增长后再引入扫描注册。

## 九、配置对象注册

配置应先绑定为强类型对象，再注册：

```csharp
var settings = configuration
    .GetSection("Resource")
    .Get<ResourceSettings>();

builder.RegisterInstance(settings)
    .As<ResourceSettings>()
    .SingleInstance();
```

规则：

- 不在业务代码中散落读取配置 key。
- 启动时校验关键配置。
- 密钥不要写入源码。
- 配置对象尽量不可变。

## 十、命名注册和 Keyed 注册

同一接口存在多个实现时，可以使用命名注册或 Keyed 注册：

```csharp
builder.RegisterType<PrimaryResourceClient>()
    .Keyed<IResourceClient>("primary")
    .SingleInstance();

builder.RegisterType<BackupResourceClient>()
    .Keyed<IResourceClient>("backup")
    .SingleInstance();
```

使用时应控制范围：

- 优先通过明确的组合服务封装选择逻辑。
- 不要让业务代码到处根据字符串解析不同实现。
- key 名称应集中定义为常量。
- 如果选择逻辑是业务规则，应放在 Service 中，而不是散落在注册代码中。

## 十一、避免 Service Locator

反例：

```csharp
public class ResourceService
{
    private readonly ILifetimeScope scope;

    public ResourceService(ILifetimeScope scope)
    {
        this.scope = scope;
    }

    public void DoWork()
    {
        var dependency = scope.Resolve<IDependency>();
    }
}
```

问题：

- 隐藏真实依赖。
- 降低可测试性。
- 容易造成生命周期错误。
- 让业务代码依赖容器。

推荐改为简写构造函数显式注入：

```csharp
public class ResourceService(IDependency dependency)
{
    public void DoWork()
    {
        dependency.Execute();
    }
}
```

## 十二、后台任务和生命周期作用域

后台任务如果需要 scoped 服务，应显式创建作用域：

```csharp
using var scope = lifetimeScope.BeginLifetimeScope();
var service = scope.Resolve<IResourceService>();
await service.ExecuteAsync();
```

规则：

- 作用域要及时释放。
- 不要把 scoped 服务保存到后台任务字段中长期使用。
- 每次任务执行创建独立作用域。
- 后台任务异常要记录日志。

## 十三、注册顺序和覆盖

Autofac 后注册的服务可能覆盖先注册的服务，具体取决于注册方式和解析方式。

规则：

- 默认注册和覆盖注册要有明确顺序。
- 测试环境替换实现应集中处理。
- 不要在多个模块里重复注册同一接口而没有说明。
- 特殊覆盖要写清楚原因。

## 十四、测试建议

测试依赖注入配置时应覆盖：

- 容器能成功构建。
- 关键 Controller 能解析。
- 关键 Service 能解析。
- DbContext、Repository 生命周期正确。
- 单例服务没有依赖 scoped 服务。
- 测试环境替换实现生效。

可以为容器注册写一个最小集成测试，尽早发现缺失注册或生命周期问题。

## 十五、新项目落地清单

新项目使用 Autofac 时，先确定：

- 注册入口在哪里。
- 是否使用 Module。
- 各层默认生命周期。
- 配置对象如何绑定和注册。
- 是否允许扫描注册。
- 多实现接口如何选择。
- 测试环境如何替换依赖。
- 是否有后台任务需要独立生命周期作用域。
- 是否有容器构建测试。
