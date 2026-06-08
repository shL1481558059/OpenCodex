using System.Collections;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Config;
using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.Core.Services.Ef;

internal static class EfServiceSupport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void InitializeDatabase(string dbPath, string defaultOwnerUsername = "admin")
    {
        var normalizedDefaultOwner = NormalizeUsername(defaultOwnerUsername);
        if (normalizedDefaultOwner.Length == 0)
        {
            normalizedDefaultOwner = "admin";
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var context = OpenCodexDbContextFactory.Create(dbPath);
        try
        {
            context.Database.EnsureCreated();
            _ = context.Users.Any();
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            context.Database.EnsureDeleted();
            context.Database.EnsureCreated();
        }
    }

    public static UserDto ToUserDto(User user)
    {
        return new UserDto(
            user.Username,
            user.Role,
            user.Enabled,
            user.CreatedAt,
            user.UpdatedAt);
    }

    public static ChannelDto ToChannelDto(Channel channel)
    {
        return new ChannelDto(
            channel.OwnerUsername,
            channel.Id,
            channel.Name,
            channel.Type,
            channel.BaseUrl,
            channel.ApiKey,
            channel.AuthMode,
            ParseJsonObject(channel.HeadersJson),
            channel.TimeoutSeconds,
            channel.RetryCount,
            ParseJsonObject(channel.CompatJson),
            ParseJsonList(channel.ModelsJson),
            channel.Enabled);
    }

    public static AccessApiKeyDto ToAccessApiKeyDto(AccessApiKey key, bool includePlaintext)
    {
        return new AccessApiKeyDto(
            key.Id,
            key.OwnerUsername,
            key.Name,
            key.KeyPrefix,
            key.KeySuffix,
            $"{key.KeyPrefix}...{key.KeySuffix}",
            key.Enabled,
            key.CreatedAt,
            key.UpdatedAt,
            key.LastUsedAt,
            includePlaintext ? key.KeyPlaintext : null);
    }

    public static RequestLogDto ToRequestLogDto(RequestLog log)
    {
        return new RequestLogDto(
            log.Id,
            log.RequestId,
            log.CreatedAt,
            log.Method,
            log.Path,
            log.ClientIp,
            log.Model,
            log.UpstreamModel,
            log.ChannelId,
            log.IsStream,
            log.TtftMs,
            log.DurationMs,
            log.StatusCode,
            log.InputTokens,
            log.CachedTokens,
            log.OutputTokens,
            log.Cost,
            log.OwnerUsername,
            log.ApiKeyId,
            log.Error,
            log.Detail?.RequestHeaders,
            log.Detail?.RequestBody,
            log.Detail?.UpstreamRequestBody,
            log.Detail?.UpstreamResponseBody,
            log.Detail?.ResponseBody,
            log.Detail?.WebSearchJson,
            RequestStatus(log.StatusCode, log.Error));
    }

    public static RequestLogEventDto ToRequestLogEventDto(RequestLog log)
    {
        return new RequestLogEventDto(
            log.Id,
            log.RequestId,
            log.CreatedAt,
            log.Method,
            log.Path,
            log.ClientIp,
            log.Model,
            log.UpstreamModel,
            log.ChannelId,
            log.IsStream,
            log.TtftMs,
            log.DurationMs,
            log.StatusCode,
            log.InputTokens,
            log.CachedTokens,
            log.OutputTokens,
            log.Cost,
            log.OwnerUsername,
            log.ApiKeyId,
            log.Error,
            RequestStatus(log.StatusCode, log.Error));
    }

    public static TavilyKeyDto ToTavilyKeyDto(TavilyKey key)
    {
        return new TavilyKeyDto(
            key.Id,
            key.Position,
            key.Provider,
            key.ApiKey,
            key.Enabled,
            key.UsageCount,
            key.UsageLimit,
            key.UsageLimit);
    }

    public static string JsonDumps(object? value)
    {
        return JsonSerializer.Serialize(NormalizeJsonValue(value), JsonOptions);
    }

    public static Dictionary<string, object?> ParseJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return FromJsonElement(document.RootElement) as Dictionary<string, object?>
                ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }

    public static List<object?> ParseJsonList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return FromJsonElement(document.RootElement) as List<object?> ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static object? FromJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => FromJsonElement(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(FromJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null
        };
    }

    public static object? NormalizeJsonValue(object? value)
    {
        return value switch
        {
            JsonElement element => FromJsonElement(element),
            IReadOnlyDictionary<string, object?> dictionary => dictionary.ToDictionary(
                pair => pair.Key,
                pair => NormalizeJsonValue(pair.Value),
                StringComparer.Ordinal),
            IReadOnlyList<object?> list => list.Select(NormalizeJsonValue).ToList(),
            _ => value
        };
    }

    public static object? GetOptionalValue(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out var value) ? value : null;
    }

    public static string? OptionalNullableString(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        var value = GetOptionalValue(dictionary, key);
        return value is null ? null : value.ToString();
    }

    public static int OptionalInt(IReadOnlyDictionary<string, object?> dictionary, string key, int defaultValue)
    {
        var value = GetOptionalValue(dictionary, key);
        return value is null ? defaultValue : ToInt(value);
    }

    public static double OptionalDouble(IReadOnlyDictionary<string, object?> dictionary, string key, double defaultValue)
    {
        var value = GetOptionalValue(dictionary, key);
        if (value is null)
        {
            return defaultValue;
        }

        try
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return defaultValue;
        }
    }

    public static int? OptionalNullableInt(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        var value = GetOptionalValue(dictionary, key);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public static long? OptionalNullableLong(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        var value = GetOptionalValue(dictionary, key);
        return value is not null && TryConvertInt64(value, out var parsed) ? parsed : null;
    }

    public static double? OptionalNullableDouble(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        var value = GetOptionalValue(dictionary, key);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public static string RequiredString(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        var value = GetOptionalValue(dictionary, key);
        if (value is null)
        {
            throw new ArgumentException($"{key} is required", key);
        }

        var text = value.ToString()?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            throw new ArgumentException($"{key} is required", key);
        }

        return text;
    }

    public static string OptionalString(
        IReadOnlyDictionary<string, object?> dictionary,
        string key,
        string defaultValue)
    {
        var value = GetOptionalValue(dictionary, key);
        return value is null ? defaultValue : value.ToString()?.Trim() ?? defaultValue;
    }

    public static int TimeoutValue(object? value, int defaultTimeout)
    {
        return IsPythonFalsy(value) ? defaultTimeout : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public static int RetryCountValue(IReadOnlyDictionary<string, object?> channel)
    {
        return Convert.ToInt32(
            channel.TryGetValue("retry_count", out var value) ? value : OpenCodexConfig.DefaultRetryCount,
            CultureInfo.InvariantCulture);
    }

    public static bool IsExplicitFalse(object? value)
    {
        return value is bool boolean && !boolean;
    }

    public static string NormalizeUsername(object? value)
    {
        return IsPythonFalsy(value) ? string.Empty : value?.ToString()?.Trim() ?? string.Empty;
    }

    public static double UnixTimeSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }

    public static int ParseRequiredPositiveInt(object? value, string label)
    {
        var parsed = ToInt(value);
        if (parsed <= 0)
        {
            throw new ArgumentException($"{label} must be a positive integer", label);
        }

        return parsed;
    }

    public static int ParseRequiredNonNegativeInt(object? value, string label)
    {
        var parsed = ToInt(value);
        if (parsed < 0)
        {
            throw new ArgumentException($"{label} must be a non-negative integer", label);
        }

        return parsed;
    }

    public static int ToInt(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        try
        {
            return value is JsonElement element
                ? element.ValueKind switch
                {
                    JsonValueKind.Number when element.TryGetInt32(out var parsed) => parsed,
                    JsonValueKind.String when int.TryParse(
                        element.GetString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var parsed) => parsed,
                    _ => 0
                }
                : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }

    public static string RequestStatus(int? statusCode, string? error)
    {
        var status = statusCode ?? 0;
        return status >= 400 || !string.IsNullOrWhiteSpace(error) ? "failed" : "success";
    }

    public static bool TryAsObject(object? value, out Dictionary<string, object?> dictionary)
    {
        if (value is Dictionary<string, object?> typedDictionary)
        {
            dictionary = typedDictionary;
            return true;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            dictionary = readOnlyDictionary.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            return true;
        }

        if (value is IDictionary<string, object?> genericDictionary)
        {
            dictionary = genericDictionary.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            return true;
        }

        if (value is IDictionary nonGenericDictionary)
        {
            dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in nonGenericDictionary)
            {
                if (entry.Key is string key)
                {
                    dictionary[key] = entry.Value;
                }
            }

            return true;
        }

        dictionary = [];
        return false;
    }

    public static bool TryAsList(object? value, out List<object?> list)
    {
        if (value is List<object?> typedList)
        {
            list = typedList;
            return true;
        }

        if (value is IList<object?> genericList)
        {
            list = genericList.ToList();
            return true;
        }

        if (value is IEnumerable enumerable and not string and not IDictionary)
        {
            list = [];
            foreach (var item in enumerable)
            {
                list.Add(item);
            }

            return true;
        }

        list = [];
        return false;
    }

    public static double? ParseTimestamp(object? value)
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

    public static string TimestampToIso(double timestamp)
    {
        var milliseconds = (long)Math.Floor(timestamp * 1000);
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            .ToLocalTime()
            .ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static int CachedTokensFromNestedDetails(
        IReadOnlyDictionary<string, object?> usage,
        string detailsKey)
    {
        return TryAsObject(GetOptionalValue(usage, detailsKey), out var details)
            ? ToInt(GetOptionalValue(details, "cached_tokens"))
            : 0;
    }

    public static int ChatCachedTokens(IReadOnlyDictionary<string, object?> usage)
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

    public static long? ParsePositiveLong(object? value)
    {
        if (value is bool)
        {
            return null;
        }

        return TryConvertInt64(value, out var parsed) && parsed > 0 ? parsed : null;
    }

    public static bool IsPythonFalsy(object? value)
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

    public static bool IsEmptyLogFilterValue(object? value)
    {
        if (value is null)
        {
            return true;
        }

        return value is string text && text.Trim().Length == 0;
    }

    public static bool TryConvertInt64(object? value, out long parsed)
    {
        try
        {
            parsed = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            parsed = 0;
            return false;
        }
    }

    public static bool TryConvertInt32(object? value, out int parsed)
    {
        try
        {
            parsed = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            parsed = 0;
            return false;
        }
    }

    public static bool TryConvertDouble(object? value, out double parsed)
    {
        try
        {
            parsed = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            parsed = 0;
            return false;
        }
    }
}
