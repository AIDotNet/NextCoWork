using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ncw.Client.Actions;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

public enum FileSearchMode
{
    /// <summary>按文件名(<c>files.suggest</c>:Core 的 <see cref="FuzzyPath"/> 分档排序,设备上算)。</summary>
    Names,

    /// <summary>按内容(<c>files.search</c>)。</summary>
    Contents,
}

/// <summary>一条搜索结果。名字搜索只有路径;内容搜索带行号和那一行的文字。</summary>
public sealed record FileSearchResult(string Path, string Name, string Directory, int? Line = null, int? Column = null, string? Text = null)
{
    public bool HasLine => Line is not null;

    public string Location => Line is { } line ? $"{Path}:{line}" : Path;
}

/// <summary>
/// 文件管理器顶部的搜索。查询文字、模式、结果都是纯 UI 状态(不进 Store);输入停顿 <see cref="Debounce"/> 后才发请求,
/// 新的查询会取消还没回来的旧查询(旧结果不会盖掉新结果)。
/// </summary>
public sealed partial class FileSearchViewModel(IFilesCommands files, TimeSpan? debounce = null, TimeProvider? clock = null) : ObservableObject
{
    private readonly TimeSpan _debounce = debounce ?? TimeSpan.FromMilliseconds(220);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private ProjectRef? _project;
    private CancellationTokenSource? _running;

    public ObservableCollection<FileSearchResult> Results { get; } = [];

    [ObservableProperty] public partial string Query { get; set; } = "";

    [ObservableProperty] public partial FileSearchMode Mode { get; set; }

    [ObservableProperty] public partial bool IsSearching { get; private set; }

    /// <summary>结果被设备截断了(命中太多或遍历到上限)。</summary>
    [ObservableProperty] public partial bool Truncated { get; private set; }

    [ObservableProperty] public partial Problem? Failure { get; private set; }

    /// <summary>当前这一轮搜索完成时完成(测试用:等到结果落定)。</summary>
    public Task Settled { get; private set; } = Task.CompletedTask;

    /// <summary>有查询文字:界面用结果列表替换文件树。</summary>
    public bool IsActive => Query.Trim().Length > 0;

    public bool IsNamesMode => Mode == FileSearchMode.Names;

    public bool IsContentsMode => Mode == FileSearchMode.Contents;

    public bool HasNoResults => IsActive && !IsSearching && Results.Count == 0 && Failure is null;

    public TimeSpan Debounce => _debounce;

    public void Bind(ProjectRef? project)
    {
        if (_project == project)
        {
            return;
        }

        _project = project;
        Query = "";
    }

    partial void OnQueryChanged(string value) => Restart();

    partial void OnModeChanged(FileSearchMode value)
    {
        OnPropertyChanged(nameof(IsNamesMode));
        OnPropertyChanged(nameof(IsContentsMode));
        Restart();
    }

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(HasNoResults));

    partial void OnFailureChanged(Problem? value) => OnPropertyChanged(nameof(HasNoResults));

    private void Restart()
    {
        OnPropertyChanged(nameof(IsActive));
        _running?.Cancel();
        _running = null;
        var query = Query.Trim();
        if (query.Length == 0 || _project is null)
        {
            Results.Clear();
            Truncated = false;
            Failure = null;
            IsSearching = false;
            Settled = Task.CompletedTask;
            OnPropertyChanged(nameof(HasNoResults));
            return;
        }

        var cts = new CancellationTokenSource();
        _running = cts;
        IsSearching = true;
        Settled = RunAsync(_project.Value, query, Mode, cts);
    }

    private async Task RunAsync(ProjectRef project, string query, FileSearchMode mode, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            if (_debounce > TimeSpan.Zero)
            {
                await Task.Delay(_debounce, _clock, ct).ConfigureAwait(true);
            }

            List<FileSearchResult> found;
            bool truncated;
            if (mode == FileSearchMode.Names)
            {
                var result = await files.SuggestAsync(project.DeviceId, project.WorkspaceId, query, FilesLimits.SuggestMax, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (!result.IsOk || result.Value is null)
                {
                    Fail(Problem.Of(result));
                    return;
                }

                found = [.. result.Value.Suggestions.Select(s => new FileSearchResult(s.Path, s.Name, DirectoryOf(s.Path)))];
                truncated = result.Value.Truncated;
            }
            else
            {
                var result = await files.SearchAsync(project.DeviceId, project.WorkspaceId, new FilesSearchParams(project.WorkspaceId, query, Limit: FilesLimits.SearchDefault), ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (!result.IsOk || result.Value is null)
                {
                    Fail(Problem.Of(result));
                    return;
                }

                found = [.. result.Value.Matches.Select(m => new FileSearchResult(m.Path, m.Path[(m.Path.LastIndexOf('/') + 1)..], DirectoryOf(m.Path), m.Line, m.Column, m.Text.Trim()))];
                truncated = result.Value.Truncated;
            }

            Results.Clear();
            foreach (var item in found)
            {
                Results.Add(item);
            }

            Truncated = truncated;
            Failure = null;
            IsSearching = false;
        }
        catch (OperationCanceledException)
        {
            // 被新的查询取代
        }
    }

    private void Fail(Problem problem)
    {
        Results.Clear();
        Truncated = false;
        Failure = problem;
        IsSearching = false;
    }

    private static string DirectoryOf(string path) => path.LastIndexOf('/') is > 0 and var i ? path[..i] : "";
}
