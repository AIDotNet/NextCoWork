using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

public class WidgetViewTests
{
    private static WidgetFacts Facts(WidgetCall call = WidgetCall.Done, bool card = true, bool interactive = false, bool submitted = false, bool awaiting = false, bool unknown = false) =>
        new(call, card, interactive, submitted, awaiting, unknown);

    [Theory]
    [InlineData(WidgetCall.Running, WidgetPhase.Loading)]
    [InlineData(WidgetCall.Done, WidgetPhase.Failed)]
    [InlineData(WidgetCall.Failed, WidgetPhase.Failed)]
    [InlineData(WidgetCall.Interrupted, WidgetPhase.Expired)]
    public void Without_a_card_the_call_state_decides(WidgetCall call, WidgetPhase expected) =>
        Assert.Equal(expected, WidgetView.Phase(Facts(call, card: false)));

    [Fact]
    public void A_disconnected_device_makes_a_generating_card_unknown_not_loading_and_not_failed()
    {
        Assert.Equal(WidgetPhase.Unknown, WidgetView.Phase(Facts(WidgetCall.Running, card: false, unknown: true)));
        Assert.Equal(WidgetPhase.Unknown, WidgetView.Phase(Facts(WidgetCall.Running, interactive: true, awaiting: true, unknown: true)));
    }

    [Fact]
    public void A_finished_static_card_stays_ready_when_the_device_drops()
    {
        // 内容是定稿的:断线不影响看,只是桥不能再调设备
        var facts = Facts(unknown: true);
        Assert.Equal(WidgetPhase.Ready, WidgetView.Phase(facts));
        Assert.False(WidgetView.CanCallDevice(facts));
    }

    [Fact]
    public void An_interactive_card_is_ready_while_waiting_and_expires_when_the_window_closes_without_a_value()
    {
        Assert.Equal(WidgetPhase.Ready, WidgetView.Phase(Facts(WidgetCall.Running, interactive: true, awaiting: true)));
        Assert.True(WidgetView.CanSubmit(Facts(WidgetCall.Running, interactive: true, awaiting: true)));

        // 刚登记 / 刚被答掉的瞬间,调用还在跑:不闪「已过期」
        Assert.Equal(WidgetPhase.Ready, WidgetView.Phase(Facts(WidgetCall.Running, interactive: true, awaiting: false)));

        var closed = Facts(WidgetCall.Done, interactive: true, awaiting: false);
        Assert.Equal(WidgetPhase.Expired, WidgetView.Phase(closed));
        Assert.False(WidgetView.CanSubmit(closed));
        Assert.Equal(WidgetPhase.Expired, WidgetView.Phase(Facts(WidgetCall.Interrupted, interactive: true)));
    }

    [Fact]
    public void A_submitted_value_is_terminal_and_beats_everything_else()
    {
        // 规则 6:提交值进了 Details,重放的旧事件(调用还在跑、交互还挂着)改不回去
        var facts = Facts(WidgetCall.Running, interactive: true, submitted: true, awaiting: true, unknown: true);
        Assert.Equal(WidgetPhase.Ready, WidgetView.Phase(facts));
        Assert.False(WidgetView.CanSubmit(facts));
    }

    [Fact]
    public void Submitting_needs_a_live_device_and_a_pending_interaction()
    {
        Assert.False(WidgetView.CanSubmit(Facts(interactive: false, awaiting: true)));
        Assert.False(WidgetView.CanSubmit(Facts(WidgetCall.Running, interactive: true, awaiting: true, unknown: true)));
        Assert.False(WidgetView.CanSubmit(Facts(WidgetCall.Running, card: false, interactive: true, awaiting: true)));
    }

    [Fact]
    public void A_failed_call_cannot_reach_the_device_but_an_expired_card_still_can()
    {
        Assert.False(WidgetView.CanCallDevice(Facts(WidgetCall.Failed)));
        Assert.True(WidgetView.CanCallDevice(Facts(WidgetCall.Done, interactive: true)));
    }
}
