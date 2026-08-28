using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.DTOs.SystemSettings;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

/// <summary>
/// 视觉转移模型配置服务
/// 不变式：FallbackChannelId 和 FallbackModel 必须同时为 null 或同时非 null，由服务层保证
/// </summary>
public sealed class VisionTransferSettingsService : IVisionTransferSettingsService
{
    private readonly IWorkContext _workContext;
    private readonly IRepository<VisionTransferSettings> _settingsRepository;
    private readonly IRepository<User> _userRepository;
    private readonly IRepository<Channel> _channelRepository;
    private readonly IModelCatalogService _modelCatalogService;

    public VisionTransferSettingsService(
        IWorkContext workContext,
        IRepository<VisionTransferSettings> settingsRepository,
        IRepository<User> userRepository,
        IRepository<Channel> channelRepository,
        IModelCatalogService modelCatalogService)
    {
        _workContext = workContext;
        _settingsRepository = settingsRepository;
        _userRepository = userRepository;
        _channelRepository = channelRepository;
        _modelCatalogService = modelCatalogService;
    }

    private string CurrentScope(string? requestOwner)
    {
        var currentUser = _workContext.RequireUser();
        return currentUser.Role == "superadmin"
            ? (requestOwner ?? currentUser.Username)
            : currentUser.Username;
    }

    public ApiOpResult<VisionTransferSettingsResponse> Read(string? ownerUsername)
    {
        var resolvedOwner = CurrentScope(ownerUsername);
        var ownerUser = _userRepository.TableNoTracking.FirstOrDefault(u => u.Username == resolvedOwner);
        if (ownerUser is null)
        {
            return ApiOpResult<VisionTransferSettingsResponse>.Fail(404, "owner user not found");
        }

        var settings = _settingsRepository.TableNoTracking
            .FirstOrDefault(s => s.OwnerUserId == ownerUser.Id);

        var response = new VisionTransferSettingsResponse
        {
            OwnerUsername = resolvedOwner,
            Configured = settings != null,
            UpdatedAt = settings?.UpdatedAt ?? 0
        };

        if (settings == null)
        {
            return ApiOpResult<VisionTransferSettingsResponse>.Succeed(response);
        }

        response.Primary = CheckAvailability(settings.PrimaryChannelId, settings.PrimaryModel, ownerUser.Id);
        if (settings.FallbackChannelId.HasValue && settings.FallbackModel != null)
        {
            response.Fallback = CheckAvailability(settings.FallbackChannelId.Value, settings.FallbackModel, ownerUser.Id);
        }

        return ApiOpResult<VisionTransferSettingsResponse>.Succeed(response);
    }

    public ApiOpResult<VisionTransferCandidateListResponse> ListCandidates(string? ownerUsername)
    {
        var resolvedOwner = CurrentScope(ownerUsername);
        var ownerUser = _userRepository.TableNoTracking.FirstOrDefault(u => u.Username == resolvedOwner);
        if (ownerUser is null)
        {
            return ApiOpResult<VisionTransferCandidateListResponse>.Fail(404, "owner user not found");
        }

        var channels = _channelRepository.TableNoTracking
            .Where(c => c.OwnerUserId == ownerUser.Id && c.Enabled)
            .ToList();

        var candidates = new List<VisionTransferCandidateDto>();

        foreach (var channel in channels)
        {
            var mappings = ParseChannelModels(channel.ModelsJson);
            foreach (var (model, upstreamModel) in mappings)
            {
                if (_modelCatalogService.SupportsImage(channel.Id, upstreamModel))
                {
                    candidates.Add(new VisionTransferCandidateDto
                    {
                        ChannelId = channel.Id,
                        ChannelName = channel.Name,
                        ChannelType = channel.Type,
                        Model = model,
                        UpstreamModel = upstreamModel
                    });
                }
            }
        }

        return ApiOpResult<VisionTransferCandidateListResponse>.Succeed(new VisionTransferCandidateListResponse
        {
            OwnerUsername = resolvedOwner,
            Candidates = candidates
        });
    }

    public ApiOpResult<VisionTransferSettingsResponse> Save(VisionTransferSettingsUpdateRequest request)
    {
        if (request.Primary is null)
        {
            return ApiOpResult<VisionTransferSettingsResponse>.Fail(400, "primary configuration is required");
        }

        var requestOwner = CurrentScope(request.OwnerUsername);
        var ownerUser = _userRepository.TableNoTracking.FirstOrDefault(u => u.Username == requestOwner);
        if (ownerUser is null)
        {
            return ApiOpResult<VisionTransferSettingsResponse>.Fail(404, "owner user not found");
        }

        if (request.Primary.ChannelId is null || string.IsNullOrWhiteSpace(request.Primary.Model))
        {
            return ApiOpResult<VisionTransferSettingsResponse>.Fail(400, "primary channel and model are both required");
        }

        // 兜底允许整组留空,但不允许只给渠道或只给模型。
        if ((request.Fallback?.ChannelId is null) != (string.IsNullOrWhiteSpace(request.Fallback?.Model)))
        {
            return ApiOpResult<VisionTransferSettingsResponse>.Fail(400, "fallback must have both channel and model or neither");
        }

        var primaryValidation = ValidateItem(request.Primary.ChannelId.Value, request.Primary.Model, ownerUser.Id);
        if (!primaryValidation.Succeeded)
        {
            return ApiOpResult<VisionTransferSettingsResponse>.Fail(primaryValidation.Code, primaryValidation.Description);
        }

        if (request.Fallback != null && request.Fallback.ChannelId.HasValue)
        {
            var fallbackValidation = ValidateItem(request.Fallback.ChannelId.Value, request.Fallback.Model!, ownerUser.Id);
            if (!fallbackValidation.Succeeded)
            {
                return ApiOpResult<VisionTransferSettingsResponse>.Fail(fallbackValidation.Code, fallbackValidation.Description);
            }

            if (request.Primary.ChannelId == request.Fallback.ChannelId &&
                request.Primary.Model.Equals(request.Fallback.Model, StringComparison.Ordinal))
            {
                return ApiOpResult<VisionTransferSettingsResponse>.Fail(400, "primary and fallback cannot be identical");
            }
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var existing = _settingsRepository.Table
            .FirstOrDefault(s => s.OwnerUserId == ownerUser.Id);

        try
        {
            if (existing == null)
            {
                var settings = new VisionTransferSettings
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = ownerUser.Id,
                    PrimaryChannelId = request.Primary.ChannelId.Value,
                    PrimaryModel = request.Primary.Model.Trim(),
                    FallbackChannelId = request.Fallback?.ChannelId,
                    FallbackModel = request.Fallback?.Model?.Trim(),
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _settingsRepository.Insert(settings);
            }
            else
            {
                existing.PrimaryChannelId = request.Primary.ChannelId.Value;
                existing.PrimaryModel = request.Primary.Model.Trim();
                existing.FallbackChannelId = request.Fallback?.ChannelId;
                existing.FallbackModel = request.Fallback?.Model?.Trim();
                existing.UpdatedAt = now;
                _settingsRepository.Update(existing);
            }

            _settingsRepository.SaveChanges();
        }
        catch (DbUpdateException) when (existing is null)
        {
            // 唯一索引冲突,通常是并发插入。重读一次改走更新路径,仍失败则返回 409。
            var existingAfterRetry = _settingsRepository.Table
                .FirstOrDefault(s => s.OwnerUserId == ownerUser.Id);
            if (existingAfterRetry != null)
            {
                existingAfterRetry.PrimaryChannelId = request.Primary.ChannelId.Value;
                existingAfterRetry.PrimaryModel = request.Primary.Model.Trim();
                existingAfterRetry.FallbackChannelId = request.Fallback?.ChannelId;
                existingAfterRetry.FallbackModel = request.Fallback?.Model?.Trim();
                existingAfterRetry.UpdatedAt = now;
                _settingsRepository.Update(existingAfterRetry);
                try
                {
                    _settingsRepository.SaveChanges();
                }
                catch (DbUpdateException ex)
                {
                    return ApiOpResult<VisionTransferSettingsResponse>.Fail(409, $"conflict updating settings, please retry: {ex.Message}");
                }
            }
            else
            {
                // 重读仍无行:说明冲突来源并非唯一键,不能吞掉异常冒充保存成功。
                return ApiOpResult<VisionTransferSettingsResponse>.Fail(
                    409,
                    "conflict saving settings, please retry");
            }
        }

        return Read(requestOwner);
    }

    public ApiOpResult Delete(string? ownerUsername)
    {
        var resolvedOwner = CurrentScope(ownerUsername);
        var ownerUser = _userRepository.TableNoTracking.FirstOrDefault(u => u.Username == resolvedOwner);
        if (ownerUser is null)
        {
            return ApiOpResult.Fail(404, "owner user not found");
        }

        var existing = _settingsRepository.Table
            .FirstOrDefault(s => s.OwnerUserId == ownerUser.Id);
        if (existing != null)
        {
            _settingsRepository.Delete(existing);
            _settingsRepository.SaveChanges();
        }

        return ApiOpResult.Succeed();
    }

    public VisionTransferSettingsSnapshot? GetSnapshot(Guid ownerUserId)
    {
        var settings = _settingsRepository.TableNoTracking
            .FirstOrDefault(s => s.OwnerUserId == ownerUserId);
        if (settings == null)
        {
            return null;
        }

        return new VisionTransferSettingsSnapshot(
            settings.PrimaryChannelId,
            settings.PrimaryModel,
            settings.FallbackChannelId,
            settings.FallbackModel);
    }

    private (bool Succeeded, int Code, string Description) ValidateItem(Guid channelId, string model, Guid ownerUserId)
    {
        model = model.Trim();
        var channel = _channelRepository.GetById(channelId);
        if (channel == null)
        {
            return (false, 400, $"channel {channelId} does not exist");
        }

        if (channel.OwnerUserId != ownerUserId)
        {
            return (false, 400, "channel does not belong to target owner");
        }

        if (!channel.Enabled)
        {
            return (false, 400, "channel is disabled");
        }

        var mappings = ParseChannelModels(channel.ModelsJson);
        var (foundModel, foundUpstream) = mappings.FirstOrDefault(m => m.Model.Equals(model, StringComparison.Ordinal));
        if (string.IsNullOrEmpty(foundModel))
        {
            return (false, 400, $"model '{model}' not found in channel {channelId}");
        }

        if (!_modelCatalogService.SupportsImage(channelId, foundUpstream))
        {
            return (false, 400, "model does not have image support enabled. Please go to model information page to mark supports_image capability.");
        }

        return (true, 200, string.Empty);
    }

    private VisionTransferConfigStatusDto CheckAvailability(Guid channelId, string model, Guid ownerUserId)
    {
        var result = new VisionTransferConfigStatusDto
        {
            ChannelId = channelId,
            Model = model,
            Available = false
        };

        var channel = _channelRepository.GetById(channelId);
        if (channel == null)
        {
            result.Reason = "channel_deleted";
            return result;
        }

        result.ChannelName = channel.Name;
        result.ChannelType = channel.Type;

        if (!channel.Enabled)
        {
            result.Reason = "channel_disabled";
            return result;
        }

        if (channel.OwnerUserId != ownerUserId)
        {
            result.Reason = "channel_owner_changed";
            return result;
        }

        var mappings = ParseChannelModels(channel.ModelsJson);
        var (foundModel, foundUpstream) = mappings.FirstOrDefault(m => m.Model.Equals(model, StringComparison.Ordinal));
        if (string.IsNullOrEmpty(foundModel))
        {
            result.Reason = "model_mapping_missing";
            return result;
        }

        result.UpstreamModel = foundUpstream;

        if (!_modelCatalogService.SupportsImage(channelId, foundUpstream))
        {
            result.Reason = "image_capability_revoked";
            return result;
        }

        result.Available = true;
        return result;
    }

    private static List<(string Model, string UpstreamModel)> ParseChannelModels(string modelsJson)
    {
        // ModelsJson 形如 [{ "model": "...", "upstream_model": "..." }],与渠道保存时的规范化结果一致。
        // 两个字段都 trim,保证与 ValidateItem 的 trim 后精确匹配口径一致。
        var result = new List<(string Model, string UpstreamModel)>();
        try
        {
            using var doc = JsonDocument.Parse(modelsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.TryGetProperty("model", out var modelProp) &&
                        modelProp.GetString() is string model &&
                        element.TryGetProperty("upstream_model", out var upstreamProp) &&
                        upstreamProp.GetString() is string upstream)
                    {
                        result.Add((model.Trim(), upstream.Trim()));
                    }
                }
            }
        }
        catch
        {
            // 忽略解析失败:非法 JSON 在渠道保存阶段已被拦截,这里不重复报错。
        }

        return result;
    }
}
