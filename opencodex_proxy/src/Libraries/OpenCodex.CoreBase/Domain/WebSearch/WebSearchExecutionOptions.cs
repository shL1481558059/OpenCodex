namespace OpenCodex.CoreBase.Domain.WebSearch;

/// <summary>
/// 代理执行原生 web_search 时要落实的选项。缺省等价于 medium 且不限制域名。
/// </summary>
public sealed class WebSearchExecutionOptions
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const int MaxAllowedDomains = 100;

    public static WebSearchExecutionOptions Default { get; } = new(Medium, []);

    public WebSearchExecutionOptions(string contextSize, IReadOnlyList<string> allowedDomains)
    {
        ContextSize = contextSize;
        AllowedDomains = allowedDomains;
    }

    public string ContextSize { get; }

    public IReadOnlyList<string> AllowedDomains { get; }
}

/// <summary>
/// 把 search_context_size 映射到各提供方的请求参数。medium 必须与改造前的请求体一致。
/// </summary>
public readonly record struct WebSearchProviderProfile(
    int TavilyMaxResults,
    string TavilySearchDepth,
    int? TavilyChunksPerSource,
    int KeenableMaxResults,
    int? KeenableSnippetMaxLength,
    int ResultLimit)
{
    public const int KeenableDomainConcurrency = 4;

    public static WebSearchProviderProfile For(string? contextSize) => contextSize switch
    {
        WebSearchExecutionOptions.Low => new(3, "basic", 1, 3, 500, 3),
        WebSearchExecutionOptions.High => new(8, "advanced", 3, 10, 2000, 10),
        _ => new(5, "basic", null, 5, null, 5)
    };
}
