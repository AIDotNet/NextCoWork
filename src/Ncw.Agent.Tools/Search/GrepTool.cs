using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools.Schema;

namespace Ncw.Agent.Tools;

/// <summary>
/// <c>Grep</c>:按内容找。名字、参数对齐 Claude Code(<c>output_mode / -i / -n / -A / -B / -C / head_limit / multiline</c> 都是 CC 的原参数名)。
/// 后端:<c>rg</c> &gt; <c>git grep</c> &gt; <c>grep</c> &gt; 托管正则(见 <see cref="NativeGrep"/>);三种输出模式的渲染只有一份,换后端不换输出格式。
/// <para>
/// <b>托管路径的 ReDoS</b>:正则是模型给的。2.x 用 worker 隔离 + 墙钟终止;.NET 里更简单:<c>RegexOptions.NonBacktracking</c> 是线性时间引擎(根本没有灾难性回溯);
/// 只有用了它不支持的语法(环视、反向引用)才退到回溯引擎,那时先过静态筛查(<see cref="Redos"/>),再带每次匹配的超时,超时就返回「部分结果 + 说明」。
/// 整次搜索另有 5 秒预算,超了同样<b>如实说</b>:静默的部分结果会让模型断言「仓库里没有」。
/// </para>
/// </summary>
public sealed class GrepTool() : BuiltinTool(
    "Grep",
    "A powerful search tool for finding text inside files.\n\n"
    + "Usage:\n"
    + "- ALWAYS use Grep to search file contents. NEVER shell out to grep or rg through Bash — this tool applies the ignore list, skips binaries, and has a timeout budget that the shell versions do not\n"
    + "- Supports full regex syntax (e.g. \"log.*Error\", \"function\\s+\\w+\"). Pattern syntax is ripgrep style: escape literal braces (use \"interface\\{\\}\" to find \"interface{}\" in Go code)\n"
    + "- Runs on ripgrep when it is installed in the environment, otherwise git grep / grep, otherwise a built-in .NET regex engine. "
    + "For portable results stick to the common subset: \\d \\w \\s \\b, character classes, groups, alternation, lazy quantifiers\n"
    + "- Filter files with glob (e.g. \"*.js\", \"**/*.tsx\") or with type by language (e.g. \"js\", \"py\")\n"
    + "- output_mode: \"content\" returns matching lines, \"files_with_matches\" returns just paths (DEFAULT), \"count\" returns a match count per file\n"
    + "- Matching is WITHIN A SINGLE LINE by default. Pass multiline: true to match across lines (e.g. \"interface\\s+X[\\s\\S]*?field\")\n"
    + "- Binary files are skipped; files over 5MB may be skipped\n"
    + $"- Dependency and build directories (node_modules, .git, dist, out, build, target, coverage, ...) are always skipped. When ripgrep or git grep does the search, files ignored by .gitignore are skipped too. To search inside those, use Bash instead.",
    S.Build(S.Object(
        ["pattern"],
        ("pattern", S.String("The regular expression to search file contents for (ripgrep syntax)", minLength: 1)),
        ("path", S.String("File or directory to search (absolute path; it may be outside the workspace). Omit it to search the whole workspace")),
        ("glob", S.String("Filter files by glob pattern, e.g. \"*.js\" or \"**/*.{ts,tsx}\". Mutually exclusive with type")),
        ("type", S.String("Filter files by language type, e.g. \"js\", \"py\", \"rust\". Easier than glob and covers the common languages. Use glob when you need finer control")),
        ("output_mode", S.Enum("Output shape: \"content\" returns the matching lines (supports -A/-B/-C/-n/head_limit); \"files_with_matches\" returns just the file paths (default); \"count\" returns a match count per file", "content", "files_with_matches", "count")),
        ("-i", S.Boolean("Case-insensitive matching")),
        ("-n", S.Boolean("Include line numbers in the output. Only used when output_mode is \"content\"")),
        ("-A", S.Integer("Lines of context to show after each match. Only used when output_mode is \"content\"", minimum: 0, maximum: 50)),
        ("-B", S.Integer("Lines of context to show before each match. Only used when output_mode is \"content\"", minimum: 0, maximum: 50)),
        ("-C", S.Integer("Lines of context to show on each side of a match. Only used when output_mode is \"content\"", minimum: 0, maximum: 50)),
        ("multiline", S.Boolean("Let the pattern match across line boundaries (. then matches newlines too). Defaults to false — by default matching happens within a single line")),
        ("head_limit", S.Integer("Keep only the first N results (lines, files, or count rows, depending on output_mode). Omit for the built-in cap", minimum: 1, maximum: 1000)))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false),
    "Search file contents with a regular expression",
    ["Use Grep to search file contents; do not use grep or rg through Bash."])
{
    private sealed class FileHits(string rel)
    {
        public string Rel { get; } = rel;

        /// <summary>命中的行号,从 1 开始,升序。</summary>
        public List<int> Lines { get; set; } = [];

        /// <summary>该文件所有行(已按行长截断),content 模式取上下文要用。只有真的要行正文时才切;其它模式一律空。</summary>
        public string[] Text { get; set; } = [];
    }

    private sealed record Scan(List<FileHits> Hits, int Total, int Scanned, bool BudgetHit, bool TimedOut);

    protected override async ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var rt = ctx.Runtime;
        var fs = rt.Environment.Fs;
        var pattern = RequiredString(args, "pattern");
        var ignoreCase = args.Bool("-i") == true;
        var multiline = args.Bool("multiline") == true;
        var mode = args.Str("output_mode") ?? "files_with_matches";
        var target = await ToolPaths.ResolveAsync(rt, args.Str("path"), ct).ConfigureAwait(false);

        // 认不出的 type 要报错,不能当成「不过滤」—— 静默放宽范围是查不出来的错
        var type = args.Str("type");
        if (type is not null && !SearchLimits.TypeGlobs.ContainsKey(type))
        {
            throw Fail($"Unknown type \"{type}\". Available: {string.Join(", ", SearchLimits.TypeGlobs.Keys)}. Or use the glob parameter and write the filename pattern directly.");
        }

        FsEntryInfo info;
        try
        {
            info = await fs.StatAsync(target.Absolute, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            throw Fail($"Path does not exist: {target.Display}");
        }

        var walkBase = await WalkBase.OfAsync(rt, target, ct).ConfigureAwait(false);
        var nameGlob = args.Str("glob") ?? (type is null ? null : SearchLimits.TypeGlobs[type]);
        var nameFilter = nameGlob is null ? null : GlobMatcher.Compile(nameGlob, caseSensitive: !rt.CaseInsensitiveFileSystem);
        var needText = mode == "content";

        // 先交给原生程序:它们跑在独立进程里,回溯卡死的是它们自己,超时由环境整树杀掉
        var native = await NativeGrep.RunAsync(rt, new NativeGrepRequest(
            pattern, ignoreCase, multiline, target.Absolute, info.IsDirectory,
            nameGlob is null ? null : NativeGrep.NameHints(nameGlob),
            SearchLimits.MaxGrepMatches, SearchLimits.NativeTimeout), ct).ConfigureAwait(false);
        if (native is NativeGrepOutcome.Ok ok)
        {
            var (nativeHits, nativeTotal) = await CollectNativeHitsAsync(rt, ok, target, info.IsDirectory, walkBase, nameFilter, needText, ct).ConfigureAwait(false);
            var nativeNotes = new List<string>();
            if (nativeTotal >= SearchLimits.MaxGrepMatches)
            {
                nativeNotes.Add(MatchCapNote());
            }

            if (ok.TimedOut || ok.Truncated)
            {
                nativeNotes.Add($"{ok.Backend} {(ok.TimedOut ? $"was stopped after {SearchLimits.NativeTimeout.TotalMilliseconds:0}ms" : "hit its output limit")}, so the result is incomplete. Narrow it with path, glob or type and search again");
            }

            return Format(rt, args, mode, nativeHits, nativeNotes,
                nativeNotes.Count > 0 ? $"No matches found under {walkBase.Label}.\n[{string.Join("; ", nativeNotes)}]" : $"No content matching \"{pattern}\" under {walkBase.Label}.",
                ok.Backend);
        }

        // 托管路径
        Regex re;
        try
        {
            re = Redos.Compile(pattern, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | (multiline ? RegexOptions.Multiline | RegexOptions.Singleline : RegexOptions.None), SearchLimits.RegexTimeout);
        }
        catch (ArgumentException e)
        {
            // rg 也说不合法时报 rg 的原话 —— 描述里告诉模型的是 ripgrep 语法
            var rgError = (native as NativeGrepOutcome.Unavailable)?.RegexError;
            throw Fail(rgError is not null
                ? $"Invalid regular expression: {rgError}"
                : $"Invalid regular expression: {e.Message} (checked by the built-in .NET regex engine). When searching for a literal, escape . ( ) [ ] {{ }} * + ? with a backslash.");
        }

        // 预算从托管搜索开始时起算(和 2.x 一样):原生后端探测 / 超时耗掉的时间不算在里面,否则它们一慢,回退到这里就一个文件都扫不了
        var started = rt.Clock.GetTimestamp();
        var prefilter = GrepPrefilter.Create(pattern, ignoreCase);
        List<(string Rel, EnvPath Abs)> files;
        var walkTruncated = false;
        var walkTimedOut = false;
        if (!info.IsDirectory)
        {
            files = [(target.Display, target.Absolute)];
        }
        else
        {
            var walk = await Walker.WalkAsync(
                new WalkOptions { Fs = fs, Root = walkBase.Root, Start = walkBase.Start, Deadline = SearchLimits.SearchBudget },
                rt.Clock,
                ct).ConfigureAwait(false);
            walkTruncated = walk.Truncated;
            walkTimedOut = walk.TimedOut;
            files = walk.Entries
                .Where(e => !e.IsDirectory && (nameFilter is null || nameFilter.IsMatch(GlobMatcher.Normalize(e.Rel))))
                .Select(e => (walkBase.Display(e.Rel), e.Abs))
                .ToList();
        }

        var scan = await ScanFilesAsync(rt, files, started, needText, ctx, (file, token) => GrepFileAsync(rt, file.Abs, file.Rel, re, prefilter, multiline, needText, token), ct).ConfigureAwait(false);

        var notes = new List<string>();
        if (scan.Total >= SearchLimits.MaxGrepMatches)
        {
            notes.Add(MatchCapNote());
        }

        // 超时必须说出来:静默返回部分结果的话,模型会据此断言「这个字符串在仓库里不存在」—— 而那是个看起来很有说服力的错误答案
        if (scan.BudgetHit || walkTimedOut)
        {
            notes.Add($"The search exceeded its {SearchLimits.SearchBudget.TotalMilliseconds:0}ms budget and only covered {scan.Scanned}/{files.Count} files, so the result is incomplete. Narrow it with path, glob or type and search again");
        }

        if (scan.TimedOut)
        {
            notes.Add($"A regular expression evaluation exceeded {SearchLimits.RegexTimeout.TotalMilliseconds:0}ms and the search was stopped after {scan.Scanned}/{files.Count} files. The result is incomplete — narrow path, glob or type, or simplify the pattern");
        }

        if (walkTruncated)
        {
            notes.Add("traversal limit reached; some directories were not scanned");
        }

        return Format(rt, args, mode, scan.Hits, notes,
            notes.Count > 0
                ? $"No matches found in the {scan.Scanned} files scanned under {walkBase.Label}.\n[{string.Join("; ", notes)}]"
                : $"No content matching \"{pattern}\" in the {files.Count} files under {walkBase.Label}.",
            "managed");
    }

    private static string MatchCapNote() => $"hit the {SearchLimits.MaxGrepMatches} match cap; narrow the pattern, glob, or path and search again";

    /// <summary>
    /// 搜一个文件。<c>null</c> = 这个文件跳过(二进制 / 太大 / 读不了),不是「没命中」。
    /// 先嗅探再解码:探测在字节上做完才解码成字符串,二进制文件永远不会被解成一堆 U+FFFD。
    /// </summary>
    private static async ValueTask<FileHits?> GrepFileAsync(ToolRuntime rt, EnvPath abs, string rel, Regex re, Func<string, bool>? prefilter, bool multiline, bool needText, CancellationToken ct)
    {
        var fs = rt.Environment.Fs;
        byte[] bytes;
        try
        {
            var size = (await fs.StatAsync(abs, ct).ConfigureAwait(false)).Size;
            if (size == 0 || size > SearchLimits.MaxGrepFileBytes)
            {
                return null;
            }

            if (size <= SearchLimits.SingleReadMaxBytes)
            {
                bytes = await fs.ReadBytesAsync(abs, SearchLimits.MaxGrepFileBytes, ct).ConfigureAwait(false);
                if (Text.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, FileLimits.SniffBytes))))
                {
                    return null;
                }
            }
            else
            {
                if (Text.LooksBinary(await fs.ReadHeadAsync(abs, FileLimits.SniffBytes, ct).ConfigureAwait(false)))
                {
                    return null;
                }

                bytes = await fs.ReadBytesAsync(abs, SearchLimits.MaxGrepFileBytes, ct).ConfigureAwait(false);
            }
        }
        catch (EnvironmentException e) when (Skippable(e))
        {
            return null;
        }

        var raw = Encoding.UTF8.GetString(TextNewlines.StripUtf8Bom(bytes));

        // 整文件字面量预筛:只许误报不许漏报
        if (prefilter is not null && !prefilter(raw))
        {
            return new FileHits(rel);
        }

        var hits = new FileHits(rel);
        if (multiline)
        {
            var text = Text.ToLf(raw.Length > SearchLimits.MaxMultilineChars ? Text.Head(raw, SearchLimits.MaxMultilineChars) : raw);
            var lineNo = 1;
            var scanned = 0;
            foreach (Match m in re.Matches(text))
            {
                // 命中行号按「起点之前的换行数」计
                for (; scanned < m.Index; scanned++)
                {
                    if (text[scanned] == '\n')
                    {
                        lineNo++;
                    }
                }

                if (hits.Lines.Count == 0 || hits.Lines[^1] != lineNo)
                {
                    hits.Lines.Add(lineNo);
                }

                if (hits.Lines.Count >= SearchLimits.MaxGrepMatches)
                {
                    break;
                }
            }

            if (hits.Lines.Count > 0 && needText)
            {
                hits.Text = SplitLines(raw);
            }

            return hits;
        }

        var lines = SplitLines(raw);
        for (var i = 0; i < lines.Length; i++)
        {
            if ((i & 0x3FF) == 0x3FF)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (re.IsMatch(lines[i]))
            {
                hits.Lines.Add(i + 1);
                if (hits.Lines.Count >= SearchLimits.MaxGrepMatches)
                {
                    break;
                }
            }
        }

        if (hits.Lines.Count > 0 && needText)
        {
            hits.Text = lines;
        }

        return hits;
    }

    /// <summary>切行 + 去掉行尾的 <c>\r</c> + 截断到 <see cref="SearchLimits.MaxLineChars"/>。截断在匹配之前,行号因此和输出对得上。文件以换行结尾时不多出一个空行。</summary>
    private static string[] SplitLines(string raw)
    {
        var lines = ReadTool.SplitLines(raw);
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length > SearchLimits.MaxLineChars)
            {
                lines[i] = Text.Head(lines[i], SearchLimits.MaxLineChars);
            }
        }

        return [.. lines];
    }

    private static bool Skippable(EnvironmentException e) =>
        e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory or EnvironmentErrorCode.PermissionDenied or EnvironmentErrorCode.IsDirectory or EnvironmentErrorCode.TooLarge;

    /// <summary>
    /// 以 <see cref="SearchLimits.GrepConcurrency"/> 路并发搜一批文件,结果按<b>原顺序</b>汇总。并发是为了速度(SSH 上每个文件好几个网络往返),
    /// 但输出必须和串行时逐字节一致:结果顺序 = 遍历顺序(靠近根的在前),撞上命中上限时截在哪个文件也要和串行时一样。
    /// 做法:文件按下标依次领取,所以开搜过的永远是一个前缀;另外维护「已连续完成的前缀」上的累计命中数,它够了就不再领新文件;最后按下标顺序拼结果、在上限处截断。
    /// 某个文件抛错(连接断线)时让所有 worker 停手再抛,而不是「第一个错就返回」—— 后者会让其余 worker 在后台继续对一条已经断掉的连接发请求。
    /// </summary>
    private static async Task<Scan> ScanFilesAsync(
        ToolRuntime rt,
        IReadOnlyList<(string Rel, EnvPath Abs)> files,
        long started,
        bool needText,
        ExecutionToolContext ctx,
        Func<(string Rel, EnvPath Abs), CancellationToken, ValueTask<FileHits?>> searchOne,
        CancellationToken ct)
    {
        var results = new FileHits?[files.Count];
        var done = new bool[files.Count];
        var next = 0;
        var completed = 0;
        var prefixEnd = 0;
        var prefixMatches = 0;
        var budgetHit = false;
        var timedOut = false;
        Exception? failure = null;
        var gate = new object();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task Worker()
        {
            while (true)
            {
                int index;
                lock (gate)
                {
                    if (next >= files.Count || failure is not null || timedOut || prefixMatches >= SearchLimits.MaxGrepMatches || ct.IsCancellationRequested)
                    {
                        return;
                    }

                    if (rt.Clock.GetElapsedTime(started) > SearchLimits.SearchBudget)
                    {
                        budgetHit = true;
                        return;
                    }

                    index = next++;
                }

                FileHits? hits;
                try
                {
                    hits = await searchOne(files[index], stop.Token).ConfigureAwait(false);
                }
                catch (RegexMatchTimeoutException)
                {
                    // 匹配超时是「结果不完整」,不是工具失败:已有结果照常返回,再在说明里写清「没扫完」
                    lock (gate)
                    {
                        timedOut = true;
                    }

                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    lock (gate)
                    {
                        failure ??= e;
                    }

                    await stop.CancelAsync().ConfigureAwait(false);
                    return;
                }

                lock (gate)
                {
                    results[index] = hits is { Lines.Count: > 0 } ? hits : null;
                    done[index] = true;
                    if (++completed % 32 == 0)
                    {
                        ctx.Progress.Report(new ToolProgress($"Searched {completed} files"));
                    }

                    while (prefixEnd < files.Count && done[prefixEnd])
                    {
                        prefixMatches += results[prefixEnd]?.Lines.Count ?? 0;
                        prefixEnd++;
                    }
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Min(SearchLimits.GrepConcurrency, files.Count)).Select(_ => Task.Run(Worker, CancellationToken.None))).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        var hitsList = new List<FileHits>();
        var total = 0;
        for (var i = 0; i < next && total < SearchLimits.MaxGrepMatches; i++)
        {
            if (results[i] is { } fh)
            {
                hitsList.Add(fh);
                total += fh.Lines.Count;
            }
        }

        return new Scan(hitsList, total, next, budgetHit, timedOut);
    }

    /// <summary>
    /// 把原生后端的「绝对路径 → 行号」变成和托管路径同形的 <see cref="FileHits"/>。忽略表与文件名过滤在这里<b>再做一遍</b>:原生侧的下推只是为了少搜,正确性以这里为准。
    /// 顺序与 <see cref="Walker"/> 的 BFS 同向:浅的在前,同层按路径逐段比较 —— 撞上命中上限时留下的是靠近根的文件。
    /// </summary>
    private static async Task<(List<FileHits> Hits, int Total)> CollectNativeHitsAsync(
        ToolRuntime rt, NativeGrepOutcome.Ok native, ToolPath target, bool isDirectory, WalkBase walkBase, Regex? nameFilter, bool needText, CancellationToken ct)
    {
        var rows = new List<(string[] Key, string Rel, EnvPath Abs, List<int> Lines)>();
        foreach (var (abs, lines) in native.Hits)
        {
            if (!isDirectory)
            {
                // 单文件:和托管路径一样,不过滤、直接用它的展示路径
                rows.Add(([], target.Display, abs, lines));
                continue;
            }

            var parts = GlobMatcher.Normalize(abs.RelativeTo(target.Absolute)).Split('/').ToList();
            var file = parts[^1];
            parts.RemoveAt(parts.Count - 1);
            if (parts.Any(d => IgnoreList.ShouldSkip(d, true)) || IgnoreList.ShouldSkip(file, false))
            {
                continue;
            }

            var fromRoot = abs.RelativeTo(walkBase.Root);
            if (nameFilter is not null && !nameFilter.IsMatch(GlobMatcher.Normalize(fromRoot)))
            {
                continue;
            }

            rows.Add((GlobMatcher.Normalize(fromRoot).Split('/'), walkBase.Display(GlobMatcher.Normalize(fromRoot)), abs, lines));
        }

        rows.Sort((a, b) =>
        {
            if (a.Key.Length != b.Key.Length)
            {
                return a.Key.Length.CompareTo(b.Key.Length);
            }

            for (var i = 0; i < a.Key.Length; i++)
            {
                var c = string.CompareOrdinal(a.Key[i], b.Key[i]);
                if (c != 0)
                {
                    return c;
                }
            }

            return 0;
        });

        var hits = new List<FileHits>();
        var total = 0;
        foreach (var row in rows)
        {
            if (total >= SearchLimits.MaxGrepMatches)
            {
                break;
            }

            var lines = row.Lines.Take(SearchLimits.MaxGrepMatches).ToList();
            hits.Add(new FileHits(row.Rel) { Lines = lines });
            total += lines.Count;
        }

        if (needText)
        {
            // 行正文自己读(原生输出里只要了行号),切行与截断和托管路径一致
            for (var i = 0; i < hits.Count; i += SearchLimits.GrepConcurrency)
            {
                var batch = Enumerable.Range(i, Math.Min(SearchLimits.GrepConcurrency, hits.Count - i)).ToList();
                await Task.WhenAll(batch.Select(async j =>
                {
                    var abs = rows.First(r => r.Rel == hits[j].Rel).Abs;
                    try
                    {
                        var bytes = await rt.Environment.Fs.ReadBytesAsync(abs, SearchLimits.MaxGrepFileBytes, ct).ConfigureAwait(false);
                        hits[j].Text = SplitLines(Encoding.UTF8.GetString(TextNewlines.StripUtf8Bom(bytes)));
                    }
                    catch (EnvironmentException e) when (Skippable(e))
                    {
                    }
                })).ConfigureAwait(false);
            }
        }

        return (hits, total);
    }

    /// <summary>三种 <c>output_mode</c> 的渲染,托管与原生两条路共用 —— 换后端不能换输出格式。<paramref name="empty"/> = 没有命中时的整句。</summary>
    private static ToolResult Format(ToolRuntime rt, JsonElement args, string mode, List<FileHits> hits, List<string> notes, string empty, string backend)
    {
        var limit = OptionalInt(args, "head_limit");
        ToolResult Result(string text, int shownCount) => ToolResult.Text(text, Details(rt, "grep", w =>
        {
            w.WriteString("pattern", args.Str("pattern") ?? "");
            w.WriteString("backend", backend);
            w.WriteString("mode", mode);
            w.WriteNumber("files", hits.Count);
            w.WriteNumber("matches", hits.Sum(h => h.Lines.Count));
            w.WriteNumber("shown", shownCount);
            w.WriteBoolean("incomplete", notes.Count > 0);
        }));

        if (hits.Count == 0)
        {
            return Result(empty, 0);
        }

        var tail = notes.Count > 0 ? $"\n\n[{string.Join("; ", notes)}]" : "";
        if (mode == "files_with_matches")
        {
            var paths = hits.Select(h => h.Rel).ToList();
            var shown = limit is null ? paths : paths.Take(limit.Value).ToList();
            return Result(string.Join('\n', shown) + (shown.Count < paths.Count ? $"\n[showing the first {shown.Count} files]" : "") + tail, shown.Count);
        }

        if (mode == "count")
        {
            var rows = hits.Select(h => $"{h.Rel}:{h.Lines.Count}").ToList();
            var shown = limit is null ? rows : rows.Take(limit.Value).ToList();
            return Result(string.Join('\n', shown) + (shown.Count < rows.Count ? $"\n[showing the first {shown.Count} files]" : "") + tail, shown.Count);
        }

        var context = OptionalInt(args, "-C");
        var before = context ?? OptionalInt(args, "-B") ?? 0;
        var after = context ?? OptionalInt(args, "-A") ?? 0;
        var rendered = RenderContent(hits, before, after, args.Bool("-n") == true);
        var lines = limit is null ? rendered : rendered.Take(limit.Value).ToList();
        return Result(string.Join('\n', lines) + (lines.Count < rendered.Count ? $"\n[showing the first {lines.Count} lines]" : "") + tail, lines.Count);
    }

    /// <summary>content 模式的渲染。格式对齐 ripgrep:命中行用 <c>:</c>,上下文行用 <c>-</c>,不相连的两段之间插一条 <c>--</c>。</summary>
    private static List<string> RenderContent(List<FileHits> hits, int before, int after, bool showLineNumbers)
    {
        var output = new List<string>();
        foreach (var f in hits)
        {
            var wanted = new SortedSet<int>();
            foreach (var ln in f.Lines)
            {
                for (var i = ln - before; i <= ln + after; i++)
                {
                    if (i >= 1 && i <= f.Text.Length)
                    {
                        wanted.Add(i);
                    }
                }
            }

            var isHit = new HashSet<int>(f.Lines);
            var previous = 0;
            foreach (var ln in wanted)
            {
                if (previous != 0 && ln > previous + 1)
                {
                    output.Add("--");
                }

                var sep = isHit.Contains(ln) ? ':' : '-';
                var body = f.Text[ln - 1];
                output.Add(showLineNumbers ? $"{f.Rel}{sep}{ln}{sep}{body}" : $"{f.Rel}{sep}{body}");
                previous = ln;
            }
        }

        return output;
    }
}
