using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenCodex.Api.Hosting;
using OpenCodex.Api.Infrastructure;
using OpenCodex.ProtocolConversion;
using Xunit;

namespace OpenCodex.Api.Tests;

[Collection("Root request capacity")]
public sealed class ProxyRequestBodyLimitTests
{
    private const string CompatibleTooLargeBody = "{\"error\":{\"message\":\"request body too large\",\"type\":\"proxy_error\"}}";

    [Theory]
    [InlineData("OPENCODEX_MAX_REQUEST_BODY_MB")]
    [InlineData("OpenCodex:MaxRequestBodyMb")]
    public void LegacyRequestCapacityKeysAreMigrationErrors(string key)
    {
        // This is a rejected migration input, never an active test/runtime override.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = "1" }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => ProxyRequestLimits.ResolveMaxRequestBodyBytes(configuration));
        Assert.Contains(key, error.Message, StringComparison.Ordinal);
        Assert.Contains("MaxRequestBodyBytes", error.Message, StringComparison.Ordinal);
        Assert.Contains("ProtocolConversion__", error.Message, StringComparison.Ordinal);
        var startup = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddOpenCodexApi(configuration));
        Assert.Contains(key, startup.Message, StringComparison.Ordinal);
        Assert.Contains(".env", startup.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveMaxRequestBodyBytesUsesTheMandatoryRootSource()
    {
        var configuration = new ConfigurationBuilder().Build();
        Assert.Equal(ProtocolConversionParameters.Current.MaxRequestBodyBytes, ProxyRequestLimits.ResolveMaxRequestBodyBytes(configuration));
    }

    [Fact]
    public Task OversizedBody_ReturnsCompatiblePayloadTooLarge() =>
        RootRequestCapacityTestProcess.RunAsync(typeof(ProxyRequestBodyLimitTests), nameof(OversizedBody_ReturnsCompatiblePayloadTooLarge),
            (budget, cancellationToken) => VerifyHttpBodies(budget, [checked(budget + 1)], HttpStatusCode.RequestEntityTooLarge, cancellationToken));

    [Fact]
    public Task BodyWithinLimit_IsNotRejectedByTheLimit() =>
        RootRequestCapacityTestProcess.RunAsync(typeof(ProxyRequestBodyLimitTests), nameof(BodyWithinLimit_IsNotRejectedByTheLimit),
            (budget, cancellationToken) => VerifyHttpBodies(budget, [checked(budget - 1), budget], HttpStatusCode.BadRequest, cancellationToken));

    private static async Task VerifyHttpBodies(int budget, int[] sizes, HttpStatusCode expectedStatus, CancellationToken cancellationToken)
    {
        var directory = AppContext.BaseDirectory;
        using var root = new OpenCodexApiFactory(Path.Combine(directory, "request-capacity.db"), Path.Combine(directory, "request-capacity-keys"));
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Private lifecycle paths only: no request/storage capacity is supplied here.
                ["ProxyAuditSpool:Directory"] = Path.Combine(directory, "request-capacity-spool"),
                ["MultiAgent:StateDirectory"] = Path.Combine(directory, "request-capacity-state")
            })));
        factory.UseKestrel(0);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, HandleCookies = false
        });
        // WebApplicationFactory's default client address is localhost:80 even with
        // dynamic Kestrel enabled; take the actual listener URL after host startup.
        var server = factory.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>();
        Assert.NotNull(addresses);
        client.BaseAddress = new Uri(Assert.Single(addresses.Addresses), UriKind.Absolute);
        Assert.NotNull(client.BaseAddress);
        Assert.True(client.BaseAddress.IsLoopback);
        Assert.True(client.BaseAddress.Port > 0);
        Assert.Equal((long)budget, ProxyRequestLimits.ResolveMaxRequestBodyBytes(factory.Services.GetRequiredService<IConfiguration>()));
        var kestrelBytes = factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize;
        Assert.Equal((long)budget, kestrelBytes);
        var cookie = await LoginAsync(client, cancellationToken);
        var apiKey = await CreateApiKeyAsync(client, cookie, cancellationToken);
        foreach (var size in sizes)
        foreach (var chunked in new[] { false, true })
        {
            var body = RootRequestCapacityTestProcess.JsonBodyAtBytes(size, "gpt-5");
            using HttpContent content = chunked ? new ChunkedJsonContent(body) : new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
            {
                Content = content, Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (chunked)
            {
                request.Headers.TransferEncodingChunked = true;
                Assert.Null(content.Headers.ContentLength);
            }
            else Assert.Equal((long)size, content.Headers.ContentLength);
            var context = $"fixtureP={budget}; actualRootP={ProtocolConversionParameters.Current.MaxRequestBodyBytes}; KestrelP={kestrelBytes}; size={size}; bodyBytes={body.Length}; chunked={chunked}; ContentLength={content.Headers.ContentLength?.ToString() ?? "none"}; TransferEncodingChunked={request.Headers.TransferEncodingChunked}; requestVersion={request.Version}; listenerPort={client.BaseAddress.Port}; cookieUtf8Bytes={Encoding.UTF8.GetByteCount(cookie)}; apiKeyCharacters={apiKey.Length}; rootE={ProtocolConversionParameters.Current.SseMaxEventBytes}; expectedStatus={(int)expectedStatus}";
            HttpResponseMessage? response = null;
            string? responseBody = null;
            var stage = "send-response-headers";
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                stage = "read-response-body";
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                stage = "assert-status";
                Assert.Equal(expectedStatus, response.StatusCode);
                if (expectedStatus == HttpStatusCode.RequestEntityTooLarge)
                {
                    stage = "assert-exact-compatible-413-body";
                    Assert.Equal(CompatibleTooLargeBody, responseBody);
                }
            }
            catch (Exception exception)
            {
                // Preserve strict assertions, but retain iteration facts after the
                // isolated child directory is deleted. Never dump credentials,
                // private configuration, raw response contents, or exception text.
                throw new Xunit.Sdk.XunitException($"HTTP capacity iteration failed: {context}; stage={stage}; {SafeResponseSummary(response, responseBody)}; failure={SafeFailureSummary(exception)}");
            }
            finally { response?.Dispose(); }
            // No channel is configured: valid P-1/P bodies retain the original 400
            // business result, rather than being intercepted as request-size 413.
        }
    }

    private static string SafeResponseSummary(HttpResponseMessage? response, string? body)
    {
        if (response is null) return "responseHeaders=none; responseBody=unread";
        var media = response.Content.Headers.ContentType?.MediaType switch
        {
            "application/json" => "json", "text/plain" => "text", null => "none", _ => "other"
        };
        var bytes = body is null ? null : Encoding.UTF8.GetBytes(body);
        var hash = bytes is null ? "unread" : Convert.ToHexStringLower(SHA256.HashData(bytes));
        return $"responseStatus={(int)response.StatusCode}; responseVersion={response.Version}; responseContentLength={response.Content.Headers.ContentLength?.ToString() ?? "none"}; media={media}; responseUtf8Bytes={bytes?.LongLength.ToString() ?? "unread"}; responseSha256={hash}; exactCompatible413={string.Equals(body, CompatibleTooLargeBody, StringComparison.Ordinal)}";
    }

    private static string SafeFailureSummary(Exception exception)
    {
        var failures = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var kind = current.GetType().FullName;
            failures.Add(current switch
            {
                HttpRequestException http => $"{kind}; HttpRequestError={http.HttpRequestError}",
                HttpIOException io => $"{kind}; HttpRequestError={io.HttpRequestError}",
                _ => kind ?? current.GetType().Name
            });
        }
        return string.Join(" -> ", failures);
    }

    private static async Task<string> LoginAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin", ["password"] = OpenCodexApiFactory.AdminPassword
        }), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        return cookies.Select(value => value.Split(';', 2)[0])
            .First(value => value.StartsWith("opencodex_admin_auth=", StringComparison.Ordinal));
    }

    private static async Task<string> CreateApiKeyAsync(HttpClient client, string cookie, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api-keys")
        {
            Content = JsonContent.Create(new { owner_username = "admin", name = $"limit-test-{Guid.NewGuid():N}" })
        };
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.GetProperty("Data").GetProperty("key").GetProperty("key").GetString()!;
    }

    private sealed class ChunkedJsonContent(byte[] body) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body, 0, body.Length);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            stream.WriteAsync(body, cancellationToken).AsTask();
    }
}
