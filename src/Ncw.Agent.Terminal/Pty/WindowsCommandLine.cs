using System.Text;

namespace Ncw.Agent.Terminal;

/// <summary>Windows 进程启动参数的纯字符串部分(命令行引号、环境块),与平台无关,所以在所有平台上都有单测。</summary>
internal static class WindowsCommandLine
{
    /// <summary>按 <c>CommandLineToArgvW</c> 的规则给每个参数加引号。</summary>
    public static string Build(string program, IReadOnlyList<string> args)
    {
        var builder = new StringBuilder();
        AppendQuoted(builder, program);
        foreach (var arg in args)
        {
            builder.Append(' ');
            AppendQuoted(builder, arg);
        }

        return builder.ToString();
    }

    private static void AppendQuoted(StringBuilder builder, string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            builder.Append(value);
            return;
        }

        builder.Append('"');
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2).Append('"');
    }

    /// <summary>UTF-16 环境块:按名字(不区分大小写)排序,以两个 NUL 结尾(<c>CreateProcess</c> 的要求)。</summary>
    public static char[] EnvironmentBlock(IReadOnlyList<KeyValuePair<string, string>> environment)
    {
        var builder = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(static e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(name).Append('=').Append(value).Append('\0');
        }

        builder.Append('\0');
        return builder.ToString().ToCharArray();
    }
}
