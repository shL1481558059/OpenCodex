using System.Globalization;
using System.Collections.Concurrent;
using Mapster;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Pricing;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;
using OpenCodex.Data;

namespace OpenCodex.Core.Services;

public sealed class ModelPricingService : IModelPricingService
{
    private static readonly ConcurrentDictionary<string, bool> SchemaInitialized = new(StringComparer.Ordinal);

    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public ModelPricingService(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public ApiOpResult<ModelPricingListResponse> ListPrices(
        string? query,
        string? vendor,
        bool? enabled)
    {
        using var context = OpenContext();
        var prices = ApplyFilters(context.ModelPricings.AsNoTracking(), query, vendor, enabled)
            .OrderBy(price => price.Vendor)
            .ThenBy(price => price.ModelId)
            .AsEnumerable()
            .Select(price => price.Adapt<ModelPricingDto>())
            .ToList();
        return ApiOpResult<ModelPricingListResponse>.Succeed(ModelPricingListResponse.From(prices));
    }

    public ApiOpResult<ModelPricingResponsePayload> CreatePrice(
        ModelPricingCreateCommand command)
    {
        try
        {
            using var context = OpenContext();
            var now = UnixTimeSeconds();
            var price = new ModelPricing
            {
                ModelId = NormalizeRequired(command.ModelId, "model_id"),
                Vendor = Normalize(command.Vendor),
                Name = Normalize(command.Name),
                MatchPattern = NormalizeMatchPattern(command.MatchPattern, command.ModelId),
                InputPrice = ValidatePrice(command.InputPrice, "input_price"),
                CachedInputPrice = ValidateNullablePrice(command.CachedInputPrice, "cached_input_price"),
                OutputPrice = ValidatePrice(command.OutputPrice, "output_price"),
                Enabled = command.Enabled,
                Source = "manual",
                CreatedAt = now,
                UpdatedAt = now
            };
            if (price.Name.Length == 0)
            {
                price.Name = price.ModelId;
            }

            context.ModelPricings.Add(price);
            context.SaveChanges();
            return ApiOpResult<ModelPricingResponsePayload>.Succeed(ModelPricingResponsePayload.From(price.Adapt<ModelPricingDto>()));
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(exception.Message);
        }
        catch (DbUpdateException exception)
        {
            return ValidationFailure("model_id already exists", exception);
        }
    }

    public ApiOpResult<ModelPricingResponsePayload> UpdatePrice(
        long id,
        ModelPricingUpdateCommand command)
    {
        try
        {
            using var context = OpenContext();
            var price = context.ModelPricings.FirstOrDefault(item => item.Id == id);
            if (price is null)
            {
                return ApiOpResult<ModelPricingResponsePayload>.Fail(404, "price not found");
            }

            ApplyUpdates(price, command.Values);
            price.UpdatedAt = UnixTimeSeconds();
            context.SaveChanges();
            return ApiOpResult<ModelPricingResponsePayload>.Succeed(ModelPricingResponsePayload.From(price.Adapt<ModelPricingDto>()));
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(exception.Message);
        }
        catch (DbUpdateException exception)
        {
            return ValidationFailure("model_id already exists", exception);
        }
    }

    public ApiOpResult<DeleteModelPricingResponse> DeletePrice(long id)
    {
        using var context = OpenContext();
        var price = context.ModelPricings.FirstOrDefault(item => item.Id == id);
        if (price is null)
        {
            return ApiOpResult<DeleteModelPricingResponse>.Fail(404, "price not found");
        }

        var deleted = price.Adapt<ModelPricingDto>();
        context.ModelPricings.Remove(price);
        context.SaveChanges();
        return ApiOpResult<DeleteModelPricingResponse>.Succeed(DeleteModelPricingResponse.From(deleted));
    }

    public ApiOpResult<SeedModelPricingResponse> SeedDefaults()
    {
        var result = SeedDefaults(insertOnlyWhenEmpty: false);
        return ApiOpResult<SeedModelPricingResponse>.Succeed(new SeedModelPricingResponse(result.Inserted, result.Skipped));
    }

    public double CalculateCost(
        string model,
        int inputTokens,
        int cachedTokens,
        int outputTokens)
    {
        using var context = OpenContext();
        var prices = context.ModelPricings
            .AsNoTracking()
            .Where(price => price.Enabled)
            .ToList();
        return OpenCodexPricing.CalculateCost(prices, model, inputTokens, cachedTokens, outputTokens);
    }

    public (int Inserted, int Skipped) SeedDefaults(bool insertOnlyWhenEmpty)
    {
        using var context = OpenContext();
        if (insertOnlyWhenEmpty && context.ModelPricings.Any())
        {
            return (0, context.ModelPricings.Count());
        }

        var now = UnixTimeSeconds();
        var existing = context.ModelPricings
            .Select(price => price.ModelId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inserted = 0;
        var skipped = 0;
        foreach (var price in OpenCodexPricingDefaults.Current())
        {
            if (existing.Contains(price.ModelId))
            {
                skipped++;
                continue;
            }

            context.ModelPricings.Add(new ModelPricing
            {
                ModelId = price.ModelId,
                Vendor = price.Vendor,
                Name = price.Name,
                MatchPattern = price.ModelId,
                InputPrice = price.InputPrice,
                CachedInputPrice = price.CachedInputPrice,
                OutputPrice = price.OutputPrice,
                Enabled = true,
                Source = OpenCodexPricingDefaults.Source,
                CreatedAt = now,
                UpdatedAt = now
            });
            existing.Add(price.ModelId);
            inserted++;
        }

        if (inserted > 0)
        {
            context.SaveChanges();
        }

        return (inserted, skipped);
    }

    private OpenCodexDbContext OpenContext()
    {
        var dbPath = _settingsProvider.GetSettings().DbPath;
        var context = OpenCodexDbContextFactory.Create(dbPath);
        if (SchemaInitialized.TryAdd(Path.GetFullPath(dbPath), true))
        {
            OpenCodexPricing.EnsureSchema(context);
        }

        return context;
    }

    private static IQueryable<ModelPricing> ApplyFilters(
        IQueryable<ModelPricing> query,
        string? search,
        string? vendor,
        bool? enabled)
    {
        var normalizedVendor = Normalize(vendor);
        if (normalizedVendor.Length > 0)
        {
            query = query.Where(price => price.Vendor == normalizedVendor);
        }

        if (enabled.HasValue)
        {
            query = query.Where(price => price.Enabled == enabled.Value);
        }

        var normalizedSearch = Normalize(search);
        if (normalizedSearch.Length > 0)
        {
            query = query.Where(price =>
                price.ModelId.Contains(normalizedSearch)
                || price.Name.Contains(normalizedSearch)
                || price.MatchPattern.Contains(normalizedSearch)
                || price.Vendor.Contains(normalizedSearch));
        }

        return query;
    }

    private static void ApplyUpdates(
        ModelPricing price,
        IReadOnlyDictionary<string, object?> values)
    {
        if (values.ContainsKey("model_id"))
        {
            price.ModelId = NormalizeRequired(JsonDictionaryValue.String(values, "model_id"), "model_id");
        }

        if (values.ContainsKey("vendor"))
        {
            price.Vendor = Normalize(JsonDictionaryValue.String(values, "vendor"));
        }

        if (values.ContainsKey("name"))
        {
            price.Name = Normalize(JsonDictionaryValue.String(values, "name"));
            if (price.Name.Length == 0)
            {
                price.Name = price.ModelId;
            }
        }

        if (values.ContainsKey("match_pattern"))
        {
            price.MatchPattern = NormalizeMatchPattern(JsonDictionaryValue.String(values, "match_pattern"), price.ModelId);
        }

        if (values.ContainsKey("input_price"))
        {
            price.InputPrice = ValidatePrice(ParseDouble(JsonDictionaryValue.Get(values, "input_price"), "input_price"), "input_price");
        }

        if (values.ContainsKey("cached_input_price"))
        {
            var value = JsonDictionaryValue.Get(values, "cached_input_price");
            price.CachedInputPrice = value is null
                ? null
                : ValidateNullablePrice(ParseDouble(value, "cached_input_price"), "cached_input_price");
        }

        if (values.ContainsKey("output_price"))
        {
            price.OutputPrice = ValidatePrice(ParseDouble(JsonDictionaryValue.Get(values, "output_price"), "output_price"), "output_price");
        }

        if (values.ContainsKey("enabled"))
        {
            price.Enabled = ParseBool(JsonDictionaryValue.Get(values, "enabled"), "enabled");
        }
    }

    private static string NormalizeRequired(string? value, string field)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"{field} is required", field);
        }

        return normalized;
    }

    private static string NormalizeMatchPattern(string? matchPattern, string modelId)
    {
        var normalized = Normalize(matchPattern);
        return normalized.Length == 0 ? NormalizeRequired(modelId, "model_id") : normalized;
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static double ValidatePrice(double value, string field)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentException($"{field} must be a non-negative number", field);
        }

        return value;
    }

    private static double? ValidateNullablePrice(double? value, string field)
    {
        return value.HasValue ? ValidatePrice(value.Value, field) : null;
    }

    private static double ParseDouble(object? value, string field)
    {
        if (value is null)
        {
            throw new ArgumentException($"{field} must be a number", field);
        }

        if (value is double doubleValue)
        {
            return doubleValue;
        }

        if (value is float floatValue)
        {
            return floatValue;
        }

        if (value is decimal decimalValue)
        {
            return (double)decimalValue;
        }

        if (value is int intValue)
        {
            return intValue;
        }

        if (value is long longValue)
        {
            return longValue;
        }

        if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException($"{field} must be a number", field);
    }

    private static bool ParseBool(object? value, string field)
    {
        if (value is bool boolValue)
        {
            return boolValue;
        }

        if (value is string stringValue && bool.TryParse(stringValue, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException($"{field} must be a boolean", field);
    }

    private static ApiOpResult<ModelPricingResponsePayload> ValidationFailure(string message)
    {
        return ApiOpResult<ModelPricingResponsePayload>.Fail(400, message);
    }

    private static ApiOpResult<ModelPricingResponsePayload> ValidationFailure(
        string message,
        Exception exception)
    {
        return ApiOpResult<ModelPricingResponsePayload>.Fail(400, message ?? exception.Message);
    }

    private static double UnixTimeSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }
}
