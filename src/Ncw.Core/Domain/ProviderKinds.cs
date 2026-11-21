namespace Ncw.Core.Domain;

/// <summary>
/// 模型供应商的类型(docs/design/07 §3、09 §8.4):文本模型 / 图片模型 / 视频模型 / 音频模型。
/// 协议、存储与客户端共用这一份常量与派生规则(AGENTS.md §3 规则 3)。
/// </summary>
public static class ProviderKinds
{
    public const string Text = "text";
    public const string Image = "image";
    public const string Video = "video";
    public const string Audio = "audio";

    /// <summary>四种供应商类型,界面列出的顺序。</summary>
    public static readonly IReadOnlyList<string> All = [Text, Image, Video, Audio];

    /// <summary>登录 NextCoWork 账户后自动提供的四家内置托管供应商 id。</summary>
    public const string ManagedTextId = "nextcowork";
    public const string ManagedImageId = "nextcowork-image";
    public const string ManagedVideoId = "nextcowork-video";
    public const string ManagedAudioId = "nextcowork-audio";

    public static readonly IReadOnlyList<string> ManagedIds = [ManagedTextId, ManagedImageId, ManagedVideoId, ManagedAudioId];

    public static bool IsValid(string? kind) => kind is Text or Image or Video or Audio;

    /// <summary>规范化供应商类型:null / 空串按历史缺省落到 <see cref="Text"/>,其余不认识返回 null。</summary>
    public static string? Normalize(string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? Text
        : kind.Trim().ToLowerInvariant() is var value && IsValid(value) ? value
        : null;

    /// <summary>是否为登录 NextCoWork 自动提供的内置托管供应商 id。</summary>
    public static bool IsManagedId(string? providerId) =>
        providerId is ManagedTextId or ManagedImageId or ManagedVideoId or ManagedAudioId;

    /// <summary>每种类型对应的 NextCoWork 内置托管供应商 id。</summary>
    public static string ManagedIdFor(string kind) => kind switch
    {
        Text => ManagedTextId,
        Image => ManagedImageId,
        Video => ManagedVideoId,
        Audio => ManagedAudioId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown provider kind."),
    };
}
