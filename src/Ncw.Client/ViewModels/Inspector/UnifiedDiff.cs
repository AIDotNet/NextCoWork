using System.Collections.Immutable;
using System.Globalization;

namespace Ncw.Client.ViewModels;

public enum DiffLineKind
{
    Context,
    Added,
    Removed,

    /// <summary>折叠起来的未改动行 / 一个 hunk 的头(<c>@@ -a,b +c,d @@</c>)。不含界面文字:<see cref="DiffRow.Text"/> 是 hunk 头。</summary>
    Fold,
}

/// <summary>diff 里的一行。<see cref="OldLine"/> / <see cref="NewLine"/> 从 1 起,没有的一侧为 null。</summary>
public sealed record DiffRow(DiffLineKind Kind, int? OldLine, int? NewLine, string Text)
{
    public bool IsAdded => Kind == DiffLineKind.Added;

    public bool IsRemoved => Kind == DiffLineKind.Removed;

    public bool IsContext => Kind == DiffLineKind.Context;

    public bool IsFold => Kind == DiffLineKind.Fold;

    /// <summary>行首的符号(<c>+</c> / <c>-</c> / 空格);不是界面文字。</summary>
    public string Sign => Kind switch { DiffLineKind.Added => "+", DiffLineKind.Removed => "−", _ => " " };

    public string OldNumber => OldLine?.ToString(CultureInfo.InvariantCulture) ?? "";

    public string NewNumber => NewLine?.ToString(CultureInfo.InvariantCulture) ?? "";
}

/// <summary>并排视图的一行:左(旧)右(新)各一格,或一整行的 hunk 头。</summary>
public sealed record SplitRow(DiffRow? Left, DiffRow? Right, string? Hunk = null)
{
    public bool IsHunk => Hunk is not null;

    public string LeftText => Left?.Text ?? "";

    public string RightText => Right?.Text ?? "";

    public string LeftNumber => Left?.OldNumber ?? "";

    public string RightNumber => Right?.NewNumber ?? "";

    public bool LeftRemoved => Left is { Kind: DiffLineKind.Removed };

    public bool RightAdded => Right is { Kind: DiffLineKind.Added };

    public bool LeftEmpty => Left is null;

    public bool RightEmpty => Right is null;
}

/// <summary>
/// diff 的行模型(纯函数,单测见 Ncw.Client.Tests)。两种来源,同一种结果:
/// <see cref="Parse"/> 读 <c>git diff</c> 的 unified 文本(Git 面板);<see cref="FromSides"/> 对 before / after 全文做行级对比(改动审查)。
/// 并排视图(<see cref="Split"/>)由同一份行重新排列得到:每个 hunk 里连续的删除和新增按顺序配对。
/// 行级增删计数口径和 Core 的 <c>LineDiff.Count</c> 一致(都是最长公共子序列;测试里互相校对)。
/// </summary>
public static class UnifiedDiff
{
    /// <summary>每处改动上下各留几行未改动的行。</summary>
    public const int Context = 3;

    /// <summary>对比的格子数上限(行数相乘);超过不做精确 LCS,中间部分按「全删 + 全加」处理。</summary>
    private const long MaxCells = 6_000_000;

    public static ImmutableArray<DiffRow> Parse(string unified)
    {
        var rows = ImmutableArray.CreateBuilder<DiffRow>();
        if (string.IsNullOrEmpty(unified))
        {
            return rows.ToImmutable();
        }

        int oldLine = 0, newLine = 0;
        var inHunk = false;
        foreach (var raw in unified.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                (oldLine, newLine) = HunkStart(line);
                rows.Add(new DiffRow(DiffLineKind.Fold, null, null, line));
                inHunk = true;
                continue;
            }

            if (!inHunk || line.Length == 0 && raw.Length == 0)
            {
                continue; // 文件头(diff --git / index / --- / +++)和末尾的空串
            }

            switch (line[0])
            {
                case '+':
                    rows.Add(new DiffRow(DiffLineKind.Added, null, newLine++, line[1..]));
                    break;
                case '-':
                    rows.Add(new DiffRow(DiffLineKind.Removed, oldLine++, null, line[1..]));
                    break;
                case '\\':
                    break; // "\ No newline at end of file"
                default:
                    rows.Add(new DiffRow(DiffLineKind.Context, oldLine++, newLine++, line.Length > 0 ? line[1..] : ""));
                    break;
            }
        }

        return rows.ToImmutable();
    }

    private static (int Old, int New) HunkStart(string header)
    {
        // @@ -12,6 +12,7 @@ optional section
        var old = 1;
        var next = 1;
        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.StartsWith('-') && part.Length > 1)
            {
                old = Leading(part[1..]);
            }
            else if (part.StartsWith('+') && part.Length > 1)
            {
                next = Leading(part[1..]);
                break;
            }
        }

        return (old, next);

        static int Leading(string text)
        {
            var comma = text.IndexOf(',', StringComparison.Ordinal);
            return int.TryParse(comma >= 0 ? text[..comma] : text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 1;
        }
    }

    /// <summary>
    /// 两段全文的行级 diff。<paramref name="before"/> 为空串 = 新建(全是新增);<paramref name="after"/> 为空串 = 删除。
    /// 未改动的行只保留改动上下各 <see cref="Context"/> 行,其余折成 hunk 头。
    /// </summary>
    public static ImmutableArray<DiffRow> FromSides(string before, string after, int context = Context)
    {
        var a = Lines(before);
        var b = Lines(after);
        var ops = Diff(a, b);
        return Fold(ops, context);
    }

    private enum Op : byte
    {
        Same,
        Add,
        Del,
    }

    private static List<(Op Op, string Text)> Diff(List<string> a, List<string> b)
    {
        var ops = new List<(Op, string)>();
        var start = 0;
        while (start < a.Count && start < b.Count && a[start] == b[start])
        {
            ops.Add((Op.Same, a[start]));
            start++;
        }

        var endA = a.Count;
        var endB = b.Count;
        var tail = new List<(Op, string)>();
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            tail.Add((Op.Same, a[endA - 1]));
            endA--;
            endB--;
        }

        var n = endA - start;
        var m = endB - start;
        if (n == 0 || m == 0 || (long)n * m > MaxCells)
        {
            for (var i = start; i < endA; i++)
            {
                ops.Add((Op.Del, a[i]));
            }

            for (var j = start; j < endB; j++)
            {
                ops.Add((Op.Add, b[j]));
            }
        }
        else
        {
            // lcs[i, j] = a[start+i..] 与 b[start+j..] 的最长公共子序列长度
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
            {
                for (var j = m - 1; j >= 0; j--)
                {
                    lcs[i, j] = a[start + i] == b[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (a[start + x] == b[start + y])
                {
                    ops.Add((Op.Same, a[start + x]));
                    x++;
                    y++;
                }
                else if (lcs[x + 1, y] >= lcs[x, y + 1])
                {
                    ops.Add((Op.Del, a[start + x++]));
                }
                else
                {
                    ops.Add((Op.Add, b[start + y++]));
                }
            }

            while (x < n)
            {
                ops.Add((Op.Del, a[start + x++]));
            }

            while (y < m)
            {
                ops.Add((Op.Add, b[start + y++]));
            }
        }

        for (var i = tail.Count - 1; i >= 0; i--)
        {
            ops.Add(tail[i]);
        }

        return ops;
    }

    private static ImmutableArray<DiffRow> Fold(List<(Op Op, string Text)> ops, int context)
    {
        // 每个操作的行号
        var numbered = new List<(Op Op, string Text, int? Old, int? New)>(ops.Count);
        int oldNo = 1, newNo = 1;
        foreach (var (op, text) in ops)
        {
            switch (op)
            {
                case Op.Same:
                    numbered.Add((op, text, oldNo++, newNo++));
                    break;
                case Op.Del:
                    numbered.Add((op, text, oldNo++, null));
                    break;
                default:
                    numbered.Add((op, text, null, newNo++));
                    break;
            }
        }

        var keep = new bool[numbered.Count];
        var changed = false;
        for (var i = 0; i < numbered.Count; i++)
        {
            if (numbered[i].Op == Op.Same)
            {
                continue;
            }

            changed = true;
            for (var k = Math.Max(0, i - context); k <= Math.Min(numbered.Count - 1, i + context); k++)
            {
                keep[k] = true;
            }
        }

        var rows = ImmutableArray.CreateBuilder<DiffRow>();
        if (!changed)
        {
            return rows.ToImmutable();
        }

        for (var i = 0; i < numbered.Count; i++)
        {
            if (!keep[i])
            {
                continue;
            }

            if (i == 0 || !keep[i - 1])
            {
                // 一个新的 hunk:头里写起止行号和行数
                var j = i;
                int oldCount = 0, newCount = 0;
                while (j < numbered.Count && keep[j])
                {
                    oldCount += numbered[j].Old is null ? 0 : 1;
                    newCount += numbered[j].New is null ? 0 : 1;
                    j++;
                }

                var oldStart = numbered[i].Old ?? FirstNumber(numbered, i, old: true);
                var newStart = numbered[i].New ?? FirstNumber(numbered, i, old: false);
                rows.Add(new DiffRow(DiffLineKind.Fold, null, null, $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@"));
            }

            var (op, text, o, n) = numbered[i];
            rows.Add(new DiffRow(op switch { Op.Add => DiffLineKind.Added, Op.Del => DiffLineKind.Removed, _ => DiffLineKind.Context }, o, n, text));
        }

        return rows.ToImmutable();
    }

    private static int FirstNumber(List<(Op Op, string Text, int? Old, int? New)> numbered, int from, bool old)
    {
        for (var i = from; i < numbered.Count; i++)
        {
            if ((old ? numbered[i].Old : numbered[i].New) is { } n)
            {
                return n;
            }
        }

        return 1;
    }

    /// <summary>并排:每个 hunk 内,一段连续的删除和紧跟着的新增按顺序一对一配对,多出来的一侧留空;未改动的行两侧都有。</summary>
    public static ImmutableArray<SplitRow> Split(ImmutableArray<DiffRow> rows)
    {
        var result = ImmutableArray.CreateBuilder<SplitRow>();
        var removed = new List<DiffRow>();
        var added = new List<DiffRow>();
        foreach (var row in rows)
        {
            if (row.Kind == DiffLineKind.Removed)
            {
                if (added.Count > 0)
                {
                    Flush();
                }

                removed.Add(row);
                continue;
            }

            if (row.Kind == DiffLineKind.Added)
            {
                added.Add(row);
                continue;
            }

            Flush();
            result.Add(row.Kind == DiffLineKind.Fold ? new SplitRow(null, null, row.Text) : new SplitRow(row, row));
        }

        Flush();
        return result.ToImmutable();

        void Flush()
        {
            for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
            {
                result.Add(new SplitRow(i < removed.Count ? removed[i] : null, i < added.Count ? added[i] : null));
            }

            removed.Clear();
            added.Clear();
        }
    }

    public static (int Added, int Removed) Count(ImmutableArray<DiffRow> rows) =>
        (rows.Count(static r => r.Kind == DiffLineKind.Added), rows.Count(static r => r.Kind == DiffLineKind.Removed));

    private static List<string> Lines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var lines = new List<string>(text.Split('\n'));
        if (lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].EndsWith('\r'))
            {
                lines[i] = lines[i][..^1];
            }
        }

        return lines;
    }
}
