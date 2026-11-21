using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;
using Ncw.UI.Chrome;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Conversation;

namespace Ncw.UI.Tests;

/// <summary>输入框粘贴图片与文件(文件粘贴路径、图片进附件托盘)。</summary>
public class ComposerPasteTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 18, 30, 0, TimeSpan.FromHours(8));

    private sealed class FakeUploader : IAttachmentUploader
    {
        public List<(string Name, string Mime, int Bytes)> Uploaded { get; } = [];

        public Task<ActionResult<AttachmentDto>> UploadAsync(string deviceId, string sessionId, string displayName, string mime, byte[] bytes, IProgress<long>? progress, CancellationToken ct = default)
        {
            Uploaded.Add((displayName, mime, bytes.Length));
            var dto = new AttachmentDto("att-" + Uploaded.Count, "session", sessionId, displayName, mime, bytes.Length, null, 0, $"ncw://attachments/sessions/{sessionId}/att-{Uploaded.Count}.png");
            return Task.FromResult(new ActionResult<AttachmentDto>(ActionOutcome.Ok, dto));
        }

        public Task<ActionResult<Unit>> RemoveAsync(string deviceId, string attachmentId, CancellationToken ct = default) =>
            Task.FromResult(new ActionResult<Unit>(ActionOutcome.Ok, Unit.Value));
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private static (Window Window, ComposerView Composer, ComposerViewModel Vm, FakeUploader Uploader, Ncw.Client.Stores.ClientStores Stores) Render()
    {
        var stores = ConversationFixtures.Base("s-ncw-2");
        ConversationFixtures.Session(stores, "s-ncw-2", []);
        var uploader = new FakeUploader();
        var vm = new ShellViewModel(stores, new FakeClock(Now), attachmentUploader: uploader);
        var shell = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = 1280, Height = 820, Content = shell };
        window.Show();
        Dispatch();

        var composer = window.GetVisualDescendants().OfType<ComposerView>().First(c => c.IsEffectivelyVisible);
        composer.FocusInput();
        Dispatch();
        return (window, composer, vm.Conversation.Composer, uploader, stores);
    }

    private static void Dispatch()
    {
        for (var i = 0; i < 3; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static WriteableBitmap MakeBitmap(int width = 8, int height = 8)
    {
        var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bmp.Lock();
        var pixels = new int[width * height];
        Array.Fill(pixels, unchecked((int)0xFF3366FF));
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        return bmp;
    }

    private static DataTransfer MakeTransfer(Action<DataTransferItem> configure)
    {
        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        configure(item);
        transfer.Add(item);
        return transfer;
    }

    [AvaloniaFact]
    public async Task Pasting_a_bitmap_via_keyboard_uploads_a_png_attachment_without_changing_draft()
    {
        var (window, composer, vm, uploader, _) = Render();
        var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;
        using var bitmap = MakeBitmap();
        await clipboard.SetDataAsync(MakeTransfer(item =>
        {
            item.SetBitmap(bitmap);
            item.SetText("ignored-url");
        }));

        window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
        await composer.WhenPasted;
        Dispatch();

        var item = Assert.Single(vm.Attachments.Items);
        Assert.True(item.IsImage);
        Assert.True(item.IsDone);
        Assert.Equal("image/png", item.Mime);
        Assert.Equal("", vm.Draft);
        Assert.True(vm.State.CanSend);
        Assert.Single(uploader.Uploaded);
    }

    [AvaloniaFact]
    public async Task Pasting_a_non_image_file_inserts_its_local_path_instead_of_filename_or_attachment()
    {
        var (window, composer, vm, uploader, _) = Render();
        var dir = Directory.CreateTempSubdirectory("ncw-paste-");
        try
        {
            var codePath = Path.Combine(dir.FullName, "example.cs");
            await File.WriteAllTextAsync(codePath, "class C {}");
            var storageFile = await window.StorageProvider.TryGetFileFromPathAsync(codePath);
            Assert.NotNull(storageFile);

            var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;
            await clipboard.SetDataAsync(MakeTransfer(item =>
            {
                item.SetFile(storageFile);
                item.SetText("example.cs"); // Finder 会顺带放一个纯文件名
            }));

            vm.Draft = "查看 ";
            composer.FocusInput();
            Dispatch();

            window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
            await composer.WhenPasted;
            Dispatch();

            Assert.Equal("查看 " + codePath, vm.Draft);
            Assert.Empty(vm.Attachments.Items);
            Assert.Empty(uploader.Uploaded);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Pasting_multiple_files_separates_their_paths_by_newline_and_attaches_image_files()
    {
        var (window, composer, vm, uploader, _) = Render();
        var dir = Directory.CreateTempSubdirectory("ncw-paste-multi-");
        try
        {
            var firstDoc = Path.Combine(dir.FullName, "first file.txt");
            var secondDoc = Path.Combine(dir.FullName, "second.md");
            var imagePath = Path.Combine(dir.FullName, "shot.png");
            await File.WriteAllTextAsync(firstDoc, "a");
            await File.WriteAllTextAsync(secondDoc, "b");
            using (var bmp = MakeBitmap())
            {
                bmp.Save(imagePath, new PngBitmapEncoderOptions());
            }

            var f1 = await window.StorageProvider.TryGetFileFromPathAsync(firstDoc);
            var f2 = await window.StorageProvider.TryGetFileFromPathAsync(secondDoc);
            var img = await window.StorageProvider.TryGetFileFromPathAsync(imagePath);
            Assert.NotNull(f1);
            Assert.NotNull(f2);
            Assert.NotNull(img);

            var transfer = new DataTransfer();
            var i1 = new DataTransferItem();
            i1.SetFile(f1);
            transfer.Add(i1);
            var i2 = new DataTransferItem();
            i2.SetFile(img);
            transfer.Add(i2);
            var i3 = new DataTransferItem();
            i3.SetFile(f2);
            transfer.Add(i3);

            var inserted = new List<string>();
            var handled = await ComposerClipboard.PasteAsync(transfer, vm, inserted.Add, new FakeClock(Now));
            Dispatch();

            Assert.True(handled);
            Assert.Equal([firstDoc + "\n" + secondDoc], inserted);
            var attachment = Assert.Single(vm.Attachments.Items);
            Assert.Equal("shot.png", attachment.Name);
            Assert.Equal("image/png", attachment.Mime);
            Assert.True(attachment.IsDone);
            Assert.Single(uploader.Uploaded);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Pasting_plain_text_falls_back_to_inserting_text_at_caret()
    {
        var (window, composer, vm, _, _) = Render();
        var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;
        await clipboard.SetTextAsync("hello");

        vm.Draft = "say ";
        composer.FocusInput();
        Dispatch();

        window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
        await composer.WhenPasted;
        Dispatch();

        Assert.Equal("say hello", vm.Draft);
        Assert.Empty(vm.Attachments.Items);
    }

    [AvaloniaFact]
    public async Task TextBox_paste_command_triggers_PastingFromClipboard_for_folders_and_images()
    {
        var (window, composer, vm, _, _) = Render();
        var dir = Directory.CreateTempSubdirectory("ncw-paste-folder-");
        try
        {
            var folder = await window.StorageProvider.TryGetFolderFromPathAsync(dir.FullName);
            Assert.NotNull(folder);

            var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;
            await clipboard.SetFileAsync(folder);

            var textBox = composer.GetVisualDescendants().OfType<TextBox>().First();
            textBox.Paste();
            await composer.WhenPasted;
            Dispatch();

            Assert.Equal(dir.FullName.TrimEnd(Path.DirectorySeparatorChar), vm.Draft.TrimEnd(Path.DirectorySeparatorChar));
            Assert.Empty(vm.Attachments.Items);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Dropping_files_and_images_via_DropAsync_attaches_images_and_inserts_paths()
    {
        var (window, _, vm, uploader, _) = Render();
        var dir = Directory.CreateTempSubdirectory("ncw-drop-");
        try
        {
            var notePath = Path.Combine(dir.FullName, "notes.txt");
            var imagePath = Path.Combine(dir.FullName, "diagram.png");
            await File.WriteAllTextAsync(notePath, "hello");
            using (var bmp = MakeBitmap())
            {
                bmp.Save(imagePath, new PngBitmapEncoderOptions());
            }

            var noteFile = await window.StorageProvider.TryGetFileFromPathAsync(notePath);
            var imgFile = await window.StorageProvider.TryGetFileFromPathAsync(imagePath);
            Assert.NotNull(noteFile);
            Assert.NotNull(imgFile);

            var transfer = new DataTransfer();
            var i1 = new DataTransferItem();
            i1.SetFile(noteFile);
            transfer.Add(i1);
            var i2 = new DataTransferItem();
            i2.SetFile(imgFile);
            transfer.Add(i2);

            var inserted = new List<string>();
            var handled = await ComposerClipboard.DropAsync(transfer, vm, inserted.Add, new FakeClock(Now));
            Dispatch();

            Assert.True(handled);
            Assert.Equal([notePath], inserted);
            var att = Assert.Single(vm.Attachments.Items);
            Assert.Equal("diagram.png", att.Name);
            Assert.True(att.IsDone);
            Assert.Single(uploader.Uploaded);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [AvaloniaFact]
    public void Desktop_attachment_picker_exposes_photo_library_and_composer_displays_plan_pill_and_context_meter()
    {
        var (window, composer, vm, _, stores) = Render();
        var picker = new Ncw.UI.Controls.NcwDesktopAttachmentPicker(() => window);
        Assert.True(picker.IsAvailable(AttachmentSource.PhotoLibrary));
        Assert.False(picker.IsAvailable(AttachmentSource.Camera));

        ConversationFixtures.Run(stores, "run-ctx", "s-ncw-2", r => r with
        {
            Context = new ContextUsageEventDto(120_000, 200_000, false, []),
        });
        var session = new Ncw.Client.Stores.SessionRef("local", "s-ncw-2");
        stores.Conversations.Update(c => c with
        {
            Sessions = c.Sessions.SetItem(session, c.Sessions[session] with { Meta = c.Sessions[session].Meta with { Mode = "plan" } }),
        });
        Dispatch();

        Assert.True(vm.HasContext);
        Assert.Equal(60, vm.ContextPercent);
        Assert.True(vm.IsPlanMode);
        var meter = composer.FindControl<Ncw.UI.Controls.PlainButton>("ContextMeter");
        Assert.NotNull(meter);
        Assert.True(meter.IsEffectivelyVisible);
    }
}
