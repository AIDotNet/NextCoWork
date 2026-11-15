using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;

namespace Ncw.Agent.Kernel.Loop;

/// <summary>
/// 回合循环(02-loop):调模型 → 执行工具 → 再调模型,直到停下。不含业务逻辑:不落库、不判权限、不组装提示词,
/// 这些都通过 <see cref="LoopHooks"/> 注入。上游、时钟、ID、退避睡眠全部由构造参数注入。
/// 一个实例可以同时跑多个 Run(没有跨 Run 的状态)。
/// </summary>
/// <remarks>
/// 事件流的保证(测试逐条覆盖):
/// <list type="bullet">
/// <item>第一个事件是 <see cref="RunStartEvent"/>,最后一个是 <see cref="RunEndEvent"/>,且恰好各一个;</item>
/// <item><see cref="TurnStartEvent"/> / <see cref="TurnEndEvent"/> 成对(包括出错、中止、Terminate);</item>
/// <item>每个 <see cref="MessageStartEvent"/> 都以 <see cref="MessageEndEvent"/> 或 <see cref="MessageDiscardEvent"/> 收尾;</item>
/// <item>每个 <see cref="ToolStartEvent"/> 恰好对应一个 <see cref="ToolEndEvent"/>。</item>
/// </list>
/// 消费者提前放弃枚举(或取消)等于中止:Run 走中止收尾(补结果、提交半截回复),然后才结束。
/// </remarks>
public sealed class AgentLoop(
    ILlmClient llm,
    IIdGenerator ids,
    TimeProvider clock,
    IDelayer delayer,
    AgentLoopOptions? options = null,
    ILoopDiagnostics? diagnostics = null)
{
    private readonly AgentLoopOptions _options = options ?? new AgentLoopOptions();
    private readonly ILoopDiagnostics _diagnostics = diagnostics ?? NullLoopDiagnostics.Instance;

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        LoopInput input, LoopHooks hooks, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(hooks);
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = new RunExecution(llm, ids, clock, delayer, _options, _diagnostics, input, hooks, channel.Writer, cts.Token);
        var producer = Task.Run(run.ExecuteAsync, CancellationToken.None);
        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                yield return ev;
            }
        }
        finally
        {
            // 消费者提前退出(或取消)= 中止:让生产者走完中止收尾再返回,不留悬空的任务。
            await cts.CancelAsync().ConfigureAwait(false);
            await producer.ConfigureAwait(false);
        }
    }
}
