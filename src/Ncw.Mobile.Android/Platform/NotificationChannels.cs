using Android.App;
using Android.Content;

namespace Ncw.Mobile.Droid.Platform;

/// <summary>通知渠道(docs/design/11 §3):<c>approvals</c>(高优先级:审批 / 提问 / 计划,需要用户处理)和 <c>runs</c>(默认:运行完成 / 失败)。名称和说明来自 Android 字符串资源(随系统语言)。</summary>
internal static class NotificationChannels
{
    public const string Approvals = "approvals";
    public const string Runs = "runs";

    public static void EnsureCreated(Context context)
    {
        if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
        {
            return;
        }

        manager.CreateNotificationChannel(new NotificationChannel(Approvals, context.GetString(Resource.String.channel_approvals), NotificationImportance.High)
        {
            Description = context.GetString(Resource.String.channel_approvals_desc),
            LockscreenVisibility = NotificationVisibility.Private, // 锁屏上只显示「有一条通知」,不显示内容
        });
        manager.CreateNotificationChannel(new NotificationChannel(Runs, context.GetString(Resource.String.channel_runs), NotificationImportance.Default)
        {
            Description = context.GetString(Resource.String.channel_runs_desc),
            LockscreenVisibility = NotificationVisibility.Private,
        });
    }
}
