namespace OpenCodex.Api.Hosting;

public static class OpenCodexContentRootResolver
{
    public static string? ResolveContentRoot()
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

    public static string? ResolveWebRoot(string? contentRoot)
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

    // Windows 上 Tauri 的 resource_dir() 会返回 \\?\ 前缀路径（扩展长度路径语法）。
    // File.Exists / Directory.Exists 能处理它，但 PhysicalFileProvider 的内部路径
    // 匹配逻辑对 \\?\ 前缀不一致，导致 UseStaticFiles 查不到文件。去掉该前缀，
    // 转换为标准 Windows 路径，所有 .NET API 都能正常处理。
    private static string? NormalizePath(string? path)
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
}
