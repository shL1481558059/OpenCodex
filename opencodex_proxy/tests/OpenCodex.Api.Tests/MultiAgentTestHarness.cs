using System.Text.Json;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Api.Tests;

internal static class MultiAgentTestHarness
{
    public static Dictionary<string, object?> Message(string text, string role = "assistant") => new()
    { ["type"] = "message", ["role"] = role, ["content"] = new List<object?> { new Dictionary<string, object?> { ["type"] = role == "assistant" ? "output_text" : "input_text", ["text"] = text } } };
    public static Dictionary<string, object?> Call(string name, object arguments) => new()
    { ["type"] = "function_call", ["call_id"] = "upstream-id", ["name"] = name, ["arguments"] = JsonSerializer.Serialize(arguments) };
    public static Dictionary<string, object?> Response(params object?[] items) => new()
    { ["status"] = "completed", ["output"] = items.ToList(), ["usage"] = new Dictionary<string, object?> { ["input_tokens"] = 10, ["output_tokens"] = 2 } };
    public static MultiAgentRun Run() => new()
    {
        Model = "test", Template = MultiAgentProtocol.NormalizeRequest(new() { ["model"] = "test" }),
        Agents = new() { ["/root"] = new() { Name = "/root", History = [Message("task", "user")] } }
    };
    public static string Agent(Dictionary<string, object?> payload) => JsonDictionaryValue.String(payload, "prompt_cache_key").Split(':')[1];
    public static string History(Dictionary<string, object?> payload) => JsonSerializer.Serialize(payload["input"]);
    public static async Task<List<Dictionary<string, object?>>> Execute(MultiAgentRun run,
        Func<Dictionary<string, object?>, CancellationToken, Task<Dictionary<string, object?>>> model,
        Dictionary<string, object?>? request = null)
    {
        var events = new List<Dictionary<string, object?>>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runtime = new MultiAgentRuntime(run, model, e => { events.Add(e); return Task.CompletedTask; }, () => Task.CompletedTask, 40);
        await runtime.ExecuteAsync(request ?? new(), deadline.Token);
        return events;
    }
}
