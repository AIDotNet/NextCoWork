using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 右侧当前项目的文件面板:可折叠的惰性文件树、文件名 / 内容搜索、右键菜单(新建 / 重命名 / 删除进回收站 / 复制路径 / 在文件管理器中显示 / 用默认程序打开 / 另存为)。
/// 点开文件不在这里预览:交给 <see cref="Tabs"/>(主工作区的文件标签页,预览与简易编辑在那里)。
/// 文件在设备上:树、搜索结果都是纯 UI 状态,<b>不进 Store</b>(设备是真源);Git 状态角标来自 Git 镜像(由 <see cref="InspectorViewModel"/> 推进树)。
/// 「显示 / 打开 / 另存为」是客户端本机的动作,经 <see cref="ILocalFiles"/>,只对「本机设备 + 本机环境」的项目开放(<see cref="CanOpenLocally"/>)。
/// </summary>
public sealed partial class FilesPanelViewModel : ObservableObject
{
    private readonly IFilesCommands _files;
    private readonly Func<IDialogService> _dialogs;
    private readonly Func<IInspectorText> _text;
    private readonly Func<ILocalFiles> _local;
    private ProjectRef? _project;
    private FileNodeViewModel? _editing;
    private bool _creatingFolder;
    private string _editingDirectory = "";

    public FilesPanelViewModel(IFilesCommands files, Func<IDialogService> dialogs, Func<IInspectorText> text, Func<ILocalFiles> local, TimeSpan? searchDebounce = null, TimeProvider? clock = null)
    {
        _files = files;
        _dialogs = dialogs;
        _text = text;
        _local = local;
        Tree = new FileTreeViewModel(files);
        Search = new FileSearchViewModel(files, searchDebounce, clock);
        Search.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileSearchViewModel.IsActive))
            {
                if (Search.IsActive) IsFilterVisible = true;
                NotifySearchChanged();
            }
        };
    }

    public FileTreeViewModel Tree { get; }

    public FileSearchViewModel Search { get; }

    public ProjectRef? Project => _project;

    public string ProjectName => Tree.ProjectName;

    public string ProjectPath => Tree.ProjectPath;

    /// <summary>文件打开的去处:主工作区的文件标签页。装配时换成 <see cref="WorkspaceViewModel"/>。</summary>
    public IFileTabHost Tabs { get; set; } = NoFileTabs.Instance;

    [ObservableProperty] public partial FileNodeViewModel? Selected { get; set; }

    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;

    [ObservableProperty] public partial bool IsFilterVisible { get; set; }

    [ObservableProperty] public partial bool IsReferenceAvailable { get; set; }

    public Action<ProjectRef, string>? ReferenceRequested { get; set; }

    /// <summary>真实视图启用行内编辑;无界面的调用保留原有弹窗流程。</summary>
    public bool UseInlineEditing { get; set; }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ToggleFilter()
    {
        IsFilterVisible = !IsFilterVisible;
        if (!IsFilterVisible) Search.Query = "";
    }

    [RelayCommand]
    private void ToggleCompactFolders() => Tree.CompactFolders = !Tree.CompactFolders;

    [RelayCommand]
    private Task LocateActiveAsync() => Tabs.ActiveFile is { } file && file.Project == _project
        ? RevealPathAsync(file.Path) : Task.CompletedTask;

    public async Task RevealPathAsync(string path)
    {
        IsExpanded = true;
        Search.Query = "";
        var node = await Tree.RevealPathAsync(path).ConfigureAwait(true);
        if (node is not null) Selected = Tree.DisplayRow(node);
    }

    [RelayCommand]
    private void Reference(FileNodeViewModel node)
    {
        if (IsReferenceAvailable && _project is { } project && node.IsFile && !node.IsDraft)
        {
            ReferenceRequested?.Invoke(project, node.Path);
        }
    }

    [RelayCommand]
    private void PinFile(FileNodeViewModel node)
    {
        if (_project is { } project && node.IsFile && !node.IsDraft) Tabs.PinFile(project, node.Path);
    }

    [ObservableProperty] public partial Problem? Failure { get; private set; }

    /// <summary>设备上的角色不是 viewer(能改文件)。</summary>
    [ObservableProperty] public partial bool CanWrite { get; set; } = true;

    /// <summary>项目在本机设备的本机环境里:「在文件管理器中显示 / 用默认程序打开」才有意义。</summary>
    [ObservableProperty] public partial bool CanOpenLocally { get; set; }

    public bool ShowsSearch => Search.IsActive;

    public bool ShowsTree => !Search.IsActive;

    /// <summary>绑定到一个项目(换项目会清空搜索、换一棵树)。</summary>
    public async Task BindAsync(ProjectRef? project, string name, string path, bool canWrite, bool canOpenLocally)
    {
        CanWrite = canWrite;
        CanOpenLocally = canOpenLocally;
        if (project != _project)
        {
            CancelInlineEdit();
            _project = project;
            Selected = null;
            Failure = null;
            Search.Bind(project);
        }

        await Tree.BindAsync(project, name, path).ConfigureAwait(true);
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(ProjectPath));
    }

    // ───────────────────────── 打开 ─────────────────────────

    /// <summary>点一个文件:在主工作区打开它的标签页。</summary>
    [RelayCommand]
    private async Task OpenFileAsync(FileNodeViewModel node)
    {
        if (node.IsMore)
        {
            await Tree.LoadMoreAsync(node).ConfigureAwait(true);
            return;
        }

        if (!node.IsFile || node.IsDraft || node.IsEditing)
        {
            return;
        }

        Selected = node;
        await OpenPreviewPathAsync(node.Path, null).ConfigureAwait(true);
    }

    /// <summary>点一条搜索结果:打开那个文件(内容搜索带行号)。</summary>
    [RelayCommand]
    private Task OpenResultAsync(FileSearchResult result) => OpenPreviewPathAsync(result.Path, result.Line);

    private Task OpenPreviewPathAsync(string path, int? line) =>
        _project is { } project ? Tabs.OpenPreviewAsync(project, path, line) : Task.CompletedTask;

    public Task OpenPathAsync(string path, int? line) =>
        _project is { } project ? Tabs.OpenAsync(project, path, line) : Task.CompletedTask;

    /// <summary>搜索框的结果列表显隐跟着查询文字(视图在查询变化时调,保持 <see cref="ShowsTree"/> / <see cref="ShowsSearch"/> 同步)。</summary>
    public void NotifySearchChanged()
    {
        OnPropertyChanged(nameof(ShowsSearch));
        OnPropertyChanged(nameof(ShowsTree));
    }

    [RelayCommand]
    private Task RefreshAsync() => Tree.RefreshAsync();

    [RelayCommand]
    private void CollapseAll() => Tree.CollapseAll();

    /// <summary>「显示被忽略的文件」开关(<c>.git</c>、<c>node_modules</c> 等默认不列出)。</summary>
    [RelayCommand]
    private void ToggleShowIgnored() => Tree.ShowIgnored = !Tree.ShowIgnored;

    // ───────────────────────── 变更 ─────────────────────────

    private string TargetDirectory(FileNodeViewModel? node) => node switch
    {
        null => "",
        { IsDirectory: true } => node.DisplayNode.Path,
        _ => node.Directory,
    };

    private static string Join(string directory, string name) => directory.Length == 0 ? name : $"{directory}/{name}";

    [RelayCommand]
    private async Task NewFileAsync(FileNodeViewModel? node)
    {
        if (!CanWrite || _project is null) return;
        if (UseInlineEditing)
        {
            await BeginCreateAsync(node, false).ConfigureAwait(true);
            return;
        }

        var directory = TargetDirectory(node);
        var text = _text();
        var name = await _dialogs().PromptTextAsync(text.NewFileTitle, text.NewItemDescription, text.NameLabel, null).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || _project is not { } project)
        {
            return;
        }

        var path = Join(directory, name.Trim());
        var result = await _files.CreateFileAsync(project.DeviceId, project.WorkspaceId, path).ConfigureAwait(true);
        if (!await AfterMutateAsync(result, directory).ConfigureAwait(true))
        {
            return;
        }

        await OpenPathAsync(path, null).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task NewFolderAsync(FileNodeViewModel? node)
    {
        if (!CanWrite || _project is null) return;
        if (UseInlineEditing)
        {
            await BeginCreateAsync(node, true).ConfigureAwait(true);
            return;
        }

        var directory = TargetDirectory(node);
        var text = _text();
        var name = await _dialogs().PromptTextAsync(text.NewFolderTitle, text.NewItemDescription, text.NameLabel, null).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || _project is not { } project)
        {
            return;
        }

        var result = await _files.CreateDirectoryAsync(project.DeviceId, project.WorkspaceId, Join(directory, name.Trim())).ConfigureAwait(true);
        await AfterMutateAsync(result, directory).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RenameAsync(FileNodeViewModel node)
    {
        if (!CanWrite || _project is null || !node.IsEntry || node.IsDraft) return;
        node = node.DisplayNode;
        if (UseInlineEditing)
        {
            CancelInlineEdit();
            _editing = node;
            _editingDirectory = node.Directory;
            node.EditName = node.Name;
            node.IsEditing = true;
            Tree.UpdatePresentation();
            return;
        }

        var text = _text();
        var name = await _dialogs().PromptTextAsync(text.RenameTitle, text.RenameDescription, text.NameLabel, node.Name).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == node.Name || _project is not { } project)
        {
            return;
        }

        var result = await _files.RenameAsync(project.DeviceId, project.WorkspaceId, node.Path, Join(node.Directory, name.Trim())).ConfigureAwait(true);
        if (await AfterMutateAsync(result, node.Directory).ConfigureAwait(true))
        {
            ClosePreviewIfInside(node.Path);
        }
    }

    private async Task BeginCreateAsync(FileNodeViewModel? node, bool folder)
    {
        CancelInlineEdit();
        var project = _project;
        var directory = TargetDirectory(node);
        var parent = directory.Length == 0 ? null : await Tree.RevealPathAsync(directory).ConfigureAwait(true);
        if (_project != project || project is null || directory.Length > 0 && parent is null) return;
        _editingDirectory = directory;
        _creatingFolder = folder;
        _editing = FileNodeViewModel.Draft(parent, folder);
        (parent?.Children ?? Tree.Roots).Insert(0, _editing);
        Tree.UpdatePresentation();
        Selected = _editing;
    }

    [RelayCommand]
    private void CancelInlineEdit()
    {
        if (_editing is not { } node) return;
        node.IsEditing = false;
        if (node.IsDraft) (node.Parent?.Children ?? Tree.Roots).Remove(node);
        _editing = null;
        Tree.UpdatePresentation();
    }

    [RelayCommand]
    private async Task CommitInlineEditAsync()
    {
        if (_editing is not { } node || _project is not { } project || !CanWrite) return;
        var name = node.EditName.Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            Failure = new Problem(ActionOutcome.Invalid, FilesErrorKeys.InvalidPath);
            return;
        }

        if (!node.IsDraft && name == node.Name)
        {
            CancelInlineEdit();
            return;
        }

        var path = Join(_editingDirectory, name);
        var result = node.IsDraft
            ? _creatingFolder
                ? await _files.CreateDirectoryAsync(project.DeviceId, project.WorkspaceId, path).ConfigureAwait(true)
                : await _files.CreateFileAsync(project.DeviceId, project.WorkspaceId, path).ConfigureAwait(true)
            : await _files.RenameAsync(project.DeviceId, project.WorkspaceId, node.Path, path).ConfigureAwait(true);
        if (_project != project || _editing != node) return;
        if (!result.IsOk)
        {
            Failure = Problem.Of(result);
            return;
        }

        var directory = _editingDirectory;
        var open = node.IsDraft && !_creatingFolder;
        if (!node.IsDraft) ClosePreviewIfInside(node.Path);
        CancelInlineEdit();
        await AfterMutateAsync(result, directory).ConfigureAwait(true);
        if (_project != project) return;
        await RevealPathAsync(path).ConfigureAwait(true);
        if (open && _project == project) await OpenPathAsync(path, null).ConfigureAwait(true);
    }

    /// <summary>删除:确认后移进项目根下的回收目录(<c>.next-cowork/trash/</c>,可恢复),不是永久删除。</summary>
    [RelayCommand]
    private async Task DeleteAsync(FileNodeViewModel node)
    {
        if (!CanWrite || !node.IsEntry || node.IsDraft) return;
        node = node.DisplayNode;
        var projectAtPrompt = _project;
        var text = _text();
        if (!await _dialogs().ConfirmAsync(text.DeleteTitle, text.DeleteMessage(node.Name), text.DeleteLabel).ConfigureAwait(true) || _project is not { } project || project != projectAtPrompt)
        {
            return;
        }

        var result = await _files.DeleteAsync(project.DeviceId, project.WorkspaceId, node.Path).ConfigureAwait(true);
        if (await AfterMutateAsync(result, node.Directory).ConfigureAwait(true))
        {
            ClosePreviewIfInside(node.Path);
        }
    }

    private async Task<bool> AfterMutateAsync<T>(ActionResult<T> result, string directory)
    {
        if (!result.IsOk)
        {
            Failure = Problem.Of(result);
            return false;
        }

        Failure = null;
        await Tree.ReloadDirectoryAsync(directory).ConfigureAwait(true);
        return true;
    }

    private void ClosePreviewIfInside(string path)
    {
        if (_project is { } project)
        {
            Tabs.CloseInside(project, path);
        }
    }

    // ───────────────────────── 本机动作 ─────────────────────────

    /// <summary>项目相对路径 → 本机绝对路径(只对本机项目有意义)。</summary>
    public string AbsolutePath(string relative) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectPath, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    [RelayCommand]
    private Task CopyPathAsync(FileNodeViewModel node) => _local().CopyTextAsync(AbsolutePath(node.DisplayNode.Path));

    [RelayCommand]
    private Task CopyRelativePathAsync(FileNodeViewModel node) => _local().CopyTextAsync(node.DisplayNode.Path);

    [RelayCommand]
    private async Task RevealAsync(FileNodeViewModel node)
    {
        if (CanOpenLocally)
        {
            await _local().RevealAsync(AbsolutePath(node.DisplayNode.Path)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task OpenExternallyAsync(FileNodeViewModel node)
    {
        if (CanOpenLocally && node.IsFile)
        {
            await _local().OpenAsync(AbsolutePath(node.Path)).ConfigureAwait(true);
        }
    }

    /// <summary>另存为:内容从设备读来(文本 / 图片 / 二进制按块读完),存到用户选的本机位置。</summary>
    [RelayCommand]
    private async Task SaveAsAsync(FileNodeViewModel node)
    {
        if (!node.IsFile || _project is not { } project)
        {
            return;
        }

        var source = new FilePreviewViewModel(_files, project, node.Path, canWrite: false);
        await source.LoadAsync().ConfigureAwait(true);
        if (source.IsFailed)
        {
            Failure = source.Failure;
            return;
        }

        var bytes = await source.ReadAllBytesAsync().ConfigureAwait(true);
        if (bytes is null)
        {
            Failure = source.Failure;
            return;
        }

        await _local().SaveAsync(node.Name, bytes).ConfigureAwait(true);
    }
}
