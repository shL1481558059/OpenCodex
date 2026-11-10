using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.Data;
using Xunit;
using D=System.Collections.Generic.Dictionary<string,object?>;

namespace OpenCodex.Api.Tests;

public sealed partial class ProxyCompatibilityTests
{
    [Theory]
    [InlineData("chat","chat")][InlineData("chat","responses")][InlineData("chat","messages")]
    [InlineData("responses","chat")][InlineData("responses","responses")][InlineData("responses","messages")]
    [InlineData("messages","chat")][InlineData("messages","responses")][InlineData("messages","messages")]
    public async Task AuthenticatedLoopbackHttp_StreamsBeforeUpstreamFinishesAndPersistsCompleteSqlAudit(string entry,string upstream)
    {
        using var root=new ProxyCompatibilityApiFactory();var source=new BarrierUpstream(upstream);
        using var factory=root.WithWebHostBuilder(builder=>builder.ConfigureTestServices(services=>{services.RemoveAll<IUpstreamClient>();services.AddSingleton<IUpstreamClient>(source);}));
        factory.UseKestrel(0);using var client=factory.CreateClient();
        var key=await ConfigureNetworkChannel(client,upstream);
        using var request=new HttpRequestMessage(HttpMethod.Post,NetworkPath(entry)){Content=JsonContent.Create(NetworkRequest(entry))};
        request.Headers.Authorization=new("Bearer",key);
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var reader=new StreamReader(await response.Content.ReadAsStreamAsync());
        var first=await ReadNetworkEvent(reader);Assert.Contains("data:",first);Assert.False(source.Released.Task.IsCompleted);
        source.Released.TrySetResult();var rest=await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("NETWORK_COMPLETE_TEXT",first+rest);
        await AssertNetworkAudit(root.DbPath,entry!=upstream);Assert.Equal(1,source.Calls);
    }

    [Theory]
    [InlineData("chat")][InlineData("responses")][InlineData("messages")]
    public async Task AuthenticatedLoopbackWs_CrossProtocolUsesRealSocketAndCompleteSqlAudit(string upstream)
    {
        using var root=new ProxyCompatibilityApiFactory();var source=new BarrierUpstream(upstream);
        using var factory=root.WithWebHostBuilder(builder=>builder.ConfigureTestServices(services=>{services.RemoveAll<IUpstreamClient>();services.AddSingleton<IUpstreamClient>(source);}));
        factory.UseKestrel(0);using var client=factory.CreateClient();var key=await ConfigureNetworkChannel(client,upstream);
        using var socket=new ClientWebSocket();socket.Options.SetRequestHeader("Authorization","Bearer "+key);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(new UriBuilder(client.BaseAddress!){Scheme="ws",Path="/v1/responses"}.Uri,stop.Token);
        var payload=NetworkRequest("responses");payload["type"]="response.create";
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload),WebSocketMessageType.Text,true,stop.Token);
        var first=await ReadNetworkWs(socket,stop.Token);Assert.Contains("response.created",first);Assert.False(source.Released.Task.IsCompleted);
        source.Released.TrySetResult();var text=new StringBuilder(first);
        while(!text.ToString().Contains("\"type\":\"response.completed\"",StringComparison.Ordinal))text.Append(await ReadNetworkWs(socket,stop.Token));
        Assert.Contains("NETWORK_COMPLETE_TEXT",text.ToString());await AssertNetworkAudit(root.DbPath,upstream!="responses");Assert.Equal(1,source.Calls);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure,"complete",stop.Token);
    }

    [Fact]
    public async Task AuthenticatedLoopbackHttp_SqlAuditFailureAfterSemanticOutputNeverPublishesSuccessOrReplays()
    {
        using var root=new ProxyCompatibilityApiFactory();var source=new BarrierUpstream("chat");
        using var factory=root.WithWebHostBuilder(builder=>builder.ConfigureTestServices(services=>{services.RemoveAll<IUpstreamClient>();services.AddSingleton<IUpstreamClient>(source);}));
        factory.UseKestrel(0);using var client=factory.CreateClient();var key=await ConfigureNetworkChannel(client,"chat");
        using var request=new HttpRequestMessage(HttpMethod.Post,"/v1/responses"){Content=JsonContent.Create(NetworkRequest("responses"))};request.Headers.Authorization=new("Bearer",key);
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);using var reader=new StreamReader(await response.Content.ReadAsStreamAsync());
        var text=new StringBuilder(await ReadNetworkEvent(reader));
        await InjectNetworkAuditFailure(root.DbPath);
        source.Released.TrySetResult();
        try { while(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) is {} line)text.AppendLine(line); }
        catch(Exception error) when(error is IOException or HttpRequestException){ }
        Assert.DoesNotContain("response.completed",text.ToString());Assert.DoesNotContain("[DONE]",text.ToString());Assert.Equal(1,source.Calls);
    }

    [Fact]
    public async Task AuthenticatedLoopbackWs_SqlAuditFailureAfterSemanticOutputNeverPublishesSuccessOrReplays()
    {
        using var root=new ProxyCompatibilityApiFactory();var source=new BarrierUpstream("chat");
        using var factory=root.WithWebHostBuilder(builder=>builder.ConfigureTestServices(services=>{services.RemoveAll<IUpstreamClient>();services.AddSingleton<IUpstreamClient>(source);}));
        factory.UseKestrel(0);using var client=factory.CreateClient();var key=await ConfigureNetworkChannel(client,"chat");
        using var socket=new ClientWebSocket();socket.Options.SetRequestHeader("Authorization","Bearer "+key);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(new UriBuilder(client.BaseAddress!){Scheme="ws",Path="/v1/responses"}.Uri,stop.Token);
        var payload=NetworkRequest("responses");payload["type"]="response.create";
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload),WebSocketMessageType.Text,true,stop.Token);
        var text=new StringBuilder(await ReadNetworkWs(socket,stop.Token));
        await InjectNetworkAuditFailure(root.DbPath);
        source.Released.TrySetResult();
        while(true)
        {
            var message=await ReadNetworkWs(socket,stop.Token);text.Append(message);
            using var document=JsonDocument.Parse(message);
            var type=document.RootElement.GetProperty("type").GetString();
            if(type is "response.failed" or "error")break;
            Assert.NotEqual("response.completed",type);
        }
        Assert.DoesNotContain("response.completed",text.ToString());Assert.Equal(1,source.Calls);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure,"complete",stop.Token);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task AuthenticatedLoopbackDisconnectCancelsBlockedUpstream(bool webSocket)
    {
        using var root=new ProxyCompatibilityApiFactory();var source=new BarrierUpstream("chat");
        using var factory=root.WithWebHostBuilder(builder=>builder.ConfigureTestServices(services=>{services.RemoveAll<IUpstreamClient>();services.AddSingleton<IUpstreamClient>(source);}));
        factory.UseKestrel(0);using var client=factory.CreateClient();var key=await ConfigureNetworkChannel(client,"chat");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        if(webSocket)
        {
            using var socket=new ClientWebSocket();socket.Options.SetRequestHeader("Authorization","Bearer "+key);
            await socket.ConnectAsync(new UriBuilder(client.BaseAddress!){Scheme="ws",Path="/v1/responses"}.Uri,stop.Token);
            var payload=NetworkRequest("responses");payload["type"]="response.create";
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload),WebSocketMessageType.Text,true,stop.Token);
            Assert.Contains("response.created",await ReadNetworkWs(socket,stop.Token));socket.Abort();
        }
        else
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,"/v1/responses"){Content=JsonContent.Create(NetworkRequest("responses"))};request.Headers.Authorization=new("Bearer",key);
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,stop.Token);
            using var reader=new StreamReader(await response.Content.ReadAsStreamAsync(stop.Token));
            Assert.Contains("data:",await ReadNetworkEvent(reader));stop.Cancel();response.Dispose();
        }
        await source.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(source.Released.Task.IsCompleted);Assert.Equal(1,source.Calls);
    }

    private static async Task InjectNetworkAuditFailure(string path)
    {
        await using var db=OpenCodexDbContextFactory.Create("sqlite",$"Data Source={path}");
        var table=((DbContext)db).Model.FindEntityType(typeof(RequestLogContentRef))!.GetTableName()!;
        // Identifier is EF model metadata, never external/user input.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"CREATE TRIGGER test_audit_fail BEFORE INSERT ON \"{table}\" BEGIN SELECT RAISE(ABORT,'injected audit failure'); END;");
#pragma warning restore EF1002
    }

    [Theory]
    [InlineData("chat",false)][InlineData("messages",false)][InlineData("chat",true)][InlineData("messages",true)]
    public async Task AuthenticatedSearchContinuationPreservesToolExecutionAuditAndKnownRoundUsage(string upstream,bool ws)
    {
        using var root=new ProxyCompatibilityApiFactory();
        var rounds=upstream=="chat"
            ?new[]{WithChatUsage(ChatToolStreamResponse("search","web_search","{\"query\":\"synthetic\"}")),WithChatUsage(ChatTextStreamResponse("NET_SEARCH_FINAL"))}
            :new[]{MessagesToolStreamResponse("search","web_search",new D{["query"]="synthetic"}),MessagesTextStreamResponse("NET_SEARCH_FINAL")};
        var source=new RecordingStreamUpstreamClient(rounds);var search=new CountedSearch();
        using var factory=root.WithWebHostBuilder(builder=>builder.ConfigureTestServices(services=>
        {services.RemoveAll<IUpstreamClient>();services.AddSingleton<IUpstreamClient>(source);services.RemoveAll<OpenCodex.CoreBase.Abstractions.IWebSearchClient>();services.AddSingleton<OpenCodex.CoreBase.Abstractions.IWebSearchClient>(search);}));
        factory.UseKestrel(0);using var client=factory.CreateClient();var key=await ConfigureNetworkChannel(client,upstream);
        await using(var db=OpenCodexDbContextFactory.Create("sqlite",$"Data Source={root.DbPath}"))
        {
            var settings=await db.WebSearchSettings.FirstOrDefaultAsync();if(settings is null){settings=new();db.WebSearchSettings.Add(settings);}
            settings.Mode=OpenCodex.CoreBase.Domain.WebSearch.WebSearchModes.Simulate;settings.KeyUsageLimit=50;
            db.TavilyKeys.Add(new(){Position=0,Provider="tavily",ApiKey="synthetic-only",Enabled=true,UsageLimit=50});await db.SaveChangesAsync();
        }
        var payload=NetworkRequest("responses");payload["tools"]=new List<object?>{new D{["type"]="web_search"}};
        string body;
        if(!ws)
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,"/v1/responses"){Content=JsonContent.Create(payload)};request.Headers.Authorization=new("Bearer",key);
            using var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);body=await response.Content.ReadAsStringAsync();
        }
        else
        {
            using var socket=new ClientWebSocket();socket.Options.SetRequestHeader("Authorization","Bearer "+key);using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new UriBuilder(client.BaseAddress!){Scheme="ws",Path="/v1/responses"}.Uri,stop.Token);payload["type"]="response.create";
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload),WebSocketMessageType.Text,true,stop.Token);
            var text=new StringBuilder();while(true){var frame=await ReadNetworkWs(socket,stop.Token);text.Append(frame);using var json=JsonDocument.Parse(frame);if(json.RootElement.GetProperty("type").GetString()=="response.completed")break;Assert.DoesNotContain("response.failed",frame);}
            body=text.ToString();await socket.CloseAsync(WebSocketCloseStatus.NormalClosure,"complete",stop.Token);
        }
        Assert.Contains("NET_SEARCH_FINAL",body);Assert.Contains("web_search_call",body);Assert.Equal(1,search.Calls);Assert.Equal(2,source.Requests.Count);
        await using var auditDb=OpenCodexDbContextFactory.Create("sqlite",$"Data Source={root.DbPath}");
        var log=Assert.Single(await auditDb.RequestLogs.Where(log=>log.RequestType=="main").ToListAsync());Assert.Equal(200,log.StatusCode);
        Assert.Equal(upstream=="chat"?4:2,log.InputTokens);Assert.Equal(upstream=="chat"?6:2,log.OutputTokens);
        Assert.Contains("NET_SEARCH_FINAL",new LogContentStore(auditDb).Read(log.Id).Get(RequestLogContentSlot.ResponseBody));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task AuthenticatedLargeUnicodeTerminalSurvivesHttpAndWebSocketEncodingWithSqlAudit(bool ws)
    {
        using var root=new ProxyCompatibilityApiFactory();var text=string.Concat(Enumerable.Repeat("中😀<&>\"\n",8192));
        var source=new RecordingStreamUpstreamClient(new[]{WithChatUsage(ChatTextStreamResponse(text))});
        using var factory=root.WithWebHostBuilder(builder=>builder.ConfigureTestServices(services=>{services.RemoveAll<IUpstreamClient>();services.AddSingleton<IUpstreamClient>(source);}));
        factory.UseKestrel(0);using var client=factory.CreateClient();var key=await ConfigureNetworkChannel(client,"chat");var payload=NetworkRequest("responses");
        string terminal;
        if(ws)
        {
            using var socket=new ClientWebSocket();socket.Options.SetRequestHeader("Authorization","Bearer "+key);using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new UriBuilder(client.BaseAddress!){Scheme="ws",Path="/v1/responses"}.Uri,stop.Token);payload["type"]="response.create";
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload),WebSocketMessageType.Text,true,stop.Token);
            while(true){terminal=await ReadNetworkWs(socket,stop.Token);using var frame=JsonDocument.Parse(terminal);if(frame.RootElement.GetProperty("type").GetString()=="response.completed")break;Assert.DoesNotContain("response.failed",terminal);}
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure,"complete",stop.Token);
        }
        else
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,"/v1/responses"){Content=JsonContent.Create(payload)};request.Headers.Authorization=new("Bearer",key);
            using var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);var wire=await response.Content.ReadAsStringAsync();
            terminal=wire.Split('\n').Single(line=>line.StartsWith("data: ",StringComparison.Ordinal)&&line.Contains("\"type\":\"response.completed\"",StringComparison.Ordinal))[6..];
        }
        using var json=JsonDocument.Parse(terminal);Assert.Equal(text,json.RootElement.GetProperty("response").GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
        await using var db=OpenCodexDbContextFactory.Create("sqlite",$"Data Source={root.DbPath}");var log=Assert.Single(await db.RequestLogs.Where(log=>log.RequestType=="main").ToListAsync());
        Assert.Equal(2,log.InputTokens);Assert.Equal(3,log.OutputTokens);using var audit=JsonDocument.Parse(new LogContentStore(db).Read(log.Id).Get(RequestLogContentSlot.ResponseBody)!);
        Assert.Equal(text,audit.RootElement.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());Assert.Single(source.Requests);
    }
    [Theory]
    [InlineData("chat","chat")][InlineData("chat","responses")][InlineData("chat","messages")]
    [InlineData("responses","chat")][InlineData("responses","responses")][InlineData("responses","messages")]
    [InlineData("messages","chat")][InlineData("messages","responses")][InlineData("messages","messages")]
    public async Task AuthenticatedNineProtocolLateTransportFailurePersistsFailureAndNeverReplays(string entry,string upstream)
    {
        using var root=new ProxyCompatibilityApiFactory();var source=new LateNetworkFailure(upstream);
        using var factory=root.WithWebHostBuilder(builder=>builder.ConfigureTestServices(services=>{services.RemoveAll<IUpstreamClient>();services.AddSingleton<IUpstreamClient>(source);}));
        factory.UseKestrel(0);using var client=factory.CreateClient();var key=await ConfigureNetworkChannel(client,upstream,2);
        using var request=new HttpRequestMessage(HttpMethod.Post,NetworkPath(entry)){Content=JsonContent.Create(NetworkRequest(entry))};request.Headers.Authorization=new("Bearer",key);
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);using var reader=new StreamReader(await response.Content.ReadAsStreamAsync());
        var wire=new StringBuilder(await ReadNetworkEvent(reader));source.Release.TrySetResult();
        try { wire.Append(await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10))); }catch(Exception e)when(e is IOException or HttpRequestException){}
        Assert.DoesNotContain("response.completed",wire.ToString());Assert.DoesNotContain("[DONE]",wire.ToString());Assert.DoesNotContain("message_stop",wire.ToString());Assert.Equal(1,source.Calls);
        await using var db=OpenCodexDbContextFactory.Create("sqlite",$"Data Source={root.DbPath}");var log=Assert.Single(await db.RequestLogs.Where(log=>log.RequestType=="main").ToListAsync());
        Assert.True(log.StatusCode>=400);Assert.NotNull(log.CompletedAt);Assert.False(string.IsNullOrWhiteSpace(log.Error));
    }
    [Theory]
    [InlineData("chat")][InlineData("responses")][InlineData("messages")]
    public async Task AuthenticatedWebSocketLateTransportFailureDoesNotReplayAndPersistsFailure(string upstream)
    {
        using var root=new ProxyCompatibilityApiFactory();var source=new LateNetworkFailure(upstream);
        using var factory=root.WithWebHostBuilder(builder=>builder.ConfigureTestServices(services=>{services.RemoveAll<IUpstreamClient>();services.AddSingleton<IUpstreamClient>(source);}));
        factory.UseKestrel(0);using var client=factory.CreateClient();var key=await ConfigureNetworkChannel(client,upstream,2);
        using var socket=new ClientWebSocket();socket.Options.SetRequestHeader("Authorization","Bearer "+key);using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(new UriBuilder(client.BaseAddress!){Scheme="ws",Path="/v1/responses"}.Uri,stop.Token);var payload=NetworkRequest("responses");payload["type"]="response.create";
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload),WebSocketMessageType.Text,true,stop.Token);
        Assert.Contains("response.created",await ReadNetworkWs(socket,stop.Token));source.Release.TrySetResult();
        while(true){var message=await ReadNetworkWs(socket,stop.Token);using var frame=JsonDocument.Parse(message);var type=frame.RootElement.GetProperty("type").GetString();Assert.NotEqual("response.completed",type);if(type is "error" or "response.failed" or "response.incomplete")break;}
        Assert.Equal(1,source.Calls);await socket.CloseAsync(WebSocketCloseStatus.NormalClosure,"complete",stop.Token);
        await using var db=OpenCodexDbContextFactory.Create("sqlite",$"Data Source={root.DbPath}");var log=Assert.Single(await db.RequestLogs.Where(log=>log.RequestType=="main").ToListAsync());Assert.True(log.StatusCode>=400);Assert.False(string.IsNullOrWhiteSpace(log.Error));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task AuthenticatedSlowLoopbackClientBackpressuresAndCancelReleasesSpool(bool ws)
    {
        using var root=new ProxyCompatibilityApiFactory();var source=new FloodNetworkSource();
        var spool=new OpenCodex.CoreBase.Domain.Proxy.ProxyAuditSpool(new(){Directory=Path.Combine(Path.GetDirectoryName(root.DbPath)!,"slow-spool"),MaxTotalBytes=128*1024*1024});
        using var factory=root.WithWebHostBuilder(builder=>builder.ConfigureTestServices(services=>{services.RemoveAll<IUpstreamClient>();services.AddSingleton<IUpstreamClient>(source);services.RemoveAll<OpenCodex.CoreBase.Domain.Proxy.ProxyAuditSpool>();services.AddSingleton(spool);}));
        factory.UseKestrel(0);using var client=factory.CreateClient();var key=await ConfigureNetworkChannel(client,"chat");var payload=NetworkRequest("responses");using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        if(ws)
        {
            using var socket=new ClientWebSocket();socket.Options.SetRequestHeader("Authorization","Bearer "+key);await socket.ConnectAsync(new UriBuilder(client.BaseAddress!){Scheme="ws",Path="/v1/responses"}.Uri,stop.Token);
            payload["type"]="response.create";await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload),WebSocketMessageType.Text,true,stop.Token);await ReadNetworkWs(socket,stop.Token);
            await source.Started.Task.WaitAsync(stop.Token);await Task.Delay(200,stop.Token);Assert.True(source.Produced<10000);socket.Abort();
        }
        else
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,"/v1/responses"){Content=JsonContent.Create(payload)};request.Headers.Authorization=new("Bearer",key);
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,stop.Token);using var reader=new StreamReader(await response.Content.ReadAsStreamAsync(stop.Token));await ReadNetworkEvent(reader);
            await source.Started.Task.WaitAsync(stop.Token);await Task.Delay(200,stop.Token);Assert.True(source.Produced<10000);stop.Cancel();response.Dispose();
        }
        await source.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for(var i=0;i<500&&spool.Usage.Bytes!=0;i++)await Task.Delay(10);
        Assert.Equal((0L,0),spool.Usage);Assert.Equal(1,source.Calls);
    }
    private sealed class FloodNetworkSource:IUpstreamClient
    {
        internal readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Finished=new(TaskCreationOptions.RunContinuationsAsynchronously);internal int Produced,Calls;
        public Task<D> PostJsonAsync(IReadOnlyDictionary<string,object?> channel,IReadOnlyDictionary<string,object?> payload,int timeout,CancellationToken ct)=>throw new NotSupportedException();
        public async IAsyncEnumerable<string> StreamJsonAsync(IReadOnlyDictionary<string,object?> channel,IReadOnlyDictionary<string,object?> payload,int timeout,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);Started.TrySetResult();var frame="data: "+JsonSerializer.Serialize(new D{["id"]="flood",["model"]="upstream-model",["choices"]=new object?[]{new D{["index"]=0,["delta"]=new D{["content"]=new string('x',4096)}}}})+"\n\n";
            try{for(var i=0;i<10000;i++){ct.ThrowIfCancellationRequested();Interlocked.Increment(ref Produced);yield return frame;}await Task.Delay(Timeout.InfiniteTimeSpan,ct);}
            finally{Finished.TrySetResult();}
        }
    }
    private sealed class LateNetworkFailure(string protocol):IUpstreamClient
    {
        internal readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);internal int Calls;
        public Task<D> PostJsonAsync(IReadOnlyDictionary<string,object?> channel,IReadOnlyDictionary<string,object?> payload,int timeout,CancellationToken ct)=>throw new NotSupportedException();
        public async IAsyncEnumerable<string> StreamJsonAsync(IReadOnlyDictionary<string,object?> channel,IReadOnlyDictionary<string,object?> payload,int timeout,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            foreach(var line in BarrierUpstream.NetworkSource(protocol)){yield return line;if(string.IsNullOrWhiteSpace(line))break;}
            await Release.Task.WaitAsync(ct);throw new IOException("injected late local upstream transport failure");
        }
    }
    private static IReadOnlyList<string> WithChatUsage(IReadOnlyList<string> source)
    {
        var lines=source.ToList();var done=lines.FindIndex(line=>line.Contains("[DONE]",StringComparison.Ordinal));
        lines.InsertRange(done,new[]{"data: {\"choices\":[],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":3}}\n","\n"});return lines;
    }
    private sealed class CountedSearch:OpenCodex.CoreBase.Abstractions.IWebSearchClient
    {
        internal int Calls;
        public Task<WebSearchProviderResult> SearchAsync(WebSearchProviderKey key,string query,CancellationToken ct)
        {Interlocked.Increment(ref Calls);return new SuccessfulWebSearchClient().SearchAsync(key,query,ct);}
    }

    private static string NetworkPath(string protocol)=>protocol switch{"chat"=>"/v1/chat/completions","messages"=>"/v1/messages",_=>"/v1/responses"};
    private static D NetworkRequest(string protocol)
    {
        var request=new D{["model"]="public-model",["stream"]=true,["store"]=false};
        if(protocol=="responses")request["input"]="question";
        else{request["messages"]=new List<object?>{new D{["role"]="user",["content"]="question"}};if(protocol=="messages")request["max_tokens"]=128;}
        return request;
    }
    private static async Task<string> ConfigureNetworkChannel(HttpClient client,string protocol,int retries=0)
    {
        var cookie=await LoginAndReadSessionCookie(client);
        using var channel=await SendJsonWithCookie(client,HttpMethod.Post,"/channels",cookie,new{id="loopback",name="Loopback",type=protocol,baseurl="https://example.test/v1",apikey="synthetic-only",auth_mode="config",timeout_seconds=30,retry_count=retries,capacity=3,enabled=true,models=new[]{new{model="public-model",upstream_model="upstream-model",supports_image=false}}});
        Assert.Equal(HttpStatusCode.OK,channel.StatusCode);
        using var created=await SendJsonWithCookie(client,HttpMethod.Post,"/api-keys",cookie,new{owner_username="admin",name="local-network-test"});
        Assert.Equal(HttpStatusCode.Created,created.StatusCode);return await ReadStringProperty(created,"Data","key","key");
    }
    private static async Task AssertNetworkAudit(string path,bool converted)
    {
        await using var db=OpenCodexDbContextFactory.Create("sqlite",$"Data Source={path}");
        var log=Assert.Single(await db.RequestLogs.AsNoTracking().Where(log=>log.RequestType=="main").ToListAsync());
        Assert.Equal(200,log.StatusCode);Assert.Equal(1,log.InputTokens);Assert.Equal(1,log.OutputTokens);Assert.NotNull(log.CompletedAt);Assert.True(string.IsNullOrEmpty(log.Error));
        var audit=new LogContentStore(db).Read(log.Id);
        Assert.Contains("NETWORK_COMPLETE_TEXT",audit.Get(RequestLogContentSlot.UpstreamResponseBody));
        // Existing same-protocol contract stores a null downstream projection and the
        // complete upstream body; cross-protocol routes store both projections.
        if(converted)Assert.Contains("NETWORK_COMPLETE_TEXT",audit.Get(RequestLogContentSlot.ResponseBody));
        else Assert.Equal("null",audit.Get(RequestLogContentSlot.ResponseBody));
    }
    private static async Task<string> ReadNetworkEvent(StreamReader reader)
    {
        var text=new StringBuilder();while(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) is {} line){if(line.Length==0)return text.ToString();text.AppendLine(line);}throw new EndOfStreamException();
    }
    private static async Task<string> ReadNetworkWs(ClientWebSocket socket,CancellationToken ct)
    {
        using var bytes=new MemoryStream();var buffer=new byte[8192];WebSocketReceiveResult item;
        do{item=await socket.ReceiveAsync(buffer,ct);if(item.MessageType==WebSocketMessageType.Close)throw new EndOfStreamException();bytes.Write(buffer,0,item.Count);}while(!item.EndOfMessage);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
    private sealed class BarrierUpstream(string protocol):IUpstreamClient
    {
        public readonly TaskCompletionSource Released=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Finished=new(TaskCreationOptions.RunContinuationsAsynchronously);public int Calls;
        public Task<D> PostJsonAsync(IReadOnlyDictionary<string,object?> channel,IReadOnlyDictionary<string,object?> payload,int timeout,CancellationToken ct)=>throw new NotSupportedException();
        public async IAsyncEnumerable<string> StreamJsonAsync(IReadOnlyDictionary<string,object?> channel,IReadOnlyDictionary<string,object?> payload,int timeout,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);var lines=NetworkSource(protocol);var paused=false;
            try { foreach(var line in lines){ct.ThrowIfCancellationRequested();yield return line;if(!paused&&string.IsNullOrWhiteSpace(line)){paused=true;await Released.Task.WaitAsync(ct);}} }
            finally { Finished.TrySetResult(); }
        }
        internal static IReadOnlyList<string> NetworkSource(string protocol)
        {
            if(protocol=="messages")return MessagesTextStreamResponse("NETWORK_COMPLETE_TEXT");
            if(protocol=="chat")
            {
                var lines=ChatTextStreamResponse("NETWORK_COMPLETE_TEXT").ToList();var index=lines.FindIndex(line=>line.Contains("[DONE]",StringComparison.Ordinal));
                lines.InsertRange(index,new[]{"data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}\n","\n"});return lines;
            }
            var final=ResponsesTextResponse("NETWORK_COMPLETE_TEXT");final["status"]="completed";
            return ToSseEventLines(("response.created",new D{["type"]="response.created",["response"]=new D{["id"]="resp_test",["model"]="upstream-model",["status"]="in_progress",["output"]=new List<object?>()}}),("response.output_text.delta",new D{["type"]="response.output_text.delta",["output_index"]=0,["content_index"]=0,["delta"]="NETWORK_COMPLETE_TEXT"}),("response.completed",new D{["type"]="response.completed",["response"]=final}));
        }
    }
}
