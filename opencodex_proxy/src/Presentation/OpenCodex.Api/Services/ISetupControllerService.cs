using OpenCodex.CoreBase.DTOs.Auth;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.Api.Services;

/// <summary>
/// 首次初始化服务：读取初始化状态、执行初始化。
/// </summary>
public interface ISetupControllerService
{
    ApiOpResult<SetupStatusResponse> Status();

    Task<ApiOpResult<SetupCompleteResponse>> SetupAsync(SetupRequest request);
}
