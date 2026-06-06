using OpenCodex.Api.Config;
using OpenCodex.Api.Errors;
using OpenCodex.Api.Routing;

namespace OpenCodex.Api.Tests.Config;

public sealed class ConfigTests
{
    [Fact]
    public void ExpandEnvironmentVariablesInNestedConfig()
    {
        var previous = Environment.GetEnvironmentVariable("OPEN_CODEX_TEST_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPEN_CODEX_TEST_KEY", "secret-value");

            var expanded = ConfigEnvironmentExpander.Expand(Obj(
                ("apikey", "${OPEN_CODEX_TEST_KEY}"),
                ("headers", Obj(("Authorization", "Bearer $OPEN_CODEX_TEST_KEY")))));

            var expandedObject = AssertObject(expanded);
            Assert.Equal("secret-value", expandedObject["apikey"]);
            var headers = AssertObject(expandedObject["headers"]);
            Assert.Equal("Bearer secret-value", headers["Authorization"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPEN_CODEX_TEST_KEY", previous);
        }
    }

    [Fact]
    public void UnknownTopLevelConfigFieldsAreRejected()
    {
        var exception = Assert.Throws<ConfigException>(() =>
            ConfigValidator.Validate(Obj(
                ("channels", List(ValidChannel())),
                ("routing", Obj(("default_channel", "chat"))))));

        Assert.Equal("unsupported config field(s): routing", exception.Message);
    }

    [Fact]
    public void ChannelsMustBeAList()
    {
        var exception = Assert.Throws<ConfigException>(() =>
            ConfigValidator.Validate(Obj(("channels", Obj()))));

        Assert.Equal("channels must be a list", exception.Message);
    }

    [Fact]
    public void EachChannelMustBeAnObject()
    {
        var exception = Assert.Throws<ConfigException>(() =>
            ConfigValidator.Validate(Obj(("channels", List("chat")))));

        Assert.Equal("each channel must be an object", exception.Message);
    }

    [Theory]
    [InlineData("", "channel.id is required")]
    [InlineData("   ", "channel.id is required")]
    public void ChannelIdIsRequired(string id, string expectedMessage)
    {
        var channel = ValidChannel();
        channel["id"] = id;

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public void ChannelTypeMustBeSupported()
    {
        var channel = ValidChannel();
        channel["type"] = "invalid";

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal("channel chat type must be one of ['chat', 'messages', 'responses']", exception.Message);
    }

    [Fact]
    public void ChannelBaseUrlIsRequired()
    {
        var channel = ValidChannel();
        channel["baseurl"] = "";

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal("channel chat baseurl is required", exception.Message);
    }

    [Fact]
    public void ChannelBaseUrlMustStartWithHttp()
    {
        var channel = ValidChannel();
        channel["baseurl"] = "ftp://example.test";

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal("channel chat baseurl must start with http(s)://", exception.Message);
    }

    [Theory]
    [InlineData("pass_through_or_config")]
    [InlineData("pass_through")]
    public void RemovedAuthModesAreRejected(string authMode)
    {
        var channel = ValidChannel();
        channel["auth_mode"] = authMode;

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal("channel chat auth_mode is invalid", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData("30")]
    [InlineData(true)]
    public void TimeoutSecondsMustBePositiveInteger(object timeout)
    {
        var channel = ValidChannel();
        channel["timeout_seconds"] = timeout;

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal("channel chat timeout_seconds must be positive", exception.Message);
    }

    [Fact]
    public void RetryCountAllowsZero()
    {
        var channel = ValidChannel();
        channel["retry_count"] = 0;

        ConfigValidator.Validate(Obj(("channels", List(channel))));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1.5)]
    [InlineData("3")]
    [InlineData(true)]
    public void RetryCountRejectsInvalidValues(object retryCount)
    {
        var channel = ValidChannel();
        channel["retry_count"] = retryCount;

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal("channel chat retry_count must be a non-negative integer", exception.Message);
    }

    [Fact]
    public void HeadersMustBeObject()
    {
        var channel = ValidChannel();
        channel["headers"] = List("X-Test");

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal("channel chat headers must be an object", exception.Message);
    }

    [Fact]
    public void EnabledMustBeBoolean()
    {
        var channel = ValidChannel();
        channel["enabled"] = "false";

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal("channel chat enabled must be a boolean", exception.Message);
    }

    [Fact]
    public void DuplicateChannelIdsAreScopedByOwner()
    {
        var first = ValidChannel();
        first["owner_username"] = "alice";
        var second = ValidChannel();
        second["owner_username"] = "bob";

        ConfigValidator.Validate(Obj(("channels", List(first, second))));
    }

    [Fact]
    public void DuplicateChannelIdsForSameOwnerAreRejected()
    {
        var first = ValidChannel();
        first["owner_username"] = "alice";
        var second = ValidChannel();
        second["owner_username"] = "alice";

        var exception = Assert.Throws<ConfigException>(() =>
            ConfigValidator.Validate(Obj(("channels", List(first, second)))));

        Assert.Equal("duplicated channel id: chat", exception.Message);
    }

    [Fact]
    public void ModelMappingsMustBeList()
    {
        var channel = ValidChannel();
        channel["models"] = "gpt-4";

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal("channel chat models must be a list", exception.Message);
    }

    [Fact]
    public void ModelMappingStringArrayIsRejected()
    {
        var channel = ValidChannel();
        channel["models"] = List("gpt-4");

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal("channel chat models[1] must be an object", exception.Message);
    }

    [Fact]
    public void ModelMappingRequiresModel()
    {
        var channel = ValidChannel();
        channel["models"] = List(Obj(("model", "")));

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal("channel chat models[1].model is required", exception.Message);
    }

    [Fact]
    public void ModelMappingEmptyUpstreamDefaultsToModelDuringNormalize()
    {
        var normalized = ConfigNormalizer.Normalize(Obj(("channels", List(Obj(
            ("id", "chat"),
            ("type", "chat"),
            ("baseurl", "https://example.test/v1"),
            ("models", List(Obj(("model", " gpt-5 "), ("upstream_model", "")))))))));

        var channels = AssertList(normalized["channels"]);
        var channel = AssertObject(channels[0]);
        var models = AssertList(channel["models"]);
        var mapping = AssertObject(models[0]);

        Assert.Equal("gpt-5", mapping["model"]);
        Assert.Equal("gpt-5", mapping["upstream_model"]);
    }

    [Fact]
    public void ModelMappingRejectsDuplicateDownstreamModel()
    {
        var channel = ValidChannel();
        channel["models"] = List(
            Obj(("model", "gpt-5"), ("upstream_model", "gpt-4")),
            Obj(("model", "gpt-5"), ("upstream_model", "gpt-4o")));

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal("channel chat duplicated model mapping: gpt-5", exception.Message);
    }

    [Fact]
    public void UnknownCompatFieldsAreRejected()
    {
        var channel = ValidChannel();
        channel["compat"] = Obj(
            ("drop_params", List("metadata")),
            ("force_protocol", "messages"));

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal("channel chat compat has unsupported field(s): force_protocol", exception.Message);
    }

    [Theory]
    [InlineData("rename_params")]
    [InlineData("force_params")]
    [InlineData("default_params")]
    public void CompatObjectFieldsMustBeObjects(string field)
    {
        var channel = ValidChannel();
        channel["compat"] = Obj((field, List("bad")));

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal($"channel chat compat.{field} must be an object", exception.Message);
    }

    [Theory]
    [InlineData("drop_params")]
    [InlineData("unsupported_params")]
    public void CompatListFieldsMustBeLists(string field)
    {
        var channel = ValidChannel();
        channel["compat"] = Obj((field, Obj(("bad", true))));

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal($"channel chat compat.{field} must be a list", exception.Message);
    }

    [Fact]
    public void CompatFallbackThinkingFlagMustBeBoolean()
    {
        var channel = ValidChannel();
        channel["compat"] = Obj(("fallback_thinking_on_tool_use", "true"));

        var exception = Assert.Throws<ConfigException>(() => ValidateSingle(channel));

        Assert.Equal(
            "channel chat compat.fallback_thinking_on_tool_use must be a boolean",
            exception.Message);
    }

    [Fact]
    public void FirstEnabledChannelIsUsed()
    {
        var config = Obj(("channels", List(
            Obj(("id", "first"), ("type", "chat"), ("baseurl", "https://example.test/v1")),
            Obj(("id", "second"), ("type", "messages"), ("baseurl", "https://example.test/v1")))));

        var result = ChannelRouter.ChooseChannel(config, "mimo-local");

        Assert.Equal("first", result.Channel["id"]);
        Assert.Equal("mimo-local", result.UpstreamModel);
    }

    [Fact]
    public void DisabledChannelsAreSkipped()
    {
        var config = Obj(("channels", List(
            Obj(("id", "disabled"), ("type", "chat"), ("baseurl", "https://example.test/v1"), ("enabled", false)),
            Obj(("id", "enabled"), ("type", "messages"), ("baseurl", "https://example.test/v1")))));

        var result = ChannelRouter.ChooseChannel(config, "gpt-4o");

        Assert.Equal("enabled", result.Channel["id"]);
    }

    [Fact]
    public void AllDisabledChannelsAreNotRouted()
    {
        var config = Obj(("channels", List(
            Obj(("id", "disabled"), ("type", "chat"), ("baseurl", "https://example.test/v1"), ("enabled", false)))));

        var exception = Assert.Throws<RoutingException>(() =>
            ChannelRouter.ChooseChannel(config, "mimo-local"));

        Assert.Equal("no enabled channels configured", exception.Message);
    }

    [Fact]
    public void ModelMappingRoutesAndRewritesUpstreamModel()
    {
        var config = Obj(("channels", List(Obj(
            ("id", "first"),
            ("type", "chat"),
            ("baseurl", "https://example.test/v1"),
            ("models", List(Obj(("model", "gpt-5"), ("upstream_model", "gpt-4"))))))));

        var result = ChannelRouter.ChooseChannel(config, "gpt-5");

        Assert.Equal("first", result.Channel["id"]);
        Assert.Equal("gpt-5", result.OriginalModel);
        Assert.Equal("gpt-4", result.UpstreamModel);
    }

    [Fact]
    public void ModelMappingPrefersFirstEnabledChannel()
    {
        var config = Obj(("channels", List(
            Obj(
                ("id", "first"),
                ("type", "chat"),
                ("baseurl", "https://example.test/v1"),
                ("models", List(Obj(("model", "gpt-5"), ("upstream_model", "gpt-4"))))),
            Obj(
                ("id", "second"),
                ("type", "chat"),
                ("baseurl", "https://second.example.test/v1"),
                ("models", List(Obj(("model", "gpt-5"), ("upstream_model", "gpt-4o"))))))));

        var result = ChannelRouter.ChooseChannel(config, "gpt-5");

        Assert.Equal("first", result.Channel["id"]);
        Assert.Equal("gpt-4", result.UpstreamModel);
    }

    [Fact]
    public void ModelMappingRequiresMatchWhenAnyMappingExists()
    {
        var config = Obj(("channels", List(
            Obj(
                ("id", "first"),
                ("type", "chat"),
                ("baseurl", "https://example.test/v1"),
                ("models", List(Obj(("model", "gpt-5"), ("upstream_model", "gpt-4"))))),
            Obj(("id", "fallback"), ("type", "chat"), ("baseurl", "https://fallback.test/v1")))));

        var exception = Assert.Throws<RoutingException>(() =>
            ChannelRouter.ChooseChannel(config, "gpt-4o"));

        Assert.Equal("no enabled channel configured for model: gpt-4o", exception.Message);
    }

    private static void ValidateSingle(Dictionary<string, object?> channel)
    {
        ConfigValidator.Validate(Obj(("channels", List(channel))));
    }

    private static Dictionary<string, object?> ValidChannel()
    {
        return Obj(
            ("id", "chat"),
            ("type", "chat"),
            ("baseurl", "https://example.test/v1"));
    }

    private static Dictionary<string, object?> Obj(params (string Key, object? Value)[] values)
    {
        return values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    }

    private static List<object?> List(params object?[] values)
    {
        return values.ToList();
    }

    private static Dictionary<string, object?> AssertObject(object? value)
    {
        return Assert.IsType<Dictionary<string, object?>>(value);
    }

    private static List<object?> AssertList(object? value)
    {
        return Assert.IsType<List<object?>>(value);
    }
}
