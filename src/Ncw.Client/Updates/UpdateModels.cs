using Ncw.Core.Domain;

namespace Ncw.Client.Updates;

/// <summary>更新通道(04 §3):<c>stable</c> / <c>beta</c>,沿用 2.x 的通道名。</summary>
public enum UpdateChannel
{
    Stable,
    Beta,
}

public static class UpdateChannels
{
    public static string Id(UpdateChannel channel) => channel == UpdateChannel.Beta ? "beta" : "stable";

    public static bool TryParse(string? id, out UpdateChannel channel)
    {
        switch (id)
        {
            case "stable": channel = UpdateChannel.Stable; return true;
            case "beta": channel = UpdateChannel.Beta; return true;
            default: channel = UpdateChannel.Stable; return false;
        }
    }
}

/// <summary>
/// 更新失败的原因(稳定的枚举,界面按它翻译;Client 不产出界面文字,AGENTS.md §2)。
/// 签名与清单类的失败都意味着「不要信这份 feed」:不下载、不安装。
/// </summary>
public enum UpdateFailureKind
{
    /// <summary>连不上 feed(网络、DNS、超时、非 2xx)。</summary>
    Network,

    /// <summary>feed 里缺东西、或内容读不懂(没有清单、清单不是合法 JSON、Velopack 的 feed 与清单对不上)。</summary>
    FeedInvalid,

    /// <summary>清单旁边没有分离签名。</summary>
    SignatureMissing,

    /// <summary>清单与签名对不上(被篡改、或用别的密钥签的)。</summary>
    SignatureInvalid,

    /// <summary>清单声明的签名密钥 id 不在内置的信任列表里(密钥已被轮换掉 / 吊销,或这不是我们的 feed)。</summary>
    UnknownSigningKey,

    /// <summary>这个构建里没有内置任何发布公钥(开发构建):无法验证,拒绝更新而不是跳过验证。</summary>
    NoTrustedKeys,

    /// <summary>清单自己说「没签名」(<c>signing.status = pending</c>)。</summary>
    ManifestUnsigned,

    /// <summary>清单签名没问题,但内容不被接受(产品名、通道、版本号、强制更新策略自相矛盾、不认识的 schema)。</summary>
    ManifestRejected,

    /// <summary>下载的文件 sha256 和清单不一致。</summary>
    ChecksumMismatch,

    /// <summary>下载中途失败。</summary>
    DownloadFailed,

    /// <summary>交给更新器应用时失败(没有更新器、权限)。</summary>
    ApplyFailed,

    /// <summary>这个平台没有对应的更新产物。</summary>
    UnsupportedPlatform,
}

/// <summary>更新服务的阶段。</summary>
public enum UpdatePhase
{
    /// <summary>这个运行方式不做自更新(开发时直接 <c>dotnet run</c>、不是安装出来的应用)。</summary>
    Disabled,

    Idle,

    Checking,

    /// <summary>检查过,当前就是 feed 里能给的最新版本(或 feed 里的版本更低:不回滚)。</summary>
    UpToDate,

    /// <summary>有新版本,还没下载。</summary>
    Available,

    Downloading,

    /// <summary>已下载并校验,等「立即重启并更新」。</summary>
    Ready,

    /// <summary>已经把更新交给更新器,应用正在退出。</summary>
    Installing,

    /// <summary>最近一次操作失败(<see cref="UpdateState.Failure"/> 说明原因);检查失败时之前已有的 Available / Ready 不会被它覆盖。</summary>
    Failed,
}

/// <summary>
/// 自更新的状态(<b>控制端里唯一一份</b>,在 <see cref="Stores.ClientStores.Updates"/>,由 <see cref="UpdateService"/> 维护;规则 1)。
/// 「更新对当前版本有没有约束」不存在这里:由 <see cref="UpdatePolicy"/> 按 <see cref="Release"/> 和时钟派生(规则 3)。
/// 状态单调(规则 6):<see cref="Generation"/> 每次开始新的检查 / 下载加一,迟到的旧结果被丢弃,Ready 不会被旧的检查结果改回 Available。
/// </summary>
/// <param name="Release">待装更新的发布信息(版本 + 强制更新策略)。来自验过签名的清单;没有待装更新为 null。</param>
/// <param name="Notes">更新说明(清单里的,Markdown 原文)。</param>
/// <param name="Progress">下载进度 0–100(只在 Downloading)。</param>
/// <param name="Failure">失败原因(只在 Failed,或 Available / Ready 之上最近一次操作失败时)。</param>
/// <param name="LastCheckedAt">最近一次<b>成功读到验过签名的清单</b>的时间(跨会话保留);从没检查过为 null。</param>
/// <param name="Tick">时间流逝的刻度:宽限期到点时服务让它加一,持有「按时钟派生」的视图(约束强度)据此重新读时钟。不是事实,没有别的含义。</param>
public sealed record UpdateState(
    UpdatePhase Phase,
    string CurrentVersion,
    UpdateChannel Channel,
    UpdateRelease? Release,
    string? Notes,
    int Progress,
    UpdateFailureKind? Failure,
    DateTimeOffset? LastCheckedAt,
    long Generation,
    long Tick = 0)
{
    public static readonly UpdateState Initial = new(UpdatePhase.Idle, "0.0.0", UpdateChannel.Stable, null, null, 0, null, null, 0);

    /// <summary>有一个待装的新版本(已知但还没装):Available / Downloading / Ready / Installing,或在这些状态之上的一次失败。</summary>
    public bool HasPendingRelease => Release is not null;
}
