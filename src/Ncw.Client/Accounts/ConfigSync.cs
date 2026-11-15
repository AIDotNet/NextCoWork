using System.Collections.Immutable;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Core.Layout;

namespace Ncw.Client.Accounts;

/// <summary>
/// 账户配置同步(2.x 的 <c>configSync</c>,决策 D10)的<b>本地快照</b>:跨端共享的布局类设置——项目栏的分组与顺序(<see cref="RailLayout"/>,B10 的 <c>RailLayout</c>)和项目外观。
/// 只是数据,不带线上格式(后端 B10 还不存在,格式随后端定);<see cref="Rev"/> 是本控制端递增的快照版本。
/// </summary>
public sealed record ConfigSnapshot(long Rev, RailLayout Rail, ImmutableDictionary<ProjectRef, ProjectAppearance> Appearance)
{
    public static readonly ConfigSnapshot Empty = new(0, RailLayout.Empty, ImmutableDictionary<ProjectRef, ProjectAppearance>.Empty);
}

/// <summary>布局状态 ↔ 本地快照的纯函数(规则 3:只有这一处决定「哪些东西属于要同步的配置」)。</summary>
public static class ConfigSnapshots
{
    /// <summary>取当前布局的快照。</summary>
    public static ConfigSnapshot Capture(LayoutState layout, long rev) => new(rev, layout.Rail, layout.Appearance);

    /// <summary>把快照应用成布局状态(整体替换,不拼增量)。</summary>
    public static LayoutState Apply(ConfigSnapshot snapshot) => new(snapshot.Rail, snapshot.Appearance);
}

/// <summary>
/// 账户配置同步的接口。后端(B10)到位前<b>保持关闭</b>:唯一的实现是 <see cref="DisabledConfigSync"/>,没有任何同步代码在跑,布局只存在本控制端的本地偏好里。
/// 后端到位之后,实现这个接口并从 <see cref="AccountService"/> 取访问令牌(<see cref="AccountService.WithAccessAsync{T}"/>)即可,界面和 Store 不用改。
/// </summary>
public interface IConfigSync
{
    /// <summary>同步是否可用(后端是否提供、账户是否已登录)。</summary>
    bool IsAvailable { get; }

    /// <summary>不可用的原因(稳定 key,界面翻译);可用为 null。</summary>
    string? UnavailableReason { get; }

    /// <summary>把本地快照推到账户;返回服务端采用的版本。</summary>
    Task<AccountResult<long>> PushAsync(ConfigSnapshot snapshot, CancellationToken ct = default);

    /// <summary>取账户上的最新快照;没有为 null。</summary>
    Task<AccountResult<ConfigSnapshot?>> PullAsync(CancellationToken ct = default);
}

/// <summary>关闭的配置同步:什么都不做,明确说明原因。</summary>
public sealed class DisabledConfigSync : IConfigSync
{
    public const string BackendMissing = "account.configSync.backendMissing";

    public static readonly DisabledConfigSync Instance = new();

    public bool IsAvailable => false;

    public string? UnavailableReason => BackendMissing;

    public Task<AccountResult<long>> PushAsync(ConfigSnapshot snapshot, CancellationToken ct = default) =>
        Task.FromResult(new AccountResult<long>(false, 0, BackendMissing));

    public Task<AccountResult<ConfigSnapshot?>> PullAsync(CancellationToken ct = default) =>
        Task.FromResult(new AccountResult<ConfigSnapshot?>(false, null, BackendMissing));
}
