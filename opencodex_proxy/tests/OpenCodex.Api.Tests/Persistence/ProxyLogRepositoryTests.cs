using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;

namespace OpenCodex.Api.Tests.Persistence;

public sealed class ProxyLogRepositoryTests
{
    [Fact]
    public void WriteRequestLogMapsTypedRecordToDatabaseColumns()
    {
        using var workspace = new TempWorkspace();
        var repository = new ProxyLogRepository(new FakeSettingsProvider(
            new OpenCodexRuntimeSettings(workspace.DatabasePath, "admin", "pw", 120)));

        var id = repository.WriteRequestLog(new RequestLogWriteRecord(
            "req_typed",
            1234,
            "POST",
            "/v1/responses",
            "203.0.113.10",
            """{"Authorization":"Bearer redacted"}""",
            """{"model":"client-model"}""",
            """{"model":"upstream-model"}""",
            """{"usage":{}}""",
            """{"output":"ok"}""",
            """{"calls":1}""",
            "client-model",
            "upstream-model",
            "chat",
            IsStream: true,
            TtftMs: 45,
            DurationMs: 123,
            StatusCode: 200,
            InputTokens: 10,
            CachedTokens: 2,
            OutputTokens: 3,
            Cost: 0.25,
            OwnerUsername: "alice",
            ApiKeyId: 7,
            Error: null));

        var saved = OpenCodexDatabase.ReadLogById(workspace.DatabasePath, id);

        Assert.NotNull(saved);
        Assert.Equal("req_typed", saved.RequestId);
        Assert.Equal("POST", saved.Method);
        Assert.Equal("/v1/responses", saved.Path);
        Assert.Equal("203.0.113.10", saved.ClientIp);
        Assert.Equal("client-model", saved.Model);
        Assert.Equal("upstream-model", saved.UpstreamModel);
        Assert.Equal("chat", saved.ChannelId);
        Assert.True(saved.IsStream);
        Assert.Equal(45, saved.TtftMs);
        Assert.Equal(123, saved.DurationMs);
        Assert.Equal(200, saved.StatusCode);
        Assert.Equal(10, saved.InputTokens);
        Assert.Equal(2, saved.CachedTokens);
        Assert.Equal(3, saved.OutputTokens);
        Assert.Equal(0.25, saved.Cost);
        Assert.Equal("alice", saved.OwnerUsername);
        Assert.Equal(7, saved.ApiKeyId);
        Assert.Equal("""{"Authorization":"Bearer redacted"}""", saved.RequestHeaders);
        Assert.Equal("""{"model":"client-model"}""", saved.RequestBody);
        Assert.Equal("""{"model":"upstream-model"}""", saved.UpstreamRequestBody);
        Assert.Equal("""{"usage":{}}""", saved.UpstreamResponseBody);
        Assert.Equal("""{"output":"ok"}""", saved.ResponseBody);
        Assert.Equal("""{"calls":1}""", saved.WebSearchJson);
        Assert.Equal("success", saved.RequestStatus);
    }

    private sealed class FakeSettingsProvider : IOpenCodexRuntimeSettingsProvider
    {
        private readonly OpenCodexRuntimeSettings _settings;

        public FakeSettingsProvider(OpenCodexRuntimeSettings settings)
        {
            _settings = settings;
        }

        public OpenCodexRuntimeSettings GetSettings()
        {
            return _settings;
        }
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"opencodex-proxy-log-repository-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            DatabasePath = System.IO.Path.Combine(Path, "test.db");
        }

        public string Path { get; }

        public string DatabasePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
