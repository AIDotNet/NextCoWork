using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Catalog;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Media;
using Ncw.Agent.Services.Attachments;
using Ncw.Agent.Services.Settings;
using Ncw.Agent.Tools.Schema;
using Ncw.Agent.WebTools.Net;
using Ncw.Core.Domain;

namespace Ncw.Agent.Host.Conversation;

/// <summary>使用设备设置锁定的供应商与模型；不接受模型在参数里改写供应商、端点或凭据。</summary>
internal sealed class MediaGenerationTools(
    SettingsService settings, ProviderService providers, CredentialResolver credentials, IUpstreamHttp upstream,
    IWebHttp downloads, AttachmentService attachments, LocalEnvironment environment, TimeProvider clock)
{
    public IReadOnlyList<IAgentTool> Create() =>
    [
        new MediaTool(this, ProviderKind.Image, "generate_image", "Generate one image from a text prompt and return a session attachment URL.",
            S.Object(["prompt"], ("prompt", S.String("Describe the desired image", 1, 16000)), ("size", S.String("Optional provider-supported image size", 1, 32)))),
        new MediaTool(this, ProviderKind.Video, "generate_video", "Generate one video from a text prompt and return a session attachment URL.",
            S.Object(["prompt"], ("prompt", S.String("Describe the desired video", 1, 16000)),
                ("duration_seconds", S.Integer("Optional provider-supported duration in seconds", 1, 60)))),
        new MediaTool(this, ProviderKind.Audio, "generate_audio", "Synthesize speech from text and return a session attachment URL.",
            S.Object(["text"], ("text", S.String("Text to speak", 1, 16000)), ("voice", S.String("Optional provider-supported voice", 1, 64)))),
    ];

    private Target? Resolve(ProviderKind kind)
    {
        var current = settings.Current();
        var selected = kind switch
        {
            ProviderKind.Image when current.ImageGeneration => current.Image,
            ProviderKind.Video => current.Video,
            ProviderKind.Audio => current.Audio,
            _ => ModelPick.None,
        };
        if (!selected.IsSet)
        {
            return null;
        }

        var snapshot = providers.Snapshot;
        var matches = from provider in snapshot.Providers
                      where provider.Enabled && provider.Kind == kind && (selected.ProviderId is null || provider.Id == selected.ProviderId)
                      join alias in snapshot.Aliases on provider.Id equals alias.ProviderId
                      where alias.IsEnabled && alias.Alias == selected.Model
                      let protocol = alias.ProtocolOverride ?? provider.Protocol
                      where protocol.KindOf() == kind
                      orderby alias.Priority ?? provider.Priority, provider.Id ascending
                      select new Target(provider, protocol, alias.UpstreamModel);
        return matches.FirstOrDefault();
    }

    private async ValueTask<ToolResult> GenerateAsync(ProviderKind kind, JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var target = Resolve(kind);
        if (target is null)
        {
            return ToolResult.Error("Select an enabled provider and model for this media tool in device settings.");
        }

        using var timeout = new CancellationTokenSource(kind == ProviderKind.Video ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(3), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var token = linked.Token;
        try
        {
            var credential = await credentials.ResolveAsync(target.Provider.CredentialRef, token).ConfigureAwait(false);
            if (credential is SignatureCredential || (credential is null && ProviderRefs.IsManagedProviderId(target.Provider.Id)))
            {
                return ToolResult.Error("The selected media provider has no supported credential. Check its sign-in or API key.");
            }

            var bearer = credential switch { ApiKeyCredential key => key.ApiKey, OAuthCredential oauth => oauth.AccessToken, _ => null };
            var (bytes, mime) = kind switch
            {
                ProviderKind.Image => await ImageAsync(target, bearer, input, token).ConfigureAwait(false),
                ProviderKind.Video => await VideoAsync(target, bearer, input, ctx, token).ConfigureAwait(false),
                ProviderKind.Audio => await AudioAsync(target, bearer, input, token).ConfigureAwait(false),
                _ => throw new InvalidOperationException(),
            };
            token.ThrowIfCancellationRequested();
            var name = $"generated-{kind.NameOf()}{Attachments.ExtOfMime(mime)}";
            var attachment = attachments.Store(ctx.SessionId, name, mime, bytes);
            var details = Jsn.ToElement(new JsonObject
            {
                ["kind"] = "media-generation", ["mediaKind"] = kind.NameOf(), ["url"] = attachment.Url,
                ["mime"] = attachment.Mime, ["name"] = attachment.DisplayName, ["size"] = attachment.Size,
                ["environment"] = new JsonObject
                {
                    ["deviceId"] = environment.Id.DeviceId, ["environment"] = environment.Id.Environment,
                    ["name"] = environment.DisplayName, ["type"] = "local",
                },
            });
            var text = new TextPart($"Generated {kind.NameOf()}: {attachment.Url}");
            return new ToolResult(kind == ProviderKind.Image ? [text, new ImagePart(attachment.Mime, attachment.Url)] : [text], details);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return ToolResult.Error("The media provider did not finish in time.");
        }
        // 不把供应商错误正文、临时签名 URL 或授权信息发给模型、转录或界面。
        catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException or ImageMediaException
            or VideoMediaException or CredentialAuthException or AttachmentException or SsrfBlockedException or MediaGenerationException)
        {
            return ToolResult.Error("Media generation failed. Check the selected provider, its credentials and model, or try a smaller output.");
        }
    }

    private async Task<(byte[] Bytes, string Mime)> ImageAsync(Target target, string? bearer, JsonElement input, CancellationToken ct)
    {
        var protocol = target.Protocol == ProviderProtocol.VolcengineImages ? ImageProtocol.VolcengineImages : ImageProtocol.OpenAiImages;
        var request = new ImageGenerateRequest(target.Model, input.Str("prompt") ?? "", Size: input.Str("size"),
            ResponseFormat: protocol == ImageProtocol.VolcengineImages ? "url" : null);
        var encoded = ImageMediaCodec.BuildRequest(protocol, target.Provider.BaseUrl, request, bearer);
        var (body, _) = await ReadAsync(encoded.Message, upstream.SendAsync, ImageResponseLimit, ct).ConfigureAwait(false);
        var result = ImageMediaCodec.ParseResponse(Encoding.UTF8.GetString(body));
        var item = result.Images.FirstOrDefault(static image => image.ErrorCode is null && (image.Base64Json is { Length: > 0 } || image.Url is { Length: > 0 }))
            ?? throw new MediaGenerationException();
        var bytes = item.Base64Json is { Length: > 0 } base64 ? Convert.FromBase64String(base64)
            : (await DownloadAsync(target, item.Url, ct).ConfigureAwait(false)).Bytes;
        // 只接收实际的光栅图片，不把任意 HTML/SVG 响应注册为图片附件。
        var mime = Attachments.ImageMimeOfBytes(bytes);
        return mime is "image/png" or "image/jpeg" or "image/webp" or "image/gif" ? (bytes, mime) : throw new MediaGenerationException();
    }

    private async Task<(byte[] Bytes, string Mime)> VideoAsync(Target target, string? bearer, JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var protocol = target.Protocol == ProviderProtocol.VolcengineVideos ? VideoProtocol.VolcengineVideos : VideoProtocol.OpenAiVideos;
        var duration = input.TryGetProperty("duration_seconds", out var seconds) ? seconds.GetInt32() : (int?)null;
        var request = VideoMediaCodec.BuildCreateRequest(protocol, target.Provider.BaseUrl,
            new VideoCreateRequest(target.Model, input.Str("prompt") ?? "", DurationSeconds: duration), bearer);
        var (body, _) = await ReadAsync(request.Message, upstream.SendAsync, VideoJobResponseLimit, ct).ConfigureAwait(false);
        var job = VideoMediaCodec.ParseCreateResponse(protocol, Encoding.UTF8.GetString(body));
        var taskId = job.TaskId;
        if (job.State == VideoTaskState.Succeeded)
        {
            job = await GetVideoStatusAsync(protocol, target.Provider.BaseUrl, taskId, bearer, ct).ConfigureAwait(false);
        }

        while (job.State is VideoTaskState.Queued or VideoTaskState.Running)
        {
            ctx.Progress.Report(new ToolProgress(Fraction: job.ProgressPercent is { } progress ? Math.Clamp(progress, 0, 100) / 100.0 : null));
            await Task.Delay(TimeSpan.FromSeconds(3), clock, ct).ConfigureAwait(false);
            job = await GetVideoStatusAsync(protocol, target.Provider.BaseUrl, taskId, bearer, ct).ConfigureAwait(false);
        }

        if (job.State != VideoTaskState.Succeeded)
        {
            throw new MediaGenerationException();
        }

        var (bytes, mime) = job.ContentUrl is { } contentUrl
            ? await DownloadContentAsync(target, contentUrl, bearer, ct).ConfigureAwait(false)
            : await DownloadAsync(target, job.VideoUrl, ct).ConfigureAwait(false);
        // 兼容用通用二进制类型返回 MP4 的网关，拒绝错误页等非视频数据。
        if ((mime is "video/mp4" or "video/quicktime" or "application/octet-stream" or null)
            && bytes.Length >= 12 && bytes.AsSpan(4, 4).SequenceEqual("ftyp"u8))
        {
            return (bytes, "video/mp4");
        }

        return mime == "video/webm" && bytes.AsSpan().StartsWith(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 })
            ? (bytes, mime) : throw new MediaGenerationException();
    }

    private async Task<VideoTaskStatus> GetVideoStatusAsync(VideoProtocol protocol, string baseUrl, string taskId, string? bearer, CancellationToken ct)
    {
        var request = VideoMediaCodec.BuildStatusRequest(protocol, baseUrl, taskId, bearer);
        var (body, _) = await ReadAsync(request, upstream.SendAsync, VideoJobResponseLimit, ct).ConfigureAwait(false);
        return VideoMediaCodec.ParseStatusResponse(protocol, baseUrl, taskId, Encoding.UTF8.GetString(body));
    }

    private async Task<(byte[] Bytes, string Mime)> AudioAsync(Target target, string? bearer, JsonElement input, CancellationToken ct)
    {
        var args = new AudioSpeechRequest(target.Model, input.Str("text") ?? "", input.Str("voice") ?? "alloy", "mp3");
        var encoded = AudioMediaCodec.BuildSpeechRequest(AudioProtocol.OpenAiAudio, target.Provider.BaseUrl, args, bearer);
        var (bytes, mime) = await ReadAsync(encoded.Message, upstream.SendAsync, AttachmentLimit, ct).ConfigureAwait(false);
        return (mime is "audio/mpeg" or "audio/mp3" or "application/octet-stream" or null)
            && (bytes.AsSpan().StartsWith("ID3"u8) || (bytes.Length >= 2 && bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0))
            ? (bytes, "audio/mpeg") : throw new MediaGenerationException();
    }

    // 只把凭据发送给协议编码器从已配置端点推导出的 OpenAI /content 地址，不发送给上游响应中任意 URL。
    private async Task<(byte[] Bytes, string? Mime)> DownloadContentAsync(Target target, string raw, string? bearer, CancellationToken ct)
    {
        var uri = ParseUrl(raw, new Uri(target.Provider.BaseUrl));
        if (!SameOrigin(uri, new Uri(target.Provider.BaseUrl)))
        {
            throw new MediaGenerationException();
        }

        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using (request)
        using (var response = await upstream.SendAsync(request, ct).ConfigureAwait(false))
        {
            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                return await DownloadPublicAsync(ParseUrl(location.OriginalString, uri), ct).ConfigureAwait(false);
            }

            return await ReadAsync(response, AttachmentLimit, ct).ConfigureAwait(false);
        }
    }

    private async Task<(byte[] Bytes, string? Mime)> DownloadAsync(Target target, string? raw, CancellationToken ct)
    {
        var origin = new Uri(target.Provider.BaseUrl);
        var uri = ParseUrl(raw, origin);
        // 自托管供应商可直接在自己已配置的源上提供文件；重定向走公网出口，不携带 Cookie 或密钥。
        if (SameOrigin(uri, origin))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await upstream.SendAsync(request, ct).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
            {
                return await ReadAsync(response, AttachmentLimit, ct).ConfigureAwait(false);
            }

            uri = ParseUrl(location.OriginalString, uri);
        }

        return await DownloadPublicAsync(uri, ct).ConfigureAwait(false);
    }

    private async Task<(byte[] Bytes, string? Mime)> DownloadPublicAsync(Uri uri, CancellationToken ct)
    {
        for (var hop = 0; hop < 5; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await downloads.SendAsync(request, ct).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
            {
                return await ReadAsync(response, AttachmentLimit, ct).ConfigureAwait(false);
            }

            uri = ParseUrl(location.OriginalString, uri);
        }

        throw new MediaGenerationException();
    }

    private static bool IsRedirect(HttpStatusCode code) => (int)code is 301 or 302 or 303 or 307 or 308;

    private static Uri ParseUrl(string? raw, Uri origin) => !string.IsNullOrWhiteSpace(raw)
        && Uri.TryCreate(origin, raw, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0
        ? uri : throw new MediaGenerationException();

    private static bool SameOrigin(Uri left, Uri right) => left.Scheme == right.Scheme && left.IdnHost == right.IdnHost && left.Port == right.Port;

    private const int AttachmentLimit = (int)Attachments.MaxBytes;
    private const int VideoJobResponseLimit = 1024 * 1024;
    private const int ImageResponseLimit = AttachmentLimit * 4 / 3 + 65536;

    private static async Task<(byte[] Bytes, string? Mime)> ReadAsync(HttpRequestMessage request,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, int limit, CancellationToken ct)
    {
        using (request)
        using (var response = await send(request, ct).ConfigureAwait(false))
        {
            return await ReadAsync(response, limit, ct).ConfigureAwait(false);
        }
    }

    private static async Task<(byte[] Bytes, string? Mime)> ReadAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > limit)
        {
            throw new MediaGenerationException();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[32 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(block, ct).ConfigureAwait(false);
            if (count == 0)
            {
                return (buffer.ToArray(), response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant());
            }

            if (buffer.Length + count > limit)
            {
                throw new MediaGenerationException();
            }

            buffer.Write(block, 0, count);
        }
    }

    private sealed class MediaGenerationException : Exception;
    private sealed record Target(ProviderRecord Provider, ProviderProtocol Protocol, string Model);

    private sealed class MediaTool(MediaGenerationTools owner, ProviderKind kind, string id, string description, JsonObject schema) : IAgentTool
    {
        public ToolSpec Info { get; } = new(id, description, Jsn.ToElement(schema), new ToolTraits(ReadOnly: false, Destructive: false, NeedsNetwork: true));

        public bool IsEnabled(ToolContext ctx) => owner.Resolve(kind) is not null;

        public ToolArgumentCheck PrepareArguments(JsonElement input)
        {
            var result = JsonSchemaValidator.Validate(Info.InputSchema, input);
            if (result.Error is { } error)
            {
                return ToolArgumentCheck.Invalid(error);
            }

            var field = kind == ProviderKind.Audio ? "text" : "prompt";
            return string.IsNullOrWhiteSpace(result.Value.Str(field)) ? ToolArgumentCheck.Invalid($"{field} must not be blank.") : ToolArgumentCheck.Valid(result.Value);
        }

        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct) => owner.GenerateAsync(kind, call.Input, ctx, ct);
    }
}
