using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Protocol.Security;

namespace Ncw.Client.ViewModels;

/// <summary>扫码配对流程的阶段(界面按它画页面,文字由界面生成)。</summary>
public enum QrPairingPhase
{
    /// <summary>还没开始(或重置后):显示「开始扫描」和「粘贴二维码内容」。</summary>
    Idle,

    /// <summary>正在向系统要相机权限。</summary>
    RequestingAccess,

    /// <summary>相机预览中,等一个二维码。</summary>
    Scanning,

    /// <summary>读到了合法的二维码,等用户确认「配对」(显示设备 id 和地址)。</summary>
    Parsed,

    /// <summary>正在配对(连接设备、出示一次性密钥)。</summary>
    Pairing,

    /// <summary>配对成功。</summary>
    Paired,

    /// <summary>失败,原因见 <see cref="QrPairingViewModel.Failure"/>。</summary>
    Failed,
}

/// <summary>扫码配对失败的原因。</summary>
public enum QrPairingFailure
{
    /// <summary>用户拒绝了相机权限。</summary>
    CameraDenied,

    /// <summary>系统限制相机。</summary>
    CameraRestricted,

    /// <summary>这个设备没有相机 / 宿主没有实现扫码(只能粘贴)。</summary>
    ScannerUnavailable,

    /// <summary>读到的不是 NextCoWork 的配对二维码。</summary>
    InvalidCode,

    /// <summary>二维码已过期。</summary>
    Expired,

    /// <summary>配对没成功,具体见 <see cref="QrPairingViewModel.Outcome"/>。</summary>
    PairingFailed,
}

/// <summary>
/// 扫码配对(docs/design/09 §3、05 M5):相机预览与解码是宿主的 <see cref="IQrScanner"/>,这里是之后的状态机——
/// 申请权限 → 扫描 → 解析(<c>PairingQr.TryParse</c>)→ 确认 → 配对(<see cref="DeviceActions.PairWithQrAsync"/>,唯一的入口)→ 成功 / 失败。
/// 没有扫码能力、被拒绝相机权限时可以「粘贴二维码内容」走同一条解析和配对路径。时间(二维码过期判断)可注入。
/// </summary>
public sealed partial class QrPairingViewModel : ObservableObject
{
    private readonly Func<IQrScanner?> _scanner;
    private readonly Func<string, CancellationToken, Task<PairOutcome>> _pair;
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _scanning;
    private string? _content;

    public QrPairingViewModel(Func<IQrScanner?> scanner, Func<string, CancellationToken, Task<PairOutcome>> pair, TimeProvider? clock = null)
    {
        _scanner = scanner;
        _pair = pair;
        _clock = clock ?? TimeProvider.System;
    }

    [ObservableProperty] public partial QrPairingPhase Phase { get; private set; }

    [ObservableProperty] public partial QrPairingFailure? Failure { get; private set; }

    /// <summary>Failure 为 <see cref="QrPairingFailure.PairingFailed"/> 时设备返回的结局。</summary>
    [ObservableProperty] public partial PairOutcome? Outcome { get; private set; }

    /// <summary>解析成功的二维码(<see cref="QrPairingPhase.Parsed"/> 起有值;只含设备 id、地址和过期时间,密钥不展示)。</summary>
    [ObservableProperty] public partial QrDeviceInfo? Device { get; private set; }

    /// <summary>「粘贴二维码内容」输入框的草稿(纯 UI 状态)。</summary>
    [ObservableProperty] public partial string PastedText { get; set; } = "";

    /// <summary>平台有扫码能力(界面决定显示「开始扫描」还是只显示粘贴入口)。</summary>
    public bool CanScan => _scanner() is { IsSupported: true };

    public bool IsBusy => Phase is QrPairingPhase.RequestingAccess or QrPairingPhase.Pairing;

    /// <summary>显示相机预览区(开始前、要权限、扫描中、失败后都留着,方便重试)。</summary>
    public bool ShowsScanner => Phase is QrPairingPhase.Idle or QrPairingPhase.RequestingAccess or QrPairingPhase.Scanning or QrPairingPhase.Failed;

    /// <summary>显示「粘贴二维码内容」备用入口(确认配对之前一直可用,没有相机的平台这是唯一入口)。</summary>
    public bool ShowsPaste => Phase is QrPairingPhase.Idle or QrPairingPhase.Scanning or QrPairingPhase.Failed;

    public bool IsScanning => Phase == QrPairingPhase.Scanning;

    public bool IsRequestingAccess => Phase == QrPairingPhase.RequestingAccess;

    public bool IsParsed => Phase == QrPairingPhase.Parsed;

    public bool IsPairing => Phase == QrPairingPhase.Pairing;

    public bool IsPaired => Phase == QrPairingPhase.Paired;

    public bool IsFailed => Phase == QrPairingPhase.Failed;

    /// <summary>可以点「开始扫描」(有相机、没在忙、没在确认)。</summary>
    public bool CanStartScan => CanScan && Phase == QrPairingPhase.Idle;

    /// <summary>开始扫描:先要相机权限,再等二维码;读到后进入解析。被 <see cref="Reset"/> / 离开页面取消。</summary>
    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (_scanner() is not { IsSupported: true } scanner)
        {
            Fail(QrPairingFailure.ScannerUnavailable);
            return;
        }

        Cancel();
        var cts = _scanning = new CancellationTokenSource();
        Phase = QrPairingPhase.RequestingAccess;
        Failure = null;
        var access = await scanner.RequestAccessAsync(cts.Token);
        if (cts.IsCancellationRequested)
        {
            return;
        }

        switch (access)
        {
            case CameraAccess.Denied:
                Fail(QrPairingFailure.CameraDenied);
                return;
            case CameraAccess.Restricted:
                Fail(QrPairingFailure.CameraRestricted);
                return;
            case CameraAccess.NoCamera:
                Fail(QrPairingFailure.ScannerUnavailable);
                return;
        }

        Phase = QrPairingPhase.Scanning;
        string? content;
        try
        {
            content = await scanner.ScanAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            content = null;
        }

        if (cts.IsCancellationRequested || content is null)
        {
            if (Phase == QrPairingPhase.Scanning)
            {
                Phase = QrPairingPhase.Idle;
            }

            return;
        }

        Submit(content);
    }

    /// <summary>用户粘贴了二维码内容(备用入口)。</summary>
    [RelayCommand]
    private void SubmitPasted() => Submit(PastedText);

    /// <summary>扫到(或粘贴了)一段内容:解析并检查有没有过期,通过就等用户确认。</summary>
    public void Submit(string content)
    {
        Cancel();
        _content = null;
        Device = null;
        if (!PairingQr.TryParse(content.Trim(), out var qr))
        {
            Fail(QrPairingFailure.InvalidCode);
            return;
        }

        if (_clock.GetUtcNow().ToUnixTimeMilliseconds() >= qr.ExpiresAt)
        {
            Fail(QrPairingFailure.Expired);
            return;
        }

        _content = content.Trim();
        Device = new QrDeviceInfo(qr.DeviceId, [.. qr.Addresses], DateTimeOffset.FromUnixTimeMilliseconds(qr.ExpiresAt));
        Failure = null;
        Outcome = null;
        Phase = QrPairingPhase.Parsed;
    }

    /// <summary>确认配对。</summary>
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (Phase != QrPairingPhase.Parsed || _content is not { } content)
        {
            return;
        }

        Phase = QrPairingPhase.Pairing;
        var outcome = await _pair(content, CancellationToken.None);
        Outcome = outcome;
        switch (outcome)
        {
            case PairOutcome.Paired:
                Failure = null;
                Phase = QrPairingPhase.Paired;
                break;
            case PairOutcome.QrInvalid:
                Fail(QrPairingFailure.InvalidCode);
                break;
            case PairOutcome.QrExpired:
                Fail(QrPairingFailure.Expired);
                break;
            default:
                Fail(QrPairingFailure.PairingFailed);
                break;
        }
    }

    /// <summary>回到开始(失败后重试、成功后再添加一台、离开页面)。取消进行中的扫描。</summary>
    [RelayCommand]
    private void Reset()
    {
        Cancel();
        _content = null;
        Device = null;
        Failure = null;
        Outcome = null;
        PastedText = "";
        Phase = QrPairingPhase.Idle;
    }

    private void Fail(QrPairingFailure failure)
    {
        Failure = failure;
        Phase = QrPairingPhase.Failed;
    }

    private void Cancel()
    {
        _scanning?.Cancel();
        _scanning = null;
    }

    partial void OnPhaseChanged(QrPairingPhase value)
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(ShowsScanner), nameof(ShowsPaste), nameof(IsScanning), nameof(IsRequestingAccess), nameof(IsParsed), nameof(IsPairing), nameof(IsPaired), nameof(IsFailed), nameof(CanStartScan) })
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>二维码里的设备信息(界面确认用)。<see cref="DeviceId"/> 是设备公钥指纹派生的 id,可以让用户和桌面上显示的核对。</summary>
public sealed record QrDeviceInfo(string DeviceId, IReadOnlyList<string> Addresses, DateTimeOffset ExpiresAt)
{
    /// <summary>设备 id 前 8 位,四位一组(界面上帮用户辨认)。</summary>
    public string ShortId
    {
        get
        {
            var id = DeviceId.StartsWith("dev_", StringComparison.Ordinal) ? DeviceId[4..] : DeviceId;
            return id.Length >= 8 ? $"{id[..4]} {id[4..8]}".ToUpperInvariant() : id;
        }
    }
}
