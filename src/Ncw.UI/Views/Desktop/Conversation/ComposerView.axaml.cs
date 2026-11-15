using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Converters;
using Ncw.UI.Localization;

namespace Ncw.UI.Views.Desktop.Conversation;

/// <summary>
/// 输入框视图。纯 UI 的部分:Enter 发送 / Shift+Enter 换行、三个下拉菜单的条目(模型 / 思考 / 权限,数据变了就重填)。
/// 能不能发送、模型候选、档位限制都在 <see cref="ComposerViewModel"/> / <see cref="ComposerLogic"/>。
/// </summary>
public partial class ComposerView : UserControl
{
    private ComposerViewModel? _vm;

    public ComposerView()
    {
        InitializeComponent();
        // 隧道阶段先看 Enter:Arc 的多行输入框自己会把 Enter 当换行
        Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        SizeChanged += (_, e) => ApplyCompact(e.NewSize.Width);
    }

    /// <summary>窄于它(或触控模式)时工具行收成紧凑版:只留图标(纯 UI 状态)。</summary>
    public const double CompactBelow = 480;

    /// <summary>当前是否紧凑版(测试用)。</summary>
    public bool IsCompact { get; private set; }

    private void ApplyCompact(double width)
    {
        var compact = NcwDensity.GetIsTouch(this) || width < CompactBelow;
        IsCompact = compact;
        Classes.Set("compact", compact);
    }

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NcwDensity.IsTouchProperty)
        {
            ApplyCompact(Bounds.Width);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Models.CollectionChanged -= OnCollectionChanged;
            _vm.ThinkingChoices.CollectionChanged -= OnCollectionChanged;
            _vm.PermissionChoices.CollectionChanged -= OnCollectionChanged;
        }

        _vm = DataContext as ComposerViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.Models.CollectionChanged += OnCollectionChanged;
            _vm.ThinkingChoices.CollectionChanged += OnCollectionChanged;
            _vm.PermissionChoices.CollectionChanged += OnCollectionChanged;
        }

        RebuildMenus();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildMenus();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComposerViewModel.SelectedModel) or nameof(ComposerViewModel.Thinking) or nameof(ComposerViewModel.Permission)
            or nameof(ComposerViewModel.CanUseFull))
        {
            RebuildMenus();
        }
    }

    /// <summary>三个菜单的文字和条目。条目里带「当前选中」,不可用的模型(停用的供应商 / 别名)显示出来但灰掉。</summary>
    private void RebuildMenus()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        NcwMenus.Populate(AttachMenu, new[] { (AttachmentSource.PhotoLibrary, NcwTextKind.AttachmentPhotoLibrary), (AttachmentSource.Camera, NcwTextKind.AttachmentCamera), (AttachmentSource.File, NcwTextKind.AttachmentFile) }
            .Where(x => vm.Attachments.IsAvailable(x.Item1))
            .Select(x => new NcwMenus.Item(NcwTexts.Get(x.Item2), vm.Attachments.PickCommand, x.Item1)));
        ModelText.Text = vm.SelectedModel is { } model ? model.DisplayName : NcwTexts.Get(NcwTextKind.NoModelSelected);
        ThinkingText.Text = ThinkingLevelTextConverter.Text(vm.Thinking);
        PermissionText.Text = PermissionModeTextConverter.Text(vm.Permission);

        var unavailable = NcwTexts.Get(NcwTextKind.ModelUnavailable);
        NcwMenus.Populate(ModelMenu, vm.Models.Select(m => new NcwMenus.Item(
            $"{m.DisplayName} · {m.WindowText}{(m.SupportsImages ? " · " + NcwTexts.Get(NcwTextKind.ModelVision) : "")}{(m.CanThink ? " · " + NcwTexts.Get(NcwTextKind.ModelThinking) : "")}{(m.IsAvailable ? "" : " · " + unavailable)}  ({m.ProviderName})",
            vm.PickModelCommand, m)
        {
            IsDisabled = !m.IsAvailable,
            IsChecked = vm.SelectedModel is { } current && current.Key == m.Key,
        }));
        NcwMenus.Populate(ThinkingMenu, vm.ThinkingChoices.Select(level => new NcwMenus.Item(
            ThinkingLevelTextConverter.Text(level), vm.SetThinkingCommand, level) { IsChecked = level == vm.Thinking }));
        NcwMenus.Populate(PermissionMenu, vm.PermissionChoices.Select(mode => new NcwMenus.Item(
            $"{PermissionModeTextConverter.Text(mode)} — {PermissionModeTextConverter.Hint(mode)}", vm.SetPermissionCommand, mode) { IsChecked = mode == vm.Permission }));
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // `@` 弹层打开时:上下移动高亮,Enter / Tab 选中候选(没有候选时 Enter 照常发送)
        if (_vm is { HasFileQuery: true } picker && picker.FileSuggestions.Count > 0)
        {
            switch (e.Key)
            {
                case Key.Down:
                    picker.MoveSuggestionCommand.Execute(1);
                    e.Handled = true;
                    return;
                case Key.Up:
                    picker.MoveSuggestionCommand.Execute(-1);
                    e.Handled = true;
                    return;
                case Key.Enter or Key.Tab when !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && picker.AcceptSuggestion():
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key is Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _vm is { } vm && vm.State.Primary is ComposerPrimary.Send or ComposerPrimary.Queue)
        {
            vm.SubmitCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPromote(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is QueueItemView item)
        {
            _vm?.PromoteCommand.Execute(item);
        }
    }

    private void OnRemoveQueued(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is QueueItemView item)
        {
            _vm?.RemoveQueuedCommand.Execute(item);
        }
    }
}
