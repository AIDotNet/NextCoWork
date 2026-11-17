using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;
using Ncw.UI.Converters;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Tree;
using Ncw.UI.Views.Dialogs;
using TKCode.Arc;

namespace Ncw.UI.Tests;

/// <summary>
/// 项目图标图片的真实解码 / 压缩(Skia)、显示用的转换器、「新建项目」与「项目图标」两个窗口、项目栏上的图片图标。
/// 截图在 <c>screenshots/new-project-*.png</c>、<c>project-icon-*.png</c>、<c>rail-image-icon.png</c>。
/// </summary>
public class ProjectIconAndNewProjectUiTests
{
    private static void Dispatch()
    {
        for (var i = 0; i < 6; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Snapshot(Window window, string name)
    {
        Dispatch();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"), new PngBitmapEncoderOptions());
    }

    /// <summary>画一张 <paramref name="width"/>×<paramref name="height"/> 的图:每个像素的颜色由 <paramref name="color"/> 决定(BGRA)。</summary>
    private static byte[] Encode(int width, int height, Func<int, int, uint> color, BitmapEncoderOptions options)
    {
        var pixels = new uint[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = color(x, y);
            }
        }

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, handle.AddrOfPinnedObject(), new PixelSize(width, height), new Vector(96, 96), width * 4);
            using var stream = new MemoryStream();
            bitmap.Save(stream, options);
            return stream.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    private const uint Red = 0xFFFF0000, Green = 0xFF00FF00, Blue = 0xFF0000FF;

    /// <summary>解码压缩结果,取 (x, y) 处的 BGRA。</summary>
    private static (Bitmap Bitmap, Func<int, int, uint> At) Decode(string base64)
    {
        var bitmap = new Bitmap(new MemoryStream(Convert.FromBase64String(base64)));
        var size = bitmap.PixelSize;
        var buffer = new uint[size.Width * size.Height];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), buffer.Length * 4, size.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        // 解码出来的通道顺序随平台(BGRA / RGBA):统一成 BGRA 再比
        var rgba = bitmap.Format == PixelFormat.Rgba8888;
        return (bitmap, (x, y) =>
        {
            var p = buffer[(y * size.Width) + x];
            return rgba ? (p & 0xFF00FF00) | ((p & 0xFF) << 16) | ((p >> 16) & 0xFF) : p;
        });
    }

    /// <summary>JPEG 会有轻微色偏:只看主通道。</summary>
    private static string Dominant(uint bgra)
    {
        var r = (bgra >> 16) & 0xFF;
        var g = (bgra >> 8) & 0xFF;
        var b = bgra & 0xFF;
        return r > 180 && g < 90 && b < 90 ? "red" : g > 180 && r < 90 && b < 90 ? "green" : b > 180 && r < 90 && g < 90 ? "blue" : $"#{bgra:X8}";
    }

    private static Task<PickedIcon> Compress(byte[] bytes) => NcwIconImages.CompressAsync(() => Task.FromResult<Stream>(new MemoryStream(bytes)), bytes.Length);

    [AvaloniaFact]
    public async Task Opaque_images_are_center_cropped_to_a_small_jpeg()
    {
        // 横图:左红 | 中绿 | 右蓝,居中裁成正方形后只剩绿色
        var source = Encode(1200, 600, (x, _) => x < 300 ? Red : x < 900 ? Green : Blue, PngBitmapEncoderOptions.Default);
        var picked = await Compress(source);

        Assert.Null(picked.Error);
        var bytes = Convert.FromBase64String(picked.Image!);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]); // JPEG
        Assert.True(bytes.Length < 8 * 1024, $"icon is {bytes.Length} bytes");
        var (bitmap, at) = Decode(picked.Image!);
        Assert.Equal(new PixelSize(ProjectIconSpec.PixelSize, ProjectIconSpec.PixelSize), bitmap.PixelSize);
        Assert.Equal("green", Dominant(at(2, 48)));
        Assert.Equal("green", Dominant(at(93, 48)));
    }

    [AvaloniaFact]
    public async Task Transparent_images_stay_png()
    {
        var source = Encode(200, 300, (x, y) => (x - 100) * (x - 100) + (y - 150) * (y - 150) < 80 * 80 ? Blue : 0x00000000u, PngBitmapEncoderOptions.Default);
        var picked = await Compress(source);

        var bytes = Convert.FromBase64String(picked.Image!);
        Assert.Equal(0x89, bytes[0]); // PNG
        var (bitmap, at) = Decode(picked.Image!);
        Assert.Equal(96, bitmap.PixelSize.Width);
        Assert.Equal(0u, at(0, 0) >> 24); // 角上仍然透明
        Assert.Equal("blue", Dominant(at(48, 48)));
    }

    /// <summary>手机照片:像素按「横着」存,EXIF 说要顺时针转 90°。压缩时摆正。</summary>
    [AvaloniaFact]
    public async Task Exif_orientation_is_applied()
    {
        // 存储方向:上红下蓝。顺时针转 90° 之后:左蓝右红。
        var jpeg = Encode(400, 400, (_, y) => y < 200 ? Red : Blue, new JpegBitmapEncoderOptions { Quality = 95 });
        var withExif = InsertExifOrientation(jpeg, 6);
        Assert.Equal(6, IconImages.ReadHeader(withExif)!.Value.Orientation);

        var picked = await Compress(withExif);
        var (_, at) = Decode(picked.Image!);
        Assert.Equal("blue", Dominant(at(10, 48)));
        Assert.Equal("red", Dominant(at(85, 48)));
    }

    private static byte[] InsertExifOrientation(byte[] jpeg, ushort orientation)
    {
        var tiff = new byte[26];
        tiff[0] = (byte)'M'; tiff[1] = (byte)'M'; tiff[3] = 42; // 大端
        tiff[7] = 8;
        tiff[9] = 1;                       // 一项
        tiff[10] = 0x01; tiff[11] = 0x12;  // Orientation
        tiff[13] = 3;                      // SHORT
        tiff[17] = 1;                      // count
        tiff[18] = (byte)(orientation >> 8); tiff[19] = (byte)orientation;
        var app1 = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var length = app1.Length + 2;
        return [.. jpeg[..2], 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. app1, .. jpeg[2..]];
    }

    [AvaloniaFact]
    public async Task Oversized_and_broken_files_are_refused_before_decoding()
    {
        // 文件头说 6000×5000 的 PNG(3000 万像素):不解码直接拒绝
        var header = new byte[64];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(header, 0);
        header[16] = 0; header[17] = 0; header[18] = 0x17; header[19] = 0x70; // 6000
        header[20] = 0; header[21] = 0; header[22] = 0x13; header[23] = 0x88; // 5000
        Assert.Equal(IconPickError.TooLarge, (await Compress(header)).Error);

        Assert.Equal(IconPickError.TooLarge, (await NcwIconImages.CompressAsync(() => throw new InvalidOperationException("must not open"), ProjectIconSpec.MaxFileBytes + 1)).Error);
        Assert.Equal(IconPickError.Unsupported, (await Compress("hello, not an image"u8.ToArray())).Error);

        // 文件头像 PNG,内容是坏的
        var broken = header.ToArray();
        broken[19] = 10; broken[17] = 0; broken[18] = 0;
        broken[23] = 10; broken[21] = 0; broken[22] = 0;
        Assert.Equal(IconPickError.Unsupported, (await Compress(broken)).Error);
    }

    [AvaloniaFact]
    public async Task Converter_decodes_once_per_image_and_ignores_garbage()
    {
        var picked = await Compress(Encode(300, 300, (_, _) => Green, PngBitmapEncoderOptions.Default));
        var image = picked.Image!;
        var first = ProjectIconImageConverter.Instance.Convert(image, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture);
        var second = ProjectIconImageConverter.Instance.Convert(image, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsType<Bitmap>(first);
        Assert.Same(first, second);
        Assert.Null(ProjectIconImageConverter.Instance.Convert("!!!not base64", typeof(object), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(ProjectIconImageConverter.Instance.Convert(null, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    // ───────────────────────── 侧边栏:图片图标 + 右键菜单 ─────────────────────────

    [AvaloniaFact]
    public async Task Sidebar_draws_image_icons_and_the_context_menu_offers_change_icon()
    {
        var stores = MockWorkspace.Create(ConversationFixtures.Now);
        var octopus = new ProjectRef("local", "octopus");
        var picked = await Compress(Encode(240, 240, (x, y) => (x / 40 + y / 40) % 2 == 0 ? 0xFFF97316 : 0xFF1E293B, PngBitmapEncoderOptions.Default));
        new LayoutActions(stores).SetAppearance(octopus, new ProjectAppearance(null, "#F97316", picked.Image));

        var vm = new ShellViewModel(stores);
        var shell = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = 1200, Height = 800, Content = shell };
        window.Show();
        Dispatch();

        var node = shell.GetVisualDescendants().OfType<SidebarProjectNode>().Single(n => (n.DataContext as SidebarProjectViewModel)?.Project == octopus);
        // 图片做成画刷铺在圆角方块里(跟着 CornerRadius 裁圆角),不是一个按矩形裁的 Image
        var image = node.HeaderRow.GetVisualDescendants().OfType<Border>().Single(b => b.Background is Avalonia.Media.ImageBrush);
        Assert.True(image.IsEffectivelyVisible);
        Assert.Equal(new CornerRadius(5), image.CornerRadius);
        Assert.IsType<Bitmap>(((Avalonia.Media.ImageBrush)image.Background!).Source);
        Assert.DoesNotContain(node.HeaderRow.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Oc");
        Snapshot(window, "sidebar-image-icon");

        // 右键菜单里有「更改图标…」,命令就是这个项目的 EditIconCommand
        var menu = Assert.IsAssignableFrom<Control>(TKCode.Arc.ContextMenu.GetMenu(node.HeaderRow));
        var entries = menu.GetLogicalDescendants().OfType<MenuEntry>().ToList();
        var change = Assert.Single(entries, e => (e.Header as string) == Ncw.UI.Localization.NcwTexts.Get(Ncw.UI.Localization.NcwTextKind.ChangeProjectIcon));
        Assert.Same(((SidebarProjectViewModel)node.DataContext!).Tile.EditIconCommand, change.Command);
    }

    // ───────────────────────── 「新建项目」窗口 ─────────────────────────

    private static ClientStores StoresWithDevices()
    {
        var stores = new ClientStores();
        stores.Workspace.Update(w => w with
        {
            Devices = ImmutableDictionary.CreateRange(new[]
            {
                KeyValuePair.Create("local", new DeviceInfo("local", "本机", DeviceKind.Local, DeviceConnection.Connected, null, Role: "owner")),
                KeyValuePair.Create("studio", new DeviceInfo("studio", "工作室 Mac mini", DeviceKind.Remote, DeviceConnection.Connected, null, Role: "member")),
            }),
        });
        return stores;
    }

    private static (Window Window, NcwDialogService Dialogs) Host(bool dark)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
        var root = new Border { Background = Avalonia.Media.Brushes.Transparent };
        var window = new Window { Width = 900, Height = 640, Content = root };
        window.Show();
        Dispatch();
        return (window, new NcwDialogService(root));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task New_project_dialog_picks_a_device_and_keeps_errors_inside(bool dark)
    {
        var (window, dialogs) = Host(dark);
        try
        {
            var stores = StoresWithDevices();
            var actions = new ProjectActions(stores, new DeviceRegistry(), () => dialogs, () => DefaultClientText.Instance);
            var form = new NewProjectViewModel(stores, actions, () => dialogs, () => DefaultClientText.Instance);
            var shown = dialogs.ShowNewProjectAsync(form);
            Dispatch();

            var content = window.GetVisualDescendants().OfType<NewProjectDialog>().Single();
            var browse = content.GetVisualDescendants().OfType<NcwButton>().Single(b => b.Command == form.BrowseCommand);
            Assert.True(browse.IsEffectivelyVisible); // 本机:可以浏览
            Snapshot(window, $"new-project-local-{(dark ? "dark" : "light")}");

            // 换到另一台设备:没有「浏览」,提示手填绝对路径;提交失败(这台设备没有连接对象)原因留在窗口里
            form.DeviceId = "studio";
            form.Path = "/Users/me/code/app";
            await form.SubmitCommand.ExecuteAsync(null);
            Dispatch();
            Assert.False(browse.IsEffectivelyVisible);
            Assert.Equal(NewProjectError.NotConnected, form.Error);
            Assert.False(shown.IsCompleted); // 窗口还开着
            Snapshot(window, $"new-project-remote-error-{(dark ? "dark" : "light")}");

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatch();
            Assert.False(await shown.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
            window.Close();
        }
    }

    // ───────────────────────── 「项目图标」窗口 ─────────────────────────

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Project_icon_dialog_saves_into_the_layout_store(bool dark)
    {
        var (window, dialogs) = Host(dark);
        try
        {
            var stores = MockWorkspace.Create(ConversationFixtures.Now);
            var project = new ProjectRef("local", "octopus");
            var form = new ProjectIconViewModel(project, "octopus", null, new LayoutActions(stores), () => dialogs);
            var shown = dialogs.ShowProjectIconAsync(form);
            Dispatch();

            form.Mode = ProjectIconMode.Text;
            form.Text = "🦑";
            form.SetColorCommand.Execute("#7C3AED");
            Dispatch();
            Assert.Single(window.GetVisualDescendants().OfType<ProjectIconDialog>());
            Snapshot(window, $"project-icon-text-{(dark ? "dark" : "light")}");

            form.Mode = ProjectIconMode.Image;
            Dispatch();
            Snapshot(window, $"project-icon-image-{(dark ? "dark" : "light")}");
            Assert.False(form.SaveCommand.CanExecute(null));

            form.Mode = ProjectIconMode.Text;
            form.SaveCommand.Execute(null);
            Dispatch();
            Assert.True(await shown.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(new ProjectAppearance("🦑", "#7C3AED"), stores.Layout.State.Appearance[project]);
        }
        finally
        {
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
            window.Close();
        }
    }
}
