namespace OpenCodex.Core.Protocols;

public static partial class SseStreamConverter
{
    internal enum ResponsesStreamEventKind
    {
        Created,
        InProgress,
        OutputItemAdded,
        OutputTextDelta,
        RefusalDelta,
        RefusalDone,
        AnnotationAdded,
        ReasoningDelta,
        ToolArgumentsDelta,
        CustomToolInputDelta,
        OutputItemDone,
        Completed,
        Incomplete,
        Failed,
        Other
    }

    internal sealed class ResponsesStreamEvent
    {
        private ResponsesStreamEvent(
            Dictionary<string, object?> payload,
            string type,
            ResponsesStreamEventKind kind)
        {
            Payload = payload;
            Type = type;
            Kind = kind;
        }

        public Dictionary<string, object?> Payload { get; }
        public string Type { get; }
        public ResponsesStreamEventKind Kind { get; }
        public int OutputIndex => ToInt(GetValue(Payload, "output_index"));
        public string Delta => StringValue(Payload, "delta", string.Empty);
        public string Refusal => StringValue(Payload, "refusal", string.Empty);
        public object? Annotation => GetValue(Payload, "annotation");

        public bool TryGetResponse(out Dictionary<string, object?> response)
        {
            return TryAsObject(GetValue(Payload, "response"), out response);
        }

        public bool TryGetItem(out Dictionary<string, object?> item)
        {
            return TryAsObject(GetValue(Payload, "item"), out item);
        }

        public static bool TryCreate(SseEvent sseEvent, out ResponsesStreamEvent streamEvent)
        {
            if (!TryAsObject(sseEvent.Data, out var payload))
            {
                streamEvent = null!;
                return false;
            }

            var type = StringValue(payload, "type", sseEvent.EventName);
            streamEvent = new ResponsesStreamEvent(payload, type, Classify(type));
            return true;
        }

        private static ResponsesStreamEventKind Classify(string type)
        {
            if (type == "response.created") return ResponsesStreamEventKind.Created;
            if (type == "response.in_progress") return ResponsesStreamEventKind.InProgress;
            if (type == "response.output_item.added") return ResponsesStreamEventKind.OutputItemAdded;
            if (type == "response.output_text.delta") return ResponsesStreamEventKind.OutputTextDelta;
            if (type == "response.refusal.delta") return ResponsesStreamEventKind.RefusalDelta;
            if (type == "response.refusal.done") return ResponsesStreamEventKind.RefusalDone;
            if (type == "response.output_text.annotation.added") return ResponsesStreamEventKind.AnnotationAdded;
            if (type == "response.reasoning_summary_text.delta") return ResponsesStreamEventKind.ReasoningDelta;
            if (type == "response.function_call_arguments.delta"
                || type.EndsWith("_call.arguments.delta", StringComparison.Ordinal)
                || type.EndsWith("_call_arguments.delta", StringComparison.Ordinal))
            {
                return ResponsesStreamEventKind.ToolArgumentsDelta;
            }
            if (type == "response.custom_tool_call_input.delta") return ResponsesStreamEventKind.CustomToolInputDelta;
            if (type == "response.output_item.done") return ResponsesStreamEventKind.OutputItemDone;
            if (type == "response.completed") return ResponsesStreamEventKind.Completed;
            if (type == "response.incomplete") return ResponsesStreamEventKind.Incomplete;
            if (type == "response.failed") return ResponsesStreamEventKind.Failed;
            return ResponsesStreamEventKind.Other;
        }
    }
}
