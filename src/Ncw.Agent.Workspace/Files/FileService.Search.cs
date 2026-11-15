using System.Text.RegularExpressions;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Workspace.Files;

public sealed partial class FileService
{
    private const int MaxLineText = 500;

    /// <summary>
    /// 内容搜索。<see cref="SearchRequest.Pattern"/> 默认是字面串;<see cref="SearchRequest.Regex"/> 为真时是正则,用<b>不回溯</b>的引擎(线性时间,没有灾难性回溯),
    /// 所以反向引用 / 环视这类需要回溯的语法拒绝(<see cref="FilesErrorCode.InvalidPattern"/>),而不是悄悄变成别的意思。逐行匹配,每行报第一处。
    /// 跳过忽略表里的目录、符号链接、二进制文件(前 8 KiB 里有 NUL)和超过 <see cref="FileServiceOptions.SearchFileMax"/> 的文件。
    /// 命中数、遍历量或时间任何一项触顶都标 <see cref="SearchResult.Truncated"/>:静默的部分结果会让调用方断言「仓库里没有」。
    /// </summary>
    public ValueTask<SearchResult> SearchAsync(FileProject project, SearchRequest request, CancellationToken ct = default) =>
        Guard(async () =>
        {
            if (string.IsNullOrEmpty(request.Pattern))
            {
                throw new FilesException(FilesErrorCode.InvalidPattern, "empty pattern");
            }

            var matcher = Matcher.Create(request);
            var glob = request.Glob is { Length: > 0 } g ? Glob.Compile(g, caseSensitive: true) : null;
            var globOnName = request.Glob is { } pattern && !pattern.Contains('/');
            var limit = request.Limit <= 0 ? FileLimits.SearchDefault : Math.Min(request.Limit, FileLimits.SearchMax);

            var fs = project.Environment.Fs;
            _ = await ProjectPaths.RealRootAsync(project, ct).ConfigureAwait(false); // 根读不到先报 ProjectUnavailable
            var start = await ProjectPaths.ResolveAsync(project, request.Path, allowRoot: true, LinkPolicy.Follow, ct).ConfigureAwait(false);
            var info = await fs.StatAsync(start, ct).ConfigureAwait(false);

            List<WalkedFile> files;
            var truncated = false;
            if (info.IsFile)
            {
                files = [new WalkedFile(request.Path, start.FileName, start, info.Size)];
            }
            else
            {
                var walked = await Walk.RunAsync(fs, start, request.Path, _options.MaxWalkEntries, _options.MaxWalkDepth, _options.WalkDeadline, _options.Time, ct).ConfigureAwait(false);
                files = walked.Files;
                truncated = walked.Truncated;
            }

            var started = _options.Time.GetTimestamp();
            var matches = new List<FileMatch>();
            var searched = 0;
            foreach (var file in files)
            {
                if (glob is not null && !glob.IsMatch(globOnName ? file.Name : file.Relative))
                {
                    continue;
                }

                if (file.Size > _options.SearchFileMax)
                {
                    continue;
                }

                if (_options.Time.GetElapsedTime(started) > _options.SearchDeadline)
                {
                    truncated = true;
                    break;
                }

                byte[] bytes;
                try
                {
                    bytes = await fs.ReadBytesAsync(file.Absolute, _options.SearchFileMax, ct).ConfigureAwait(false);
                }
                catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.PermissionDenied or EnvironmentErrorCode.TooLarge or EnvironmentErrorCode.IsDirectory)
                {
                    continue; // 刚被删 / 没权限 / 读的时候长大了:跳过这一个
                }

                searched++;
                if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0)
                {
                    continue; // 二进制
                }

                var text = System.Text.Encoding.UTF8.GetString(bytes);
                var lineNumber = 0;
                foreach (var lineRaw in text.Split('\n'))
                {
                    lineNumber++;
                    var line = lineRaw.EndsWith('\r') ? lineRaw[..^1] : lineRaw;
                    if (matcher.Find(line) is not { } column)
                    {
                        continue;
                    }

                    if (matches.Count >= limit)
                    {
                        return new SearchResult(matches, searched, true);
                    }

                    matches.Add(new FileMatch(file.Relative, lineNumber, column + 1, line.Length > MaxLineText ? line[..MaxLineText] : line));
                }
            }

            return new SearchResult(matches, searched, truncated);
        });

    private abstract class Matcher
    {
        /// <summary>这一行第一处匹配的 0 起列;没有返回 null。</summary>
        public abstract int? Find(string line);

        public static Matcher Create(SearchRequest request)
        {
            if (!request.Regex)
            {
                return new Literal(request.Pattern, request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            }

            try
            {
                var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | (request.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                return new Pattern(new Regex(request.Pattern, options, TimeSpan.FromSeconds(2)));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                // RegexParseException 派生自 ArgumentException;NonBacktracking 不支持的构造抛 NotSupportedException
                throw new FilesException(FilesErrorCode.InvalidPattern, "unsupported or invalid regular expression", e);
            }
        }
    }

    private sealed class Literal(string needle, StringComparison comparison) : Matcher
    {
        public override int? Find(string line) => line.IndexOf(needle, comparison) is var i and >= 0 ? i : null;
    }

    private sealed class Pattern(Regex regex) : Matcher
    {
        public override int? Find(string line)
        {
            try
            {
                return regex.Match(line) is { Success: true } m ? m.Index : null;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }
    }
}
