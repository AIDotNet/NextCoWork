using Avalonia.Controls;

namespace Ncw.UI.Controls;

/// <summary>托盘菜单的一项(交给系统托盘的快照)。分隔线的 <see cref="Text"/> 为空、<see cref="Invoke"/> 为 null。</summary>
public sealed record NativeTrayEntry(string Text, bool IsEnabled, bool IsSeparator, Action? Invoke);

/// <summary>
/// 宿主自己实现的系统托盘图标(docs/design/03 §11.2)。现在只有 Windows 用:Avalonia 在 Windows 上的托盘菜单是用自带控件画的一个窗口,
/// 在真机上右键弹不出来(issue #200),所以 Windows 改用系统的通知区域图标 + 系统弹出菜单。菜单内容仍由 <see cref="NcwAttention"/> 的同一份
/// <see cref="NativeMenu"/> 决定,每次弹出前经 <see cref="NativeTrayMenu.Snapshot"/> 取当时的快照。
/// </summary>
public interface INativeTray : IDisposable
{
    /// <summary>更新鼠标悬停时的提示文字。</summary>
    void SetToolTip(string text);
}

/// <summary>
/// 建一个系统托盘图标。<paramref name="menu"/> 在每次右键时调用,返回此刻的菜单;<paramref name="activate"/> 是左键单击。
/// 这个平台不用(或建不起来)时返回 null,调用方退回 Avalonia 的 <see cref="TrayIcon"/>。
/// </summary>
public delegate INativeTray? NativeTrayFactory(string toolTip, Func<IReadOnlyList<NativeTrayEntry>> menu, Action activate);

/// <summary>把 <see cref="NativeMenu"/> 摊平成系统托盘能直接用的条目。</summary>
public static class NativeTrayMenu
{
    /// <summary>
    /// 跳过隐藏的项;分隔线不出现在开头、结尾,也不连着出现(中间的项被隐藏时会留下相邻的分隔线)。
    /// 选中一项执行它的 <see cref="NativeMenuItem.Command"/>(带 <see cref="NativeMenuItem.CommandParameter"/>);子菜单不支持(托盘菜单没有)。
    /// </summary>
    public static IReadOnlyList<NativeTrayEntry> Snapshot(NativeMenu menu)
    {
        var entries = new List<NativeTrayEntry>();
        foreach (var item in menu.Items)
        {
            if (item is NativeMenuItemSeparator)
            {
                if (entries.Count > 0 && !entries[^1].IsSeparator)
                {
                    entries.Add(new NativeTrayEntry("", false, true, null));
                }
            }
            else if (item is NativeMenuItem { IsVisible: true } menuItem)
            {
                var command = menuItem.Command;
                var parameter = menuItem.CommandParameter;
                var enabled = menuItem.IsEnabled && (command?.CanExecute(parameter) ?? false);
                entries.Add(new NativeTrayEntry(menuItem.Header ?? "", enabled, false, command is null ? null : () => command.Execute(parameter)));
            }
        }

        if (entries.Count > 0 && entries[^1].IsSeparator)
        {
            entries.RemoveAt(entries.Count - 1);
        }

        return entries;
    }
}
