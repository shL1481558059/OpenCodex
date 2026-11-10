using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using D=System.Collections.Generic.Dictionary<string,object?>;
using static OpenCodex.Api.Tests.MultiAgentApiTestContext;

namespace OpenCodex.Api.Tests;

public sealed class ClientConnectionCacheTests
{
    private static D Value(string value="x")=>new(){["value"]=value};
    [Fact]
    public void ChurnBoundsBothRecordsAndKnownIdsAndCloseReleasesAll()
    {
        using var cache=new ClientConnectionCache(new(){MaxEntries=8,MaxConnectionEntries=4});
        using(var scope=cache.Open(Guid.NewGuid()))
        {
            for(var i=0;i<10_000;i++)
            {
                scope.RememberId("id"+i);scope.RememberResponse("id"+i,Value(),Value(),Value());
                Assert.InRange(cache.Usage.Entries,0,4);Assert.InRange(cache.Usage.Bytes,0,16*1024*1024);
            }
            Assert.Null(scope.FindResponse<D>("id0"));Assert.False(scope.Knows("id0"));Assert.NotNull(scope.FindResponse<D>("id9999"));
        }
        Assert.Equal((0,0L),cache.Usage);
    }
    [Fact]
    public void TenantAndConnectionIsolationAreNotInferredFromStringKeys()
    {
        using var cache=new ClientConnectionCache(new(){MaxTenantBytes=4000,MaxConnectionEntries=10});
        var tenant=Guid.NewGuid();using var a=cache.Open(tenant);using var a2=cache.Open(tenant);using var b=cache.Open(Guid.NewGuid());
        b.RememberResponse("same",Value("tenant-b"),Value(),Value());
        for(var i=0;i<10;i++)a.RememberResponse("a"+i,Value("tenant-a"),Value(),Value());
        Assert.Equal("tenant-b",b.FindResponse<D>("same")!["value"]);Assert.Null(a.FindResponse<D>("same"));Assert.Null(a2.FindResponse<D>("a9"));
        Assert.InRange(cache.Usage.Bytes,1,128*1024*1024);
    }
    [Fact]
    public void IdleTtlRefreshAndOversizeRejectionDoNotRetainGraph()
    {
        var clock=new Clock();using var cache=new ClientConnectionCache(new(){TtlSeconds=2,MaxSnapshotBytes=4096},clock);
        using var scope=cache.Open(Guid.NewGuid());
        Assert.False(scope.RememberResponse("big",Value(),Value(new string('x',10_000)),Value()));Assert.Equal((0,0L),cache.Usage);
        Assert.True(scope.RememberResponse("small",Value(),Value(),Value()));scope.RememberId("small");clock.Advance(1);
        Assert.NotNull(scope.FindResponse<D>("small"));clock.Advance(1);cache.Cleanup();Assert.NotNull(scope.FindResponse<D>("small"));
        clock.Advance(3);cache.Cleanup();Assert.Null(scope.FindResponse<D>("small"));Assert.False(scope.Knows("small"));Assert.Equal((0,0L),cache.Usage);
    }
    [Fact]
    public async Task ConcurrentPutCloseAndCleanupLeaveNoEntriesOrCrossScopeReads()
    {
        using var cache=new ClientConnectionCache(new(){MaxEntries=64,MaxConnectionEntries=8});
        await Task.WhenAll(Enumerable.Range(0,50).Select(async worker=>
        {
            using var scope=cache.Open(Guid.NewGuid());
            await Task.WhenAll(Enumerable.Range(0,30).Select(index=>Task.Run(()=>{scope.RememberResponse("r"+index,Value(),Value(),Value());scope.RememberId("r"+index);cache.Cleanup();})));
        }));
        Assert.Equal((0,0L),cache.Usage);
    }
    [Fact]
    public async Task EvictedWsHistoryReturnsRecoverableErrorWithoutCallingModelAndCloseClearsBudget()
    {
        using var cache=new ClientConnectionCache(new(){MaxConnectionEntries=2});
        using var context=new MultiAgentApiTestContext(ctx=>Stream(ctx,Events(Terminal(Message("unexpected")))),connectionCache:cache);
        context.CatalogEnabled=false;var socket=context.Socket();var operation=context.Service.ResponsesWebSocket();
        try
        {
            D Warm()=>new(){["type"]="response.create",["model"]="fake",["generate"]=false,["input"]="hello"};
            socket.ClientSend(Warm());var first=(D)(await socket.Until("response.completed",context.Lifetime.Token))["response"]!;
            socket.ClientSend(Warm());await socket.Until("response.completed",context.Lifetime.Token);
            socket.ClientSend(new(){["type"]="response.create",["model"]="fake",["previous_response_id"]=first["id"],["input"]="next"});
            var error=await socket.Until("error",context.Lifetime.Token);
            Assert.Equal("previous_response_not_found",JsonDictionaryValue.String(JsonDictionaryValue.Object(error,"error",WebSearchPayload.DeepCopyObject),"code"));
            Assert.Empty(context.FakeEndpoint.Calls);Assert.InRange(cache.Usage.Entries,0,2);
        }
        finally{socket.ClientClose();await operation;}
        Assert.Equal((0,0L),cache.Usage);
    }
    [Fact]
    public async Task WsFailureAfterTerminalAttemptDoesNotSendSecondErrorTerminal()
    {
        using var context=new MultiAgentApiTestContext(async ctx=>
        {
            await Stream(ctx,Events(new D{["type"]="response.failed",["response"]=new D{["id"]="failed-once",["status"]="failed",["output"]=new List<object?>()}}));
            throw new OpenCodex.Core.Errors.UpstreamException("failure after terminal send");
        });
        context.CatalogEnabled=false;var socket=context.Socket();var operation=context.Service.ResponsesWebSocket();
        try{var request=Request();request["type"]="response.create";socket.ClientSend(request);await socket.Until("response.failed",context.Lifetime.Token);}
        finally{socket.ClientClose();await operation;}
        Assert.Single(socket.Sent,item=>Type(item)=="response.failed");Assert.DoesNotContain(socket.Sent,item=>Type(item)=="error"||Type(item)=="response.completed");
    }

    private sealed class Clock:TimeProvider
    {
        private DateTimeOffset _now=DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow()=>_now;
        public void Advance(int seconds)=>_now=_now.AddSeconds(seconds);
    }
}
