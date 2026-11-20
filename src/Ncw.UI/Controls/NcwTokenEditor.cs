using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Ncw.Client.ViewModels;

namespace Ncw.UI.Controls;

/// <summary>
/// Arc 的 token 编辑器(输入框的书写区):文字里的 <c>@文件</c>、开头的 <c>/命令</c> 画成整体的 token,
/// 方向键跳过整个 token,一次退格删掉整个 token。它自己不画框,外框是输入框卡片。
/// 哪些片段是 token 只由 <see cref="MentionSource"/>(ViewModel 的 <see cref="ComposerViewModel.Mentions"/>)决定,
/// 所以程序改写草稿、换会话之后 token 照样对得上。业务视图只用这个封装,不直接碰 Arc 的 token 类型。
/// </summary>
public class NcwTokenEditor : TKCode.Arc.TokenTextEditor
{
    private TextBox? _textBox;
    private Func<string, IReadOnlyList<MentionSpan>>? _source;

    protected override Type StyleKeyOverride => typeof(TKCode.Arc.TokenTextEditor);

    /// <summary>草稿 → token 片段。设置后立刻按当前文字重新解析。</summary>
    public Func<string, IReadOnlyList<MentionSpan>>? MentionSource
    {
        get => _source;
        set
        {
            _source = value;
            Reparse();
        }
    }

    /// <summary>规则依赖的数据变了(命令清单到了)而文字没变时,按当前文字重新解析一次。</summary>
    public void Reparse() =>
        TokenParser = _source is { } source
            ? text => source(text).Select(static m => new TKCode.Arc.TokenSpan(m.Kind, m.Id, m.Label, m.Start, m.End))
            : null;

    /// <summary>光标放到末尾并取得焦点(选中候选、换会话之后)。</summary>
    public void MoveCaretToEnd(bool focus)
    {
        var end = Text?.Length ?? 0;
        Select(end, end);
        if (focus)
        {
            _textBox?.Focus();
        }
    }

    /// <summary>键盘焦点在不在书写区里(测试与键盘处理用)。</summary>
    public bool IsEditing => _textBox?.IsFocused == true;

    /// <summary>内部文本框即将从剪贴板粘贴内容(设 <c>e.Handled = true</c> 可接管图片 / 文件粘贴)。</summary>
    public event EventHandler<RoutedEventArgs>? PastingFromClipboard;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_textBox is not null)
        {
            _textBox.PastingFromClipboard -= OnTextBoxPasting;
        }

        base.OnApplyTemplate(e);
        _textBox = e.NameScope.Find<TextBox>(TextBoxPart);
        if (_textBox is not null)
        {
            _textBox.PastingFromClipboard += OnTextBoxPasting;
        }
    }

    private void OnTextBoxPasting(object? sender, RoutedEventArgs e) => PastingFromClipboard?.Invoke(this, e);
}
