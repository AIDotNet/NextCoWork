using System.Collections.Immutable;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.Transcript;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>用户消息里的图片:引用解析、图片来源(data: / ncw://)、缩略图的加载状态机、灯箱。</summary>
public class MessageImagesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly ProjectRef Project = new("local", "nextcowork");

    // 1x1 透明 PNG
    private const string PngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
    private static readonly string PngDataUrl = "data:image/png;base64," + PngBase64;

    private static MessageDto UserMessage(string id, params string[] parts) =>
        new(id, "user", Now.ToUnixTimeMilliseconds(), false, "[" + string.Join(",", parts) + "]");

    private static string Text(string text) => $"{{\"type\":\"text\",\"text\":\"{text}\"}}";

    private static string Image(string dataRef, string mime = "image/png") => $"{{\"type\":\"image\",\"mime\":\"{mime}\",\"dataRef\":\"{dataRef}\"}}";

    // ───────────────────────── 引用解析 ─────────────────────────

    [Fact]
    public void A_user_message_yields_its_image_refs_in_order()
    {
        var message = UserMessage("u1", Text("look"), Image("ncw://att-1"), Image(PngDataUrl), Image("ncw://att-2", "image/jpeg"));
        var images = MessageImages.Of(message);

        Assert.Equal([("image/png", "ncw://att-1"), ("image/png", PngDataUrl), ("image/jpeg", "ncw://att-2")], images.Select(i => (i.Mime, i.DataRef)));
    }

    [Fact]
    public void Internal_assistant_and_malformed_messages_have_no_images()
    {
        Assert.Empty(MessageImages.Of(UserMessage("u1", Text("hi"))));
        Assert.Empty(MessageImages.Of(new MessageDto("a1", "assistant", 1, false, "[" + Image("ncw://x") + "]")));
        Assert.Empty(MessageImages.Of(new MessageDto("i1", "user", 1, true, "[" + Image("ncw://x") + "]"))); // 内部消息(压缩边界等)
        Assert.Empty(MessageImages.Of(new MessageDto("m1", "user", 1, false, "not json")));
        Assert.Empty(MessageImages.Of(UserMessage("u2", "{\"type\":\"image\",\"mime\":\"image/png\"}", "{\"type\":\"image\",\"dataRef\":\"ncw://x\"}"))); // 缺 mime / 缺引用
    }

    // ───────────────────────── 图片来源 ─────────────────────────

    [Fact]
    public void Data_urls_decode_only_when_they_are_base64_images()
    {
        var png = AttachmentImageSource.DecodeDataUrl(PngDataUrl);
        Assert.NotNull(png);
        Assert.Equal("image/png", png!.Mime);
        Assert.Equal(Convert.FromBase64String(PngBase64), png.Data);

        Assert.Null(AttachmentImageSource.DecodeDataUrl("data:text/html;base64,PGgxPmhpPC9oMT4=")); // 不是图片
        Assert.Null(AttachmentImageSource.DecodeDataUrl("data:image/png,rawbytes"));                // 不是 base64
        Assert.Null(AttachmentImageSource.DecodeDataUrl("data:image/png;base64,!!!not-base64!!!"));
        Assert.Null(AttachmentImageSource.DecodeDataUrl("data:image/png;base64,"));
        Assert.Null(AttachmentImageSource.DecodeDataUrl("data:image/png;base64"));
        Assert.Null(AttachmentImageSource.DecodeDataUrl("https://evil.test/a.png"));
        Assert.Equal("image/png", AttachmentImageSource.DecodeDataUrl("DATA:IMAGE/PNG;BASE64," + PngBase64)!.Mime); // 大小写不敏感,MIME 归一成小写
    }

    [Fact]
    public async Task Inline_images_load_without_a_device_and_are_cached()
    {
        var source = new AttachmentImageSource(new AttachmentActions(new DeviceRegistry()));
        var first = await source.LoadAsync("local", PngDataUrl);
        var second = await source.LoadAsync("local", PngDataUrl);

        Assert.NotNull(first);
        Assert.Same(first, second); // 滚出去又滚回来不重复解码
    }

    [Fact]
    public async Task Unknown_schemes_and_a_missing_device_fail_instead_of_throwing()
    {
        var source = new AttachmentImageSource(new AttachmentActions(new DeviceRegistry()));
        Assert.Null(await source.LoadAsync("local", "file:///etc/passwd"));      // 不认识的引用不去取
        Assert.Null(await source.LoadAsync("local", "https://evil.test/a.png"));
        Assert.Null(await source.LoadAsync("local", "ncw://att-1"));             // 设备没连上
        Assert.Null(await source.LoadAsync("local", "ncw://att-1"));             // 失败不缓存,再试一次也是真的再试
    }

    [Fact]
    public async Task The_cache_is_bounded()
    {
        var source = new AttachmentImageSource(new AttachmentActions(new DeviceRegistry()));
        for (var i = 0; i < AttachmentImageSource.MaxCachedImages + 10; i++)
        {
            // 每张都是不同的引用(末尾加一个无害的 base64 填充变体不行,所以换设备 id 区分缓存键)
            Assert.NotNull(await source.LoadAsync($"device-{i}", PngDataUrl));
        }

        // 能继续加载,没有因为超过上限而出错
        Assert.NotNull(await source.LoadAsync("device-0", PngDataUrl));
    }

    // ───────────────────────── 缩略图状态机 ─────────────────────────

    private static ImageThumbViewModel Thumb(Func<CancellationToken, Task<ImageBytes?>> loader) => new("image/png", "ncw://att-1", loader);

    [Fact]
    public async Task A_thumb_loads_once_however_often_it_is_asked()
    {
        var calls = 0;
        var thumb = Thumb(_ =>
        {
            calls++;
            return Task.FromResult<ImageBytes?>(new ImageBytes([1, 2, 3], "image/png"));
        });
        Assert.True(thumb.IsLoading); // 还没开始也算「加载中」(显示转圈,不显示失败)
        Assert.Null(thumb.Data);

        await Task.WhenAll(thumb.EnsureLoadedAsync(), thumb.EnsureLoadedAsync());
        await thumb.EnsureLoadedAsync();

        Assert.Equal(1, calls);
        Assert.True(thumb.IsReady);
        Assert.Equal([1, 2, 3], thumb.Data);
    }

    [Theory]
    [InlineData(false)] // 取不到
    [InlineData(true)]  // 取到的是空字节
    public async Task A_thumb_that_cannot_load_fails_and_retry_tries_again(bool emptyBytes)
    {
        var attempts = 0;
        var thumb = Thumb(_ =>
        {
            attempts++;
            return Task.FromResult<ImageBytes?>(attempts == 1 ? (emptyBytes ? new ImageBytes([], "image/png") : null) : new ImageBytes([9], "image/png"));
        });
        await thumb.EnsureLoadedAsync();
        Assert.True(thumb.IsFailed);
        Assert.Null(thumb.Data);

        await thumb.RetryCommand.ExecuteAsync(null);
        Assert.True(thumb.IsReady);
        Assert.Equal([9], thumb.Data);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_loader_that_throws_marks_the_thumb_failed_and_never_escapes()
    {
        var thumb = Thumb(_ => throw new IOException("device went away"));
        await thumb.EnsureLoadedAsync();
        Assert.True(thumb.IsFailed);
    }

    [Fact]
    public async Task Retry_does_nothing_unless_the_thumb_failed()
    {
        var calls = 0;
        var thumb = Thumb(_ =>
        {
            calls++;
            return Task.FromResult<ImageBytes?>(new ImageBytes([1], "image/png"));
        });
        await thumb.RetryCommand.ExecuteAsync(null);
        Assert.Equal(0, calls);
        await thumb.EnsureLoadedAsync();
        await thumb.RetryCommand.ExecuteAsync(null);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task State_changes_notify_the_derived_flags()
    {
        var thumb = Thumb(_ => Task.FromResult<ImageBytes?>(new ImageBytes([1], "image/png")));
        var changed = new List<string>();
        thumb.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        await thumb.EnsureLoadedAsync();

        Assert.Contains(nameof(ImageThumbViewModel.IsReady), changed);
        Assert.Contains(nameof(ImageThumbViewModel.IsLoading), changed);
        Assert.Contains(nameof(ImageThumbViewModel.IsFailed), changed);
    }

    // ───────────────────────── 对话里的缩略图与灯箱 ─────────────────────────

    private sealed class NoSend : IConversationCommands
    {
        public Task<SendResult> SendAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(new SendResult(SendOutcome.Started));

        public Task<bool> EnqueueAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> SteerAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> AbortAsync(RunRef run, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> PromoteQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> RemoveQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<InteractionResult> RespondAsync(string deviceId, InteractionDto interaction, InteractionResponseDto response, CancellationToken ct = default) =>
            Task.FromResult(new InteractionResult(InteractionOutcome.Answered));

        public Task<SessionRef?> CreateSessionAsync(ProjectRef project, string? model = null, CancellationToken ct = default) => Task.FromResult<SessionRef?>(null);
    }

    private sealed class FakeImages : IImageSource
    {
        public List<(string DeviceId, string Ref)> Loads { get; } = [];

        public Task<ImageBytes?> LoadAsync(string deviceId, string dataRef, CancellationToken ct = default)
        {
            Loads.Add((deviceId, dataRef));
            return Task.FromResult<ImageBytes?>(new ImageBytes([1, 2, 3], "image/png"));
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ClientStores Stores(string sessionId, params MessageDto[] messages)
    {
        var stores = MockWorkspace.Create(Now);
        stores.Providers.Update(_ => new ProvidersMirror(ImmutableDictionary<string, DeviceProviders>.Empty.Add("local", DeviceProviders.Unknown with { Known = true, Options = [] })));
        stores.Selection.Update(_ => new SelectionState(Project, sessionId));
        var meta = new SessionDto(sessionId, "nextcowork", null, "user", "t", null, "opus", null, "code", "auto", false, false, 1, 2);
        stores.Conversations.Update(c => ConversationReducer.ApplySession(c, "local",
            new SessionTopicState(new SessionPageDto(meta, messages, false, [], [], []), new QueueDto(sessionId, 1, []), null)));
        return stores;
    }

    [Fact]
    public async Task A_user_row_gets_a_thumb_per_image_and_loads_through_the_image_source()
    {
        var stores = Stores("s-ncw-3", UserMessage("u1", Text("two pics"), Image("ncw://att-1"), Image("ncw://att-2")));
        var images = new FakeImages();
        var conversation = new ConversationViewModel(stores, new NoSend(), clock: new Clock(), images: images);
        var row = conversation.Rows.OfType<UserRowViewModel>().Single();

        Assert.True(row.HasThumbs);
        Assert.False(row.HasImageCountOnly);
        Assert.Equal(["ncw://att-1", "ncw://att-2"], row.Images.Select(i => i.DataRef));
        Assert.Empty(images.Loads); // 行创建时不取字节:要等视图挂上去

        await row.Images[0].EnsureLoadedAsync();
        Assert.Equal([("local", "ncw://att-1")], images.Loads);
        Assert.True(row.Images[0].IsReady);
    }

    [Fact]
    public void Without_an_image_source_the_row_falls_back_to_the_count()
    {
        var stores = Stores("s-ncw-3", UserMessage("u1", Text("pic"), Image("ncw://att-1")));
        var row = new ConversationViewModel(stores, new NoSend(), clock: new Clock()).Rows.OfType<UserRowViewModel>().Single();

        Assert.False(row.HasThumbs);
        Assert.True(row.HasImageCountOnly);
        Assert.Equal(1, row.Row.ImageCount);
    }

    [Fact]
    public void A_text_only_message_has_neither_thumbs_nor_a_count()
    {
        var stores = Stores("s-ncw-3", UserMessage("u1", Text("plain")));
        var row = new ConversationViewModel(stores, new NoSend(), clock: new Clock(), images: new FakeImages()).Rows.OfType<UserRowViewModel>().Single();

        Assert.False(row.HasThumbs);
        Assert.False(row.HasImageCountOnly);
    }

    [Fact]
    public async Task The_lightbox_opens_only_for_a_loaded_image_and_closes_on_request_or_when_the_session_changes()
    {
        var stores = Stores("s-ncw-3", UserMessage("u1", Text("pic"), Image("ncw://att-1")));
        var conversation = new ConversationViewModel(stores, new NoSend(), clock: new Clock(), images: new FakeImages());
        var thumb = conversation.Rows.OfType<UserRowViewModel>().Single().Images[0];

        conversation.OpenImageCommand.Execute(thumb); // 还没加载好:不开
        Assert.False(conversation.HasLightbox);

        await thumb.EnsureLoadedAsync();
        conversation.OpenImageCommand.Execute(thumb);
        Assert.True(conversation.HasLightbox);
        Assert.Same(thumb, conversation.Lightbox);

        conversation.CloseLightboxCommand.Execute(null);
        Assert.False(conversation.HasLightbox);

        conversation.OpenImageCommand.Execute(thumb);
        stores.Selection.Update(s => s with { SessionId = "s-ncw-1" }); // 换会话
        Assert.False(conversation.HasLightbox);
    }
}
