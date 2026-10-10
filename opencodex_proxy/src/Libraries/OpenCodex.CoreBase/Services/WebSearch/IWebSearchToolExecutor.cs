using OpenCodex.CoreBase.Domain.WebSearch;

namespace OpenCodex.CoreBase.Services.WebSearch;

public interface IWebSearchToolExecutor
{
    string CurrentMode();

    Task<WebSearchToolResult> ExecuteAsync(
        string callId,
        string arguments,
        CancellationToken cancellationToken);

    /// <summary>
    /// 按请求里声明的搜索选项执行。未重写该重载的实现忽略选项。
    /// </summary>
    Task<WebSearchToolResult> ExecuteAsync(
        string callId,
        string arguments,
        WebSearchExecutionOptions options,
        CancellationToken cancellationToken)
        => ExecuteAsync(callId, arguments, cancellationToken);
}
