namespace Ncw.Agent.Workspace.Files;

/// <summary>
/// 遍历时跳过哪些目录。<b>一张硬编码的表,不解析 <c>.gitignore</c></b>(和内置工具的 <c>IgnoreList</c> 同一张表:gitignore 是一门完整的模式语言,
/// 半套实现产出的不是「少几个结果」而是静默的错误答案)。只按<b>目录名</b>匹配(任何层级),不跳过点开头的目录(<c>.github</c> 里全是用户想看见的东西)。
/// </summary>
public static class IgnoreRules
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.Ordinal)
    {
        ".git", ".hg", ".svn",
        "node_modules", "bower_components", "vendor", ".yarn", ".pnpm-store",
        "dist", "out", "build", "target", ".next", ".nuxt", ".svelte-kit", ".angular", ".output",
        ".cache", ".turbo", ".parcel-cache", ".gradle", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox",
        ".venv", "venv", ".idea", "Pods",
        "coverage", ".nyc_output",
    };

    private static readonly HashSet<string> SkipFiles = new(StringComparer.Ordinal) { ".DS_Store", "Thumbs.db" };

    public static bool ShouldSkip(string name, bool isDirectory) => isDirectory ? SkipDirs.Contains(name) : SkipFiles.Contains(name);
}
