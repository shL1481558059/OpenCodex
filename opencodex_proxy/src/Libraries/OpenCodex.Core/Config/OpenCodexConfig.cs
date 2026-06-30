namespace OpenCodex.Core.Config;

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
        "enable_apply_patch_prompt_compat",
        "rename_params",
        "drop_params",
        "drop_tool_types",
        "force_params",
        "default_params",
        "unsupported_params"
    };
}
