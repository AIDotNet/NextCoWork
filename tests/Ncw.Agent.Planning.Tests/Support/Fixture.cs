using System.Text;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Extensions.Planning.Plans;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Tools;

namespace Ncw.Agent.Planning.Tests.Support;

/// <summary>真文件的夹具:临时目录里有项目根 <c>ws</c>,跑在 <see cref="LocalEnvironment"/> 上。夹具自己用 System.IO 摆文件(测试可以;受限的是产品代码)。</summary>
public sealed class Fixture : IDisposable
{
    public Fixture(bool withRoot = true)
    {
        var raw = Path.Combine(Path.GetTempPath(), "ncw-plan-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(raw);
        Env = new LocalEnvironment("dev-1");
        // macOS 的 /var → /private/var:路径守卫返回的是 realpath,期望值也必须是
        Base = Env.Fs.RealPathAsync(EnvPath.Parse(raw, Style)).AsTask().GetAwaiter().GetResult().Value;
        Root = Path.Combine(Base, "ws");
        Directory.CreateDirectory(Root);
        Runtime = new ToolRuntime { Environment = Env, Root = withRoot ? EnvPath.Parse(Root, Style) : null };
        Gate = new InteractionGate(new SeqIds("i"));
        Permissions = new PermissionRuns();
    }

    public static PathStyle Style => OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix;

    public string Base { get; }

    public string Root { get; }

    public LocalEnvironment Env { get; }

    public ToolRuntime Runtime { get; }

    public InteractionGate Gate { get; }

    public PermissionRuns Permissions { get; }

    public string Abs(string rel) => Path.Combine(Root, rel);

    public EnvPath P(string rel) => EnvPath.Parse(Abs(rel), Style);

    public void Put(string rel, string content)
    {
        var path = Abs(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    public string Read(string rel) => File.ReadAllText(Abs(rel));

    /// <summary>登记一个 Run 的权限状态(带交互句柄)。<paramref name="depth"/> &gt; 0 = 子代理(不能问人)。</summary>
    public PermissionRunState Register(RunId runId, string sessionId = "s1", int depth = 0)
    {
        var state = PermissionRunState.FromRuntime(Runtime, runId, sessionId, Gate.OpenRun(runId, depth), PermissionMode.Full);
        Permissions.Register(state);
        return state;
    }

    /// <summary>工具拿到的上下文:带执行环境的 <see cref="ExecutionToolContext"/>。</summary>
    public ExecutionToolContext Ctx(RunId runId, string sessionId = "s1", int depth = 0, string callId = "call-1", FileMutationQueue? queue = null) =>
        new(new ToolContext(runId, callId, sessionId, depth, new EnvironmentTag("dev-1", "local"), new Progress(), queue ?? new FileMutationQueue()), Runtime);

    public PlanService Plans(Func<Ncw.Agent.Kernel.Extensions.ExtensionContext, bool>? workflow = null) =>
        new(new PlanOptions { PermissionRuns = Permissions, PlanWorkflow = workflow ?? (_ => true), Ids = new SeqIds("P") });

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

    private sealed class Progress : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value)
        {
        }
    }
}
