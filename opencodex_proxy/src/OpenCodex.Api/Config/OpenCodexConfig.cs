namespace OpenCodex.Api.Config;

public static class OpenCodexConfig
{
    public const int DefaultRetryCount = 3;

    public static readonly HashSet<string> ChannelTypes = new(StringComparer.Ordinal)
    {
        "responses",
        "chat",
        "messages"
    };

    public static readonly HashSet<string> AuthModes = new(StringComparer.Ordinal)
    {
        "config",
        "none"
    };

    public static readonly HashSet<string> ConfigFields = new(StringComparer.Ordinal)
    {
        "channels"
    };

    public static readonly HashSet<string> CompatFields = new(StringComparer.Ordinal)
    {
        "rename_params",
        "drop_params",
        "force_params",
        "default_params",
        "unsupported_params",
        "fallback_thinking_on_tool_use"
    };
}
