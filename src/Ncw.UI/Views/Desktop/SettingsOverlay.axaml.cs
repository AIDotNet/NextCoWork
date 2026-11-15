using Avalonia.Controls;

namespace Ncw.UI.Views.Desktop;

public partial class SettingsOverlay : UserControl
{
    /// <summary>窄于它时导航收成图标栏(纯 UI 状态)。</summary>
    public const double CompactBelow = 760;

    public SettingsOverlay()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyCompact(e.NewSize.Width < CompactBelow);
    }

    /// <summary>导航是否收成了图标栏(测试用)。</summary>
    public bool IsCompact { get; private set; }

    private void ApplyCompact(bool compact)
    {
        IsCompact = compact;
        Layout.Classes.Set("compact", compact);
        Layout.ColumnDefinitions[0].Width = new GridLength(compact ? 56 : 220);
        Body.Margin = compact ? new Avalonia.Thickness(16, 8, 16, 32) : new Avalonia.Thickness(32, 8, 32, 32);
    }
}
