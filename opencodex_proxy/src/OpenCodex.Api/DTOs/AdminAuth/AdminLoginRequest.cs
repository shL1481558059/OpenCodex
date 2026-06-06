namespace OpenCodex.Api.DTOs.AdminAuth;

public sealed class AdminLoginRequest
{
    public AdminLoginRequest(string username, string password)
    {
        Username = username;
        Password = password;
    }

    public string Username { get; }

    public string Password { get; }

    public static AdminLoginRequest From(IReadOnlyDictionary<string, object?> body)
    {
        return new AdminLoginRequest(
            StringValue(body, "username"),
            StringValue(body, "password"));
    }

    private static string StringValue(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        if (!dictionary.TryGetValue(key, out var value))
        {
            return string.Empty;
        }

        return value switch
        {
            string text => text.Trim(),
            int or long or double or bool => value.ToString()?.Trim() ?? string.Empty,
            _ => string.Empty
        };
    }
}
