using System.Buffers.Binary;
using System.Collections.Immutable;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;

namespace Ncw.Client.Tests;

/// <summary>项目栏的「新建项目」窗口、「项目图标」窗口、图标图片的文件头 / 预算 / 方向、项目外观的本地持久化。</summary>
public class ProjectIconAndNewProjectTests
{
    // ───────────────────────── 图标图片:文件头 ─────────────────────────

    [Fact]
    public void Reads_png_gif_bmp_headers()
    {
        var png = new byte[24];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 4000);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 3000);
        Assert.Equal(new IconImageHeader(IconImageFormat.Png, 4000, 3000), IconImages.ReadHeader(png));

        var gif = "GIF89a"u8.ToArray().Concat(new byte[] { 0x40, 0x01, 0xF0, 0x00 }).ToArray();
        Assert.Equal(new IconImageHeader(IconImageFormat.Gif, 320, 240), IconImages.ReadHeader(gif));

        var bmp = new byte[26];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 640);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -480); // 自上而下的 BMP 高度是负数
        Assert.Equal(new IconImageHeader(IconImageFormat.Bmp, 640, 480), IconImages.ReadHeader(bmp));
    }

    [Fact]
    public void Reads_webp_headers()
    {
        static byte[] Riff(string chunk, int size = 30)
        {
            var b = new byte[size];
            "RIFF"u8.CopyTo(b);
            "WEBP"u8.CopyTo(b.AsSpan(8));
            System.Text.Encoding.ASCII.GetBytes(chunk).CopyTo(b, 12);
            return b;
        }

        var vp8x = Riff("VP8X");
        vp8x[24] = 0xFF; vp8x[25] = 0x0F; // 宽 - 1 = 4095
        vp8x[27] = 0xFF; vp8x[28] = 0x07; // 高 - 1 = 2047
        Assert.Equal(new IconImageHeader(IconImageFormat.WebP, 4096, 2048), IconImages.ReadHeader(vp8x));

        var vp8l = Riff("VP8L");
        vp8l[20] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(vp8l.AsSpan(21), (uint)(99 | (49 << 14))); // 100 × 50
        Assert.Equal(new IconImageHeader(IconImageFormat.WebP, 100, 50), IconImages.ReadHeader(vp8l));

        var vp8 = Riff("VP8 ");
        vp8[23] = 0x9D; vp8[24] = 0x01; vp8[25] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(vp8.AsSpan(26), 800);
        BinaryPrimitives.WriteUInt16LittleEndian(vp8.AsSpan(28), 600);
        var header = IconImages.ReadHeader(vp8);
        Assert.Equal(new IconImageHeader(IconImageFormat.WebP, 800, 600), header);
        Assert.True(header!.Value.CanDecodeScaled);
    }

    /// <summary>手机照片:EXIF 段在 SOF 前面,方向 6(顺时针 90°)。</summary>
    [Fact]
    public void Reads_jpeg_size_after_exif_and_orientation()
    {
        var jpeg = new List<byte> { 0xFF, 0xD8 };
        // APP1 / Exif:小端 TIFF,IFD0 只有一项 0x0112 = 6
        var tiff = new byte[8 + 2 + 12 + 4];
        tiff[0] = (byte)'I'; tiff[1] = (byte)'I'; tiff[2] = 42;
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x0112);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), 6);
        var app1 = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        jpeg.AddRange([0xFF, 0xE1, (byte)((app1.Length + 2) >> 8), (byte)(app1.Length + 2)]);
        jpeg.AddRange(app1);
        // 一个无关的 APP 段,再加填充字节
        jpeg.AddRange([0xFF, 0xEE, 0x00, 0x04, 0x01, 0x02, 0xFF]);
        // SOF2(渐进式):精度 8,高 3024,宽 4032
        jpeg.AddRange([0xFF, 0xC2, 0x00, 0x11, 0x08, 0x0B, 0xD0, 0x0F, 0xC0, 0x03, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1]);

        var header = IconImages.ReadHeader([.. jpeg]);
        Assert.Equal(new IconImageHeader(IconImageFormat.Jpeg, 4032, 3024, 6), header);
    }

    [Fact]
    public void Rejects_unknown_or_truncated_data()
    {
        Assert.Null(IconImages.ReadHeader("not an image"u8));
        Assert.Null(IconImages.ReadHeader([0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02])); // 没有 SOF 就进了图像数据
        Assert.Null(IconImages.ReadHeader([0xFF, 0xD8, 0xFF, 0xE1, 0x7F, 0xFF])); // 段比数据还长、没有 SOF
        Assert.Null(IconImages.ReadHeader([]));
    }

    // ───────────────────────── 图标图片:内存预算 ─────────────────────────

    [Fact]
    public void Budget_rejects_big_files_and_unscalable_huge_images()
    {
        Assert.Equal(IconPickError.TooLarge, IconImages.Check(ProjectIconSpec.MaxFileBytes + 1, new IconImageHeader(IconImageFormat.Jpeg, 10, 10)));
        Assert.Equal(IconPickError.Unsupported, IconImages.Check(100, null));
        Assert.Equal(IconPickError.Unsupported, IconImages.Check(100, new IconImageHeader(IconImageFormat.Png, 0, 10)));

        // PNG 要先按原尺寸解码:2500 万像素以上不收
        Assert.Null(IconImages.Check(1000, new IconImageHeader(IconImageFormat.Png, 5000, 5000)));
        Assert.Equal(IconPickError.TooLarge, IconImages.Check(1000, new IconImageHeader(IconImageFormat.Png, 6000, 5000)));

        // JPEG 能按 1/8 解码:同样尺寸可以收
        Assert.Null(IconImages.Check(1000, new IconImageHeader(IconImageFormat.Jpeg, 6000, 5000)));
        Assert.Equal(IconPickError.TooLarge, IconImages.Check(1000, new IconImageHeader(IconImageFormat.Jpeg, 20000, 20000)));
    }

    // ───────────────────────── 图标图片:方向与透明 ─────────────────────────

    /// <summary>3×3 的像素,每个像素的第一个字节是它的编号(行优先 0..8)。</summary>
    private static byte[] Grid3() => [.. Enumerable.Range(0, 9).SelectMany(i => new byte[] { (byte)i, 0, 0, 0xFF })];

    private static int[] Ids(byte[] pixels) => [.. pixels.Where((_, i) => i % 4 == 0).Select(b => (int)b)];

    [Fact]
    public void Orient_rotates_and_flips_square_pixels()
    {
        // 0 1 2 / 3 4 5 / 6 7 8
        Assert.Equal([2, 1, 0, 5, 4, 3, 8, 7, 6], Ids(IconImages.Orient(Grid3(), 3, 2)));
        Assert.Equal([8, 7, 6, 5, 4, 3, 2, 1, 0], Ids(IconImages.Orient(Grid3(), 3, 3)));
        Assert.Equal([6, 7, 8, 3, 4, 5, 0, 1, 2], Ids(IconImages.Orient(Grid3(), 3, 4)));
        Assert.Equal([0, 3, 6, 1, 4, 7, 2, 5, 8], Ids(IconImages.Orient(Grid3(), 3, 5)));
        Assert.Equal([6, 3, 0, 7, 4, 1, 8, 5, 2], Ids(IconImages.Orient(Grid3(), 3, 6))); // 顺时针 90°
        Assert.Equal([8, 5, 2, 7, 4, 1, 6, 3, 0], Ids(IconImages.Orient(Grid3(), 3, 7)));
        Assert.Equal([2, 5, 8, 1, 4, 7, 0, 3, 6], Ids(IconImages.Orient(Grid3(), 3, 8))); // 逆时针 90°

        var same = Grid3();
        Assert.Same(same, IconImages.Orient(same, 3, 1));
        Assert.Same(same, IconImages.Orient(same, 3, 9));
    }

    [Fact]
    public void Transparency_decides_png_versus_jpeg()
    {
        var opaque = Grid3();
        Assert.False(IconImages.HasTransparency(opaque));
        opaque[4 * 4 + 3] = 0x80;
        Assert.True(IconImages.HasTransparency(opaque));
    }

    // ───────────────────────── 「项目图标」窗口 ─────────────────────────

    private sealed class IconDialogs(PickedIcon? next) : IDialogService
    {
        public int Picks { get; private set; }

        public Task<string?> PromptTextAsync(string title, string description, string label, string? initial) => Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<PickedIcon?> PickIconImageAsync()
        {
            Picks++;
            return Task.FromResult(next);
        }
    }

    private static readonly ProjectRef Ncw = new("local", "nextcowork");
    private static readonly ProjectRef Octopus = new("local", "octopus");

    [Fact]
    public void Icon_form_starts_from_the_current_appearance()
    {
        var stores = MockWorkspace.Create(DateTimeOffset.Now);
        var actions = new LayoutActions(stores);

        var text = new ProjectIconViewModel(Ncw, "NextCoWork", stores.Layout.State.Appearance[Ncw], actions, () => NoDialogs.Instance);
        Assert.Equal(ProjectIconMode.Text, text.Mode);
        Assert.Equal("🐙", text.PreviewGlyph);
        Assert.Equal("#0F766E", text.Color);

        var initials = new ProjectIconViewModel(Octopus, "octopus", stores.Layout.State.Appearance[Octopus], actions, () => NoDialogs.Instance);
        Assert.Equal(ProjectIconMode.Initials, initials.Mode);
        Assert.Equal("Oc", initials.PreviewGlyph); // 默认图标取两个字母(03 §11.8)

        var image = new ProjectIconViewModel(Ncw, "NextCoWork", new ProjectAppearance(null, "#2563EB", "AAAA"), actions, () => NoDialogs.Instance);
        Assert.Equal(ProjectIconMode.Image, image.Mode);
        Assert.Equal("AAAA", image.PreviewImage);
        Assert.False(image.ShowsColor);
    }

    [Fact]
    public void Icon_text_keeps_at_most_two_user_visible_characters()
    {
        Assert.Null(ProjectIconViewModel.NormalizeGlyph("   "));
        Assert.Equal("AB", ProjectIconViewModel.NormalizeGlyph(" ABC "));
        Assert.Equal("项目", ProjectIconViewModel.NormalizeGlyph("项目管理"));
        // 家庭 emoji 是一个文本元素(多个码位 + ZWJ),不能被截成半个
        Assert.Equal("👨‍👩‍👧🚀", ProjectIconViewModel.NormalizeGlyph("👨‍👩‍👧🚀✨"));
    }

    [Fact]
    public void Saving_writes_the_store_and_default_looking_icons_leave_no_record()
    {
        var stores = MockWorkspace.Create(DateTimeOffset.Now);
        var actions = new LayoutActions(stores);
        var completed = 0;

        var form = new ProjectIconViewModel(Octopus, "octopus", null, actions, () => NoDialogs.Instance);
        form.Completed += () => completed++;
        form.Mode = ProjectIconMode.Text;
        form.Text = "🦑";
        form.SetColorCommand.Execute("#DB2777");
        form.SaveCommand.Execute(null);
        Assert.Equal(new ProjectAppearance("🦑", "#DB2777"), stores.Layout.State.Appearance[Octopus]);
        Assert.Equal(1, completed);

        // 首字母 + 默认色 = 默认:不留记录
        var plain = new ProjectIconViewModel(Octopus, "octopus", null, actions, () => NoDialogs.Instance);
        Assert.Null(plain.Build());
        plain.SaveCommand.Execute(null);
        Assert.False(stores.Layout.State.Appearance.ContainsKey(Octopus));

        // 恢复默认
        stores.Layout.Update(s => s with { Appearance = s.Appearance.SetItem(Octopus, new ProjectAppearance("X", "#000000")) });
        new ProjectIconViewModel(Octopus, "octopus", stores.Layout.State.Appearance[Octopus], actions, () => NoDialogs.Instance).ResetCommand.Execute(null);
        Assert.False(stores.Layout.State.Appearance.ContainsKey(Octopus));
    }

    [Fact]
    public async Task Picking_an_image_switches_to_image_mode_and_errors_are_kept_in_the_form()
    {
        var stores = MockWorkspace.Create(DateTimeOffset.Now);
        var actions = new LayoutActions(stores);

        var failing = new IconDialogs(PickedIcon.Fail(IconPickError.TooLarge));
        var form = new ProjectIconViewModel(Octopus, "octopus", null, actions, () => failing);
        form.Mode = ProjectIconMode.Image;
        Assert.False(form.SaveCommand.CanExecute(null)); // 图片模式下还没选图,不能保存
        await form.PickImageCommand.ExecuteAsync(null);
        Assert.Equal(IconPickError.TooLarge, form.ImageError);
        Assert.Null(form.Image);

        var ok = new IconDialogs(PickedIcon.Ok("BASE64"));
        form = new ProjectIconViewModel(Octopus, "octopus", null, actions, () => ok);
        await form.PickImageCommand.ExecuteAsync(null);
        Assert.Equal(ProjectIconMode.Image, form.Mode);
        Assert.Null(form.ImageError);
        Assert.True(form.SaveCommand.CanExecute(null));
        form.SaveCommand.Execute(null);
        Assert.Equal("BASE64", stores.Layout.State.Appearance[Octopus].Image);

        // 项目栏的图标跟着 Store 走
        var rail = new RailViewModel(stores, actions);
        var tile = rail.Items.OfType<RailProjectViewModel>().Concat(rail.Items.OfType<RailGroupViewModel>().SelectMany(g => g.Members)).Single(t => t.Project == Octopus);
        Assert.True(tile.HasImage);
        Assert.Equal("BASE64", tile.Image);

        // 取消选图:什么都不变
        var cancel = new IconDialogs(null);
        form = new ProjectIconViewModel(Octopus, "octopus", stores.Layout.State.Appearance[Octopus], actions, () => cancel);
        await form.PickImageCommand.ExecuteAsync(null);
        Assert.Equal(1, cancel.Picks);
        Assert.Equal("BASE64", form.Image);
    }

    [Fact]
    public void Rail_tile_ignores_oversized_stored_images()
    {
        var stores = MockWorkspace.Create(DateTimeOffset.Now);
        stores.Layout.Update(s => s with { Appearance = s.Appearance.SetItem(Octopus, new ProjectAppearance(null, "#000000", new string('A', ProjectIconSpec.MaxStoredLength + 1))) });
        var rail = new RailViewModel(stores, new LayoutActions(stores));
        var tile = rail.Items.OfType<RailProjectViewModel>().Concat(rail.Items.OfType<RailGroupViewModel>().SelectMany(g => g.Members)).Single(t => t.Project == Octopus);
        Assert.False(tile.HasImage);
        Assert.True(tile.CanEditIcon);
    }

    // ───────────────────────── 项目外观的本地持久化 ─────────────────────────

    private sealed class MemoryLayout(ImmutableDictionary<ProjectRef, ProjectAppearance>? initial = null) : ILayoutRepository
    {
        public ImmutableDictionary<ProjectRef, ProjectAppearance>? Saved { get; private set; } = initial;

        public int Saves { get; private set; }

        public ImmutableDictionary<ProjectRef, ProjectAppearance>? Load() => Saved;

        public void Save(ImmutableDictionary<ProjectRef, ProjectAppearance> appearance)
        {
            Saved = appearance;
            Saves++;
        }
    }

    [Fact]
    public void Saved_appearance_is_loaded_and_only_appearance_changes_are_written()
    {
        var stores = MockWorkspace.Create(DateTimeOffset.Now);
        var repo = new MemoryLayout(ImmutableDictionary<ProjectRef, ProjectAppearance>.Empty.Add(Octopus, new ProjectAppearance(null, "#000000", "IMG")));
        using var persistence = new LayoutPersistence(stores, repo, TimeSpan.FromHours(1));
        Assert.Equal("IMG", stores.Layout.State.Appearance[Octopus].Image);
        Assert.Equal("🐙", stores.Layout.State.Appearance[Ncw].Glyph); // 文件里没有的保持不变

        var actions = new LayoutActions(stores);
        actions.WrapInGroup(Octopus); // 只动项目栏:不写
        persistence.Flush();
        Assert.Equal(0, repo.Saves);

        actions.SetAppearance(Octopus, null);
        persistence.Flush();
        Assert.Equal(1, repo.Saves);
        Assert.False(repo.Saved!.ContainsKey(Octopus));
    }

    [Fact]
    public void Layout_file_round_trips_and_drops_bad_entries()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ncw-layout-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "layout.json");
        try
        {
            var repo = new JsonFileLayoutRepository(path);
            var appearance = ImmutableDictionary<ProjectRef, ProjectAppearance>.Empty
                .Add(Ncw, new ProjectAppearance("🐙", "#0F766E"))
                .Add(Octopus, new ProjectAppearance(null, "#2563EB", "AAAA"));
            repo.Save(appearance);
            Assert.Equal(appearance, repo.Load());

            File.WriteAllText(path, $$"""
            { "version": 1, "appearance": [
              { "deviceId": "local", "workspaceId": "a", "color": "#111111", "glyph": "" },
              { "deviceId": "", "workspaceId": "b", "color": "#111111" },
              { "deviceId": "local", "workspaceId": "c", "color": "" },
              { "deviceId": "local", "workspaceId": "d", "color": "#222222", "image": "{{new string('A', ProjectIconSpec.MaxStoredLength + 1)}}" }
            ], "future": true }
            """);
            var loaded = repo.Load()!;
            Assert.Equal(2, loaded.Count);
            Assert.Equal(new ProjectAppearance(null, "#111111"), loaded[new ProjectRef("local", "a")]);
            Assert.Equal(new ProjectAppearance(null, "#222222"), loaded[new ProjectRef("local", "d")]); // 过长的图片当作没有
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
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
                KeyValuePair.Create("viewer", new DeviceInfo("viewer", "只读的机器", DeviceKind.Remote, DeviceConnection.Connected, null, Role: "viewer")),
                KeyValuePair.Create("offline", new DeviceInfo("offline", "离线的机器", DeviceKind.Remote, DeviceConnection.Offline, null)),
                KeyValuePair.Create("cloud", new DeviceInfo("cloud", "云端", DeviceKind.Cloud, DeviceConnection.Connected, null, Role: "owner")),
            }),
        });
        return stores;
    }

    private static NewProjectViewModel Form(ClientStores stores, string? preferred = null, IDialogService? dialogs = null)
    {
        var d = dialogs ?? NoDialogs.Instance;
        return new NewProjectViewModel(stores, new ProjectActions(stores, new DeviceRegistry(), () => d, () => DefaultClientText.Instance), () => d, () => DefaultClientText.Instance, preferred);
    }

    [Fact]
    public void Lists_connected_devices_that_can_add_projects_local_first()
    {
        var choices = NewProjectViewModel.Choices(StoresWithDevices().Workspace.State);
        Assert.Equal(["local", "cloud", "studio"], choices.Select(c => c.DeviceId));
    }

    [Fact]
    public void Defaults_to_the_selected_projects_device_and_only_local_can_browse()
    {
        var stores = StoresWithDevices();
        using var fallback = Form(stores, preferred: "gone");
        Assert.Equal("local", fallback.DeviceId);
        Assert.True(fallback.CanBrowse);
        Assert.False(fallback.IsRemoteDevice);

        using var remote = Form(stores, preferred: "studio");
        Assert.Equal("studio", remote.DeviceId);
        Assert.False(remote.CanBrowse);
        Assert.True(remote.IsRemoteDevice);

        // 设备掉线:换到还能用的设备
        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("studio", w.Devices["studio"] with { Connection = DeviceConnection.Offline }) });
        Assert.Equal("local", remote.DeviceId);
        Assert.DoesNotContain(remote.Devices, d => d.DeviceId == "studio");
    }

    [Fact]
    public async Task Submitting_reports_errors_in_the_form_without_closing()
    {
        var stores = StoresWithDevices();
        using var form = Form(stores, preferred: "studio");
        var completed = false;
        form.Completed += () => completed = true;

        Assert.False(await form.SubmitAsync());
        Assert.Equal(NewProjectError.PathRequired, form.Error);

        form.Path = "/Users/me/code/app";
        await form.SubmitCommand.ExecuteAsync(null);
        Assert.Equal(NewProjectError.NotConnected, form.Error); // 注册表里没有这台设备的连接
        Assert.False(completed);
        Assert.False(form.IsBusy);
    }

    [Fact]
    public void No_devices_means_nothing_to_submit()
    {
        using var form = Form(new ClientStores());
        Assert.False(form.HasDevices);
        Assert.Null(form.DeviceId);
        Assert.False(form.SubmitCommand.CanExecute(null));
    }

    private sealed class FolderDialogs(string? folder) : IDialogService
    {
        public string? Error { get; private set; }

        public Task<string?> PromptTextAsync(string title, string description, string label, string? initial) => Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult(folder);

        public Task ShowErrorAsync(string title, string message)
        {
            Error = message;
            return Task.CompletedTask;
        }
    }

    /// <summary>没有界面的对话框实现(测试替身)走默认路径:浏览本机文件夹并提交,失败时报错。</summary>
    [Fact]
    public async Task Headless_dialogs_fall_back_to_browse_and_submit()
    {
        var stores = StoresWithDevices();
        var dialogs = new FolderDialogs("/tmp/app");
        using var form = Form(stores, dialogs: dialogs);
        Assert.False(await ((IDialogService)dialogs).ShowNewProjectAsync(form));
        Assert.Equal("/tmp/app", form.Path);
        Assert.Equal(NewProjectError.NotConnected, form.Error);
        Assert.NotNull(dialogs.Error);

        // 选中的是别的设备:没法浏览,当作取消
        using var remote = Form(stores, preferred: "studio", dialogs: dialogs);
        Assert.False(await ((IDialogService)dialogs).ShowNewProjectAsync(remote));
        Assert.Equal("", remote.Path);
    }
}
