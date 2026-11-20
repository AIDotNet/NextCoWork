using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;
using Ncw.UI.Controls;
using Ncw.UI.Converters;
using Ncw.UI.Localization;

namespace Ncw.UI.Views.Dialogs;

/// <summary>
/// 「供应商调试」窗口的内容。这里只放纯 UI 的事:思考下拉的文字、模型候选菜单、模型候选那一行提示、状态行的小标签、
/// 失败说明的拼接、标签页的显隐、两栏的高度跟窗口走、事件流跟到底。意图都是 ViewModel 的命令。
/// </summary>
public partial class ProviderDebugDialog : UserControl
{
    /// <summary>窗口里除了两栏以外的高度(弹窗上下边距、标题栏、按钮行),同 <see cref="ProviderEditorDialog"/>。</summary>
    private const double ChromeHeight = 260;

    private ProviderDebugViewModel? _vm;

    public ProviderDebugDialog()
    {
        InitializeComponent();
        ThinkingSelect.ItemsSource = ProviderDebugViewModel.ThinkingLevels;
        ThinkingSelect.TextSelector = item => ThinkingText(item as string);
    }

    internal static string ThinkingText(string? level) => level switch
    {
        DebugThinking.Off => NcwTexts.Get(NcwTextKind.ProviderDebugThinkingOff),
        DebugThinking.Low => NcwTexts.Get(NcwTextKind.ProviderDebugThinkingLow),
        DebugThinking.Medium => NcwTexts.Get(NcwTextKind.ProviderDebugThinkingMedium),
        DebugThinking.High => NcwTexts.Get(NcwTextKind.ProviderDebugThinkingHigh),
        _ => NcwTexts.Get(NcwTextKind.ProviderDebugThinkingAuto),
    };

    /// <summary>失败类别 → 一句带修法的话(<see cref="DebugFailures"/>;认不出的给通用的那句)。</summary>
    internal static string FailureText(string? failure) => NcwTexts.Get(failure switch
    {
        DebugFailures.Rejected => NcwTextKind.ProviderDebugFailRejected,
        DebugFailures.NotFound => NcwTextKind.ProviderDebugFailNotFound,
        DebugFailures.Network => NcwTextKind.ProviderDebugFailNetwork,
        DebugFailures.Timeout => NcwTextKind.ProviderDebugFailTimeout,
        DebugFailures.Incomplete => NcwTextKind.ProviderDebugFailIncomplete,
        DebugFailures.Stream => NcwTextKind.ProviderDebugFailStream,
        DebugFailures.Local => NcwTextKind.ProviderDebugFailLocal,
        _ => NcwTextKind.ProviderDebugFailHttp,
    });

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.ModelCandidates.CollectionChanged -= OnCandidatesChanged;
            _vm.Events.CollectionChanged -= OnEventsChanged;
        }

        _vm = DataContext as ProviderDebugViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _vm.ModelCandidates.CollectionChanged += OnCandidatesChanged;
            _vm.Events.CollectionChanged += OnEventsChanged;
        }

        UpdateModelMenu();
        UpdateModelsHint();
        UpdateResult();
    }

    /// <summary>打开后焦点放在模型框上。</summary>
    internal void FocusFirst() => ModelInput.Focus();

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProviderDebugViewModel.IsLoadingModels) or nameof(ProviderDebugViewModel.ModelAttempts) or nameof(ProviderDebugViewModel.ModelsErrorKey)
                or nameof(ProviderDebugViewModel.FetchedCount):
                UpdateModelsHint();
                break;
            case nameof(ProviderDebugViewModel.Result) or nameof(ProviderDebugViewModel.IsRunning) or nameof(ProviderDebugViewModel.WasStopped)
                or nameof(ProviderDebugViewModel.LiveStatus) or nameof(ProviderDebugViewModel.SelectedTab) or nameof(ProviderDebugViewModel.ReplyText)
                or nameof(ProviderDebugViewModel.ToolCallsText) or nameof(ProviderDebugViewModel.ThinkingText):
                UpdateResult();
                break;
        }
    }

    private void OnCandidatesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateModelMenu();

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateResult();
        if (_vm is { IsRunning: true, SelectedTab: DebugTab.Events } && e.Action == NotifyCollectionChangedAction.Add)
        {
            EventScroll.ScrollToEnd();
        }
    }

    private void UpdateModelMenu()
    {
        var vm = _vm;
        ModelMenuButton.IsEnabled = vm is { ModelCandidates.Count: > 0 };
        if (vm is null)
        {
            return;
        }

        NcwMenus.Populate(ModelMenu, vm.ModelCandidates.Select(model => new NcwMenus.Item(model, new PickModel(vm, model)) { IsChecked = model == vm.Model }));
    }

    private void UpdateModelsHint()
    {
        if (_vm is not { } vm)
        {
            ModelsHint.Text = "";
            return;
        }

        ModelsHint.Text = vm switch
        {
            { IsLoadingModels: true } => NcwTexts.Get(NcwTextKind.ProviderDebugModelsLoading),
            { ModelsErrorKey: { } key } => ProviderErrorTextConverter.Text(key),
            { ModelsUnavailable: true } => NcwTexts.Format(NcwTextKind.ProviderDebugModelsUnavailable,
                string.Join(" / ", vm.ModelAttempts.Select(a => a.Status is { } s ? $"{a.Family} {s}" : $"{a.Family} {ProviderErrorTextConverter.Text(a.ErrorKey)}"))),
            { FetchedCount: > 0 } => NcwTexts.Format(NcwTextKind.ProviderDebugModelsFetched, vm.FetchedCount),
            _ => "",
        };
        ModelsHint.IsVisible = ModelsHint.Text.Length > 0;
    }

    private void UpdateResult()
    {
        var vm = _vm;
        var result = vm?.Result;
        var hasAnything = result is not null || vm?.Events.Count > 0;

        // 还没发过 / 正在等第一条事件 / 停了还什么都没收到:占位
        Placeholder.IsVisible = !hasAnything;
        PlaceholderSpinner.IsVisible = vm?.IsRunning == true;
        PlaceholderText.Text = NcwTexts.Get(vm switch
        {
            { IsRunning: true } => NcwTextKind.ProviderDebugRunning,
            { WasStopped: true } => NcwTextKind.ProviderDebugStopped,
            _ => NcwTextKind.ProviderDebugEmpty,
        });

        var tab = vm?.SelectedTab ?? DebugTab.Reply;
        ReplyPane.IsVisible = hasAnything && tab == DebugTab.Reply;
        RequestPane.IsVisible = hasAnything && tab == DebugTab.Request;
        ResponsePane.IsVisible = hasAnything && tab == DebugTab.Response;
        EventsPane.IsVisible = hasAnything && tab == DebugTab.Events;

        NoReplyText.IsVisible = result is not null && result.Text.Length == 0 && result.Thinking.Length == 0 && result.ToolCalls.Length == 0;
        NoResponseText.IsVisible = result is { Status: null };
        EventsTruncatedText.IsVisible = result is { EventsTruncated: true };
        EventsTruncatedText.Text = NcwTexts.Format(NcwTextKind.ProviderDebugEventsTruncated, ProviderDebugViewModel.MaxRows);

        FailureLine.IsVisible = result is { Ok: false };
        FailureLine.Text = result is { Ok: false } failed
            ? FailureText(failed.Failure) + (failed.Detail is { Length: > 0 } detail ? $" {detail}" : "")
            : vm?.WasStopped == true && hasAnything ? NcwTexts.Get(NcwTextKind.ProviderDebugStopped) : "";
        if (vm?.WasStopped == true && hasAnything)
        {
            FailureLine.IsVisible = true;
        }

        BuildStatusRow(vm, result);
    }

    private void BuildStatusRow(ProviderDebugViewModel? vm, ProviderDebugResultDto? result)
    {
        StatusRow.Children.Clear();
        if (vm is null || (result is null && !vm.IsRunning && vm.Events.Count == 0))
        {
            StatusRow.IsVisible = false;
            return;
        }

        StatusRow.IsVisible = true;
        if (result is not null)
        {
            var tone = result.Ok ? "ok" : "bad";
            var head = result.Status is { } status ? status.ToString(CultureInfo.InvariantCulture) : NcwTexts.Get(NcwTextKind.ProviderDebugFailed);
            StatusRow.Children.Add(Chip($"{head} · {NcwTexts.Get(result.Ok ? NcwTextKind.ProviderDebugOk : NcwTextKind.ProviderDebugFailed)}", tone));
            if (result.FirstTokenMs is { } first)
            {
                StatusRow.Children.Add(Chip(NcwTexts.Format(NcwTextKind.ProviderDebugFirstToken, first)));
            }

            if (result.TotalMs is { } total)
            {
                StatusRow.Children.Add(Chip(NcwTexts.Format(NcwTextKind.ProviderDebugTotal, total)));
            }

            if (result.Usage is { } usage)
            {
                StatusRow.Children.Add(Chip(NcwTexts.Format(NcwTextKind.ProviderDebugTokens, usage.Input, usage.Output)));
            }

            if (result.StopReason is { } stop)
            {
                StatusRow.Children.Add(Chip(NcwTexts.Format(NcwTextKind.ProviderDebugStopReason, stop)));
            }

            if (result.ResponseModel is { Length: > 0 } model && model != result.Model)
            {
                StatusRow.Children.Add(Chip(model));
            }

            return;
        }

        // 跑着:状态码到了就先显示,否则只有「正在等待响应」
        StatusRow.Children.Add(Chip(vm.LiveStatus is { } live ? live.ToString(CultureInfo.InvariantCulture) : NcwTexts.Get(NcwTextKind.ProviderDebugRunning), "warn"));
    }

    private static Border Chip(string text, string? tone = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (tone is not null)
        {
            row.Children.Add(new NcwStatusDot { Tone = tone, VerticalAlignment = VerticalAlignment.Center });
        }

        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        label.Classes.Add("hint");
        row.Children.Add(label);
        var chip = new Border { Child = row };
        chip.Classes.Add("chip");
        return chip;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            Fit(top);
            top.PropertyChanged += OnTopLevelChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.PropertyChanged -= OnTopLevelChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnTopLevelChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty && sender is TopLevel top)
        {
            Fit(top);
        }
    }

    /// <summary>两栏的高度 = 窗口高 − 标题和按钮行,夹在 320 到 640 之间(按钮行永远在窗口里)。</summary>
    private void Fit(TopLevel top) => Root.Height = Math.Clamp(top.Bounds.Height - ChromeHeight, 320, 640);

    /// <summary>模型候选菜单的一行:点了就把模型框填成它。</summary>
    private sealed class PickModel(ProviderDebugViewModel vm, string model) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => vm.Model = model;
    }
}
