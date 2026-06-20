using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Api.Configuration;

public sealed class OpenCodexRuntimeSettingsProvider : IOpenCodexRuntimeSettingsProvider
{
    private readonly IConfiguration _configuration;

    public OpenCodexRuntimeSettingsProvider(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public OpenCodexRuntimeSettings GetSettings()
    {
        return new OpenCodexRuntimeSettings(
            DbProvider(),
            DbConnectionString(),
            AdminUsername(),
            (ConfigValue("OpenCodex:AdminPassword", "OPENCODEX_ADMIN_PASSWORD") ?? string.Empty).Trim(),
            PositiveInt("OpenCodex:DefaultTimeout", "OPENCODEX_DEFAULT_TIMEOUT", 120),
            ConfigValue("OpenCodex:OcrCacheDir", "OPENCODEX_OCR_CACHE_DIR") ?? "ocr-cache",
            LocalOcrModel());
    }

    private string DbProvider()
    {
        var value = (ConfigValue("OpenCodex:DbProvider", "OPENCODEX_DB_PROVIDER") ?? "sqlite").Trim();
        return value.Length == 0 ? "sqlite" : value.ToLowerInvariant();
    }

    private string DbConnectionString()
    {
        var value = (ConfigValue("OpenCodex:DbConnectionString", "OPENCODEX_DB_CONNECTION_STRING") ?? string.Empty).Trim();
        if (value.Length > 0)
        {
            return value;
        }

        // 默认 SQLite,文件落在运行目录下的 logs/opencodex.db。
        return "Data Source=logs/opencodex.db";
    }

    private string AdminUsername()
    {
        var username = (ConfigValue("OpenCodex:AdminUsername", "OPENCODEX_ADMIN_USERNAME") ?? "admin").Trim();
        return username.Length == 0 ? "admin" : username;
    }

    private int PositiveInt(string primaryKey, string fallbackKey, int defaultValue)
    {
        var value = ConfigValue(primaryKey, fallbackKey);
        return int.TryParse(value, out var parsedValue) && parsedValue > 0
            ? parsedValue
            : defaultValue;
    }

    private string LocalOcrModel()
    {
        var configured = ConfigValue("OpenCodex:LocalOcrModel", "OPENCODEX_LOCAL_OCR_MODEL");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        var legacy = ConfigValue("OpenCodex:TesseractLang", "OPENCODEX_TESSERACT_LANG");
        var normalized = (legacy ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Contains("eng", StringComparison.Ordinal) && !normalized.Contains("chi", StringComparison.Ordinal)
            ? "EnglishV5"
            : "ChineseV5";
    }

    private string? ConfigValue(string primaryKey, string fallbackKey)
    {
        return _configuration[primaryKey] ?? _configuration[fallbackKey];
    }
}
