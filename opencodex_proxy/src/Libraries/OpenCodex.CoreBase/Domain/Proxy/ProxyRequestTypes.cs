namespace OpenCodex.CoreBase.Domain.Proxy;

/// <summary>
/// 定义代理请求日志类型。
/// </summary>
public static class ProxyRequestTypes
{
    public const string Main = "main";

    public const string Ocr = "ocr";

    public const string Attempt = "attempt";

    /// <summary>
    /// 管理台渠道诊断（测试渠道/发现模型）产生的日志，不计入业务统计。
    /// </summary>
    public const string Diagnostic = "diagnostic";
}
