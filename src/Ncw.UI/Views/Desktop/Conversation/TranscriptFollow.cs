namespace Ncw.UI.Views.Desktop.Conversation;

/// <summary>
/// 转录「跟随最新」的状态机(纯 UI 状态,没有任何 Avalonia 类型,单测直接驱动;接线在 <see cref="ConversationView"/>)。
/// <para><b>只认用户意图,不从偏移变化里猜。</b>虚拟化列表(<c>VirtualizingStackPanel</c>)按估算高度排布,行被实例化、量出真实高度之后,
/// 总高度和滚动偏移都会自己漂(往上、往下都有,甚至漂到底);WebView 卡片报高、展开动画也一样。旧实现把「偏移往上变了」当成用户在往上翻,
/// 于是会无故停止跟随、或在用户翻看时把视图拽回底部。这里改成:</para>
/// <list type="bullet">
/// <item>停止跟随:用户往上滚(滚轮 / 触控板向上、键盘 PageUp / ↑ / Home、拖滚动条往上)、展开一行、定位到某一行。</item>
/// <item>恢复跟随:用户<b>自己</b>滚到底(滚轮 / 键盘 / 拖滚动条,在操作窗口内到达底部)、点「回到最新」、换会话。程序和布局造成的「到底」不算。</item>
/// <item>跟随中:每一轮布局之后不在底部就贴到底(<see cref="ShouldPin"/>,同一帧里完成,没有「先画错位置再跳」的一帧)。</item>
/// </list>
/// </summary>
public sealed class TranscriptFollow
{
    /// <summary>距底部多少以内算「在底部」。</summary>
    public const double BottomTolerance = 8;

    /// <summary>一次用户滚动操作之后多久内的偏移变化算这次操作造成的(触控板的惯性滚动会持续一小段时间,每个惯性事件都会续期)。</summary>
    public const long UserWindowMs = 350;

    private long _userUntil = long.MinValue;
    private bool _dragging;

    public bool IsFollowing { get; private set; } = true;

    /// <summary>正在拖滚动条(期间不贴底,松手时在底部就恢复跟随)。</summary>
    public bool IsDragging => _dragging;

    public static bool AtBottom(double offset, double extent, double viewport) =>
        offset >= extent - viewport - BottomTolerance;

    /// <summary>
    /// 用户滚动输入(滚轮、触控板、键盘、触摸手势)。<paramref name="towardTop"/>:方向明确是往上;<paramref name="canMoveUp"/>:还没到顶
    /// (已经在顶上、或内容不够一屏时往上滚什么也不会发生,不能因此停止跟随——否则之后内容长出来也不跟了)。
    /// 方向不明确的输入(手势)只开操作窗口,由随后的偏移变化决定。
    /// </summary>
    public void UserScroll(long now, bool towardTop, bool canMoveUp)
    {
        _userUntil = now + UserWindowMs;
        if (towardTop && canMoveUp)
        {
            IsFollowing = false;
        }
    }

    public void BeginDrag() => _dragging = true;

    public void EndDrag(bool atBottom)
    {
        _dragging = false;
        if (atBottom)
        {
            IsFollowing = true;
        }
    }

    /// <summary>
    /// 滚动偏移变了。只有在用户操作窗口内、或正在拖滚动条时才解释成用户意图:往上 → 停止跟随;到底 → 恢复跟随。
    /// 窗口外的变化(布局重估、内容变高、程序滚动)一律不改状态。
    /// </summary>
    public void OffsetChanged(long now, double deltaY, bool atBottom)
    {
        if (!_dragging && now > _userUntil)
        {
            return;
        }

        if (deltaY < 0 && !atBottom)
        {
            IsFollowing = false;
        }
        else if (deltaY > 0 && atBottom)
        {
            IsFollowing = true;
        }
    }

    /// <summary>用户展开 / 收起一行、或定位到某一行:停止跟随(展开让内容变高,贴底会把刚展开的内容顶出视野)。</summary>
    public void Pause()
    {
        IsFollowing = false;
        _userUntil = long.MinValue; // 点击本身是一次操作,但它不是「滚到底」
    }

    /// <summary>「回到最新」、换会话:恢复跟随,下一轮布局贴底。</summary>
    public void Resume()
    {
        IsFollowing = true;
        _dragging = false;
        _userUntil = long.MinValue;
    }

    /// <summary>这一轮布局之后要不要把视图贴到底。</summary>
    public bool ShouldPin(bool atBottom) => IsFollowing && !_dragging && !atBottom;
}
