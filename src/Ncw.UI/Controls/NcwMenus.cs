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

    private static MenuEntry Build(Item item)
    {
        var row = new MenuEntry
        {
            Header = item.Header,
            IsDestructive = item.IsDestructive,
            HasSeparatorBefore = item.SeparatorBefore,
        };

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
