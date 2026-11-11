using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.Core.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ToolWireNameAliasTests
{
    private const string ApifoxNamespace = "mcp__apifox_new_mcp";
    private const string DiscoverMethods = "discoverMethods";
    private const string DotAlias = "mcp__apifox_new_mcp.discoverMethods";
    private const string UnderscoreAlias = "mcp__apifox_new_mcp__discoverMethods";

    public static IEnumerable<object[]> ApifoxNameVectors()
    {
        yield return [DiscoverMethods];
        yield return [DotAlias];
        yield return [UnderscoreAlias];
    }

    [Theory]
    [MemberData(nameof(ApifoxNameVectors))]
    public void NameVectors_ResolveToDeclaredLeafAndNamespace(string upstreamName)
    {
        var mappings = ProtocolConverter.BuildResponsesToolCallMappings(ApifoxRequest());
        Assert.Equal(DiscoverMethods, mappings[DiscoverMethods].ResponsesName);
        Assert.Equal(ApifoxNamespace, mappings[DiscoverMethods].Namespace);
        Assert.True(ProtocolConverter.TryGetResponsesToolCallMapping(upstreamName, mappings, out var mapping));
        Assert.Equal(DiscoverMethods, mapping.ResponsesName);
        Assert.Equal(ApifoxNamespace, mapping.Namespace);

        var shape = ProtocolConverter.ResolveResponsesToolCallShape(upstreamName, mappings);
        Assert.Equal(DiscoverMethods, shape.Name);
        Assert.Equal(ApifoxNamespace, shape.Namespace);

        var item = ProtocolConverter.ResponsesToolCallItemFromToolCall("call", upstreamName, "{}", mappings: mappings);
        Assert.Equal(DiscoverMethods, item["name"]);
        Assert.Equal(ApifoxNamespace, item["namespace"]);
    }

    [Fact]
    public void UniqueLeaf_KeepsBareOutboundWireName()
    {
        var converted = ProtocolConverter.ConvertRequest(
            ApifoxRequest(), ProtocolConverter.Responses, ProtocolConverter.Chat, "upstream");
        Assert.Equal(DiscoverMethods, SingleChatToolName(converted));
        Assert.DoesNotContain(UnderscoreAlias, DeclaredChatToolNames(converted));
    }

    [Fact]
    public void Collision_StillPrefixesOutboundWireNames()
    {
        var request = Request(
            Namespace("alpha", Tool("shared")),
            Namespace("beta", Tool("shared")));
        var converted = ProtocolConverter.ConvertRequest(
            request, ProtocolConverter.Responses, ProtocolConverter.Chat, "upstream");
        Assert.Equal(["alpha__shared", "beta__shared"], DeclaredChatToolNames(converted));

        var mappings = ProtocolConverter.BuildResponsesToolCallMappings(request);
        Assert.True(ProtocolConverter.TryGetResponsesToolCallMapping("alpha.shared", mappings, out var alpha));
        Assert.Equal("alpha", alpha.Namespace);
        Assert.Equal("shared", alpha.ResponsesName);
        Assert.Equal("alpha__shared", alpha.ChatName);
        Assert.False(ProtocolConverter.TryGetResponsesToolCallMapping("shared", mappings, out _));
    }

    [Fact]
    public void ExactLiteralName_WinsOverGeneratedAlias()
    {
        var request = Request(Tool("org.tool"), Namespace("org", Tool("tool")));
        var mappings = ProtocolConverter.BuildResponsesToolCallMappings(request);
        Assert.Null(mappings["org.tool"].Namespace);
        Assert.Equal("org.tool", mappings["org.tool"].ResponsesName);
        Assert.True(ProtocolConverter.TryGetResponsesToolCallMapping("org__tool", mappings, out var aliased));
        Assert.Equal("org", aliased.Namespace);
        Assert.Equal("tool", aliased.ResponsesName);

        var exact = ProtocolConverter.ResponsesToolCallItemFromToolCall("call", "org.tool", "{}", mappings: mappings);
        Assert.Equal("org.tool", exact["name"]);
        Assert.False(exact.ContainsKey("namespace"));

        var converted = ProtocolConverter.ConvertRequest(
            RequestWithChoice(request, "org.tool"),
            ProtocolConverter.Responses, ProtocolConverter.Chat, "upstream");
        Assert.Equal("org.tool", Object(Object(converted["tool_choice"])["function"])["name"]);
        Assert.Contains("org.tool", DeclaredChatToolNames(converted));
        Assert.Contains("tool", DeclaredChatToolNames(converted));
    }

    [Theory]
    [InlineData("foo.bar", "baz", "foo", "bar.baz", "foo.bar.baz", "foo.bar__baz", "foo__bar.baz")]
    [InlineData("foo__bar", "baz", "foo", "bar__baz", "foo__bar__baz", "foo__bar.baz", "foo.bar__baz")]
    public void AmbiguousAlias_IsIndexedWithoutRejectingThePlan(
        string firstNamespace, string firstName, string secondNamespace, string secondName,
        string ambiguousAlias, string firstUniqueAlias, string secondUniqueAlias)
    {
        // Legal vector: leaves differ, so exact wires stay unique. The generated
        // namespace.name / namespace__name aliases can still collide on one form.
        var request = Request(
            Namespace(firstNamespace, Tool(firstName)),
            Namespace(secondNamespace, Tool(secondName)));
        var mappings = ProtocolConverter.BuildResponsesToolCallMappings(request);
        Assert.True(mappings[ambiguousAlias].IsAmbiguous);
        Assert.False(ProtocolConverter.TryGetResponsesToolCallMapping(ambiguousAlias, mappings, out _));

        var firstWire = ProtocolConverter.ResponsesToolCallItemFromToolCall(
            "first", firstName, "{}", mappings: mappings);
        Assert.Equal(firstName, firstWire["name"]);
        Assert.Equal(firstNamespace, firstWire["namespace"]);
        var secondWire = ProtocolConverter.ResponsesToolCallItemFromToolCall(
            "second", secondName, "{}", mappings: mappings);
        Assert.Equal(secondName, secondWire["name"]);
        Assert.Equal(secondNamespace, secondWire["namespace"]);

        var firstAliased = ProtocolConverter.ResponsesToolCallItemFromToolCall(
            "first-alias", firstUniqueAlias, "{}", mappings: mappings);
        Assert.Equal(firstName, firstAliased["name"]);
        Assert.Equal(firstNamespace, firstAliased["namespace"]);
        var secondAliased = ProtocolConverter.ResponsesToolCallItemFromToolCall(
            "second-alias", secondUniqueAlias, "{}", mappings: mappings);
        Assert.Equal(secondName, secondAliased["name"]);
        Assert.Equal(secondNamespace, secondAliased["namespace"]);

        var error = Assert.Throws<UpstreamException>(
            () => ProtocolConverter.ResolveResponsesToolCallShape(ambiguousAlias, mappings));
        Assert.Equal(ProtocolConverter.UndeclaredToolCallErrorCode, error.ErrorCode);
        Assert.Throws<BadRequestException>(() => ProtocolConverter.ConvertRequest(
            RequestWithChoice(request, ambiguousAlias),
            ProtocolConverter.Responses, ProtocolConverter.Chat, "upstream"));

        var converted = ProtocolConverter.ConvertRequest(
            RequestWithChoice(Request(
                Namespace(firstNamespace, Tool(firstName)),
                Namespace(secondNamespace, Tool(secondName))), firstName, firstNamespace),
            ProtocolConverter.Responses, ProtocolConverter.Chat, "upstream");
        Assert.Equal(firstName, Object(Object(converted["tool_choice"])["function"])["name"]);
    }

    [Theory]
    [InlineData("apifox_new_mcp.discoverMethods")]
    [InlineData("mcp.apifox_new_mcp.discoverMethods")]
    [InlineData("mcp__unknown.discoverMethods")]
    [InlineData("mcp__apifox_new_mcp..discoverMethods")]
    public void UnknownQualifiedNames_AreNotSplitOrStrippedToADeclaredTool(string upstreamName)
    {
        var mappings = ProtocolConverter.BuildResponsesToolCallMappings(ApifoxRequest());
        var error = Assert.Throws<UpstreamException>(
            () => ProtocolConverter.ResolveResponsesToolCallShape(upstreamName, mappings));
        Assert.Equal(ProtocolConverter.UndeclaredToolCallErrorCode, error.ErrorCode);
    }

    [Fact]
    public void UndeclaredUpdatePlan_IsStillRejected()
    {
        var request = Request(Tool("update_goal"));
        var mappings = ProtocolConverter.BuildResponsesToolCallMappings(request);
        var error = Assert.Throws<UpstreamException>(
            () => ProtocolConverter.ResolveResponsesToolCallShape("update_plan", mappings));
        Assert.Equal(ProtocolConverter.UndeclaredToolCallErrorCode, error.ErrorCode);
        Assert.False(ProtocolConverter.TryGetResponsesToolCallMapping("update_plan", mappings, out _));
    }

    [Theory]
    [MemberData(nameof(ApifoxNameVectors))]
    public async Task Stream_AssemblesArbitraryNameFragmentsAndRestoresIdentity(string upstreamName)
    {
        foreach (var size in new[] { 1, 3, Math.Max(1, upstreamName.Length / 2), upstreamName.Length })
        {
            var events = await CollectAsync(SseStreamConverter.ChatToResponsesEvents(
                ChatToolNameStream(SplitEvery(upstreamName, size), "{}"),
                "gpt-5",
                new ConvertedStreamResult { ToolCallMappings = ProtocolConverter.BuildResponsesToolCallMappings(ApifoxRequest()) },
                CancellationToken.None));
            var added = Assert.Single(AllByType(ParseEvents(events), "response.output_item.added"));
            var item = Object(added["item"]);
            Assert.Equal(DiscoverMethods, item["name"]);
            Assert.Equal(ApifoxNamespace, item["namespace"]);
        }
    }

    [Fact]
    public async Task Stream_DoesNotCommitAShortPrefixUntilTheLongerNameFinishes()
    {
        var request = Request(Tool("mcp"), Namespace(ApifoxNamespace, Tool(DiscoverMethods)));
        var mappings = ProtocolConverter.BuildResponsesToolCallMappings(request);
        var events = await CollectAsync(SseStreamConverter.ChatToResponsesEvents(
            ChatToolNameStream(["mcp", "__apifox_new_mcp.discoverMethods"], "{\"ok\":true}"),
            "gpt-5",
            new ConvertedStreamResult { ToolCallMappings = mappings },
            CancellationToken.None));
        var added = Assert.Single(AllByType(ParseEvents(events), "response.output_item.added"));
        var item = Object(added["item"]);
        Assert.Equal(DiscoverMethods, item["name"]);
        Assert.Equal(ApifoxNamespace, item["namespace"]);
    }

    [Fact]
    public async Task Stream_CommitsTheShortExactNameOnceArgumentsArrive()
    {
        var request = Request(Tool("mcp"), Namespace(ApifoxNamespace, Tool(DiscoverMethods)));
        var events = await CollectAsync(SseStreamConverter.ChatToResponsesEvents(
            SseLines(
                SseBlock(ChatChunk(toolCalls: new List<object?> { ChatToolCall(0, "call_1", "mcp", "{}") })),
                SseBlock(ChatChunk(finishReason: "tool_calls")),
                SseBlock("[DONE]")),
            "gpt-5",
            new ConvertedStreamResult { ToolCallMappings = ProtocolConverter.BuildResponsesToolCallMappings(request) },
            CancellationToken.None));
        var added = Assert.Single(AllByType(ParseEvents(events), "response.output_item.added"));
        var item = Object(added["item"]);
        Assert.Equal("mcp", item["name"]);
        Assert.False(item.ContainsKey("namespace"));
    }

    [Theory]
    [InlineData(ProtocolConverter.Chat, DiscoverMethods, null)]
    [InlineData(ProtocolConverter.Chat, DotAlias, null)]
    [InlineData(ProtocolConverter.Chat, UnderscoreAlias, null)]
    [InlineData(ProtocolConverter.Chat, DiscoverMethods, ApifoxNamespace)]
    [InlineData(ProtocolConverter.Chat, DotAlias, ApifoxNamespace)]
    [InlineData(ProtocolConverter.Messages, DiscoverMethods, null)]
    [InlineData(ProtocolConverter.Messages, DotAlias, null)]
    [InlineData(ProtocolConverter.Messages, UnderscoreAlias, null)]
    public void ToolChoice_ResolvesNameVectorsToTheDeclaredWireName(
        string protocol, string choiceName, string? namespaceName)
    {
        var request = RequestWithChoice(ApifoxRequest(), choiceName, namespaceName);
        var converted = ProtocolConverter.ConvertRequest(
            request, ProtocolConverter.Responses, protocol, "upstream");
        var choice = Object(converted["tool_choice"]);
        Assert.Equal(DiscoverMethods, protocol == ProtocolConverter.Chat
            ? Object(choice["function"])["name"]
            : choice["name"]);
    }

    [Theory]
    [InlineData(ProtocolConverter.Chat, DiscoverMethods, null)]
    [InlineData(ProtocolConverter.Chat, DotAlias, null)]
    [InlineData(ProtocolConverter.Chat, UnderscoreAlias, null)]
    [InlineData(ProtocolConverter.Chat, DiscoverMethods, ApifoxNamespace)]
    [InlineData(ProtocolConverter.Messages, DotAlias, null)]
    [InlineData(ProtocolConverter.Messages, UnderscoreAlias, ApifoxNamespace)]
    public void History_RewritesNameVectorsToTheDeclaredWireName(
        string protocol, string historyName, string? namespaceName)
    {
        var request = ApifoxRequest();
        request["input"] = HistoryCall(historyName, namespaceName, "{\"query\":\"list\"}");
        var converted = ProtocolConverter.ConvertRequest(
            request, ProtocolConverter.Responses, protocol, "upstream");
        if (protocol == ProtocolConverter.Chat)
        {
            var assistant = List(converted, "messages").Select(Object)
                .First(message => message["role"]?.ToString() == "assistant" && List(message, "tool_calls").Count > 0);
            var call = Object(Assert.Single(List(assistant, "tool_calls")));
            Assert.Equal(DiscoverMethods, Object(call["function"])["name"]);
        }
        else
        {
            var block = List(converted, "messages").Select(Object)
                .SelectMany(message => List(message, "content")).Select(Object)
                .First(item => item["type"]?.ToString() == "tool_use");
            Assert.Equal(DiscoverMethods, block["name"]);
        }
    }

    [Theory]
    [InlineData(ProtocolConverter.Chat)]
    [InlineData(ProtocolConverter.Messages)]
    public void DynamicToolSearchOutput_IsInTheSameAttemptContract(string protocol)
    {
        var request = new Dictionary<string, object?>
        {
            ["model"] = "public",
            ["tools"] = new List<object?> { Tool("update_goal") },
            ["input"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "tool_search_output",
                    ["call_id"] = "search_1",
                    ["tools"] = new List<object?> { Namespace(ApifoxNamespace, Tool(DiscoverMethods)) }
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "function_call",
                    ["call_id"] = "call_1",
                    ["name"] = DotAlias,
                    ["arguments"] = "{}"
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = "call_1",
                    ["output"] = "ok"
                }
            },
            ["tool_choice"] = new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["name"] = UnderscoreAlias
            }
        };

        var attempt = ProtocolConverter.ConvertRequestWithMetadata(
            request, ProtocolConverter.Responses, protocol, "upstream");
        Assert.True(ProtocolConverter.TryGetResponsesToolCallMapping(
            DotAlias, attempt.ToolCallMappings, out var mapping));
        Assert.Equal(DiscoverMethods, mapping.ResponsesName);
        Assert.Equal(ApifoxNamespace, mapping.Namespace);
        Assert.False(ProtocolConverter.TryGetResponsesToolCallMapping(
            "update_plan", attempt.ToolCallMappings, out _));

        var choice = Object(attempt.Request["tool_choice"]);
        Assert.Equal(DiscoverMethods, protocol == ProtocolConverter.Chat
            ? Object(choice["function"])["name"]
            : choice["name"]);

        if (protocol == ProtocolConverter.Chat)
        {
            var assistant = List(attempt.Request, "messages").Select(Object)
                .First(message => message["role"]?.ToString() == "assistant" && List(message, "tool_calls").Count > 0);
            Assert.Equal(DiscoverMethods, Object(Object(Assert.Single(List(assistant, "tool_calls")))["function"])["name"]);
        }
    }

    [Theory]
    [InlineData("discoverMethods", "other")]
    [InlineData("mcp__apifox_new_mcp.discoverMethods", "other")]
    [InlineData("mcp__apifox_new_mcp__discoverMethods", "other")]
    public void ExplicitNamespace_DoesNotFallBackAcrossNamespaces(string name, string namespaceName)
    {
        var request = RequestWithChoice(ApifoxRequest(), name, namespaceName);
        Assert.Throws<BadRequestException>(() => ProtocolConverter.ConvertRequest(
            request, ProtocolConverter.Responses, ProtocolConverter.Chat, "upstream"));

        var mappings = ProtocolConverter.BuildResponsesToolCallMappings(ApifoxRequest());
        Assert.Throws<UpstreamException>(() => ProtocolConverter.ResponsesToolCallItemFromToolCall(
            "call", name, "{}", namespaceValue: namespaceName, mappings: mappings));
    }

    [Fact]
    public void AttemptMappings_DoNotLeakAliasesAcrossRequests()
    {
        var first = ProtocolConverter.ConvertRequestWithMetadata(
            ApifoxRequest(), ProtocolConverter.Responses, ProtocolConverter.Chat, "upstream");
        var secondRequest = Request(Tool("update_goal"));
        var second = ProtocolConverter.ConvertRequestWithMetadata(
            secondRequest, ProtocolConverter.Responses, ProtocolConverter.Chat, "other-upstream");

        Assert.True(ProtocolConverter.TryGetResponsesToolCallMapping(DotAlias, first.ToolCallMappings, out _));
        Assert.False(ProtocolConverter.TryGetResponsesToolCallMapping(DotAlias, second.ToolCallMappings, out _));
        Assert.False(ProtocolConverter.TryGetResponsesToolCallMapping(DiscoverMethods, second.ToolCallMappings, out _));
        Assert.NotSame(first.ToolCallMappings, second.ToolCallMappings);

        ApifoxRequest()["tools"] = new List<object?> { Tool("mutated") };
        Assert.True(ProtocolConverter.TryGetResponsesToolCallMapping(
            UnderscoreAlias, first.ToolCallMappings, out var stillFirst));
        Assert.Equal(ApifoxNamespace, stillFirst.Namespace);
        Assert.DoesNotContain("mutated", first.ToolCallMappings!.Keys);
        Assert.Contains("update_goal", second.ToolCallMappings!.Keys);
    }

    [Fact]
    public void NodeReplEnrichment_UsesResolvedIdentityInsteadOfTheCallString()
    {
        var request = Request(Namespace("mcp__node_repl", Tool("run")));
        request["input"] = HistoryCall("run", "mcp__node_repl", new Dictionary<string, object?>());
        var converted = ProtocolConverter.ConvertRequest(
            request, ProtocolConverter.Responses, ProtocolConverter.Chat, "upstream");
        var assistant = List(converted, "messages").Select(Object)
            .First(message => List(message, "tool_calls").Count > 0);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            Object(Object(Assert.Single(List(assistant, "tool_calls")))["function"])["arguments"]!.ToString()!)!;
        Assert.Equal("use_default", arguments["sandboxPolicy"]?.ToString());
        Assert.Equal("run", Object(Object(Assert.Single(List(assistant, "tool_calls")))["function"])["name"]);

        var stringArgs = Request(Namespace("mcp__node_repl", Tool("run")));
        stringArgs["input"] = HistoryCall("run", "mcp__node_repl", "{}");
        var stringConverted = ProtocolConverter.ConvertRequest(
            stringArgs, ProtocolConverter.Responses, ProtocolConverter.Chat, "upstream");
        var stringAssistant = List(stringConverted, "messages").Select(Object)
            .First(message => List(message, "tool_calls").Count > 0);
        Assert.Equal("{}", Object(Object(Assert.Single(List(stringAssistant, "tool_calls")))["function"])["arguments"]);

        var unrelated = Request(Namespace("mcp__other", Tool("run")));
        unrelated["input"] = HistoryCall("run", "mcp__other", new Dictionary<string, object?>());
        var other = ProtocolConverter.ConvertRequest(
            unrelated, ProtocolConverter.Responses, ProtocolConverter.Chat, "upstream");
        var otherAssistant = List(other, "messages").Select(Object)
            .First(message => List(message, "tool_calls").Count > 0);
        var otherArguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            Object(Object(Assert.Single(List(otherAssistant, "tool_calls")))["function"])["arguments"]!.ToString()!)!;
        Assert.False(otherArguments.ContainsKey("sandboxPolicy"));
    }

    [Theory]
    [MemberData(nameof(ApifoxNameVectors))]
    public void NativeRestoreDeclaredToolIdentity_RestoresLeafAndNamespaceFromWireOrAlias(string upstreamName)
    {
        var mappings = ProtocolConverter.BuildResponsesToolCallMappings(ApifoxRequest());
        var item = new Dictionary<string, object?>
        {
            ["type"] = "function_call",
            ["name"] = upstreamName
        };
        NativeClientResponseExecutor.RestoreDeclaredToolIdentity(item, mappings);
        Assert.Equal(DiscoverMethods, item["name"]);
        Assert.Equal(ApifoxNamespace, item["namespace"]);
    }

    [Fact]
    public void NativeRestoreDeclaredToolIdentity_DoesNotRewriteExistingNamespaceOrAmbiguousAlias()
    {
        var request = Request(
            Namespace("foo.bar", Tool("baz")),
            Namespace("foo", Tool("bar.baz")));
        var mappings = ProtocolConverter.BuildResponsesToolCallMappings(request);
        var alreadyNamespaced = new Dictionary<string, object?>
        {
            ["type"] = "function_call",
            ["name"] = "baz",
            ["namespace"] = "other"
        };
        NativeClientResponseExecutor.RestoreDeclaredToolIdentity(alreadyNamespaced, mappings);
        Assert.Equal("baz", alreadyNamespaced["name"]);
        Assert.Equal("other", alreadyNamespaced["namespace"]);

        var ambiguous = new Dictionary<string, object?>
        {
            ["type"] = "function_call",
            ["name"] = "foo.bar.baz"
        };
        NativeClientResponseExecutor.RestoreDeclaredToolIdentity(ambiguous, mappings);
        Assert.Equal("foo.bar.baz", ambiguous["name"]);
        Assert.False(ambiguous.ContainsKey("namespace"));
    }

    [Theory]
    [MemberData(nameof(ApifoxNameVectors))]
    public void ConvertedChatResponse_RestoresLeafNameAndNamespace(string upstreamName)
    {
        var upstream = new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl-1",
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["tool_calls"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["id"] = "call_1",
                                ["type"] = "function",
                                ["function"] = new Dictionary<string, object?>
                                {
                                    ["name"] = upstreamName,
                                    ["arguments"] = "{}"
                                }
                            }
                        }
                    },
                    ["finish_reason"] = "tool_calls"
                }
            }
        };
        var response = ProtocolConverter.ConvertResponse(
            upstream,
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "test",
            toolCallMappings: ProtocolConverter.BuildResponsesToolCallMappings(ApifoxRequest()));
        var call = Object(Assert.Single(List(response, "output")));
        Assert.Equal(DiscoverMethods, call["name"]);
        Assert.Equal(ApifoxNamespace, call["namespace"]);
    }

    private static Dictionary<string, object?> ApifoxRequest() =>
        Request(Namespace(ApifoxNamespace, Tool(DiscoverMethods)));

    private static Dictionary<string, object?> Request(params Dictionary<string, object?>[] tools) => new()
    {
        ["model"] = "public",
        ["input"] = "hello",
        ["tools"] = tools.Cast<object?>().ToList()
    };

    private static Dictionary<string, object?> RequestWithChoice(
        Dictionary<string, object?> request, string name, string? namespaceName = null)
    {
        var choice = new Dictionary<string, object?> { ["type"] = "function", ["name"] = name };
        if (namespaceName is not null)
        {
            choice["namespace"] = namespaceName;
        }

        request["tool_choice"] = choice;
        return request;
    }

    private static Dictionary<string, object?> Tool(string name) => new()
    {
        ["type"] = "function",
        ["name"] = name,
        ["parameters"] = new Dictionary<string, object?> { ["type"] = "object" }
    };

    private static Dictionary<string, object?> Namespace(string name, Dictionary<string, object?> tool) => new()
    {
        ["type"] = "namespace",
        ["name"] = name,
        ["tools"] = new List<object?> { tool }
    };

    private static List<object?> HistoryCall(string name, string? namespaceName, object? arguments)
    {
        var call = new Dictionary<string, object?>
        {
            ["type"] = "function_call",
            ["call_id"] = "call_1",
            ["name"] = name,
            ["arguments"] = arguments
        };
        if (namespaceName is not null)
        {
            call["namespace"] = namespaceName;
        }

        return
        [
            call,
            new Dictionary<string, object?>
            {
                ["type"] = "function_call_output",
                ["call_id"] = "call_1",
                ["output"] = "ok"
            }
        ];
    }

    private static IAsyncEnumerable<string> ChatToolNameStream(IReadOnlyList<string> nameParts, string arguments)
    {
        var blocks = new List<string>
        {
            SseBlock(ChatChunk(toolCalls: new List<object?> { ChatToolCall(0, "call_1", nameParts[0]) }))
        };
        foreach (var part in nameParts.Skip(1))
        {
            blocks.Add(SseBlock(ChatChunk(toolCalls: new List<object?> { ChatToolCall(0, name: part) })));
        }

        blocks.Add(SseBlock(ChatChunk(toolCalls: new List<object?> { ChatToolCall(0, arguments: arguments) })));
        blocks.Add(SseBlock(ChatChunk(finishReason: "tool_calls")));
        blocks.Add(SseBlock("[DONE]"));
        return SseLines([.. blocks]);
    }

    private static async IAsyncEnumerable<string> SseLines(params string[] sseBlocks)
    {
        foreach (var block in sseBlocks)
        {
            foreach (var line in block.Split('\n'))
            {
                yield return line;
            }
        }

        await Task.CompletedTask;
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> enumerable)
    {
        var list = new List<string>();
        await foreach (var line in enumerable)
        {
            list.Add(line);
        }

        return list;
    }

    private static string SseBlock(string data)
    {
        return $"data: {data}\n\n";
    }

    private static string ChatChunk(object? toolCalls = null, string? finishReason = null)
    {
        var delta = new Dictionary<string, object?>();
        if (toolCalls is not null)
        {
            delta["tool_calls"] = toolCalls;
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl-1",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1700000000,
            ["model"] = "gpt-5",
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["finish_reason"] = finishReason
                }
            }
        });
    }

    private static Dictionary<string, object?> ChatToolCall(
        int index, string? id = null, string? name = null, string? arguments = null)
    {
        var function = new Dictionary<string, object?>();
        if (name is not null)
        {
            function["name"] = name;
        }

        if (arguments is not null)
        {
            function["arguments"] = arguments;
        }

        var toolCall = new Dictionary<string, object?>
        {
            ["index"] = index,
            ["function"] = function
        };
        if (id is not null)
        {
            toolCall["id"] = id;
        }

        if (id is not null || name is not null)
        {
            toolCall["type"] = "function";
        }

        return toolCall;
    }

    private static IReadOnlyList<string> SplitEvery(string value, int size)
    {
        var parts = new List<string>();
        for (var index = 0; index < value.Length; index += size)
        {
            parts.Add(value.Substring(index, Math.Min(size, value.Length - index)));
        }

        return parts;
    }

    private static List<Dictionary<string, object?>> ParseEvents(List<string> lines)
    {
        var events = new List<Dictionary<string, object?>>();
        foreach (var line in lines)
        {
            if (!line.Contains("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var idx = line.IndexOf("data: ", StringComparison.Ordinal);
            var json = line[(idx + "data: ".Length)..].TrimEnd('\n');
            if (json is "[DONE]" or "")
            {
                continue;
            }

            using var doc = JsonDocument.Parse(json);
            events.Add(doc.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => ElementToObject(property.Value), StringComparer.Ordinal));
        }

        return events;
    }

    private static object? ElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(property => property.Name, property => ElementToObject(property.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(ElementToObject).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var number) ? number : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static List<Dictionary<string, object?>> AllByType(List<Dictionary<string, object?>> events, string type) =>
        events.Where(entry => entry["type"]?.ToString() == type).ToList();

    private static List<string?> DeclaredChatToolNames(Dictionary<string, object?> request) =>
        List(request, "tools").Select(Object).Select(tool => Object(tool["function"])["name"]?.ToString()).ToList();

    private static string? SingleChatToolName(Dictionary<string, object?> request) =>
        Assert.Single(DeclaredChatToolNames(request));

    private static Dictionary<string, object?> Object(object? value) =>
        Assert.IsType<Dictionary<string, object?>>(value);

    private static List<object?> List(Dictionary<string, object?> value, string key) =>
        value.TryGetValue(key, out var list) ? Assert.IsType<List<object?>>(list) : [];
}
