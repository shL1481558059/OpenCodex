using Microsoft.Extensions.Configuration;

namespace OpenCodex.Api.Configuration;

public static class DotEnvDefaults
{
    public static Dictionary<string, string?> Load(string path, IConfiguration configuration)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            if (key.Length == 0 || !string.IsNullOrEmpty(configuration[key]))
            {
                continue;
            }

            values[key] = Unquote(line[(separatorIndex + 1)..].Trim());
        }

        return values;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            var first = value[0];
            var last = value[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                return value[1..^1];
            }
        }

        return value;
    }
}
