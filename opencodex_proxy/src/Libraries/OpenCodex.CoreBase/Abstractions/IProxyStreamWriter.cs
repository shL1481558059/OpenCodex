namespace OpenCodex.CoreBase.Abstractions;

/// <summary>
/// 定义代理流式输出使用的响应写入器。
/// </summary>
public interface IProxyStreamWriter
{
    /// <summary>
    /// 将响应准备为服务器发送事件输出。
    /// </summary>
    void PrepareSse();

    /// <summary>
    /// 将流式响应行写入响应。
    /// </summary>
    /// <param name="lines">要写入的流式响应行。</param>
    /// <param name="countsForTtft">用于判断某一行是否计入首 token 时间的谓词。</param>
    /// <param name="elapsedMilliseconds">返回请求已耗时毫秒数的回调。</param>
    /// <param name="cancellationToken">用于取消写入的令牌。</param>
    /// <returns>首 token 时间，单位为毫秒；如果没有写入可计数行则返回 <see langword="null"/>。</returns>
    Task<int?> WriteLinesAsync(
        IAsyncEnumerable<string> lines,
        Func<string, bool> countsForTtft,
        Func<int> elapsedMilliseconds,
        CancellationToken cancellationToken = default);
}
