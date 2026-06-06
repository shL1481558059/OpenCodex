# API 后端通用代码规范

本文档总结 API 后端应用的通用架构、代码风格和新增接口时应遵循的规则。

适用范围：

- HTTP API 应用
- 业务服务层
- 领域模型层
- 数据访问层
- 基础设施配置

不包含范围：

- 前端、页面模板、JavaScript、CSS 相关规则
- 认证、授权、Token、权限策略相关规则
- Factory 相关规则
- 某个具体项目、具体目录或具体业务模块的约定

## 一、项目架构

项目分层、模块边界、依赖方向、调用链、启动流程、数据访问架构和横切能力，统一维护在 [PROJECT_ARCHITECTURE.md](PROJECT_ARCHITECTURE.md)。

本文档只描述 API 后端代码层面的写法和规则；涉及架构取舍时，应优先参考项目架构文档。

## 二、项目结构

API 后端项目建议采用稳定目录和职责划分：

- `Controllers`：处理 HTTP 请求、参数默认值、结果包装。
- `DTOs`：定义 API 输入和输出数据结构。
- `Services`：仅放应用本地特有服务；通用业务服务应放入业务服务层。
- `Infrastructure`：放依赖注册、启动扩展、日志、配置、接口文档等基础设施代码。
- `Models`：仅在确实需要 API 本地模型时使用，避免和 DTO 混用。

新增功能时优先放入已有业务模块，不要随意新增横向目录或跨层目录。

## 三、基础技术约定

- 目标框架和语言版本应在项目级配置中统一。
- 是否启用 nullable、隐式 using、代码分析器，应在团队级别保持一致。
- 新增文件应显式声明所需依赖，避免依赖隐式行为。
- 应沿用现有启动方式，不因单个接口引入新的应用启动范式。
- 依赖注入通过统一容器和统一注册入口完成。
- 日志、配置、请求管道、接口文档等基础能力应通过基础设施层接入。
- 不在 Controller 中自行搭建基础设施逻辑。

## 四、命名风格

### 类型命名

- Controller 使用 `XxxController`。
- Service 接口使用 `IXxxService`。
- Service 实现使用 `XxxService`。
- Repository 接口使用 `IXxxRepository` 或统一仓储泛型。
- DTO 使用 `XxxDto`。
- 请求 DTO 可使用 `XxxRequestDto`、`XxxCreateDto`、`XxxUpdateDto`、`XxxSearchDto`。
- 响应 DTO 可使用 `XxxDto`、`XxxDetailDto`、`XxxSummaryDto`。
- 配置类可使用 `XxxSettings`。
- 默认值类可使用 `XxxDefaults`。

## 五、Controller 规则

### 基类

API Controller 应继承统一的 API Controller 基类，便于共享通用行为。

```csharp
public class ResourceController : ApiControllerBase
{
}
```

### 路由

Controller 级路由使用小写资源名，通常为复数：

```csharp
[Route("resources")]
public class ResourceController : ApiControllerBase
{
}
```

常见写法：

- 查询列表：`GET /resources`
- 查询详情：`GET /resources/{id}`
- 查询子资源：`GET /resources/{id}/items`
- 创建资源：`POST /resources`
- 更新资源：`PUT /resources/{id}` 或 `PATCH /resources/{id}`
- 删除资源：`DELETE /resources/{id}`
- 执行业务动作：`POST /resources/{id}/actions/{actionName}`

路由中已有业务习惯时优先保持兼容，不为了风格统一随意重命名已发布接口。

### 方法命名

Action 方法使用明确业务语义：

- `GetResources`
- `GetResourceById`
- `CreateResource`
- `UpdateResource`
- `DeleteResource`
- `ExecuteResourceAction`

不要使用过于泛化的方法名，例如 `Get`、`Post`、`Handle`，除非目标文件已有一致写法。

### Controller 职责

Controller 只做薄层编排：

- 接收请求参数或 DTO。
- 设置分页、排序等默认值。
- 做必要的轻量输入检查。
- 调用 Service。
- 将 Service 结果包装成 API 返回结构。

Controller 不应承担：

- 复杂业务规则。
- 复杂查询拼装。
- 跨表数据组装。
- 事务控制。
- 直接持久化细节。
- 基础设施初始化。

## 六、请求参数和 DTO

### GET 参数

简单查询参数可以直接放在 Action 参数中：

```csharp
public IActionResult GetResources(string keyword, int? page, int? pageSize)
```

可选参数使用 nullable 类型或默认值：

```csharp
int? page
int? pageSize
string status = ""
DateTime? startDate = null
DateTime? endDate = null
```

复杂查询条件建议使用查询 DTO，但要保持绑定方式清晰。

### POST / PUT / PATCH 参数

复杂提交使用 DTO：

```csharp
public IActionResult CreateResource(ResourceCreateDto dto)
```

DTO 命名应体现使用场景：

- 查询：`XxxSearchDto`
- 创建：`XxxCreateDto`
- 更新：`XxxUpdateDto`
- 保存：`XxxSaveDto`
- 请求：`XxxRequestDto`
- 输出：`XxxDto`

DTO 不应包含数据库实体的全部字段，应该按接口场景定义。

## 七、分页规则

分页参数建议统一：

```csharp
int? page, int? pageSize
```

默认值由统一配置提供：

```csharp
var currentPage = page ?? 1;
var currentPageSize = pageSize ?? defaultPageSize;
```

规则：

- Controller 负责补齐默认分页参数。
- Service 接收明确的分页参数。
- 页码默认从 1 开始，除非全项目明确采用从 0 开始。
- 分页大小应有默认值和最大值限制。
- 新增分页接口应沿用现有分页命名和默认值规则。

## 八、返回结果规则

API 返回应使用统一结果包装对象。

原则：

- 业务成功和业务失败都优先通过统一 API 结果表达。
- 不要直接返回裸 DTO，除非目标模块已有明确先例。
- 不要在多个接口中发明新的返回包装类型。
- 不要把业务错误混入不稳定的匿名对象。
- Controller 只负责把业务结果转换为 API 结果，不在转换过程中补写业务规则。
- 结果对象的具体定义和示例统一维护在 [API_RESULT_GUIDE.md](API_RESULT_GUIDE.md)。

## 九、Service 和 Repository 职责

Controller 不直接访问 Repository。

推荐调用链：

```text
Controller -> Service -> Repository / DbContext
```

Service 负责：

- 业务校验。
- 业务流程编排。
- 查询和持久化协调。
- 事务边界。
- 返回业务结果对象或业务 DTO。
- 处理本业务域内的组合逻辑。

Repository 负责：

- 基础数据访问。
- 查询入口。
- 新增、更新、删除等基础持久化能力。
- 隔离数据库访问细节。

新增 API 时，如果需要新业务逻辑，应优先放在对应业务 Service 中。只有 API 应用特有、不可复用的任务才放在 API 应用本地服务中。

## 十、依赖注入

普通服务注册应集中在统一依赖注册入口。

新增服务时：

- 放到对应业务分组附近。
- 生命周期与已有同类服务保持一致。
- 优先注册接口到实现。
- 不要在 Controller 中手动 new Service。
- 不要在业务方法内部临时构造依赖图。
- 不要在业务代码中直接持有容器或临时解析服务。

如果新项目使用 Autofac，注册方式、生命周期、模块化注册和常见风险见 [AUTOFAC_GUIDE.md](AUTOFAC_GUIDE.md)。

## 十一、启动和基础设施

API 应用应沿用统一启动流程：

- 注册框架服务。
- 注册业务依赖。
- 注册基础设施服务。
- 配置请求管道。
- 启动应用。

新增基础设施配置时应优先通过基础设施层的启动类或扩展方法接入，避免把大量配置堆到 Controller 或业务 Service 中。

接口文档、日志、缓存、对象映射、数据上下文等非业务配置应集中组织，不要散落在业务代码中。

## 十二、参数校验

参数校验应分层处理：

- Controller 只做轻量校验，例如必填参数、简单格式、分页默认值。
- DTO 校验可使用数据注解或独立校验器。
- 涉及业务规则、数据库状态、权限边界以外的业务约束，应放在 Service。
- 多字段组合校验应明确放在 Service 或专门校验组件。
- 校验失败应返回统一业务结果，不要返回随意拼装的匿名对象。

新增接口至少考虑：

- 必填参数为空。
- 字符串为空白。
- 数字越界。
- 日期范围非法。
- 分页参数非法。
- 枚举值不在允许范围内。

## 十三、异常处理和日志

异常处理应有统一入口：

- 可预期业务失败使用业务结果对象表达。
- 不可预期错误交给全局异常处理。
- Controller 不应到处写重复 try/catch。
- 不应吞掉异常后返回看似成功的结果。

日志规则：

- 记录关键业务动作、关键状态变化和外部系统调用。
- 异常日志应包含 traceId 或 requestId，便于排查。
- 不记录密码、Token、密钥、身份证号、完整手机号等敏感信息。
- Controller 日志保持克制，复杂业务日志优先放在 Service。
- 重试、补偿、异步任务应记录开始、结束和失败原因。

## 十四、配置和环境

配置应集中绑定和注入：

- 不在业务代码中散落硬编码配置 key。
- 不把密钥、连接串、证书内容写入源码。
- 配置对象使用清晰命名，例如 `XxxSettings`。
- 不同环境的配置应通过环境变量、配置文件或密钥管理系统区分。
- 启动时应校验关键配置是否存在，避免运行中才暴露缺失。

默认值要明确来源，避免在多个接口里重复写魔法数字。

## 十五、事务和一致性

涉及多个写操作时，必须明确事务边界：

- 单一 Service 方法通常是业务事务边界。
- Controller 不控制事务。
- 跨多个 Repository 或 DbContext 写入时，应使用统一事务机制。
- 外部系统调用和数据库写入混合时，要考虑失败补偿。
- 写操作应考虑重复提交、幂等键或业务唯一约束。

不要为了省事把事务逻辑散落在多个 Controller Action 中。

## 十六、异步和性能

涉及 IO 的操作优先使用异步：

- 数据库查询。
- HTTP 调用。
- 文件读写。
- 消息发送。
- 缓存访问。

规则：

- 避免 `.Result` 和 `.Wait()`。
- 异步方法使用 `Async` 后缀。
- 查询接口优先限制分页大小。
- 列表接口不要返回不必要的大对象。
- 只读查询优先使用无跟踪查询。
- 避免在循环中逐条访问数据库造成 N+1 查询。

## 十七、接口兼容和文档

已发布接口应保持兼容：

- 不随意修改路由。
- 不随意删除响应字段。
- 不随意改变字段语义。
- 不把可选字段改成必填字段。
- 需要破坏兼容时，应通过版本化或新接口处理。

接口文档应覆盖：

- 请求方法和路由。
- 请求参数和示例。
- 响应结构和示例。
- 错误码。
- 分页规则。
- 幂等规则。
- 重要字段说明。

## 十八、测试规则

新增 API 应至少考虑最小测试集：

- 成功路径。
- 业务失败路径。
- 参数为空或非法。
- 分页默认值。
- 边界值。
- 重复提交。
- Service 关键业务规则。
- Repository 或数据访问的关键查询。

测试分层建议：

- Controller 可做轻量集成测试。
- Service 做业务单元测试或集成测试。
- Repository 做数据库集成测试。
- 外部系统调用使用 mock、stub 或测试替身。

## 十九、幂等和重复提交

写接口需要判断是否存在重复提交风险：

- 创建订单、支付、退款、发货等业务动作必须考虑幂等。
- 客户端重试、网络超时、消息重复投递都可能造成重复写入。
- 可使用幂等键、业务唯一索引、请求流水号或状态机防重。
- 幂等接口重复调用时，应返回稳定结果，而不是制造新的业务副作用。

## 二十、代码格式习惯

- 命名空间风格应跟随同目录同类型文件。
- 类内部可以使用统一的区域分块，但不要滥用。
- 依赖注入优先使用主构造函数或简写构造函数，避免为简单赋值展开传统构造函数。
- 只有需要额外初始化、参数校验、兼容旧语言版本或复杂构造逻辑时，才展开传统构造函数。
- 简单 `if` 可以省略大括号，但复杂逻辑建议使用大括号提高可读性。
- `using` 顺序建议为系统命名空间、第三方库、项目命名空间。
- 新增代码应优先贴合当前模块已有风格，不做无关格式化。
- 不因局部改动重排大段无关代码。

## 二十一、新增 API 推荐流程

1. 确认接口属于哪个 API 应用和业务资源。
2. 明确接口解决的核心业务问题。
3. 定义或复用请求 DTO 和响应 DTO。
4. 在 Service 中实现业务逻辑，返回业务结果对象或明确 DTO。
5. Controller 设置默认参数并调用 Service。
6. Controller 使用统一 API 结果包装返回。
7. 如新增 Service，在统一依赖注册入口注册。
8. 补充接口文档和错误码说明。
9. 补充最小验证方式：至少覆盖成功、业务失败、分页默认值和关键参数为空的情况。
10. 检查是否存在幂等、事务、日志、配置和兼容性风险。

## 二十二、注意事项

- 如果项目关闭 nullable，新代码要主动处理 null 输入和空集合。
- 不要把可预期业务错误简单转换为异常；优先使用业务结果对象表达。
- 不要为了局部接口引入新的响应格式。
- 不要在 API 层绕过已有 Service 直接写数据库查询。
- 不要跨层随意移动 DTO、Service 或实体，除非明确需要调整公共边界。
- 修改已有接口时要谨慎，路由和返回结构可能已被客户端依赖。
- 新增规则应能跨多个未来任务复用，不应只服务单次改动。
