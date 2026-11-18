namespace OpenCodex.Core.Protocols;

public static partial class SseStreamConverter
{
    internal sealed class ResponsesStreamState
    {
        private readonly string? _clientModel;
        private readonly ConvertedStreamResult _result;

        public ResponsesStreamState(string? clientModel, ConvertedStreamResult result)
        {
            _clientModel = clientModel;
            _result = result;
            ResponseModel = clientModel ?? string.Empty;
            ResponseId = $"resp_{Guid.NewGuid():N}";
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            TextParts = result.CreateTextParts();
            RefusalParts = result.CreateTextParts();
            ReasoningParts = result.CreateTextParts();
            Accumulator = result.SharedCapture?.Accumulator as ResponsesStreamResponseAccumulator
                ?? new ResponsesStreamResponseAccumulator(new StreamCaptureBudget(int.MaxValue, int.MaxValue));
        }

    public string ResponseModel { get; private set; }
    public string UpstreamResponseModel { get; private set; } = string.Empty;
    public long CreatedAt { get; }
    public string ResponseId { get; private set; }
    public string FinishStatus { get; private set; } = "completed";
    public Dictionary<string, object?> Usage { get; private set; } = new(StringComparer.Ordinal);
    public ConverterText TextParts { get; }
    public ConverterText RefusalParts { get; }
    public ConverterText ReasoningParts { get; }
    public List<object?> Annotations { get; } = [];
    public SortedDictionary<int, Dictionary<string, object?>> OutputByIndex { get; } = [];
    public ResponsesStreamResponseAccumulator Accumulator { get; }
    public string FinalResponseModel => UpstreamResponseModel.Length > 0 ? UpstreamResponseModel : ResponseModel;

    public void Accept(SseEvent sseEvent)
    {
        if (_result.SharedCapture is null)
        {
            Accumulator.Accept(sseEvent);
        }
    }

    public void ApplyResponse(Dictionary<string, object?> response)
    {
        ResponseId = StringValue(response, "id", ResponseId);
        UpstreamResponseModel = StringValue(response, "model", UpstreamResponseModel);
        ResponseModel = _clientModel ?? UpstreamResponseModel;
        if (TryAsObject(GetValue(response, "usage"), out var responseUsage))
        {
            Usage = responseUsage;
        }
    }

    public void ApplyCapturedResponse(Dictionary<string, object?> capturedResponse)
    {
        ResponseId = StringValue(capturedResponse, "id", ResponseId);
        UpstreamResponseModel = StringValue(capturedResponse, "model", UpstreamResponseModel);
        ResponseModel = _clientModel ?? UpstreamResponseModel;
    }

    public void ApplyTerminalResponse(
        Dictionary<string, object?> payload,
        string eventType,
        Func<int, bool> hasToolState,
        string targetDescription)
    {
        if (!TryAsObject(GetValue(payload, "response"), out var response))
        {
            return;
        }

        ApplyResponse(response);
        FinishStatus = StringValue(
            response,
            "status",
            eventType == "response.incomplete" ? "incomplete" : FinishStatus);

        if (!TryAsList(GetValue(response, "output"), out var responseOutput))
        {
            return;
        }

        OutputByIndex.Clear();
        for (var index = 0; index < responseOutput.Count; index++)
        {
            if (!TryAsObject(responseOutput[index], out var outputItem))
            {
                continue;
            }

            var itemType = StringValue(outputItem, "type", string.Empty);
            if ((IsNativeMcpItemType(itemType)
                    || (IsResponsesNativeToolCallType(itemType)
                        && !IsServerExecutedNativeToolCallType(itemType)
                        && !IsServerExecutedToolSearchCall(outputItem)))
                && !hasToolState(index))
            {
                throw new InvalidOperationException(
                    $"Responses terminal output item '{itemType}' has no compatible {targetDescription} representation.");
            }

            OutputByIndex[index] = new Dictionary<string, object?>(outputItem, StringComparer.Ordinal);
        }
    }

    public void AddFallbackOutputItems()
    {
        var combinedText = TextParts.ToString();
        var combinedReasoning = ReasoningParts.ToString();

        if ((combinedText.Length > 0 || RefusalParts.Count > 0)
            && !OutputByIndex.Values.Any(item => StringValue(item, "type", string.Empty) == "message"))
        {
            var messageIndex = OutputByIndex.Count > 0 ? OutputByIndex.Keys.Max() + 1 : 0;
            OutputByIndex[messageIndex] = new Dictionary<string, object?>
            {
                ["id"] = $"msg_{Guid.NewGuid():N}",
                ["type"] = "message",
                ["status"] = "completed",
                ["role"] = "assistant",
                ["content"] = BuildResponsesMessageContent(combinedText, RefusalParts, Annotations)
            };
        }

        if (combinedReasoning.Length > 0
            && !OutputByIndex.Values.Any(item => StringValue(item, "type", string.Empty) == "reasoning"))
        {
            var reasoningIndex = OutputByIndex.Count > 0 ? OutputByIndex.Keys.Max() + 1 : 0;
            OutputByIndex[reasoningIndex] = new Dictionary<string, object?>
            {
                ["id"] = $"rs_{Guid.NewGuid():N}",
                ["type"] = "reasoning",
                ["status"] = "completed",
                ["summary"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "summary_text",
                        ["text"] = combinedReasoning
                    }
                },
                ["encrypted_content"] = combinedReasoning
            };
        }
    }

        public Dictionary<string, object?> BuildCapturedResponse(
            string eventType,
            string status,
            bool preserveCapturedOutputAndUsage = false)
        {
            return BuildCapturedResponsesUpstreamResponse(
                Accumulator,
                eventType,
                ResponseId,
                CreatedAt,
                status,
                FinalResponseModel,
                OutputByIndex.Values,
                Usage,
                preserveCapturedOutputAndUsage);
        }
    }
}
