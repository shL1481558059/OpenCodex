using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public interface IProxyLogRepository
{
    UsageRecord ExtractUsage(
        IReadOnlyDictionary<string, object?> response,
        string protocol);

    double CalculateCost(
        string model,
        int inputTokens,
        int cachedTokens,
        int outputTokens);

    long WriteRequestLog(RequestLogWriteRecord record);
}
