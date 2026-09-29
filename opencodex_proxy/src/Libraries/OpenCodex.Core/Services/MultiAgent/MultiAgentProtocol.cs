using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services.MultiAgent;

public static class MultiAgentProtocol
{
    private const string Prefix = "ocxp_ma_";
    private static readonly HashSet<string> Actions =
        ["spawn_agent", "send_message", "followup_task", "wait_agent", "interrupt_agent", "list_agents"];

    public const string RootInstructions = """
        Multi-agent orchestration is provided by the OpenCodex server. Use only the ocxp_ma_ tools
        to spawn agents, send messages, assign follow-up tasks, wait, interrupt, or list agents.
        Do not call client-side collaboration tools. Each agent has its own conversation and mailbox.
        Agent paths are hierarchical, starting at /root. Delegate concrete independent tasks, continue
        useful work, and use ocxp_ma_wait_agent when awaiting results. The root agent synthesizes actual
        completed results into the final answer. A normal final answer ends the current agent's task.
        """;

    public static Dictionary<string, object?> NormalizeRequest(Dictionary<string, object?> request)
    {
        var result = WebSearchPayload.DeepCopyObject(request);
        result["tools"] = MergeClientTools([], JsonDictionaryValue.List(result, "tools")
            .Concat(AdditionalTools(result))).Concat(CreateTools()).ToList();
        foreach (var field in new[] { "input", "multi_agent", "previous_response_id", "conversation", "context_management" })
            result.Remove(field);
        return result;
    }

    /// <summary>
    /// Apply explicit continuation updates. Omitted fields inherit the previous template;
    /// null clears an optional parameter, and an explicit tools array replaces client tools.
    /// In-flight model calls retain their independently copied template.
    /// </summary>
    public static Dictionary<string, object?> ApplyUpdates(
        Dictionary<string, object?> currentTemplate, Dictionary<string, object?> incoming)
    {
        var result = WebSearchPayload.DeepCopyObject(currentTemplate);
        foreach (var field in new[]
                 {
                     "instructions", "reasoning", "text", "tool_choice", "parallel_tool_calls",
                     "temperature", "top_p", "max_output_tokens", "truncation", "service_tier",
                     "metadata", "user", "safety_identifier", "prompt_cache_key", "prompt_cache_retention"
                 })
        {
            if (!incoming.TryGetValue(field, out var value)) continue;
            if (value is null) result.Remove(field);
            else result[field] = WebSearchPayload.DeepCopy(value);
        }

        var initial = incoming.ContainsKey("tools")
            ? JsonDictionaryValue.List(incoming, "tools")
            : JsonDictionaryValue.List(currentTemplate, "tools")
                .Where(tool => tool is not IReadOnlyDictionary<string, object?> definition
                    || ActionName(JsonDictionaryValue.String(definition, "name")) is null);
        result["tools"] = MergeClientTools([], initial.Concat(AdditionalTools(incoming)))
            .Concat(CreateTools()).ToList();
        return result;
    }

    private static IEnumerable<object?> AdditionalTools(IReadOnlyDictionary<string, object?> request) =>
        JsonDictionaryValue.List(request, "input")
            .OfType<IReadOnlyDictionary<string, object?>>()
            .SelectMany(item => JsonDictionaryValue.String(item, "type") == "additional_tools"
                ? JsonDictionaryValue.List(item, "tools")
                : JsonDictionaryValue.List(item, "additional_tools"));

    private static List<object?> MergeClientTools(IEnumerable<object?> existing, IEnumerable<object?> candidates)
    {
        var tools = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var candidate in existing.Concat(candidates))
        {
            if (candidate is not IReadOnlyDictionary<string, object?> tool)
                throw new BadRequestException("Multi-agent tools must be objects.");
            var name = JsonDictionaryValue.String(tool, "name");
            var type = JsonDictionaryValue.String(tool, "type");
            if (name == "collaboration" || IsClientAction(name) || type is "multi_agent" or "collaboration")
                continue;
            if (name.StartsWith(Prefix, StringComparison.Ordinal))
                throw new BadRequestException("Tool names beginning with ocxp_ma_ are reserved for server multi-agent orchestration.");

            // Named tools share an identity within their namespace even if their definition type changes.
            var key = name.Length > 0 ? name : type;
            var copy = WebSearchPayload.DeepCopyObject(tool);
            if (type == "namespace")
            {
                var previous = tools.TryGetValue(key, out var value)
                    && JsonDictionaryValue.String(value, "type") == "namespace"
                    ? JsonDictionaryValue.List(value, "tools") : [];
                copy["tools"] = MergeClientTools(previous, JsonDictionaryValue.List(tool, "tools"));
            }
            tools[key] = copy;
        }
        return tools.Values.Cast<object?>().ToList();
    }

    public static List<object?> InitialHistory(Dictionary<string, object?> request)
    {
        if (JsonDictionaryValue.Get(request, "input") is string text)
            return [Message(text)];

        var history = new List<object?>();
        foreach (var value in JsonDictionaryValue.List(request, "input"))
        {
            if (value is not IReadOnlyDictionary<string, object?> source)
                throw new BadRequestException("Multi-agent input items must be objects.");
            var item = WebSearchPayload.DeepCopyObject(source);
            var type = JsonDictionaryValue.String(item, "type");
            if (type == "additional_tools") continue;
            item.Remove("additional_tools");
            item.Remove("agent");
            if (type == "agent_message")
            {
                var blocks = JsonDictionaryValue.List(item, "content");
                var content = JsonDictionaryValue.Get(item, "content") is string plain
                    ? plain
                    : string.Join("\n", blocks.OfType<IReadOnlyDictionary<string, object?>>()
                        .Select(AgentMessageText));
                history.Add(Message($"Agent message from {JsonDictionaryValue.String(item, "author")} to {JsonDictionaryValue.String(item, "recipient")}:\n{content}"));
            }
            else if (type is "multi_agent_call" or "multi_agent_call_output")
            {
                throw new BadRequestException("Importing an external hosted multi-agent run is not supported. Start a new server multi-agent conversation.");
            }
            else if (item.Count > 0)
            {
                history.Add(item);
            }
        }
        return history;
    }

    public static string? ActionName(string toolName)
    {
        if (toolName.StartsWith(Prefix, StringComparison.Ordinal) && Actions.Contains(toolName[Prefix.Length..]))
            return toolName[Prefix.Length..];
        return null;
    }

    private static bool IsClientAction(string name)
    {
        return name.StartsWith("collaboration.", StringComparison.Ordinal) && Actions.Contains(name[14..])
            || name.StartsWith("collaboration_", StringComparison.Ordinal) && Actions.Contains(name[14..]);
    }

    private static string AgentMessageText(IReadOnlyDictionary<string, object?> block)
    {
        if (JsonDictionaryValue.String(block, "type") != "encrypted_content")
            return JsonDictionaryValue.String(block, "text");

        var content = JsonDictionaryValue.String(block, "encrypted_content");
        if (content.StartsWith("enc_", StringComparison.Ordinal))
            throw new BadRequestException("External encrypted agent history is not supported. Start a new server multi-agent conversation with plain text.");
        return content;
    }

    private static Dictionary<string, object?> Message(string text) => new()
    {
        ["type"] = "message", ["role"] = "user", ["content"] = text
    };

    private static IEnumerable<object?> CreateTools()
    {
        yield return Tool("spawn_agent", "Create a child agent for a concrete independent task. Returns its canonical agent path.",
            new Dictionary<string, object?>
            {
                ["task_name"] = StringParameter("Child task name using lowercase letters, digits, and underscores."),
                ["message"] = StringParameter("Initial task instructions in plain text."),
                ["fork_turns"] = StringParameter("Context to inherit: none, all, or a positive turn count. Defaults to all.")
            }, "task_name", "message");
        yield return Tool("send_message", "Deliver a plain-text message to another agent. Does not start a new task for an idle agent.",
            TargetAndMessage(), "target", "message");
        yield return Tool("followup_task", "Assign follow-up work to an existing non-root agent and start its next task when idle.",
            TargetAndMessage(), "target", "message");
        yield return Tool("wait_agent", "Wait for a mailbox update or timeout. Returns messages and agent completion notifications.",
            new Dictionary<string, object?>
            {
                ["timeout_ms"] = new Dictionary<string, object?>
                {
                    ["type"] = "integer", ["description"] = "Maximum time to wait in milliseconds. Defaults to 10000.",
                    ["minimum"] = 1, ["maximum"] = 60000
                }
            });
        yield return Tool("interrupt_agent", "Interrupt an agent's current task while preserving its conversation.",
            new Dictionary<string, object?> { ["target"] = StringParameter("Relative or canonical agent path.") }, "target");
        yield return Tool("list_agents", "List agents, task paths, and their current states in this run.",
            new Dictionary<string, object?> { ["path_prefix"] = StringParameter("Optional canonical task-path prefix.") });
    }

    private static Dictionary<string, object?> TargetAndMessage() => new()
    {
        ["target"] = StringParameter("Relative or canonical agent path."),
        ["message"] = StringParameter("Plain-text message or task instructions.")
    };

    private static Dictionary<string, object?> StringParameter(string description) => new()
    {
        ["type"] = "string", ["description"] = description
    };

    private static Dictionary<string, object?> Tool(string action, string description,
        Dictionary<string, object?> properties, params string[] required) => new()
    {
        ["type"] = "function", ["name"] = Prefix + action, ["description"] = description, ["strict"] = false,
        ["parameters"] = new Dictionary<string, object?>
        {
            ["type"] = "object", ["properties"] = properties, ["required"] = required,
            ["additionalProperties"] = false
        }
    };
}
