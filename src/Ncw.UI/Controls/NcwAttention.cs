using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Ncw.Client.Services;
using Ncw.Client.Shortcuts;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.UI.Localization;

namespace Ncw.UI.Controls;

/// <summary>
/// 操作系统给的提醒能力(桌面宿主实现,docs/design/03 §11.2):系统通知、应用图标角标、请求注意(Dock 弹跳 / 任务栏闪烁)。
/// 做不到的平台如实报告 <see cref="SupportsNotifications"/> / <see cref="SupportsBadge"/> 为 false,设置页据此说明。
/// </summary>
public interface INativeAttention
{
    bool SupportsNotifications { get; }

    bool SupportsBadge { get; }

    /// <summary>发一条系统通知;<paramref name="key"/> 在用户点它时经 <see cref="Activated"/> 回来。</summary>
    void Notify(string title, string body, string key);

    void SetBadge(int count);

    /// <summary>窗口不在前台时请求注意(macOS Dock 弹跳一次、Windows 任务栏闪烁)。</summary>
    void RequestAttention(Window window);

    /// <summary>用户点了某条通知(参数是通知的 key)。在 UI 线程上触发。</summary>
    event Action<string>? Activated;
}

/// <summary>
/// 桌面的提醒出口:把 Client 给的结构化提醒翻成当前语言的文字交给 <see cref="INativeAttention"/>,
/// 并持有托盘(菜单栏)图标——显示待处理 / 运行中的数量,菜单里能打开窗口、转到下一项待处理、新建会话、打开设置、退出。
/// <para>
/// 「关窗后继续在后台运行」跟托盘开关走(<c>AttentionPrefs.Tray</c>):开着时点窗口的关闭按钮只是把窗口藏起来,本机 Agent 照常运行,
/// 从托盘菜单 / Dock 图标再打开;真正退出要从托盘菜单或 ⌘Q。关着时关窗就退出(原来的行为)。
/// </para>
/// </summary>
public sealed class NcwAttention : IAttentionSink, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly Window _window;
    private readonly IClassicDesktopStyleApplicationLifetime? _desktop;
    private readonly INativeAttention? _native;
    private readonly NativeTrayFactory? _nativeTrayFactory;
    private TrayIcon? _tray;
    private INativeTray? _nativeTray;
    private string _trayToolTip = "";
    private NativeMenuItem? _pendingItem;
    private NativeMenuItem? _runningItem;
    private NativeMenuItem? _unknownItem;
    private AttentionSummary _summary = new(0, 0, false);

    public NcwAttention(ShellViewModel shell, Window window, IClassicDesktopStyleApplicationLifetime? desktop, INativeAttention? native,
        NativeTrayFactory? nativeTray = null)
    {
        _shell = shell;
        _window = window;
        _desktop = desktop;
        _native = native;
        _nativeTrayFactory = nativeTray;
        shell.Settings.Notifications.IsSystemNotificationSupported = native?.SupportsNotifications ?? false;
        shell.Settings.Notifications.IsBadgeSupported = native?.SupportsBadge ?? false;
        shell.IsWindowActive = () => _window.IsActive && _window.IsVisible;
        shell.AttentionSink = this;
        if (native is not null)
        {
            native.Activated += OnNotificationActivated;
        }

        window.Closing += OnWindowClosing;
        shell.Stores.Prefs.Changed += ApplyTrayPreference;
        ApplyTrayPreference();
    }

    /// <summary>托盘图标此刻是否存在(测试用)。</summary>
    public bool HasTray => _tray is not null || _nativeTray is not null;

    /// <summary>最近一次托盘提示文字(测试用)。</summary>
    public string TrayToolTip => HasTray ? _trayToolTip : "";

    public void Dispose()
    {
        _window.Closing -= OnWindowClosing;
        _shell.Stores.Prefs.Changed -= ApplyTrayPreference;
        if (_native is not null)
        {
            _native.Activated -= OnNotificationActivated;
        }

        RemoveTray();
    }

    // ───────────────────────── IAttentionSink ─────────────────────────

    public void Notify(AttentionNotice notice)
    {
        var (title, body) = Describe(notice);
        _native?.Notify(title, body, notice.Key);
        if (!_window.IsActive && notice.Kind is not (InboxKind.RunCompleted))
        {
            _native?.RequestAttention(_window);
        }
    }

    public void SetBadge(int count) => _native?.SetBadge(count);

    public void SetSummary(AttentionSummary summary)
    {
        _summary = summary;
        UpdateTrayTexts();
    }

    /// <summary>通知的标题和正文(当前语言)。标题说发生了什么,正文说在哪(项目 · 会话)和什么(工具摘要 / 计划第一行 / 问题)。</summary>
    public static (string Title, string Body) Describe(AttentionNotice notice)
    {
        var title = NcwTexts.Get(notice.Kind switch
        {
            InboxKind.ToolApproval => notice.Dangerous ? NcwTextKind.AttnToolApprovalDangerous : NcwTextKind.AttnToolApproval,
            InboxKind.Question => NcwTextKind.AttnQuestion,
            InboxKind.PlanApproval => NcwTextKind.AttnPlan,
            InboxKind.GoalProposal => NcwTextKind.AttnGoal,
            InboxKind.WorkflowApproval => NcwTextKind.AttnWorkflow,
            InboxKind.DeviceApproval => NcwTextKind.AttnDevice,
            InboxKind.RunFailed => NcwTextKind.AttnFailed,
            _ => NcwTextKind.AttnCompleted,
        });

        var where = string.Join(" · ", new[] { notice.ProjectName, notice.SessionTitle }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var what = notice.Kind == InboxKind.ToolApproval
            ? string.Join(" ", new[] { notice.ToolName, notice.Target }.Where(s => !string.IsNullOrWhiteSpace(s)))
            : notice.Detail;
        var body = string.Join("\n", new[] { where, what }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return (title, body);
    }

    private void OnNotificationActivated(string key)
    {
        ShowWindow();
        if (_shell.Inbox.Current.Entries.FirstOrDefault(e => e.Key == key) is { } entry)
        {
            _shell.OpenAttention(entry.DeviceId, entry.WorkspaceId, entry.SessionId, entry.Key);
        }
    }

    // ───────────────────────── 窗口与托盘 ─────────────────────────

    /// <summary>把主窗口带回前台(托盘、Dock 图标、通知点击)。</summary>
    public void ShowWindow()
    {
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    /// <summary>托盘开着时,用户点关闭按钮只是藏起窗口(本机 Agent 照常运行);应用退出、系统关机这类关闭照常进行。</summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (HasTray && e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
        {
            e.Cancel = true;
            _window.Hide();
        }
    }

    private void ApplyTrayPreference()
    {
        var wanted = _shell.Stores.Prefs.State.AttentionOrDefault.Tray && _desktop is not null;
        if (wanted && !HasTray)
        {
            CreateTray();
        }
        else if (!wanted && HasTray)
        {
            RemoveTray();
            if (!_window.IsVisible)
            {
                ShowWindow(); // 关掉托盘时窗口不能还藏着(否则就找不回来了)
            }
        }

        if (_desktop is not null)
        {
            _desktop.ShutdownMode = HasTray ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnLastWindowClose;
        }
    }

    private void CreateTray()
    {
        _pendingItem = new NativeMenuItem { Command = new Relay(() => { ShowWindow(); _shell.Execute(AppCommand.NextAttention); }) };
        _runningItem = new NativeMenuItem { IsEnabled = false };
        _unknownItem = new NativeMenuItem { IsEnabled = false, Header = NcwTexts.Get(NcwTextKind.TrayUnknown) };
        var menu = new NativeMenu
        {
            new NativeMenuItem(NcwTexts.Get(NcwTextKind.TrayOpen)) { Command = new Relay(ShowWindow) },
            new NativeMenuItemSeparator(),
            _pendingItem,
            _runningItem,
            _unknownItem,
            new NativeMenuItemSeparator(),
            new NativeMenuItem(NcwTexts.Get(NcwTextKind.TrayNewSession)) { Command = new Relay(() => { ShowWindow(); _shell.Execute(AppCommand.NewSession); }) },
            new NativeMenuItem(NcwTexts.Get(NcwTextKind.TraySettings)) { Command = new Relay(() => { ShowWindow(); _shell.OpenSettingsSection(SettingsSection.Notifications); }) },
            new NativeMenuItemSeparator(),
            new NativeMenuItem(NcwTexts.Get(NcwTextKind.TrayQuit)) { Command = new Relay(() => _desktop?.Shutdown()) },
        };

        // 宿主给了系统托盘(Windows)就用它:Avalonia 在 Windows 上自己画的托盘菜单真机右键弹不出来(issue #200)
        try
        {
            _nativeTray = _nativeTrayFactory?.Invoke("NextCoWork", () => NativeTrayMenu.Snapshot(menu), ShowWindow);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _nativeTray = null; // 建不起来就退回 Avalonia 的托盘
        }

        if (_nativeTray is not null)
        {
            UpdateTrayTexts();
            return;
        }

        // 提示文字必须在挂上之前就有:macOS 的原生托盘在挂上时直接读它,null 会抛(真实启动冒烟里发现)
        _tray = new TrayIcon { Menu = menu, Command = new Relay(ShowWindow), ToolTipText = "NextCoWork" };
        UpdateTrayTexts();
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Ncw.UI/Assets/nextcowork-mark.png"));
            _tray.Icon = new WindowIcon(stream);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or FileNotFoundException)
        {
            // 没有图标也照样能用菜单
        }

        try
        {
            if (Application.Current is { } app)
            {
                var icons = TrayIcon.GetIcons(app) ?? [];
                icons.Add(_tray);
                TrayIcon.SetIcons(app, icons);
            }

            _tray.IsVisible = true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            // 这个平台的托盘起不来:没有托盘(关窗就退出),但应用照常用,不能因为托盘把启动搞崩
            _tray.Dispose();
            _tray = null;
        }
    }

    private void RemoveTray()
    {
        if (_nativeTray is not null)
        {
            _nativeTray.Dispose();
            _nativeTray = null;
        }

        if (_tray is null)
        {
            return;
        }

        _tray.IsVisible = false;
        if (Application.Current is { } app && TrayIcon.GetIcons(app) is { } icons)
        {
            icons.Remove(_tray);
        }

        _tray.Dispose();
        _tray = null;
    }

    private void UpdateTrayTexts()
    {
        if (!HasTray)
        {
            return;
        }

        var s = _summary;
        _pendingItem!.Header = s.Pending > 0 ? NcwTexts.Format(NcwTextKind.TrayPending, s.Pending) : NcwTexts.Get(NcwTextKind.TrayNothingPending);
        _pendingItem.IsEnabled = s.Pending > 0;
        _runningItem!.Header = NcwTexts.Format(NcwTextKind.TrayRunning, s.Running);
        _runningItem.IsVisible = s.Running > 0;
        _unknownItem!.IsVisible = s.Unknown;
        _trayToolTip = NcwTexts.Format(NcwTextKind.TrayTooltip, s.Pending, s.Running);
        if (_tray is not null)
        {
            _tray.ToolTipText = _trayToolTip;
        }

        _nativeTray?.SetToolTip(_trayToolTip);
    }

    private sealed class Relay(Action action) : System.Windows.Input.ICommand
    {
#pragma warning disable CS0067 // 命令永远可用
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Dispatcher.UIThread.Post(action);
    }
}
