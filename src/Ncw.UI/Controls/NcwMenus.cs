using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;
using TKCode.Arc;

namespace Ncw.UI.Controls;

/// <summary>
/// 右键菜单(Arc ContextMenu + MenuSurface)。业务视图只描述条目,不直接构造 Arc 类型(决策 D5)。
/// </summary>
public static class NcwMenus
{
    /// <summary>一行菜单。带 <see cref="Children"/> 的行打开子菜单;破坏性操作放最后并标红。</summary>
    public sealed record Item(string Header, ICommand? Command, object? Parameter = null, bool IsDestructive = false, bool SeparatorBefore = false)
    {
        /// <summary>灰显、不可点(例如停用的模型:显示出来,但标成不可用)。</summary>
        public bool IsDisabled { get; init; }

        /// <summary>前面打勾:当前选中项。</summary>
        public bool IsChecked { get; init; }

        public Geometry? Glyph { get; init; }

        /// <summary>图标位里的小控件(颜色色块),优先于 <see cref="Glyph"/>。</summary>
        public Control? Icon { get; init; }

        public IReadOnlyList<Item>? Children { get; init; }
    }

    public static void Attach(Control target, IEnumerable<Item> items)
    {
        var surface = new MenuSurface();
        foreach (var item in items)
        {
            surface.Items.Add(Build(item));
        }

        TKCode.Arc.ContextMenu.SetMenu(target, surface);
    }

    /// <summary>下拉菜单(<see cref="NcwDropdownMenu"/>)的条目:每次数据变了重新填一遍。</summary>
    public static void Populate(NcwDropdownMenu menu, IEnumerable<Item> items) =>
        menu.ItemsSource = items.Select(Build).ToList();

    private static MenuEntry Build(Item item)
    {
        var row = new MenuEntry
        {
            Header = item.Header,
            IsDestructive = item.IsDestructive,
            HasSeparatorBefore = item.SeparatorBefore,
            IsEnabled = !item.IsDisabled,
        };

        if (item.IsChecked)
        {
            // 当前选中项:单选行,前面显示圆点(图标位没有图标时)
            row.ToggleType = MenuEntryToggleType.Radio;
            row.IsChecked = true;
        }

        if (item.Children is { Count: > 0 } children)
        {
            foreach (var child in children)
            {
                row.Items.Add(Build(child));
            }
        }
        else
        {
            row.Command = item.Command;
            row.CommandParameter = item.Parameter;
        }

        if (item.Icon is { } icon)
        {
            row.Icon = icon;
        }
        else if (item.Glyph is { } glyph)
        {
            row.Icon = glyph;
        }

        return row;
    }
}
