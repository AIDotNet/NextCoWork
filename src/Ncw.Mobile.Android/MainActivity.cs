using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Avalonia.Android;
using Ncw.Mobile.Droid.Platform;

namespace Ncw.Mobile.Droid;

/// <summary>
/// 唯一的 Activity(单视图宿主,根视图是 <c>AdaptiveShell</c>)。职责只有三件:
/// 把 <c>nextcowork://</c> 链接交给共享层(<see cref="AndroidHost.SubmitLink"/>);把前后台切换交给连接策略;
/// 把权限 / 系统选择器的结果交给平台服务(<see cref="ActivityResults"/>)。
/// <c>ConfigurationChanges</c> 全部自己处理(旋转、折叠、深浅色、软键盘、语言):Activity 不重建,界面状态不丢。
/// </summary>
[Activity(Label = "@string/app_name", Theme = "@style/NcwTheme", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.UiMode | ConfigChanges.Density | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation
        | ConfigChanges.LayoutDirection | ConfigChanges.Locale | ConfigChanges.FontScale)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "nextcowork", AutoVerify = false)]
public class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        AndroidHost.Activity = this;
        base.OnCreate(savedInstanceState);
        HandleLink(Intent);
        if (Intent?.GetBooleanExtra("fonttest", false) == true)
        {
            Task.Delay(4000).ContinueWith(_ => LayoutDiagnostics.FontTest());
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleLink(intent);
    }

    protected override void OnStart()
    {
        base.OnStart();
        AndroidHost.OnForeground();
    }

    protected override void OnStop()
    {
        AndroidHost.OnBackground();
        base.OnStop();
    }

    protected override void OnResume()
    {
        base.OnResume();
        AndroidHost.OnResumed();
    }

    protected override void OnPause()
    {
        AndroidHost.OnPaused();
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(AndroidHost.Activity, this))
        {
            AndroidHost.Activity = null;
        }

        base.OnDestroy();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        ActivityResults.Complete(requestCode, resultCode, data);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        ActivityResults.CompletePermissions(requestCode, permissions, grantResults);
    }

    private static void HandleLink(Intent? intent)
    {
        if (intent?.GetBooleanExtra("layouttree", false) == true)
        {
            LayoutDiagnostics.Log("manual-tree");
        }

        if (intent?.Action == Intent.ActionView && intent.DataString is { Length: > 0 } uri)
        {
            AndroidHost.SubmitLink(uri);
        }
    }
}
