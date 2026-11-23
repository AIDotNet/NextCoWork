using System.Collections.Immutable;

namespace Ncw.Client.Shortcuts;

/// <summary>
/// 桌面上可以用快捷键 / 命令面板触发的用户意图(docs/design/03 §11)。每个成员对应 <see cref="ViewModels.ShellViewModel.Execute"/> 里的一个分支,
/// 分支只调已有的 Actions / 命令(AGENTS.md §3 规则 4:同一个意图只有一个入口,快捷键和命令面板都走这里)。
/// 声明顺序就是「快捷键」设置页和命令面板里的顺序。成员名是持久化在偏好文件里的键,不要改名。
/// </summary>
public enum AppCommand
{
    CommandPalette,
    NewSession,
    SearchProject,
    CloseTab,
    StopRun,
    ApproveFirst,
    NextAttention,
    FocusComposer,
    ToggleSidebar,
    ToggleInspector,
    OpenSettings,
    AddProject,
    RecentProject,
    OpenReview,
    OpenGit,
    OpenTerminal,
    SelectProject1,
    SelectProject2,
    SelectProject3,
    SelectProject4,
    SelectProject5,
    SelectProject6,
    SelectProject7,
    SelectProject8,
    SelectProject9,

    /// <summary>退出应用(让本机 Agent 收尾)。托盘开着时关窗只是藏起窗口,Windows / Linux 上需要一个不依赖托盘菜单的退出入口(issue #200)。</summary>
    Quit,
}

/// <summary>一条生效的快捷键:意图 + 按键组合(Avalonia <c>KeyGesture</c> 的文本写法,比如 <c>Meta+K</c>)。<see cref="IsCustom"/> = 用户改过。</summary>
public sealed record ShortcutBinding(AppCommand Command, string? Gesture, bool IsCustom);

/// <summary>
/// 快捷键表:默认值、用户改过的覆盖、平台修饰键、冲突检测。全是纯函数,不认识 Avalonia(手势是字符串,界面层负责解析和按平台画成 ⌘ ⇧ ⌥ ⌃)。
/// <para>默认值里的 <c>Mod</c> 在 macOS 上是 ⌘(<c>Meta</c>),其他平台是 <c>Ctrl</c>。覆盖里存的是解析后的真实手势;空字符串 = 用户把它清空了(没有快捷键)。</para>
/// </summary>
public static class ShortcutCatalog
{
    /// <summary>默认手势。没有列出的意图默认没有快捷键(只能从命令面板触发)。</summary>
    public static readonly ImmutableDictionary<AppCommand, string> Defaults = new Dictionary<AppCommand, string>
    {
        [AppCommand.CommandPalette] = "Mod+K",
        [AppCommand.NewSession] = "Mod+N",
        [AppCommand.SearchProject] = "Mod+P",
        [AppCommand.CloseTab] = "Mod+W",
        [AppCommand.StopRun] = "Mod+OemPeriod",
        [AppCommand.ApproveFirst] = "Mod+Enter",
        [AppCommand.NextAttention] = "Mod+J",
        [AppCommand.FocusComposer] = "Mod+L",
        [AppCommand.ToggleSidebar] = "Mod+B",
        [AppCommand.ToggleInspector] = "Mod+Alt+I",
        [AppCommand.OpenSettings] = "Mod+OemComma",
        [AppCommand.AddProject] = "Mod+Shift+O",
        [AppCommand.RecentProject] = "Ctrl+Tab",
        [AppCommand.OpenReview] = "Mod+Shift+R",
        [AppCommand.OpenGit] = "Mod+Shift+G",
        [AppCommand.SelectProject1] = "Mod+D1",
        [AppCommand.SelectProject2] = "Mod+D2",
        [AppCommand.SelectProject3] = "Mod+D3",
        [AppCommand.SelectProject4] = "Mod+D4",
        [AppCommand.SelectProject5] = "Mod+D5",
        [AppCommand.SelectProject6] = "Mod+D6",
        [AppCommand.SelectProject7] = "Mod+D7",
        [AppCommand.SelectProject8] = "Mod+D8",
        [AppCommand.SelectProject9] = "Mod+D9",
        [AppCommand.Quit] = "Mod+Q",
    }.ToImmutableDictionary();

    /// <summary>命令面板里不列出的意图(打开面板自己、⌘P 也是打开面板、按序号选项目——面板里直接列项目)。</summary>
    public static bool ShowsInPalette(AppCommand command) =>
        command is not (AppCommand.CommandPalette or AppCommand.SearchProject or >= AppCommand.SelectProject1 and <= AppCommand.SelectProject9);

    /// <summary><c>SelectProjectN</c> 的 N(1–9);其他意图为 null。</summary>
    public static int? ProjectIndex(AppCommand command) =>
        command is >= AppCommand.SelectProject1 and <= AppCommand.SelectProject9 ? command - AppCommand.SelectProject1 + 1 : null;

    /// <summary>把默认值里的 <c>Mod</c> 换成平台的修饰键。</summary>
    public static string Platform(string gesture, bool mac) =>
        string.Join('+', gesture.Split('+').Select(p => p == "Mod" ? (mac ? "Meta" : "Ctrl") : p));

    /// <summary>
    /// 所有意图的生效快捷键(按 <see cref="AppCommand"/> 的声明顺序)。覆盖优先;覆盖为空字符串 = 没有快捷键;
    /// 一个手势被用户分给了别的意图,默认值里同手势的那一条让出(用户的选择压过默认)。
    /// </summary>
    public static ImmutableArray<ShortcutBinding> Resolve(IReadOnlyDictionary<AppCommand, string>? overrides, bool mac)
    {
        overrides ??= ImmutableDictionary<AppCommand, string>.Empty;
        var custom = overrides.Where(kv => kv.Value.Length > 0).Select(kv => Normalize(kv.Value)).ToHashSet(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<ShortcutBinding>();
        foreach (var command in Enum.GetValues<AppCommand>())
        {
            if (overrides.TryGetValue(command, out var mine))
            {
                result.Add(new ShortcutBinding(command, mine.Length == 0 ? null : Normalize(mine), IsCustom: true));
                continue;
            }

            var gesture = Defaults.TryGetValue(command, out var d) ? Normalize(Platform(d, mac)) : null;
            if (gesture is not null && custom.Contains(gesture))
            {
                gesture = null; // 被用户分给了别的意图
            }

            result.Add(new ShortcutBinding(command, gesture, IsCustom: false));
        }

        return result.ToImmutable();
    }

    /// <summary>把 <paramref name="gesture"/> 分给 <paramref name="command"/> 后会和哪个意图冲突(同一个手势);没有冲突为 null。</summary>
    public static AppCommand? ConflictOf(ImmutableArray<ShortcutBinding> bindings, AppCommand command, string gesture)
    {
        var normalized = Normalize(gesture);
        foreach (var binding in bindings)
        {
            if (binding.Command != command && binding.Gesture == normalized)
            {
                return binding.Command;
            }
        }

        return null;
    }

    /// <summary>
    /// 给 <paramref name="command"/> 设一个新手势:冲突的那一条被清空(用户明确要这个组合);<paramref name="gesture"/> 为 null = 恢复默认,空字符串 = 不要快捷键。
    /// 设成和默认值一样的手势时不留覆盖。
    /// </summary>
    public static ImmutableDictionary<AppCommand, string> Assign(ImmutableDictionary<AppCommand, string> overrides, AppCommand command, string? gesture, bool mac)
    {
        if (gesture is null)
        {
            return overrides.Remove(command);
        }

        var normalized = gesture.Length == 0 ? "" : Normalize(gesture);
        var bindings = Resolve(overrides, mac);
        if (normalized.Length > 0 && ConflictOf(bindings, command, normalized) is { } other)
        {
            overrides = overrides.SetItem(other, "");
        }

        var isDefault = Defaults.TryGetValue(command, out var d) && Normalize(Platform(d, mac)) == normalized;
        return isDefault ? overrides.Remove(command) : overrides.SetItem(command, normalized);
    }

    /// <summary>
    /// 手势的规范写法:修饰键按 Ctrl、Shift、Alt、Meta 排在前面,按键在最后,各段去空白。
    /// 同一个组合不管用户怎么录、谁先按,比较时都是同一个字符串。
    /// </summary>
    public static string Normalize(string gesture)
    {
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return "";
        }

        var modifiers = new List<string>();
        string? key = null;
        foreach (var part in parts)
        {
            var canonical = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => "Ctrl",
                "shift" => "Shift",
                "alt" or "option" => "Alt",
                "meta" or "cmd" or "command" or "win" => "Meta",
                _ => null,
            };
            if (canonical is null)
            {
                key = char.ToUpperInvariant(part[0]) + part[1..];
            }
            else if (!modifiers.Contains(canonical))
            {
                modifiers.Add(canonical);
            }
        }

        string[] order = ["Ctrl", "Shift", "Alt", "Meta"];
        var sorted = order.Where(modifiers.Contains).ToList();
        if (key is not null)
        {
            sorted.Add(key);
        }

        return string.Join('+', sorted);
    }

    /// <summary>一个手势能不能当快捷键用:必须有按键,而且除了 F1–F12 / Esc 之外要带 Ctrl、Alt 或 Meta(只带 Shift 的组合会吃掉正常输入)。</summary>
    public static bool IsUsable(string gesture)
    {
        var parts = Normalize(gesture).Split('+');
        var key = parts[^1];
        if (key is "Ctrl" or "Shift" or "Alt" or "Meta" or "")
        {
            return false;
        }

        var isFunctionKey = key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key[1..], out var n) && n is >= 1 and <= 24;
        return isFunctionKey || key == "Escape" || parts.Any(p => p is "Ctrl" or "Alt" or "Meta");
    }
}
