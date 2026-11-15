using Android;
using Android.Content.PM;
using Android.OS;
using Ncw.Client.Services;

namespace Ncw.Mobile.Droid.Platform;

/// <summary>
/// <see cref="IPushRegistration"/>(FCM,docs/design/11 §3)。
/// <b>没有 Firebase 配置时</b>(<c>google-services.json</c> 不进仓库,构建时不存在):<see cref="IsConfigured"/> 为 false,
/// 界面显示「推送服务未配置」、不弹权限对话框、不取令牌——<b>不会伪造令牌</b>。有配置(构建时定义 <c>NCW_FCM</c>)时走 FCM:
/// Android 13+ 的 <c>POST_NOTIFICATIONS</c> 运行时权限 + <c>FirebaseMessaging.getToken()</c> / <c>onNewToken</c>。
/// </summary>
internal sealed class AndroidPushRegistration : IPushRegistration
{
    private const string PrefsName = "ncw.push";
    // Manifest.Permission.PostNotifications 只在 API 33+ 的绑定里标为受限;minSdk 是 26,用字符串常量,调用前自己判 SDK 版本
    private const string PostNotificationsPermission = "android.permission.POST_NOTIFICATIONS";
    private const string AskedKey = "notification-permission-asked";

    internal static event Action<string>? TokenArrived;

    public AndroidPushRegistration() => TokenArrived += token => TokenRefreshed?.Invoke(token); // 进程级单例:订阅不需要退订

    public bool IsSupported => true;

#if NCW_FCM
    public bool IsConfigured => true;
#else
    public bool IsConfigured => false;
#endif

    public PushPlatform Platform => PushPlatform.Fcm;

    public event Action<string>? TokenRefreshed;

    internal static void RaiseTokenArrived(string token) => TokenArrived?.Invoke(token);

    public Task<PushPermission> GetPermissionAsync(CancellationToken ct = default)
    {
        if ((int)Build.VERSION.SdkInt < 33)
        {
            return Task.FromResult(PushPermission.Granted); // 13 以下没有运行时通知权限
        }

        var context = AndroidHost.Context;
        if (context.CheckSelfPermission(PostNotificationsPermission) == Permission.Granted)
        {
            return Task.FromResult(PushPermission.Granted);
        }

        var asked = context.GetSharedPreferences(PrefsName, global::Android.Content.FileCreationMode.Private)!.GetBoolean(AskedKey, false);
        return Task.FromResult(asked ? PushPermission.Denied : PushPermission.NotDetermined);
    }

    public async Task<PushPermission> RequestPermissionAsync(CancellationToken ct = default)
    {
        if ((int)Build.VERSION.SdkInt < 33)
        {
            return PushPermission.Granted;
        }

        if (AndroidHost.Context.CheckSelfPermission(PostNotificationsPermission) == Permission.Granted)
        {
            return PushPermission.Granted;
        }

        if (AndroidHost.Activity is not { } activity)
        {
            return PushPermission.Denied;
        }

        var results = await ActivityResults.RequestPermissionsAsync(activity, [PostNotificationsPermission], ActivityResults.NotificationPermission).ConfigureAwait(true);
        AndroidHost.Context.GetSharedPreferences(PrefsName, global::Android.Content.FileCreationMode.Private)!.Edit()!.PutBoolean(AskedKey, true)!.Apply();
        return results.Length > 0 && results[0] == Permission.Granted ? PushPermission.Granted : PushPermission.Denied;
    }

    public Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
#if NCW_FCM
        return FcmTokens.GetAsync(ct);
#else
        return Task.FromResult<string?>(null); // 没有 Firebase 配置:没有令牌(绝不伪造)
#endif
    }
}
