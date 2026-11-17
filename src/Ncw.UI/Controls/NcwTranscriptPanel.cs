using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Ncw.UI.Controls;

/// <summary>给虚拟化列表估计「还没量过的行」有多高(不必精确:量过之后用真实高度,估计值还会按行类型校准)。</summary>
public interface IRowHeightEstimator
{
    /// <summary>这一项在这个宽度下大约多高;给不出返回 null(用同类行的平均高度)。只能读项上的数据,不能碰控件。</summary>
    double? Estimate(object item, double width);
}

/// <summary>
/// 对话转录专用的纵向虚拟化面板(替代 Avalonia 的 <c>VirtualizingStackPanel</c>,docs/design/03 §6)。
/// <para>Avalonia 12.1.5 的 <c>VirtualizingStackPanel</c> 把每一个没实例化的行都估成「当前实例化的那几行的平均高度」,而且量过的高度不记:
/// 视口里是一串 26px 的工具行时,上面几百 px 的 Markdown 回答也按 26px 算,总高度短得离谱——滚动条的滑块太大、拖动时乱跳、往上翻时内容「长出来」把位置顶走。</para>
/// <para>这里的做法:</para>
/// <list type="bullet">
/// <item><b>记住量过的高度</b>(按项的引用 + 宽度;项就是行视图模型,原地更新时引用不变)。滚出去再滚回来、或者只是从没看过的行,高度都稳定。</item>
/// <item><b>没量过的行按内容估</b>(<see cref="Estimator"/>:Markdown 按字数和宽度估行数、工具行固定一行……),再按行类型用「真实 / 估计」的累计比值校准;
/// 估不出的用同类行的平均高度,都没有才用 <see cref="DefaultRowHeight"/>。</item>
/// <item>每一行的位置都来自同一份前缀和(量过的用真实值,其余用估计值),排布处处一致;视口上方的行量出真实高度后位置会变,
/// 由 <c>ScrollContentPresenter</c> 的滚动锚定按锚点行的位移补偿偏移,内容不跳(和 <c>VirtualizingStackPanel</c> 一样注册锚点候选)。</item>
/// <item>只实例化「视口 ± <see cref="CacheLength"/> 个视口高度」里的行;容器按「回收键 + 项的类型」进池复用,同类行复用同一个模板实例。</item>
/// <item>带键盘焦点的容器(行内编辑)滚出去也不回收,避免编辑框丢焦点。</item>
/// </list>
/// 一次测量是 O(行数) 的前缀和(估计值按「项 + 宽度」缓存),几千行也只是几万次加法;测量只发生在视口移动、行变高、增删行时。
/// </summary>
public sealed class NcwTranscriptPanel : VirtualizingPanel
{
    /// <summary>既没量过、也估不出、同类行也没有平均值时的行高。</summary>
    public const double DefaultRowHeight = 44;

    private readonly Dictionary<object, Measured> _measured = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, double> _estimates = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Type, TypeStats> _stats = [];
    private readonly Dictionary<object, Control> _realized = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Control, Realized> _containers = [];
    private readonly Dictionary<(object Key, Type Type), Stack<Control>> _pool = [];

    private double[] _offsets = [0];
    private int _first;
    private int _last = -1;
    private double _width = double.NaN;
    private Rect _viewport;
    private bool _hasViewport;
    private bool _inLayout;
    private IScrollAnchorProvider? _anchors;

    private sealed record Measured(double Height, double Width);

    private sealed class TypeStats
    {
        public double ActualSum;
        public double EstimateSum;
        public double HeightSum;
        public int Count;
    }

    private sealed class Realized(object item, object? recycleKey, int index)
    {
        public object Item { get; set; } = item;
        public object? RecycleKey { get; } = recycleKey;
        public int Index { get; set; } = index;

        /// <summary>增删过行、下一轮测量之前:<see cref="Index"/> 是旧下标(发「下标变了」时当旧值用),不能拿来排布或回答查询。</summary>
        public bool Stale { get; set; }
    }

    public NcwTranscriptPanel()
    {
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    /// <summary>估计没量过的行有多高;null = 只用同类平均高度。</summary>
    public IRowHeightEstimator? Estimator { get; set; }

    /// <summary>视口上下各多实例化几个视口高度的行(滚动时不至于露白)。</summary>
    public double CacheLength { get; set; } = 0.75;

    /// <summary>当前实例化的行的下标区间(测试 / 诊断用)。</summary>
    public (int First, int Last) RealizedRange => (_first, _last);

    /// <summary>一项现在用来排布的高度(量过的真实值,否则估计值;测试 / 诊断用)。</summary>
    public double HeightOf(int index) => index >= 0 && index + 1 < _offsets.Length ? _offsets[index + 1] - _offsets[index] : double.NaN;

    // ───────────────────────── 布局 ─────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : Bounds.Width > 0 ? Bounds.Width : 800;
        if (!_width.Equals(width))
        {
            _width = width;
            _estimates.Clear(); // 换了宽度:文字重新折行,估计值重算(量过的高度在新宽度下重量之前仍是比估计更好的近似)
        }

        if (items.Count == 0)
        {
            RecycleAllExcept(null);
            _offsets = [0];
            (_first, _last) = (0, -1);
            return default;
        }

        _inLayout = true;
        try
        {
            var childSize = new Size(width, double.PositiveInfinity);
            var (first, last) = (0, -1);
            // 量出真实高度之后前缀和会变,覆盖视口的行也可能变:重来几轮直到稳定(通常一两轮)
            for (var pass = 0; pass < 4; pass++)
            {
                BuildOffsets(items, width);
                (first, last) = RangeFor(ExtendedViewport(), items.Count);
                var changed = false;
                for (var i = first; i <= last; i++)
                {
                    var container = Realize(items, i);
                    container.Measure(childSize);
                    changed |= Record(items[i]!, container.DesiredSize.Height, width);
                }

                if (!changed)
                {
                    break;
                }
            }

            // 只留最后一轮区间里的行(前几轮多实例化的回收掉);带键盘焦点的容器(行内编辑)也留着,滚回来时编辑框还在、焦点不丢
            var keep = new HashSet<Control>();
            for (var i = first; i <= last; i++)
            {
                if (_realized.TryGetValue(items[i]!, out var container))
                {
                    keep.Add(container);
                }
            }

            foreach (var (container, info) in _containers)
            {
                if (keep.Contains(container) || !container.IsKeyboardFocusWithin)
                {
                    continue;
                }

                var index = IndexFromContainer(container);
                if (index >= 0)
                {
                    info.Stale = false;
                    if (info.Index != index)
                    {
                        var old = info.Index;
                        info.Index = index;
                        ItemContainerGenerator?.ItemContainerIndexChanged(container, old, index);
                    }

                    container.Measure(childSize);
                    Record(info.Item, container.DesiredSize.Height, width);
                    keep.Add(container);
                }
            }

            RecycleAllExcept(keep);
            BuildOffsets(items, width);
            (_first, _last) = (first, last);
            return new Size(width, _offsets[^1]);
        }
        finally
        {
            _inLayout = false;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _inLayout = true;
        try
        {
            foreach (var (container, info) in _containers)
            {
                if (info.Stale || info.Index < 0 || info.Index + 1 >= _offsets.Length)
                {
                    continue;
                }

                var rect = new Rect(0, _offsets[info.Index], finalSize.Width, _offsets[info.Index + 1] - _offsets[info.Index]);
                container.Arrange(rect);
                if (container.IsVisible && _viewport.Intersects(rect))
                {
                    try
                    {
                        _anchors?.RegisterAnchorCandidate(container);
                    }
                    catch (InvalidOperationException)
                    {
                        // 不在滚动容器里(单独摆放的面板):没有锚定可做
                    }
                }
            }

            return finalSize;
        }
        finally
        {
            _inLayout = false;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _anchors = this.FindAncestorOfType<IScrollAnchorProvider>();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _anchors = null;
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var viewport = e.EffectiveViewport;
        if (_hasViewport && Math.Abs(viewport.Top - _viewport.Top) < 0.5 && Math.Abs(viewport.Bottom - _viewport.Bottom) < 0.5)
        {
            return;
        }

        _viewport = viewport;
        _hasViewport = true;
        if (!_inLayout)
        {
            InvalidateMeasure();
        }
    }

    private Rect ExtendedViewport()
    {
        if (!_hasViewport || _viewport.Height <= 0)
        {
            // 还不知道视口(第一次布局):先按顶上一屏实例化
            var height = Bounds.Height > 0 ? Bounds.Height : 1000;
            return new Rect(0, 0, _width, height);
        }

        var buffer = _viewport.Height * Math.Max(0, CacheLength);
        return new Rect(0, _viewport.Top - buffer, _width, _viewport.Height + 2 * buffer);
    }

    /// <summary>和区间 [top, bottom) 相交的行。区间整个在内容之后(内容刚变短、偏移还没被钳回来)时取最后一屏的行。</summary>
    private (int First, int Last) RangeFor(Rect area, int count)
    {
        var total = _offsets[count];
        var top = Math.Max(0, area.Top);
        var bottom = Math.Max(top + 1, area.Bottom);
        if (top >= total)
        {
            top = Math.Max(0, total - Math.Max(1, area.Height));
            bottom = total + 1;
        }

        var first = UpperBound(top) - 1;
        first = Math.Clamp(first, 0, count - 1);
        var last = first;
        while (last + 1 < count && _offsets[last + 1] < bottom)
        {
            last++;
        }

        return (first, last);
    }

    /// <summary>第一个满足 offsets[i] &gt; value 的 i(offsets 单调不减)。</summary>
    private int UpperBound(double value)
    {
        int lo = 0, hi = _offsets.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_offsets[mid] > value)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }

    // ───────────────────────── 高度:量过的 / 估计的 ─────────────────────────

    private void BuildOffsets(IReadOnlyList<object?> items, double width)
    {
        if (_offsets.Length != items.Count + 1)
        {
            _offsets = new double[items.Count + 1];
        }

        var y = 0.0;
        for (var i = 0; i < items.Count; i++)
        {
            _offsets[i] = y;
            y += items[i] is { } item ? HeightFor(item, width) : 0;
        }

        _offsets[items.Count] = y;
    }

    private double HeightFor(object item, double width)
    {
        if (_measured.TryGetValue(item, out var measured) && Math.Abs(measured.Width - width) < 1)
        {
            return measured.Height;
        }

        if (!_estimates.TryGetValue(item, out var estimate))
        {
            estimate = Estimate(item, width) ?? (measured?.Height ?? DefaultRowHeight);
            _estimates[item] = estimate;
        }

        return estimate;
    }

    /// <summary>估计值:估算器给的 × 同类行的校准比值;估算器给不出就用同类行的平均高度。</summary>
    private double? Estimate(object item, double width)
    {
        _stats.TryGetValue(item.GetType(), out var stats);
        if (Estimator?.Estimate(item, width) is { } raw && double.IsFinite(raw) && raw > 0)
        {
            var ratio = stats is { EstimateSum: > 0, ActualSum: > 0 } ? Math.Clamp(stats.ActualSum / stats.EstimateSum, 0.5, 2.5) : 1;
            return raw * ratio;
        }

        return stats is { Count: > 0 } ? stats.HeightSum / stats.Count : null;
    }

    /// <summary>记下一项量出来的高度;和原来记的不一样返回 true(前缀和要重算)。第一次量到时顺便校准同类行的估计。</summary>
    private bool Record(object item, double height, double width)
    {
        if (_measured.TryGetValue(item, out var old) && Math.Abs(old.Width - width) < 1 && Math.Abs(old.Height - height) < 0.5)
        {
            return false;
        }

        var first = old is null;
        _measured[item] = new Measured(height, width);
        if (first)
        {
            var type = item.GetType();
            if (!_stats.TryGetValue(type, out var stats))
            {
                stats = new TypeStats();
                _stats[type] = stats;
            }

            stats.HeightSum += height;
            stats.Count++;
            if (Estimator?.Estimate(item, width) is { } raw && double.IsFinite(raw) && raw > 0)
            {
                stats.ActualSum += height;
                stats.EstimateSum += raw;
            }
        }

        // 新的真实值和之前用的估计值不一样:前缀和要变
        var used = _estimates.Remove(item, out var estimate) ? estimate : old?.Height ?? double.NaN;
        return double.IsNaN(used) || Math.Abs(used - height) >= 0.5;
    }

    private void Forget(object item)
    {
        _measured.Remove(item);
        _estimates.Remove(item);
    }

    // ───────────────────────── 容器:实例化 / 回收 ─────────────────────────

    private Control Realize(IReadOnlyList<object?> items, int index)
    {
        var item = items[index]!;
        var generator = ItemContainerGenerator!;
        if (_realized.TryGetValue(item, out var existing))
        {
            var info = _containers[existing];
            info.Stale = false;
            if (info.Index != index)
            {
                var old = info.Index;
                info.Index = index;
                generator.ItemContainerIndexChanged(existing, old, index);
            }

            return existing;
        }

        Control container;
        object? recycleKey = null;
        if (generator.NeedsContainer(item, index, out recycleKey))
        {
            if (recycleKey is not null && _pool.TryGetValue((recycleKey, item.GetType()), out var stack) && stack.Count > 0)
            {
                container = stack.Pop();
            }
            else
            {
                container = generator.CreateContainer(item, index, recycleKey);
            }

            container.SetCurrentValue(IsVisibleProperty, true);
            generator.PrepareItemContainer(container, item, index);
            AddInternalChild(container);
            generator.ItemContainerPrepared(container, item, index);
        }
        else
        {
            // 项本身就是控件(本应用的转录不会出现,保底)
            container = (Control)item;
            generator.PrepareItemContainer(container, container, index);
            AddInternalChild(container);
            generator.ItemContainerPrepared(container, item, index);
        }

        _realized[item] = container;
        _containers[container] = new Realized(item, recycleKey, index);
        return container;
    }

    private void RecycleAllExcept(HashSet<Control>? keep)
    {
        if (_containers.Count == 0)
        {
            return;
        }

        foreach (var container in _containers.Keys.ToList())
        {
            if (keep is null || !keep.Contains(container))
            {
                Recycle(container);
            }
        }
    }

    private void Recycle(Control container)
    {
        if (!_containers.Remove(container, out var info))
        {
            return;
        }

        _realized.Remove(info.Item);
        _anchors?.UnregisterAnchorCandidate(container);
        var generator = ItemContainerGenerator;
        if (ReferenceEquals(container, info.Item))
        {
            generator?.ClearItemContainer(container);
            RemoveInternalChild(container);
            return;
        }

        generator?.ClearItemContainer(container);
        container.SetCurrentValue(IsVisibleProperty, false);
        RemoveInternalChild(container);
        if (info.RecycleKey is { } key)
        {
            var poolKey = (key, info.Item.GetType());
            if (!_pool.TryGetValue(poolKey, out var stack))
            {
                stack = new Stack<Control>();
                _pool[poolKey] = stack;
            }

            stack.Push(container);
        }
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        InvalidateMeasure();
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace when e.OldItems is not null:
                foreach (var old in e.OldItems)
                {
                    if (old is null)
                    {
                        continue;
                    }

                    if (_realized.TryGetValue(old, out var container))
                    {
                        Recycle(container);
                    }

                    Forget(old);
                }

                break;
            case NotifyCollectionChangedAction.Reset:
            {
                var present = new HashSet<object>(items.OfType<object>(), ReferenceEqualityComparer.Instance);
                foreach (var container in _containers.Where(c => !present.Contains(c.Value.Item)).Select(c => c.Key).ToList())
                {
                    Recycle(container);
                }

                foreach (var item in _measured.Keys.Where(k => !present.Contains(k)).ToList())
                {
                    Forget(item);
                }

                foreach (var item in _estimates.Keys.Where(k => !present.Contains(k)).ToList())
                {
                    _estimates.Remove(item);
                }

                break;
            }
        }

        // 下标在下一轮测量里按项重新对齐(Realize 会发 ItemContainerIndexChanged);这之前别人按下标问,用项现在的位置答
        foreach (var info in _containers.Values)
        {
            info.Stale = true;
        }
    }

    // ───────────────────────── VirtualizingPanel 的其余约定 ─────────────────────────

    protected override IEnumerable<Control>? GetRealizedContainers() => _containers.Keys;

    protected override Control? ContainerFromIndex(int index)
    {
        var items = Items;
        return index >= 0 && index < items.Count && items[index] is { } item && _realized.TryGetValue(item, out var container) ? container : null;
    }

    protected override int IndexFromContainer(Control container)
    {
        if (!_containers.TryGetValue(container, out var info))
        {
            return -1;
        }

        if (!info.Stale)
        {
            return info.Index;
        }

        var items = Items;
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], info.Item))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 把一行滚进视野。已经实例化就直接 <c>BringIntoView</c>;否则先按前缀和里的位置(估计值)请求滚过去,布局之后它被实例化、量出真实高度,再对准一次。
    /// </summary>
    protected override Control? ScrollIntoView(int index)
    {
        var items = Items;
        if (_inLayout || index < 0 || index >= items.Count || !IsEffectivelyVisible)
        {
            return null;
        }

        if (ContainerFromIndex(index) is { } realized)
        {
            realized.BringIntoView();
            return realized;
        }

        if (_offsets.Length != items.Count + 1)
        {
            BuildOffsets(items, double.IsNaN(_width) ? Bounds.Width : _width);
        }

        this.BringIntoView(new Rect(0, _offsets[index], Bounds.Width, Math.Max(1, _offsets[index + 1] - _offsets[index])));
        UpdateLayout();
        if (ContainerFromIndex(index) is { } container)
        {
            container.BringIntoView();
            UpdateLayout();
            return container;
        }

        return null;
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var count = Items.Count;
        if (count == 0)
        {
            return null;
        }

        var current = from is Control control && _containers.ContainsKey(control) ? IndexFromContainer(control) : -1;
        var target = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Down or NavigationDirection.Next => current + 1,
            NavigationDirection.Up or NavigationDirection.Previous => current < 0 ? count - 1 : current - 1,
            _ => -1,
        };

        if (wrap)
        {
            target = target < 0 ? count - 1 : target >= count ? 0 : target;
        }

        return target >= 0 && target < count ? ScrollIntoView(target) : null;
    }
}
