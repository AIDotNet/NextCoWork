using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.ViewModels;

/// <summary>执行环境连接状态的显示(纯函数 <see cref="EnvironmentStates.Of"/>)。设备断开 = <see cref="Unknown"/>(规则 7)。</summary>
public enum EnvironmentDisplayState
{
    Unknown,
    Idle,
    Connecting,
    Ready,
    Reconnecting,
    Failed,
}

public static class EnvironmentStates
{
    public static EnvironmentDisplayState Of(bool known, string state) => !known
        ? EnvironmentDisplayState.Unknown
        : state switch
        {
            EnvironmentStateNames.Connecting => EnvironmentDisplayState.Connecting,
            EnvironmentStateNames.Ready => EnvironmentDisplayState.Ready,
            EnvironmentStateNames.Reconnecting => EnvironmentDisplayState.Reconnecting,
            EnvironmentStateNames.Failed => EnvironmentDisplayState.Failed,
            _ => EnvironmentDisplayState.Idle,
        };
}

/// <summary>环境表单校验问题(界面按枚举翻译)。</summary>
public enum EnvironmentProblem
{
    Name,
    Host,
    Port,
    Username,
    ProxyHost,
    ProxyPort,
}

/// <summary>列表里的一台 SSH 执行环境。数据全部来自设备的 <see cref="EnvironmentDto"/>(真源);凭据只暴露「有没有」。</summary>
public sealed partial class EnvironmentRowViewModel(EnvironmentsViewModel owner, EnvironmentDto dto, bool known) : ObservableObject
{
    public EnvironmentDto Dto { get; } = dto;

    public string Id => Dto.Id;

    public string Name => Dto.Name;

    public bool IsEnabled => Dto.Enabled;

    public bool IsDefault => Dto.IsDefault;

    public long Revision => Dto.Revision;

    public EnvironmentDisplayState DisplayState { get; } = EnvironmentStates.Of(known, dto.State);

    public string TargetKind => Dto.Target?.Kind ?? "manual";

    public bool IsConfigTarget => TargetKind == "config";

    public string AuthMethod => Dto.AuthMethod ?? "auto";

    public string Platform => Dto.Platform ?? "auto";

    public bool HasSavedPassword => Dto.HasSavedPassword;

    public bool HasIdentityFile => Dto.Target?.HasIdentityFile ?? false;

    public bool HasProxy => Dto.Proxy is not null;

    public string? ProxySummary => Dto.Proxy is { } p ? $"{p.Kind}://{p.Host}:{p.Port}" : null;

    public string? ProxyJump => Dto.Target?.ProxyJump;

    public bool HasProxyJump => !string.IsNullOrWhiteSpace(ProxyJump);

    /// <summary>一行目标摘要:手工模式 <c>user@host:port</c>(默认 22 省略端口);配置模式为 Host 别名。</summary>
    public string TargetSummary => FormatTarget(Dto.Target);

    public long? LatencyMs => DisplayState == EnvironmentDisplayState.Ready ? Dto.LatencyMs : null;

    public bool HasLatency => LatencyMs is not null;

    public EnvironmentFactsDto? Facts => DisplayState == EnvironmentDisplayState.Ready ? Dto.Facts : null;

    public string? FactsSummary => Facts is { } f ? $"{f.Os} · {f.Architecture} · {f.Hostname}" : null;

    public string? ErrorKey => DisplayState == EnvironmentDisplayState.Failed ? Dto.Error?.Key : null;

    public HostKeyMismatchDto? HostKeyMismatch => DisplayState == EnvironmentDisplayState.Failed ? Dto.Error?.HostKey : null;

    public bool HasHostKeyMismatch => HostKeyMismatch is not null;

    public string? PinnedFingerprints => HostKeyMismatch is { Pinned.Length: > 0 } m
        ? string.Join(", ", m.Pinned.Select(static p => $"{p.Algorithm}: SHA256:{p.Fingerprint}"))
        : null;

    public bool IsReady => DisplayState == EnvironmentDisplayState.Ready;

    public bool IsConnecting => DisplayState is EnvironmentDisplayState.Connecting or EnvironmentDisplayState.Reconnecting;

    public bool CanConnect => !IsReady && !IsConnecting && IsEnabled && DisplayState != EnvironmentDisplayState.Unknown;

    public bool CanDisconnect => IsReady && DisplayState != EnvironmentDisplayState.Unknown;

    /// <summary>按状态决定主按钮:已连接 → 断开;连接中 → 取消;未连接 / 失败 → 连接 / 重连。</summary>
    public bool PrimaryIsDisconnect => IsReady;

    public bool PrimaryIsCancel => IsConnecting;

    public bool PrimaryIsConnect => !IsReady && !IsConnecting;

    public bool PrimaryIsReconnect => DisplayState == EnvironmentDisplayState.Failed;

    [RelayCommand]
    private Task ToggleAsync() => owner.SetEnabledAsync(this, !IsEnabled);

    [RelayCommand]
    private Task ConnectAsync() => owner.ConnectAsync(this);

    [RelayCommand]
    private Task DisconnectAsync() => owner.DisconnectAsync(this);

    [RelayCommand]
    private void CancelConnect() => owner.CancelConnect(this);

    [RelayCommand]
    private Task TestAsync() => owner.TestSavedAsync(this);

    [RelayCommand]
    private Task SetDefaultAsync() => owner.SetDefaultAsync(Id);

    [RelayCommand]
    private void Edit() => owner.Edit(this);

    [RelayCommand]
    private void AskRemove() => owner.AskRemove(this);

    [RelayCommand]
    private Task ForgetMismatchHostKeyAsync() => HostKeyMismatch is { } m ? owner.ForgetHostKeyAsync(m.HostName) : Task.CompletedTask;

    private static string FormatTarget(SshTargetDto? target)
    {
        if (target is null)
        {
            return "";
        }

        if (target.Kind == "config")
        {
            return target.Host;
        }

        var user = string.IsNullOrWhiteSpace(target.Username) ? "" : $"{target.Username}@";
        var port = target.Port is { } p and not 22 ? $":{p.ToString(CultureInfo.InvariantCulture)}" : "";
        return $"{user}{target.Host}{port}";
    }
}

/// <summary>从 <c>~/.ssh/config</c> 解析出的一个候选主机。</summary>
public sealed partial class SshConfigCandidateRowViewModel(EnvironmentsViewModel owner, SshConfigCandidateDto dto, bool alreadyImported) : ObservableObject
{
    public SshConfigCandidateDto Dto { get; } = dto;

    public string Alias => Dto.Alias;

    public string HostSummary
    {
        get
        {
            var user = string.IsNullOrWhiteSpace(Dto.Username) ? "" : $"{Dto.Username}@";
            var port = Dto.Port != 22 ? $":{Dto.Port.ToString(CultureInfo.InvariantCulture)}" : "";
            return $"{user}{Dto.HostName}{port}";
        }
    }

    public string? ProxyJump => Dto.ProxyJump;

    public bool HasProxyJump => !string.IsNullOrWhiteSpace(ProxyJump);

    public bool HasIdentityFile => Dto.HasIdentityFile;

    public bool Importable => Dto.Importable;

    public string[] Warnings => Dto.Warnings;

    public bool HasWarnings => Warnings.Length > 0;

    [ObservableProperty] public partial bool AlreadyImported { get; internal set; } = alreadyImported;

    public bool CanImport => Importable && !AlreadyImported;

    partial void OnAlreadyImportedChanged(bool value) => OnPropertyChanged(nameof(CanImport));

    [RelayCommand]
    private Task ImportAsync() => owner.ImportCandidateAsync(this);
}

/// <summary>已知主机指纹列表的一行。</summary>
public sealed partial class KnownHostRowViewModel(EnvironmentsViewModel owner, KnownHostDto dto) : ObservableObject
{
    public string HostName { get; } = dto.HostName;

    public string Algorithm { get; } = dto.Algorithm;

    public string Fingerprint { get; } = dto.Fingerprint;

    public string DisplayFingerprint => $"SHA256:{Fingerprint}";

    [RelayCommand]
    private Task ForgetAsync() => owner.ForgetHostKeyAsync(HostName);
}

/// <summary>新建 / 编辑 SSH 环境的表单(与设备 <c>EnvironmentProfiles.Validate</c> 保持一致;密码 / 口令 / 路径只写不读)。</summary>
public sealed partial class EnvironmentEditorViewModel : ObservableObject
{
    [ObservableProperty] public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    public partial bool IsNew { get; private set; } = true;

    public bool IsEditing => !IsNew;

    public string? EditingId { get; private set; }

    public long ExpectedRevision { get; private set; }

    [ObservableProperty] public partial string Name { get; set; } = "";

    /// <summary><c>manual</c> 或 <c>config</c>。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManual), nameof(IsConfig), nameof(UsesIdentity), nameof(UsesPassword))]
    public partial string TargetKind { get; set; } = "manual";

    [ObservableProperty] public partial string Host { get; set; } = "";

    [ObservableProperty] public partial string Port { get; set; } = "22";

    [ObservableProperty] public partial string Username { get; set; } = "";

    [ObservableProperty] public partial string ConfigFile { get; set; } = "";

    [ObservableProperty] public partial bool HasSavedConfigFile { get; private set; }

    [ObservableProperty] public partial bool ClearConfigFile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesIdentity), nameof(UsesPassword))]
    public partial string AuthMethod { get; set; } = "auto";

    [ObservableProperty] public partial string IdentityFile { get; set; } = "";

    [ObservableProperty] public partial bool HasSavedIdentityFile { get; private set; }

    [ObservableProperty] public partial bool ClearIdentityFile { get; set; }

    [ObservableProperty] public partial string KeyPassphrase { get; set; } = "";

    [ObservableProperty] public partial bool ClearKeyPassphrase { get; set; }

    [ObservableProperty] public partial string Password { get; set; } = "";

    [ObservableProperty] public partial bool HasSavedPassword { get; private set; }

    [ObservableProperty] public partial bool ClearPassword { get; set; }

    [ObservableProperty] public partial string ProxyJump { get; set; } = "";

    [ObservableProperty] public partial string Platform { get; set; } = "auto";

    [ObservableProperty] public partial bool Enabled { get; set; } = true;

    // ── 可选 HTTP / SOCKS 代理(3.0 新增) ──
    [ObservableProperty] public partial bool ProxyEnabled { get; set; }

    [ObservableProperty] public partial string ProxyKind { get; set; } = "socks5";

    [ObservableProperty] public partial string ProxyHost { get; set; } = "";

    [ObservableProperty] public partial string ProxyPort { get; set; } = "";

    [ObservableProperty] public partial string ProxyUsername { get; set; } = "";

    [ObservableProperty] public partial string ProxyPassword { get; set; } = "";

    [ObservableProperty] public partial bool HasSavedProxyPassword { get; private set; }

    [ObservableProperty] public partial bool ClearProxyPassword { get; set; }

    // ── 目标变更二次确认(有项目绑定时修改主机 / 端口 / 用户名 / 跳板机 / 平台) ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool NeedsTargetChangeConfirm { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool ConfirmTargetChange { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool IsSaving { get; set; }

    [ObservableProperty] public partial Failure? SaveFailure { get; set; }

    public bool IsManual => TargetKind == "manual";

    public bool IsConfig => TargetKind == "config";

    public bool UsesIdentity => AuthMethod is "auto" or "key";

    public bool UsesPassword => AuthMethod is "auto" or "password";

    public IReadOnlyList<EnvironmentProblem> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    public bool CanSave => Problems.Count == 0 && !IsSaving && (!NeedsTargetChangeConfirm || ConfirmTargetChange);

    public bool CanTest => Problems.Count == 0 && !IsSaving;

    partial void OnNameChanged(string value) => Touch();

    partial void OnTargetKindChanged(string value) => TouchTarget();

    partial void OnHostChanged(string value) => TouchTarget();

    partial void OnPortChanged(string value) => TouchTarget();

    partial void OnUsernameChanged(string value) => TouchTarget();

    partial void OnConfigFileChanged(string value)
    {
        if (value.Length > 0)
        {
            ClearConfigFile = false;
        }

        TouchTarget();
    }

    partial void OnAuthMethodChanged(string value) => Touch();

    partial void OnIdentityFileChanged(string value)
    {
        if (value.Length > 0)
        {
            ClearIdentityFile = false;
        }

        Touch();
    }

    partial void OnPasswordChanged(string value)
    {
        if (value.Length > 0)
        {
            ClearPassword = false;
        }

        Touch();
    }

    partial void OnKeyPassphraseChanged(string value)
    {
        if (value.Length > 0)
        {
            ClearKeyPassphrase = false;
        }

        Touch();
    }

    partial void OnProxyJumpChanged(string value) => TouchTarget();

    partial void OnPlatformChanged(string value) => TouchTarget();

    partial void OnProxyEnabledChanged(bool value) => Touch();

    partial void OnProxyKindChanged(string value) => Touch();

    partial void OnProxyHostChanged(string value) => Touch();

    partial void OnProxyPortChanged(string value) => Touch();

    partial void OnProxyPasswordChanged(string value)
    {
        if (value.Length > 0)
        {
            ClearProxyPassword = false;
        }

        Touch();
    }

    private void TouchTarget()
    {
        NeedsTargetChangeConfirm = false;
        ConfirmTargetChange = false;
        Touch();
    }

    private void Touch()
    {
        SaveFailure = null;
        Validate();
    }

    internal void OpenNew()
    {
        IsNew = true;
        EditingId = null;
        ExpectedRevision = 0;
        Name = "";
        TargetKind = "manual";
        Host = "";
        Port = "22";
        Username = "";
        ConfigFile = "";
        HasSavedConfigFile = false;
        ClearConfigFile = false;
        AuthMethod = "auto";
        IdentityFile = "";
        HasSavedIdentityFile = false;
        ClearIdentityFile = false;
        KeyPassphrase = "";
        ClearKeyPassphrase = false;
        Password = "";
        HasSavedPassword = false;
        ClearPassword = false;
        ProxyJump = "";
        Platform = "auto";
        Enabled = true;
        ProxyEnabled = false;
        ProxyKind = "socks5";
        ProxyHost = "";
        ProxyPort = "";
        ProxyUsername = "";
        ProxyPassword = "";
        HasSavedProxyPassword = false;
        ClearProxyPassword = false;
        NeedsTargetChangeConfirm = false;
        ConfirmTargetChange = false;
        SaveFailure = null;
        Validate();
        IsOpen = true;
    }

    internal void OpenEdit(EnvironmentDto dto)
    {
        IsNew = false;
        EditingId = dto.Id;
        ExpectedRevision = dto.Revision;
        Name = dto.Name;
        TargetKind = dto.Target?.Kind is "config" ? "config" : "manual";
        Host = dto.Target?.Host ?? "";
        Port = (dto.Target?.Port ?? 22).ToString(CultureInfo.InvariantCulture);
        Username = dto.Target?.Username ?? "";
        ConfigFile = "";
        HasSavedConfigFile = dto.Target?.HasConfigFile ?? false;
        ClearConfigFile = false;
        AuthMethod = string.IsNullOrWhiteSpace(dto.AuthMethod) ? "auto" : dto.AuthMethod;
        IdentityFile = "";
        HasSavedIdentityFile = dto.Target?.HasIdentityFile ?? false;
        ClearIdentityFile = false;
        KeyPassphrase = "";
        ClearKeyPassphrase = false;
        Password = "";
        HasSavedPassword = dto.HasSavedPassword;
        ClearPassword = false;
        ProxyJump = dto.Target?.ProxyJump ?? "";
        Platform = string.IsNullOrWhiteSpace(dto.Platform) ? "auto" : dto.Platform;
        Enabled = dto.Enabled;
        ProxyEnabled = dto.Proxy is not null;
        ProxyKind = dto.Proxy?.Kind ?? "socks5";
        ProxyHost = dto.Proxy?.Host ?? "";
        ProxyPort = dto.Proxy is { } p ? p.Port.ToString(CultureInfo.InvariantCulture) : "";
        ProxyUsername = dto.Proxy?.Username ?? "";
        ProxyPassword = "";
        HasSavedProxyPassword = dto.Proxy?.HasPassword ?? false;
        ClearProxyPassword = false;
        NeedsTargetChangeConfirm = false;
        ConfirmTargetChange = false;
        SaveFailure = null;
        Validate();
        IsOpen = true;
    }

    internal void ClearSecretDrafts()
    {
        Password = "";
        KeyPassphrase = "";
        ProxyPassword = "";
    }

    internal (EnvironmentInput Profile, EnvironmentSecretsInput? Secrets) ToRequest()
    {
        var host = Host.Trim();
        var identity = ResolveOptionalPath(IdentityFile, ClearIdentityFile);
        var configFile = IsConfig ? ResolveOptionalPath(ConfigFile, ClearConfigFile) : null;
        SshTargetInput target = IsManual
            ? new("manual", host, ParsePort(Port) ?? 22, Username.Trim(), identity, null, string.IsNullOrWhiteSpace(ProxyJump) ? null : ProxyJump.Trim())
            : new("config", host, null, null, identity, configFile, null);
        SshProxyInput? proxy = ProxyEnabled
            ? new(ProxyKind, ProxyHost.Trim(), ParsePort(ProxyPort) ?? 0, string.IsNullOrWhiteSpace(ProxyUsername) ? null : ProxyUsername.Trim())
            : null;
        var profile = new EnvironmentInput(Name.Trim(), target, AuthMethod, Platform, Enabled, proxy);

        var password = ResolveOptionalSecret(Password, ClearPassword);
        var passphrase = ResolveOptionalSecret(KeyPassphrase, ClearKeyPassphrase);
        var proxyPassword = ProxyEnabled ? ResolveOptionalSecret(ProxyPassword, ClearProxyPassword) : (HasSavedProxyPassword ? "" : null);
        var secrets = password is null && passphrase is null && proxyPassword is null
            ? null
            : new EnvironmentSecretsInput(password, passphrase, proxyPassword);
        return (profile, secrets);
    }

    private static string? ResolveOptionalPath(string input, bool clear) =>
        clear ? "" : string.IsNullOrWhiteSpace(input) ? null : input.Trim();

    private static string? ResolveOptionalSecret(string input, bool clear) =>
        clear ? "" : input.Length == 0 ? null : input;

    private static int? ParsePort(string raw) =>
        int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535 ? port : null;

    private void Validate()
    {
        var list = new List<EnvironmentProblem>();
        var name = Name.Trim();
        if (name.Length is 0 or > 120 || name.Any(char.IsControl))
        {
            list.Add(EnvironmentProblem.Name);
        }

        var host = Host.Trim();
        if (host.Length is 0 or > 255 || host.StartsWith('-') || host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            list.Add(EnvironmentProblem.Host);
        }

        if (IsManual)
        {
            if (ParsePort(Port) is null)
            {
                list.Add(EnvironmentProblem.Port);
            }

            var user = Username.Trim();
            if (user.Length is 0 or > 128 || user.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                list.Add(EnvironmentProblem.Username);
            }
        }

        if (ProxyEnabled)
        {
            var proxyHost = ProxyHost.Trim();
            if (proxyHost.Length is 0 or > 255 || proxyHost.StartsWith('-') || proxyHost.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                list.Add(EnvironmentProblem.ProxyHost);
            }

            if (ParsePort(ProxyPort) is null)
            {
                list.Add(EnvironmentProblem.ProxyPort);
            }
        }

        Problems = list;
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanTest));
    }
}

/// <summary>
/// 设置 · 设备 · 执行环境(docs/design/06 §7 / §10):SSH 服务器列表、实时连接状态(<see cref="ClientStores.Environments"/> 镜像)、
/// 新增 / 编辑 / 删除、测试连接、从 <c>~/.ssh/config</c> 导入、已知主机指纹查看与忘记。
/// 角色:读取与连接 / 断开 member 起,配置写入 / 测试 / 导入 / 已知主机 owner(06 §10.2)。
/// </summary>
public sealed partial class EnvironmentsViewModel : ManagementPageViewModel
{
    private readonly ClientStores _stores;
    private readonly IEnvironmentCommands _commands;
    private readonly Func<IDialogService> _dialogs;
    private readonly Dictionary<string, CancellationTokenSource> _connectCts = new(StringComparer.Ordinal);
    private CancellationTokenSource? _testCts;
    private EnvironmentsTopicState? _fallback;

    public EnvironmentsViewModel(SettingsScope scope, ClientStores stores, IEnvironmentCommands commands, Func<IDialogService> dialogs)
        : base(scope, Need.Member)
    {
        _stores = stores;
        _commands = commands;
        _dialogs = dialogs;
        stores.Environments.Changed += SyncFromStore;
        stores.Workspace.Changed += RaiseGates;
    }

    public ObservableCollection<EnvironmentRowViewModel> Items { get; } = [];

    public EnvironmentEditorViewModel Editor { get; } = new();

    public bool HasItems => Items.Count > 0;

    public bool ShowsEmpty => IsReady && Items.Count == 0;

    [ObservableProperty] public partial string DefaultEnvironmentId { get; private set; } = "local";

    public bool IsDefaultRemote => DefaultEnvironmentId != "local";

    /// <summary>member 起即可触发连接 / 断开(06 §10.2)。</summary>
    public Gate ConnectGate => Scope.GateFor(Need.Member);

    public bool CanConnect => ConnectGate.Allowed;

    /// <summary>本机设备且有写权限才能打开本机文件选择框选私钥 / ssh_config。</summary>
    public bool CanPickLocalFile => Scope.IsLocalChannel && CanWrite;

    [ObservableProperty] public partial PendingConfirm? PendingRemove { get; private set; }

    public bool HasPendingRemove => PendingRemove is not null;

    partial void OnPendingRemoveChanged(PendingConfirm? value) => OnPropertyChanged(nameof(HasPendingRemove));

    // ── 测试连接结果(纯 UI 状态) ──
    [ObservableProperty] public partial bool IsTesting { get; private set; }

    [ObservableProperty] public partial EnvironmentTestResult? TestResult { get; private set; }

    public bool HasTestResult => TestResult is not null;

    public bool TestHasHostKeyMismatch => TestResult?.Error?.HostKey is not null;

    public string? TestFactsSummary => TestResult?.Facts is { } f ? $"{f.Os} · {f.Architecture} · {f.Hostname}" : null;

    partial void OnTestResultChanged(EnvironmentTestResult? value)
    {
        OnPropertyChanged(nameof(HasTestResult));
        OnPropertyChanged(nameof(TestHasHostKeyMismatch));
        OnPropertyChanged(nameof(TestFactsSummary));
    }

    // ── 从 ~/.ssh/config 导入 ──
    [ObservableProperty] public partial bool IsImportOpen { get; set; }

    [ObservableProperty] public partial string ImportConfigFile { get; set; } = "";

    [ObservableProperty] public partial bool IsImporting { get; private set; }

    [ObservableProperty] public partial Failure? ImportFailure { get; private set; }

    public ObservableCollection<SshConfigCandidateRowViewModel> ImportCandidates { get; } = [];

    public ObservableCollection<string> ImportWarnings { get; } = [];

    public bool HasImportCandidates => ImportCandidates.Count > 0;

    public bool HasImportWarnings => ImportWarnings.Count > 0;

    public bool ShowsImportEmpty => IsImportOpen && !IsImporting && ImportFailure is null && ImportCandidates.Count == 0;

    // ── 已知主机指纹 ──
    [ObservableProperty] public partial bool IsKnownHostsOpen { get; set; }

    [ObservableProperty] public partial bool IsLoadingKnownHosts { get; private set; }

    [ObservableProperty] public partial Failure? KnownHostsFailure { get; private set; }

    public ObservableCollection<KnownHostRowViewModel> KnownHosts { get; } = [];

    public bool HasKnownHosts => KnownHosts.Count > 0;

    public bool ShowsKnownHostsEmpty => IsKnownHostsOpen && !IsLoadingKnownHosts && KnownHostsFailure is null && KnownHosts.Count == 0;

    protected override void OnGatesChanged()
    {
        OnPropertyChanged(nameof(ConnectGate));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanPickLocalFile));
    }

    protected override async Task LoadAsync()
    {
        if (Scope.DeviceId is not { } device)
        {
            return;
        }

        _fallback = null;
        TestResult = null;
        CancelAllConnects();
        var mirror = _stores.Environments.State.Of(device);
        if (mirror.Known)
        {
            Rebuild(known: true, mirror.DefaultId, mirror.Environments);
            Ready();
            return;
        }

        var result = await _commands.ListAsync(device);
        if (!IsCurrent(device))
        {
            return;
        }

        if (!result.IsOk)
        {
            Fail(result);
            return;
        }

        _fallback = result.Value;
        Rebuild(known: true, result.Value!.DefaultId, result.Value.Environments);
        Ready();
    }

    protected override void OnUnavailable()
    {
        CancelAllConnects();
        CancelTest();
        if (Scope.DeviceId is { } device)
        {
            var mirror = _stores.Environments.State.Of(device);
            var list = mirror.Environments.Length > 0 ? (IEnumerable<EnvironmentDto>)mirror.Environments : _fallback?.Environments ?? [];
            Rebuild(known: false, mirror.DefaultId, list);
        }

        Editor.IsOpen = false;
        IsImportOpen = false;
        IsKnownHostsOpen = false;
        PendingRemove = null;
    }

    private void SyncFromStore()
    {
        if (Scope.DeviceId is not { } device || !Scope.IsConnected || !ReadGate.Allowed)
        {
            return;
        }

        var mirror = _stores.Environments.State.Of(device);
        if (!mirror.Known)
        {
            return;
        }

        _fallback = null;
        Rebuild(known: true, mirror.DefaultId, mirror.Environments);
        if (State != PageState.Ready)
        {
            Ready();
        }
    }

    private void Rebuild(bool known, string defaultId, IEnumerable<EnvironmentDto> environments)
    {
        DefaultEnvironmentId = string.IsNullOrEmpty(defaultId) ? "local" : defaultId;
        OnPropertyChanged(nameof(IsDefaultRemote));
        var rows = environments
            .Where(static e => e.Kind == EnvironmentKindNames.Ssh)
            .OrderBy(static e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static e => e.Id, StringComparer.Ordinal)
            .Select(e => new EnvironmentRowViewModel(this, e, known))
            .ToList();
        Items.Clear();
        foreach (var row in rows)
        {
            Items.Add(row);
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowsEmpty));
        SyncImportStatuses();
    }

    private void SyncImportStatuses()
    {
        if (ImportCandidates.Count == 0)
        {
            return;
        }

        var existing = Items.Where(static i => i.IsConfigTarget).Select(static i => i.Dto.Target?.Host ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in ImportCandidates)
        {
            candidate.AlreadyImported = existing.Contains(candidate.Alias);
        }
    }

    // ───────────────────────── 新增 / 编辑 / 删除 ─────────────────────────

    [RelayCommand]
    private void New()
    {
        if (!CanWrite)
        {
            return;
        }

        PendingRemove = null;
        IsImportOpen = false;
        TestResult = null;
        Editor.OpenNew();
    }

    internal void Edit(EnvironmentRowViewModel row)
    {
        if (!CanWrite)
        {
            return;
        }

        PendingRemove = null;
        IsImportOpen = false;
        TestResult = null;
        Editor.OpenEdit(row.Dto);
    }

    [RelayCommand]
    private void CloseEditor()
    {
        CancelTest();
        Editor.ClearSecretDrafts();
        Editor.IsOpen = false;
    }

    [RelayCommand]
    private async Task PickIdentityFileAsync()
    {
        if (!CanPickLocalFile)
        {
            return;
        }

        var picked = await _dialogs().PickFileAsync(FilePickKind.SshIdentityFile);
        if (!string.IsNullOrWhiteSpace(picked?.Path))
        {
            Editor.IdentityFile = picked.Path;
        }
    }

    [RelayCommand]
    private async Task PickConfigFileAsync()
    {
        if (!CanPickLocalFile)
        {
            return;
        }

        var picked = await _dialogs().PickFileAsync(FilePickKind.SshConfigFile);
        if (!string.IsNullOrWhiteSpace(picked?.Path))
        {
            Editor.ConfigFile = picked.Path;
        }
    }

    [RelayCommand]
    private async Task PickImportConfigFileAsync()
    {
        if (!CanPickLocalFile)
        {
            return;
        }

        var picked = await _dialogs().PickFileAsync(FilePickKind.SshConfigFile);
        if (!string.IsNullOrWhiteSpace(picked?.Path))
        {
            ImportConfigFile = picked.Path;
            await LoadImportCandidatesAsync();
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Scope.DeviceId is not { } device || !CanWrite || !Editor.CanSave)
        {
            return;
        }

        Editor.IsSaving = true;
        Editor.SaveFailure = null;
        try
        {
            var (profile, secrets) = Editor.ToRequest();
            var result = Editor.IsNew
                ? await _commands.CreateAsync(device, profile, secrets)
                : await _commands.UpdateAsync(device, Editor.EditingId!, Editor.ExpectedRevision, profile, secrets, Editor.ConfirmTargetChange);
            if (!result.IsOk)
            {
                if (result.ErrorKey == EnvironmentErrorKeys.TargetChangeNeedsConfirm)
                {
                    Editor.NeedsTargetChangeConfirm = true;
                }

                Editor.SaveFailure = Failure.Of(result);
                return;
            }

            Editor.ClearSecretDrafts();
            Editor.IsOpen = false;
            await RefreshFallbackIfNeededAsync(device);
        }
        finally
        {
            Editor.IsSaving = false;
        }
    }

    internal async Task SetEnabledAsync(EnvironmentRowViewModel row, bool enabled)
    {
        if (Scope.DeviceId is not { } device || !CanWrite || row.Dto.Target is not { } target)
        {
            return;
        }

        var targetInput = target.Kind == "config"
            ? new SshTargetInput("config", target.Host)
            : new SshTargetInput("manual", target.Host, target.Port ?? 22, target.Username, null, null, target.ProxyJump);
        var proxyInput = row.Dto.Proxy is { } p ? new SshProxyInput(p.Kind, p.Host, p.Port, p.Username) : null;
        var profile = new EnvironmentInput(row.Name, targetInput, row.AuthMethod, row.Platform, enabled, proxyInput);
        var result = await Act(_commands.UpdateAsync(device, row.Id, row.Revision, profile));
        if (result.IsOk)
        {
            await RefreshFallbackIfNeededAsync(device);
        }
    }

    internal async Task SetDefaultAsync(string environmentId)
    {
        if (Scope.DeviceId is not { } device || !CanWrite)
        {
            return;
        }

        var result = await Act(_commands.SetDefaultAsync(device, environmentId));
        if (result.IsOk)
        {
            await RefreshFallbackIfNeededAsync(device);
        }
    }

    [RelayCommand]
    private Task ResetDefaultToLocalAsync() => SetDefaultAsync("local");

    internal void AskRemove(EnvironmentRowViewModel row)
    {
        if (!CanWrite)
        {
            return;
        }

        Editor.IsOpen = false;
        PendingRemove = new PendingConfirm(row.Name, row.Id);
    }

    [RelayCommand]
    private void CancelRemove() => PendingRemove = null;

    [RelayCommand]
    private async Task ConfirmRemoveAsync()
    {
        if (Scope.DeviceId is not { } device || PendingRemove is not { } pending || !CanWrite)
        {
            return;
        }

        PendingRemove = null;
        var result = await Act(_commands.RemoveAsync(device, pending.Id));
        if (result.IsOk)
        {
            await RefreshFallbackIfNeededAsync(device);
        }
    }

    // ───────────────────────── 连接 / 断开 / 测试 ─────────────────────────

    internal async Task ConnectAsync(EnvironmentRowViewModel row)
    {
        if (Scope.DeviceId is not { } device || !CanConnect)
        {
            return;
        }

        CancelConnect(row);
        var cts = new CancellationTokenSource();
        _connectCts[row.Id] = cts;
        ActionFailure = null;
        try
        {
            var result = await _commands.ConnectAsync(device, row.Id, cts.Token);
            if (!result.IsOk && !cts.IsCancellationRequested)
            {
                ActionFailure = Failure.Of(result);
            }
            else if (result.IsOk)
            {
                await RefreshFallbackIfNeededAsync(device);
            }
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消连接
        }
        finally
        {
            if (_connectCts.TryGetValue(row.Id, out var current) && ReferenceEquals(current, cts))
            {
                _connectCts.Remove(row.Id);
            }

            cts.Dispose();
        }
    }

    internal void CancelConnect(EnvironmentRowViewModel row)
    {
        if (_connectCts.Remove(row.Id, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    internal async Task DisconnectAsync(EnvironmentRowViewModel row)
    {
        if (Scope.DeviceId is not { } device || !CanConnect)
        {
            return;
        }

        var result = await Act(_commands.DisconnectAsync(device, row.Id));
        if (result.IsOk)
        {
            await RefreshFallbackIfNeededAsync(device);
        }
    }

    [RelayCommand]
    private Task TestDraftAsync()
    {
        if (!CanWrite || !Editor.CanTest)
        {
            return Task.CompletedTask;
        }

        var (profile, secrets) = Editor.ToRequest();
        return RunTestAsync(profile, Editor.IsNew ? null : Editor.EditingId, secrets);
    }

    internal Task TestSavedAsync(EnvironmentRowViewModel row)
    {
        if (!CanWrite || row.Dto.Target is not { } target)
        {
            return Task.CompletedTask;
        }

        var targetInput = target.Kind == "config"
            ? new SshTargetInput("config", target.Host)
            : new SshTargetInput("manual", target.Host, target.Port ?? 22, target.Username, null, null, target.ProxyJump);
        var proxyInput = row.Dto.Proxy is { } p ? new SshProxyInput(p.Kind, p.Host, p.Port, p.Username) : null;
        var profile = new EnvironmentInput(row.Name, targetInput, row.AuthMethod, row.Platform, row.IsEnabled, proxyInput);
        return RunTestAsync(profile, row.Id, null);
    }

    [RelayCommand]
    private void CancelTest()
    {
        if (_testCts is not null)
        {
            _testCts.Cancel();
            _testCts.Dispose();
            _testCts = null;
        }
    }

    [RelayCommand]
    private void DismissTestResult() => TestResult = null;

    [RelayCommand]
    private Task ForgetTestMismatchHostKeyAsync() =>
        TestResult?.Error?.HostKey is { } m ? ForgetHostKeyAsync(m.HostName) : Task.CompletedTask;

    private async Task RunTestAsync(EnvironmentInput profile, string? existingId, EnvironmentSecretsInput? secrets)
    {
        if (Scope.DeviceId is not { } device || !CanWrite)
        {
            return;
        }

        CancelTest();
        var cts = new CancellationTokenSource();
        _testCts = cts;
        IsTesting = true;
        TestResult = null;
        ActionFailure = null;
        try
        {
            var result = await _commands.TestAsync(device, profile, existingId, secrets, cts.Token);
            if (!IsCurrent(device))
            {
                return;
            }

            if (result.IsOk)
            {
                TestResult = result.Value;
            }
            else if (!cts.IsCancellationRequested)
            {
                ActionFailure = Failure.Of(result);
            }
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消测试
        }
        finally
        {
            if (ReferenceEquals(_testCts, cts))
            {
                _testCts = null;
            }

            cts.Dispose();
            IsTesting = false;
        }
    }

    // ───────────────────────── 从 ~/.ssh/config 导入 ─────────────────────────

    [RelayCommand]
    private async Task OpenImportAsync()
    {
        if (!CanWrite)
        {
            return;
        }

        Editor.IsOpen = false;
        PendingRemove = null;
        IsImportOpen = true;
        await LoadImportCandidatesAsync();
    }

    [RelayCommand]
    private void CloseImport() => IsImportOpen = false;

    [RelayCommand]
    private async Task LoadImportCandidatesAsync()
    {
        if (Scope.DeviceId is not { } device || !CanWrite)
        {
            return;
        }

        IsImporting = true;
        ImportFailure = null;
        try
        {
            var path = string.IsNullOrWhiteSpace(ImportConfigFile) ? null : ImportConfigFile.Trim();
            var result = await _commands.ImportSshConfigAsync(device, path);
            if (!IsCurrent(device))
            {
                return;
            }

            ImportCandidates.Clear();
            ImportWarnings.Clear();
            if (!result.IsOk)
            {
                ImportFailure = Failure.Of(result);
                return;
            }

            var existing = Items.Where(static i => i.IsConfigTarget).Select(static i => i.Dto.Target?.Host ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in result.Value!.Candidates)
            {
                ImportCandidates.Add(new SshConfigCandidateRowViewModel(this, candidate, existing.Contains(candidate.Alias)));
            }

            foreach (var warning in result.Value.Warnings)
            {
                ImportWarnings.Add(warning);
            }
        }
        finally
        {
            IsImporting = false;
            OnPropertyChanged(nameof(HasImportCandidates));
            OnPropertyChanged(nameof(HasImportWarnings));
            OnPropertyChanged(nameof(ShowsImportEmpty));
        }
    }

    internal async Task ImportCandidateAsync(SshConfigCandidateRowViewModel candidate)
    {
        if (Scope.DeviceId is not { } device || !CanWrite || !candidate.CanImport)
        {
            return;
        }

        var configFile = string.IsNullOrWhiteSpace(ImportConfigFile) ? null : ImportConfigFile.Trim();
        var profile = new EnvironmentInput(candidate.Alias, new SshTargetInput("config", candidate.Alias, ConfigFile: configFile));
        var result = await Act(_commands.CreateAsync(device, profile));
        if (result.IsOk)
        {
            candidate.AlreadyImported = true;
            await RefreshFallbackIfNeededAsync(device);
        }
    }

    // ───────────────────────── 已知主机指纹 ─────────────────────────

    [RelayCommand]
    private async Task ToggleKnownHostsAsync()
    {
        if (!CanWrite)
        {
            return;
        }

        IsKnownHostsOpen = !IsKnownHostsOpen;
        if (IsKnownHostsOpen)
        {
            await LoadKnownHostsAsync();
        }
    }

    [RelayCommand]
    private async Task LoadKnownHostsAsync()
    {
        if (Scope.DeviceId is not { } device || !CanWrite)
        {
            return;
        }

        IsLoadingKnownHosts = true;
        KnownHostsFailure = null;
        try
        {
            var result = await _commands.KnownHostsAsync(device);
            if (!IsCurrent(device))
            {
                return;
            }

            KnownHosts.Clear();
            if (!result.IsOk)
            {
                KnownHostsFailure = Failure.Of(result);
                return;
            }

            foreach (var host in result.Value!.Hosts.OrderBy(static h => h.HostName, StringComparer.OrdinalIgnoreCase))
            {
                KnownHosts.Add(new KnownHostRowViewModel(this, host));
            }
        }
        finally
        {
            IsLoadingKnownHosts = false;
            OnPropertyChanged(nameof(HasKnownHosts));
            OnPropertyChanged(nameof(ShowsKnownHostsEmpty));
        }
    }

    internal async Task ForgetHostKeyAsync(string hostName)
    {
        if (Scope.DeviceId is not { } device || !CanWrite || string.IsNullOrWhiteSpace(hostName))
        {
            return;
        }

        var result = await Act(_commands.ForgetHostKeyAsync(device, hostName));
        if (result.IsOk && IsKnownHostsOpen)
        {
            await LoadKnownHostsAsync();
        }
    }

    private async Task RefreshFallbackIfNeededAsync(string device)
    {
        if (_stores.Environments.State.Of(device).Known)
        {
            return;
        }

        var list = await _commands.ListAsync(device);
        if (list.IsOk && IsCurrent(device))
        {
            _fallback = list.Value;
            Rebuild(known: true, list.Value!.DefaultId, list.Value.Environments);
        }
    }

    private void CancelAllConnects()
    {
        foreach (var cts in _connectCts.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _connectCts.Clear();
    }
}

/// <summary>未接入环境命令时的空替身(测试与未初始化壳用)。</summary>
internal sealed class UnavailableEnvironmentCommands : IEnvironmentCommands
{
    public static readonly UnavailableEnvironmentCommands Instance = new();

    private static Task<ActionResult<T>> Unsupported<T>() => Task.FromResult(new ActionResult<T>(ActionOutcome.Unsupported, default, EnvironmentErrorKeys.NoSshServices, null));

    public Task<ActionResult<EnvironmentsTopicState>> ListAsync(string deviceId, CancellationToken ct = default) => Unsupported<EnvironmentsTopicState>();

    public Task<ActionResult<EnvironmentDto>> CreateAsync(string deviceId, EnvironmentInput profile, EnvironmentSecretsInput? secrets = null, CancellationToken ct = default) => Unsupported<EnvironmentDto>();

    public Task<ActionResult<EnvironmentDto>> UpdateAsync(
        string deviceId, string environmentId, long expectedRevision, EnvironmentInput profile, EnvironmentSecretsInput? secrets = null, bool confirmTargetChange = false, CancellationToken ct = default) => Unsupported<EnvironmentDto>();

    public Task<ActionResult<Unit>> RemoveAsync(string deviceId, string environmentId, CancellationToken ct = default) => Unsupported<Unit>();

    public Task<ActionResult<Unit>> SetDefaultAsync(string deviceId, string environmentId, CancellationToken ct = default) => Unsupported<Unit>();

    public Task<ActionResult<EnvironmentDto>> ConnectAsync(string deviceId, string environmentId, CancellationToken ct = default) => Unsupported<EnvironmentDto>();

    public Task<ActionResult<EnvironmentDto>> DisconnectAsync(string deviceId, string environmentId, CancellationToken ct = default) => Unsupported<EnvironmentDto>();

    public Task<ActionResult<EnvironmentTestResult>> TestAsync(string deviceId, EnvironmentInput profile, string? existingId = null, EnvironmentSecretsInput? secrets = null, CancellationToken ct = default) => Unsupported<EnvironmentTestResult>();

    public Task<ActionResult<RemoteDirectoryDto>> BrowseAsync(string deviceId, string environmentId, string? path = null, CancellationToken ct = default) => Unsupported<RemoteDirectoryDto>();

    public Task<ActionResult<ProjectDto>> AddProjectAsync(string deviceId, string environmentId, string remotePath, string? name = null, CancellationToken ct = default) => Unsupported<ProjectDto>();

    public Task<ActionResult<SshConfigImportResult>> ImportSshConfigAsync(string deviceId, string? configFile = null, CancellationToken ct = default) => Unsupported<SshConfigImportResult>();

    public Task<ActionResult<KnownHostsResult>> KnownHostsAsync(string deviceId, CancellationToken ct = default) => Unsupported<KnownHostsResult>();

    public Task<ActionResult<Unit>> ForgetHostKeyAsync(string deviceId, string hostName, CancellationToken ct = default) => Unsupported<Unit>();

    public Task<InteractionResult> AnswerHostKeyAsync(string deviceId, SshHostKeyInteractionDto interaction, bool trust, CancellationToken ct = default) =>
        Task.FromResult(new InteractionResult(InteractionOutcome.Unavailable));

    public Task<InteractionResult> AnswerCredentialAsync(string deviceId, SshCredentialInteractionDto interaction, IReadOnlyList<string>? answers, bool remember = false, CancellationToken ct = default) =>
        Task.FromResult(new InteractionResult(InteractionOutcome.Unavailable));
}
