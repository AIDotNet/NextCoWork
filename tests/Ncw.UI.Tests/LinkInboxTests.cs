using Ncw.UI;

namespace Ncw.UI.Tests;

/// <summary>系统转来的链接的收件箱:没有接收方时排队、接上后按顺序补发;激活请求不排队;内容原样交给接收方(解析和白名单不在这里)。</summary>
[Collection("NcwLinkInbox")]
public sealed class LinkInboxTests : IDisposable
{
    public LinkInboxTests() => NcwLinkInbox.Detach();

    public void Dispose() => NcwLinkInbox.Detach();

    [Fact]
    public void Links_that_arrive_before_the_app_is_ready_are_replayed_in_order_when_it_attaches()
    {
        NcwLinkInbox.Submit("link nextcowork://auth/callback?code=a&state=1");
        NcwLinkInbox.Submit("activate"); // 没有接收方:激活请求不排队(没有窗口可激活)
        NcwLinkInbox.Submit("link nextcowork://pair?invite=x");

        var links = new List<string>();
        var activations = 0;
        NcwLinkInbox.Attach(links.Add, () => activations++);
        Assert.Equal(["nextcowork://auth/callback?code=a&state=1", "nextcowork://pair?invite=x"], links);
        Assert.Equal(0, activations);

        NcwLinkInbox.Submit("link nextcowork://auth/callback?code=b&state=2");
        Assert.Equal("nextcowork://auth/callback?code=b&state=2", links[^1]);
        Assert.Equal(1, activations); // 收到链接时顺带把窗口带到前台
        NcwLinkInbox.Submit("activate");
        Assert.Equal(2, activations);
    }

    [Fact]
    public void The_backlog_is_bounded_and_unknown_messages_are_ignored()
    {
        for (var i = 0; i < 40; i++)
        {
            NcwLinkInbox.Submit("link nextcowork://open?n=" + i);
        }

        NcwLinkInbox.Submit("rm -rf /");
        NcwLinkInbox.Submit("");

        var links = new List<string>();
        NcwLinkInbox.Attach(links.Add, () => { });
        Assert.Equal(16, links.Count);
        Assert.Equal("nextcowork://open?n=24", links[0]); // 丢最旧的
        Assert.Equal("nextcowork://open?n=39", links[^1]);
    }

    [Fact]
    public void Detaching_stops_delivery_and_clears_the_backlog()
    {
        var links = new List<string>();
        NcwLinkInbox.Attach(links.Add, () => { });
        NcwLinkInbox.Detach();
        NcwLinkInbox.Submit("link nextcowork://pair?invite=x"); // 没人接:排队
        var again = new List<string>();
        NcwLinkInbox.Attach(again.Add, () => { });
        Assert.Empty(links);
        Assert.Equal(["nextcowork://pair?invite=x"], again);
    }
}
