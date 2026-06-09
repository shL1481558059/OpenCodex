using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyImageFallbackTests
{
    [Theory]
    [MemberData(nameof(UserImagePayloads))]
    public void PayloadRewriter_RewritesUserImagesForAllProtocols(
        string protocol,
        Dictionary<string, object?> payload,
        string removedImageMarker)
    {
        var rewriter = new ProxyImagePayloadRewriter();

        var plan = rewriter.Prepare(payload, protocol);
        var rewritten = rewriter.ApplyOcrResults(
            plan,
            [
                new ProxyOcrResult(
                    1,
                    "HELLO",
                    "屏幕截图",
                    ProxyOcrEngines.Vision,
                    ProxyImageSourceKinds.Data,
                    cacheHit: false)
            ]);

        Assert.Single(plan.UserImages);
        Assert.False(ContainsImageMarker(rewritten, removedImageMarker));
        var texts = CollectTextValues(rewritten).ToArray();
        Assert.Contains(texts, text => text.Contains("[图片 1 OCR文字]", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("HELLO", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("[图片 1 描述]", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("屏幕截图", StringComparison.Ordinal));
    }

    [Fact]
    public void PayloadRewriter_RewritesResponsesAssistantAndToolImagesToPlaceholders()
    {
        var rewriter = new ProxyImagePayloadRewriter();
        var payload = new Dictionary<string, object?>
        {
            ["input"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["type"] = "input_image", ["image_url"] = "custom://unsupported" }
                    }
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = "call_1",
                    ["output"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["type"] = "input_image", ["image_url"] = "custom://unsupported" }
                    }
                }
            }
        };

        var plan = rewriter.Prepare(payload, ProtocolConverter.Responses);

        Assert.Empty(plan.UserImages);
        var texts = CollectTextValues(plan.Payload).ToArray();
        Assert.Contains(texts, text => text.Contains("[图片已省略：非用户消息中的图片不会参与 OCR]", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("[工具结果图片已省略：不会参与 OCR]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResponsesTextModelWithImage_UsesVisionOcrAndKeepsMainRoute()
    {
        using var factory = new ProxyImageFallbackApiFactory(
            [
                ResponsesOcrResponse("vision-upstream", "HELLO", "屏幕截图"),
                ResponsesTextResponse("text-upstream", "done")
            ]);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        var cookie = await LoginAndReadSessionCookie(client);
        await ConfigureModelsAsync(client, cookie, includeVisionModel: true);
        var apiKey = await CreateApiKeyAsync(client, cookie, "cli-fallback");

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent.Create(new
            {
                model = "text-model",
                input = new object[]
                {
                    new
                    {
                        type = "message",
                        role = "user",
                        content = new object[]
                        {
                            new { type = "input_text", text = "请看这张图" },
                            new { type = "input_image", image_url = "data:image/png;base64,AAAA" }
                        }
                    }
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var context = OpenCodexDbContextFactory.Create(factory.DbPath);
        var logs = context.RequestLogs.OrderBy(item => item.Id).ToList();
        Assert.Equal(2, logs.Count);
        var mainLog = Assert.Single(logs, item => item.RequestType == ProxyRequestTypes.Main);
        var ocrLog = Assert.Single(logs, item => item.RequestType == ProxyRequestTypes.Ocr);
        Assert.Equal(mainLog.Id, ocrLog.ParentRequestLogId);
        Assert.Equal("text-model", mainLog.Model);
        Assert.Equal("text-upstream", mainLog.UpstreamModel);
        Assert.Equal("vision-model", ocrLog.Model);
        Assert.Equal("vision-upstream", ocrLog.UpstreamModel);
        Assert.Equal("/v1/responses", mainLog.Path);
        Assert.Equal("/internal/ocr/vision", ocrLog.Path);
        Assert.NotEmpty(factory.Upstream.RequestJsons);
        Assert.Contains("\"model\":\"vision-upstream\"", factory.Upstream.RequestJsons[0], StringComparison.Ordinal);
        Assert.Equal(2, factory.Upstream.Requests.Count);
        Assert.Contains("\"model\":\"text-upstream\"", factory.Upstream.RequestJsons[1], StringComparison.Ordinal);
        Assert.Contains("\"input_image\"", factory.Upstream.RequestJsons[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\"input_image\"", factory.Upstream.RequestJsons[1], StringComparison.Ordinal);
        Assert.Contains("[图片 1 OCR文字]", factory.Upstream.RequestJsons[1], StringComparison.Ordinal);
        Assert.Contains("HELLO", factory.Upstream.RequestJsons[1], StringComparison.Ordinal);
        Assert.Contains("[图片 1 描述]", factory.Upstream.RequestJsons[1], StringComparison.Ordinal);

        var mainDetail = context.RequestLogDetails.Single(item => item.RequestLogId == mainLog.Id);
        var ocrDetail = context.RequestLogDetails.Single(item => item.RequestLogId == ocrLog.Id);
        Assert.Contains("data:image/png;base64,AAAA", mainDetail.RequestBody, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,AAAA", ocrDetail.RequestBody, StringComparison.Ordinal);
        Assert.Contains("[图片 1 OCR文字]", mainDetail.UpstreamRequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"input_image\"", mainDetail.UpstreamRequestBody, StringComparison.Ordinal);

        using var ocrJson = JsonDocument.Parse(ocrDetail.OcrJson!);
        Assert.Equal("vision", ocrJson.RootElement.GetProperty("engine").GetString());
        Assert.False(ocrJson.RootElement.GetProperty("cache_hit").GetBoolean());
        Assert.Equal(mainLog.Id, ocrJson.RootElement.GetProperty("parent_request_log_id").GetInt64());
    }

    [Fact]
    public async Task RemoteUrlWithoutVisionModel_Returns400AndWritesOcrChildLog()
    {
        using var factory = new ProxyImageFallbackApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        var cookie = await LoginAndReadSessionCookie(client);
        await ConfigureModelsAsync(client, cookie, includeVisionModel: false);
        var apiKey = await CreateApiKeyAsync(client, cookie, "cli-no-vision");

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent.Create(new
            {
                model = "text-model",
                input = new object[]
                {
                    new
                    {
                        type = "message",
                        role = "user",
                        content = new object[]
                        {
                            new { type = "input_text", text = "请看这张图" },
                            new { type = "input_image", image_url = "https://example.com/image.png" }
                        }
                    }
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("require a configured vision OCR model", body, StringComparison.Ordinal);
        Assert.Empty(factory.Upstream.Requests);

        using var context = OpenCodexDbContextFactory.Create(factory.DbPath);
        var logs = context.RequestLogs.OrderBy(item => item.Id).ToList();
        Assert.Equal(2, logs.Count);
        var mainLog = Assert.Single(logs, item => item.RequestType == ProxyRequestTypes.Main);
        var ocrLog = Assert.Single(logs, item => item.RequestType == ProxyRequestTypes.Ocr);
        Assert.Equal(400, mainLog.StatusCode);
        Assert.Equal(400, ocrLog.StatusCode);
        Assert.Equal("__ocr_paddleocr__", ocrLog.Model);
        Assert.Equal("__ocr_paddleocr__", ocrLog.UpstreamModel);
        Assert.Equal("__local__", ocrLog.ChannelId);
        Assert.Equal("/internal/ocr/paddleocr", ocrLog.Path);
    }

    [Fact]
    public async Task DataImageWithoutVisionModel_UsesLocalPaddleOcrAndKeepsMainRoute()
    {
        using var factory = new ProxyImageFallbackApiFactory(
            [ResponsesTextResponse("text-upstream", "done")],
            new FakeLocalImageOcrService("本地识别文本"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        var cookie = await LoginAndReadSessionCookie(client);
        await ConfigureModelsAsync(client, cookie, includeVisionModel: false);
        var apiKey = await CreateApiKeyAsync(client, cookie, "cli-local-ocr");

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent.Create(new
            {
                model = "text-model",
                input = new object[]
                {
                    new
                    {
                        type = "message",
                        role = "user",
                        content = new object[]
                        {
                            new { type = "input_text", text = "请看这张图" },
                            new { type = "input_image", image_url = "data:image/png;base64,AAAA" }
                        }
                    }
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(factory.Upstream.Requests);
        Assert.Contains("\"model\":\"text-upstream\"", factory.Upstream.RequestJsons[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\"input_image\"", factory.Upstream.RequestJsons[0], StringComparison.Ordinal);
        Assert.Contains("本地识别文本", factory.Upstream.RequestJsons[0], StringComparison.Ordinal);

        using var context = OpenCodexDbContextFactory.Create(factory.DbPath);
        var logs = context.RequestLogs.OrderBy(item => item.Id).ToList();
        Assert.Equal(2, logs.Count);
        var mainLog = Assert.Single(logs, item => item.RequestType == ProxyRequestTypes.Main);
        var ocrLog = Assert.Single(logs, item => item.RequestType == ProxyRequestTypes.Ocr);
        Assert.Equal(mainLog.Id, ocrLog.ParentRequestLogId);
        Assert.Equal("text-model", mainLog.Model);
        Assert.Equal("text-upstream", mainLog.UpstreamModel);
        Assert.Equal("__ocr_paddleocr__", ocrLog.Model);
        Assert.Equal("__ocr_paddleocr__", ocrLog.UpstreamModel);
        Assert.Equal("/internal/ocr/paddleocr", ocrLog.Path);

        using var ocrJson = JsonDocument.Parse(context.RequestLogDetails.Single(item => item.RequestLogId == ocrLog.Id).OcrJson!);
        Assert.Equal("paddleocr", ocrJson.RootElement.GetProperty("engine").GetString());
        Assert.False(ocrJson.RootElement.GetProperty("cache_hit").GetBoolean());
    }

    [Fact]
    public async Task OcrService_UsesCachedRemoteUrlResult_WhenVisionRouteIsUnavailable()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "opencodex-ocr-cache-tests", $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var context = OpenCodexDbContextFactory.Create(dbPath))
        {
            context.Database.EnsureCreated();
        }

        var cachedUrl = "https://example.com/cached-image.png";
        var cacheKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cachedUrl)));
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "opencodex-ocr-cache-tests",
            $"{Guid.NewGuid():N}");
        var cacheDir = Path.Combine(cacheRoot, "results");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(
            Path.Combine(cacheDir, $"{cacheKey}.json"),
            """
            {"Engine":"vision","SourceKind":"url","Text":"CACHED","Description":"缓存命中","CreatedAt":1,"Model":"vision-model","UpstreamModel":"vision-upstream","ChannelId":"vision","ChannelType":"responses"}
            """);

        var settingsProvider = new FixedSettingsProvider(dbPath, cacheRoot);
        var pricing = new ModelPricingService(settingsProvider);
        var logs = new ProxyLogService(settingsProvider, pricing);
        var upstream = new RecordingUpstreamClient();
        var service = new ProxyOcrService(upstream, logs, new FakeLocalImageOcrService(), settingsProvider);

        var result = await service.RecognizeAsync(new ProxyOcrContext(
            "req_cached",
            "admin",
            apiKeyId: null,
            new ProxyRequestMetadata(
                "POST",
                "/v1/responses",
                clientIp: null,
                headers: new Dictionary<string, string>(StringComparer.Ordinal)),
            new ProxyImageInput(
                1,
                ProxyImageSourceKinds.Url,
                cachedUrl,
                imageBytes: null,
                mediaType: string.Empty),
            visionRoute: null,
            defaultTimeout: 120,
            cancellationToken: CancellationToken.None));

        Assert.True(result.CacheHit);
        Assert.Equal("CACHED", result.Text);
        Assert.Equal("vision", result.Engine);
        Assert.Empty(upstream.Requests);

        using var readContext = OpenCodexDbContextFactory.Create(dbPath);
        var ocrLog = Assert.Single(readContext.RequestLogs.Where(item => item.RequestType == ProxyRequestTypes.Ocr));
        Assert.Equal("/internal/ocr/vision", ocrLog.Path);
        using var ocrJson = JsonDocument.Parse(readContext.RequestLogDetails.Single(item => item.RequestLogId == ocrLog.Id).OcrJson!);
        Assert.True(ocrJson.RootElement.GetProperty("cache_hit").GetBoolean());
    }

    public static IEnumerable<object[]> UserImagePayloads()
    {
        yield return
        [
            ProtocolConverter.Responses,
            new Dictionary<string, object?>
            {
                ["input"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "message",
                        ["role"] = "user",
                        ["content"] = new List<object?>
                        {
                            new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = "look" },
                            new Dictionary<string, object?> { ["type"] = "input_image", ["image_url"] = "data:image/png;base64,AAAA" }
                        }
                    }
                }
            },
            "input_image"
        ];

        yield return
        [
            ProtocolConverter.Chat,
            new Dictionary<string, object?>
            {
                ["messages"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = new List<object?>
                        {
                            new Dictionary<string, object?> { ["type"] = "text", ["text"] = "look" },
                            new Dictionary<string, object?>
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new Dictionary<string, object?> { ["url"] = "data:image/png;base64,AAAA" }
                            }
                        }
                    }
                }
            },
            "image_url"
        ];

        yield return
        [
            ProtocolConverter.Messages,
            new Dictionary<string, object?>
            {
                ["messages"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = new List<object?>
                        {
                            new Dictionary<string, object?> { ["type"] = "text", ["text"] = "look" },
                            new Dictionary<string, object?>
                            {
                                ["type"] = "image",
                                ["source"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "base64",
                                    ["media_type"] = "image/png",
                                    ["data"] = "AAAA"
                                }
                            }
                        }
                    }
                }
            },
            "\"type\":\"image\""
        ];
    }

    private static async Task<string> LoginAndReadSessionCookie(HttpClient client)
    {
        var response = await client.PostAsync(
            "/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "admin",
                ["password"] = OpenCodexApiFactory.AdminPassword
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        var cookie = cookies
            .Select(value => value.Split(';', 2)[0])
            .FirstOrDefault(value => value.StartsWith(".AspNetCore.Session=", StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(cookie));
        return cookie!;
    }

    private static async Task ConfigureModelsAsync(HttpClient client, string cookie, bool includeVisionModel)
    {
        var models = new List<object?>
        {
            new { model = "text-model", upstream_model = "text-upstream", supports_image = false }
        };
        if (includeVisionModel)
        {
            models.Add(new { model = "vision-model", upstream_model = "vision-upstream", supports_image = true });
        }

        var response = await SendJsonWithCookie(
            client,
            HttpMethod.Post,
            "/config",
            cookie,
            new
            {
                channels = new[]
                {
                    new
                    {
                        id = "primary",
                        name = "Primary",
                        type = "responses",
                        baseurl = "https://example.test/v1",
                        apikey = "secret",
                        auth_mode = "config",
                        timeout_seconds = 30,
                        retry_count = 0,
                        enabled = true,
                        models = models.ToArray()
                    }
                }
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string> CreateApiKeyAsync(HttpClient client, string cookie, string name)
    {
        var response = await SendJsonWithCookie(
            client,
            HttpMethod.Post,
            "/api-keys",
            cookie,
            new { owner_username = "admin", name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.GetProperty("Data").GetProperty("key").GetProperty("key").GetString()!;
    }

    private static Task<HttpResponseMessage> SendJsonWithCookie(
        HttpClient client,
        HttpMethod method,
        string requestUri,
        string cookie,
        object body)
    {
        var request = new HttpRequestMessage(method, requestUri)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    private static Dictionary<string, object?> ResponsesOcrResponse(
        string model,
        string text,
        string description)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "resp_ocr",
            ["model"] = model,
            ["output"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "output_text",
                            ["text"] = JsonSerializer.Serialize(new Dictionary<string, object?>
                            {
                                ["text"] = text,
                                ["description"] = description
                            })
                        }
                    }
                }
            },
            ["usage"] = new Dictionary<string, object?>
            {
                ["input_tokens"] = 1,
                ["output_tokens"] = 1
            }
        };
    }

    private static Dictionary<string, object?> ResponsesTextResponse(string model, string text)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "resp_main",
            ["model"] = model,
            ["output"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "output_text",
                            ["text"] = text
                        }
                    }
                }
            },
            ["usage"] = new Dictionary<string, object?>
            {
                ["input_tokens"] = 2,
                ["output_tokens"] = 2
            }
        };
    }

    private sealed class ProxyImageFallbackApiFactory : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, object?>[] _responses;
        private readonly ILocalImageOcrService? _localOcr;

        public ProxyImageFallbackApiFactory(
            Dictionary<string, object?>[]? responses = null,
            ILocalImageOcrService? localOcr = null)
        {
            _responses = responses ?? [];
            _localOcr = localOcr;
            Upstream = new RecordingUpstreamClient(_responses);
        }

        public string DbPath { get; } = Path.Combine(
            Path.GetTempPath(),
            "opencodex-image-fallback-tests",
            $"{Guid.NewGuid():N}.db");

        public string OcrCacheDir { get; } = Path.Combine(
            Path.GetTempPath(),
            "opencodex-image-fallback-cache",
            $"{Guid.NewGuid():N}");

        public RecordingUpstreamClient Upstream { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OPENCODEX_ADMIN_USERNAME"] = "admin",
                    ["OPENCODEX_ADMIN_PASSWORD"] = OpenCodexApiFactory.AdminPassword,
                    ["OPENCODEX_DB_PATH"] = DbPath,
                    ["OPENCODEX_DEFAULT_TIMEOUT"] = "120",
                    ["OPENCODEX_OCR_CACHE_DIR"] = OcrCacheDir
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUpstreamClient>();
                services.AddSingleton<IUpstreamClient>(Upstream);
                if (_localOcr is not null)
                {
                    services.RemoveAll<ILocalImageOcrService>();
                    services.AddSingleton(_localOcr);
                }
            });
        }
    }

    private static bool ContainsImageMarker(object? value, string marker)
    {
        foreach (var text in CollectTextValues(value))
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> CollectTextValues(object? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case string text:
                yield return text;
                yield break;
            case Dictionary<string, object?> dictionary:
                foreach (var item in CollectTextValues(dictionary.Values))
                {
                    yield return item;
                }
                yield break;
            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                foreach (var item in CollectTextValues(readOnlyDictionary.Values))
                {
                    yield return item;
                }
                yield break;
            case IEnumerable<object?> list:
                foreach (var item in list)
                {
                    foreach (var nested in CollectTextValues(item))
                    {
                        yield return nested;
                    }
                }
                yield break;
        }
    }

    private sealed class RecordingUpstreamClient : IUpstreamClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly Queue<Dictionary<string, object?>> _responses;

        public RecordingUpstreamClient(params Dictionary<string, object?>[] responses)
        {
            _responses = new Queue<Dictionary<string, object?>>(responses);
        }

        public List<Dictionary<string, object?>> Requests { get; } = [];

        public List<string> RequestJsons { get; } = [];

        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            var copy = DeepCopyObject(payload);
            Requests.Add(copy);
            RequestJsons.Add(JsonSerializer.Serialize(copy, JsonOptions));
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("no upstream response queued");
            }

            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        private static Dictionary<string, object?> DeepCopyObject(IReadOnlyDictionary<string, object?> payload)
        {
            return payload.ToDictionary(
                pair => pair.Key,
                pair => DeepCopyValue(pair.Value),
                StringComparer.Ordinal);
        }

        private static object? DeepCopyValue(object? value)
        {
            if (value is Dictionary<string, object?> dictionary)
            {
                return dictionary.ToDictionary(
                    pair => pair.Key,
                    pair => DeepCopyValue(pair.Value),
                    StringComparer.Ordinal);
            }

            if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
            {
                return readOnlyDictionary.ToDictionary(
                    pair => pair.Key,
                    pair => DeepCopyValue(pair.Value),
                    StringComparer.Ordinal);
            }

            if (value is IEnumerable<object?> list && value is not string)
            {
                return list.Select(DeepCopyValue).ToList();
            }

            return value;
        }
    }

    private sealed class FixedSettingsProvider : IOpenCodexRuntimeSettingsProvider
    {
        private readonly string _dbPath;
        private readonly string _ocrCacheDir;

        public FixedSettingsProvider(string dbPath, string? ocrCacheDir = null)
        {
            _dbPath = dbPath;
            _ocrCacheDir = ocrCacheDir ?? "ocr-cache";
        }

        public OpenCodexRuntimeSettings GetSettings()
        {
            return new OpenCodexRuntimeSettings(
                _dbPath,
                "admin",
                OpenCodexApiFactory.AdminPassword,
                120,
                _ocrCacheDir);
        }
    }

    private sealed class FakeLocalImageOcrService : ILocalImageOcrService
    {
        private readonly string _text;

        public FakeLocalImageOcrService(string text = "")
        {
            _text = text;
        }

        public Task<string> RecognizeTextAsync(
            byte[] imageBytes,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_text);
        }
    }
}
