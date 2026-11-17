using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>项目图标的三种样子。</summary>
public enum ProjectIconMode
{
    /// <summary>项目名首字母 + 底色(默认)。</summary>
    Initials,

    /// <summary>自定义文字 / emoji + 底色。</summary>
    Text,

    /// <summary>自定义图片(压缩成 96×96)。</summary>
    Image,
}

/// <summary>
/// 「项目图标」窗口(项目栏右键「更改图标…」,docs/design/03 §1「栏 1」)。编辑中的样子是这个窗口的纯 UI 状态;
/// 「保存」/「恢复默认」才经 <see cref="LayoutActions.SetAppearance"/> 写进 <see cref="ClientStores.Layout"/>(规则 4)。
/// </summary>
public sealed partial class ProjectIconViewModel : ObservableObject
{
    /// <summary>文字图标最多几个字(按用户看到的字符算,emoji 组合算一个)。</summary>
    public const int MaxGlyphLength = 2;

    private readonly LayoutActions _actions;
    private readonly Func<IDialogService> _dialogs;
    private readonly string _defaultColor;

    public ProjectIconViewModel(ProjectRef project, string projectName, ProjectAppearance? current, LayoutActions actions, Func<IDialogService> dialogs)
    {
        Project = project;
        ProjectName = projectName;
        _actions = actions;
        _dialogs = dialogs;
        _defaultColor = RailProjectViewModel.DefaultColor(project);
        Color = current?.Color ?? _defaultColor;
        Image = current?.Image;
        Text = current?.Glyph ?? "";
        Mode = current switch
        {
            { Image: not null } => ProjectIconMode.Image,
            { Glyph: not null } => ProjectIconMode.Text,
            _ => ProjectIconMode.Initials,
        };
    }

    public ProjectRef Project { get; }

    public string ProjectName { get; }

    /// <summary>可选的底色(用户数据,不是主题色;和默认色是同一组)。</summary>
    public IReadOnlyList<string> Palette => RailProjectViewModel.IconPalette;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInitials), nameof(IsText), nameof(IsImage), nameof(PreviewGlyph), nameof(PreviewImage), nameof(ShowsColor))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial ProjectIconMode Mode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewGlyph))]
    public partial string Text { get; set; }

    [ObservableProperty] public partial string Color { get; set; }

    /// <summary>选好的图片(压缩后的 base64);没选为 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewImage), nameof(HasImage))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string? Image { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImageError))]
    public partial IconPickError? ImageError { get; private set; }

    [ObservableProperty] public partial bool IsPicking { get; private set; }

    public bool IsInitials => Mode == ProjectIconMode.Initials;

    public bool IsText => Mode == ProjectIconMode.Text;

    public bool IsImage => Mode == ProjectIconMode.Image;

    public bool HasImage => Image is not null;

    public bool HasImageError => ImageError is not null;

    /// <summary>图片模式下不需要底色。</summary>
    public bool ShowsColor => Mode != ProjectIconMode.Image;

    /// <summary>预览里显示的文字(图片模式下图片压过它)。</summary>
    public string PreviewGlyph => Mode == ProjectIconMode.Text && NormalizeGlyph(Text) is { } glyph ? glyph : RailProjectViewModel.Initials(ProjectName);

    public string? PreviewImage => Mode == ProjectIconMode.Image ? Image : null;

    /// <summary>保存或恢复默认之后:界面据此关窗口。</summary>
    public event Action? Completed;

    [RelayCommand]
    private void SetColor(string color) => Color = color;

    [RelayCommand]
    private async Task PickImageAsync()
    {
        if (IsPicking)
        {
            return;
        }

        IsPicking = true;
        try
        {
            var picked = await _dialogs().PickIconImageAsync();
            if (picked is null)
            {
                return;
            }

            ImageError = picked.Error;
            if (picked.Image is { } image)
            {
                Image = image;
                Mode = ProjectIconMode.Image;
            }
        }
        finally
        {
            IsPicking = false;
        }
    }

    private bool CanSave() => Mode != ProjectIconMode.Image || Image is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        _actions.SetAppearance(Project, Build());
        Completed?.Invoke();
    }

    [RelayCommand]
    private void Reset()
    {
        _actions.SetAppearance(Project, null);
        Completed?.Invoke();
    }

    /// <summary>当前编辑结果对应的外观;和默认完全一样时为 null(不留一条没用的记录)。</summary>
    public ProjectAppearance? Build() => Mode switch
    {
        ProjectIconMode.Image when Image is not null => new ProjectAppearance(null, Color, Image),
        ProjectIconMode.Text when NormalizeGlyph(Text) is { } glyph => new ProjectAppearance(glyph, Color),
        _ => Color == _defaultColor ? null : new ProjectAppearance(null, Color),
    };

    /// <summary>去掉首尾空白,最多留 <see cref="MaxGlyphLength"/> 个字符(按文本元素,emoji 组合不会被截断);空的返回 null。</summary>
    public static string? NormalizeGlyph(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var info = new StringInfo(trimmed);
        return info.LengthInTextElements <= MaxGlyphLength ? trimmed : info.SubstringByTextElements(0, MaxGlyphLength);
    }
}
