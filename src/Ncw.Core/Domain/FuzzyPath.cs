namespace Ncw.Core.Domain;

/// <summary>
/// 路径模糊匹配 —— 输入框 <c>@</c> 文件选择器背后的那个排序(移植自 2.x 的 <c>fuzzy-path.ts</c>)。
/// <para>
/// <b>为什么排序是纯函数住在 Core</b>:它是这个功能里唯一有对错可言的部分 —— 敲 <c>chatv</c> 该先看到 <c>views/chat/ChatView.tsx</c> 而不是 <c>docs/chat-view-notes.md</c>。
/// 排序的回归是「不报错、只是变难用了」那种退化,所以放在没有 IO 的地方,直接喂字符串断言。
/// </para>
/// <para>
/// <b>分档而不是加权求和</b>:文件名前缀 &gt; 文件名包含 &gt; 整条路径包含 &gt; 文件名子序列 &gt; 路径子序列,档内才用深度和长度打破平局。
/// 加权求和调起来没有尽头,而且「为什么这一条排前面」永远解释不清;分档的结果可以一句话说明白。
/// </para>
/// </summary>
public static class FuzzyPath
{
    private const int NamePrefix = 5000;
    private const int NameContains = 4000;
    private const int PathContains = 3000;
    private const int NameSubsequence = 2000;
    private const int PathSubsequence = 1000;

    /// <summary>
    /// <paramref name="needle"/> 的每个字符是否按顺序出现在 <paramref name="hay"/> 里(允许跳字符)。
    /// 这一档存在的理由是缩写(<c>chv</c> 找到 <c>chat/ChatView.tsx</c>);代价是对短查询极其宽松,所以它是最低的两档。
    /// 两边都按 UTF-16 码元走:文件名里的 emoji 不该让两边错位。
    /// </summary>
    public static bool IsSubsequence(string hay, string needle)
    {
        if (needle.Length == 0)
        {
            return true;
        }

        var i = 0;
        foreach (var c in hay)
        {
            if (c == needle[i])
            {
                i++;
            }

            if (i == needle.Length)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>档内的微调 —— 越小越靠前,而且整体小于一个档的间距(1000),否则一条又深又长的路径会掉到下一档去。深度权重大于长度。</summary>
    private static int TieBreak(string path)
    {
        var depth = 0;
        foreach (var c in path)
        {
            if (c == '/')
            {
                depth++;
            }
        }

        return Math.Min(depth * 12 + path.Length, 900);
    }

    /// <summary>
    /// 一条候选对这次查询的得分。<b>不匹配返回 null</b>,不是 0 —— 0 是一个合法的低分,用它表示「不匹配」会让调用方漏掉过滤。
    /// <paramref name="path"/> 是项目相对、<c>/</c> 分隔;<paramref name="name"/> 是文件名。
    /// </summary>
    public static int? Score(string path, string name, string query)
    {
        var q = query.ToLowerInvariant();
        var penalty = TieBreak(path);
        if (q.Length == 0)
        {
            return -penalty;
        }

        var lowerName = name.ToLowerInvariant();
        var lowerPath = path.ToLowerInvariant();
        var inName = lowerName.IndexOf(q, StringComparison.Ordinal);
        if (inName == 0)
        {
            return NamePrefix - penalty;
        }

        if (inName > 0)
        {
            return NameContains - inName - penalty;
        }

        // 整条路径这一档要能吃下 chat/comp 这种带斜杠的写法:用户脑子里的定位方式常常是「哪个目录下的什么」
        if (lowerPath.Contains(q, StringComparison.Ordinal))
        {
            return PathContains - penalty;
        }

        if (IsSubsequence(lowerName, q))
        {
            return NameSubsequence - penalty;
        }

        return IsSubsequence(lowerPath, q) ? PathSubsequence - penalty : null;
    }

    /// <summary>排序并截断。<b>稳定排序</b>:分数相同的两条保持候选里的原有次序,否则同一个查询在两次敲键之间可能给出不同的顺序,列表会自己跳。</summary>
    public static IReadOnlyList<T> Rank<T>(IReadOnlyList<T> candidates, string query, int limit, Func<T, string> path, Func<T, string> name)
    {
        var scored = new List<(T Item, int Score, int Index)>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var item = candidates[i];
            if (Score(path(item), name(item), query) is { } score)
            {
                scored.Add((item, score, i));
            }
        }

        scored.Sort(static (a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.Index.CompareTo(b.Index));
        return [.. scored.Take(Math.Max(limit, 0)).Select(static s => s.Item)];
    }
}
