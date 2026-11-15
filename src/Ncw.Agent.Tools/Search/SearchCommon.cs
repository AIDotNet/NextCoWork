using Ncw.Agent.Environments;

namespace Ncw.Agent.Tools;

/// <summary>搜索工具的限额(沿用 2.x)。</summary>
public static class SearchLimits
{
    /// <summary>结果条数上限。再多模型也读不完,只会挤掉上下文。</summary>
    public const int MaxGlobResults = 200;

    public const int MaxGrepMatches = 200;

    /// <summary>单行截断。压住良性但慢的模式和内存;指数级回溯靠的是线性引擎 / 静态筛查 / 匹配超时,不是它。</summary>
    public const int MaxLineChars = 2000;

    /// <summary><c>multiline</c> 时整份文件文本一次性进正则,单行截断这道防线就没了,所以另给一条更小的上限。</summary>
    public const int MaxMultilineChars = 256 * 1024;

    /// <summary>整次搜索(遍历 + 匹配)的时间预算。超了返回部分结果并如实说明。</summary>
    public static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(5);

    /// <summary>单次匹配调用的超时(回溯引擎的兜底)。</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>比这(磁盘字节)大的文件不进托管 Grep —— 多半是数据或产物。</summary>
    public const long MaxGrepFileBytes = 5L * 1024 * 1024;

    /// <summary>原生后端单次进程的超时。比托管的宽:rg 在大仓库上为了有序输出放弃了并行,宁可多等几秒拿全结果。</summary>
    public static readonly TimeSpan NativeTimeout = TimeSpan.FromSeconds(10);

    public const int GrepConcurrency = 16;

    /// <summary>不大于这个的文件一次读完再嗅探(源码文件绝大多数在几十 KB 以内,省掉一次 open/close);更大的先读头嗅探、是文本才全量读。</summary>
    public const int SingleReadMaxBytes = 256 * 1024;

    /// <summary>
    /// <c>type</c> 参数认识的语言别名(和 ripgrep 的 <c>--type</c> 对齐了常用的那一批)。认不出的 type 要报错,
    /// 不能忽略后当成「不过滤」—— 那会让模型拿到一份范围完全不对的结果,而它没有任何办法察觉。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TypeGlobs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["js"] = "**/*.{js,jsx,mjs,cjs}",
        ["ts"] = "**/*.{ts,tsx,mts,cts}",
        ["tsx"] = "**/*.tsx",
        ["jsx"] = "**/*.jsx",
        ["py"] = "**/*.{py,pyi}",
        ["go"] = "**/*.go",
        ["rust"] = "**/*.rs",
        ["java"] = "**/*.java",
        ["kotlin"] = "**/*.{kt,kts}",
        ["swift"] = "**/*.swift",
        ["c"] = "**/*.{c,h}",
        ["cpp"] = "**/*.{cc,cpp,cxx,hh,hpp,hxx}",
        ["cs"] = "**/*.cs",
        ["rb"] = "**/*.rb",
        ["php"] = "**/*.php",
        ["sh"] = "**/*.{sh,bash,zsh}",
        ["html"] = "**/*.{html,htm}",
        ["css"] = "**/*.{css,scss,sass,less}",
        ["vue"] = "**/*.vue",
        ["svelte"] = "**/*.svelte",
        ["json"] = "**/*.json",
        ["yaml"] = "**/*.{yaml,yml}",
        ["toml"] = "**/*.toml",
        ["xml"] = "**/*.xml",
        ["md"] = "**/*.{md,markdown}",
        ["sql"] = "**/*.sql",
    };
}

/// <summary>
/// 一趟遍历的基准。<c>Walker</c> 的 rel 是相对 <see cref="Root"/> 算的,而 Root 同时还是它内部挡软链出界的信任基点。
/// 所以目标落在项目根外面时,不能继续把 Root 钉死在项目根上再拿绝对路径当起点 —— 正确的做法是<b>把基准整个换过去</b>。
/// </summary>
internal sealed record WalkBase(EnvPath Root, string Start, string Label, bool Outside)
{
    /// <summary>把 Walker 产出的 rel 变成展示形式:根内保持相对,根外是绝对路径。</summary>
    public string Display(string rel) => !Outside ? rel : rel.Length == 0 ? Root.Value : Root.Join(rel.Split('/')).Value;

    public static async ValueTask<WalkBase> OfAsync(ToolRuntime rt, ToolPath target, CancellationToken ct)
    {
        if (target.Outside || rt.Root is not { } root)
        {
            return new WalkBase(target.Absolute, "", target.Absolute.Value, Outside: true);
        }

        var realRoot = await rt.Environment.Fs.RealPathAsync(root, ct).ConfigureAwait(false);
        var relative = target.Absolute.RelativeTo(realRoot);
        return new WalkBase(realRoot, relative, target.Display, Outside: false);
    }
}
