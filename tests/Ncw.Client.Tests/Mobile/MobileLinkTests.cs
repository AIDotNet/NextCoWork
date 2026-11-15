using Ncw.Client.Mock;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Security;

namespace Ncw.Client.Tests.Mobile;

/// <summary>手机壳收到的 <c>nextcowork://</c> 链接:白名单、畸形输入、配对二维码只进入「待确认」而不自动配对。</summary>
public class MobileLinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private static string Qr(DateTimeOffset expires)
    {
        var identity = PeerIdentity.Create("mac");
        return new PairingQr(identity.DeviceId, identity.Fingerprint, ["10.0.2.2:7810"], new byte[PairingQr.TokenLength], expires.ToUnixTimeMilliseconds()).ToUri();
    }

    [Fact]
    public void A_pairing_qr_uri_is_a_pair_link()
    {
        var uri = Qr(Now.AddMinutes(5));

        Assert.Equal(new PairQrMobileLink(uri), MobileLinkParser.Parse(uri));
    }

    [Fact]
    public void A_pair_uri_with_a_bad_fingerprint_is_rejected()
    {
        var uri = Qr(Now.AddMinutes(5)).Replace("&d=", "&d=x", StringComparison.Ordinal);

        Assert.Null(MobileLinkParser.Parse(uri));
    }

    [Fact]
    public void The_inbox_link_carries_only_opaque_token_fields()
    {
        var link = MobileLinkParser.Parse("nextcowork://inbox?d=dev_abc123&k=approval&id=i-77");

        Assert.Equal(new InboxMobileLink("dev_abc123", "approval", "i-77"), link);
        Assert.Equal(new InboxMobileLink("dev_abc123", null, null), MobileLinkParser.Parse("nextcowork://inbox?d=dev_abc123"));
    }

    [Theory]
    [InlineData("nextcowork://inbox")]
    [InlineData("nextcowork://inbox?k=approval")]
    [InlineData("nextcowork://inbox?d=a&d=b")]
    [InlineData("nextcowork://inbox?d=a&extra=1")]
    [InlineData("nextcowork://inbox?d=a%2Fb")]
    [InlineData("nextcowork://inbox?d=a b")]
    [InlineData("nextcowork://inbox?d=a#frag")]
    [InlineData("nextcowork://user@inbox?d=a")]
    [InlineData("nextcowork://inbox:99?d=a")]
    [InlineData("nextcowork://inbox/extra?d=a")]
    [InlineData("other://inbox?d=a")]
    [InlineData("nextcowork://open?p=1")]
    [InlineData("nextcowork://pair?invite=abc")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_outside_the_whitelist_is_ignored(string? raw) => Assert.Null(MobileLinkParser.Parse(raw));

    [Fact]
    public void Oversized_links_are_ignored()
    {
        Assert.Null(MobileLinkParser.Parse("nextcowork://inbox?d=" + new string('a', 5000)));
        Assert.Null(MobileLinkParser.Parse("nextcowork://inbox?d=" + new string('a', 129)));
    }

    [Fact]
    public void The_auth_callback_uses_the_strict_desktop_parser()
    {
        Assert.IsType<AuthCallbackMobileLink>(MobileLinkParser.Parse("nextcowork://auth/callback?code=c&state=s"));
        Assert.Null(MobileLinkParser.Parse("nextcowork://auth/callback?code=c")); // 缺 state
    }

    private static ShellViewModel Shell() => new(MockWorkspace.Create(Now), new FixedClock(Now));

    [Fact]
    public async Task A_pair_link_opens_the_qr_page_and_waits_for_confirmation()
    {
        var shell = Shell();

        var outcome = await new MobileLinkHandler(shell).HandleAsync(Qr(Now.AddMinutes(5)));

        Assert.Equal(MobileLinkOutcome.Handled, outcome);
        Assert.Equal(MobileTab.Devices, shell.Navigation.SelectedTab);
        Assert.IsType<QrPairRoute>(shell.Navigation.Top);
        Assert.Equal(QrPairingPhase.Parsed, shell.QrPairing.Phase); // 只到「待确认」,没有配对
        Assert.Equal("10.0.2.2:7810", shell.QrPairing.Device!.Addresses.Single());
    }

    [Fact]
    public async Task A_second_pair_link_reuses_the_open_qr_page()
    {
        var shell = Shell();
        var handler = new MobileLinkHandler(shell);

        await handler.HandleAsync(Qr(Now.AddMinutes(5)));
        await handler.HandleAsync(Qr(Now.AddMinutes(6)));

        Assert.Single(shell.Navigation.StackOf(MobileTab.Devices));
    }

    [Fact]
    public async Task An_expired_pair_link_lands_on_the_qr_page_as_a_failure()
    {
        var shell = Shell();

        await new MobileLinkHandler(shell).HandleAsync(Qr(Now.AddMinutes(-1)));

        Assert.Equal(QrPairingPhase.Failed, shell.QrPairing.Phase);
        Assert.Equal(QrPairingFailure.Expired, shell.QrPairing.Failure);
    }

    [Fact]
    public async Task The_inbox_link_selects_the_inbox_tab()
    {
        var shell = Shell();
        shell.Navigation.SelectTabCommand.Execute(MobileTab.Settings);

        var outcome = await new MobileLinkHandler(shell).HandleAsync("nextcowork://inbox?d=dev_x&k=approval&id=1");

        Assert.Equal(MobileLinkOutcome.Handled, outcome);
        Assert.Equal(MobileTab.Inbox, shell.Navigation.SelectedTab);
    }

    [Fact]
    public async Task The_auth_callback_is_reported_as_not_wired_when_the_shell_has_no_account_service()
    {
        var shell = Shell();

        var outcome = await new MobileLinkHandler(shell).HandleAsync("nextcowork://auth/callback?code=c&state=s");

        Assert.Equal(MobileLinkOutcome.AccountNotWired, outcome);
    }

    [Fact]
    public async Task Unknown_links_change_nothing()
    {
        var shell = Shell();

        var outcome = await new MobileLinkHandler(shell).HandleAsync("nextcowork://evil?x=1");

        Assert.Equal(MobileLinkOutcome.Ignored, outcome);
        Assert.Equal(MobileTab.Inbox, shell.Navigation.SelectedTab);
        Assert.Equal(QrPairingPhase.Idle, shell.QrPairing.Phase);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
