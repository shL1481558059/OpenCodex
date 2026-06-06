# API 结果对象设计指南

本文档定义 API 后端中业务结果对象和 HTTP 响应结果对象的推荐设计。目标是让新项目从一开始就具备稳定、可读、可测试的返回结构。

## 一、设计目标

统一结果对象要解决几个问题：

- 让成功和失败响应结构稳定。
- 区分业务失败和系统异常。
- 让错误码、错误说明、分页、追踪信息有固定位置。
- 避免 Controller 返回匿名对象或裸 DTO。
- 方便客户端、接口文档、日志和测试统一处理。

## 二、职责边界

推荐区分两个层级的结果对象：

- `ServiceResult`：业务服务层使用，表达业务成功、业务失败、错误码、错误说明和业务数据。
- `ApiResult`：API 层使用，表达最终 HTTP 响应体。

调用关系：

```text
Service 返回 ServiceResult
Controller 将 ServiceResult 转换为 ApiResult
ApiResult 作为 HTTP 响应体返回
```

规则：

- Service 不依赖 HTTP 响应对象。
- Controller 不补写业务规则，只做结果转换。
- 可预期业务失败使用 `ServiceResult` 表达。
- 不可预期错误交给全局异常处理转换为 `ApiResult`。

## 三、ApiResult 推荐字段

推荐响应结构：

```json
{
  "succeeded": true,
  "code": 0,
  "message": "OK",
  "data": {},
  "errors": [],
  "traceId": "request-trace-id"
}
```

字段说明：

- `succeeded`：是否成功。
- `code`：业务错误码。成功建议为 `0` 或团队约定的成功码。
- `message`：面向调用方的错误说明或成功说明。
- `data`：业务数据。无数据时可为 `null`。
- `errors`：详细错误列表，常用于参数校验失败。
- `traceId`：请求追踪标识，便于日志排查。

字段命名应在项目开始时确定。不要在不同接口中混用 `success`、`succeeded`、`isSuccess` 等多个名称。

## 四、ServiceResult 推荐字段

推荐业务结果对象包含：

```csharp
public class ServiceResult
{
    public bool Succeeded { get; init; }
    public int Code { get; init; }
    public string Message { get; init; }
    public IReadOnlyList<ErrorItem> Errors { get; init; } = Array.Empty<ErrorItem>();
}

public class ServiceResult<T> : ServiceResult
{
    public T Data { get; init; }
}
```

业务层可以使用 `Message` 或 `Description`，但同一项目内要统一。

推荐提供静态创建方法：

```csharp
public static ServiceResult Success(string message = "OK")
{
    return new ServiceResult
    {
        Succeeded = true,
        Code = 0,
        Message = message
    };
}

public static ServiceResult<T> Success<T>(T data, string message = "OK")
{
    return new ServiceResult<T>
    {
        Succeeded = true,
        Code = 0,
        Message = message,
        Data = data
    };
}

public static ServiceResult Fail(int code, string message, IReadOnlyList<ErrorItem> errors = null)
{
    return new ServiceResult
    {
        Succeeded = false,
        Code = code,
        Message = message,
        Errors = errors ?? Array.Empty<ErrorItem>()
    };
}
```

## 五、ApiResult 推荐实现

推荐 API 结果对象：

```csharp
public class ApiResult
{
    public bool Succeeded { get; init; }
    public int Code { get; init; }
    public string Message { get; init; }
    public IReadOnlyList<ErrorItem> Errors { get; init; } = Array.Empty<ErrorItem>();
    public string TraceId { get; init; }
}

public class ApiResult<T> : ApiResult
{
    public T Data { get; init; }
}
```

推荐创建方法：

```csharp
public static ApiResult Success(string message = "OK", string traceId = null)
{
    return new ApiResult
    {
        Succeeded = true,
        Code = 0,
        Message = message,
        TraceId = traceId
    };
}

public static ApiResult<T> Success<T>(T data, string message = "OK", string traceId = null)
{
    return new ApiResult<T>
    {
        Succeeded = true,
        Code = 0,
        Message = message,
        Data = data,
        TraceId = traceId
    };
}

public static ApiResult Fail(int code, string message, IReadOnlyList<ErrorItem> errors = null, string traceId = null)
{
    return new ApiResult
    {
        Succeeded = false,
        Code = code,
        Message = message,
        Errors = errors ?? Array.Empty<ErrorItem>(),
        TraceId = traceId
    };
}
```

## 六、ErrorItem 推荐字段

参数校验或复杂业务错误可使用错误明细：

```csharp
public class ErrorItem
{
    public string Field { get; init; }
    public int Code { get; init; }
    public string Message { get; init; }
}
```

示例：

```json
{
  "succeeded": false,
  "code": 400001,
  "message": "Validation failed.",
  "errors": [
    {
      "field": "pageSize",
      "code": 400101,
      "message": "pageSize must be between 1 and 100."
    }
  ],
  "traceId": "request-trace-id"
}
```

## 七、分页结果

分页数据不要把分页字段散落在不同接口中。推荐统一分页对象：

```csharp
public class PageResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public long TotalCount { get; init; }
    public int TotalPages { get; init; }
}
```

分页响应：

```json
{
  "succeeded": true,
  "code": 0,
  "message": "OK",
  "data": {
    "items": [],
    "page": 1,
    "pageSize": 20,
    "totalCount": 0,
    "totalPages": 0
  },
  "errors": [],
  "traceId": "request-trace-id"
}
```

规则：

- 列表数据放在 `items`。
- 当前页、页大小、总数、总页数使用固定字段。
- 页码从 1 还是 0 开始，应在项目初始阶段确定。
- 分页大小应有默认值和最大值限制。

## 八、错误码规则

错误码要可分类、可排查、可稳定复用。

推荐分段：

- `0`：成功。
- `400xxx`：请求参数或校验错误。
- `401xxx`：认证相关错误。如果项目另有认证文档，可在认证文档中维护。
- `403xxx`：访问控制相关错误。如果项目另有授权文档，可在授权文档中维护。
- `404xxx`：资源不存在。
- `409xxx`：状态冲突、重复提交、幂等冲突。
- `500xxx`：系统错误或基础设施错误。
- 业务域可使用独立区间，例如 `100001` 到 `199999`。

规则：

- 错误码一旦对外发布，应保持稳定。
- 不同业务错误不要共用一个模糊错误码。
- 错误码说明应出现在接口文档中。
- 不要把数据库错误、异常类型名直接暴露给调用方。

## 九、HTTP 状态码和业务结果

推荐原则：

- HTTP 状态码表达 HTTP 层语义。
- `ApiResult.code` 表达业务语义。
- 可预期业务失败可以返回 `200` 加业务失败结果，也可以使用对应 HTTP 状态码；关键是项目内统一。
- 参数绑定失败、资源不存在、系统异常等 HTTP 语义明确的场景，可以使用对应状态码。

推荐关系：

- `200 OK`：请求被正常处理，业务成功或可预期业务失败。
- `400 Bad Request`：请求格式错误、参数无法绑定、明显非法输入。
- `404 Not Found`：资源不存在，且项目决定用 HTTP 状态码表达该语义。
- `409 Conflict`：重复提交、状态冲突、幂等冲突。
- `500 Internal Server Error`：不可预期系统错误。

团队应在项目初期确定业务失败是否统一返回 `200`。一旦确定，不要在不同接口之间混用。

## 十、Controller 转换示例

```csharp
public IActionResult GetResource(string id)
{
    var serviceResult = resourceService.GetResource(id);

    if (serviceResult.Succeeded)
    {
        return Ok(ApiResult.Success(serviceResult.Data, traceId: GetTraceId()));
    }

    return Ok(ApiResult.Fail(
        serviceResult.Code,
        serviceResult.Message,
        serviceResult.Errors,
        GetTraceId()));
}
```

转换规则：

- 不在 Controller 中重新解释错误码。
- 不在 Controller 中修改业务数据。
- 不在 Controller 中吞掉失败结果。
- 统一追加 `traceId`。

## 十一、全局异常响应

不可预期异常由全局异常处理转换：

```json
{
  "succeeded": false,
  "code": 500000,
  "message": "An unexpected error occurred.",
  "data": null,
  "errors": [],
  "traceId": "request-trace-id"
}
```

规则：

- 对外隐藏异常堆栈。
- 日志中记录完整异常。
- 响应中返回稳定错误码和 traceId。
- 开发环境可以显示更多调试信息，但不要影响生产环境。

## 十二、常见反例

避免这些写法：

- 成功接口返回 DTO，失败接口返回字符串。
- 某些接口返回 `success`，某些接口返回 `succeeded`。
- 某些接口把错误放 `message`，某些接口把错误放 `data`。
- 直接返回匿名对象。
- 把数据库异常消息直接返回给调用方。
- 每个 Controller 自己定义一套结果格式。
- 分页接口有的叫 `rows`，有的叫 `items`，有的叫 `list`。

## 十三、新项目落地清单

新项目启动时应先确定：

- 成功字段名称。
- 成功码。
- 错误码分段规则。
- 业务失败是否返回 `200`。
- 追踪字段名称。
- 分页字段名称。
- 参数校验错误格式。
- 全局异常响应格式。
- 接口文档中的响应示例。
