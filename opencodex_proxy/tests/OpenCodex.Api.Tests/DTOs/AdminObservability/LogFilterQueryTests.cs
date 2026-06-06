using OpenCodex.Api.DTOs.AdminObservability;

namespace OpenCodex.Api.Tests.DTOs.AdminObservability;

public sealed class LogFilterQueryTests
{
    [Fact]
    public void FromQueryKeepsAllowedFiltersAndDropsEmptyUnknownAndExcludedKeys()
    {
        var query = new Dictionary<string, string?>
        {
            ["model"] = "gpt-4o",
            ["owner_username"] = "admin",
            ["status_code"] = "502",
            ["request_id"] = string.Empty,
            ["unknown"] = "ignored"
        };

        var filters = LogFilterQuery.FromQuery(query, excludedKey: "owner_username");

        Assert.Equal(2, filters.Count);
        Assert.Equal("gpt-4o", filters["model"]);
        Assert.Equal("502", filters["status_code"]);
        Assert.False(filters.ContainsKey("owner_username"));
        Assert.False(filters.ContainsKey("request_id"));
        Assert.False(filters.ContainsKey("unknown"));
    }
}
