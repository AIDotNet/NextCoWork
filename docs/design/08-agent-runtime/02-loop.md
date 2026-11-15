# 08.02 · Agent 循环(`Ncw.Agent.Kernel.Loop`)

循环层对应 pi 的 `agentLoop`,行为对应 2.x `agent-session.ts` 的 `loop / turn / executeAll / executeOne`。
它**不含业务逻辑**:不落库、不判权限、不组装提示词。这些都通过 `LoopHooks`(扩展点的循环层子集)注入。

实现位置:`src/Ncw.Agent.Kernel`(命名空间 `Ncw.Agent.Kernel.Loop` / `.Tools` / `.Extensions`),只依赖模型层 `Ncw.Agent.Llm`;
上游(`ILlmClient`)、时钟(`TimeProvider`)、ID(`IIdGenerator`)、退避睡眠(`IDelayer`)全部构造注入。已按本文逐条实现并用假上游 + 假工具单独验收
(`tests/Ncw.Agent.Kernel.Tests`,§9 的表把每条不变量对到测试)。与本文最初草稿不同的地方在各节用「实现注」标出,汇总在 §10。

## 1. 输入与钩子

```csharp
public sealed record LoopInput(
    RunId RunId, string SessionId, string WorkspaceId,
    IReadOnlyList<Message> Context,          // 已经过 messagesForModel 的上下文(从最后一个压缩边界开始)
    IReadOnlyList<Message> NewMessages,      // 本次 Run 的输入(可以为空,表示「继续」);id 由调用方提供
    ModelRef Model, GenerationOptions Options,
    EnvironmentTag? Environment = null,      // 项目的执行环境标识(设备 + 环境):tool_start 和 ToolContext 都带着它
    int Depth = 0);                          // 子代理深度

public sealed class AgentLoop(ILlmClient llm, IIdGenerator ids, TimeProvider clock, IDelayer delayer,
                              AgentLoopOptions? options = null, ILoopDiagnostics? diagnostics = null)
{
    public IAsyncEnumerable<AgentEvent> RunAsync(LoopInput input, LoopHooks hooks, CancellationToken ct = default);
}

public sealed class LoopHooks
{
    // 每个回合开始前:压缩、刷新工具快照、装配请求。返回本回合的请求 + 工具快照(或要求结束)。**唯一必填**
    public required Func<TurnContext, CancellationToken, ValueTask<TurnPlan>> PrepareTurn { get; init; }
    // 发出前最后一次改写请求(before_provider_request,最后者胜,仅一方扩展)
    public Func<LlmRequest, CancellationToken, ValueTask<LlmRequest>>? BeforeProviderRequest { get; init; }
    // 工具预检:参数校验之后、执行之前。可以放行、拒绝(带原因)、改写参数。权限扩展挂在这里
    public Func<ToolInvocation, CancellationToken, ValueTask<PreflightDecision>> BeforeTool { get; init; }
    // 工具执行之后:只能追加内容、翻转 IsError(2.x PostToolUse 规则),不能改写输出。Loop 会校验返回值
    public Func<ToolInvocation, ToolResult, CancellationToken, ValueTask<ToolResult>> AfterTool { get; init; }
    // 模型不再调用工具、本来要停的时候:可以要求继续(带一条内部消息)
    public Func<TurnEnd, CancellationToken, ValueTask<TurnEndDecision>> OnTurnEnd { get; init; }
    // 回合边界取引导消息(取走即消费)
    public Func<IReadOnlyList<Message>> TakeSteering { get; init; }
    // 每条消息完成时(助手消息、工具结果、引导消息、内部消息、用户输入):会话层在这里落库
    public Func<Message, CancellationToken, ValueTask> CommitMessage { get; init; }
    // 上游报 ContextLength 且本回合没有内容字节时:让会话层压缩一次,返回 true = 丢弃本回合重来(被动压缩,每回合最多一次)
    public Func<TurnContext, CancellationToken, ValueTask<bool>>? RecoverContextLength { get; init; }
    // 把基础 ToolContext 换成会话层的派生类型(带执行环境句柄、权限档位快照、SpawnSubagent、Interact 等能力)
    public Func<ToolContext, ToolContext>? EnrichToolContext { get; init; }
}

public sealed record TurnPlan   // 工厂:TurnPlan.Run(request, tools, events) / TurnPlan.Stop(status, error, events)
// Run:请求里的工具列表**总以 tools 快照为准**(Loop 覆盖),同一个快照既告诉模型有哪些工具,也用来按名字找回工具(2.x ★)
// events:请求发出前要写进事件流的会话层事件(context_usage、context_status)
public abstract record PreflightDecision   // Allow | Deny(reason) | Edit(input)
public sealed record TurnEndDecision(TurnEndAction Action, Message? InternalMessage, LoopNotice? Notice)  // Stop | Finish | Continue
```

**实现注**:

- 原稿所有钩子都是 `required`;实现里只有 `PrepareTurn` 必填,其余缺省为空操作(放行 / 原样返回 / 正常结束 / 无引导 / 不落库),单测和小场景不必写样板。
  会话层必须自己提供 `CommitMessage`——不提供就等于不落库。
- **会话层需要的两点(M3 加进循环层)**:① `LoopHooks.RecoverContextLength`:`StreamError(ContextLength)` 且 `StreamAssembler` 里没有任何内容块时才调(已经有内容的拒绝不是「提示词过长」那类),
  钩子抛异常按「没处理」;返回 true 时已发过 start 的助手消息补 `message_discard`,回合以 `turn_end` 收尾,不提交错误,再走一遍 `PrepareTurn`;每个回合最多一次(一个回合正常结束后复位),
  压完还被拒就如实报错。② `TurnContext.LastUsage` / `LastUsageAnchor`:本 Run 里上一次**完成**的请求(收到 `MessageEnd`)回报的真实用量,和发出那次请求时转录的最后一条消息的 id
  (2.x 的 `usageAnchor`:压缩判据读「真值 + 其后新增消息的估算」);本 Run 还没有完成过请求时为 null。
- `LoopInput` 增加了 `SessionId`、`WorkspaceId`(进 `RequestContext`)、`Environment`、`Depth`。`RunId` 是 `Ncw.Agent.Kernel` 里的 `readonly record struct`。
- `OnTurnEnd` 的返回值是 `TurnEndDecision`:`Stop`(正常结束,但此刻队列里若又来了引导消息仍会继续)、`Finish`(立即结束,不再看引导消息)、
  `Continue(内部消息)`(再跑一个回合;没有非空的内部消息就退化成 `Stop`,否则会原样重发同一个请求)。可附带一条 `LoopNotice`(转成 `notification` 事件)。
  `TurnEnd` 里带着 2.x 的 `toolCallsThisRun` / `stoppedTurnStreak` / `stopHookActive`(目标刹车和 Stop Hook 防循环要用)。
- 钩子的异常语义:`PrepareTurn`、`TakeSteering`、`CommitMessage` 抛异常 → Run 以 `Error`(`MessageKey = loop.internal`)结束,不让异常逃出事件流;
  `BeforeTool` 抛异常 → **按拒绝处理**(失败即关闭);`AfterTool`、`OnTurnEnd` 抛异常 → 吞掉并记诊断(后者否则会无限重试,2.x ★);
  取消异常(令牌已触发)一律原样传播。`CommitMessage` 总是收到 `CancellationToken.None`:落库不可中断(中止收尾也要落库)。

## 2. 回合循环

```
RunAsync:
  emit run_start
  CloseOrphans(RecoveredInterrupted)                 // 上个 Run 崩溃留下的孤儿,必须在新用户消息之前补
  commit NewMessages(用户输入,id 由客户端提供;空消息跳过)
  loop:
    plan = PrepareTurn(...)                         // 压缩、工具快照、装配请求;plan.Events 先写进事件流
    if plan.Stop → break(status / error 由 plan 给)
    emit turn_start
    流式调用 Llm(plan.Request,工具列表以 plan.Tools 为准),转发 message_* 事件
    if 流中断(Network 错误,含「没有 MessageEnd 就结束」)且续传次数没用完 →
        丢弃已累积内容(message_discard)、emit provider_retry、按 [1s,2s,5s,15s,45s] 睡、重来   // 本回合还没执行工具,天然满足
    msg = 折成助手消息;非空才 CommitMessage          // 空消息不提交;出错时消息带 ErrorPart、没收尾的工具调用块丢弃
    if 出错 → CloseOrphans(StreamFailed); emit turn_end; finish(error); break
    calls = msg 里的工具调用
    if stopReason == MaxTokens && calls 非空 → 每个调用都返回错误「参数可能被截断,请重新发出」,不执行,提交一条 tool_result 消息
    elif calls 非空 → ExecuteTools(calls)(结果在收尾里提交一条 tool_result 消息,按调用顺序)
         if 所有结果都 Terminate(且都不是错误) → emit turn_end; finish(done); break
    else
         if stopReason == MaxTokens → 错误 OutputLimit(已在上一步折进消息的 ErrorPart)
         steer = TakeSteering()                      // 已经有引导消息 = 有活要干,不问判定器
         if steer 非空 → emit turn_end; 逐条 CommitMessage(steer); continue
         d = OnTurnEnd(...)(只在 end_turn 时问;异常吞掉)
         if d.Continue(非空内部消息) → CommitMessage(内部消息, Internal=true); emit turn_end; continue
         if d 不是 Finish → steer = TakeSteering()    // 判定期间可能又来了
              if steer 非空 → emit turn_end; 逐条 CommitMessage(steer); continue
         emit turn_end; finish(done); break
    emit turn_end
    steer = TakeSteering()                           // 工具结果落库之后、下一次请求之前
    逐条 CommitMessage(steer);continue               // 有工具调用的回合不管有没有 steer 都继续
  emit run_end
```

**实现注**:`turn_end` 对每个已经 `turn_start` 的回合都会发(出错、Terminate、中止也一样),`turn_start` / `turn_end` 严格成对;原稿的伪代码在 Terminate 路径上漏了它。
取引导消息的位置和顺序以 2.x 为准(原稿只在回合末取一次):没有工具调用时先取一次再问判定器,判定之后再取一次。
中止、异常都不会跳过收尾:`FinalizeAbort` 提交半截回复、补「已中止」结果,然后才发 `run_end`。

要点:

- **没有回合上限**(pi 和 2.x 一致)。停止条件只有:中止、错误、模型不再调工具且 `OnTurnEnd` 不要求继续、工具要求 `Terminate`。
- **重试只在模型层**。循环层唯一的「重试」是流中断续传,并且只在本回合尚未执行任何工具时进行(否则会重放工具调用)。
- **继续用循环,不用递归**(2.x 的目标模式长时间运行曾因递归爆栈)。
- `OnTurnEnd` 里的异常被吞掉并记日志,否则循环会无限重试(2.x ★)。
- 流中断续传只认 `AgentErrorCode.Network`,刻意不放宽到全部可重试:限流、5xx、冷却中的供应商走模型层那条信息更全的路(认 `Retry-After`、会换供应商),
  再套一个盲循环最坏是 5 × 3 次尝试的长时间空转(2.x 的决定,理由逐字保留在 `RunExecution.Streaming.cs`)。断流那次已生成的内容整个丢弃,不留半截回答。
- 没有工具调用的回合撞上输出上限(`MaxTokens`)仍是错误(`MessageKey = loop.outputLimit`,2.x 的 `agent.error.outputLimit`),带工具调用的才按 pi 规则「判失败并提示重发」。

## 3. 事件流

事件是一个 `Channel<AgentEvent>`,由循环层和会话层写入,编排层消费(写事件日志、推送给客户端)。
`AgentLoop.RunAsync` 返回 `IAsyncEnumerable<AgentEvent>`(内部是 `Channel` + 生产者任务,和模型层同一个模式);消费者提前放弃枚举等于中止。
`AgentEvent` 是开放层级(非 sealed),会话层和编排层往同一条流里写自己的事件。

循环层保证(`EventRules` 在每个测试里自动检查):第一个事件是 `run_start`、最后一个是 `run_end`,各恰好一个;`turn_start` / `turn_end` 严格成对嵌套;
每个 `message_start` 恰好一个 `message_end` 或 `message_discard`;`message_end` 的消息永远非空;每个 `tool_start` 恰好一个 `tool_end`。

| 事件 | 载荷 | 说明 |
|---|---|---|
| `run_start` | runId、sessionId、model | |
| `turn_start` | turnIndex | |
| `message_start` | messageId、role、model、providerId | 每条消息都有 start / end,包括用户、引导、工具结果消息。助手消息在**第一个内容事件到来时**才发(只有 `MessageStart` 没有内容的回合不产生任何消息事件) |
| `message_delta` | messageId、contentIndex、kind(text / thinking / tool_args)、delta、callId?、toolName? | **只推增量**(R7);按 `contentIndex` 归位,块可以交错。`tool_args` 的第一条增量为空串并带 `toolName`,让界面在参数还在流式输出时就能画工具卡片 |
| `message_end` | 完整 `Message`、usage、cost、stopReason | **唯一的落库点**;循环层**先**调 `CommitMessage`、成功后才发它;客户端用它替换累积的增量 |
| `message_discard` | messageId | **新增**:已发过 `message_start` 的助手消息最后没有提交(流中断续传时丢弃那一份、或者什么内容都没有):客户端丢掉对应的增量。保证每个 `message_start` 都以 `message_end` 或 `message_discard` 收尾 |
| `provider_retry` / `provider_switch` | attempt、delayMs、reason / from、to | 来自模型层;循环层的断流续传复用 `provider_retry` |
| `tool_start` | callId、toolName(外部名)、input、environment | 带执行环境标识(见 [06 执行环境](../06-environments-ssh.md))。**助手消息里的每个调用都有**(包括 JSON 无效、工具不存在、被拒绝、被中止的),预检时按调用顺序发 |
| `tool_progress` | callId、message、fraction、details | **易失**,不写进记录;`tool_end` 之后日志里清掉(子代理派发的排位 `{"kind":"queued"}` 除外,见 05 §2);由工具线程经 `RunRegistry.Post` 直接投的进度,`RunHandle` 保证排在这次调用的 `tool_start` 之后 |
| `tool_end` | callId、output(Content / Details / 截断标记)、IsError、durationMs | 失败也必须发,否则界面卡片一直转;每个 `tool_start` 恰好一个。并行执行时按**完成顺序**发 |
| `interaction_request` / `interaction_resolved` | 见 [03 §4](03-tools-permissions.md#4-交互) | |
| `subagent_start` / `subagent_update` / `subagent_end` | 见 [05 §5](05-orchestration.md#5-子代理) | |
| `context_usage` | used、window、shouldCompact、segments | 每次请求前发 |
| `context_status` | compacting / compacted / failed / disabled | |
| `queue_update` | 引导队列和排队消息 | 来自 pi |
| `notification` | key、detail | 结构化通知(`OnTurnEnd` 的 `LoopNotice`),不带界面文字,控制端按 key 翻译 |
| `turn_end` | turnIndex | |
| `run_end` | status、error | 终态;此时**可能还没落库完** |
| `run_settled` | status | 落库、释放会话锁之后才发(R8)。下一个 Run 等它 |

**序号**:编排层给每个事件分配严格递增的 `seq`(每个 Run 一套)。日志会裁剪(`message_end` 之后清掉该消息的增量,
`tool_end` 之后清掉该调用的进度),所以续传时序号可能有空洞,客户端不做空洞检查,直接以快照的 `seq` 为准(2.x ★)。

## 4. 工具执行

```
ExecuteTools(calls):
  // 1. 顺序预检(R4)
  foreach call in calls:
      emit tool_start
      if JSON 无效 → 结果 = 错误(回显原始参数)
      elif 工具不存在 → 结果 = 错误(列出可用工具名)
      else
          args = tool.PrepareArguments(input)        // 校验 + 宽松转换(如 "5" → 5);失败 → 结果 = 错误(给模型看的说明)
          d = BeforeTool(call)                       // 权限扩展:可能发起审批交互,等待回答
          deny → 结果 = 可见的失败;edit → 用改写后的参数(重新过一遍 PrepareArguments)
  // 2. 执行
  if 批内任何工具 ConcurrencySafe == false → 顺序执行,遇到中止立即停
  else → 并行执行;同一个真实路径的文件修改通过 FileMutationQueue 串行(pi)
  // 3. 收尾
  每个结果:超过 64KB 且工具没自己截断 → 截断并标记;AfterTool;emit tool_end
  结果按调用顺序写回一条 tool_result 消息,在 finally 里提交(中止前已完成的结果也要落库,2.x ★)
```

- **中止异常永远原样传播**,不包装成工具失败(R15)。否则模型会以为工具出错了,继续往下做。工具自己内部超时抛的 `OperationCanceledException`(取消令牌**没**触发)
  不是中止,按工具失败处理。等待工具、审批钩子、`AfterTool` 都是「可中止的等待」:中止时立即返回,不等一个不认取消令牌的实现(被丢下的任务的异常会被观察掉)。
- 尚未开始的工具在中止时得到「操作已中止」结果;中止前已完成的工具保留真实结果。中止收尾在**同一条** tool_result 消息里按调用顺序写下所有结果,
  然后才把取消异常抛给上层(2.x 是先提交已完成的、再由 `finalizeAbort` 另补一条)。
- 并行批里任何一个调用的**非中止**异常(工具抛的)只影响它自己:收敛成工具错误,同批其他调用照常完成。
- 批内是否顺序执行只看**通过预检的**调用(被拒绝的调用不会让整批变慢)。
- **所有**结果都 `Terminate` 且都不是错误才结束 Run(pi 规则,03 §1);2.x 是任一个 `stopRun` 就停,见 [parity](../../parity.md)。
- 超过 64 KiB **个字符**(沿用 2.x 的口径)且工具没自己截断才由 Loop 截断,`OriginalBytes` 记原文字符数;提示文字是发给模型的英文。截断不切开代理对。
- `AfterTool` 的返回值由 Loop 校验:只接受「原内容 + 追加的块」和 `IsError` false → true;改写已有内容、改 `Details` / `Terminate`、把错误翻成成功一律忽略。
- 工具拿到的 `ToolContext`:`RunId`、`CallId`、`SessionId`、`Depth`、`Environment`(标识)、`IProgress<ToolProgress>`(结束后迟到的进度被丢弃)、`FileMutationQueue`。
  `ToolContext` 不是 sealed,会话层派生出带执行环境句柄等能力的子类型,经 `LoopHooks.EnrichToolContext` 换上。
- **`FileMutationQueue`**:调用方先把路径解析成真实路径再当 key,同 key 的读-改-写串行(FIFO),不同 key 互不阻塞,空闲 key 立即回收;可注入比较器(大小写不敏感的文件系统)。
- 预检是**顺序**的(与 pi 一致,Q17):前一个调用的审批回答之前,后面的调用不会发起审批。手机和桌面上审批卡片都按调用顺序一张一张出现。
- 某个调用被拒绝,不影响同批其他调用继续预检和执行。

## 5. 引导消息与排队消息

沿用 2.x 的语义,用 pi 的命名:

| 类型 | 来源 | 何时生效 |
|---|---|---|
| **引导消息(steer)** | 用户在 Run 进行中「插话」(2.x 的 promoted 队列项)、内部消息(后台子代理的报告、目标检查) | 当前回合的工具结果落库之后、下一次请求之前。不打断正在执行的工具批 |
| **排队消息(follow-up)** | 用户在 Run 进行中发送、但不要求插话(2.x 的 pending 队列项) | 当前 Run **以 `done` 结束并 settle 之后**,作为一个新的 Run 开始。中止或出错的 Run 不自动续跑;冷启动也不自动跑 |

规则(2.x ★):

- 取引导消息是「取走即消费」,防止重复注入。
- 每条引导消息作为独立的用户消息提交,**消息 id 等于队列项 id**。看到同 id 的 `message_end` 才算送达,不是「放进邮箱」就算。
- 插入位置必须在工具结果之后,否则会把工具调用和结果拆开。
- 已消费的输入有持久化回执(`session_input_receipts`),保证不会被重放。
- 空内容的条目直接跳过(提交空消息,下一次请求会被上游拒掉)。

**实现注**:循环层只管「取」:`LoopHooks.TakeSteering`。两种队列本身是 `RunInputQueues`(同在 `Ncw.Agent.Kernel.Loop`,线程安全,可直接接 `TakeSteering`):

- 排队项先是 `Pending`;`TogglePromote(id)`(2.x「插话」是 toggle:pending ⇄ promoted)变成 `Promoted`,按引入顺序排(取消再引入排到已引入者的队尾;编辑不改顺序);上限 20 条。
- `TakeSteering()` 取走全部 `Promoted`(按引入顺序)和内部引导消息(后台报告、目标检查;不会被用户侧的整份替换抹掉),**pending 一条都不捎带**,否则任何人排队都会打断当前执行。
- `TakeFollowUps(RunStatus)`:只有 Run 以 `Done` 结束才交出(promoted 在前、pending 按入队顺序),并移出队列;`Aborted` / `Error` 什么都不交出,条目原样留着。
  多条合并成一个 Run 的输入(2.x 的 `mergeBatch`)、`session_input_receipts` 回执、冷启动不自动续跑是编排层 / 会话层的事,不在这里。
- `Snapshot()` 带单调递增的 `Rev`,是 `queue_update` 的载荷;`Close()` 之后拒绝内部引导消息。
- **编排层追加**(M3,`Ncw.Agent.Runs` 要用):`Restore(entries)` 整体替换用户侧条目并保留状态与引入顺序(冷启动从存档恢复、持久化失败时回滚内存、续跑移交 / 放回),`TakeInternal()` 只取走内部引导消息(Run 收尾时用,免得下一个 Run 顺手取走重复投递)。
  编排层的续跑**不用** `TakeFollowUps`:2.x 的 `pickNextBatch` 是「有 promoted 就只发 promoted,否则 FIFO 取一条」,不是「全部交出」,见 [05 §3 实现注](05-orchestration.md#3-排队消息会话运行时)。`Snapshot().Rev` 是每个队列实例自己的计数,会话级的 `queue_update` 用编排层的全局 `Rev`。

## 6. 中止

一次中止要完成五件事(2.x 规格 §4.8):

1. 取消上游请求(同一个 `CancellationToken`)。
2. 拒绝挂起的交互(交互闸门监听同一个 token)。
3. 取消工具(`ToolContext` 里的 token)。
4. 为没有结果的工具调用补上「已中止」的结果。**漏掉会让下一次请求 400**。
5. 级联中止子 Run。

中止只发信号,不直接改 Run 状态。状态由循环在收尾时写成 `aborted`。编排层另有 5 秒看门狗(见 [05 §1](05-orchestration.md#1-run-生命周期))。

**实现注**:循环层负责 1、3、4(2 和 5 是交互闸门和编排层挂在同一个令牌上的)。取消令牌触发后:流式等待、工具等待、审批等待都立即返回;
半截回复提交(标 `Aborted`,回放时整条跳过;没收尾的工具调用块丢弃,所以不制造孤儿);仍然没有结果的调用补「已中止」;发 `turn_end`、`run_end(aborted)`。
上游报 `StreamError(Aborted)` 而令牌没触发时同样按中止处理。

## 7. 孤儿工具调用修复

「每个工具调用都必须紧跟它的结果」是最重要的不变量。以下四处都要修复没有结果的调用:

| 时机 | 补的内容 | 在哪修 |
|---|---|---|
| 会话构造时(上一个 Run 崩溃留下的) | 「上次运行被中断」,**在新用户消息之前**补,不能让新消息插在调用和结果之间 | `RunAsync` 开头,提交 `NewMessages` 之前(会话层不必再做一遍;幂等) |
| 出错时 | 错误说明 | 循环 |
| 中止时 | 「已中止」 | 工具批收尾 + `FinalizeAbort` 兜底 |
| 流中断放弃时 | 错误说明 | 循环 |

`StreamAssembler` 对没收尾的工具调用的处理和 2.x 的 `BlockAccumulator.Finalize()` 不同:它**保留**调用块并标 `InputError`(这样正常结束的消息里,
参数 JSON 解析失败的调用仍然输出一个调用块(输入为 `{}`)和一个失败结果,保证成对,2.x ★)。循环在**出错 / 中止**的消息里把没收尾的块丢掉
(`PendingToolCalls`),和 2.x 一致;正常结束(`MessageEnd` 已到)的消息里的坏调用都得到失败结果。

孤儿检测 `Orphans.Find(messages)` 是纯函数:所有出现过但没有任何结果的调用,按出现顺序。补的结果是一条 tool_result 消息;
这个 Run 里提交的调用补 `tool_start` + `tool_end`,上个 Run 留下的只补 `tool_end`(卡片已经在历史里)。

## 8. 不变量清单(移植验收)

- [x] 每个工具调用都有紧邻的结果(四处修复 + 引导消息位置 + 坏 JSON 成对)
- [x] 永远不提交空的助手消息
- [x] 会话层不重试;流中断续传只在本回合未执行工具时
- [x] 中止异常不被包装
- [x] `tool_end` 一定发出
- [x] 工具结果在 `finally` 里提交
- [x] 引导消息取走即消费,送达以同 id 的 `message_end` 为准
- [x] 继续用循环不用递归;`OnTurnEnd` 的异常不导致无限重试

## 9. 不变量与测试对照

| # | 不变量 | 测试(`tests/Ncw.Agent.Kernel.Tests`) |
|---|---|---|
| L1 | 工具调用和结果成对(正常 / 坏 JSON / 未知工具 / 被拒绝) | `ToolTests.Bad_json_arguments…`、`An_unknown_tool_name…`、`A_denied_call…`;每个测试里 `Pairing.AssertPaired` |
| L2 | 孤儿修复·会话开始(在新用户消息之前,幂等) | `StreamFailureTests.Orphans_left_by_a_crashed_run…`、`Repair_is_idempotent…`、`Orphans_Find_…` |
| L3 | 孤儿修复·出错(已收尾的调用补结果,没收尾的丢弃) | `StreamFailureTests.Calls_stranded_by_a_stream_failure…`、`A_tool_call_cut_off_before_its_end…` |
| L4 | 孤儿修复·中止(含工具执行中、审批等待中、顺序批) | `AbortTests.Aborting_while_a_tool_runs…`、`Results_of_tools_that_finished…`、`Calls_that_had_not_started…`、`Aborting_while_waiting_for_an_approval…`、`Abandoning_the_enumeration_early…` |
| L5 | 孤儿修复·流中断放弃(半截调用不进记录、不执行) | `StreamFailureTests.A_resume_never_replays_tools…`、`AbortTests.An_unfinished_tool_call_in_the_half_reply…` |
| L6 | 永远不提交空的助手消息 | `LoopTests.An_empty_assistant_message…`、`…started_streaming_but_ends_up_empty…`;`EventRules` 检查每个 `message_end` 非空 |
| L7 | 重试只在模型层;循环只对 Network 断流续传、退避表 [1,2,5,15,45]s、用尽即报错、不重放工具 | `StreamFailureTests.A_network_break_mid_stream…`、`Resuming_uses_the_backoff_table…`、`Only_network_errors_are_resumed`、`The_tool_is_never_executed_twice…`、`With_an_empty_backoff_table…` |
| L8 | 中止异常不被包装;不认令牌的上游 / 工具 / 审批钩子不能拖住中止 | `AbortTests.Aborting_while_a_tool_runs…`、`A_stalled_upstream_that_ignores_cancellation…`、`An_approval_hook_that_ignores_the_token…`;`ToolTests.A_cancellation_that_the_tool_raised_on_its_own…` |
| L9 | `tool_end` 一定发出(失败、拒绝、中止、孤儿都有) | 每个测试里 `EventRules`;`ToolTests.A_tool_that_throws…`、`AbortTests.*` |
| L10 | 工具结果一条消息、按调用顺序、完成顺序无关;中止前完成的结果保留 | `ToolTests.Tools_in_one_batch_run_in_parallel…`、`AbortTests.Results_of_tools_that_finished…` |
| L11 | 引导消息:取走即消费、id 等于队列项 id、插在工具结果之后、不打断工具批、空消息跳过、多条按序 | `SteeringTests.*`、`QueueTests.*` |
| L12 | `OnTurnEnd` 异常被吞、不无限重试;Continue 带内部消息;Finish 不看引导;连续空转计数 | `SteeringTests.OnTurnEnd_throwing_is_swallowed…`、`OnTurnEnd_can_ask_to_continue…`、`The_streak_resets…`、`OnTurnEnd_Finish…` |
| L13 | 继续用循环不用递归(1500 回合) | `LoopTests.There_is_no_turn_limit…` |
| L14 | 顺序预检、并行执行、`ConcurrencySafe=false` 顺序、同一文件串行 | `ToolTests.Preflight_is_sequential…`、`A_batch_with_a_non_concurrency_safe_tool…`、`Parallel_calls_that_mutate_the_same_file…`;`ToolProtocolTests.*` |
| L15 | `stopReason=MaxTokens`:带调用的全部判失败并提示重发、不执行;不带调用报 `loop.outputLimit` | `StreamFailureTests.Hitting_the_output_limit_*`、`The_output_limit_error_results…` |
| L16 | Terminate:全部 Terminate 才停;错误的不算;回合仍然关闭 | `ToolTests.A_single_Terminate_result…`、`Terminate_needs_every_result…`、`A_Terminate_result_that_is_an_error…` |
| L17 | 工具结果:Details 只给界面、64K 截断(保留工具自己的标记)、AfterTool 只能追加 | `ToolTests.Details_go_to_the_record…`、`Oversized_output…`、`A_tool_that_truncated_its_own_output…`、`AfterTool_*` |
| L18 | 事件流完整性:run / turn / message / tool 成对;先落库后 `message_end`;事件顺序 | `Rig.RunAsync` 里的 `EventRules.AssertWellFormed`(每个测试);`LoopTests.A_single_turn_streams…`、`ToolTests.All_tool_starts_precede…` |
| L19 | 钩子异常语义:权限(`BeforeTool`)失败即关闭;落库失败 Run 以错误结束;收尾失败不盖掉真实结局 | `ToolTests.A_throwing_permission_hook…`、`StreamFailureTests.A_failing_CommitMessage…`、`Cleanup_that_itself_fails…` |
| L20 | 扩展点组合语义(通知 / 就地修改 / 链式变换 / 闸门 / 最后者胜;异常不传播;权限失败即关闭;取消不吞) | `ExtensionTests.*` |
| L21 | 被动压缩钩子:上游 `ContextLength` 且无内容时问一次、重来;每回合最多一次;有内容 / 钩子拒绝 / 钩子抛异常照常报错 | `StreamFailureTests.A_context_length_rejection_with_no_content_…`、`The_hook_is_asked_at_most_once_…`、`A_context_length_rejection_after_content_started_…`、`A_hook_that_declines_or_throws_…` |
| L22 | `TurnContext` 带上一次完成请求的真实用量和锚点 | `LoopTests.PrepareTurn_sees_the_last_real_usage_…` |

## 10. 相对原稿的实现偏离(汇总)

1. 只有 `PrepareTurn` 必填;其余钩子有空操作缺省(§1)。
2. `OnTurnEnd` 返回 `TurnEndDecision`(Stop / Finish / Continue + 通知);`TurnEnd` 带 2.x 的三个计数(§1)。
3. 新增事件 `message_discard`;助手消息的 `message_start` 延迟到第一个内容事件;`tool_start` 对每个调用都发(2.x 对 JSON 无效 / 未知工具只发 `tool_end`)(§3)。
4. `turn_end` 总是发(含 Terminate、出错、中止)(§2)。
5. 引导消息在没有工具调用的回合先取一次再问 `OnTurnEnd`,之后再取一次(2.x 顺序;原稿只取一次)(§2)。
6. 孤儿修复(会话开始)放在 `RunAsync` 开头,会话层不必重复做(§7)。
7. 参数校验 + 宽松转换由工具的 `PrepareArguments` 做(Loop 只负责在预检里调用它、把失败变成工具错误);JSON schema 校验器属于 Tools 层(§4)。
8. 所有结果都 Terminate 才停(pi / 03 §1);2.x 任一个 `stopRun` 就停(§4)。
9. 中止收尾把已完成的真实结果和「已中止」占位写在同一条 tool_result 消息里(2.x 分两条)(§4)。
10. `RunInputQueues` 把 2.x 渲染层 / 主进程两处的队列规则收成一个类(§5)。
11. 会话层要用的两点加进了循环层:`LoopHooks.RecoverContextLength`(被动压缩)和 `TurnContext.LastUsage / LastUsageAnchor`(上游真值锚点)(§1 实现注)。
