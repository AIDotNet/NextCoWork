using System.Text;
using System.Text.RegularExpressions;

namespace Ncw.Agent.Workspace.Files;

/// <summary>
/// glob 匹配器(纯函数,零 IO;和内置 Glob 工具同一套语法):<c>*</c> 段内任意字符不跨 <c>/</c>;<c>**</c> 任意层级,后面跟 <c>/</c> 时可以匹配零层;
/// <c>?</c> 段内单个字符;<c>{a,b}</c> 二选一(不嵌套)。其余全部按字面量,不支持字符类 / 取反。
/// </summary>
internal static class Glob
{
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
                    if (".+^$()|[]\\{}".Contains(ch))
                    {
                        sb.Append('\\');
                    }

                    sb.Append(ch);
                    break;
            }
        }

        while (braceDepth-- > 0)
        {
            sb.Append(')');
        }

        sb.Append("\\z");
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase));
    }
}
