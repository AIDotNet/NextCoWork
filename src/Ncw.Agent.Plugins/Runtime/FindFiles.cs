using Ncw.Agent.Environments;

namespace Ncw.Agent.Plugins;

/// <summary>
/// <c>workspace.findFiles</c>:前缀 glob(<c>src/**</c>、<c>docs/*.md</c>、<c>**/*.ts</c>)。只支持 <c>*</c>(不跨目录)、<c>**</c>(跨目录)和 <c>?</c>,不引 glob 引擎:
/// 这一层挨着安全边界,通用 glob 的语义(<c>{a,b}</c>、否定)每一条都是一次「我以为它匹配不到」的机会。遍历有深度、条目数上限,跳过 <c>.git</c> / <c>node_modules</c>,不跟随软链。
/// </summary>
internal static class FindFiles
{
    private const int MaxDepth = 12;
    private const int MaxVisited = 20_000;

    public static async Task<IReadOnlyList<string>> RunAsync(IExecutionEnvironment env, EnvPath root, string glob, int limit, CancellationToken ct)
    {
        // 起点 = 第一个通配符之前的目录部分
        var wildcard = glob.IndexOfAny(['*', '?']);
        var literal = wildcard < 0 ? glob : glob[..wildcard];
        var cut = literal.LastIndexOf('/');
        var startRelative = cut < 0 ? "" : literal[..cut];
        if (startRelative.Split('/').Any(s => s == ".."))
        {
            return [];
        }

        var start = startRelative.Length == 0 ? root : root.Join(startRelative.Split('/'));
        var matches = new List<string>();
        var visited = 0;
        var queue = new Queue<(EnvPath Dir, string Rel, int Depth)>();
        queue.Enqueue((start, startRelative, 0));
        while (queue.Count > 0 && matches.Count < limit)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, rel, depth) = queue.Dequeue();
            IReadOnlyList<FsDirEntry> entries;
            try
            {
                entries = await env.Fs.ListAsync(dir, ct).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory or EnvironmentErrorCode.PermissionDenied)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (++visited > MaxVisited)
                {
                    return matches;
                }

                var path = rel.Length == 0 ? entry.Name : $"{rel}/{entry.Name}";
                if (entry.Info.IsDirectory)
                {
                    if (depth < MaxDepth && entry.Name is not (".git" or "node_modules"))
                    {
                        queue.Enqueue((dir.Join(entry.Name), path, depth + 1));
                    }
                }
                else if (entry.Info.IsFile && Matches(glob, path))
                {
                    matches.Add(path);
                    if (matches.Count >= limit)
                    {
                        break;
                    }
                }
            }
        }

        matches.Sort(StringComparer.Ordinal);
        return matches;
    }

    /// <summary>glob 匹配(<c>**</c> 跨目录,<c>*</c> 不跨 <c>/</c>,<c>?</c> 单个非 <c>/</c> 字符;末尾的 <c>**</c> / <c>/**</c> 匹配该目录下的一切)。</summary>
    public static bool Matches(string glob, string path) => MatchFrom(glob, 0, path, 0);

    private static bool MatchFrom(string g, int gi, string p, int pi)
    {
        while (gi < g.Length)
        {
            var c = g[gi];
            if (c == '*')
            {
                var doubleStar = gi + 1 < g.Length && g[gi + 1] == '*';
                if (doubleStar)
                {
                    var next = gi + 2;
                    if (next < g.Length && g[next] == '/')
                    {
                        next++;
                    }

                    for (var k = pi; k <= p.Length; k++)
                    {
                        if (MatchFrom(g, next, p, k))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                for (var k = pi; k <= p.Length; k++)
                {
                    if (MatchFrom(g, gi + 1, p, k))
                    {
                        return true;
                    }

                    if (k < p.Length && p[k] == '/')
                    {
                        break;
                    }
                }

                return false;
            }

            if (pi >= p.Length)
            {
                return false;
            }

            if (c == '?' ? p[pi] == '/' : c != p[pi])
            {
                return false;
            }

            gi++;
            pi++;
        }

        return pi == p.Length;
    }
}
