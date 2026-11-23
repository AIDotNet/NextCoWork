using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Transcript;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using TKCode.Arc;

namespace Ncw.UI.Tests;

public class GeneratedVideoPreviewTests
{
    private const string Url = "ncw://attachments/sessions/session/movie.mp4";

    [AvaloniaFact]
    public async Task Play_stages_only_on_request_and_releases_the_file_when_the_row_changes()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var requests = 0;
        var model = Model(ToolRowStatus.Running);
        var preview = new NcwVideoPreview(() => true, async (row, destination, ct) =>
        {
            Assert.Equal(Url, row.Row.Media?.Url);
            requests++;
            await destination.WriteAsync(bytes, ct);
            return true;
        }) { Row = model };
        var window = new Window { Content = preview, Width = 320, Height = 450 };
        window.Show();
        Assert.False(preview.IsVisible);
        model.Update(Tool(ToolRowStatus.Done));
        window.UpdateLayout();
        Assert.True(preview.IsVisible);
        var player = Assert.Single(Assert.IsType<StackPanel>(preview.Content).Children.OfType<VideoPlayer>());
        Assert.Null(player.Source);
        Assert.Equal(0, requests);

        await preview.PlayAsync();
        Assert.Equal(1, requests);
        var source = Assert.IsType<UriVideoSource>(player.Source);
        Assert.True(source.Uri.IsFile);
        Assert.Equal(".mp4", Path.GetExtension(source.Uri.LocalPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source.Uri.LocalPath));
        window.UpdateLayout();
        Assert.InRange(player.Bounds.Width, 1, 320);
        Assert.InRange(player.Bounds.Height, 1, 260);

        model.Update(Tool(ToolRowStatus.Done) with { Media = new GeneratedMedia(Url.Replace("movie", "replacement", StringComparison.Ordinal), "video/mp4") });
        Assert.Null(player.Source);
        Assert.False(File.Exists(source.Uri.LocalPath));
        Assert.Equal(1, requests);
    }

    [AvaloniaFact]
    public async Task Detaching_during_download_cancels_it_and_deletes_the_partial_file()
    {
        string? path = null;
        var preview = new NcwVideoPreview(() => true, async (_, destination, ct) =>
        {
            path = Assert.IsType<FileStream>(destination).Name;
            await destination.WriteAsync(new byte[] { 1, 2, 3 }, ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return true;
        }) { Row = Model(ToolRowStatus.Done) };
        var window = new Window { Content = preview };
        window.Show();
        var player = Assert.Single(preview.GetVisualDescendants().OfType<VideoPlayer>());
        var load = preview.PlayAsync();
        Assert.NotNull(path);
        Assert.True(File.Exists(path));

        window.Content = null;
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(player.Source);
        Assert.False(File.Exists(path));
    }

    [AvaloniaFact]
    public async Task A_missing_native_backend_does_not_download_or_block_save()
    {
        var model = Model(ToolRowStatus.Done);
        var preview = new NcwVideoPreview(() => false, (_, _, _) => throw new InvalidOperationException("Must not download")) { Row = model };
        var window = new Window { Content = preview };
        window.Show();

        await preview.PlayAsync();
        Assert.Null(Assert.Single(preview.GetVisualDescendants().OfType<VideoPlayer>()).Source);
        Assert.True(model.CanSaveMedia);
    }

    private static ToolRowViewModel Model(ToolRowStatus status) => new(Tool(status), id => id, deviceId: "device",
        attachments: new AttachmentActions(new DeviceRegistry()));

    private static ToolRow Tool(ToolRowStatus status) => new("call:video", "video", "generate_video", ToolKind.Other, "", status,
        null, "", 0, 0, 0, false, null, GeneratedMediaKind.Video, status == ToolRowStatus.Done ? new GeneratedMedia(Url, "video/mp4") : null);
}
