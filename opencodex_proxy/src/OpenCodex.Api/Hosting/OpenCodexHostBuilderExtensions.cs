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

    public static WebApplicationBuilder UseOpenCodexUrls(this WebApplicationBuilder builder)
    {
        var host = (builder.Configuration["OpenCodex:Host"]
            ?? builder.Configuration["OPENCODEX_HOST"]
            ?? "0.0.0.0").Trim();
        if (host.Length == 0)
        {
            host = "0.0.0.0";
        }

        var portValue = builder.Configuration["OpenCodex:Port"]
            ?? builder.Configuration["OPENCODEX_PORT"];
        var port = int.TryParse(portValue, out var parsedPort) && parsedPort > 0
            ? parsedPort
            : 8000;

        builder.WebHost.UseUrls($"http://{host}:{port}");
        return builder;
    }
}
