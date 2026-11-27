using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCodex.Api.Services;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.ProtocolConversion;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

[CollectionDefinition("F1 root event budget", DisableParallelization = true)]
public sealed class F1RootEventBudgetCollection;

[Collection("F1 root event budget")]
public sealed class F1WebSocketBudgetRegressionTests
{
    private const string ParameterFileName = "protocol-conversion.parameters.json";
    private const string ProbeFileName = "f1-budget-probe.json";
    private const int BytesPerMegabyte = 1024 * 1024;

    [Fact]
    public async Task NonDefaultRootBudgetIsUsedInAnIsolatedProcess()
    {
        var sourceDirectory = AppContext.BaseDirectory;
        var probePath = Path.Combine(sourceDirectory, ProbeFileName);
        // The parent explicitly creates this manifest only in its isolated output.
        // It selects the child test entry point; it is not a production limit fallback.
        if (File.Exists(probePath))
        {
            using var probe = JsonDocument.Parse(File.ReadAllText(probePath));
            Assert.Equal("isolated-root-budget", probe.RootElement.GetProperty("mode").GetString());
            var expectedBudget = probe.RootElement.GetProperty("expectedBudget").GetInt32();
            Assert.NotEqual(probe.RootElement.GetProperty("originalBudget").GetInt32(), expectedBudget);
            var isolatedParameters = JsonNode.Parse(File.ReadAllText(Path.Combine(sourceDirectory, ParameterFileName)))!.AsObject();
            Assert.Equal(expectedBudget, isolatedParameters["SseMaxEventBytes"]!.GetValue<int>());
            Assert.Equal(expectedBudget, ProtocolConversionParameters.Current.SseMaxEventBytes);
            await VerifyServiceRootBudget(multiAgent: false);
            await VerifyServiceRootBudget(multiAgent: true);
            return;
        }
        var originalParameters = File.ReadAllBytes(Path.Combine(sourceDirectory, ParameterFileName));
        var parameters = JsonNode.Parse(originalParameters)!.AsObject();
        var originalBudget = parameters["SseMaxEventBytes"]!.GetValue<int>();
        // A distinct test budget comes from another explicit root parameter, not a
        // second operational default. Only the isolated output copy is changed.
        var isolatedBudget = parameters["FlushBytes"]!.GetValue<int>();
        Assert.True(isolatedBudget > 0);
        Assert.NotEqual(originalBudget, isolatedBudget);
        parameters["SseMaxEventBytes"] = isolatedBudget;

        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "opencodex-f1-budget-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            CopyOutputDirectory(sourceDirectory, directory);
            File.WriteAllText(Path.Combine(directory, ParameterFileName), parameters.ToJsonString());
            File.WriteAllText(Path.Combine(directory, ProbeFileName), JsonSerializer.Serialize(new
            {
                mode = "isolated-root-budget", expectedBudget = isolatedBudget, originalBudget
            }));
            var assemblyName = Path.GetFileName(typeof(F1WebSocketBudgetRegressionTests).Assembly.Location);
            var results = Path.Combine(directory, "f1-results");
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(Path.Combine(directory, assemblyName));
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName~" +
                typeof(F1WebSocketBudgetRegressionTests).FullName + "." + nameof(NonDefaultRootBudgetIsUsedInAnIsolatedProcess));
            start.ArgumentList.Add("--Logger:trx;LogFileName=f1-isolated.trx");
            start.ArgumentList.Add("--ResultsDirectory:" + results);
            using var process = new Process { StartInfo = start };
            Assert.True(process.Start());
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await stdout + Environment.NewLine + await stderr);

            var report = XDocument.Load(Path.Combine(results, "f1-isolated.trx"));
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var counters = Assert.Single(report.Descendants(ns + "Counters"));
            Assert.Equal("1", counters.Attribute("executed")!.Value);
            Assert.Equal("1", counters.Attribute("passed")!.Value);
            Assert.Equal("0", counters.Attribute("failed")!.Value);
            Assert.Equal(isolatedBudget,
                JsonNode.Parse(File.ReadAllText(Path.Combine(directory, ParameterFileName)))!["SseMaxEventBytes"]!.GetValue<int>());
            Assert.Equal(originalParameters, File.ReadAllBytes(Path.Combine(sourceDirectory, ParameterFileName)));
        }
        finally
        {
            // This directory was created and populated exclusively by this test.
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task VerifyServiceRootBudget(bool multiAgent)
    {
        var parameters = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, ParameterFileName)))!.AsObject();
        var budget = parameters["SseMaxEventBytes"]!.GetValue<int>();
        Assert.True(budget > 0);
        Assert.Equal(budget, ProtocolConversionParameters.Current.SseMaxEventBytes);

        // Challenge the obsolete configuration with an explicitly different limit.
        // Both the challenge value and fixture lengths derive from the root budget.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProxyStreaming:MaxEventBytes"] = checked(2L * budget).ToString(CultureInfo.InvariantCulture),
            // Admit the generated request so this regression measures outbound, not
            // inbound admission. No production request-size policy is changed.
            ["OpenCodex:MaxRequestBodyMb"] = ((2L * budget + BytesPerMegabyte - 1) / BytesPerMegabyte)
                .ToString(CultureInfo.InvariantCulture)
        }).Build();

        var withinId = new string('a', budget / 2);
        using (var context = new MultiAgentApiTestContext(_ => throw new InvalidOperationException("No model call belongs to this budget probe."),
                   configuration: configuration))
        using (var socket = new BudgetSocket(multiAgent ? Interrupt(withinId) : null))
        {
            await Handle(context, socket, Interrupt(withinId), multiAgent, configuration);
            var frame = Assert.Single(socket.Events);
            Assert.Equal("response.interrupt.failed", frame.GetProperty("type").GetString());
            Assert.Equal(withinId, frame.GetProperty("response_id").GetString());
            Assert.Equal("response_not_found", frame.GetProperty("error").GetProperty("code").GetString());
            Assert.Empty(context.FakeEndpoint.Calls);
        }

        // The identity alone fills the entire budget; JSON framing makes the event
        // strictly larger. A preflight failure must precede every socket fragment.
        var oversizedId = new string('a', budget);
        using (var context = new MultiAgentApiTestContext(_ => throw new InvalidOperationException("No model call belongs to this budget probe."),
                   configuration: configuration))
        using (var socket = new BudgetSocket(multiAgent ? Interrupt(oversizedId) : null))
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                Handle(context, socket, Interrupt(oversizedId), multiAgent, configuration));
            Assert.Contains("MaxEventBytes", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, socket.SendAttempts);
            Assert.Empty(socket.Events);
            Assert.Empty(context.FakeEndpoint.Calls);
        }
    }

    private static D Interrupt(string identity) => new()
    {
        ["type"] = "response.interrupt",
        ["response_id"] = identity,
        ["mode"] = "discard_partial_items"
    };

    private static async Task Handle(MultiAgentApiTestContext context, BudgetSocket socket,
        D firstRequest, bool multiAgent, IConfiguration configuration)
    {
        if (multiAgent)
        {
            context.Http.Features.Set<IHttpWebSocketFeature>(new BudgetSocketFeature(socket));
            await context.Service.ResponsesWebSocket();
            return;
        }
        var services = context.Http.RequestServices;
        var service = new ResponsesWebSocketClientService(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ResponsesWebSocketClientService>.Instance,
            services.GetRequiredService<ClientConnectionCache>(),
            services.GetRequiredService<IProxyIdentityContext>(),
            configuration);
        await service.HandleAsync(socket, firstRequest,
            new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            context.Lifetime.Token);
    }

    private static void CopyOutputDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var child in Directory.EnumerateDirectories(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(child));
            Directory.CreateDirectory(target);
            CopyOutputDirectory(child, target);
        }
    }

    private sealed class BudgetSocketFeature(BudgetSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult<WebSocket>(socket);
    }

    private sealed class BudgetSocket : WebSocket
    {
        private readonly byte[] _incoming;
        private readonly MemoryStream _event = new();
        private int _offset;
        private WebSocketState _state = WebSocketState.Open;
        public List<JsonElement> Events { get; } = [];
        public int SendAttempts { get; private set; }

        public BudgetSocket(D? incoming)
        {
            _incoming = incoming is null ? [] : JsonSerializer.SerializeToUtf8Bytes(incoming);
        }
        public override WebSocketCloseStatus? CloseStatus => _state == WebSocketState.Open ? null : WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => _state;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_offset == _incoming.Length)
            {
                _state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }
            var count = Math.Min(buffer.Count, _incoming.Length - _offset);
            _incoming.AsSpan(_offset, count).CopyTo(buffer.AsSpan());
            _offset += count;
            return Task.FromResult(new WebSocketReceiveResult(count, WebSocketMessageType.Text, _offset == _incoming.Length));
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(WebSocketMessageType.Text, type);
            SendAttempts++;
            _event.Write(buffer.AsSpan());
            if (endOfMessage)
            {
                using var document = JsonDocument.Parse(_event.ToArray());
                Events.Add(document.RootElement.Clone());
                _event.SetLength(0);
            }
            return Task.CompletedTask;
        }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
            => CloseOutputAsync(status, description, ct);
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _event.Dispose();
        }
    }
}
