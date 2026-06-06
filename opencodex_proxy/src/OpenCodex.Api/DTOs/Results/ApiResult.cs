using OpenCodex.Api.Abstractions;

namespace OpenCodex.Api.DTOs.Results;

public class ApiResult
{
    public bool Succeeded { get; init; }

    public int Code { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<ErrorItem> Errors { get; init; } = Array.Empty<ErrorItem>();

    public string? TraceId { get; init; }

    public static ApiResult Success(string message = "OK", string? traceId = null)
    {
        return new ApiResult
        {
            Succeeded = true,
            Code = 0,
            Message = message,
            TraceId = traceId
        };
    }

    public static ApiResult<T> Success<T>(T data, string message = "OK", string? traceId = null)
    {
        return new ApiResult<T>
        {
            Succeeded = true,
            Code = 0,
            Message = message,
            Data = data,
            TraceId = traceId
        };
    }

    public static ApiResult Fail(
        int code,
        string message,
        IReadOnlyList<ErrorItem>? errors = null,
        string? traceId = null)
    {
        return new ApiResult
        {
            Succeeded = false,
            Code = code,
            Message = message,
            Errors = errors ?? Array.Empty<ErrorItem>(),
            TraceId = traceId
        };
    }
}

public sealed class ApiResult<T> : ApiResult
{
    public T? Data { get; init; }
}
