using Ncw.Build.Core;

namespace Ncw.Build.Bundle;

/// <summary>包内一个文件:路径相对应用根(macOS 是 .app 目录),sha256 小写十六进制。</summary>
internal sealed record BundleFile(string Path, long Size, string Sha256);

/// <summary>打包清单 <c>bundle.json</c>:这个包是什么版本、sidecar 在哪、每个文件的校验值。<c>sign</c> 之后会重写它(签名会改变二进制)。</summary>
internal sealed record BundleManifest(
    int Schema,
    string Product,
    string Version,
    string Rid,
    DateTimeOffset CreatedUtc,
    string AppRoot,
    string MainExe,
    string AgentExe,
    bool Signed,
    BundleFile[] Files)
{
    public const int CurrentSchema = 1;

    /// <summary>扫描应用根,生成文件清单(按路径排序,结果稳定)。</summary>
    public static BundleFile[] ScanFiles(string appRoot)
    {
        var root = Path.GetFullPath(appRoot);
        var files = new List<BundleFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null)
            {
                continue; // 符号链接不计(目标文件本身会被列出)
            }

            files.Add(new BundleFile(Path.GetRelativePath(root, path).Replace('\\', '/'), info.Length, Hashing.Sha256(path)));
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return [.. files];
    }

    /// <summary>核对磁盘上的文件和清单是否一致;返回差异描述(空 = 一致)。</summary>
    public IReadOnlyList<string> Verify(string appRoot)
    {
        var problems = new List<string>();
        var root = Path.GetFullPath(appRoot);
        var expected = Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var actual = ScanFiles(root).ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var (path, file) in expected)
        {
            if (!actual.TryGetValue(path, out var found))
            {
                problems.Add($"missing: {path}");
            }
            else if (found.Sha256 != file.Sha256)
            {
                problems.Add($"changed: {path}");
            }
        }

        foreach (var path in actual.Keys.Where(p => !expected.ContainsKey(p)))
        {
            problems.Add($"unexpected: {path}");
        }

        return problems;
    }
}
