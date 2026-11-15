using System.Globalization;

namespace Ncw.Core.Domain;

/// <summary>
/// 发布清单里的强制更新策略(docs/design/04 §3,语义沿用 2.x 的 <c>canStartNewRuns</c>):
/// <c>mandatory</c> / <c>minimumSupportedVersion</c> / <c>graceUntil</c>。这是事实的描述,不含判断;判断在 <see cref="UpdatePolicy"/>。
/// </summary>
public sealed record UpdateRelease(string Version, bool Mandatory = false, string? MinimumSupportedVersion = null, DateTimeOffset? GraceUntil = null);

/// <summary>一次待安装更新对当前版本的约束强度。</summary>
public enum UpdateEnforcement
{
    /// <summary>没有更新,或更新是可选的:不约束。</summary>
    None,

    /// <summary>更新是必需的(<c>mandatory</c>),但还没到阻断:没有 <c>minimumSupportedVersion</c>、当前版本已满足它、或还在宽限期内。界面要提示,不拦用户。</summary>
    Required,

    /// <summary>必须更新才能继续:<c>mandatory</c>、当前版本低于 <c>minimumSupportedVersion</c>、并且已过宽限期。界面阻断,Agent 侧据此拒绝新 Run(04 §3)。</summary>
    Blocking,
}

/// <summary>
/// 更新相关的唯一判定入口(AGENTS.md §3 规则 3:同一个事实在多处展示,派生逻辑只能是 Core 里的一个纯函数)。
/// 设置页的「关于」、主界面的阻断提示、Agent 侧「拒绝过低版本控制端的新 Run」共用它;时钟由调用方传入,所以可以精确测试。
/// 看不懂的版本串一律不拦(和 sidecar 的版本规则一致:协议握手本身还有一道检查)。
/// </summary>
public static class UpdatePolicy
{
    /// <summary><paramref name="candidate"/> 是否严格新于 <paramref name="current"/>(SemVer 2.0 优先级,忽略 <c>+构建元数据</c>)。任何一边看不懂返回 false。</summary>
    public static bool IsNewer(string? candidate, string? current) =>
        TryCompare(candidate, current, out var order) && order > 0;

    /// <summary>两个版本的 SemVer 优先级比较。任何一边看不懂返回 false。</summary>
    public static bool TryCompare(string? left, string? right, out int order)
    {
        order = 0;
        if (!VersionKey.TryParse(left, out var a) || !VersionKey.TryParse(right, out var b))
        {
            return false;
        }

        order = a.CompareTo(b);
        return true;
    }

    /// <summary>
    /// 约束强度。规则(与 2.x 逐条对应):
    /// <list type="number">
    /// <item>没有待装更新、或不是 <c>mandatory</c>:<see cref="UpdateEnforcement.None"/>。</item>
    /// <item><c>mandatory</c> 但没有(或看不懂)<c>minimumSupportedVersion</c>:<see cref="UpdateEnforcement.Required"/>(2.x:只有「必需」的标记,不阻断)。</item>
    /// <item>当前版本 ≥ <c>minimumSupportedVersion</c>:<see cref="UpdateEnforcement.Required"/>。</item>
    /// <item><c>graceUntil</c> 晚于 <paramref name="now"/>(宽限期没过):<see cref="UpdateEnforcement.Required"/>;恰好等于 <c>graceUntil</c> 算已过。</item>
    /// <item>否则 <see cref="UpdateEnforcement.Blocking"/>。</item>
    /// </list>
    /// </summary>
    public static UpdateEnforcement EnforcementOf(string? currentVersion, UpdateRelease? pending, DateTimeOffset now)
    {
        if (pending is null || !pending.Mandatory)
        {
            return UpdateEnforcement.None;
        }

        if (!TryCompare(currentVersion, pending.MinimumSupportedVersion, out var order))
        {
            return UpdateEnforcement.Required; // 没有最低版本,或版本串看不懂:不拦
        }

        if (order >= 0)
        {
            return UpdateEnforcement.Required;
        }

        return pending.GraceUntil is { } grace && grace > now ? UpdateEnforcement.Required : UpdateEnforcement.Blocking;
    }

    /// <summary>是否允许开始新的 Run(2.x 的 <c>canStartNewRuns</c>)。只有 <see cref="UpdateEnforcement.Blocking"/> 禁止。</summary>
    public static bool CanStartNewRuns(string? currentVersion, UpdateRelease? pending, DateTimeOffset now) =>
        EnforcementOf(currentVersion, pending, now) != UpdateEnforcement.Blocking;

    /// <summary>
    /// 清单本身是否自洽:<c>minimumSupportedVersion</c> 不能高于这个发布的版本(否则装完更新仍然低于最低版本,用户被永远阻断)。
    /// 版本串看不懂也算不自洽(<c>minimumSupportedVersion</c> 为空除外)。
    /// </summary>
    public static bool IsCoherent(UpdateRelease release)
    {
        if (!VersionKey.TryParse(release.Version, out _))
        {
            return false;
        }

        if (release.MinimumSupportedVersion is null)
        {
            return true;
        }

        return TryCompare(release.Version, release.MinimumSupportedVersion, out var order) && order >= 0;
    }

    /// <summary>SemVer 2.0 优先级:<c>3.0.0-alpha.1 &lt; 3.0.0-alpha.2 &lt; 3.0.0-beta &lt; 3.0.0</c>;忽略 <c>+构建元数据</c>;前导 <c>v</c> 允许;允许 2 到 4 段数字。</summary>
    private readonly record struct VersionKey(int Major, int Minor, int Patch, int Revision, string[] Pre) : IComparable<VersionKey>
    {
        public static bool TryParse(string? text, out VersionKey key)
        {
            key = default;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var core = text.Trim().TrimStart('v');
            var plus = core.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0)
            {
                core = core[..plus];
            }

            string[] pre = [];
            var dash = core.IndexOf('-', StringComparison.Ordinal);
            if (dash >= 0)
            {
                pre = core[(dash + 1)..].Split('.');
                core = core[..dash];
                if (pre.Any(p => p.Length == 0))
                {
                    return false;
                }
            }

            var parts = core.Split('.');
            if (parts.Length is < 2 or > 4)
            {
                return false;
            }

            var numbers = new int[4];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                {
                    return false;
                }
            }

            key = new VersionKey(numbers[0], numbers[1], numbers[2], numbers[3], pre);
            return true;
        }

        public int CompareTo(VersionKey other)
        {
            int c;
            if ((c = Major.CompareTo(other.Major)) != 0 || (c = Minor.CompareTo(other.Minor)) != 0
                || (c = Patch.CompareTo(other.Patch)) != 0 || (c = Revision.CompareTo(other.Revision)) != 0)
            {
                return c;
            }

            // 没有预发布标识的比有的新
            if (Pre.Length == 0 || other.Pre.Length == 0)
            {
                return Pre.Length == other.Pre.Length ? 0 : Pre.Length == 0 ? 1 : -1;
            }

            for (var i = 0; i < Math.Min(Pre.Length, other.Pre.Length); i++)
            {
                var left = Pre[i];
                var right = other.Pre[i];
                var leftNumeric = int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var l);
                var rightNumeric = int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var r);
                c = (leftNumeric, rightNumeric) switch
                {
                    (true, true) => l.CompareTo(r),
                    (true, false) => -1, // 数字标识符比字母标识符小
                    (false, true) => 1,
                    _ => string.CompareOrdinal(left, right),
                };
                if (c != 0)
                {
                    return Math.Sign(c);
                }
            }

            return Pre.Length.CompareTo(other.Pre.Length);
        }
    }
}
