using Ncw.Agent.Environments;

namespace Ncw.Agent.Workspace.Files;

internal sealed record WalkedFile(string Relative, string Name, EnvPath Absolute, long Size);

internal sealed record WalkOutcome(List<WalkedFile> Files, bool Truncated);

/// <summary>
/// 遍历(文件名建议和内容搜索共用):广度优先,经环境的 <see cref="IEnvironmentFs"/> 列目录。三道闸门,每一道对应一种真实的挂死方式:
/// <b>符号链接不进</b>(成环、出界都从源头上不存在:链接目录和链接文件都跳过);<b>量与深度</b>(<c>MaxEntries</c> / <c>MaxDepth</c>);<b>墙钟预算</b>(超了就停)。
/// 任何一道触发都照实标 <c>Truncated</c>:静默的部分结果会让调用方断言「仓库里没有」。忽略表命中的目录整个跳过(不读 <c>.gitignore</c>)。
/// </summary>
internal static class Walk
{
    public static async Task<WalkOutcome> RunAsync(
        IEnvironmentFs fs, EnvPath start, string startRelative, int maxEntries, int maxDepth, TimeSpan deadline, TimeProvider clock, CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        var files = new List<WalkedFile>();
        var truncated = false;
        var queue = new Queue<(EnvPath Path, string Relative, int Depth)>();
        queue.Enqueue((start, startRelative, 0));
        while (queue.TryDequeue(out var dir))
        {
            ct.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(started) > deadline)
            {
                truncated = true;
                break;
            }

            IReadOnlyList<FsDirEntry> items;
            try
            {
                items = await fs.ListAsync(dir.Path, ct).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory or EnvironmentErrorCode.PermissionDenied)
            {
                // 这一个目录没法进(刚被删 / 没权限):跳过,不让整次遍历失败。连接中断等其它错误原样抛出,不能当成「目录是空的」。
                continue;
            }

            foreach (var item in items)
            {
                if (item.Info.IsSymlink || IgnoreRules.ShouldSkip(item.Name, item.Info.IsDirectory))
                {
                    continue;
                }

                var relative = ProjectPaths.Join(dir.Relative, item.Name);
                if (item.Info.IsDirectory)
                {
                    if (dir.Depth + 1 > maxDepth)
                    {
                        truncated = true;
                    }
                    else
                    {
                        queue.Enqueue((dir.Path.Join(item.Name), relative, dir.Depth + 1));
                    }
                }
                else if (item.Info.IsFile)
                {
                    if (files.Count >= maxEntries)
                    {
                        return new WalkOutcome(files, true);
                    }

                    files.Add(new WalkedFile(relative, item.Name, dir.Path.Join(item.Name), item.Info.Size));
                }
            }
        }

        return new WalkOutcome(files, truncated);
    }
}
