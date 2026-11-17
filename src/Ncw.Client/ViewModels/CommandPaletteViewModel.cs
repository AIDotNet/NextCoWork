using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Shortcuts;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>命令面板里一行的种类;声明顺序是查询为空时的分组顺序。</summary>
public enum PaletteItemKind
{
    Command,
    Session,
    Project,
    File,
    Settings,
}

/// <summary>
/// 命令面板的一行。只带结构化数据:命令 / 设置分区的标题由界面按 <see cref="Command"/> / <see cref="Section"/> 翻译(Client 不产出界面文字),
/// 项目、会话、文件的标题是数据本身。<see cref="SearchText"/> 是排序时用来匹配的文字(界面装配时给的当前语言标题)。
/// </summary>
public sealed partial class PaletteItemViewModel : ObservableObject
{
    public required PaletteItemKind Kind { get; init; }

    public AppCommand? Command { get; init; }

    public SettingsSection? Section { get; init; }

    public ProjectRef? Project { get; init; }

    public string? SessionId { get; init; }

    /// <summary>文件:项目相对路径。</summary>
    public string? Path { get; init; }

    /// <summary>项目 / 会话 / 文件的名字;命令和设置为 null(界面按种类翻译)。</summary>
    public string? Title { get; init; }

    /// <summary>次要说明:会话所在项目、文件所在目录、项目所在设备。</summary>
    public string? Subtitle { get; init; }

    /// <summary>生效的快捷键手势(界面按平台画成 ⌘K / Ctrl+K);没有为 null。</summary>
    public string? Gesture { get; init; }

    public required string SearchText { get; init; }

    /// <summary>键盘高亮(纯 UI 状态)。</summary>
    [ObservableProperty] public partial bool IsActive { get; set; }

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    public bool HasGesture => !string.IsNullOrEmpty(Gesture);

    /// <summary>右侧显示种类名(命令行不显示:命令占大多数,每行都写「命令」只是噪音)。</summary>
    public bool ShowsKind => Kind != PaletteItemKind.Command;
}

/// <summary>
/// 命令面板(⌘K,docs/design/03 §11):一个输入框 + 结果列表,能跑命令、切项目、打开会话、打开文件、跳到某个设置分区。
/// <para>
/// 数据都是现成的镜像(项目、会话来自 <see cref="ClientStores.Workspace"/>,快捷键来自偏好),只有文件要问设备(<c>files.suggest</c>,
/// 输入停顿后查当前项目,结果是面板自己的 UI 状态,不进 Store)。排序复用 Core 的 <see cref="FuzzyPath"/>(和 <c>@</c> 文件选择器同一套分档)。
/// 选中一行不在这里执行:交给 <see cref="Invoked"/>,由外壳调对应意图的唯一入口(规则 4)。
/// </para>
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private const int MaxResults = 60;

    private readonly ClientStores _stores;
    private readonly IFilesCommands? _files;
    private readonly Func<bool> _isMac;
    private readonly TimeSpan _fileDelay;
    private CancellationTokenSource? _fileSearch;
    private IReadOnlyList<PaletteItemViewModel> _fileItems = [];

    public CommandPaletteViewModel(ClientStores stores, IFilesCommands? files = null, Func<bool>? isMac = null, TimeSpan? fileDelay = null)
    {
        _stores = stores;
        _files = files;
        _isMac = isMac ?? OperatingSystem.IsMacOS;
        _fileDelay = fileDelay ?? TimeSpan.FromMilliseconds(150);
    }

    /// <summary>命令的当前语言标题(界面装配时接上;默认是成员名,测试里够用)。</summary>
    public Func<AppCommand, string> CommandTitle { get; set; } = static c => c.ToString();

    /// <summary>设置分区的当前语言标题。</summary>
    public Func<SettingsSection, string> SectionTitle { get; set; } = static s => s.ToString();

    /// <summary>设置分区在面板里的前缀(「设置:」)。</summary>
    public Func<string> SettingsPrefix { get; set; } = static () => "Settings: ";

    /// <summary>选中了一行:外壳执行它。</summary>
    public event Action<PaletteItemViewModel>? Invoked;

    [ObservableProperty] public partial bool IsOpen { get; private set; }

    [ObservableProperty] public partial string Query { get; set; } = "";

    /// <summary>正在问设备找文件。</summary>
    [ObservableProperty] public partial bool IsSearchingFiles { get; private set; }

    public ObservableCollection<PaletteItemViewModel> Items { get; } = [];

    [ObservableProperty] public partial PaletteItemViewModel? Active { get; private set; }

    public bool HasItems => Items.Count > 0;

    public bool ShowsNoMatch => !HasItems && Query.Trim().Length > 0 && !IsSearchingFiles;

    [RelayCommand]
    public void Open()
    {
        Query = "";
        _fileItems = [];
        IsOpen = true;
        Rebuild();
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        _fileSearch?.Cancel();
        IsSearchingFiles = false;
    }

    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    /// <summary>↑ / ↓:在结果里移动高亮(到头后绕回)。</summary>
    [RelayCommand]
    public void Move(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }

        var index = Active is null ? -1 : Items.IndexOf(Active);
        index = ((index + delta) % Items.Count + Items.Count) % Items.Count;
        SetActive(Items[index]);
    }

    /// <summary>回车 / 点一行:关闭面板,交给外壳执行。</summary>
    [RelayCommand]
    public void Accept(PaletteItemViewModel? item = null)
    {
        item ??= Active;
        if (item is null)
        {
            return;
        }

        Close();
        Invoked?.Invoke(item);
    }

    partial void OnQueryChanged(string value)
    {
        _fileItems = [];
        Rebuild();
        _ = SearchFilesAsync(value);
    }

    private async Task SearchFilesAsync(string query)
    {
        _fileSearch?.Cancel();
        var q = query.Trim();
        if (_files is null || q.Length < 2 || _stores.Selection.State.Project is not { } project || !IsOpen)
        {
            IsSearchingFiles = false;
            return;
        }

        var cts = _fileSearch = new CancellationTokenSource();
        IsSearchingFiles = true;
        try
        {
            if (_fileDelay > TimeSpan.Zero)
            {
                await Task.Delay(_fileDelay, cts.Token).ConfigureAwait(true);
            }

            var result = await _files.SuggestAsync(project.DeviceId, project.WorkspaceId, q, 12, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _fileItems = result.IsOk && result.Value is { } value
                ? [.. value.Suggestions.Select(s => new PaletteItemViewModel
                {
                    Kind = PaletteItemKind.File,
                    Project = project,
                    Path = s.Path,
                    Title = s.Name,
                    Subtitle = s.Path.LastIndexOf('/') is > 0 and var i ? s.Path[..i] : null,
                    SearchText = s.Name,
                })]
                : [];
            Rebuild();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_fileSearch, cts))
            {
                IsSearchingFiles = false;
                OnPropertyChanged(nameof(ShowsNoMatch));
            }
        }
    }

    /// <summary>候选的全集(命令、当前项目的会话、所有项目、设置分区)+ 设备给的文件,按查询过滤排序。</summary>
    private void Rebuild()
    {
        var query = Query.Trim();
        var candidates = Candidates().Concat(_fileItems);
        List<PaletteItemViewModel> ranked;
        if (query.Length == 0)
        {
            // 没有查询:命令在前,然后是当前项目最近的会话,项目;设置和文件要搜才出现
            ranked = [.. candidates.Where(c => c.Kind is PaletteItemKind.Command or PaletteItemKind.Session or PaletteItemKind.Project)
                .OrderBy(c => c.Kind).Take(MaxResults)];
        }
        else
        {
            ranked = [.. candidates
                .Select(c => (Item: c, Score: Score(c, query)))
                .Where(x => x.Score is not null)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Item.Kind)
                .Select(x => x.Item)
                .Take(MaxResults)];
        }

        Items.Clear();
        foreach (var item in ranked)
        {
            Items.Add(item);
        }

        SetActive(Items.FirstOrDefault());
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowsNoMatch));
    }

    /// <summary>
    /// 一行对查询的得分(null = 不匹配)。标题按「文件名」、副标题 + 标题按「路径」交给 <see cref="FuzzyPath.Score"/>,分档规则因此和 <c>@</c> 选择器一致;
    /// 文件的路径就是它的项目相对路径。
    /// </summary>
    public static int? Score(PaletteItemViewModel item, string query)
    {
        var path = item.Kind == PaletteItemKind.File && item.Path is { } p ? p : string.IsNullOrEmpty(item.Subtitle) ? item.SearchText : item.Subtitle + "/" + item.SearchText;
        return FuzzyPath.Score(path, item.SearchText, query);
    }

    private IEnumerable<PaletteItemViewModel> Candidates()
    {
        var prefs = _stores.Prefs.State;
        var bindings = ShortcutCatalog.Resolve(prefs.ShortcutOverrides, _isMac()).ToDictionary(b => b.Command, b => b.Gesture);
        var workspace = _stores.Workspace.State;
        var current = _stores.Selection.State.Project;

        foreach (var command in Enum.GetValues<AppCommand>().Where(ShortcutCatalog.ShowsInPalette))
        {
            yield return new PaletteItemViewModel
            {
                Kind = PaletteItemKind.Command,
                Command = command,
                Gesture = bindings.GetValueOrDefault(command),
                SearchText = CommandTitle(command),
            };
        }

        if (current is { } project)
        {
            var projectName = workspace.Projects.TryGetValue(project, out var info) ? info.Name : null;
            foreach (var session in workspace.Sessions.Where(s => s.Project == project && !s.Archived).OrderByDescending(s => s.UpdatedAt).Take(30))
            {
                yield return new PaletteItemViewModel
                {
                    Kind = PaletteItemKind.Session,
                    Project = project,
                    SessionId = session.Id,
                    Title = session.Title,
                    Subtitle = projectName,
                    SearchText = session.Title,
                };
            }
        }

        foreach (var projectRef in _stores.Layout.State.Rail.Projects())
        {
            if (projectRef == current || !workspace.Projects.TryGetValue(projectRef, out var info))
            {
                continue;
            }

            yield return new PaletteItemViewModel
            {
                Kind = PaletteItemKind.Project,
                Project = projectRef,
                Title = info.Name,
                Subtitle = workspace.Devices.TryGetValue(projectRef.DeviceId, out var device) ? device.Name : null,
                SearchText = info.Name,
            };
        }

        var prefix = SettingsPrefix();
        foreach (var section in Enum.GetValues<SettingsSection>())
        {
            yield return new PaletteItemViewModel
            {
                Kind = PaletteItemKind.Settings,
                Section = section,
                SearchText = prefix + SectionTitle(section),
            };
        }
    }

    private void SetActive(PaletteItemViewModel? item)
    {
        if (Active is { } old)
        {
            old.IsActive = false;
        }

        Active = item;
        if (item is not null)
        {
            item.IsActive = true;
        }
    }
}
