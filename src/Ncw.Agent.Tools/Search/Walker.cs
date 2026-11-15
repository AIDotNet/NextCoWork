using Ncw.Agent.Environments;

namespace Ncw.Agent.Tools;

/// <param name="Rel">相对这一趟的 root、始终用 <c>/</c> 分隔。</param>
internal sealed record WalkEntry(string Rel, EnvPath Abs, bool IsDirectory);

internal sealed record WalkOptions
{
    public required IEnvironmentFs Fs { get; init; }

    /// <summary>这一趟的基点(项目根,或模型指到根外时的那个目录)。必须是已经 realpath 过的。</summary>
    public required EnvPath Root { get; init; }

    /// <summary>从基点下的哪个子目录开始(<c>/</c> 分隔)。空 = 基点本身。</summary>
    public string Start { get; init; } = "";

    public int MaxEntries { get; init; } = 20_000;

    /// <summary>相对起点的最大深度。够深,又挡得住软链没查到的怪环。</summary>
    public int MaxDepth { get; init; } = 32;

    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(5);

    public Func<string, bool, bool> Skip { get; init; } = IgnoreList.ShouldSkip;
}

internal sealed record WalkResult(IReadOnlyList<WalkEntry> Entries, bool Truncated, bool TimedOut);

/// <summary>
/// 目录遍历 —— Glob / Grep 共用的那一趟走(移植自 2.x <c>walk.ts</c>)。三道闸门,每一道对应一种真实的挂死方式:
/// <list type="number">
/// <item><b>软链成环</b>:<c>a/link -&gt; ..</c> 在词法上完全正常,遍历会无限深下去。按目录的 realpath 去重,进过的目录不再进。</item>
/// <item><b>软链出界</b>:<c>ws/link -&gt; /</c> 会让 grep 把用户的整个磁盘搜一遍。要进的符号链接目录 realpath 之后出了这一趟的 root 就跳过(不是报错)。
/// 这不是权限边界(模型可以直接把 root 指到那个目录),它拦的是「你只要了一个目录,却顺着一条链把半个磁盘搜了」。</item>
/// <item><b>量与时间</b>:<c>MaxEntries / MaxDepth / Deadline</c> 都必须有,超了要<b>照实说</b>(<c>Truncated / TimedOut</c>):静默的部分结果会让模型断言「仓库里没有」。</item>
/// </list>
/// 广度优先:撞上 MaxEntries 时,BFS 留下的是靠近根的文件(通常正是模型想要的),DFS 留下的是第一棵子树钻到底的那一串。
/// 目录列表并发取(上限 <see cref="Concurrency"/>,SSH 上每次 List 都是网络往返)、按顺序用,所以结果顺序、截断落点、软链去重谁先谁后都和串行时逐项一致。
/// </summary>
internal static class Walker
{
    public const int Concurrency = 8;

    /// <summary>这些错误码 = 这一个目录没法进(没权限 / 刚被删掉 / 断链),跳过它,不让整次遍历失败。连接中断等其它错误原样抛出,不能当成「目录是空的」。</summary>
    private static bool Skippable(EnvironmentException e) =>
        e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory or EnvironmentErrorCode.PermissionDenied;

    private sealed record Dir(string Rel, EnvPath Abs, EnvPath Real, int Depth);

    private sealed record Listing(IReadOnlyList<FsDirEntry>? Items, EnvironmentException? Error);

    public static async Task<WalkResult> WalkAsync(WalkOptions options, TimeProvider clock, CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        bool Expired() => clock.GetElapsedTime(started) > options.Deadline;

        var entries = new List<WalkEntry>();
        var truncated = false;
        var timedOut = false;
        var fs = options.Fs;

        var startAbs = options.Start.Length == 0 ? options.Root : options.Root.Join(options.Start.Split('/'));
        var realRoot = await fs.RealPathAsync(options.Root, ct).ConfigureAwait(false);
        var startReal = await fs.RealPathAsync(startAbs, ct).ConfigureAwait(false);

        var visited = new HashSet<string>(StringComparer.Ordinal) { ReadTracker.Key(startReal) };
        var frontier = new List<Dir> { new(options.Start, startAbs, startReal, 0) };
        var gate = new SemaphoreSlim(Concurrency);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task<Listing> ListAsync(Dir dir)
        {
            var acquired = false;
            try
            {
                await gate.WaitAsync(stop.Token).ConfigureAwait(false);
                acquired = true;
                return new Listing(await fs.ListAsync(dir.Abs, stop.Token).ConfigureAwait(false), null);
            }
            catch (EnvironmentException e)
            {
                return new Listing(null, e);
            }
            catch (OperationCanceledException)
            {
                return new Listing(null, null);
            }
            finally
            {
                if (acquired)
                {
                    gate.Release();
                }
            }
        }

        try
        {
            while (frontier.Count > 0)
            {
                var level = frontier;
                var next = new List<Dir>();
                var listings = level.Select(ListAsync).ToArray();
                for (var di = 0; di < level.Count; di++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Expired())
                    {
                        return new WalkResult(entries, truncated, TimedOut: true);
                    }

                    var dir = level[di];
                    var listing = await listings[di].ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (listing.Items is null)
                    {
                        if (listing.Error is { } error && !Skippable(error))
                        {
                            throw error;
                        }

                        continue;
                    }

                    var subdirs = new List<(string Rel, EnvPath Abs, FsDirEntry Entry)>();
                    foreach (var item in listing.Items)
                    {
                        // 符号链接要 stat 才知道指向文件还是目录;断链跳过
                        var kind = item.Info.Kind;
                        if (kind == FsEntryKind.Symlink)
                        {
                            try
                            {
                                kind = (await fs.StatAsync(dir.Abs.Join(item.Name), ct).ConfigureAwait(false)).Kind;
                            }
                            catch (EnvironmentException e) when (Skippable(e))
                            {
                                continue;
                            }
                        }

                        var isDirectory = kind == FsEntryKind.Directory;
                        if (options.Skip(item.Name, isDirectory))
                        {
                            continue;
                        }

                        if (kind is not (FsEntryKind.Directory or FsEntryKind.File))
                        {
                            continue; // socket、fifo 之类
                        }

                        if (entries.Count >= options.MaxEntries)
                        {
                            return new WalkResult(entries, Truncated: true, timedOut);
                        }

                        var rel = dir.Rel.Length == 0 ? item.Name : $"{dir.Rel}/{item.Name}";
                        var abs = dir.Abs.Join(item.Name);
                        entries.Add(new WalkEntry(rel, abs, isDirectory));
                        if (!isDirectory)
                        {
                            continue;
                        }

                        if (dir.Depth + 1 >= options.MaxDepth)
                        {
                            truncated = true;
                            continue;
                        }

                        subdirs.Add((rel, abs, item));
                    }

                    // 要往里走的目录:真实路径。普通目录 = 父目录的真实路径 + 名字(不需要 IO);只有符号链接才付 realpath 的钱
                    foreach (var (rel, abs, item) in subdirs)
                    {
                        EnvPath real;
                        if (item.Info.Kind == FsEntryKind.Symlink)
                        {
                            try
                            {
                                real = await fs.RealPathAsync(abs, ct).ConfigureAwait(false);
                            }
                            catch (EnvironmentException e) when (Skippable(e))
                            {
                                continue;
                            }

                            // 软链出界:跳过,但不中止遍历
                            if (!real.IsWithin(realRoot))
                            {
                                continue;
                            }
                        }
                        else
                        {
                            real = dir.Real.Join(item.Name);
                        }

                        if (!visited.Add(ReadTracker.Key(real)))
                        {
                            continue;
                        }

                        next.Add(new Dir(rel, abs, real, dir.Depth + 1));
                    }
                }

                frontier = next;
            }

            return new WalkResult(entries, truncated, timedOut);
        }
        finally
        {
            // 还在途的预取:取消它们并观察异常(ListAsync 已经把异常装进返回值,不会有未观察的 Task 异常)
            await stop.CancelAsync().ConfigureAwait(false);
        }
    }
}
