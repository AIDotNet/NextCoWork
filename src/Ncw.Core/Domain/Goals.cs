using System.Text;

namespace Ncw.Core.Domain;

/// <summary>目标是谁设的。遥测与界面文案都按它分叉。</summary>
public enum GoalOrigin
{
    User,
    ProposalDirect,
    ProposalApproved,
    Restored,
}

/// <summary>目标被清掉的原因。只进遥测与日志,不进界面文案。</summary>
public enum GoalClearReason
{
    UserClear,
    Superseded,
    Met,
    Impossible,
    SessionEnded,
    RunFailed,
}

/// <summary>
/// 一个会话当前的目标(Agent 持有真源,客户端镜像一份,key 带 <c>deviceId</c>)。<see cref="Id"/> 每次设定都换新(哪怕条件一字不改):它是「这一个目标」的身份,
/// 判定的裁决、空闲检查、批准条都认它,旧身份的一切在各层都被丢弃。
/// </summary>
public sealed record ActiveGoal(string Id, string Condition, GoalOrigin Origin, DateTimeOffset SetAt, long TokensAtStart)
{
    /// <summary>已评估轮数(达成 / 未达成各算一次;超时与出错<b>不算</b>)。</summary>
    public int Iterations { get; init; }

    /// <summary>上一次判未达成的理由(界面那行「上次判定」)。</summary>
    public string? LastReason { get; init; }

    /// <summary>后台子代理在跑而推迟判定的起点。<c>null</c> = 当前不处于推迟态。</summary>
    public DateTimeOffset? DeferredSince { get; init; }

    /// <summary>本轮推迟已注入过几次检查。</summary>
    public int CheckinCount { get; init; }

    public DateTimeOffset? LastDeferralPassAt { get; init; }

    /// <summary>空闲定时器注入次数,到 <see cref="GoalConditions.IdleCheckinCap"/> 封顶。</summary>
    public int IdleCheckinCount { get; init; }

    /// <summary>目标激活以来消耗的 token(由用量账本更新)。</summary>
    public long Tokens { get; init; }
}

/// <summary><c>/goal &lt;参数&gt;</c> 的语义。三种结局各自对应一条不同的路径,不要在调用方再判一次。</summary>
public abstract record GoalCommandIntent
{
    private GoalCommandIntent()
    {
    }

    public sealed record Show : GoalCommandIntent;

    public sealed record Clear : GoalCommandIntent;

    public sealed record Set(string Condition) : GoalCommandIntent;

    public sealed record Invalid(GoalInvalidReason Reason, int Length) : GoalCommandIntent;
}

public enum GoalInvalidReason
{
    Empty,
    TooLong,
}

/// <summary>
/// 目标条件本身的规则:规范化、清除词、长度上限、命令解析。斜杠命令、<c>ProposeGoal</c> 工具、协议方法三个入口都要用——它们是<b>同一份规则</b>,各写一份就会各漂各的。
/// </summary>
public static class GoalConditions
{
    /// <summary>条件上限。超了<b>拒绝</b>,不截断——截断后的条件是一个用户没同意的目标。</summary>
    public const int Max = 4000;

    /// <summary><c>ProposeGoal</c> 那条更严:模型提的目标必须是一句能核的话,不是一篇需求。</summary>
    public const int ProposalMax = 500;

    /// <summary>连续这么多个回合没有调用工具 → 强制结束并警告。这是唯一的「回合上限」(2.x <c>GOAL_IDLE_STREAK_CAP</c>)。</summary>
    public const int IdleStreakCap = 8;

    /// <summary>空闲定时器最多注入几次检查(2.x <c>GOAL_IDLE_CHECKIN_CAP</c>)。</summary>
    public const int IdleCheckinCap = 3;

    /// <summary>检查间隔的下限(2.x <c>GOAL_CHECKIN_MIN_DELAY_MS</c>)。</summary>
    public static readonly TimeSpan CheckinMinDelay = TimeSpan.FromMinutes(1);

    /// <summary>检查间隔的缺省基数(2.x 的 30 分钟);第 n 次按 2^min(n,2) 倍。</summary>
    public static readonly TimeSpan CheckinDefaultInterval = TimeSpan.FromMinutes(30);

    /// <summary><c>/goal</c> 的清除词(照搬 Claude Code 那一组)。一个恰好叫 <c>stop</c> 的「条件」当清除指令处理是刻意的:用户打 <c>/goal stop</c> 的意图在任何真实场景里都是「别再跑了」。</summary>
    public static IReadOnlyList<string> ClearWords { get; } = ["clear", "stop", "off", "reset", "none", "cancel"];

    /// <summary>
    /// 条件规范化。★ 剥掉不可见字符<b>之后</b>再判空:一串零宽空格看起来是「用户写了东西」,而判定器收到的是一个空条件——它会稳定地判未达成,于是这一轮永远停不下来。
    /// 普通空白(空格、制表、换行)保留。
    /// </summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            if (IsDefaultIgnorable(cp) || IsInvisibleControl(cp))
            {
                continue;
            }

            sb.Append(rune.ToString());
        }

        return sb.ToString().Trim();
    }

    public static bool IsClearWord(string text) => ClearWords.Contains(Normalize(text).ToLowerInvariant());

    public static GoalCommandIntent ParseCommand(string args, int max = Max)
    {
        var text = Normalize(args);
        if (text.Length == 0)
        {
            return args.Trim().Length == 0 ? new GoalCommandIntent.Show() : new GoalCommandIntent.Invalid(GoalInvalidReason.Empty, 0);
        }

        if (IsClearWord(text))
        {
            return new GoalCommandIntent.Clear();
        }

        return text.Length > max ? new GoalCommandIntent.Invalid(GoalInvalidReason.TooLong, text.Length) : new GoalCommandIntent.Set(text);
    }

    /// <summary>
    /// 检查间隔:首次 <paramref name="baseInterval"/>,之后翻倍、封顶 4 倍(默认 30 / 60 / 120 分钟);配置低于下限的抬到 <see cref="CheckinMinDelay"/>;非正数当缺省。
    /// </summary>
    public static TimeSpan CheckinInterval(int count, TimeSpan? baseInterval = null)
    {
        var b = baseInterval is { } given && given > TimeSpan.Zero ? given : CheckinDefaultInterval;
        var scaled = TimeSpan.FromTicks(b.Ticks * (1L << Math.Min(Math.Max(0, count), 2)));
        return scaled < CheckinMinDelay ? CheckinMinDelay : scaled;
    }

    /// <summary>C0/C1 控制字符里除了普通空白(制表、换行、回车)以外的:削掉。</summary>
    private static bool IsInvisibleControl(int cp) => cp is <= 0x08 or 0x0B or 0x0C or (>= 0x0E and <= 0x1F) or (>= 0x7F and <= 0x9F);

    /// <summary>Unicode 的 <c>Default_Ignorable_Code_Point</c>(.NET 的正则没有这个属性,按码点表写出来)。</summary>
    private static bool IsDefaultIgnorable(int cp) =>
        cp is 0x00AD or 0x034F or 0x061C or 0x3164 or 0xFEFF or 0xFFA0
            or (>= 0x115F and <= 0x1160)
            or (>= 0x17B4 and <= 0x17B5)
            or (>= 0x180B and <= 0x180F)
            or (>= 0x200B and <= 0x200F)
            or (>= 0x202A and <= 0x202E)
            or (>= 0x2060 and <= 0x206F)
            or (>= 0xFE00 and <= 0xFE0F)
            or (>= 0xFFF0 and <= 0xFFF8)
            or (>= 0x1BCA0 and <= 0x1BCA3)
            or (>= 0x1D173 and <= 0x1D17A)
            or (>= 0xE0000 and <= 0xE0FFF);
}
