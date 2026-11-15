using System.Text;
using System.Text.RegularExpressions;
using Ncw.Agent.Environments;
using Ncw.Core.Domain;

namespace Ncw.Agent.Workspace.Git;

/// <summary>
/// 一个项目的 Git 操作(移植自 2.x 的 <c>ipc/git.ts</c>):概览、分支、提交、diff、暂存、提交、拉取 / 推送、生成提交信息。
/// <para>
/// ★ <b>全部经项目的 <see cref="IExecutionEnvironment"/></b>:git 以 argv 形式执行(<see cref="CommandSpec.Exec"/>,不经 shell),读文件经 <c>env.Fs</c>。
/// 输入 —— 分支名、提交信息、文件路径 —— 全部来自控制端,拼进 shell 字符串就必须自己做引号转义,而转义只要漏一个形状就是一次命令注入
/// (一个叫 <c>$(rm -rf ~)</c> 的分支名足够了)。argv 数组压根不起 shell,这个问题在源头就不存在。
/// </para>
/// <para>
/// ★ <b>flag 注入</b>:argv 解决了 shell,但没解决 git 自己 —— 一个叫 <c>--upload-pack=...</c> 的分支名仍然会被 git 当成选项。路径一律放在 <c>--</c> 之后;
/// <c>--</c> 管不到的位置(<c>switch &lt;branch&gt;</c>)用 <see cref="SafeRef"/> 挡掉前导 <c>-</c>。另加 <c>--literal-pathspecs</c>:
/// 带 <c>[</c> <c>*</c> 的文件名不会被 git 当成通配(2.x 没有这一条,<c>a[1].txt</c> 暂存不了)。
/// </para>
/// <para>
/// ★ <b>「开不出来」不是错误</b>:目录没了 / 没装 git / 不是仓库,<see cref="OverviewAsync"/> 返回 <see cref="GitOverview.Available"/> = false 和原因,分支 / 提交列表返回空;
/// 写操作遇到同样情形才抛 <see cref="GitException"/>(<c>git.unavailable.*</c>)。
/// </para>
/// <para>
/// ★ <b>两个 cwd</b>:不带路径参数的命令(status / log / branch / pull……)用项目根;带路径参数的(add / reset / diff -- path / ls-files)必须用<b>仓库根</b>。
/// porcelain 给的路径是仓库根相对的,而 git 的 pathspec 是 cwd 相对的 —— 两者在「项目就是仓库根」时恰好一样,这个错能一路活到有人在子目录里打开项目为止。
/// </para>
/// 本类型每个项目一份、很便宜:不持有任何连接,Host 按调用现造。
/// </summary>
public sealed class GitService(IExecutionEnvironment environment, EnvPath root, GitServiceOptions? options = null)
{
    private const int ReadOutputLimit = 16 * 1024 * 1024;

    /// <summary>发给模型的参考提交条数。够它认出语言和风格,再多是浪费。</summary>
    private const int SubjectSamples = 8;

    /// <summary>
    /// <c>--no-optional-locks</c>:别和用户开着的 IDE / GUI 抢 index 锁。<c>core.quotePath=false</c>:否则中文、带空格的路径会被 git 转义成八进制。
    /// <c>--literal-pathspecs</c>:见类型说明。
    /// </summary>
    private static readonly string[] Prefix = ["--no-optional-locks", "-c", "core.quotePath=false", "--literal-pathspecs"];

    /// <summary>关掉一切会等输入的东西:pull / push 遇到要密码的 remote 时 git 会去开终端提示符,而这里没有终端,它就永远挂在那儿。宁可当场失败,把 stderr 如实给用户。</summary>
    private static readonly IReadOnlyDictionary<string, string> NoPromptEnvironment = new Dictionary<string, string>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_ASKPASS"] = "",
        ["SSH_ASKPASS"] = "",
    };

    private static readonly Regex BinaryLine = new(@"^Binary files .* differ\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex BadRef = new(@"[\s\0~^:?*\[\\]", RegexOptions.CultureInvariant);

    private readonly GitServiceOptions _options = options ?? new GitServiceOptions();

    // ───────────────────────── 概览 / 分支 / 提交 ─────────────────────────

    public async Task<GitOverview> OverviewAsync(CancellationToken ct = default)
    {
        var repo = await OpenRepoAsync(ct).ConfigureAwait(false);
        if (repo.Reason is { } reason)
        {
            return GitOverview.Unavailable(reason);
        }

        var status = await RunAsync(root, ["status", "--porcelain=v2", "--branch", "-z"], _options.Timeouts.Read, ct).ConfigureAwait(false);
        if (status.Missing)
        {
            return GitOverview.Unavailable(GitUnavailableReason.GitMissing);
        }

        if (!status.Ok)
        {
            throw Failure(status);
        }

        return new GitOverview(true, null, repo.Root!.Value.Value, GitStatus.Parse(status.Stdout));
    }

    public async Task<IReadOnlyList<GitBranch>> BranchesAsync(CancellationToken ct = default)
    {
        if ((await OpenRepoAsync(ct).ConfigureAwait(false)).Reason is not null)
        {
            return [];
        }

        // ★ 用 %09(制表符)当分隔符是安全的:git 的 ref 名禁止一切 ASCII 控制字符,所以分支名和上游名里都不可能出现它。
        var result = await RunAsync(root, ["branch", "--list", "--format=%(refname:short)%09%(upstream:short)%09%(HEAD)"], _options.Timeouts.Read, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return [];
        }

        var branches = new List<GitBranch>();
        foreach (var line in result.Stdout.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var fields = line.TrimEnd('\r').Split('\t');
            var name = fields[0];
            // detached HEAD 时 git 会列出 "(HEAD detached at abc)" 这样的伪条目,它不是一个能切过去的分支
            if (name.Length == 0 || name.StartsWith('('))
            {
                continue;
            }

            branches.Add(new GitBranch(name, fields.ElementAtOrDefault(2)?.Trim() == "*", fields.ElementAtOrDefault(1) ?? ""));
        }

        return branches;
    }

    /// <summary>提交记录的字段 / 记录分隔符 —— ASCII 的 US(0x1f)和 RS(0x1e)。提交标题里不可能出现控制字符,所以不必担心它们把一条记录劈成两半。</summary>
    private const char Field = '\u001f';

    private const char Record = '\u001e';

    public async Task<IReadOnlyList<GitCommit>> CommitsAsync(int limit = 50, CancellationToken ct = default)
    {
        if ((await OpenRepoAsync(ct).ConfigureAwait(false)).Reason is not null)
        {
            return [];
        }

        var count = Math.Clamp(limit, 1, 500);
        var result = await RunAsync(root, ["log", $"-{count}", $"--format=%H{Field}%h{Field}%s{Field}%an{Field}%at{Record}"], _options.Timeouts.Read, ct).ConfigureAwait(false);

        // ★ 空仓库(还没有第一个提交)时 git log 退出 128。那不是错误,是「还没有提交」:返回空数组,让界面显示空态而不是一条红色报错。
        if (!result.Ok)
        {
            return [];
        }

        var commits = new List<GitCommit>();
        foreach (var record in result.Stdout.Split(Record))
        {
            // git 在记录之间还会加一个换行,它落在 RS 之后
            var line = record.StartsWith('\n') ? record[1..] : record;
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var fields = line.Split(Field);
            var hash = fields[0];
            if (hash.Length == 0)
            {
                continue;
            }

            var seconds = long.TryParse(fields.ElementAtOrDefault(4), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var at) ? at : 0;
            commits.Add(new GitCommit(hash, fields.ElementAtOrDefault(1) ?? "", fields.ElementAtOrDefault(2) ?? "", fields.ElementAtOrDefault(3) ?? "", seconds * 1000));
        }

        return commits;
    }

    // ───────────────────────── diff ─────────────────────────

    public async Task<GitDiff> DiffAsync(string path, bool staged, CancellationToken ct = default)
    {
        var repo = await RequireRepoAsync(ct).ConfigureAwait(false);
        path = SafePath(path);

        if (!staged)
        {
            // ★ 先问 git「这个文件跟踪了吗」。未跟踪文件的 git diff 输出是空的(它不在 index 里,没有可比的一侧),
            //   直接跑的话用户点开一个新文件只会看到一片空白,而那看起来完全像是加载失败。
            var tracked = await RunAsync(repo, ["ls-files", "--error-unmatch", "--", path], _options.Timeouts.Read, ct).ConfigureAwait(false);
            if (!tracked.Ok)
            {
                return await ReadUntrackedAsync(repo, path, ct).ConfigureAwait(false);
            }
        }

        var args = new List<string> { "diff", "--no-color", "--no-ext-diff" };
        if (staged)
        {
            args.Add("--cached");
        }

        args.Add("--");
        args.Add(path);
        var result = await RunAsync(repo, args, _options.Timeouts.Read, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            throw Failure(result);
        }

        return ClampDiff(path, staged, result.Stdout);
    }

    /// <summary>
    /// 对比视图的两侧全文。基准和 <c>git diff</c> 一致:看工作区是「暂存区 → 工作区」,看暂存区是「HEAD → 暂存区」。
    /// 返回 null = 这一份没法按全文对比(二进制、超过上限、冲突中、子模块),界面退回 <see cref="DiffAsync"/> 的 unified 文本。
    /// </summary>
    public async Task<GitDiffSides?> DiffSidesAsync(string path, bool staged, CancellationToken ct = default)
    {
        var repo = await RequireRepoAsync(ct).ConfigureAwait(false);
        path = SafePath(path);
        var entry = await IndexEntryAsync(repo, path, ct).ConfigureAwait(false);
        if (entry.Special)
        {
            return null;
        }

        string? original;
        string? modified;
        if (staged)
        {
            // 暂存区视图:HEAD → index。index 里没有 = 暂存了一次删除
            original = await ReadBlobSideAsync(repo, $"HEAD:{path}", ct).ConfigureAwait(false);
            modified = entry.Blob is null ? "" : await ReadBlobSideAsync(repo, entry.Blob, ct).ConfigureAwait(false);
        }
        else
        {
            // 工作区视图:index → 工作区。未跟踪文件没有可比的一侧,原文就是空的
            original = entry.Blob is null ? "" : await ReadBlobSideAsync(repo, entry.Blob, ct).ConfigureAwait(false);
            modified = await ReadWorktreeSideAsync(repo, path, ct).ConfigureAwait(false);
        }

        return original is null || modified is null ? null : new GitDiffSides(original, modified);
    }

    private GitDiff ClampDiff(string path, bool staged, string text)
    {
        var binary = BinaryLine.IsMatch(text) || text.Contains("GIT binary patch", StringComparison.Ordinal);
        var truncated = !binary && text.Length > _options.MaxDiffChars;
        return new GitDiff(path, staged, binary, truncated, binary ? "" : truncated ? CutChars(text, _options.MaxDiffChars) : text);
    }

    /// <summary>
    /// 未跟踪文件的 diff。★ 自己拼,不走 <c>git diff --no-index /dev/null &lt;path&gt;</c>:那条命令在 Windows 上没有可靠的 null 设备写法,
    /// 而未跟踪文件恰恰是新建项目里最常点开的那一类。拼出来的是标准 unified diff,和 git 的输出同形,界面那边不需要第二套渲染。
    /// </summary>
    private async Task<GitDiff> ReadUntrackedAsync(EnvPath repo, string path, CancellationToken ct)
    {
        var empty = new GitDiff(path, false, false, false, "");
        var target = InRepo(repo, path);
        string content;
        bool truncated;
        try
        {
            var info = await environment.Fs.LStatAsync(target, ct).ConfigureAwait(false);
            if (info.IsSymlink)
            {
                // git 存的是链接目标的文本;跟进去读目标文件会把根外的内容带进来
                if (await ReadLinkAsync(target, ct).ConfigureAwait(false) is not { } link)
                {
                    return empty;
                }

                content = link;
                truncated = false;
            }
            else if (!info.IsFile)
            {
                return empty;
            }
            else
            {
                var bytes = await ReadUpToAsync(target, _options.MaxDiffChars, ct).ConfigureAwait(false);
                // NUL 字节 = 二进制。和 git 自己的判据一致
                if (Array.IndexOf(bytes, (byte)0) >= 0)
                {
                    return empty with { Binary = true };
                }

                content = Encoding.UTF8.GetString(bytes);
                truncated = info.Size > _options.MaxDiffChars;
            }
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            // 列出来之后文件又没了 —— 给一个空 diff,别报错
            return empty;
        }

        var lines = content.Length == 0 ? [] : (content.EndsWith('\n') ? content[..^1] : content).Split('\n');
        var header = $"--- /dev/null\n+++ b/{path}\n@@ -0,0 +1,{lines.Length} @@\n";
        return new GitDiff(path, false, false, truncated, header + string.Join('\n', lines.Select(static l => "+" + l)));
    }

    /// <summary>
    /// 从对象库读一个 blob。★ 用 <c>cat-file blob</c> 而不是 <c>show</c>:前者给的是原始内容,不走 textconv。
    /// 退出码 128 = 对象不存在(新文件在 HEAD 里没有、空仓库没有 HEAD)—— 那一侧就是空的,不是失败。
    /// </summary>
    private async Task<string?> ReadBlobSideAsync(EnvPath repo, string objectName, CancellationToken ct)
    {
        var result = await RunAsync(repo, ["cat-file", "blob", objectName], _options.Timeouts.Read, ct, limit: SideLimitBytes()).ConfigureAwait(false);
        if (result.Code == 128)
        {
            return "";
        }

        return result.Ok ? TextSide(result.Stdout) : null;
    }

    /// <summary>
    /// 读工作区里的那一侧。★ 符号链接读<b>链接本身</b>(目标路径文本),不跟进去 —— git 存的就是这个,
    /// 跟进去读目标文件的话,一个没动过的链接也会显示成「整份重写」。
    /// </summary>
    private async Task<string?> ReadWorktreeSideAsync(EnvPath repo, string path, CancellationToken ct)
    {
        var target = InRepo(repo, path);
        FsEntryInfo info;
        try
        {
            info = await environment.Fs.LStatAsync(target, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            return ""; // 工作区里删掉了 —— 这一侧就是空的
        }

        try
        {
            if (info.IsSymlink)
            {
                return await ReadLinkAsync(target, ct).ConfigureAwait(false);
            }

            if (!info.IsFile || info.Size > SideLimitBytes())
            {
                return null;
            }

            return TextSide(Encoding.UTF8.GetString(await ReadUpToAsync(target, SideLimitBytes(), ct).ConfigureAwait(false)));
        }
        catch (EnvironmentException e) when (e.Code is not (EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.StaleGeneration))
        {
            return null;
        }
    }

    private int SideLimitBytes() => _options.MaxSideChars * 4;

    private string? TextSide(string content) =>
        content.Length > _options.MaxSideChars || content.Contains('\0') ? null : content;

    /// <summary>
    /// 链接目标的文本。<c>IEnvironmentFs</c> 没有 readlink,POSIX 环境里经环境的进程跑 <c>readlink</c>(本机和 SSH 一致);
    /// Windows 环境读不了,返回 null(调用方退回 unified 文本)。
    /// </summary>
    private async Task<string?> ReadLinkAsync(EnvPath path, CancellationToken ct)
    {
        if (environment.Facts.Os == EnvironmentOs.Windows)
        {
            return null;
        }

        var result = await environment.RunAsync(CommandSpec.Exec("readlink", "--", path.Value), new RunOptions { Timeout = _options.Timeouts.Read, OutputPolicy = OutputPolicies.Head(8192) }, ct).ConfigureAwait(false);
        // macOS 的 readlink 不认 --:失败再试不带的
        if (!result.Succeeded)
        {
            result = await environment.RunAsync(CommandSpec.Exec("readlink", path.Value), new RunOptions { Timeout = _options.Timeouts.Read, OutputPolicy = OutputPolicies.Head(8192) }, ct).ConfigureAwait(false);
        }

        return result.Succeeded ? result.Stdout.ToText().TrimEnd('\n') : null;
    }

    private async Task<byte[]> ReadUpToAsync(EnvPath path, int max, CancellationToken ct)
    {
        var stream = await environment.Fs.OpenReadAsync(path, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[Math.Min(max, 81920)];
            while (buffer.Length < max)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, max - buffer.Length)), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    /// <summary>
    /// 这个路径在 index 里的条目:<c>Special</c> = 冲突中(有 1/2/3 号 stage)或子模块(160000),全文对比说不清楚;
    /// <c>Blob</c> 为 null 而 <c>Special</c> 为假 = 不在 index 里(未跟踪);否则是 stage 0 那条的 blob。
    /// </summary>
    private async Task<(bool Special, string? Blob)> IndexEntryAsync(EnvPath repo, string path, CancellationToken ct)
    {
        var result = await RunAsync(repo, ["ls-files", "--stage", "-z", "--", path], _options.Timeouts.Read, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (true, null);
        }

        // 每条:`<mode> <blob> <stage>\t<path>`。pathspec 命中目录时会带出多条,只认精确路径
        var entries = new List<(string Mode, string Blob, string Stage)>();
        foreach (var item in result.Stdout.Split('\0'))
        {
            var tab = item.IndexOf('\t');
            if (tab < 0 || item[(tab + 1)..] != path)
            {
                continue;
            }

            var fields = item[..tab].Split(' ');
            entries.Add((fields[0], fields.ElementAtOrDefault(1) ?? "", fields.ElementAtOrDefault(2) ?? ""));
        }

        if (entries.Count == 0)
        {
            return (false, null);
        }

        var entry = entries[0];
        return entries.Count > 1 || entry.Stage != "0" || entry.Mode == "160000" ? (true, null) : (false, entry.Blob);
    }

    // ───────────────────────── 写 ─────────────────────────

    public async Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var repo = await RequireRepoAsync(ct).ConfigureAwait(false);
        var safe = SafePaths(paths);
        // ★ -A 让删除也能被暂存 —— 只写 git add <path> 的话,删掉的文件暂存不了
        await RunOrFailAsync(repo, ["add", "-A", "--", .. safe], _options.Timeouts.Write, ct).ConfigureAwait(false);
    }

    public async Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var repo = await RequireRepoAsync(ct).ConfigureAwait(false);
        var safe = SafePaths(paths);

        // ★ 空仓库要换一条命令。git reset -- <path> 要拿 HEAD 当比较基准,而还没有第一个提交时 HEAD 不存在,它会 fatal。
        //   这种仓库里取消暂存 = 把条目从 index 删掉。
        var head = await RunAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD"], _options.Timeouts.Read, ct).ConfigureAwait(false);
        string[] args = head.Ok ? ["reset", "-q", "HEAD", "--", .. safe] : ["rm", "-q", "--cached", "-r", "--", .. safe];
        await RunOrFailAsync(repo, args, _options.Timeouts.Write, ct).ConfigureAwait(false);
    }

    public async Task<GitCommit> CommitAsync(string message, CancellationToken ct = default)
    {
        await RequireRepoAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new GitException(GitErrorKind.InvalidRequest, GitKeys.EmptyMessage);
        }

        await RunOrFailAsync(root, ["commit", "-m", message], _options.Timeouts.Write, ct).ConfigureAwait(false);
        var commits = await CommitsAsync(1, ct).ConfigureAwait(false);
        return commits.Count > 0 ? commits[0] : throw new GitException(GitErrorKind.Failed, GitKeys.Failed, "git log returned no commit after commit");
    }

    public async Task CheckoutAsync(string branch, CancellationToken ct = default)
    {
        await RequireRepoAsync(ct).ConfigureAwait(false);
        await RunOrFailAsync(root, ["switch", SafeRef(branch)], _options.Timeouts.Write, ct).ConfigureAwait(false);
    }

    public async Task<GitBranch> CreateBranchAsync(string name, bool checkout, CancellationToken ct = default)
    {
        await RequireRepoAsync(ct).ConfigureAwait(false);
        var safe = SafeRef(name);
        await RunOrFailAsync(root, checkout ? ["switch", "-c", safe] : ["branch", safe], _options.Timeouts.Write, ct).ConfigureAwait(false);
        var branches = await BranchesAsync(ct).ConfigureAwait(false);
        return branches.FirstOrDefault(b => b.Name == safe) ?? new GitBranch(safe, checkout, "");
    }

    public async Task PullAsync(CancellationToken ct = default)
    {
        await RequireRepoAsync(ct).ConfigureAwait(false);
        // ★ --ff-only:自动合并出冲突的话,用户会在一个没有终端的面板里面对一个半合并状态。快进不了就如实失败,把「需要合并」这件事交回给用户。
        await RunOrFailAsync(root, ["pull", "--ff-only"], _options.Timeouts.Network, ct).ConfigureAwait(false);
    }

    public async Task PushAsync(CancellationToken ct = default)
    {
        await RequireRepoAsync(ct).ConfigureAwait(false);
        // ★ 没有上游时补 -u origin <branch>。裸 git push 在这种分支上会失败并建议一条命令,而用户点的就是「推送」,让他去终端粘一条命令没有道理。
        var upstream = await RunAsync(root, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], _options.Timeouts.Read, ct).ConfigureAwait(false);
        string[] args = ["push"];
        if (!upstream.Ok)
        {
            var branch = await RunAsync(root, ["branch", "--show-current"], _options.Timeouts.Read, ct).ConfigureAwait(false);
            var current = branch.Stdout.Trim();
            // detached HEAD:没有分支可推,也没有合理的默认值可猜
            if (current.Length == 0)
            {
                throw new GitException(GitErrorKind.InvalidRequest, GitKeys.DetachedPush);
            }

            args = ["push", "-u", "origin", current];
        }

        await RunOrFailAsync(root, args, _options.Timeouts.Network, ct).ConfigureAwait(false);
    }

    // ───────────────────────── 生成提交信息 ─────────────────────────

    /// <summary>
    /// 读暂存区,交给模型写一条提交信息草稿(填进输入框,不直接提交:真正的闸门在人身上)。
    /// ★ diff 在这里截断,不在生成器里:截断需要知道「原本有多长」才能如实告诉模型它看到的是残篇,而这份原文只有这儿有。
    /// </summary>
    public async Task<string> GenerateCommitMessageAsync(ICommitMessageModel? model, CommitMessageGeneratorOptions? generatorOptions = null, CancellationToken ct = default)
    {
        await RequireRepoAsync(ct).ConfigureAwait(false);
        var diffResult = await RunAsync(root, ["diff", "--cached", "--no-color", "--no-ext-diff"], _options.Timeouts.Read, ct).ConfigureAwait(false);
        if (!diffResult.Ok)
        {
            throw Failure(diffResult);
        }

        var full = diffResult.Stdout;
        if (full.AsSpan().Trim().IsEmpty)
        {
            throw new GitException(GitErrorKind.InvalidRequest, GitKeys.NothingStaged);
        }

        var truncated = full.Length > CommitMessageGenerator.DiffLimit;
        var diff = truncated ? CutChars(full, CommitMessageGenerator.DiffLimit) : full;

        // 文件清单单独取:diff 被截断时它仍然是完整的,模型至少知道还动了些什么
        var names = await RunAsync(root, ["diff", "--cached", "--name-only", "-z"], _options.Timeouts.Read, ct).ConfigureAwait(false);
        var files = names.Ok ? names.Stdout.Split('\0').Where(static n => n.Length > 0).Take(GitStatus.MaxFiles).ToList() : [];

        // 空仓库里 git log 退出 128 —— 那是「还没有提交」,不是失败,给空样本就行
        var log = await RunAsync(root, ["log", $"-{SubjectSamples}", "--format=%s"], _options.Timeouts.Read, ct).ConfigureAwait(false);
        var subjects = log.Ok ? log.Stdout.Split('\n').Select(static l => l.Trim()).Where(static l => l.Length > 0).ToList() : [];

        if (model is null)
        {
            throw new GitException(GitErrorKind.InvalidRequest, GitKeys.AiNoModel);
        }

        return await new CommitMessageGenerator(model, generatorOptions).GenerateAsync(new CommitMessageRequest(diff, files, subjects, truncated), ct).ConfigureAwait(false);
    }

    // ───────────────────────── 仓库解析 ─────────────────────────

    private async Task<(GitUnavailableReason? Reason, EnvPath? Root)> OpenRepoAsync(CancellationToken ct)
    {
        // ★ 一条命令吸收两种情况:没装 git(missing)、不是仓库(非零退出)。--is-inside-work-tree 在裸仓库里返回 false 而退出码为 0 ——
        //   裸仓库没有工作区,这个面板的暂存 / 提交全都无从谈起,所以也归到 NotRepository。
        GitRun inside;
        try
        {
            inside = await RunAsync(root, ["rev-parse", "--is-inside-work-tree"], _options.Timeouts.Read, ct).ConfigureAwait(false);
        }
        catch (GitException e) when (e.Kind == GitErrorKind.Unavailable && e.Key == GitKeys.Unavailable(GitUnavailableReason.WorkspaceUnavailable))
        {
            return (GitUnavailableReason.WorkspaceUnavailable, null);
        }

        if (inside.Missing)
        {
            return (GitUnavailableReason.GitMissing, null);
        }

        if (!inside.Ok || inside.Stdout.Trim() != "true")
        {
            return (GitUnavailableReason.NotRepository, null);
        }

        var top = await RunAsync(root, ["rev-parse", "--show-toplevel"], _options.Timeouts.Read, ct).ConfigureAwait(false);
        return top.Ok ? (null, EnvPath.Parse(top.Stdout.Trim(), environment.Facts.PathStyle)) : (GitUnavailableReason.NotRepository, null);
    }

    /// <summary>拿到仓库根或抛错。写操作用这个:到了要 commit 的时候「不可用」就是真的失败了。</summary>
    private async Task<EnvPath> RequireRepoAsync(CancellationToken ct)
    {
        var (reason, repo) = await OpenRepoAsync(ct).ConfigureAwait(false);
        return reason is { } r ? throw new GitException(GitErrorKind.NotAvailable, GitKeys.Unavailable(r)) : repo!.Value;
    }

    // ───────────────────────── 进程 ─────────────────────────

    private async Task<GitRun> RunAsync(EnvPath cwd, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, int limit = ReadOutputLimit)
    {
        var spec = new CommandSpec { Program = "git", Args = [.. Prefix, .. args], WorkingDirectory = cwd, Environment = GitEnvironment.Merge(NoPromptEnvironment, _options.ExtraEnvironment?.Invoke()) };
        ProcessResult result;
        try
        {
            result = await environment.RunAsync(spec, new RunOptions { Timeout = timeout, OutputPolicy = OutputPolicies.Head(limit) }, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.ProcessStartFailed)
        {
            // 本机没装 git / 不在 PATH 里
            return new GitRun(127, "", e.Message, Missing: true);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory or EnvironmentErrorCode.InvalidPath)
        {
            throw new GitException(GitErrorKind.Unavailable, GitKeys.Unavailable(GitUnavailableReason.WorkspaceUnavailable));
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.StaleGeneration or EnvironmentErrorCode.ConnectionFailed or EnvironmentErrorCode.ResultUnknown)
        {
            throw new GitException(GitErrorKind.Unavailable, GitKeys.Unavailable(GitUnavailableReason.WorkspaceUnavailable), e.Code.ToString());
        }
        catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.Unsupported)
        {
            throw new GitException(GitErrorKind.Unsupported, GitKeys.Failed, e.Code.ToString());
        }

        switch (result.Outcome)
        {
            case ProcessOutcome.TimedOut:
                throw new GitException(GitErrorKind.Unavailable, GitKeys.Timeout, $"git {args[0]} timed out");
            case ProcessOutcome.Cancelled:
                ct.ThrowIfCancellationRequested();
                throw new OperationCanceledException();
            case ProcessOutcome.ConnectionLost:
                throw new GitException(GitErrorKind.Unavailable, GitKeys.Unavailable(GitUnavailableReason.WorkspaceUnavailable), "connection lost");
        }

        var stderr = result.Stderr.ToText();
        // SSH 的 exec 经远端 shell 起命令:没有 git 时是退出码 127 + "not found",而不是启动失败
        var missing = result.ExitCode == 127 && stderr.Contains("not found", StringComparison.OrdinalIgnoreCase);
        return new GitRun(result.ExitCode ?? -1, result.Stdout.ToText(), stderr, missing);
    }

    private async Task RunOrFailAsync(EnvPath cwd, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var result = await RunAsync(cwd, args, timeout, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            throw Failure(result);
        }
    }

    /// <summary>git 失败时给用户看的那句话:优先 stderr —— git 的 fatal 信息本身就是最好的解释。</summary>
    private static GitException Failure(GitRun result)
    {
        if (result.Missing)
        {
            return new GitException(GitErrorKind.NotAvailable, GitKeys.Unavailable(GitUnavailableReason.GitMissing));
        }

        var detail = result.Stderr.Trim().Length > 0 ? result.Stderr.Trim() : result.Stdout.Trim();
        // 别的 git 进程(或用户开着的 IDE)占着 index:等一下再来就好,不是这次操作本身的错
        return detail.Contains("index.lock", StringComparison.Ordinal)
            ? new GitException(GitErrorKind.Busy, GitKeys.Failed, detail)
            : new GitException(GitErrorKind.Failed, GitKeys.Failed, detail.Length == 0 ? $"git exited with {result.Code}" : detail);
    }

    // ───────────────────────── 输入校验 ─────────────────────────

    /// <summary>
    /// 仓库相对路径。<b>只校验形状,不做 fs 审计</b> —— 它不参与路径拼接,只被原样放在 <c>--</c> 之后递给 git
    /// (读文件的两处用 <see cref="InRepo"/> 另外查一次)。
    /// </summary>
    internal static string SafePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0') || path.StartsWith('/') || path.StartsWith('\\')
            || (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0])))
        {
            throw new GitException(GitErrorKind.InvalidRequest, GitKeys.InvalidPath);
        }

        if (path.Split('/', '\\').Any(static part => part == ".."))
        {
            throw new GitException(GitErrorKind.InvalidRequest, GitKeys.InvalidPath);
        }

        return path;
    }

    private static List<string> SafePaths(IReadOnlyList<string>? paths) =>
        paths is null or { Count: 0 } ? throw new GitException(GitErrorKind.InvalidRequest, GitKeys.InvalidPath) : [.. paths.Select(SafePath)];

    /// <summary>
    /// 分支名 / ref。★ 这里挡的是 <b>flag 注入</b>,不是 shell 注入:<c>git switch &lt;name&gt;</c> 里的 name 没有 <c>--</c> 可以垫,
    /// 一个叫 <c>--orphan</c> 的分支名会被当成选项。git 自己的 check-ref-format 本来就禁掉了空格和大部分特殊字符,前导 <c>-</c> 是它允许、而我们必须拒的那一类。
    /// </summary>
    internal static string SafeRef(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.StartsWith('-') || BadRef.IsMatch(trimmed))
        {
            throw new GitException(GitErrorKind.InvalidRequest, GitKeys.InvalidBranch);
        }

        return trimmed;
    }

    /// <summary>仓库相对路径 → 环境里的绝对路径。只有两处真的去拼 fs 路径(读未跟踪文件 / 读工作区一侧),越界一律拒。</summary>
    private static EnvPath InRepo(EnvPath repo, string path)
    {
        var target = repo.Join(path.Split('/', '\\'));
        return target.IsWithin(repo) && target.Value != repo.Value ? target : throw new GitException(GitErrorKind.InvalidRequest, GitKeys.InvalidPath);
    }

    private static string CutChars(string text, int max)
    {
        // 别把一个代理对劈成两半
        var length = max > 0 && max < text.Length && char.IsHighSurrogate(text[max - 1]) ? max - 1 : Math.Min(max, text.Length);
        return text[..length];
    }
}
