namespace Ncw.Agent.Tools;

/// <summary>
/// 遍历时跳过哪些目录。<b>一张硬编码的表,不解析 <c>.gitignore</c></b>(移植自 2.x <c>ignore.ts</c>)。
/// gitignore 是一门完整的模式语言;半套实现产出的不是「少几个结果」,而是静默的错误答案 —— grep 说仓库里没有这个字符串,而它就在那儿。
/// 一张写死的表至少是可预测的,而且 Glob / Grep 的描述里直说它跳过什么。只按<b>目录名</b>匹配(任何层级),不跳过点开头的目录(<c>.github</c>、<c>.claude</c> 里全是用户想让模型看见的东西)。
/// </summary>
internal static class IgnoreList
{
    public static readonly IReadOnlySet<string> SkipDirs = new HashSet<string>(StringComparer.Ordinal)
    {
        // 版本控制
        ".git", ".hg", ".svn",
        // 依赖
        "node_modules", "bower_components", "vendor", ".yarn", ".pnpm-store",
        // 构建产物
        "dist", "out", "build", "target", ".next", ".nuxt", ".svelte-kit", ".angular", ".output",
        // 缓存
        ".cache", ".turbo", ".parcel-cache", ".gradle", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox",
        // 环境 / IDE
        ".venv", "venv", ".idea", "Pods",
        // 覆盖率
        "coverage", ".nyc_output",
    };

    /// <summary>无论如何都不该出现在搜索结果里的文件名。</summary>
    public static readonly IReadOnlySet<string> SkipFiles = new HashSet<string>(StringComparer.Ordinal) { ".DS_Store", "Thumbs.db" };

    public static bool ShouldSkip(string name, bool isDirectory) => isDirectory ? SkipDirs.Contains(name) : SkipFiles.Contains(name);

    /// <summary>写进 Glob / Grep 描述里的那句话:模型据此判断「没搜到」是不是可信。</summary>
    public const string Note =
        "Dependency and build directories (node_modules, .git, dist, out, build, target, coverage, ...) are skipped automatically. "
        + "This is a fixed list; .gitignore is NOT read. To search inside those directories, use Bash instead.";
}
