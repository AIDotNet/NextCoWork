using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ncw.Agent.Llm.Auth;
using Ncw.Core.Domain;

namespace Ncw.Agent.Llm.Media;

/// <summary>视频生成任务动作。</summary>
public enum VideoGenerateAction
{
    Generate,
    ImageToVideo,
    FirstLastFrame,
    Edit,
    Extend,
}

/// <summary>视频任务状态。</summary>
public enum VideoTaskState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled,
}

/// <summary>创建视频生成任务的输入。</summary>
public sealed record VideoCreateRequest(
    string Model,
    string Prompt,
    VideoGenerateAction Action = VideoGenerateAction.Generate,
    string? FirstFrameUrl = null,
    string? LastFrameUrl = null,
    IReadOnlyList<string>? ReferenceImageUrls = null,
    string? ReferenceVideoUrl = null,
    string? AspectRatio = null,
    string? Resolution = null,
    int? DurationSeconds = null,
    int? Seed = null);

/// <summary>一次视频任务的创建 / 轮询结果。</summary>
public sealed record VideoTaskStatus(
    string TaskId,
    VideoTaskState State,
    int? ProgressPercent = null,
    string? VideoUrl = null,
    string? ContentUrl = null,
    string? ErrorMessage = null);

public sealed class VideoMediaException(int? statusCode, string message) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>
/// 视频模型的线上编解码(纯函数,不发请求):
/// <list type="bullet">
/// <item><see cref="VideoProtocol.VolcengineVideos"/>:火山方舟异步任务接口 <c>/contents/generations/tasks</c>(创建 POST / 轮询 GET / 取消 DELETE),
/// 输入以 <c>content: [...]</c> 数组组织文本、首帧(<c>first_frame</c>)、尾帧(<c>last_frame</c>)、参考图(<c>reference_image</c>)、参考视频(<c>reference_video</c>);
/// 编辑 / 延长固定 <c>ratio = "adaptive"</c>、<c>duration = -1</c>,Seedance 2.5 附带 <c>omni_reference_task_type</c>。</item>
/// <item><see cref="VideoProtocol.OpenAiVideos"/>:OpenAI 兼容视频任务接口 <c>/videos</c>(创建 POST / 轮询 GET / 取消 DELETE)。</item>
/// </list>
/// </summary>
public static partial class VideoMediaCodec
{
    public const string OpenAiVideosPath = "/videos";
    public const string VolcengineTasksPath = "/contents/generations/tasks";

    public static EncodedMediaHttp BuildCreateRequest(
        VideoProtocol protocol,
        string baseUrl,
        VideoCreateRequest request,
        string? bearerToken,
        string userAgent = "NextCoWork/3.x")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);

        return protocol switch
        {
            VideoProtocol.VolcengineVideos => BuildVolcengineCreate(baseUrl, request, bearerToken, userAgent),
            VideoProtocol.OpenAiVideos => BuildOpenAiCreate(baseUrl, request, bearerToken, userAgent),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        };
    }

    public static HttpRequestMessage BuildStatusRequest(
        VideoProtocol protocol,
        string baseUrl,
        string taskId,
        string? bearerToken,
        string userAgent = "NextCoWork/3.x")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        var path = protocol == VideoProtocol.VolcengineVideos
            ? $"{VolcengineTasksPath}/{Uri.EscapeDataString(taskId)}"
            : $"{OpenAiVideosPath}/{Uri.EscapeDataString(taskId)}";
        var url = ProviderEndpoints.JoinUrl(baseUrl, path);
        var message = new HttpRequestMessage(HttpMethod.Get, url);
        ImageMediaCodec.ApplyCommonHeaders(message, bearerToken, userAgent);
        return message;
    }

    public static HttpRequestMessage BuildCancelRequest(
        VideoProtocol protocol,
        string baseUrl,
        string taskId,
        string? bearerToken,
        string userAgent = "NextCoWork/3.x")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        var path = protocol == VideoProtocol.VolcengineVideos
            ? $"{VolcengineTasksPath}/{Uri.EscapeDataString(taskId)}"
            : $"{OpenAiVideosPath}/{Uri.EscapeDataString(taskId)}";
        var url = ProviderEndpoints.JoinUrl(baseUrl, path);
        var message = new HttpRequestMessage(HttpMethod.Delete, url);
        ImageMediaCodec.ApplyCommonHeaders(message, bearerToken, userAgent);
        return message;
    }

    private static EncodedMediaHttp BuildVolcengineCreate(
        string baseUrl,
        VideoCreateRequest request,
        string? bearerToken,
        string userAgent)
    {
        var url = ProviderEndpoints.JoinUrl(baseUrl, VolcengineTasksPath);
        var content = new JsonArray
        {
            (JsonNode)new JsonObject
            {
                ["type"] = "text",
                ["text"] = request.Prompt,
            },
        };

        if (!string.IsNullOrWhiteSpace(request.FirstFrameUrl))
        {
            content.Add((JsonNode)new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = request.FirstFrameUrl },
                ["role"] = "first_frame",
            });
        }

        if (!string.IsNullOrWhiteSpace(request.LastFrameUrl))
        {
            content.Add((JsonNode)new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = request.LastFrameUrl },
                ["role"] = "last_frame",
            });
        }

        if (request.ReferenceImageUrls is { Count: > 0 } refs)
        {
            foreach (var imageUrl in refs)
            {
                if (!string.IsNullOrWhiteSpace(imageUrl))
                {
                    content.Add((JsonNode)new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = imageUrl },
                        ["role"] = "reference_image",
                    });
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ReferenceVideoUrl))
        {
            content.Add((JsonNode)new JsonObject
            {
                ["type"] = "video_url",
                ["video_url"] = new JsonObject { ["url"] = request.ReferenceVideoUrl },
                ["role"] = "reference_video",
            });
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["content"] = content,
        };

        var editing = request.Action is VideoGenerateAction.Edit or VideoGenerateAction.Extend;
        if (editing)
        {
            body["ratio"] = "adaptive";
            body["duration"] = -1;
            if (Seedance25Pattern().IsMatch(request.Model))
            {
                body["omni_reference_task_type"] = request.Action == VideoGenerateAction.Edit ? "edit" : "extend";
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(request.Resolution))
            {
                body["resolution"] = request.Resolution.Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            {
                body["ratio"] = request.AspectRatio.Trim();
            }

            if (request.DurationSeconds is { } duration)
            {
                body["duration"] = duration;
            }

            if (request.Seed is { } seed)
            {
                body["seed"] = seed;
            }
        }

        return ImageMediaCodec.CreateJsonPost(url, body, bearerToken, userAgent);
    }

    private static EncodedMediaHttp BuildOpenAiCreate(
        string baseUrl,
        VideoCreateRequest request,
        string? bearerToken,
        string userAgent)
    {
        var url = ProviderEndpoints.JoinUrl(baseUrl, OpenAiVideosPath);
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
        };

        if (request.DurationSeconds is { } duration)
        {
            body["seconds"] = duration.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(request.Resolution))
        {
            body["size"] = request.Resolution.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            body["size"] = request.AspectRatio.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.FirstFrameUrl))
        {
            body["input_reference"] = request.FirstFrameUrl;
        }

        return ImageMediaCodec.CreateJsonPost(url, body, bearerToken, userAgent);
    }

    public static VideoTaskStatus ParseCreateResponse(VideoProtocol protocol, string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new VideoMediaException(null, $"Invalid JSON response from video create endpoint: {e.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new VideoMediaException(null, "Video create response is not a JSON object.");
            }

            var id = ImageMediaCodec.StringProp(root, "id") ?? ImageMediaCodec.StringProp(root, "task_id");
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new VideoMediaException(null, $"{protocol.NameOf()}: create response carried no task id.");
            }

            var state = ParseState(ImageMediaCodec.StringProp(root, "status"));
            var progress = IntProp(root, "progress");
            return new VideoTaskStatus(id, state, progress);
        }
    }

    public static VideoTaskStatus ParseStatusResponse(VideoProtocol protocol, string baseUrl, string taskId, string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new VideoMediaException(null, $"Invalid JSON response from video status endpoint: {e.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new VideoMediaException(null, "Video status response is not a JSON object.");
            }

            var id = ImageMediaCodec.StringProp(root, "id") ?? ImageMediaCodec.StringProp(root, "task_id") ?? taskId;
            var state = ParseState(ImageMediaCodec.StringProp(root, "status"));
            var progress = IntProp(root, "progress");

            if (state == VideoTaskState.Failed)
            {
                var error = ExtractTaskError(root) ?? $"{protocol.NameOf()} video generation failed.";
                return new VideoTaskStatus(id, VideoTaskState.Failed, progress, ErrorMessage: error);
            }

            if (state == VideoTaskState.Canceled)
            {
                return new VideoTaskStatus(id, VideoTaskState.Canceled, progress);
            }

            if (state == VideoTaskState.Succeeded)
            {
                var videoUrl = ExtractVideoUrl(protocol, root);
                if (protocol == VideoProtocol.VolcengineVideos && string.IsNullOrWhiteSpace(videoUrl))
                {
                    return new VideoTaskStatus(id, VideoTaskState.Failed, 100, ErrorMessage: "Ark reported succeeded but carried no content.video_url.");
                }

                var contentUrl = protocol == VideoProtocol.OpenAiVideos && string.IsNullOrWhiteSpace(videoUrl)
                    ? ProviderEndpoints.JoinUrl(baseUrl, $"{OpenAiVideosPath}/{Uri.EscapeDataString(id)}/content")
                    : null;
                return new VideoTaskStatus(id, VideoTaskState.Succeeded, 100, VideoUrl: videoUrl, ContentUrl: contentUrl);
            }

            return new VideoTaskStatus(id, state, progress);
        }
    }

    private static VideoTaskState ParseState(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "succeeded" or "completed" or "done" => VideoTaskState.Succeeded,
        "failed" or "error" => VideoTaskState.Failed,
        "cancelled" or "canceled" => VideoTaskState.Canceled,
        "running" or "in_progress" or "processing" => VideoTaskState.Running,
        _ => VideoTaskState.Queued,
    };

    private static string? ExtractVideoUrl(VideoProtocol protocol, JsonElement root)
    {
        if (protocol == VideoProtocol.VolcengineVideos)
        {
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
            {
                return ImageMediaCodec.StringProp(content, "video_url");
            }

            return ImageMediaCodec.StringProp(root, "video_url");
        }

        if (ImageMediaCodec.StringProp(root, "video_url") is { Length: > 0 } direct)
        {
            return direct;
        }

        if (ImageMediaCodec.StringProp(root, "url") is { Length: > 0 } url)
        {
            return url;
        }

        if (ImageMediaCodec.StringProp(root, "output_url") is { Length: > 0 } output)
        {
            return output;
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && ImageMediaCodec.StringProp(item, "url") is { Length: > 0 } itemUrl)
                {
                    return itemUrl;
                }
            }
        }

        return null;
    }

    private static string? ExtractTaskError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var err))
        {
            if (err.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(err.GetString()))
            {
                return err.GetString();
            }

            if (err.ValueKind == JsonValueKind.Object)
            {
                return ImageMediaCodec.StringProp(err, "message") ?? ImageMediaCodec.StringProp(err, "code");
            }
        }

        return ImageMediaCodec.StringProp(root, "message");
    }

    private static int? IntProp(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n) ? n : null;

    [GeneratedRegex(@"seedance-2-5|2\.5", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Seedance25Pattern();
}

/// <summary>视频协议客户端:按 <see cref="VideoProtocol"/> 创建、查询、取消异步视频任务。</summary>
public sealed class VideoMediaClient(IUpstreamHttp http, string userAgent = "NextCoWork/3.x")
{
    public async Task<VideoTaskStatus> CreateAsync(
        VideoProtocol protocol,
        string baseUrl,
        VideoCreateRequest request,
        string? bearerToken,
        CancellationToken ct = default)
    {
        var encoded = VideoMediaCodec.BuildCreateRequest(protocol, baseUrl, request, bearerToken, userAgent);
        using (encoded.Message)
        {
            using var response = await http.SendAsync(encoded.Message, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                throw new VideoMediaException(status, ImageMediaCodec.ExtractErrorMessage(status, body));
            }

            return VideoMediaCodec.ParseCreateResponse(protocol, body);
        }
    }

    public async Task<VideoTaskStatus> GetStatusAsync(
        VideoProtocol protocol,
        string baseUrl,
        string taskId,
        string? bearerToken,
        CancellationToken ct = default)
    {
        using var message = VideoMediaCodec.BuildStatusRequest(protocol, baseUrl, taskId, bearerToken, userAgent);
        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            throw new VideoMediaException(status, ImageMediaCodec.ExtractErrorMessage(status, body));
        }

        return VideoMediaCodec.ParseStatusResponse(protocol, baseUrl, taskId, body);
    }

    public async Task CancelAsync(
        VideoProtocol protocol,
        string baseUrl,
        string taskId,
        string? bearerToken,
        CancellationToken ct = default)
    {
        using var message = VideoMediaCodec.BuildCancelRequest(protocol, baseUrl, taskId, bearerToken, userAgent);
        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            throw new VideoMediaException(status, ImageMediaCodec.ExtractErrorMessage(status, body));
        }
    }
}
