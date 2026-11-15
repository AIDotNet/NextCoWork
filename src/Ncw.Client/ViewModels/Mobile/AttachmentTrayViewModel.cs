using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>一个附件在输入框上方的状态。</summary>
public enum AttachmentState
{
    /// <summary>读取字节 / 上传中,<see cref="AttachmentItemViewModel.Progress"/> 在走。</summary>
    Uploading,

    /// <summary>上传完成,发送消息时会带上它。</summary>
    Done,

    /// <summary>失败,原因见 <see cref="AttachmentItemViewModel.Failure"/>;可以重试或移除。</summary>
    Failed,
}

public enum AttachmentFailure
{
    /// <summary>超过单个附件上限(32 MiB),没有发出任何请求。</summary>
    TooLarge,

    /// <summary>空文件。</summary>
    Empty,

    /// <summary>设备没连上 / 数据没就绪(可以重试)。</summary>
    DeviceUnavailable,

    /// <summary>没有权限(只读角色)。</summary>
    Forbidden,

    /// <summary>设备拒绝(格式不支持、写入失败)。</summary>
    Rejected,

    /// <summary>读取所选文件失败(沙盒、文件被删)。</summary>
    ReadFailed,
}

/// <summary>托盘里的一个附件:名字、大小、上传进度、结果。</summary>
public sealed partial class AttachmentItemViewModel : ObservableObject
{
    internal AttachmentItemViewModel(PickedAttachment source) => Source = source;

    internal PickedAttachment Source { get; }

    public string Name => Source.Name;

    public string Mime => Source.Mime;

    public long Size => Source.Size;

    public bool IsImage => Source.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] public partial AttachmentState State { get; internal set; } = AttachmentState.Uploading;

    /// <summary>已经上传的字节数。</summary>
    [ObservableProperty] public partial long Sent { get; internal set; }

    /// <summary>0 – 1;没有大小(0 字节)时为 0。</summary>
    public double Progress => Size > 0 ? Math.Clamp((double)Sent / Size, 0, 1) : 0;

    [ObservableProperty] public partial AttachmentFailure? Failure { get; internal set; }

    /// <summary>上传完成后设备给的 <c>ncw://</c> 引用和附件 id。</summary>
    public AttachmentDto? Uploaded { get; internal set; }

    public bool IsUploading => State == AttachmentState.Uploading;

    public bool IsDone => State == AttachmentState.Done;

    public bool IsFailed => State == AttachmentState.Failed;

    internal CancellationTokenSource? Cancel { get; set; }

    partial void OnStateChanged(AttachmentState value)
    {
        OnPropertyChanged(nameof(IsUploading));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsFailed));
    }

    partial void OnSentChanged(long value) => OnPropertyChanged(nameof(Progress));
}

/// <summary>
/// 输入框的附件托盘(手机:拍照 / 相册 / 文件)。选择由宿主的 <see cref="IAttachmentPicker"/> 完成;上传走 <c>attachments.upload.*</c>
/// (<see cref="IAttachmentUploader"/>,分块、带进度);上传完成的附件在发送消息时作为 <c>image</c> / <c>file</c> 块一起发出(<see cref="Ready"/>)。
/// 附件按会话各记一份(换会话再换回来还在);设备上的草稿 10 分钟内没提交会过期,之后重试即可。
/// </summary>
public sealed partial class AttachmentTrayViewModel : ObservableObject
{
    private readonly IAttachmentUploader? _uploader;
    private readonly Func<IAttachmentPicker?> _picker;
    private readonly Dictionary<SessionRef, List<AttachmentItemViewModel>> _bySession = [];
    private SessionRef? _session;

    public AttachmentTrayViewModel(IAttachmentUploader? uploader = null, Func<IAttachmentPicker?>? picker = null)
    {
        _uploader = uploader;
        _picker = picker ?? (() => null);
    }

    public ObservableCollection<AttachmentItemViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>有宿主选择器、且当前有会话:附件按钮才可用。</summary>
    public bool CanPick => _session is not null && _picker() is not null;

    public bool IsUploading => Items.Any(static i => i.IsUploading);

    public bool IsAvailable(AttachmentSource source) => _picker()?.IsAvailable(source) == true;

    /// <summary>已经上传完成、可以随消息发出的附件。</summary>
    public IReadOnlyList<InputFile> Ready() =>
        [.. Items.Where(static i => i.IsDone && i.Uploaded is not null).Select(static i => InputFile.Attachment(i.Uploaded!.Url, i.Uploaded.DisplayName, i.Uploaded.Mime))];

    /// <summary>会话变了(上层每次 Store 变化后调用,同一个会话重复调用无副作用)。</summary>
    public void SetSession(SessionRef? session)
    {
        if (_session == session)
        {
            return;
        }

        _session = session;
        Items.Clear();
        if (session is { } s && _bySession.TryGetValue(s, out var items))
        {
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }

        Changed();
    }

    /// <summary>选择来源 → 弹宿主选择器 → 逐个上传。用户取消选择什么也不做。</summary>
    [RelayCommand]
    private async Task PickAsync(AttachmentSource source)
    {
        if (_picker() is not { } picker || !picker.IsAvailable(source) || _session is null)
        {
            return;
        }

        var picked = await picker.PickAsync(source);
        foreach (var file in picked)
        {
            _ = AddAsync(file);
        }
    }

    /// <summary>加入一个已经选好的附件并开始上传(<see cref="PickAsync"/> 的下半截;也是测试入口)。返回的任务在上传结束(成功 / 失败 / 取消)时完成。</summary>
    public async Task AddAsync(PickedAttachment file)
    {
        if (_session is not { } session)
        {
            return;
        }

        var item = new AttachmentItemViewModel(file);
        Track(session, item);
        await UploadAsync(session, item);
    }

    /// <summary>失败后重试。</summary>
    [RelayCommand]
    private async Task RetryAsync(AttachmentItemViewModel item)
    {
        if (_session is { } session && item.IsFailed)
        {
            await UploadAsync(session, item);
        }
    }

    /// <summary>移除:上传中的先取消;已经传到设备上的草稿也一并移除(幂等)。</summary>
    [RelayCommand]
    private async Task RemoveAsync(AttachmentItemViewModel item)
    {
        item.Cancel?.Cancel();
        Items.Remove(item);
        foreach (var list in _bySession.Values)
        {
            list.Remove(item);
        }

        Changed();
        if (item.Uploaded is { } uploaded && _session is { } session && _uploader is not null)
        {
            await _uploader.RemoveAsync(session.DeviceId, uploaded.Id);
        }
    }

    /// <summary>消息发出去了:附件已经由设备提交,托盘清空(不删设备上的文件)。</summary>
    public void Clear()
    {
        if (_session is { } session)
        {
            _bySession.Remove(session);
        }

        Items.Clear();
        Changed();
    }

    private void Track(SessionRef session, AttachmentItemViewModel item)
    {
        if (!_bySession.TryGetValue(session, out var list))
        {
            _bySession[session] = list = [];
        }

        list.Add(item);
        if (_session == session)
        {
            Items.Add(item);
        }

        Changed();
    }

    private async Task UploadAsync(SessionRef session, AttachmentItemViewModel item)
    {
        item.Failure = null;
        item.Sent = 0;
        item.State = AttachmentState.Uploading;
        Changed();

        if (item.Size <= 0)
        {
            Fail(item, AttachmentFailure.Empty);
            return;
        }

        if (item.Size > Attachments.MaxBytes)
        {
            Fail(item, AttachmentFailure.TooLarge); // 不读文件、不发请求
            return;
        }

        if (_uploader is null)
        {
            Fail(item, AttachmentFailure.DeviceUnavailable);
            return;
        }

        var cts = item.Cancel = new CancellationTokenSource();
        try
        {
            byte[] bytes;
            try
            {
                bytes = await item.Source.ReadAllAsync(cts.Token);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Fail(item, AttachmentFailure.ReadFailed);
                return;
            }

            // 进度回调可能在线程池线程:回到创建这次上传的同步上下文(UI 线程)再改 ViewModel
            var progress = new UiProgress(SynchronizationContext.Current, sent => item.Sent = sent);
            var result = await _uploader.UploadAsync(session.DeviceId, session.SessionId, item.Name, item.Mime, bytes, progress, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return; // 用户移除了它
            }

            if (result.IsOk && result.Value is { } uploaded)
            {
                item.Uploaded = uploaded;
                item.Sent = item.Size;
                item.State = AttachmentState.Done;
                Changed();
            }
            else
            {
                Fail(item, result.Outcome switch
                {
                    ActionOutcome.Forbidden => AttachmentFailure.Forbidden,
                    ActionOutcome.Unavailable => AttachmentFailure.DeviceUnavailable,
                    _ => AttachmentFailure.Rejected,
                });
            }
        }
        catch (OperationCanceledException)
        {
            // 用户移除了它:托盘里已经没有这一项
        }
        finally
        {
            item.Cancel = null;
            cts.Dispose();
        }
    }

    private void Fail(AttachmentItemViewModel item, AttachmentFailure failure)
    {
        item.Failure = failure;
        item.State = AttachmentState.Failed;
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CanPick));
        OnPropertyChanged(nameof(IsUploading));
    }

    private sealed class UiProgress(SynchronizationContext? context, Action<long> apply) : IProgress<long>
    {
        public void Report(long value)
        {
            if (context is null)
            {
                apply(value);
            }
            else
            {
                context.Post(_ => apply(value), null);
            }
        }
    }
}
