using OpenCodex.Api.Hosting;

var contentRoot = NormalizePath(ResolveContentRoot());
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

    var resolved = configured.Trim() == "APP_CONTEXT_BASE_DIRECTORY"
        ? AppContext.BaseDirectory
        : Path.GetFullPath(configured.Trim());

    return NormalizePath(resolved);
}

// Windows 上 Tauri 的 resource_dir() 会返回 \\?\ 前缀路径（扩展长度路径语法）。
// File.Exists / Directory.Exists 能处理它，但 PhysicalFileProvider 的内部路径
// 匹配逻辑对 \\?\ 前缀不一致，导致 UseStaticFiles 查不到文件。去掉该前缀，
// 转换为标准 Windows 路径，所有 .NET API 都能正常处理。
static string? NormalizePath(string? path)
{
    if (string.IsNullOrEmpty(path))
    {
        return path;
    }

    var p = path;
    if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
    {
        p = @"\\" + p[8..];
    }
    else if (p.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
    {
        p = p[4..];
    }

    return Path.GetFullPath(p);
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
