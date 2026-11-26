using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Client.Updates;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>设置页的分组(docs/design/07)。先实现「设备」和「连接与远程」,其余分组随功能加入。</summary>
public enum SettingsSection
{
    /// <summary>本应用:本控制端配对过的设备、添加设备。</summary>
    Devices,

    /// <summary>设备:模型与供应商(供应商、凭据、模型别名、模型目录)。</summary>
    Models,

    /// <summary>设备(本机):远程开关、配对码、已配对的控制端。</summary>
    Remote,

    /// <summary>设备:通用(辅助模型、超时、轮换与故障转移、压缩、代理、个性化)。</summary>
    General,

    /// <summary>项目:项目设置。</summary>
    Project,

    /// <summary>设备:技能。</summary>
    Skills,

    /// <summary>设备:斜杠命令。</summary>
    Commands,

    /// <summary>设备:Hooks。</summary>
    Hooks,

    /// <summary>设备:MCP 服务器。</summary>
    Mcp,

    /// <summary>设备:执行环境(SSH 服务器、导入 ssh_config、已知主机指纹;docs/design/06)。</summary>
    Environments,

    /// <summary>设备:用量统计。</summary>
    Usage,

    /// <summary>设备:存储与数据维护。</summary>
    Storage,

    /// <summary>设备:开放网关。</summary>
    Gateway,

    /// <summary>本应用:账户(账户信息、团队、已登录的控制端、退出登录;不涉及支付)。</summary>
    Account,

    /// <summary>设备:插件(已安装 / 权限 / 配置 / 活动与诊断 / 市场 / 待处理的提问)。</summary>
    Plugins,

    /// <summary>本应用:关于与更新(当前版本、通道、检查更新、下载、重启并更新)。</summary>
    About,

    /// <summary>本应用:外观(目前只有动效级别,docs/design/03 §9)。</summary>
    Appearance,

    /// <summary>本应用:快捷键(查看、录制、恢复默认,docs/design/03 §11)。</summary>
    Shortcuts,

    /// <summary>本应用:通知(系统通知、Dock / 任务栏角标、托盘图标,docs/design/07 §3)。</summary>
    Notifications,
}

/// <summary>
/// 设置页:整窗覆盖在外壳上,三栏「导航 → 列表栏 → 内容」(docs/design/07 §3,方案 D)。打开与否、当前分区是纯 UI 状态;
/// 当前导航项、列表栏里的子分区都从 <see cref="Section"/> 派生(<see cref="SettingsNav"/>)。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(ClientStores stores, DeviceRegistry devices, DeviceActions actions, ModelsSettingsViewModel models, ManagementPages pages, AccountViewModel? account = null, AboutViewModel? about = null)
    {
        Account = account ?? new AccountViewModel(stores, null);
        About = about ?? new AboutViewModel(stores, new UpdateActions(null));
        Appearance = new AppearanceViewModel(stores);
        Shortcuts = new ShortcutsSettingsViewModel(stores);
        Notifications = new NotificationsSettingsViewModel(stores);
        Devices = new DevicesSettingsViewModel(stores, devices, actions);
        Remote = new RemoteSettingsViewModel(stores, devices, actions);
        Models = models;
        Pages = pages;
        // 设备切换器只有一份(导航「设备」分组下):模型页跟随它
        Pages.Scope.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsScope.DeviceId) && Pages.Scope.DeviceId is { } device)
            {
                Models.DeviceId = device;
            }
        };
        if (Pages.Scope.DeviceId is { } initial)
        {
            Models.DeviceId = initial;
        }

        RebuildSubSections();
    }

    /// <summary>设备 / 项目作用域的管理页(通用、项目、技能、命令、Hooks、MCP、用量、存储、网关)。</summary>
    public ManagementPages Pages { get; }

    /// <summary>「账户」页(本应用作用域):账户信息、团队、已登录的控制端、退出登录。</summary>
    public AccountViewModel Account { get; }

    /// <summary>「关于」页(本应用作用域):当前版本、通道、检查更新、下载进度、重启并更新。</summary>
    public AboutViewModel About { get; }

    /// <summary>「外观」页(本应用作用域):动效级别。</summary>
    public AppearanceViewModel Appearance { get; }

    /// <summary>「快捷键」页(本应用作用域)。</summary>
    public ShortcutsSettingsViewModel Shortcuts { get; }

    /// <summary>「通知」页(本应用作用域)。</summary>
    public NotificationsSettingsViewModel Notifications { get; }

    public ModelsSettingsViewModel Models { get; }

    public DevicesSettingsViewModel Devices { get; }

    public RemoteSettingsViewModel Remote { get; }

    [ObservableProperty] public partial bool IsOpen { get; set; }

    [ObservableProperty] public partial SettingsSection Section { get; set; }

    public bool IsDevicesSection => Section == SettingsSection.Devices;

    public bool IsRemoteSection => Section == SettingsSection.Remote;

    public bool IsModelsSection => Section == SettingsSection.Models;

    public bool IsAccountSection => Section == SettingsSection.Account;

    public bool IsAboutSection => Section == SettingsSection.About;

    public bool IsAppearanceSection => Section == SettingsSection.Appearance;

    /// <summary>当前导航项(从 <see cref="Section"/> 派生)。</summary>
    public SettingsPage Page => SettingsNav.PageOf(Section);

    /// <summary>列表栏里的子分区;只有一个分区的导航项为空。</summary>
    public ObservableCollection<SettingsSubItem> SubSections { get; } = [];

    /// <summary>当前导航项有列表栏(子分区,或模型与供应商的供应商列表)。</summary>
    public bool ShowsList => SettingsNav.HasList(Page);

    /// <summary>列表栏是子分区列表。</summary>
    public bool ShowsSubSections => SubSections.Count > 1;

    /// <summary>列表栏是供应商列表(模型与供应商)。</summary>
    public bool ShowsProviderList => Page == SettingsPage.Models;

    /// <summary>每个导航项这次打开设置以后最后看的分区(点导航项回到那里;纯 UI 状态)。</summary>
    private readonly Dictionary<SettingsPage, SettingsSection> _lastSection = [];

    private void RebuildSubSections()
    {
        var sections = SettingsNav.SectionsOf(Page);
        SubSections.Clear();
        if (sections.Count > 1)
        {
            foreach (var section in sections)
            {
                SubSections.Add(new SettingsSubItem(section, section == Section));
            }
        }
    }

    partial void OnSectionChanged(SettingsSection value)
    {
        _lastSection[SettingsNav.PageOf(value)] = value;
        RebuildSubSections();
        OnPropertyChanged(nameof(Page));
        OnPropertyChanged(nameof(ShowsList));
        OnPropertyChanged(nameof(ShowsSubSections));
        OnPropertyChanged(nameof(ShowsProviderList));
        OnPropertyChanged(nameof(IsAboutSection));
        OnPropertyChanged(nameof(IsAppearanceSection));
        OnPropertyChanged(nameof(IsDevicesSection));
        OnPropertyChanged(nameof(IsRemoteSection));
        OnPropertyChanged(nameof(IsModelsSection));
        OnPropertyChanged(nameof(IsAccountSection));
        if (IsOpen)
        {
            Activate(value);
        }
    }

    /// <summary>这次打开设置以后已经读过数据的分区。</summary>
    private readonly HashSet<SettingsSection> _activated = [];

    /// <summary>
    /// 分区成为当前分区时读它的数据——每次打开设置只读一次(来回切换分区不重读,不会把刚出的结果、包括失败的提示,换成又一次读取)。
    /// 网关的密钥照旧在离开网关时收起。重新打开设置会全部重读。
    /// </summary>
    private void Activate(SettingsSection section)
    {
        if (section != SettingsSection.Gateway)
        {
            Pages.Gateway.HideKeys();
        }

        if (!_activated.Add(section))
        {
            return;
        }

        if (section == SettingsSection.Account)
        {
            _ = Account.ActivateAsync(); // 打开「账户」页时读已登录的控制端
        }
        else if (section is SettingsSection.About or SettingsSection.Appearance or SettingsSection.Shortcuts or SettingsSection.Notifications)
        {
            // 「关于」「外观」「快捷键」「通知」是本应用的页:没有设备数据要读(自更新状态、界面偏好都在 Store 里)
        }
        else
        {
            _ = Pages.ActivateAsync(section);
        }
    }

    [RelayCommand]
    private void Open()
    {
        _activated.Clear();
        _lastSection.Clear();
        Devices.Refresh();
        IsOpen = true;
        Activate(Section);
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        SearchQuery = "";
        Pages.Gateway.HideKeys(); // 密钥只在页面打开时短暂持有
    }

    [RelayCommand]
    private void Show(SettingsSection section) => Section = section;

    /// <summary>点导航项:回到这个导航项上次看的分区,没看过就是它的第一个分区。</summary>
    [RelayCommand]
    private void ShowPage(SettingsPage page) =>
        Section = _lastSection.TryGetValue(page, out var last) ? last : SettingsNav.SectionsOf(page)[0];

    // ───────────────────────── 搜索(docs/design/03 §11.6)─────────────────────────

    /// <summary>每个分区的全部文字(当前语言),界面装配时接上;没有接上时搜索没有结果。</summary>
    public Func<IReadOnlyList<(SettingsSection Section, string Title, IReadOnlyList<string> Texts)>> SearchIndex { get; set; } = static () => [];

    /// <summary>搜索框草稿(纯 UI 状态)。</summary>
    [ObservableProperty] public partial string SearchQuery { get; set; } = "";

    public ObservableCollection<SettingsHit> SearchHits { get; } = [];

    public bool IsSearching => SearchQuery.Trim().Length > 0;

    public bool ShowsNoSearchHits => IsSearching && SearchHits.Count == 0;

    partial void OnSearchQueryChanged(string value)
    {
        SearchHits.Clear();

        // 查询为空时不建索引:界面建索引要先建出所有分区的页面(docs/design/03 §12),关设置时清空搜索框不该把刚释放的页面又建回来
        if (IsSearching)
        {
            foreach (var hit in SettingsSearch.Find(value, SearchIndex()))
            {
                SearchHits.Add(hit);
            }
        }

        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowsNoSearchHits));
    }

    /// <summary>点一个搜索结果:跳到那个分区,清掉搜索。</summary>
    [RelayCommand]
    private void OpenHit(SettingsHit hit)
    {
        SearchQuery = "";
        Section = hit.Section;
    }
}

/// <summary>「设备」页里的一行:一台配对过的远程设备。</summary>
public sealed record PairedDeviceItem(string DeviceId, string Name, string Address, DeviceConnection Connection, long PairedAt);

/// <summary>「设备」页:本控制端配对过的设备,和「添加设备」表单(地址 + 配对码)。</summary>
public sealed partial class DevicesSettingsViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly DeviceRegistry _devices;
    private readonly DeviceActions _actions;

    public DevicesSettingsViewModel(ClientStores stores, DeviceRegistry devices, DeviceActions actions)
    {
        _stores = stores;
        _devices = devices;
        _actions = actions;
        stores.Paired.Changed += Refresh;
        stores.Workspace.Changed += Refresh;
        Refresh();
    }

    public ObservableCollection<PairedDeviceItem> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>宿主支持远程设备(桌面端);模拟数据模式下不显示添加表单。宿主在外壳创建之后才接上,所以每次刷新时重新判断。</summary>
    [ObservableProperty] public partial bool CanAdd { get; private set; }

    [ObservableProperty] public partial string Address { get; set; } = "";

    [ObservableProperty] public partial string Code { get; set; } = "";

    [ObservableProperty] public partial bool IsPairing { get; private set; }

    /// <summary>最近一次添加的结果;成功或还没试过为 null。</summary>
    [ObservableProperty] public partial PairOutcome? LastError { get; private set; }

    /// <summary>刚添加成功(显示一句确认,改输入后消失)。</summary>
    [ObservableProperty] public partial bool JustPaired { get; private set; }

    partial void OnAddressChanged(string value) => ClearResult();

    partial void OnCodeChanged(string value) => ClearResult();

    private void ClearResult()
    {
        LastError = null;
        JustPaired = false;
    }

    [RelayCommand]
    private async Task PairAsync()
    {
        IsPairing = true;
        try
        {
            var outcome = await _actions.PairAsync(Address, Code);
            if (outcome == PairOutcome.Paired)
            {
                Address = "";
                Code = "";
                LastError = null;
                JustPaired = true;
            }
            else
            {
                LastError = outcome;
            }
        }
        finally
        {
            IsPairing = false;
        }
    }

    [RelayCommand]
    private Task RemoveAsync(string deviceId) => _actions.RemoveAsync(deviceId);

    public void Refresh()
    {
        CanAdd = _devices.Remote is not null;
        var workspace = _stores.Workspace.State;
        var items = _stores.Paired.State.Select(d =>
        {
            var live = workspace.Devices.GetValueOrDefault(d.DeviceId);
            return new PairedDeviceItem(d.DeviceId, live?.Name ?? d.Name, $"{d.Host}:{d.Port}",
                live?.Connection ?? DeviceConnection.Connecting, d.PairedAt);
        }).ToList();

        if (items.SequenceEqual(Items))
        {
            return;
        }

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(nameof(HasItems));
    }
}

/// <summary>「连接与远程」页里的一行:一个配对过本机的控制端。</summary>
public sealed record ControllerItem(string Fingerprint, string Name, string ShortFingerprint, string Role, bool Connected, long PairedAt, long? LastSeenAt);

/// <summary>改角色菜单里的一项。</summary>
public sealed record RoleChange(string Fingerprint, string Role);

/// <summary>
/// 「连接与远程」页(本机设备):远程开关、地址、配对码、已配对的控制端。数据只来自 Store 里本机设备的 <see cref="DeviceInfo.Remote"/>
/// (设备是真源);改动经 <see cref="DeviceActions"/> 发给设备,结果再经 remote 主题回来。
/// </summary>
public sealed partial class RemoteSettingsViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly DeviceRegistry _devices;
    private readonly DeviceActions _actions;

    public RemoteSettingsViewModel(ClientStores stores, DeviceRegistry devices, DeviceActions actions)
    {
        _stores = stores;
        _devices = devices;
        _actions = actions;
        stores.Workspace.Changed += Refresh;
        Refresh();
    }

    /// <summary>本机设备连着、数据就绪,能读到远程状态。</summary>
    [ObservableProperty] public partial bool IsAvailable { get; private set; }

    [ObservableProperty] public partial bool IsEnabled { get; private set; }

    [ObservableProperty] public partial string? PairingCode { get; private set; }

    /// <summary>打开失败的原因代码(例如 port-unavailable)。</summary>
    [ObservableProperty] public partial string? Error { get; private set; }

    [ObservableProperty] public partial bool IsBusy { get; private set; }

    /// <summary>设备上保存的首选端口(设备推回来的值)。</summary>
    [ObservableProperty] public partial int PreferredPort { get; private set; }

    [ObservableProperty] public partial bool IsLanOnly { get; private set; }

    /// <summary>设备上保存的中继地址(设备推回来的值);没配置为 null(默认,中继关闭)。</summary>
    [ObservableProperty] public partial string? RelayUrl { get; private set; }

    /// <summary>到中继的注册状态:<c>off</c> / <c>connecting</c> / <c>registered</c> / <c>failed</c>;没配置中继为 null。</summary>
    [ObservableProperty] public partial string? RelayState { get; private set; }

    /// <summary>输入框里的草稿(双向绑定)。点「应用」才发给设备;留空 = 关闭中继。</summary>
    [ObservableProperty] public partial string RelayDraft { get; set; } = "";

    /// <summary>上一次提交的地址被设备拒绝(格式不对)。</summary>
    [ObservableProperty] public partial bool RelayInvalid { get; private set; }

    private Timer? _portTimer;
    private int? _pendingPort;

    /// <summary>端口输入框(双向绑定)。步进按钮每点一下都会改值,停下来一会儿才发给设备(每次改端口设备都要重新监听)。</summary>
    public double Port
    {
        get => _pendingPort ?? PreferredPort;
        set
        {
            var port = (int)Math.Round(value);
            if (port == (_pendingPort ?? PreferredPort) || port is < 1024 or > 65535)
            {
                return;
            }

            _pendingPort = port;
            _portTimer?.Dispose();
            _portTimer = new Timer(_ => _ = CommitPortAsync(), null, PortDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>端口改动合并的等待时间(测试里可以调短)。</summary>
    public TimeSpan PortDebounce { get; set; } = TimeSpan.FromMilliseconds(800);

    private async Task CommitPortAsync()
    {
        if (_pendingPort is not { } port)
        {
            return;
        }

        await _actions.ConfigureRemoteAsync(port, null);
    }

    /// <summary>「仅允许局域网地址」开关(双向绑定)。</summary>
    public bool LanOnly
    {
        get => IsLanOnly;
        set
        {
            if (value != IsLanOnly)
            {
                _ = _actions.ConfigureRemoteAsync(null, value);
            }
        }
    }

    public ObservableCollection<string> Addresses { get; } = [];

    public ObservableCollection<ControllerItem> Controllers { get; } = [];

    public bool HasControllers => Controllers.Count > 0;

    public bool HasAddresses => Addresses.Count > 0;

    /// <summary>开关(双向绑定):写入时把意图发给设备,显示的值仍以设备推回来的为准。</summary>
    public bool Enabled
    {
        get => IsEnabled;
        set
        {
            if (value != IsEnabled)
            {
                _ = SetEnabledAsync(value);
            }
        }
    }

    private async Task SetEnabledAsync(bool value)
    {
        IsBusy = true;
        try
        {
            await _actions.SetRemoteEnabledAsync(value);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(Enabled));
        }
    }

    /// <summary>草稿和设备上的地址不同(「应用」按钮可用)。</summary>
    public bool CanApplyRelay => RelayDraft.Trim() != (RelayUrl ?? "");

    partial void OnRelayDraftChanged(string value)
    {
        RelayInvalid = false;
        OnPropertyChanged(nameof(CanApplyRelay));
    }

    /// <summary>把中继地址发给设备(空 = 关闭)。设备校验格式:不合法时标出来,不改设置。</summary>
    [RelayCommand]
    private async Task ApplyRelayAsync() => RelayInvalid = await _actions.SetRelayAsync(RelayDraft) == RelayOutcome.Invalid;

    [RelayCommand]
    private Task RotateCodeAsync() => _actions.RotatePairingCodeAsync();

    [RelayCommand]
    private Task RevokeAsync(string fingerprint) => _actions.RevokeControllerAsync(fingerprint);

    [RelayCommand]
    private Task SetRoleAsync(RoleChange change) => _actions.SetControllerRoleAsync(change.Fingerprint, change.Role);

    private void Refresh()
    {
        var remote = _devices.Local is { } link && _stores.Workspace.State.Devices.TryGetValue(link.DeviceId, out var device) ? device.Remote : null;
        IsAvailable = remote is not null;
        IsEnabled = remote?.Status.Enabled ?? false;
        PairingCode = remote?.Status.PairingCode is { Length: 6 } code ? $"{code[..3]} {code[3..]}" : null;
        Error = remote?.Status.Error;
        OnPropertyChanged(nameof(Enabled));
        if (remote is not null)
        {
            PreferredPort = remote.Status.PreferredPort;
            IsLanOnly = remote.Status.LanOnly;
            var previousRelay = RelayUrl;
            RelayUrl = remote.Status.RelayUrl;
            RelayState = remote.Status.RelayState;
            if (RelayUrl != previousRelay)
            {
                RelayDraft = RelayUrl ?? ""; // 设备采用了新值(或在别处改了):输入框跟随
            }

            OnPropertyChanged(nameof(CanApplyRelay));
            if (_pendingPort == PreferredPort)
            {
                _pendingPort = null; // 设备已经采用了这个端口
            }
        }

        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(LanOnly));

        var port = remote?.Status.Port;
        var addresses = remote is null || port is null
            ? []
            : remote.Status.Addresses.Select(a => a.Contains(':', StringComparison.Ordinal) ? $"[{a}]:{port}" : $"{a}:{port}").ToList();
        Sync(Addresses, addresses);
        OnPropertyChanged(nameof(HasAddresses));

        var controllers = remote?.Controllers
            .Select(c => new ControllerItem(c.Fingerprint, c.Name, ShortFingerprint(c.Fingerprint), c.Role, c.Connected, c.PairedAt, c.LastSeenAt))
            .ToList() ?? [];
        Sync(Controllers, controllers);
        OnPropertyChanged(nameof(HasControllers));
    }

    /// <summary>指纹的前 8 位,四位一组(界面上帮用户辨认是哪台设备)。</summary>
    private static string ShortFingerprint(string hex) => hex.Length >= 8 ? $"{hex[..4]} {hex[4..8]}".ToUpperInvariant() : hex;

    private static void Sync<T>(ObservableCollection<T> target, List<T> items)
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
