using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.ViewModels;
using Ncw.UI.Localization;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.DesignTokens;
using TKCode.Terminal.Avalonia;
using TKCode.Terminal.Core;

namespace Ncw.UI.Controls;

/// <summary>
/// 终端控件(TKCode.Arc.Terminal 的 <see cref="TerminalView"/> + libghostty-vt 的 <see cref="TerminalCore"/>)。
/// 本类是 <see cref="ITerminalSurface"/> 的 UI 实现:<see cref="Write"/> / <see cref="Reset"/> 可以从任意线程调用(切到 UI 线程、保持顺序),
/// 键盘输入按终端当前的模式编码成 PTY 要的字节后经 <see cref="Input"/> 交出去,显示区能容纳的行列数变了经 <see cref="GridChanged"/> 交出去。
/// 颜色全部来自 Arc token(默认前景 / 背景、ANSI 16 色由 token 调和,256 色立方体和灰阶按标准公式算),主题切换时重算。
/// 本机没有 libghostty-vt 的原生库(不支持的平台)时 <see cref="IsUnavailable"/> 为真,控件什么也不画,页签上仍有元数据和操作。
/// </summary>
public sealed class NcwTerminal : Border, ITerminalSurface
{
    private readonly TerminalView? _view;
    private readonly InputEncoder? _encoder;
    private TerminalCore? _core;
    private Effects? _effects;
    private bool _captureScheduled;
    private bool _replaying;
    private bool _replayNext;
    private (int Column, int Row)? _anchor;
    private bool _dragged;

    public NcwTerminal()
    {
        Focusable = true;
        ClipToBounds = true;
        try
        {
            _view = new TerminalView { Padding = 6 };
            _encoder = new InputEncoder();
            Child = _view;
            _view.KeyPressed += OnKeyPressed;
            _view.TextEntered += OnTextEntered;
            _view.PointerChanged += OnPointer;
            Background = Brushes.Transparent;
            // 右键菜单(issue #194):按下右键时按当前状态重建(有没有选中决定「复制」能不能点)。隧道 + 已处理也收,终端视图吃掉指针事件也不影响
            AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            RebuildMenu();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            IsUnavailable = true;
        }
    }

    /// <summary>终端的原生库加载失败(平台不支持)。</summary>
    public bool IsUnavailable { get; private set; }

    /// <summary>这个终端跑的 shell(页签元数据)。清屏后要不要发 Ctrl+L 让 shell 重画提示符取决于它:cmd 不认 Ctrl+L。</summary>
    public string? Shell { get; set; }

    /// <summary>有没有选中的文字。</summary>
    public bool HasSelection => _core?.HasSelection ?? false;

    public (int Columns, int Rows) Grid { get; private set; } = (80, 24);

    public event Action<byte[]>? Input;

    public event Action<int, int>? GridChanged;

    /// <summary>字体:跟着 <see cref="TextElement.FontFamilyProperty"/> / <see cref="TextElement.FontSizeProperty"/>(XAML 里绑 Arc 的等宽字体 token)。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_view is null)
        {
            return;
        }

        if (change.Property == TextElement.FontFamilyProperty && change.NewValue is FontFamily family)
        {
            _view.FontFamily = family;
        }
        else if (change.Property == TextElement.FontSizeProperty && change.NewValue is double size)
        {
            _view.FontSize = size;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_view is null)
        {
            return;
        }

        _view.FontFamily = TextElement.GetFontFamily(this);
        _view.FontSize = TextElement.GetFontSize(this);
        EnsureCore();
        ActualThemeVariantChanged += OnThemeChanged;
        ApplyPalette();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyPalette();
        Capture(force: true);
    }

    private void EnsureCore()
    {
        if (_core is not null || _view is null)
        {
            return;
        }

        try
        {
            _effects = new Effects(this);
            var options = new TerminalCoreOptions(new TerminalSize(Grid.Columns, Grid.Rows, Math.Max(1, _view.Metrics?.CellWidth ?? 8), Math.Max(1, _view.Metrics?.CellHeight ?? 16)));
            _core = TerminalCore.Create(in options, _effects);
            ApplyPalette();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or GhosttyException)
        {
            IsUnavailable = true;
        }
    }

    // ───────────────────────── 输出(任意线程) ─────────────────────────

    public void Reset()
    {
        Dispatcher.UIThread.Post(() =>
        {
            EnsureCore();
            if (_core is null)
            {
                return;
            }

            _core.Reset();
            _core.ClearSelection();
            _replayNext = true; // 紧跟着的那次 Write 是整份缓冲的回放
            Capture(force: true);
        });
    }

    public void Write(byte[] data)
    {
        Dispatcher.UIThread.Post(() =>
        {
            EnsureCore();
            if (_core is null)
            {
                return;
            }

            _replaying = _replayNext;
            _replayNext = false;
            try
            {
                _core.Write(data);
            }
            finally
            {
                _replaying = false;
            }

            if (!_captureScheduled)
            {
                _captureScheduled = true;
                Dispatcher.UIThread.Post(() =>
                {
                    _captureScheduled = false;
                    Capture(force: false);
                }, DispatcherPriority.Render);
            }
        });
    }

    private void Capture(bool force)
    {
        if (_core is null || _view is null)
        {
            return;
        }

        var frame = _core.CaptureFrame(force);
        if (frame is not null)
        {
            _view.Snapshot = frame;
        }
    }

    // ───────────────────────── 大小 ─────────────────────────

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (_view is { } view && _core is { } core && finalSize.Width > 0 && finalSize.Height > 0)
        {
            var grid = view.GridSizeFor(finalSize);
            if (grid.Columns > 0 && grid.Rows > 0 && (grid.Columns, grid.Rows) != Grid)
            {
                Grid = (grid.Columns, grid.Rows);
                core.Resize(grid);
                Capture(force: true);
                GridChanged?.Invoke(grid.Columns, grid.Rows);
            }
        }

        return size;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        // 没有限定大小(放在 ScrollViewer 里)时给一个能容纳 80x24 的默认大小
        return new Size(double.IsInfinity(availableSize.Width) ? Math.Max(size.Width, 480) : size.Width, double.IsInfinity(availableSize.Height) ? Math.Max(size.Height, 240) : size.Height);
    }

    // ───────────────────────── 输入 ─────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    private void OnKeyPressed(object? sender, TerminalKeyEventArgs e)
    {
        if (_core is null || _encoder is null)
        {
            return;
        }

        var input = e.Input;
        var mac = OperatingSystem.IsMacOS();
        var command = e.Modifiers.HasFlag(mac ? KeyModifiers.Meta : KeyModifiers.Control | KeyModifiers.Shift);
        if (command && input.Action != KeyAction.Release)
        {
            if (input.Key == TerminalKey.V)
            {
                _ = PasteAsync();
                e.Handled = true;
                return;
            }

            if (input.Key == TerminalKey.C)
            {
                // 没有选中时也吞掉:否则 Ctrl+Shift+C 落到编码器,给 shell 发了一个 ^C
                CopySelection();
                e.Handled = true;
                return;
            }
        }

        // Windows / Linux:有选中时 Ctrl+C 是复制(和 Windows Terminal 一样),没有选中才是中断
        if (!mac && e.Modifiers == KeyModifiers.Control && input.Key == TerminalKey.C && input.Action != KeyAction.Release && _core.HasSelection)
        {
            CopySelection();
            _core.ClearSelection();
            Capture(force: true);
            e.Handled = true;
            return;
        }

        _encoder.Apply(_core.ReadInputModes());
        Span<byte> buffer = stackalloc byte[128];
        var key = input;
        var length = _encoder.EncodeKey(in key, buffer);
        if (length > 0)
        {
            Input?.Invoke(buffer[..length].ToArray());
            e.Handled = true;
        }
    }

    private void OnTextEntered(object? sender, string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Input?.Invoke(Encoding.UTF8.GetBytes(text));
        }
    }

    /// <summary>把选中的文字放进剪贴板。没有选中时什么也不做,返回 false。</summary>
    public bool CopySelection()
    {
        if (_core is not { HasSelection: true } core)
        {
            return false;
        }

        var text = core.GetSelectionText(TextFormat.Plain);
        if (string.IsNullOrEmpty(text) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return false;
        }

        _ = clipboard.SetTextAsync(text);
        return true;
    }

    /// <summary>
    /// 清屏(issue #194):清掉本地显示的屏幕和回滚,再给 shell 发 Ctrl+L 让它重画提示符(bash / zsh / fish / PowerShell 都认;cmd 不认,不发)。
    /// 只动这块显示面:设备上的输出缓冲不清,切走页签再回来、重连后会按设备缓冲重放。
    /// </summary>
    public void Clear()
    {
        if (_core is null)
        {
            return;
        }

        // 解析器停在半截转义序列里时注入清屏序列会切断程序的输出;这时只靠 shell 自己清
        if (_core.IsParserGround)
        {
            _core.Clear(ClearTarget.All);
        }

        _core.ClearSelection();
        Capture(force: true);
        if (!IsCmd(Shell))
        {
            Input?.Invoke([0x0c]);
        }
    }

    /// <summary>shell 是设备报的(可能是从 Mac 控制端看 Windows 设备),两种路径分隔符都要认。</summary>
    private static bool IsCmd(string? shell)
    {
        if (string.IsNullOrWhiteSpace(shell))
        {
            return false;
        }

        var name = shell.Trim().Trim('"');
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.Equals("cmd", StringComparison.OrdinalIgnoreCase) || name.StartsWith("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("cmd ", StringComparison.OrdinalIgnoreCase);
    }

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            RebuildMenu();
        }
    }

    private void RebuildMenu()
    {
        if (IsUnavailable)
        {
            return;
        }

        NcwMenus.Attach(this,
        [
            new(NcwTexts.Get(NcwTextKind.TerminalCopy), new RelayCommand(() => CopySelection())) { IsDisabled = !HasSelection, Glyph = ArcIcons.Copy },
            new(NcwTexts.Get(NcwTextKind.TerminalPaste), new RelayCommand(() => _ = PasteAsync())) { Glyph = Lucide.Geometry(LucideIconKind.ClipboardPaste) },
            new(NcwTexts.Get(NcwTextKind.TerminalClear), new RelayCommand(Clear), SeparatorBefore: true) { Glyph = Lucide.Geometry(LucideIconKind.Eraser) },
        ]);
    }

    public async Task PasteAsync()
    {
        if (_core is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var bytes = PasteEncoder.Encode(Encoding.UTF8.GetBytes(text), _core.ReadInputModes().BracketedPaste);
        Input?.Invoke(bytes);
    }

    private void OnPointer(object? sender, TerminalPointerEventArgs e)
    {
        if (_core is null)
        {
            return;
        }

        switch (e.Kind)
        {
            case TerminalPointerKind.Wheel:
                _core.ScrollViewport(ViewportScroll.Lines((long)-Math.Round(e.WheelDelta * 3)));
                Capture(force: true);
                e.Handled = true;
                break;
            case TerminalPointerKind.Press when e.Mouse.Button == TKCode.Terminal.Core.MouseButton.Left:
                _anchor = (e.Cell.Column, e.Cell.Row);
                _dragged = false;
                break;
            case TerminalPointerKind.Move when _anchor is { } anchor:
                _dragged = true;
                var request = SelectionRequest.Range(new ViewportPoint(anchor.Column, anchor.Row), e.Cell, false);
                _core.Select(in request);
                Capture(force: true);
                break;
            case TerminalPointerKind.Release:
                if (_anchor is not null && !_dragged && _core.HasSelection)
                {
                    _core.ClearSelection();
                    Capture(force: true);
                }

                _anchor = null;
                break;
        }
    }

    // ───────────────────────── 颜色(Arc token) ─────────────────────────

    private void ApplyPalette()
    {
        if (_core is null)
        {
            return;
        }

        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        _core.SetDefaultColors(BuildPalette(), dark ? ColorScheme.Dark : ColorScheme.Light);
    }

    private TerminalPalette BuildPalette()
    {
        TerminalRgb Token(ArcDesignTokenKind kind)
        {
            var c = ArcTokens.GetColor(this, kind);
            return new TerminalRgb(c.R, c.G, c.B);
        }

        static TerminalRgb Mix(TerminalRgb a, TerminalRgb b, double t) => new(
            (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

        var foreground = Token(ArcDesignTokenKind.Foreground);
        var background = Token(ArcDesignTokenKind.SurfaceMuted);
        var red = Token(ArcDesignTokenKind.Danger);
        var green = Token(ArcDesignTokenKind.Success);
        var yellow = Token(ArcDesignTokenKind.Warning);
        var blue = Token(ArcDesignTokenKind.Accent);
        var magenta = Mix(red, blue, 0.5);
        var cyan = Mix(green, blue, 0.5);
        var colors = new TerminalRgb[256];
        TerminalRgb[] ansi = [Mix(background, foreground, 0.1), red, green, yellow, blue, magenta, cyan, Mix(background, foreground, 0.75)];
        for (var i = 0; i < 8; i++)
        {
            colors[i] = ansi[i];
            colors[i + 8] = Mix(ansi[i], foreground, 0.28);
        }

        // 6x6x6 立方体和 24 级灰阶:xterm 的标准公式
        for (var i = 0; i < 216; i++)
        {
            static byte Level(int n) => n == 0 ? (byte)0 : (byte)(55 + 40 * n);
            colors[16 + i] = new TerminalRgb(Level(i / 36), Level(i / 6 % 6), Level(i % 6));
        }

        for (var i = 0; i < 24; i++)
        {
            var v = (byte)(8 + 10 * i);
            colors[232 + i] = new TerminalRgb(v, v, v);
        }

        return new TerminalPalette(foreground, background, foreground, colors);
    }

    // ───────────────────────── 终端副作用 ─────────────────────────

    private sealed class Effects(NcwTerminal owner) : ITerminalEffects
    {
        public void WriteToPty(ReadOnlySpan<byte> data)
        {
            // 终端对查询的应答(光标位置、设备属性……)。回放缓冲时不应答:缓冲里旧的查询不该再让 shell 收到一遍
            if (!owner._replaying && !data.IsEmpty)
            {
                owner.Input?.Invoke(data.ToArray());
            }
        }

        public void Bell()
        {
        }

        public void TitleChanged(string title)
        {
        }

        public void WorkingDirectoryChanged(string path)
        {
        }

        public ClipboardWriteResult ClipboardWrite(ClipboardTarget target, string mime, ReadOnlySpan<byte> data) => ClipboardWriteResult.Denied;

        public ClipboardReadDecision ClipboardRead(ref PendingClipboardRead request) => ClipboardReadDecision.Denied;

        public void Notification(string title, string body)
        {
        }

        public void Progress(ProgressState state, int? percent)
        {
        }

        public void SemanticPrompt(ref SemanticPromptEvent prompt)
        {
        }

        public void RenderHold(bool held)
        {
        }

        public void ResizeWindowRequested(int columns, int rows)
        {
        }

        public void FullReset()
        {
        }

        public void UnknownOsc(ReadOnlySpan<byte> data, bool bell)
        {
        }
    }
}
