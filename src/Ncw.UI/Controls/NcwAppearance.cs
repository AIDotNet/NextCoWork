using Avalonia.Threading;
using Ncw.Client.Stores;
using TKCode.Arc;

namespace Ncw.UI.Controls;

/// <summary>
/// 明暗主题偏好(<see cref="UiPrefs.Theme"/>)到 Arc 主题的绑定。
/// 只改 <see cref="ArcAppearance"/>,保留强调色、动效级别等其余主题状态。
/// </summary>
public static class NcwAppearance
{
    public static void Apply(UiThemePreference preference) =>
        ArcTheme.Apply(t => t.Appearance(ToArc(preference)));

    public static ArcAppearance ToArc(UiThemePreference preference) => preference switch
    {
        UiThemePreference.Light => ArcAppearance.Light,
        UiThemePreference.System => ArcAppearance.System,
        _ => ArcAppearance.Dark,
    };

    /// <summary>启动时调用一次:立即应用当前偏好,之后偏好变化时重新应用。返回的对象在退出时释放。</summary>
    public static IDisposable Bind(ClientStores stores)
    {
        var last = (UiThemePreference?)null;
        void Sync()
        {
            var preference = stores.Prefs.State.Theme;
            if (last == preference)
            {
                return;
            }

            last = preference;
            Apply(preference);
        }

        void OnChanged()
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                Sync();
            }
            else
            {
                Dispatcher.UIThread.Post(Sync);
            }
        }

        Sync();
        stores.Prefs.Changed += OnChanged;
        return new Unbinder(() => stores.Prefs.Changed -= OnChanged);
    }

    private sealed class Unbinder(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
