using System.Globalization;

namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    private static double? ParseTimestamp(object? value)
    {
        if (IsEmptyLogFilterValue(value) || !TryConvertDouble(value, out var parsed))
        {
            return null;
        }

        if (parsed > 10_000_000_000)
        {
            parsed /= 1000;
        }

        return parsed > 0 ? parsed : null;
    }

    private static string TimestampToIso(double timestamp)
    {
        var milliseconds = (long)Math.Floor(timestamp * 1000);
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            .ToLocalTime()
            .ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static int CachedTokensFromNestedDetails(
        IReadOnlyDictionary<string, object?> usage,
        string detailsKey)
    {
        return TryAsObject(GetOptionalValue(usage, detailsKey), out var details)
            ? ToInt(GetOptionalValue(details, "cached_tokens"))
            : 0;
    }

    private static int ChatCachedTokens(IReadOnlyDictionary<string, object?> usage)
    {
        if (TryAsObject(GetOptionalValue(usage, "prompt_tokens_details"), out var promptDetails)
            && promptDetails.Count > 0)
        {
            return ToInt(GetOptionalValue(promptDetails, "cached_tokens"));
        }

        return TryAsObject(GetOptionalValue(usage, "input_tokens_details"), out var inputDetails)
            ? ToInt(GetOptionalValue(inputDetails, "cached_tokens"))
            : 0;
    }

    private static string RequestStatus(int? statusCode, string? error)
    {
        var status = statusCode ?? 0;
        return status >= 400 || !string.IsNullOrWhiteSpace(error) ? "failed" : "success";
    }

    private static int TimeoutValue(object? value, int defaultTimeout)
    {
        return IsPythonFalsy(value) ? defaultTimeout : Convert.ToInt32(value);
    }

    private static int RetryCountValue(IReadOnlyDictionary<string, object?> channel)
    {
        return Convert.ToInt32(channel.TryGetValue("retry_count", out var value) ? value : DefaultRetryCount);
    }

    private static string NormalizeWebSearchProvider(object? value)
    {
        var provider = (IsPythonFalsy(value) ? "tavily" : value?.ToString() ?? "tavily")
            .Trim()
            .ToLowerInvariant();
        if (!WebSearchProviders.Contains(provider))
        {
            throw new ArgumentException($"unsupported web search provider: {provider}");
        }

        return provider;
    }

    private static string WebSearchApiKey(IReadOnlyDictionary<string, object?> item)
    {
        var value = GetOptionalValue(item, "key") ?? GetOptionalValue(item, "api_key");
        return (value?.ToString() ?? string.Empty).Trim();
    }

    private static int ParseRequiredPositiveInt(object? value, string label)
    {
        if (value is bool)
        {
            throw new ArgumentException($"{label} must be a positive integer");
        }

        try
        {
            var parsed = Convert.ToInt32(value);
            if (parsed <= 0)
            {
                throw new ArgumentException($"{label} must be a positive integer");
            }

            return parsed;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            throw new ArgumentException($"{label} must be a positive integer", exception);
        }
    }

    private static int ParseRequiredNonNegativeInt(object? value, string label)
    {
        if (value is bool)
        {
            throw new ArgumentException($"{label} must be a non-negative integer");
        }

        if (value is float floatValue && floatValue % 1 != 0
            || value is double doubleValue && doubleValue % 1 != 0
            || value is decimal decimalValue && decimalValue % 1 != 0)
        {
            throw new ArgumentException($"{label} must be a non-negative integer");
        }

        try
        {
            var parsed = Convert.ToInt32(value);
            if (parsed < 0)
            {
                throw new ArgumentException($"{label} must be a non-negative integer");
            }

            return parsed;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            throw new ArgumentException($"{label} must be a non-negative integer", exception);
        }
    }

    private static long? ParsePositiveLong(object? value)
    {
        if (value is bool)
        {
            return null;
        }

        try
        {
            var parsed = Convert.ToInt64(value);
            return parsed > 0 ? parsed : null;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static bool IsExplicitFalse(object? value)
    {
        return value is bool boolValue && !boolValue;
    }

    private static string NormalizeUsername(object? value)
    {
        if (IsPythonFalsy(value))
        {
            return string.Empty;
        }

        return value?.ToString()?.Trim() ?? string.Empty;
    }

    private static bool IsPythonFalsy(object? value)
    {
        return value switch
        {
            null => true,
            string text => text.Length == 0,
            bool boolValue => !boolValue,
            byte byteValue => byteValue == 0,
            sbyte sbyteValue => sbyteValue == 0,
            short shortValue => shortValue == 0,
            ushort ushortValue => ushortValue == 0,
            int intValue => intValue == 0,
            uint uintValue => uintValue == 0,
            long longValue => longValue == 0,
            ulong ulongValue => ulongValue == 0,
            float floatValue => floatValue == 0,
            double doubleValue => doubleValue == 0,
            decimal decimalValue => decimalValue == 0,
            _ => false
        };
    }

    private static double UnixTimeSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }

}
