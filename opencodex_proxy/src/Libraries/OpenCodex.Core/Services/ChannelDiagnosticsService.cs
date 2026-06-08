using System.Diagnostics;
using OpenCodex.Core.Config;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs.ChannelDiagnostics;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

public sealed partial class ChannelDiagnosticsService : IChannelDiagnosticsService
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IUpstreamClient _upstreamClient;
    private readonly IUpstreamModelClient _upstreamModelClient;

    public ChannelDiagnosticsService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IUpstreamClient upstreamClient,
        IUpstreamModelClient upstreamModelClient)
    {
        _settingsProvider = settingsProvider;
        _upstreamClient = upstreamClient;
        _upstreamModelClient = upstreamModelClient;
    }

    public async Task<ApiOpResult<DiscoverModelsResponse>> DiscoverModelsAsync(
        IReadOnlyDictionary<string, object?> body,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var raw = await _upstreamModelClient.ListModelsAsync(
                DraftChannelFromBody(body),
                DefaultTimeout(),
                cancellationToken);
            return ApiOpResult<DiscoverModelsResponse>.Succeed(DiscoverModelsResponse.From(
                ExtractModelIds(raw),
                raw,
                ElapsedMilliseconds(started)));
        }
        catch (ConfigException exception)
        {
            return ApiOpResult<DiscoverModelsResponse>.Fail(400, exception.Message);
        }
        catch (UpstreamException exception)
        {
            return ApiOpResult<DiscoverModelsResponse>.Fail(
                exception.StatusCode,
                exception.Message);
        }
    }

    public async Task<ApiOpResult<TestChannelResponse>> TestChannelAsync(
        IReadOnlyDictionary<string, object?> body,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var (channel, payload) = ParseTestChannelBody(body);
            var (originalModel, upstreamModel) = TestModels(channel, JsonDictionaryValue.Get(payload, "model"));
            var upstreamRequest = ProtocolConverter.ConvertRequest(
                payload,
                JsonDictionaryValue.String(channel, "type"),
                JsonDictionaryValue.String(channel, "type"),
                upstreamModel);
            var (compatibleRequest, compatDetails) = ApplyCompat(
                upstreamRequest,
                JsonDictionaryValue.Object(channel, "compat", CloneObject));
            var upstreamResponse = await _upstreamClient.PostJsonAsync(
                channel,
                compatibleRequest,
                DefaultTimeout(),
                cancellationToken);
            var responsePayload = ProtocolConverter.ConvertResponse(
                upstreamResponse,
                JsonDictionaryValue.String(channel, "type"),
                JsonDictionaryValue.String(channel, "type"),
                originalModel);

            return ApiOpResult<TestChannelResponse>.Succeed(TestChannelResponse.From(
                originalModel,
                upstreamModel,
                compatDetails,
                responsePayload,
                ElapsedMilliseconds(started)));
        }
        catch (ConfigException exception)
        {
            return ApiOpResult<TestChannelResponse>.Fail(400, exception.Message);
        }
        catch (ProxyException exception)
        {
            return ApiOpResult<TestChannelResponse>.Fail(
                exception.StatusCode,
                exception.Message);
        }
    }

    private static (string OriginalModel, string UpstreamModel) TestModels(
        IReadOnlyDictionary<string, object?> channel,
        object? model)
    {
        var originalModel = (model?.ToString() ?? string.Empty).Trim();
        foreach (var item in JsonDictionaryValue.List(channel, "models"))
        {
            if (item is not IReadOnlyDictionary<string, object?> mapping)
            {
                continue;
            }

            if (JsonDictionaryValue.String(mapping, "model") == originalModel)
            {
                var upstreamModel = JsonDictionaryValue.String(mapping, "upstream_model");
                return (originalModel, upstreamModel.Length == 0 ? originalModel : upstreamModel);
            }
        }

        return (originalModel, originalModel);
    }

    private static List<string> ExtractModelIds(IReadOnlyDictionary<string, object?> raw)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in JsonDictionaryValue.List(raw, "data"))
        {
            if (item is not IReadOnlyDictionary<string, object?> model)
            {
                continue;
            }

            var modelId = JsonDictionaryValue.String(model, "id");
            if (modelId.Length > 0 && seen.Add(modelId))
            {
                ids.Add(modelId);
            }
        }

        return ids;
    }

    private int DefaultTimeout()
    {
        return _settingsProvider.GetSettings().DefaultTimeout;
    }

    private static int ElapsedMilliseconds(long started)
    {
        return (int)Math.Round(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            MidpointRounding.AwayFromZero);
    }
}
