using Ncw.Protocol.Orchestration;

namespace Ncw.Agent.Cloud;

/// <summary>应用一条指令的结局。</summary>
public readonly record struct CommandOutcome(string Status, string? Code = null)
{
    public static readonly CommandOutcome Applied = new(AckStatuses.Applied);

    /// <summary>暂时做不了(例如休眠请求时有 Run 在跑)。序号不被消耗:编排服务可以重发同一条。</summary>
    public static CommandOutcome Busy(string? code = null) => new(AckStatuses.Busy, code);

    public static CommandOutcome Invalid(string code) => new(AckStatuses.Rejected, code);
}

/// <summary>指令的应用方(Host 的 <c>CloudRuntime</c> 实现)。<b>必须幂等</b>(同一条指令因为崩溃被应用两次不能出事)。</summary>
public interface ICommandHandler
{
    Task<CommandOutcome> ApplyAsync(CommandBody command, CancellationToken ct);
}

/// <summary>
/// 指令泵:验签 → 防重放 → 应用 → 回执。所有指令串行处理(同一时刻只处理一条)。
/// <list type="number">
/// <item><b>验签</b>(Ed25519,编排密钥来自启动配置):错误签名、不认识的 kid、格式不对、发给别的设备、已过期的指令一律拒绝,不应用、不消耗序号。</item>
/// <item><b>防重放</b>:序号必须严格大于已应用的最大序号。序号<b>先落盘再应用</b>(宁可崩溃时丢一条、由编排服务用新序号重发,也不让攻击者重放);
/// 已应用过的序号回 <see cref="AckStatuses.Duplicate"/>(让编排服务停止重发),不再应用。</item>
/// <item><b>应用</b>:<see cref="ICommandHandler"/>。结局 <c>busy</c> 时序号回退(这条还没做,编排服务可以重发)。</item>
/// <item><b>回执</b>:每条都回执(尽力而为;回执失败不影响已应用的状态——编排服务下次重发时会得到 <c>duplicate</c>)。</item>
/// </list>
/// </summary>
public sealed class CommandPump
{
    private readonly CommandVerifier _verifier;
    private readonly ReplayGuard _guard;
    private readonly ICloudStore _store;
    private readonly ICommandHandler _handler;
    private readonly IOrchestrationChannel _channel;
    private readonly Action<string, string?>? _log;
    private readonly SemaphoreSlim _serial = new(1, 1);

    public CommandPump(CommandVerifier verifier, ICloudStore store, ICommandHandler handler, IOrchestrationChannel channel, Action<string, string?>? log = null)
    {
        _verifier = verifier;
        _store = store;
        _handler = handler;
        _channel = channel;
        _log = log;
        _guard = new ReplayGuard(store.ReadLong(CloudKeys.CommandSeq));
    }

    /// <summary>已应用的最大序号(打招呼时告诉编排服务,让它知道重发哪些)。</summary>
    public long LastSeq => _guard.Last;

    /// <summary>处理一批指令(按序号升序;乱序到达的批次里,小序号的会被当成重放拒绝,所以这里先排序)。返回各条的结局。</summary>
    public async Task<IReadOnlyList<CommandOutcome>> ProcessAsync(IEnumerable<SignedEnvelope> envelopes, CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var checks = envelopes.Take(OrchestrationProtocol.MaxCommandsPerPoll).Select(_verifier.Verify).OrderBy(static c => c.Seq).ToList();
            var outcomes = new List<CommandOutcome>(checks.Count);
            foreach (var check in checks)
            {
                outcomes.Add(await ProcessOneAsync(check, ct).ConfigureAwait(false));
            }

            return outcomes;
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<CommandOutcome> ProcessOneAsync(CommandCheck check, CancellationToken ct)
    {
        if (!check.Ok)
        {
            _log?.Invoke("command.rejected", check.RejectCode);
            var rejected = CommandOutcome.Invalid(check.RejectCode!);
            await AckAsync(check.Seq, rejected, ct).ConfigureAwait(false);
            return rejected;
        }

        var body = check.Body!;
        var previous = _guard.Last;
        if (!_guard.TryAdvance(body.Seq))
        {
            _log?.Invoke("command.replayed", body.Type);
            var duplicate = new CommandOutcome(AckStatuses.Duplicate, RejectCodes.Replayed);
            await AckAsync(body.Seq, duplicate, ct).ConfigureAwait(false);
            return duplicate;
        }

        _store.WriteLong(CloudKeys.CommandSeq, body.Seq); // 先落盘再应用

        CommandOutcome outcome;
        try
        {
            outcome = await _handler.ApplyAsync(body, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log?.Invoke("command.failed", body.Type);
            outcome = CommandOutcome.Invalid(RejectCodes.Invalid);
        }

        if (outcome.Status == AckStatuses.Busy)
        {
            // 没做成:把序号退回去,编排服务重发同一条时还能被接受(命令是串行处理的,所以回退是安全的)
            _guard.Reset(previous);
            _store.WriteLong(CloudKeys.CommandSeq, previous);
        }

        await AckAsync(body.Seq, outcome, ct).ConfigureAwait(false);
        return outcome;
    }

    private async Task AckAsync(long seq, CommandOutcome outcome, CancellationToken ct)
    {
        if (seq <= 0)
        {
            return; // 连序号都解不出来的垃圾,没有东西可以回执
        }

        try
        {
            await _channel.AckAsync(new CommandAck(seq, outcome.Status, outcome.Code), ct).ConfigureAwait(false);
        }
        catch (OrchestrationException)
        {
            // 回执丢了:编排服务会重发,得到 duplicate
        }
    }
}
