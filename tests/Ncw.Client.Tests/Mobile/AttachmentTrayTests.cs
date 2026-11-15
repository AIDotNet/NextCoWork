using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Mobile;

/// <summary>输入框的附件托盘:选择 → 读取 → 分块上传(带进度)→ 完成 / 失败 / 取消;上传完成的附件随消息作为 image / file 块发出。</summary>
public class AttachmentTrayTests
{
    private static readonly SessionRef S1 = new("local", "s1");

    private static PickedAttachment Photo(string name = "a.jpg", long size = 1000, string mime = "image/jpeg") =>
        new(name, mime, size, _ => Task.FromResult(new byte[Math.Min(size, 16)]));

    private static (AttachmentTrayViewModel Tray, FakeUploader Uploader) Make(IAttachmentPicker? picker = null)
    {
        var uploader = new FakeUploader();
        var tray = new AttachmentTrayViewModel(uploader, () => picker);
        tray.SetSession(S1);
        return (tray, uploader);
    }

    [Fact]
    public async Task Progress_follows_the_chunks_and_completion_marks_the_attachment_ready_to_send()
    {
        var (tray, uploader) = Make();

        var upload = tray.AddAsync(Photo(size: 1000));
        var item = Assert.Single(tray.Items);
        Assert.True(item.IsUploading);
        Assert.True(tray.IsUploading);

        uploader.Uploads[0].Progress!.Report(250);
        Assert.Equal(0.25, item.Progress, 3);
        uploader.Uploads[0].Progress!.Report(1000);
        uploader.Uploads[0].Done.SetResult(FakeUploader.Ok("a1", "a.jpg", "image/jpeg", 1000));
        await upload;

        Assert.True(item.IsDone);
        Assert.False(tray.IsUploading);
        var ready = Assert.Single(tray.Ready());
        Assert.Equal("ncw://attachments/sessions/s1/a1", ready.Path);
        Assert.Equal("image/jpeg", ready.Mime);
    }

    [Fact]
    public async Task A_file_over_the_attachment_limit_fails_as_too_large_without_reading_or_uploading()
    {
        var (tray, uploader) = Make();
        var read = false;
        var huge = new PickedAttachment("big.mov", "video/quicktime", Attachments.MaxBytes + 1, _ =>
        {
            read = true;
            return Task.FromResult(new byte[1]);
        });

        await tray.AddAsync(huge);

        Assert.Equal(AttachmentFailure.TooLarge, tray.Items.Single().Failure);
        Assert.False(read);
        Assert.Empty(uploader.Uploads);
    }

    [Fact]
    public async Task An_empty_file_and_an_unreadable_file_fail_with_their_own_reasons()
    {
        var (tray, _) = Make();

        await tray.AddAsync(Photo("empty.png", 0, "image/png"));
        await tray.AddAsync(new PickedAttachment("gone.pdf", "application/pdf", 10, _ => throw new FileNotFoundException()));

        Assert.Equal([AttachmentFailure.Empty, AttachmentFailure.ReadFailed], tray.Items.Select(i => i.Failure));
    }

    [Theory]
    [InlineData(ActionOutcome.Unavailable, AttachmentFailure.DeviceUnavailable)]
    [InlineData(ActionOutcome.Forbidden, AttachmentFailure.Forbidden)]
    [InlineData(ActionOutcome.Invalid, AttachmentFailure.Rejected)]
    [InlineData(ActionOutcome.Failed, AttachmentFailure.Rejected)]
    public async Task Device_outcomes_map_to_failure_reasons_and_a_failed_upload_can_be_retried(ActionOutcome outcome, AttachmentFailure expected)
    {
        var (tray, uploader) = Make();
        var upload = tray.AddAsync(Photo());
        uploader.Uploads[0].Done.SetResult(new ActionResult<AttachmentDto>(outcome));
        await upload;

        var item = tray.Items.Single();
        Assert.True(item.IsFailed);
        Assert.Equal(expected, item.Failure);
        Assert.Empty(tray.Ready());

        var retry = tray.RetryCommand.ExecuteAsync(item);
        Assert.True(item.IsUploading);
        Assert.Null(item.Failure);
        uploader.Uploads[1].Done.SetResult(FakeUploader.Ok("a2", "a.jpg", "image/jpeg", 1000));
        await retry;
        Assert.True(item.IsDone);
    }

    [Fact]
    public async Task Removing_an_uploading_attachment_cancels_it_and_removing_a_finished_one_also_drops_the_draft_on_the_device()
    {
        var (tray, uploader) = Make();
        var first = tray.AddAsync(Photo("one.jpg"));
        var second = tray.AddAsync(Photo("two.jpg"));
        uploader.Uploads[1].Done.SetResult(FakeUploader.Ok("a2", "two.jpg", "image/jpeg", 1000));
        await second;

        await tray.RemoveCommand.ExecuteAsync(tray.Items[0]);
        Assert.True(uploader.Uploads[0].Token.IsCancellationRequested);
        uploader.Uploads[0].Done.SetResult(FakeUploader.Ok("a1", "one.jpg", "image/jpeg", 1000)); // 取消之后才返回的结果被丢弃
        await first;
        Assert.Equal("two.jpg", tray.Items.Single().Name);

        await tray.RemoveCommand.ExecuteAsync(tray.Items[0]);
        Assert.Empty(tray.Items);
        Assert.Equal(["a2"], uploader.Removed);
    }

    [Fact]
    public async Task Attachments_belong_to_their_session_and_come_back_when_the_session_does()
    {
        var (tray, uploader) = Make();
        var upload = tray.AddAsync(Photo());
        uploader.Uploads[0].Done.SetResult(FakeUploader.Ok("a1", "a.jpg", "image/jpeg", 1000));
        await upload;

        tray.SetSession(new SessionRef("local", "s2"));
        Assert.Empty(tray.Items);
        Assert.Empty(tray.Ready());
        tray.SetSession(S1);

        Assert.Single(tray.Ready());
    }

    [Fact]
    public async Task Pick_asks_the_host_picker_for_the_chosen_source_and_uploads_every_picked_file()
    {
        var picker = new Picker(Photo("p1.jpg"), Photo("p2.jpg"));
        var (tray, uploader) = Make(picker);

        Assert.True(tray.CanPick);
        Assert.True(tray.IsAvailable(AttachmentSource.Camera));
        await tray.PickCommand.ExecuteAsync(AttachmentSource.PhotoLibrary);

        Assert.Equal([AttachmentSource.PhotoLibrary], picker.Asked);
        Assert.Equal(["p1.jpg", "p2.jpg"], uploader.Uploads.Select(u => u.Name));
    }

    [Fact]
    public void Without_a_host_picker_or_a_session_the_attachment_button_cannot_be_used()
    {
        var uploader = new FakeUploader();
        var tray = new AttachmentTrayViewModel(uploader, () => null);
        Assert.False(tray.CanPick);

        var withPicker = new AttachmentTrayViewModel(uploader, () => new Picker());
        Assert.False(withPicker.CanPick); // 还没有会话
        withPicker.SetSession(S1);
        Assert.True(withPicker.CanPick);
    }

    [Fact]
    public async Task Clearing_after_a_send_empties_the_tray_without_deleting_the_committed_files()
    {
        var (tray, uploader) = Make();
        var upload = tray.AddAsync(Photo());
        uploader.Uploads[0].Done.SetResult(FakeUploader.Ok("a1", "a.jpg", "image/jpeg", 1000));
        await upload;

        tray.Clear();

        Assert.Empty(tray.Items);
        Assert.Empty(uploader.Removed);
    }

    private sealed class Picker(params PickedAttachment[] files) : IAttachmentPicker
    {
        public List<AttachmentSource> Asked { get; } = [];

        public bool IsAvailable(AttachmentSource source) => true;

        public Task<IReadOnlyList<PickedAttachment>> PickAsync(AttachmentSource source, CancellationToken ct = default)
        {
            Asked.Add(source);
            return Task.FromResult<IReadOnlyList<PickedAttachment>>(files);
        }
    }
}
