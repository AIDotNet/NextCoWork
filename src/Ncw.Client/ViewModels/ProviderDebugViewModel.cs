using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>调试弹窗要用到的设备意图(<see cref="ProvidersActions"/> 实现;测试换成假的)。</summary>
public interface IProviderDebugCommands
{
    Task<ActionResult<ProviderDebugModelsResult>> DebugModelsAsync(string deviceId, ProviderDebugTargetDto target, CancellationToken ct = default);

    /// <summary><paramref name="onEvent"/> 在 UI 线程上收到实时进度;取消 <paramref name="ct"/> 抛 <see cref="OperationCanceledException"/>。</summary>
    Task<ActionResult<ProviderDebugResultDto>> DebugRunAsync(string deviceId, ProviderDebugRunParams run, Action<ProviderDebugEventDto>? onEvent = null, CancellationToken ct = default);
}

/// <summary>协议分段上的小圆点:还没试 / 正在跑 / 上一次通过 / 上一次失败。</summary>
public enum DebugProtocolState
{
    Untested,
    Running,
    Ok,
    Failed,
}

/// <summary>右栏的四个标签页。</summary>
public enum DebugTab
{
    Reply,
    Request,
    Response,
    Events,
}

/// <summary>协议分段里的一段。</summary>
public sealed partial class DebugProtocolItem(string protocol) : ObservableObject
{
    public string Protocol { get; } = protocol;

    [ObservableProperty] public partial DebugProtocolState State { get; set; }

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>
/// 供应商调试弹窗(docs/design/07「供应商调试」,方案 A「调试台」):左边配一条请求(模型、协议、提示词、思考、最大输出、带不带工具),右边看结果
/// (回复 / 请求 / 响应 / 事件流)。一次一条请求;协议分段上的圆点记着每个协议上一次的结局。
/// 调试对象是已保存的供应商或「添加 / 编辑供应商」表单的草稿(<see cref="ProviderDebugTargetDto"/>,草稿 key 只用于请求、不在这里保留别的副本)。
/// 弹窗的全部状态都是这一次的纯 UI 状态,不进 Store;权威结果是设备返回的完整记录,实时事件只是先睹为快。
/// </summary>
public sealed partial class ProviderDebugViewModel : ObservableObject
{
    public const int DefaultMaxOutputTokens = 1024;

    /// <summary>事件流最多显示多少行(设备那边也有上限)。</summary>
    public const int MaxRows = 2000;

    private readonly IProviderDebugCommands _commands;
    private readonly string _deviceId;
    private readonly ProviderDebugTargetDto _target;
    private readonly bool _canApplyProtocol;
    private readonly IReadOnlyList<string> _knownModels;
    private CancellationTokenSource? _run;
    private string? _runId;
    private int _lastSeq;
    private string? _lastOkProtocol;

    /// <param name="target">调试对象。</param>
    /// <param name="providerName">标题里的名字。</param>
    /// <param name="baseUrl">显示「最终地址」用(和调试对象里的地址一致)。</param>
    /// <param name="protocol">打开时选中的协议(供应商 / 草稿当前的协议)。</param>
    /// <param name="knownModels">这家已经配过的上游模型(排在候选前面,拉列表失败时也有得选)。</param>
    /// <param name="canApplyProtocol">从编辑弹窗打开:跑通了别的协议可以「使用这个协议」。</param>
    public ProviderDebugViewModel(
        IProviderDebugCommands commands, string deviceId, ProviderDebugTargetDto target, string providerName, string baseUrl, string protocol,
        IReadOnlyList<string>? knownModels = null, bool canApplyProtocol = false)
    {
        _commands = commands;
        _deviceId = deviceId;
        _target = target;
        _canApplyProtocol = canApplyProtocol;
        _knownModels = knownModels ?? [];
        ProviderName = providerName;
        BaseUrl = baseUrl.Trim();
        InitialProtocol = ProviderEndpoints.Protocols.Contains(protocol) ? protocol : ProviderEndpoints.OpenAiChat;
        foreach (var p in ProviderEndpoints.Protocols)
        {
            Protocols.Add(new DebugProtocolItem(p));
        }

        foreach (var known in _knownModels)
        {
            ModelCandidates.Add(known);
        }

        Model = _knownModels.FirstOrDefault() ?? "";
        SelectedProtocol = InitialProtocol;
    }

    public string ProviderName { get; }

    public string BaseUrl { get; }

    public string InitialProtocol { get; }

    /// <summary>点了「使用这个协议」:编辑弹窗据此改草稿的协议。</summary>
    public string? AppliedProtocol { get; private set; }

    /// <summary>界面据此关窗口(「使用这个协议」之后)。</summary>
    public event Action? Completed;

    // ── 协议 ──

    public ObservableCollection<DebugProtocolItem> Protocols { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FinalUrl), nameof(CanUseProtocol))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string SelectedProtocol { get; set; }

    partial void OnSelectedProtocolChanged(string value)
    {
        foreach (var item in Protocols)
        {
            item.IsSelected = item.Protocol == value;
        }
    }

    [RelayCommand]
    private void SelectProtocol(string? protocol)
    {
        if (protocol is not null && !IsRunning && ProviderEndpoints.Protocols.Contains(protocol))
        {
            SelectedProtocol = protocol;
        }
    }

    /// <summary>按所选协议拼出来的请求地址(和设备同一条规则)。</summary>
    public string? FinalUrl => ProviderEndpoints.ChatUrl(BaseUrl, SelectedProtocol);

    // ── 模型 ──

    public ObservableCollection<string> ModelCandidates { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Model { get; set; } = "";

    [ObservableProperty] public partial bool IsLoadingModels { get; private set; }

    /// <summary>上游列表报了几个(不算这家已配过的)。</summary>
    [ObservableProperty] public partial int FetchedCount { get; private set; }

    /// <summary>每一族列表端点的尝试(界面在模型框下面写「上游列表 · N 个」或失败原因)。</summary>
    [ObservableProperty] public partial IReadOnlyList<DebugListAttemptDto> ModelAttempts { get; private set; } = [];

    /// <summary>拉候选这一次调用本身失败了(没连上、调试对象不合法):稳定 key,界面翻译。</summary>
    [ObservableProperty] public partial string? ModelsErrorKey { get; private set; }

    /// <summary>两族都没拉到(界面提示可以手填)。</summary>
    public bool ModelsUnavailable => !IsLoadingModels && ModelAttempts.Count > 0 && ModelAttempts.All(a => !a.Ok);

    partial void OnModelAttemptsChanged(IReadOnlyList<DebugListAttemptDto> value) => OnPropertyChanged(nameof(ModelsUnavailable));

    partial void OnIsLoadingModelsChanged(bool value) => OnPropertyChanged(nameof(ModelsUnavailable));

    /// <summary>拉一次模型候选(打开弹窗时调一次;「刷新」再调)。</summary>
    [RelayCommand]
    public async Task LoadModelsAsync()
    {
        if (IsLoadingModels)
        {
            return;
        }

        IsLoadingModels = true;
        ModelsErrorKey = null;
        try
        {
            var result = await _commands.DebugModelsAsync(_deviceId, _target);
            if (!result.IsOk || result.Value is not { } listing)
            {
                ModelsErrorKey = result.ErrorKey ?? result.Outcome.ToString();
                return;
            }

            ModelAttempts = listing.Attempts;
            var seen = new HashSet<string>(ModelCandidates, StringComparer.Ordinal);
            foreach (var model in listing.Models)
            {
                if (seen.Add(model.Id))
                {
                    ModelCandidates.Add(model.Id);
                }
            }

            FetchedCount = listing.Models.Length;
            if (Model.Trim().Length == 0 && ModelCandidates.Count > 0)
            {
                Model = ModelCandidates[0];
            }
        }
        finally
        {
            IsLoadingModels = false;
        }
    }

    // ── 请求选项 ──

    /// <summary>提示词;留空 = 设备用缺省的「只回复 OK」。</summary>
    [ObservableProperty] public partial string Prompt { get; set; } = "";

    public static IReadOnlyList<string> ThinkingLevels => DebugThinking.All;

    [ObservableProperty] public partial string Thinking { get; set; } = DebugThinking.Auto;

    [ObservableProperty] public partial double MaxOutputTokens { get; set; } = DefaultMaxOutputTokens;

    [ObservableProperty] public partial bool WithTool { get; set; }

    // ── 发送 / 停止 ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseProtocol))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand))]
    public partial bool IsRunning { get; private set; }

    /// <summary>点了「停止」:结果区显示「已停止」,保留已经收到的事件。</summary>
    [ObservableProperty] public partial bool WasStopped { get; private set; }

    /// <summary>这次调用本身失败了(没连上、模型没填、设备拒绝):稳定 key,界面翻译。上游的失败不在这里,在 <see cref="Result"/> 里。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunError))]
    public partial string? RunErrorKey { get; private set; }

    public bool HasRunError => RunErrorKey is not null;

    private bool CanSend() => !IsRunning && Model.Trim().Length > 0 && FinalUrl is not null;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (!CanSend())
        {
            return;
        }

        var runId = Guid.NewGuid().ToString("N");
        var protocol = SelectedProtocol;
        using var cts = new CancellationTokenSource();
        _run = cts;
        _runId = runId;
        _lastSeq = 0;
        Result = null;
        RunErrorKey = null;
        WasStopped = false;
        Events.Clear();
        ReplyText = "";
        ThinkingText = "";
        ToolCallsText = "";
        LiveRequest = null;
        LiveStatus = null;
        var previous = Protocols.First(p => p.Protocol == protocol).State;
        SetState(protocol, DebugProtocolState.Running);
        IsRunning = true;
        try
        {
            var run = new ProviderDebugRunParams(runId, _target, protocol, Model.Trim(), Prompt, (int)Math.Clamp(MaxOutputTokens, 1, 1_000_000), Thinking, WithTool);
            var result = await _commands.DebugRunAsync(_deviceId, run, ev => OnLiveEvent(runId, ev), cts.Token);
            if (result is { IsOk: true, Value: { } record })
            {
                Apply(record);
                SetState(protocol, record.Ok ? DebugProtocolState.Ok : DebugProtocolState.Failed);
                if (record.Ok)
                {
                    _lastOkProtocol = protocol;
                }
            }
            else
            {
                RunErrorKey = result.ErrorKey ?? result.Outcome.ToString();
                SetState(protocol, previous);
            }
        }
        catch (OperationCanceledException)
        {
            WasStopped = true;
            SetState(protocol, previous);
        }
        finally
        {
            _run = null;
            _runId = null;
            IsRunning = false;
            OnPropertyChanged(nameof(CanUseProtocol));
        }
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _run?.Cancel();

    /// <summary>弹窗关了:还在跑的那一条取消掉(设备掐断上游连接)。</summary>
    public void Close() => _run?.Cancel();

    private void SetState(string protocol, DebugProtocolState state)
    {
        foreach (var item in Protocols.Where(p => p.Protocol == protocol))
        {
            item.State = state;
        }
    }

    // ── 结果 ──

    [ObservableProperty] public partial DebugTab SelectedTab { get; set; } = DebugTab.Reply;

    [RelayCommand]
    private void SelectTab(DebugTab tab) => SelectedTab = tab;

    /// <summary>事件流(实时追加;跑完换成完整记录里的那份)。</summary>
    public ObservableCollection<ProviderDebugEventDto> Events { get; } = [];

    [ObservableProperty] public partial string ReplyText { get; private set; } = "";

    [ObservableProperty] public partial string ThinkingText { get; private set; } = "";

    /// <summary>工具调用,一行一个 <c>name(args)</c>。</summary>
    [ObservableProperty] public partial string ToolCallsText { get; private set; } = "";

    /// <summary>跑完之前,请求事件带来的 URL 和正文(请求页先显示这个)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequestText))]
    public partial (string Url, string Body)? LiveRequest { get; private set; }

    /// <summary>跑完之前,响应事件带来的状态码。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResponseText), nameof(StatusCode))]
    public partial int? LiveStatus { get; private set; }

    /// <summary>设备返回的完整记录(权威)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(IsOk), nameof(IsFailed), nameof(StatusCode), nameof(RequestText), nameof(ResponseText), nameof(Usage), nameof(HasUsage))]
    public partial ProviderDebugResultDto? Result { get; private set; }

    public bool HasResult => Result is not null;

    public bool IsOk => Result?.Ok == true;

    public bool IsFailed => Result is { Ok: false };

    public int? StatusCode => Result?.Status ?? LiveStatus;

    public DebugUsageDto? Usage => Result?.Usage;

    public bool HasUsage => Usage is not null;

    /// <summary>请求页:方法 + URL、请求头、空行、正文。</summary>
    public string RequestText
    {
        get
        {
            if (Result is { } r)
            {
                var text = new StringBuilder();
                text.Append(r.Method).Append(' ').AppendLine(r.Url);
                foreach (var h in r.RequestHeaders)
                {
                    text.Append(h.Name).Append(": ").AppendLine(h.Value);
                }

                return text.AppendLine().Append(r.RequestBody).ToString();
            }

            return LiveRequest is { } live ? $"POST {live.Url}\n\n{live.Body}" : "";
        }
    }

    /// <summary>响应页:状态行、响应头、空行、错误正文(成功时内容在事件流里)。</summary>
    public string ResponseText
    {
        get
        {
            if (Result is not { } r)
            {
                return LiveStatus is { } s ? $"HTTP {s}" : "";
            }

            if (r.Status is not { } status)
            {
                return "";
            }

            var text = new StringBuilder().Append("HTTP ").Append(status).AppendLine();
            foreach (var h in r.ResponseHeaders)
            {
                text.Append(h.Name).Append(": ").AppendLine(h.Value);
            }

            if (r.ResponseBody is { Length: > 0 } body)
            {
                text.AppendLine().Append(body);
            }

            return text.ToString();
        }
    }

    private void OnLiveEvent(string runId, ProviderDebugEventDto ev)
    {
        // 跑完之后(或换了一条)才到的进度不要:完整记录已经是权威的了。设备在快照和事件里都可能给同一条,按序号去重。
        if (runId != _runId || Result is not null || ev.Seq <= _lastSeq)
        {
            return;
        }

        _lastSeq = ev.Seq;
        Append(ev);
    }

    private void Append(ProviderDebugEventDto ev)
    {
        if (Events.Count < MaxRows)
        {
            Events.Add(ev);
        }

        switch (ev.Kind)
        {
            case DebugEventKinds.Request:
                LiveRequest = (ev.Name ?? "", ev.Data);
                break;
            case DebugEventKinds.Response when int.TryParse(ev.Name, out var status):
                LiveStatus = status;
                break;
            case DebugEventKinds.Text:
                ReplyText += ev.Data;
                break;
            case DebugEventKinds.Thinking:
                ThinkingText += ev.Data;
                break;
            case DebugEventKinds.ToolCall:
                ToolCallsText += (ToolCallsText.Length == 0 ? "" : "\n") + $"{ev.Name}({ev.Data})";
                break;
        }
    }

    private void Apply(ProviderDebugResultDto record)
    {
        Events.Clear();
        foreach (var ev in record.Events.Take(MaxRows))
        {
            Events.Add(ev);
        }

        ReplyText = record.Text;
        ThinkingText = record.Thinking;
        ToolCallsText = string.Join('\n', record.ToolCalls);
        Result = record;
        // 失败时多半要看响应:没连上的看事件流(里面是错误那一条)
        if (!record.Ok && SelectedTab == DebugTab.Reply && record.Status is not null)
        {
            SelectedTab = DebugTab.Response;
        }
    }

    // ── 使用这个协议 ──

    /// <summary>从编辑弹窗打开、所选协议刚跑通、且和草稿的不一样:可以把草稿改成这个协议。</summary>
    public bool CanUseProtocol => _canApplyProtocol && !IsRunning && _lastOkProtocol == SelectedProtocol && SelectedProtocol != InitialProtocol;

    [RelayCommand]
    private void UseProtocol()
    {
        if (!CanUseProtocol)
        {
            return;
        }

        AppliedProtocol = SelectedProtocol;
        Completed?.Invoke();
    }
}
