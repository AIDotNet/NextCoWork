using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ncw.Client.Stores;

namespace Ncw.Client.ViewModels;

/// <summary>右侧面板的文件树。由平铺的 <see cref="FileEntry"/> 建成目录树;展开状态是纯 UI 状态,重建时按路径保留。</summary>
public sealed partial class FileTreeViewModel : ObservableObject
{
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private string _root = "";

    public ObservableCollection<FileNodeViewModel> Roots { get; } = [];

    [ObservableProperty] public partial string ProjectName { get; private set; } = "";

    [ObservableProperty] public partial string ProjectPath { get; private set; } = "";

    [ObservableProperty] public partial bool IsEmpty { get; private set; } = true;

    public void Load(string projectName, string projectPath, IReadOnlyList<FileEntry> entries)
    {
        if (!string.Equals(_root, projectPath, StringComparison.Ordinal))
        {
            // 换了项目:展开状态不跨项目保留,默认展开 src。
            _expanded.Clear();
            _expanded.Add("src");
            _root = projectPath;
        }
        else
        {
            RememberExpanded(Roots);
        }

        ProjectName = projectName;
        ProjectPath = projectPath;
        Roots.Clear();

        var byPath = new Dictionary<string, FileNodeViewModel>(StringComparer.Ordinal);
        foreach (var entry in entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal))
        {
            var parentPath = entry.RelativePath.LastIndexOf('/') is > 0 and var i ? entry.RelativePath[..i] : "";
            var node = new FileNodeViewModel(entry, _expanded.Contains(entry.RelativePath));
            byPath[entry.RelativePath] = node;
            if (parentPath.Length > 0 && byPath.TryGetValue(parentPath, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                Roots.Add(node);
            }
        }

        foreach (var node in Roots)
        {
            node.Sort();
            node.RefreshChangeMarkers();
        }

        Sort(Roots);
        IsEmpty = Roots.Count == 0;
    }

    private void RememberExpanded(IEnumerable<FileNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsExpanded)
            {
                _expanded.Add(node.Path);
            }
            else
            {
                _expanded.Remove(node.Path);
            }

            RememberExpanded(node.Children);
        }
    }

    /// <summary>目录在前,同类按名字排序。</summary>
    internal static void Sort(ObservableCollection<FileNodeViewModel> nodes)
    {
        var ordered = nodes.OrderByDescending(n => n.IsDirectory).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
        nodes.Clear();
        foreach (var node in ordered)
        {
            nodes.Add(node);
        }
    }
}

public sealed partial class FileNodeViewModel(FileEntry entry, bool expanded) : ObservableObject
{
    public string Path { get; } = entry.RelativePath;

    public string Name { get; } = entry.RelativePath[(entry.RelativePath.LastIndexOf('/') + 1)..];

    public bool IsDirectory { get; } = entry.IsDirectory;

    public bool IsFile => !IsDirectory;

    public bool IsHidden => Name.StartsWith('.');

    public GitChange? Change { get; } = entry.Change;

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

    public ObservableCollection<FileNodeViewModel> Children { get; } = [];

    [ObservableProperty] public partial bool IsExpanded { get; set; } = expanded;

    /// <summary>目录下有变更的文件时,目录名后面显示一个点。</summary>
    [ObservableProperty] public partial bool HasChangedDescendant { get; private set; }

    internal void Sort()
    {
        FileTreeViewModel.Sort(Children);
        foreach (var child in Children)
        {
            child.Sort();
        }
    }

    internal bool RefreshChangeMarkers()
    {
        var any = false;
        foreach (var child in Children)
        {
            any |= child.RefreshChangeMarkers();
        }

        HasChangedDescendant = IsDirectory && any;
        return any || HasChange;
    }
}
