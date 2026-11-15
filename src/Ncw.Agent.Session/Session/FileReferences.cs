using Ncw.Agent.Environments;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Session;

/// <summary>
/// 用户拖 / 选进来的文件引用,路径改写成<b>说给模型听的那一种形式</b>(2.x 的 <c>normalizePaths</c>):工作区内的压成工作区相对(<c>src/a.ts</c>),工作区外的保留绝对路径。
/// <para>
/// 为什么归一化在会话层,而不在界面或编码层:界面没有 realpath(macOS 上工作区根记录的是 <c>/var/…</c>,拖进来的文件是 <c>/private/var/…</c>,词法比较会把工作区<b>内</b>的文件判到外面去);
/// 编码层没有工作区根,而且改写发生在编码层的话,转录里存的与发出去的就成了两个字符串。这里是入口的汇合点,改一处就够。
/// 相对形式不是为了好看:工具以工作区根为 cwd,<c>src/a.ts</c> 可以直接喂回读文件工具,和它在 grep 回执里看到的是同一个写法(同一个 <c>Display</c>)。
/// </para>
/// 解析失败的引用原样保留(不抛):丢掉用户拖进来的东西比留一个没归一化的写法更糟。
/// </summary>
public static class FileReferences
{
    public static async ValueTask<IReadOnlyList<Part>> NormalizeAsync(
        IReadOnlyList<Part> parts, IExecutionEnvironment environment, EnvPath root, CancellationToken ct)
    {
        if (!parts.Any(p => p is FileRefPart))
        {
            return parts;
        }

        EnvPath? canonicalRoot = null;
        try
        {
            canonicalRoot = await environment.Fs.RealPathAsync(root, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException)
        {
        }

        var result = new List<Part>(parts.Count);
        foreach (var part in parts)
        {
            if (part is not FileRefPart file)
            {
                result.Add(part);
                continue;
            }

            try
            {
                var resolved = await environment.Paths.ResolveAsync(root, file.Path, ct).ConfigureAwait(false);
                var shown = resolved.Outside || canonicalRoot is null
                    ? resolved.Absolute.Value
                    : environment.Paths.Display(canonicalRoot, resolved.Absolute);
                result.Add(file with { Path = shown });
            }
            catch (EnvironmentException e) when (e.Code is not (EnvironmentErrorCode.Cancelled or EnvironmentErrorCode.Disconnected))
            {
                result.Add(file);
            }
        }

        return result;
    }
}
