using Avalonia.Threading;
using Ncw.Client.Devices;

namespace Ncw.UI;

/// <summary>把协议回调切到 Avalonia 的 UI 线程。</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public static readonly AvaloniaUiDispatcher Instance = new();

    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
