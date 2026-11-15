using System.Globalization;

namespace Ncw.Desktop.Sidecar;

/// <summary>
/// 版本号比较(SemVer 2.0 的优先级规则,忽略 <c>+构建元数据</c>)。桌面端与 sidecar 的版本一致性判断用它:
/// <c>3.0.0-alpha.1 &lt; 3.0.0-alpha.2 &lt; 3.0.0-beta &lt; 3.0.0</c>。
/// </summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string[] PreRelease) : IComparable<SemanticVersion>
{
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var core = text.Trim().TrimStart('v');
        var plus = core.IndexOf('+');
        if (plus >= 0)
        {
            core = core[..plus];
        }

        string[] pre = [];
        var dash = core.IndexOf('-');
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
        if (parts.Length is < 2 or > 4
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        var patch = 0;
        if (parts.Length >= 3 && !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, pre);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) { return c; }
        c = Minor.CompareTo(other.Minor);
        if (c != 0) { return c; }
        c = Patch.CompareTo(other.Patch);
        if (c != 0) { return c; }

        // 没有预发布标识的版本比有的大
        var a = PreRelease ?? [];
        var b = other.PreRelease ?? [];
        if (a.Length == 0 || b.Length == 0)
        {
            return a.Length == b.Length ? 0 : a.Length == 0 ? 1 : -1;
        }

        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bn = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    public bool Equals(SemanticVersion other) => CompareTo(other) == 0;

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', PreRelease ?? []));

    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;
}

/// <summary>桌面端对一个已经在运行的 Agent 的版本判断(docs/design/04 §3:sidecar 和客户端版本一致)。</summary>
public enum VersionVerdict
{
    /// <summary>版本一致,或看不懂其中一个(此时不拦,协议握手本身还有 N / N-1 兼容检查)。</summary>
    Compatible,

    /// <summary>Agent 比桌面端旧:例如「关窗后继续运行」的旧 Agent 在应用更新之后还活着。桌面端让它退出并拉起包里的新版本。</summary>
    AgentOlder,

    /// <summary>Agent 比桌面端新:桌面端过旧(例如回退安装了旧版),提示用户更新客户端,不去动 Agent。</summary>
    AgentNewer,
}

public static class SidecarVersionPolicy
{
    public static VersionVerdict Evaluate(string desktopVersion, string agentVersion)
    {
        if (!SemanticVersion.TryParse(desktopVersion, out var desktop) || !SemanticVersion.TryParse(agentVersion, out var agent))
        {
            return VersionVerdict.Compatible;
        }

        var c = agent.CompareTo(desktop);
        return c == 0 ? VersionVerdict.Compatible : c < 0 ? VersionVerdict.AgentOlder : VersionVerdict.AgentNewer;
    }

    /// <summary>当前程序集的版本(<c>Directory.Build.props</c> 的 Version,去掉 +构建元数据),和 <c>AgentHost.Version</c> 同一个取法。</summary>
    public static string OfAssembly(System.Reflection.Assembly assembly) =>
        assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0]
        ?? assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
