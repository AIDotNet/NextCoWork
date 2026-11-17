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
using TKCode.Arc.Icons.Lucide;

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

    /// <summary>窄于它时工具行收一点(模型名更短、思考不显示文字),但仍是桌面版式。</summary>
    public const double TightBelow = 680;

    /// <summary>当前是否紧凑版(测试用)。</summary>
    public bool IsCompact { get; private set; }

    private void ApplyCompact(double width)
    {
        var compact = NcwDensity.GetIsTouch(this) || width < CompactBelow;
        IsCompact = compact;
        Classes.Set("compact", compact);
        // 中等宽度:模型名短一些、思考只留刻度,工具行不至于挤掉右边的权限
        Classes.Set("tight", !compact && width < TightBelow);
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
            _vm.DraftReplaced -= OnDraftReplaced;
            _vm.SlashCommands.CollectionChanged -= OnSlashCommandsChanged;
            _vm.Models.CollectionChanged -= OnCollectionChanged;
            _vm.ThinkingChoices.CollectionChanged -= OnCollectionChanged;
            _vm.PermissionChoices.CollectionChanged -= OnCollectionChanged;
        }

        _vm = DataContext as ComposerViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.DraftReplaced += OnDraftReplaced;
            _vm.SlashCommands.CollectionChanged += OnSlashCommandsChanged;
            _vm.Models.CollectionChanged += OnCollectionChanged;
            _vm.ThinkingChoices.CollectionChanged += OnCollectionChanged;
            _vm.PermissionChoices.CollectionChanged += OnCollectionChanged;
        }

        // 哪些片段画成 token:只问 ViewModel(ComposerLogic.Mentions 一处规则)
        Input.MentionSource = _vm is { } source ? source.Mentions : null;
        RebuildMenus();
    }

    /// <summary>选中了文件 / 命令:光标放到末尾并取回焦点(点候选行时焦点在弹层里的按钮上)。</summary>
    private void OnDraftReplaced(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Input.MoveCaretToEnd(focus: true));

    /// <summary>命令清单到了:文字没变也要重新解析一次,开头的 <c>/命令 </c> 才会变成 token。</summary>
    /// <summary>⌘L:把焦点放进输入框,光标到末尾。</summary>
    public void FocusInput() => Input.MoveCaretToEnd(focus: true);

    private void OnSlashCommandsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Input.Reparse();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildMenus();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComposerViewModel.ShowsSuggestions) or nameof(ComposerViewModel.HasSlashQuery) && _vm is { } open)
        {
            // 辅助技术读到「列表展开着」
            Input.SuggestionsExpanded = open.ShowsSuggestions || open.HasSlashQuery;
        }

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
        // 触发器:供应商首字母 + 模型名;思考刻度 + 短名;权限图标 + 文字(按档位着色)
        ModelText.Text = vm.SelectedModel is { } model ? model.DisplayName : NcwTexts.Get(NcwTextKind.NoModelSelected);
        ModelGlyph.Text = vm.SelectedModel is { ProviderName.Length: > 0 } provider ? provider.ProviderName[..1].ToUpperInvariant() : "?";
        // 触发器上带上「思考 / 权限」前缀:单独一个「自动」「询问」看不出是在调什么(03 §11.5)
        ThinkingText.Text = NcwTexts.Format(NcwTextKind.ComposerThinkingLabel, ThinkingLevelTextConverter.ShortText(vm.Thinking));
        ThinkingMeter.Level = ThinkingLevelTextConverter.MeterLevel(vm.Thinking);
        ThinkingMeter.IsAuto = vm.Thinking == "auto";
        // 不支持思考的模型只有「关」一档:按钮灰掉,不让人以为能调
        ThinkingMenu.IsEnabled = vm.ThinkingChoices.Count > 1;
        PermissionText.Text = NcwTexts.Format(NcwTextKind.ComposerPermissionLabel, PermissionModeTextConverter.Text(vm.Permission));
        PermissionIcon.Kind = vm.Permission switch
        {
            "auto" => LucideIconKind.ShieldCheck,
            "full" => LucideIconKind.ShieldAlert,
            _ => LucideIconKind.ShieldQuestion,
        };
        PermissionButton.Classes.Set("perm-ask", vm.Permission is not ("auto" or "full"));
        PermissionButton.Classes.Set("perm-auto", vm.Permission == "auto");
        PermissionButton.Classes.Set("perm-full", vm.Permission == "full");

        NcwMenus.Populate(ModelMenu, ModelMenuItems(vm));
        NcwMenus.Populate(ThinkingMenu, vm.ThinkingChoices.Select(level => new NcwMenus.Item(
            ThinkingLevelTextConverter.ShortText(level), vm.SetThinkingCommand, level) { IsChecked = level == vm.Thinking }));
        NcwMenus.Populate(PermissionMenu, vm.PermissionChoices.Select(mode => new NcwMenus.Item(
            PermissionModeTextConverter.Text(mode), vm.SetPermissionCommand, mode)
        {
            IsChecked = mode == vm.Permission,
            Hint = PermissionModeTextConverter.Hint(mode),
        }));
    }

    /// <summary>模型菜单:按供应商分组(组标签 = 供应商名,组的顺序是候选里第一次出现的顺序),行尾是「上下文窗口 · 视觉 · 思考」,不可用的灰显并注明。</summary>
    private static IEnumerable<NcwMenus.Item> ModelMenuItems(ComposerViewModel vm)
    {
        var vision = NcwTexts.Get(NcwTextKind.ModelVision);
        var thinking = NcwTexts.Get(NcwTextKind.ModelThinking);
        var unavailable = NcwTexts.Get(NcwTextKind.ModelUnavailable);
        foreach (var group in vm.Models.GroupBy(m => m.ProviderId))
        {
            yield return NcwMenus.Item.Label(group.First().ProviderName);
            foreach (var m in group)
            {
                var hint = string.Join(" · ", new[]
                {
                    m.WindowText,
                    m.SupportsImages ? vision : null,
                    m.CanThink ? thinking : null,
                    m.IsAvailable ? null : unavailable,
                }.Where(x => !string.IsNullOrEmpty(x)));
                yield return new NcwMenus.Item(m.DisplayName, vm.PickModelCommand, m)
                {
                    IsDisabled = !m.IsAvailable,
                    IsChecked = vm.SelectedModel is { } current && current.Key == m.Key,
                    Hint = hint,
                };
            }
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // 输入法组字中:Enter / 方向键归输入法
        if (Input.IsComposing)
        {
            return;
        }

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

    /// <summary>上下文环:打开 / 关上上下文窗口卡片(纯 UI 状态)。</summary>
    private void OnContextClick(object? sender, RoutedEventArgs e) => ContextPopup.IsOpen = !ContextPopup.IsOpen;

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
