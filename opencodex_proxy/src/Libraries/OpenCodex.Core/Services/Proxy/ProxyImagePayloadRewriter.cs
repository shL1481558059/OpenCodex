using System.Collections;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyImagePayloadRewriter : IProxyImagePayloadRewriter
{
    private const string NonUserImagePlaceholder = "[图片已省略：非用户消息中的图片不会参与 OCR]";
    private const string ToolImagePlaceholder = "[工具结果图片已省略：不会参与 OCR]";
    private const string MissingOcrText = "[未识别到可提取文字]";
    private const string MissingDescription = "[未生成图片描述]";

    public ProxyImagePayloadRewritePlan Prepare(
        Dictionary<string, object?> payload,
        string entryProtocol)
    {
        var rewritten = DeepCopyObject(payload);
        return entryProtocol switch
        {
            ProtocolConverter.Responses => PrepareResponses(rewritten),
            ProtocolConverter.Chat => PrepareChat(rewritten),
            ProtocolConverter.Messages => PrepareMessages(rewritten),
            _ => new ProxyImagePayloadRewritePlan(rewritten, [], [])
        };
    }

    public Dictionary<string, object?> ApplyOcrResults(
        ProxyImagePayloadRewritePlan plan,
        IReadOnlyList<ProxyOcrResult> results)
    {
        var byImageNumber = results.ToDictionary(result => result.ImageNumber);
        foreach (var target in plan.InjectionTargets.OrderBy(item => item.ImageNumber))
        {
            if (!byImageNumber.TryGetValue(target.ImageNumber, out var result))
            {
                throw new InvalidOperationException($"missing OCR result for image {target.ImageNumber}");
            }

            target.ContentBlocks.Add(TextBlock(
                target.TextBlockType,
                $"[图片 {target.ImageNumber} OCR文字]\n{NormalizeInjectedText(result.Text, MissingOcrText)}"));
            target.ContentBlocks.Add(TextBlock(
                target.TextBlockType,
                $"[图片 {target.ImageNumber} 描述]\n{NormalizeInjectedText(result.Description, MissingDescription)}"));
        }

        return plan.Payload;
    }

    private static ProxyImagePayloadRewritePlan PrepareResponses(Dictionary<string, object?> payload)
    {
        var userImages = new List<ProxyImageInput>();
        var targets = new List<ProxyImageInjectionTarget>();
        if (!TryAsList(GetValue(payload, "input"), out var inputItems))
        {
            return new ProxyImagePayloadRewritePlan(payload, userImages, targets);
        }

        for (var i = 0; i < inputItems.Count; i++)
        {
            if (!TryAsObject(inputItems[i], out var item))
            {
                continue;
            }

            var itemType = StringValue(item, "type");
            if (itemType == "message")
            {
                RewriteResponsesMessage(item, userImages, targets);
                continue;
            }

            if (IsResponsesToolOutput(itemType))
            {
                RewriteResponsesToolOutput(item);
            }
        }

        return new ProxyImagePayloadRewritePlan(payload, userImages, targets);
    }

    private static ProxyImagePayloadRewritePlan PrepareChat(Dictionary<string, object?> payload)
    {
        var userImages = new List<ProxyImageInput>();
        var targets = new List<ProxyImageInjectionTarget>();
        if (!TryAsList(GetValue(payload, "messages"), out var messages))
        {
            return new ProxyImagePayloadRewritePlan(payload, userImages, targets);
        }

        foreach (var messageItem in messages)
        {
            if (!TryAsObject(messageItem, out var message))
            {
                continue;
            }

            var role = StringValue(message, "role");
            RewriteChatLikeMessage(
                message,
                role,
                "content",
                "text",
                "image_url",
                userImages,
                targets,
                ParseChatImage);
        }

        return new ProxyImagePayloadRewritePlan(payload, userImages, targets);
    }

    private static ProxyImagePayloadRewritePlan PrepareMessages(Dictionary<string, object?> payload)
    {
        var userImages = new List<ProxyImageInput>();
        var targets = new List<ProxyImageInjectionTarget>();
        if (!TryAsList(GetValue(payload, "messages"), out var messages))
        {
            return new ProxyImagePayloadRewritePlan(payload, userImages, targets);
        }

        foreach (var messageItem in messages)
        {
            if (!TryAsObject(messageItem, out var message))
            {
                continue;
            }

            var role = StringValue(message, "role");
            RewriteChatLikeMessage(
                message,
                role,
                "content",
                "text",
                "image",
                userImages,
                targets,
                ParseAnthropicImage);
        }

        return new ProxyImagePayloadRewritePlan(payload, userImages, targets);
    }

    private static void RewriteResponsesMessage(
        Dictionary<string, object?> message,
        List<ProxyImageInput> userImages,
        List<ProxyImageInjectionTarget> targets)
    {
        var role = StringValue(message, "role");
        if (!TryAsList(GetValue(message, "content"), out var contentBlocks))
        {
            return;
        }

        var rewrittenBlocks = new List<object?>();
        var imageNumbers = new List<int>();
        foreach (var blockItem in contentBlocks)
        {
            if (!TryAsObject(blockItem, out var block))
            {
                rewrittenBlocks.Add(DeepCopyValue(blockItem));
                continue;
            }

            if (StringValue(block, "type") != "input_image")
            {
                rewrittenBlocks.Add(DeepCopyValue(block));
                continue;
            }

            if (IsUserRole(role))
            {
                var image = ParseResponsesImage(userImages.Count + 1, block);
                userImages.Add(image);
                imageNumbers.Add(image.ImageNumber);
                continue;
            }

            rewrittenBlocks.Add(TextBlock(ResponseTextBlockType(role), PlaceholderForRole(role)));
        }

        message["content"] = rewrittenBlocks;
        foreach (var imageNumber in imageNumbers)
        {
            targets.Add(new ProxyImageInjectionTarget(
                imageNumber,
                rewrittenBlocks,
                ResponseTextBlockType(role)));
        }
    }

    private static void RewriteResponsesToolOutput(Dictionary<string, object?> item)
    {
        RewriteImageListField(item, "output", "input_text", "input_image", ToolImagePlaceholder);
        RewriteImageListField(item, "content", "input_text", "input_image", ToolImagePlaceholder);
    }

    private static void RewriteChatLikeMessage(
        Dictionary<string, object?> message,
        string role,
        string contentKey,
        string textBlockType,
        string imageBlockType,
        List<ProxyImageInput> userImages,
        List<ProxyImageInjectionTarget> targets,
        Func<int, Dictionary<string, object?>, ProxyImageInput> imageParser)
    {
        if (!TryAsList(GetValue(message, contentKey), out var contentBlocks))
        {
            return;
        }

        var rewrittenBlocks = new List<object?>();
        var imageNumbers = new List<int>();
        foreach (var blockItem in contentBlocks)
        {
            if (!TryAsObject(blockItem, out var block))
            {
                rewrittenBlocks.Add(DeepCopyValue(blockItem));
                continue;
            }

            if (StringValue(block, "type") != imageBlockType)
            {
                rewrittenBlocks.Add(DeepCopyValue(block));
                continue;
            }

            if (IsUserRole(role))
            {
                var image = imageParser(userImages.Count + 1, block);
                userImages.Add(image);
                imageNumbers.Add(image.ImageNumber);
                continue;
            }

            rewrittenBlocks.Add(TextBlock(textBlockType, PlaceholderForRole(role)));
        }

        message[contentKey] = rewrittenBlocks;
        foreach (var imageNumber in imageNumbers)
        {
            targets.Add(new ProxyImageInjectionTarget(
                imageNumber,
                rewrittenBlocks,
                textBlockType));
        }
    }

    private static void RewriteImageListField(
        Dictionary<string, object?> container,
        string key,
        string textBlockType,
        string imageBlockType,
        string placeholder)
    {
        var value = GetValue(container, key);
        if (value is null)
        {
            return;
        }

        if (TryAsObject(value, out var singleObject) && StringValue(singleObject, "type") == imageBlockType)
        {
            container[key] = new List<object?> { TextBlock(textBlockType, placeholder) };
            return;
        }

        if (!TryAsList(value, out var items))
        {
            return;
        }

        var rewritten = new List<object?>();
        foreach (var item in items)
        {
            if (!TryAsObject(item, out var block) || StringValue(block, "type") != imageBlockType)
            {
                rewritten.Add(DeepCopyValue(item));
                continue;
            }

            rewritten.Add(TextBlock(textBlockType, placeholder));
        }

        container[key] = rewritten;
    }

    private static ProxyImageInput ParseResponsesImage(int imageNumber, Dictionary<string, object?> block)
    {
        var value = GetValue(block, "image_url");
        if (value is string text)
        {
            return ParseImageReference(imageNumber, text, mediaType: null);
        }

        if (TryAsObject(value, out var imageObject))
        {
            return ParseImageReference(
                imageNumber,
                StringValue(imageObject, "url"),
                StringValue(imageObject, "media_type"));
        }

        throw new BadRequestException("unsupported image source in responses input_image");
    }

    private static ProxyImageInput ParseChatImage(int imageNumber, Dictionary<string, object?> block)
    {
        var value = GetValue(block, "image_url");
        if (value is string text)
        {
            return ParseImageReference(imageNumber, text, mediaType: null);
        }

        if (TryAsObject(value, out var imageObject))
        {
            return ParseImageReference(
                imageNumber,
                StringValue(imageObject, "url"),
                StringValue(imageObject, "media_type"));
        }

        throw new BadRequestException("unsupported image source in chat image_url");
    }

    private static ProxyImageInput ParseAnthropicImage(int imageNumber, Dictionary<string, object?> block)
    {
        if (!TryAsObject(GetValue(block, "source"), out var source))
        {
            throw new BadRequestException("unsupported image source in messages image block");
        }

        var sourceType = StringValue(source, "type");
        if (sourceType == "base64")
        {
            var mediaType = StringValue(source, "media_type");
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                mediaType = "image/png";
            }

            var data = StringValue(source, "data");
            return ParseImageReference(
                imageNumber,
                $"data:{mediaType};base64,{data}",
                mediaType);
        }

        if (sourceType == "url")
        {
            return ParseImageReference(
                imageNumber,
                StringValue(source, "url"),
                StringValue(source, "media_type"));
        }

        throw new BadRequestException("unsupported image source in messages image block");
    }

    private static ProxyImageInput ParseImageReference(
        int imageNumber,
        string imageReference,
        string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(imageReference))
        {
            throw new BadRequestException("unsupported image source: image reference is empty");
        }

        imageReference = imageReference.Trim();
        if (imageReference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return ParseDataUrl(imageNumber, imageReference, mediaType);
        }

        if (Uri.TryCreate(imageReference, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return new ProxyImageInput(
                imageNumber,
                ProxyImageSourceKinds.Url,
                imageReference,
                imageBytes: null,
                mediaType ?? string.Empty);
        }

        throw new BadRequestException("unsupported image source: only data URLs and http(s) URLs are supported");
    }

    private static ProxyImageInput ParseDataUrl(
        int imageNumber,
        string imageReference,
        string? fallbackMediaType)
    {
        var commaIndex = imageReference.IndexOf(',');
        if (commaIndex <= 5)
        {
            throw new BadRequestException("unsupported image source: invalid data URL");
        }

        var metadata = imageReference[5..commaIndex];
        if (!metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            throw new BadRequestException("unsupported image source: only base64 data URLs are supported");
        }

        var mediaType = fallbackMediaType;
        var separatorIndex = metadata.IndexOf(';');
        if (string.IsNullOrWhiteSpace(mediaType) && separatorIndex > 0)
        {
            mediaType = metadata[..separatorIndex];
        }

        if (string.IsNullOrWhiteSpace(mediaType))
        {
            mediaType = "image/png";
        }

        var base64 = imageReference[(commaIndex + 1)..];
        try
        {
            return new ProxyImageInput(
                imageNumber,
                ProxyImageSourceKinds.Data,
                imageReference,
                imageBytes: Convert.FromBase64String(base64),
                mediaType);
        }
        catch (FormatException)
        {
            throw new BadRequestException("unsupported image source: invalid base64 image data");
        }
    }

    private static string NormalizeInjectedText(string? text, string fallback)
    {
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static bool IsResponsesToolOutput(string? itemType)
    {
        return itemType is "function_call_output"
            or "custom_tool_call_output"
            or "local_shell_call_output"
            or "shell_call_output"
            or "apply_patch_call_output"
            or "tool_result";
    }

    private static bool IsUserRole(string role)
    {
        return string.Equals(role, "user", StringComparison.Ordinal);
    }

    private static string PlaceholderForRole(string role)
    {
        return string.Equals(role, "tool", StringComparison.Ordinal)
            ? ToolImagePlaceholder
            : NonUserImagePlaceholder;
    }

    private static string ResponseTextBlockType(string role)
    {
        return string.Equals(role, "assistant", StringComparison.Ordinal)
            ? "output_text"
            : "input_text";
    }

    private static Dictionary<string, object?> TextBlock(string type, string text)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["text"] = text
        };
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
        value = NormalizeValue(value);
        if (TryAsObject(value, out var dictionary))
        {
            return dictionary.ToDictionary(
                pair => pair.Key,
                pair => DeepCopyValue(pair.Value),
                StringComparer.Ordinal);
        }

        if (TryAsList(value, out var list))
        {
            return list.Select(DeepCopyValue).ToList();
        }

        if (value is byte[] bytes)
        {
            return bytes.ToArray();
        }

        return value;
    }

    private static object? GetValue(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out var value) ? NormalizeValue(value) : null;
    }

    private static string StringValue(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return Convert.ToString(GetValue(dictionary, key)) ?? string.Empty;
    }

    private static object? NormalizeValue(object? value)
    {
        return value;
    }

    private static bool TryAsObject(object? value, out Dictionary<string, object?> dictionary)
    {
        if (value is Dictionary<string, object?> typed)
        {
            dictionary = typed;
            return true;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnly)
        {
            dictionary = readOnly.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            return true;
        }

        if (value is IDictionary<string, object?> generic)
        {
            dictionary = generic.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            return true;
        }

        if (value is IDictionary nonGeneric)
        {
            dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in nonGeneric)
            {
                if (entry.Key is string key)
                {
                    dictionary[key] = entry.Value;
                }
            }

            return true;
        }

        dictionary = [];
        return false;
    }

    private static bool TryAsList(object? value, out List<object?> list)
    {
        if (value is List<object?> typed)
        {
            list = typed;
            return true;
        }

        if (value is IReadOnlyList<object?> readOnly)
        {
            list = readOnly.ToList();
            return true;
        }

        if (value is IEnumerable<object?> enumerable)
        {
            list = enumerable.ToList();
            return true;
        }

        if (value is IEnumerable nonGeneric && value is not string && value is not IDictionary)
        {
            list = [];
            foreach (var item in nonGeneric)
            {
                list.Add(item);
            }

            return true;
        }

        list = [];
        return false;
    }
}
