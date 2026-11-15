using Ncw.Core.Domain;

namespace Ncw.Agent.Workspace.Git;

/// <summary>Git 面板「开不出来」的三种原因(都不是错误,是如实说明这个项目没有 git 可管)。</summary>
public enum GitUnavailableReason
{
    /// <summary>项目目录读不到(被删、被移走、盘没挂上、环境断开)。</summary>
    WorkspaceUnavailable,

    /// <summary>这个环境里没有 git(或不在 PATH 里)。</summary>
    GitMissing,

    /// <summary>目录存在,但不是(也不在)一个 git 工作区里(裸仓库也算)。</summary>
    NotRepository,
}

/// <summary>仓库此刻的样子。<see cref="Available"/> = false 时只有 <see cref="Reason"/>。</summary>
/// <param name="Root">仓库根(环境里的绝对路径;可能在项目根的上层)。</param>
public sealed record GitOverview(bool Available, GitUnavailableReason? Reason, string Root, GitStatusSnapshot? Status)
{
    public static GitOverview Unavailable(GitUnavailableReason reason) => new(false, reason, "", null);
}

public sealed record GitBranch(string Name, bool Current, string Upstream);

/// <param name="Timestamp">提交时间,Unix 毫秒。</param>
public sealed record GitCommit(string Hash, string ShortHash, string Subject, string Author, long Timestamp);

public sealed record GitDiff(string Path, bool Staged, bool Binary, bool Truncated, string Text);

/// <summary>对比视图的两侧全文。某一侧不存在(新文件、已删除、空仓库)是空串。</summary>
public sealed record GitDiffSides(string Original, string Modified);

/// <summary>稳定的错误 key(和协议的 <c>GitErrorKeys</c> 同值;Host 里有测试守着两边一致)。本层不产出界面文字。</summary>
public static class GitKeys
{
    public const string UnavailablePrefix = "git.unavailable.";
    public const string InvalidPath = "git.invalidPath";
    public const string InvalidBranch = "git.invalidBranch";
    public const string EmptyMessage = "git.emptyMessage";
    public const string NothingStaged = "git.nothingStaged";
    public const string DetachedPush = "git.detachedPush";
    public const string Failed = "git.failed";
    public const string Timeout = "git.timeout";
    public const string AiNoModel = "git.aiNoModel";
    public const string AiFailed = "git.aiFailed";
    public const string AiTimeout = "git.aiTimeout";
    public const string AiTruncated = "git.aiTruncated";
    public const string AiUnparsable = "git.aiUnparsable";

    public static string Unavailable(GitUnavailableReason reason) => UnavailablePrefix + reason switch
    {
        GitUnavailableReason.WorkspaceUnavailable => "workspaceUnavailable",
        GitUnavailableReason.GitMissing => "gitMissing",
        _ => "notRepository",
    };
}

/// <summary>失败的粗分类(Host 据此映射协议错误码)。</summary>
public enum GitErrorKind
{
    /// <summary>参数不合法(路径、分支名、提交信息、没有暂存、没有配置模型)→ <c>invalid_request</c>。</summary>
    InvalidRequest,

    /// <summary>这个项目没有可用的仓库 → <c>not_found</c>。</summary>
    NotAvailable,

    /// <summary>git 命令失败(<see cref="GitException.Detail"/> 是 git 的 stderr)→ <c>conflict</c>。</summary>
    Failed,

    /// <summary><c>index.lock</c> 被占用(别的 git 进程 / IDE 在用)→ <c>busy</c>,可重试。</summary>
    Busy,

    /// <summary>超时 / 环境断开 / 上游模型失败 → <c>unavailable</c>,可重试。</summary>
    Unavailable,

    /// <summary>这个环境跑不了 git 命令 → <c>unsupported</c>。</summary>
    Unsupported,
}

/// <summary>Git 服务的失败。<see cref="Key"/> 是稳定 key(<see cref="GitKeys"/>),<see cref="Detail"/> 是英文诊断(git 的 stderr),可能含路径。</summary>
public sealed class GitException(GitErrorKind kind, string key, string? detail = null) : Exception(detail is null ? key : $"{key}: {detail}")
{
    public GitErrorKind Kind { get; } = kind;

    public string Key { get; } = key;

    public string? Detail { get; } = detail;
}

/// <summary>各类命令的超时。默认和 2.x 一致:读 10 秒、改本地状态 30 秒、走网络 120 秒。</summary>
public sealed record GitTimeouts
{
    public TimeSpan Read { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan Write { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan Network { get; init; } = TimeSpan.FromSeconds(120);
}

public sealed record GitServiceOptions
{
    public GitTimeouts Timeouts { get; init; } = new();

    /// <summary>单个 diff 的字符上限。超出截断并置 <see cref="GitDiff.Truncated"/>。</summary>
    public int MaxDiffChars { get; init; } = 200 * 1024;

    /// <summary>对比视图单侧全文的上限。超出就退回 unified 文本:全文对比要的是<b>完整</b>的两份,截一半比不出任何可信的东西。</summary>
    public int MaxSideChars { get; init; } = 1024 * 1024;

    /// <summary>
    /// 每次起 git 进程时追加的环境变量(Host 在这里注入 Git 主机的 HTTPS 凭据:<c>GIT_CONFIG_COUNT</c> / <c>GIT_CONFIG_KEY_n</c> / <c>GIT_CONFIG_VALUE_n</c> 的 <c>http.&lt;url&gt;.extraHeader</c>)。
    /// 令牌因此不出现在命令行参数(<c>ps</c> 看得到)、<c>.git/config</c> 或远程 URL 里;按 URL 作用域下发,只会发给对应主机。每次调用现取(令牌更新立即生效)。
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>>? ExtraEnvironment { get; init; }
}

/// <summary>一次 git 进程的结果。</summary>
internal sealed record GitRun(int Code, string Stdout, string Stderr, bool Missing)
{
    public bool Ok => Code == 0;
}
