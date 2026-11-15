using Ncw.Agent.Environments;
using Ncw.Core.Domain;

namespace Ncw.Agent.Workspace.Files;

public sealed partial class FileService
{
    /// <summary>
    /// 列一层目录(不递归)。目录在前、各自再按 <paramref name="sort"/> 排,排完分页。
    /// 逐项 lstat 与跟随链接的 stat 分开:指向不存在目标的符号链接按「文件、无大小」列出,不能让整次列目录失败。
    /// </summary>
    public ValueTask<DirListing> ListAsync(FileProject project, string path, FileSortBy sort, bool hideIgnored, int offset, int limit, CancellationToken ct = default) =>
        Guard(async () =>
        {
            var dir = await ProjectPaths.ResolveAsync(project, path, allowRoot: true, LinkPolicy.Follow, ct).ConfigureAwait(false);
            var fs = project.Environment.Fs;
            IReadOnlyList<FsDirEntry> raw;
            try
            {
                raw = await fs.ListAsync(dir, ct).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.NotDirectory)
            {
                throw new FilesException(FilesErrorCode.NotFile, "not a directory", e);
            }

            var entries = new List<FileEntry>(raw.Count);
            foreach (var item in raw)
            {
                var isDirectory = item.Info.IsDirectory;
                var isLink = item.Info.IsSymlink;
                long? size = item.Info.IsFile ? item.Info.Size : null;
                long? mtime = item.Info.ModifiedAt.ToUnixTimeMilliseconds();
                if (isLink)
                {
                    // 软链要看它指向什么:指向目录的软链在树里就该是个能展开的目录
                    try
                    {
                        var target = await fs.StatAsync(dir.Join(item.Name), ct).ConfigureAwait(false);
                        isDirectory = target.IsDirectory;
                        size = target.IsFile ? target.Size : null;
                        mtime = target.ModifiedAt.ToUnixTimeMilliseconds();
                    }
                    catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory or EnvironmentErrorCode.PermissionDenied)
                    {
                        // 断链 / 刚被删掉 / 没权限:仍然列出来,只是没有大小
                        size = null;
                    }
                }

                var ignored = IgnoreRules.ShouldSkip(item.Name, isDirectory);
                if (hideIgnored && ignored)
                {
                    continue;
                }

                entries.Add(new FileEntry(item.Name, ProjectPaths.Join(path, item.Name), isDirectory, isLink, item.Name.StartsWith('.'), ignored, size, mtime));
            }

            var sorted = FileEntrySort.Sort(entries, sort, static e => e.Name, static e => e.IsDirectory, static e => e.Mtime, static e => e.Size);
            var start = Math.Clamp(offset, 0, sorted.Count);
            var take = limit <= 0 ? FileLimits.ListDefault : Math.Min(limit, FileLimits.ListMax);
            var page = sorted.Skip(start).Take(take).ToArray();
            return new DirListing(path, page, sorted.Count, start + page.Length < sorted.Count);
        });
}
