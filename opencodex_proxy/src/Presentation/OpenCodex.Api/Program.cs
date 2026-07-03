using OpenCodex.Api.Hosting;

var contentRoot = ResolveContentRoot();
var webRoot = ResolveWebRoot(contentRoot);

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

WriteStartupDiagnostic(app, contentRoot, webRoot);

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

static string? ResolveWebRoot(string? contentRoot)
{
    var candidates = new List<string?>();
    if (contentRoot is not null)
    {
        candidates.Add(Path.Combine(contentRoot, "wwwroot"));
    }
    candidates.Add(Path.Combine(AppContext.BaseDirectory, "wwwroot"));

    foreach (var candidate in candidates)
    {
        if (candidate is not null && Directory.Exists(candidate))
        {
            return candidate;
        }
    }
    return null;
}

static void WriteStartupDiagnostic(WebApplication app, string? contentRoot, string? webRoot)
{
    try
    {
        var diagPath = Path.Combine(AppContext.BaseDirectory, "opencodex-startup-diagnostic.txt");
        var envContentRoot = Environment.GetEnvironmentVariable("OPENCODEX_CONTENT_ROOT");
        var resolvedContentRoot = app.Environment.ContentRootPath ?? "(null)";
        var resolvedWebRoot = app.Environment.WebRootPath ?? "(null)";

        var lines = new List<string>
        {
            $"Timestamp: {DateTime.Now:O}",
            $"EnvVar OPENCODEX_CONTENT_ROOT: {envContentRoot ?? "(null)"}",
            $"Resolved contentRoot (Program.cs): {contentRoot ?? "(null)"}",
            $"Resolved webRoot (Program.cs): {webRoot ?? "(null)"}",
            $"App ContentRootPath: {resolvedContentRoot}",
            $"App WebRootPath: {resolvedWebRoot}",
            $"AppContext.BaseDirectory: {AppContext.BaseDirectory}",
            $"wwwroot exists at ContentRoot: {Directory.Exists(Path.Combine(resolvedContentRoot, "wwwroot"))}",
            $"wwwroot exists at BaseDirectory: {Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))}",
            $"admin dir exists at wwwroot: {Directory.Exists(Path.Combine(resolvedWebRoot, "admin"))}",
            $"admin/assets dir exists: {Directory.Exists(Path.Combine(resolvedWebRoot, "admin", "assets"))}"
        };

        var assetDir = Path.Combine(resolvedWebRoot, "admin", "assets");
        if (Directory.Exists(assetDir))
        {
            lines.Add("Files in admin/assets (first 15):");
            foreach (var f in Directory.GetFiles(assetDir).Take(15))
            {
                lines.Add($"  {Path.GetFileName(f)}");
            }
        }

        File.WriteAllLines(diagPath, lines);
    }
    catch
    {
        // 诊断日志不应影响正常启动
    }
}

public partial class Program;
