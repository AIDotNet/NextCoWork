namespace Ncw.Agent.Environments;

/// <summary>路径解析结果:<see cref="Absolute"/> 是规范化、解析过符号链接的绝对路径,<see cref="Outside"/> 表示落在根目录之外。</summary>
public readonly record struct PathResolution(EnvPath Absolute, bool Outside);

/// <summary>
/// 路径守卫(移植自 2.x <c>environment/paths.ts</c> 的 <c>createWorkspacePaths</c>)。
/// 要点:
/// <list type="bullet">
/// <item>先对「最近的已存在祖先」做 realpath,再拼回不存在的尾部,所以指向根外的符号链接不能用来写新文件;</item>
/// <item>只有 NotFound / NotDirectory 才当作「路径不存在」继续向上找;连接断开等其它错误原样抛出,不能当成路径不存在;</item>
/// <item>根目录本身也取 realpath(macOS 的 <c>/var</c> → <c>/private/var</c>),再做包含判断。</item>
/// </list>
/// </summary>
public sealed class PathGuard(IEnvironmentFs fs, PathStyle style)
{
    public PathStyle Style { get; } = style;

    /// <summary>
    /// 解析 <paramref name="input"/>(相对输入接在 <paramref name="root"/> 后面)。<paramref name="root"/> 为 <c>null</c> 表示没有根,
    /// 此时输入必须是绝对路径,结果恒为 <see cref="PathResolution.Outside"/>。
    /// </summary>
    public async ValueTask<PathResolution> ResolveAsync(EnvPath? root, string input, CancellationToken ct = default)
    {
        if (!PathRules.IsValidInput(Style, input) || (root is { } r && (r.Style != Style || !PathRules.IsValidInput(Style, r.Value))))
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidPath);
        }

        var inputAbsolute = PathRules.IsAbsolute(Style, input);
        if (!inputAbsolute && (root is null || !root.Value.IsAbsolute))
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidPath, "relative path needs an absolute root");
        }

        var joined = inputAbsolute ? PathRules.Normalize(Style, input) : PathRules.Resolve(Style, root!.Value.Value, input);
        var absolute = await CanonicalAsync(joined, ct).ConfigureAwait(false);
        if (root is null)
        {
            return new PathResolution(absolute, true);
        }

        try
        {
            var canonicalRoot = await fs.RealPathAsync(root.Value, ct).ConfigureAwait(false);
            return new PathResolution(absolute, !absolute.IsWithin(canonicalRoot));
        }
        catch (EnvironmentException e) when (inputAbsolute && IsMissing(e))
        {
            return new PathResolution(absolute, true);
        }
    }

    /// <summary>解析并要求落在根内,否则抛 <see cref="EnvironmentErrorCode.PathEscape"/>。</summary>
    public async ValueTask<EnvPath> ResolveWithinAsync(EnvPath root, string input, CancellationToken ct = default)
    {
        var result = await ResolveAsync(root, input, ct).ConfigureAwait(false);
        if (result.Outside)
        {
            throw new EnvironmentException(EnvironmentErrorCode.PathEscape, input);
        }

        return result.Absolute;
    }

    /// <summary>给界面显示的写法:根内用相对路径(根本身是 <c>.</c>),根外用绝对路径。不碰文件系统。</summary>
    public string Display(EnvPath? root, EnvPath absolute)
    {
        if (root is not { } r || !absolute.IsWithin(r))
        {
            return absolute.Value;
        }

        var relative = absolute.RelativeTo(r);
        return relative.Length == 0 ? "." : relative;
    }

    private async ValueTask<EnvPath> CanonicalAsync(string path, CancellationToken ct)
    {
        var current = path;
        var tail = new List<string>();
        for (;;)
        {
            try
            {
                var real = await fs.RealPathAsync(EnvPath.Parse(current, Style), ct).ConfigureAwait(false);
                return tail.Count == 0 ? real : real.Join([.. tail]);
            }
            catch (EnvironmentException e) when (IsMissing(e))
            {
                var parent = PathRules.Dirname(Style, current);
                if (parent == current)
                {
                    throw;
                }

                tail.Insert(0, PathRules.Basename(Style, current));
                current = parent;
            }
        }
    }

    private static bool IsMissing(EnvironmentException e) =>
        e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory;
}
