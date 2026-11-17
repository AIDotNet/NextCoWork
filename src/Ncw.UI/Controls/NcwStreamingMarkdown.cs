using Avalonia;

namespace Ncw.UI.Controls;

/// <summary>
/// 助手回复的 Markdown:流式中只把<b>新增的那一截</b>推给 Arc 的流(<see cref="TKCode.Arc.Markdown.Append"/>,StreamMark 增量解析尾部),
/// 而不是每个增量都把整段文字重新赋给 <c>Text</c>(每次整篇重新解析,回复越长越慢)。
/// <list type="bullet">
/// <item><see cref="IsLive"/> 为真且新文字以已推送的文字开头:只推差量。</item>
/// <item>转为落库(<see cref="IsLive"/> 变假)且文字仍是延续:推完剩下的、<see cref="TKCode.Arc.Markdown.Complete"/>,保留流的文档,不闪。</item>
/// <item>其余(容器被回收给另一行、编辑后文字整个换掉、落库文字和流式的对不上):直接设 <c>Text</c>(会结束正在进行的流)。</item>
/// </list>
/// 打字机节奏(<c>IsPaced</c>)只在从头开始流时打开;中途接上(容器回收后重新实例化一行还在流的回复)关掉,不把已有的整段再「打」一遍。
/// </summary>
public class NcwStreamingMarkdown : NcwMarkdown
{
    /// <summary>从头开始流时,已有文字不超过这么长才用打字机节奏。</summary>
    private const int PacedStartLimit = 160;

    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<NcwStreamingMarkdown, string?>(nameof(Source));

    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<NcwStreamingMarkdown, bool>(nameof(IsLive));

    private string _pushed = "";
    private bool _streaming;

    /// <summary>要显示的全文(绑定行的文字)。</summary>
    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>这段文字还在流(绑定行的 <c>IsStreaming</c>)。</summary>
    public bool IsLive
    {
        get => GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    /// <summary>现在是不是在走增量流(测试 / 诊断用)。</summary>
    public bool IsAppending => _streaming;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == IsLiveProperty)
        {
            Sync();
        }
    }

    private void Sync()
    {
        var source = Source ?? "";
        var continues = _streaming && source.StartsWith(_pushed, StringComparison.Ordinal);
        if (IsLive)
        {
            if (continues)
            {
                if (source.Length > _pushed.Length)
                {
                    Append(source[_pushed.Length..]);
                    _pushed = source;
                }

                return;
            }

            // 开始一段新的流(或接上一行已经流了一半的回复)
            IsPaced = source.Length <= PacedStartLimit;
            _streaming = true;
            _pushed = source;
            if (source.Length > 0)
            {
                Append(source);
            }
            else
            {
                SetCurrentValue(TextProperty, "");
            }

            return;
        }

        if (continues)
        {
            if (source.Length > _pushed.Length)
            {
                Append(source[_pushed.Length..]);
            }

            Complete();
            _streaming = false;
            _pushed = source;
            return;
        }

        _streaming = false;
        _pushed = source;
        if (Stream is not null || !string.Equals(Text, source, StringComparison.Ordinal))
        {
            SetCurrentValue(TextProperty, source); // 设 Text 会结束正在进行的流
        }
    }
}
