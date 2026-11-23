using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Ncw.Client.Transcript;
using Ncw.Client.ViewModels;
using Ncw.UI.Localization;
using TKCode.Arc;

namespace Ncw.UI.Controls;

/// <summary>生成视频的 Arc 播放器;点击才认证下载,播放器只接触本地暂存文件。</summary>
public sealed class NcwVideoPreview : UserControl
{
    public static readonly StyledProperty<ToolRowViewModel?> RowProperty =
        AvaloniaProperty.Register<NcwVideoPreview, ToolRowViewModel?>(nameof(Row));

    private static readonly Lazy<bool> DesktopVideo = new(() =>
        (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) && ArcVideoNative.Use());

    private readonly Func<bool> _ensureBackend;
    private readonly Func<ToolRowViewModel, Stream, CancellationToken, Task<bool>> _download;
    private readonly NcwButton _play = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
    private readonly VideoPlayer _player = new()
    {
        ShowControls = VideoControlsVisibility.Always,
        AspectRatio = 16d / 9d,
        MaxWidth = 440,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        IsVisible = false,
    };

    private ToolRowViewModel? _observed;
    private GeneratedMedia? _media;
    private CancellationTokenSource? _load;
    private DirectoryInfo? _staged;

    public NcwVideoPreview() : this(() => DesktopVideo.Value,
        static (row, destination, ct) => row.SaveMediaAsync(destination, ct)) { }

    internal NcwVideoPreview(Func<bool> ensureBackend,
        Func<ToolRowViewModel, Stream, CancellationToken, Task<bool>> download)
    {
        _ensureBackend = ensureBackend;
        _download = download;
        IsVisible = false;
        HorizontalAlignment = HorizontalAlignment.Left;
        _status.Classes.Add("hint");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Content = new StackPanel { Spacing = 4, Children = { _play, _status, _player } };
        _play.Click += OnPlay;
    }

    public ToolRowViewModel? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowProperty && this.IsAttachedToVisualTree()) Observe(Row);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _play.Content = NcwTexts.Get(NcwTextKind.ToolMediaPlay);
        Observe(Row);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Observe(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void Observe(ToolRowViewModel? row)
    {
        if (ReferenceEquals(_observed, row)) return;
        if (_observed is not null) _observed.PropertyChanged -= OnRowChanged;
        ResetPlayback();
        _media = null;
        _observed = row;
        if (row is not null) row.PropertyChanged += OnRowChanged;
        Refresh();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)) Refresh();
    }

    private void Refresh()
    {
        var media = _observed is { IsDone: true, CanSaveMedia: true, Row.MediaKind: GeneratedMediaKind.Video }
            ? _observed.Row.Media : null;
        if (media != _media)
        {
            ResetPlayback();
            _media = media;
        }
        IsVisible = media is not null;
    }

    private void ResetPlayback()
    {
        _load?.Cancel();
        _load = null;
        _player.IsFullScreen = false;
        _player.Pause();
        _player.Source = null; // Arc 同步释放播放器和原生文件句柄,再删除暂存文件。
        _player.IsVisible = false;
        DeleteStaging(_staged);
        _staged = null;
        _play.IsVisible = true;
        _play.IsEnabled = true;
        _status.IsVisible = false;
    }

    private void OnPlay(object? sender, RoutedEventArgs e) => _ = PlayAsync();

    internal async Task PlayAsync()
    {
        if (_observed is not { MediaFileExtension: { } extension } row || _media is null || _load is not null || _staged is not null)
        {
            return;
        }
        if (!_ensureBackend())
        {
            _play.IsEnabled = false;
            ShowStatus(NcwTextKind.ToolMediaVideoUnavailable);
            return;
        }

        using var load = new CancellationTokenSource();
        _load = load;
        _play.IsEnabled = false;
        ShowStatus(NcwTextKind.ToolMediaVideoLoading);
        DirectoryInfo? staging = null;
        try
        {
            // CreateTempSubdirectory 原子创建随机私有目录(Unix 0700),扩展名来自 MIME 白名单。
            staging = Directory.CreateTempSubdirectory("ncw-video-");
            var path = Path.Combine(staging.FullName, "clip" + extension);
            await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, bufferSize: 4096, FileOptions.Asynchronous))
            {
                if (!await _download(row, destination, load.Token))
                {
                    if (ReferenceEquals(_load, load)) ShowStatus(NcwTextKind.ToolMediaVideoFailed);
                    return;
                }
            }
            if (!ReferenceEquals(_load, load)) return;

            _player.IsVisible = true;
            _player.Source = VideoSource.FromFile(path);
            // Arc 的 AutoPlay 只适用于静音素材;这里的播放请求来自用户点击。
            if (_player.State != VideoPlaybackState.Failed) _player.Play();
            _staged = staging;
            staging = null;
            _status.IsVisible = false;
            _play.IsVisible = false;
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            // 行已被更换或回收。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            if (ReferenceEquals(_load, load))
            {
                _player.Pause();
                _player.Source = null;
                _player.IsVisible = false;
                ShowStatus(NcwTextKind.ToolMediaVideoFailed);
            }
        }
        finally
        {
            DeleteStaging(staging);
            if (ReferenceEquals(_load, load))
            {
                _load = null;
                _play.IsEnabled = true;
            }
        }
    }

    private void ShowStatus(NcwTextKind text)
    {
        _status.Text = NcwTexts.Get(text);
        _status.IsVisible = true;
    }

    private static void DeleteStaging(DirectoryInfo? directory)
    {
        try
        {
            directory?.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 操作系统或另一个进程可能还占用文件;清理失败不应阻止回收转录行。
        }
    }
}
