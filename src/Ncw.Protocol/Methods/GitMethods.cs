using PolyType;

namespace Ncw.Protocol.Methods;

/// <summary>
/// Git 的方法表(docs/design/09 §8.4,功能盘点 10 的 <c>git</c> 模块,13 个通道)。能力 <see cref="Capabilities.Git"/>,承载 本机 + 远程。
/// 读取(概览、分支、提交、diff)viewer 起;写入(暂存、提交、切分支、拉取 / 推送)和「生成提交信息」(会花模型的钱)member 起;写入 command 带幂等键。
/// <para>
/// 命令经项目的执行环境(<c>IExecutionEnvironment</c>)以 argv 形式执行(不经 shell,分支名、提交信息、路径都没有注入面),所以 SSH 项目的 git 也在远端跑(2.x 的 <c>remote-workspace</c> 不可用状态取消)。
/// 「开不出来」不是错误:没有这个项目目录 / 没装 git / 不是仓库,<c>git.overview</c> 返回 <see cref="GitOverviewDto.Available"/> = false 和 <see cref="GitOverviewDto.Reason"/>(<see cref="GitUnavailableReasons"/>),
/// 由界面如实说明;写操作遇到同样情形才是错误。路径一律是<b>仓库相对</b>(git 自己的写法,<c>/</c> 分隔),不是项目相对:仓库根可能在项目根的上层。
/// </para>
/// <para>
/// <b>错误</b>:<c>invalid_request</c>(路径 / 分支名 / 提交信息不合法)、<c>not_found</c>(项目没有可用的仓库)、<c>conflict</c>(git 命令失败,<see cref="GitErrorDto.Detail"/> 是 git 的 stderr)、
/// <c>unavailable</c>(超时 / 环境断开,可重试)、<c>unsupported</c>(这个环境跑不了 git)、<c>busy</c>(<c>index.lock</c> 被占用,可重试)。稳定 key 在 <c>error.details</c>(<see cref="GitErrorDto"/>,<see cref="GitErrorKeys"/>),控制端翻译。
/// </para>
/// </summary>
public static class GitMethods
{
    public static readonly MethodDef<GitWorkspaceParams, GitOverviewDto> Overview = Query<GitWorkspaceParams, GitOverviewDto>("git.overview", Role.Viewer);

    public static readonly MethodDef<GitWorkspaceParams, GitBranchesResult> Branches = Query<GitWorkspaceParams, GitBranchesResult>("git.branches", Role.Viewer);

    public static readonly MethodDef<GitCommitsParams, GitCommitsResult> Commits = Query<GitCommitsParams, GitCommitsResult>("git.commits", Role.Viewer);

    public static readonly MethodDef<GitDiffParams, GitDiffDto> Diff = Query<GitDiffParams, GitDiffDto>("git.diff", Role.Viewer);

    /// <summary>对比视图用的两侧全文(工作区视图:暂存区 → 工作区;暂存区视图:HEAD → 暂存区)。没法按全文对比(二进制 / 超限 / 冲突 / 子模块)时 <see cref="GitDiffSidesDto.Comparable"/> = false,界面退回 <c>git.diff</c> 的 unified 文本。</summary>
    public static readonly MethodDef<GitDiffParams, GitDiffSidesDto> DiffSides = Query<GitDiffParams, GitDiffSidesDto>("git.diffSides", Role.Viewer);

    public static readonly MethodDef<GitPathsParams, Unit> Stage = Command<GitPathsParams, Unit>("git.stage", Role.Member);

    public static readonly MethodDef<GitPathsParams, Unit> Unstage = Command<GitPathsParams, Unit>("git.unstage", Role.Member);

    public static readonly MethodDef<GitCommitParams, GitCommitDto> Commit = Command<GitCommitParams, GitCommitDto>("git.commit", Role.Member);

    public static readonly MethodDef<GitCheckoutParams, Unit> Checkout = Command<GitCheckoutParams, Unit>("git.checkout", Role.Member);

    public static readonly MethodDef<GitCreateBranchParams, GitBranchDto> CreateBranch = Command<GitCreateBranchParams, GitBranchDto>("git.createBranch", Role.Member);

    /// <summary><c>git pull --ff-only</c>:快进不了就如实失败,不在没有终端的面板里留下半合并状态。</summary>
    public static readonly MethodDef<GitWorkspaceParams, Unit> Pull = Command<GitWorkspaceParams, Unit>("git.pull", Role.Member);

    /// <summary>推送当前分支;没有上游时补 <c>-u origin &lt;branch&gt;</c>。凭据提示被关掉(没有终端可答),需要认证的远端当场失败。</summary>
    public static readonly MethodDef<GitWorkspaceParams, Unit> Push = Command<GitWorkspaceParams, Unit>("git.push", Role.Member);

    /// <summary>
    /// 读暂存区的 diff,用设置里的辅助模型写一条提交信息草稿(填进输入框,不直接提交)。没有副作用但会花钱,所以是 member 起的 query。
    /// 模型没配置 <c>invalid_request</c> + <see cref="GitErrorKeys.AiNoModel"/>;其它失败 <see cref="GitErrorKeys"/> 的 <c>git.ai*</c>。
    /// </summary>
    public static readonly MethodDef<GitWorkspaceParams, GitGeneratedMessageDto> GenerateMessage = Query<GitWorkspaceParams, GitGeneratedMessageDto>("git.generateMessage", Role.Member);

    public static readonly IReadOnlyList<MethodDef> All =
        [Overview, Branches, Commits, Diff, DiffSides, Stage, Unstage, Commit, Checkout, CreateBranch, Pull, Push, GenerateMessage];

    private static MethodDef<TP, TR> Query<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Query, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Git };

    private static MethodDef<TP, TR> Command<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Command, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Git };
}

/// <summary><see cref="GitOverviewDto.Reason"/> 的取值:面板开不出来的原因(都不是错误)。</summary>
public static class GitUnavailableReasons
{
    /// <summary>项目目录读不到(被删、被移走、盘没挂上、环境断开)。</summary>
    public const string WorkspaceUnavailable = "workspaceUnavailable";

    /// <summary>这个环境里没有 git(或不在 PATH 里)。</summary>
    public const string GitMissing = "gitMissing";

    /// <summary>目录存在,但不是(也不在)一个 git 工作区里(裸仓库也算)。</summary>
    public const string NotRepository = "notRepository";
}

/// <summary><see cref="GitErrorDto.Key"/> 的取值(控制端翻译)。写操作遇到「不可用」时 key 是 <c>git.unavailable.&lt;reason&gt;</c>(<see cref="UnavailablePrefix"/> + <see cref="GitUnavailableReasons"/>)。</summary>
public static class GitErrorKeys
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
}

/// <summary><c>error.details</c> 的结构化补充。<see cref="Detail"/> 是 git 的 stderr(英文,可能含路径,只给有写权限的调用者)。</summary>
public sealed record GitErrorDto(string Key, string? Detail = null);

public sealed record GitWorkspaceParams(string WorkspaceId);

/// <summary>
/// 一个文件的状态。porcelain 的 XY 两位原样带上来(<see cref="Index"/> / <see cref="Worktree"/>,一个字符;<c>.</c> = 这一栏没有改动):
/// 同一个文件可以「暂存了一次修改、之后又改了一遍」(<c>MM</c>),压成一个枚举就得二选一,而界面要的是分别显示。
/// <see cref="Staged"/> / <see cref="Unstaged"/> / <see cref="Untracked"/> / <see cref="Conflicted"/> 是派生值(Core 的 <c>GitStatus</c> 唯一生成函数,Agent 和控制端共用)。
/// </summary>
public sealed record GitFileChangeDto(string Path, string Index, string Worktree, bool Staged, bool Unstaged, bool Untracked, bool Conflicted, string? RenamedFrom = null);

/// <summary>仓库此刻的样子。<see cref="Available"/> = false 时只有 <see cref="Reason"/>,其它字段是默认值。</summary>
/// <param name="Root">仓库根(设备上的绝对路径,给界面显示「在哪个仓库」;可能在项目根的上层)。</param>
/// <param name="Branch">空串 = detached HEAD 或空仓库。</param>
/// <param name="Unborn">还没有任何提交的新仓库(此时 <c>git log</c> 是空的,不是出错)。</param>
/// <param name="FilesTruncated">改动文件太多,列表被截断到 2000 条。</param>
public sealed record GitOverviewDto(
    bool Available,
    string? Reason = null,
    string Root = "",
    string Branch = "",
    bool Detached = false,
    bool Unborn = false,
    string Upstream = "",
    int Ahead = 0,
    int Behind = 0,
    GitFileChangeDto[]? Files = null,
    bool FilesTruncated = false);

/// <param name="Upstream">上游分支(<c>origin/main</c>);没设上游是空串。</param>
public sealed record GitBranchDto(string Name, bool Current, string Upstream);

public sealed record GitBranchesResult(GitBranchDto[] Branches);

/// <param name="Limit">1..500,缺省 50。</param>
public sealed record GitCommitsParams(string WorkspaceId, int Limit = 50);

/// <param name="Timestamp">提交时间,Unix 毫秒。</param>
public sealed record GitCommitDto(string Hash, string ShortHash, string Subject, string Author, long Timestamp);

public sealed record GitCommitsResult(GitCommitDto[] Commits);

/// <param name="Path">仓库相对路径。</param>
/// <param name="Staged">true = 暂存区视图(HEAD → 暂存区),false = 工作区视图(暂存区 → 工作区;未跟踪文件整份算新增)。</param>
public sealed record GitDiffParams(string WorkspaceId, string Path, bool Staged);

/// <param name="Binary">二进制文件:git 不给文本 diff,界面显示一行说明而不是空白。</param>
/// <param name="Truncated">超过 200 KiB 被截断。</param>
public sealed record GitDiffDto(string Path, bool Staged, bool Binary, bool Truncated, string Text);

public sealed record GitDiffSidesDto(bool Comparable, string Original = "", string Modified = "");

public sealed record GitPathsParams(string WorkspaceId, string[] Paths);

public sealed record GitCommitParams(string WorkspaceId, string Message);

public sealed record GitCheckoutParams(string WorkspaceId, string Branch);

public sealed record GitCreateBranchParams(string WorkspaceId, string Name, bool Checkout);

public sealed record GitGeneratedMessageDto(string Message);

/// <summary>Git 方法的载荷形状(与其它领域同属一个 <c>PayloadShapes</c>)。</summary>
[GenerateShapeFor<GitWorkspaceParams>]
[GenerateShapeFor<GitOverviewDto>]
[GenerateShapeFor<GitBranchesResult>]
[GenerateShapeFor<GitCommitsParams>]
[GenerateShapeFor<GitCommitsResult>]
[GenerateShapeFor<GitDiffParams>]
[GenerateShapeFor<GitDiffDto>]
[GenerateShapeFor<GitDiffSidesDto>]
[GenerateShapeFor<GitPathsParams>]
[GenerateShapeFor<GitCommitParams>]
[GenerateShapeFor<GitCommitDto>]
[GenerateShapeFor<GitCheckoutParams>]
[GenerateShapeFor<GitCreateBranchParams>]
[GenerateShapeFor<GitBranchDto>]
[GenerateShapeFor<GitGeneratedMessageDto>]
[GenerateShapeFor<GitErrorDto>]
public sealed partial class PayloadShapes;
