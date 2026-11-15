using Android.Content;
using Ncw.Client.Services;

namespace Ncw.Mobile.Droid.Platform;

/// <summary>
/// <see cref="IUrlLauncher"/>:用 Intent 打开系统浏览器(登录授权页、文档链接)。只放行 http / https。
/// <b>目前没有调用方</b>:账户服务在手机壳里还没有接线(手机壳没有账户页,<c>NcwApp</c> 的单视图分支不创建 <c>AccountService</c>),接线时把它传进去即可。
/// </summary>
internal sealed class AndroidUrlLauncher : IUrlLauncher
{
    public Task OpenAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Only http(s) URLs can be opened", nameof(url));
        }

        var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(uri.AbsoluteUri));
        intent.AddFlags(ActivityFlags.NewTask);
        AndroidHost.Context.StartActivity(intent);
        return Task.CompletedTask;
    }
}
