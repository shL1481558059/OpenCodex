using System.Text.Json;
using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>Identifies the native client thread carrying one server-managed agent.</summary>
public sealed class MultiAgentClientIdentity
{
    public string ThreadId { get; init; } = "";
    public string ParentThreadId { get; init; } = "";
    public string RootThreadId { get; init; } = "";
    public string AgentName { get; init; } = "";
    public string TurnId { get; init; } = "";

    public static MultiAgentClientIdentity? Parse(IReadOnlyDictionary<string, string> headers)
    {
        var metadata = ReadMetadata(Header(headers, "x-codex-turn-metadata"));
        var thread = Agree(Header(headers, "thread-id"), Field(metadata, "thread_id"), "thread_id");
        var parent = Agree(Header(headers, "x-codex-parent-thread-id"), Field(metadata, "parent_thread_id"), "parent_thread_id");
        var session = Agree(Header(headers, "session-id"), Field(metadata, "session_id"), "session_id");
        var agent = Field(metadata, "agent_name");
        var subagentKind = Field(metadata, "subagent_kind");
        var turn = Field(metadata, "turn_id");

        if (thread.Length == 0)
        {
            if (parent.Length > 0 || agent.Length > 0 || subagentKind.Length > 0)
                throw new BadRequestException("Client agent identity requires thread_id.");
            return null;
        }

        if (parent.Length == 0)
        {
            if (subagentKind.Length > 0 || agent.Length > 0 && agent != "/root")
                throw new BadRequestException("Client child identity requires parent_thread_id.");
            return new() { ThreadId = thread, RootThreadId = thread, AgentName = "/root", TurnId = turn };
        }

        if (session.Length == 0 || !IsChildPath(agent))
            throw new BadRequestException("Client child identity requires root session_id and canonical agent_name.");
        if (thread == parent || thread == session)
            throw new BadRequestException("Client child thread must differ from its parent and root thread.");

        return new()
        {
            ThreadId = thread, ParentThreadId = parent, RootThreadId = session,
            AgentName = agent, TurnId = turn
        };
    }

    private static bool IsChildPath(string value) => value.StartsWith("/root/", StringComparison.Ordinal)
        && value[6..].Split('/').All(segment => segment.Length > 0
            && segment.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'));

    private static string Header(IReadOnlyDictionary<string, string> headers, string name)
    {
        var values = headers.Where(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (values.Count > 1)
            throw new BadRequestException($"Conflicting client identity header: {name}.");
        return values.SingleOrDefault() ?? "";
    }

    private static Dictionary<string, JsonElement> ReadMetadata(string text)
    {
        if (text.Length == 0) return [];
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new BadRequestException("Client turn metadata must be a JSON object.");
            var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!metadata.TryAdd(property.Name, property.Value.Clone()))
                    throw new BadRequestException("Client turn metadata contains a duplicate field.");
            }
            return metadata;
        }
        catch (JsonException)
        {
            throw new BadRequestException("Client turn metadata is invalid JSON.");
        }
    }

    private static string Field(IReadOnlyDictionary<string, JsonElement> metadata, string name)
    {
        if (!metadata.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) return "";
        if (value.ValueKind != JsonValueKind.String)
            throw new BadRequestException($"Client identity field {name} must be a string.");
        return value.GetString()!.Trim();
    }

    private static string Agree(string header, string metadata, string field)
    {
        if (header.Length > 0 && metadata.Length > 0 && header != metadata)
            throw new BadRequestException($"Conflicting client identity field: {field}.");
        return header.Length > 0 ? header : metadata;
    }
}
