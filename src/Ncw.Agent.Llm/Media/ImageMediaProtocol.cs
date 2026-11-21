using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Auth;
using Ncw.Core.Domain;

namespace Ncw.Agent.Llm.Media;

/// <summary>图片输入(公网 URL 或 <c>data:image/...;base64,...</c>)。</summary>
public sealed record ImageInput(string DataRef, string? Mime = null);

/// <summary>
/// 一次图片生成或图生图 / 改图请求。
/// 当 <see cref="Images"/> 非空时:
/// <list type="bullet">
/// <item><see cref="ImageProtocol.VolcengineImages"/> 走同一个 <c>POST /images/generations</c>,在 JSON 体里传 <c>image: [...]</c>;</item>
/// <item><see cref="ImageProtocol.OpenAiImages"/> 走 <c>POST /images/edits</c>(二进制数据走 multipart,URL 走 JSON)。</item>
/// </list>
/// </summary>
public sealed record ImageGenerateRequest(
    string Model,
    string Prompt,
    IReadOnlyList<ImageInput>? Images = null,
    string? Size = null,
    int Count = 1,
    string? ResponseFormat = null,
    bool? Watermark = null,
    int? Seed = null);

public sealed record GeneratedImageItem(
    string? Base64Json,
    string? Url,
    string? RevisedPrompt = null,
    string? Size = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record ImageGenerateResult(
    IReadOnlyList<GeneratedImageItem> Images,
    string? Model = null,
    long? Created = null);

public sealed class ImageMediaException(int? statusCode, string message) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>构建好的图片 HTTP 请求(含用于调试展示的文本正文预览;二进制 multipart 时正文给结构化摘要)。</summary>
public sealed record EncodedMediaHttp(HttpRequestMessage Message, string Url, string BodyPreview);

/// <summary>
/// 图片模型的线上编解码(纯函数,不发请求):覆盖 OpenAI 兼容图片协议与火山方舟图片协议(Seedream 等)。
/// 火山方舟的图生图 / 改图 / 多图参考不走 <c>/images/edits</c>,而是在 <c>/images/generations</c> 的 JSON 体里以 <c>image: [...]</c> 数组传入。
/// </summary>
public static class ImageMediaCodec
{
    public const string GenerationsPath = "/images/generations";
    public const string EditsPath = "/images/edits";

    public static EncodedMediaHttp BuildRequest(
        ImageProtocol protocol,
        string baseUrl,
        ImageGenerateRequest request,
        string? bearerToken,
        string userAgent = "NextCoWork/3.x")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var images = request.Images ?? [];

        return protocol switch
        {
            ImageProtocol.VolcengineImages => BuildVolcengine(baseUrl, request, images, bearerToken, userAgent),
            ImageProtocol.OpenAiImages => images.Count == 0
                ? BuildOpenAiGenerate(baseUrl, request, bearerToken, userAgent)
                : BuildOpenAiEdit(baseUrl, request, images, bearerToken, userAgent),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        };
    }

    private static EncodedMediaHttp BuildVolcengine(
        string baseUrl,
        ImageGenerateRequest request,
        IReadOnlyList<ImageInput> images,
        string? bearerToken,
        string userAgent)
    {
        var url = ProviderEndpoints.JoinUrl(baseUrl, GenerationsPath);
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
        };

        if (images.Count > 0)
        {
            var array = new JsonArray();
            foreach (var image in images)
            {
                if (!string.IsNullOrWhiteSpace(image.DataRef))
                {
                    array.Add((JsonNode)image.DataRef);
                }
            }

            if (array.Count > 0)
            {
                body["image"] = array;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Size))
        {
            body["size"] = request.Size.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.ResponseFormat))
        {
            body["response_format"] = request.ResponseFormat.Trim();
        }

        if (request.Watermark is { } watermark)
        {
            body["watermark"] = watermark;
        }

        if (request.Seed is { } seed)
        {
            body["seed"] = seed;
        }

        if (request.Count > 1)
        {
            body["sequential_image_generation"] = "auto";
            body["sequential_image_generation_options"] = new JsonObject { ["max_images"] = request.Count };
        }

        return CreateJsonPost(url, body, bearerToken, userAgent);
    }

    private static EncodedMediaHttp BuildOpenAiGenerate(
        string baseUrl,
        ImageGenerateRequest request,
        string? bearerToken,
        string userAgent)
    {
        var url = ProviderEndpoints.JoinUrl(baseUrl, GenerationsPath);
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["n"] = Math.Max(1, request.Count),
        };

        if (!string.IsNullOrWhiteSpace(request.Size))
        {
            body["size"] = request.Size.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.ResponseFormat))
        {
            body["response_format"] = request.ResponseFormat.Trim();
        }

        return CreateJsonPost(url, body, bearerToken, userAgent);
    }

    private static EncodedMediaHttp BuildOpenAiEdit(
        string baseUrl,
        ImageGenerateRequest request,
        IReadOnlyList<ImageInput> images,
        string? bearerToken,
        string userAgent)
    {
        var url = ProviderEndpoints.JoinUrl(baseUrl, EditsPath);
        var decoded = new List<(byte[] Bytes, string Mime)>(images.Count);
        var allDataUrls = true;
        foreach (var image in images)
        {
            if (TryDecodeDataUrl(image.DataRef, image.Mime, out var bytes, out var mime))
            {
                decoded.Add((bytes, mime));
            }
            else
            {
                allDataUrls = false;
                break;
            }
        }

        if (!allDataUrls)
        {
            // URL 源(如 xAI 等只收 JSON 的兼容网关):发 JSON
            var body = new JsonObject
            {
                ["model"] = request.Model,
                ["prompt"] = request.Prompt,
            };
            if (images.Count == 1)
            {
                body["image"] = new JsonObject { ["url"] = images[0].DataRef, ["type"] = "image_url" };
            }
            else
            {
                var array = new JsonArray();
                foreach (var img in images)
                {
                    array.Add((JsonNode)new JsonObject { ["url"] = img.DataRef, ["type"] = "image_url" });
                }

                body["image"] = array;
            }

            if (!string.IsNullOrWhiteSpace(request.Size))
            {
                body["size"] = request.Size.Trim();
            }

            return CreateJsonPost(url, body, bearerToken, userAgent);
        }

        var form = new MultipartFormDataContent
        {
            { new StringContent(request.Model, Encoding.UTF8), "model" },
            { new StringContent(request.Prompt, Encoding.UTF8), "prompt" },
        };
        if (request.Count > 1)
        {
            form.Add(new StringContent(request.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8), "n");
        }

        if (!string.IsNullOrWhiteSpace(request.Size))
        {
            form.Add(new StringContent(request.Size.Trim(), Encoding.UTF8), "size");
        }

        var fieldName = decoded.Count > 1 ? "image[]" : "image";
        for (var i = 0; i < decoded.Count; i++)
        {
            var (bytes, mime) = decoded[i];
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(mime);
            form.Add(part, fieldName, $"image-{i}{ExtensionOf(mime)}");
        }

        var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        ApplyCommonHeaders(message, bearerToken, userAgent);
        var preview = new JsonObject
        {
            ["multipart"] = true,
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["images"] = decoded.Count,
        }.ToJsonString();
        return new EncodedMediaHttp(message, url, preview);
    }

    public static ImageGenerateResult ParseResponse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new ImageMediaException(null, $"Invalid JSON response from image endpoint: {e.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ImageMediaException(null, "Image endpoint response is not a JSON object.");
            }

            var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            long? created = root.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var ts) ? ts : null;

            var items = new List<GeneratedImageItem>();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in data.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var b64 = StringProp(entry, "b64_json");
                    var url = StringProp(entry, "url");
                    var revised = StringProp(entry, "revised_prompt");
                    var size = StringProp(entry, "size");
                    string? errCode = null;
                    string? errMsg = null;
                    if (entry.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                    {
                        errCode = StringProp(err, "code");
                        errMsg = StringProp(err, "message");
                    }

                    items.Add(new GeneratedImageItem(b64, url, revised, size, errCode, errMsg));
                }
            }

            return new ImageGenerateResult(items, model, created);
        }
    }

    internal static EncodedMediaHttp CreateJsonPost(string url, JsonObject body, string? bearerToken, string userAgent)
    {
        var json = body.ToJsonString();
        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        ApplyCommonHeaders(message, bearerToken, userAgent);
        return new EncodedMediaHttp(message, url, json);
    }

    internal static void ApplyCommonHeaders(HttpRequestMessage message, string? bearerToken, string userAgent)
    {
        message.Headers.TryAddWithoutValidation("user-agent", userAgent);
        message.Headers.TryAddWithoutValidation("accept", "application/json");
        if (!string.IsNullOrEmpty(bearerToken))
        {
            message.Headers.TryAddWithoutValidation("authorization", "Bearer " + bearerToken);
        }
    }

    internal static string? StringProp(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

    internal static string ExtractErrorMessage(int statusCode, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString()))
                    {
                        return error.GetString()!;
                    }

                    if (error.ValueKind == JsonValueKind.Object && StringProp(error, "message") is { Length: > 0 } msg)
                    {
                        return msg;
                    }
                }

                if (StringProp(root, "message") is { Length: > 0 } top)
                {
                    return top;
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 正文走下面截断兜底
        }

        var line = body.Trim().Split('\n')[0].Trim();
        return line.Length == 0 || line.StartsWith('<')
            ? $"HTTP {statusCode}"
            : line.Length <= 200 ? line : line[..200] + "…";
    }

    private static bool TryDecodeDataUrl(string dataRef, string? fallbackMime, out byte[] bytes, out string mime)
    {
        bytes = [];
        mime = fallbackMime ?? "image/png";
        const string marker = ";base64,";
        if (!dataRef.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var at = dataRef.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 5)
        {
            return false;
        }

        var declared = dataRef[5..at].Trim();
        if (declared.Length > 0)
        {
            mime = declared;
        }

        try
        {
            bytes = Convert.FromBase64String(dataRef[(at + marker.Length)..]);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ExtensionOf(string mime) => mime.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".png",
    };
}

/// <summary>图片协议客户端:按 <see cref="ImageProtocol"/> 组请求、经 <see cref="IUpstreamHttp"/> 发送并解析结果。</summary>
public sealed class ImageMediaClient(IUpstreamHttp http, string userAgent = "NextCoWork/3.x")
{
    public async Task<ImageGenerateResult> GenerateAsync(
        ImageProtocol protocol,
        string baseUrl,
        ImageGenerateRequest request,
        string? bearerToken,
        CancellationToken ct = default)
    {
        var encoded = ImageMediaCodec.BuildRequest(protocol, baseUrl, request, bearerToken, userAgent);
        using (encoded.Message)
        {
            using var response = await http.SendAsync(encoded.Message, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                throw new ImageMediaException(status, ImageMediaCodec.ExtractErrorMessage(status, body));
            }

            return ImageMediaCodec.ParseResponse(body);
        }
    }
}
