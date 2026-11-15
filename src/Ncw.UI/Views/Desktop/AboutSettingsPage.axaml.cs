using Avalonia.Controls;

namespace Ncw.UI.Views.Desktop;

/// <summary>设置 · 本应用 · 关于。纯展示:所有状态来自 <see cref="Ncw.Client.ViewModels.AboutViewModel"/>(它只读更新状态的 Store、只调 UpdateActions)。</summary>
public partial class AboutSettingsPage : UserControl
{
    public AboutSettingsPage() => InitializeComponent();
}
