namespace OpenCodex.CoreBase.Domain.WebSearch;

public static class WebSearchModes
{
    public const string Simulate = "simulate";
    public const string Convert = "convert";
    public const string Disabled = "disabled";

    public static bool IsValid(string mode)
    {
        return mode is Simulate or Convert or Disabled;
    }
}
