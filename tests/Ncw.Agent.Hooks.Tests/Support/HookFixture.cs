using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Hooks.Tests.Support;

/// <summary>真文件 + 真进程的夹具:临时目录里有 <c>profile/</c>(全局设置)和项目根 <c>ws/</c>,跑在 <see cref="LocalEnvironment"/> 上。夹具自己用 System.IO 摆文件(测试可以;受限的是产品代码)。</summary>
public sealed class HookFixture : IAsyncDisposable
{
    public const string Workspace = "ws";

    public HookFixture()
    {
        var raw = Path.Combine(Path.GetTempPath(), "ncw-hooks-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(raw);
        Env = new LocalEnvironment("dev-1");
        // macOS 的 /var → /private/var:路径守卫返回的是 realpath,期望值也必须是
        Base = Env.Fs.RealPathAsync(EnvPath.Parse(raw, Style)).AsTask().GetAwaiter().GetResult().Value;
        Root = Path.Combine(Base, "ws");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Base, "profile"));
    }

    public static PathStyle Style => OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix;

    public string Base { get; }

    public string Root { get; }

    public LocalEnvironment Env { get; }

    public EnvPath RootPath => EnvPath.Parse(Root, Style);

    public string GlobalFile => Path.Combine(Base, "profile", "settings.json");

    public string ProjectFile => Path.Combine(Root, ".next-cowork", "settings.local.json");

    public string Abs(string rel) => Path.Combine(Root, rel);

    public void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    public string Read(string path) => File.ReadAllText(path);

    public HooksService NewService(IHookEvaluator? evaluator = null, Ncw.Agent.Kernel.Loop.ILoopDiagnostics? log = null) => new(new HooksServiceOptions
    {
        GlobalEnvironment = Env,
        GlobalSettingsPath = EnvPath.Parse(GlobalFile, Style),
        ResolveProject = (id, _) => ValueTask.FromResult(id == Workspace ? new HookProjectTarget(Env, RootPath) : null),
        Evaluator = evaluator,
        Log = log ?? Ncw.Agent.Kernel.Loop.NullLoopDiagnostics.Instance,
    });

    public static HookDefinition Command(string command, string hookEvent = HookEvents.PreToolUse, string id = "h1", int timeoutMs = 10_000, string? matcher = null) => new()
    {
        Id = id,
        Event = hookEvent,
        Command = command,
        TimeoutMs = timeoutMs,
        Matcher = matcher,
    };

    public static HookPayload Payload(string hookEvent = HookEvents.PreToolUse, HookScope scope = HookScope.Project) => new()
    {
        Event = hookEvent,
        SessionId = "s1",
        RunId = "r1",
        WorkspaceRoot = "/ws",
        Scope = scope,
        ToolName = "Bash",
        ToolInternalId = "Bash",
        ToolInput = System.Text.Json.JsonDocument.Parse("""{"command":"echo hello"}""").RootElement.Clone(),
    };

    public async ValueTask DisposeAsync()
    {
        await Env.DisposeAsync();
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
