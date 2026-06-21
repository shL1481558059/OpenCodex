using System.Globalization;
using System.Collections.Concurrent;
using System.Text.Json;
using Mapster;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Pricing;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

public sealed class ModelPricingService : IModelPricingService
{
    private readonly IRepository<ModelPricing> _repository;

    public ModelPricingService(IRepository<ModelPricing> repository)
    {
        _repository = repository;
    }

    public ApiOpResult<ModelPricingListResponse> ListPrices(
        string? query,
        string? vendor,
        bool? enabled)
    {
        var prices = ApplyFilters(_repository.TableNoTracking, query, vendor, enabled)
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

            if (_repository.TableNoTracking.Any(p => p.ModelId == price.ModelId))
            {
                return ValidationFailure("model_id already exists");
            }

            _repository.Insert(price);
            return ApiOpResult<ModelPricingResponsePayload>.Succeed(ModelPricingResponsePayload.From(price.Adapt<ModelPricingDto>()));
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(exception.Message);
        }
        catch (Exception exception)
        {
            return ValidationFailure("model_id already exists", exception);
        }
    }

    public ApiOpResult<ModelPricingResponsePayload> UpdatePrice(
        Guid id,
        ModelPricingUpdateCommand command)
    {
        try
        {
            var price = _repository.Table.FirstOrDefault(item => item.Id == id);
            if (price is null)
            {
                return ApiOpResult<ModelPricingResponsePayload>.Fail(404, "price not found");
            }

            ApplyUpdates(price, command.Values);
            price.UpdatedAt = UnixTimeSeconds();
            _repository.Update(price);
            return ApiOpResult<ModelPricingResponsePayload>.Succeed(ModelPricingResponsePayload.From(price.Adapt<ModelPricingDto>()));
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(exception.Message);
        }
        catch (Exception exception)
        {
            return ValidationFailure("model_id already exists", exception);
        }
    }

    public ApiOpResult<DeleteModelPricingResponse> DeletePrice(Guid id)
    {
        var price = _repository.Table.FirstOrDefault(item => item.Id == id);
        if (price is null)
        {
            return ApiOpResult<DeleteModelPricingResponse>.Fail(404, "price not found");
        }

        var deleted = price.Adapt<ModelPricingDto>();
        _repository.Delete(price);
        return ApiOpResult<DeleteModelPricingResponse>.Succeed(DeleteModelPricingResponse.From(deleted));
    }

    public async Task<ApiOpResult<SeedModelPricingResponse>> SeedDefaultsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var defaults = await OpenCodexPricingDefaults.CurrentRemoteAsync(cancellationToken);
            var result = UpdateDefaults(defaults);
            return ApiOpResult<SeedModelPricingResponse>.Succeed(
                new SeedModelPricingResponse(result.Inserted, result.Updated, result.Skipped));
        }
        catch (Exception exception) when (exception is HttpRequestException
            or JsonException
            or InvalidOperationException)
        {
            return ApiOpResult<SeedModelPricingResponse>.Fail(
                502,
                $"failed to fetch latest model pricing: {exception.Message}");
        }
    }

    public double CalculateCost(
        string model,
        int inputTokens,
        int cachedTokens,
        int outputTokens)
    {
        var prices = _repository.TableNoTracking
            .Where(price => price.Enabled)
            .ToList();
        return OpenCodexPricing.CalculateCost(prices, model, inputTokens, cachedTokens, outputTokens);
    }

    public (int Inserted, int Skipped) SeedDefaults(bool insertOnlyWhenEmpty)
    {
        if (insertOnlyWhenEmpty && _repository.TableNoTracking.Any())
        {
            return (0, _repository.TableNoTracking.Count());
        }

        var now = UnixTimeSeconds();
        var existing = _repository.TableNoTracking
            .Select(price => price.ModelId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inserted = 0;
        var skipped = 0;
        var toInsert = new List<ModelPricing>();
        foreach (var price in OpenCodexPricingDefaults.Current())
        {
            if (existing.Contains(price.ModelId))
            {
                skipped++;
                continue;
            }

            toInsert.Add(new ModelPricing
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

        if (toInsert.Count > 0)
        {
            _repository.Insert(toInsert);
        }

        return (inserted, skipped);
    }

    internal (int Inserted, int Updated, int Skipped) UpdateDefaults(
        IReadOnlyList<DefaultModelPricing> defaults)
    {
        var now = UnixTimeSeconds();
        var existing = _repository.Table
            .ToDictionary(price => price.ModelId, StringComparer.OrdinalIgnoreCase);
        var inserted = 0;
        var updated = 0;
        var skipped = 0;
        var toInsert = new List<ModelPricing>();

        foreach (var price in defaults)
        {
            if (!existing.TryGetValue(price.ModelId, out var existingPrice))
            {
                var created = new ModelPricing
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
                };
                toInsert.Add(created);
                existing.Add(price.ModelId, created);
                inserted++;
                continue;
            }

            if (!string.Equals(existingPrice.Source, OpenCodexPricingDefaults.Source, StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            if (!ApplyDefaultUpdates(existingPrice, price))
            {
                skipped++;
                continue;
            }

            existingPrice.UpdatedAt = now;
            updated++;
        }

        if (toInsert.Count > 0)
        {
            _repository.Insert(toInsert);
        }

        if (updated > 0)
        {
            _repository.SaveChanges();
        }

        return (inserted, updated, skipped);
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

        price.Source = "manual";
    }

    private static bool ApplyDefaultUpdates(
        ModelPricing target,
        DefaultModelPricing source)
    {
        var changed = false;
        if (!string.Equals(target.Vendor, source.Vendor, StringComparison.Ordinal))
        {
            target.Vendor = source.Vendor;
            changed = true;
        }

        if (!string.Equals(target.Name, source.Name, StringComparison.Ordinal))
        {
            target.Name = source.Name;
            changed = true;
        }

        if (!string.Equals(target.MatchPattern, source.ModelId, StringComparison.Ordinal))
        {
            target.MatchPattern = source.ModelId;
            changed = true;
        }

        if (Math.Abs(target.InputPrice - source.InputPrice) >= double.Epsilon)
        {
            target.InputPrice = source.InputPrice;
            changed = true;
        }

        if (!Nullable.Equals(target.CachedInputPrice, source.CachedInputPrice))
        {
            target.CachedInputPrice = source.CachedInputPrice;
            changed = true;
        }

        if (Math.Abs(target.OutputPrice - source.OutputPrice) >= double.Epsilon)
        {
            target.OutputPrice = source.OutputPrice;
            changed = true;
        }

        return changed;
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
