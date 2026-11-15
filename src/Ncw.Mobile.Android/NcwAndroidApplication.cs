using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Ncw.Mobile.Droid.Platform;
using Ncw.UI;

namespace Ncw.Mobile.Droid;

/// <summary>
/// Android 应用对象:Avalonia 的启动入口(<see cref="AvaloniaAndroidApplication{TApp}"/> 创建 <see cref="NcwApp"/>)。
/// 装配顺序同 docs/design/11 §1:<b>在 Application 创建之前</b>设置后端工厂和平台服务工厂,再用 <c>UseNcw()</c> 注册 Arc、字体、语言(和桌面同一个 <see cref="NcwAppSetup"/>)。
/// </summary>
[Application(Label = "@string/app_name", Icon = "@mipmap/ic_launcher", RoundIcon = "@mipmap/ic_launcher_round", Theme = "@style/NcwTheme", AllowBackup = false, SupportsRtl = true)]
public class NcwAndroidApplication : AvaloniaAndroidApplication<NcwApp>
{
    protected NcwAndroidApplication(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        AndroidHost.Initialize(this);
        return CjkWeightFix.Install(base.CustomizeAppBuilder(builder).UseNcw());
    }
}
