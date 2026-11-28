using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenCodex.Core.Domain;
using OpenCodex.ProtocolConversion;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services.Proxy;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Api.Tests;

internal sealed class WebSearchProxyHarness : IDisposable
{
    private readonly string _auditDirectory = Path.Combine(Path.GetTempPath(), "opencodex-search-harness-" + Guid.NewGuid().ToString("N"));
    private readonly IUpstreamClient _upstream;
    private readonly WebSearchToolExecutor _executor;
    private readonly ProxyAuditSpool _auditSpool;
    private readonly List<CapturingLogs> _logs = [];

    public WebSearchProxyHarness(
        IUpstreamClient upstream, IWebSearchClient search,
        IRepository<WebSearchSettings> settings, IRepository<TavilyKey> keys)
    {
        _upstream = upstream;
        _executor = new(search, settings, keys);
        _auditSpool = new ProxyAuditSpool(new() { Directory = _auditDirectory });
    }

    public void Dispose()
    {
        foreach (var logs in _logs) logs.Dispose();
        _logs.Clear();
        _auditSpool.Dispose();
        if (Directory.Exists(_auditDirectory)) Directory.Delete(_auditDirectory, recursive: true);
    }

    public async Task<WebSearchSimulationResult> RunAsync(
        IReadOnlyDictionary<string, object?> channel,
        Dictionary<string, object?> upstreamRequest,
        Dictionary<string, object?> payload,
        string? originalModel,
        int defaultTimeout,
        CancellationToken cancellationToken)
    {
        var history = WebSearchTestStore.Create();
        var logs = new CapturingLogs();
        _logs.Add(logs);
        var (prepared, request, binding, route) = Prepare(channel, upstreamRequest, payload, originalModel);
        var context = new ProxyNonStreamContext(
            Stopwatch.GetTimestamp(), Guid.NewGuid(), "test", "admin", Guid.Empty,
            payload, prepared, request, "responses", route, StringValue(channel, "type"),
            StringValue(channel, "id"), "superadmin", "upstream", originalModel,
            defaultTimeout, Metadata(), cancellationToken) { BuiltinTools = binding };
        var result = await new ProxyNonStreamService(_upstream, logs, _executor, history, _auditSpool).SendAsync(context);
        if (result.FailureException is not null)
        {
            throw result.FailureException;
        }
        var log = logs.Last ?? throw new InvalidOperationException("proxy did not record the request");
        return new WebSearchSimulationResult(
            log.UpstreamRequest!, log.UpstreamResponse, log.ResponsePayload!, log.WebSearchDetails ?? []);
    }

    public async IAsyncEnumerable<string> RunChatStreamAsync(
        IReadOnlyDictionary<string, object?> channel,
        Dictionary<string, object?> upstreamRequest,
        Dictionary<string, object?> payload,
        string? originalModel,
        int defaultTimeout,
        WebSearchStreamResult result,
        Func<IAsyncEnumerable<string>, string, IAsyncEnumerable<string>>? streamCapture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var history = WebSearchTestStore.Create();
        var logs = new CapturingLogs();
        _logs.Add(logs);
        var writer = new CapturingWriter();
        var (prepared, request, binding, route) = Prepare(channel, upstreamRequest, payload, originalModel);
        var context = new ProxyStreamContext(
            Stopwatch.GetTimestamp(), Guid.NewGuid(), "test", "admin", Guid.Empty,
            payload, prepared, request, "responses", route, StringValue(channel, "type"),
            StringValue(channel, "id"), "superadmin", "upstream", originalModel,
            defaultTimeout, Metadata(), writer, cancellationToken) { BuiltinTools = binding };
        await new ProxyStreamService(
            new CapturingUpstream(_upstream, streamCapture), logs, _executor, history, _auditSpool).StreamAsync(context);
        var log = logs.Last ?? throw new InvalidOperationException("proxy did not record the request");
        result.FinalUpstreamRequest = log.UpstreamRequest;
        result.FinalUpstreamResponse = log.UpstreamResponse;
        result.ResponsePayload = log.ResponsePayload;
        result.Details = log.WebSearchDetails;
        foreach (var line in writer.Lines)
        {
            yield return line;
        }
    }

    private (Dictionary<string, object?> Payload, Dictionary<string, object?> Request, BuiltinToolRequestContext? Binding, ProxyRouteDto Route) Prepare(
        IReadOnlyDictionary<string, object?> channel,
        Dictionary<string, object?> upstreamRequest,
        Dictionary<string, object?> payload,
        string? originalModel)
    {
        var prepared = DeepCopyObject(payload);
        var protocol = StringValue(channel, "type");
        var binding = WebSearchRequestPolicy.RegisterBuiltin(
            prepared,
            _executor.CurrentMode(),
            "responses",
            protocol,
            "superadmin",
            WebSearchTestStore.OwnerUserId,
            _executor.CurrentStrict());
        var request = DeepCopyObject(upstreamRequest);
        var definitions = ProtocolConverter.ConvertRequest(prepared, "responses", protocol, "upstream");
        var replacement = ListValue(definitions, "tools").OfType<Dictionary<string, object?>>().Single(tool =>
            StringValue(tool, "name", StringValue(ObjectValue(tool, "function"), "name")) == binding?.WebSearchToolName);
        var tools = ListValue(request, "tools");
        for (var index = 0; index < tools.Count; index++)
        {
            if (TryAsObject(tools[index], out var tool)
                && StringValue(tool, "name", StringValue(ObjectValue(tool, "function"), "name"))
                    is WebSearchRequestPolicy.ToolName or WebSearchRequestPolicy.ToolName)
            {
                tools[index] = DeepCopyObject(replacement);
            }
        }
        WebSearchRequestPolicy.FinalizeUpstreamRequest(request, binding);
        var route = new ProxyRouteDto(DeepCopyObject(channel), originalModel ?? "public", "upstream", false, true);
        return (prepared, request, binding, route);
    }

    private static ProxyRequestMetadata Metadata() => new("POST", "/v1/responses", null, new Dictionary<string, string>());

    private sealed class CapturingUpstream(
        IUpstreamClient inner,
        Func<IAsyncEnumerable<string>, string, IAsyncEnumerable<string>>? capture) : IUpstreamClient
    {
        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel, IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout, CancellationToken cancellationToken) =>
            inner.PostJsonAsync(channel, payload, defaultTimeout, cancellationToken);

        public IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel, IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout, CancellationToken cancellationToken)
        {
            var lines = inner.StreamJsonAsync(channel, payload, defaultTimeout, cancellationToken);
            return capture is null ? lines : capture(lines, "upstream");
        }
    }

    private sealed class CapturingWriter : IProxyStreamWriter
    {
        public List<string> Lines { get; } = [];
        public void PrepareSse() { }
        public async Task<StreamWriteMetrics> WriteLinesAsync(
            IAsyncEnumerable<string> lines, Func<string, bool> countsForTtft,
            Func<int> elapsedMilliseconds, CancellationToken cancellationToken = default)
        {
            await foreach (var line in lines.WithCancellation(cancellationToken))
            {
                Lines.Add(line);
            }
            return new StreamWriteMetrics();
        }
    }

    private sealed class CapturingLogs : IProxyLogService, IDisposable
    {
        private readonly HashSet<ProxyLogBodyHandle> _bodies = [];

        private void Own(ProxyLogBodyHandle? body)
        {
            if (body is not null) _bodies.Add(body);
        }

        public void Dispose()
        {
            foreach (var body in _bodies) body.Dispose();
            _bodies.Clear();
        }

        public ProxyLogContext? Last { get; private set; }
        public Guid CreateQueuedLog(ProxyRequestLogQueuedContext context)
        {
            Own(context.BodyHandles?.Headers);
            Own(context.BodyHandles?.Request);
            Own(context.BodyHandles?.UpstreamRequest);
            return Guid.NewGuid();
        }
        public void MarkProcessing(Guid requestLogId, ProxyRequestLogProcessingContext context)
        {
            Own(context.UpstreamRequestBody);
        }
        public Task CompleteLogAsync(Guid requestLogId, ProxyLogContext context, ProxyRequestMetadata request)
        {
            Own(context.UpstreamRequestBody);
            Last = context;
            return Task.CompletedTask;
        }
        public Task<Guid> WriteLogAsync(ProxyLogContext context, ProxyRequestMetadata request)
        {
            Own(context.UpstreamRequestBody);
            Last = context;
            return Task.FromResult(Guid.NewGuid());
        }
        public Task<Guid> WriteLogAsync(ProxyRequestLogContext context) => Task.FromResult(Guid.NewGuid());
    }
}
