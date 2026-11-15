using Ncw.Client.Accounts;
using Ncw.Protocol.Security;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 手机壳从系统收到的 <c>nextcowork://</c> 链接(docs/design/11 §3)。和桌面的 <see cref="NcwLink"/> 的区别:手机还要处理
/// <b>配对二维码本身</b>(<c>nextcowork://pair?v=1&amp;d=…</c>,用系统相机扫到后跳进来)和通知打开的 <c>inbox</c> 链接。
/// 链接来自系统和别的程序,<b>不可信</b>:这里只做结构检查,任何有副作用的动作(配对、登录)都要用户确认或由 state 校验兜底。
/// </summary>
public abstract record MobileLink;

/// <summary>配对二维码的内容(已通过 <see cref="PairingQr.TryParse"/>)。只导航到扫码页并填入「待确认」,<b>不会自动配对</b>。</summary>
public sealed record PairQrMobileLink(string Content) : MobileLink;

/// <summary><c>nextcowork://inbox?d=&lt;deviceId&gt;&amp;k=&lt;kind&gt;&amp;id=&lt;opaqueId&gt;</c>:通知点击后打开收件箱(推送内容只有这三个不透明字段,09 §10)。</summary>
public sealed record InboxMobileLink(string DeviceId, string? Kind, string? OpaqueId) : MobileLink;

/// <summary><c>nextcowork://auth/callback?…</c>:登录回调,交给账户登录流程(<see cref="LinkRouter"/>)。</summary>
public sealed record AuthCallbackMobileLink(string Raw) : MobileLink;

/// <summary>手机壳对一条链接的处理结果。</summary>
public enum MobileLinkOutcome
{
    /// <summary>不在白名单里 / 畸形:什么也没做。</summary>
    Ignored,

    /// <summary>已经按链接导航(配对页 / 收件箱)或交给了账户流程。</summary>
    Handled,

    /// <summary>是登录回调,但这个壳里的账户服务还没接线(手机壳目前没有账户页):忽略,不改任何状态。</summary>
    AccountNotWired,
}

/// <summary>手机链接解析(纯函数)。白名单:配对二维码、<c>inbox</c>、<c>auth/callback</c>;其他一律 null。</summary>
public static class MobileLinkParser
{
    public const int MaxLength = NcwLinkParser.MaxLength;

    public static MobileLink? Parse(string? raw)
    {
        if (!Plausible(raw))
        {
            return null;
        }

        if (PairingQr.TryParse(raw, out _))
        {
            return new PairQrMobileLink(raw);
        }

        if (TryParseInbox(raw) is { } inbox)
        {
            return inbox;
        }

        // 登录回调沿用桌面的严格解析(scheme、userinfo、端口、片段、重复参数、百分号编码)
        return NcwLinkParser.Parse(raw) is AuthCallbackLink ? new AuthCallbackMobileLink(raw) : null;
    }

    private static bool Plausible([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? raw) =>
        !string.IsNullOrEmpty(raw) && raw.Length <= MaxLength && !raw.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c));

    /// <summary>解析 <c>nextcowork://inbox?…</c>;其他返回 null。值只允许 token 字符(字母数字 <c>_ - .</c>),不做百分号解码。</summary>
    public static InboxMobileLink? TryParseInbox(string? raw)
    {
        if (!Plausible(raw)
            || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, NcwLinkParser.Scheme, StringComparison.Ordinal)
            || !string.Equals(uri.Host, "inbox", StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length > 0 || !uri.IsDefaultPort || raw.Contains('#', StringComparison.Ordinal) || uri.AbsolutePath is not ("" or "/"))
        {
            return null;
        }

        var question = raw.IndexOf('?', StringComparison.Ordinal);
        if (question < 0)
        {
            return null;
        }

        string? device = null, kind = null, id = null;
        foreach (var pair in raw[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                return null;
            }

            var name = pair[..eq];
            var value = pair[(eq + 1)..];
            if (value.Length is 0 or > 128 || !value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            {
                return null;
            }

            switch (name)
            {
                case "d" when device is null:
                    device = value;
                    break;
                case "k" when kind is null:
                    kind = value;
                    break;
                case "id" when id is null:
                    id = value;
                    break;
                default:
                    return null; // 未知或重复的参数
            }
        }

        return device is null ? null : new InboxMobileLink(device, kind, id);
    }
}

/// <summary>
/// 手机壳的链接路由:系统转来的链接 → 导航(<see cref="MobileNavigationViewModel"/>)或配对流程(<see cref="QrPairingViewModel"/>)。
/// 必须在 UI 线程上调用(导航与 ViewModel 是 UI 状态)。配对二维码链接和扫码得到的内容走同一个入口 <see cref="QrPairingViewModel.Submit"/>:
/// 解析、过期检查、<b>等用户确认</b>都在状态机里,链接本身不会触发配对。
/// </summary>
public sealed class MobileLinkHandler(ShellViewModel shell)
{
    public async Task<MobileLinkOutcome> HandleAsync(string? raw)
    {
        switch (MobileLinkParser.Parse(raw))
        {
            case PairQrMobileLink pair:
                // 先推页面再填内容:页面离开视图树时会重置流程,先填后推会被上一个页面的重置冲掉
                if (shell.Navigation is not { SelectedTab: MobileTab.Devices, Top: QrPairRoute })
                {
                    shell.Navigation.OpenDevicesPage(new QrPairRoute());
                }

                shell.QrPairing.Submit(pair.Content);
                return MobileLinkOutcome.Handled;
            case InboxMobileLink:
                shell.Navigation.SelectTabCommand.Execute(MobileTab.Inbox);
                return MobileLinkOutcome.Handled;
            case AuthCallbackMobileLink callback:
                if (shell.Links is not { } router)
                {
                    return MobileLinkOutcome.AccountNotWired;
                }

                await router.HandleAsync(callback.Raw).ConfigureAwait(true);
                return MobileLinkOutcome.Handled;
            default:
                return MobileLinkOutcome.Ignored;
        }
    }
}
