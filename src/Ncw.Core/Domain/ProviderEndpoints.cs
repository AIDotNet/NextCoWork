namespace Ncw.Core.Domain;

/// <summary>
/// 供应商地址 + 协议 → 实际请求的 URL 与「供应商类型 ↔ 协议」规则(docs/design/07 §3「供应商调试」)。
/// 设备发请求(<c>WireProtocols.JoinUrl</c> / 媒体编解码)和界面显示「最终地址」、选择协议列表共用这同一份规则(AGENTS.md §3 规则 3)。
/// </summary>
public static class ProviderEndpoints
{
    // ── 文本模型协议 ──
    public const string Anthropic = "anthropic";
    public const string OpenAiChat = "openai-chat";
    public const string OpenAiResponses = "openai-responses";

    // ── 图片模型协议 ──
    public const string OpenAiImages = "openai-images";
    public const string VolcengineImages = "volcengine-images";

    // ── 视频模型协议 ──
    public const string OpenAiVideos = "openai-videos";
    public const string VolcengineVideos = "volcengine-videos";

    // ── 音频模型协议 ──
    public const string OpenAiAudio = "openai-audio";

    /// <summary>文本模型的三种协议,界面列出的顺序。</summary>
    public static readonly IReadOnlyList<string> Protocols = [Anthropic, OpenAiChat, OpenAiResponses];

    /// <summary>图片模型的协议,界面列出的顺序。</summary>
    public static readonly IReadOnlyList<string> ImageProtocols = [OpenAiImages, VolcengineImages];

    /// <summary>视频模型的协议,界面列出的顺序。</summary>
    public static readonly IReadOnlyList<string> VideoProtocols = [OpenAiVideos, VolcengineVideos];

    /// <summary>音频模型的协议,界面列出的顺序。</summary>
    public static readonly IReadOnlyList<string> AudioProtocols = [OpenAiAudio];

    /// <summary>全部协议。</summary>
    public static readonly IReadOnlyList<string> AllProtocols =
    [
        Anthropic, OpenAiChat, OpenAiResponses,
        OpenAiImages, VolcengineImages,
        OpenAiVideos, VolcengineVideos,
        OpenAiAudio,
    ];

    /// <summary>某类供应商允许使用的协议列表(认不出的类型退回文本协议)。</summary>
    public static IReadOnlyList<string> ProtocolsFor(string? kind) => kind switch
    {
        ProviderKinds.Image => ImageProtocols,
        ProviderKinds.Video => VideoProtocols,
        ProviderKinds.Audio => AudioProtocols,
        _ => Protocols,
    };

    /// <summary>某类供应商新建自定义时的默认协议。</summary>
    public static string DefaultProtocolFor(string? kind) => kind switch
    {
        ProviderKinds.Image => OpenAiImages,
        ProviderKinds.Video => OpenAiVideos,
        ProviderKinds.Audio => OpenAiAudio,
        _ => OpenAiChat,
    };

    /// <summary>一个协议归属哪一类供应商;认不出返回 null。</summary>
    public static string? KindOfProtocol(string? protocol) => protocol switch
    {
        Anthropic or OpenAiChat or OpenAiResponses => ProviderKinds.Text,
        OpenAiImages or VolcengineImages => ProviderKinds.Image,
        OpenAiVideos or VolcengineVideos => ProviderKinds.Video,
        OpenAiAudio => ProviderKinds.Audio,
        _ => null,
    };

    public static bool IsValidProtocol(string? protocol) => KindOfProtocol(protocol) is not null;

    public static bool IsProtocolForKind(string? protocol, string? kind) =>
        ProviderKinds.Normalize(kind) is { } normalized && KindOfProtocol(protocol) == normalized;

    /// <summary>
    /// 对话请求的路径(文本协议)。两族协议的版本段约定是反的:Anthropic 的 base 不带 <c>/v1</c>,路径自己补;
    /// OpenAI 族的版本段是 base 的一部分(智谱 <c>/api/paas/v4</c>、火山 <c>/api/v3</c>……),路径只有 <c>/chat/completions</c>。认不出的协议为 null。
    /// </summary>
    public static string? ChatPath(string protocol) => protocol switch
    {
        Anthropic => "/v1/messages",
        OpenAiChat => "/chat/completions",
        OpenAiResponses => "/responses",
        _ => null,
    };

    /// <summary>
    /// 任意协议的主执行路径(对话 / 生图 / 视频任务创建 / 语音合成)。
    /// 火山图片与 OpenAI 图片都打 <c>/images/generations</c>(区别在火山图生图 / 改图也在生成接口里用 <c>image: [...]</c> 传递);
    /// 火山视频打方舟异步任务端点 <c>/contents/generations/tasks</c>。
    /// </summary>
    public static string? EndpointPath(string protocol) => protocol switch
    {
        Anthropic => "/v1/messages",
        OpenAiChat => "/chat/completions",
        OpenAiResponses => "/responses",
        OpenAiImages => "/images/generations",
        VolcengineImages => "/images/generations",
        OpenAiVideos => "/videos",
        VolcengineVideos => "/contents/generations/tasks",
        OpenAiAudio => "/audio/speech",
        _ => null,
    };

    /// <summary>
    /// base 与 path 拼接。去重 <c>/v1</c> 只服务 Anthropic:用户手填 <c>.../anthropic/v1</c> 时避免拼出 <c>/v1/v1/messages</c>。
    /// </summary>
    public static string JoinUrl(string baseUrl, string path)
    {
        var baseTrimmed = baseUrl.TrimEnd('/');
        var p = path.StartsWith('/') ? path : "/" + path;
        return baseTrimmed.EndsWith("/v1", StringComparison.Ordinal) && p.StartsWith("/v1/", StringComparison.Ordinal)
            ? baseTrimmed + p[3..]
            : baseTrimmed + p;
    }

    /// <summary>这个地址用这个文本协议对话时请求的 URL;协议认不出或地址为空为 null。</summary>
    public static string? ChatUrl(string baseUrl, string protocol) =>
        string.IsNullOrWhiteSpace(baseUrl) || ChatPath(protocol) is not { } path ? null : JoinUrl(baseUrl.Trim(), path);

    /// <summary>这个地址用任意协议(文本 / 图片 / 视频 / 音频)请求时的主 URL;协议认不出或地址为空为 null。</summary>
    public static string? EndpointUrl(string baseUrl, string protocol) =>
        string.IsNullOrWhiteSpace(baseUrl) || EndpointPath(protocol) is not { } path ? null : JoinUrl(baseUrl.Trim(), path);
}
