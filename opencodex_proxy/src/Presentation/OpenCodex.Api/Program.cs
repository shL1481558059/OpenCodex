using Microsoft.Extensions.DependencyInjection;
using OpenCodex.Api.Hosting;
using OpenCodex.CoreBase.Services;

var contentRoot = OpenCodexContentRootResolver.ResolveContentRoot();
var webRoot = OpenCodexContentRootResolver.ResolveWebRoot(contentRoot);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot,
    WebRootPath = webRoot
});

builder
    .AddOpenCodexConfiguration();

builder.Services.AddOpenCodexApi(builder.Configuration);

var app = builder.Build();

if (args.Contains("--cleanup-legacy-stream-lines", StringComparer.Ordinal))
{
    using var scope = app.Services.CreateScope();
    var cleanup = scope.ServiceProvider.GetRequiredService<IStreamLineLogCleanupService>();
    var result = await cleanup.ExecuteAsync(args.Contains("--dry-run", StringComparer.Ordinal));
    Console.WriteLine(
        $"legacy stream line cleanup (dryRun={result.DryRun}): "
        + $"contentRefs={result.ContentRefs}, manifests={result.Manifests}, "
        + $"manifestChunks={result.ManifestChunks}, blocks={result.Blocks}, "
        + $"rawBytes={result.RawBytes}, storedBytes={result.StoredBytes}");
    return;
}

app.UseOpenCodexApi();
app.Run();

public partial class Program;
