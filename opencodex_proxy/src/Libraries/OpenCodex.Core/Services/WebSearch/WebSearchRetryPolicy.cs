using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.WebSearch;

internal static class ToolContractRetryPolicy
{
    public const string RetryExhaustedErrorCode = "upstream_tool_contract_error";

    public static bool IsUndeclaredToolCall(UpstreamException exception)
    {
        return exception.ErrorCode == ProtocolConverter.UndeclaredToolCallErrorCode;
    }

    public static UpstreamException RetryExhausted(UpstreamException lastFailure)
    {
        return new UpstreamException(
            "The upstream model repeatedly returned an undeclared tool name.",
            body: lastFailure.Body,
            errorCode: RetryExhaustedErrorCode);
    }

    public static Dictionary<string, object?> BuildRetryRequest(
        IReadOnlyDictionary<string, object?> request,
        string channelType,
        IEnumerable<string> declaredToolNames,
        string? webSearchToolName = null)
    {
        var result = DeepCopyObject(request);
        var names = declaredToolNames
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name => $"`{name}`")
            .ToList();
        var webSearchContract = string.IsNullOrEmpty(webSearchToolName)
            ? string.Empty
            : $"\nFor the proxy web search tool, use the exact name `{webSearchToolName}`.";
        var instruction = $"""
            Tool call contract:
            Call only tools explicitly declared in this request.
            Use each tool name exactly as declared. Never rename, translate, abbreviate, merge, or invent tool names.
            Nested method names found in tool descriptions are not top-level tools.{webSearchContract}
            Allowed tool names: {string.Join(", ", names)}
            """;

        if (channelType == ProtocolConverter.Messages)
        {
            AppendMessagesSystemInstruction(result, instruction);
        }
        else
        {
            AppendChatSystemInstruction(result, instruction);
        }

        return result;
    }

    private static void AppendChatSystemInstruction(
        Dictionary<string, object?> request,
        string instruction)
    {
        var messages = ListValue(request, "messages");
        var system = messages
            .OfType<Dictionary<string, object?>>()
            .FirstOrDefault(message => StringValue(message, "role") == "system");
        if (system is null)
        {
            messages.Insert(0, new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = instruction
            });
            request["messages"] = messages;
            return;
        }

        system["content"] = AppendTextContent(GetValue(system, "content"), instruction);
    }

    private static void AppendMessagesSystemInstruction(
        Dictionary<string, object?> request,
        string instruction)
    {
        request["system"] = AppendTextContent(GetValue(request, "system"), instruction);
    }

    private static object AppendTextContent(object? current, string instruction)
    {
        if (current is null)
        {
            return instruction;
        }

        if (current is string text)
        {
            return text.Length == 0 ? instruction : $"{text}\n\n{instruction}";
        }

        if (TryAsList(current, out var blocks))
        {
            var copy = blocks.Select(DeepCopy).ToList();
            copy.Add(new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = instruction
            });
            return copy;
        }

        return instruction;
    }
}
