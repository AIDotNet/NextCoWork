using System.Text.RegularExpressions;

namespace Ncw.Agent.Plugins;

public readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    public int CompareTo(SemVer other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }
}

public enum RangeOp
{
    Caret,
    Tilde,
    AtLeast,
    Exact,
}

public sealed record VersionRange(RangeOp Op, SemVer Version);

/// <summary>版本与范围(2.x <c>shared/plugin/manifest.ts</c> 的 <c>parseSemVer</c> / <c>parseRange</c> / <c>satisfiesEngine</c> / <c>hasNewerVersion</c>)。</summary>
public static partial class PluginVersions
{
    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static bool IsVersion(string value) => VersionPattern().IsMatch(value);

    public static SemVer? Parse(string value)
    {
        var m = VersionPattern().Match(value);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var major) || !int.TryParse(m.Groups[2].Value, out var minor) || !int.TryParse(m.Groups[3].Value, out var patch))
        {
            return null;
        }

        return new SemVer(major, minor, patch);
    }

    /// <summary>
    /// 只认四种形状:<c>^x.y.z</c> / <c>~x.y.z</c> / <c>&gt;=x.y.z</c> / <c>x.y.z</c>。★ 刻意不支持复合 range:range 语法是 npm 生态里最容易写错、也最难向用户解释的一块;
    /// 认不出来的一律拒绝,比装上之后行为不可预测好。
    /// </summary>
    public static VersionRange? ParseRange(string value)
    {
        var trimmed = value.Trim();
        foreach (var (prefix, op) in new[] { (">=", RangeOp.AtLeast), ("^", RangeOp.Caret), ("~", RangeOp.Tilde) })
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Parse(trimmed[prefix.Length..].Trim()) is { } v ? new VersionRange(op, v) : null;
            }
        }

        return Parse(trimmed) is { } exact ? new VersionRange(RangeOp.Exact, exact) : null;
    }

    /// <summary>宿主版本满足这个 range 吗。range 读不懂时返回 false(不是 true)。</summary>
    public static bool SatisfiesEngine(string range, string hostVersion)
    {
        if (ParseRange(range) is not { } parsed || Parse(hostVersion) is not { } host)
        {
            return false;
        }

        var version = parsed.Version;
        switch (parsed.Op)
        {
            case RangeOp.Exact:
                return host.CompareTo(version) == 0;
            case RangeOp.AtLeast:
                return host.CompareTo(version) >= 0;
        }

        if (host.CompareTo(version) < 0)
        {
            return false;
        }

        if (parsed.Op == RangeOp.Caret)
        {
            // 0.x 的 ^ 按 npm 的规矩只锁到 minor——插件 API 在 1.0 之前明确可以 break,这条正好是想要的语义
            return version.Major == 0 ? host.Major == 0 && host.Minor == version.Minor : host.Major == version.Major;
        }

        return host.Major == version.Major && host.Minor == version.Minor;
    }

    /// <summary>
    /// 市场那一版比本机这一版新吗——「要不要提示更新」的唯一判据。★ <see cref="Parse"/> 丢预发布标签,所以核心版本相等时单独补一条:本机带预发布、市场不带 = 有更新。
    /// 读不懂的版本号返回 false(宁可不提示,也不要提示一次点下去必然失败的更新)。
    /// </summary>
    public static bool HasNewerVersion(string current, string? latest)
    {
        if (latest is null || Parse(current) is not { } mine || Parse(latest) is not { } theirs)
        {
            return false;
        }

        var diff = theirs.CompareTo(mine);
        return diff != 0 ? diff > 0 : current.Contains('-') && !latest.Contains('-');
    }
}
