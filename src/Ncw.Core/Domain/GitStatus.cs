using System.Collections.Immutable;
using System.Globalization;

namespace Ncw.Core.Domain;

/// <summary>
/// 一个文件在仓库里的状态。porcelain 的 XY 两位原样带上来(<see cref="Index"/> / <see cref="Worktree"/>):
/// 同一个文件可以「暂存了一次修改、之后又改了一遍」(<c>MM</c>),压成一个枚举就得二选一,而界面要的是分别显示这两栏。
/// 路径是<b>仓库相对</b>(git 自己的写法,<c>/</c> 分隔),不参与任何路径拼接,只被原样回传给 git。
/// </summary>
/// <param name="Index">porcelain 的 X 位(暂存区)。<c>.</c> / 空格 = 这一栏没有改动。</param>
/// <param name="Worktree">porcelain 的 Y 位(工作区)。</param>
/// <param name="RenamedFrom">重命名 / 复制的来源路径(XY 含 <c>R</c> / <c>C</c> 时才有)。</param>
public sealed record GitFileChange(
    string Path,
    string Index,
    string Worktree,
    bool Staged,
    bool Unstaged,
    bool Untracked,
    bool Conflicted,
    string? RenamedFrom = null);

/// <summary><c>git status --porcelain=v2 --branch -z</c> 解析出的仓库快照。</summary>
public sealed record GitStatusSnapshot(
    string Branch,
    bool Detached,
    bool Unborn,
    string Upstream,
    int Ahead,
    int Behind,
    ImmutableArray<GitFileChange> Files,
    bool FilesTruncated);

/// <summary>改动文件的各类计数(Git 面板的分组标题、侧栏角标等界面共用,只在这里算一份)。</summary>
/// <param name="Staged">进「暂存区」分组的文件数。</param>
/// <param name="Unstaged">进「未暂存」分组的文件数(已暂存后又改过的文件两个分组都算)。</param>
public sealed record GitStatusSummary(int Total, int Staged, int Unstaged, int Untracked, int Conflicted)
{
    public static readonly GitStatusSummary Empty = new(0, 0, 0, 0, 0);

    /// <summary>工作区是干净的(没有任何改动、未跟踪文件和冲突)。</summary>
    public bool Clean => Total == 0;

    public static GitStatusSummary Of(IEnumerable<GitFileChange> files)
    {
        int total = 0, staged = 0, unstaged = 0, untracked = 0, conflicted = 0;
        foreach (var file in files)
        {
            total++;
            staged += file.Staged ? 1 : 0;
            unstaged += file.Unstaged ? 1 : 0;
            untracked += file.Untracked ? 1 : 0;
            conflicted += file.Conflicted ? 1 : 0;
        }

        return new GitStatusSummary(total, staged, unstaged, untracked, conflicted);
    }
}

/// <summary>
/// <c>git status --porcelain=v2 --branch -z</c> 的解析与「暂存 / 未暂存 / 未跟踪 / 冲突」的派生(移植自 2.x 的 <c>ipc/git-status.ts</c>)。
/// Agent 和控制端共用这<b>一个</b>函数(AGENTS.md §3 规则 3):分组「暂存区 / 未暂存」是整个 Git 面板唯一的结构,完全由这里算出的 <c>Staged</c> / <c>Unstaged</c> 决定。
/// <para>
/// ★ <b>v2 的「这一栏没有改动」是 <c>.</c>,不是空格</b>。v1 用空格,v2 换成了点号,两者只差一个字符。判成空格的表现是:每一个已暂存的文件同时出现在「未暂存」分组里,
/// 点「暂存」按钮等于对一个已暂存文件再 <c>git add</c> 一次,界面纹丝不动。所以两位都按 <c>.</c> 和空格一起判。
/// </para>
/// <para>
/// ★ 用 <c>-z</c> 是为了中文和带空格的文件名:非 <c>-z</c> 的输出会把这类路径加引号并转义,解析端要自己反转义,解析错的表现是文件点不开,而且只在中文路径上出现。
/// <c>-z</c> 模式下 rename 的<b>来源路径是下一条记录</b>,少读这一条,后面每一条记录的类型判断都会错位一格。
/// </para>
/// </summary>
public static class GitStatus
{
    /// <summary>改动文件列表的上限。一个 .gitignore 写漏的仓库能有几十万条。</summary>
    public const int MaxFiles = 2000;

    public static GitStatusSnapshot Parse(string stdout)
    {
        // -z:每条记录以 NUL 结尾(不是分隔),末尾会留一个空串
        var records = stdout.Split('\0');
        var branch = "";
        var detached = false;
        var unborn = false;
        var upstream = "";
        var ahead = 0;
        var behind = 0;
        var files = ImmutableArray.CreateBuilder<GitFileChange>();
        var truncated = false;

        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length == 0)
            {
                continue;
            }

            if (record.StartsWith("# ", StringComparison.Ordinal))
            {
                var body = record[2..];
                var space = body.IndexOf(' ');
                var key = space < 0 ? body : body[..space];
                var value = space < 0 ? "" : body[(space + 1)..];
                switch (key)
                {
                    case "branch.oid":
                        // `(initial)` = 还没有第一个提交
                        unborn = value == "(initial)";
                        break;
                    case "branch.head":
                        // `(detached)` 是 git 的字面输出,不是一个真的分支名
                        if (value == "(detached)")
                        {
                            detached = true;
                        }
                        else
                        {
                            branch = value;
                        }

                        break;
                    case "branch.upstream":
                        upstream = value;
                        break;
                    case "branch.ab":
                        // 形如 `+1 -2`
                        var parts = value.Split(' ');
                        ahead = ParseInt(parts.ElementAtOrDefault(0)?.Replace("+", "", StringComparison.Ordinal));
                        behind = Math.Abs(ParseInt(parts.ElementAtOrDefault(1)));
                        break;
                }

                continue;
            }

            if (files.Count >= MaxFiles)
            {
                truncated = true;
                // 仍然继续扫,只是不再收:rename 记录要吃掉它后面那条来源路径
                if (record.StartsWith("2 ", StringComparison.Ordinal))
                {
                    i++;
                }

                continue;
            }

            var kind = record[0];
            if (kind is '1' or '2')
            {
                // 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
                // 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path>
                // 固定字段都不含空格,路径可能含,所以从第 N 个空格之后的全部是路径。
                var xy = Xy(record);
                var path = Tail(record, kind == '1' ? 8 : 9);
                string? renamedFrom = null;
                if (kind == '2')
                {
                    i++;
                    if (i < records.Length && records[i].Length > 0)
                    {
                        renamedFrom = records[i];
                    }
                }

                if (path.Length == 0)
                {
                    continue;
                }

                files.Add(Change(path, xy.X, xy.Y, untracked: false, renamedFrom));
            }
            else if (kind == 'u')
            {
                // u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
                var xy = Xy(record);
                var path = Tail(record, 10);
                if (path.Length == 0)
                {
                    continue;
                }

                files.Add(Change(path, xy.X, xy.Y, untracked: false));
            }
            else if (kind == '?')
            {
                var path = record.Length > 2 ? record[2..] : "";
                if (path.Length == 0)
                {
                    continue;
                }

                files.Add(Change(path, "?", "?", untracked: true));
            }

            // `!`(ignored)不会出现:我们没传 --ignored
        }

        return new GitStatusSnapshot(branch, detached, unborn, upstream, ahead, behind, files.ToImmutable(), truncated);
    }

    /// <summary>从 porcelain 的 X / Y 两位派生分组标志。<paramref name="index"/> / <paramref name="worktree"/> 缺省或空串按「无改动」处理。</summary>
    public static GitFileChange Change(string path, string index, string worktree, bool untracked, string? renamedFrom = null)
    {
        // ★ 冲突的判据是 `U`,外加 `AA`(双方都新增)和 `DD`(双方都删除):后两种 XY 里一个 U 都没有,
        //   只看 U 会把它们当成普通改动给出暂存按钮。
        var conflicted = index == "U" || worktree == "U" || (index == "A" && worktree == "A") || (index == "D" && worktree == "D");
        return new GitFileChange(
            path,
            index,
            worktree,
            Staged: !untracked && !conflicted && !Unchanged(index) && index != "?",
            Unstaged: !untracked && !conflicted && !Unchanged(worktree) && worktree != "?",
            Untracked: untracked,
            Conflicted: conflicted,
            RenamedFrom: renamedFrom);
    }

    /// <summary>porcelain v2 里「这一栏没有改动」的几种写法。见类型说明 ★。</summary>
    private static bool Unchanged(string letter) => letter is "." or " " or "";

    private static (string X, string Y) Xy(string record)
    {
        // `1 XY ...`:第二个字段恰好两个字符
        var second = record.Length > 3 && record[1] == ' ' ? record.AsSpan(2) : [];
        var end = second.IndexOf(' ');
        var field = end < 0 ? second : second[..end];
        return (field.Length > 0 ? field[..1].ToString() : " ", field.Length > 1 ? field[1..2].ToString() : " ");
    }

    /// <summary>第 <paramref name="fixedFields"/> 个空格之后的全部(固定字段不含空格,路径可以含)。</summary>
    private static string Tail(string record, int fixedFields)
    {
        var position = 0;
        for (var seen = 0; seen < fixedFields; seen++)
        {
            var space = record.IndexOf(' ', position);
            if (space < 0)
            {
                return "";
            }

            position = space + 1;
        }

        return record[position..];
    }

    private static int ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
