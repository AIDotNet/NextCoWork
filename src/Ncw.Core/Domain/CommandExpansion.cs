using System.Text.RegularExpressions;

namespace Ncw.Core.Domain;

/// <summary>一次 <c>/命令 参数</c> 的解析结果。<see cref="Args"/> 是命令名之后的全部内容(已 trim),没有参数时是空串。</summary>
public sealed record CommandInvocation(string Name, string Args);

/// <summary>
/// 斜杠命令的展开(2.x 的 <c>shared/domain/command.ts</c>):命令是<b>一段提示词模板</b>,不是一个能执行的动作。
/// <b>展开在客户端发送前完成,设备收到的是普通文本</b>——手机和桌面看到的输入一致;<c>/init</c> 和用户自己把那一整段话打进输入框在下游是同一件事。
/// 纯函数,客户端和 Agent 共用(AGENTS.md §3 规则 3)。
/// </summary>
public static partial class CommandExpansion
{
    /// <summary>命令名。会出现在 <c>/xxx</c> 里,所以不许带空白和路径分隔符。</summary>
    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    public const int DescriptionMax = 200;

    public const int PromptMax = 32 * 1024;

    /// <summary>
    /// 草稿是不是<b>整条</b>都是一次命令调用:<c>帮我看下 /init 这个命令</c> 里的 <c>/init</c> 是在说话,不是在调用;判据落在「<c>/</c> 在开头」上。
    /// </summary>
    public static CommandInvocation? Parse(string draft)
    {
        var m = InvocationPattern().Match(draft);
        return m.Success ? new CommandInvocation(m.Groups[1].Value, m.Groups[2].Value.Trim()) : null;
    }

    /// <summary>
    /// 把参数塞进模板:<c>$ARGUMENTS</c> → 全部参数原样;<c>$1</c>…<c>$9</c> → 按空白切开的第 n 个。
    /// ★ 模板里<b>一个占位符都没有</b>、用户却带了参数时,把参数追加到末尾,而不是丢掉(丢掉的话用户敲的那句话会凭空消失)。
    /// </summary>
    public static string Expand(string prompt, string args)
    {
        var positional = args.Length == 0 ? [] : WhitespacePattern().Split(args);
        var used = false;
        var output = AllArgumentsPattern().Replace(prompt, _ =>
        {
            used = true;
            return args;
        });
        output = PositionalPattern().Replace(output, m =>
        {
            used = true;
            var index = m.Groups[1].Value[0] - '1';
            return index < positional.Length ? positional[index] : "";
        });
        return used || args.Length == 0 ? output : $"{output}\n\n{args}";
    }

    /// <summary>
    /// 草稿 → 真正要发出去的文本。不是一次命令调用就原样返回;<b>认不出的命令名也原样返回</b>(用户可能真的想以 <c>/</c> 开头说一句话,吞掉或报错都比照他写的发出去更糟)。
    /// 名字不区分大小写。
    /// </summary>
    public static string Apply(string draft, IEnumerable<(string Name, string Prompt)> commands)
    {
        if (Parse(draft) is not { } call)
        {
            return draft;
        }

        foreach (var (name, prompt) in commands)
        {
            if (string.Equals(name, call.Name, StringComparison.OrdinalIgnoreCase))
            {
                return Expand(prompt, call.Args);
            }
        }

        return draft;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,63}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^\s*/([A-Za-z0-9][A-Za-z0-9_-]{0,63})(?:[ \t]+([\s\S]*))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex InvocationPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"\$ARGUMENTS(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex AllArgumentsPattern();

    [GeneratedRegex(@"\$([1-9])(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex PositionalPattern();
}
