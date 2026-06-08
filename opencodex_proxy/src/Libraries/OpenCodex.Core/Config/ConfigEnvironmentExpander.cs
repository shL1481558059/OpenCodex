using System.Text.RegularExpressions;

namespace OpenCodex.Core.Config;

public static partial class ConfigEnvironmentExpander
{
    public static object? Expand(object? value)
    {
        if (value is string stringValue)
        {
            return ExpandString(stringValue);
        }

        if (ConfigValue.TryAsObject(value, out var dictionary))
        {
            return dictionary.ToDictionary(
                pair => pair.Key,
                pair => Expand(pair.Value),
                StringComparer.Ordinal);
        }

        if (ConfigValue.TryAsList(value, out var list))
        {
            return list.Select(Expand).ToList();
        }

        return value;
    }

    private static string ExpandString(string value)
    {
        return EnvironmentVariableRegex().Replace(value, match =>
        {
            var name = match.Groups["braced"].Success
                ? match.Groups["braced"].Value
                : match.Groups["plain"].Value;

            return Environment.GetEnvironmentVariable(name) ?? match.Value;
        });
    }

    [GeneratedRegex(@"\$\{(?<braced>[A-Za-z_][A-Za-z0-9_]*)\}|\$(?<plain>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex EnvironmentVariableRegex();
}
