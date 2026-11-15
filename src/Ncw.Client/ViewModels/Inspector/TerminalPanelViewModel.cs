using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 终端页签:当前项目的终端会话(多标签)。元数据读 <see cref="ClientStores.Terminals"/>(镜像,key 带 deviceId;设备断开时是「未知」),
/// 输出字节不经 Store,由每个标签的 <see cref="TerminalTabViewModel.Relay"/> 交给终端控件。
/// <para>
/// 新建终端的握手(本机和远程同一条路径):<c>terminals.prepare</c> —— 本机直接得到终端;SSH 项目得到带<b>执行环境标识(设备 + 环境)</b>的意图,
/// 标签里显示批准卡片,用户批准后 <c>terminals.approve</c> 拿一次性凭据再 <c>terminals.create</c>;之后订阅 <c>terminal/&lt;id&gt;</c> 主题(<c>AttachAsync</c>)。
/// 页签切走(整个检查器页签不可见也算)就退订,回来再订阅拿回缓冲;断线重连后的游标续传在连接层(<c>TerminalWatches</c>),这里不重复实现。
/// </para>
/// </summary>
public sealed partial class TerminalPanelViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly ITerminalCommands _terminals;
    private readonly Dictionary<(string DeviceId, string TerminalId), TerminalTabViewModel> _cache = [];
    private readonly Dictionary<string, TerminalTabViewModel> _pending = new(StringComparer.Ordinal);
    private ProjectRef? _project;
    private bool _visible;
    private int _columns = 80;
    private int _rows = 24;

    public TerminalPanelViewModel(ClientStores stores, ITerminalCommands terminals)
    {
        _stores = stores;
        _terminals = terminals;
        stores.Terminals.Changed += Derive;
        stores.Workspace.Changed += Derive; // 设备连上 / 断开:标签的阶段(未知 / 活着)跟着变
    }

    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = [];

    [ObservableProperty] public partial TerminalTabViewModel? Active { get; private set; }

    [ObservableProperty] public partial bool IsCreating { get; private set; }

    [ObservableProperty] public partial Problem? Failure { get; private set; }

    public bool HasTabs => Tabs.Count > 0;

    /// <summary>选中的标签有需要在底部说明的状态(未知 / 已退出 / 启动中)。</summary>
    public bool HasStatus => Active is { IsUnknown: true } or { IsExited: true } or { IsStarting: true };

    public bool IsEmpty => Tabs.Count == 0;

    /// <summary>当前项目所在的设备已连上(创建终端要求连着)。</summary>
    public bool CanCreate => _project is { } p && IsConnected(p.DeviceId) && !IsCreating;

    public ProjectRef? Project => _project;

    private bool IsConnected(string deviceId) =>
        _stores.Workspace.State.Devices.TryGetValue(deviceId, out var d) && d.Connection == DeviceConnection.Connected;

    private string DeviceName(string deviceId) =>
        _stores.Workspace.State.Devices.TryGetValue(deviceId, out var d) ? d.Name : deviceId;

    /// <summary>换了项目:退订旧的,列出新项目里已有的终端。</summary>
    public void Bind(ProjectRef? project)
    {
        if (project == _project)
        {
            Derive();
            return;
        }

        _ = DeactivateAsync();
        _project = project;
        Active = null;
        Failure = null;
        Derive();
        if (_visible)
        {
            _ = LoadAsync();
        }
    }

    /// <summary>终端页签变成可见 / 不可见。不可见时退订输出(设备上的终端照常运行)。</summary>
    public async Task SetVisibleAsync(bool visible)
    {
        if (_visible == visible)
        {
            return;
        }

        _visible = visible;
        if (visible)
        {
            await LoadAsync().ConfigureAwait(true);
            if (Active is { } active)
            {
                await active.AttachAsync().ConfigureAwait(true);
            }
        }
        else
        {
            await DeactivateAsync().ConfigureAwait(true);
        }
    }

    /// <summary>列出当前项目里已有的终端(重新打开应用 / 别的控制端开的本机终端);结果合进 Store。</summary>
    public async Task LoadAsync()
    {
        if (_project is not { } project || !IsConnected(project.DeviceId))
        {
            return;
        }

        var result = await _terminals.ListAsync(project.DeviceId, project.WorkspaceId).ConfigureAwait(true);
        Failure = result.IsOk ? null : Problem.Of(result);
        Derive();
        if (_visible && Active is { } active)
        {
            await active.AttachAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Store 变了之后重新派生标签:镜像里这个项目的终端 + 等批准的意图。</summary>
    public void Derive()
    {
        if (_project is not { } project)
        {
            Tabs.Clear();
            Active = null;
            Raise();
            return;
        }

        var connected = IsConnected(project.DeviceId);
        var deviceName = DeviceName(project.DeviceId);
        var views = _stores.Terminals.State.InProject(project.DeviceId, project.WorkspaceId);
        var tabs = new List<TerminalTabViewModel>();
        foreach (var view in views)
        {
            var tab = Tab(project.DeviceId, view.Info.Id);
            _pending.Remove(view.Info.Id);
            tab.Intent = null;
            tab.Update(view, connected, deviceName);
            tabs.Add(tab);
        }

        foreach (var pending in _pending.Values.Where(p => !tabs.Contains(p)))
        {
            pending.Update(null, connected, deviceName);
            tabs.Add(pending);
        }

        if (!Tabs.SequenceEqual(tabs))
        {
            foreach (var removed in Tabs.Except(tabs).ToList())
            {
                _ = removed.DetachAsync();
            }

            Tabs.Clear();
            foreach (var tab in tabs)
            {
                Tabs.Add(tab);
            }
        }

        if (Active is null || !Tabs.Contains(Active))
        {
            Select(Tabs.LastOrDefault());
        }

        Raise();
    }

    private TerminalTabViewModel Tab(string deviceId, string terminalId)
    {
        if (!_cache.TryGetValue((deviceId, terminalId), out var tab))
        {
            _cache[(deviceId, terminalId)] = tab = _pending.GetValueOrDefault(terminalId) ?? new TerminalTabViewModel(_terminals, deviceId, terminalId);
        }

        return tab;
    }

    private void Select(TerminalTabViewModel? tab)
    {
        if (ReferenceEquals(tab, Active))
        {
            return;
        }

        var previous = Active;
        if (previous is not null)
        {
            previous.IsSelected = false;
        }

        if (tab is not null)
        {
            tab.IsSelected = true;
        }

        Active = tab;
        if (previous is not null)
        {
            _ = previous.DetachAsync();
        }

        if (tab is not null && _visible)
        {
            _ = tab.AttachAsync();
        }
    }

    partial void OnActiveChanged(TerminalTabViewModel? oldValue, TerminalTabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnActiveTabChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnActiveTabChanged;
        }

        OnPropertyChanged(nameof(HasStatus));
    }

    private void OnActiveTabChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalTabViewModel.Phase))
        {
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanCreate));
    }

    private async Task DeactivateAsync()
    {
        foreach (var tab in Tabs.ToList())
        {
            await tab.DetachAsync().ConfigureAwait(true);
        }
    }

    /// <summary>显示区能容纳的行列数(终端控件报的);新建终端用最近一次的大小。</summary>
    public void SetGrid(int columns, int rows)
    {
        if (columns > 0 && rows > 0)
        {
            _columns = columns;
            _rows = rows;
        }
    }

    // ───────────────────────── 意图 ─────────────────────────

    [RelayCommand]
    private void Activate(TerminalTabViewModel tab) => Select(tab);

    /// <summary>新建终端:本机直接创建;远程环境出批准卡片。</summary>
    [RelayCommand]
    private async Task NewTerminalAsync()
    {
        if (_project is not { } project || IsCreating || !IsConnected(project.DeviceId))
        {
            return;
        }

        IsCreating = true;
        OnPropertyChanged(nameof(CanCreate));
        try
        {
            var result = await _terminals.PrepareAsync(project.DeviceId, project.WorkspaceId, _columns, _rows).ConfigureAwait(true);
            if (!result.IsOk || result.Value is not { } prepared)
            {
                Failure = Problem.Of(result);
                return;
            }

            Failure = null;
            if (prepared.Intent is { } intent)
            {
                var tab = new TerminalTabViewModel(_terminals, project.DeviceId, intent.TerminalId, intent);
                _pending[intent.TerminalId] = tab;
                Derive();
                Select(tab);
            }
            else if (prepared.Terminal is { } terminal)
            {
                // 镜像已经由 Action 更新;选中新终端
                Derive();
                if (Tabs.FirstOrDefault(t => t.Id == terminal.Id) is { } created)
                {
                    Select(created);
                }
            }
        }
        finally
        {
            IsCreating = false;
            OnPropertyChanged(nameof(CanCreate));
        }
    }

    /// <summary>批准一个远程终端意图:拿一次性凭据 → 创建 → 订阅。</summary>
    [RelayCommand]
    private async Task ApproveAsync(TerminalTabViewModel tab)
    {
        if (tab.Intent is not { } intent || _project is not { } project)
        {
            return;
        }

        var approval = await _terminals.ApproveAsync(project.DeviceId, intent.Id, approved: true).ConfigureAwait(true);
        if (!approval.IsOk || approval.Value?.Approval is not { } credential)
        {
            tab.Failure = Problem.Of(approval);
            if (approval.Outcome != ActionOutcome.Unavailable)
            {
                // 过期 / 被别的控制端处理了:这个意图作废
                _pending.Remove(intent.TerminalId);
                Derive();
            }

            return;
        }

        var created = await _terminals.CreateAsync(project.DeviceId, project.WorkspaceId, _columns, _rows, intent.TerminalId, intent.Cwd, credential).ConfigureAwait(true);
        if (!created.IsOk)
        {
            tab.Failure = Problem.Of(created);
            return;
        }

        tab.Failure = null;
        tab.Intent = null;
        _pending.Remove(intent.TerminalId);
        Derive();
        if (_visible && ReferenceEquals(Active, tab))
        {
            await tab.AttachAsync().ConfigureAwait(true);
        }
    }

    /// <summary>拒绝一个远程终端意图。</summary>
    [RelayCommand]
    private async Task DenyAsync(TerminalTabViewModel tab)
    {
        if (tab.Intent is not { } intent || _project is not { } project)
        {
            return;
        }

        _ = await _terminals.ApproveAsync(project.DeviceId, intent.Id, approved: false).ConfigureAwait(true);
        _pending.Remove(intent.TerminalId);
        _cache.Remove((project.DeviceId, intent.TerminalId));
        tab.Dispose();
        Derive();
    }

    /// <summary>终止终端进程(会话保留为「已退出」,可回看输出)。</summary>
    [RelayCommand]
    private async Task KillAsync(TerminalTabViewModel tab)
    {
        var result = await _terminals.KillAsync(tab.DeviceId, tab.Id).ConfigureAwait(true);
        tab.Failure = result.IsOk ? null : Problem.Of(result);
    }

    /// <summary>关闭页签:设备上终止(若还在跑)并移除。</summary>
    [RelayCommand]
    private async Task CloseAsync(TerminalTabViewModel tab)
    {
        if (tab.Intent is not null)
        {
            await DenyAsync(tab).ConfigureAwait(true);
            return;
        }

        var result = await _terminals.CloseAsync(tab.DeviceId, tab.Id).ConfigureAwait(true);
        if (result.IsOk || result.Outcome == ActionOutcome.NotFound)
        {
            _cache.Remove((tab.DeviceId, tab.Id));
            tab.Dispose();
            Derive();
        }
        else
        {
            tab.Failure = Problem.Of(result);
        }
    }
}
