using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 右侧当前项目的文件树。目录<b>惰性展开</b>:展开时才 <c>files.list</c> 一层(分页,默认 2000 条),没展开的目录里只有一个占位子节点,所以树控件认得出它是「能展开的」。
/// 排序只有 Core 的 <see cref="FileEntrySort"/> 一份(目录在前,不分大小写,数字按值);忽略表里的条目(<c>.git</c>、<c>node_modules</c>……)缺省不列出,
/// 「显示忽略的文件」打开后列出来并淡显(<see cref="FileNodeViewModel.IsDimmed"/>)。展开状态是纯 UI 状态(按路径记着,刷新后恢复);
/// 文件内容、目录列表都不进 Store(设备是真源),Git 状态角标来自 Git 镜像(<see cref="ApplyChanges"/>)。
/// </summary>
public sealed partial class FileTreeViewModel(IFilesCommands files) : ObservableObject
{
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private ProjectRef? _project;
    private string _root = "";
    private int _generation;
    private bool _firstLoad = true;
    private IReadOnlyDictionary<string, GitChange> _changes = new Dictionary<string, GitChange>();

    /// <summary>一次列目录完成、节点集合变了(视图据此把该展开的目录在树控件里展开 / 刷新)。参数是列出来的目录;根目录是 null。</summary>
    public event Action<FileNodeViewModel?>? Loaded;

    /// <summary>项目根下的一层(<see cref="FileNodeViewModel"/>)。</summary>
    public ObservableCollection<FileNodeViewModel> Roots { get; } = [];

    [ObservableProperty] public partial string ProjectName { get; private set; } = "";

    [ObservableProperty] public partial string ProjectPath { get; private set; } = "";

    [ObservableProperty] public partial bool IsEmpty { get; private set; } = true;

    [ObservableProperty] public partial bool IsLoading { get; private set; }

    /// <summary>最近一次列目录失败的原因(成功后清掉)。</summary>
    [ObservableProperty] public partial Problem? Failure { get; private set; }

    /// <summary>显示忽略表里的条目(<c>hideIgnored: false</c>)。纯 UI 状态。</summary>
    [ObservableProperty] public partial bool ShowIgnored { get; set; }

    partial void OnShowIgnoredChanged(bool value) => _ = RefreshAsync();

    /// <summary>只合并已经惰性加载、且唯一子项为目录的链;真实节点和路径不变。</summary>
    [ObservableProperty] public partial bool CompactFolders { get; set; } = true;

    partial void OnCompactFoldersChanged(bool value) => UpdatePresentation();

    public void UpdatePresentation()
    {
        foreach (var root in Roots)
        {
            root.UpdatePresentation(CompactFolders);
        }

        Loaded?.Invoke(null);
    }

    /// <summary>绑定到一个项目(换了项目:展开状态不跨项目保留,默认展开 <c>src</c>)。同一个项目再次绑定只刷新名字。</summary>
    public async Task BindAsync(ProjectRef? project, string name, string path)
    {
        ProjectName = name;
        ProjectPath = path;
        if (project == _project && string.Equals(path, _root, StringComparison.Ordinal))
        {
            return;
        }

        _project = project;
        _root = path;
        _generation++;
        _expanded.Clear();
        _firstLoad = true;
        Roots.Clear();
        IsEmpty = true;
        Failure = null;
        if (project is not null)
        {
            await LoadRootAsync().ConfigureAwait(true);
        }
    }

    /// <summary>重新列根目录和所有展开着的目录。</summary>
    public async Task RefreshAsync()
    {
        if (_project is null)
        {
            return;
        }

        _generation++;
        await LoadRootAsync().ConfigureAwait(true);
    }

    private async Task LoadRootAsync()
    {
        var generation = _generation;
        IsLoading = true;
        try
        {
            var page = await ListAsync("", 0).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            if (page is null)
            {
                return;
            }

            Roots.Clear();
            foreach (var node in Build(null, page))
            {
                Roots.Add(node);
            }

            AddMore(Roots, null, page);
            IsEmpty = Roots.Count == 0;
            if (_firstLoad)
            {
                _firstLoad = false;
                if (Roots.FirstOrDefault(n => n.IsDirectory && n.Path == "src") is { } src)
                {
                    _expanded.Add(src.Path);
                }
            }

            await RestoreExpandedAsync(Roots, generation).ConfigureAwait(true);
            ApplyChanges(_changes);
            UpdatePresentation();
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
            }
        }
    }

    private async Task RestoreExpandedAsync(IEnumerable<FileNodeViewModel> nodes, int generation)
    {
        foreach (var node in nodes.Where(n => n.IsDirectory && _expanded.Contains(n.Path)).ToList())
        {
            await ExpandAsync(node).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }
        }
    }

    /// <summary>展开一个目录(首次展开才列目录)。</summary>
    public async Task ExpandAsync(FileNodeViewModel node)
    {
        if (!node.IsDirectory)
        {
            return;
        }

        node.IsExpanded = true;
        _expanded.Add(node.Path);
        if (node.IsLoading)
        {
            return;
        }

        if (node.ChildrenLoaded)
        {
            await ExpandCompactChildAsync(node).ConfigureAwait(true);
            UpdatePresentation();
            return;
        }

        var generation = _generation;
        node.IsLoading = true;
        try
        {
            var page = await ListAsync(node.Path, 0).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            if (page is null)
            {
                node.IsExpanded = false;
                _expanded.Remove(node.Path);
                return;
            }

            node.Children.Clear();
            foreach (var child in Build(node, page))
            {
                node.Children.Add(child);
            }

            AddMore(node.Children, node, page);
            node.ChildrenLoaded = true;
            ApplyChanges(_changes);
            await RestoreExpandedAsync(node.Children, generation).ConfigureAwait(true);
            await ExpandCompactChildAsync(node).ConfigureAwait(true);
            UpdatePresentation();
        }
        finally
        {
            node.IsLoading = false;
        }
    }

    private async Task ExpandCompactChildAsync(FileNodeViewModel node)
    {
        if (CompactFolders && !node.IsLink && node.Children.Count == 1 && node.Children[0] is { IsDirectory: true, IsLink: false, IsDraft: false } child)
        {
            await ExpandAsync(child).ConfigureAwait(true);
        }
    }

    /// <summary>沿真实路径惰性加载祖先并翻页,返回真实节点;不依赖显示名称。</summary>
    public async Task<FileNodeViewModel?> RevealPathAsync(string path)
    {
        var generation = _generation;
        var nodes = Roots;
        FileNodeViewModel? found = null;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            found = nodes.FirstOrDefault(n => n.Name == segment && n.IsEntry && !n.IsDraft);
            while (found is null && nodes.LastOrDefault() is { IsMore: true } more)
            {
                var count = nodes.Count;
                await LoadMoreAsync(more).ConfigureAwait(true);
                if (generation != _generation || nodes.Count == count && nodes.Contains(more)) return null;
                found = nodes.FirstOrDefault(n => n.Name == segment && n.IsEntry && !n.IsDraft);
            }

            if (found is null || generation != _generation) return null;
            if (found.IsDirectory)
            {
                await ExpandAsync(found).ConfigureAwait(true);
                if (generation != _generation) return null;
            }

            nodes = found.Children;
        }

        UpdatePresentation();
        return found;
    }

    /// <summary>返回真实节点对应的可见紧凑行。</summary>
    public FileNodeViewModel DisplayRow(FileNodeViewModel node)
    {
        var row = node;
        while (row.Parent is { } parent && parent.DisplayNode != parent && parent.Children.Count == 1)
        {
            row = parent;
        }

        return row;
    }

    /// <summary>目录被用户收起(树控件的事件)。</summary>
    public void Collapse(FileNodeViewModel node)
    {
        var end = node.DisplayNode;
        for (var current = node; ; current = current.Children[0])
        {
            current.IsExpanded = false;
            _expanded.Remove(current.Path);
            if (current == end) break;
        }
    }

    public void CollapseAll()
    {
        _expanded.Clear();
        foreach (var node in Roots)
        {
            CollapseRecursive(node);
        }

        Loaded?.Invoke(null);
    }

    private static void CollapseRecursive(FileNodeViewModel node)
    {
        node.IsExpanded = false;
        foreach (var child in node.Children)
        {
            CollapseRecursive(child);
        }
    }

    /// <summary>「显示更多」:同一层的下一页(追加,不重排前面已经显示的)。</summary>
    public async Task LoadMoreAsync(FileNodeViewModel more)
    {
        var parent = more.Parent;
        var siblings = parent?.Children ?? Roots;
        var generation = _generation;
        var loaded = siblings.Count(n => n.IsEntry && !n.IsDraft);
        var page = await ListAsync(parent?.Path ?? "", loaded).ConfigureAwait(true);
        if (generation != _generation || page is null)
        {
            return;
        }

        siblings.Remove(more);
        foreach (var child in Build(parent, page))
        {
            siblings.Add(child);
        }

        AddMore(siblings, parent, page, loaded);
        ApplyChanges(_changes);
        UpdatePresentation();
    }

    /// <summary>重新列一个目录(新建 / 改名 / 删除之后)。空串 = 根。</summary>
    public async Task ReloadDirectoryAsync(string directory)
    {
        if (directory.Length == 0)
        {
            await RefreshAsync().ConfigureAwait(true);
            return;
        }

        var node = Find(directory);
        if (node is null || !node.IsDirectory)
        {
            return;
        }

        node.ChildrenLoaded = false;
        node.Children.Clear();
        node.Children.Add(FileNodeViewModel.Placeholder(node));
        if (node.IsExpanded)
        {
            await ExpandAsync(node).ConfigureAwait(true);
        }

        UpdatePresentation();
    }

    public FileNodeViewModel? Find(string path)
    {
        var nodes = Roots;
        FileNodeViewModel? found = null;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            found = nodes.FirstOrDefault(n => n.Name == segment && n.IsEntry && !n.IsDraft);
            if (found is null)
            {
                return null;
            }

            nodes = found.Children;
        }

        return found;
    }

    /// <summary>Git 状态角标:文件显示字母,含变更的目录(哪怕还没展开)后面一个点。键是项目相对路径。</summary>
    public void ApplyChanges(IReadOnlyDictionary<string, GitChange> changes)
    {
        _changes = changes;
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in changes.Keys)
        {
            for (var i = path.IndexOf('/', StringComparison.Ordinal); i > 0; i = path.IndexOf('/', i + 1))
            {
                dirs.Add(path[..i]);
            }
        }

        Mark(Roots);

        void Mark(IEnumerable<FileNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsMore || node.IsPlaceholder)
                {
                    continue;
                }

                node.Change = node.IsFile && changes.TryGetValue(node.Path, out var change) ? change : null;
                node.HasChangedDescendant = node.IsDirectory && dirs.Contains(node.Path);
                Mark(node.Children);
            }
        }
    }

    private async Task<FilesListResult?> ListAsync(string path, int offset)
    {
        if (_project is not { } project)
        {
            return null;
        }

        var result = await files.ListAsync(project.DeviceId, project.WorkspaceId, path, "name", hideIgnored: !ShowIgnored, offset: offset).ConfigureAwait(true);
        if (!result.IsOk || result.Value is null)
        {
            Failure = Problem.Of(result);
            return null;
        }

        Failure = null;
        return result.Value;
    }

    private static IEnumerable<FileNodeViewModel> Build(FileNodeViewModel? parent, FilesListResult page) =>
        FileEntrySort.Sort(page.Entries, FileSortBy.Name, static e => e.Name, static e => e.Kind == "dir", static e => e.Mtime, static e => e.Size)
            .Select(e => new FileNodeViewModel(e, parent));

    private static void AddMore(ObservableCollection<FileNodeViewModel> into, FileNodeViewModel? parent, FilesListResult page, int alreadyLoaded = 0)
    {
        if (page.Truncated)
        {
            into.Add(FileNodeViewModel.More(parent, page.Total - alreadyLoaded - page.Entries.Length));
        }
    }
}

/// <summary>文件树里文件的图标种类(按扩展名,纯函数)。</summary>
public enum FileGlyph
{
    Other,
    Code,
    Text,
    Image,
    Config,
}

public static class FileGlyphs
{
    private static readonly HashSet<string> Code = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".axaml", ".xaml", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".py", ".go", ".rs", ".java", ".kt", ".swift", ".rb", ".php", ".c", ".h", ".cpp", ".hpp",
        ".sh", ".ps1", ".sql", ".css", ".scss", ".html", ".htm", ".lua",
    };

    private static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".txt", ".rst", ".log", ".csv" };

    private static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico", ".bmp" };

    private static readonly HashSet<string> Config = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonc", ".yml", ".yaml", ".toml", ".xml", ".csproj", ".props", ".targets", ".slnx", ".sln", ".xlf", ".editorconfig", ".gitignore", ".gitattributes", ".env",
    };

    public static FileGlyph Of(string name)
    {
        var dot = name.LastIndexOf('.');
        var extension = dot >= 0 ? name[dot..] : "";
        return Code.Contains(extension) ? FileGlyph.Code
            : Text.Contains(extension) ? FileGlyph.Text
            : Image.Contains(extension) ? FileGlyph.Image
            : Config.Contains(extension) ? FileGlyph.Config
            : FileGlyph.Other;
    }
}

/// <summary>文件树里的一行:文件、目录、「加载中」占位或「显示更多」。</summary>
public sealed partial class FileNodeViewModel : ObservableObject
{
    private FileNodeViewModel(string path, string name, FileNodeViewModel? parent)
    {
        Path = path;
        Name = name;
        Parent = parent;
        DisplayNode = this;
    }

    public FileNodeViewModel(FileEntryDto entry, FileNodeViewModel? parent)
        : this(entry.Path, entry.Name, parent)
    {
        IsDirectory = entry.Kind == "dir";
        IsLink = entry.Link;
        IsHidden = entry.Hidden;
        IsIgnored = entry.Ignored;
        Size = entry.Size;
        Mtime = entry.Mtime;
        if (IsDirectory)
        {
            // 没展开的目录里放一个占位子节点:树控件按「有没有子节点」决定显不显示展开箭头
            Children.Add(Placeholder(this));
        }
    }

    /// <summary>目录还没列出来时它里面的占位行(显示「加载中」)。</summary>
    public static FileNodeViewModel Placeholder(FileNodeViewModel parent) =>
        new(parent.Path + "/\u0000loading", "…", parent) { IsPlaceholder = true };

    /// <summary>一层列表被分页截断时末尾的「显示更多」行。</summary>
    public static FileNodeViewModel More(FileNodeViewModel? parent, int remaining) =>
        new((parent?.Path ?? "") + "/\u0000more", "…", parent) { IsMore = true, Remaining = Math.Max(0, remaining) };

    public string Path { get; }

    public string Name { get; }

    public FileNodeViewModel DisplayNode { get; private set; } = null!;

    public string DisplayName => DisplayNode == this || Children.Count == 0 ? Name : Name + "/" + Children[0].DisplayName;

    public ObservableCollection<FileNodeViewModel> DisplayChildren => DisplayNode?.Children ?? Children;

    internal void UpdatePresentation(bool compact)
    {
        foreach (var child in Children)
        {
            child.UpdatePresentation(compact);
        }

        DisplayNode = compact && !IsLink && !IsEditing && ChildrenLoaded && Children.Count == 1 && Children[0] is { IsDirectory: true, IsLink: false, IsDraft: false, IsEditing: false } childDirectory
            ? childDirectory.DisplayNode : this;
        OnPropertyChanged(nameof(DisplayNode));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplayChildren));
    }

    public static FileNodeViewModel Draft(FileNodeViewModel? parent, bool directory) =>
        new((parent?.Path ?? "") + "/\u0000draft", "", parent) { IsDirectory = directory, IsDraft = true, IsEditing = true };

    public bool IsDraft { get; private init; }

    public bool CanReference => IsFile && !IsDraft;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLabel))]
    public partial bool IsEditing { get; internal set; }

    public bool ShowsLabel => IsEntry && !IsEditing;

    [ObservableProperty] public partial string EditName { get; set; } = "";

    public FileNodeViewModel? Parent { get; }

    public bool IsDirectory { get; private init; }

    public bool IsFile => !IsDirectory && !IsPlaceholder && !IsMore;

    /// <summary>文件图标的种类(代码 / 文本 / 图片 / 配置 / 其它)。</summary>
    public FileGlyph Glyph => IsFile ? FileGlyphs.Of(Name) : FileGlyph.Other;

    public bool IsCodeFile => Glyph == FileGlyph.Code;

    public bool IsTextFile => Glyph == FileGlyph.Text;

    public bool IsImageFile => Glyph == FileGlyph.Image;

    public bool IsConfigFile => Glyph == FileGlyph.Config;

    public bool IsOtherFile => IsFile && Glyph == FileGlyph.Other;

    /// <summary>目录图标:展开着的目录是打开的文件夹。</summary>
    public bool IsClosedDirectory => IsDirectory && !IsExpanded;

    public bool IsOpenDirectory => IsDirectory && IsExpanded;

    public bool IsLink { get; private init; }

    public bool IsHidden { get; private init; }

    public bool IsIgnored { get; private init; }

    /// <summary>淡显:点号开头的、忽略表里的。</summary>
    public bool IsDimmed => IsHidden || IsIgnored;

    public long? Size { get; private init; }

    public long? Mtime { get; private init; }

    public bool IsPlaceholder { get; private init; }

    public bool IsMore { get; private init; }

    /// <summary><see cref="IsMore"/> 行:还有多少条没显示。</summary>
    public int Remaining { get; private init; }

    /// <summary>真实的行(不是占位、不是「显示更多」)。</summary>
    public bool IsEntry => !IsPlaceholder && !IsMore;

    public bool ChildrenLoaded { get; internal set; }

    public ObservableCollection<FileNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClosedDirectory), nameof(IsOpenDirectory))]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty] public partial bool IsLoading { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangeLetter), nameof(HasChange), nameof(IsAddedChange), nameof(IsDeletedChange), nameof(IsModifiedChange), nameof(IsRenamedChange))]
    public partial GitChange? Change { get; internal set; }

    /// <summary>目录下有变更的文件时,目录名后面显示一个点。</summary>
    [ObservableProperty] public partial bool HasChangedDescendant { get; internal set; }

    public string? ChangeLetter => Change switch
    {
        GitChange.Modified => "M",
        GitChange.Added => "A",
        GitChange.Deleted => "D",
        GitChange.Renamed => "R",
        GitChange.Untracked => "U",
        _ => null,
    };

    public bool HasChange => Change is not null;

    public bool IsAddedChange => Change is GitChange.Added or GitChange.Untracked;

    public bool IsDeletedChange => Change is GitChange.Deleted;

    public bool IsModifiedChange => Change is GitChange.Modified;

    public bool IsRenamedChange => Change is GitChange.Renamed;

    /// <summary>所在目录(项目相对,根是空串)。</summary>
    public string Directory => Path.LastIndexOf('/') is > 0 and var i ? Path[..i] : "";
}
