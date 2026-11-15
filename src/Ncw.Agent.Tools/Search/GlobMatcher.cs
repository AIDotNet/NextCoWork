using System.Text;
using System.Text.RegularExpressions;

namespace Ncw.Agent.Tools;

/// <summary>
/// glob 匹配器 —— 纯函数,零 IO(移植自 2.x <c>glob-match.ts</c>)。不引外部库:它们自己做文件遍历,正好绕过环境层的路径守卫。
/// <list type="table">
/// <item><term><c>*</c></term><description>段内任意字符,<b>不跨 <c>/</c></b></description></item>
/// <item><term><c>**</c></term><description>任意层级;后面跟 <c>/</c> 时可以匹配<b>零</b>层</description></item>
/// <item><term><c>?</c></term><description>段内单个字符</description></item>
/// <item><term><c>{a,b}</c></term><description>二选一,不支持嵌套</description></item>
/// </list>
/// 其余全部按字面量,正则元字符一律转义。<b>明确不支持</b>字符类 <c>[abc]</c>、取反 <c>!</c>、扩展 glob:它们按字面量走,不会静默变成别的意思。
/// </summary>
public static class GlobMatcher
{
    /// <summary>
    /// 把 glob 编成正则。后面跟着斜杠的双星编出来的必须是「零层也算命中」的形式(<c>(?:[^/]*/)*</c>),
    /// 用「任意字符 + 斜杠」的话,「搜所有 ts 文件」会<b>静默漏掉根目录下的那几个</b>,而结果看起来完全合理。
    /// </summary>
    public static Regex Compile(string pattern, bool caseSensitive)
    {
        var sb = new StringBuilder("^");
        var braceDepth = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            switch (ch)
            {
                case '*':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                        {
                            i++;
                            sb.Append("(?:[^/]*/)*");
                        }
                        else
                        {
                            sb.Append(".*");
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }

                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '{':
                    braceDepth++;
                    sb.Append("(?:");
                    break;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    sb.Append(')');
                    break;
                case ',' when braceDepth > 0:
                    sb.Append('|');
                    break;
                default:
                    if (".+^$()|[]\\".Contains(ch))
                    {
                        sb.Append('\\');
                    }

                    sb.Append(ch);
                    break;
            }
        }

        // 花括号没闭合 —— 不抛错(pattern 来自模型),补齐了事,大不了匹配不到
        while (braceDepth-- > 0)
        {
            sb.Append(')');
        }

        // 末尾用 \z 而不是 $:$ 会匹配结尾换行之前
        sb.Append("\\z");
        // Singleline:文件名里的换行符不该让 . 之类的失效(这里没有 . 通配,只是防御)
        var options = RegexOptions.CultureInvariant | RegexOptions.Singleline | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        return new Regex(sb.ToString(), options);
    }

    /// <summary>归一化成匹配用的形式:Windows 分隔符换成 <c>/</c>,去掉开头的 <c>./</c>。匹配的目标一律是相对路径。</summary>
    public static string Normalize(string path)
    {
        var s = path.Replace('\\', '/');
        while (s.StartsWith("./", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        return s;
    }

    public static bool IsMatch(string pattern, string path, bool caseSensitive = true) => Compile(pattern, caseSensitive).IsMatch(Normalize(path));
}
