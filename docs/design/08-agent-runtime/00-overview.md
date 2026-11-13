# 08.00 · 总览

## 1. 设计原则

1. **小内核,功能在外层**(取自 pi)。最内层的 `AgentLoop` 只做一件事:调模型 → 执行工具 → 再调模型,直到停下。
   它不知道会话怎么存、权限怎么判、子代理怎么排队。这些都通过扩展点挂上去。
2. **行为以 2.x 为准**。2.x 的 ★ 不变量(工具调用和结果必须成对、只在首字节前重试、子代理不能提问……)全部保留,
   并在对应文档的「不变量」小节逐条列出,作为移植验收项。
3. **内置功能也走扩展点**。权限、计划模式、待办、目标、子代理、Hooks、Skills 都是「一方扩展」,
   和插件(Jaunt)用的是同一套扩展接口。区别只是:一方扩展默认启用,权限扩展不可关闭。
4. **一切可注入、可回放**。时钟、ID 生成、模型上游、执行环境都通过接口注入。
   同一份录制输入,在 C# 和 2.x 上回放必须得到相同的事件序列(见 [01 架构 §4](../01-architecture.md#4-agent-移植策略))。
5. **多端优先**。所有状态都能通过协议查询(不能只是一个悬挂的 `Task`);交互先到先得;断线后可以按游标续传。

## 2. 分层

```
┌───────────────────────────────────────────────────────────────────────────────┐
│ Ncw.Agent.Host        协议适配:RPC 方法、事件推送、快照、activeRuns 广播           │
├───────────────────────────────────────────────────────────────────────────────┤
│ 编排层 Runs           RunRegistry、Run 生命周期、会话互斥、事件日志、子代理队列、      │
│                       排队消息、目标、定时任务、后台 Shell                           │
├───────────────────────────────────────────────────────────────────────────────┤
│ 会话层 Session        AgentSession:装配扩展、上下文组装、压缩、落库、孤儿调用修复       │
│   ├ 一方扩展          权限 / 计划模式 / 待办 / 目标 / 子代理 / Hooks / Skills / 插件   │
├───────────────────────────────────────────────────────────────────────────────┤
│ 循环层 Loop           AgentLoop:回合循环、工具执行、引导消息、中止 —— 无业务逻辑        │
├───────────────────────────────────────────────────────────────────────────────┤
│ 模型层 Llm            统一消息模型、流式事件、协议编解码、路由、重试、认证、计价          │
├───────────────────────────────────────────────────────────────────────────────┤
│ 基础 Ncw.Agent.Environments(执行环境)、Ncw.Agent.Storage(SQLite)、IClock 等    │
└───────────────────────────────────────────────────────────────────────────────┘
```

| 层 | 项目 / 命名空间 | 对应 pi | 对应 2.x |
|---|---|---|---|
| 模型层 | `Ncw.Agent.Llm`(原规划的 `Ncw.Agent.Providers` 改名) | `pi-ai` | `kernel/upstream/*`、`kernel/oauth/*` |
| 循环层 | `Ncw.Agent.Kernel.Loop` | `pi-agent-core` 的 `agentLoop` | `agent-session.ts` 的 `loop/turn/executeAll` |
| 会话层 | `Ncw.Agent.Kernel.Session` | `pi-coding-agent` 的 `AgentSession` | `agent-session.ts` 其余部分、`context-assembler.ts`、`compaction/*` |
| 编排层 | `Ncw.Agent.Kernel.Runs` | (pi 没有) | `run-registry.ts`、`services/agent.ts`、`session-runtime.ts`、`subagent-queue.ts`、`goal/*` |
| 扩展 | `Ncw.Agent.Extensions` | pi 的 Extension API | `approval.ts`、`hooks.ts`、`plan-run.ts`、`todo-reconciliation.ts` 等 |

**依赖规则**:下层不引用上层。`Loop` 只依赖 `Llm` 和工具接口;`Session` 不知道协议;`Host` 是唯一知道 `Ncw.Protocol` 的层。

## 3. 从 pi 取什么、不取什么

| pi 的设计 | 3.0 | 说明 |
|---|---|---|
| 三层事件:agent / turn / message,工具执行 start / update / end | ✅ 采用 | 事件命名见 [02 §3](02-loop.md#3-事件流) |
| 每条助手消息记录来源 `api / provider / model`,换模型时丢弃思考内容 | ✅ 采用 | 2.x 只按协议判断透传数据,不够精确 |
| 工具结果分为「给模型的内容」和「只给界面的 details」 | ✅ 采用 | 2.x 的 `card` 等字段归入 details |
| 工具默认并行;预检(校验 + 权限)按顺序做,执行并行;同一文件的修改串行 | ✅ 采用 | 2.x 是并行里各自审批,改为顺序预检,审批卡片顺序可预期 |
| `stopReason=length` 时所有工具调用判失败并提示重发 | ✅ 采用 | 2.x 直接报错结束,太粗暴 |
| `Edit` 支持一次多处替换、精确匹配失败时做宽松匹配 | ✅ 采用 | 同时保留 2.x「写之前必须读过」的检查 |
| `Bash` 输出保留尾部并把完整输出存到临时文件 | ✅ 采用 | 2.x 的后台 Shell 保留 |
| 压缩摘要用结构化模板 + 累积的读写文件清单 + 迭代摘要 | ✅ 采用 | 触发时机和边界消息沿用 2.x |
| 引导消息(steer)和后续消息(follow-up)两种队列 | ✅ 采用,语义按 2.x | 见 [02 §5](02-loop.md#5-引导消息与排队消息) |
| 扩展点的组合语义(通知 / 就地修改 / 链式变换 / 闸门 / 最后者胜) | ✅ 采用 | 见 [06](06-extensions.md) |
| 工具的 `PromptSnippet` / `PromptGuidelines` 拼出系统提示词的工具段 | ✅ 采用 | |
| 系统提示词写进对话记录、按分节打补丁 | ❌ 不采用 | 2.x 的「系统提示词稳定 + 装饰副本」已经能保住缓存,而且不改存储格式 |
| JSONL 会话树 | ❌ 不采用 | 沿用 2.x 的 SQLite(决策 D13)。会话分叉用「复制前缀到新会话」实现 |
| 无权限系统、无子代理、无计划模式、无待办 | ❌ 不采用 | 这些是 NextCoWork 的核心功能,也是手机审批的前提。以一方扩展实现 |
| 不设回合上限 | ✅ 一致 | 2.x 也没有上限,只有目标模式下的空转刹车 |
| 项目指令按目录取 `AGENTS.override.md` → `AGENTS.md` → `CLAUDE.md` 的第一个,并遍历祖先目录 | ✅ 采用(Q16) | 2.x 只读 `AGENTS.md`;不可信边界等安全处理保留 2.x 的 |

## 4. 关键取舍

| # | 取舍 | 结论 |
|---|---|---|
| R1 | 内核最小化 vs 功能齐全 | 内核最小,功能做成一方扩展,默认全开 |
| R2 | 统一消息模型以谁为准 | 以 2.x 的 Anthropic 块模型为底(信息最全),加上 pi 的来源字段 |
| R3 | 重试在哪做 | 只在模型层的路由里、首个内容字节之前;会话层只做「流中断续传」(2.x 规则) |
| R4 | 审批是并行还是顺序 | 顺序预检、并行执行(pi 规则,已确认 Q17) |
| R5 | 工具结果的形状 | `Content`(给模型)+ `Details`(给界面)+ `IsError` + `Terminate` |
| R6 | 何时落库 | 只在消息完成时落库(`message_end` 提交),流式增量只是事件 |
| R7 | 事件推送 | 线上只推增量,`message_end` 带完整消息;客户端以完整消息替换(pi 的 RPC 规则) |
| R8 | Run 何时算真的结束 | 新增 `run_settled` 事件(相当于 pi 的 `agent_settled`),落库和释放会话锁之后才发。下一个 Run 只等这个事件 |
| R9 | 会话存储 | SQLite,沿用 2.x schema;不做会话树 |
| R10 | 系统提示词 | 沿用 2.x 的三段式(稳定的 system、头部块、尾部状态块),基础提示词按 pi 的思路精简 |
| R11 | 压缩 | 2.x 的触发与边界 + pi 的摘要模板 |
| R12 | 子代理 | 保留,沿用 2.x 的深度、队列、权限收窄、不能提问 |
| R13 | 权限 | 保留 2.x 的决策链,作为不可关闭的一方扩展 |
| R14 | 并发模型 | 每个 Run 一个 `Task`,事件通过 `Channel<T>` 传递;共享状态只在编排层,用锁或单线程调度器保护 |
| R15 | 取消 | 全链路 `CancellationToken`;中止异常永远原样传播,不包装成工具失败 |
| R16 | 时间与 ID | `IClock`、`IIdGenerator` 注入;会话层不读系统时间 |
| R17 | 扩展的执行位置 | 一方扩展是 C# 代码,在进程内;插件扩展在 Jaunt 里,通过同一套扩展点,但只能注册白名单里的事件 |
| R18 | 工具访问文件和进程 | 只通过 `IExecutionEnvironment`(见 [06 执行环境](../06-environments-ssh.md)) |
| R19 | 模型目录 | 由 `ModelCatalogService`(models.dev)提供,模型层只读 |
| R20 | 辅助模型调用(标题、建议、压缩、审查) | 统一走一个 `AuxiliaryModel` 服务,不使用账户池,不带工具,用量单独记账 |

## 5. 核心类型速览

```csharp
// 模型层
public sealed record ModelRef(string ProviderId, string Alias);
public sealed record LlmRequest(string System, IReadOnlyList<Message> Messages, IReadOnlyList<ToolInfo> Tools,
                                GenerationOptions Options);
public abstract record LlmEvent;                                     // 见 01 §2

// 循环层
public interface IAgentTool { ToolInfo Info { get; } ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct); }
public sealed class AgentLoop { public IAsyncEnumerable<AgentEvent> RunAsync(LoopInput input, LoopHooks hooks, CancellationToken ct); }

// 会话层
public sealed class AgentSession { public Task<RunOutcome> RunAsync(RunRequest req, IRunSink sink, CancellationToken ct); }

// 编排层
public sealed class RunRegistry { RunHandle Create(...); Snapshot Snapshot(RunId id, long sinceSeq); ActiveIndex ActiveIndex(); }

// 扩展
public interface IAgentExtension { string Id { get; } void Register(IExtensionRegistry r); }
```
