using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ncw.Client.Shortcuts;
using Ncw.Client.ViewModels;
using Ncw.UI.Localization;

namespace Ncw.UI.Controls;

/// <summary>快捷键的显示:按平台画成 ⌘⇧K 或 Ctrl+Shift+K(docs/design/03 §11)。</summary>
public static class ShortcutText
{
    /// <summary>按 macOS 的样子画(测试可以改)。</summary>
    public static bool IsMac { get; set; } = OperatingSystem.IsMacOS();

    private static IReadOnlyDictionary<AppCommand, string?>? _current;

    /// <summary>窗口上的快捷键路由每次重建手势表时更新(按钮上的快捷键提示据此跟着用户的修改走)。</summary>
    internal static void SetCurrent(IReadOnlyDictionary<AppCommand, string?> current)
    {
        _current = current;
        CurrentChanged?.Invoke();
    }

    /// <summary>生效的手势表换了(用户在设置里改了快捷键):常驻的快捷键提示(侧边栏)据此刷新。</summary>
    public static event Action? CurrentChanged;

    /// <summary>一个意图此刻生效的手势;路由还没建起来时按默认值。</summary>
    public static string? CurrentGesture(AppCommand command) =>
        _current is { } current ? current.GetValueOrDefault(command)
            : ShortcutCatalog.Resolve(null, IsMac).FirstOrDefault(b => b.Command == command)?.Gesture;

    public static string Format(string? gesture)
    {
        if (string.IsNullOrEmpty(gesture))
        {
            return "";
        }

        var parts = ShortcutCatalog.Normalize(gesture).Split('+');
        if (IsMac)
        {
            // macOS 的惯例顺序:⌃ ⌥ ⇧ ⌘
            var mods = "";
            if (parts.Contains("Ctrl")) mods += "⌃";
            if (parts.Contains("Alt")) mods += "⌥";
            if (parts.Contains("Shift")) mods += "⇧";
            if (parts.Contains("Meta")) mods += "⌘";
            return mods + KeyName(parts[^1]);
        }

        return string.Join("+", parts.Select(p => p switch
        {
            "Meta" => "Win",
            "Ctrl" or "Shift" or "Alt" => p,
            _ => KeyName(p),
        }));
    }

    private static string KeyName(string key) => key switch
    {
        "OemPeriod" => ".",
        "OemComma" => ",",
        "OemMinus" => "-",
        "OemPlus" => "=",
        "OemQuestion" => "/",
        "OemSemicolon" => ";",
        "OemOpenBrackets" => "[",
        "OemCloseBrackets" => "]",
        "OemQuotes" => "'",
        "OemPipe" => "\\",
        "OemTilde" => "`",
        "Enter" or "Return" => IsMac ? "↵" : "Enter",
        "Back" => IsMac ? "⌫" : "Backspace",
        "Delete" => IsMac ? "⌦" : "Del",
        "Escape" => "Esc",
        "Tab" => IsMac ? "⇥" : "Tab",
        "Space" => IsMac ? "␣" : "Space",
        "Up" => "↑",
        "Down" => "↓",
        "Left" => "←",
        "Right" => "→",
        _ when key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) => key[1..],
        _ when key.StartsWith("NumPad", StringComparison.Ordinal) && key.Length == 7 => key[6..],
        _ => key,
    };

    /// <summary>一个意图的当前语言标题(命令面板、快捷键页共用)。</summary>
    public static string Title(AppCommand command) =>
        ShortcutCatalog.ProjectIndex(command) is { } n
            ? NcwTexts.Format(NcwTextKind.CmdSelectProject, n)
            : KeyText.Get("Cmd", command.ToString());

    /// <summary>录制时把一次按键翻成手势文本;只按了修饰键返回 null(还在等主键)。</summary>
    public static string? Capture(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None)
        {
            return null;
        }

        var parts = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Meta");
        parts.Add(e.Key.ToString());
        return string.Join('+', parts);
    }
}

/// <summary>
/// 桌面快捷键的路由:在窗口(TopLevel)上挂一个隧道阶段的 KeyDown,先于输入框拿到按键;按偏好里生效的手势表匹配,
/// 匹配上就交给 <see cref="ShellViewModel.Execute"/>。意图这次什么也没做(没有运行可停、没有待批的审批)时不吞掉按键,让它照常往下传。
/// 手势表在偏好变化时重建;录制快捷键期间(<see cref="IsRecording"/>)不触发任何快捷键。
/// </summary>
public sealed class NcwShortcutRouter : IDisposable
{
    private readonly TopLevel _top;
    private readonly ShellViewModel _vm;
    private List<(KeyGesture Gesture, AppCommand Command)> _table = [];

    public NcwShortcutRouter(TopLevel top, ShellViewModel vm)
    {
        _top = top;
        _vm = vm;
        Rebuild();
        vm.Stores.Prefs.Changed += Rebuild;
        top.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>设置页正在录制快捷键。</summary>
    public static bool IsRecording { get; set; }

    public void Dispose()
    {
        _vm.Stores.Prefs.Changed -= Rebuild;
        _top.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
    }

    private void Rebuild()
    {
        var table = new List<(KeyGesture, AppCommand)>();
        var bindings = ShortcutCatalog.Resolve(_vm.Stores.Prefs.State.ShortcutOverrides, ShortcutText.IsMac);
        ShortcutText.SetCurrent(bindings.ToDictionary(b => b.Command, b => b.Gesture));
        foreach (var binding in bindings)
        {
            if (binding.Gesture is { } text)
            {
                try
                {
                    table.Add((KeyGesture.Parse(text), binding.Command));
                }
                catch (Exception)
                {
                    // 偏好文件里写坏的手势:这一条没有快捷键,别的照常
                }
            }
        }

        _table = table;
    }

    /// <summary>测试用:按手势表处理一次按键,返回是否吞掉了它。</summary>
    public bool Handle(Key key, KeyModifiers modifiers)
    {
        if (IsRecording)
        {
            return false;
        }

        foreach (var (gesture, command) in _table)
        {
            if (gesture.Key == key && gesture.KeyModifiers == modifiers)
            {
                return _vm.Execute(command);
            }
        }

        return false;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Handled && Handle(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }
}
