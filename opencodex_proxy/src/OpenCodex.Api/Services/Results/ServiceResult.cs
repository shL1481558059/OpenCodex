using OpenCodex.Api.Abstractions;

namespace OpenCodex.Api.Services.Results;

public class ServiceResult
{
    public bool Succeeded { get; init; }

    public int Code { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<ErrorItem> Errors { get; init; } = Array.Empty<ErrorItem>();

    public static ServiceResult Success(string message = "OK")
    {
        return new ServiceResult
        {
            Succeeded = true,
            Code = 0,
            Message = message
        };
    }

    public static ServiceResult<T> Success<T>(T data, string message = "OK")
    {
        return new ServiceResult<T>
        {
            Succeeded = true,
            Code = 0,
            Message = message,
            Data = data
        };
    }

    public static ServiceResult Fail(
        int code,
        string message,
        IReadOnlyList<ErrorItem>? errors = null)
    {
        return new ServiceResult
        {
            Succeeded = false,
            Code = code,
            Message = message,
            Errors = errors ?? Array.Empty<ErrorItem>()
        };
    }

    public static ServiceResult<T> Fail<T>(
        int code,
        string message,
        IReadOnlyList<ErrorItem>? errors = null)
    {
        return new ServiceResult<T>
        {
            Succeeded = false,
            Code = code,
            Message = message,
            Errors = errors ?? Array.Empty<ErrorItem>()
        };
    }
}

public sealed class ServiceResult<T> : ServiceResult
{
    public T? Data { get; init; }
}
