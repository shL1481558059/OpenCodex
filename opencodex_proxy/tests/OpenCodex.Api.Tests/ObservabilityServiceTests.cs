using OpenCodex.Core.Domain;
using OpenCodex.Core.Services;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs.Observability;
using OpenCodex.CoreBase.Services;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ObservabilityServiceTests
{
    [Fact]
    public void LogsAndApiKeyFilterOptionsExposeApiKeyName()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-api-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        using (var context = OpenCodexDbContextFactory.Create(dbPath))
        {
            context.Database.EnsureCreated();
            context.Users.Add(new User
            {
                Username = "admin",
                PasswordHash = "hash",
                Role = "superadmin",
                Enabled = true,
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.AccessApiKeys.Add(new AccessApiKey
            {
                Id = 101,
                OwnerUsername = "admin",
                Name = "Primary Key",
                KeyHash = "hash-101",
                KeyPrefix = "sk",
                KeySuffix = "0101",
                Enabled = true,
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.RequestLogs.Add(new RequestLog
            {
                Id = 501,
                RequestId = "req-501",
                CreatedAt = 1,
                Method = "POST",
                Path = "/v1/chat/completions",
                Model = "gpt-test",
                UpstreamModel = "gpt-test",
                ChannelId = "channel-a",
                IsStream = false,
                StatusCode = 200,
                OwnerUsername = "admin",
                ApiKeyId = 101
            });
            context.SaveChanges();
        }

        var service = new ObservabilityService(
            new TestSettingsProvider(dbPath),
            new TestWorkContext("admin", "superadmin"));

        var logs = service.ReadLogsPage(1, 20, new Dictionary<string, object?>());

        Assert.True(logs.Succeeded);
        var log = Assert.Single(logs.Payload!.Events);
        Assert.Equal(101, log.ApiKeyId);
        Assert.Equal("Primary Key", log.ApiKeyName);

        var options = service.ReadLogFilterOption(
            "api_key_id",
            "Primary",
            new Dictionary<string, object?>());

        Assert.True(options.Succeeded);
        var apiKeyOptions = Assert.IsType<List<LogApiKeyFilterOption>>(options.Payload!["api_key_ids"]);
        var option = Assert.Single(apiKeyOptions);
        Assert.Equal(101, option.Id);
        Assert.Equal("Primary Key", option.Name);
    }

    private sealed class TestSettingsProvider : IOpenCodexRuntimeSettingsProvider
    {
        private readonly OpenCodexRuntimeSettings _settings;

        public TestSettingsProvider(string dbPath)
        {
            _settings = new OpenCodexRuntimeSettings(
                dbPath,
                "admin",
                "password",
                120);
        }

        public OpenCodexRuntimeSettings GetSettings()
        {
            return _settings;
        }
    }

    private sealed class TestWorkContext : IWorkContext
    {
        private readonly SessionUser _user;

        public TestWorkContext(string username, string role)
        {
            _user = new SessionUser(username, role, true);
        }

        public SessionUser? CurrentUser => _user;

        public bool IsSignedIn => true;

        public bool IsSuperadmin => _user.Role == "superadmin";

        public SessionUser RequireUser()
        {
            return _user;
        }

        public SessionUser RequireSuperadmin()
        {
            return IsSuperadmin
                ? _user
                : throw new UnauthorizedAccessException("superadmin required");
        }
    }
}
