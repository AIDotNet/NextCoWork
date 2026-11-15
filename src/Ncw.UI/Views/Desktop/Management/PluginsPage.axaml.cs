using Avalonia.Controls;
using Ncw.Client.ViewModels;
using TKCode.Arc.Localization;

namespace Ncw.UI.Views.Desktop.Management;

public partial class PluginsPage : UserControl
{
    public PluginsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ApplyLocale();
    }

    /// <summary>插件自己带的词条按界面语言取(取不到回退 en-US):把当前语言告诉 ViewModel。</summary>
    private void ApplyLocale()
    {
        if (DataContext is PluginsViewModel vm)
        {
            vm.Locale = ArcLanguage.Current.ToString();
        }
    }
}
