using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Shortcuts;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using TKCode.Arc;

namespace Ncw.UI.Views.Desktop;

public partial class WorkspaceArea : UserControl
{
    private ShellViewModel? _shell;
    private ProjectPanelViewModel? _panel;

    public WorkspaceArea() => InitializeComponent();

    /// <summary>
    /// 顶部这一行左右的留白:左边让给收起后并入的侧边栏头部,右边让给右上角常驻按钮(检查器收起时)。
    /// </summary>
    public void SetTopRowInsets(double leading, double trailing) =>
        TabRow.Margin = new Thickness(8 + leading, 0, 8 + trailing, 0);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Unsubscribe();
        _shell = DataContext as ShellViewModel;
        if (VisualRoot is not null)
        {
            Subscribe();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (_shell is null || _panel is not null)
        {
            return;
        }

        _panel = _shell.Panel;
        _panel.PropertyChanged += OnPanelChanged;
        ShortcutText.CurrentChanged += RebuildMoreMenu;
        RebuildMoreMenu();
    }

    private void Unsubscribe()
    {
        if (_panel is null)
        {
            return;
        }

        _panel.PropertyChanged -= OnPanelChanged;
        ShortcutText.CurrentChanged -= RebuildMoreMenu;
        _panel = null;
    }

    private void OnPanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectPanelViewModel.ActiveSession))
        {
            RebuildMoreMenu();
        }
    }

    /// <summary>
    /// 右上角「更多」菜单。只描述条目:当前会话的操作用会话行的同一组命令(最终都到 SessionActions,规则 4),
    /// 全局命令走 <see cref="ShellViewModel.Execute"/>(和快捷键、命令面板同一个入口),行尾显示当前生效的快捷键。
    /// </summary>
    private void RebuildMoreMenu()
    {
        if (_shell is not { } shell)
        {
            return;
        }

        List<NcwMenus.Item> items = [];
        var session = shell.Panel.ActiveSession;
        if (session is not null)
        {
            items.Add(new(NcwTexts.Get(NcwTextKind.Rename), session.RenameCommand) { Glyph = ArcIcons.Pencil });
            items.Add(new(NcwTexts.Get(session.IsFavorite ? NcwTextKind.Unfavorite : NcwTextKind.Favorite), session.ToggleFavoriteCommand) { Glyph = ArcIcons.Star });
            items.Add(new(NcwTexts.Get(session.IsArchived ? NcwTextKind.Unarchive : NcwTextKind.Archive), session.ToggleArchivedCommand) { Glyph = ArcIcons.Archive });
        }

        items.Add(Global(shell, AppCommand.NewSession, ArcIcons.Plus, separatorBefore: session is not null));
        items.Add(Global(shell, AppCommand.CommandPalette, ArcIcons.Command));
        items.Add(Global(shell, AppCommand.OpenSettings, ArcIcons.Settings));
        if (session is not null)
        {
            items.Add(new(NcwTexts.Get(NcwTextKind.DeleteSession), session.DeleteCommand, IsDestructive: true, SeparatorBefore: true) { Glyph = ArcIcons.Trash2 });
        }

        NcwMenus.Populate(MoreMenu, items);
    }

    private static NcwMenus.Item Global(ShellViewModel shell, AppCommand command, Avalonia.Media.Geometry glyph, bool separatorBefore = false) =>
        new(ShortcutText.Title(command), new RelayCommand(() => shell.Execute(command)), SeparatorBefore: separatorBefore)
        {
            Glyph = glyph,
            Hint = ShortcutText.CurrentGesture(command) is { Length: > 0 } gesture ? ShortcutText.Format(gesture) : null,
        };
}
