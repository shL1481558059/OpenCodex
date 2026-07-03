using OpenCodex.Api.Hosting;

var contentRoot = ResolveContentRoot();
var webRoot = contentRoot is null ? null : Path.Combine(contentRoot, "wwwroot");

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

app.UseOpenCodexApi();
app.Run();

static string? ResolveContentRoot()
{
    var configured = Environment.GetEnvironmentVariable("OPENCODEX_CONTENT_ROOT");
    if (string.IsNullOrWhiteSpace(configured))
    {
        return null;
    }

    return configured.Trim() == "APP_CONTEXT_BASE_DIRECTORY"
        ? AppContext.BaseDirectory
        : Path.GetFullPath(configured.Trim());
}

public partial class Program;
