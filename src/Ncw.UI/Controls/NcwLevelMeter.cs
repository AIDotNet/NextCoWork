using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Ncw.UI.Controls;

/// <summary>
/// 六格的强度刻度条(输入框里的思考档位):<see cref="Level"/> 格点亮,从矮到高。
/// <see cref="IsAuto"/> 时点亮的格子用强调色(档位交给模型自己决定)。颜色在 <c>Themes/NcwStyles.axaml</c> 里按 token 写。
/// </summary>
public class NcwLevelMeter : StackPanel
{
    public const int Bars = 6;

    public static readonly StyledProperty<int> LevelProperty =
        AvaloniaProperty.Register<NcwLevelMeter, int>(nameof(Level));

    public static readonly StyledProperty<bool> IsAutoProperty =
        AvaloniaProperty.Register<NcwLevelMeter, bool>(nameof(IsAuto));

    public NcwLevelMeter()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 2;
        VerticalAlignment = VerticalAlignment.Center;
        for (var i = 0; i < Bars; i++)
        {
            var bar = new Border { Width = 3, Height = 3 + (i + 1) * 1.5, CornerRadius = new CornerRadius(1), VerticalAlignment = VerticalAlignment.Bottom };
            bar.Classes.Add("bar");
            Children.Add(bar);
        }

        Height = 12;
        Apply();
    }

    public int Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool IsAuto
    {
        get => GetValue(IsAutoProperty);
        set => SetValue(IsAutoProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LevelProperty || change.Property == IsAutoProperty)
        {
            Apply();
        }
    }

    private void Apply()
    {
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Classes.Set("on", i < Level);
        }

        Classes.Set("auto", IsAuto);
    }
}
