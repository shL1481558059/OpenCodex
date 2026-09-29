using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>
/// 按调用方提供的 owner + session 隔离键保存快照。修改运行及保存期间必须持有 Gate。
/// 重启只在下一次请求加载快照，不自动发起模型调用；running 恢复为 ready，模型回合可能重做。
/// ReceivedCalls 保留已消费工具结果，调用方须据此拒绝重复投递。
/// </summary>
public sealed class MultiAgentRunStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private readonly Dictionary<string, Dictionary<string, MultiAgentRun>> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _latest = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<MultiAgentRun, SemaphoreSlim> _gates = new();
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public MultiAgentRunStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public SemaphoreSlim Gate(MultiAgentRun run) => _gates.GetOrAdd(run, static _ => new SemaphoreSlim(1, 1));

    public async Task<MultiAgentRun> GetAsync(
        string sessionKey,
        string? previousResponseId,
        Func<MultiAgentRun> create,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        await _indexGate.WaitAsync(ct);
        try
        {
            if (!_sessions.TryGetValue(sessionKey, out var runs))
            {
                runs = await LoadSessionAsync(sessionKey, ct);
                _sessions.Add(sessionKey, runs);
            }

            if (!string.IsNullOrEmpty(previousResponseId))
            {
                return runs.Values.FirstOrDefault(run => run.ResponseIds.Contains(previousResponseId)
                    || run.LastResponseId == previousResponseId)
                    ?? throw new KeyNotFoundException("The previous multi-agent response is unavailable in this session.");
            }

            if (_latest.TryGetValue(sessionKey, out var latestId) && runs.TryGetValue(latestId, out var latest))
            {
                return latest;
            }

            var created = create();
            ArgumentException.ThrowIfNullOrWhiteSpace(created.Id);
            created.SessionKey = sessionKey;
            runs.Add(created.Id, created);
            _latest[sessionKey] = created.Id;
            return created;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    public async Task SaveAsync(MultiAgentRun run, CancellationToken ct, bool persist = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(run.SessionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(run.Id);
        await _indexGate.WaitAsync(ct);
        try
        {
            if (run.LastResponseId.Length > 0)
            {
                run.ResponseIds.Add(run.LastResponseId);
            }

            if (!_sessions.TryGetValue(run.SessionKey, out var runs))
            {
                runs = new Dictionary<string, MultiAgentRun>(StringComparer.Ordinal);
                _sessions.Add(run.SessionKey, runs);
            }
            runs[run.Id] = run;
            _latest[run.SessionKey] = run.Id;
            if (!persist)
            {
                return;
            }

            var directory = SessionDirectory(run.SessionKey);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, Hash(run.Id) + ".json");
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, run, JsonOptions, ct);
                    await stream.FlushAsync(ct);
                    stream.Flush(flushToDisk: true);
                }

                ct.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private async Task<Dictionary<string, MultiAgentRun>> LoadSessionAsync(string sessionKey, CancellationToken ct)
    {
        var runs = new Dictionary<string, MultiAgentRun>(StringComparer.Ordinal);
        var directory = SessionDirectory(sessionKey);
        if (!Directory.Exists(directory))
        {
            return runs;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(File.GetLastWriteTimeUtc))
        {
            await using var stream = File.OpenRead(path);
            var run = await JsonSerializer.DeserializeAsync<MultiAgentRun>(stream, JsonOptions, ct)
                ?? throw new InvalidDataException("The multi-agent snapshot is empty.");
            if (run.SessionKey != sessionKey || string.IsNullOrWhiteSpace(run.Id))
            {
                throw new InvalidDataException("The multi-agent snapshot does not belong to this session.");
            }

            foreach (var agent in run.Agents.Values)
            {
                if (agent.Status == "running")
                {
                    agent.Status = "ready";
                }
            }

            runs.Add(run.Id, run);
            _latest[sessionKey] = run.Id;
        }

        return runs;
    }

    private string SessionDirectory(string sessionKey) => Path.Combine(_directory, Hash(sessionKey));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ObjectValueConverter());
        return options;
    }

    private sealed class ObjectValueConverter : JsonConverter<object>
    {
        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return ConvertElement(document.RootElement);
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            if (value.GetType() == typeof(object))
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
                return;
            }

            JsonSerializer.Serialize(writer, value, value.GetType(), options);
        }

        private static object? ConvertElement(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(
                property => property.Name, property => ConvertElement(property.Value), StringComparer.Ordinal),
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertElement).ToList(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var integer) ? (object)integer : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new JsonException("Unsupported snapshot value.")
        };
    }
}
