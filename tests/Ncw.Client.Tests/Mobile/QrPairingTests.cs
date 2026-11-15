using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Security;

namespace Ncw.Client.Tests.Mobile;

/// <summary>扫码配对的状态机:权限 → 扫描 → 解析(PairingQr.TryParse)→ 确认 → 配对(DeviceActions.PairWithQrAsync)→ 成功 / 失败;时间可注入。</summary>
public class QrPairingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private static string Qr(DateTimeOffset expires, string name = "mac")
    {
        var identity = PeerIdentity.Create(name);
        return new PairingQr(identity.DeviceId, identity.Fingerprint, ["192.168.1.23:7810", "[fe80::1]:7810"], new byte[PairingQr.TokenLength], expires.ToUnixTimeMilliseconds()).ToUri();
    }

    private static (QrPairingViewModel Vm, FakeScanner Scanner, ManualClock Clock, List<string> Paired) Make(PairOutcome outcome = PairOutcome.Paired, FakeScanner? scanner = null)
    {
        var clock = new ManualClock(T0);
        scanner ??= new FakeScanner();
        var paired = new List<string>();
        var vm = new QrPairingViewModel(() => scanner, (content, _) =>
        {
            paired.Add(content);
            return Task.FromResult(outcome);
        }, clock);
        return (vm, scanner, clock, paired);
    }

    [Fact]
    public async Task Scanning_a_valid_code_parses_it_and_waits_for_confirmation_then_pairs()
    {
        var (vm, scanner, clock, paired) = Make();
        var code = Qr(clock.GetUtcNow().AddMinutes(10));

        var scan = vm.StartScanCommand.ExecuteAsync(null);
        Assert.Equal(QrPairingPhase.Scanning, vm.Phase);
        scanner.Read(code);
        await scan;

        Assert.Equal(QrPairingPhase.Parsed, vm.Phase);
        Assert.Equal(["192.168.1.23:7810", "[fe80::1]:7810"], vm.Device!.Addresses);
        Assert.Empty(paired); // 解析成功不会自动配对

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(QrPairingPhase.Paired, vm.Phase);
        Assert.Equal([code], paired);
        Assert.Null(vm.Failure);
    }

    [Fact]
    public async Task A_code_that_is_not_a_pairing_code_fails_as_invalid_and_never_reaches_the_pairing_call()
    {
        var (vm, scanner, _, paired) = Make();
        var scan = vm.StartScanCommand.ExecuteAsync(null);
        scanner.Read("https://example.com/not-ours");
        await scan;

        Assert.Equal(QrPairingPhase.Failed, vm.Phase);
        Assert.Equal(QrPairingFailure.InvalidCode, vm.Failure);
        Assert.Empty(paired);
    }

    [Fact]
    public void A_code_past_its_expiry_fails_as_expired_judged_by_the_injected_clock()
    {
        var (vm, _, clock, paired) = Make();
        var code = Qr(clock.GetUtcNow().AddMinutes(10));

        clock.Advance(TimeSpan.FromMinutes(11));
        vm.Submit(code);

        Assert.Equal(QrPairingFailure.Expired, vm.Failure);
        Assert.Empty(paired);
    }

    [Fact]
    public async Task A_code_that_expires_between_scanning_and_confirming_is_rejected_by_the_pairing_intent_not_silently_sent()
    {
        var (vm, _, clock, _) = Make(PairOutcome.QrExpired);
        vm.Submit(Qr(clock.GetUtcNow().AddMinutes(10)));

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(QrPairingPhase.Failed, vm.Phase);
        Assert.Equal(QrPairingFailure.Expired, vm.Failure);
    }

    [Theory]
    [InlineData(PairOutcome.QrRejected)]
    [InlineData(PairOutcome.Unreachable)]
    [InlineData(PairOutcome.DeviceNotVerified)]
    [InlineData(PairOutcome.RemoteDisabled)]
    public async Task Other_pairing_outcomes_fail_with_the_outcome_kept_for_the_message(PairOutcome outcome)
    {
        var (vm, _, clock, _) = Make(outcome);
        vm.Submit(Qr(clock.GetUtcNow().AddMinutes(10)));

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(QrPairingFailure.PairingFailed, vm.Failure);
        Assert.Equal(outcome, vm.Outcome);
    }

    [Theory]
    [InlineData(CameraAccess.Denied, QrPairingFailure.CameraDenied)]
    [InlineData(CameraAccess.Restricted, QrPairingFailure.CameraRestricted)]
    [InlineData(CameraAccess.NoCamera, QrPairingFailure.ScannerUnavailable)]
    public async Task Camera_permission_problems_fail_without_scanning_and_the_paste_entry_still_works(CameraAccess access, QrPairingFailure expected)
    {
        var scanner = new FakeScanner(access: access);
        var (vm, _, clock, paired) = Make(scanner: scanner);

        await vm.StartScanCommand.ExecuteAsync(null);

        Assert.Equal(expected, vm.Failure);
        Assert.Equal(0, scanner.Scans);
        Assert.True(vm.ShowsPaste);

        vm.PastedText = Qr(clock.GetUtcNow().AddMinutes(10));
        vm.SubmitPastedCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(QrPairingPhase.Paired, vm.Phase);
        Assert.Single(paired);
    }

    [Fact]
    public async Task Without_a_scanner_the_only_entry_is_pasting_and_starting_a_scan_says_so()
    {
        var clock = new ManualClock(T0);
        var vm = new QrPairingViewModel(() => null, (_, _) => Task.FromResult(PairOutcome.Paired), clock);

        Assert.False(vm.CanScan);
        Assert.False(vm.CanStartScan);
        await vm.StartScanCommand.ExecuteAsync(null);
        Assert.Equal(QrPairingFailure.ScannerUnavailable, vm.Failure);
    }

    [Fact]
    public async Task Resetting_while_scanning_cancels_the_scan_and_returns_to_idle()
    {
        var (vm, scanner, _, _) = Make();
        var scan = vm.StartScanCommand.ExecuteAsync(null);
        Assert.True(vm.IsScanning);

        vm.ResetCommand.Execute(null);
        await scan;

        Assert.Equal(QrPairingPhase.Idle, vm.Phase);
        Assert.Equal(1, scanner.Scans);
        Assert.Null(vm.Device);
    }

    [Fact]
    public void The_phase_flags_the_view_binds_follow_the_phase()
    {
        var (vm, _, clock, _) = Make();
        Assert.True(vm.ShowsScanner);
        Assert.True(vm.ShowsPaste);

        vm.Submit(Qr(clock.GetUtcNow().AddMinutes(10)));

        Assert.True(vm.IsParsed);
        Assert.False(vm.ShowsScanner);
        Assert.False(vm.ShowsPaste);
        Assert.DoesNotContain("DEV_", vm.Device!.ShortId); // 不显示 dev_ 前缀,只给 4+4 个字符帮用户核对
    }

    [Fact]
    public async Task A_second_confirm_while_pairing_or_after_it_is_ignored()
    {
        var (vm, _, clock, paired) = Make();
        vm.Submit(Qr(clock.GetUtcNow().AddMinutes(10)));

        await vm.ConfirmCommand.ExecuteAsync(null);
        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Single(paired);
    }

    // ───────────────────────── DeviceActions.PairWithQrAsync(唯一的配对入口)─────────────────────────

    [Fact]
    public async Task The_pairing_intent_rejects_garbage_and_expired_codes_before_any_network_request()
    {
        var clock = new ManualClock(T0);
        var actions = new DeviceActions(new Ncw.Client.Devices.DeviceRegistry(), clock);

        Assert.Equal(PairOutcome.QrInvalid, await actions.PairWithQrAsync("nope"));
        Assert.Equal(PairOutcome.QrExpired, await actions.PairWithQrAsync(Qr(T0.AddMinutes(-1))));
        Assert.Equal(PairOutcome.KeyStoreUnavailable, await actions.PairWithQrAsync(Qr(T0.AddMinutes(10)))); // 宿主没有远程设备管理器
    }

    [Fact]
    public async Task The_pairing_intent_reports_an_unreachable_device_when_no_address_in_the_code_connects()
    {
        var stores = new Ncw.Client.Stores.ClientStores();
        var clock = new ManualClock(T0);
        var devices = new Ncw.Client.Devices.DeviceRegistry
        {
            Remote = new Ncw.Client.Devices.RemoteDeviceManager(stores, Ncw.Client.Devices.InlineDispatcher.Instance, new Ncw.Client.Services.EphemeralControllerIdentity(), new Ncw.Client.Services.MemoryDeviceBook(), "tests", clock),
        };
        var actions = new DeviceActions(devices, clock);
        var identity = PeerIdentity.Create("mac");
        var code = new PairingQr(identity.DeviceId, identity.Fingerprint, ["127.0.0.1:1"], new byte[PairingQr.TokenLength], T0.AddMinutes(10).ToUnixTimeMilliseconds()).ToUri();

        Assert.Equal(PairOutcome.Unreachable, await actions.PairWithQrAsync(code));
    }
}
