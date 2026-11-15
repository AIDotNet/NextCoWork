using System.Threading.Channels;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Core.Domain;

namespace Ncw.Agent.Kernel.Subagents;

/// <summary>
/// 监听一个子 Run 的低频状态,并把汇总镜像到父 Run 的事件流里(2.x <c>monitorChildRun</c>):工具开始 / 结束、上下文用量、消息结束、重试 / 切换供应商 → <see cref="SubagentUpdateEvent"/>,
/// 带 <c>ChildSeq</c> 去重。<b>只在父 Run 仍在运行时</b>才有意义——父 Run 一结束,<see cref="RunRegistry.Post"/> 就丢弃(终态之后的事件一律丢弃)。
/// 原始子 Run 事件仍会经继承的订阅推给控制端,后台子代理在父 Run 结束后也能继续被观察。
/// <para>
/// 接法:作为子 Run 的<b>启动前绑定的订阅</b>(<see cref="RunLaunch.Subscriptions"/>)——第一个事件之前就挂好。订阅会被孙 Run 继承,所以按 <c>RunId</c> 过滤。
/// 订阅回调在发送线程上串行执行,不许回调注册表的写方法,所以这里只做计数,真正的 <c>Post</c> 交给一个串行的泵(保序)。
/// </para>
/// </summary>
internal sealed class ChildMonitor : IRunListener
{
    private readonly RunRegistry _registry;
    private readonly RunId _parent;
    private readonly RunId _child;
    private readonly string _callId;
    private readonly TimeProvider _clock;
    private readonly Channel<AgentEvent> _outbox = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;
    private int _toolCalls;
    private int _toolErrors;
    private TokenUsage? _usage;

    public ChildMonitor(RunRegistry registry, RunId parent, RunId child, string callId, TimeProvider clock, Ncw.Agent.Kernel.Loop.ILoopDiagnostics log)
    {
        _registry = registry;
        _parent = parent;
        _child = child;
        _callId = callId;
        _clock = clock;
        _pump = Task.Run(async () =>
        {
            await foreach (var ev in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    _registry.Post(_parent, ev);
                }
                catch (Exception e)
                {
                    log.Warn("Mirroring a subagent update to the parent run failed.", e);
                }
            }
        });
        Subscription = registry.Subscribe(this);
    }

    public RunSubscription Subscription { get; }

    /// <summary>子 Run 累计的用量(给前台结局带回去)。</summary>
    public TokenUsage? Usage => Volatile.Read(ref _usage);

    public bool TryDeliver(IReadOnlyList<RunEventBatch> batches)
    {
        foreach (var batch in batches)
        {
            if (batch.RunId != _child)
            {
                continue; // 孙 Run 的事件(订阅被继承下去了)
            }

            foreach (var sequenced in batch.Events)
            {
                Mirror(sequenced.Event, sequenced.Seq);
            }
        }

        return true;
    }

    public void Resync(ResyncReason reason)
    {
    }

    private void Mirror(AgentEvent ev, long seq)
    {
        var at = _clock.GetUtcNow();
        switch (ev)
        {
            case ToolStartEvent start:
                _toolCalls++;
                Send(new SubagentUpdateEvent(_callId, _child)
                {
                    Phase = SubagentPhase.Tool,
                    CurrentTool = Patch<string>.Set(start.ToolName),
                    CurrentTarget = TargetOf(start.Input) is { } target ? Patch<string>.Set(target) : Patch<string>.Clear,
                    ToolCalls = _toolCalls,
                    ToolErrors = _toolErrors,
                    ChildSeq = seq,
                    At = at,
                });
                break;
            case ToolEndEvent end:
                if (end.IsError)
                {
                    _toolErrors++;
                }

                Send(new SubagentUpdateEvent(_callId, _child)
                {
                    Phase = SubagentPhase.Thinking,
                    CurrentTool = Patch<string>.Clear,
                    CurrentTarget = Patch<string>.Clear,
                    ToolCalls = _toolCalls,
                    ToolErrors = _toolErrors,
                    ChildSeq = seq,
                    At = at,
                });
                break;
            case ContextUsageEvent context:
                Send(new SubagentUpdateEvent(_callId, _child)
                {
                    ContextUsage = new SubagentContextUsage(context.Used, context.Window, context.ShouldCompact),
                    ToolCalls = _toolCalls,
                    ToolErrors = _toolErrors,
                    ChildSeq = seq,
                });
                break;
            case MessageEndEvent { Message.Role: Role.Assistant } message:
                if (message.Usage is { } usage)
                {
                    _usage = _usage is null ? usage : _usage.Add(usage);
                }

                // 一轮回复完整结束 = 之前那句「正在退避重试」必然已经过期。清除走的是和 CurrentTool 同一个约定(键在即清),
                // 所以哪怕这条遥测抢在继承事件前头到达,卡片上也不会挂着一句过期的重试提示。
                Send(new SubagentUpdateEvent(_callId, _child)
                {
                    Phase = SubagentPhase.Finishing,
                    Notice = Patch<SubagentNotice>.Clear,
                    Usage = message.Usage,
                    ToolCalls = _toolCalls,
                    ToolErrors = _toolErrors,
                    ChildSeq = seq,
                });
                break;
            case ProviderRetryEvent retry:
                Send(new SubagentUpdateEvent(_callId, _child)
                {
                    Notice = Patch<SubagentNotice>.Set(new SubagentNotice(SubagentNotice.Retry, retry.Reason, retry.Attempt)),
                    ChildSeq = seq,
                });
                break;
            case ProviderSwitchEvent switched:
                Send(new SubagentUpdateEvent(_callId, _child)
                {
                    Notice = Patch<SubagentNotice>.Set(new SubagentNotice(SubagentNotice.Switch, switched.Reason, To: switched.To)),
                    ChildSeq = seq,
                });
                break;
        }
    }

    private void Send(AgentEvent ev) => _outbox.Writer.TryWrite(ev);

    /// <summary>子 Run 结束后把还没送出去的更新送完(保证 <c>subagent_end</c> 排在它们后面)。</summary>
    public async Task DrainAsync()
    {
        _outbox.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
    }

    /// <summary>卡片上「正在做什么」的目标:路径 / 查询 / 模式 / 命令 / URL 里第一个非空的,截 160 字。</summary>
    internal static string? TargetOf(System.Text.Json.JsonElement input)
    {
        if (input.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return Clip(input.GetString() ?? "");
        }

        if (input.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in (ReadOnlySpan<string>)["path", "filePath", "file_path", "query", "pattern", "command", "url"])
        {
            if (input.TryGetProperty(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String && value.GetString() is { } text && text.Trim().Length > 0)
            {
                return Clip(text);
            }
        }

        return null;

        static string Clip(string text) => text.Length <= 160 ? text : text[..160];
    }
}
