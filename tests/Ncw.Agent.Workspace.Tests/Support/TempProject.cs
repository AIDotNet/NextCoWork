using Ncw.Agent.Environments;

namespace Ncw.Agent.Workspace.Tests;

/// <summary>
/// 真文件、真进程的测试夹具:一个临时目录里有项目根 <c>ws</c> 和根外的 <c>outside</c>,用 <see cref="LocalEnvironment"/> 跑。
/// 夹具自己用 System.IO 摆文件(测试可以;受限的是被测的服务),服务只能经 <see cref="IExecutionEnvironment"/> 访问。
/// </summary>
public sealed class TempProject : IDisposable
{
    public TempProject()
    {
        var raw = Path.Combine(Path.GetTempPath(), "ncw-ws-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(raw);
        Env = new LocalEnvironment("dev-1");
        // macOS 的 /var → /private/var:路径守卫返回的是 realpath,期望值也必须是
        Base = Env.Fs.RealPathAsync(EnvPath.Parse(raw, Style)).AsTask().GetAwaiter().GetResult().Value;
        Root = Path.Combine(Base, "ws");
        Outside = Path.Combine(Base, "outside");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Outside);
    }

    public static PathStyle Style => OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix;

    public LocalEnvironment Env { get; }

    /// <summary>临时目录(已 realpath)。</summary>
    public string Base { get; }

    /// <summary>项目根(已存在)。</summary>
    public string Root { get; }

    /// <summary>项目根之外的目录(已存在),放「不许碰」的东西。</summary>
    public string Outside { get; }

    public EnvPath RootPath => EnvPath.Parse(Root, Style);

    public string Abs(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>摆一个文件(自动建父目录),返回绝对路径。</summary>
    public string Write(string relative, string content)
    {
        var path = Abs(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string Read(string relative) => File.ReadAllText(Abs(relative));

    public bool Exists(string relative) => File.Exists(Abs(relative)) || Directory.Exists(Abs(relative));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Base, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 临时目录清不掉不影响测试结论
        }
    }
}

/// <summary>需要真 git 的测试:机器上没有 git 就跳过(<see cref="Assert.Skip"/>)。</summary>
public static class GitAvailability
{
    private static readonly Lazy<bool> Present = new(Probe);

    public static void RequireGit() => Assert.SkipUnless(Present.Value, "git is not available on this machine");

    private static bool Probe()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
