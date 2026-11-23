using System.ComponentModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Ncw.Client.Services;
using Ncw.Client.Transcript;
using Ncw.Client.ViewModels;
using Ncw.UI.Localization;
using TKCode.Arc;

namespace Ncw.UI.Controls;

/// <summary>用工具调用和真实附件驱动 Arc 的 ImageReveal;加载和位图只活在可见的转录容器里。</summary>
public sealed class NcwImageReveal : ImageReveal
{
    public const double PreviewMaxWidth = 440;
    public const double PreviewMaxHeight = 460;
    public const double InitialAspectRatio = 1.5;

    public static readonly StyledProperty<ToolRowViewModel?> ToolProperty =
        AvaloniaProperty.Register<NcwImageReveal, ToolRowViewModel?>(nameof(Tool));

    private ToolRowViewModel? _observed;
    private CancellationTokenSource? _load;
    private Bitmap? _bitmap;
    private string? _url;

    public NcwImageReveal()
    {
        ShowsProgress = false; // 工具不报告步数,不显示 Arc 的估计步数
        AllowsRetry = false;
        AspectRatio = InitialAspectRatio;
        MaxWidth = PreviewMaxWidth;
        RetryRequested += (_, _) =>
        {
            ResetImage();
            Refresh();
        };
    }

    protected override Type StyleKeyOverride => typeof(ImageReveal);

    public ToolRowViewModel? Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToolProperty && this.IsAttachedToVisualTree())
        {
            Observe(Tool);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Labels = new ImageRevealLabels
        {
            Queued = NcwTexts.Get(NcwTextKind.MediaImageQueued),
            Generating = NcwTexts.Get(NcwTextKind.MediaImageGenerating),
            GeneratingImage = NcwTexts.Get(NcwTextKind.MediaImageGenerating),
            Done = NcwTexts.Get(NcwTextKind.MediaImageReady),
            ImageReady = NcwTexts.Get(NcwTextKind.MediaImageReady),
            Failed = NcwTexts.Get(NcwTextKind.MediaImageFailed),
            GeneratedImage = NcwTexts.Get(NcwTextKind.MediaImageGenerated),
            Retry = NcwTexts.Get(NcwTextKind.Retry),
            NoImage = NcwTexts.Get(NcwTextKind.MsgImageFailed),
        };
        Observe(Tool);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Observe(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void Observe(ToolRowViewModel? tool)
    {
        if (ReferenceEquals(_observed, tool)) return;

        if (_observed is not null) _observed.PropertyChanged -= OnToolChanged;
        ResetImage();
        _observed = tool;
        if (_observed is not null) _observed.PropertyChanged += OnToolChanged;
        Refresh();
    }

    private void OnToolChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)) Refresh();
    }

    private void ResetImage()
    {
        _load?.Cancel();
        _load = null;
        _url = null;
        Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        AspectRatio = InitialAspectRatio;
        MaxWidth = PreviewMaxWidth;
    }

    private void Refresh()
    {
        if (_observed is not { } tool)
        {
            Status = null;
            return;
        }

        var url = tool.Row.Media?.Url;
        if (url != _url) ResetImage();

        if (tool.Row.Status == ToolRowStatus.Done && url is not null)
        {
            if (_url == url) return; // 其他字段更新,不重复读取附件

            QueueLabel = NcwTexts.Get(NcwTextKind.MediaImageLoading);
            Status = ImageRevealStatus.Queued;
            AllowsRetry = false;
            _url = url;
            _load = new CancellationTokenSource();
            _ = LoadAsync(tool, url, _load);
            return;
        }

        AllowsRetry = false;
        QueueLabel = tool.IsWaiting ? NcwTexts.Get(NcwTextKind.ToolWaitingApproval) : null;
        ErrorMessage = tool.Row.Status switch
        {
            ToolRowStatus.Unknown => NcwTexts.Get(NcwTextKind.DeviceConnecting),
            ToolRowStatus.Interrupted => NcwTexts.Get(NcwTextKind.ToolInterrupted),
            ToolRowStatus.Done => NcwTexts.Get(NcwTextKind.MsgImageFailed),
            _ => null,
        };
        Status = tool.Row.Status switch
        {
            ToolRowStatus.Running when !tool.IsQueued => ImageRevealStatus.Generating,
            ToolRowStatus.Running or ToolRowStatus.WaitingApproval => ImageRevealStatus.Queued,
            _ => ImageRevealStatus.Error,
        };
    }

    private async Task LoadAsync(ToolRowViewModel tool, string url, CancellationTokenSource load)
    {
        Bitmap? image = null;
        try
        {
            var bytes = await tool.LoadMediaImageAsync(url, load.Token);
            if (bytes is { } data && data.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                image = await Task.Run(() => Decode(data.Data), load.Token);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 设备断开、附件被删除、格式不认识:都收成可重试的读取失败。
        }
        finally
        {
            load.Dispose();
        }

        if (!ReferenceEquals(_load, load))
        {
            image?.Dispose();
            return;
        }

        _load = null;
        if (image is null)
        {
            ErrorMessage = NcwTexts.Get(NcwTextKind.MsgImageFailed);
            AllowsRetry = true;
            Status = ImageRevealStatus.Error;
            return;
        }

        var ratio = image.Size.Width / image.Size.Height;
        AspectRatio = ratio;
        MaxWidth = Math.Min(PreviewMaxWidth, PreviewMaxHeight * ratio);
        _bitmap = image;
        Source = image;
        Status = ImageRevealStatus.Done;
    }

    internal static Bitmap? Decode(byte[] data, int limit = 1400)
    {
        // 尺寸预算在解码前检查,再把长边限到显示所需的大小。
        if (IconImages.ReadHeader(data.AsSpan(0, Math.Min(data.Length, IconImages.HeaderBytes))) is not { Width: > 0, Height: > 0 } header
            || (long)header.Width * header.Height > 50_000_000)
        {
            return null;
        }

        using var stream = new MemoryStream(data, writable: false);
        return Math.Max(header.Width, header.Height) <= limit ? new Bitmap(stream)
            : header.Width >= header.Height ? Bitmap.DecodeToWidth(stream, limit) : Bitmap.DecodeToHeight(stream, limit);
    }
}
