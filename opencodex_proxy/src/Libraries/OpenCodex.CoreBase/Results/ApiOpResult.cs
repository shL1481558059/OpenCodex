using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.Results;

/// <summary>
/// 表示一次接口调用的操作结果。
/// </summary>
[Serializable]
public class ApiOpResult
{
    /// <summary>
    /// 初始化 <see cref="ApiOpResult"/> 类的新实例。
    /// </summary>
    public ApiOpResult()
    {
    }

    /// <summary>
    /// 使用指定错误码和说明初始化 <see cref="ApiOpResult"/> 类的新实例。
    /// </summary>
    /// <param name="code">操作结果码。</param>
    /// <param name="description">操作结果说明。</param>
    public ApiOpResult(int code, string? description)
    {
        Code = code;
        Description = description;
    }

    /// <summary>
    /// 将当前操作结果转换为等效的字符串表示形式。
    /// </summary>
    /// <returns>当前操作结果的字符串表示形式。</returns>
    public override string ToString()
    {
        return Succeeded
            ? $"Succeeded {Description}"
            : $"Failed: {Code} {Description}";
    }

    /// <summary>
    /// 创建表示成功的操作结果。
    /// </summary>
    /// <param name="description">操作结果说明。</param>
    /// <returns>表示成功的操作结果。</returns>
    public static ApiOpResult Succeed(string? description = null)
    {
        return new ApiOpResult(0, description);
    }

    /// <summary>
    /// 创建表示失败的操作结果。
    /// </summary>
    /// <param name="code">操作结果码。</param>
    /// <param name="description">操作结果说明。</param>
    /// <returns>表示失败的操作结果。</returns>
    public static ApiOpResult Fail(int code, string? description)
    {
        return new ApiOpResult(code, description);
    }

    /// <summary>
    /// 获取或设置操作结果码。
    /// </summary>
    [JsonPropertyName("ErrorCode")]
    public int Code { get; set; }

    /// <summary>
    /// 获取或设置操作结果说明。
    /// </summary>
    [JsonPropertyName("ErrorMsg")]
    public string? Description { get; set; }

    /// <summary>
    /// 获取一个值，该值指示操作是否成功。
    /// </summary>
    public bool Succeeded => Code == 0;
}

/// <summary>
/// 表示包含数据载荷的接口调用操作结果。
/// </summary>
/// <typeparam name="TPayload">数据载荷的类型。</typeparam>
[Serializable]
public class ApiOpResult<TPayload> : ApiOpResult
{
    /// <summary>
    /// 初始化 <see cref="ApiOpResult{TPayload}"/> 类的新实例。
    /// </summary>
    public ApiOpResult()
    {
    }

    /// <summary>
    /// 使用指定错误码、说明和数据载荷初始化 <see cref="ApiOpResult{TPayload}"/> 类的新实例。
    /// </summary>
    /// <param name="code">操作结果码。</param>
    /// <param name="description">操作结果说明。</param>
    /// <param name="payload">操作结果的数据载荷。</param>
    public ApiOpResult(int code, string? description, TPayload? payload)
        : base(code, description)
    {
        Payload = payload;
    }

    /// <summary>
    /// 创建表示成功的操作结果。
    /// </summary>
    /// <param name="payload">操作结果的数据载荷。</param>
    /// <returns>表示成功的操作结果。</returns>
    public static ApiOpResult<TPayload> Succeed(TPayload? payload = default)
    {
        return Succeed(null, payload);
    }

    /// <summary>
    /// 创建表示成功的操作结果。
    /// </summary>
    /// <param name="description">操作结果说明。</param>
    /// <param name="payload">操作结果的数据载荷。</param>
    /// <returns>表示成功的操作结果。</returns>
    public static ApiOpResult<TPayload> Succeed(string? description, TPayload? payload = default)
    {
        return new ApiOpResult<TPayload>(0, description, payload);
    }

    /// <summary>
    /// 创建表示失败的操作结果。
    /// </summary>
    /// <param name="code">操作结果码。</param>
    /// <param name="description">操作结果说明。</param>
    /// <param name="payload">操作结果的数据载荷。</param>
    /// <returns>表示失败的操作结果。</returns>
    public static ApiOpResult<TPayload> Fail(
        int code,
        string? description,
        TPayload? payload = default)
    {
        return new ApiOpResult<TPayload>(code, description, payload);
    }

    /// <summary>
    /// 获取或设置操作结果的数据载荷。
    /// </summary>
    [JsonPropertyName("Data")]
    public TPayload? Payload { get; set; }
}
