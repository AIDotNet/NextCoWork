using System.Text;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Tools;

namespace Ncw.Agent.Extensions.Tests.Support;

/// <summary>真文件、真进程的夹具:临时目录里有项目根 <c>ws</c> 和根外的 <c>outside</c>,用 <see cref="LocalEnvironment"/> 跑。夹具自己用 System.IO 摆文件(测试可以;受限的是产品代码)。</summary>
public sealed class Fixture : IDisposable
{
    public Fixture(bool withRoot = true)
    {
        var raw = Path.Combine(Path.GetTempPath(), "ncw-perm-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(raw);
        Env = new LocalEnvironment("dev-1");
        // macOS 的 /var → /private/var:路径守卫返回的是 realpath,期望值也必须是
        Base = Env.Fs.RealPathAsync(EnvPath.Parse(raw, Style)).AsTask().GetAwaiter().GetResult().Value;
        Root = Path.Combine(Base, "ws");
        Outside = Path.Combine(Base, "outside");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Outside);
        Runtime = new ToolRuntime { Environment = Env, Root = withRoot ? EnvPath.Parse(Root, Style) : null };
    }

    public static PathStyle Style => OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix;

    public string Base { get; }

    public string Root { get; }

    public string Outside { get; }

    public LocalEnvironment Env { get; }

    public ToolRuntime Runtime { get; }

    public string Abs(string rel) => Path.Combine(Root, rel);

    public EnvPath P(string rel) => EnvPath.Parse(Abs(rel), Style);

    public void Put(string rel, string content)
    {
        var path = Abs(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    public string SettingsPath => Path.Combine(Root, ".next-cowork", "settings.local.json");

    public void PutSettings(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, json);
    }

    public string? ReadSettings() => File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null;

    public RuleStoreContext RuleContext(string session = "session-1", bool withRoot = true) =>
        new(session, Env, withRoot ? EnvPath.Parse(Root, Style) : null);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Base, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
