using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>一个操作需要的最低权限(docs/design/09 §4):viewer 只读,member 部分可写,owner 全部;<see cref="LocalOwner"/> = owner 且只能经本机通道。</summary>
public enum Need
{
    Viewer,
    Member,
    Owner,
    LocalOwner,
}

/// <summary>按钮被禁用 / 页面不可读的原因(界面按它写文字说明为什么)。</summary>
public enum GateReason
{
    None,

    /// <summary>设备没连上:状态是未知(规则 7),这里不是「没有」,也不是「结束」。</summary>
    Offline,

    /// <summary>角色不够:需要 member。</summary>
    NeedsMember,

    /// <summary>角色不够:需要 owner。</summary>
    NeedsOwner,

    /// <summary>只有本机通道允许(密钥、危险操作):远程 owner 也不行。</summary>
    LocalOnly,
}

public readonly record struct Gate(GateReason Reason)
{
    public static readonly Gate Open = new(GateReason.None);

    public bool Allowed => Reason == GateReason.None;
}

/// <summary>「能不能做」的唯一判断(纯函数):界面上所有设置 / 管理页的禁用与说明都来自这里。</summary>
public static class ManagementAccess
{
    public static int Rank(string? role) => role switch { "owner" => 2, "member" => 1, _ => 0 };

    public static Gate Evaluate(DeviceConnection connection, string? role, bool isLocalChannel, Need need)
    {
        if (connection != DeviceConnection.Connected)
        {
            return new Gate(GateReason.Offline);
        }

        var rank = Rank(role);
        return need switch
        {
            Need.Viewer => Gate.Open,
            Need.Member => rank >= 1 ? Gate.Open : new Gate(GateReason.NeedsMember),
            Need.Owner => rank >= 2 ? Gate.Open : new Gate(GateReason.NeedsOwner),
            _ => rank < 2 ? new Gate(GateReason.NeedsOwner) : isLocalChannel ? Gate.Open : new Gate(GateReason.LocalOnly),
        };
    }
}

/// <summary>一次操作失败的原因:结局 + 设备给的稳定 key(<c>skills.*</c> / <c>mcp.*</c> …)。界面按 key 翻译,Client 不产出文字。</summary>
public sealed record Failure(ActionOutcome Outcome, string? Key)
{
    public static Failure Of<T>(ActionResult<T> result) => new(result.Outcome, result.ErrorKey);

    /// <summary>成功返回 null。</summary>
    public static Failure? OfOrNull<T>(ActionResult<T> result) => result.IsOk ? null : Of(result);
}

/// <summary>页面的加载状态机。</summary>
public enum PageState
{
    Idle,
    Loading,
    Ready,

    /// <summary>读取失败(<see cref="ManagementPageViewModel.Failure"/> 说明原因)。</summary>
    Error,

    /// <summary>设备没连上:状态未知(规则 7)。</summary>
    Unknown,

    /// <summary>角色读不了这一页(例如 viewer 看不到 Hooks 的命令文本)。</summary>
    Forbidden,
}

/// <summary>下拉框里的一项。<see cref="Text"/> 为 null = 「跟随默认」,由界面按语言写成文字。</summary>
public sealed record ChoiceItem(string Value, string? Text);

/// <summary>
/// 设置页里「设备」和「项目」两个作用域的当前选择(docs/design/07 §3:设备作用域页面顶部有设备切换器,项目作用域有项目切换器)。
/// 选择本身是纯 UI 状态;设备的连接、角色和项目清单都从 Store 读。设备断开时仍然可选(显示未知),不会悄悄换成别的设备。
/// </summary>
public sealed partial class SettingsScope : ObservableObject
{
    private readonly ClientStores _stores;

    public SettingsScope(ClientStores stores)
    {
        _stores = stores;
        stores.Workspace.Changed += Refresh;
        stores.Selection.Changed += Refresh;
        Refresh();
    }

    /// <summary>设备、项目、连接或角色变了(页面据此重新加载)。</summary>
    public event Action? Changed;

    public ObservableCollection<ChoiceItem> DeviceChoices { get; } = [];

    public ObservableCollection<ChoiceItem> ProjectChoices { get; } = [];

    [ObservableProperty] public partial string? DeviceId { get; set; }

    [ObservableProperty] public partial string? WorkspaceId { get; set; }

    [ObservableProperty] public partial DeviceConnection Connection { get; private set; } = DeviceConnection.Connecting;

    [ObservableProperty] public partial string? Role { get; private set; }

    [ObservableProperty] public partial bool IsLocalChannel { get; private set; }

    [ObservableProperty] public partial string DeviceName { get; private set; } = "";

    public bool IsConnected => Connection == DeviceConnection.Connected;

    public bool HasDevices => DeviceChoices.Count > 0;

    public bool HasProjects => ProjectChoices.Count > 0;

    /// <summary>设备这个维度要不要露出来(只有一台本机、没有 SSH 时不要;Core 的 <see cref="DeviceDimension"/>,03 §11.7)。</summary>
    [ObservableProperty] public partial bool ShowsDevices { get; private set; } = true;

    /// <summary>页头那一排「设备 · 连接状态 · 角色」要不要画(设备维度藏起来时,连接不正常或不是 owner 仍然要画)。</summary>
    public bool ShowsScopeDetails => DeviceDimension.ShowsScopeDetails(ShowsDevices, Connection, Role);

    /// <summary>设备切换器:有设备,而且设备维度露出来。</summary>
    public bool ShowsDeviceSwitcher => HasDevices && ShowsDevices;

    partial void OnShowsDevicesChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsScopeDetails));
        OnPropertyChanged(nameof(ShowsDeviceSwitcher));
    }

    partial void OnConnectionChanged(DeviceConnection value) => OnPropertyChanged(nameof(ShowsScopeDetails));

    partial void OnRoleChanged(string? value) => OnPropertyChanged(nameof(ShowsScopeDetails));

    public Gate GateFor(Need need) => ManagementAccess.Evaluate(Connection, Role, IsLocalChannel, need);

    partial void OnDeviceIdChanged(string? value) => Refresh();

    partial void OnWorkspaceIdChanged(string? value)
    {
        // 切换器换数据源时会先写一次 null、再写回原值:这一下不是用户选了「没有项目」,不通知(否则所有页面白白重读一遍)
        if (value is null && ProjectChoices.Count > 0 && !_refreshing)
        {
            return;
        }

        Announce();
    }

    private bool _refreshing;

    /// <summary>上一次通知出去的作用域。只有「安定下来的」作用域真的变了才通知页面重读。</summary>
    private (string? DeviceId, string? WorkspaceId, DeviceConnection Connection, string? Role)? _announced;

    private void Announce()
    {
        var now = (DeviceId, WorkspaceId, Connection, Role);
        if (_announced == now)
        {
            return;
        }

        _announced = now;
        Changed?.Invoke();
    }

    private void Refresh()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var workspace = _stores.Workspace.State;
            var devices = workspace.Devices.Values
                .Where(d => d.Connection != DeviceConnection.Revoked)
                .OrderBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.Ordinal)
                .Select(d => new ChoiceItem(d.DeviceId, d.Name)).ToList();
            Sync(DeviceChoices, devices);
            OnPropertyChanged(nameof(HasDevices));
            ShowsDevices = DeviceDimension.Shows(
                devices.Count,
                workspace.Devices.Values.Any(d => d.Kind != DeviceKind.Local && d.Connection != DeviceConnection.Revoked),
                workspace.Projects.Values.Any(p => p.Environment == EnvironmentKind.Ssh));
            OnPropertyChanged(nameof(ShowsDeviceSwitcher));

            if (DeviceId is null || !workspace.Devices.ContainsKey(DeviceId))
            {
                var selected = _stores.Selection.State.Project?.DeviceId;
                DeviceId = selected is not null && workspace.Devices.ContainsKey(selected)
                    ? selected
                    : workspace.Devices.Values.FirstOrDefault(d => d.Connection == DeviceConnection.Connected)?.DeviceId ?? devices.FirstOrDefault()?.Value;
            }

            if (DeviceId is { } id && workspace.Devices.TryGetValue(id, out var device))
            {
                Connection = device.Connection;
                Role = device.Role;
                IsLocalChannel = device.Kind == DeviceKind.Local;
                DeviceName = device.Name;
                var projects = workspace.Projects.Values.Where(p => p.Ref.DeviceId == id)
                    .OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new ChoiceItem(p.Ref.WorkspaceId, p.Name)).ToList();
                Sync(ProjectChoices, projects);
                if (WorkspaceId is null || projects.All(p => p.Value != WorkspaceId))
                {
                    var current = _stores.Selection.State.Project;
                    WorkspaceId = current is { } cur && cur.DeviceId == id && projects.Any(p => p.Value == cur.WorkspaceId)
                        ? cur.WorkspaceId
                        : projects.FirstOrDefault()?.Value;
                }
            }
            else
            {
                Connection = DeviceConnection.Offline;
                Role = null;
                IsLocalChannel = false;
                DeviceName = "";
                Sync(ProjectChoices, []);
                WorkspaceId = null;
            }

            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(HasProjects));
        }
        finally
        {
            _refreshing = false;
        }

        Announce();
    }

    private static void Sync(ObservableCollection<ChoiceItem> target, List<ChoiceItem> items)
    {
        if (items.SequenceEqual(target))
        {
            return;
        }

        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}

/// <summary>
/// 设置 / 管理页的公共骨架:加载状态机(加载中 / 就绪 / 错误 / 设备未知 / 角色不可读)、操作失败条、权限判断。
/// 页面只读 Store 或调用 Actions;切换设备 / 项目、设备重连、角色变化时自动重新加载(只在页面被打开过之后)。
/// </summary>
public abstract partial class ManagementPageViewModel : ObservableObject
{
    private bool _active;
    private long _loadVersion;

    protected ManagementPageViewModel(SettingsScope scope, Need readNeed)
    {
        Scope = scope;
        ReadNeed = readNeed;
        scope.Changed += OnScopeChanged;
    }

    public SettingsScope Scope { get; }

    protected Need ReadNeed { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsLoading), nameof(IsReady), nameof(IsError), nameof(IsUnknown), nameof(IsForbidden), nameof(ShowsContent))]
    public partial PageState State { get; protected set; } = PageState.Idle;

    /// <summary>还没读过(设置页里还没滚到这一段):画骨架占位,不让分区看起来是空的(docs/design/03 §11.6)。</summary>
    public bool IsIdle => State == PageState.Idle;

    /// <summary>读取失败的原因(<see cref="PageState.Error"/>)。</summary>
    [ObservableProperty] public partial Failure? LoadFailure { get; protected set; }

    /// <summary>最近一次写操作失败的原因(显示成页面上的失败条,可关闭;不挡住内容)。</summary>
    [ObservableProperty] public partial Failure? ActionFailure { get; protected set; }

    [ObservableProperty] public partial bool IsBusy { get; protected set; }

    public bool IsLoading => State == PageState.Loading;

    public bool IsReady => State == PageState.Ready;

    public bool IsError => State == PageState.Error;

    public bool IsUnknown => State == PageState.Unknown;

    public bool IsForbidden => State == PageState.Forbidden;

    /// <summary>内容区是否显示(就绪)。</summary>
    public bool ShowsContent => State == PageState.Ready;

    public Gate ReadGate => Scope.GateFor(ReadNeed);

    /// <summary>写操作所需的权限(页面可以覆盖;个别操作更严的在页面里单独判断)。</summary>
    protected virtual Need WriteNeed => Need.Owner;

    public Gate WriteGate => Scope.GateFor(WriteNeed);

    public bool CanWrite => WriteGate.Allowed;

    public bool IsReadOnly => ShowsContent && !CanWrite;

    /// <summary>设备上的项目作用域(项目页用):没有项目时为 null。</summary>
    protected string? DeviceId => Scope.DeviceId;

    /// <summary>页面被打开(切到这个标签页):第一次触发加载。</summary>
    public Task ActivateAsync()
    {
        _active = true;
        return ReloadAsync();
    }

    [RelayCommand]
    public Task ReloadAsync() => ReloadCoreAsync();

    private void OnScopeChanged()
    {
        RaiseGates();
        if (_active)
        {
            _ = ReloadCoreAsync();
        }
    }

    protected void RaiseGates()
    {
        OnPropertyChanged(nameof(ReadGate));
        OnPropertyChanged(nameof(WriteGate));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(IsReadOnly));
        OnGatesChanged();
    }

    /// <summary>角色 / 连接变化后,页面自己的派生权限(例如「只有本机 owner」)在这里通知。</summary>
    protected virtual void OnGatesChanged()
    {
    }

    private async Task ReloadCoreAsync()
    {
        var version = Interlocked.Increment(ref _loadVersion);
        ActionFailure = null;
        if (Scope.DeviceId is null || !Scope.IsConnected)
        {
            LoadFailure = null;
            State = PageState.Unknown;
            OnUnavailable();
            return;
        }

        if (!ReadGate.Allowed)
        {
            LoadFailure = null;
            State = PageState.Forbidden;
            return;
        }

        LoadFailure = null;
        State = PageState.Loading;
        try
        {
            await LoadAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 被新的加载顶掉
        }

        _ = version;
    }

    /// <summary>设备断开:页面把已有数据当作未知(不判成空、不继续计时)。</summary>
    protected virtual void OnUnavailable()
    {
    }

    /// <summary>读取页面数据;成功调用 <see cref="Ready"/>,失败调用 <see cref="Fail{T}"/>。</summary>
    protected abstract Task LoadAsync();

    protected void Ready()
    {
        LoadFailure = null;
        State = PageState.Ready;
        RaiseGates();
    }

    protected void Fail<T>(ActionResult<T> result)
    {
        LoadFailure = Failure.Of(result);
        State = result.Outcome == ActionOutcome.Unavailable ? PageState.Unknown : PageState.Error;
    }

    /// <summary>
    /// 一次写操作:忙标记、失败条。返回结果让调用方继续(例如把返回值合进页面状态)。
    /// 失败时 <see cref="ActionFailure"/> 带上原因,成功时清掉。
    /// </summary>
    protected async Task<ActionResult<T>> Act<T>(Task<ActionResult<T>> call)
    {
        IsBusy = true;
        ActionFailure = null;
        try
        {
            var result = await call.ConfigureAwait(true);
            if (!result.IsOk)
            {
                ActionFailure = Failure.Of(result);
            }

            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>当前加载是不是已经被后来的加载取代(设备 / 项目切换时丢弃迟到的结果)。</summary>
    protected bool IsCurrent(string? device, string? workspace = null) => device == Scope.DeviceId && (workspace is null || workspace == Scope.WorkspaceId);

    [RelayCommand]
    private void DismissFailure() => ActionFailure = null;
}
