using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;

namespace Ncw.UI.Controls;

/// <summary>
/// 思考块展开后的正文(docs/design/03 §6「思考块」)。思考可能有几万字,而且是流式一点点长出来的,原来一个 <c>SelectableTextBlock</c> 装全文:
/// 每个增量都把整段重新排版,越长越慢(16 KB 时一帧就要 40 多毫秒)。现在:
/// <list type="bullet">
/// <item><b>限高</b>:最高 <see cref="MaxBodyHeight"/>,超出的在框里滚动,不把整个转录撑长;</item>
/// <item><b>只建看得见的</b>:文字按段落切成块(过长的段落再按 <see cref="MaxChunk"/> 字左右切开),放进虚拟化列表,框里看得见的那几块才有控件;</item>
/// <item><b>增量</b>:新文字是旧文字的延续时,只重切最后一块(替换最后一项、追加新项),前面的块不动、不重新排版;
/// 折叠着(看不见、不测量)时什么都不做,等下一次真的要显示时一次补上;</item>
/// <item><b>跟随</b>:流式时停在最新内容;用户往上翻就不再抢位置,翻回底部恢复跟随。已经结束的思考展开时从头显示。</item>
/// </list>
/// </summary>
public partial class NcwThinkingView : UserControl
{
    /// <summary>正文框的最大高度(转录的高度估算也按它封顶,见 <c>TranscriptHeights</c>)。</summary>
    public const double MaxBodyHeight = 240;

    /// <summary>一块最多这么多字;更长的段落在这附近找空白或标点切开(切点处会多一个换行,思考是辅助信息,可以接受)。</summary>
    internal const int MaxChunk = 1000;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<NcwThinkingView, string?>(nameof(Text));

    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<NcwThinkingView, bool>(nameof(IsLive));

    private readonly AvaloniaList<string> _chunks = [];
    private string _source = "";
    private int _tailStart;
    private bool _dirty;
    private bool _follow;
    private bool _settingOffset;

    public NcwThinkingView()
    {
        InitializeComponent();
        Scroll.MaxHeight = MaxBodyHeight;
        Chunks.ItemsSource = _chunks;
        Scroll.PropertyChanged += OnScrollPropertyChanged;
    }

    /// <summary>思考的全文(绑定行的文字)。</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>还在流(绑定行的 <c>IsStreaming</c>)。</summary>
    public bool IsLive
    {
        get => GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    /// <summary>切好的块(测试用)。</summary>
    internal IReadOnlyList<string> ChunkTexts => _chunks;

    /// <summary>是否跟随到最新(测试用)。</summary>
    internal bool IsFollowing => _follow;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            // 只记下来:看不见时不测量,也就不切块;真要显示时(MeasureOverride)一次补上
            _dirty = true;
            InvalidateMeasure();
        }
        else if (change.Property == IsLiveProperty && !IsLive)
        {
            _follow = false;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_dirty)
        {
            Sync();
        }

        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (_follow)
        {
            // 排布完滚动框的范围才是新的;往上翻的判断见 OnScrollPropertyChanged
            FollowToBottom();
        }

        return size;
    }

    private void FollowToBottom()
    {
        var bottom = Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height);
        if (Math.Abs(Scroll.Offset.Y - bottom) > 0.5)
        {
            SetOffset(new Vector(0, bottom));
        }
    }

    private void Sync()
    {
        _dirty = false;
        var text = Text ?? "";
        if (_chunks.Count > 0 && text.Length >= _source.Length && text.StartsWith(_source, StringComparison.Ordinal))
        {
            if (text.Length == _source.Length)
            {
                return;
            }

            // 延续:从最后一块的起点重新切,替换最后一项、追加新项;前面的块不动
            var pieces = Split(text, _tailStart);
            _chunks[^1] = Piece(text, pieces[0]);
            for (var i = 1; i < pieces.Count; i++)
            {
                _chunks.Add(Piece(text, pieces[i]));
            }

            _tailStart = pieces[^1].Start;
        }
        else
        {
            // 换了一段文字(容器回收给了另一行、编辑后整个换掉):从头切,流式中跟到最新,已结束的从头看
            var pieces = Split(text, 0);
            _chunks.Clear();
            _chunks.AddRange(pieces.Select(p => Piece(text, p)));
            _tailStart = pieces[^1].Start;
            _follow = IsLive;
            SetOffset(default);
        }

        _source = text;
    }

    /// <summary>
    /// 跟随的开关看滚动位置本身的变化(同步触发),不看 <c>ScrollChanged</c>:后者在布局之后合并发出,
    /// 用户往上翻的那一下和同一轮布局里的跟随会合成一个事件,分不出来。自己设的位置(跟随、从头显示)不算。
    /// </summary>
    private void OnScrollPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty)
        {
            // 虚拟化列表量出真实高度后会修正范围(不一定再经过本控件的排布):跟随时重新贴到底部
            if (_follow)
            {
                FollowToBottom();
            }

            return;
        }

        if (e.Property != ScrollViewer.OffsetProperty || _settingOffset)
        {
            return;
        }

        var previous = e.GetOldValue<Vector>();
        var current = e.GetNewValue<Vector>();
        var bottom = Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height);
        if (current.Y >= bottom - 2)
        {
            _follow = true;            // 在底部(包括用户翻回底部):跟随
        }
        else if (current.Y < previous.Y - 0.5)
        {
            _follow = false;           // 往上了:用户在往回看,不抢
        }
    }

    private void SetOffset(Vector offset)
    {
        _settingOffset = true;
        try
        {
            Scroll.Offset = offset;
        }
        finally
        {
            _settingOffset = false;
        }
    }

    private static string Piece(string text, (int Start, int End) piece) =>
        text.AsSpan(piece.Start, piece.End - piece.Start).TrimEnd('\r').ToString();

    /// <summary>
    /// 把 <paramref name="text"/> 从 <paramref name="from"/> 起切成块:每个换行一块(换行本身不进块),超过 <see cref="MaxChunk"/> 的段落再切。
    /// 至少返回一块;最后一块延伸到结尾(可能是空串,例如文字以换行结尾)。纯函数。
    /// </summary>
    internal static List<(int Start, int End)> Split(string text, int from)
    {
        var result = new List<(int Start, int End)>();
        var start = from;
        while (true)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline;
            while (end - start > MaxChunk)
            {
                var cut = BreakAt(text, start, start + MaxChunk);
                result.Add((start, cut));
                start = cut;
            }

            result.Add((start, end));
            if (newline < 0)
            {
                return result;
            }

            start = newline + 1;
        }
    }

    /// <summary>在 (start, limit] 的后半段里从后往前找空白或标点,切在它后面;找不到就切在 limit(不切开代理对)。</summary>
    private static int BreakAt(string text, int start, int limit)
    {
        for (var i = limit; i > start + MaxChunk / 2; i--)
        {
            var c = text[i - 1];
            if (char.IsWhiteSpace(c) || c is '。' or '，' or '；' or '！' or '？' or '、' or ',' or '.' or ';' or '!' or '?')
            {
                return i;
            }
        }

        return char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
    }
}
