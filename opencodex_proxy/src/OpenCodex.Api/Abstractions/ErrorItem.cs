namespace OpenCodex.Api.Abstractions;

public sealed class ErrorItem
{
    public ErrorItem(string field, int code, string message)
    {
        Field = field;
        Code = code;
        Message = message;
    }

    public string Field { get; }

    public int Code { get; }

    public string Message { get; }
}
