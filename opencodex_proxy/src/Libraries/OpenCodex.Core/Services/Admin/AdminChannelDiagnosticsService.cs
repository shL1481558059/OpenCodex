using System.Diagnostics;
using OpenCodex.Core.Config;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs.AdminChannelDiagnostics;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Admin;

namespace OpenCodex.Core.Services.Admin;

public sealed partial class AdminChannelDiagnosticsService : IAdminChannelDiagnosticsService
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IUpstreamClient _upstreamClient;
    private readonly IUpstreamModelClient _upstreamModelClient;

    public AdminChannelDiagnosticsService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IUpstreamClient upstreamClient,
        IUpstreamModelClient upstreamModelClient)
    {
        _settingsProvider = settingsProvider;
        _upstreamClient = upstreamClient;
        _upstreamModelClient = upstreamModelClient;
    }

    public async Task<ApiResult<DiscoverModelsResponse>> DiscoverModelsAsync(
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
            return ApiResult.Success(DiscoverModelsResponse.From(
                ExtractModelIds(raw),
                raw,
                ElapsedMilliseconds(started)));
        }
        catch (ConfigException exception)
        {
            return ApiResult.Fail<DiscoverModelsResponse>(
                AdminChannelDiagnosticsErrorCodes.Validation,
                exception.Message);
        }
        catch (UpstreamException exception)
        {
            return ApiResult.Fail<DiscoverModelsResponse>(
                ErrorCode(exception.StatusCode),
                exception.Message);
        }
    }

    public async Task<ApiResult<TestChannelResponse>> TestChannelAsync(
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

            return ApiResult.Success(TestChannelResponse.From(
                originalModel,
                upstreamModel,
                compatDetails,
                responsePayload,
                ElapsedMilliseconds(started)));
        }
        catch (ConfigException exception)
        {
            return ApiResult.Fail<TestChannelResponse>(
                AdminChannelDiagnosticsErrorCodes.Validation,
                exception.Message);
        }
        catch (ProxyException exception)
        {
            return ApiResult.Fail<TestChannelResponse>(
                ErrorCode(exception.StatusCode),
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

    private static int ErrorCode(int statusCode)
    {
        return (statusCode * 1000) + 301;
    }

    private static int ElapsedMilliseconds(long started)
    {
        return (int)Math.Round(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            MidpointRounding.AwayFromZero);
    }
}
