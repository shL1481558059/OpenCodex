using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using OpenCodex.ProtocolConversion.Json;
namespace OpenCodex.ProtocolConversion.Streaming;
/// <summary>One complete JSON WS message. Large string values encode in bounded
/// segments; every send is awaited and only the last fragment ends the message.</summary>
public static class WebSocketJsonWriter
{
    public static async Task SendAsync(WebSocket socket,IReadOnlyDictionary<string,object?> payload,int maxBytes,CancellationToken ct)
        => await SendAsync(socket,payload,maxBytes,ct,null,null);

    public static async Task SendAsync(
        WebSocket socket,
        IReadOnlyDictionary<string,object?> payload,
        int maxBytes,
        CancellationToken ct,
        string? typeOverride,
        IReadOnlySet<string>? excludedProperties)
    {
        ct.ThrowIfCancellationRequested();
        if(maxBytes<=0)throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if(typeOverride is null&&excludedProperties is null&&GeneratedSseSerialization.IsSmall(payload,null)&&IsPlainJson(payload))
        {
            var bytes=JsonSerializer.SerializeToUtf8Bytes(payload);if(bytes.Length>maxBytes)throw new InvalidDataException("WebSocket event exceeds configured MaxEventBytes.");
            await socket.SendAsync(bytes,WebSocketMessageType.Text,true,ct);return;
        }
        // Preflight the complete event BEFORE any fragment is attempted. This uses
        // the same default encoder as the old JsonSerializer wire, not relaxed SSE JSON.
        using(var count=new CountStream(maxBytes,ct))using(var json=new Utf8JsonWriter(count))WriteRoot(json,payload,typeOverride,excludedProperties);
        var buffer=new ArrayBufferWriter<byte>(16*1024);using var writer=new Utf8JsonWriter(buffer);var sent=0;
        await Value(payload,0,true);await Drain(true);
        async ValueTask Value(object? value,int depth,bool root=false)
        {
            ct.ThrowIfCancellationRequested();if(depth>64)throw new JsonException("JSON depth limit exceeded.");
            switch(value)
            {
                case string text:
                    if(text.Length==0)writer.WriteStringValue("");
                    for(var offset=0;offset<text.Length;)
                    {
                        var size=Math.Min(1024,text.Length-offset);if(offset+size<text.Length&&char.IsHighSurrogate(text[offset+size-1]))size--;
                        writer.WriteStringValueSegment(text.AsSpan(offset,size),offset+size==text.Length);offset+=size;await Drain(false);
                    }
                    break;
                case IReadOnlyDictionary<string,object?> map:
                    writer.WriteStartObject();
                    if(root&&typeOverride is not null)writer.WriteString("type",typeOverride);
                    foreach(var pair in map)
                    {
                        if(root&&(pair.Key=="type"&&typeOverride is not null||excludedProperties?.Contains(pair.Key)==true))continue;
                        writer.WritePropertyName(pair.Key);await Value(pair.Value,depth+1);
                    }
                    writer.WriteEndObject();break;
                case IReadOnlyList<object?> items:
                    writer.WriteStartArray();foreach(var item in items)await Value(item,depth+1);writer.WriteEndArray();break;
                default:JsonSerializer.Serialize(writer,value);break;
            }
            await Drain(false);
        }
        async ValueTask Drain(bool final)
        {
            writer.Flush();if(!final&&buffer.WrittenCount<16*1024)return;
            if(buffer.WrittenCount>maxBytes-sent)throw new InvalidDataException("WebSocket event changed beyond configured MaxEventBytes; output was not truncated.");
            sent+=buffer.WrittenCount;
            await socket.SendAsync(buffer.WrittenMemory,WebSocketMessageType.Text,final,ct);buffer.Clear();
        }
    }

    private static void WriteRoot(
        Utf8JsonWriter writer,
        IReadOnlyDictionary<string,object?> payload,
        string? typeOverride,
        IReadOnlySet<string>? excludedProperties)
    {
        writer.WriteStartObject();
        if(typeOverride is not null)writer.WriteString("type",typeOverride);
        foreach(var pair in payload)
        {
            if(pair.Key=="type"&&typeOverride is not null||excludedProperties?.Contains(pair.Key)==true)continue;
            writer.WritePropertyName(pair.Key);
            ProtocolJsonWriter.Write(writer,pair.Value,1);
        }
        writer.WriteEndObject();
    }
    // Unknown extension DTOs may hide a large string: they cannot use the small
    // whole-message allocation path merely because their CLR wrapper is small.
    private static bool IsPlainJson(object? value) => value switch
    {
        null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => true,
        IReadOnlyDictionary<string, object?> map => map.Values.All(IsPlainJson),
        IReadOnlyList<object?> items => items.All(IsPlainJson),
        _ => false
    };
    private sealed class CountStream(int maxBytes,CancellationToken ct):Stream
    {
        private long _count;
        public override void Write(ReadOnlySpan<byte> bytes){ct.ThrowIfCancellationRequested();_count+=bytes.Length;if(_count>maxBytes)throw new InvalidDataException("WebSocket event exceeds configured MaxEventBytes; output was not truncated.");}
        public override void Write(byte[] bytes,int offset,int count)=>Write(bytes.AsSpan(offset,count));
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){}public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long v)=>throw new NotSupportedException();
    }
}
