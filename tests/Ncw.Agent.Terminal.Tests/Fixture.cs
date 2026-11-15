using Ncw.Agent.Environments;

namespace Ncw.Agent.Terminal.Tests;

/// <summary>管理器测试的公共装置:手动时钟、顺序 id、真实的临时目录(路径守卫和目录校验是真的)、假终端。</summary>
internal sealed class Fixture : IAsyncDisposable
{
    private int _next;

    public Fixture(Func<TerminalManagerOptions, TerminalManagerOptions>? configure = null, EnvironmentKind kind = EnvironmentKind.Local)
    {
        Time = new ManualTimeProvider();
        Dir = new TempDir();
        Env = new TestEnvironment(kind == EnvironmentKind.Local ? "local" : "dev-server", kind);
        var options = new TerminalManagerOptions { Time = Time, NewId = () => $"term-{Interlocked.Increment(ref _next)}" };
        Manager = new TerminalManager(configure?.Invoke(options) ?? options);
    }

    public ManualTimeProvider Time { get; }

    public TempDir Dir { get; }

    public TestEnvironment Env { get; }

    public TerminalManager Manager { get; }

    public const string Alice = "alice";

    public const string Bob = "bob";

    public TerminalCreateRequest Request(string scope = "ws", string? id = null, string? cwd = null, string? approval = null, TestEnvironment? env = null, int columns = 100, int rows = 30) =>
        new(scope, env ?? Env, Dir.Env, columns, rows, id, cwd, approval);

    public Task<TerminalInfo> CreateAsync(string scope = "ws", string? id = null, string controller = Alice, TestEnvironment? env = null) =>
        Manager.CreateAsync(Request(scope, id, env: env), controller);

    /// <summary>走完远程环境的「准备 → 批准」,返回凭据。</summary>
    public async Task<string> GrantAsync(string id, string controller = Alice, string scope = "ws", string? cwd = null)
    {
        var prepared = await Manager.PrepareAsync(Request(scope, id, cwd), controller);
        var approval = Assert.IsType<TerminalPreparation.ApprovalRequired>(prepared);
        return Manager.Approve(approval.Intent.Id, true, controller)!;
    }

    public static async Task<T> Throws<T>(Func<Task> action)
        where T : Exception => await Assert.ThrowsAsync<T>(action);

    public static async Task ExpectAsync(TerminalErrorCode code, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<TerminalException>(action);
        Assert.Equal(code, error.Code);
    }

    public static void Expect(TerminalErrorCode code, Action action)
    {
        var error = Assert.Throws<TerminalException>(action);
        Assert.Equal(code, error.Code);
    }

    public async Task WaitExitedAsync(string id) =>
        await Wait.UntilAsync(() => !Manager.Get(id).Alive, 10_000, $"terminal {id} to be marked exited");

    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        Dir.Dispose();
    }
}
