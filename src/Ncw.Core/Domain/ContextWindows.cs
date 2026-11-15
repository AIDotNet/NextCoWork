namespace Ncw.Core.Domain;

/// <summary>
/// 按字符数估 token(2.x <c>shared/agent/token-estimate.ts</c>)。Agent 的上下文估算和客户端的思考卡片共用这一个函数
/// (AGENTS.md §3 规则 3:同一个事实在多处展示时,派生逻辑只有一份)。
/// 这是<b>估算</b>,不是真值:拉丁文字 4 字符 1 token,CJK 1 字符 1 token(误差量级:英文约 ±15%,中文约 ±25%)。
/// 真值在上游回报的 usage 里;估算只用于请求发出之前,以及「上一次真值之后新增部分」的补算。
/// </summary>
public static class TokenEstimate
{
    private const int CharsPerTokenLatin = 4;

    /// <summary>按码点迭代:一个 emoji(代理对)只算一个字符。</summary>
    public static int OfText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var cjk = 0;
        var total = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            total++;
            if (IsCjk(rune.Value))
            {
                cjk++;
            }
        }

        return (int)Math.Ceiling(cjk + ((total - cjk) / (double)CharsPerTokenLatin));
    }

    private static bool IsCjk(int cp) =>
        (cp >= 0x2E80 && cp <= 0x9FFF) // 部首、假名、CJK 统一表意
        || (cp >= 0xAC00 && cp <= 0xD7AF) // 谚文
        || (cp >= 0xF900 && cp <= 0xFAFF) // 兼容表意
        || (cp >= 0xFF00 && cp <= 0xFF60) // 全角
        || (cp >= 0x20000 && cp <= 0x3FFFF); // 扩展 B 及以上
}

/// <summary>
/// 三层窗口(2.x <c>shared/agent/context-management.ts</c>,04 §3)。一个 <c>contextWindow</c> 曾经同时回答三个不同的问题,这里把它们拆开:
/// <list type="number">
/// <item><b>协议窗口</b> = 模型目录里的原值,回答「发出去会不会被上游 400」,只用于硬校验,永远不受「最大上下文」开关影响;</item>
/// <item><b>有效窗口</b> = <see cref="Effective"/>,回答「我自愿用到多少」,是压缩判据和圆环的分母,默认夹在计费分界内;</item>
/// <item><b>压缩阈值</b> = <see cref="AutoCompactThreshold"/>(Claude Code 的公式),回答「什么时候开始压」。</item>
/// </list>
/// Agent(真正触发压缩)和客户端(状态行按当前窗口重判)读同一份,否则状态行会和真正的判据悄悄分叉。
/// </summary>
public static class ContextWindows
{
    /// <summary>目录里查不到这个模型时,协议窗口按它算。保守取小:猜大了压缩会迟到,那一轮直接被上游 400。</summary>
    public const int FallbackWindow = 200_000;

    /// <summary>
    /// OpenAI 现代四款的长上下文计费分界。<b>价格常量,不是能力常量</b>:越过它模型照样工作,只是更贵,
    /// 所以默认把有效窗口夹在这里,「最大上下文」开关的语义是花钱开关,不是解锁更大容量。
    /// </summary>
    public const int LongContextThreshold = 272_000;

    /// <summary>摘要请求的输出上限(Claude Code 的 <c>COMPACT_MAX_OUTPUT_TOKENS</c>),阈值里也为它留位置。</summary>
    public const int CompactMaxOutputTokens = 20_000;

    /// <summary>阈值与有效窗口之间的缓冲(Claude Code 的 <c>AUTOCOMPACT_BUFFER_TOKENS</c>)。</summary>
    public const int AutoCompactBufferTokens = 13_000;

    /// <summary>状态行提前多少开始提示「接近上限」。</summary>
    public const int WarningBufferTokens = 20_000;

    /// <summary>阈值封底:窗口的这个比例。小窗口(8K / 32K 的本地模型)光减 33K 就是负数,每轮都压缩而且永远收敛不了。</summary>
    public const double MinThresholdRatio = 0.5;

    /// <summary>每回合输出额度的出厂值:大输出模型不该从空会话起就挤占上下文压力读数,也不该让压缩过早触发。</summary>
    public const int DefaultMaxOutputTokens = 32_000;

    /// <summary>
    /// 每回合输出额度由唯一一个全局设置项说了算,模型目录里的输出上限不参与(在一处改完就该对所有模型生效);
    /// 唯一的硬边界是上下文窗口:输出额度大于整个窗口的请求必然 400。缺省 / 非正用 <see cref="DefaultMaxOutputTokens"/>。
    /// </summary>
    public static int ResolveMaxOutputTokens(int? configured, int? contextWindow)
    {
        var wanted = configured is >= 1 ? configured.Value : DefaultMaxOutputTokens;
        return contextWindow is >= 1 ? Math.Min(wanted, contextWindow.Value) : wanted;
    }

    /// <summary>协议窗口缺失、非正一律落到 <see cref="FallbackWindow"/>;<paramref name="maxContext"/> 关(默认)夹在计费分界内。</summary>
    public static int Effective(int? protocolWindow, bool maxContext = false)
    {
        var protocol = protocolWindow is > 0 ? protocolWindow.Value : FallbackWindow;
        return maxContext ? protocol : Math.Min(protocol, LongContextThreshold);
    }

    /// <summary>自动压缩在占用达到多少时触发:<c>窗口 − min(最大输出, 20K) − 13K</c>,下限为窗口的一半。</summary>
    public static int AutoCompactThreshold(int contextWindow, int maxOutputTokens)
    {
        var reserve = Math.Min(Math.Max(0, maxOutputTokens), CompactMaxOutputTokens);
        var threshold = contextWindow - reserve - AutoCompactBufferTokens;
        return Math.Max(threshold, (int)Math.Floor(contextWindow * MinThresholdRatio));
    }

    /// <summary>「这一轮该压缩了吗」。Agent 和客户端状态行共用。</summary>
    public static bool ShouldCompact(long inputTokens, int contextWindow, int maxOutputTokens) =>
        inputTokens >= AutoCompactThreshold(contextWindow, maxOutputTokens);

    /// <summary>状态行开始提示「接近上限」的那条线。</summary>
    public static bool ShouldWarn(long inputTokens, int contextWindow, int maxOutputTokens) =>
        inputTokens >= AutoCompactThreshold(contextWindow, maxOutputTokens) - WarningBufferTokens;

    /// <summary>这个模型开「最大上下文」有没有意义:协议窗口得真的比计费分界大。只用来置灰菜单项,绝不用来改写存下来的值。</summary>
    public static bool SupportsMaxContext(int? protocolWindow) => Effective(protocolWindow, maxContext: true) > LongContextThreshold;
}
