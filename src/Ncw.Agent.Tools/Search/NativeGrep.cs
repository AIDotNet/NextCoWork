using System.Text.RegularExpressions;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Tools;

internal sealed record NativeGrepRequest(
    string Pattern,
    bool IgnoreCase,
    bool Multiline,
    EnvPath Target,
    bool IsDirectory,
    IReadOnlyList<string>? NameHints,
    int MaxPerFile,
    TimeSpan Timeout);

internal abstract record NativeGrepOutcome
{
    /// <param name="Hits">绝对路径 → 命中行号(升序、去重)。</param>
    /// <param name="TimedOut">原生进程超时被杀,结果是部分的。</param>
    /// <param name="Truncated">stdout 撑到了上限,结果是部分的。</param>
    public sealed record Ok(string Backend, IReadOnlyDictionary<EnvPath, List<int>> Hits, bool TimedOut, bool Truncated) : NativeGrepOutcome;

    /// <param name="RegexError">rg 明确说正则不合法时的原话 —— 托管引擎也编译失败时拿它报给模型。</param>
    public sealed record Unavailable(string? RegexError = null) : NativeGrepOutcome;
}

/// <summary>
/// Grep 的<b>原生后端</b>(移植自 2.x <c>grep-native.ts</c>):优先交给环境里现成的搜索程序,托管引擎只做最后的兜底。优先级 <c>rg</c> &gt; <c>git grep</c> &gt; <c>grep</c> &gt; 托管。
/// 它们跑在独立进程里,卡住的是它们自己,超时由环境整树杀掉。
/// <list type="bullet">
/// <item><b>只负责「哪些文件的哪些行命中」</b>:行正文、上下文、三种输出模式的渲染都在 GrepTool,所以换后端不换输出格式。</item>
/// <item>「没装 / 不支持这个参数 / 方言不认这个正则 / 跑挂了」一律是 <c>Unavailable</c>,调用方顺延到下一个后端。<b>绝不把一次失败当成「没有命中」</b>:那会给出一个很像是真的「仓库里没有」。</item>
/// <item>固定忽略表在原生侧尽量下推,GrepTool 还会再按同一张表过滤一遍;文件名过滤同理(下推的永远是超集)。rg / git grep 另外遵守 <c>.gitignore</c>。</item>
/// </list>
/// 3.0 的差异:命令用 <see cref="CommandSpec.Exec"/>(程序 + 参数向量),不经过 shell,所以 2.x 的 <c>quoteShellArg</c>(含 cmd 下引不了的字符就整个后端顺延)整个消失。
/// </summary>
internal static class NativeGrep
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private const long OutputCap = 512 * 1024;

    public static async Task<NativeGrepOutcome> RunAsync(ToolRuntime rt, NativeGrepRequest req, CancellationToken ct)
    {
        var allowed = rt.GrepBackends;
        if (allowed == GrepBackends.None)
        {
            return new NativeGrepOutcome.Unavailable();
        }

        var env = rt.Environment;
        var facts = await rt.ProbeAsync().ConfigureAwait(false);
        var cwd = req.IsDirectory ? req.Target : req.Target.Parent;
        var operand = req.IsDirectory ? "." : req.Target.FileName;

        var plans = new List<(string Backend, Func<Task<CommandSpec?>> Build)>();
        if (facts.HasTool("rg") && allowed.HasFlag(GrepBackends.Rg))
        {
            plans.Add(("rg", () => Task.FromResult<CommandSpec?>(Spec("rg", RgArgs(req, operand), cwd))));
        }

        // git grep / grep 都没有跨行匹配,multiline 只有 rg 能接
        if (!req.Multiline && facts.HasTool("git") && allowed.HasFlag(GrepBackends.Git))
        {
            plans.Add(("git", async () => await GitEligibleAsync(env, cwd, operand, ct).ConfigureAwait(false) ? Spec("git", GitArgs(req, operand), cwd) : null));
        }

        if (!req.Multiline && facts.HasTool("grep") && allowed.HasFlag(GrepBackends.Grep))
        {
            plans.Add(("grep", async () => Spec("grep", GrepArgs(req, operand, await GrepFlavorAsync(env, cwd, ct).ConfigureAwait(false)), cwd)));
        }

        string? regexError = null;
        foreach (var (backend, build) in plans)
        {
            ct.ThrowIfCancellationRequested();
            var spec = await build().ConfigureAwait(false);
            if (spec is null)
            {
                continue;
            }

            ProcessResult result;
            try
            {
                result = await env.RunAsync(spec, new RunOptions { Timeout = req.Timeout, OutputPolicy = OutputPolicies.Head(OutputCap) }, ct).ConfigureAwait(false);
            }
            catch (EnvironmentException)
            {
                continue; // 起不来:这个后端用不了
            }

            ct.ThrowIfCancellationRequested(); // 中止原样往上抛,不顺延:用户按了停止,不该再换个后端重跑一遍
            if (result.Outcome == ProcessOutcome.ConnectionLost)
            {
                continue;
            }

            var timedOut = result.Outcome == ProcessOutcome.TimedOut;
            var hits = Parse(cwd, result.Stdout.ToText());
            // 有输出就算数,不论退出码:rg / grep 遇到读不了的文件会以 2 退出,但已经输出的命中是真的。
            // 没有输出时只认 0/1 是「确实没命中」,其余都是这个后端出了事 —— 顺延
            var code = result.ExitCode;
            if (hits.Count > 0 || timedOut || code is 0 or 1)
            {
                if (hits.Count == 0 && code == 0 && !timedOut)
                {
                    continue; // 退出码说有命中却解析不出一条:输出格式对不上(被别名/包装改过),不信它
                }

                return new NativeGrepOutcome.Ok(backend, hits, timedOut, result.Stdout.Truncated);
            }

            if (backend == "rg")
            {
                regexError = RgRegexError(result.Stderr.ToText());
            }
        }

        return new NativeGrepOutcome.Unavailable(regexError);
    }

    private static CommandSpec Spec(string program, IReadOnlyList<string> args, EnvPath cwd) =>
        new() { Program = program, Args = args, WorkingDirectory = cwd };

    private static List<string> RgArgs(NativeGrepRequest req, string operand)
    {
        var args = new List<string>
        {
            "--no-config", "--null", "--line-number", "--with-filename", "--no-heading", "--color", "never", "--no-messages",
            // 隐藏文件也搜(.git 等由忽略表挡掉)
            "--hidden",
            // 只要行号,正文由 GrepTool 自己读 —— 压住 stdout,别让一行压缩产物吃掉输出上限
            "--max-columns", "1",
            "--max-filesize", "5M",
            "--max-count", req.MaxPerFile.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // 有序输出:结果可复现,而且被截断时拿到的是一个前缀,不是随机子集
            "--sort", "path",
            // 默认 Rust 引擎不认的写法(环视、反向引用)自动切 PCRE2
            "--engine", "auto",
        };
        if (req.IgnoreCase)
        {
            args.Add("-i");
        }

        if (req.Multiline)
        {
            args.AddRange(["-U", "--multiline-dotall"]);
        }

        if (req.IsDirectory)
        {
            foreach (var name in IgnoreList.SkipDirs.Concat(IgnoreList.SkipFiles))
            {
                args.AddRange(["--glob", $"!{name}"]);
            }
        }

        foreach (var hint in req.NameHints ?? [])
        {
            args.AddRange(["--iglob", hint]);
        }

        args.AddRange(["-e", req.Pattern, "--", operand]);
        return args;
    }

    private static List<string> GitArgs(NativeGrepRequest req, string operand)
    {
        var args = new List<string>
        {
            "-c", "core.quotepath=off", "grep", "--no-color", "-n", "-z", "-I",
            // 未跟踪但没被忽略的文件也搜 —— 新建还没 add 的文件不该「搜不到」
            "--untracked", "-P",
        };
        if (req.IgnoreCase)
        {
            args.Add("-i");
        }

        args.AddRange(["-e", req.Pattern, "--"]);
        if (req.IsDirectory && req.NameHints is not null)
        {
            args.AddRange(req.NameHints.Select(h => $":(glob,icase)**/{h}"));
        }
        else
        {
            args.Add(operand);
        }

        if (req.IsDirectory)
        {
            args.AddRange(IgnoreList.SkipDirs.Select(d => $":(exclude,glob)**/{d}/**"));
        }

        return args;
    }

    private static List<string> GrepArgs(NativeGrepRequest req, string operand, char flavor)
    {
        var args = new List<string> { "-r", "-n", "-H", "-I", "-s", "--null", "--color=never", $"-{flavor}" };
        if (req.IgnoreCase)
        {
            args.Add("-i");
        }

        args.AddRange(["-m", req.MaxPerFile.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (req.IsDirectory)
        {
            args.AddRange(IgnoreList.SkipDirs.Select(d => $"--exclude-dir={d}"));
            args.AddRange(IgnoreList.SkipFiles.Select(f => $"--exclude={f}"));
        }

        args.AddRange(["-e", req.Pattern, "--", operand]);
        return args;
    }

    /// <summary>
    /// 解析 <c>path\0行号&lt;分隔&gt;正文</c> 一行一条的输出。rg / grep 的分隔是 <c>:</c>,git grep 的 <c>-z</c> 把它也换成了 <c>\0</c> —— 两种都只取行号前的数字。
    /// </summary>
    internal static Dictionary<EnvPath, List<int>> Parse(EnvPath cwd, string stdout)
    {
        var byFile = new Dictionary<EnvPath, SortedSet<int>>();
        foreach (var record in stdout.Split('\n'))
        {
            var nul = record.IndexOf('\0');
            if (nul <= 0)
            {
                continue;
            }

            var rest = record.AsSpan(nul + 1);
            var digits = 0;
            while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
            {
                digits++;
            }

            if (digits == 0 || !int.TryParse(rest[..digits], out var line))
            {
                continue;
            }

            var path = record[..nul];
            while (path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith(".\\", StringComparison.Ordinal))
            {
                path = path[2..];
            }

            var abs = cwd.Join(path);
            if (!byFile.TryGetValue(abs, out var set))
            {
                byFile[abs] = set = [];
            }

            set.Add(line);
        }

        return byFile.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
    }

    /// <summary>起点在 git 工作树里、且自己没被 .gitignore 掉 —— 否则 git grep 会静默地什么都不搜。</summary>
    private static async Task<bool> GitEligibleAsync(IExecutionEnvironment env, EnvPath cwd, string operand, CancellationToken ct)
    {
        var inside = await TryRunAsync(env, Spec("git", ["rev-parse", "--is-inside-work-tree"], cwd), ct).ConfigureAwait(false);
        if (inside is not { ExitCode: 0 } || inside.Stdout.ToText().Trim() != "true")
        {
            return false;
        }

        var ignored = await TryRunAsync(env, Spec("git", ["check-ignore", "-q", "--", operand], cwd), ct).ConfigureAwait(false);
        // 0 = 被忽略,1 = 没被忽略,其余 = 说不清,一律不用
        return ignored?.ExitCode == 1;
    }

    private static async Task<char> GrepFlavorAsync(IExecutionEnvironment env, EnvPath cwd, CancellationToken ct)
    {
        var version = await TryRunAsync(env, Spec("grep", ["--version"], cwd), ct).ConfigureAwait(false);
        // GNU grep 有 -P(PCRE),BSD 只有 -E
        return version is { ExitCode: 0 } && version.Stdout.ToText().Contains("GNU grep", StringComparison.Ordinal) ? 'P' : 'E';
    }

    private static async Task<ProcessResult?> TryRunAsync(IExecutionEnvironment env, CommandSpec spec, CancellationToken ct)
    {
        try
        {
            var result = await env.RunAsync(spec, new RunOptions { Timeout = ProbeTimeout, OutputPolicy = OutputPolicies.Head(16 * 1024) }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (EnvironmentException)
        {
            return null;
        }
    }

    private static readonly Regex RgError = new(@"regex parse error|error parsing regex|PCRE2: error compiling pattern", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>rg 的正则编译错误。只认 rg 的:grep 家族的报错格式五花八门,不值得猜。</summary>
    private static string? RgRegexError(string stderr) => RgError.IsMatch(stderr) ? stderr.Trim() : null;

    /// <summary>
    /// 从 glob 参数里抽出能安全下推给原生程序的 basename 模式。只认两种形状:不含 <c>/</c> 的(如 <c>*.ts</c>),和 <c>**/</c> 开头、后面不再含 <c>/</c> 的。
    /// 原生侧按「任意深度、大小写不敏感」匹配它们,于是下推结果必然是精确语义的<b>超集</b>;其余形状(<c>src/**/*.ts</c> 这种和搜索根有关的)一律不下推。
    /// 花括号展开一层(<c>*.{ts,tsx}</c> → <c>*.ts</c> <c>*.tsx</c>),因为 grep / git 的 pathspec 不认它。
    /// </summary>
    internal static List<string>? NameHints(string glob)
    {
        var tail = glob.StartsWith("**/", StringComparison.Ordinal) ? glob[3..] : glob;
        if (tail.Length == 0 || tail.Contains('/') || tail.Contains('\\'))
        {
            return null;
        }

        var open = tail.IndexOf('{');
        var close = tail.IndexOf('}');
        if (open < 0 && close < 0)
        {
            return [tail];
        }

        if (open < 0 || close < open || tail.IndexOf('{', open + 1) >= 0 || tail.IndexOf('}', close + 1) >= 0)
        {
            return null;
        }

        var prefix = tail[..open];
        var suffix = tail[(close + 1)..];
        return tail[(open + 1)..close].Split(',').Select(alt => prefix + alt + suffix).ToList();
    }
}
