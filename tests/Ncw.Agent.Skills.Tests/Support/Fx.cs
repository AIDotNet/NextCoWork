using System.IO.Compression;
using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Skills.Tests.Support;

/// <summary>
/// 真临时目录 + 本机执行环境:<c>global/skills</c>(设备本机的全局层)与 <c>project/.next-cowork/skills</c>(项目层)。
/// ★ 不打桩文件系统:软链逃逸这条最重要的用例靠真的 realpath 才测得出来。
/// </summary>
public sealed class Fx : IDisposable
{
    public Fx()
    {
        Root = Path.Combine(Path.GetTempPath(), $"ncw-skill-{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(Root);
        Env = new LocalEnvironment("dev");
        GlobalSkills = Path.Combine(Root, "global", "skills");
        ProjectDir = Path.Combine(Root, "project");
        ProjectSkills = Path.Combine(ProjectDir, ".next-cowork", "skills");
        Directory.CreateDirectory(GlobalSkills);
        Directory.CreateDirectory(ProjectSkills);
    }

    public string Root { get; }

    public LocalEnvironment Env { get; }

    public string GlobalSkills { get; }

    public string ProjectDir { get; }

    public string ProjectSkills { get; }

    public EnvPath P(string path) => LocalEnvironment.PathOf(path);

    public SkillScanInput Input(bool global = true, bool project = true, IExecutionEnvironment? projectEnvironment = null, IReadOnlyList<PluginSkillRoot>? plugins = null) =>
        new(global ? Env : null, global ? P(GlobalSkills) : null, project ? projectEnvironment ?? Env : null, project ? P(ProjectSkills) : null, plugins);

    public Task<SkillScanResult> ScanAsync(IReadOnlyList<PluginSkillRoot>? plugins = null) => SkillLoader.ScanAsync(Input(plugins: plugins));

    /// <summary>在某一层里放一条 Skill。<paramref name="content"/> 是整个 SKILL.md。</summary>
    public static void Put(string scopeRoot, string dir, string content)
    {
        Directory.CreateDirectory(Path.Combine(scopeRoot, dir));
        File.WriteAllText(Path.Combine(scopeRoot, dir, "SKILL.md"), content);
    }

    /// <summary>一份最小的 SKILL.md。刻意不写 name:名字由目录名给,和 CC 一致。</summary>
    public static string Md(string? description, string body = "照着这个做。") =>
        $"---\n{(description is null ? "" : $"description: {description}\n")}---\n{body}\n";

    public static bool Said(SkillScanResult r, string key) => r.Diagnostics.Any(d => d.Key == key);

    public static byte[] Zip(params (string Path, string Content)[] entries) => Zip(entries.Select(e => (e.Path, Encoding.UTF8.GetBytes(e.Content))).ToArray());

    public static byte[] Zip(params (string Path, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var output = entry.Open();
                output.Write(content);
            }
        }

        return stream.ToArray();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Host 的「Skill 在哪」的测试版:全局 = <c>Root/global/skills</c>,项目 = 一个固定的项目。</summary>
public sealed class TestLocations(Fx fx, IExecutionEnvironment? projectEnvironment = null, IReadOnlyList<PluginSkillRoot>? plugins = null) : ISkillLocations
{
    public ValueTask<SkillScanInput> ScanInputAsync(string? workspaceId, CancellationToken ct) =>
        ValueTask.FromResult(fx.Input(project: workspaceId is not null, projectEnvironment: projectEnvironment, plugins: plugins));

    public ValueTask<SkillInstallRoot> InstallRootAsync(SkillInstallScope scope, string? workspaceId, CancellationToken ct)
    {
        if (scope == SkillInstallScope.Project)
        {
            if (workspaceId is null)
            {
                throw new SkillException(SkillErrorKeys.WorkspaceRequired, "A project install needs a workspace.");
            }

            return ValueTask.FromResult(new SkillInstallRoot(projectEnvironment ?? fx.Env, fx.P(fx.ProjectSkills)));
        }

        return ValueTask.FromResult(new SkillInstallRoot(fx.Env, fx.P(fx.GlobalSkills)));
    }
}
