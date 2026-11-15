using Avalonia;
using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Converters;
using TKCode.Arc;

namespace Ncw.UI.Views.Desktop.Inspector;

/// <summary>失败提示条:<see cref="Problem"/> 为空时隐藏。纯展示,翻译在 <see cref="ProblemTextConverter"/>。</summary>
public partial class ProblemBar : UserControl
{
    public static readonly StyledProperty<Problem?> ProblemProperty = AvaloniaProperty.Register<ProblemBar, Problem?>(nameof(Problem));

    public ProblemBar() => InitializeComponent();

    public Problem? Problem
    {
        get => GetValue(ProblemProperty);
        set => SetValue(ProblemProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ProblemProperty)
        {
            var problem = Problem;
            Bar.IsVisible = problem is not null;
            Message.Text = problem is { } p ? ProblemTextConverter.Text(p) : null;
            Tooltip.SetTip(Bar, problem is { Detail: { Length: > 0 } detail } ? detail : null);
        }
    }
}
