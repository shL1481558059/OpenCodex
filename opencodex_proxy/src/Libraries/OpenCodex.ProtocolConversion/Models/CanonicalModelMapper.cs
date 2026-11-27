using System.Text.Json;
using OpenCodex.ProtocolConversion.Errors;

namespace OpenCodex.ProtocolConversion.Models;

internal static class CanonicalModelMapper
{
    private static readonly HashSet<string> RequestStructureFields = new(StringComparer.Ordinal)
    {
        "model", "messages", "input", "instructions", "system", "tools", "tool_choice", "params"
    };
    public static CanonicalRequest FromRequestGraph(Dictionary<string, object?> graph, ProtocolKind sourceProtocol)
    {
        var model = ProtocolJson.To<string>(graph.GetValueOrDefault("model")) ?? string.Empty;
        var messages = ReadMessages(graph.GetValueOrDefault("messages"));
        var tools = ReadTools(graph.GetValueOrDefault("tools"));
        var parameters = ReadParameters(graph.GetValueOrDefault("params"))
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var extensions = ReadExtensions(
            graph,
            "model",
            "messages",
            "tools",
            "tool_choice",
            "params");

        return new CanonicalRequest
        {
            SourceProtocol = sourceProtocol,
            Model = model,
            Messages = messages,
            Tools = tools,
            ToolChoice = ProtocolJson.ToElement(graph.GetValueOrDefault("tool_choice")),
            Parameters = parameters,
            ExtensionData = extensions
        };
    }

    public static Dictionary<string, object?> ToRequestGraph(CanonicalRequest value)
    {
        var parameters = WriteParameters(value.Parameters);
        if (value.ExtensionData is not null)
        {
            foreach (var (key, item) in value.ExtensionData)
            {
                if (!RequestStructureFields.Contains(key) && !parameters.ContainsKey(key))
                    parameters[key] = ProtocolJson.ToObject(item);
            }
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = value.Model,
            ["messages"] = value.Messages.Select(WriteMessage).Cast<object?>().ToList(),
            ["tools"] = WriteTools(value.Tools),
            ["tool_choice"] = ProtocolJson.ToObject(value.ToolChoice),
            ["params"] = parameters
        };
    }

    public static CanonicalResponse FromResponseGraph(
        Dictionary<string, object?> graph,
        ProtocolKind sourceProtocol,
        IReadOnlyDictionary<string, object?> original,
        JsonElement? rawSnapshot = null)
    {
        var extensions = ReadExtensions(graph,
            "id", "model", "created", "text", "reasoning", "refusal", "annotations",
            "tool_calls", "tool_results", "finish_reason", "usage", "anthropic_thinking_encrypted",
            "raw", "error", "status");
        var raw = rawSnapshot ?? ProtocolJson.ToElement(original);
        if (raw.ValueKind == JsonValueKind.Object)
        {
            extensions ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in raw.EnumerateObject())
            {
                if (!graph.ContainsKey(property.Name))
                {
                    extensions.TryAdd(property.Name, property.Value);
                }
            }
        }

        return new CanonicalResponse
        {
            SourceProtocol = sourceProtocol,
            Id = ProtocolJson.To<string>(graph.GetValueOrDefault("id")) ?? string.Empty,
            Model = ProtocolJson.To<string>(graph.GetValueOrDefault("model")),
            Created = raw.TryGetProperty(sourceProtocol == ProtocolKind.Responses ? "created_at" : "created", out var created)
                ? ProtocolJson.To<long?>(created) : null,
            Text = ProtocolJson.To<string>(graph.GetValueOrDefault("text")) ?? string.Empty,
            Reasoning = ProtocolJson.To<string>(graph.GetValueOrDefault("reasoning")) ?? string.Empty,
            Refusal = ProtocolJson.To<string>(graph.GetValueOrDefault("refusal")) ?? string.Empty,
            Annotations = ReadAnnotations(graph.GetValueOrDefault("annotations")),
            ToolCalls = ReadToolCalls(graph.GetValueOrDefault("tool_calls")),
            ToolResults = ReadToolResults(graph.GetValueOrDefault("tool_results")),
            Status = raw.TryGetProperty("status", out var status)
                ? ProtocolJson.To<string>(status) : ProtocolJson.To<string>(graph.GetValueOrDefault("status")),
            FinishReason = ProtocolJson.To<string>(graph.GetValueOrDefault("finish_reason")) ?? "stop",
            Usage = ReadUsage(graph.GetValueOrDefault("usage")),
            AnthropicThinkingEncrypted = ProtocolJson.To<string>(graph.GetValueOrDefault("anthropic_thinking_encrypted")),
            Raw = raw,
            Error = ProtocolJson.ToElement(graph.GetValueOrDefault("error")),
            ExtensionData = extensions
        };
    }

    public static Dictionary<string, object?> ToSameProtocolResponseGraph(CanonicalResponse value, ProtocolKind source)
    {
        var raw = value.Raw!.Value;
        var result = (Dictionary<string, object?>)ProtocolJson.ToObject(raw)!;
        var baseline = ToResponseGraph(FromResponseGraph(
            ProtocolConverter.ToCanonicalResponse(result, source.ToWireValue(), null, null), source, result, raw));
        var current = ToResponseGraph(value);
        var changed = current.Keys.Where(key => !JsonElement.DeepEquals(
            ProtocolJson.ToElement(current[key]), ProtocolJson.ToElement(baseline[key])))
            .ToHashSet(StringComparer.Ordinal);

        // Raw is the immutable wire baseline. Only changed typed fields replace its known projections.
        if (changed.Count > 0)
        {
            if (source == ProtocolKind.Responses) ValidateResponseStatusMutation(value, changed);
            var generated = ProtocolConverter.FromCanonicalResponse(current, source.ToWireValue());
            foreach (var key in new[] { "id", "model", "status", "error" })
            {
                if (changed.Contains(key)) ProjectField(result, generated, key);
            }
            if (changed.Contains("created"))
                ProjectField(result, generated, source == ProtocolKind.Responses ? "created_at" : "created");
            var contentChanged = changed.Overlaps(new[] { "text", "reasoning", "refusal", "annotations", "tool_calls", "tool_results", "anthropic_thinking_encrypted" });
            if (source == ProtocolKind.Chat && (contentChanged || changed.Contains("finish_reason")))
                ProjectChatFields(result, generated, changed);
            else if (source == ProtocolKind.Messages)
            {
                if (contentChanged) ProjectSingleRawText(result, value.Text, source, changed);
                if (changed.Contains("finish_reason")) ProjectField(result, generated, "stop_reason");
            }
            else if (source == ProtocolKind.Responses)
            {
                if (contentChanged) ProjectSingleRawText(result, value.Text, source, changed);
                if (changed.Contains("finish_reason"))
                {
                    ProjectField(result, generated, "status");
                    ProjectField(result, generated, "incomplete_details");
                }
            }
            if (changed.Contains("usage")) ProjectSameProtocolUsage(result, value.Usage, source, (Dictionary<string, object?>)baseline["usage"]!);
            if (changed.Contains("status") && value.Status == "failed" && source != ProtocolKind.Responses)
            {
                result.Remove("choices"); result.Remove("content"); result.Remove("stop_reason");
                if (source == ProtocolKind.Messages) result["type"] = "error";
            }
        }

        if (value.ExtensionData is not null)
        {
            foreach (var (key, item) in value.ExtensionData)
            {
                if (key is not ("id" or "model" or "created" or "created_at" or "status" or "error" or "usage"
                    or "choices" or "content" or "output" or "stop_reason" or "stop_sequence" or "incomplete_details"
                    or "type" or "role" or "object" or "text" or "reasoning" or "refusal" or "annotations" or "tool_calls" or "tool_results"))
                    result[key] = ProtocolJson.ToObject(item);
            }
        }
        return result;
    }

    private static void ProjectSingleRawText(Dictionary<string, object?> output, string text,
        ProtocolKind source, HashSet<string> changed)
    {
        if (changed.Overlaps(new[] { "reasoning", "refusal", "annotations", "tool_calls", "tool_results", "anthropic_thinking_encrypted" }))
            throw new BadRequestException($"Structured changes cannot be projected losslessly into a raw {source.ToWireValue()} response.");

        // The canonical Text is an aggregate. Only one original text node has an unambiguous edit target.
        var field = source == ProtocolKind.Messages ? "content" : "output";
        if (!output.TryGetValue(field, out var content) || content is not List<object?> items)
            throw new BadRequestException($"Changing raw {source.ToWireValue()} Text requires an original {field} array and exactly one original text block.");
        var blocks = source == ProtocolKind.Messages
            ? items.OfType<Dictionary<string, object?>>()
                .Where(block => block.GetValueOrDefault("type") is "text").ToList()
            : items.OfType<Dictionary<string, object?>>()
                .Where(item => item.GetValueOrDefault("type") is "message")
                .SelectMany(item => item.GetValueOrDefault("content") is List<object?> parts
                    ? parts.OfType<Dictionary<string, object?>>()
                    : throw new BadRequestException("Changing raw Responses Text requires original message content arrays."))
                .Where(block => block.GetValueOrDefault("type") is "output_text" or "text").ToList();
        if (blocks.Count != 1)
            throw new BadRequestException($"Changing raw {source.ToWireValue()} Text requires exactly one original text block; an aggregate edit has no lossless target.");
        blocks[0]["text"] = text;
    }

    private static void ValidateResponseStatusMutation(CanonicalResponse value, HashSet<string> changed)
    {
        if (!changed.Contains("status") && !changed.Contains("finish_reason")) return;
        if (changed.Contains("status") && value.Status is null)
            throw new BadRequestException("An explicit Responses status cannot be cleared.");
        ValidateResponseStatusForRendering(value);
    }

    internal static void ValidateResponseStatusForRendering(CanonicalResponse value)
    {
        if (value.Status == "failed")
        {
            if (value.FinishReason != "stop" || value.Error is null || value.Error.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new BadRequestException("Changing a response to failed requires an explicit error.");
            return;
        }
        var status = value.FinishReason switch
        {
            "stop" or "tool_calls" => "completed",
            "length" or "content_filter" => "incomplete",
            _ => throw new BadRequestException("The modified response finish reason cannot be represented by Responses.")
        };
        if (value.Status is not null && value.Status != status)
            throw new BadRequestException("The explicit response Status conflicts with FinishReason.");
    }

    private static void ProjectField(Dictionary<string, object?> output, Dictionary<string, object?> generated, string key)
    {
        if (generated.TryGetValue(key, out var value)) output[key] = value;
        else output.Remove(key);
    }

    private static void ProjectChatFields(Dictionary<string, object?> output, Dictionary<string, object?> generated, HashSet<string> changed)
    {
        if (!generated.TryGetValue("choices", out var generatedChoices)) return;
        var generatedChoice = (Dictionary<string, object?>)((List<object?>)generatedChoices!)[0]!;
        if (!output.TryGetValue("choices", out var rawChoices) || ((List<object?>)rawChoices!).Count == 0)
        {
            output["choices"] = generatedChoices;
            return;
        }
        var choice = (Dictionary<string, object?>)((List<object?>)rawChoices!)[0]!;
        var message = (Dictionary<string, object?>)choice["message"]!;
        var generatedMessage = (Dictionary<string, object?>)generatedChoice["message"]!;
        foreach (var (canonical, wire) in new[] { ("text", "content"), ("reasoning", "reasoning_content"),
            ("refusal", "refusal"), ("annotations", "annotations"), ("tool_calls", "tool_calls"),
            ("anthropic_thinking_encrypted", "anthropic_thinking_encrypted") })
            if (changed.Contains(canonical)) ProjectField(message, generatedMessage, wire);
        if (changed.Contains("finish_reason")) ProjectField(choice, generatedChoice, "finish_reason");
    }

    private static void ProjectSameProtocolUsage(Dictionary<string, object?> output, CanonicalUsage usage,
        ProtocolKind source, Dictionary<string, object?> baseline)
    {
        var wire = output.GetValueOrDefault("usage") as Dictionary<string, object?> ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        if (usage.ExtensionData is not null)
        {
            foreach (var (key, value) in usage.ExtensionData)
                if (key is not ("input_tokens" or "output_tokens" or "total_tokens" or "cached_tokens" or "prompt_tokens" or "completion_tokens" or "cache_read_input_tokens" or "cache_creation_input_tokens"))
                    wire[key] = ProtocolJson.ToObject(value);
        }
        foreach (var (canonical, target, count) in new[] {
            ("input_tokens", source == ProtocolKind.Chat ? "prompt_tokens" : "input_tokens", usage.InputTokens),
            ("output_tokens", source == ProtocolKind.Chat ? "completion_tokens" : "output_tokens", usage.OutputTokens),
            ("total_tokens", "total_tokens", usage.TotalTokens) })
            if (!Equals(baseline[canonical], count)) wire[target] = count;
        var cachedChanged = !Equals(baseline["cached_tokens"], usage.CachedTokens);
        if (source == ProtocolKind.Messages)
        {
            var details = usage.ExtensionData;
            var detailsChanged = details is not null && new[] { "cache_read_input_tokens", "cache_creation_input_tokens" }
                .Any(key => details.TryGetValue(key, out var value) && !JsonElement.DeepEquals(value, ProtocolJson.ToElement(baseline.GetValueOrDefault(key))));
            if (cachedChanged || detailsChanged)
            {
                if (details is null || !details.TryGetValue("cache_read_input_tokens", out var read)
                    || !details.TryGetValue("cache_creation_input_tokens", out var creation)
                    || read.ValueKind != JsonValueKind.Number || creation.ValueKind != JsonValueKind.Number
                    || !read.TryGetInt32(out var readTokens) || !creation.TryGetInt32(out var creationTokens)
                    || readTokens < 0 || creationTokens < 0 || (long)readTokens + creationTokens != usage.CachedTokens)
                    throw new BadRequestException("Changing Messages cache counts requires explicit read/creation details whose sum equals CachedTokens.");
                wire["cache_read_input_tokens"] = readTokens;
                wire["cache_creation_input_tokens"] = creationTokens;
            }
        }
        else
        {
            var key = source == ProtocolKind.Chat ? "prompt_tokens_details" : "input_tokens_details";
            var details = wire.GetValueOrDefault(key) as Dictionary<string, object?>;
            if (cachedChanged)
            {
                details ??= new Dictionary<string, object?>(StringComparer.Ordinal);
                wire[key] = details;
            }
            if (details is not null && (cachedChanged || details.ContainsKey("cached_tokens")))
                details["cached_tokens"] = usage.CachedTokens;
        }
        output["usage"] = wire;
    }

    public static Dictionary<string, object?> ToResponseGraph(CanonicalResponse value)
    {
        var graph = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = value.Id,
            ["model"] = value.Model,
            ["created"] = value.Created,
            ["text"] = value.Text,
            ["reasoning"] = value.Reasoning,
            ["refusal"] = value.Refusal,
            ["annotations"] = value.Annotations.Select(value => ProtocolJson.ToObject(value)).ToList(),
            ["tool_calls"] = value.ToolCalls.Select(WriteToolCall).Cast<object?>().ToList(),
            ["tool_results"] = value.ToolResults.Select(WriteToolResult).Cast<object?>().ToList(),
            ["status"] = value.Status,
            ["finish_reason"] = value.FinishReason,
            ["usage"] = WriteUsage(value.Usage),
            ["anthropic_thinking_encrypted"] = value.AnthropicThinkingEncrypted,
            ["error"] = ProtocolJson.ToObject(value.Error)
        };
        return graph;
    }

    private static List<CanonicalMessage> ReadMessages(object? value)
    {
        if (value is not IEnumerable<object?> items)
        {
            return [];
        }

        var result = new List<CanonicalMessage>();
        foreach (var item in items)
        {
            if (item is not Dictionary<string, object?> message)
            {
                continue;
            }

            result.Add(new CanonicalMessage
            {
                Role = ProtocolJson.To<string>(message.GetValueOrDefault("role")) ?? "user",
                Content = ReadContent(message.GetValueOrDefault("content")),
                ToolCalls = ReadRequestToolCalls(message.GetValueOrDefault("tool_calls")),
                ToolCallId = ProtocolJson.To<string>(message.GetValueOrDefault("tool_call_id")),
                Name = ProtocolJson.To<string>(message.GetValueOrDefault("name")),
                NativeType = ProtocolJson.To<string>(message.GetValueOrDefault("native_type")),
                ReasoningContent = ProtocolJson.To<string>(message.GetValueOrDefault("reasoning_content")),
                AnthropicThinkingEncrypted = ProtocolJson.To<string>(message.GetValueOrDefault("anthropic_thinking_encrypted")),
                IsError = ProtocolJson.To<bool?>(message.GetValueOrDefault("is_error")),
                ExtensionData = ReadExtensions(message,
                    "role", "content", "tool_calls", "tool_call_id", "name", "native_type",
                    "reasoning_content", "anthropic_thinking_encrypted", "is_error")
            });
        }
        return result;
    }

    private static Dictionary<string, object?> WriteMessage(CanonicalMessage message)
    {
        var graph = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = message.Role,
            ["content"] = WriteContent(message.Content),
            ["tool_calls"] = message.ToolCalls?.Select(WriteRequestToolCall).Cast<object?>().ToList(),
            ["tool_call_id"] = message.ToolCallId,
            ["name"] = message.Name,
            ["native_type"] = message.NativeType,
            ["reasoning_content"] = message.ReasoningContent,
            ["anthropic_thinking_encrypted"] = message.AnthropicThinkingEncrypted,
            ["is_error"] = message.IsError
        };
        WriteExtensions(graph, message.ExtensionData);
        return graph;
    }

    private static IList<CanonicalContentPart> ReadContent(object? value)
    {
        var result = new List<CanonicalContentPart>();
        if (value is string text)
        {
            result.Add(new CanonicalContentPart { Type = "text", Text = text });
            return result;
        }
        if (value is not IEnumerable<object?> items)
        {
            return result;
        }

        foreach (var item in items)
        {
            if (item is not Dictionary<string, object?> part)
            {
                continue;
            }
            var image = part.GetValueOrDefault("image_url");
            var detail = part.GetValueOrDefault("detail");
            if (image is Dictionary<string, object?> imageObject)
            {
                image = imageObject.GetValueOrDefault("url");
                detail = imageObject.GetValueOrDefault("detail");
            }
            result.Add(new CanonicalContentPart
            {
                Type = ProtocolJson.To<string>(part.GetValueOrDefault("type")) ?? "text",
                Text = ProtocolJson.To<string>(part.GetValueOrDefault("text")),
                ImageUrl = ProtocolJson.To<string>(image),
                Detail = ProtocolJson.To<string>(detail),
                FileId = ProtocolJson.To<string>(part.GetValueOrDefault("file_id")),
                FileData = ProtocolJson.To<string>(part.GetValueOrDefault("file_data")),
                FileName = ProtocolJson.To<string>(part.GetValueOrDefault("filename")),
                FileUrl = ProtocolJson.To<string>(part.GetValueOrDefault("file_url")),
                ExtensionData = ReadExtensions(part,
                    "type", "text", "image_url", "detail", "file_id", "file_data", "filename", "file_url")
            });
        }
        return result;
    }

    private static List<object?> WriteContent(IList<CanonicalContentPart> parts)
    {
        return parts.Select(part =>
        {
            var graph = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = part.Type
            };
            if (part.Text is not null) graph["text"] = part.Text;
            if (part.FileId is not null) graph["file_id"] = part.FileId;
            if (part.FileData is not null) graph["file_data"] = part.FileData;
            if (part.FileName is not null) graph["filename"] = part.FileName;
            if (part.FileUrl is not null) graph["file_url"] = part.FileUrl;
            if (part.Type == "image_url")
            {
                var image = new Dictionary<string, object?> { ["url"] = part.ImageUrl };
                if (part.Detail is not null) image["detail"] = part.Detail;
                graph["image_url"] = image;
            }
            else
            {
                if (part.ImageUrl is not null) graph["image_url"] = part.ImageUrl;
                if (part.Detail is not null) graph["detail"] = part.Detail;
            }
            WriteExtensions(graph, part.ExtensionData, "text", "image_url", "detail", "file_id", "file_data", "filename", "file_url");
            return (object?)graph;
        }).ToList();
    }

    private static IList<CanonicalToolDefinition> ReadTools(object? value)
    {
        if (value is not IEnumerable<object?> items)
        {
            return [];
        }

        var result = new List<CanonicalToolDefinition>();
        foreach (var item in items)
        {
            if (item is not Dictionary<string, object?> tool)
            {
                continue;
            }
            var declaration = tool.GetValueOrDefault("raw") as Dictionary<string, object?>;
            var namespaceName = ProtocolJson.To<string>(tool.GetValueOrDefault("namespace"));
            var declaredName = ProtocolJson.To<string>(declaration is null ? tool.GetValueOrDefault("name") : declaration.GetValueOrDefault("name")) ?? string.Empty;
            var prefix = namespaceName + "__";
            if (declaration is null && !string.IsNullOrEmpty(namespaceName) && declaredName.StartsWith(prefix, StringComparison.Ordinal))
                declaredName = declaredName[prefix.Length..];
            result.Add(new CanonicalToolDefinition
            {
                Name = declaredName,
                Type = ProtocolJson.To<string>(declaration is null ? tool.GetValueOrDefault("type") : declaration.GetValueOrDefault("type")) ?? "function",
                Namespace = namespaceName,
                NativeType = ProtocolJson.To<string>(tool.GetValueOrDefault("native_type")),
                InputSchema = ProtocolJson.ToElement(tool.GetValueOrDefault("parameters")),
                ExtensionData = ReadExtensions(tool, "name", "type", "namespace", "native_type", "parameters")
            });
        }
        return result;
    }

    private static List<object?> WriteTools(IList<CanonicalToolDefinition> tools)
    {
        return tools.Select(tool =>
        {
            var graph = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = string.IsNullOrEmpty(tool.Namespace) ? tool.Name : $"{tool.Namespace}__{tool.Name}",
                ["type"] = tool.Type,
                ["namespace"] = tool.Namespace,
                ["native_type"] = tool.NativeType ?? tool.Type,
                ["parameters"] = ProtocolJson.ToObject(tool.InputSchema)
            };
            WriteExtensions(graph, tool.ExtensionData);
            if (graph.GetValueOrDefault("raw") is Dictionary<string, object?> || tool.Type != "function")
            {
                var declaration = graph.GetValueOrDefault("raw") as Dictionary<string, object?>
                    ?? new Dictionary<string, object?>(StringComparer.Ordinal);
                declaration["type"] = tool.Type;
                declaration["name"] = tool.Name;
                if (tool.Type == "function") declaration["parameters"] = ProtocolJson.ToObject(tool.InputSchema);
                foreach (var key in new[] { "description", "format", "strict", "defer_loading", "allowed_callers", "output_schema" })
                    if (graph.TryGetValue(key, out var value)) declaration[key] = value;
                graph["raw"] = declaration;
            }
            return (object?)graph;
        }).ToList();
    }

    // Request graphs use Chat-shaped nested function objects; response graphs are flat.
    private static List<CanonicalToolCall> ReadRequestToolCalls(object? value)
    {
        if (value is null) return [];
        var result = new List<CanonicalToolCall>();
        foreach (var item in (IEnumerable<object?>)value)
        {
            var call = (Dictionary<string, object?>)item!;
            var function = (Dictionary<string, object?>)call["function"]!;
            var name = ProtocolJson.To<string>(function["name"]);
            if (string.IsNullOrEmpty(name)) throw new BadRequestException("A canonical request tool call requires a function name.");
            result.Add(new CanonicalToolCall
            {
                Id = ProtocolJson.To<string>(call["id"])!,
                Name = name,
                Namespace = ProtocolJson.To<string>(call.GetValueOrDefault("namespace")),
                Arguments = ProtocolJson.ToElement(function["arguments"]),
                NativeType = ProtocolJson.To<string>(call.GetValueOrDefault("native_type")),
                FunctionExtensionData = ReadExtensions(function, "name", "arguments"),
                ExtensionData = ReadExtensions(call, "id", "type", "function", "name", "arguments", "namespace", "native_type")
            });
        }
        return result;
    }

    private static Dictionary<string, object?> WriteRequestToolCall(CanonicalToolCall call)
    {
        if (string.IsNullOrEmpty(call.Id) || string.IsNullOrEmpty(call.Name)
            || call.Arguments.ValueKind == JsonValueKind.Undefined)
            throw new BadRequestException("A canonical request tool call requires id, name and arguments.");
        var arguments = call.Arguments.ValueKind == JsonValueKind.String
            ? call.Arguments.GetString()! : call.Arguments.GetRawText();
        var function = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = call.Name,
            ["arguments"] = arguments
        };
        WriteExtensions(function, call.FunctionExtensionData);
        var graph = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = call.Id,
            ["type"] = "function",
            ["function"] = function,
            ["namespace"] = call.Namespace,
            ["native_type"] = call.NativeType
        };
        WriteExtensions(graph, call.ExtensionData, "name", "arguments");
        return graph;
    }

    private static List<CanonicalToolCall> ReadToolCalls(object? value)
    {
        if (value is not IEnumerable<object?> items)
        {
            return [];
        }
        var result = new List<CanonicalToolCall>();
        foreach (var item in items)
        {
            if (item is not Dictionary<string, object?> call)
            {
                continue;
            }
            result.Add(new CanonicalToolCall
            {
                Id = ProtocolJson.To<string>(call.GetValueOrDefault("id")) ?? string.Empty,
                Name = ProtocolJson.To<string>(call.GetValueOrDefault("name")) ?? string.Empty,
                Namespace = ProtocolJson.To<string>(call.GetValueOrDefault("namespace")),
                Arguments = ProtocolJson.ToElement(call.GetValueOrDefault("arguments")),
                NativeType = ProtocolJson.To<string>(call.GetValueOrDefault("native_type")),
                ExtensionData = ReadExtensions(call, "id", "name", "namespace", "arguments", "native_type")
            });
        }
        return result;
    }

    private static Dictionary<string, object?> WriteToolCall(CanonicalToolCall call)
    {
        var graph = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = call.Id,
            ["name"] = call.Name,
            ["namespace"] = call.Namespace,
            ["arguments"] = ProtocolJson.ToObject(call.Arguments),
            ["native_type"] = call.NativeType
        };
        WriteExtensions(graph, call.ExtensionData);
        return graph;
    }

    private static List<CanonicalToolResult> ReadToolResults(object? value)
    {
        if (value is not IEnumerable<object?> items)
        {
            return [];
        }
        var result = new List<CanonicalToolResult>();
        foreach (var item in items)
        {
            if (item is not Dictionary<string, object?> call)
            {
                continue;
            }
            result.Add(new CanonicalToolResult
            {
                Id = ProtocolJson.To<string>(call.GetValueOrDefault("id")) ?? string.Empty,
                Output = ProtocolJson.ToElement(call.GetValueOrDefault("output")),
                IsError = ProtocolJson.To<bool?>(call.GetValueOrDefault("is_error")),
                NativeType = ProtocolJson.To<string>(call.GetValueOrDefault("native_type")),
                ExtensionData = ReadExtensions(call, "id", "output", "is_error", "native_type")
            });
        }
        return result;
    }

    private static Dictionary<string, object?> WriteToolResult(CanonicalToolResult result)
    {
        var graph = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = result.Id,
            ["output"] = ProtocolJson.ToObject(result.Output),
            ["is_error"] = result.IsError,
            ["native_type"] = result.NativeType
        };
        WriteExtensions(graph, result.ExtensionData);
        return graph;
    }

    private static List<JsonElement> ReadAnnotations(object? value)
    {
        return value is IEnumerable<object?> items
            ? items.Select(ProtocolJson.ToElement).Where(item => item.ValueKind != JsonValueKind.Undefined).ToList()
            : [];
    }

    private static CanonicalUsage ReadUsage(object? value)
    {
        return value is Dictionary<string, object?> usage
            ? new CanonicalUsage
            {
                InputTokens = ProtocolJson.To<int>(usage.GetValueOrDefault("input_tokens")),
                OutputTokens = ProtocolJson.To<int>(usage.GetValueOrDefault("output_tokens")),
                TotalTokens = ProtocolJson.To<int>(usage.GetValueOrDefault("total_tokens")),
                CachedTokens = ProtocolJson.To<int>(usage.GetValueOrDefault("cached_tokens")),
                ExtensionData = ReadExtensions(
                    usage,
                    "input_tokens", "output_tokens", "total_tokens", "cached_tokens")
            }
            : new CanonicalUsage();
    }

    private static Dictionary<string, object?> WriteUsage(CanonicalUsage usage)
    {
        var graph = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["input_tokens"] = usage.InputTokens,
            ["output_tokens"] = usage.OutputTokens,
            ["total_tokens"] = usage.TotalTokens,
            ["cached_tokens"] = usage.CachedTokens
        };
        WriteExtensions(graph, usage.ExtensionData);
        return graph;
    }

    private static IReadOnlyDictionary<string, JsonElement>? ReadParameters(object? value)
    {
        return value is Dictionary<string, object?> parameters
            ? parameters.ToDictionary(pair => pair.Key, pair => ProtocolJson.ToElement(pair.Value), StringComparer.Ordinal)
            : null;
    }

    private static Dictionary<string, object?> WriteParameters(IReadOnlyDictionary<string, JsonElement>? parameters) =>
        parameters?.Where(pair => !RequestStructureFields.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => ProtocolJson.ToObject(pair.Value), StringComparer.Ordinal)
        ?? new Dictionary<string, object?>(StringComparer.Ordinal);

    private static Dictionary<string, JsonElement>? ReadExtensions(
        IReadOnlyDictionary<string, object?> graph,
        params string[] known)
    {
        var knownSet = new HashSet<string>(known, StringComparer.Ordinal);
        var extensions = graph
            .Where(pair => !knownSet.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => ProtocolJson.ToElement(pair.Value), StringComparer.Ordinal);
        return extensions.Count == 0 ? null : extensions;
    }

    private static void WriteExtensions(
        Dictionary<string, object?> graph,
        IReadOnlyDictionary<string, JsonElement>? extensions,
        params string[] reserved)
    {
        if (extensions is null)
        {
            return;
        }
        foreach (var (key, value) in extensions)
        {
            if (!graph.ContainsKey(key) && !reserved.Contains(key, StringComparer.Ordinal))
                graph[key] = ProtocolJson.ToObject(value);
        }
    }
}

public static class ProtocolJson
{
    public static JsonElement ToElement(object? value) =>
        JsonSerializer.SerializeToElement(value);

    public static object? ToObject(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }
        return FromJsonElement(value.Value);
    }

    public static object? FromJsonElement(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(
            property => property.Name,
            property => FromJsonElement(property.Value),
            StringComparer.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Select(FromJsonElement).ToList(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt32(out var integer) => integer,
        JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    public static T? To<T>(object? value)
    {
        if (value is null)
        {
            return default;
        }
        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Undefined ? default : element.Deserialize<T>();
        }
        if (value is T typed)
        {
            return typed;
        }
        return JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToElement(value));
    }
}
