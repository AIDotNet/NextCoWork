using System.Globalization;
using System.Text;

namespace Ncw.Agent.Tools;

internal sealed record EditSpec(string Old, string New, bool ReplaceAll);

/// <summary>在(LF 归一化后的)原文里的一处替换:[Start, End) 换成 Text。</summary>
internal sealed record Replacement(int Start, int End, string Text, int EditIndex);

internal sealed record LocatedEdits(IReadOnlyList<Replacement> Replacements, bool Fuzzy);

/// <summary>
/// 宽松匹配用的「折叠」文本(03 §2,取自 pi):NFKC、弯引号 / 各种破折号 / 特殊空格归一、行尾空白去掉。
/// 折叠后的每个字符都记着它在原文里的区间,所以匹配之后替换的是<b>原文的那一段</b>——不会把整个文件悄悄归一化。
/// </summary>
internal sealed class FuzzyText
{
    private readonly string _folded;
    private readonly int[] _starts;
    private readonly int[] _ends;

    private FuzzyText(string folded, int[] starts, int[] ends)
    {
        _folded = folded;
        _starts = starts;
        _ends = ends;
    }

    public static FuzzyText Build(string text)
    {
        var starts = new List<int>(text.Length);
        var ends = new List<int>(text.Length);
        var folded = Fold(text, starts, ends);
        return new FuzzyText(folded, [.. starts], [.. ends]);
    }

    /// <summary>折叠一段文本(不要映射)。模型给的 old_string 用它。</summary>
    public static string Normalize(string text) => Fold(text, null, null);

    /// <summary>所有不重叠的命中,返回原文里的区间。</summary>
    public List<(int Start, int End)> FindAll(string needle)
    {
        var result = new List<(int, int)>();
        var folded = Normalize(needle);
        if (folded.Length == 0)
        {
            return result;
        }

        var from = 0;
        while (from <= _folded.Length - folded.Length)
        {
            var index = _folded.IndexOf(folded, from, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            result.Add((_starts[index], _ends[index + folded.Length - 1]));
            from = index + folded.Length;
        }

        return result;
    }

    private static string Fold(string text, List<int>? starts, List<int>? ends)
    {
        var output = new StringBuilder(text.Length);
        var pending = new List<(char C, int S, int E)>();

        void Emit(char c, int s, int e)
        {
            if (c == '\n')
            {
                pending.Clear(); // 行尾空白不要
            }
            else if (c is ' ' or '\t')
            {
                pending.Add((c, s, e));
                return;
            }
            else
            {
                foreach (var (pc, ps, pe) in pending)
                {
                    Append(pc, ps, pe);
                }

                pending.Clear();
            }

            Append(c, s, e);
        }

        void Append(char c, int s, int e)
        {
            output.Append(c);
            starts?.Add(s);
            ends?.Add(e);
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c < 0x80 && (i + 1 >= text.Length || text[i + 1] < 0x300))
            {
                Emit(c, i, i + 1);
                i++;
                continue;
            }

            var length = System.Globalization.StringInfo.GetNextTextElementLength(text.AsSpan(i));
            var element = text.Substring(i, length);
            var normalized = element.Normalize(NormalizationForm.FormKC);
            foreach (var n in normalized)
            {
                Emit(Map(n), i, i + length);
            }

            i += length;
        }

        // 末尾的空白也是行尾空白
        pending.Clear();
        return output.ToString();
    }

    private static char Map(char c) => c switch
    {
        '‘' or '’' or '‚' or '‛' => '\'',
        '“' or '”' or '„' or '‟' => '"',
        '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' => '-',
        ' ' or ' ' or ' ' or ' ' or ' ' or ' ' or ' ' or ' ' or ' ' or ' ' or ' ' or ' ' or '　' => ' ',
        _ => c,
    };
}

internal static class EditEngine
{
    /// <summary>
    /// 找出每处编辑在原文里的位置。要求(03 §2,取自 pi):每处必须唯一(除非 replace_all)、互不重叠,都相对<b>原文件</b>匹配。
    /// 精确匹配失败时退到宽松匹配。失败抛 <see cref="ToolFailureException"/>,消息给出下一步该做什么。
    /// </summary>
    public static LocatedEdits Locate(string text, IReadOnlyList<EditSpec> edits, string rel, bool singleForm)
    {
        FuzzyText? fuzzy = null;
        var usedFuzzy = false;
        var all = new List<Replacement>();
        for (var index = 0; index < edits.Count; index++)
        {
            var edit = edits[index];
            var prefix = singleForm ? "" : $"edits[{index}]: ";
            var needle = Text.ToLf(edit.Old);
            var replacement = Text.ToLf(edit.New);
            if (needle.Length == 0)
            {
                throw new ToolFailureException($"{prefix}old_string must not be empty.");
            }

            var spans = FindExact(text, needle);
            if (spans.Count == 0)
            {
                fuzzy ??= FuzzyText.Build(text);
                spans = fuzzy.FindAll(needle);
                if (spans.Count > 0)
                {
                    usedFuzzy = true;
                }
            }

            if (spans.Count == 0)
            {
                // 不要把文件内容附在错误里:模型会以为那是它该抄的原文;一次失败的编辑也不该把整个文件塞进上下文窗。给一条可执行的下一步就够了。
                throw new ToolFailureException(
                    $"{prefix}old_string was not found in {rel}. Common causes: the indentation does not match (tabs vs spaces), "
                    + "trailing whitespace, or you copied the line-number prefix out of Read output — "
                    + "the number and the tab after it are not part of the file. Read the file again to get the exact text. No edit was applied.");
            }

            if (spans.Count > 1 && !edit.ReplaceAll)
            {
                throw new ToolFailureException(
                    $"{prefix}old_string appears {spans.Count} times in {rel}, so it is not unique and nothing was changed. "
                    + "Add surrounding lines until the match is unique; pass replace_all: true only if you really do mean every occurrence.");
            }

            foreach (var (start, end) in edit.ReplaceAll ? spans : spans.GetRange(0, 1))
            {
                all.Add(new Replacement(start, end, replacement, index));
            }
        }

        all.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        for (var i = 1; i < all.Count; i++)
        {
            if (all[i].Start < all[i - 1].End)
            {
                var (a, b) = (all[i - 1].EditIndex, all[i].EditIndex);
                throw new ToolFailureException(
                    a == b
                        ? $"Edit {a} matches overlapping text in {rel}. Nothing was changed."
                        : $"edits[{a}] and edits[{b}] overlap in {rel}. Merge them into one edit that covers the whole region; nothing was changed.");
            }
        }

        return new LocatedEdits(all, usedFuzzy);
    }

    public static string Apply(string text, IReadOnlyList<Replacement> replacements)
    {
        var output = new StringBuilder(text.Length);
        var position = 0;
        foreach (var r in replacements)
        {
            output.Append(text, position, r.Start - position).Append(r.Text);
            position = r.End;
        }

        return output.Append(text, position, text.Length - position).ToString();
    }

    private static List<(int Start, int End)> FindExact(string text, string needle)
    {
        var result = new List<(int, int)>();
        var from = 0;
        while (from <= text.Length - needle.Length)
        {
            var index = text.IndexOf(needle, from, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            result.Add((index, index + needle.Length));
            from = index + needle.Length;
        }

        return result;
    }

    // ───────────────────────── diff(只给界面) ─────────────────────────

    private const int ContextLines = 3;
    private const int MaxDiffChars = 50_000;

    /// <summary>
    /// 由编辑区间直接生成统一 diff 风格的文本(不跑通用 diff:我们知道每处替换的确切区间,O(文件大小)):
    /// 共用行的替换合成一块,相距不超过两倍上下文行的块合成一个 hunk。返回 diff、第一处改动的行号(1 起)、增删行数。
    /// </summary>
    public static (string Diff, int FirstChangedLine, int Added, int Removed) BuildDiff(string original, IReadOnlyList<Replacement> replacements)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < original.Length; i++)
        {
            if (original[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }

        int LineOf(int offset)
        {
            var index = lineStarts.BinarySearch(offset);
            return index >= 0 ? index : ~index - 1;
        }

        int LineEnd(int line) => line + 1 < lineStarts.Count ? lineStarts[line + 1] - 1 : original.Length; // 不含换行

        // 1. 按行合并成块
        var blocks = new List<(int FirstLine, int LastLine, List<Replacement> Items)>();
        foreach (var r in replacements)
        {
            var first = LineOf(r.Start);
            var last = LineOf(r.End); // End 是匹配之后的第一个字符:它所在的行就是后缀接着的行(刚好在行首时那一整行没变,下面会当上下文)
            if (blocks.Count > 0 && first <= blocks[^1].LastLine)
            {
                var b = blocks[^1];
                b.Items.Add(r);
                blocks[^1] = (b.FirstLine, Math.Max(b.LastLine, last), b.Items);
            }
            else
            {
                blocks.Add((first, last, [r]));
            }
        }

        var output = new StringBuilder();
        var added = 0;
        var removed = 0;
        var delta = 0; // 新文件相对原文件的行偏移
        var firstChanged = blocks.Count > 0 ? blocks[0].FirstLine + 1 : 1;
        var totalLines = lineStarts.Count;

        var h = 0;
        while (h < blocks.Count)
        {
            // 2. 把相距不远的块并进同一个 hunk
            var end = h;
            while (end + 1 < blocks.Count && blocks[end + 1].FirstLine - blocks[end].LastLine - 1 <= ContextLines * 2)
            {
                end++;
            }

            var hunkFirst = Math.Max(0, blocks[h].FirstLine - ContextLines);
            var hunkLast = Math.Min(totalLines - 1, blocks[end].LastLine + ContextLines);
            var body = new StringBuilder();
            var oldCount = 0;
            var newCount = 0;
            var cursor = hunkFirst;

            void Context(int fromLine, int toLine)
            {
                for (var l = fromLine; l <= toLine; l++)
                {
                    body.Append(' ').Append(original, lineStarts[l], LineEnd(l) - lineStarts[l]).Append('\n');
                    oldCount++;
                    newCount++;
                }
            }

            for (var k = h; k <= end; k++)
            {
                var block = blocks[k];
                Context(cursor, block.FirstLine - 1);

                var regionStart = lineStarts[block.FirstLine];
                var regionEnd = LineEnd(block.LastLine);
                var oldLines = original[regionStart..regionEnd].Split('\n');
                var newRegion = new StringBuilder();
                var pos = regionStart;
                foreach (var r in block.Items)
                {
                    newRegion.Append(original, pos, r.Start - pos).Append(r.Text);
                    pos = r.End;
                }

                newRegion.Append(original, pos, regionEnd - pos);
                var newLines = newRegion.ToString().Split('\n');

                // 块首尾没变的行当上下文(匹配刚好在行首 / 行尾结束时,相邻的整行是原样的)
                var lead = 0;
                while (lead < oldLines.Length && lead < newLines.Length && oldLines[lead] == newLines[lead])
                {
                    lead++;
                }

                var trail = 0;
                while (trail < oldLines.Length - lead && trail < newLines.Length - lead && oldLines[^(trail + 1)] == newLines[^(trail + 1)])
                {
                    trail++;
                }

                for (var l = 0; l < lead; l++)
                {
                    body.Append(' ').Append(oldLines[l]).Append('\n');
                    oldCount++;
                    newCount++;
                }

                foreach (var line in oldLines.Skip(lead).Take(oldLines.Length - lead - trail))
                {
                    body.Append('-').Append(line).Append('\n');
                    oldCount++;
                    removed++;
                }

                foreach (var line in newLines.Skip(lead).Take(newLines.Length - lead - trail))
                {
                    body.Append('+').Append(line).Append('\n');
                    newCount++;
                    added++;
                }

                for (var l = oldLines.Length - trail; l < oldLines.Length; l++)
                {
                    body.Append(' ').Append(oldLines[l]).Append('\n');
                    oldCount++;
                    newCount++;
                }

                cursor = block.LastLine + 1;
            }

            Context(cursor, hunkLast);
            output.Append(string.Create(CultureInfo.InvariantCulture, $"@@ -{hunkFirst + 1},{oldCount} +{hunkFirst + 1 + delta},{newCount} @@\n"));
            output.Append(body);
            delta += newCount - oldCount;
            h = end + 1;
            if (output.Length > MaxDiffChars)
            {
                output.Append("[diff truncated]\n");
                break;
            }
        }

        return (output.ToString(), firstChanged, added, removed);
    }
}
