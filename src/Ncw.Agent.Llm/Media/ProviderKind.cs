using Ncw.Agent.Llm.Wire;
using Ncw.Core.Domain;

namespace Ncw.Agent.Llm.Media;

/// <summary>供应商类型:文本 / 图片 / 视频 / 音频(线上字符串见 <see cref="ProviderKinds"/>)。</summary>
public enum ProviderKind
{
    Text,
    Image,
    Video,
    Audio,
}

/// <summary>图片模型协议。</summary>
public enum ImageProtocol
{
    /// <summary>OpenAI 兼容图片协议:文生图 <c>POST /images/generations</c>,改图 <c>POST /images/edits</c>。</summary>
    OpenAiImages,

    /// <summary>火山方舟图片协议(Seedream 等):文生图与图生图 / 多图参考统一走 <c>POST /images/generations</c>,参考图用 <c>image: [...]</c> 传递。</summary>
    VolcengineImages,
}

/// <summary>视频模型协议。</summary>
public enum VideoProtocol
{
    /// <summary>OpenAI 兼容视频任务协议:<c>POST /videos</c>、<c>GET /videos/{id}</c>、<c>DELETE /videos/{id}</c>。</summary>
    OpenAiVideos,

    /// <summary>火山方舟视频任务协议(Seedance 等):<c>POST /contents/generations/tasks</c>、<c>GET /contents/generations/tasks/{id}</c>、<c>DELETE /contents/generations/tasks/{id}</c>。</summary>
    VolcengineVideos,
}

/// <summary>音频模型协议。</summary>
public enum AudioProtocol
{
    /// <summary>OpenAI 兼容音频协议:TTS <c>POST /audio/speech</c>,STT <c>POST /audio/transcriptions</c>。</summary>
    OpenAiAudio,
}

/// <summary>
/// 供应商层统一的协议枚举(涵盖文本、图片、视频、音频四类)。文本流式编解码仍只认 <see cref="WireProtocol"/>,
/// 避免透传推理回放标签和路由逻辑混入非对话协议。
/// </summary>
public enum ProviderProtocol
{
    Anthropic,
    OpenAiChat,
    OpenAiResponses,
    OpenAiImages,
    VolcengineImages,
    OpenAiVideos,
    VolcengineVideos,
    OpenAiAudio,
}

public static class ProviderKindExtensions
{
    public static string NameOf(this ProviderKind kind) => kind switch
    {
        ProviderKind.Text => ProviderKinds.Text,
        ProviderKind.Image => ProviderKinds.Image,
        ProviderKind.Video => ProviderKinds.Video,
        ProviderKind.Audio => ProviderKinds.Audio,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static ProviderKind? ParseKind(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        ProviderKinds.Text => ProviderKind.Text,
        ProviderKinds.Image => ProviderKind.Image,
        ProviderKinds.Video => ProviderKind.Video,
        ProviderKinds.Audio => ProviderKind.Audio,
        _ => null,
    };

    /// <summary>这种类型默认使用的协议。</summary>
    public static ProviderProtocol DefaultProtocol(this ProviderKind kind) => kind switch
    {
        ProviderKind.Image => ProviderProtocol.OpenAiImages,
        ProviderKind.Video => ProviderProtocol.OpenAiVideos,
        ProviderKind.Audio => ProviderProtocol.OpenAiAudio,
        _ => ProviderProtocol.OpenAiChat,
    };
}

public static class ProviderProtocols
{
    public static string NameOf(this ProviderProtocol protocol) => protocol switch
    {
        ProviderProtocol.Anthropic => ProviderEndpoints.Anthropic,
        ProviderProtocol.OpenAiChat => ProviderEndpoints.OpenAiChat,
        ProviderProtocol.OpenAiResponses => ProviderEndpoints.OpenAiResponses,
        ProviderProtocol.OpenAiImages => ProviderEndpoints.OpenAiImages,
        ProviderProtocol.VolcengineImages => ProviderEndpoints.VolcengineImages,
        ProviderProtocol.OpenAiVideos => ProviderEndpoints.OpenAiVideos,
        ProviderProtocol.VolcengineVideos => ProviderEndpoints.VolcengineVideos,
        ProviderProtocol.OpenAiAudio => ProviderEndpoints.OpenAiAudio,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    public static string NameOf(this ImageProtocol protocol) => protocol switch
    {
        ImageProtocol.OpenAiImages => ProviderEndpoints.OpenAiImages,
        ImageProtocol.VolcengineImages => ProviderEndpoints.VolcengineImages,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    public static string NameOf(this VideoProtocol protocol) => protocol switch
    {
        VideoProtocol.OpenAiVideos => ProviderEndpoints.OpenAiVideos,
        VideoProtocol.VolcengineVideos => ProviderEndpoints.VolcengineVideos,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    public static string NameOf(this AudioProtocol protocol) => protocol switch
    {
        AudioProtocol.OpenAiAudio => ProviderEndpoints.OpenAiAudio,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    public static ProviderProtocol? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        ProviderEndpoints.Anthropic => ProviderProtocol.Anthropic,
        ProviderEndpoints.OpenAiChat => ProviderProtocol.OpenAiChat,
        ProviderEndpoints.OpenAiResponses => ProviderProtocol.OpenAiResponses,
        ProviderEndpoints.OpenAiImages => ProviderProtocol.OpenAiImages,
        ProviderEndpoints.VolcengineImages => ProviderProtocol.VolcengineImages,
        ProviderEndpoints.OpenAiVideos => ProviderProtocol.OpenAiVideos,
        ProviderEndpoints.VolcengineVideos => ProviderProtocol.VolcengineVideos,
        ProviderEndpoints.OpenAiAudio => ProviderProtocol.OpenAiAudio,
        _ => null,
    };

    public static ProviderKind KindOf(this ProviderProtocol protocol) => protocol switch
    {
        ProviderProtocol.Anthropic or ProviderProtocol.OpenAiChat or ProviderProtocol.OpenAiResponses => ProviderKind.Text,
        ProviderProtocol.OpenAiImages or ProviderProtocol.VolcengineImages => ProviderKind.Image,
        ProviderProtocol.OpenAiVideos or ProviderProtocol.VolcengineVideos => ProviderKind.Video,
        ProviderProtocol.OpenAiAudio => ProviderKind.Audio,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    public static ProviderProtocol ToProviderProtocol(this WireProtocol protocol) => protocol switch
    {
        WireProtocol.Anthropic => ProviderProtocol.Anthropic,
        WireProtocol.OpenAiChat => ProviderProtocol.OpenAiChat,
        WireProtocol.OpenAiResponses => ProviderProtocol.OpenAiResponses,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    public static WireProtocol? ToWireProtocol(this ProviderProtocol protocol) => protocol switch
    {
        ProviderProtocol.Anthropic => WireProtocol.Anthropic,
        ProviderProtocol.OpenAiChat => WireProtocol.OpenAiChat,
        ProviderProtocol.OpenAiResponses => WireProtocol.OpenAiResponses,
        _ => null,
    };

    public static ImageProtocol? ToImageProtocol(this ProviderProtocol protocol) => protocol switch
    {
        ProviderProtocol.OpenAiImages => ImageProtocol.OpenAiImages,
        ProviderProtocol.VolcengineImages => ImageProtocol.VolcengineImages,
        _ => null,
    };

    public static VideoProtocol? ToVideoProtocol(this ProviderProtocol protocol) => protocol switch
    {
        ProviderProtocol.OpenAiVideos => VideoProtocol.OpenAiVideos,
        ProviderProtocol.VolcengineVideos => VideoProtocol.VolcengineVideos,
        _ => null,
    };

    public static AudioProtocol? ToAudioProtocol(this ProviderProtocol protocol) => protocol switch
    {
        ProviderProtocol.OpenAiAudio => AudioProtocol.OpenAiAudio,
        _ => null,
    };

    public static string PathOf(this ProviderProtocol protocol) =>
        ProviderEndpoints.EndpointPath(protocol.NameOf()) ?? throw new ArgumentOutOfRangeException(nameof(protocol));
}
