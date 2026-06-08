using OpenCodex.Api.Configuration;

namespace OpenCodex.Api.Hosting;

public static class OpenCodexHostBuilderExtensions
{
    public static WebApplicationBuilder AddOpenCodexConfiguration(this WebApplicationBuilder builder)
    {
        var dotenvDefaults = DotEnvDefaults.Load(".env", builder.Configuration);
        if (dotenvDefaults.Count > 0)
        {
            builder.Configuration.AddInMemoryCollection(dotenvDefaults);
        }

        return builder;
    }

}
