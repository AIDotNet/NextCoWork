using Android.App;
using Android.Content;
using Android.Content.PM;

namespace Ncw.Mobile.Droid.Platform;

/// <summary>
/// 把 Activity 的结果回调(<c>OnActivityResult</c> / <c>OnRequestPermissionsResult</c>)变成可 await 的任务。
/// 请求码要落在低 16 位里(AppCompat 的限制);每个用途一个固定的码,同一用途的新请求会取消上一个。
/// </summary>
internal static class ActivityResults
{
    public const int CameraPermission = 0x4E01;
    public const int NotificationPermission = 0x4E02;
    public const int CameraCapture = 0x4E03;

    private static readonly object Gate = new();
    private static readonly Dictionary<int, TaskCompletionSource<(Result Result, Intent? Data)>> Pending = [];
    private static readonly Dictionary<int, TaskCompletionSource<Permission[]>> PendingPermissions = [];

    public static Task<(Result Result, Intent? Data)> StartAsync(Activity activity, Intent intent, int requestCode)
    {
        var tcs = new TaskCompletionSource<(Result, Intent?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            if (Pending.Remove(requestCode, out var old))
            {
                old.TrySetCanceled();
            }

            Pending[requestCode] = tcs;
        }

#pragma warning disable CA1422 // 与 Avalonia 的 Activity 基类一致:仍用 StartActivityForResult / OnActivityResult
        activity.StartActivityForResult(intent, requestCode);
#pragma warning restore CA1422
        return tcs.Task;
    }

    public static Task<Permission[]> RequestPermissionsAsync(Activity activity, string[] permissions, int requestCode)
    {
        var tcs = new TaskCompletionSource<Permission[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            if (PendingPermissions.Remove(requestCode, out var old))
            {
                old.TrySetCanceled();
            }

            PendingPermissions[requestCode] = tcs;
        }

        activity.RequestPermissions(permissions, requestCode);
        return tcs.Task;
    }

    public static void Complete(int requestCode, Result result, Intent? data)
    {
        TaskCompletionSource<(Result, Intent?)>? tcs;
        lock (Gate)
        {
            Pending.Remove(requestCode, out tcs);
        }

        tcs?.TrySetResult((result, data));
    }

    public static void CompletePermissions(int requestCode, string[] permissions, Permission[] grantResults)
    {
        TaskCompletionSource<Permission[]>? tcs;
        lock (Gate)
        {
            PendingPermissions.Remove(requestCode, out tcs);
        }

        tcs?.TrySetResult(grantResults);
    }
}
