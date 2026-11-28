using System.Globalization;

namespace OpenCodex.ProtocolConversion;

/// <summary>
/// All protocol-conversion budgets. Every value is mandatory and supplied by the deployment
/// environment: a process variable wins, otherwise the .env file next to the application is
/// read. There is no code default, no clamping and no silent fallback.
/// </summary>
internal sealed class ProtocolConversionParameters
{
    internal static ProtocolConversionParameters Current { get; } = Load();

    public required int MaxRequestBodyBytes { get; init; }
    public required int StringSegmentCharacters { get; init; }
    public required int FlushBytes { get; init; }
    public required int GeneratedFrameCacheBytes { get; init; }
    public required int CaptureCopyCharacters { get; init; }
    public required int SseReadBufferCharacters { get; init; }
    public required int SseRetainedBufferCharacters { get; init; }
    public required int SseMaxEventBytes { get; init; }
    public required int SseMaxCaptureBytes { get; init; }
    public required int SseMaxCaptureItems { get; init; }
    public required int SseMaxCaptureFields { get; init; }
    public required long SseMaxBufferedRoundBytes { get; init; }
    public required int SseMaxBufferedRoundFrames { get; init; }
    public required int SseMaxProbeBytes { get; init; }
    public required int SseMaxProbeFrames { get; init; }
    public required int SseMaxWebSocketMessageBytes { get; init; }
    public required int SseMaxBatchBytes { get; init; }
    public required int SseMaxFlushDelayMilliseconds { get; init; }
    public required int SseMaxQueuedEvents { get; init; }
    public required int SseMaxQueuedBytes { get; init; }

    private static ProtocolConversionParameters Load()
    {
        var values = DotEnvValues.Load();
        var parameters = new ProtocolConversionParameters
        {
            MaxRequestBodyBytes = values.Int32(nameof(MaxRequestBodyBytes)),
            StringSegmentCharacters = values.Int32(nameof(StringSegmentCharacters)),
            FlushBytes = values.Int32(nameof(FlushBytes)),
            GeneratedFrameCacheBytes = values.Int32(nameof(GeneratedFrameCacheBytes)),
            CaptureCopyCharacters = values.Int32(nameof(CaptureCopyCharacters)),
            SseReadBufferCharacters = values.Int32(nameof(SseReadBufferCharacters)),
            SseRetainedBufferCharacters = values.Int32(nameof(SseRetainedBufferCharacters)),
            SseMaxEventBytes = values.Int32(nameof(SseMaxEventBytes)),
            SseMaxCaptureBytes = values.Int32(nameof(SseMaxCaptureBytes)),
            SseMaxCaptureItems = values.Int32(nameof(SseMaxCaptureItems)),
            SseMaxCaptureFields = values.Int32(nameof(SseMaxCaptureFields)),
            SseMaxBufferedRoundBytes = values.Int64(nameof(SseMaxBufferedRoundBytes)),
            SseMaxBufferedRoundFrames = values.Int32(nameof(SseMaxBufferedRoundFrames)),
            SseMaxProbeBytes = values.Int32(nameof(SseMaxProbeBytes)),
            SseMaxProbeFrames = values.Int32(nameof(SseMaxProbeFrames)),
            SseMaxWebSocketMessageBytes = values.Int32(nameof(SseMaxWebSocketMessageBytes)),
            SseMaxBatchBytes = values.Int32(nameof(SseMaxBatchBytes)),
            SseMaxFlushDelayMilliseconds = values.Int32(nameof(SseMaxFlushDelayMilliseconds)),
            SseMaxQueuedEvents = values.Int32(nameof(SseMaxQueuedEvents)),
            SseMaxQueuedBytes = values.Int32(nameof(SseMaxQueuedBytes))
        };

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.MaxRequestBodyBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parameters.MaxRequestBodyBytes, Array.MaxLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.StringSegmentCharacters, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.FlushBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.GeneratedFrameCacheBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.CaptureCopyCharacters, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseReadBufferCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseRetainedBufferCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxEventBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxCaptureBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxCaptureItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxCaptureFields);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxBufferedRoundBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxBufferedRoundFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxProbeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxProbeFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxWebSocketMessageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxBatchBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxFlushDelayMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxQueuedEvents);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxQueuedBytes);
        return parameters;
    }

    /// <summary>
    /// Mandatory values from the deployment environment. The process environment wins so a
    /// container can inject the same keys; otherwise the .env file beside the application is
    /// parsed. Every key uses the <c>ProtocolConversion__&lt;Field&gt;</c> spelling documented in
    /// .env.example, so the file stays the single measurable definition of these budgets.
    /// </summary>
    private sealed class DotEnvValues
    {
        internal const string Prefix = "ProtocolConversion__";
        private readonly string _path;
        private readonly Dictionary<string, string> _file;

        private DotEnvValues(string path, Dictionary<string, string> file)
        {
            _path = path;
            _file = file;
        }

        internal static DotEnvValues Load()
        {
            var configured = Environment.GetEnvironmentVariable("OPENCODEX_DOTENV_PATH");
            var path = string.IsNullOrWhiteSpace(configured) ? Locate() : Path.GetFullPath(configured.Trim());
            // The file is optional so a container may inject every key directly; each key
            // is still mandatory and Read() fails with the exact name when it is absent.
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (File.Exists(path))
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("export ", StringComparison.Ordinal)) line = line["export ".Length..].TrimStart();
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line[..separator].Trim();
                if (key.Length == 0) continue;
                values[key] = Unquote(line[(separator + 1)..].Trim());
            }

            return new DotEnvValues(path, values);
        }

        // Deployments run from the application directory; test hosts may run elsewhere.
        private static string Locate()
        {
            var besideApplication = Path.Combine(AppContext.BaseDirectory, ".env");
            if (File.Exists(besideApplication)) return besideApplication;
            return Path.GetFullPath(".env");
        }

        internal int Int32(string name)
        {
            var value = Read(name);
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                throw Invalid(name, value, "an integer");
            return parsed;
        }

        internal long Int64(string name)
        {
            var value = Read(name);
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                throw Invalid(name, value, "a 64-bit integer");
            return parsed;
        }

        private string Read(string name)
        {
            var key = Prefix + name;
            var value = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(value)) value = _file.GetValueOrDefault(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"Required setting '{key}' is missing from '{_path}'. See .env.example for every supported key.");
            }
            return value.Trim();
        }

        private InvalidOperationException Invalid(string name, string value, string expected) =>
            new($"Setting '{Prefix}{name}' in '{_path}' must be {expected} but was '{value}'.");

        private static string Unquote(string value)
        {
            if (value.Length < 2) return value;
            var first = value[0];
            var last = value[^1];
            var doubleQuoted = first == (char)34 && last == (char)34;
            var singleQuoted = first == (char)39 && last == (char)39;
            return doubleQuoted || singleQuoted ? value[1..^1] : value;
        }
    }
}
