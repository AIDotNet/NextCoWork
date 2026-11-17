using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

public enum FilePreviewKind
{
    Loading,
    Text,
    Image,

    /// <summary>二进制(或超过上限 / 不是 UTF-8):只提示,可以「另存为」。</summary>
    Binary,
    Failed,
}

/// <summary>文件扩展名 → 代码高亮的语言名(Arc 的 CodeBlock 接受围栏风格的名字)。未知的返回 null(纯文本)。纯函数。</summary>
public static class FileLanguages
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp", [".csx"] = "csharp", [".axaml"] = "xml", [".xaml"] = "xml", [".xml"] = "xml", [".csproj"] = "xml", [".props"] = "xml", [".targets"] = "xml",
        [".slnx"] = "xml", [".xlf"] = "xml", [".svg"] = "xml", [".html"] = "html", [".htm"] = "html", [".css"] = "css", [".scss"] = "scss",
        [".js"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript", [".jsx"] = "jsx", [".ts"] = "typescript", [".tsx"] = "tsx",
        [".json"] = "json", [".jsonc"] = "json", [".yml"] = "yaml", [".yaml"] = "yaml", [".toml"] = "toml", [".md"] = "markdown", [".markdown"] = "markdown",
        [".py"] = "python", [".go"] = "go", [".rs"] = "rust", [".java"] = "java", [".kt"] = "kotlin", [".swift"] = "swift", [".rb"] = "ruby", [".php"] = "php",
        [".c"] = "c", [".h"] = "c", [".cpp"] = "cpp", [".cc"] = "cpp", [".hpp"] = "cpp", [".sh"] = "bash", [".bash"] = "bash", [".zsh"] = "bash", [".ps1"] = "powershell",
        [".sql"] = "sql", [".dockerfile"] = "dockerfile", [".lua"] = "lua", [".gradle"] = "groovy",
    };

    public static string? Of(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (string.Equals(name, "Dockerfile", StringComparison.OrdinalIgnoreCase))
        {
            return "dockerfile";
        }

        var dot = name.LastIndexOf('.');
        return dot >= 0 && ByExtension.TryGetValue(name[dot..], out var language) ? language : null;
    }
}

/// <summary>
/// 一个打开的文件(预览 + 简易编辑)。内容、修订号、编辑中的文字都是这个面板自己的纯 UI 状态(设备是真源,不进 Store)。
/// <list type="bullet">
/// <item>小文件(≤ 2 MiB 的 UTF-8 文本)一次读完(<c>files.read</c>),可以编辑;保存带读到的 <c>Revision</c>,对不上是 <see cref="HasConflict"/>(文件在这之后被改过),用户选「重新载入」或「保留我的」。</item>
/// <item>超过上限的文本按 <c>files.readRange</c> 分块读(每块 256 KiB,带上一块的修订号;UTF-8 字符被切在两块之间由流式解码器接上),只读。显示按行截断,「显示更多」先放出已读的行,读完再读下一块。</item>
/// <item>二进制 / 不是 UTF-8 / 过大的图片:只提示,不尝试显示;可以「另存为」(按块读完整个文件)。图片(≤ 16 MiB)显示出来。</item>
/// </list>
/// </summary>
public sealed partial class FilePreviewViewModel : ObservableObject
{
    public const int ChunkBytes = 256 * 1024;
    public const int LinesPerPage = 4000;

    private readonly IFilesCommands _files;
    private readonly ProjectRef _project;
    private readonly bool _canWrite;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _text = new();
    private long _nextOffset;
    private string? _chunkRevision;
    private int _lineLimit = LinesPerPage;
    private int _totalLines;
    private readonly Func<ILocalFiles>? _local;
    private readonly string? _localRoot;

    /// <param name="local">客户端本机的文件动作(另存为 / 显示 / 打开 / 复制路径);没有就不开放这些动作。</param>
    /// <param name="localRoot">项目在本机上的根目录。只有「本机设备 + 本机环境」的项目才给,「显示 / 用默认程序打开」据此开放。</param>
    public FilePreviewViewModel(IFilesCommands files, ProjectRef project, string path, bool canWrite, int? line = null, Func<ILocalFiles>? local = null, string? localRoot = null)
    {
        _files = files;
        _project = project;
        _canWrite = canWrite;
        _local = local;
        _localRoot = string.IsNullOrEmpty(localRoot) ? null : localRoot;
        Path = path;
        Line = line;
        Language = FileLanguages.Of(path);
    }

    public ProjectRef Project => _project;

    /// <summary>项目在本机设备的本机环境里:「在文件管理器中显示 / 用默认程序打开」才有意义。</summary>
    public bool CanOpenLocally => _local is not null && _localRoot is not null;

    /// <summary>Markdown 文件:默认显示渲染后的样子,可以切到源码(编辑时一定是源码)。</summary>
    public bool IsMarkdown => Language == "markdown";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRendered), nameof(ShowsSource))]
    public partial bool ShowRendered { get; set; } = true;

    /// <summary>显示渲染后的 Markdown(只读文本、Markdown 文件、选了「预览」、不在编辑)。</summary>
    public bool ShowsRendered => IsText && IsMarkdown && ShowRendered && !IsEditing;

    /// <summary>显示源码(代码块着色)。</summary>
    public bool ShowsSource => IsText && !IsEditing && !(IsMarkdown && ShowRendered);

    /// <summary>已读到的内容共多少行(大文件分块读时,是目前读到的行数)。</summary>
    [ObservableProperty] public partial int TotalLines { get; private set; } = 1;

    [RelayCommand]
    private void ToggleRendered() => ShowRendered = !ShowRendered;

    public string Path { get; }

    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    public string Directory => Path.LastIndexOf('/') is > 0 and var i ? Path[..i] : "";

    /// <summary>从内容搜索点进来时,命中的行(从 1 起)。</summary>
    public int? Line { get; }

    public string? Language { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsText), nameof(IsImage), nameof(IsBinary), nameof(IsFailed), nameof(CanEdit), nameof(ShowsRendered), nameof(ShowsSource))]
    public partial FilePreviewKind Kind { get; private set; } = FilePreviewKind.Loading;

    public bool IsLoading => Kind == FilePreviewKind.Loading;

    public bool IsText => Kind == FilePreviewKind.Text;

    public bool IsImage => Kind == FilePreviewKind.Image;

    public bool IsBinary => Kind == FilePreviewKind.Binary;

    public bool IsFailed => Kind == FilePreviewKind.Failed;

    [ObservableProperty] public partial long Size { get; private set; }

    /// <summary>读到的修订号(保存时带回,乐观锁)。</summary>
    [ObservableProperty] public partial string Revision { get; private set; } = "";

    /// <summary>二进制时的原因:<c>unsupported</c> / <c>tooLarge</c> / <c>encoding</c> / <c>binary</c>。</summary>
    [ObservableProperty] public partial string? Reason { get; private set; }

    [ObservableProperty] public partial byte[]? ImageData { get; private set; }

    [ObservableProperty] public partial Problem? Failure { get; private set; }

    /// <summary>按块读的大文件:只读,还没读完时有「显示更多」。</summary>
    [ObservableProperty] public partial bool IsChunked { get; private set; }

    [ObservableProperty] public partial bool IsReadingMore { get; private set; }

    /// <summary>当前显示的文字(按行截断,见 <see cref="LinesPerPage"/>)。</summary>
    [ObservableProperty] public partial string DisplayText { get; private set; } = "";

    [ObservableProperty] public partial bool CanLoadMore { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave), nameof(ShowsRendered), nameof(ShowsSource))]
    public partial bool IsEditing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave))]
    public partial string EditText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool IsSaving { get; private set; }

    /// <summary>保存时发现文件在这之后被改过(<c>files.conflict</c>)。</summary>
    [ObservableProperty] public partial bool HasConflict { get; private set; }

    /// <summary>刚保存成功(界面显示一下「已保存」)。</summary>
    [ObservableProperty] public partial bool JustSaved { get; private set; }

    public bool CanEdit => Kind == FilePreviewKind.Text && !IsChunked && _canWrite;

    public bool IsDirty => IsEditing && !string.Equals(EditText, Original, StringComparison.Ordinal);

    public bool CanSave => IsEditing && IsDirty && !IsSaving;

    private string _original = "";

    private string Original => _original;

    partial void OnIsChunkedChanged(bool value) => OnPropertyChanged(nameof(CanEdit));

    // ───────────────────────── 读 ─────────────────────────

    public async Task LoadAsync(CancellationToken ct = default)
    {
        Kind = FilePreviewKind.Loading;
        Failure = null;
        var result = await _files.ReadAsync(_project.DeviceId, _project.WorkspaceId, Path, ct).ConfigureAwait(true);
        if (!result.IsOk || result.Value is not { } file)
        {
            Failure = Problem.Of(result);
            Kind = FilePreviewKind.Failed;
            return;
        }

        Size = file.Size;
        Revision = file.Revision;
        switch (file.Kind)
        {
            case "text":
                SetText(file.Content ?? "");
                Kind = FilePreviewKind.Text;
                break;
            case "image" when file.Data is { Length: > 0 } data:
                ImageData = data;
                Kind = FilePreviewKind.Image;
                break;
            case "image":
                await LoadChunkedImageAsync(file, ct).ConfigureAwait(true);
                break;
            default:
                Reason = file.Reason;
                if (file.Reason == "tooLarge")
                {
                    // 太大不是二进制:先读第一块,看是不是文本
                    await BeginChunkedAsync(ct).ConfigureAwait(true);
                }
                else
                {
                    Kind = FilePreviewKind.Binary;
                }

                break;
        }
    }

    private async Task BeginChunkedAsync(CancellationToken ct)
    {
        var first = await _files.ReadRangeAsync(_project.DeviceId, _project.WorkspaceId, Path, 0, ChunkBytes, null, ct).ConfigureAwait(true);
        if (!first.IsOk || first.Value is not { } range)
        {
            Failure = Problem.Of(first);
            Kind = FilePreviewKind.Failed;
            return;
        }

        if (Array.IndexOf(range.Data, (byte)0) >= 0)
        {
            Reason = "binary";
            Kind = FilePreviewKind.Binary;
            return;
        }

        IsChunked = true;
        _chunkRevision = range.Revision;
        Size = range.Size;
        _nextOffset = range.Offset + range.Length;
        AppendDecoded(range.Data, range.Length);
        ChunkEof = range.Eof;
        _lineLimit = LinesPerPage;
        Kind = FilePreviewKind.Text;
        RefreshDisplay();
    }

    private bool ChunkEof { get; set; } = true;

    private async Task LoadChunkedImageAsync(FileContentDto file, CancellationToken ct)
    {
        if (file.Size > FilesLimits.ImageMax)
        {
            Reason = "tooLarge";
            Kind = FilePreviewKind.Binary;
            return;
        }

        var bytes = await ReadAllBytesAsync(ct).ConfigureAwait(true);
        if (bytes is null)
        {
            Kind = FilePreviewKind.Failed;
            return;
        }

        ImageData = bytes;
        Kind = FilePreviewKind.Image;
    }

    /// <summary>「显示更多」:先放出已经读到的行;都显示了再读下一块。</summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsReadingMore)
        {
            return;
        }

        if (_lineLimit < _totalLines)
        {
            _lineLimit += LinesPerPage;
            RefreshDisplay();
            return;
        }

        if (!IsChunked || ChunkEof)
        {
            return;
        }

        IsReadingMore = true;
        try
        {
            var next = await _files.ReadRangeAsync(_project.DeviceId, _project.WorkspaceId, Path, _nextOffset, ChunkBytes, _chunkRevision).ConfigureAwait(true);
            if (!next.IsOk || next.Value is not { } range)
            {
                // 文件在分块读的中途变了(conflict)或断线:停在已读到的地方,提示原因
                Failure = Problem.Of(next);
                return;
            }

            Failure = null;
            _nextOffset = range.Offset + range.Length;
            AppendDecoded(range.Data, range.Length);
            ChunkEof = range.Eof;
            _lineLimit += LinesPerPage;
            RefreshDisplay();
        }
        finally
        {
            IsReadingMore = false;
        }
    }

    private void AppendDecoded(byte[] data, int length)
    {
        // 流式解码:一个多字节字符被切在两块之间时,前半留在解码器里,和下一块的开头拼成一个字符
        var chars = new char[_decoder.GetCharCount(data, 0, length, flush: false)];
        var count = _decoder.GetChars(data, 0, length, chars, 0, flush: false);
        _text.Append(chars, 0, count);
    }

    private void SetText(string text)
    {
        _original = text;
        _text.Clear();
        _text.Append(text);
        _lineLimit = LinesPerPage;
        ChunkEof = true;
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        var all = _text.ToString();
        var total = 1;
        var cut = all.Length;
        for (var i = 0; i < all.Length; i++)
        {
            if (all[i] == '\n')
            {
                total++;
                if (total == _lineLimit + 1 && cut == all.Length)
                {
                    cut = i;
                }
            }
        }

        _totalLines = total;
        TotalLines = total;
        DisplayText = cut < all.Length ? all[..cut] : all;
        CanLoadMore = _lineLimit < total || (IsChunked && !ChunkEof);
    }

    // ───────────────────────── 编辑 ─────────────────────────

    [RelayCommand]
    private void Edit()
    {
        if (!CanEdit)
        {
            return;
        }

        EditText = _original;
        HasConflict = false;
        JustSaved = false;
        Failure = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        HasConflict = false;
        Failure = null;
    }

    /// <summary>保存。成功后正文就是编辑的内容、修订号换成新的;<c>files.conflict</c> 不丢编辑内容,界面问用户怎么办。</summary>
    [RelayCommand]
    private Task SaveAsync() => SaveCoreAsync(Revision);

    private async Task SaveCoreAsync(string revision)
    {
        if (!IsEditing || IsSaving)
        {
            return;
        }

        IsSaving = true;
        Failure = null;
        try
        {
            var result = await _files.WriteTextAsync(_project.DeviceId, _project.WorkspaceId, Path, EditText, revision).ConfigureAwait(true);
            if (result.IsOk && result.Value is { } written)
            {
                SetText(EditText);
                Revision = written.Revision;
                Size = written.Size;
                HasConflict = false;
                IsEditing = false;
                JustSaved = true;
                return;
            }

            if (result.Outcome == ActionOutcome.Conflict && result.ErrorKey == FilesErrorKeys.Conflict)
            {
                HasConflict = true;
                return;
            }

            Failure = Problem.Of(result);
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>冲突后:放弃我的修改,重新载入磁盘上的新内容。</summary>
    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsEditing = false;
        HasConflict = false;
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>冲突后:保留我的修改。先读一次拿到磁盘上现在的修订号,再用它覆盖(用户明确选了盖掉外部的修改)。</summary>
    [RelayCommand]
    private async Task KeepMineAsync()
    {
        var current = await _files.ReadAsync(_project.DeviceId, _project.WorkspaceId, Path).ConfigureAwait(true);
        if (!current.IsOk || current.Value is not { } file)
        {
            Failure = Problem.Of(current);
            return;
        }

        await SaveCoreAsync(file.Revision).ConfigureAwait(true);
    }

    // ───────────────────────── 另存为 ─────────────────────────

    /// <summary>整个文件的字节(另存为用):文本 / 图片直接用已读到的,其余按块读完(上限 256 MiB)。</summary>
    public async Task<byte[]?> ReadAllBytesAsync(CancellationToken ct = default)
    {
        if (Kind == FilePreviewKind.Image && ImageData is { } image)
        {
            return image;
        }

        if (Kind == FilePreviewKind.Text && !IsChunked)
        {
            return new UTF8Encoding(false).GetBytes(_original);
        }

        const long cap = 256L * 1024 * 1024;
        using var buffer = new MemoryStream();
        string? revision = null;
        long offset = 0;
        while (true)
        {
            var part = await _files.ReadRangeAsync(_project.DeviceId, _project.WorkspaceId, Path, offset, FilesLimits.RangeMax, revision, ct).ConfigureAwait(true);
            if (!part.IsOk || part.Value is not { } range)
            {
                Failure = Problem.Of(part);
                return null;
            }

            buffer.Write(range.Data, 0, range.Length);
            offset = range.Offset + range.Length;
            revision = range.Revision;
            if (range.Eof || range.Length == 0)
            {
                return buffer.ToArray();
            }

            if (offset > cap)
            {
                Reason = "tooLarge";
                return null;
            }
        }
    }

    // ───────────────────────── 本机动作 ─────────────────────────

    /// <summary>项目相对路径 → 本机绝对路径(只对本机项目有意义)。</summary>
    public string AbsolutePath => System.IO.Path.GetFullPath(System.IO.Path.Combine(_localRoot ?? "", Path.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    [RelayCommand]
    private Task CopyPathAsync() => _local?.Invoke().CopyTextAsync(_localRoot is null ? Path : AbsolutePath) ?? Task.CompletedTask;

    /// <summary>复制已加载的文本(编辑中复制编辑框里的内容)。</summary>
    [RelayCommand]
    private Task CopyContentAsync() => _local?.Invoke().CopyTextAsync(IsEditing ? EditText : DisplayText) ?? Task.CompletedTask;

    [RelayCommand]
    private Task CopyRelativePathAsync() => _local?.Invoke().CopyTextAsync(Path) ?? Task.CompletedTask;

    [RelayCommand]
    private async Task RevealAsync()
    {
        if (CanOpenLocally)
        {
            await _local!().RevealAsync(AbsolutePath).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task OpenExternallyAsync()
    {
        if (CanOpenLocally)
        {
            await _local!().OpenAsync(AbsolutePath).ConfigureAwait(true);
        }
    }

    /// <summary>另存为:内容从设备读来(文本 / 图片 / 二进制按块读完),存到用户选的本机位置。</summary>
    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (_local is null)
        {
            return;
        }

        var bytes = await ReadAllBytesAsync().ConfigureAwait(true);
        if (bytes is not null)
        {
            await _local().SaveAsync(Name, bytes).ConfigureAwait(true);
        }
    }
}
