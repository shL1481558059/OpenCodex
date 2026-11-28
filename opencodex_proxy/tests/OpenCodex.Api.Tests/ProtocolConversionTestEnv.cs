using System.Globalization;
using Xunit;

namespace OpenCodex.Api.Tests;

/// <summary>
/// Test-only access to the mandatory <c>.env</c> that supplies protocol-conversion budgets.
/// Rewrites a single key while preserving every other line, so an isolated child process can
/// prove a value is read from configuration rather than hard-coded.
/// </summary>
internal static class ProtocolConversionTestEnv
{
    internal const string FileName = ".env";
    internal const string Prefix = "ProtocolConversion__";

    internal static string PathIn(string directory) =>
        System.IO.Path.Combine(directory, FileName);

    internal static int Read(string directory, string key)
    {
        var path = PathIn(directory);
        Assert.True(File.Exists(path), $"The mandatory test .env was not found at '{path}'.");
        var value = ReadValue(path, Prefix + key);
        Assert.True(value is not null, $"'{Prefix}{key}' must be set in '{path}'.");
        var parsed = int.Parse(value!, NumberStyles.Integer, CultureInfo.InvariantCulture);
        Assert.True(parsed > 0, $"'{Prefix}{key}' must be positive.");
        return parsed;
    }

    internal static void Write(string directory, string key, int value)
    {
        var path = PathIn(directory);
        var name = Prefix + key;
        var lines = File.ReadAllLines(path).ToList();
        var index = lines.FindIndex(line => line.TrimStart().StartsWith(name + "=", StringComparison.Ordinal));
        Assert.True(index >= 0, $"'{name}' must exist in '{path}' before it can be rewritten.");
        lines[index] = name + "=" + value.ToString(CultureInfo.InvariantCulture);
        File.WriteAllLines(path, lines);
    }

    private static string? ReadValue(string path, string name)
    {
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line["export ".Length..].TrimStart();
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            if (!line[..separator].Trim().Equals(name, StringComparison.Ordinal)) continue;
            var value = line[(separator + 1)..].Trim();
            return value.Length >= 2 && value[0] == value[^1] && (value[0] == (char)34 || value[0] == (char)39)
                ? value[1..^1]
                : value;
        }
        return null;
    }
}
