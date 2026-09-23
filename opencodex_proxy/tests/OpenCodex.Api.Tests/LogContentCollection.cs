using Xunit;

namespace OpenCodex.Api.Tests;

/// <summary>
/// 日志内容相关测试共用的串行集合。
/// </summary>
/// <remarks>
/// <see cref="OpenCodex.Core.Services.Proxy.LogContentCodec.CompressionInvocationCount"/>
/// 是进程级静态状态,若这些测试与其他集合并行运行会互相干扰计数。
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LogContentCollection
{
    public const string Name = "LogContent";
}
