using Avalonia.Controls;
using Avalonia.Interactivity;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Converters;
using Ncw.UI.Localization;

namespace Ncw.UI.Views.Desktop.Conversation;

/// <summary>交互卡片。点选提问选项是 ViewModel 上的方法;这里只负责「记住范围」下拉里的文字(按当前语言)。</summary>
public partial class InteractionCard : UserControl
{
    public InteractionCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_scopes is not null)
            {
                _scopes.CollectionChanged -= OnScopesChanged;
            }

            _scopes = Row?.Scopes;
            if (_scopes is not null)
            {
                _scopes.CollectionChanged += OnScopesChanged;
            }

            PopulateAlwaysMenu();
        };

        // 触控模式(手机壳设置的继承属性):危险 / 普通审批都改成「长按允许」。手势的状态机在 Client 的 HoldGesture,这里只转发指针事件
        HoldAllow.HoldStarted += (_, _) => Row?.BeginAllowHold();
        HoldAllow.HoldMoved += (_, travel) => Row?.MoveAllowHold(travel);
        HoldAllow.HoldEnded += (_, _) => Row?.EndAllowHold();
        HoldAllow.HoldInterrupted += (_, _) => Row?.InterruptAllowHold();
        HoldAllow.Tick += (_, _) => Row?.TickAllowHold();
    }

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NcwDensity.IsTouchProperty)
        {
            Classes.Set("touch", change.NewValue is true);
        }
    }

    private InteractionRowViewModel? Row => DataContext as InteractionRowViewModel;

    private System.Collections.ObjectModel.ObservableCollection<string>? _scopes;

    private void OnScopesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => PopulateAlwaysMenu();

    /// <summary>「总是允许 ▾」的菜单:设备给的每个记住范围一项(按当前语言);「允许一次」上标出当前的 ⌘↵ 快捷键(用户改过就跟着变)。</summary>
    private void PopulateAlwaysMenu()
    {
        if (Row is not { } row)
        {
            return;
        }

        NcwMenus.Populate(AlwaysMenu, row.Scopes.Select(scope => new NcwMenus.Item(
            NcwTexts.Format(NcwTextKind.ApprovalAlwaysAllowIn, ScopeTextConverter.Text(scope)), row.AlwaysAllowInCommand, scope)));
        AllowGesture.Text = ShortcutText.Format(ShortcutText.CurrentGesture(Ncw.Client.Shortcuts.AppCommand.ApproveFirst));
    }

    private void OnToggleOption(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AskOptionViewModel option && FindQuestion(sender as Control, option) is { } question)
        {
            question.Toggle(option);
        }
    }

    private AskQuestionViewModel? FindQuestion(Control? from, AskOptionViewModel option) =>
        DataContext is InteractionRowViewModel row ? row.Questions.FirstOrDefault(q => q.Options.Contains(option)) : null;
}
