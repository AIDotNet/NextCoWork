using Android.Content;
using Android.Net;

namespace Ncw.Mobile.Droid.Platform;

/// <summary>
/// 默认网络的变化 → <see cref="ConnectionSupervisor"/>(换网络重连、断网立刻标 unknown、网络回来恢复)。
/// 回调在系统线程池上;网络用 <see cref="Network.NetworkHandle"/> 区分(换网络时旧网络的 onLost 比新网络的 onAvailable 晚到,策略里忽略)。
/// </summary>
internal static class NetworkMonitor
{
    private static Callback? _callback;

    public static void Start(Context context, ConnectionSupervisor supervisor)
    {
        if (_callback is not null || context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager manager)
        {
            return;
        }

        _callback = new Callback(supervisor);
        manager.RegisterDefaultNetworkCallback(_callback);
    }

    private sealed class Callback(ConnectionSupervisor supervisor) : ConnectivityManager.NetworkCallback
    {
        public override void OnAvailable(Network network) => Observe(supervisor.NetworkAvailableAsync(Id(network)));

        public override void OnLost(Network network) => Observe(supervisor.NetworkLostAsync(Id(network)));

        private static string Id(Network network) => network.NetworkHandle.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static void Observe(Task task) =>
            task.ContinueWith(static t => global::Android.Util.Log.Warn("Ncw", $"network event failed: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
    }
}
