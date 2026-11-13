# 08.02 · Agent 循环(`Ncw.Agent.Kernel.Loop`)

循环层对应 pi 的 `agentLoop`,行为对应 2.x `agent-session.ts` 的 `loop / turn / executeAll / executeOne`。
它**不含业务逻辑**:不落库、不判权限、不组装提示词。这些都通过 `LoopHooks`(扩展点的循环层子集)注入。

## 1. 输入与钩子

```csharp
public sealed record LoopInput(
    RunId RunId,
    IReadOnlyList<Message> Context,          // 已经过 messagesForModel 的上下文(从最后一个压缩边界开始)
    IReadOnlyList<Message> NewMessages,      // 本次 Run 的输入(可以为空,表示「继续」)
    ModelRef Model, GenerationOptions Options);

public sealed class LoopHooks
{
    // 每个回合开始前:压缩、刷新工具快照、装配请求。返回本回合的请求(或要求结束)
    public required Func<TurnContext, CancellationToken, ValueTask<TurnPlan>> PrepareTurn;
    // 工具预检:参数校验之后、执行之前。可以放行、拒绝(带原因)、改写参数。权限扩展挂在这里
    public required Func<ToolInvocation, CancellationToken, ValueTask<PreflightDecision>> BeforeTool;
    // 工具执行之后:只能追加反馈、翻转 IsError(2.x PostToolUse 规则),不能改写输出
    public required Func<ToolInvocation, ToolResult, CancellationToken, ValueTask<ToolResult>> AfterTool;
    // 模型不再调用工具、本来要停的时候:可以要求继续(带一条内部消息)
    public required Func<TurnEnd, CancellationToken, ValueTask<TurnEndDecision>> OnTurnEnd;
    // 回合边界取引导消息(取走即消费)
    public required Func<IReadOnlyList<Message>> TakeSteering;
    // 每条消息完成时(助手消息、工具结果、引导消息、内部消息):会话层在这里落库
    public required Func<Message, CancellationToken, ValueTask> CommitMessage;
}
```

## 2. 回合循环

```
RunAsync:
  emit run_start
  commit NewMessages(用户输入,id 由客户端提供)
  loop:
    plan = PrepareTurn(...)                         // 压缩、工具快照、装配请求
    if plan.Stop → break
    emit turn_start
    stream = Llm.StreamAsync(plan.Request)         // 路由层负责首字节前的重试和换供应商
    累积成块(BlockAccumulator);转发 message_* 事件
    if 流中断(network)且本回合还没执行工具 → 丢弃已累积内容,按 [1s,2s,5s,15s,45s] 续传
    msg = acc.Finalize();  CommitMessage(msg)       // 空消息不提交
    if 出错 → CloseOrphans; emit turn_end; finish(error); break
    calls = msg 里的工具调用
    if stopReason == length && calls 非空 → 每个调用都返回错误「参数可能被截断,请重新发出」
    else if calls 非空 → results = ExecuteTools(calls); CommitMessage(results)   // 一条 tool_result 消息,按调用顺序
         if 所有结果都 Terminate → finish(done); break
    else
         d = OnTurnEnd(...)
         if d.Continue → CommitMessage(d.InternalMessage); emit turn_end; continue
    emit turn_end
    steer = TakeSteering()                           // 工具结果落库之后、下一次请求之前
    if steer 非空 → 逐条 CommitMessage; continue
    if calls 为空 → finish(done); break
  emit run_end
```

要点:

- **没有回合上限**(pi 和 2.x 一致)。停止条件只有:中止、错误、模型不再调工具且 `OnTurnEnd` 不要求继续、工具要求 `Terminate`。
- **重试只在模型层**。循环层唯一的「重试」是流中断续传,并且只在本回合尚未执行任何工具时进行(否则会重放工具调用)。
- **继续用循环,不用递归**(2.x 的目标模式长时间运行曾因递归爆栈)。
- `OnTurnEnd` 里的异常被吞掉并记日志,否则循环会无限重试(2.x ★)。

## 3. 事件流

事件是一个 `Channel<AgentEvent>`,由循环层和会话层写入,编排层消费(写事件日志、推送给客户端)。

| 事件 | 载荷 | 说明 |
|---|---|---|
| `run_start` | runId、sessionId、model | |
| `turn_start` | turnIndex | |
| `message_start` | messageId、role、model、providerId | 每条消息都有 start / end,包括用户、引导、工具结果消息 |
| `message_delta` | messageId、contentIndex、kind(text / thinking / tool_args)、delta | **只推增量**(R7);按 `contentIndex` 归位,块可以交错 |
| `message_end` | 完整 `Message`、usage、cost、stopReason | **唯一的落库点**;客户端用它替换累积的增量 |
| `provider_retry` / `provider_switch` | attempt、delayMs、reason / from、to | 来自模型层 |
| `tool_start` | callId、toolName、input、environment | 带执行环境标识(见 [06 执行环境](../06-environments-ssh.md)) |
| `tool_progress` | callId、message、fraction、details | **易失**,不写进记录;`tool_end` 之后日志里清掉 |
| `tool_end` | callId、Content、Details、IsError、durationMs | 失败也必须发,否则界面卡片一直转 |
| `interaction_request` / `interaction_resolved` | 见 [03 §4](03-tools-permissions.md#4-交互) | |
| `subagent_start` / `subagent_update` / `subagent_end` | 见 [05 §5](05-orchestration.md#5-子代理) | |
| `context_usage` | used、window、shouldCompact、segments | 每次请求前发 |
| `context_status` | compacting / compacted / failed / disabled | |
| `queue_update` | 引导队列和排队消息 | 来自 pi |
| `notification` | warning | |
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
          args = 校验 + 宽松转换(如 "5" → 5)
          d = BeforeTool(call)                       // 权限扩展:可能发起审批交互,等待回答
          deny → 结果 = 可见的失败;edit → 用改写后的参数
  // 2. 执行
  if 批内任何工具 ConcurrencySafe == false → 顺序执行,遇到中止立即停
  else → 并行执行;同一个真实路径的文件修改通过 FileMutationQueue 串行(pi)
  // 3. 收尾
  每个结果:超过 64KB 且工具没自己截断 → 截断并标记;AfterTool;emit tool_end
  结果按调用顺序写回一条 tool_result 消息,在 finally 里提交(中止前已完成的结果也要落库,2.x ★)
```

- **中止异常永远原样传播**,不包装成工具失败(R15)。否则模型会以为工具出错了,继续往下做。
- 尚未开始的工具在中止时得到「操作已中止」结果。
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

## 6. 中止

一次中止要完成五件事(2.x 规格 §4.8):

1. 取消上游请求(同一个 `CancellationToken`)。
2. 拒绝挂起的交互(交互闸门监听同一个 token)。
3. 取消工具(`ToolContext` 里的 token)。
4. 为没有结果的工具调用补上「已中止」的结果。**漏掉会让下一次请求 400**。
5. 级联中止子 Run。

中止只发信号,不直接改 Run 状态。状态由循环在收尾时写成 `aborted`。编排层另有 5 秒看门狗(见 [05 §1](05-orchestration.md#1-run-生命周期))。

## 7. 孤儿工具调用修复

「每个工具调用都必须紧跟它的结果」是最重要的不变量。以下四处都要修复没有结果的调用:

| 时机 | 补的内容 |
|---|---|
| 会话构造时(上一个 Run 崩溃留下的) | 「上次运行被中断」,**在新用户消息之前**补,不能让新消息插在调用和结果之间 |
| 出错时 | 错误说明 |
| 中止时 | 「已中止」 |
| 流中断放弃时 | 错误说明 |

`BlockAccumulator.Finalize()` 丢弃没收尾的工具调用;参数 JSON 解析失败的调用仍然输出一个调用块(输入为 `{}`)和一个失败结果,
保证成对(2.x ★)。

## 8. 不变量清单(移植验收)

- [ ] 每个工具调用都有紧邻的结果(四处修复 + 引导消息位置 + 坏 JSON 成对)
- [ ] 永远不提交空的助手消息
- [ ] 会话层不重试;流中断续传只在本回合未执行工具时
- [ ] 中止异常不被包装
- [ ] `tool_end` 一定发出
- [ ] 工具结果在 `finally` 里提交
- [ ] 引导消息取走即消费,送达以同 id 的 `message_end` 为准
- [ ] 继续用循环不用递归;`OnTurnEnd` 的异常不导致无限重试
