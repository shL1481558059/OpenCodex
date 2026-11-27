using OpenCodex.ProtocolConversion.Models;
using OpenCodex.ProtocolConversion.Errors;
using System.Text.Json;

namespace OpenCodex.ProtocolConversion;

public sealed class ProtocolConversionFacade
{
    public CanonicalRequest ToCanonicalRequest(
        Dictionary<string, object?> payload,
        ProtocolKind sourceProtocol,
        IReadOnlyDictionary<string, object?>? compat = null)
    {
        var graph = ProtocolConverter.ToFacadeCanonicalRequest(payload, sourceProtocol.ToWireValue(), compat);
        return CanonicalModelMapper.FromRequestGraph(graph, sourceProtocol);
    }

    public CanonicalResponse ToCanonicalResponse(
        Dictionary<string, object?> payload,
        ProtocolKind sourceProtocol,
        string? originalModel = null)
    {
        var graph = ProtocolConverter.ToCanonicalResponse(
            payload,
            sourceProtocol.ToWireValue(),
            originalModel,
            null);
        return CanonicalModelMapper.FromResponseGraph(graph, sourceProtocol, payload);
    }

    public Dictionary<string, object?> ConvertRequest(
        CanonicalRequest request,
        ProtocolKind targetProtocol,
        string upstreamModel,
        IReadOnlyDictionary<string, object?>? compat = null)
    {
        return ConvertRequestWithMetadata(request, targetProtocol, upstreamModel, compat).Request;
    }

    public RequestConversionResult ConvertRequestWithMetadata(
        CanonicalRequest request,
        ProtocolKind targetProtocol,
        string upstreamModel,
        IReadOnlyDictionary<string, object?>? compat = null)
    {
        return ProtocolConverter.ConvertFacadeCanonicalRequest(
            CanonicalModelMapper.ToRequestGraph(request), request.SourceProtocol?.ToWireValue(),
            targetProtocol.ToWireValue(), upstreamModel, compat);
    }

    public Dictionary<string, object?> ConvertResponse(
        CanonicalResponse response,
        ProtocolKind targetProtocol,
        ProtocolKind? sourceProtocol = null)
    {
        if (sourceProtocol is not null && response.SourceProtocol is not null && sourceProtocol != response.SourceProtocol)
            throw new BadRequestException("The explicit response source protocol conflicts with CanonicalResponse.SourceProtocol.");
        var source = response.SourceProtocol ?? sourceProtocol;
        if (response.Raw is not null)
        {
            if (source is null || response.Raw.Value.ValueKind != JsonValueKind.Object)
                throw new BadRequestException("A raw response requires an explicit source protocol and a JSON object snapshot.");
            if (source == targetProtocol) return CanonicalModelMapper.ToSameProtocolResponseGraph(response, source.Value);
        }
        if (targetProtocol == ProtocolKind.Responses) CanonicalModelMapper.ValidateResponseStatusForRendering(response);
        return ProtocolConverter.FromCanonicalResponse(
            CanonicalModelMapper.ToResponseGraph(response), targetProtocol.ToWireValue());
    }
}
