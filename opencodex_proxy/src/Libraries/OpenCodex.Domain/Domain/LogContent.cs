namespace OpenCodex.Core.Domain;

/// <summary>
/// 日志正文在内容寻址存储中的用途。枚举值是持久化契约，新增值只能追加。
/// </summary>
public enum RequestLogContentSlot : short
{
    RequestHeaders = 1,
    RequestBody = 2,
    UpstreamRequestBody = 3,
    UpstreamResponseBody = 4,
    ResponseBody = 5,
    WebSearchJson = 6,
    OcrJson = 7,
    StreamLinesJson = 8
}

/// <summary>
/// 未压缩日志内容的物理块。Sha256 对原始 UTF-8 字节计算，Data 可以是可逆压缩结果。
/// </summary>
public sealed class LogContentBlock : BaseEntity<Guid>
{
    public string Sha256 { get; set; } = string.Empty;

    public long RawLength { get; set; }

    public int StoredLength { get; set; }

    public string Compression { get; set; } = "raw";

    public byte[] Data { get; set; } = [];

    public double CreatedAt { get; set; }
}

/// <summary>
/// 一个完整逻辑正文的不可变分块清单。
/// </summary>
public sealed class LogContentManifest : BaseEntity<Guid>
{
    public string Sha256 { get; set; } = string.Empty;

    public long RawLength { get; set; }

    public int ChunkCount { get; set; }

    public string Encoding { get; set; } = "utf-8";
}

/// <summary>
/// manifest 中按顺序排列的物理块引用。
/// </summary>
public sealed class LogContentManifestChunk : BaseEntity<Guid>
{
    public Guid ManifestId { get; set; }

    public int Ordinal { get; set; }

    public Guid BlockId { get; set; }

    public int RawLength { get; set; }
}

/// <summary>
/// 请求日志与正文 manifest 的槽位引用。
/// </summary>
public sealed class RequestLogContentRef : BaseEntity<Guid>
{
    public Guid RequestLogId { get; set; }

    public RequestLogContentSlot Slot { get; set; }

    public Guid ManifestId { get; set; }
}
