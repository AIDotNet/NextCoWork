using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools;

namespace Ncw.Agent.Tools.Tests;

/// <summary>
/// 真文件、真进程的测试夹具:一个临时目录里有项目根 <c>ws</c> 和根外的 <c>outside</c>,用 <see cref="LocalEnvironment"/> 跑。
/// 夹具自己用 System.IO 摆文件(测试可以;受限的是工具),工具只能经 <see cref="IExecutionEnvironment"/> 访问。
/// </summary>
public sealed class Fixture : IDisposable
{
    public const string Secret = "AKIA-TOTALLY-SECRET-CONTENT-9f3c";

    private readonly FileMutationQueue _queue = new();

    /// <param name="clock">工具运行时的时钟。搜索类用例传一个不走的时钟:5 秒的搜索预算量的是墙上时间,runner 卡顿十几秒时(Windows CI 上出现过)与预算无关的用例也会被判「超出预算」。</param>
    public Fixture(bool shells = false, GrepBackends backends = GrepBackends.None, bool withRoot = true, EnvPath? writeFence = null, TimeProvider? clock = null)
    {
        var raw = Path.Combine(Path.GetTempPath(), "ncw-tools-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(raw);
        Env = new LocalEnvironment("dev-1");
        // macOS 的 /var → /private/var:路径守卫返回的是 realpath,期望值也必须是
        Base = Env.Fs.RealPathAsync(EnvPath.Parse(raw, Style)).AsTask().GetAwaiter().GetResult().Value;
        Root = Path.Combine(Base, "ws");
        Outside = Path.Combine(Base, "outside");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "secret.txt"), Secret);

        Shells = shells ? new BackgroundShells() : null;
        Runtime = new ToolRuntime
        {
            Environment = Env,
            Root = withRoot ? EnvPath.Parse(Root, Style) : null,
            Shells = Shells,
            GrepBackends = backends,
            WriteFence = writeFence,
            Clock = clock ?? TimeProvider.System,
        };
    }

    public static PathStyle Style => OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix;

    public string Base { get; private set; }

    public string Root { get; }

    public string Outside { get; }

    public LocalEnvironment Env { get; }

    public ToolRuntime Runtime { get; }

    public BackgroundShells? Shells { get; }

    public List<ToolProgress> Progress { get; } = [];

    public EnvPath P(string relativeToRoot) => EnvPath.Parse(Path.Combine(Root, relativeToRoot), Style);

    public string Abs(string relativeToRoot) => Path.Combine(Root, relativeToRoot);

    public void Put(string rel, string content)
    {
        var path = Abs(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    public void PutBytes(string rel, byte[] bytes)
    {
        var path = Abs(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public string Text(string rel) => File.ReadAllText(Abs(rel));

    public byte[] Bytes(string rel) => File.ReadAllBytes(Abs(rel));

    /// <summary>构造一次调用的上下文并执行(像 Loop 那样先做预检:参数校验)。</summary>
    public async Task<ToolResult> Run(IAgentTool tool, JsonElement input, string run = "run-1", CancellationToken ct = default, ToolRuntime? runtime = null)
    {
        var check = tool.PrepareArguments(input);
        if (!check.IsValid)
        {
            return ToolResult.Error(check.Error!);
        }

        var tag = new EnvironmentTag(Env.Id.DeviceId, Env.Id.Environment);
        var inner = new ToolContext(new RunId(run), "call-1", "session-1", 0, tag, new SyncProgress(this), _queue);
        var ctx = new ExecutionToolContext(inner, runtime ?? Runtime);
        return await tool.ExecuteAsync(new ToolInvocation("call-1", tool.Info.InternalId, tool, check.Input), ctx, ct);
    }

    private sealed class SyncProgress(Fixture fixture) : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value)
        {
            lock (fixture.Progress)
            {
                fixture.Progress.Add(value);
            }
        }
    }

    public Task<ToolResult> Run(IAgentTool tool, params (string Name, object? Value)[] args) => Run(tool, Args(args));

    public Task<ToolResult> RunAs(string run, IAgentTool tool, params (string Name, object? Value)[] args) => Run(tool, Args(args), run);

    public static JsonElement Args(params (string Name, object? Value)[] args)
    {
        var o = new JsonObject();
        foreach (var (name, value) in args)
        {
            o[name] = value switch
            {
                null => null,
                string s => JsonValue.Create(s),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                bool b => JsonValue.Create(b),
                JsonNode n => n,
                _ => throw new ArgumentException($"unsupported test arg {value.GetType()}"),
            };
        }

        return Jsn.ToElement(o);
    }

    public static JsonArray Edits(params (string Old, string New)[] edits)
    {
        var a = new JsonArray();
        foreach (var (o, n) in edits)
        {
            a.Push(new JsonObject { ["old_string"] = o, ["new_string"] = n });
        }

        return a;
    }

    public void Dispose()
    {
        Shells?.ShutdownAsync().AsTask().GetAwaiter().GetResult();
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

public static class ResultExtensions
{
    public static string Text(this ToolResult result) =>
        string.Join('\n', result.Content.OfType<TextPart>().Select(p => p.Text));

    public static JsonElement DetailsOrThrow(this ToolResult result) => result.Details ?? throw new InvalidOperationException("no details");
}
