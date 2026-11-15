using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Client.Updates;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 设置 · 本应用 · 关于(docs/design/07 §3、04 §3):当前版本、通道、检查更新、下载进度、「立即重启并更新」、必需更新的阻断提示、签名校验失败的提示、上次检查时间。
/// <b>只读 Store、只调 Actions</b>(规则 2):事实都在 <see cref="ClientStores.Updates"/>(自更新状态的唯一真源);
/// 「必需 / 阻断」由 Core 的 <see cref="UpdatePolicy"/> 按状态和时钟派生(规则 3,和 Agent 侧共用),这里不另写一份判断。
/// 还在跑的任务数来自本机设备的活动 Run 索引(<c>Workspace</c> 镜像):重启会让 Agent 收尾,提示用户。
/// </summary>
public sealed partial class AboutViewModel : ObservableObject, IDisposable
{
    private readonly ClientStores _stores;
    private readonly UpdateActions _actions;
    private readonly TimeProvider _clock;

    public AboutViewModel(ClientStores stores, UpdateActions actions, TimeProvider? clock = null)
    {
        _stores = stores;
        _actions = actions;
        _clock = clock ?? TimeProvider.System;
        stores.Updates.Changed += Refresh;
        stores.Workspace.Changed += RefreshRuns;
        Refresh();
    }

    public void Dispose()
    {
        _stores.Updates.Changed -= Refresh;
        _stores.Workspace.Changed -= RefreshRuns;
    }

    [ObservableProperty] public partial UpdatePhase Phase { get; private set; }

    [ObservableProperty] public partial string CurrentVersion { get; private set; } = "";

    [ObservableProperty] public partial UpdateChannel Channel { get; private set; }

    /// <summary>待装更新的版本(Available / Downloading / Ready / Installing);没有为 null。</summary>
    [ObservableProperty] public partial string? NewVersion { get; private set; }

    [ObservableProperty] public partial string? Notes { get; private set; }

    /// <summary>下载进度 0–100。</summary>
    [ObservableProperty] public partial int Progress { get; private set; }

    [ObservableProperty] public partial UpdateFailureKind? Failure { get; private set; }

    [ObservableProperty] public partial DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>待装更新对当前版本的约束(派生,见类注释)。</summary>
    [ObservableProperty] public partial UpdateEnforcement Enforcement { get; private set; }

    /// <summary>必需更新的宽限期截止(仍在宽限期内才有;用来显示「在此之前可以继续使用」)。</summary>
    [ObservableProperty] public partial DateTimeOffset? GraceUntil { get; private set; }

    /// <summary>本机设备上还在运行的任务数:重启并更新会中断它们。</summary>
    [ObservableProperty] public partial int ActiveRuns { get; private set; }

    public bool IsAvailable => _actions.IsAvailable;

    public bool IsDisabled => Phase == UpdatePhase.Disabled;

    public bool HasNewVersion => NewVersion is not null;

    public bool IsChecking => Phase == UpdatePhase.Checking;

    public bool IsDownloading => Phase == UpdatePhase.Downloading;

    public bool IsReady => Phase == UpdatePhase.Ready;

    public bool IsInstalling => Phase == UpdatePhase.Installing;

    public bool IsUpToDate => Phase == UpdatePhase.UpToDate;

    /// <summary>有新版本、还没下载。</summary>
    public bool CanDownload => _actions.IsAvailable && Phase == UpdatePhase.Available;

    public bool CanCheck => _actions.IsAvailable && Phase is UpdatePhase.Idle or UpdatePhase.UpToDate or UpdatePhase.Failed or UpdatePhase.Available;

    public bool CanRestart => _actions.IsAvailable && Phase == UpdatePhase.Ready;

    /// <summary>通道只在没有下载 / 待装 / 安装的时候能换。</summary>
    public bool CanChangeChannel => _actions.IsAvailable && Phase is UpdatePhase.Idle or UpdatePhase.UpToDate or UpdatePhase.Failed or UpdatePhase.Available;

    public bool IsBeta => Channel == UpdateChannel.Beta;

    public bool IsIdle => Phase == UpdatePhase.Idle;

    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes) && NewVersion is not null;

    /// <summary>重启会中断本机上还在运行的任务,在「立即重启并更新」可用时提示。</summary>
    public bool ShowsRunWarning => HasActiveRuns && Phase is UpdatePhase.Ready;

    public bool HasFailure => Failure is not null;

    /// <summary>失败了,但不是「不可信」那一类(网络、下载、交接):用普通的问题条提示。</summary>
    public bool ShowsPlainFailure => HasFailure && !IsTrustFailure;

    /// <summary>通道选择器的双向绑定入口:写入走 <see cref="UpdateActions.SetChannelAsync"/>(换不成时界面回到真实的通道)。读出的永远是 Store 里的值。</summary>
    public UpdateChannel SelectedChannel
    {
        get => Channel;
        set
        {
            if (value != Channel && CanChangeChannel)
            {
                _ = SwitchChannelAsync(value);
            }
        }
    }

    private async Task SwitchChannelAsync(UpdateChannel channel)
    {
        await _actions.SetChannelAsync(channel);
        OnPropertyChanged(nameof(SelectedChannel));
    }

    /// <summary>签名 / 清单类的失败:意味着「这份 feed 不可信」,界面要用醒目的方式提示,不能当成普通的网络错误。</summary>
    public bool IsTrustFailure => Failure is UpdateFailureKind.SignatureInvalid or UpdateFailureKind.SignatureMissing or UpdateFailureKind.UnknownSigningKey
        or UpdateFailureKind.ManifestUnsigned or UpdateFailureKind.ManifestRejected or UpdateFailureKind.NoTrustedKeys or UpdateFailureKind.ChecksumMismatch;

    public bool IsRequired => Enforcement == UpdateEnforcement.Required;

    public bool IsBlocking => Enforcement == UpdateEnforcement.Blocking;

    public bool InGracePeriod => Enforcement == UpdateEnforcement.Required && GraceUntil is not null;

    public bool HasLastChecked => LastCheckedAt is not null;

    public bool HasActiveRuns => ActiveRuns > 0;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => _actions.CheckAsync();

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private Task DownloadAsync() => _actions.DownloadAsync();

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task RestartAsync() => _actions.RestartAndApplyAsync();

    [RelayCommand(CanExecute = nameof(CanChangeChannel))]
    private Task SelectChannelAsync(UpdateChannel channel) => _actions.SetChannelAsync(channel);

    private void Refresh()
    {
        var state = _stores.Updates.State;
        Phase = state.Phase;
        CurrentVersion = state.CurrentVersion;
        Channel = state.Channel;
        NewVersion = state.Release?.Version;
        Notes = state.Notes;
        Progress = state.Progress;
        Failure = state.Failure;
        LastCheckedAt = state.LastCheckedAt;
        var now = _clock.GetUtcNow();
        Enforcement = UpdatePolicy.EnforcementOf(state.CurrentVersion, state.Release, now);
        GraceUntil = state.Release is { Mandatory: true, GraceUntil: { } g } && g > now ? g : null;
        RefreshRuns();
        foreach (var name in new[]
        {
            nameof(IsAvailable), nameof(IsDisabled), nameof(HasNewVersion), nameof(IsChecking), nameof(IsDownloading), nameof(IsReady), nameof(IsInstalling), nameof(IsUpToDate),
            nameof(CanDownload), nameof(CanCheck), nameof(CanRestart), nameof(CanChangeChannel), nameof(IsBeta), nameof(HasFailure), nameof(IsTrustFailure),
            nameof(IsRequired), nameof(IsBlocking), nameof(InGracePeriod), nameof(HasLastChecked), nameof(IsIdle), nameof(HasNotes), nameof(ShowsPlainFailure),
            nameof(SelectedChannel), nameof(ShowsRunWarning),
        })
        {
            OnPropertyChanged(name);
        }

        CheckCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        SelectChannelCommand.NotifyCanExecuteChanged();
    }

    private void RefreshRuns()
    {
        var workspace = _stores.Workspace.State;
        var runs = workspace.Devices.Values.Where(d => d.Kind == Ncw.Core.Domain.DeviceKind.Local && d.Connection == Ncw.Core.Domain.DeviceConnection.Connected)
            .Sum(d => d.Index?.Runs.Length ?? 0);
        if (runs != ActiveRuns)
        {
            ActiveRuns = runs;
            OnPropertyChanged(nameof(HasActiveRuns));
            OnPropertyChanged(nameof(ShowsRunWarning));
        }
    }
}
