using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>首次启动的几步(docs/design/07 §1)。登录或离线在「欢迎」里完成;「数据」一步显示 2.x 接管的结果(接管本身由数据页盖住窗口完成,见 <see cref="DataGateViewModel"/>)。</summary>
public enum OnboardingStep
{
    Welcome,
    Data,
    Provider,
    Done,
}

/// <summary>
/// 首次启动流程:欢迎(登录 / 使用自己的模型供应商 / 跳过)→ 数据(接管状态)→ 选模型供应商引导 → 完成(打开第一个项目)。
/// <list type="bullet">
/// <item><b>什么时候出现</b>:本控制端的账户还没做过决定(<see cref="AccountMode.Undecided"/>,账户记录不存在)。出现之后一直开着,直到用户点完成;中途退出应用,下次因为已经做过决定而不再出现(设置里随时可以补)。</item>
/// <item><b>状态不重复</b>:登录进度、失败、过期都在 <see cref="AccountViewModel"/>(同一份账户状态),这里只管走到第几步;「数据」一步只读 <see cref="DataGateViewModel"/>。</item>
/// <item>一步一步的前进条件:欢迎 → 数据:账户做出决定(登录成功、离线、跳过)时自动前进;数据 → 供应商:数据没在准备 / 被挡住时可继续。</item>
/// </list>
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject
{
    public const int StepCount = 4;

    private readonly ClientStores _stores;
    private readonly DataGateViewModel _data;
    private readonly SettingsViewModel _settings;
    private readonly Func<Task>? _addProject;
    private readonly Action? _connectSsh;
    private bool _active;
    private bool _finished;

    public OnboardingViewModel(ClientStores stores, AccountViewModel account, DataGateViewModel data, SettingsViewModel settings,
        Func<Task>? addProject = null, Action? connectSsh = null)
    {
        _stores = stores;
        _data = data;
        _settings = settings;
        _addProject = addProject;
        _connectSsh = connectSsh;
        Account = account;
        stores.Account.Changed += Refresh;
        data.PropertyChanged += (_, _) => RaiseDataState();
        Refresh();
    }

    /// <summary>账户的展示状态和登录动作(欢迎一步直接用它)。</summary>
    public AccountViewModel Account { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsDataStep), nameof(IsProvider), nameof(IsDone), nameof(StepNumber), nameof(IsFirstStep))]
    public partial OnboardingStep Step { get; private set; }

    /// <summary>用户在欢迎页选了「使用自己的模型供应商」:供应商一步直接引导去配置。</summary>
    [ObservableProperty] public partial bool WantsOwnProvider { get; private set; }

    /// <summary>首次启动流程是否显示。</summary>
    public bool IsOpen => _active && !_finished;

    public bool IsWelcome => Step == OnboardingStep.Welcome;

    public bool IsDataStep => Step == OnboardingStep.Data;

    public bool IsProvider => Step == OnboardingStep.Provider;

    public bool IsDone => Step == OnboardingStep.Done;

    public bool IsFirstStep => Step == OnboardingStep.Welcome;

    /// <summary>当前是第几步(从 1 开始,给进度点用)。</summary>
    public int StepNumber => (int)Step + 1;

    // ───────────────────────── 数据一步 ─────────────────────────

    /// <summary>数据正在准备 / 接管:等。</summary>
    public bool DataBusy => _data.IsBusy;

    /// <summary>被挡住或失败(例如 2.x 还开着):要重试。</summary>
    public bool DataBlocked => _data.CanRetry;

    public bool DataReady => _data.IsReady;

    /// <summary>被挡住 / 失败的原因(<see cref="DataReasons"/>)。</summary>
    public string? DataReason => _data.Reason;

    /// <summary>刚刚接管了 2.x 的数据(带数量);全新开始为 null。</summary>
    public TakeoverSummary? TakenOver => _data.Summary;

    public bool HasTakeover => _data.Summary is not null;

    /// <summary>设备还没连上(本机 Agent 在启动):显示「正在连接」。</summary>
    public bool DataConnecting => !_data.IsReady && !_data.IsBusy && !_data.CanRetry;

    public bool CanContinueFromData => !DataBusy && !DataBlocked;

    /// <summary>重试被挡住的数据准备(同一个意图,转给数据页)。</summary>
    public IRelayCommand RetryDataCommand => _data.RetryCommand;

    // ───────────────────────── 供应商一步 ─────────────────────────

    /// <summary>已经通过 NextCoWork 账户拿到了模型通道。</summary>
    public bool HasAccountModels => Account.IsActive;

    public bool NeedsOwnProvider => !HasAccountModels;

    // ───────────────────────── 前进 ─────────────────────────

    /// <summary>欢迎:使用自己的模型供应商(不登录,稍后可以再登录;下一步引导去配置)。</summary>
    [RelayCommand]
    private async Task UseOwnProviderAsync()
    {
        WantsOwnProvider = true;
        await Account.UseOfflineCommand.ExecuteAsync(null);
    }

    /// <summary>欢迎:跳过(不登录)。</summary>
    [RelayCommand]
    private async Task SkipAsync()
    {
        WantsOwnProvider = false;
        await Account.UseOfflineCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void Next()
    {
        if (Step == OnboardingStep.Data && !CanContinueFromData)
        {
            return;
        }

        if (Step < OnboardingStep.Done)
        {
            Step++;
            RaiseProviderState();
        }
    }

    [RelayCommand]
    private void Back()
    {
        // 欢迎一步之后账户已经做过决定,不回到欢迎;数据 / 供应商 / 完成之间可以来回
        if (Step > OnboardingStep.Data)
        {
            Step--;
        }
    }

    /// <summary>供应商:打开设置里的「模型」页(自己的供应商在那里添加),流程停在这一步,设置关掉之后可以继续。</summary>
    [RelayCommand]
    private void OpenModelSettings()
    {
        _settings.Section = SettingsSection.Models;
        _settings.OpenCommand.Execute(null);
    }

    /// <summary>完成:打开本机文件夹作为第一个项目,然后结束流程。</summary>
    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        Finish();
        if (_addProject is not null)
        {
            await _addProject();
        }
    }

    /// <summary>完成:前往连接 SSH 主机(打开执行环境设置页),结束首次引导。</summary>
    [RelayCommand]
    private void ConnectSsh()
    {
        Finish();
        if (_connectSsh is not null)
        {
            _connectSsh();
        }
        else
        {
            _settings.Section = SettingsSection.Environments;
            _settings.OpenCommand.Execute(null);
        }
    }

    /// <summary>完成:结束流程,进入主界面。</summary>
    [RelayCommand]
    private void Finish()
    {
        _finished = true;
        OnPropertyChanged(nameof(IsOpen));
    }

    // ───────────────────────── 派生 ─────────────────────────

    private void Refresh()
    {
        var mode = _stores.Account.State.Mode;
        if (mode == AccountMode.Undecided)
        {
            _active = true;
        }

        // 欢迎一步里账户做出了决定(登录成功 / 离线 / 跳过):自动前进到「数据」
        if (_active && !_finished && Step == OnboardingStep.Welcome && mode is AccountMode.Authenticated or AccountMode.Offline)
        {
            Step = OnboardingStep.Data;
        }

        OnPropertyChanged(nameof(IsOpen));
        RaiseProviderState();
    }

    private void RaiseProviderState()
    {
        OnPropertyChanged(nameof(HasAccountModels));
        OnPropertyChanged(nameof(NeedsOwnProvider));
    }

    private void RaiseDataState()
    {
        foreach (var name in new[] { nameof(DataBusy), nameof(DataBlocked), nameof(DataReady), nameof(DataReason), nameof(TakenOver), nameof(HasTakeover), nameof(DataConnecting), nameof(CanContinueFromData) })
        {
            OnPropertyChanged(name);
        }
    }
}
