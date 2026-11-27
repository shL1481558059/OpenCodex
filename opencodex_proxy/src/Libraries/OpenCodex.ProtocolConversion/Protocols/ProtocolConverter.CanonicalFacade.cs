using OpenCodex.ProtocolConversion.Errors;
using OpenCodex.ProtocolConversion.Streaming;

namespace OpenCodex.ProtocolConversion;

public static partial class ProtocolConverter
{
    internal static Dictionary<string, object?> ToFacadeCanonicalRequest(
        Dictionary<string, object?> payload, string protocol, IReadOnlyDictionary<string, object?>? compat)
    {
        var owned = AsObject(DeepCopy(payload));
        var canonical = ToCanonicalRequest(owned, protocol, compat, null, null);
        var declaredNames = BuildResponsesToolWireNamePlan(ListValue(canonical, "tools"));
        if (protocol != Responses)
        {
            foreach (var message in ListValue(canonical, "messages").OfType<Dictionary<string, object?>>())
            foreach (var call in ListValue(message, "tool_calls").OfType<Dictionary<string, object?>>())
            {
                var function = ObjectValue(call, "function");
                if (declaredNames.TryResolveInbound(GetString(function, "name")!, GetString(call, "namespace"), out var name))
                {
                    function["name"] = name.ResponsesName;
                    call["namespace"] = name.Namespace;
                }
            }
            return canonical;
        }

        var invocations = ListValue(owned, "input").OfType<Dictionary<string, object?>>()
            .Where(IsResponsesToolCallLike)
            .ToDictionary(item => Convert.ToString(GetValue(item, "call_id") ?? GetValue(item, "id"))!, StringComparer.Ordinal);
        foreach (var message in ListValue(canonical, "messages").OfType<Dictionary<string, object?>>())
        {
            foreach (var call in ListValue(message, "tool_calls").OfType<Dictionary<string, object?>>())
            {
                if (!invocations.TryGetValue(Convert.ToString(call["id"])!, out var invocation)) continue;
                var type = GetString(invocation, "type")!;
                var function = ObjectValue(call, "function");
                function["name"] = ResponsesToolCallName(invocation);
                call["namespace"] = GetValue(invocation, "namespace");
                call["native_type"] = type == "custom_tool_call" ? "custom" : type[..^"_call".Length];
                if (type == "custom_tool_call")
                {
                    if (GetValue(invocation, "input") is not string input)
                        throw new BadRequestException("A custom tool call requires its raw string input.");
                    function["arguments"] = input;
                }
                else if (type == "function_call")
                {
                    var arguments = ResponsesToolCallArguments(invocation);
                    function["arguments"] = arguments is string text ? text : JsonDumps(arguments);
                }
            }
        }
        return canonical;
    }

    internal static RequestConversionResult ConvertFacadeCanonicalRequest(
        Dictionary<string, object?> canonical, string? sourceProtocol, string targetProtocol,
        string upstreamModel, IReadOnlyDictionary<string, object?>? compat)
    {
        ValidateCanonicalRequestSemanticCompatibility(ObjectValue(canonical, "params"), sourceProtocol, targetProtocol);
        canonical["model"] = upstreamModel;
        if (sourceProtocol == Chat && targetProtocol == Chat)
        {
            var samePlan = BuildResponsesToolWireNamePlan(ListValue(canonical, "tools"));
            foreach (var message in ListValue(canonical, "messages").OfType<Dictionary<string, object?>>())
            foreach (var call in ListValue(message, "tool_calls").OfType<Dictionary<string, object?>>())
            {
                var function = ObjectValue(call, "function");
                if (samePlan.TryResolveInbound(GetString(function, "name")!, GetString(call, "namespace"), out var name))
                    function["name"] = name.WireName;
            }
            var same = FromCanonicalRequest(canonical, Chat, samePlan, preserveExtensions: true);
            var originalMessages = ListValue(canonical, "messages");
            var projectedMessages = ListValue(same, "messages");
            for (var index = 0; index < originalMessages.Count; index++)
            {
                var original = (Dictionary<string, object?>)originalMessages[index]!;
                var projected = (Dictionary<string, object?>)projectedMessages[index]!;
                foreach (var (key, value) in original) projected.TryAdd(key, value);
            }
            SanitizeRequestToolSchemas(same, Chat);
            SanitizeRequestToolChoiceConsistency(same);
            return new RequestConversionResult(same, samePlan.Mappings);
        }

        // Responses is a deliberate lossless tool bridge, never a guessed source identity.
        // Reuse the existing declaration/history/name-plan/compat pipeline after origin validation.
        var bridge = FromCanonicalRequest(canonical, Responses, null, preserveExtensions: true);
        RestoreFacadeResponsesInvocations(bridge, canonical);
        if (canonical.GetValueOrDefault("tool_choice") is Dictionary<string, object?> choice
            && bridge.GetValueOrDefault("tool_choice") is Dictionary<string, object?> projectedChoice
            && choice.TryGetValue("namespace", out var choiceNamespace))
            projectedChoice["namespace"] = choiceNamespace;
        if (targetProtocol == Responses)
        {
            SanitizeRequestToolChoiceConsistency(bridge);
            return new RequestConversionResult(bridge, null);
        }

        var tools = ResponsesRequestToolsToCanonical(bridge, compat);
        var plan = BuildResponsesToolWireNamePlan(tools);
        var normalized = ToCanonicalRequest(bridge, Responses, compat, plan, tools);
        var result = FromCanonicalRequest(normalized, targetProtocol, plan, sourceProtocol == targetProtocol);
        SanitizeRequestToolChoiceConsistency(result);
        return new RequestConversionResult(result, plan.Mappings);
    }

    private static void RestoreFacadeResponsesInvocations(
        Dictionary<string, object?> responseRequest, Dictionary<string, object?> canonical)
    {
        var names = BuildResponsesToolWireNamePlan(ListValue(canonical, "tools"));
        var calls = ListValue(canonical, "messages").OfType<Dictionary<string, object?>>()
            .SelectMany(message => ListValue(message, "tool_calls").OfType<Dictionary<string, object?>>())
            .ToDictionary(call => Convert.ToString(call["id"])!, StringComparer.Ordinal);
        foreach (var item in ListValue(responseRequest, "input").OfType<Dictionary<string, object?>>())
        {
            var type = GetString(item, "type");
            if (type is not ("function_call" or "function_call_output")) continue;
            if (!calls.TryGetValue(Convert.ToString(item["call_id"])!, out var call)) continue;
            if (type == "function_call")
            {
                var function = ObjectValue(call, "function");
                item["name"] = function["name"];
                if (HasNonNullValue(call, "namespace")) item["namespace"] = call["namespace"];
                if (names.TryResolveInbound(GetString(function, "name")!, GetString(call, "namespace"), out var name))
                {
                    item["name"] = name.ResponsesName;
                    if (!string.IsNullOrEmpty(name.Namespace)) item["namespace"] = name.Namespace;
                }
                if (GetString(call, "native_type") == "custom")
                {
                    item["type"] = "custom_tool_call";
                    item["input"] = function["arguments"];
                    item.Remove("arguments");
                }
            }
            else if (GetString(call, "native_type") == "custom") item["type"] = "custom_tool_call_output";
        }
    }
}
