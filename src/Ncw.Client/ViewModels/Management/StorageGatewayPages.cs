using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

// ═══════════════════════════ 存储 ═══════════════════════════

/// <summary>危险操作的种类(<c>storage.cleanup.*</c> 的 kind + 恢复备份 + 应用导入)。</summary>
public enum DangerKind
{
    CleanupAge,
    CleanupHistory,
    CleanupLocalData,
    Restore,
    Import,
}

/// <summary>危险操作的状态机:预览(拿一次性令牌)→ 向用户展示数字并等确认 → 执行 → 结果。</summary>
public enum DangerStage
{
    Idle,
    Previewing,
    Confirming,
    Executing,
    Done,
}

/// <summary>
/// 设置 · 设备 · 存储:统计、整理(vacuum)、备份状态与手动备份、回收附件、导出(owner,远程也行),
/// 以及<b>危险操作</b>(按年龄清理 / 清空历史 / 清除本地数据 / 恢复备份 / 应用导入):owner + 仅本机通道 + 二次确认令牌 —
/// 先预览拿到令牌并向用户展示将被删除 / 覆盖的数量,用户确认(清空历史与清除数据还要勾选「我已了解」)后才执行;令牌 10 分钟过期,过期要求重新预览。
/// 备份目录与频率是设备设置(<c>data.*</c>)。统计 / 备份状态 member 起可读。
/// </summary>
public sealed partial class StorageViewModel : ManagementPageViewModel
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);
    public static readonly IReadOnlyList<string> Ages = ["7d", "15d", "1", "3", "6", "12"];
    public static readonly IReadOnlyList<string> Frequencies = ["manual", "daily", "weekly"];

    private readonly ClientStores _stores;
    private readonly IStorageCommands _commands;
    private readonly ISettingsCommands _settings;
    private readonly Func<IDialogService> _dialogs;
    private readonly TimeProvider _clock;
    private DateTimeOffset _previewAt;
    private string? _confirmToken;
    private string? _restorePath;
    private string? _uploadId;
    private readonly IDataRollbackCommands? _rollback;

    public StorageViewModel(SettingsScope scope, ClientStores stores, IStorageCommands commands, ISettingsCommands settings, Func<IDialogService> dialogs, TimeProvider clock,
        IDataRollbackCommands? rollback = null)
        : base(scope, Need.Member)
    {
        _rollback = rollback;
        _stores = stores;
        _commands = commands;
        _settings = settings;
        _dialogs = dialogs;
        _clock = clock;
        stores.Settings.Changed += SyncBackupSettings;
        stores.Workspace.Changed += RaiseGates;
    }

    [ObservableProperty] public partial StorageStatsDto? Stats { get; private set; }

    [ObservableProperty] public partial BackupStatusDto? Backup { get; private set; }

    /// <summary>危险操作的闸门:owner 且本机通道(远程 owner 不行)。</summary>
    public Gate DangerGate => Scope.GateFor(Need.LocalOwner);

    public Gate OwnerGate => Scope.GateFor(Need.Owner);

    protected override void OnGatesChanged()
    {
        OnPropertyChanged(nameof(DangerGate));
        OnPropertyChanged(nameof(ShowsRollback));
        OnPropertyChanged(nameof(CanRollBack));
    }

    protected override void OnUnavailable()
    {
        Stats = null;
        Backup = null;
        Rollback = null;
        CancelDanger();
    }

    protected override async Task LoadAsync()
    {
        var device = Scope.DeviceId!;
        var stats = await _commands.GetStatsAsync(device);
        if (!IsCurrent(device))
        {
            return;
        }

        if (!stats.IsOk)
        {
            Fail(stats);
            return;
        }

        var backup = await _commands.GetBackupStatusAsync(device);
        Stats = stats.Value;
        Backup = backup.IsOk ? backup.Value : null;
        SyncBackupSettings();
        Ready();
        await LoadRollbackAsync();
    }

    // ───────────────────────── 回退到 2.x(docs/design/04 §5,只对本机设备) ─────────────────────────

    /// <summary>能不能回退;null = 没问过 / 不适用(远程设备、没有宿主)。</summary>
    [ObservableProperty] public partial DataRollbackInfo? Rollback { get; private set; }

    /// <summary>「我已了解:迁移之后在 3.0 里新写的数据不会带回 2.x」。</summary>
    [ObservableProperty] public partial bool RollbackAcknowledged { get; set; }

    [ObservableProperty] public partial bool RollingBack { get; private set; }

    [ObservableProperty] public partial Failure? RollbackFailure { get; private set; }

    /// <summary>只在本机 owner、并且这份数据确实是从 2.x 接管来的(接管前的备份还在)时显示。</summary>
    public bool ShowsRollback => _rollback is not null && DangerGate.Allowed && Rollback is { Available: true };

    public bool CanRollBack => ShowsRollback && RollbackAcknowledged && !RollingBack;

    partial void OnRollbackChanged(DataRollbackInfo? value)
    {
        OnPropertyChanged(nameof(ShowsRollback));
        OnPropertyChanged(nameof(CanRollBack));
    }

    partial void OnRollbackAcknowledgedChanged(bool value) => OnPropertyChanged(nameof(CanRollBack));

    partial void OnRollingBackChanged(bool value) => OnPropertyChanged(nameof(CanRollBack));

    private async Task LoadRollbackAsync()
    {
        if (_rollback is null || !DangerGate.Allowed || Scope.DeviceId is not { } device)
        {
            return;
        }

        var info = await _rollback.GetRollbackInfoAsync();
        if (IsCurrent(device))
        {
            Rollback = info.IsOk ? info.Value : null;
        }
    }

    /// <summary>登记回退 → 本机 Agent 关库后执行 → 应用退出。失败时停在这里并说明原因(什么都没停、没退出)。</summary>
    [RelayCommand]
    private async Task RollBackToLegacyAsync()
    {
        if (_rollback is null || !CanRollBack)
        {
            return;
        }

        RollingBack = true;
        RollbackFailure = null;
        try
        {
            var result = await _rollback.RollBackToLegacyAsync();
            if (result.IsOk)
            {
                Rollback = result.Value;
            }
            else
            {
                RollbackFailure = Failure.Of(result);
            }
        }
        finally
        {
            RollingBack = false;
        }
    }

    /// <summary>撤回还没执行的回退请求(例如退出被取消,用户改主意了)。</summary>
    [RelayCommand]
    private async Task CancelRollbackAsync()
    {
        if (_rollback is null)
        {
            return;
        }

        var result = await _rollback.CancelRollbackAsync();
        if (result.IsOk)
        {
            Rollback = result.Value;
            RollbackAcknowledged = false;
        }
        else
        {
            RollbackFailure = Failure.Of(result);
        }
    }

    // ───────────────────────── 备份设置(设备设置的 data 段) ─────────────────────────

    [ObservableProperty] public partial string BackupDirectory { get; set; } = "";

    [ObservableProperty] public partial string BackupFrequency { get; set; } = "manual";

    [ObservableProperty] public partial bool BackupDirty { get; private set; }

    private bool _syncing;

    partial void OnBackupDirectoryChanged(string value)
    {
        if (!_syncing)
        {
            BackupDirty = true;
        }
    }

    private void SyncBackupSettings()
    {
        if (Scope.DeviceId is not { } device || BackupDirty)
        {
            return;
        }

        if (_stores.Settings.State.Of(device) is { Known: true, Settings: { } s })
        {
            _syncing = true;
            BackupDirectory = s.Data.BackupDirectory ?? "";
            BackupFrequency = s.Data.BackupFrequency;
            _syncing = false;
        }
    }

    [RelayCommand]
    private async Task ApplyBackupSettingsAsync()
    {
        if (Scope.DeviceId is not { } device || !OwnerGate.Allowed)
        {
            return;
        }

        var result = await Act(_settings.UpdateAsync(device, new DeviceSettingsPatchDto(Data: new DataSettingsPatchDto(BackupDirectory.Trim(), BackupFrequency))));
        if (result.IsOk)
        {
            BackupDirty = false;
            SyncBackupSettings();
            var status = await _commands.GetBackupStatusAsync(device);
            Backup = status.IsOk ? status.Value : Backup;
        }
    }

    /// <summary>选备份目录(只有本机设备:路径必须是设备上的路径)。</summary>
    [RelayCommand]
    private async Task PickBackupDirectoryAsync()
    {
        if (!Scope.IsLocalChannel || !OwnerGate.Allowed)
        {
            return;
        }

        var folder = await _dialogs().PickFolderAsync("");
        if (folder is not null)
        {
            BackupDirectory = folder;
        }
    }

    // ───────────────────────── 不会丢内容的维护(owner,远程也行) ─────────────────────────

    [ObservableProperty] public partial CleanupResultDto? AttachmentsResult { get; private set; }

    [ObservableProperty] public partial bool BackupJustCreated { get; private set; }

    [ObservableProperty] public partial bool Vacuumed { get; private set; }

    [ObservableProperty] public partial bool ExportDone { get; private set; }

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        if (Scope.DeviceId is not { } device || !OwnerGate.Allowed)
        {
            return;
        }

        BackupJustCreated = false;
        var result = await Act(_commands.CreateBackupAsync(device));
        if (result.IsOk)
        {
            Backup = result.Value;
            BackupJustCreated = true;
        }
    }

    [RelayCommand]
    private async Task VacuumAsync()
    {
        if (Scope.DeviceId is not { } device || !OwnerGate.Allowed)
        {
            return;
        }

        Vacuumed = false;
        var result = await Act(_commands.VacuumAsync(device));
        if (result.IsOk)
        {
            Stats = result.Value;
            Vacuumed = true;
        }
    }

    [RelayCommand]
    private async Task CleanAttachmentsAsync()
    {
        if (Scope.DeviceId is not { } device || !OwnerGate.Allowed)
        {
            return;
        }

        AttachmentsResult = null;
        var result = await Act(_commands.CleanupAttachmentsAsync(device));
        if (result.IsOk)
        {
            AttachmentsResult = result.Value;
            var stats = await _commands.GetStatsAsync(device);
            Stats = stats.IsOk ? stats.Value : Stats;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Scope.DeviceId is not { } device || !OwnerGate.Allowed)
        {
            return;
        }

        ExportDone = false;
        await using var stream = await _dialogs().PickSaveStreamAsync(FilePickKind.ExportData, $"nextcowork-export-{_clock.GetUtcNow():yyyyMMdd}.json");
        if (stream is null)
        {
            return;
        }

        var result = await Act(_commands.ExportToAsync(device, stream));
        ExportDone = result.IsOk;
    }

    // ───────────────────────── 危险操作 ─────────────────────────

    [ObservableProperty] public partial DangerKind Kind { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming), nameof(IsDangerIdle), nameof(IsWorking), nameof(IsDone))]
    public partial DangerStage Stage { get; private set; }

    /// <summary>按年龄清理时选的年龄(<see cref="Ages"/>)。</summary>
    [ObservableProperty] public partial string Age { get; set; } = "3";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUndeletable))]
    public partial CleanupPreviewDto? CleanupPreview { get; private set; }

    /// <summary>预览里有删不掉的会话(例如正在运行的)。</summary>
    public bool HasUndeletable => CleanupPreview is { Undeletable.Length: > 0 };

    [ObservableProperty] public partial RestorePreviewDto? RestorePreview { get; private set; }

    [ObservableProperty] public partial ImportPreviewDto? ImportPreview { get; private set; }

    [ObservableProperty] public partial CleanupResultDto? CleanupResult { get; private set; }

    [ObservableProperty] public partial BackupStatusDto? RestoreResult { get; private set; }

    [ObservableProperty] public partial ImportResultDto? ImportResult { get; private set; }

    /// <summary>用户勾选了「我已了解这无法撤销」(清空历史 / 清除数据 / 恢复备份 / 导入必须勾选)。纯 UI 状态。</summary>
    [ObservableProperty] public partial bool Acknowledged { get; set; }

    /// <summary>危险操作失败的原因(例如 <c>storage.busy</c>:有运行中的 Agent;<c>storage.confirmationRequired</c>:令牌过期)。</summary>
    [ObservableProperty] public partial Failure? DangerFailure { get; private set; }

    public bool IsDangerIdle => Stage == DangerStage.Idle;

    public bool IsConfirming => Stage == DangerStage.Confirming;

    public bool IsWorking => Stage is DangerStage.Previewing or DangerStage.Executing;

    public bool IsDone => Stage == DangerStage.Done;

    /// <summary>需要勾选「我已了解」才能确认(不可撤销的几类)。</summary>
    public bool NeedsAcknowledge => Kind is DangerKind.CleanupHistory or DangerKind.CleanupLocalData or DangerKind.Restore or DangerKind.Import;

    public bool CanConfirm => IsConfirming && (!NeedsAcknowledge || Acknowledged);

    partial void OnAcknowledgedChanged(bool value) => OnPropertyChanged(nameof(CanConfirm));

    partial void OnStageChanged(DangerStage value) => OnPropertyChanged(nameof(CanConfirm));

    [RelayCommand]
    private Task PreviewCleanupAsync(DangerKind kind) => StartAsync(kind);

    private async Task StartAsync(DangerKind kind)
    {
        if (Scope.DeviceId is not { } device || !DangerGate.Allowed || Stage is DangerStage.Previewing or DangerStage.Executing)
        {
            return;
        }

        CancelDanger();
        Kind = kind;
        Stage = DangerStage.Previewing;
        DangerFailure = null;
        OnPropertyChanged(nameof(NeedsAcknowledge));
        switch (kind)
        {
            case DangerKind.Restore:
            {
                var file = await _dialogs().PickFileAsync(FilePickKind.BackupFile);
                if (file?.Path is not { } path)
                {
                    Stage = DangerStage.Idle;
                    return;
                }

                var preview = await _commands.PreviewRestoreAsync(device, path);
                if (!preview.IsOk)
                {
                    Stage = DangerStage.Idle;
                    DangerFailure = Failure.Of(preview);
                    return;
                }

                (_restorePath, _confirmToken, RestorePreview) = (path, preview.Value!.ConfirmToken, preview.Value);
                break;
            }

            case DangerKind.Import:
            {
                var file = await _dialogs().PickFileAsync(FilePickKind.ImportData);
                if (file is null)
                {
                    Stage = DangerStage.Idle;
                    return;
                }

                await using var source = await file.OpenRead();
                var preview = await _commands.ImportFromAsync(device, source);
                if (!preview.IsOk)
                {
                    Stage = DangerStage.Idle;
                    DangerFailure = Failure.Of(preview);
                    return;
                }

                (_uploadId, _confirmToken, ImportPreview) = (preview.Value!.UploadId, preview.Value.ConfirmToken, preview.Value);
                break;
            }

            default:
            {
                var (name, age) = kind switch
                {
                    DangerKind.CleanupAge => ("age", Age),
                    DangerKind.CleanupHistory => ("history", null),
                    _ => ("local-data", null),
                };
                var preview = await _commands.PreviewCleanupAsync(device, name, age);
                if (!preview.IsOk || preview.Value!.ConfirmToken is null)
                {
                    Stage = DangerStage.Idle;
                    DangerFailure = preview.IsOk ? new Failure(ActionOutcome.Failed, "storage.confirmationRequired") : Failure.Of(preview);
                    return;
                }

                (_confirmToken, CleanupPreview) = (preview.Value.ConfirmToken, preview.Value);
                break;
            }
        }

        _previewAt = _clock.GetUtcNow();
        Acknowledged = false;
        Stage = DangerStage.Confirming;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (Scope.DeviceId is not { } device || !DangerGate.Allowed || !CanConfirm || _confirmToken is null)
        {
            return;
        }

        // 令牌 10 分钟过期:不拿一枚过期的令牌去试,回到预览前(用户重新看一遍数字)
        if (_clock.GetUtcNow() - _previewAt > TokenLifetime)
        {
            var kind = Kind;
            CancelDanger();
            DangerFailure = new Failure(ActionOutcome.Forbidden, "storage.confirmationRequired");
            Kind = kind;
            return;
        }

        Stage = DangerStage.Executing;
        DangerFailure = null;
        var token = _confirmToken;
        switch (Kind)
        {
            case DangerKind.Restore:
            {
                var result = await _commands.RestoreBackupAsync(device, _restorePath!, token);
                Finish(result, r => RestoreResult = r);
                break;
            }

            case DangerKind.Import:
            {
                var result = await _commands.ApplyImportAsync(device, _uploadId!, token);
                Finish(result, r => ImportResult = r);
                break;
            }

            default:
            {
                var preview = CleanupPreview!;
                var result = await _commands.ExecuteCleanupAsync(device, preview.Kind, Kind == DangerKind.CleanupAge ? Age : null, token);
                Finish(result, r => CleanupResult = r);
                break;
            }
        }

        if (Stage == DangerStage.Done)
        {
            var stats = await _commands.GetStatsAsync(device);
            Stats = stats.IsOk ? stats.Value : Stats;
        }
    }

    private void Finish<T>(ActionResult<T> result, Action<T> onOk)
    {
        _confirmToken = null; // 令牌一次性
        if (result.IsOk)
        {
            onOk(result.Value!);
            Stage = DangerStage.Done;
        }
        else
        {
            DangerFailure = Failure.Of(result);
            Stage = DangerStage.Idle;
        }
    }

    /// <summary>取消(或确认前放弃):已上传的导入文件在设备上丢弃,令牌作废。</summary>
    [RelayCommand]
    private void CancelDanger()
    {
        if (_uploadId is { } upload && Scope.DeviceId is { } device)
        {
            _ = _commands.DiscardImportAsync(device, upload);
        }

        (_confirmToken, _restorePath, _uploadId) = (null, null, null);
        (CleanupPreview, RestorePreview, ImportPreview) = (null, null, null);
        (CleanupResult, RestoreResult, ImportResult) = (null, null, null);
        Acknowledged = false;
        Stage = DangerStage.Idle;
    }

    [RelayCommand]
    private void DismissDone()
    {
        (CleanupResult, RestoreResult, ImportResult) = (null, null, null);
        Stage = DangerStage.Idle;
    }
}

// ═══════════════════════════ 网关 ═══════════════════════════

/// <summary>网关密钥的一行(viewer / member 各一把)。密钥只在本机 owner 点「显示」后短暂持有,不进 Store,离开页面即清掉。</summary>
public sealed partial class GatewayKeyRowViewModel(GatewayViewModel owner, string role) : ObservableObject
{
    public string Role { get; } = role;

    [ObservableProperty] public partial string? Key { get; set; }

    [ObservableProperty] public partial bool IsLoading { get; set; }

    public bool IsRevealed => Key is not null;

    partial void OnKeyChanged(string? value) => OnPropertyChanged(nameof(IsRevealed));

    [RelayCommand]
    private Task RevealAsync() => owner.RevealAsync(this);

    [RelayCommand]
    private void Hide() => Key = null;

    [RelayCommand]
    private void AskRotate() => owner.AskRotate(this);
}

/// <summary>
/// 设置 · 设备 · 网关(<c>gateway.*</c>):开关(仅本机 owner)、实际可访问的地址、OpenAPI 链接、API 密钥显示与轮换(仅本机 owner,轮换二次确认)、供应商健康与重置、
/// 「允许局域网访问」(设备设置 <c>gateway.allowRemote</c>,打开前确认明文 HTTP 的风险)。网关是明文 HTTP:密钥以 Bearer 头在网络上明文传输,所以默认只绑回环地址。
/// 读取需要 owner(<c>gateway.getStatus</c> 是 owner 方法),所以 member / viewer 只看到说明。
/// </summary>
public sealed partial class GatewayViewModel : ManagementPageViewModel
{
    private readonly ClientStores _stores;
    private readonly IGatewayCommands _commands;
    private readonly ISettingsCommands _settings;
    private readonly Func<IUrlLauncher> _launcher;

    public GatewayViewModel(SettingsScope scope, ClientStores stores, IGatewayCommands commands, ISettingsCommands settings, Func<IUrlLauncher> launcher) : base(scope, Need.Owner)
    {
        _stores = stores;
        _commands = commands;
        _settings = settings;
        _launcher = launcher;
        Keys = [new GatewayKeyRowViewModel(this, "viewer"), new GatewayKeyRowViewModel(this, "member")];
        stores.Workspace.Changed += RaiseGates;
        stores.Settings.Changed += () => OnPropertyChanged(nameof(PreferredPort));
    }

    public IReadOnlyList<GatewayKeyRowViewModel> Keys { get; }

    public ObservableCollection<string> Addresses { get; } = [];

    [ObservableProperty] public partial GatewayStatusDto? Status { get; private set; }

    [ObservableProperty] public partial PendingConfirm? PendingRotate { get; private set; }

    /// <summary>用户正在确认「允许局域网访问」(明文 HTTP 的风险)。</summary>
    [ObservableProperty] public partial bool ConfirmingAllowRemote { get; private set; }

    public bool HasPendingRotate => PendingRotate is not null;

    partial void OnPendingRotateChanged(PendingConfirm? value) => OnPropertyChanged(nameof(HasPendingRotate));

    /// <summary>开关 / 密钥只有本机 owner。</summary>
    public Gate LocalGate => Scope.GateFor(Need.LocalOwner);

    protected override void OnGatesChanged() => OnPropertyChanged(nameof(LocalGate));

    public bool IsEnabled => Status?.Enabled ?? false;

    public bool AllowRemote => Status?.AllowRemote ?? false;

    public bool IsListening => Status is { Enabled: true, Port: not null, Error: null };

    /// <summary>绑定的不是回环地址:明文 HTTP 上的密钥会被同一网络里的人看到,要比平时更醒目地提示。</summary>
    public bool ExposedToLan => Status is { Enabled: true } s && (s.AllowRemote || s.Host != "127.0.0.1");

    public int PreferredPort => Scope.DeviceId is { } d && _stores.Settings.State.Of(d).Settings is { } s ? s.Gateway.PreferredPort : 0;

    public string? OpenApiUrl => IsListening && Addresses.Count > 0 ? $"{Addresses[0].TrimEnd('/')}/api/openapi.json" : null;

    protected override void OnUnavailable()
    {
        Status = null; // 未知,不是「已关闭」
        HideKeys();
        PendingRotate = null;
    }

    protected override async Task LoadAsync()
    {
        var device = Scope.DeviceId!;
        var status = await _commands.GetStatusAsync(device);
        if (!IsCurrent(device))
        {
            return;
        }

        if (!status.IsOk)
        {
            Fail(status);
            return;
        }

        Status = status.Value;
        await LoadAddressesAsync(device);
        Ready();
        Notify();
    }

    private async Task LoadAddressesAsync(string device)
    {
        Addresses.Clear();
        if (Status is { Enabled: true })
        {
            var addresses = await _commands.GetAddressesAsync(device);
            if (addresses.IsOk && IsCurrent(device))
            {
                foreach (var a in addresses.Value!.Addresses)
                {
                    Addresses.Add(a);
                }
            }
        }
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(AllowRemote));
        OnPropertyChanged(nameof(IsListening));
        OnPropertyChanged(nameof(ExposedToLan));
        OnPropertyChanged(nameof(OpenApiUrl));
        OnPropertyChanged(nameof(PreferredPort));
    }

    /// <summary>开关(仅本机 owner)。</summary>
    [RelayCommand]
    private async Task SetEnabledAsync(bool enabled)
    {
        if (Scope.DeviceId is not { } device || !LocalGate.Allowed)
        {
            Notify();
            return;
        }

        var result = await Act(_commands.SetEnabledAsync(device, enabled));
        if (result.IsOk && IsCurrent(device))
        {
            Status = result.Value;
            if (!enabled)
            {
                HideKeys();
            }

            await LoadAddressesAsync(device);
        }

        Notify();
    }

    /// <summary>点「允许局域网访问」:关 → 直接关;开 → 先出确认卡片(明文 HTTP 风险),确认后才写设置。</summary>
    [RelayCommand]
    private async Task ToggleAllowRemoteAsync()
    {
        if (!WriteGate.Allowed)
        {
            return;
        }

        if (!AllowRemote)
        {
            ConfirmingAllowRemote = true;
            return;
        }

        await SetAllowRemoteAsync(false);
    }

    [RelayCommand]
    private Task ConfirmAllowRemoteAsync()
    {
        ConfirmingAllowRemote = false;
        return SetAllowRemoteAsync(true);
    }

    [RelayCommand]
    private void CancelAllowRemote() => ConfirmingAllowRemote = false;

    private async Task SetAllowRemoteAsync(bool allow)
    {
        if (Scope.DeviceId is not { } device || !WriteGate.Allowed)
        {
            return;
        }

        var result = await Act(_settings.UpdateAsync(device, new DeviceSettingsPatchDto(Gateway: new GatewaySettingsPatchDto(AllowRemote: allow))));
        if (result.IsOk)
        {
            var status = await _commands.GetStatusAsync(device);
            if (status.IsOk && IsCurrent(device))
            {
                Status = status.Value;
                await LoadAddressesAsync(device);
            }
        }

        Notify();
    }

    internal async Task RevealAsync(GatewayKeyRowViewModel row)
    {
        if (Scope.DeviceId is not { } device || !LocalGate.Allowed)
        {
            return;
        }

        row.IsLoading = true;
        var result = await Act(_commands.GetKeyAsync(device, row.Role));
        row.IsLoading = false;
        row.Key = result.IsOk && IsCurrent(device) ? result.Value!.Key : null;
    }

    internal void AskRotate(GatewayKeyRowViewModel row) => PendingRotate = LocalGate.Allowed ? new PendingConfirm(row.Role, row.Role) : null;

    [RelayCommand]
    private void CancelRotate() => PendingRotate = null;

    [RelayCommand]
    private async Task ConfirmRotateAsync()
    {
        if (PendingRotate is not { } pending || Scope.DeviceId is not { } device || !LocalGate.Allowed)
        {
            return;
        }

        PendingRotate = null;
        var row = Keys.First(k => k.Role == pending.Id);
        var result = await Act(_commands.RotateKeyAsync(device, pending.Id));
        if (result.IsOk && IsCurrent(device))
        {
            row.Key = result.Value!.Key; // 旧密钥立刻失效;新密钥只在这里短暂可见
        }
    }

    [RelayCommand]
    private async Task ResetHealthAsync(string? providerId)
    {
        if (Scope.DeviceId is not { } device || !WriteGate.Allowed)
        {
            return;
        }

        var result = await Act(_commands.ResetHealthAsync(device, string.IsNullOrEmpty(providerId) ? null : providerId));
        if (result.IsOk)
        {
            Status = result.Value;
            Notify();
        }
    }

    [RelayCommand]
    private async Task OpenApiAsync()
    {
        if (OpenApiUrl is { } url)
        {
            await _launcher().OpenAsync(url);
        }
    }

    /// <summary>离开页面 / 设备断开 / 关闭网关:清掉内存里的密钥。</summary>
    [RelayCommand]
    public void HideKeys()
    {
        foreach (var key in Keys)
        {
            key.Key = null;
        }
    }
}
