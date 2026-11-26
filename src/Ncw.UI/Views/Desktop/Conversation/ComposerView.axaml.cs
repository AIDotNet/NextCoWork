using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Converters;
using Ncw.UI.Localization;
using TKCode.Arc.Icons.Lucide;

namespace Ncw.UI.Views.Desktop.Conversation;

/// <summary>
/// 输入框视图。纯 UI 的部分:Enter 发送 / Shift+Enter 换行、四个选择弹层(附件 / 模型 / 思考 / 权限,各自只在对应数据变化时更新)。
/// 能不能发送、模型候选、档位限制都在 <see cref="ComposerViewModel"/> / <see cref="ComposerLogic"/>。
/// </summary>
public partial class ComposerView : UserControl
{
    private const int AutoCollapseProviderThreshold = 4;
    private const int AutoCollapseModelThreshold = 12;

    private ComposerViewModel? _vm;
    private Task _pasteTask = Task.CompletedTask;
    private readonly HashSet<string> _collapsedProviders = new(StringComparer.Ordinal);
    private bool _hasManualCollapseToggle;
    private string? _attachMenuKey;
    private string? _modelPickerKey;
    private string? _thinkingMenuKey;
    private string? _permissionMenuKey;

    public ComposerView()
    {
        InitializeComponent();
        // 隧道阶段先看 Enter:Arc 的多行输入框自己会把 Enter 当换行
        Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        Input.PastingFromClipboard += OnInputPasting;
        Card.AddHandler(DragDrop.DragOverEvent, OnCardDragOver);
        Card.AddHandler(DragDrop.DropEvent, OnCardDrop);
        SizeChanged += (_, e) => ApplyCompact(e.NewSize.Width);
    }

    /// <summary>最近一次粘贴处理结束时完成(测试用)。</summary>
    internal Task WhenPasted => _pasteTask;

    /// <summary>附件 / 思考 / 权限菜单实际重新填充的次数(测试用:确认流式刷新不触发无谓重建)。</summary>
    internal int MenuRebuildCount { get; private set; }

    /// <summary>模型弹层实际重建的次数(测试用)。</summary>
    internal int ModelPickerRebuildCount { get; private set; }

    /// <summary>某个供应商在模型弹层中是否处于折叠状态(测试用)。</summary>
    internal bool IsProviderCollapsed(string providerId) => _collapsedProviders.Contains(providerId);

    /// <summary>可见的模型分组列表(测试用)。</summary>
    internal static IReadOnlyList<ModelChoice> VisibleModels(ComposerViewModel vm) =>
        vm.Models.Where(m => m.IsAvailable || (vm.SelectedModel is { } current && current.Key == m.Key)).ToList();

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
            _vm.ModelMenuRequested -= OnModelMenuRequested;
            _vm.SlashCommands.CollectionChanged -= OnSlashCommandsChanged;
            _vm.Models.CollectionChanged -= OnModelsChanged;
            _vm.ThinkingChoices.CollectionChanged -= OnThinkingChoicesChanged;
            _vm.PermissionChoices.CollectionChanged -= OnPermissionChoicesChanged;
        }

        _vm = DataContext as ComposerViewModel;
        _attachMenuKey = null;
        _modelPickerKey = null;
        _thinkingMenuKey = null;
        _permissionMenuKey = null;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.DraftReplaced += OnDraftReplaced;
            _vm.ModelMenuRequested += OnModelMenuRequested;
            _vm.SlashCommands.CollectionChanged += OnSlashCommandsChanged;
            _vm.Models.CollectionChanged += OnModelsChanged;
            _vm.ThinkingChoices.CollectionChanged += OnThinkingChoicesChanged;
            _vm.PermissionChoices.CollectionChanged += OnPermissionChoicesChanged;
        }

        // 哪些片段画成 token:只问 ViewModel(ComposerLogic.Mentions 一处规则)
        Input.MentionSource = _vm is { } source ? source.Mentions : null;
        RebuildAttachMenu();
        RebuildModelPicker();
        RebuildThinkingMenu();
        RebuildPermissionMenu();
    }

    /// <summary>选中了文件 / 命令:光标放到末尾并取回焦点(点候选行时焦点在弹层里的按钮上)。</summary>
    private void OnDraftReplaced(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Input.MoveCaretToEnd(focus: true));

    /// <summary><c>/model</c> 没带名字:打开模型弹层(和点模型按钮一样)。</summary>
    private void OnModelMenuRequested() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ModelPopup.IsOpen = true);

    private void OnModelButtonClick(object? sender, RoutedEventArgs e) =>
        ModelPopup.IsOpen = !ModelPopup.IsOpen;

    /// <summary>命令清单到了:文字没变也要重新解析一次,开头的 <c>/命令 </c> 才会变成 token。</summary>
    /// <summary>⌘L:把焦点放进输入框,光标到末尾。</summary>
    public void FocusInput() => Input.MoveCaretToEnd(focus: true);

    private void OnSlashCommandsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Input.Reparse();

    private void OnModelsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildModelPicker();

    private void OnThinkingChoicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildThinkingMenu();

    private void OnPermissionChoicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildPermissionMenu();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComposerViewModel.ShowsSuggestions) or nameof(ComposerViewModel.HasSlashQuery) && _vm is { } open)
        {
            // 辅助技术读到「列表展开着」
            Input.SuggestionsExpanded = open.ShowsSuggestions || open.HasSlashQuery;
        }

        switch (e.PropertyName)
        {
            case nameof(ComposerViewModel.SelectedModel):
                RebuildModelPicker();
                break;
            case nameof(ComposerViewModel.Thinking):
                RebuildThinkingMenu();
                break;
            case nameof(ComposerViewModel.Permission) or nameof(ComposerViewModel.CanUseFull):
                RebuildPermissionMenu();
                break;
        }
    }

    private void RebuildAttachMenu()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        var sources = new[]
        {
            (AttachmentSource.PhotoLibrary, NcwTextKind.AttachmentPhotoLibrary, LucideIconKind.Image),
            (AttachmentSource.Camera, NcwTextKind.AttachmentCamera, LucideIconKind.Camera),
            (AttachmentSource.File, NcwTextKind.AttachmentFile, LucideIconKind.Paperclip),
        }
        .Where(x => vm.Attachments.IsAvailable(x.Item1))
        .ToArray();

        var key = string.Join(",", sources.Select(static s => (int)s.Item1));
        if (key == _attachMenuKey)
        {
            return;
        }

        _attachMenuKey = key;
        MenuRebuildCount++;
        NcwMenus.Populate(AttachMenu, sources.Select(x => new NcwMenus.Item(NcwTexts.Get(x.Item2), vm.Attachments.PickCommand, x.Item1)
        {
            Glyph = Lucide.Geometry(x.Item3),
        }));
    }

    private void RebuildThinkingMenu()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        ThinkingText.Text = NcwTexts.Format(NcwTextKind.ComposerThinkingLabel, ThinkingLevelTextConverter.ShortText(vm.Thinking));
        ThinkingMeter.Level = ThinkingLevelTextConverter.MeterLevel(vm.Thinking);
        ThinkingMeter.IsAuto = vm.Thinking == "auto";
        // 不支持思考的模型只有「关」一档:按钮灰掉,不让人以为能调
        ThinkingMenu.IsEnabled = vm.ThinkingChoices.Count > 1;

        var key = vm.Thinking + "|" + string.Join(",", vm.ThinkingChoices);
        if (key == _thinkingMenuKey)
        {
            return;
        }

        _thinkingMenuKey = key;
        MenuRebuildCount++;
        NcwMenus.Populate(ThinkingMenu, vm.ThinkingChoices.Select(level => new NcwMenus.Item(
            ThinkingLevelTextConverter.ShortText(level), vm.SetThinkingCommand, level) { IsChecked = level == vm.Thinking }));
    }

    private void RebuildPermissionMenu()
    {
        if (_vm is not { } vm)
        {
            return;
        }

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

        var key = vm.Permission + "|" + string.Join(",", vm.PermissionChoices);
        if (key == _permissionMenuKey)
        {
            return;
        }

        _permissionMenuKey = key;
        MenuRebuildCount++;
        NcwMenus.Populate(PermissionMenu, vm.PermissionChoices.Select(mode => new NcwMenus.Item(
            PermissionModeTextConverter.Text(mode), vm.SetPermissionCommand, mode)
        {
            IsChecked = mode == vm.Permission,
            Hint = PermissionModeTextConverter.Hint(mode),
        }));
    }

    /// <summary>模型弹层:隐藏已停用供应商的模型(仅保留当前会话恰好选中的不可用模型),按供应商分组并支持折叠/展开。</summary>
    private void RebuildModelPicker()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        ModelText.Text = vm.SelectedModel is { } model ? model.DisplayName : NcwTexts.Get(NcwTextKind.NoModelSelected);
        ModelGlyph.Text = vm.SelectedModel is { ProviderName.Length: > 0 } provider ? provider.ProviderName[..1].ToUpperInvariant() : "?";

        var visible = VisibleModels(vm);
        var key = (vm.SelectedModel?.Key ?? "") + "|" + string.Join(";", visible.Select(static m => $"{m.Key}:{m.DisplayName}:{m.IsAvailable}:{m.WindowText}:{m.SupportsImages}:{m.CanThink}"));
        if (key == _modelPickerKey)
        {
            return;
        }

        _modelPickerKey = key;
        ModelPickerRebuildCount++;
        ModelListPanel.Children.Clear();

        var groups = visible.GroupBy(static m => m.ProviderId, StringComparer.Ordinal).ToList();
        if (!_hasManualCollapseToggle && groups.Count >= AutoCollapseProviderThreshold && visible.Count > AutoCollapseModelThreshold)
        {
            var activeProvider = vm.SelectedModel?.ProviderId;
            _collapsedProviders.Clear();
            foreach (var group in groups)
            {
                if (!string.Equals(group.Key, activeProvider, StringComparison.Ordinal))
                {
                    _collapsedProviders.Add(group.Key);
                }
            }
        }

        var vision = NcwTexts.Get(NcwTextKind.ModelVision);
        var thinking = NcwTexts.Get(NcwTextKind.ModelThinking);
        var unavailable = NcwTexts.Get(NcwTextKind.ModelUnavailable);

        foreach (var group in groups)
        {
            var providerId = group.Key;
            var providerName = group.First().ProviderName;
            var items = group.ToList();
            var collapsed = _collapsedProviders.Contains(providerId);

            var chevron = new LucideIcon
            {
                Kind = collapsed ? LucideIconKind.ChevronRight : LucideIconKind.ChevronDown,
                Size = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var nameText = new TextBlock
            {
                Classes = { "modelGroupName" },
                Text = providerName,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var countText = new TextBlock
            {
                Classes = { "modelGroupCount" },
                Text = items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6 };
            headerGrid.Children.Add(chevron);
            Grid.SetColumn(nameText, 1);
            headerGrid.Children.Add(nameText);
            Grid.SetColumn(countText, 2);
            headerGrid.Children.Add(countText);

            var rowsPanel = new StackPanel { Spacing = 1, IsVisible = !collapsed };
            var headerButton = new PlainButton
            {
                Classes = { "modelGroupHeader" },
                Content = headerGrid,
                Tag = providerId,
            };
            headerButton.Click += (_, _) =>
            {
                _hasManualCollapseToggle = true;
                var nowCollapsed = _collapsedProviders.Add(providerId);
                if (!nowCollapsed)
                {
                    _collapsedProviders.Remove(providerId);
                }

                rowsPanel.IsVisible = !nowCollapsed;
                chevron.Kind = nowCollapsed ? LucideIconKind.ChevronRight : LucideIconKind.ChevronDown;
            };

            foreach (var m in items)
            {
                var isSelected = vm.SelectedModel is { } current && current.Key == m.Key;
                var hint = string.Join(" · ", new[]
                {
                    m.WindowText,
                    m.SupportsImages ? vision : null,
                    m.CanThink ? thinking : null,
                    m.IsAvailable ? null : unavailable,
                }.Where(static x => !string.IsNullOrEmpty(x)));

                var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("14,*,Auto"), ColumnSpacing = 6 };
                if (isSelected)
                {
                    var check = new LucideIcon
                    {
                        Classes = { "modelRowCheck" },
                        Kind = LucideIconKind.Check,
                        Size = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    rowGrid.Children.Add(check);
                }

                var title = new TextBlock
                {
                    Classes = { "modelRowTitle" },
                    Text = m.DisplayName,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(title, 1);
                rowGrid.Children.Add(title);

                if (hint.Length > 0)
                {
                    var hintText = new TextBlock
                    {
                        Classes = { "modelRowHint" },
                        Text = hint,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(hintText, 2);
                    rowGrid.Children.Add(hintText);
                }

                var rowButton = new PlainButton
                {
                    Classes = { "modelRow" },
                    IsEnabled = m.IsAvailable,
                    Content = rowGrid,
                    Tag = m,
                };
                rowButton.Click += (_, _) =>
                {
                    vm.PickModelCommand.Execute(m);
                    ModelPopup.IsOpen = false;
                };
                rowsPanel.Children.Add(rowButton);
            }

            var groupPanel = new StackPanel { Spacing = 1 };
            groupPanel.Children.Add(headerButton);
            groupPanel.Children.Add(rowsPanel);
            ModelListPanel.Children.Add(groupPanel);
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

        // `/` 菜单打开时:上下移动高亮,Tab 补全高亮的命令;Enter 也补全,除非已经打全了一个命令名(那就是要执行它)
        if (_vm is { HasSlashQuery: true } slash)
        {
            switch (e.Key)
            {
                case Key.Down:
                    slash.MoveSlash(1);
                    e.Handled = true;
                    return;
                case Key.Up:
                    slash.MoveSlash(-1);
                    e.Handled = true;
                    return;
                case Key.Tab or Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && slash.AcceptSlash(enter: e.Key == Key.Enter):
                    e.Handled = true;
                    return;
            }
        }

        if (OperatingSystem.IsMacOS() && e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control && !Input.IsReadOnly)
        {
            StartPaste();
            e.Handled = true;
            return;
        }

        var steerModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key is Key.Enter && e.KeyModifiers == steerModifier && _vm is { State.CanSteer: true } steerVm)
        {
            steerVm.SteerCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _vm is { } vm && vm.State.Primary is ComposerPrimary.Send or ComposerPrimary.Queue)
        {
            vm.SubmitCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCardDragOver(object? sender, DragEventArgs e)
    {
        if (_vm is null || Input.IsReadOnly)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var supported = e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(DataFormat.Bitmap) || e.DataTransfer.Contains(DataFormat.Text);
        e.DragEffects = supported ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnCardDrop(object? sender, DragEventArgs e)
    {
        if (_vm is not { } vm || Input.IsReadOnly)
        {
            return;
        }

        e.Handled = true;
        try
        {
            await ComposerClipboard.DropAsync(e.DataTransfer, vm, Input.InsertText);
        }
        catch (Exception)
        {
            // 拖拽内容读取失败时不崩界面
        }
    }

    private void OnInputPasting(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || Input.IsReadOnly || TopLevel.GetTopLevel(this)?.Clipboard is null)
        {
            return;
        }

        e.Handled = true;
        StartPaste();
    }

    private void StartPaste() => _pasteTask = PasteFromClipboardAsync();

    private async Task PasteFromClipboardAsync()
    {
        if (_vm is not { } vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            using var data = await clipboard.TryGetDataAsync();
            if (data is null)
            {
                return;
            }

            await ComposerClipboard.PasteAsync(data, vm, Input.InsertText);
        }
        catch (Exception)
        {
            // 剪贴板不可用或平台读取异常时不崩界面
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
