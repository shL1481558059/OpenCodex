namespace OpenCodex.CoreBase.Abstractions;

public interface IWebSearchClient
{
    Task<WebSearchProviderResult> SearchAsync(
        WebSearchProviderKey key,
        string query,
        CancellationToken cancellationToken);
}

public sealed class WebSearchProviderKey
{
    public WebSearchProviderKey(string provider, string key)
    {
        Provider = provider;
        Key = key;
    }

    public string Provider { get; }

    public string Key { get; }
}

public sealed class WebSearchProviderResult
{
    public WebSearchProviderResult(
        bool ok,
        int? statusCode,
        int durationMs,
        string? errorType,
        string? error,
        WebSearchSummary summary,
        object? raw)
    {
        Ok = ok;
        StatusCode = statusCode;
        DurationMs = durationMs;
        ErrorType = errorType;
        Error = error;
        Summary = summary;
        Raw = raw;
    }

    public bool Ok { get; }

    public int? StatusCode { get; }

    public int DurationMs { get; }

    public string? ErrorType { get; }

    public string? Error { get; }

    public WebSearchSummary Summary { get; }

    public object? Raw { get; }
}

public sealed class WebSearchSummary
{
    public WebSearchSummary(
        string answer,
        IReadOnlyList<Dictionary<string, object?>> results,
        string? error)
    {
        Answer = answer;
        Results = results;
        Error = error;
    }

    public string Answer { get; }

    public IReadOnlyList<Dictionary<string, object?>> Results { get; }

    public string? Error { get; }
}
