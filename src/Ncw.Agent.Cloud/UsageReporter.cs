using Ncw.Protocol.Orchestration;

namespace Ncw.Agent.Cloud;

/// <summary>上报进度:已经被编排服务确认的累计基线,和还没确认的那一批(<see cref="Outbox"/>,重试时原样重发)。</summary>
public sealed record ReportState(long Seq, long Runs, long Input, long Output, long Cache, long Compute, long LastTo, UsageBatch? Outbox);

/// <summary>
/// 用量上报(Agent → 编排服务,B9):<b>批量、可重试、幂等</b>。
/// <list type="bullet">
/// <item>每一批是自上一批被确认以来的<b>增量</b>(Run 数、token、计算秒数);<c>batchId = deviceId:seq</c>。</item>
/// <item>发送失败(网络 / 5xx / 429)时这一批留在发件箱(落盘),下次<b>原样</b>重发——同一个 batchId,编排服务按它去重;
/// 重发期间新增的用量不并进旧批次,而是下一批(批次内容一旦生成就不变,所以重放永远得到相同的结果)。</item>
/// <item>只有收到成功(含 <see cref="AckResponse.Duplicate"/>)才推进序号和基线;基线落盘,Agent 重启 / 休眠唤醒之后不会重报也不会漏报。</item>
/// <item>没有任何增量时不发(不制造空批次)。</item>
/// </list>
/// </summary>
public sealed class UsageReporter(string deviceId, ICloudStore store, IUsageSource usage, QuotaService quota, IOrchestrationChannel channel, TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ReportState State => store.Read(CloudKeys.Report, CloudStateJson.Default.ReportState) ?? new ReportState(0, 0, 0, 0, 0, 0, 0, null);

    /// <summary>
    /// 尽力上报一次:先补发发件箱里没确认的批次,再把新增量作为下一批发出。返回已经被确认的批次数。
    /// 失败时抛 <see cref="OrchestrationException"/>(状态不变,下次再来)。
    /// </summary>
    public async Task<int> FlushAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sent = 0;
            // 最多两批:补发旧的 + 发新的
            for (var i = 0; i < 2; i++)
            {
                var state = State;
                if (state.Outbox is null)
                {
                    state = Prepare(state);
                    if (state.Outbox is null)
                    {
                        return sent;
                    }
                }

                var batch = state.Outbox;
                var ack = await channel.ReportUsageAsync(batch, ct).ConfigureAwait(false);
                if (!ack.Ok)
                {
                    throw new OrchestrationException("The orchestration service did not accept the usage batch.", retryable: true);
                }

                // 确认:基线前进一个批次的增量,发件箱清空
                store.Write(CloudKeys.Report, state with
                {
                    Seq = batch.Seq,
                    Runs = state.Runs + batch.Runs,
                    Input = state.Input + batch.InputTokens,
                    Output = state.Output + batch.OutputTokens,
                    Cache = state.Cache + batch.CacheTokens,
                    Compute = state.Compute + batch.ComputeSeconds,
                    LastTo = batch.To,
                    Outbox = null,
                }, CloudStateJson.Default.ReportState);
                sent++;
            }

            return sent;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>用当前累计值减去基线,生成下一批并写进发件箱(落盘);没有增量返回原状态。</summary>
    private ReportState Prepare(ReportState state)
    {
        quota.Meter();
        var ledger = quota.Ledger;
        var (input, output, cache) = usage.TokenTotals();
        var runs = ledger.CumRuns - state.Runs;
        var inTokens = input - state.Input;
        var outTokens = output - state.Output;
        var cacheTokens = cache - state.Cache;
        var compute = ledger.CumComputeSeconds - state.Compute;
        if (runs <= 0 && inTokens <= 0 && outTokens <= 0 && cacheTokens <= 0 && compute <= 0)
        {
            return state;
        }

        var seq = state.Seq + 1;
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var batch = new UsageBatch(
            OrchestrationProtocol.Version, $"{deviceId}:{seq}", deviceId, seq, state.LastTo == 0 ? now : state.LastTo, now,
            Math.Max(0, runs), Math.Max(0, inTokens), Math.Max(0, outTokens), Math.Max(0, cacheTokens), Math.Max(0, compute), usage.StorageBytes());
        var next = state with { Outbox = batch };
        store.Write(CloudKeys.Report, next, CloudStateJson.Default.ReportState);
        return next;
    }
}
