using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.CoreBase.Results;

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
        string message,
        IReadOnlyList<ErrorItem>? errors = null,
        string? traceId = null)
    {
        return Fail(0, message, errors, traceId);
    }

    public static ApiResult<T> Fail<T>(
        string message,
        IReadOnlyList<ErrorItem>? errors = null,
        string? traceId = null)
    {
        return Fail<T>(0, message, errors, traceId);
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

    public static ApiResult<T> Fail<T>(
        int code,
        string message,
        IReadOnlyList<ErrorItem>? errors = null,
        string? traceId = null)
    {
        return new ApiResult<T>
        {
            Succeeded = false,
            Code = code,
            Message = message,
            Errors = errors ?? Array.Empty<ErrorItem>(),
            TraceId = traceId
        };
    }

    public virtual ApiResult WithTraceId(string traceId)
    {
        return new ApiResult
        {
            Succeeded = Succeeded,
            Code = Code,
            Message = Message,
            Errors = Errors,
            TraceId = traceId
        };
    }
}

public sealed class ApiResult<T> : ApiResult
{
    public T? Data { get; init; }

    public override ApiResult WithTraceId(string traceId)
    {
        return new ApiResult<T>
        {
            Succeeded = Succeeded,
            Code = Code,
            Message = Message,
            Errors = Errors,
            Data = Data,
            TraceId = traceId
        };
    }
}
