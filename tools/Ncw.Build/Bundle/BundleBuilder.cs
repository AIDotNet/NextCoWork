using Ncw.Build.Core;

namespace Ncw.Build.Bundle;

/// <summary>把桌面发布目录和 ncw-agent 的 AOT 发布目录合成「桌面应用包」(布局见 <see cref="BundleLayout"/>)。</summary>
internal static class BundleBuilder
{
    public static BundleManifest Build(BundleLayout layout, string desktopDir, string agentDir, string version, TimeProvider clock)
    {
        var rid = layout.Rid;
        Require(Path.Combine(desktopDir, rid.DesktopExecutable), $"desktop publish for {rid} (run publish-desktop first)");
        Require(Path.Combine(agentDir, rid.AgentExecutable), $"ncw-agent publish for {rid} (run publish-agent first)");

        if (Directory.Exists(layout.OutputDir))
        {
            Directory.Delete(layout.OutputDir, recursive: true);
        }

        Directory.CreateDirectory(layout.ExecutableDir);
        CopyTree(desktopDir, layout.ExecutableDir, skipTopLevelNames: [BundleLayout.AgentDirName]);
        CopyTree(agentDir, layout.AgentDir, skipTopLevelNames: []);

        if (layout.IsMacBundle)
        {
            Directory.CreateDirectory(layout.ResourcesDir);
            File.WriteAllText(layout.InfoPlist, InfoPlist.Render(version));
        }

        EnsureExecutable(layout.DesktopExe, rid);
        EnsureExecutable(layout.AgentExe, rid);

        var manifest = Describe(layout, version, clock, signed: false);
        Write(layout, manifest);
        return manifest;
    }

    /// <summary>重新扫描包并重写清单(签名之后二进制变了,清单跟着更新)。保留创建时间和版本。</summary>
    public static BundleManifest Refresh(BundleLayout layout, BundleManifest previous, bool signed)
    {
        var manifest = previous with { Files = BundleManifest.ScanFiles(layout.AppRoot), Signed = signed };
        Write(layout, manifest);
        return manifest;
    }

    public static BundleManifest Describe(BundleLayout layout, string version, TimeProvider clock, bool signed) =>
        new(
            BundleManifest.CurrentSchema,
            BundleLayout.AppName,
            version,
            layout.Rid.Value,
            clock.GetUtcNow(),
            Path.GetFileName(layout.AppRoot),
            Rel(layout, layout.DesktopExe),
            Rel(layout, layout.AgentExe),
            signed,
            BundleManifest.ScanFiles(layout.AppRoot));

    public static void Write(BundleLayout layout, BundleManifest manifest) =>
        File.WriteAllText(layout.ManifestPath, JsonOut.Serialize(manifest, BuildJson.Default.BundleManifest));

    public static BundleManifest Read(string manifestPath) =>
        JsonOut.Deserialize(File.ReadAllText(manifestPath), BuildJson.Default.BundleManifest, manifestPath);

    private static string Rel(BundleLayout layout, string path) => Path.GetRelativePath(layout.AppRoot, path).Replace('\\', '/');

    private static void Require(string path, string what)
    {
        if (!File.Exists(path))
        {
            throw new BuildException($"Missing {what}: {path} not found.");
        }
    }

    /// <summary>发布目录里的调试符号(.pdb / .dSYM / .dbg)不进包。</summary>
    internal static bool IsSymbol(string name) =>
        name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".dSYM", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase);

    private static void CopyTree(string source, string target, string[] skipTopLevelNames, bool top = true)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (IsSymbol(name) || (top && skipTopLevelNames.Contains(name, StringComparer.Ordinal)))
            {
                continue;
            }

            File.Copy(file, Path.Combine(target, name), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (IsSymbol(name) || (top && skipTopLevelNames.Contains(name, StringComparer.Ordinal)))
            {
                continue;
            }

            CopyTree(dir, Path.Combine(target, name), skipTopLevelNames, top: false);
        }
    }

    private static void EnsureExecutable(string path, Rid rid)
    {
        if (rid.IsUnix && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }
}
