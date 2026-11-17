using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>模型选择器里的一行(<c>providers.models.list</c> 的候选,设备端算好的)。</summary>
public sealed record ModelChoice(string ProviderId, string ProviderName, string Alias, string DisplayName, string WindowText, bool SupportsImages, bool CanThink, bool IsAvailable)
{
    public string Key => $"{ProviderId}/{Alias}";

    public static ModelChoice Of(ModelOptionDto o) => new(
        o.ProviderId, o.ProviderName, o.Alias, o.DisplayName.Length > 0 ? o.DisplayName : o.Alias,
        ComposerLogic.WindowText(o.ContextWindow), o.SupportsImages, o.ThinkingMode is not null, o.Enabled && o.ProviderEnabled);
}

/// <summary>排队消息的一行。<see cref="IsPromoted"/> = 已经「插话」(下一个回合边界送进正在跑的 Run)。</summary>
public sealed record QueueItemView(string Id, string Text, bool IsPromoted);

/// <summary>斜杠命令一行。<see cref="IsAvailable"/> = false 是还没接入的内置命令(占位,选中只补全文字)。<see cref="Description"/> 来自 <c>commands.list</c>。</summary>
public sealed record SlashCommandItem(string Command, bool IsAvailable, string? Description = null, string? ArgumentHint = null)
{
    public bool HasDescription => !string.IsNullOrEmpty(Description);
}

/// <summary>
/// 输入框。草稿、档位选择是纯 UI 状态(按会话各记一份);能不能发送 / 排队 / 停止由 <see cref="ComposerLogic"/> 从 Store 的事实算出来;
/// 发送、排队、插话、停止、移除排队项各是 <see cref="IConversationCommands"/> 里的一个意图。
/// 上层(<see cref="ConversationViewModel"/>)每次 Store 变化后调 <see cref="Update"/> 把事实推进来,这里不订阅任何东西。
/// </summary>
public sealed partial class ComposerViewModel : ObservableObject
{
    private readonly IConversationCommands _commands;
    private readonly Dictionary<SessionRef, string> _drafts = [];
    private SessionRef? _session;
    private string? _activeRunId;
    private string? _role;
    private string _rememberedModel = "";
    private string? _rememberedProvider;
    private List<ModelOptionDto> _options = [];
    private SessionActivity _activity;
    private DeviceConnection _connection = DeviceConnection.Connecting;
    private readonly IComposerLookups? _lookups;
    private readonly TimeSpan _suggestDelay;
    private readonly Dictionary<SessionRef, List<FileRefChip>> _refs = [];
    private string? _workspaceId;
    private CancellationTokenSource? _suggesting;
    private (string Device, string Workspace)? _commandsFor;
    private IReadOnlyList<CommandDto> _projectCommands = [];
    private bool _restoring;

    public ComposerViewModel(IConversationCommands commands, IComposerLookups? lookups = null, TimeSpan? suggestDelay = null, AttachmentTrayViewModel? attachments = null)
    {
        _commands = commands;
        Attachments = attachments ?? new AttachmentTrayViewModel();
        Attachments.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AttachmentTrayViewModel.IsUploading))
            {
                RefreshState();
            }
        };
        _lookups = lookups;
        _suggestDelay = suggestDelay ?? TimeSpan.FromMilliseconds(120);
    }

    /// <summary>附件托盘(手机:拍照 / 相册 / 文件,带上传进度)。上传完成的附件随消息一起发出。</summary>
    public AttachmentTrayViewModel Attachments { get; }

    /// <summary>草稿(双向绑定)。</summary>
    [ObservableProperty] public partial string Draft { get; set; } = "";

    [ObservableProperty] public partial ComposerState State { get; private set; }

    /// <summary><c>ask</c> / <c>auto</c> / <c>full</c>。</summary>
    [ObservableProperty] public partial string Permission { get; private set; } = "ask";

    /// <summary><c>auto</c> / <c>off</c> / <c>minimal</c> …… <c>max</c>。</summary>
    [ObservableProperty] public partial string Thinking { get; private set; } = "auto";

    [ObservableProperty] public partial ModelChoice? SelectedModel { get; private set; }

    /// <summary>最近一次发送失败的原因(成功后清掉)。界面按它写提示。</summary>
    [ObservableProperty] public partial SendOutcome? LastFailure { get; private set; }

    [ObservableProperty] public partial bool IsSending { get; private set; }

    /// <summary>草稿末尾正在输入的 <c>@</c> 引用(不含 @);没有为 null。files.* 协议接入后这里出候选,现在只是占位。</summary>
    [ObservableProperty] public partial string? FileQuery { get; private set; }

    /// <summary>草稿以 <c>/</c> 开头时的命令前缀(含 /);没有为 null。</summary>
    [ObservableProperty] public partial string? SlashQuery { get; private set; }

    public ObservableCollection<ModelChoice> Models { get; } = [];

    public ObservableCollection<string> ThinkingChoices { get; } = [];

    public ObservableCollection<string> PermissionChoices { get; } = [];

    public ObservableCollection<QueueItemView> Queue { get; } = [];

    public ObservableCollection<SlashCommandItem> SlashCommands { get; } = [];

    /// <summary>输入框上方已经选好的文件引用(chip)。按会话各记一份;发送时作为文件块和正文一起发出。</summary>
    public ObservableCollection<FileRefChip> FileRefs { get; } = [];

    /// <summary><c>@</c> 弹层里的文件候选(<c>files.suggest</c>)。</summary>
    public ObservableCollection<FileSuggestionRow> FileSuggestions { get; } = [];

    /// <summary>弹层里高亮的候选(键盘上下移动,Enter / Tab 选中)。</summary>
    [ObservableProperty] public partial int SuggestionIndex { get; set; }

    partial void OnSuggestionIndexChanged(int value)
    {
        for (var i = 0; i < FileSuggestions.Count; i++)
        {
            FileSuggestions[i].IsActive = i == value;
        }
    }

    [ObservableProperty] public partial bool IsSuggesting { get; private set; }

    /// <summary>候选没取到(设备没连上……):弹层里写一句提示,不影响输入。</summary>
    [ObservableProperty] public partial bool SuggestFailed { get; private set; }

    public bool HasFileRefs => FileRefs.Count > 0;

    /// <summary>有 <c>@</c> 查询、且有候选(或正在取 / 没取到):弹层显示。</summary>
    public bool ShowsSuggestions => HasFileQuery;

    public bool HasNoSuggestions => HasFileQuery && !IsSuggesting && !SuggestFailed && FileSuggestions.Count == 0;

    public bool HasQueue => Queue.Count > 0;

    /// <summary>主按钮此刻是哪一个(三选一;不能操作时仍显示灰掉的「发送」)。</summary>
    public bool PrimaryIsStop => State.Primary == ComposerPrimary.Stop;

    public bool PrimaryIsQueue => State.Primary == ComposerPrimary.Queue;

    public bool PrimaryIsSend => State.Primary is ComposerPrimary.Send or ComposerPrimary.Disabled;

    public bool HasBlock => State.Block != ComposerBlock.None && State.Block != ComposerBlock.NoSession;

    public bool HasFailure => LastFailure is not null;

    partial void OnStateChanged(ComposerState value)
    {
        OnPropertyChanged(nameof(PrimaryIsStop));
        OnPropertyChanged(nameof(PrimaryIsQueue));
        OnPropertyChanged(nameof(PrimaryIsSend));
        OnPropertyChanged(nameof(HasBlock));
        OnPropertyChanged(nameof(NeedsProvider));
    }

    partial void OnLastFailureChanged(SendOutcome? value) => OnPropertyChanged(nameof(HasFailure));

    public bool HasFileQuery => FileQuery is not null;

    public bool HasSlashQuery => SlashQuery is not null && SlashCommands.Count > 0;

    /// <summary>设备是不是 owner:界面据此显示「完全访问」档位。</summary>
    public bool CanUseFull => _role == "owner";

    /// <summary>内置命令:还没接入,保留占位(设备上的命令清单有同名的,以清单为准)。</summary>
    private static readonly SlashCommandItem[] KnownSlash =
    [
        new("/goal", false), new("/compact", false), new("/clear", false), new("/model", false),
    ];

    partial void OnDraftChanged(string value)
    {
        if (_session is { } session)
        {
            _drafts[session] = value;
        }

        if (!_restoring)
        {
            PruneRefs(value);
        }

        RefreshState();
        UpdateTriggers(value);
    }

    /// <summary>
    /// 用户选中了候选(文件 / 命令),草稿被程序改写:输入框据此把光标放到末尾并取回焦点。打字、换会话不触发。
    /// </summary>
    public event EventHandler? DraftReplaced;

    /// <summary>已知命令名(不含 <c>/</c>):设备上的命令清单。草稿开头的 <c>/命令 </c> 据此画成 token。</summary>
    public IEnumerable<string> CommandNames => _projectCommands.Select(static c => c.Name);

    /// <summary>草稿里应当画成 token 的片段(只有 <see cref="ComposerLogic.Mentions"/> 一处规则)。</summary>
    public IReadOnlyList<MentionSpan> Mentions(string text) => ComposerLogic.Mentions(text, FileRefs, CommandNames);

    /// <summary>草稿里已经没有 <c>@名字</c> 的引用跟着去掉(删掉 token = 删掉引用)。</summary>
    private void PruneRefs(string draft)
    {
        if (FileRefs.Count == 0)
        {
            return;
        }

        var alive = ComposerLogic.Mentions(draft, FileRefs, []).Where(m => m.Kind == MentionSpan.FileKind).Select(m => m.Id).ToHashSet();
        var gone = FileRefs.Where(r => !alive.Contains(r.Path)).ToList();
        if (gone.Count == 0)
        {
            return;
        }

        foreach (var chip in gone)
        {
            FileRefs.Remove(chip);
        }

        Remember();
    }

    private void ReplaceDraft(string text)
    {
        Draft = text;
        DraftReplaced?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把 Store 里的事实推进来(会话换了、模型候选变了、设备断开……)。</summary>
    public void Update(SessionRef? session, SessionDto? meta, QueueDto? queue, string? activeRunId, SessionActivity activity,
        DeviceConnection connection, string? role, IEnumerable<ModelOptionDto> options)
    {
        var changedSession = session != _session;
        _session = session;
        _activeRunId = activeRunId;
        _activity = activity;
        _connection = connection;
        _role = role;
        var rememberedChanged = changedSession || (meta?.Model ?? "") != _rememberedModel || meta?.ModelProviderId != _rememberedProvider;
        _rememberedModel = meta?.Model ?? "";
        _rememberedProvider = meta?.ModelProviderId;
        _workspaceId = meta?.WorkspaceId ?? _workspaceId;
        _options = [.. options.Where(o => o.IsChatModel)];

        if (changedSession)
        {
            // 换会话:先换回这个会话的引用,再换草稿(草稿里的 @名字 要对得上引用);换的过程中不按草稿删引用
            _restoring = true;
            Sync(FileRefs, session is { } rs && _refs.TryGetValue(rs, out var chips) ? chips : []);
            Draft = session is { } s && _drafts.TryGetValue(s, out var draft) ? draft : "";
            _restoring = false;
            LastFailure = null;
            Attachments.SetSession(session);
            OnPropertyChanged(nameof(HasFileRefs));
            CancelSuggestions();
            _commandsFor = null;
        }

        SyncModels(changedSession, rememberedChanged);
        Permission = ComposerLogic.ClampPermission(Permission, role);
        Thinking = ComposerLogic.ClampThinking(Thinking, CurrentOption());
        Sync(PermissionChoices, ComposerLogic.PermissionModesFor(role));
        Sync(ThinkingChoices, ComposerLogic.ThinkingLevels(CurrentOption()));
        OnPropertyChanged(nameof(CanUseFull));

        Sync(Queue, queue?.Items.Select(i => new QueueItemView(i.Id, string.Concat(i.Parts.Where(p => p.Kind == InputPartKinds.Text).Select(p => p.Text)), i.State == QueueStates.Promoted)).ToList() ?? []);
        OnPropertyChanged(nameof(HasQueue));
        RefreshState();
    }

    /// <summary>
    /// 选中哪个模型:会话上记住的(设备真源)变了就跟它走——换了会话、快照刚到、选完之后设备推回来、别的控制端改了;
    /// 没变就保留刚才的选择(写回还在路上,或者写回失败了也不把用户的选择撤掉);都没有才兜底成第一个可用的。
    /// ★ 兜底结果不写回(2.x 同样):用户没选过,会话就该继续跟着默认走。
    /// </summary>
    private void SyncModels(bool changedSession, bool rememberedChanged)
    {
        var choices = _options.Select(ModelChoice.Of).ToList();
        Sync(Models, choices);
        var remembered = choices.FirstOrDefault(c => c.Alias == _rememberedModel && (_rememberedProvider is null || c.ProviderId == _rememberedProvider) && c.IsAvailable);
        // 上一个会话的选择不带到这个会话来
        var picked = !changedSession && SelectedModel is { } p ? choices.FirstOrDefault(c => c.Key == p.Key) : null;
        SelectedModel = (rememberedChanged ? remembered : null) ?? picked ?? remembered ?? choices.FirstOrDefault(c => c.IsAvailable);
    }

    private ModelOptionDto? CurrentOption() =>
        SelectedModel is { } m ? _options.FirstOrDefault(o => o.ProviderId == m.ProviderId && o.Alias == m.Alias) : null;

    private void RefreshState()
    {
        State = ComposerLogic.Of(Draft, _session is not null, _activity, _connection, _role, hasModel: SelectedModel is not null || _rememberedModel.Length > 0);
        if (Attachments.IsUploading)
        {
            // 附件还在上传:现在发出去会丢掉它,等传完(停止不受影响)
            State = State with { CanSend = false, CanQueue = false, CanSteer = false };
        }
        OnPropertyChanged(nameof(CanUseFull));
    }

    private void UpdateTriggers(string draft)
    {
        var at = draft.LastIndexOf('@');
        var query = at >= 0 && (at == 0 || char.IsWhiteSpace(draft[at - 1])) && !draft[(at + 1)..].Any(char.IsWhiteSpace) ? draft[(at + 1)..] : null;
        var changed = query != FileQuery;
        FileQuery = query;
        SlashQuery = draft.StartsWith('/') && !draft.Contains(' ') ? draft : null;
        RefreshSlash();
        OnPropertyChanged(nameof(HasFileQuery));
        OnPropertyChanged(nameof(ShowsSuggestions));
        OnPropertyChanged(nameof(HasSlashQuery));
        if (changed)
        {
            StartSuggest(query);
        }

        if (SlashQuery is not null)
        {
            _ = EnsureCommandsAsync();
        }
    }

    /// <summary>斜杠菜单 = 设备上的命令清单(<c>commands.list</c>,按前缀过滤)+ 还没接入的内置命令占位(同名的以清单为准)。</summary>
    private void RefreshSlash()
    {
        if (SlashQuery is not { } q)
        {
            Sync(SlashCommands, []);
            return;
        }

        var real = _projectCommands
            .Select(c => new SlashCommandItem("/" + c.Name, true, c.Description, c.ArgumentHint))
            .Where(c => c.Command.StartsWith(q, StringComparison.OrdinalIgnoreCase));
        var builtin = KnownSlash.Where(c => c.Command.StartsWith(q, StringComparison.OrdinalIgnoreCase) && _projectCommands.All(p => "/" + p.Name != c.Command));
        Sync(SlashCommands, [.. real, .. builtin]);
        OnPropertyChanged(nameof(HasSlashQuery));
    }

    /// <summary>第一次敲 <c>/</c>(或换了会话)时取一次命令清单;取回来重新过滤。</summary>
    private async Task EnsureCommandsAsync()
    {
        if (_lookups is null || _session is not { } session || _workspaceId is not { } workspace || _commandsFor == (session.DeviceId, workspace))
        {
            return;
        }

        _commandsFor = (session.DeviceId, workspace);
        var list = await _lookups.ListCommandsAsync(session, workspace, CancellationToken.None);
        if (list is null || _session != session)
        {
            _commandsFor = null; // 没取到:下次敲 / 再试
            return;
        }

        _projectCommands = list;
        RefreshSlash();
    }

    // ───────────────────────── @ 文件引用 ─────────────────────────

    private void CancelSuggestions()
    {
        _suggesting?.Cancel();
        _suggesting = null;
        IsSuggesting = false;
        SuggestFailed = false;
        FileSuggestions.Clear();
        SuggestionIndex = 0;
    }

    private void StartSuggest(string? query)
    {
        _suggesting?.Cancel();
        _suggesting = null;
        if (query is null || _lookups is null || _session is not { } session || _workspaceId is not { } workspace)
        {
            IsSuggesting = false;
            SuggestFailed = false;
            FileSuggestions.Clear();
            SuggestionIndex = 0;
            OnPropertyChanged(nameof(HasNoSuggestions));
            return;
        }

        var cts = new CancellationTokenSource();
        _suggesting = cts;
        IsSuggesting = true;
        SuggestFailed = false;
        OnPropertyChanged(nameof(HasNoSuggestions));
        _suggestTask = SuggestAsync(session, workspace, query, cts);
    }

    private async Task SuggestAsync(SessionRef session, string workspace, string query, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            if (_suggestDelay > TimeSpan.Zero && query.Length > 0)
            {
                await Task.Delay(_suggestDelay, ct);
            }

            var found = await _lookups!.SuggestFilesAsync(session, workspace, query, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            SuggestFailed = found is null;
            FileSuggestions.Clear();
            foreach (var item in found?.Where(f => FileRefs.All(r => r.Path != f.Path)) ?? [])
            {
                FileSuggestions.Add(new FileSuggestionRow(item));
            }

            SuggestionIndex = 0;
            OnSuggestionIndexChanged(0);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsSuggesting = false;
                OnPropertyChanged(nameof(HasNoSuggestions));
            }
        }
    }

    /// <summary>当前这一轮候选查询结束时完成(测试用:等到候选落定)。</summary>
    public Task WhenSuggested => _suggestTask;

    private Task _suggestTask = Task.CompletedTask;

    /// <summary>键盘上下移动弹层里的高亮。</summary>
    [RelayCommand]
    private void MoveSuggestion(int delta)
    {
        if (FileSuggestions.Count == 0)
        {
            return;
        }

        SuggestionIndex = (SuggestionIndex + delta + FileSuggestions.Count) % FileSuggestions.Count;
    }

    /// <summary>Enter / Tab:选中高亮的候选。没有候选返回 false(Enter 照常发送)。</summary>
    public bool AcceptSuggestion()
    {
        if (!HasFileQuery || FileSuggestions.Count == 0)
        {
            return false;
        }

        PickFile(FileSuggestions[Math.Clamp(SuggestionIndex, 0, FileSuggestions.Count - 1)].Item);
        return true;
    }

    /// <summary>
    /// 选中一个候选:草稿末尾的 <c>@查询</c> 换成 <c>@名字 </c>(输入框把它画成一个 token),同时记下这个文件引用。
    /// 和已引用的另一个文件重名时,<c>@</c> 后面写项目相对路径以示区分。
    /// </summary>
    [RelayCommand]
    private void PickFile(FileSuggestionItem item)
    {
        var chip = FileRefs.FirstOrDefault(r => r.Path == item.Path);
        if (chip is null)
        {
            chip = new FileRefChip(item.Path, item.Name)
            {
                Label = FileRefs.Any(r => r.Label == item.Name) ? item.Path : item.Name,
            };
            FileRefs.Add(chip);
            Remember();
        }

        var at = FileQuery is not null ? Draft.LastIndexOf('@') : -1;
        var head = at >= 0 ? Draft[..at] : Draft.Length > 0 && !char.IsWhiteSpace(Draft[^1]) ? Draft + " " : Draft;
        ReplaceDraft(head + chip.Mention + " ");
        CancelSuggestions();
    }

    /// <summary>去掉一个文件引用:草稿里它的 <c>@名字</c>(连同后面一个空格)一起删掉。</summary>
    [RelayCommand]
    private void RemoveFile(FileRefChip chip)
    {
        var span = ComposerLogic.Mentions(Draft, [chip], []).FirstOrDefault();
        FileRefs.Remove(chip);
        Remember();
        if (span is not null)
        {
            var end = span.End < Draft.Length && Draft[span.End] == ' ' ? span.End + 1 : span.End;
            Draft = Draft[..span.Start] + Draft[end..];
        }
    }

    private void Remember()
    {
        if (_session is { } session)
        {
            _refs[session] = [.. FileRefs];
        }

        OnPropertyChanged(nameof(HasFileRefs));
    }

    private IReadOnlyList<InputFile> InputFiles() => [.. FileRefs.Select(static r => new InputFile(r.Path, r.Name)), .. Attachments.Ready()];

    private void ClearRefs()
    {
        FileRefs.Clear();
        Attachments.Clear();
        Remember();
    }

    /// <summary>草稿 → 真正要发出去的文本:以 <c>/</c> 开头且是一次命令调用就展开(失败返回 null,不静默地把没展开的 <c>/xxx</c> 当普通文本发出去)。</summary>
    private async Task<string?> ExpandedAsync(SessionRef session, string text)
    {
        if (_lookups is null || _workspaceId is not { } workspace || !text.StartsWith('/'))
        {
            return text;
        }

        return await _lookups.ExpandAsync(session, workspace, text, CancellationToken.None);
    }

    // ───────────────────────── 选择 ─────────────────────────

    [RelayCommand]
    private void PickModel(ModelChoice choice)
    {
        if (!choice.IsAvailable)
        {
            return;
        }

        SelectedModel = choice;
        Thinking = ComposerLogic.ClampThinking(Thinking, CurrentOption());
        Sync(ThinkingChoices, ComposerLogic.ThinkingLevels(CurrentOption()));
        RefreshState();
        if (_session is { } session && (choice.Alias != _rememberedModel || choice.ProviderId != _rememberedProvider))
        {
            // 记在会话上;owner 还顺带写成这个项目新会话的默认模型(2.x 的写回)。结果经会话主题推回来,SyncModels 跟着它走
            _modelWrite = _commands.SetModelAsync(session, choice.Alias, choice.ProviderId, _role == "owner" ? _workspaceId : null);
        }
    }

    /// <summary>最近一次模型写回(测试用:等到它落定)。</summary>
    public Task WhenModelSaved => _modelWrite;

    private Task _modelWrite = Task.CompletedTask;

    [RelayCommand]
    private void SetPermission(string mode) => Permission = ComposerLogic.ClampPermission(mode, _role);

    [RelayCommand]
    private void SetThinking(string level) => Thinking = ComposerLogic.ClampThinking(level, CurrentOption());

    [RelayCommand]
    private void InsertSlash(SlashCommandItem item) => ReplaceDraft(item.Command + " ");

    /// <summary>这一条的发送档位(逐条冻结:排队 5 分钟里改两次模型,三条消息就是三份档位)。</summary>
    public SendOptionsDto Options() => new(
        Model: SelectedModel?.Alias ?? _rememberedModel,
        ModelProviderId: SelectedModel is { } m && (m.Alias != _rememberedModel || m.ProviderId != _rememberedProvider) ? m.ProviderId : _rememberedProvider,
        Thinking: Thinking,
        MaxContext: false,
        Mode: "",
        PermissionMode: Permission);

    // ───────────────────────── 意图 ─────────────────────────

    /// <summary>Enter / 主按钮:空闲发送;运行中有文字就排队;运行中没文字就停止。</summary>
    [RelayCommand]
    private Task SubmitAsync() => State.Primary switch
    {
        ComposerPrimary.Send => SendAsync(),
        ComposerPrimary.Queue => EnqueueAsync(),
        ComposerPrimary.Stop => StopAsync(),
        _ => Task.CompletedTask,
    };

    [RelayCommand]
    private async Task SendAsync()
    {
        if (!State.CanSend || _session is not { } session)
        {
            return;
        }

        var text = Draft.Trim();
        var options = Options();
        var files = InputFiles();
        IsSending = true;
        Draft = "";
        try
        {
            var final = await ExpandedAsync(session, text);
            var result = final is null
                ? new SendResult(SendOutcome.Unavailable)
                : await _commands.SendAsync(session, final, files, options);
            // 送达(Started)和进了队列(Queued)都不是失败;其余把草稿还给用户,不丢他写的字
            if (result.Outcome is SendOutcome.Started or SendOutcome.Queued)
            {
                LastFailure = null;
                ClearRefs();
                if (_rememberedModel.Length == 0 && options.Model.Length > 0 && _session == session)
                {
                    // 会话上还没记模型(老会话、设备上没配默认模型):这一条真的用了它,记在会话上,重开时还是它。只写会话,不写项目默认值
                    _modelWrite = _commands.SetModelAsync(session, options.Model, options.ModelProviderId);
                }
            }
            else
            {
                LastFailure = result.Outcome;
                if (Draft.Length == 0)
                {
                    Draft = text;
                }
            }
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>运行中:这句话先排队,等当前 Run 收尾后自动发出。</summary>
    [RelayCommand]
    private Task EnqueueAsync() => RunningText((session, text, files, options) => _commands.EnqueueAsync(session, text, files, options));

    /// <summary>运行中:这句话立刻插进当前 Run(下一个回合边界送达)。</summary>
    [RelayCommand]
    private Task SteerAsync() => RunningText((session, text, files, options) => _commands.SteerAsync(session, text, files, options));

    private async Task RunningText(Func<SessionRef, string, IReadOnlyList<InputFile>, SendOptionsDto, Task<bool>> call)
    {
        if (!State.CanQueue || _session is not { } session)
        {
            return;
        }

        var text = Draft.Trim();
        var options = Options();
        var files = InputFiles();
        Draft = "";
        var final = await ExpandedAsync(session, text);
        if (final is not null && await call(session, final, files, options))
        {
            LastFailure = null;
            ClearRefs();
        }
        else
        {
            // 被拒绝(队列满了、设备没连上):草稿还给用户
            LastFailure = SendOutcome.Rejected;
            if (Draft.Length == 0)
            {
                Draft = text;
            }
        }
    }

    /// <summary>没有可用的模型(没配供应商):输入框里给「添加供应商」按钮。</summary>
    public bool NeedsProvider => State.Block == ComposerBlock.NoModel;

    /// <summary>「添加供应商」:交给上层打开设置的「模型与供应商」。</summary>
    public event Action? ProviderSettingsRequested;

    [RelayCommand]
    private void OpenProviderSettings() => ProviderSettingsRequested?.Invoke();

    [RelayCommand]
    private async Task StopAsync()
    {
        if (State.CanStop && _session is { } session && _activeRunId is { } runId)
        {
            await _commands.AbortAsync(new RunRef(session.DeviceId, runId));
        }
    }

    /// <summary>「插话」开关:没引入的引入,已引入的退回排队。</summary>
    [RelayCommand]
    private async Task PromoteAsync(QueueItemView item)
    {
        if (_session is { } session)
        {
            await _commands.PromoteQueuedAsync(session, item.Id);
        }
    }

    [RelayCommand]
    private async Task RemoveQueuedAsync(QueueItemView item)
    {
        if (_session is { } session)
        {
            await _commands.RemoveQueuedAsync(session, item.Id);
        }
    }

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        if (target.SequenceEqual(items))
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
