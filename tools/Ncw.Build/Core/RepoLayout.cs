namespace Ncw.Build.Core;

/// <summary>仓库里的固定位置与 artifacts/ 下的产物目录约定(artifacts/ 在 .gitignore 里,不放进 bin/ 或 obj/)。</summary>
internal sealed class RepoLayout
{
    public RepoLayout(string root, string? artifactsDir = null)
    {
        Root = Path.GetFullPath(root);
        ArtifactsDir = Path.GetFullPath(artifactsDir ?? Path.Combine(Root, "artifacts"));
    }

    public string Root { get; }

    public string ArtifactsDir { get; }

    public string AgentProject => Path.Combine(Root, "src", "Ncw.Agent", "Ncw.Agent.csproj");

    public string RelayProject => Path.Combine(Root, "src", "Ncw.Relay", "Ncw.Relay.csproj");

    public string DesktopProject => Path.Combine(Root, "src", "Ncw.Desktop", "Ncw.Desktop.csproj");

    /// <summary>应用图标(macOS 的 .icns,由同目录的 AppIcon.png 用 iconutil 生成后入库;不在构建时生成,这样在非 macOS 主机上跨平台打 osx 包也有图标)。</summary>
    public string MacIcon => Path.Combine(Root, "src", "Ncw.Desktop", "Assets", "AppIcon.icns");

    public string BuildProps => Path.Combine(Root, "Directory.Build.props");

    public string ToolManifest => Path.Combine(Root, ".config", "dotnet-tools.json");

    public string PublishDir(Rid rid, string what) => Path.Combine(ArtifactsDir, "publish", rid.Value, what);

    public string SymbolsDir(Rid rid) => Path.Combine(ArtifactsDir, "symbols", rid.Value);

    public string BundleDir(Rid rid) => Path.Combine(ArtifactsDir, "bundle", rid.Value);

    public string VelopackDir(Rid rid, string channel) => Path.Combine(ArtifactsDir, "velopack", rid.Value, channel);

    public string ReleaseDir(string version) => Path.Combine(ArtifactsDir, "release", version);

    /// <summary>从当前目录或程序所在目录向上找 NextCoWork.slnx。</summary>
    public static RepoLayout Find(string? artifactsDir = null, string? startDir = null)
    {
        foreach (var start in new[] { startDir ?? Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "NextCoWork.slnx")))
                {
                    return new RepoLayout(dir.FullName, artifactsDir);
                }
            }
        }

        throw new BuildException("Cannot find the repository root (NextCoWork.slnx). Run from inside the repository.");
    }

    /// <summary>应用版本:<c>Directory.Build.props</c> 里的 <c>Version</c>(客户端、ncw-agent、安装包共用这一个,docs/design/04 §3)。</summary>
    public string ReadVersion()
    {
        var text = File.ReadAllText(BuildProps);
        var match = System.Text.RegularExpressions.Regex.Match(text, @"<Version>\s*([^<\s]+)\s*</Version>");
        return match.Success ? match.Groups[1].Value : throw new BuildException($"No <Version> in {BuildProps}.");
    }
}
