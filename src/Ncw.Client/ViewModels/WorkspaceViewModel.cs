using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 栏 3 顶部的标签栏。标签有两种:会话标签,和文件标签(点文件树 / 搜索结果里的文件,在这里打开预览与简易编辑)。
/// 「打开了哪些标签、哪个文件标签是当前的」是纯 UI 状态,按项目各记一份;会话标签的标题、运行状态都从 Store 读,选中哪个会话是 <see cref="ClientStores.Selection"/> 的事实。
/// 文件标签的内容在设备上(文件是设备的事实),标签只持有一个 <see cref="FilePreviewViewModel"/>,不进 Store。
/// 当前标签的规则:有当前文件标签就是它;否则是选中的会话。选中了别的会话(项目面板、新建会话等任何入口)就让位给会话。
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject, IFileTabHost
{
    private readonly ClientStores _stores;
    private readonly SessionActions? _sessions;
    private readonly IFilesCommands? _files;
    private readonly Func<IDialogService>? _dialogs;
    private readonly Func<IInspectorText>? _text;
    private readonly Func<ILocalFiles>? _local;
    private readonly Dictionary<ProjectRef, List<TabKey>> _open = [];
    private readonly Dictionary<ProjectRef, Dictionary<string, FilePreviewViewModel>> _filesOf = [];
    private readonly Dictionary<ProjectRef, string> _activeFile = [];
    private (ProjectRef? Project, string? SessionId) _lastSelection;

    private readonly record struct TabKey(bool IsFile, string Id);

    public WorkspaceViewModel(ClientStores stores, SessionActions? sessions = null, IFilesCommands? files = null,
        Func<IDialogService>? dialogs = null, Func<IInspectorText>? text = null, Func<ILocalFiles>? local = null)
    {
        _stores = stores;
        _sessions = sessions;
        _files = files;
        _dialogs = dialogs;
        _text = text;
        _local = local;
        stores.Selection.Changed += Refresh;
        stores.Workspace.Changed += Refresh;
        Refresh();
    }

    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = [];

    [ObservableProperty] public partial WorkspaceTabViewModel? ActiveTab { get; private set; }

    /// <summary>当前的文件标签的内容;null = 当前是会话(或没有标签)。</summary>
    [ObservableProperty] public partial FilePreviewViewModel? ActiveFile { get; private set; }

    // ───────────────────────── 打开 / 激活 / 关闭 ─────────────────────────

    /// <summary>路径段点击时,交给文件树展开并选中对应项目路径。</summary>
    public Action<ProjectRef, string>? RevealRequested { get; set; }

    /// <summary>引用文件时,交给当前会话的输入框追加路径。</summary>
    public Action<ProjectRef, string>? ReferenceRequested { get; set; }

    /// <summary>当前项目是否有可接受文件引用的会话。</summary>
    public Func<ProjectRef, bool>? CanReferenceRequested { get; set; }

    /// <summary>会话可用性发生变化时,同步所有项目的文件引用按钮。</summary>
    public void RefreshReferenceAvailability()
    {
        foreach (var files in _filesOf.Values)
        {
            foreach (var file in files.Values)
            {
                file.RefreshReferenceAvailability();
            }
        }
    }

    /// <summary>程序化打开文件固定为常驻标签,已经打开时激活并固定。</summary>
    public Task OpenAsync(ProjectRef project, string path, int? line = null) => OpenCoreAsync(project, path, line, false);

    /// <summary>单击文件树打开临时预览;每个项目至多一个,不会替换编辑中的文件。</summary>
    public Task OpenPreviewAsync(ProjectRef project, string path, int? line = null) => OpenCoreAsync(project, path, line, true);

    /// <summary>把临时预览固定为常驻标签。</summary>
    public void PinFile(ProjectRef project, string path)
    {
        _filesOf.GetValueOrDefault(project)?.GetValueOrDefault(path)?.PinCommand.Execute(null);
    }

    /// <summary>返回项目当前选中的会话,但不关闭打开的文件标签。</summary>
    public void ShowConversation(ProjectRef project)
    {
        _activeFile.Remove(project);
        Refresh();
    }

    private async Task OpenCoreAsync(ProjectRef project, string path, int? line, bool isPreview)
    {
        if (_files is null)
        {
            return;
        }

        if (!_filesOf.TryGetValue(project, out var files))
        {
            _filesOf[project] = files = [];
        }

        if (!_open.TryGetValue(project, out var list))
        {
            _open[project] = list = [];
        }

        var created = false;
        var insertAt = list.Count;
        if (!files.TryGetValue(path, out var preview))
        {
            if (isPreview && files.Values.FirstOrDefault(f => f.IsPreview) is { } previous)
            {
                if (previous.IsDirty || previous.IsEditing)
                {
                    previous.PinCommand.Execute(null);
                }
                else
                {
                    insertAt = list.IndexOf(new TabKey(true, previous.Path));
                    RemoveFile(project, previous.Path);
                }
            }

            preview = new FilePreviewViewModel(_files, project, path, CanWrite(project), line, _local, LocalRoot(project))
            {
                IsPreview = isPreview,
                RevealRequested = (p, target) => RevealRequested?.Invoke(p, target),
                ReferenceRequested = (p, target) =>
                {
                    ReferenceRequested?.Invoke(p, target);
                    ShowConversation(p);
                },
                CanReferenceRequested = p => ReferenceRequested is not null && CanReferenceRequested?.Invoke(p) == true,
            };
            files[path] = preview;
            created = true;
        }
        else if (!isPreview)
        {
            preview.PinCommand.Execute(null);
        }

        var key = new TabKey(true, path);
        if (!list.Contains(key))
        {
            list.Insert(Math.Clamp(insertAt, 0, list.Count), key);
        }

        _activeFile[project] = path;
        Refresh();
        if (created)
        {
            await preview.LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>文件被重命名 / 删除:它(或它下面的文件)的标签直接关掉,不再询问(文件已经不在了)。</summary>
    public void CloseInside(ProjectRef project, string path)
    {
        if (!_filesOf.TryGetValue(project, out var files))
        {
            return;
        }

        var doomed = files.Keys.Where(p => p == path || p.StartsWith(path + "/", StringComparison.Ordinal)).ToList();
        foreach (var p in doomed)
        {
            RemoveFile(project, p);
        }

        if (doomed.Count > 0)
        {
            Refresh();
        }
    }

    [RelayCommand]
    private void Activate(WorkspaceTabViewModel tab)
    {
        if (_stores.Selection.State.Project is not { } project)
        {
            return;
        }

        if (tab.IsFile)
        {
            _activeFile[project] = tab.Path!;
            Refresh();
            return;
        }

        _activeFile.Remove(project);
        _sessions?.Open(project, tab.SessionId!);
        Refresh();
    }

    [RelayCommand]
    private async Task CloseAsync(WorkspaceTabViewModel tab)
    {
        if (_stores.Selection.State.Project is not { } project || !_open.TryGetValue(project, out var list))
        {
            return;
        }

        if (tab.IsFile)
        {
            if (tab.File is { IsDirty: true } && !await ConfirmDiscardAsync().ConfigureAwait(true))
            {
                return;
            }

            var wasActive = _activeFile.GetValueOrDefault(project) == tab.Path;
            var index = list.IndexOf(new TabKey(true, tab.Path!));
            RemoveFile(project, tab.Path!);
            if (wasActive)
            {
                ActivateNeighbor(project, list, index);
            }

            Refresh();
            return;
        }

        var selectedId = _stores.Selection.State.SessionId;
        var sessionIndex = list.IndexOf(new TabKey(false, tab.SessionId!));
        list.Remove(new TabKey(false, tab.SessionId!));
        if (tab.SessionId == selectedId)
        {
            // 关掉选中的会话:有文件标签在前台时只是不再选中会话;否则前台移到旁边的标签
            if (_activeFile.ContainsKey(project))
            {
                _sessions?.Deselect(project);
            }
            else
            {
                ActivateNeighbor(project, list, sessionIndex);
            }
        }

        Refresh();
    }

    /// <summary>关掉前台标签之后,前台交给原来位置上的邻居;一个标签也不剩就不再选中会话。</summary>
    private void ActivateNeighbor(ProjectRef project, List<TabKey> list, int index)
    {
        _activeFile.Remove(project);
        if (list.Count == 0)
        {
            _sessions?.Deselect(project);
            return;
        }

        var next = list[Math.Clamp(index, 0, list.Count - 1)];
        if (next.IsFile)
        {
            _activeFile[project] = next.Id;
        }
        else
        {
            _sessions?.Open(project, next.Id);
        }
    }

    private void RemoveFile(ProjectRef project, string path)
    {
        if (_open.TryGetValue(project, out var list))
        {
            list.Remove(new TabKey(true, path));
        }

        _filesOf.GetValueOrDefault(project)?.Remove(path);
        if (_activeFile.GetValueOrDefault(project) == path)
        {
            _activeFile.Remove(project);
            // 关的是前台:回到会话(选中的会话还在就显示它)
        }
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (_dialogs is null || _text is null)
        {
            return true;
        }

        var text = _text();
        return await _dialogs().ConfirmAsync(text.DiscardTitle, text.DiscardMessage, text.DiscardLabel).ConfigureAwait(true);
    }

    // ───────────────────────── 项目属性 ─────────────────────────

    private bool CanWrite(ProjectRef project) =>
        _stores.Workspace.State.Devices.GetValueOrDefault(project.DeviceId)?.Role != "viewer";

    /// <summary>项目在本机设备的本机环境里才有本机根目录(「显示 / 用默认程序打开」才有意义)。</summary>
    private string? LocalRoot(ProjectRef project)
    {
        var workspace = _stores.Workspace.State;
        var device = workspace.Devices.GetValueOrDefault(project.DeviceId);
        return device is { Kind: DeviceKind.Local } && workspace.Projects.TryGetValue(project, out var info) && info.Environment == EnvironmentKind.Local ? info.Path : null;
    }

    // ───────────────────────── 派生 ─────────────────────────

    private void Refresh()
    {
        var selection = _stores.Selection.State;
        var workspace = _stores.Workspace.State;
        if (selection.Project is not { } project)
        {
            _lastSelection = (null, null);
            Tabs.Clear();
            ActiveTab = null;
            ActiveFile = null;
            return;
        }

        if (!_open.TryGetValue(project, out var list))
        {
            _open[project] = list = [];
        }

        // 选中了另一个会话(任何入口):文件标签让位给它
        if (selection.SessionId is { } current && (_lastSelection.Project, _lastSelection.SessionId) != (project, current) && _lastSelection.Project == project)
        {
            _activeFile.Remove(project);
        }

        _lastSelection = (project, selection.SessionId);

        // 选中的会话一定有标签;设备上已经删掉的会话(列表里没有了)不再有标签。文件标签不受设备会话列表影响
        if (selection.SessionId is { } selected && !list.Contains(new TabKey(false, selected)))
        {
            list.Add(new TabKey(false, selected));
        }

        var known = workspace.Sessions.Where(s => s.Project == project).ToDictionary(s => s.Id);
        if (known.Count > 0)
        {
            list.RemoveAll(k => !k.IsFile && k.Id != selection.SessionId && !known.ContainsKey(k.Id));
        }

        var activePath = _activeFile.GetValueOrDefault(project);
        var files = _filesOf.GetValueOrDefault(project);
        if (activePath is not null && files?.ContainsKey(activePath) != true)
        {
            _activeFile.Remove(project);
            activePath = null;
        }

        if (files is not null)
        {
            foreach (var file in files.Values)
            {
                file.RefreshReferenceAvailability();
            }
        }

        var device = workspace.Devices.GetValueOrDefault(project.DeviceId);
        var connection = device?.Connection ?? DeviceConnection.Connecting;
        var tabs = new List<WorkspaceTabViewModel>();
        foreach (var key in list)
        {
            if (key.IsFile)
            {
                if (files?.GetValueOrDefault(key.Id) is { } file)
                {
                    tabs.Add(WorkspaceTabViewModel.ForFile(file, key.Id == activePath));
                }

                continue;
            }

            var summary = known.GetValueOrDefault(key.Id);
            tabs.Add(WorkspaceTabViewModel.ForSession(
                key.Id,
                summary?.Title ?? _stores.Conversations.State.Sessions.GetValueOrDefault(new SessionRef(project.DeviceId, key.Id))?.Meta.Title ?? "",
                SessionActivityView.Of(key.Id, device?.Index, connection),
                activePath is null && key.Id == selection.SessionId,
                summary?.Favorited ?? false,
                summary?.Archived ?? false));
        }

        if (!tabs.SequenceEqual(Tabs, TabComparer.Instance))
        {
            Tabs.Clear();
            foreach (var tab in tabs)
            {
                Tabs.Add(tab);
            }
        }

        ActiveTab = Tabs.FirstOrDefault(t => t.IsActive);
        ActiveFile = activePath is not null ? files![activePath] : null;
    }

    private sealed class TabComparer : IEqualityComparer<WorkspaceTabViewModel>
    {
        public static readonly TabComparer Instance = new();

        public bool Equals(WorkspaceTabViewModel? x, WorkspaceTabViewModel? y) =>
            x is not null && y is not null && x.Key == y.Key && x.Title == y.Title && x.Activity == y.Activity
            && x.IsActive == y.IsActive && x.IsFavorite == y.IsFavorite && x.IsArchived == y.IsArchived && ReferenceEquals(x.File, y.File);

        public int GetHashCode(WorkspaceTabViewModel obj) => obj.Key.GetHashCode(StringComparison.Ordinal);
    }
}

/// <summary>
/// 标签栏上的一个标签:会话(<see cref="SessionId"/>)或文件(<see cref="File"/>)。文件标签的「有未保存的修改」点直接绑 <c>File.IsDirty</c>,
/// 所以输入时标签不用重建。
/// </summary>
public sealed class WorkspaceTabViewModel
{
    private WorkspaceTabViewModel(string key, string title, bool isActive)
    {
        Key = key;
        Title = title;
        IsActive = isActive;
    }

    public static WorkspaceTabViewModel ForSession(string sessionId, string title, SessionActivity activity, bool isActive, bool isFavorite = false, bool isArchived = false) =>
        new("s:" + sessionId, title, isActive) { SessionId = sessionId, Activity = activity, IsFavorite = isFavorite, IsArchived = isArchived };

    public static WorkspaceTabViewModel ForFile(FilePreviewViewModel file, bool isActive) =>
        new("f:" + file.Path, file.Name, isActive) { File = file, Path = file.Path };

    /// <summary>标签的身份(会话和文件不会撞)。</summary>
    public string Key { get; }

    public string Title { get; }

    /// <summary>会话标签的会话 id;文件标签为 null。</summary>
    public string? SessionId { get; private init; }

    /// <summary>文件标签的项目相对路径;会话标签为 null。</summary>
    public string? Path { get; private init; }

    public FilePreviewViewModel? File { get; private init; }

    public bool IsFile => File is not null;

    public bool IsSession => File is null;

    public SessionActivity Activity { get; private init; } = SessionActivity.Idle;

    public bool IsRunning => Activity == SessionActivity.Running;

    public bool IsUnknown => Activity == SessionActivity.Unknown;

    public bool IsFavorite { get; private init; }

    public bool IsArchived { get; private init; }

    public bool IsActive { get; }
}
