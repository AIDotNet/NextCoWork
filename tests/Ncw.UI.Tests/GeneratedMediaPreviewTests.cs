using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ncw.Client.Services;
using Ncw.Client.Transcript;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop.Conversation;
using TKCode.Arc;

namespace Ncw.UI.Tests;

public class GeneratedMediaPreviewTests
{
    private const string Url = "ncw://attachments/sessions/session/image.png";
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    [AvaloniaFact]
    public async Task The_preview_uses_real_attachment_bytes_and_fits_a_narrow_tool_row()
    {
        var images = new ControlledImages();
        var running = Tool(ToolRowStatus.Running);
        var model = new ToolRowViewModel(running, id => id, deviceId: "device", images: images);
        var tool = new ToolCard { DataContext = model };
        var window = new Window { Content = tool, Width = 320, Height = 550 };
        window.Show();
        var reveal = Assert.Single(tool.GetVisualDescendants().OfType<NcwImageReveal>());
        Assert.True(reveal.IsVisible);
        Assert.Equal(ImageRevealStatus.Generating, reveal.Status);
        Assert.Null(reveal.Source);
        Assert.Null(reveal.Simulation);
        Assert.False(reveal.ShowsProgress);
        Assert.Equal(0, images.LoadCount);

        model.Update(running with { Status = ToolRowStatus.Done, Media = new GeneratedMedia(Url, "image/png") });
        Assert.Equal(ImageRevealStatus.Queued, reveal.Status);
        Assert.Equal(("device", Url), images.Request);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reveal.StatusChanged += (_, e) =>
        {
            if (e.NewStatus == ImageRevealStatus.Done) done.TrySetResult();
        };
        images.Result.SetResult(new ImageBytes(Convert.FromBase64String(Png), "image/png"));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(reveal.Source);
        Assert.Equal(1, reveal.AspectRatio);
        Assert.InRange(reveal.Bounds.Width, 1, 320);
        Assert.False(tool.FindControl<Grid>("ToolHeader")!.IsVisible);
        Assert.True(tool.FindControl<PlainButton>("MediaViewButton")!.IsVisible);
        Assert.True(tool.FindControl<PlainButton>("MediaCopyButton")!.IsVisible);
        Assert.Equal(1, images.LoadCount);
    }

    [AvaloniaFact]
    public void A_failed_generation_has_no_retry_or_attachment_fetch()
    {
        var images = new ControlledImages();
        var model = new ToolRowViewModel(Tool(ToolRowStatus.Failed), id => id, deviceId: "device", images: images);
        var tool = new ToolCard { DataContext = model };
        var window = new Window { Content = tool, Width = 320, Height = 550 };
        window.Show();
        var reveal = Assert.Single(tool.GetVisualDescendants().OfType<NcwImageReveal>());
        Assert.Equal(ImageRevealStatus.Error, reveal.Status);
        Assert.False(reveal.AllowsRetry);
        Assert.Null(reveal.Source);
        Assert.Equal(0, images.LoadCount);
    }

    [AvaloniaFact]
    public async Task Clicking_the_finished_image_opens_a_narrow_dialog_with_copy_and_close()
    {
        var images = new ControlledImages();
        var model = new ToolRowViewModel(Tool(ToolRowStatus.Done) with { Media = new GeneratedMedia(Url, "image/png") }, id => id,
            deviceId: "device", images: images);
        var card = new ToolCard { DataContext = model };
        var window = new Window { Content = card, Width = 320, Height = 550 };
        window.Show();
        try
        {
            var reveal = Assert.Single(card.GetVisualDescendants().OfType<NcwImageReveal>());
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reveal.StatusChanged += (_, e) =>
            {
                if (e.NewStatus == ImageRevealStatus.Done) done.TrySetResult();
            };
            images.Result.SetResult(new ImageBytes(Convert.FromBase64String(Png), "image/png"));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var hit = Assert.Single(card.GetVisualDescendants().OfType<PlainButton>(), b => b.Name == "MediaImageHit");
            Assert.True(hit.IsVisible);
            hit.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            DialogSurface? surface;
            while ((surface = window.GetVisualDescendants().OfType<DialogSurface>().FirstOrDefault()) is null)
            {
                await Task.Delay(10, timeout.Token);
            }

            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.InRange(surface.Bounds.Width, 1, 320);
            Assert.Contains(surface.GetVisualDescendants().OfType<NcwButton>(), b => b.Name == "CopyGeneratedImage");
            Assert.NotNull(Assert.Single(surface.GetVisualDescendants().OfType<Image>(), image => image.Source is not null).Source);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Copy_places_image_pixels_on_the_system_clipboard()
    {
        var window = new Window();
        window.Show();
        Assert.True(await ToolCard.CopyBitmapAsync(window, Convert.FromBase64String(Png)));
        using var bitmap = await window.Clipboard!.TryGetBitmapAsync();
        Assert.NotNull(bitmap);
        Assert.Equal(1, bitmap.PixelSize.Width);
        Assert.Equal(1, bitmap.PixelSize.Height);
    }

    [AvaloniaFact]
    public void Video_and_audio_statuses_hide_the_generic_tool_header()
    {
        foreach (var kind in new[] { GeneratedMediaKind.Video, GeneratedMediaKind.Audio })
        {
            var model = new ToolRowViewModel(Tool(ToolRowStatus.Running) with { MediaKind = kind }, id => id);
            var card = new ToolCard { DataContext = model };
            var window = new Window { Content = card };
            window.Show();
            Assert.False(card.FindControl<Grid>("ToolHeader")!.IsVisible);
            Assert.True(model.ShowsNonImageMediaStatus);
            window.Close();
        }
    }

    private static ToolRow Tool(ToolRowStatus status) => new("call:test", "test", "generate_image", ToolKind.Other, "", status,
        null, "", 0, 0, 0, false, null, GeneratedMediaKind.Image);

    private sealed class ControlledImages : IImageSource
    {
        public TaskCompletionSource<ImageBytes?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public (string Device, string Url) Request { get; private set; }

        public int LoadCount { get; private set; }

        public Task<ImageBytes?> LoadAsync(string deviceId, string dataRef, CancellationToken ct = default)
        {
            LoadCount++;
            Request = (deviceId, dataRef);
            return Result.Task.WaitAsync(ct);
        }
    }
}
