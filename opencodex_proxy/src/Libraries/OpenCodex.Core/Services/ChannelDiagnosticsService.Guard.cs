using System.Net;
using System.Text.RegularExpressions;
using OpenCodex.Core.Config;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services;

public sealed partial class ChannelDiagnosticsService
{
    // 诊断是一次性探活,超时超过 60s 视为失控,直接钳到上限。
    private const int MaxDiagnosticTimeoutSeconds = 60;
    // 诊断语义是单次探活,禁止重试以免放大上游计费。
    private const int DiagnosticRetryCount = 0;
    // 探活用最小输出即可,避免无意义的大额生成计费。
    private const int MaxDiagnosticOutputTokens = 1024;
    // 探活输入超过 4000 字符会被截断,防止把大段上下文发给上游。
    private const int MaxDiagnosticInputLength = 4000;

    [GeneratedRegex(@"\$\{(?<braced>[A-Za-z_][A-Za-z0-9_]*)\}|\$(?<plain>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex EnvironmentPlaceholderRegex();

    private static void RejectEnvironmentPlaceholders(
        IReadOnlyDictionary<string, object?> channel)
    {
        foreach (var value in channel.Values)
        {
            RejectPlaceholderValue(value);
        }
    }

    private static void RejectPlaceholderValue(object? value)
    {
        switch (value)
        {
            case string text when EnvironmentPlaceholderRegex().IsMatch(text):
                throw new ConfigException("diagnostics does not allow environment variable placeholders");
            case IReadOnlyDictionary<string, object?> dictionary:
                RejectEnvironmentPlaceholders(dictionary);
                break;
            case IReadOnlyList<object?> list:
                foreach (var item in list)
                {
                    RejectPlaceholderValue(item);
                }
                break;
        }
    }

    private static void EnsurePublicBaseUrl(IReadOnlyDictionary<string, object?> channel)
    {
        var baseUrl = JsonDictionaryValue.String(channel, "baseurl");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ConfigException("diagnostics does not allow non-public baseurl host");
        }

        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "metadata.google.internal", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigException("diagnostics does not allow non-public baseurl host");
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            // 域名不做 DNS 解析,无法判断是否私有网段,直接放行。
            return;
        }

        if (IsLoopback(address)
            || IsPrivate(address)
            || IsLinkLocal(address)
            || IsCloudMetadata(address))
        {
            throw new ConfigException("diagnostics does not allow non-public baseurl host");
        }
    }

    private static bool IsLoopback(IPAddress address)
    {
        return IPAddress.IsLoopback(address)
            || (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && address.GetAddressBytes()[0] == 127);
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            // fc00::/7
            return (bytes[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 169 && bytes[1] == 254;
        }

       if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
       {
           var bytes = address.GetAddressBytes();
           // fe80::/10
            return bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80;
       }

        return false;
    }

    private static bool IsCloudMetadata(IPAddress address)
    {
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && address.Equals(IPAddress.Parse("169.254.169.254"));
    }

    private static Dictionary<string, object?> ClampDiagnosticsChannel(
        IReadOnlyDictionary<string, object?> channel)
    {
        var clamped = CloneObject(channel);
        if (clamped.TryGetValue("timeout_seconds", out var timeout)
            && timeout is int timeoutSeconds
            && timeoutSeconds > MaxDiagnosticTimeoutSeconds)
        {
            clamped["timeout_seconds"] = MaxDiagnosticTimeoutSeconds;
        }

        clamped["retry_count"] = DiagnosticRetryCount;
        return clamped;
    }

    private static void ClampDiagnosticsPayloadInputs(
        Dictionary<string, object?> requestBody)
    {
        if (requestBody.TryGetValue("max_output_tokens", out var maxOutputTokens)
            && maxOutputTokens is int maxTokens)
        {
            requestBody["max_output_tokens"] = Math.Clamp(maxTokens, 1, MaxDiagnosticOutputTokens);
        }

        var input = JsonDictionaryValue.Get(requestBody, "input");
        if (input is string inputText && inputText.Length > MaxDiagnosticInputLength)
        {
            requestBody["input"] = inputText[..MaxDiagnosticInputLength];
        }
    }
}
