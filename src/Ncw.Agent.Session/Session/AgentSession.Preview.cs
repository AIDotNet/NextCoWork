using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Session;

public sealed partial class AgentSession
{
    /// <summary>
    /// 「现在这段历史占多少上下文」(<c>context.preview</c>):用下一个 Run 会用的同一套组装(系统提示词、工具快照、项目指令、git 状态、窗口)估一次,
    /// 不发请求、不落库、不占会话锁(只读)。会话不存在返回 null。
    /// 估算和每回合发出的 <c>context_usage</c> 是同一个函数的结果,所以压力条在两处看到的数一致;上游的最近一次真值要等真有一个 Run 才有,这里只用估算。
    /// </summary>
    public async Task<ContextUsage?> PreviewContextAsync(ModelRef model, ThinkingLevel thinking, SessionSettings? settings = null, CancellationToken ct = default)
    {
        var records = LoadHistory();
        if (records is null)
        {
            return null;
        }

        var runId = new RunId("preview-" + _deps.Ids.NewId());
        var request = new RunRequest(runId, [], model, thinking) { Settings = settings ?? new SessionSettings() };
        var state = await StartStateAsync(request, new RunDraft(request), records, new ExtensionContext(runId, SessionId, Log), ct).ConfigureAwait(false);
        var turn = new TurnContext(runId, SessionId, 0, state.Messages, model, state.Options, 0);
        return AssembleTurn(state, turn, SnapshotTools(state)).Usage;
    }
}
