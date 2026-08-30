using System.Net;
using Microsoft.EntityFrameworkCore;
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
using OpenCodex.Core.Domain;
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
        ConfigureVisionTransfer(factory.DbPath);
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

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={factory.DbPath}");
        context.Database.Migrate();
        var logs = context.RequestLogs.OrderBy(item => item.Id).ToList();
        var mainLog = Assert.Single(logs, item => item.RequestType == ProxyRequestTypes.Main);
        var ocrLog = Assert.Single(logs, item => item.RequestType == ProxyRequestTypes.Ocr);
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

        var contentStore = new LogContentStore(context);
        var mainDetail = contentStore.Read(mainLog.Id);
        var ocrDetail = contentStore.Read(ocrLog.Id);
        Assert.Contains(
            "data:image/png;base64,AAAA",
            mainDetail.Get(RequestLogContentSlot.RequestBody),
            StringComparison.Ordinal);
        Assert.Contains(
            "data:image/png;base64,AAAA",
            ocrDetail.Get(RequestLogContentSlot.RequestBody),
            StringComparison.Ordinal);
        Assert.Contains(
            "[图片 1 OCR文字]",
            mainDetail.Get(RequestLogContentSlot.UpstreamRequestBody),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"input_image\"",
            mainDetail.Get(RequestLogContentSlot.UpstreamRequestBody),
            StringComparison.Ordinal);

        using var ocrJson = JsonDocument.Parse(ocrDetail.Get(RequestLogContentSlot.OcrJson)!);
        Assert.Equal("vision", ocrJson.RootElement.GetProperty("engine").GetString());
        Assert.False(ocrJson.RootElement.GetProperty("cache_hit").GetBoolean());
        Assert.Equal(mainLog.Id, Guid.Parse(ocrJson.RootElement.GetProperty("parent_request_log_id").GetString()!));
    }

    [Fact]
    public async Task PaddleOcrCache_IsIgnoredAndVisionOcrRunsAgain()
    {
        using var factory = new ProxyImageFallbackApiFactory(
            [
                ResponsesOcrResponse("vision-upstream", "LIVE", "实时识别"),
                ResponsesTextResponse("text-upstream", "done")
            ]);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        var cookie = await LoginAndReadSessionCookie(client);
        await ConfigureModelsAsync(client, cookie, includeVisionModel: true);
        var visionChannelId = ConfigureVisionTransfer(factory.DbPath);

        // 缓存键包含视觉路由身份,所以要用真实的渠道 id 和上游模型名生成。
        var imageBytes = Convert.FromBase64String("AAAA");
        var cacheKey = Convert.ToHexStringLower(SHA256.HashData(
            [.. imageBytes, .. Encoding.UTF8.GetBytes($"|{visionChannelId}|vision-upstream")]));
        var cacheDir = Path.Combine(factory.OcrCacheDir, "results");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(
            Path.Combine(cacheDir, $"{cacheKey}.json"),
            """
            {"Engine":"paddleocr","SourceKind":"data","Text":"STALE","Description":"旧缓存","CreatedAt":1,"Model":"old-model","UpstreamModel":"old-upstream","ChannelId":"old-channel","ChannelType":"responses"}
            """);

        var apiKey = await CreateApiKeyAsync(client, cookie, "cli-paddle-cache");

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
        Assert.Equal(2, factory.Upstream.Requests.Count);
        Assert.Contains("LIVE", factory.Upstream.RequestJsons[1], StringComparison.Ordinal);

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={factory.DbPath}");
        context.Database.Migrate();
        var ocrLog = Assert.Single(context.RequestLogs.Where(item => item.RequestType == ProxyRequestTypes.Ocr));
        var contentStore = new LogContentStore(context);
        using var ocrJson = JsonDocument.Parse(contentStore.Read(ocrLog.Id).Get(RequestLogContentSlot.OcrJson)!);
        Assert.Equal("vision", ocrJson.RootElement.GetProperty("engine").GetString());
        Assert.False(ocrJson.RootElement.GetProperty("cache_hit").GetBoolean());
    }

    [Fact]
    public async Task VisionOcrCacheHit_DoesNotWriteOcrLog()
    {
        using var factory = new ProxyImageFallbackApiFactory(
            [
                ResponsesTextResponse("text-upstream", "done")
            ]);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        var cookie = await LoginAndReadSessionCookie(client);
        await ConfigureModelsAsync(client, cookie, includeVisionModel: true);
        var visionChannelId = ConfigureVisionTransfer(factory.DbPath);

        // 写入命中 vision 引擎的缓存条目,让本次请求直接走缓存而不调用视觉模型。
        var imageBytes = Convert.FromBase64String("AAAA");
        var cacheKey = Convert.ToHexStringLower(SHA256.HashData(
            [.. imageBytes, .. Encoding.UTF8.GetBytes($"|{visionChannelId}|vision-upstream")]));
        var cacheDir = Path.Combine(factory.OcrCacheDir, "results");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(
            Path.Combine(cacheDir, $"{cacheKey}.json"),
            """
            {"Engine":"vision","SourceKind":"data","Text":"CACHED","Description":"缓存描述","CreatedAt":1,"Model":"vision-model","UpstreamModel":"vision-upstream","ChannelId":"vision-channel","ChannelType":"responses"}
            """);

        var apiKey = await CreateApiKeyAsync(client, cookie, "cli-cache-hit");

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
        // 缓存命中后只应有一个主请求,不再有视觉子请求。
        Assert.Single(factory.Upstream.RequestJsons);

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={factory.DbPath}");
        context.Database.Migrate();
        var logs = context.RequestLogs.OrderBy(item => item.Id).ToList();
        var mainLog = Assert.Single(logs, item => item.RequestType == ProxyRequestTypes.Main);
        Assert.DoesNotContain(logs, item => item.RequestType == ProxyRequestTypes.Ocr);

        var contentStore = new LogContentStore(context);
        Assert.Contains(
            "[图片 1 OCR文字]",
            contentStore.Read(mainLog.Id).Get(RequestLogContentSlot.UpstreamRequestBody),
            StringComparison.Ordinal);
        Assert.Contains(
            "CACHED",
            contentStore.Read(mainLog.Id).Get(RequestLogContentSlot.UpstreamRequestBody),
            StringComparison.Ordinal);
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
        Assert.Contains("vision transfer model is not configured", body, StringComparison.Ordinal);
        Assert.Empty(factory.Upstream.Requests);

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={factory.DbPath}");
        context.Database.Migrate();
        var logs = context.RequestLogs.OrderBy(item => item.Id).ToList();
        var mainLog = Assert.Single(logs, item => item.RequestType == ProxyRequestTypes.Main);
        var ocrLog = Assert.Single(logs, item => item.RequestType == ProxyRequestTypes.Ocr);
        Assert.Equal(400, mainLog.StatusCode);
        Assert.Equal(400, ocrLog.StatusCode);
        Assert.Null(ocrLog.Model);
        Assert.Null(ocrLog.UpstreamModel);
        Assert.Null(ocrLog.ChannelId);
        Assert.Equal("/internal/ocr/vision", ocrLog.Path);
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
            .FirstOrDefault(value => value.StartsWith("opencodex_admin_auth=", StringComparison.Ordinal));
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
            "/channels",
            cookie,
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
                capacity = 3,
                enabled = true,
                models = models.ToArray()
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        if (includeVisionModel)
        {
            await EnsureVisionModelInfoAsync(client, cookie);
        }
    }

    /// <summary>
    /// 直接写入该 owner 的图片识别转移配置。移除自动发现后,OCR 链路只认显式配置,
    /// 集成测试必须先落这一行才会触发视觉子请求。
    /// </summary>
    private static Guid ConfigureVisionTransfer(string dbPath, string primaryModel = "vision-model")
    {
        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        context.Database.Migrate();
        var channel = context.Channels.OrderBy(item => item.Position).First();
        context.VisionTransferSettings.Add(new VisionTransferSettings
        {
            Id = Guid.NewGuid(),
            OwnerUserId = channel.OwnerUserId,
            PrimaryChannelId = channel.Id,
            PrimaryModel = primaryModel,
            CreatedAt = 1,
            UpdatedAt = 1
        });
        context.SaveChanges();
        return channel.Id;
    }

    private static async Task EnsureVisionModelInfoAsync(HttpClient client, string cookie)
    {
        var provider = await SendJsonWithCookie(
            client,
            HttpMethod.Post,
            "/model-providers",
            cookie,
            new
            {
                code = "openai",
                name = "OpenAI",
                enabled = true,
                sort_order = 10
            });
        Assert.Equal(HttpStatusCode.Created, provider.StatusCode);

        var response = await SendJsonWithCookie(
            client,
            HttpMethod.Post,
            "/model-infos",
            cookie,
            new
            {
                provider_code = "openai",
                model_key = "vision-upstream",
                display_name = "Vision Upstream",
                match_type = "exact",
                match_pattern = "vision-upstream",
                catalog = new
                {
                    slug = "vision-upstream",
                    display_name = "Vision Upstream",
                    visibility = "list",
                    supported_in_api = true
                },
                capabilities = new
                {
                    supports_image = true,
                    context_window = 128000
                },
                enabled = true
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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

        public ProxyImageFallbackApiFactory(
            Dictionary<string, object?>[]? responses = null)
        {
            _responses = responses ?? [];
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
                    ["OPENCODEX_DB_PROVIDER"] = "sqlite",
                    ["OPENCODEX_DB_CONNECTION_STRING"] = $"Data Source={DbPath}",
                    ["OPENCODEX_DEFAULT_TIMEOUT"] = "120",
                    ["OPENCODEX_OCR_CACHE_DIR"] = OcrCacheDir
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUpstreamClient>();
                services.AddSingleton<IUpstreamClient>(Upstream);
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

}
