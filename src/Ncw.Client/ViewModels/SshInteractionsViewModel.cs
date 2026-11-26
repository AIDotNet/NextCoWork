using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>凭据提问中的单个输入项(密码 / 口令 / 键盘交互式的一道题)。值只在内存里短暂持有,提交或关闭即清空。</summary>
public sealed partial class SshPromptFieldViewModel(string prompt, bool echo) : ObservableObject
{
    public string Prompt { get; } = prompt;

    public bool HasPrompt => !string.IsNullOrWhiteSpace(Prompt);

    /// <summary>true = 服务器允许明文回显;false = 密码框掩码。</summary>
    public bool Echo { get; } = echo;

    [ObservableProperty] public partial string Value { get; set; } = "";
}

/// <summary>
/// 一条挂起的 SSH 设备级交互(<see cref="SshHostKeyInteractionDto"/> 或 <see cref="SshCredentialInteractionDto"/>)。
/// 真源在设备的 <c>interactions</c> 主题(<see cref="ClientStores.Conversations"/>);回答经 <see cref="IEnvironmentCommands"/> 送出,
/// 先到先得(别的控制端抢先时报告 <see cref="InteractionOutcome.AlreadyHandled"/>)。
/// </summary>
public sealed partial class SshInteractionPromptViewModel : ObservableObject
{
    private readonly IEnvironmentCommands _commands;
    private readonly Action<InteractionResult>? _onResolved;

    public SshInteractionPromptViewModel(string deviceId, string deviceName, InteractionDto interaction, IEnvironmentCommands commands, Action<InteractionResult>? onResolved = null)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        Interaction = interaction;
        _commands = commands;
        _onResolved = onResolved;

        if (interaction is SshHostKeyInteractionDto hostKey)
        {
            EnvLabel = $"{deviceName} · {hostKey.Env.Name}";
            HostName = hostKey.HostName;
            Algorithm = hostKey.Algorithm;
            Fingerprint = hostKey.Fingerprint;
        }
        else if (interaction is SshCredentialInteractionDto cred)
        {
            EnvLabel = $"{deviceName} · {cred.Env.Name}";
            PromptKind = cred.PromptKind;
            HostName = cred.HostName;
            UserName = cred.UserName;
            KeyName = cred.KeyName;
            Instruction = cred.Instruction;
            PreviousRejected = cred.PreviousRejected;
            CanRemember = cred.CanRemember;
            HasSaved = cred.HasSaved;
            if (cred.Prompts.Length > 0)
            {
                foreach (var p in cred.Prompts)
                {
                    Prompts.Add(new SshPromptFieldViewModel(p.Text, p.Echo));
                }
            }
            else
            {
                Prompts.Add(new SshPromptFieldViewModel("", false));
            }

            foreach (var field in Prompts)
            {
                field.PropertyChanged += (_, _) => SubmitCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string DeviceId { get; }

    public string DeviceName { get; }

    public InteractionDto Interaction { get; }

    public string InteractionId => Interaction.Id;

    public bool IsHostKey => Interaction is SshHostKeyInteractionDto;

    public bool IsCredential => Interaction is SshCredentialInteractionDto;

    public string EnvLabel { get; } = "";

    public string HostName { get; } = "";

    // ── 主机指纹 ──
    public string Algorithm { get; } = "";

    public string Fingerprint { get; } = "";

    public string DisplayFingerprint => Fingerprint.Length > 0 ? $"SHA256:{Fingerprint}" : "";

    // ── 凭据提问 ──
    public string PromptKind { get; } = "";

    public string UserName { get; } = "";

    public string? KeyName { get; }

    public bool HasKeyName => !string.IsNullOrWhiteSpace(KeyName);

    public string? Instruction { get; }

    public bool HasInstruction => !string.IsNullOrWhiteSpace(Instruction);

    public bool PreviousRejected { get; }

    public bool CanRemember { get; }

    public bool HasSaved { get; }

    public ObservableCollection<SshPromptFieldViewModel> Prompts { get; } = [];

    [ObservableProperty] public partial bool Remember { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrustCommand), nameof(RejectCommand), nameof(SubmitCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial InteractionOutcome? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    private bool CanAct() => !IsBusy;

    private bool CanSubmit() => !IsBusy && IsCredential && Prompts.Count > 0 && Prompts.All(static p => p.Value.Length > 0);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task TrustAsync() => AnswerHostKeyAsync(trust: true);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task RejectAsync() => AnswerHostKeyAsync(trust: false);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private Task SubmitAsync() => AnswerCredentialAsync(cancelled: false);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task CancelAsync() => IsHostKey ? AnswerHostKeyAsync(trust: false) : AnswerCredentialAsync(cancelled: true);

    private async Task AnswerHostKeyAsync(bool trust)
    {
        if (Interaction is not SshHostKeyInteractionDto hostKey || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Problem = null;
        try
        {
            var result = await _commands.AnswerHostKeyAsync(DeviceId, hostKey, trust);
            ApplyResult(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AnswerCredentialAsync(bool cancelled)
    {
        if (Interaction is not SshCredentialInteractionDto cred || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Problem = null;
        try
        {
            IReadOnlyList<string>? answers = cancelled ? null : [.. Prompts.Select(static p => p.Value)];
            var result = await _commands.AnswerCredentialAsync(DeviceId, cred, answers, !cancelled && CanRemember && Remember);
            if (result.Outcome == InteractionOutcome.Answered || cancelled)
            {
                ClearValues();
            }

            ApplyResult(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyResult(InteractionResult result)
    {
        if (result.Outcome == InteractionOutcome.Answered)
        {
            _onResolved?.Invoke(result);
            return;
        }

        Problem = result.Outcome;
        if (result.Outcome == InteractionOutcome.AlreadyHandled)
        {
            _onResolved?.Invoke(result);
        }
    }

    private void ClearValues()
    {
        foreach (var field in Prompts)
        {
            field.Value = "";
        }
    }
}

/// <summary>
/// 全局 SSH 设备级交互协调器(docs/design/06 §10.4):聚合所有已连接设备上挂起的 <see cref="SshHostKeyInteractionDto"/> 与 <see cref="SshCredentialInteractionDto"/>。
/// 桌面端浮在窗口之上(或在新建项目弹窗内联展示),手机端挂在设备页顶部,任何控制端都能抢答。
/// </summary>
public sealed partial class SshInteractionsViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly IEnvironmentCommands _commands;
    private readonly Dictionary<string, SshInteractionPromptViewModel> _cache = new(StringComparer.Ordinal);

    public SshInteractionsViewModel(ClientStores stores, IEnvironmentCommands commands)
    {
        _stores = stores;
        _commands = commands;
        stores.Conversations.Changed += Refresh;
        stores.Workspace.Changed += Refresh;
        Refresh();
    }

    public ObservableCollection<SshInteractionPromptViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending), nameof(ShowsGlobalOverlay))]
    public partial SshInteractionPromptViewModel? Current { get; private set; }

    [ObservableProperty] public partial int QueueCount { get; private set; }

    public bool HasPending => Current is not null;

    /// <summary>有模态对话框(如新建项目窗口)内联承载交互时,暂隐外壳的全局浮层避免双重显示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsGlobalOverlay))]
    public partial bool SuppressGlobalOverlay { get; set; }

    public bool ShowsGlobalOverlay => HasPending && !SuppressGlobalOverlay;

    private void Refresh()
    {
        var workspace = _stores.Workspace.State;
        var conversations = _stores.Conversations.State;
        var active = new List<(string DeviceId, string DeviceName, InteractionDto Interaction)>();

        foreach (var (deviceId, device) in workspace.Devices)
        {
            if (device.Connection != DeviceConnection.Connected)
            {
                continue;
            }

            foreach (var interaction in conversations.InteractionsOf(deviceId).Pending)
            {
                if (interaction is SshHostKeyInteractionDto or SshCredentialInteractionDto)
                {
                    active.Add((deviceId, device.Name, interaction));
                }
            }
        }

        active.Sort(static (a, b) => a.Interaction.CreatedAt.CompareTo(b.Interaction.CreatedAt));

        var liveKeys = new HashSet<string>(StringComparer.Ordinal);
        var next = new List<SshInteractionPromptViewModel>(active.Count);
        foreach (var (deviceId, deviceName, interaction) in active)
        {
            var key = $"{deviceId}\0{interaction.Id}\0{interaction.Version}";
            liveKeys.Add(key);
            if (!_cache.TryGetValue(key, out var vm))
            {
                _cache[key] = vm = new SshInteractionPromptViewModel(deviceId, deviceName, interaction, _commands);
            }

            next.Add(vm);
        }

        foreach (var stale in _cache.Keys.Where(k => !liveKeys.Contains(k)).ToList())
        {
            _cache.Remove(stale);
        }

        if (!next.SequenceEqual(Items))
        {
            Items.Clear();
            foreach (var vm in next)
            {
                Items.Add(vm);
            }
        }

        Current = Items.FirstOrDefault();
        QueueCount = Items.Count;
    }
}
