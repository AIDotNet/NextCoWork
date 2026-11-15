using System.Collections.Specialized;
using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Converters;

namespace Ncw.UI.Views.Desktop.Inspector;

/// <summary>改动审查页签的视图。code-behind 只管「选轮」下拉的条目(轮的列表变了重填);撤销 / 重做 / 看 diff 都是 <see cref="ReviewPanelViewModel"/> 的命令。</summary>
public partial class ReviewPane : UserControl
{
    private ReviewPanelViewModel? _vm;

    public ReviewPane() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Runs.CollectionChanged -= OnRunsChanged;
        }

        _vm = DataContext as ReviewPanelViewModel;
        if (_vm is not null)
        {
            _vm.Runs.CollectionChanged += OnRunsChanged;
        }

        RebuildRunMenu();
    }

    private void OnRunsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildRunMenu();

    private void RebuildRunMenu()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        NcwMenus.Populate(RunMenu, vm.Runs.Select(run => new NcwMenus.Item(
            $"{RelativeTimeConverter.Instance.Convert(run.UpdatedAt, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture)} · "
            + $"{Ncw.UI.Localization.NcwTexts.Format(Ncw.UI.Localization.NcwTextKind.ReviewFiles, run.FileCount)}"
            + (run.IsReverted ? $" · {Ncw.UI.Localization.NcwTexts.Get(Ncw.UI.Localization.NcwTextKind.ReviewReverted)}" : ""),
            vm.SelectRunCommand, run) { IsChecked = vm.Selected?.RunId == run.RunId }));
    }
}
