using Ncw.Agent.Environments;

namespace Ncw.Agent.Tools;

/// <param name="Absolute">解析并 realpath 之后的绝对路径(最近的已存在祖先 realpath,再拼回不存在的尾部)。</param>
/// <param name="Outside">落在项目根之外。<b>不是错误</b>:谁能碰哪个文件由权限档位决定,不是一道写死的目录边界(2.x 的取舍,见 03 §3)。</param>
/// <param name="Display">说给模型听的写法:根内用相对路径(根本身是 <c>.</c>),根外用绝对路径。绝不产出 <c>../x</c>。</param>
internal readonly record struct ToolPath(EnvPath Absolute, bool Outside, string Display);

internal static class ToolPaths
{
    /// <summary>
    /// 没有工作区时的说辞。没有工作区就没有 cwd,一条命令无处可跑。
    /// 不给一个临时目录:给的话模型会以为自己在用户的项目里干活,把文件写进一个谁也不会去看的地方,并报告「已完成」(2.x ★)。
    /// </summary>
    public const string NoWorkspace =
        "This session has no workspace bound, so there is no working directory. Ask the user to open a workspace directory first, then retry this step.";

    /// <summary>绝对路径不受这条限制:没有工作区也照样能读写它,所以不能说成「文件工具不可用」(那会让模型连试都不试)。</summary>
    public const string NoWorkspaceRelative =
        "This session has no workspace bound, so a relative path has nothing to resolve against. Pass an absolute path instead, or ask the user to open a workspace directory first.";

    /// <summary>所有碰路径的工具的第一行。<paramref name="input"/> 为空 = 项目根。</summary>
    public static async ValueTask<ToolPath> ResolveAsync(ToolRuntime runtime, string? input, CancellationToken ct)
    {
        var style = runtime.PathStyle;
        if (string.IsNullOrEmpty(input))
        {
            if (runtime.Root is null)
            {
                throw new ToolFailureException(NoWorkspaceRelative);
            }

            input = ".";
        }

        if (runtime.Root is null && !PathRules.IsAbsolute(style, input))
        {
            throw new ToolFailureException(NoWorkspaceRelative);
        }

        try
        {
            var resolution = await runtime.Environment.Paths.ResolveAsync(runtime.Root, input, ct).ConfigureAwait(false);
            return new ToolPath(resolution.Absolute, resolution.Outside, Display(resolution.Absolute, resolution.Outside, await runtime.RealRootAsync(ct).ConfigureAwait(false)));
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.InvalidPath or EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            // 失败信息里只出现模型自己传进来的那个路径,不出现目标文件的任何内容。根本身不存在(工作区被删了/改名了)是环境问题,不是模型的错。
            throw new ToolFailureException($"Could not resolve the path \"{input}\": {(e.Code == EnvironmentErrorCode.InvalidPath ? "the path is not valid here" : "the path or the workspace root does not exist")}.");
        }
    }

    /// <summary>说给模型听的写法:根内相对(根本身是 <c>.</c>),根外绝对。<paramref name="realRoot"/> 必须是 realpath 之后的根(见 <see cref="ToolRuntime.RealRootAsync"/>)。</summary>
    private static string Display(EnvPath absolute, bool outside, EnvPath? realRoot)
    {
        if (outside || realRoot is not { } root || !absolute.IsWithin(root))
        {
            return absolute.Value;
        }

        var relative = absolute.RelativeTo(root);
        return relative.Length == 0 ? "." : relative;
    }

    /// <summary>同一个文件的读-改-写串行用的 key:真实路径(Windows 风格折叠大小写)。</summary>
    public static string MutationKey(EnvPath path) => ReadTracker.Key(path);
}
