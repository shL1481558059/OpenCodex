using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Domain.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

[Collection(LogContentCollection.Name)]
public sealed class StreamLineJsonSerializerTests
{
    [Fact]
    public void Serialize_MatchesLegacyJsonSerializerOutput()
    {
        var lines = BuildLines();

        var legacyValues = lines
            .OrderBy(item => item.Sequence)
            .Select(item => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sequence"] = item.Sequence,
                ["source"] = item.Source,
                ["raw_line"] = item.RawLine
            })
            .ToList();
        var legacyJson = JsonSerializer.Serialize(legacyValues, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var legacyBytes = Encoding.UTF8.GetBytes(legacyJson);

        var actual = StreamLineJsonSerializer.Serialize(lines);

        Assert.Equal(legacyBytes, actual);
    }

    [Fact]
    public void Serialize_EmptyList_MatchesLegacyOutput()
    {
        var actual = StreamLineJsonSerializer.Serialize([]);

        Assert.Equal(Encoding.UTF8.GetBytes("[]"), actual);
    }

    private static List<ProxyRequestStreamLineCapture> BuildLines()
    {
        return
        [
            new(0, "upstream", "data: {\"id\":\"chatcmpl-1\",\"choices\":[]}"),
            new(1, "downstream", "data: 中文、emoji 😀、换行\n和引号\"与反斜杠\\"),
            new(2, "upstream", "\u0000\u001f 控制字符"),
            new(3, "upstream", "代理对: \ud83d\ude00 与孤立高位 \ud83d"),
            new(4, "upstream", string.Empty),
            new(5, "upstream", "制表符\t与回车\r"),
            new(6, "upstream", "<script>alert(1)</script> & <tag>"),
            new(7, "downstream", "data: [DONE]"),
            new(8, "upstream", new string('x', 4096))
        ];
    }
}
