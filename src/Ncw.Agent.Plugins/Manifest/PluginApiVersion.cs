namespace Ncw.Agent.Plugins;

public enum EngineCompatibility
{
    Ok,
    Deprecated,
    Incompatible,
}

/// <summary>
/// 插件 <b>API 版本</b>(2.x <c>shared/plugin/api-version.ts</c>):与应用版本是两件东西。<c>engines.nextcowork</c> 比的是它,不是应用版本
/// (2.x 曾把两者混用,结果按官方模板写的 <c>^0.2.0</c> 插件装上一律「装载失败」)。
/// 1.0 之前按 <c>^0.x</c> 的规矩:minor 递增 = 允许 break;新增方法 / 贡献点而不删不改时递增 patch。
/// </summary>
public static class PluginApiVersion
{
    /// <summary>
    /// 当前实现的插件 API 版本。3.0 实现 2.x <c>0.3.3</c> 的<b>非 UI 部分</b>(命令、Agent 工具、Skills、工作区文件、命令、网络、存储、配置、拦截器 / 上下文);
    /// 视图类(<c>nextcowork/view</c>、<c>documents</c>、<c>tabs</c>)不实现。版本号照 2.x,因为 <c>engines</c> 的比对语义就是它。
    /// </summary>
    public const string Current = "0.3.3";

    /// <summary>认得、但已经弃用的 range——照常装载,只推一条 warn(0.2 声明的那批插件在 2.x 的宿主上从来没跑起来过,所以「兼容它们」实际上等于「让它们第一次能跑」)。</summary>
    public static readonly IReadOnlyList<string> LegacyRanges = ["^0.2.0", "~0.2.0", "0.2.0"];

    /// <summary>读不懂的 range 一律 incompatible(不是 ok):宁可装不上,也不要在语义不明的约束下跑第三方代码。</summary>
    public static EngineCompatibility Check(string range, string apiVersion = Current)
    {
        if (PluginVersions.SatisfiesEngine(range, apiVersion))
        {
            return EngineCompatibility.Ok;
        }

        return LegacyRanges.Contains(range.Trim(), StringComparer.Ordinal) ? EngineCompatibility.Deprecated : EngineCompatibility.Incompatible;
    }
}
