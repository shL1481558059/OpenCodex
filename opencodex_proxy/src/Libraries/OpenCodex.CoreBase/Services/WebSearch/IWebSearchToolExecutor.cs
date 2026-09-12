using OpenCodex.CoreBase.Domain.WebSearch;

namespace OpenCodex.CoreBase.Services.WebSearch;

public interface IWebSearchToolExecutor
{
    string CurrentMode();

    Task<WebSearchToolResult> ExecuteAsync(
        string callId,
        string arguments,
        CancellationToken cancellationToken);
}
