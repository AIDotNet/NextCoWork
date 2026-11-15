using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 文件页签(默认页签):惰性文件树、文件名 / 内容搜索、文件预览与简易编辑、右键菜单(新建 / 重命名 / 删除进回收站 / 复制路径 / 在文件管理器中显示 / 用默认程序打开 / 另存为)。
/// 文件在设备上:树、预览、搜索结果都是纯 UI 状态,<b>不进 Store</b>(设备是真源);Git 状态角标来自 Git 镜像(由 <see cref="InspectorViewModel"/> 推进树)。
/// 「显示 / 打开 / 另存为」是客户端本机的动作,经 <see cref="ILocalFiles"/>,只对「本机设备 + 本机环境」的项目开放(<see cref="CanOpenLocally"/>)。
/// </summary>
public sealed partial class FilesPanelViewModel : ObservableObject
{
    private readonly IFilesCommands _files;
    private readonly Func<IDialogService> _dialogs;
    private readonly Func<IInspectorText> _text;
    private readonly Func<ILocalFiles> _local;
    private ProjectRef? _project;

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
                NotifySearchChanged();
            }
        };
    }

    public FileTreeViewModel Tree { get; }

    public FileSearchViewModel Search { get; }

    public ProjectRef? Project => _project;

    public string ProjectName => Tree.ProjectName;

    public string ProjectPath => Tree.ProjectPath;

    /// <summary>打开的文件(预览 / 编辑);null = 显示树。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(ShowsTree), nameof(ShowsSearch))]
    public partial FilePreviewViewModel? Preview { get; private set; }

    [ObservableProperty] public partial FileNodeViewModel? Selected { get; set; }

    [ObservableProperty] public partial Problem? Failure { get; private set; }

    /// <summary>设备上的角色不是 viewer(能改文件)。</summary>
    [ObservableProperty] public partial bool CanWrite { get; set; } = true;

    /// <summary>项目在本机设备的本机环境里:「在文件管理器中显示 / 用默认程序打开」才有意义。</summary>
    [ObservableProperty] public partial bool CanOpenLocally { get; set; }

    public bool HasPreview => Preview is not null;

    public bool ShowsSearch => Preview is null && Search.IsActive;

    public bool ShowsTree => Preview is null && !Search.IsActive;

    /// <summary>绑定到一个项目(换项目会关掉预览、清空搜索、换一棵树)。</summary>
    public async Task BindAsync(ProjectRef? project, string name, string path, bool canWrite, bool canOpenLocally)
    {
        CanWrite = canWrite;
        CanOpenLocally = canOpenLocally;
        if (project != _project)
        {
            _project = project;
            Preview = null;
            Selected = null;
            Failure = null;
            Search.Bind(project);
        }

        await Tree.BindAsync(project, name, path).ConfigureAwait(true);
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(ProjectPath));
    }

    // ───────────────────────── 预览 ─────────────────────────

    /// <summary>点一个文件:预览。</summary>
    [RelayCommand]
    private async Task OpenFileAsync(FileNodeViewModel node)
    {
        if (node.IsMore)
        {
            await Tree.LoadMoreAsync(node).ConfigureAwait(true);
            return;
        }

        if (!node.IsFile)
        {
            return;
        }

        Selected = node;
        await OpenPathAsync(node.Path, null).ConfigureAwait(true);
    }

    /// <summary>点一条搜索结果:预览那个文件(内容搜索带行号)。</summary>
    [RelayCommand]
    private Task OpenResultAsync(FileSearchResult result) => OpenPathAsync(result.Path, result.Line);

    public async Task OpenPathAsync(string path, int? line)
    {
        if (_project is not { } project)
        {
            return;
        }

        if (Preview is { IsDirty: true } && !await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            return;
        }

        var preview = new FilePreviewViewModel(_files, project, path, CanWrite, line);
        Preview = preview;
        OnPropertyChanged(nameof(ShowsSearch));
        await preview.LoadAsync().ConfigureAwait(true);
    }

    /// <summary>关掉预览回到树。有没保存的编辑要先确认。</summary>
    [RelayCommand]
    private async Task ClosePreviewAsync()
    {
        if (Preview is { IsDirty: true } && !await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            return;
        }

        Preview = null;
        OnPropertyChanged(nameof(ShowsSearch));
        OnPropertyChanged(nameof(ShowsTree));
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        var text = _text();
        return await _dialogs().ConfirmAsync(text.DiscardTitle, text.DiscardMessage, text.DiscardLabel).ConfigureAwait(true);
    }

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
        { IsDirectory: true } => node.Path,
        _ => node.Directory,
    };

    private static string Join(string directory, string name) => directory.Length == 0 ? name : $"{directory}/{name}";

    [RelayCommand]
    private async Task NewFileAsync(FileNodeViewModel? node)
    {
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

    /// <summary>删除:确认后移进项目根下的回收目录(<c>.next-cowork/trash/</c>,可恢复),不是永久删除。</summary>
    [RelayCommand]
    private async Task DeleteAsync(FileNodeViewModel node)
    {
        var text = _text();
        if (!await _dialogs().ConfirmAsync(text.DeleteTitle, text.DeleteMessage(node.Name), text.DeleteLabel).ConfigureAwait(true) || _project is not { } project)
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
        if (Preview is { } preview && (preview.Path == path || preview.Path.StartsWith(path + "/", StringComparison.Ordinal)))
        {
            Preview = null;
            NotifySearchChanged();
        }
    }

    // ───────────────────────── 本机动作 ─────────────────────────

    /// <summary>项目相对路径 → 本机绝对路径(只对本机项目有意义)。</summary>
    public string AbsolutePath(string relative) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectPath, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    [RelayCommand]
    private Task CopyPathAsync(FileNodeViewModel node) => _local().CopyTextAsync(AbsolutePath(node.Path));

    [RelayCommand]
    private Task CopyRelativePathAsync(FileNodeViewModel node) => _local().CopyTextAsync(node.Path);

    [RelayCommand]
    private async Task RevealAsync(FileNodeViewModel node)
    {
        if (CanOpenLocally)
        {
            await _local().RevealAsync(AbsolutePath(node.Path)).ConfigureAwait(true);
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

    /// <summary>预览头里的「另存为」。</summary>
    [RelayCommand]
    private async Task SavePreviewAsAsync()
    {
        if (Preview is not { } preview)
        {
            return;
        }

        var bytes = await preview.ReadAllBytesAsync().ConfigureAwait(true);
        if (bytes is not null)
        {
            await _local().SaveAsync(preview.Name, bytes).ConfigureAwait(true);
        }
    }
}
