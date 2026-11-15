#if NCW_FCM
using Android.App;
using Android.Content;
using Android.Gms.Tasks;
using AndroidX.Core.App;
using Firebase.Messaging;
using Ncw.Client.ViewModels;

namespace Ncw.Mobile.Droid.Platform;

/// <summary>FCM 令牌的读取(只在有 <c>google-services.json</c> 的构建里编译;未在真实 Firebase 项目上验证,见 docs/design/11 §6)。</summary>
internal static class FcmTokens
{
    public static Task<string?> GetAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        FirebaseMessaging.Instance.GetToken().AddOnCompleteListener(new Listener(tcs));
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    private sealed class Listener(TaskCompletionSource<string?> tcs) : Java.Lang.Object, IOnCompleteListener
    {
        public void OnComplete(global::Android.Gms.Tasks.Task task) => tcs.TrySetResult(task.IsSuccessful ? task.Result?.ToString() : null);
    }
}

/// <summary>
/// FCM 消息服务:令牌刷新 → <see cref="AndroidPushRegistration"/>;收到消息 → 本地通知。消息内容只有 <c>{ deviceId, kind, opaqueId }</c>(09 §10),
/// 通知标题是固定文字(「需要你确认」),不含命令、路径、对话;点击打开 <c>nextcowork://inbox?d=&amp;k=&amp;id=</c>。字段不合法的消息丢弃。
/// </summary>
[Service(Exported = false)]
[IntentFilter(["com.google.firebase.MESSAGING_EVENT"])]
public class NcwFirebaseMessagingService : FirebaseMessagingService
{
    public override void OnNewToken(string token) => AndroidPushRegistration.RaiseTokenArrived(token);

    public override void OnMessageReceived(RemoteMessage message)
    {
        var data = message.Data;
        if (data is null || !data.TryGetValue("deviceId", out var device))
        {
            return;
        }

        data.TryGetValue("kind", out var kind);
        data.TryGetValue("opaqueId", out var id);
        var link = $"nextcowork://inbox?d={device}" + (kind is null ? "" : $"&k={kind}") + (id is null ? "" : $"&id={id}");
        if (MobileLinkParser.TryParseInbox(link) is null)
        {
            return;
        }

        var needsAction = kind is "approval" or "question" or "plan" or "goal";
        var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(link)).SetPackage(PackageName).AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        var pending = PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var notification = new NotificationCompat.Builder(this, needsAction ? NotificationChannels.Approvals : NotificationChannels.Runs)
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
            .SetContentTitle(GetString(needsAction ? Resource.String.notification_needs_attention : Resource.String.notification_run_finished))
            .SetContentIntent(pending)
            .SetAutoCancel(true)
            .SetVisibility(NotificationCompat.VisibilityPrivate)
            .Build();
        (GetSystemService(NotificationService) as NotificationManager)?.Notify(Math.Abs(link.GetHashCode()), notification);
    }
}
#endif
