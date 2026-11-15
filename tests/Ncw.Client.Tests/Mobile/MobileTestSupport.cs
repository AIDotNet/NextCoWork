using System.Diagnostics;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Mobile;

/// <summary>时间由测试推进的时钟:<c>GetUtcNow</c> 和 <c>GetTimestamp</c> 一起走(长按手势、二维码过期、通知时间)。</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long _timestamp = 1_000;
    private TimeSpan _advanced;

    public void Advance(TimeSpan by)
    {
        _advanced += by;
        _timestamp += (long)(by.TotalSeconds * Stopwatch.Frequency);
    }

    public override DateTimeOffset GetUtcNow() => (start + _advanced).ToUniversalTime();

    public override long GetTimestamp() => _timestamp;
}

/// <summary>扫码器的脚本化假实现:权限结果可设,扫描一直等到测试给一个内容(或被取消)。</summary>
public sealed class FakeScanner(bool supported = true, CameraAccess access = CameraAccess.Granted) : IQrScanner
{
    private TaskCompletionSource<string?> _scan = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Scans { get; private set; }

    public bool IsSupported { get; } = supported;

    public CameraAccess Access { get; set; } = access;

    public Task<CameraAccess> RequestAccessAsync(CancellationToken ct = default) => Task.FromResult(Access);

    public Task<string?> ScanAsync(CancellationToken ct = default)
    {
        Scans++;
        _scan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => _scan.TrySetResult(null));
        return _scan.Task;
    }

    public void Read(string content) => _scan.TrySetResult(content);
}

/// <summary>上传的假实现:每次上传记下进度回调,等测试放行。</summary>
public sealed class FakeUploader : IAttachmentUploader
{
    public List<(string Name, byte[] Bytes, IProgress<long>? Progress, TaskCompletionSource<ActionResult<AttachmentDto>> Done, CancellationToken Token)> Uploads { get; } = [];

    public List<string> Removed { get; } = [];

    public Task<ActionResult<AttachmentDto>> UploadAsync(string deviceId, string sessionId, string displayName, string mime, byte[] bytes, IProgress<long>? progress, CancellationToken ct = default)
    {
        var done = new TaskCompletionSource<ActionResult<AttachmentDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Uploads.Add((displayName, bytes, progress, done, ct));
        return done.Task;
    }

    public Task<ActionResult<Unit>> RemoveAsync(string deviceId, string attachmentId, CancellationToken ct = default)
    {
        Removed.Add(attachmentId);
        return Task.FromResult(new ActionResult<Unit>(ActionOutcome.Ok, Unit.Value));
    }

    public static ActionResult<AttachmentDto> Ok(string id, string name, string mime, long size) =>
        new(ActionOutcome.Ok, new AttachmentDto(id, "session", "s1", name, mime, size, null, 0, $"ncw://attachments/sessions/s1/{id}"));
}
