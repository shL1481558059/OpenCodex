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
public sealed partial class MultiAgentRunStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private readonly Dictionary<string, Dictionary<string, MultiAgentRun>> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _latest = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<MultiAgentRun> GetAsync(
        string sessionKey,
        string? previousResponseId,
        Func<MultiAgentRun> create,
        CancellationToken ct, Guid? owner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        if(sessionKey.Length>_limits.MaxSessionKeyCharacters)throw Limit();
        await _indexGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed,this);Sweep();
            if (!_sessions.TryGetValue(sessionKey, out var runs))
            {
                var tenant=owner??Guid.Empty;Reserve(tenant,sessionKey);MakeRoom(tenant,0,1,null);
                Dictionary<string,MultiAgentRun> restored;
                try{restored=await LoadSessionAsync(sessionKey,ct);}
                catch{_latest.Remove(sessionKey);throw;}
                if(restored.Count==0&&_safety[(tenant,Hash(sessionKey))]==1)throw Missing();
                _sessions.Add(sessionKey,runs=new());_sessionResources.Add(sessionKey,new(tenant,Now));
                try{foreach(var restoredRun in restored.Values)Register(restoredRun,true);}
                catch{Retire(sessionKey);throw;}
            }
            else if(owner is not null&&_sessionResources[sessionKey].Owner!=owner.Value)throw Missing();
            _sessionResources[sessionKey].Accessed=Now;

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
            try{Register(created,false);}
            catch{if(runs.Count==0){_sessions.Remove(sessionKey);_sessionResources.Remove(sessionKey);_latest.Remove(sessionKey);}throw;}
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

            ObjectDisposedException.ThrowIf(_disposed,this);
            if(!_sessions.ContainsKey(run.SessionKey))
            {
                if(_safety.ContainsKey((Guid.Empty,Hash(run.SessionKey))))throw Missing();
                Reserve(Guid.Empty,run.SessionKey);MakeRoom(Guid.Empty,0,1,null);
                _sessions.Add(run.SessionKey,new());_sessionResources.Add(run.SessionKey,new(Guid.Empty,Now));
            }
            if(!_resources.TryGetValue(run,out var entry)){Register(run,false);entry=Owned(run);}
            entry.Durable=false;
            var weight=Weight(run);_bytes+=weight-entry.Weight;entry.Weight=weight;
            _sessionResources[run.SessionKey].Accessed=Now;
            entry.Rejected=true;
            if(weight>_limits.MaxRunBytes)throw Limit();
            MakeRoom(_sessionResources[run.SessionKey].Owner,0,0,run.SessionKey);
            entry.Rejected=false;
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
                entry.Durable=true;
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

        long bytes=0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(File.GetLastWriteTimeUtc))
        {
            if(runs.Count>=_limits.MaxRunsPerSession||(bytes+=new FileInfo(path).Length)>_limits.MaxColdLoadBytes)throw Limit();
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
