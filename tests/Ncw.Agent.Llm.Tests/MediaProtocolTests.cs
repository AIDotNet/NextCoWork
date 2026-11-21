using System.Net;
using System.Text;
using System.Text.Json;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Media;

namespace Ncw.Agent.Llm.Tests;

/// <summary>
/// 图片 / 视频 / 音频媒体协议编解码与客户端单测:
/// 重点验证火山方舟图片(<c>volcengine-images</c>)在同一个 <c>POST /images/generations</c> 里用 <c>image: [...]</c> 传参考图、
/// 火山方舟视频(<c>volcengine-videos</c>)的 <c>/contents/generations/tasks</c> 异步任务生命周期,以及 OpenAI 图片 / 视频 / 音频兼容协议。
/// </summary>
public sealed class MediaProtocolTests
{
    private const string ArkBase = "https://ark.cn-beijing.volces.com/api/v3";
    private const string OpenAiBase = "https://api.openai.com/v1";

    private sealed class RecordingHttp(Func<HttpRequestMessage, HttpResponseMessage> responder) : IUpstreamHttp
    {
        public List<(string Method, string Url, string? Auth, string? ContentType, string Body)> Seen { get; } = [];

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var auth = request.Headers.TryGetValues("authorization", out var values) ? string.Join(",", values) : null;
            var contentType = request.Content?.Headers.ContentType?.MediaType;
            Seen.Add((request.Method.Method, request.RequestUri!.ToString(), auth, contentType, body));
            return responder(request);
        }
    }

    // ── 火山方舟图片(volcengine-images) ──

    [Fact]
    public async Task Volcengine_images_uses_generations_endpoint_with_image_array_for_reference_images_and_sequential_options_for_multi_image()
    {
        var http = new RecordingHttp(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"model":"doubao-seedream-4-5-251128","created":1730000000,"data":[{"url":"https://tos.example/1.png","size":"2048x2048"},{"b64_json":"aGVsbG8="}]}""",
                Encoding.UTF8,
                "application/json"),
        });
        var client = new ImageMediaClient(http);

        var result = await client.GenerateAsync(
            ImageProtocol.VolcengineImages,
            ArkBase,
            new ImageGenerateRequest(
                "doubao-seedream-4-5-251128",
                "keep character style and add neon rain",
                Images: [new ImageInput("https://cdn.example/ref-1.png"), new ImageInput("data:image/png;base64,iVBORw0KGgo=")],
                Size: "2K",
                Count: 2,
                ResponseFormat: "url",
                Watermark: false,
                Seed: 42),
            "ark-key-1234");

        var seen = Assert.Single(http.Seen);
        Assert.Equal("POST", seen.Method);
        // 火山协议即使带参考图也走 /images/generations,绝不打 /images/edits
        Assert.Equal("https://ark.cn-beijing.volces.com/api/v3/images/generations", seen.Url);
        Assert.Equal("Bearer ark-key-1234", seen.Auth);
        Assert.Equal("application/json", seen.ContentType);

        using var doc = JsonDocument.Parse(seen.Body);
        var root = doc.RootElement;
        Assert.Equal("doubao-seedream-4-5-251128", root.GetProperty("model").GetString());
        Assert.Equal("2K", root.GetProperty("size").GetString());
        Assert.False(root.GetProperty("watermark").GetBoolean());
        Assert.Equal(42, root.GetProperty("seed").GetInt32());
        Assert.Equal("auto", root.GetProperty("sequential_image_generation").GetString());
        Assert.Equal(2, root.GetProperty("sequential_image_generation_options").GetProperty("max_images").GetInt32());
        var refs = root.GetProperty("image").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["https://cdn.example/ref-1.png", "data:image/png;base64,iVBORw0KGgo="], refs);

        Assert.Equal("doubao-seedream-4-5-251128", result.Model);
        Assert.Equal(2, result.Images.Count);
        Assert.Equal("https://tos.example/1.png", result.Images[0].Url);
        Assert.Equal("aGVsbG8=", result.Images[1].Base64Json);
    }

    [Fact]
    public void Volcengine_images_omits_image_field_for_pure_text_to_image()
    {
        var encoded = ImageMediaCodec.BuildRequest(
            ImageProtocol.VolcengineImages,
            ArkBase,
            new ImageGenerateRequest("doubao-seedream-4-5-251128", "a mountain at dawn"),
            "ark-key");
        using (encoded.Message)
        {
            using var doc = JsonDocument.Parse(encoded.BodyPreview);
            Assert.False(doc.RootElement.TryGetProperty("image", out _));
            Assert.False(doc.RootElement.TryGetProperty("sequential_image_generation", out _));
        }
    }

    // ── OpenAI 兼容图片(openai-images) ──

    [Fact]
    public void Openai_images_splits_text_to_image_and_edits_by_input_images()
    {
        var gen = ImageMediaCodec.BuildRequest(
            ImageProtocol.OpenAiImages,
            OpenAiBase,
            new ImageGenerateRequest("gpt-image-1.5", "a red bicycle", Count: 2, Size: "1024x1024"),
            "sk-1");
        using (gen.Message)
        {
            Assert.Equal("https://api.openai.com/v1/images/generations", gen.Url);
            using var doc = JsonDocument.Parse(gen.BodyPreview);
            Assert.Equal(2, doc.RootElement.GetProperty("n").GetInt32());
        }

        // data URL 输入走 multipart/form-data 的 /images/edits
        var editMultipart = ImageMediaCodec.BuildRequest(
            ImageProtocol.OpenAiImages,
            OpenAiBase,
            new ImageGenerateRequest("gpt-image-1.5", "remove background", Images: [new ImageInput("data:image/png;base64,iVBORw0KGgo=")]),
            "sk-1");
        using (editMultipart.Message)
        {
            Assert.Equal("https://api.openai.com/v1/images/edits", editMultipart.Url);
            Assert.IsType<MultipartFormDataContent>(editMultipart.Message.Content);
        }

        // 公网 URL 输入走 JSON 的 /images/edits
        var editJson = ImageMediaCodec.BuildRequest(
            ImageProtocol.OpenAiImages,
            OpenAiBase,
            new ImageGenerateRequest("grok-2-image", "make it winter", Images: [new ImageInput("https://cdn.example/a.png")]),
            "sk-1");
        using (editJson.Message)
        {
            Assert.Equal("https://api.openai.com/v1/images/edits", editJson.Url);
            using var doc = JsonDocument.Parse(editJson.BodyPreview);
            Assert.Equal("https://cdn.example/a.png", doc.RootElement.GetProperty("image").GetProperty("url").GetString());
        }
    }

    // ── 火山方舟视频(volcengine-videos) ──

    [Fact]
    public async Task Volcengine_videos_encodes_content_roles_edit_constraints_and_polls_task_lifecycle()
    {
        var step = 0;
        var http = new RecordingHttp(_ =>
        {
            step++;
            var payload = step switch
            {
                1 => """{"id":"cgt-2026-001","status":"queued"}""",
                2 => """{"id":"cgt-2026-001","status":"succeeded","content":{"video_url":"https://tos.example/out.mp4"}}""",
                _ => """{}""",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        });
        var client = new VideoMediaClient(http);

        var created = await client.CreateAsync(
            VideoProtocol.VolcengineVideos,
            ArkBase,
            new VideoCreateRequest(
                "doubao-seedance-1-5-pro-251215",
                "camera pans across a misty lake",
                Action: VideoGenerateAction.FirstLastFrame,
                FirstFrameUrl: "https://cdn.example/first.png",
                LastFrameUrl: "https://cdn.example/last.png",
                ReferenceImageUrls: ["https://cdn.example/style.png"],
                AspectRatio: "16:9",
                Resolution: "1080p",
                DurationSeconds: 5,
                Seed: 7),
            "ark-video-key");

        Assert.Equal(("cgt-2026-001", VideoTaskState.Queued), (created.TaskId, created.State));
        Assert.Equal("https://ark.cn-beijing.volces.com/api/v3/contents/generations/tasks", http.Seen[0].Url);
        using (var doc = JsonDocument.Parse(http.Seen[0].Body))
        {
            var root = doc.RootElement;
            Assert.Equal("1080p", root.GetProperty("resolution").GetString());
            Assert.Equal("16:9", root.GetProperty("ratio").GetString());
            Assert.Equal(5, root.GetProperty("duration").GetInt32());
            Assert.Equal(7, root.GetProperty("seed").GetInt32());
            var content = root.GetProperty("content").EnumerateArray().ToArray();
            Assert.Equal(4, content.Length);
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            Assert.Equal("first_frame", content[1].GetProperty("role").GetString());
            Assert.Equal("last_frame", content[2].GetProperty("role").GetString());
            Assert.Equal("reference_image", content[3].GetProperty("role").GetString());
        }

        var done = await client.GetStatusAsync(VideoProtocol.VolcengineVideos, ArkBase, "cgt-2026-001", "ark-video-key");
        Assert.Equal(VideoTaskState.Succeeded, done.State);
        Assert.Equal("https://tos.example/out.mp4", done.VideoUrl);
        Assert.Equal(("GET", "https://ark.cn-beijing.volces.com/api/v3/contents/generations/tasks/cgt-2026-001"), (http.Seen[1].Method, http.Seen[1].Url));

        await client.CancelAsync(VideoProtocol.VolcengineVideos, ArkBase, "cgt-2026-001", "ark-video-key");
        Assert.Equal(("DELETE", "https://ark.cn-beijing.volces.com/api/v3/contents/generations/tasks/cgt-2026-001"), (http.Seen[2].Method, http.Seen[2].Url));
    }

    [Fact]
    public void Volcengine_seedance_25_edit_forces_adaptive_ratio_minus_one_duration_and_omni_task_type()
    {
        var encoded = VideoMediaCodec.BuildCreateRequest(
            VideoProtocol.VolcengineVideos,
            ArkBase,
            new VideoCreateRequest(
                "doubao-seedance-2-5-pro",
                "replace sky with aurora",
                Action: VideoGenerateAction.Edit,
                ReferenceVideoUrl: "https://cdn.example/in.mp4",
                AspectRatio: "16:9",
                DurationSeconds: 10),
            "ark-key");
        using (encoded.Message)
        {
            using var doc = JsonDocument.Parse(encoded.BodyPreview);
            var root = doc.RootElement;
            Assert.Equal("adaptive", root.GetProperty("ratio").GetString());
            Assert.Equal(-1, root.GetProperty("duration").GetInt32());
            Assert.Equal("edit", root.GetProperty("omni_reference_task_type").GetString());
            Assert.Equal("reference_video", root.GetProperty("content")[1].GetProperty("role").GetString());
        }

        // succeeded 却没有 content.video_url 时必须判为失败而不是空成功
        var broken = VideoMediaCodec.ParseStatusResponse(VideoProtocol.VolcengineVideos, ArkBase, "cgt-1", """{"id":"cgt-1","status":"succeeded","content":{}}""");
        Assert.Equal(VideoTaskState.Failed, broken.State);
    }

    // ── OpenAI 兼容视频(openai-videos)与音频(openai-audio) ──

    [Fact]
    public void Openai_videos_falls_back_to_content_download_endpoint_when_status_has_no_direct_url()
    {
        var encoded = VideoMediaCodec.BuildCreateRequest(
            VideoProtocol.OpenAiVideos,
            OpenAiBase,
            new VideoCreateRequest("sora-2", "a paper boat", DurationSeconds: 8, Resolution: "1280x720", FirstFrameUrl: "https://cdn.example/frame.png"),
            "sk-1");
        using (encoded.Message)
        {
            Assert.Equal("https://api.openai.com/v1/videos", encoded.Url);
            using var doc = JsonDocument.Parse(encoded.BodyPreview);
            Assert.Equal("8", doc.RootElement.GetProperty("seconds").GetString());
            Assert.Equal("1280x720", doc.RootElement.GetProperty("size").GetString());
            Assert.Equal("https://cdn.example/frame.png", doc.RootElement.GetProperty("input_reference").GetString());
        }

        var status = VideoMediaCodec.ParseStatusResponse(VideoProtocol.OpenAiVideos, OpenAiBase, "vid_123", """{"id":"vid_123","status":"completed"}""");
        Assert.Equal(VideoTaskState.Succeeded, status.State);
        Assert.Null(status.VideoUrl);
        Assert.Equal("https://api.openai.com/v1/videos/vid_123/content", status.ContentUrl);
    }

    [Fact]
    public async Task Openai_audio_client_handles_speech_bytes_and_transcription_json()
    {
        var audioBytes = new byte[] { 0x49, 0x44, 0x33, 0x04 };
        var step = 0;
        var http = new RecordingHttp(_ =>
        {
            step++;
            if (step == 1)
            {
                var content = new ByteArrayContent(audioBytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"hello world","language":"en","duration":1.5}""", Encoding.UTF8, "application/json"),
            };
        });
        var client = new AudioMediaClient(http);

        var speech = await client.GenerateSpeechAsync(AudioProtocol.OpenAiAudio, OpenAiBase, new AudioSpeechRequest("gpt-4o-mini-tts", "Hello", Voice: "nova"), "sk-audio");
        Assert.Equal(audioBytes, speech.AudioBytes);
        Assert.Equal("audio/mpeg", speech.ContentType);
        Assert.Equal("https://api.openai.com/v1/audio/speech", http.Seen[0].Url);

        var stt = await client.TranscribeAsync(AudioProtocol.OpenAiAudio, OpenAiBase, new AudioTranscriptionRequest("whisper-1", audioBytes, "clip.mp3", Language: "en"), "sk-audio");
        Assert.Equal(("hello world", "en", 1.5), (stt.Text, stt.Language, stt.DurationSeconds));
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", http.Seen[1].Url);
    }
}
