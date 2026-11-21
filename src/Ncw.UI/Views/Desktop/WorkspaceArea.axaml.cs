using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Shortcuts;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Ncw.UI.Views.Desktop;

public partial class WorkspaceArea : UserControl
{
    private ShellViewModel? _shell;
    private ProjectPanelViewModel? _panel;

    /// <summary>上一次界面上显示着的标签 key(纯 UI 状态,只用来认「刚出现的标签」)。</summary>
    private HashSet<string> _shownTabs = new(StringComparer.Ordinal);

    private bool _tabsSnapshotPending;

    public WorkspaceArea()
    {
        InitializeComponent();
        TabStrip.ContainerPrepared += OnTabPrepared;
    }

    private void OnTabDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: WorkspaceTabViewModel { File: { } file } })
        {
            file.PinCommand.Execute(null);
            e.Handled = true;
        }
        else if (sender is Control { DataContext: WorkspaceTabViewModel { SessionId: { } sessionId } } && _panel?.FindSessionItem(sessionId) is { } session)
        {
            session.RenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 新标签淡入(docs/design/03 §9.4)。标签集合每次变化都整批重建(<c>WorkspaceViewModel.Refresh</c>),所以不能用 ArriveOnAttach(每次都会整排闪);
    /// 这里按 key 认「真正新出现的」:上一批里没有、且和上一批有交集(同一个项目;换项目 / 首次显示是整批换掉,不播放),一次最多一个(新建会话、打开文件)。
    /// </summary>
    private void OnTabPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container.DataContext is not WorkspaceTabViewModel tab)
        {
            return;
        }

        AttachTabMenu(e.Container, tab);
        if (IsNewTab(tab.Key))
        {
            NcwMotion.Arrive(e.Container, NcwArrival.Fade, tier: NcwMotionTier.Fast);
        }
        else
        {
            NcwMotion.Rest(e.Container);
        }

        if (!_tabsSnapshotPending)
        {
            // 这一批 Clear + Add 都处理完后再记下「现在显示着的」
            _tabsSnapshotPending = true;
            Dispatcher.UIThread.Post(SnapshotTabs, DispatcherPriority.Background);
        }
    }

    private void AttachTabMenu(Control container, WorkspaceTabViewModel tab)
    {
        if (_shell is not { } shell)
        {
            return;
        }

        var target = container.GetVisualDescendants().OfType<PlainButton>().FirstOrDefault() ?? container;
        List<NcwMenus.Item> items = [];
        if (tab.SessionId is { } sessionId && shell.Panel.FindSessionItem(sessionId) is { } session)
        {
            var pinLabel = NcwTexts.Get(session.IsFavorite ? NcwTextKind.UnpinSession : NcwTextKind.PinSession);
            items.Add(new(NcwTexts.Get(NcwTextKind.Rename), session.RenameCommand) { Glyph = ArcIcons.Pencil });
            items.Add(new(pinLabel, session.ToggleFavoriteCommand) { Glyph = Lucide.Geometry(session.IsFavorite ? LucideIconKind.PinOff : LucideIconKind.Pin) });
            items.Add(new(NcwTexts.Get(session.IsArchived ? NcwTextKind.Unarchive : NcwTextKind.Archive), session.ToggleArchivedCommand) { Glyph = ArcIcons.Archive });
            items.Add(new(NcwTexts.Get(NcwTextKind.CloseTab), shell.Workspace.CloseCommand, tab, SeparatorBefore: true) { Glyph = ArcIcons.X });
            items.Add(new(NcwTexts.Get(NcwTextKind.DeleteSession), session.DeleteCommand, IsDestructive: true, SeparatorBefore: true) { Glyph = ArcIcons.Trash2 });
        }
        else if (tab.File is { } file)
        {
            items.Add(new(NcwTexts.Get(NcwTextKind.FilePinTab), file.PinCommand) { IsDisabled = !file.IsPreview, Glyph = Lucide.Geometry(LucideIconKind.Pin) });
            items.Add(new(NcwTexts.Get(NcwTextKind.CloseTab), shell.Workspace.CloseCommand, tab, SeparatorBefore: true) { Glyph = ArcIcons.X });
        }

        if (items.Count > 0)
        {
            NcwMenus.Attach(target, items);
        }
    }

    private bool IsNewTab(string key)
    {
        if (_shownTabs.Count == 0 || _shownTabs.Contains(key) || _shell is not { } shell)
        {
            return false;
        }

        var tabs = shell.Workspace.Tabs;
        var fresh = 0;
        var kept = false;
        foreach (var t in tabs)
        {
            if (_shownTabs.Contains(t.Key))
            {
                kept = true;
            }
            else
            {
                fresh++;
            }
        }

        return kept && fresh == 1;
    }

    private void SnapshotTabs()
    {
        _tabsSnapshotPending = false;
        _shownTabs = _shell is { } shell ? shell.Workspace.Tabs.Select(t => t.Key).ToHashSet(StringComparer.Ordinal) : new(StringComparer.Ordinal);
    }

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
        if (e.PropertyName is nameof(ProjectPanelViewModel.ActiveSession) or nameof(ProjectPanelViewModel.HasProject))
        {
            RebuildMoreMenu();
        }
    }

    /// <summary>
    /// 右上角「更多」菜单与标签栏「+」新建菜单。只描述条目:当前会话的操作用会话行的同一组命令(最终都到 SessionActions,规则 4),
    /// 全局命令走 <see cref="ShellViewModel.Execute"/>(和快捷键、命令面板同一个入口),行尾显示当前生效的快捷键。
    /// </summary>
    private void RebuildMoreMenu()
    {
        if (_shell is not { } shell)
        {
            return;
        }

        RebuildNewMenu(shell);
        List<NcwMenus.Item> items = [];
        var session = shell.Panel.ActiveSession;
        if (session is not null)
        {
            var pinLabel = NcwTexts.Get(session.IsFavorite ? NcwTextKind.UnpinSession : NcwTextKind.PinSession);
            items.Add(new(NcwTexts.Get(NcwTextKind.Rename), session.RenameCommand) { Glyph = ArcIcons.Pencil });
            items.Add(new(pinLabel, session.ToggleFavoriteCommand) { Glyph = Lucide.Geometry(session.IsFavorite ? LucideIconKind.PinOff : LucideIconKind.Pin) });
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

    private void RebuildNewMenu(ShellViewModel shell)
    {
        var hasProject = shell.Panel.HasProject;
        List<NcwMenus.Item> items =
        [
            new(NcwTexts.Get(NcwTextKind.NewSession), shell.Sidebar.NewSessionCommand)
            {
                IsDisabled = !hasProject,
                Glyph = ArcIcons.SquarePen,
                Hint = ShortcutText.CurrentGesture(AppCommand.NewSession) is { Length: > 0 } g1 ? ShortcutText.Format(g1) : null,
            },
            new(NcwTexts.Get(NcwTextKind.NewTerminal), shell.Sidebar.NewTerminalCommand)
            {
                IsDisabled = !hasProject,
                Glyph = ArcIcons.Terminal,
            },
            new(NcwTexts.Get(NcwTextKind.FilesNewFile), shell.Sidebar.NewFileCommand)
            {
                IsDisabled = !hasProject,
                Glyph = Lucide.Geometry(LucideIconKind.FilePlus),
            },
            new(NcwTexts.Get(NcwTextKind.FilesNewFolder), shell.Sidebar.NewFolderCommand)
            {
                IsDisabled = !hasProject,
                Glyph = Lucide.Geometry(LucideIconKind.FolderPlus),
            },
            new(NcwTexts.Get(NcwTextKind.ScheduledTasks), shell.Sidebar.OpenScheduledTasksCommand, SeparatorBefore: true)
            {
                Glyph = Lucide.Geometry(LucideIconKind.CalendarClock),
            },
            Global(shell, AppCommand.AddProject, ArcIcons.Folder),
        ];
        NcwMenus.Populate(NewMenu, items);
    }

    private static NcwMenus.Item Global(ShellViewModel shell, AppCommand command, Avalonia.Media.Geometry glyph, bool separatorBefore = false) =>
        new(ShortcutText.Title(command), new RelayCommand(() => shell.Execute(command)), SeparatorBefore: separatorBefore)
        {
            Glyph = glyph,
            Hint = ShortcutText.CurrentGesture(command) is { Length: > 0 } gesture ? ShortcutText.Format(gesture) : null,
        };
}
