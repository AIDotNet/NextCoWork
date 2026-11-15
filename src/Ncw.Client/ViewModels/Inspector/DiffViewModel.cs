using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

public enum DiffMode
{
    Unified,
    Split,
}

/// <summary>
/// 一个文件的 diff(Git 面板和改动审查面板共用)。行模型来自 <see cref="UnifiedDiff"/>;显示方式(统一 / 并排)是纯 UI 状态。
/// 不含界面文字:状态(二进制、被截断、过大、没有差异)只是几个布尔值,视图按语言写提示。
/// </summary>
public sealed partial class DiffViewModel : ObservableObject
{
    private DiffViewModel(string path, ImmutableArray<DiffRow> rows, int added, int removed)
    {
        Path = path;
        Rows = rows;
        SplitRows = UnifiedDiff.Split(rows);
        Added = added;
        Removed = removed;
    }

    public string Path { get; }

    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    public ImmutableArray<DiffRow> Rows { get; }

    public ImmutableArray<SplitRow> SplitRows { get; }

    public int Added { get; }

    public int Removed { get; }

    public bool HasStats => Added > 0 || Removed > 0;

    /// <summary>二进制文件:只提示,没有行可显示。</summary>
    public bool IsBinary { get; private init; }

    /// <summary>设备把过长的 diff 截断了。</summary>
    public bool IsTruncated { get; private init; }

    /// <summary>文件太大,设备没有给出全文(改动审查的 <c>Oversize</c>)。</summary>
    public bool IsOversize { get; private init; }

    /// <summary>没有行级差异(只改了权限 / 内容相同)。</summary>
    public bool IsEmpty => Rows.IsEmpty && !IsBinary && !IsOversize;

    public bool HasRows => !Rows.IsEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnified), nameof(IsSplit))]
    public partial DiffMode Mode { get; set; }

    [RelayCommand]
    private void SetMode(DiffMode mode) => Mode = mode;

    public bool IsUnified => Mode == DiffMode.Unified;

    public bool IsSplit => Mode == DiffMode.Split;

    /// <summary>从 <c>git diff</c> 的 unified 文本建。</summary>
    public static DiffViewModel FromUnified(string path, string text, bool binary, bool truncated)
    {
        var rows = binary ? [] : UnifiedDiff.Parse(text);
        var (added, removed) = UnifiedDiff.Count(rows);
        return new DiffViewModel(path, rows, added, removed) { IsBinary = binary, IsTruncated = truncated };
    }

    /// <summary>从 before / after 全文建(改动审查)。<paramref name="oversize"/>:设备没给全文。</summary>
    public static DiffViewModel FromSides(string path, string before, string after, bool oversize)
    {
        if (oversize)
        {
            return new DiffViewModel(path, [], 0, 0) { IsOversize = true };
        }

        var rows = UnifiedDiff.FromSides(before, after);
        // 计数口径是 Core 的 LineDiff(Agent 封包变更集时也用它):改成空文件、新建空文件这类边界两边一致
        var (added, removed) = LineDiff.Count(string.IsNullOrEmpty(before) ? null : before, string.IsNullOrEmpty(after) ? null : after);
        return new DiffViewModel(path, rows, added, removed);
    }
}
