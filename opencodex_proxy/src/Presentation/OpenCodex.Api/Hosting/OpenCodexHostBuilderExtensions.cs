using OpenCodex.Api.Configuration;

namespace OpenCodex.Api.Hosting;

public static class OpenCodexHostBuilderExtensions
{
    public static WebApplicationBuilder AddOpenCodexConfiguration(this WebApplicationBuilder builder)
    {
        if (IsTruthy(builder.Configuration["OPENCODEX_DISABLE_DOTENV"]))
        {
            return builder;
        }

        var dotenvPath = builder.Configuration["OPENCODEX_DOTENV_PATH"];
        var dotenvDefaults = DotEnvDefaults.Load(
            string.IsNullOrWhiteSpace(dotenvPath) ? ".env" : dotenvPath.Trim(),
            builder.Configuration);
        if (dotenvDefaults.Count > 0)
        {
            builder.Configuration.AddInMemoryCollection(dotenvDefaults);
        }

        return builder;
    }

    private static bool IsTruthy(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "1" or "true" or "yes" or "on";
    }
}
