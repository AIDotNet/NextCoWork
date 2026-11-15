namespace Ncw.UI;

/// <summary>
/// 系统转给应用的 <c>nextcowork://</c> 链接和「回到前台」请求的收件箱。宿主(桌面)的单实例转发线程和启动参数把消息投进来(任意线程),
/// 应用装配好链接路由之后 <see cref="Attach"/>:此前收到的消息按顺序补发。消息格式是 <c>link &lt;uri&gt;</c> 或 <c>activate</c>(见 <c>SingleInstance</c>);
/// 内容<b>不可信</b>,这里只做分发,解析和白名单在 <c>NcwLinkParser</c>。
/// </summary>
public static class NcwLinkInbox
{
    private const string LinkPrefix = "link ";
    private const int MaxPending = 16;

    private static readonly object Gate = new();
    private static readonly Queue<string> Pending = new();
    private static Action<string>? _onLink;
    private static Action? _onActivate;

    /// <summary>投递一条消息(任意线程)。没有接收方时先排队(最多 16 条,多的丢最旧的)。</summary>
    public static void Submit(string message)
    {
        Action<string>? link;
        Action? activate;
        lock (Gate)
        {
            link = _onLink;
            activate = _onActivate;
            if (link is null && message.StartsWith(LinkPrefix, StringComparison.Ordinal))
            {
                Pending.Enqueue(message[LinkPrefix.Length..]);
                while (Pending.Count > MaxPending)
                {
                    Pending.Dequeue();
                }

                return;
            }
        }

        if (message.StartsWith(LinkPrefix, StringComparison.Ordinal))
        {
            link?.Invoke(message[LinkPrefix.Length..]);
            activate?.Invoke();
        }
        else if (message == "activate")
        {
            activate?.Invoke();
        }
    }

    /// <summary>接上接收方;先把排着队的链接按顺序交给它。</summary>
    public static void Attach(Action<string> onLink, Action onActivate)
    {
        string[] backlog;
        lock (Gate)
        {
            _onLink = onLink;
            _onActivate = onActivate;
            backlog = [.. Pending];
            Pending.Clear();
        }

        foreach (var link in backlog)
        {
            onLink(link);
        }
    }

    /// <summary>断开接收方(应用退出、测试清理)。</summary>
    public static void Detach()
    {
        lock (Gate)
        {
            _onLink = null;
            _onActivate = null;
            Pending.Clear();
        }
    }
}
