using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Transcript;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>「已在另一台设备上处理」:回答被别人抢先了。<see cref="ResolvedBy"/> 是回答方的 clientId(中止时为 null),<see cref="At"/> 是什么时候。</summary>
public sealed record HandledNotice(string InteractionId, string Kind, string? ResolvedBy, DateTimeOffset At, bool Aborted);

public sealed partial class AskOptionViewModel(AskUserOptionDto option) : ObservableObject
{
    public string Label { get; } = option.Label;

    public string? Description { get; } = option.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

public sealed partial class AskQuestionViewModel : ObservableObject
{
    public AskQuestionViewModel(AskUserQuestionDto question)
    {
        Header = question.Header;
        Question = question.Question;
        MultiSelect = question.MultiSelect;
        AllowFreeform = question.AllowFreeform;
        Options = [.. question.Options.Select(o => new AskOptionViewModel(o))];
    }

    public string Header { get; }

    public string Question { get; }

    public bool MultiSelect { get; }

    public bool AllowFreeform { get; }

    public IReadOnlyList<AskOptionViewModel> Options { get; }

    /// <summary>自由输入(<see cref="AllowFreeform"/> 才显示)。</summary>
    [ObservableProperty] public partial string Freeform { get; set; } = "";

    /// <summary>点选一个选项:单选互斥,多选切换。</summary>
    public void Toggle(AskOptionViewModel option)
    {
        if (MultiSelect)
        {
            option.IsSelected = !option.IsSelected;
            return;
        }

        foreach (var o in Options)
        {
            o.IsSelected = ReferenceEquals(o, option) && !o.IsSelected;
        }
    }

    public string[] Values()
    {
        var values = Options.Where(o => o.IsSelected).Select(o => o.Label).ToList();
        if (AllowFreeform && !string.IsNullOrWhiteSpace(Freeform))
        {
            values.Add(Freeform.Trim());
        }

        return [.. values];
    }
}

/// <summary>
/// 一张待处理的交互卡片(工具审批、提问、计划批准、目标提案,以及不认识的种类)。事实在设备上:挂起的交互全量到达 Store,
/// 这里只在派生行变化时更新;点按钮就是一次 <see cref="IConversationCommands.RespondAsync"/>(带着看到的版本号),别人抢先处理了 → 通知上层显示「已在另一台设备上处理」。
/// 审批卡片必须带执行环境标识(设备 + 环境,<see cref="EnvLabel"/>)。
/// </summary>
public sealed partial class InteractionRowViewModel : RowViewModel<InteractionRow>
{
    private readonly string _deviceId;
    private readonly IConversationCommands _commands;
    private readonly Func<string, string> _deviceName;
    private readonly Action<HandledNotice> _handled;
    private readonly TimeProvider _clock;

    public InteractionRowViewModel(InteractionRow row, string deviceId, IConversationCommands commands, Func<string, string> deviceName, Action<HandledNotice> handled, TimeProvider clock)
        : base(row)
    {
        _deviceId = deviceId;
        _commands = commands;
        _deviceName = deviceName;
        _handled = handled;
        _clock = clock;
        Build();
    }

    public InteractionDto Interaction => Row.Interaction;

    public string InteractionId => Row.Interaction.Id;

    // ── 种类 ──
    public bool IsToolPermission => Row.Interaction is ToolPermissionInteractionDto;

    public bool IsAskUser => Row.Interaction is AskUserInteractionDto;

    public bool IsPlan => Row.Interaction is PlanApprovalInteractionDto;

    public bool IsGoalProposal => Row.Interaction is GoalProposalInteractionDto;

    public bool IsWorkflow => Row.Interaction is WorkflowApprovalInteractionDto;

    public bool IsWidget => Row.Interaction is WidgetSubmitInteractionDto;

    // ── 工具审批 ──
    public string ToolName => (Row.Interaction as ToolPermissionInteractionDto)?.ToolName ?? "";

    /// <summary>被审批的调用的参数摘要(命令 / 路径),一行。</summary>
    public string Target { get; private set; } = "";

    public string? EnvLabel => Row.Interaction is ToolPermissionInteractionDto { Env: var env } ? $"{_deviceName(env.DeviceId)} · {env.Name}" : null;

    public bool IsSsh => Row.Interaction is ToolPermissionInteractionDto { Env.Type: "ssh" };

    public bool IsDangerous => Row.Interaction is ToolPermissionInteractionDto { Risk: "dangerous" } or ToolPermissionInteractionDto { Destructive: true };

    public bool IsReadOnlyTool => Row.Interaction is ToolPermissionInteractionDto { ReadOnly: true };

    public bool NeedsNetwork => Row.Interaction is ToolPermissionInteractionDto { NeedsNetwork: true };

    public bool OutsideWorkspace => Row.Interaction is ToolPermissionInteractionDto { OutsideWorkspace: true };

    public string? SuggestedRule => (Row.Interaction as ToolPermissionInteractionDto)?.SuggestedRule;

    public bool HasSuggestedRule => !string.IsNullOrWhiteSpace(SuggestedRule);

    /// <summary>「总是允许」能记住的范围(设备给的)。</summary>
    public ObservableCollection<string> Scopes { get; } = [];

    /// <summary>选中的记住范围(默认第一个:最窄的)。</summary>
    [ObservableProperty] public partial string? SelectedScope { get; set; }

    public bool CanAlwaysAllow => Scopes.Count > 0;

    // ── 提问 ──
    public IReadOnlyList<AskQuestionViewModel> Questions { get; private set; } = [];

    // ── 计划 / 目标 ──
    public string Plan => (Row.Interaction as PlanApprovalInteractionDto)?.Plan ?? "";

    [ObservableProperty] public partial string Feedback { get; set; } = "";

    public string Condition => (Row.Interaction as GoalProposalInteractionDto)?.Condition ?? "";

    public string WidgetTitle => (Row.Interaction as WidgetSubmitInteractionDto)?.Title ?? "";

    // ── 回答状态 ──
    [ObservableProperty] public partial bool IsBusy { get; private set; }

    /// <summary>最近一次回答没成功的原因(成功后卡片随 Store 更新消失)。</summary>
    [ObservableProperty] public partial InteractionOutcome? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    private void Build()
    {
        Scopes.Clear();
        if (Row.Interaction is ToolPermissionInteractionDto tool)
        {
            foreach (var scope in tool.AllowedScopes)
            {
                Scopes.Add(scope);
            }

            Target = TranscriptBuilder.TargetOf(tool.ToolName, tool.InputJson);
        }

        SelectedScope = Scopes.Contains(SelectedScope ?? "") ? SelectedScope : Scopes.FirstOrDefault();
        if (Row.Interaction is AskUserInteractionDto ask && Questions.Count == 0)
        {
            Questions = [.. ask.Questions.Select(q => new AskQuestionViewModel(q))];
        }
    }

    /// <summary>派生行更新时(同一个交互换了版本):重建派生状态,但保住用户已经选的 / 输入的。</summary>
    public bool Refresh(InteractionRow row)
    {
        var changed = Update(row);
        if (changed)
        {
            CancelHoldOnRefresh();
            Build();
            Problem = null;
        }

        return changed;
    }

    // ───────────────────────── 回答 ─────────────────────────

    [RelayCommand]
    private Task AllowOnceAsync() => RespondAsync(new ToolPermissionResponseDto(PermissionChoices.AllowOnce));

    [RelayCommand]
    private Task AlwaysAllowAsync() => RespondAsync(new ToolPermissionResponseDto(PermissionChoices.AllowAlways, SelectedScope));

    [RelayCommand]
    private Task DenyAsync() => RespondAsync(new ToolPermissionResponseDto(PermissionChoices.Deny, Reason: Feedback.Length > 0 ? Feedback : null));

    [RelayCommand]
    private Task SubmitAnswersAsync() => RespondAsync(new AskUserResponseDto([.. Questions.Select(q => new AskUserAnswerDto(q.Values()))]));

    /// <summary>关掉提问框(不是回答)。</summary>
    [RelayCommand]
    private Task DismissAskAsync() => RespondAsync(new AskUserResponseDto(null));

    [RelayCommand]
    private Task ApprovePlanAsync() => RespondAsync(new PlanApprovalResponseDto("approveCurrent"));

    [RelayCommand]
    private Task RequestRevisionAsync() => RespondAsync(new PlanApprovalResponseDto("requestRevision", Feedback.Length > 0 ? Feedback : null));

    [RelayCommand]
    private Task RejectPlanAsync() => RespondAsync(new PlanApprovalResponseDto("reject"));

    [RelayCommand]
    private Task ApproveGoalAsync() => RespondAsync(new GoalProposalResponseDto(true));

    [RelayCommand]
    private Task RejectGoalAsync() => RespondAsync(new GoalProposalResponseDto(false));

    [RelayCommand]
    private Task RunWorkflowAsync() => RespondAsync(new WorkflowApprovalResponseDto("run"));

    [RelayCommand]
    private Task CancelWorkflowAsync() => RespondAsync(new WorkflowApprovalResponseDto("cancel"));

    [RelayCommand]
    private Task DismissWidgetAsync() => RespondAsync(new WidgetSubmitResponseDto(null));

    private async Task RespondAsync(InteractionResponseDto response)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            ApplyOutcome(await _commands.RespondAsync(_deviceId, Row.Interaction, response));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>回答的结局。Answered:卡片随 Store 的全量交互更新消失;AlreadyHandled:别人抢先了,通知上层显示谁、什么时候。</summary>
    public void ApplyOutcome(InteractionResult result)
    {
        switch (result.Outcome)
        {
            case InteractionOutcome.Answered:
                Problem = null;
                break;
            case InteractionOutcome.AlreadyHandled:
                Problem = null;
                var resolution = result.Resolution;
                _handled(new HandledNotice(
                    InteractionId, Row.Interaction.GetType().Name, resolution?.ResolvedBy,
                    resolution is null ? _clock.GetUtcNow() : DateTimeOffset.FromUnixTimeMilliseconds(resolution.At), resolution?.Status == "aborted"));
                break;
            default:
                Problem = result.Outcome;
                break;
        }

        OnPropertyChanged(nameof(HasProblem));
    }
}
