using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>供应商类型、协议归属与模型类型归类(docs/design/07 §3「供应商类型、模型过滤与媒体协议」)。</summary>
public sealed class ModelKindClassifierTests
{
    [Fact]
    public void Provider_kinds_normalize_and_map_managed_ids()
    {
        Assert.Equal(["text", "image", "video", "audio"], ProviderKinds.All);
        Assert.Equal("text", ProviderKinds.Normalize(null));
        Assert.Equal("text", ProviderKinds.Normalize("   "));
        Assert.Equal("image", ProviderKinds.Normalize(" IMAGE "));
        Assert.Equal("video", ProviderKinds.Normalize("Video"));
        Assert.Equal("audio", ProviderKinds.Normalize("audio"));
        Assert.Null(ProviderKinds.Normalize("embedding"));

        Assert.True(ProviderKinds.IsManagedId("nextcowork"));
        Assert.True(ProviderKinds.IsManagedId("nextcowork-image"));
        Assert.True(ProviderKinds.IsManagedId("nextcowork-video"));
        Assert.True(ProviderKinds.IsManagedId("nextcowork-audio"));
        Assert.False(ProviderKinds.IsManagedId("openai"));

        Assert.Equal("nextcowork", ProviderKinds.ManagedIdFor(ProviderKinds.Text));
        Assert.Equal("nextcowork-image", ProviderKinds.ManagedIdFor(ProviderKinds.Image));
        Assert.Equal("nextcowork-video", ProviderKinds.ManagedIdFor(ProviderKinds.Video));
        Assert.Equal("nextcowork-audio", ProviderKinds.ManagedIdFor(ProviderKinds.Audio));
    }

    [Theory]
    [InlineData("anthropic", "text", "https://api.anthropic.com", "https://api.anthropic.com/v1/messages")]
    [InlineData("openai-chat", "text", "https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("openai-responses", "text", "https://api.openai.com/v1", "https://api.openai.com/v1/responses")]
    [InlineData("openai-images", "image", "https://api.openai.com/v1", "https://api.openai.com/v1/images/generations")]
    [InlineData("volcengine-images", "image", "https://ark.cn-beijing.volces.com/api/v3", "https://ark.cn-beijing.volces.com/api/v3/images/generations")]
    [InlineData("openai-videos", "video", "https://api.openai.com/v1", "https://api.openai.com/v1/videos")]
    [InlineData("volcengine-videos", "video", "https://ark.cn-beijing.volces.com/api/v3", "https://ark.cn-beijing.volces.com/api/v3/contents/generations/tasks")]
    [InlineData("openai-audio", "audio", "https://api.openai.com/v1", "https://api.openai.com/v1/audio/speech")]
    public void Protocol_endpoints_and_kind_ownership_are_consistent(string protocol, string kind, string baseUrl, string expectedUrl)
    {
        Assert.True(ProviderEndpoints.IsValidProtocol(protocol));
        Assert.Equal(kind, ProviderEndpoints.KindOfProtocol(protocol));
        Assert.True(ProviderEndpoints.IsProtocolForKind(protocol, kind));
        Assert.Contains(protocol, ProviderEndpoints.ProtocolsFor(kind));
        Assert.Equal(expectedUrl, ProviderEndpoints.EndpointUrl(baseUrl, protocol));
    }

    [Fact]
    public void Default_protocols_match_each_provider_kind()
    {
        Assert.Equal("openai-chat", ProviderEndpoints.DefaultProtocolFor(ProviderKinds.Text));
        Assert.Equal("openai-images", ProviderEndpoints.DefaultProtocolFor(ProviderKinds.Image));
        Assert.Equal("openai-videos", ProviderEndpoints.DefaultProtocolFor(ProviderKinds.Video));
        Assert.Equal("openai-audio", ProviderEndpoints.DefaultProtocolFor(ProviderKinds.Audio));
    }

    [Theory]
    [InlineData("custom-id", "image-generation", "image")]
    [InlineData("custom-id", "text->image", "image")]
    [InlineData("custom-id", "text+image->video", "video")]
    [InlineData("custom-id", "audio->text", "audio")]
    [InlineData("custom-id", "text+image->text", "text")]
    [InlineData("custom-id", "embedding", "other")]
    [InlineData("custom-id", "VolcengineTts", "audio")]
    [InlineData("custom-id", "WebSearch", "other")]
    public void Upstream_declared_kind_takes_top_priority(string modelId, string declared, string expected)
    {
        // 即便目录说它是文本模态,上游显式声明的模态也优先
        var actual = ModelKindClassifier.Classify(modelId, declaredKind: declared, catalogInputModalities: ["text"], catalogOutputModalities: ["text"]);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Catalog_modalities_distinguish_vision_chat_from_image_video_and_stt_audio()
    {
        // 支持看图的多模态对话模型仍是文本模型(输出只有 text,输入含 text+image)
        Assert.Equal(ProviderKinds.Text, ModelKindClassifier.Classify("mystery-vl", catalogInputModalities: ["text", "image"], catalogOutputModalities: ["text"]));
        // 全模态对话模型(output 同时有 text+audio 或 text+video)归文本对话模型;纯语音/实时音频模型仍归音频
        Assert.Equal(ProviderKinds.Text, ModelKindClassifier.Classify("qwen3-omni-flash", catalogInputModalities: ["text", "audio", "image"], catalogOutputModalities: ["text", "audio"]));
        Assert.Equal(ProviderKinds.Audio, ModelKindClassifier.Classify("gpt-audio-mini", catalogInputModalities: ["text", "audio"], catalogOutputModalities: ["text", "audio"]));
        Assert.Equal(ProviderKinds.Audio, ModelKindClassifier.Classify("lyria-3-pro-preview", catalogInputModalities: ["text"], catalogOutputModalities: ["text", "audio"]));
        Assert.Equal(ProviderKinds.Text, ModelKindClassifier.Classify("gemini-omni-1.2-flash", catalogInputModalities: ["text", "video"], catalogOutputModalities: ["text", "video"]));
        // 生图 / 生视频 / TTS 看输出模态
        Assert.Equal(ProviderKinds.Image, ModelKindClassifier.Classify("mystery-draw", catalogInputModalities: ["text"], catalogOutputModalities: ["image"]));
        Assert.Equal(ProviderKinds.Video, ModelKindClassifier.Classify("mystery-film", catalogInputModalities: ["text", "image"], catalogOutputModalities: ["video"]));
        Assert.Equal(ProviderKinds.Audio, ModelKindClassifier.Classify("mystery-tts", catalogInputModalities: ["text"], catalogOutputModalities: ["audio"]));
        // 纯语音转写(STT:输入仅 audio、输出 text)归入音频模型
        Assert.Equal(ProviderKinds.Audio, ModelKindClassifier.Classify("mystery-stt", catalogInputModalities: ["audio"], catalogOutputModalities: ["text"]));
    }

    [Theory]
    // 视频模型(包括含 imagine / seedance 的名字,不能误判为图片)
    [InlineData("doubao-seedance-1-5-pro-251215", "video")]
    [InlineData("doubao-seedance-2-0-260128", "video")]
    [InlineData("doubao-seedance-2-0-fast-260128", "video")]
    [InlineData("doubao-seedance-2-0-mini-260615", "video")]
    [InlineData("doubao-seedance-2-5-260628", "video")]
    [InlineData("doubao-seedance-2-5-flash-261115", "video")]
    [InlineData("doubao-seedance-2-5", "video")]
    [InlineData("grok-imagine-video", "video")]
    [InlineData("sora-2", "video")]
    [InlineData("veo-3.0-generate", "video")]
    [InlineData("wan2.1-t2v-turbo", "video")]
    [InlineData("kling-v2-master", "video")]
    // 图片模型
    [InlineData("doubao-seedream-4-5-251128", "image")]
    [InlineData("doubao-seedream-5-0-260128", "image")]
    [InlineData("gpt-image-1.5", "image")]
    [InlineData("dall-e-3", "image")]
    [InlineData("black-forest-labs/flux-1-dev", "image")]
    [InlineData("qwen-image", "image")]
    [InlineData("wanx2.1-t2i-plus", "image")]
    // 音频模型
    [InlineData("whisper-large-v3", "audio")]
    [InlineData("tts-1-hd", "audio")]
    [InlineData("gpt-4o-mini-tts", "audio")]
    [InlineData("cosyvoice-v2", "audio")]
    [InlineData("qwen-tts", "audio")]
    // 嵌入 / 重排 / 审核等非生成工具模型
    [InlineData("text-embedding-3-large", "other")]
    [InlineData("bge-reranker-v2-m3", "other")]
    [InlineData("omni-moderation-latest", "other")]
    // 文本对话模型
    [InlineData("gpt-5.4", "text")]
    [InlineData("claude-sonnet-4-5", "text")]
    [InlineData("doubao-seed-2-0-pro-260215", "text")]
    [InlineData("deepseek-reasoner", "text")]
    public void Conservative_id_heuristics_classify_known_families(string modelId, string expected) =>
        Assert.Equal(expected, ModelKindClassifier.Classify(modelId));
}
