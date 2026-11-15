using System.Text.RegularExpressions;

namespace Ncw.Build.Publish;

internal enum WarningKind
{
    /// <summary>IL / ILC 的裁剪与 AOT 分析警告:发布失败(AGENTS.md §5「零裁剪警告」)。</summary>
    Trim,

    /// <summary>白名单里的环境警告(clang 模块缓存 .pcm 等,和代码无关)。</summary>
    AllowedEnvironment,

    /// <summary>其他警告:报告出来,<c>--strict-warnings</c> 时才算失败。</summary>
    Other,
}

internal sealed record ClassifiedWarning(WarningKind Kind, string Code, string Line);

internal sealed record WarningReport(IReadOnlyList<ClassifiedWarning> All)
{
    public IEnumerable<ClassifiedWarning> Trim => All.Where(w => w.Kind == WarningKind.Trim);

    public IEnumerable<ClassifiedWarning> AllowedEnvironment => All.Where(w => w.Kind == WarningKind.AllowedEnvironment);

    public IEnumerable<ClassifiedWarning> Other => All.Where(w => w.Kind == WarningKind.Other);

    /// <summary>没有裁剪 / AOT 警告(白名单里的环境警告不影响)。</summary>
    public bool TrimClean => !Trim.Any();
}

/// <summary>
/// 把 <c>dotnet publish</c> 的输出分成三类:裁剪警告(失败)、白名单环境警告(忽略并说明)、其他警告。
/// 同一条警告会被 MSBuild 在多个项目、多次构建里重复打印,这里按「代码 + 去掉项目后缀的正文」去重。
/// </summary>
internal static partial class WarningFilter
{
    // `warning IL2026:`、`warning IL3050:`、`ILC : AOT analysis warning IL3053:`、`warning ILC1234:` 都带 "warning IL…"。
    [GeneratedRegex(@"\bwarning\s+(?<code>IL[C]?\d{1,5})\b", RegexOptions.IgnoreCase)]
    private static partial Regex TrimCode();

    // 没有代码的分析警告写法:"Trim analysis warning" / "AOT analysis warning"。
    [GeneratedRegex(@"\b(?:trim|aot)\s+analysis\s+warning\b", RegexOptions.IgnoreCase)]
    private static partial Regex AnalysisWarning();

    // MSBuild:`warning CS0168:`;clang / ld:`clang: warning: …`、`ld: warning: …`;或整行以 `warning:` 开头。
    [GeneratedRegex(@"(?:\bwarning\s+(?<code>[A-Za-z]{2,}\d+)\s*:)|(?::\s*warning\s*:)|(?:^\s*warning\s*:)", RegexOptions.IgnoreCase)]
    private static partial Regex GenericWarning();

    // clang 模块缓存(.pcm)的环境类警告:缓存目录不可写、模块被别的 clang 版本构建过……和我们的代码无关。
    [GeneratedRegex(@"\.pcm\b|module\s*cache|ModuleCache", RegexOptions.IgnoreCase)]
    private static partial Regex ModuleCache();

    // MSBuild 行尾的 `[/path/to/project.csproj]` 后缀,同一条警告因项目不同而不同,去重时去掉。
    [GeneratedRegex(@"\s*\[[^\]]+\.(?:csproj|proj|slnx?)\]\s*$")]
    private static partial Regex ProjectSuffix();

    public static WarningReport Analyze(IEnumerable<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ClassifiedWarning>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (Classify(line) is not { } classified)
            {
                continue;
            }

            var key = classified.Code + "|" + ProjectSuffix().Replace(line, "").Trim();
            if (seen.Add(key))
            {
                result.Add(classified);
            }
        }

        return new WarningReport(result);
    }

    public static ClassifiedWarning? Classify(string line)
    {
        if (TrimCode().Match(line) is { Success: true } trim)
        {
            return new ClassifiedWarning(WarningKind.Trim, trim.Groups["code"].Value.ToUpperInvariant(), line);
        }

        if (AnalysisWarning().IsMatch(line))
        {
            return new ClassifiedWarning(WarningKind.Trim, "IL????", line);
        }

        var generic = GenericWarning().Match(line);
        if (!generic.Success)
        {
            return null;
        }

        var code = generic.Groups["code"].Success ? generic.Groups["code"].Value.ToUpperInvariant() : "native";
        return ModuleCache().IsMatch(line)
            ? new ClassifiedWarning(WarningKind.AllowedEnvironment, code, line)
            : new ClassifiedWarning(WarningKind.Other, code, line);
    }
}
