namespace Ncw.Client.ViewModels;

/// <summary>
/// 内置的<b>本地动作</b>命令。斜杠命令本来是「一段提示词模板」(<c>commands.list</c>,发送前展开成普通文本);这几条是例外(2.x <c>Composer.tsx</c> 的 <c>localActions</c>):
/// 它们改的是会话的状态或者客户端的选择,展开成文本发给模型只会让模型口头答应一声。所以它们在提交时被拦下,设备永远收不到 <c>/compact</c> 这几个字。
/// </summary>
public enum SlashAction
{
    /// <summary><c>/goal &lt;条件&gt;</c> 设定目标;<c>/goal clear</c>(及其他清除词)清除;没有参数给用法提示。</summary>
    Goal,

    /// <summary><c>/compact [说明]</c> 立刻压缩上下文(只能在会话空闲时),说明接在摘要提示词的 <c>Additional focus:</c> 后面。</summary>
    Compact,

    /// <summary><c>/clear</c> 在同一个项目里开一个新会话(旧会话原样留在列表里)。</summary>
    Clear,

    /// <summary><c>/model &lt;名称&gt;</c> 切换模型;没有参数打开模型菜单。</summary>
    Model,
}

/// <summary>一次本地动作调用:动作 + 命令名之后的整段参数(已去掉首尾空白)。</summary>
public sealed record SlashInvocation(SlashAction Action, string Args);

/// <summary>本地动作的结果提示(结构化,界面翻译)。</summary>
public enum SlashNoticeKind
{
    /// <summary><c>/goal</c> 没有参数:给用法。</summary>
    GoalUsage,

    /// <summary>条件只有不可见字符。</summary>
    GoalEmpty,

    /// <summary>条件超过上限;<see cref="SlashNotice.Arg"/> = 上限。</summary>
    GoalTooLong,

    /// <summary>会话正在运行:压缩要等它停下。</summary>
    CompactBusy,

    /// <summary>边界之后还没有新的对话,没什么可压缩的。</summary>
    CompactNothing,

    /// <summary>压缩失败(模型出错、没有可用的压缩模型……)。</summary>
    CompactFailed,

    /// <summary>没有匹配的模型;<see cref="SlashNotice.Arg"/> = 用户写的名字。</summary>
    ModelNotFound,

    /// <summary>名字匹配到不止一个模型;<see cref="SlashNotice.Arg"/> = 用户写的名字。</summary>
    ModelAmbiguous,

    /// <summary>匹配到的模型不可用(供应商停用 / 没配密钥);<see cref="SlashNotice.Arg"/> = 模型名。</summary>
    ModelUnavailable,

    /// <summary>已切换模型;<see cref="SlashNotice.Arg"/> = 模型名。</summary>
    ModelSwitched,

    /// <summary>设备没连上 / 这条命令在这里用不了。</summary>
    Unavailable,
}

public sealed record SlashNotice(SlashNoticeKind Kind, string? Arg = null);

/// <summary>内置本地动作的清单与解析(纯函数)。</summary>
public static class ComposerSlash
{
    /// <summary>菜单里的顺序。</summary>
    public static IReadOnlyList<(SlashAction Action, string Name)> Builtins { get; } =
    [
        (SlashAction.Goal, "goal"),
        (SlashAction.Compact, "compact"),
        (SlashAction.Model, "model"),
        (SlashAction.Clear, "clear"),
    ];

    public static string NameOf(SlashAction action) => Builtins.First(b => b.Action == action).Name;

    /// <summary>
    /// 草稿是不是一次本地动作调用:以 <c>/名字</c> 开头、名字后面是空白或结尾(<c>/goals</c> 不是 <c>/goal</c>)。名字不分大小写。不是就返回 null(交给命令展开 / 普通发送)。
    /// </summary>
    public static SlashInvocation? Parse(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('/'))
        {
            return null;
        }

        var end = 1;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]))
        {
            end++;
        }

        var name = trimmed[1..end];
        foreach (var (action, builtin) in Builtins)
        {
            if (string.Equals(name, builtin, StringComparison.OrdinalIgnoreCase))
            {
                return new SlashInvocation(action, trimmed[end..].Trim());
            }
        }

        return null;
    }

    /// <summary>
    /// <c>/model &lt;名称&gt;</c> 匹配候选:先精确(显示名、别名、<c>供应商/别名</c>,不分大小写),再唯一的子串匹配。
    /// 精确命中多条时优先可用的那条;子串命中多条 = 有歧义(返回 <c>Count &gt; 1</c>,不替用户猜)。
    /// </summary>
    public static (ModelChoice? Match, int Count) MatchModel(IReadOnlyList<ModelChoice> choices, string query)
    {
        var q = query.Trim();
        if (q.Length == 0)
        {
            return (null, 0);
        }

        var exact = choices.Where(c =>
            string.Equals(c.DisplayName, q, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Alias, q, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Key, q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0)
        {
            var available = exact.Where(static c => c.IsAvailable).ToList();
            return available.Count == 1 ? (available[0], 1) : exact.Count == 1 ? (exact[0], 1) : (null, exact.Count);
        }

        var partial = choices.Where(c =>
            c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.Alias.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count > 1 && partial.Where(static c => c.IsAvailable).ToList() is [var only])
        {
            return (only, 1);
        }

        return partial.Count == 1 ? (partial[0], 1) : (null, partial.Count);
    }
}
