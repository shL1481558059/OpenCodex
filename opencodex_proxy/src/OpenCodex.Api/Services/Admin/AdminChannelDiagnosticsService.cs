using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Protocols;

namespace OpenCodex.Api.Services;

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

    public async Task<AdminDiscoverModelsResult> DiscoverModelsAsync(
        IReadOnlyDictionary<string, object?> body,
        CancellationToken cancellationToken)
    {
        var raw = await _upstreamModelClient.ListModelsAsync(
            DraftChannelFromBody(body),
            DefaultTimeout(),
            cancellationToken);
        return new AdminDiscoverModelsResult(ExtractModelIds(raw), raw);
    }

    public async Task<AdminChannelTestResult> TestChannelAsync(
        IReadOnlyDictionary<string, object?> body,
        CancellationToken cancellationToken)
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

        return new AdminChannelTestResult(
            originalModel,
            upstreamModel,
            compatDetails,
            responsePayload);
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

}
