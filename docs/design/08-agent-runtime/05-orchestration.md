# 08.05 · 编排(`Ncw.Agent.Kernel.Runs`)

pi 没有这一层;它对应 2.x 的 `run-registry.ts`、`services/agent.ts`、`session-runtime.ts`、`subagent-queue.ts`、`goal/*`、`scheduled/*`。
多端、子代理、后台任务都在这一层。

**实现状态(M3)**:`src/Ncw.Agent.Runs`(命名空间 `Ncw.Agent.Kernel.Runs`)已实现 §1 Run 生命周期、§2 事件日志 / 订阅 / 对账、§3 排队消息、§8 的后台 Shell 清理,并为 §5 子代理、定时任务留好扩展点
(`IRunExtension`、`RunLaunch.ParentRunId`、`RunRegistry.Post`、`RunRegistry.DeliverInternal`、`RunHandle.Background`,见 §10);§5 子代理已在 `src/Ncw.Agent.Subagents`(命名空间 `Ncw.Agent.Kernel.Subagents`)实现,
见 §5 的实现注;§6 计划、§7 待办与目标已在 `src/Ncw.Agent.Planning`(命名空间 `Ncw.Agent.Extensions.Planning`)实现——它们是一方扩展、不在编排层里,见 §6、§7 的实现注;定时任务已在 `src/Ncw.Agent.Services`(`Schedules/`)实现,见 §8。
单独成项目的理由与会话层相同(见 [04](04-session-context.md) 开头):编排层要同时碰会话层、存储和工具层的后台 Shell 注册表,所以放在它们之上。
已按本文逐条实现并用真 SQLite + 假上游 + 假工具验收(`tests/Ncw.Agent.Runs.Tests`,§11 的表把每条不变量对到测试);与本文最初草稿不同的地方在各节用「实现注」标出,汇总在 §12。

## 1. Run 生命周期

**`RunHandle`**:`RunId`(由**客户端生成**,这样客户端能在 Run 开始前就订阅)、`SessionId`、父 Run、深度、一个 `CancellationTokenSource`、
只追加的事件日志(递增 `seq`)、挂起的交互、子 Run 集合(只增不减)、引导消息邮箱、实时权限模式、`Abandoned` 标记。

**状态**:`Running` → `Done | Error | Aborted`,只能从运行中走向终态。终态之后再来的事件丢弃并记警告。

**入口**(统一走 `RunLauncher.Launch`,2.x 里定时任务绕过了它,3.0 不再允许):

| 入口 | 说明 |
|---|---|
| 用户发送 | 会话已有运行中的顶层 Run 时,改为进入排队消息 |
| 内部发起 | 排队消息续跑、后台子代理报告、目标检查 |
| 子代理 | 先继承父 Run 的订阅再启动 |
| 开放网关 | 会话忙时返回 409,不排队 |
| 定时任务 | 同样走 `Launch` |

**`Launch` 顺序**:创建 → 取会话互斥 → 建事件泵 → 记录 Run 开始 → 启动看门狗 → 运行驱动 → `finally` 里 settle(只执行一次):
释放互斥 → 写 `run_settled` → 处理排队消息 → 触发回收。

**会话互斥**:每个会话同时最多一个运行中的 Run 或一个「操作」(如手动压缩)。

**`run_end` 早于落库**:所以下一个 Run 只能在 `run_settled` 之后开始(R8)。

**看门狗**:中止后 5 秒还没 settle,就强制 `finish(Aborted)` 并 settle,标记 `Abandoned`。之后驱动的落库全部跳过,
因为会话可能已经属于下一个 Run(2.x #187)。

**回收**:Run 结束后同时满足以下条件才回收:没有订阅者、互斥已释放、**没有任何运行中的后代**(一直往下查)、没有挂起的交互、
过了 2 分钟(或已结束 Run 的缓存总量超过 64MB)。每 30 秒检查一次。

**实现注**:

- `RunRegistry.Launch(RunLaunch)` 是唯一入口;被拒绝时**返回** `LaunchResult.Refused`(会话忙 / RunId 重复 / 正在退出 / 请求无效),不抛、不留半成品。`RunRegistry.SendAsync(UserSend)` 是「用户发送」:
  会话已有运行中的顶层 Run 时进队列(回执 `SendResult.Queued`,客户端据此撤掉乐观消息),开放网关(`RunOrigin.Gateway`)忙时直接拒绝。排队续跑、内部输入也经 `Launch`(起点是 `RunOrigin.Internal`)。
- **`run_settled` 由编排层发,不由会话层发**:`AgentSession` 自己会发一个 `run_settled`,但那时注册表的互斥还没放(`RunAsync` 还没返回),客户端收到就立刻发消息会撞上「会话忙」。
  所以 `HandleSink` 把会话层那一个拦下,注册表在 `settle` 里按「记录 Run 结束 → 释放互斥 → 发 `run_settled` → 扩展 → 续跑排队消息 → 回收」的顺序自己发。`Settle` 只执行一次(`TryBeginSettle`)。
- `runs` 表:Launch 时写 `running`,settle 时写终态和结束时间(写失败只记诊断,不让 Run 失败)。用量落账**不在这里**:用量按「一次 HTTP 尝试一条」记在模型层路由里(2.x 的 `usage_records`),编排层没有任何可记的东西。
- **看门狗**只在「中止后宽限期(5 秒)过了、`run_end` 还没出现」时动手(2.x `launch` 的判据 `status !== 'running'` 就返回)。已经有 `run_end`、驱动正在落库的 Run 不强收:那时放锁,下一个 Run 会和还在写库的驱动交错。
  本文原稿「中止后 5 秒还没 settle 就强制」按 2.x 收紧为这一条。`Abandoned` 之后:该 Run 的事件在 `HandleSink` 里一律丢弃,`Settle` 不会再收第二次。
  **Abandoned 之后会话层写库的跳过(已补齐)**:2.x 的「驱动的落库全部跳过」靠运行时自己查 `abandoned`;3.0 在 `RunRequest` 上加了 `IsAbandoned`(`Func<bool>?`,`null` = 永不放弃),
  编排层的 `Augment` 把它接到 `RunHandle.Abandoned`(单调:只会 false → true;看门狗强收和删除会话前的 `ForceSettleSessions` 都走 `Abandon`)。`AgentSession` 在**每个落库点**动手之前统一问一次(`SkipIfAbandoned(request, site)`),
  放弃了就跳过并记一条诊断,日志行形如 `session.write.skippedAbandoned site=<site> run=<id> session=<id>`(key 是 `SessionDiagnosticKeys.WriteSkippedAbandoned`,只进诊断日志,不产生界面文字)。落点(`site`):
  `commit`(`CommitAsync`:用户输入、助手消息、工具结果、引导 / 内部消息,以及循环层在开始 / 出错 / 中止 / 流中断时补的孤儿修复结果——它们全部经这一个入口,内存转录也不再更新)、
  `compact`(压缩开始前:不发摘要请求)、`compact_commit`(摘要请求在飞期间被放弃:边界消息不落库,`after_compact` 不发,不计压缩失败也不熔断)、
  `prepare_turn`(回合边界与压缩之后:直接以 `Aborted` 收尾,不再给上游发请求)、`run_end` 与 `run_settled`(会话层的这两个**扩展通知**:目标 / 计划 / Hook 据此写状态或起续跑,编排层已按 `Aborted` 收过口,扩展不能再收到第二个 `run_end` / `run_settled`)。
  `Abandoned` 之后事件一律丢弃(`HandleSink`)、`Settle` 只收一次这两条既有规则没有改;`run_settled` 仍由编排层发且只发一次;正常 Run 的 `IsAbandoned` 恒为 false,落库顺序(`message_end` 提交语义)不变。
  **仍然成立的边界**:① 检查与 SQLite 写入之间不是原子的——放弃发生在「已通过检查、正在写」的那一刻时,这一条照常落库(此时它仍在会话锁内,下一个 Run 得到 `session.busy`,不会交错;写进去的是放弃之前就已开始的那一条,不是放弃之后才发起的);
  ② `AgentSession` 的会话锁仍由醒不过来的驱动攥着(锁是 `SemaphoreSlim`,不能替它放),所以放弃后到驱动真正退出之前,同一会话的下一个 Run 会收到 `session.busy`——这是「用户看到的卡死」的残余,不是写库交错;
  ③ 不归会话层管的写入:扩展与工具自己的状态(目标 / 计划 / 待办的 kv、`ChangeRecorder` 的内存采集、磁盘上的文件改动本身)。改动集入库发生在 `run_end` 进日志时的 `OnEnded`(编排层的 Run 扩展,一次性、读即清),放弃之后工具再写盘只会进已清空的内存桶、不会再落库;
  ④ `RepairOrphans` / `CompactNowAsync` 是「会话空闲时」的冷调用(占会话锁、没有 Run),不受 `IsAbandoned` 管,被放弃的驱动攥着锁时它们得到 `SessionBusyException`。
  确定性测试:`Ncw.Agent.Session.Tests.AbandonedRunTests`(每个落库点)、`Ncw.Agent.Runs.Tests.AbandonedWriteTests`(手动时钟推动真看门狗 / `ForceSettleSessions` 放弃一个卡在工具里的真 `AgentSession`,放行后断言库里没有放弃之后的写入、`run_settled` 只一次、下一个 Run 正常落库)。
- 会话互斥是注册表里的一张「会话 → 占用者」表(占用者是一个 Run 或一个操作);`AcquireOperation` 给手动压缩这类跨 await 的历史操作用,释放只清自己拿的那把。`Activity` / `ForceSettleSessions` 是删除会话前的活体检查和强制收口(2.x 同名函数)。
- **没有全局并发上限**:2.x 对顶层 Run 没有全局上限(只有子代理有:每父 Run 默认 4、全局可配,见 §5),所以这里不加。`ActiveChildrenOf` / `ActiveSubagentRunIds` 给子代理队列用,数的是**运行中的**(不是 `Children` 这个只增不减的累计集合)。
- **回收**用定时器(默认 30 秒)和每次 settle 触发;条件按本节:已 settle(互斥已放开)、没有订阅者、**没有任何运行中的后代**、交互闸门说没有挂起的交互(`IInteractionView.PendingCount`,Host 适配)、结束满 TTL(2 分钟)或已结束 Run 的日志总量超过 64MB(从最早结束的回收起,其余条件照样要满足)。

## 2. 事件日志、推送与对账

- 每个 Run 一份事件日志,`seq` 严格递增。裁剪规则见 [02 §3](02-loop.md#3-事件流);硬上限 2000 条 / 16MB,超出时标记 `LogTrimmed`。
- **快照** `Snapshot(runId, sinceSeq)`:状态、当前 `seq`、`seq` 之后的事件(过滤而不是切片)、挂起的交互、子 Run 列表。
- **推送**:每个 Run 一个事件泵。只有文本、思考、工具参数增量按 16ms 或 64 条批量合并;结构性事件立即推送。
  手机控制端在 `hello` 里声明 `coalesceMs`(见 [02 A3](../02-agent.md#a3-能力协商)),泵按控制端各自合并。
  没有订阅者时丢弃**整批**,不丢单条;Run 照常运行,日志才是真源。
- **订阅顺序**:先刷新泵 → 订阅本 Run 和所有后代 → 再取快照(2.x ★,否则会漏事件)。
- **权威广播** `activeRuns`:
  - `runs`:运行中的顶层 Run,含**所有后代**的待处理交互数。
  - `subagents`:运行中的子 Run(`sessionId` 用父会话,父 Run 已回收时用 `parentSessionId`)。
  - 在 Run 创建、`run_end`、每次交互发起和处理时广播;客户端首帧和后续广播用同一个生成函数和类型。
  - 这是客户端状态规则 5 的兜底:丢了任何一条结束事件,界面都能被这条广播收口。
- **多设备**:每台设备各自广播。客户端按 `(deviceId, runId)` 记账;设备断开时状态标为 `unknown`。

**实现注**:

- **日志**(`RunEventLog`):`seq` 在 `RunHandle` 的锁里分配,严格递增、不受裁剪影响。裁剪分两种:**无损**的——`message_end` / `message_discard` 之后清该消息的增量(保留 `message_start` 和提交本身)、`tool_end` 之后清该调用的进度(**子代理派发的排位 `{"kind":"queued"}` 除外**:后台 Task 调用立刻 `tool_end` 而派发还在排队,客户端把排位留到 `subagent_start`,日志不能先裁掉它),
  结构性事件永不裁;**有损**的硬上限(2000 条 / 16 MiB,最新一条永远留着)——从头砍,标 `LogTrimmed` 并记下被砍掉的最大序号。重放用**过滤**(`seq > since`)。字节数是估算(字符数 + 固定开销),不序列化、不走反射。
- **游标**(`CursorStatus`):不带游标 = `Fresh`(全量);游标还在日志里 = `Continued`(只给其后的事件);游标早已被硬上限挤出去 = `Lost`(给现存全部,客户端以快照重建)。无损裁剪永远不会让游标丢失。对应 09 §8.2 的「游标为空,或游标已不在日志里 → snapshot」。
- **订阅**(`RunSubscription`):一个控制端对一棵 Run 树的订阅 + **它自己的事件泵**(手机各自的 `coalesceMs`,默认 16 ms、单批上限 64)。只有 `message_delta` 会被攒;结构性事件立即送,并带上它前面攒着的增量(保序)。
  没有订阅者时 Run 不产生任何推送开销。启动时绑定的订阅(`RunLaunch.Subscriptions`)在第一个事件之前就挂好;子 Run 创建时继承父 Run 的全部订阅(都在注册表锁里,与 `Attach` 互斥)。
- **`Attach`**(2.x ★ 订阅顺序「刷新 → 订阅 → 快照」):对每个 Run,「取快照 + 挂订阅」在它的锁里原子完成——快照里的事件不会再推一遍,快照之后的事件一条不漏;
  因为每个订阅有自己的缓冲,所以不再需要「先刷新泵」这一步,这个顺序在结构上成立。`Attach` 订阅根 Run 和它**所有后代**,返回 `AttachResult`(根快照 + 各后代快照);游标只对根 Run 生效。Run 不存在返回 null(客户端退回库里的终态)。
- **工具作用域事件的顺序**(`IToolCallScopedEvent`:`tool_progress`、`subagent_start / update / end`):工具线程经 `RunRegistry.Post` 直接投事件,而循环自己的事件(包括这次调用的 `tool_start`)要经循环的事件通道,CPU 吃紧时前者先到——控制端对「还没有卡片的进度」只能丢掉。
  `RunHandle` 因此保证这类事件**永远排在对应的 `tool_start` 之后**:先到的暂存(`Emit` 返回非 0,表示已接受),`tool_start` 一落下就紧跟着落(各自分配 `seq`、各自推给订阅者);Run 结束时仍无 `tool_start` 的暂存事件丢弃并记一笔。
  **`run_settled` 不蕴含子 Run 已收口**:`Task` 工具是循环里「可中止的等待」(中止时循环立即返回、不等工具,见 `RunExecution.Abortable`),所以级联中止时父 Run 可能先于子 Run 的 `run_end(aborted)` 结束,子 Run 的 `subagent_end` 因父 Run 已终态而被丢弃——收口靠 `activeRuns` 权威广播(它的 `Subagents` 半边由仍在运行的子 Run 算出),不靠父 Run 的日志;断言「父已 settle ⇒ 子已终态」是错的。
- **积压溢出**(09 §8.2):接收方(`IRunListener.TryDeliver`)返回 false = 发送缓冲满了。只含增量的批**整批丢掉**(`DroppedBatches`,Run 照常运行,`message_end` 带完整消息补上);含结构性事件的批**永不悄悄丢弃**——订阅结束,接收方收到 `Resync`,客户端必须不带游标重新订阅。接收方抛异常只记诊断,不影响 Run 和别的订阅者。
- **终态单调**:`RunHandle.State` 只由第一个被接受的 `run_end` 写下;终态之后的事件(迟到的、重放的旧事件,**包括旧的 `run_start`**)一律丢弃并记警告、不推进 `seq`;唯一的例外是紧随终态的 `run_settled`(恰好一次)。
  Core 里的 `RunLifecycle.Merge` 把同一条规则给客户端用:终态 > 运行中 > 未知,不论哪一份更晚到。
- **`activeRuns` 权威广播**:`RunRegistry.ActiveRunsChanged` 事件,载荷是全量的 `ActiveRunIndex`(`Ncw.Core.Domain`:顶层 `Runs` + 子代理 `Subagents` + 单调的 `Rev`)。生成函数只有一个——Core 里的 `ActiveRunIndexBuilder.Build(deviceId, facts, rev)`,
  首帧(`RunRegistry.ActiveRuns()`,`device` 主题的快照)和每次广播都调它;注册表只负责把自己的 Run 表整理成 `RunFact`。触发点:Run 创建、`run_end`(不是 settle:此时 Run 已经不是运行中了)、`NotifyInteractionsChanged()`(交互发起 / 处理)、运行期间每 30 秒的兜底重播(01 §5)。
  构建和入队在同一把锁里,所以 `Rev` 与内容同序;派发在锁外串行(一个派发者),订阅者收到的 `Rev` 严格递增;每个订阅者各自 try/catch(多播委托遇到第一个抛异常的就会停下,排在后面的收不到,所以逐个调用)。它**不依赖任何 Run 订阅**。
- 交互的真源只有交互闸门(`Ncw.Agent.Extensions`):注册表不引用它,只经两个接缝接进来——`IInteractionView.PendingCount`(数数)和 `RunRegistry.Post(runId, event)`(把闸门的 `interaction_request` / `interaction_resolved` 并进 Run 的事件流、分配 `seq`)。

## 3. 排队消息(会话运行时)

2.x 的 `SessionRuntime` 移植为 `SessionQueue`:

- 用户在 Run 进行中发送 → 进入 pending;用户点「插话」→ 提升为 promoted,整体替换 Run 的引导邮箱。
- 内部消息(后台报告、目标启动)走 `EnqueueInternal`,在客户端替换队列时也不会丢。
- Run **以 Done 结束并 settle 后**才续跑:下一批是全部 promoted 合并,或者 pending 里按先进先出取一条。
- 中止或出错不自动续跑;Agent 冷启动不自动跑。
- 送达确认:看到同 id 的 `message_end` 才把队列项标为已送达;没确认的内部消息在 settle 时收回。
- 队列变化发 `queue_update` 事件,所有控制端看到同一个队列。

**实现注**(`SessionQueues`,2.x `SessionRuntime` 的移植;草稿相关的部分——防抖、flush、草稿写盘失败——不移植,3.0 里草稿是客户端本地状态,kv 行里的草稿和未知字段原样保留):

- 每个会话一个条目,条目里的 `RunInputQueues` **同时是用户队列和正在跑的 Run 的引导信箱**:`SessionQueues.TakeSteering` 直接接 `RunRequest.TakeSteering`,所以「点插话 / 编辑 / 删除」不需要 2.x 的 `syncInterject`(把全集推给信箱)这一步。
  条目只在有内容时常驻(空了就丢,`Rev` 是全局计数,所以重建后不回退)。
- **续跑选择沿用 2.x,不用 `RunInputQueues.TakeFollowUps`**:`QueueBatches.PickNext`——有 promoted 就只发 promoted(按引入顺序,一条 pending 都不捎带),没有就 FIFO 取**一条**;`Merge` 合并成一次输入(32000 字符上限,超限退回 pending 并保序,首条无论多长都完整发出)。
  `TakeFollowUps` 的「全部交出」与 2.x 不符(见 02 §5 实现注),编排层不用它。
- **送达以同 id 的 `message_end` 为准**:被 `TakeSteering` 取走的用户条目先记进 inflight(仍然落盘),看到同 id 的 `message_end` 才真的移出并落盘;Run 收尾时还没确认的,先按持久消费回执(`session_input_receipts`)过滤,再以 promoted 退回队列
  (「已执行的输入绝不会再发一次」的硬保证:清队列的写可能失败、事件可能被丢、转录可能被压缩改写,回执独立留痕)。读不到回执时不据此删条目。
- **先落盘再确认**:入队 / 引入 / 编辑 / 删除 / 撤回 / 改档位 / 续跑移交,都是先写 kv(`SessionInputStore`,经 `IQueueStore` 接缝),成功后才递增全局 `Rev`、广播 `QueueChanged`、返回回执;写失败 → 回滚内存、抛 `QueuePersistenceException`,
  绝不回 accepted,也不去续跑。续跑移交后启动失败 → 条目放回队首并再落盘(这一步再失败就抛出去)。超过 20 条、同 id、找不到 → `Accepted = false`,不写盘、不广播。
- **续跑入口**只有三个:`OnRunSettled`(`run_settled` 之后,**不是** `run_end`)、空闲时的入队 / 引入、手动 `ResumeQueue`。只有 Run 以 `Done` 结束才自动续跑,中止 / 出错的原样留着;冷启动从 kv 恢复(结构对不上 / 过期整份丢弃),恢复本身不会跑任何东西。
  冷启动恢复出来的「已插话」条目,在用户随后发起的第一个 Run 的回合边界会被送进去(2.x 是等该 Run 结束后续跑;两者都满足「用户的下一个动作之后才会动」)。
- **内部输入**(`DeliverInternal`,后台子代理报告、目标检查):Run 在跑就进它的引导队列(`EnqueueInternalSteering`,用户侧的整份替换抹不掉),会话在收尾 / 被操作占着就排到之后(有界重试:每秒一次、最多 120 次,耗尽则回调 `Done(false)`),空闲就起一个新的内部 Run;
  `Done(true)` 要等同 id 的 `message_end`;Run 正常结束时没被消费的收回、排在排队的用户消息**之前**重投,中止 / 出错 / 启动被拒则 `Done(false)`;没有任何档位可用时失败而不是静默 return。
  收尾时还留在引导队列里没被取走的内部消息先取出(`RunInputQueues.TakeInternal`),否则会被下一个 Run 顺手取走(重复投递)。汇报状态的持久化(pending / injecting / reported / blocked)属于子代理层。
- 单条的批沿用条目 id 作为续跑那条用户消息的 id(2.x 总是新生成一个):这样「已消费回执」对它同样有效;多条合并时生成新 id。
- 排队消息的文件引用在入队前经 `AgentSession.NormalizeInputAsync` 归一化(`RunRegistry.EnqueueAsync` / 排队分支的 `SendAsync`),和首条输入同一个写法。

## 4. 并发模型

- 每个 Run 的驱动是一个 `Task`;循环层和会话层在这个任务里顺序执行(工具并行执行除外)。
- 编排层的共享结构(Run 表、会话互斥、子代理队列)由一个**单线程调度器**(`Channel` 驱动的 actor)维护,避免锁;
  对外提供的查询返回不可变快照。
- 所有等待都带 `CancellationToken`;没有任何无界等待(交互除外,它没有超时但会被中止和 `run_end` 结束)。

**实现注**:共享结构(Run 表、会话互斥表、订阅、队列条目)用**锁**维护而不是单线程调度器——R14 允许「锁或单线程调度器」。锁的层次固定为:会话队列条目锁 → 注册表锁 → `RunHandle` 锁 → 订阅缓冲锁,
回调(广播、订阅投递、扩展、交互视图)一律在锁外调用(队列广播除外:它在会话队列条目锁内触发,以保证同一个会话的广播严格按 `Rev` 有序,处理器必须快速返回)。`System.Threading.Lock` 可重入,
所以队列续跑里 `Launch` → `OnRunStarted` 回到同一把锁是安全的。每个 Run 的驱动是 `Task.Run` 出来的一个 `Task`;应用退出时 `ShutdownAsync` 等所有驱动(有上限)。

## 5. 子代理

**启动**(`Task` 工具):
1. 深度检查(`MaxDepth = 2`)。
2. 未知的 Agent 名称 → 拒绝,并列出可用的 Agent。**不悄悄回退**到通用 Agent。
3. 子 RunId `<父 RunId>:sub:<N>`;子会话 `<父会话>:sub:<子 RunId>`,`parent_session_id` 记真实父会话。
4. 子请求:深度 +1,模式 `code`,权限 `min(父实时值, 定义值)`,联网开关和最大上下文继承。
5. 进入子代理队列。

**队列**:
- 两个上限:每个父 Run(默认 4)和全局(0 表示不限),每次调度时重新读设置。排队上限每个父 Run 16、全局 64。
- `Acquire` **同步决定**「排队还是立即开始」,调用方马上知道结果。
- 公平:同一个父 Run 已有排队项时,不走快速通道。
- 死锁检测:所有占用名额的 Run 自己都在队列里阻塞(只计前台等待者)时拒绝。
- 等待者在父 Run 中止**和**父 Run 结束两条路径上都要取消(两者不同,2.x ★)。
- 1 秒的安全网定时调度,外加子 Run 结束时主动通知。

**父子联动**:
- 父 Run 上发 `subagent_start`;子 Run 的工具开始和结束、上下文用量、消息结束,在父 Run 仍在运行时镜像成 `subagent_update`,带 `childSeq` 去重。
- `subagent_update` 的字段语义:键存在但值为空 = 清除;键不存在 = 保持。
- 子 Run 结束后**等驱动真正结束**(而不只是 `run_end`,因为历史在 `run_end` 之后才写),再读子会话最后一条助手消息,
  更新父会话里的持久回执,触发 `SubagentStop` Hook,发 `subagent_end`。
- 子代理**不能发起交互**(三处对称,见 [03 §7](03-tools-permissions.md#7-不变量清单))。2.x 曾因此死锁一小时。

**后台子代理**:`run_in_background` 立即返回(区分「已排队」和「已开始」);前台子代理可以被用户挪到后台。
后台结果以**全文**作为内部引导消息报告给父会话(不是 240 字符的摘要),报告状态 `pending → injecting → reported / blocked`。

**状态单调**(和客户端共用 `Ncw.Core` 里的函数):
- `MergeSubagentRecord(saved, live)`:终态压过运行中,不论来自哪边;遥测取实时值。
- 父会话最终写历史前合并最新回执,排名 未知 < 运行中 < 终态,不会把已完成的子代理降级。
- 重放的 `subagent_start` 不能让终态卡片复活;终态之后的更新忽略。

**实现注**(`src/Ncw.Agent.Subagents`,M3):

- **项目与依赖**:在编排层(`Ncw.Agent.Runs`)和一方扩展(`Ncw.Agent.Extensions`:权限状态、交互闸门)之上,另引用目录层(只用 `ModelSelection` / `ModelSelections.ForSubagent` 这个纯函数)。不引用 Protocol、Avalonia、客户端、Host;不直接碰文件和进程(定义文件只经 `IExecutionEnvironment`);不产出界面文字(诊断带稳定 key + 英文,发给模型的文字是英文常量)。
- **`Task` 工具**(`TaskTool`):`description` 按**当前工作区的清单**拼(`AgentRegistry.List()` 同一个列表对象 = 内容没变 = 同一个 `ToolSpec`,提示词缓存稳定);可选参数含 `run_in_background`、`resume`(同父会话同工作区恢复已有子会话转录)、`max_turns`、`model`、`isolation: "worktree"`;深度闸门第一行;结局映射三种不同的东西(拒绝原样转交 / 空文字当失败 / 网络故障明说瞬时);
  **中止原样抛**,只有「子代理被单独停掉、父 Run 还活着」时才返回一个清楚的失败结果(C# 的循环对非中止的 `OperationCanceledException` 会当成「工具失败」,所以 2.x 那条「`aborted` 一律抛」要按父 Run 的令牌区分)。
- **能力注入,不套 `ToolContext`**(偏离 03 §1 / 06 的「`ToolContext` 派生类型带 `SpawnSubagent`」):`SessionToolContext`、`ExecutionToolContext` 都是「换一个子类型」的写法,两个兄弟子类型套在一起会丢掉彼此的能力(`base(inner)` 只拷贝 `ToolContext` 自己的字段)。
  所以 `Task` 经注入的 `ISubagentLauncher`(`SubagentService`)工作,按 `ctx.RunId` 找回父 Run 的句柄。
- **定义**(`AgentLoader` / `AgentRegistry` / `AgentCatalog`):全局目录在设备本机、项目目录 `<项目根>/.next-cowork/agents` 在项目的执行环境里;永不抛(连接断开除外:那不是文件的问题,原样抛出,不能判成「没有子代理」);
  内置四条写死在代码里先进表;缺 description / 正文为空 / `tools:` 一个都认不出 / `permissionMode:` 认不出 → **整条作废**,`thinking:` / `effort:` / `color:` / `maxTurns:` / `isolation:` 读错 → 当没写并记诊断;支持 Claude Code 兼容字段 `disallowedTools`、`maxTurns`、`effort`、`background: true`、`isolation: worktree`;软链逃逸被路径守卫挡住;frontmatter 用 `FrontmatterParser`(2.x `frontmatter.ts` 的逐条移植,Skills 也将用它)。
  诊断是 `AgentDiagnostic(Path, Key, Message, Params)`(2.x 是中文文案,3.0 改成 key + 英文)。`AgentCatalog` 每个工作区一份注册表,`RefreshAsync` 在顶层 Run 的 `before_run` 里扫一遍并滤掉被用户关掉的;`ListAsync` / `DiagnosticsAsync` 只读,不碰注册表。
- **子 Run 的创建与隔离**(`SubagentService.SpawnAsync` → `StartChild`):子 RunId `<父 RunId>:sub:<N>`(N 进程内单调递增,2.x 同,所以孙代理可能是 `:sub:2` 而不是 `:sub:1`);`SessionRepository.Ensure(…ParentSessionId)` 建或复用子会话行(名额到手才建;`resume` 时校验同父同工作区且不在运行中,保留原 SQLite 转录);
  请求由 Host 的 `IRunRequestFactory` 出模板、再改 `Depth`、`AgentPrompt`、`AgentType`、`MaxTurns`(定义与调用取 `min`,会话层在回合开始前检查 `turnIndex >= maxTurns` 收尾)与 `WorkspaceRoot`(仅当 `isolation: worktree` 时指向临时 git worktree);模式 `code`、`maxContext` 继承;模型 = `ModelSelections.ForSubagent`(调用覆盖 / 声明 > 设置里的默认子代理 > 父 Run;悬空的供应商锁摘掉、悬空的默认值落到下一档,经 `ISubagentModels` 询问路由器);
  思考档位 = `SubagentThinkingSelection.Select` 再经 `ISubagentModels.NormalizeThinking` 静默降级;启动模式遵循 `run_in_background ?? definition.DefaultBackground`;`isolation: worktree` 由 `SubagentWorktree` 全程经 `IExecutionEnvironment.RunAsync` 跑 `git worktree add/status/rev-list/remove`,干净时自动清理、有未提交改动或新提交时保留并将路径与分支交回父代理。
- **权限与交互**:`SubagentService` 同时是 `IRunExtension`:`OnCreated`(子 Run)取父 Run 的 `PermissionRunState.ForSubagent`(档位 = `min(父实时值, 定义值)`,父 Run 中途调低子代理立刻跟着降)并 `OpenRun(depth > 0)`;拿不到父状态就不登记(权限扩展对没登记的 Run 是拒绝,失败即关闭)。
  `OnEnded` 兜底结清交互。工具快照由 `SubagentExtension` 的 `before_run` 收窄(定义里的 tools ∩ 去掉 `AskUserQuestion` / `ExitPlanMode`)。三道「不能提问」的闸(快照过滤 / 不提供 `Interact` / 审批直接拒绝)现在都有端到端测试。
- **队列**(`SubagentQueue`,2.x 逐条移植):用一把可重入锁代替单线程事件循环;`Start` 在锁内同步执行(避开 TOCTOU);排位回调在锁外调;兜底重扫用 `TimeProvider` 的定时器(测试用 `ManualClock`)。
  **新增两处线程安全修正**:① `CancellationTokenRegistration.Dispose` 会等正在执行的回调,而回调(`Cancel`)在等队列锁——锁内 Dispose 是死锁,所以摘取消登记一律在锁外做;② 派发循环对「父 Run 已被要求中止」的等待者不再起新子 Run,另外每个子 Run 挂在父 Run 的取消令牌上
  (级联中止遍历的是它当时看到的子 Run 集合,排队等到名额的那个子 Run 可能恰好是在那之后才登记的,不挂的话它会成为孤儿)。
  **2.x 的特性原样保留**:排队中的等待者在父 Run 结束时也被取消,后台派发也一样(回执改成 `aborted`,留给用户点「处理」)。
- **子代理事件**:`subagent_start` / `subagent_update` / `subagent_end` 是本层定义的 `AgentEvent`(`SubagentStartEvent` / `SubagentUpdateEvent` / `SubagentEndEvent`,字段语义「键存在但值为空 = 清除」用 Core 的 `Patch<T>`)。
  镜像由 `ChildMonitor` 做:它是子 Run 的**启动前绑定的订阅**(`RunLaunch.Subscriptions`),订阅回调里只计数,真正的 `RunRegistry.Post` 交给串行的泵(订阅回调不许回调注册表的写方法);父 Run 结束后 `Post` 返回 0,自然丢弃。
- **收尾**:等 `RunHandle.Completion`(驱动 + settle,不只是 `run_end`:历史在它之后才落库)→ 读子会话最后一条助手消息 → 更新回执 → 发 `subagent_end` → 后台的向父会话汇报 → `Stopped` 事件(`SubagentStop` Hook 的触发点)。永不抛。
- **回执持久化**(`ReceiptStore`):2.x 整段回写历史再合并最新回执;3.0 消息只在完成时写一次,改成对**单条消息的比较并替换**(`ReplacePartsIfUnchanged`)。状态经 Core 的 `SubagentView.MergeStatus` 单调合并(终态压过运行中,第一个终态胜出),汇报进度里 `reported` 是终点。
  镜像问题:子代理比父 Run 提交那条工具结果还快时回执所在的消息还不在库里——更新记下来,父会话 `run_settled`(`IRunExtension.OnSettled`)之后补写。`SubagentReceipt` 追加了 `Background / ReportStatus / Color / Error`,新增 `SubagentReportPart`(2.x 的 `subagent` 块,只给界面,编码器丢弃)。
- **报告**(`SubagentReports`):全文作为内部引导消息经 `RunRegistry.DeliverInternal` 回到父会话;中止的不自动汇报;进程内按 `(会话, callId)` 去重;没有任何档位 → `blocked`;`ReportManually` 是用户点「处理」。状态变化经 `ReportChanged` 事件给 Host 推送。
- **前台转后台**(`DetachForeground`):标记是可变的,结束那一刻才读;只认「还在跑」的。
- **Core 补齐**(`Ncw.Core.Domain.Subagents.cs`):`SubagentReportStatus`、`Patch<T>`、`SubagentRecord` 追加 `ChildRunId / ChildSessionId / Background / ReportStatus / CurrentTarget / LastEventAt`、
  `SubagentView.ReportOf / CompareByRecency / MergeStatus / WhenMissing / ApplyStart / ApplyUpdate / ApplyEnd / ParseStatus / ToWire`(客户端对 `subagent_*` 事件的归约和 Agent 写回执用同一份规则)。

**Host 接线清单**(**已全部接上**,`Ncw.Agent.Host/Conversation`,见 [09 §8.2 / §8.4](../09-protocol.md)):`SubagentService` 在造 `RunRegistry` 之前造(依赖:`SessionRepository`、与顶层 Run 同一个 `IRunRequestFactory`、`AgentCatalog`、`PermissionRuns`、`InteractionGate`、设置源 `StoredSubagentSettings`、`ISubagentModels` = `IModelResolver` 的适配)、
放进 `RunRegistryDeps.Extensions`、造完 `Bind(registry)`;Host 自己的 Run 扩展对 `Origin == Subagent` 的 Run 不再登记权限状态 / 开交互作用域(只给它造 `ToolRuntime`、套 `ExecutionToolContext`);每个工作区的会话扩展里一个 `SubagentExtension(service, workspaceId)`;
`IAgentScanSource`(全局目录 `<数据根>/agents` 在设备本机环境,项目目录在项目的执行环境)和 `IAgentSwitchStore`(kv `agents.disabled`);`RunLaunch.Options` 由注册表自己带上(子代理继承父 Run 的模型 / 思考档位 / 最大上下文)。
协议映射:`subagent_start / update / end` 是 `RunEventDto` 的三个多态类型,**并进父 Run 的 `run/<id>` 主题**(子 Run 的完整事件流是它自己的 `run/<childRunId>` 主题,控制端点开面板才订阅——不需要设备主动推送的子主题);排位走 `tool_progress` 的结构化 `Details`;
`ReportChanged` → 父会话主题的 `subagent.report`,`Stopped` → `subagent.stopped`;`runs.background` = `DetachForeground`;`subagents.list / diagnostics / setEnabled / report`。

## 6. 计划模式

- 活动计划**按会话**存(一个计划跨多个 Run)。
- 工具允许列表每回合重新计算:总是去掉 `Task`;有活动计划时隐藏 `EnterPlanMode`;没有时隐藏 `Write / Edit / ExitPlanMode`。
- 写入围栏:计划期间 `Write / Edit` 只能写计划文件。
- 计划文件路径每回合重新注入状态块(压缩会抹掉包含路径的工具输出)。
- `ExitPlanMode`:发起 `plan_approval`,回答后**重新读文件**;批准或拒绝都结束计划并 `Terminate`;要求修订则保持计划。
  批准后由客户端以 `code` 模式发起新 Run,带上计划内容。

**实现注**(`src/Ncw.Agent.Planning/Plans`,一方扩展 `PlanModeExtension`〔Id `plan-mode`,Order 400〕+ Run 扩展 `PlanRunExtension`):

- **状态**:`PlanRegistry` 按**会话**存活动计划(一个会话同时一份,最多记住 64 个会话、超了丢最旧的,兜「提了一半就再没回来」的)。和 2.x 一样**只在进程内**:重启后计划文件还在但围栏没了(`EnterPlanMode` 重新可用,模型会开一份新的)。
  `PlanService` 是工具、扩展、Run 扩展共用的那一个对象(活动计划、每个 Run 的草稿引用、围栏同步、允许列表)。
- **工具允许列表**:`before_run` 把过滤器**套在别人已设的 `ToolFilter` 外面**(只过滤不新增,永远提不了权),闭包每次取快照现算,所以计划在 Run 中间开 / 关,下一回合的工具表就跟着变。
  「这个 Run 开着计划工作流」(2.x 的 `mode.id === 'plan' || mode.tools 含 EnterPlanMode`)由装配方回答(`PlanOptions.PlanWorkflow(ExtensionContext)`,模式 id 不在会话层的 `RunRequest` 里),缺省 false = 两个计划工具对所有模式隐藏;没开时只摘 `EnterPlanMode / ExitPlanMode`,`Task` 只在计划工作流里被摘。子代理(`depth > 0`)两个工具都不下发。
- **写入围栏**:`ToolRuntime.WriteFence` 与 `PermissionRunState.PlanFence` 永远是**同一个值**(`PlanService.ApplyFence` 一起设)。一个计划跨多个 Run 而围栏在 Run 中间开关,所以 `ToolRuntime.WriteFence` 从 `init` 改成了线程安全的可写属性(对 Tools 层的小追加,
  写工具本来就每次调用现读)。进入 / 退出时由工具设;新 Run 里由 `PlanRunExtension.EnrichToolContext` 每次丰富工具上下文时按会话的活动计划同步(**装配方不必记得去设**;它必须排在造 `ToolRuntime` 的 Host 的 Run 扩展**之后**)。
- **计划文件**:`.plan/<ulid>.md`,只经 `IExecutionEnvironment`(`Paths.ResolveWithin` + `WriteStreamAsync`,进入时先落一个空文件并记一次「已读」,否则 Write 的「覆盖前必须读过」会挡住第一次写),1 MiB 上限。
- **`ExitPlanMode`**:交互句柄取自 `PermissionRuns.Get(runId).Interactions`(子代理拿到的不能提问 → 调用当场失败);发 `plan_approval`,回答后**重读**文件(审阅期间可能被编辑器改过 / 改坏);回执 JSON 逐字沿用 2.x(`{"type":"plan_file","planId","path","action","feedback"?}` + 空行 + 反馈),
  批准 / 拒绝 → `Terminate` 并清计划 / 围栏,要求修订 → 计划原样保留(修订往往发生在下一轮)。文件没了 → 把计划一并放掉,否则模型既写不成这一份、也重开不了新的。
- **状态块**:计划文件路径放在 `RunDraft.StateSections`,**进入 / 退出时就地同步**(`PlanService.Refresh`:状态块是每个 Run 算一次的快照,但路径是写文件的唯一依据,不能等到下一个 Run),压缩后经 `compact_attachments` 重附。
- **批准后执行**:客户端带着 `PlanFileRef` 以 `code` 模式发起新 Run 时,Host 先 `PlanService.ExpectExecution(runId, ref)`;`before_run` 里 `PlanExecution.LoadAsync` 载入(路径必须等于 `.plan/<id>.md`、存在、是文件、非空、≤ 1 MiB、非纯空白)放进状态块。
  **载入失败不静默**:`before_run` 的异常会被吞掉,模型就会在没有计划的情况下「执行计划」,所以失败写成一段「could not be loaded … do not guess」放进状态块。状态块每一段最多 4000 字符(`ContextAssembler.StateSectionMax`),而计划可以有 1 MiB:批准的计划**按行切成多段**,一行不丢。

### 不变量与测试对照(`tests/Ncw.Agent.Planning.Tests`)

| # | ★ 不变量 | 测试 |
|---|---|---|
| PM1 | 计划按会话存,跨 Run 不丢:下一条用户消息(新 RunId、同一会话)里 `Write / Edit / ExitPlanMode` 还在、围栏还在;另一会话不受影响 | `PlanModeTests.Keeps_the_plan_and_its_write_tools_available_in_later_runs_…`、`A_new_run_of_the_same_session_gets_the_fence_…`;`EndToEndTests.A_revision_request_keeps_the_plan_across_runs_…` |
| PM2 | 工具允许列表每回合现算、套在已有过滤器外面;计划工作流里总去掉 `Task`;没开工作流只摘两个计划工具;子代理不下发 | `PlanModeTests.The_before_run_filter_is_recomputed_…`、`The_allow_list_always_drops_Task_…`、`Without_the_plan_workflow_…`、`A_subagent_cannot_review_a_plan_…`、`Creates_one_markdown_file_and_changes_the_available_workflow_tools_by_phase`;`EndToEndTests.Plan_mode_fences_writes_…`、`Without_the_plan_workflow_the_plan_tools_are_not_offered_at_all` |
| PM3 | 写入围栏:`Write / Edit` 只许改计划文件(文件不被动一个字节);权限层与工具层是同一个值;新 Run 不需要装配方设;`ask` 档只有围栏文件被当作只读 | `PlanModeTests.Hard_fences_Write_and_Edit_…`、`The_fence_is_the_same_value_on_the_tool_runtime_and_the_permission_state`;`EndToEndTests.Plan_mode_fences_writes_…`、`In_ask_mode_the_plan_file_is_trusted_as_read_only_…` |
| PM4 | 计划文件路径每回合在状态块里,计划一变就同步,压缩后重附;中途开始的 Run 第一回合就看得见 | `PlanModeTests.The_state_block_carries_the_plan_file_path_…`、`A_run_started_in_the_middle_of_a_plan_…`、`Compaction_reattaches_the_plan_file_path`、`Without_the_workflow_there_is_no_plan_section_…` |
| PM5 | 批准 / 拒绝结束计划并 `Terminate`(批准后 Run 没有再请求一次模型),要求修订保持计划与围栏 | `PlanModeTests.Presents_the_saved_plan_and_stops_the_run_after_approval`(短 / 多字节 / 1 MiB)、`Rejection_also_ends_…`、`Continues_the_same_plan_when_the_user_requests_a_revision`;`EndToEndTests.Plan_mode_fences_writes_…` |
| PM6 | 回答后重读文件;展示前 / 审阅期间文件空、是目录、纯空白、超 1 MiB 都是工具错误且计划保持原样、不展示 | `PlanModeTests.Rejects_an_oversized_…`(ASCII / UTF-8)、`Rejects_a_plan_that_exceeds_the_size_limit_while_review_is_open`、`An_empty_plan_…`、`A_plan_path_that_is_a_directory_…`、`A_whitespace_only_plan_…` |
| PM7 | 文件没了就把计划一起放掉(围栏也撤),可以重开一份 | `PlanModeTests.Releases_the_plan_when_its_file_disappeared_…` |
| PM8 | 交互句柄不能问人(子代理)→ 审批当场失败;中止时挂起的审批被清理、计划保留 | `PlanModeTests.Review_needs_an_interaction_scope_that_can_ask`、`Aborting_the_run_aborts_a_pending_plan_approval_…`;`EndToEndTests.Aborting_the_run_while_the_plan_is_under_review_…` |
| PM9 | 批准的计划:路径对不上 id / 缺失 / 空 / 纯空白 / 目录 / 超限 / 逃出项目根都不静默(写进状态块);大计划按行切成多段、一行不丢、不切开代理对 | `PlanModeTests.An_approved_plan_*`、`Loading_an_approved_plan_rejects_a_path_that_escapes_…`、`Chunking_prefers_line_boundaries_…`;`EndToEndTests.An_approved_plan_is_carried_into_the_state_block_…` |
| PM10 | 注册表有界(64 个会话丢最旧)、一个会话至多一份;进入计划需要工作区;工具结果带执行环境标识 | `PlanModeTests.The_registry_remembers_a_bounded_number_…`、`A_session_has_at_most_one_plan_…`、`Enter_needs_a_workspace`、`Tool_results_carry_the_execution_environment_…` |

## 7. 待办与目标

- **待办**:从记录推出,不另存。每个 Run 最多提醒一次:本 Run 写过的最新待办还有未完成项且模型要结束时,注入一条内部提示让它继续。
  有目标、计划模式、后台子代理时不提醒。
- **目标**:按会话保存在内存,从记录里的 `goal_status` 恢复;注册为 `Stop` Hook。
  - 回合结束时:有后台子代理 → 延后检查(定时器,60 秒起指数退避,最多 3 次空闲检查);否则评估,达成或不可能 → 结束;未达成 → 继续。
  - 空转刹车:连续 8 个回合没有调用工具 → 强制结束并警告。这是唯一的「回合上限」。
  - 中止时暂停目标(轮换目标 id,旧 id 的消息在各层都被丢弃)。

### 待办的实现注(`src/Ncw.Agent.Planning/TodoLists` + `Ncw.Core.Domain.Todos`)

- **派生逻辑只有一份**(AGENTS.md §3 规则 3):形状 `TodoItem`、标记 `[x] / [~] / [ ]`、`Narrow`(严格收窄,不抛)、`Latest`(最近一次**成功**的)、`HistoryLookup`(每次调用之前那份)、`Plan`(增量)、视图 `TodoView` 都在 `Ncw.Core.Domain/Todos.cs`。
  Core 不认识 Llm 的消息类型,所以输入是一串按转录顺序的 `TodoEvent`(`Call` / `Result`):Agent 和客户端各自把自己的消息压成它(`TodoTranscript.EventsOf`,几行的映射),判据(回执存在且不是错误、还没有回执的不算、外部名按调用自己带的名字分组)只在 Core 里写一遍。
- **待办没有表,持久化 = 转录本身**。`TodoWrite` 完全无状态(回执里的「上一份」从转录读,`ITranscriptReader`;这次调用自己已在库里但还没有回执,所以不会算进自己的「之前」)。
  状态块里的待办段在 `before_run` 算**一次**(文案写着 "as of the start of this run":每回合现算 = 状态块每回合都变 = 前缀整体作废),压缩后经 `compact_attachments` 重附最新清单。
- **收尾前补报**(`TodoReconciler`,每个 Run 一个实例 = 额度每 Run 一次):挂在 `turn_end`(Order 600,在目标之后);只认**本 Run** 提交的消息(`prepare_turn` 第一回合记下起点:输入消息 id 在转录里的位置;没有输入消息 = 当时转录的末尾);本回合工具快照里没有 `TodoWrite` / 中断 / 子 Run / 让路时**不提醒也不吃额度**。
  「让路」(`TodoOptions.YieldTo`):会话里有目标、活动计划或后台子代理在跑——补报只是进度核对,不能覆盖它们的裁决,也不能催计划模式写平行清单。清单文字按不可信数据处理(削控制字符、中和提醒标签、单条 ≤ 200 字符带截断标记)。

### 目标的实现注(`src/Ncw.Agent.Planning/Goals` + `Ncw.Core.Domain.GoalConditions / ActiveGoal`)

- **组成**:`GoalStore`(进程内状态机 + 全量 `GoalChange` 通知;一个会话同时一个目标,`Set` 先清旧;`Revision` 版本号:用户设定 / 清除让它前进,自动完成不)、`GoalService`(设定 / 清除 / 恢复、回合末判定、空闲唤醒、提案)、`GoalEvaluator`(判定调用)、`GoalMarkers`(转录标记)、`ProposeGoalTool`、一方扩展 `GoalExtension`(Order 500)+ Run 扩展 `GoalRunExtension`。
  条件的规范化 / 清除词 / `/goal` 命令解析 / 检查间隔是 Core 的纯函数 `GoalConditions`(斜杠命令、`ProposeGoal`、协议方法三个入口用同一份),`ActiveGoal` 是客户端镜像的状态类型(首帧 `goal.get` 和后续 `GoalChange` 同一个类型,key 带 `deviceId`)。
- **不再是 `Stop` 钩子**:Hooks 一方扩展还没有,目标直接是 `turn_end` 上的处理器(Order 500,排在将来 Hooks 的 100 之后:收尾裁决按 Order 取第一个不是 `Stop` 的,所以 Stop 钩子的阻断先于目标、目标的续跑 / 强停先于待办补报)。
  判定器是旁路模型请求(`GoalEvaluator`:无工具、禁思考〔`CompactBinding.AuxiliaryThinking` 归一化,关不掉的模型降到最低可用档〕、1024 输出、30 秒墙钟);判定模型与供应商在 Run 开始时**成对冻结**(`GoalRunContext`)。Stop 钩子与目标共存的用例随 Hooks 移植。
- **回合末**(`GoalService.HandleTurnEndAsync`):子 Run / 已中断 → 不表态;会话名下有后台子代理 → **推迟**(不叫判定器、不计轮、标记推迟态、挂定时器,到检查点才注入 check-in);否则判定,达成 / 判不可能 → 清目标、`Finish`;未达成 → 轮数 +1、记理由、注入 `Stop hook feedback:` 续跑提示;没判成(超时 / 出错 / 无模型)→ **不算一轮、不清目标**。
  **空转刹车**:本该续跑,但 `StoppedTurnStreak ≥ 8`(连续 8 个回合没有调用工具,工具一执行就清零)→ `Finish` + `LoopNotice("goal.warn.idleStreak")`,目标留着;达成 / 不可能不受它拦。裁决属于**被判定的那个身份**:判定期间目标被换掉(哪怕条件一字不改)、被清掉、或调用方中断,那份裁决作废。
- **空闲唤醒**:推迟期间的检查间隔是**基数 30 分钟(`GoalOptions.CheckinInterval`,下限 60 秒)、第 n 次 ×2^min(n,2)**(30 / 60 / 120 分钟),最多 3 次空闲检查,第 3 次带「不再自动唤醒」的尾巴,到顶后只挂空定时器;投递失败按退避重试且**不消耗**额度;
  用户开口那一轮(`BindRun(userMessage: true)`)把空闲计数归零。原稿「60 秒起指数退避」按 2.x 的真实行为改正:60 秒只是下限。检查和 kickoff 都经 `RunRegistry.DeliverInternal`(Run 在跑进引导队列,会话在收尾排到之后,空闲起一个内部 Run,档位取 Run 的发送档位快照),2.x 的 `wakeGoal` launcher / 「订阅着那个 Run 的窗口」整套消失。
- **标记**:转录里助手消息上的 `GoalStatusPart`(`Set / Origin / Iterations / DurationMs / Tokens / Id / CreatedAt` 是追加的可选字段,2.x 写的旧标记照读)。写标记是**带外**的「比较并替换」(`IGoalTranscript` → Storage 的 `ReplacePartsIfUnchanged`,只往原样的 parts 文本后面追加一个块,不重新编码别的块;冲突重读重试 3 次),挂在最后一条助手消息上、**不新增消息**;
  还没有助手消息时先排队,下一个回合末 / Run 结束时落上去,**只落一次**。3.0 的会话层逐条提交消息、没有 Run 末整段回写历史,所以 2.x 的 `mergeGoalStatusHistory` 这一步不存在。只有用户清除盖「已清除」标记(它必须活过收尾,否则被清掉的目标下次打开会自己回来);关会话 / 退出不盖,活跃标记留给下次启动。
- **恢复**:`session_start` 时只读最后一条带标记的助手消息(`LastGoalStatus`),最后一条是 met / failed / cleared 都不恢复;恢复只重建状态,**不重开 Run**、不 kickoff、不挂定时器。
- **中止**:`IRunExtension.OnAbortRequested` 立刻 `Pause`(轮换目标 id、停定时器,条件保留给下一轮);Run 以非 Done 结束同样暂停,Done 则 `Release`(推迟态要留着上下文和定时器)。
- **模型自提目标**(`ProposeGoal` + `GoalService.Propose`):五道门(顶层 Run 且能问人 / 交互式会话 / Run 还在跑没被要求中止 / 不在计划模式 / 设置不是 disabled / 没有待决提案)快照里判一次、`ExecuteAsync` 进门再核一遍;**只等派发不等人**(`GoalProposal` 交互 `SurvivesNormalRunEnd`,Run 收尾后仍可批准);
  人的决定从不变成工具结果;批准条会过期(Run 被中止、进了计划模式、设置被关、版本号前进、会话没了都不设);用户手动设定 / 清除撤掉待决的提案。`auto` 且模型断言「用户原话如此」才直接设立,`alwaysAsk` 下强制走审批。
- 目标只对**主 Run** 生效:子 Run 和定时任务的 Run(`RunOrigin.Scheduled`)不参与。

### 目标与待办的不变量与测试对照(`tests/Ncw.Agent.Planning.Tests`、`tests/Ncw.Core.Tests`)

| # | ★ 不变量 | 测试 |
|---|---|---|
| TD1 | 当前清单只从**成功**调用派生:被工具拒绝的(循环先提交后校验,拒绝的清单原样躺在转录里)、还没有回执的(中断时的孤儿)、坏形状(来自模型,不抛)、超上限都不算;半份清单好过没有 | `Ncw.Core.Tests.TodoDerivationTests.Latest_ignores_a_call_the_tool_rejected_…`、`Latest_ignores_a_call_with_no_result_yet`、`A_bad_shape_from_the_model_…`、`Good_items_stay_and_bad_ones_are_skipped_…`、`Items_over_the_cap_are_cut_…` |
| TD2 | 外部名按转录里调用自己带的名字查,对不上时**静默为空**(失败模式本身写成用例);「上一份」取不到是「不知道」不是「全丢了」 | `TodoDerivationTests.Latest_with_a_name_that_does_not_match_…`、`History_lookup_groups_by_the_name_…`;`TodoWriteToolTests.A_transcript_whose_calls_carry_a_different_name_…`、`The_call_being_executed_does_not_count_as_its_own_previous_list` |
| TD3 | 增量:不知道上一份 → 全空(不是整份新增);按内容一对一消耗;顺序 / `activeForm` 措辞不算变化;从完成退回不计数 | `TodoDerivationTests.Plan_*`、`History_lookup_*` |
| TD4 | `TodoWrite` 无状态(第二次调用不知第一次);空清单被拒并说清为什么;两个 `in_progress` 被拒并点名;schema(≤ 40 项、`activeForm` 必填、status 三档);全绿与未完成给两条不同回执;描述里的硬规则逐句 | `TodoWriteToolTests.*`(`todo.test.ts` 全部用例) |
| TD5 | `TodoWrite` 标只读(计划模式下的权限闸门不为它弹审批) | `TodoWriteToolTests.Is_read_only_…`;`ArchitectureTests.Every_tool_declares_its_traits_…` |
| TD6 | 补报额度每 Run 一次(不是每份清单一次);只认本 Run 的清单;不吃额度的情形(工具本回合没下发 / 中断 / 子 Run / 让路);全绿 / 无成功清单不提醒;清单文字是不可信数据;提醒不改转录一个字节 | `TodoReconcilerTests.*`(`todo-derive.test.ts` 里 `createTodoReconciler` 全部用例)、`TodoExtensionTests.*` |
| TD7 | 让路:目标 / 活动计划 / 后台子代理在时不补报且不吃额度;目标判成 met 的强制收尾不被覆盖 | `TodoExtensionTests.Yielding_to_a_goal_…`;`EndToEndTests.While_a_goal_is_active_the_checklist_reminder_yields_…`、`While_a_plan_is_active_the_checklist_is_not_nagged` |
| TD8 | 状态块里的待办每 Run 算一次;压缩后附件重附最新清单;子代理自己的会话天然看不到父清单 | `TodoExtensionTests.The_state_block_gets_the_latest_successful_list_…`、`The_snapshot_is_per_run_…`、`Compaction_reattaches_the_latest_list_…`、`A_subagent_has_its_own_session_…` |
| TD9 | 端到端对账:写清单 → 想停 → 补报 → 收尾只提醒一次;提醒后又写未完成项仍能收尾;下一句提问有新额度;拒绝的清单 / 上游错误 / 用户中断都不追加补报请求 | `EndToEndTests.TodoWrite_is_reconciled_once_…`、`The_reminder_quota_is_one_per_run_…`、`A_list_the_tool_rejected_…`、`A_provider_error_…`、`A_user_abort_…` |
| GL1 | 条件规范化(零宽 / 控制字符剥掉后再判空)、清除词整条参数才算、超长拒绝不截断、检查间隔 30 / 60 / 120 与 60 秒下限 | `Ncw.Core.Tests.GoalConditionsTests.*`(`goal.test.ts` 的规范化 / 清除词 / `parseGoalCommand`) |
| GL2 | 判定解析:垃圾 / 缺字段 / 数组 = `Skipped` 不是 `NotMet`(工具故障不算在用户目标头上);围栏和前后文字容错 | `GoalPureTests.*`(`goal.test.ts` 的 `parseGoalVerdict`) |
| GL3 | 判定调用:无工具 / 禁思考 / 1024 输出 / system 逐字;模型与供应商**成对**回落;只有完整 `end_turn` 才算回复;**中断之后绝不 `Met`**;一次判定**一个墙钟**(重试不翻倍);重试预算真的更小;已中断不发请求 | `GoalEvaluatorTests.*`(`goal-evaluate.test.ts` 全部用例) |
| GL4 | 转录渲染:按回合边界裁、裁掉的换显式前缀、最新一回合自己装不下则整份不发、`thinking` / `goal_status` 不进、`tool_result` 带 callId 与错误状态、按估算器而不是 chars/4 | `GoalEvaluatorTests.The_cut_part_becomes_an_explicit_prefix_…`、`When_the_latest_turn_alone_does_not_fit_…` 等 |
| GL5 | 标记:挂在最后一条助手消息上不新增消息;没有助手消息时排队且只落一次;冲突重试;原样保留不认识的字段;codec 往返全部字段、读得了 2.x 的旧标记 | `GoalServiceTests.Set_and_clear_markers_…`、`With_no_assistant_message_yet_…`;`GoalPureTests.Appending_a_marker_…`、`The_codec_round_trips_…`、`A_stale_replace_is_retried_…` |
| GL6 | 恢复:只认最后一条、三个终态(met / failed / cleared)都不恢复、只第一次读库且只读最后一条带标记的消息、不 kickoff 不挂定时器;用户清掉的目标重启后不复活 | `GoalPureTests`(`goal-restore.test.ts` 全部用例)、`GoalServiceTests.Restore_*`、`A_goal_the_user_cleared_does_not_come_back_…`;`EndToEndTests.A_goal_that_survives_a_restart_…`、`A_goal_the_user_cleared_before_the_next_run_…` |
| GL7 | 裁决属于被判定的身份:判定期间目标被换掉(同条件)/ 清掉 / 暂停 / 调用方中断 → 作废,旧判定被取消 | `GoalServiceTests.A_goal_replaced_during_the_evaluation_…`、`…cleared_by_the_user_during…`、`An_abort_during_the_evaluation_…`、`A_pause_during_the_evaluation_…` |
| GL8 | `Met` / `Impossible` 收尾并清目标(空转刹车拦不住);`NotMet` 轮数 +1 + 续跑提示;没判成不算一轮不清目标 | `GoalServiceTests.Not_met_adds_a_round_…`、`Met_finishes_and_clears_…`、`Impossible_also_finishes_…`、`A_skipped_judgement_…` |
| GL9 | **空转刹车**:连续 8 个回合无工具 → `Finish` + 警告、目标留着、不会自己再跑;有工具调用则清零、不被刹 | `GoalServiceTests.Hitting_the_idle_streak_cap_…`、`One_turn_below_the_cap_still_continues`;`EndToEndTests.The_idle_streak_brake_…`、`A_tool_call_resets_the_stall_count_…` |
| GL10 | 后台推迟:不叫判定器、不计轮;空闲检查 30 / 60 / 120 分钟、第三次带尾巴、到顶不再注入;投递失败退避且不吃额度;清除 / 暂停 / 会话没了清定时器;用户开口归零;后台结束后回到判定 | `GoalServiceTests.Background_work_defers_…`、`Idle_injections_come_at_30_60_and_120_minutes_…`、`A_failed_delivery_backs_off_…`、`Clearing_the_goal_takes_the_timer_with_it_…`、`When_the_session_is_gone_…`、`A_manual_pause_…`、`The_turn_the_user_speaks_in_…`、`When_the_background_work_has_finished_…` |
| GL11 | 中止暂停目标(轮换 id、不清条件)、下一轮接上 | `EndToEndTests.A_user_abort_pauses_the_goal_…`;`GoalServiceTests.A_pause_during_the_evaluation_…` |
| GL12 | 模型自提:五道门(快照 + 进门各一次)、**只等派发不等人**、人的决定不进工具结果(结果恰好四个字段)、批准条过期、提案不占住 Run、用户设定 / 清除撤提案、`alwaysAsk` 强制审批、条件规范化 / 超长拒绝不截断 | `GoalProposalTests.*`、`ProposeGoalToolTests.*`(`goal-runtime.test.ts` 的 `goalProposalsFor` 与 `goal-tool.test.ts` 全部用例);`EndToEndTests.The_model_can_propose_a_goal_…`、`A_proposal_that_needs_approval_does_not_hold_the_run_…`、`While_the_plan_workflow_is_on_…` |
| GL13 | 端到端:目标续跑直到判定器确认(未达成 → 主模型真的又跑一轮、带续跑注入 → 达成收尾清目标);标记依次是设定 / 未达成 / 达成,不新增消息;不会被重载捞回来 | `EndToEndTests.A_goal_keeps_the_agent_working_until_the_evaluator_confirms_it` |
| GL14 | 目标版本号:用户设定 / 清除前进,自动完成不前进;变更通知全量、订阅者异常不连累设定;token 从设定那一刻算 | `GoalServiceTests.A_user_set_or_clear_advances_the_revision_…`、`Changes_are_broadcast_in_full_…`、`A_throwing_subscriber_…`、`Tokens_count_from_the_moment_…` |
| GL15 | 用户设定 / 清除(`goal.set / goal.clear`):校验空 / 超长且不改任何东西、kickoff 经内部输入交付并带目标 id、清除先恢复 | `GoalServiceTests.A_user_set_*`、`A_user_clear_restores_first_…` |
| GL16 | 接线(真 `AgentLoop` / `RunRegistry`):判定模型与供应商在 Run 开始时**成对冻结**(同一个 Run 的两次判定都用冻结的那一对,下一个 Run 才换);判定途中用户清掉目标 → 不落 `met` 章、「已清除」标记活过收尾、目标不复活;会话名下有后台子 Run(旧父 Run 派出去的)→ 整个推迟、判定器一次不叫、目标留着、待办也不催 | `EndToEndTests.The_judge_model_is_frozen_with_its_provider_at_run_start_…`、`A_goal_cleared_while_the_judge_is_still_answering_…`、`A_background_subagent_in_the_session_defers_the_evaluation_…`、`With_no_goal_but_a_background_subagent_running_…` |
| PK1 | 依赖规则:Planning 不引用 Protocol / Host / 客户端 / Avalonia,下层不引用它;不直接碰文件和进程;不用反射序列化;不产出界面文字;三个扩展的 Order 是「目标先于待办」;每个功能可单独关闭 | `ArchitectureTests.*` |

## 8. 定时任务与后台 Shell

- 定时任务:创建 `origin = scheduled` 的会话,固定权限模式(`full`,所以协议里**所有写方法都是 owner**),**走 `Launch`**(`RunOrigin.Scheduled`);不启用目标;限制调用链深度。**已实现**(`Ncw.Agent.Services/Schedules`:`ScheduleCalculator`、`ScheduledTaskService`、`Scheduler`、`AgentScheduling`;Host 的 `ScheduledRunHost` 经 `RunRegistry` 起 Run;时钟是注入的 `TimeProvider`)。
  - **规则**沿用 2.x:`once` / `daily` / `weekly` + 重复窗口(开始时间到结束时间、每隔 N 分钟),IANA 时区,本地 → UTC 两步换算(跨夏令时),定时器延迟上限按 32 位毫秒封顶后续接。**不是 cron**。
  - **错过的触发**(2.x:启动时按策略补跑)——3.0 偏离:启动对账时**一次性跳过全部**错过的触发,只记一条 `skipped` / `scheduled.missed`,不补发一串(设备睡了一晚醒来不应该一口气跑十次)。上一次还在跑时又到点:记 `skipped` / `scheduled.overlap`(2.x 是静默丢弃)。一次性任务触发后关闭。
  - `schedules` 主题是全量类型,运行状态由权威广播给出,终态之后不再是「运行中」;断线标 `unknown`。
  - **未做**:模型侧的定时任务工具(`AgentScheduling` 桥接已移植、工具还没挂上);设备断开期间的调度由设备自己负责,不依赖控制端在线。
- 后台 Shell:注册表按 Run 记账;Run 结束不杀后台 Shell,但会话删除时清理;`BashOutput` 增量读取。
  **实现注**:Shell 注册表是工具层的 `BackgroundShells`(`IShellRegistry`),Runs 引用 Tools 只为这一件事(依赖方向合法:Runs 在 Tools 之上)。规则按 2.x:**Run 结束 / 中止都不杀后台 Shell**(那正是后台的意义;前台命令的句柄由工具在 `finally` 里释放,
  中止时同一个取消令牌掐掉它);应用退出(`ShutdownAsync`:先中止所有 Run 并等它们收尾,冲掉队列存档,再 `BackgroundShells.ShutdownAsync`)和会话删除(`ForgetSessionsAsync`:中止并等待、按 `runs` 表找出这些会话的 Run id、只杀 `RunId` 属于它们的后台 Shell、丢掉内存里的排队状态)才清理。
  独立进程组不随主进程消失,不杀的话重启后端口还占着,界面上再也找不到是谁占的。

## 9. 不变量清单

勾选 = 已有测试(`tests/Ncw.Agent.Runs.Tests`,除非另注;对照见 §11)。

- [x] RunId 由客户端生成;先订阅再启动;子 Run 继承订阅后再启动
- [x] 下一个 Run 等 `run_settled`;看门狗 5 秒;`Abandoned` 后丢弃事件、`settle` 只收一次
- [x] 回收要求没有运行中的后代
- [x] 并发门限按**运行中**的子 Run 计,不按累计集合计
- [x] 工具作用域事件(进度、`subagent_*`)永远排在对应 `tool_start` 之后;排位不被 `tool_end` 裁掉(O32);`run_settled` 不蕴含子 Run 已收口,收口靠权威广播(§2 实现注)
- [x] 停机收尾必达:在途会话以取消收场不能跳过「中止 Run / 杀终端与后台 Shell / 关库」(`Ncw.Agent.Host.Tests.TerminalTests`,见并发不稳定测试一节 of `docs/parity.md`)
- [x] 订阅顺序:订阅 + 快照原子(无漏无重)
- [x] `activeRuns` 是全量权威广播,首帧和后续同一个函数
- [x] 子代理不能提问;未知 Agent 不回退;权限只收窄(`tests/Ncw.Agent.Subagents.Tests`,见 §11 O21–O23)
- [x] 子代理状态单调,合并 / 归约函数与客户端共用(`Ncw.Core.Domain.SubagentView`:`Merge` / `MergeStatus` / `ApplyStart` / `ApplyUpdate` / `ApplyEnd`;回执写库经同一个 `MergeStatus`,见 §11 O24;Run 自己的状态单调见 O8)
- [x] 子代理并发队列:满了排队而不是失败、`Start` 同步内联(一个空位只给一个等待者)、公平(同父 Run 有排队项不走快速通道)、禁用(全局上限 0)当场拒绝、死锁每轮重判、溢出拒绝、父 Run 中止**和**结束两条路径都取消等待者(O25)
- [x] 父 Run 中止级联到运行中的子 Run 和排队的派发,不留等待者、不留孤儿子 Run;后台子代理不随父 Run 正常结束而结束(O26)
- [x] 后台子代理报告:发全文、只汇报一次、中止的不自动汇报、没有档位则 blocked、重启后可手动汇报、回执状态单调(O27)
- [x] 深度上限 2(第三层被拒且不建会话);子会话的 `parent_session_id` 是真实父关系(O28)
- [x] 子代理定义加载永不抛、整条作废的三种情形(缺描述 / 空工具表 / 非法 permissionMode)、软链逃逸被拒、内建永远在且排最前(O29)
- [x] 所有入口都走 `Launch`
- [x] Run 状态单调:终态压过运行中,重放的旧事件不能改回运行中;设备断开是 `unknown`(`Ncw.Core.Tests.RunLifecycleTests`)
- [x] 排队:先落盘再确认、写失败回滚、送达以同 id 的 `message_end` 为准、回执挡住重发、中止 / 出错不续跑、冷启动不自动跑
- [x] 内部输入不被用户侧的整份替换抹掉;没确认就收回
- [x] 后台 Shell 不随 Run 结束而结束;退出和会话删除才清理
- [x] 计划模式:按会话存、围栏同源(工具层 / 权限层)、批准 / 拒绝 `Terminate`、状态块带路径(`tests/Ncw.Agent.Planning.Tests`,见 §6 的 PM1–PM10)
- [x] 待办:只从成功调用派生(Core 一份纯函数)、无状态、补报每 Run 一次(见 §7 的 TD1–TD9)
- [x] 目标:裁决属于被判定的身份、空转刹车、后台推迟与空闲唤醒、中止暂停、提案只等派发(见 §7 的 GL1–GL15)

## 10. 扩展点(给还没实现的部分留的接缝)

| 接缝 | 谁用 | 说明 |
|---|---|---|
| `RunLaunch.ParentRunId` / `ParentSessionId` / `RunOrigin.Subagent` | 子代理层(`Task` 工具、子代理队列) | 子 Run 登记到父 Run 的 `Children`、继承父 Run 的订阅;广播的 `subagents` 半边由 Core 的生成函数从这两个字段派生(父 Run 被回收时退回 `ParentSessionId`) |
| `RunHandle.Background`、`ActiveBackgroundChildrenOfSession` | 后台子代理 | 跨父 Run 找父会话名下的后台子 Run |
| `IRunExtension.OnCreated / EnrichToolContext / OnAbortRequested / OnEnded / OnSettled` | 交互闸门(`OpenRun` / `End`)、子代理(`SpawnSubagent` 能力)、目标(中止时暂停)、定时任务 | 所有回调的异常都被吞掉并记诊断,不影响 Run 的生命周期;`EnrichToolContext` 依次套在会话层的 `SessionToolContext` 外面 |
| `RunRegistry.Post(runId, event)` | 交互闸门的 `RunEvent`、子代理的 `subagent_*` | 往 Run 的事件流追加事件、分配 `seq`;终态之后返回 0 |
| `RunRegistry.NotifyInteractionsChanged()`、`IInteractionView` | 交互闸门 | 交互发起 / 处理 → 重播 `activeRuns`;数待处理交互、钉住回收 |
| `RunRegistry.DeliverInternal(sessionId, InternalInput)` | 后台子代理报告、目标检查 | 见 §3 实现注 |
| `RunRegistry.AcquireOperation / Activity / ForceSettleSessions / ForgetSessionsAsync` | 手动压缩、会话删除 | 见 §1、§8 实现注 |
| `IRunRequestFactory`、`ISessionDirectory`、`IRunDriver`、`IQueueStore` | Host(装配)、测试 | 请求由 Host 装配(设置快照、模式提示词);会话对象按会话缓存;驱动可替换(测看门狗);队列存档可包一层注入失败 |
| `RunOrigin.Workflow` / `WorkflowNode`、`TodoOptions.YieldTo` | 工作流编排(`Ncw.Agent.Workflows`,设计见 [12 工作流](../12-workflows.md)) | 确定性与智能体混合的 DAG 编排:复用 `RunRegistry`、独立子会话、并发队列、权限收窄与 `InteractionGate`,并在运行期间让待办补报让路 |

## 11. 不变量与测试对照

| # | 不变量(2.x ★ 与 3.0 新增) | 测试(`tests/Ncw.Agent.Runs.Tests`) |
|---|---|---|
| O1 | RunId 由客户端生成;重复 RunId 拒绝;订阅在启动前绑定;子 Run 继承订阅后再启动(首个事件不丢);后代随 `attach` 一起订阅 | `LifecycleTests.A_duplicate_run_id_is_refused_…`;`SubscriptionTests.A_subscription_bound_at_launch_…`、`A_child_run_inherits_the_parents_subscriptions_…`、`Attach_includes_all_descendants_…` |
| O2 | `run_end` 早于落库:互斥一直攥到 `run_settled` 之后;`run_settled` 在落库、释放互斥之后才发;会话层自己的 `run_settled` 被拦下;窗口里的用户发送被拒、排队不起新 Run | `LifecycleTests.Run_settled_is_sent_only_after_…`、`Between_run_end_and_run_settled_…`、`A_run_goes_through_the_real_session_…`;`QueueTests.In_the_window_between_run_end_and_run_settled_…`、`The_follow_up_starts_only_after_run_settled_…`;`InternalInputTests.A_session_that_is_settling_defers_it_…` |
| O3 | 看门狗:中止后 5 秒仍没有 `run_end` → 强制 `Aborted` 并 settle、标 `Abandoned`;迟到事件丢弃、`settle` 只收一次、别人的锁不被放;按时停下的不受影响;已有 `run_end` 的(正在落库)不强收 | `LifecycleTests.A_driver_that_ignores_cancellation_is_ended_by_the_watchdog_…`、`A_run_that_stops_in_time_…`、`A_run_whose_run_end_is_already_in_the_log_…`;`RegistryTests.Force_settle_…` |
| O4 | 回收:已 settle、无订阅者、没有运行中的后代(往下查到底)、没有挂起的交互、TTL / 总预算 | `RegistryTests.A_finished_run_stays_for_the_ttl_…`、`The_periodic_timer_reaps_…`、`Over_the_total_budget_…`、`A_run_is_not_reaped_while_the_session_mutex_…`、`A_run_with_a_running_descendant_anywhere_below_it_…`;`BroadcastTests.A_listener_does_not_pin_a_finished_run_…` |
| O5 | 并发门限按**运行中**的子 Run 计,不按累计集合计;全局子 Run 集合;后台子 Run 按父会话找 | `RegistryTests.Running_children_are_counted_…`、`Running_in_a_workspace_…`、`Background_children_are_found_…` |
| O6 | 订阅 + 快照原子:不漏不重(并发发射下);无人订阅时日志仍是真源;游标(Fresh / Continued / Lost) | `SubscriptionTests.Attach_is_atomic_with_emission_…`、`Nobody_listening_costs_nothing_…`、`Attach_after_settle_…`;`EventLogTests.A_cursor_still_in_the_log_…`、`Lossless_trimming_never_makes_a_cursor_lost` |
| O7 | `activeRuns` 全量权威广播:起止都播、没订阅过也收得到、子代理在自己那半边(父会话)、待处理交互含后代、交互变化重播、首帧与广播同一函数、`Rev` 严格递增(并发)、运行期间定期重播、订阅者异常不连累别人 | `BroadcastTests.*`;`Ncw.Core.Tests.RunLifecycleTests`(生成函数:顶层 / 子代理半边、父 Run 已回收、待处理含后代、环、排序) |
| O8 | 状态单调:终态之后的事件丢弃且不推进 `seq`、第二个 `run_end` 不覆盖、重放的旧 `run_start` 不能复活、`run_settled` 恰好一次;客户端丢了结束事件靠广播收口;断线是 `unknown` 不判结束 | `EventLogTests.Events_after_the_terminal_state_…`、`A_second_run_end_…`、`A_replayed_old_run_start_…`、`Run_settled_follows_…`、`Finish_without_a_run_start_…`;`RobustnessTests.A_replayed_run_start_posted_after_…`;`BroadcastTests.A_client_that_lost_run_end_…`、`A_disconnected_device_…`;`Ncw.Core.Tests.RunLifecycleTests.Merge_…` |
| O9 | 所有入口都走 `Launch`;用户发送忙时进队列;开放网关忙时 409 不排队 | `QueueTests.Sending_while_a_run_is_active_…`;`RobustnessTests.The_open_gateway_…`;`LifecycleTests.Between_run_end_and_run_settled_…`(窗口里 `SendAsync` 被拒) |
| O10 | 日志裁剪无损(消息增量、工具进度)、结构性事件永不裁;硬上限有损且最新一条永远留着、序号照常递增 | `EventLogTests.Message_end_drops_…`、`Trimming_is_lossless_…`、`Trimming_only_reaches_back_…`、`A_discarded_message_…`、`Structural_events_…`、`Since_filters_by_seq_…`、`Tool_end_clears_progress_…`、`Past_the_byte_budget_…`、`The_newest_event_is_always_kept_…`、`The_entry_cap_…` |
| O11 | 事件泵:只攒增量、结构性事件立即送且保序、批内 / 批间 `seq` 递增、攒满即送;积压溢出:增量整批丢、结构性事件 → `Resync` 并结束订阅;接收方异常不影响 Run | `SubscriptionTests.Text_deltas_are_held_…`、`A_structural_event_is_sent_at_once_…`、`A_burst_larger_than_the_batch_cap_…`、`Seq_inside_and_across_batches_…`、`When_the_listener_cannot_take_a_batch_…`、`A_structural_event_that_cannot_be_delivered_…`、`A_listener_that_throws_…` |
| O12 | 中止:取消令牌贯通(工具、交互闸门挂同一个令牌),每个 `tool_call` 仍配得上 `tool_result`;级联 / 不级联;幂等;不存在的 Run 不抛 | `LifecycleTests.Aborting_while_a_tool_runs_…`、`Abort_cascades_to_the_whole_subtree_…`、`Aborting_a_run_that_does_not_exist_…`;`RobustnessTests.Extensions_are_told_when_an_abort_is_requested_…` |
| O13 | 会话互斥:同会话拒绝、不同会话并行不串台;操作与 Run 互斥、旧释放函数不清新锁;删除前活体检查分类 | `LifecycleTests.One_session_cannot_run_two_runs_…`、`Concurrent_runs_in_different_sessions_…`;`RegistryTests.An_operation_holds_the_session_exclusively_…`、`Activity_classifies_…`;`RobustnessTests.Many_concurrent_runs_…` |
| O14 | 排队 · 续跑时机:`run_settled` 之后;没有订阅者也会续;档位逐条冻结;附件不丢;中止 / 出错不续跑、手动继续同一条路径;冷启动不自动跑、重启恢复;启动被拒原样放回 | `QueueTests.The_follow_up_starts_only_after_run_settled_…`、`Without_any_subscription_…`、`The_follow_up_reuses_the_options_…`、`Attachments_come_back_…`、`Stop_does_not_continue_the_queue_…`、`A_cold_start_restores_the_queue_…`、`A_follow_up_that_cannot_start_…` |
| O15 | 排队 · 插话:toggle、再引入排队尾、时间戳严格递增、空闲点插话 = 立即发送且只发 promoted、多条按引入顺序合并、运行中取走即消费并插在工具结果之后(id = 队列项 id)、同 id 的 `message_end` 才算送达 | `QueueTests.Promote_toggles_…`、`Promoted_ids_get_distinct_…`、`Promoting_in_an_idle_session_…`、`Several_promoted_items_merge_…`、`A_promoted_item_is_steered_into_the_running_run_…`;`QueueBatchTests.*` |
| O16 | 排队 · 回执:取走未确认的退回队列;提交过的(回执在)绝不重发;重启后旧队列行不推翻已提交的消息 | `QueueTests.A_steering_message_that_was_taken_but_never_confirmed_…`、`A_steering_message_that_was_committed_but_whose_event_never_arrived_…`、`After_a_restart_an_old_queue_row_…` |
| O17 | 排队 · 先落盘再确认:入队 / 编辑 / 删除 / 引入 / 改档位 / 撤回写失败全部回滚、不广播、不 accepted;移交写失败不启动不丢;放回写失败抛出且内存仍在;广播失败不算失败;`Rev` 全局单调;软上限 20、同 id 拒绝;存档读失败 / 结构不对按空;草稿和未知字段保留 | `QueueTests.A_failed_enqueue_…`、`A_failed_edit_drop_promote_or_retag_…`、`A_follow_up_whose_hand_over_…`、`When_a_failed_start_cannot_be_put_back_…`、`A_broadcast_that_throws_…`、`The_revision_is_global_…`、`The_queue_has_a_soft_cap_…`、`An_unreadable_saved_queue_…`、`A_saved_queue_with_a_wrong_structure_…`、`The_draft_and_unknown_fields_…`、`Edit_changes_…`、`Take_hands_…`、`Retagging_…`、`File_references_…` |
| O18 | 内部输入:空闲起 Run(沿用上次档位)、运行中进引导队列且 `Done` 等同 id 的 `message_end`、用户侧替换抹不掉、收尾中排到之后且在排队消息之前、中止后退回、正常结束没消费的重投、已确认的不重投、启动被拒无残留、没档位则失败、有界重试 | `InternalInputTests.*` |
| O19 | 后台 Shell 不随 Run 结束 / 中止而结束;退出杀光;会话删除只杀属于它的 | `ShellTests.*` |
| O20 | 依赖规则:编排层不引用 Protocol / Avalonia / 客户端 / Host / 一方扩展,不用反射 | `ArchitectureTests.*` |

| O21 | 子代理不能提问(三道闸):快照里没有 `AskUserQuestion` / `ExitPlanMode`(恰好这两个),不提供 `Interact`(作用域 `depth > 0`,发起即抛),子代理的审批请求当场拒绝(拒绝进转录、给出可添加的规则、闸门里没有挂起项、Run 照常跑完) | `SubagentRunTests.Invariant_a_subagent_request_for_approval_is_refused_on_the_spot_…`、`The_child_has_no_way_to_open_an_interaction_…`、`Invariant_the_childs_tool_list_has_no_interactive_tools_…`、`The_interactive_tool_set_is_exactly_the_two_…` |
| O22 | 未知 Agent 名称 → 拒绝并列出可用清单,不回退到通用代理(原样转交、不加前缀、没有建任何 Run / 会话);被用户关掉的子代理既不在清单也不能派 | `SubagentRunTests.Invariant_an_unknown_agent_name_is_refused_…`、`A_disabled_agent_cannot_be_dispatched_…`;`TaskToolTests.Invariant_a_refusal_reason_is_forwarded_verbatim_…`、`Invariant_the_name_is_not_corrected_…`;`AgentRegistryTests.Get_looks_up_by_name_…` |
| O23 | 权限只收窄:`min(父实时值, 定义值)`、父中途调低子代理立刻跟着降、不能被调得比定义更宽、省略定义 = 取父的档位 | `SubagentRunTests.Invariant_the_permission_mode_is_the_min_of_…`、`A_definition_without_a_permission_mode_takes_the_parents_mode_…` |
| O24 | 子代理状态单调:终态压过运行中且不论哪一份更晚到、重放的 `subagent_start` 不复活终态卡片、终态之后的遥测忽略、第一个终态胜出、`reported` 不被退回、回执写库同一规则、更新早于消息落库不丢(`run_settled` 后补写) | `Ncw.Core.Tests.SubagentMonotonicTests.*`(移植自 `subagent-monotonic.test.ts`、`subagent-view.test.ts`)、`SubagentViewTests`;`ReceiptStoreTests.*`;`SubagentRunTests.The_childs_tool_use_is_mirrored_…`(Agent 发出的事件喂给 Core 归约) |
| O25 | 并发队列(`subagent-queue.test.ts` 全部用例逐条移植 + 线程安全):满了排队不失败且按序启动、队头卡住不挡别的会话、全局 0 当场拒绝且不建定时器、死锁(含自己也算卡住的、后来才形成的)、后台等待者不算卡住、无墙钟上限、溢出(每父 / 全局)、中止与结束两条路径都唤醒、运行期调高上限下一轮生效、Reset 结清、一个空位只给一个等待者、200 路并发不超上限、`Start` 抛异常只失败那一次派发 | `SubagentQueueTests.*`;`SubagentRunTests.With_a_limit_of_one_per_parent_three_dispatches_…`、`Invariant_a_global_limit_of_zero_…`、`A_background_dispatch_into_a_full_pool_…`、`Raising_the_limit_while_queued_…` |
| O26 | 父 Run 中止:级联到运行中的子 Run、排队的派发被取消(不留等待者、不留运行中的子 Run)、每个 `tool_call` 仍配得上 `tool_result`;父 Run 正常结束不杀后台子代理,之后的 `subagent_end` 到不了已结束的父 Run | `SubagentRunTests.Invariant_aborting_the_parent_cascades_…`、`Aborting_the_parent_while_the_child_is_stuck_in_the_queue_…`、`A_parent_that_ends_normally_does_not_kill_its_background_child_…` |
| O27 | 后台子代理报告:立即回话(区分已排队 / 已开始)、结束后全文以内部消息回父会话(空闲起内部 Run / 运行中进引导队列)且只一次、中止的不自动汇报(回执 pending)、手动「处理」同一路径、重启后只有库里回执时可汇报、无档位 → blocked、前台可转后台(结束时按后台收尾) | `SubagentRunTests.A_background_subagent_returns_at_once_…`、`When_the_parent_run_is_still_going_…`、`Invariant_an_aborted_background_subagent_is_not_reported_automatically_…`、`Invariant_with_no_send_options_at_all_…`、`After_a_restart_pressing_handle_…`、`A_foreground_subagent_can_be_moved_to_the_background_…` |
| O28 | 深度上限 2:depth 0 → 1 → 2 都派得出、第三层的 `Task` 被拒(说明为什么、改做什么)且不建 Run / 会话;子会话 `parent_session_id` 是逐层真实父关系,删父会话连带回收;子转录不进侧边栏 / 列表 | `SubagentRunTests.Invariant_the_nesting_limit_is_two_levels_…`、`A_foreground_subagent_runs_in_its_own_session_…`、`Deleting_the_parent_session_takes_the_subagent_transcripts_…`;`TaskToolTests.Invariant_at_the_depth_limit_…` |
| O29 | 定义加载(`agent-load.test.ts` 逐条移植,真临时目录):基本形状、同名项目胜出、覆盖内建 / 覆盖失败留内建、缺 description / 空正文 / 非法名字作废、软链逃逸拒绝、`tools:` 三种结局、`permissionMode:` 非法作废、控制字符消毒与限长、一个坏文件不拖垮整次扫描、环境断开不吞;注册表出厂非空、内建排最前且 `general-purpose` 第一;`frontmatter.test.ts` 逐条移植 | `AgentLoaderTests.*`、`AgentRegistryTests.*`、`FrontmatterTests.*`、`SubagentThinkingTests.*`(`subagent-thinking.test.ts`) |
| O30 | `Task` 工具(`task-tool.test.ts` 逐条移植):`readOnly: false`、参数名照搬 CC、description 跟着清单、结局映射、深度闸门不调启动器、无启动器给人话、后台 / 排队 / 转后台的话术 | `TaskToolTests.*` |
| O31 | 依赖规则:子代理层不引用 Protocol / Avalonia / 客户端 / Host,不用反射,不直接碰文件和进程,字符串字面量里没有中文 | `ArchitectureTests.*` |
| O32 | 工具作用域事件顺序与排位保留:进度 / `subagent_*` 先于 `tool_start` 到达时暂存、`tool_start` 之后紧跟着落(`seq` 连续递增),暂存的在 Run 结束时丢弃并记一笔;`tool_end` 裁进度时保留子代理派发的排位,续传游标 / 快照里都读得到 | `EventLogTests.A_tool_scoped_event_that_arrives_before_its_tool_start_…`、`Events_held_for_a_tool_start_that_never_comes_…`、`Tool_end_keeps_the_queue_position_of_a_dispatch_…`;`Ncw.Integration.Tests.OrchestrationLinkTests.A_dispatch_waiting_for_a_slot_…` |
| O33 | `Abandoned` 之后会话层不再写库:放弃前已落库的在、放弃后的消息提交 / 孤儿修复 / 压缩边界一条都没有;放弃后不再发摘要请求 / 上游请求;`run_end` / `run_settled` / `after_compact` 扩展通知不再触发,诊断带 `session.write.skippedAbandoned`;放行驱动后会话可用、下一个 Run 照常落库;`run_settled` 只一次、事件仍丢弃;检查回调抛异常按存活处理 | `AbandonedRunTests.*`(会话层,逐个落库点);`AbandonedWriteTests.The_watchdog_abandons_a_driver_stuck_in_a_tool_…`、`A_forced_settle_before_deleting_the_session_…`(手动时钟 + 真看门狗,去掉接线后这两条会失败) |

## 12. 相对原稿的实现偏离(汇总)

1. 编排层单独成项目 `Ncw.Agent.Runs`(命名空间仍是 `Ncw.Agent.Kernel.Runs`),在会话层、存储、工具层之上(开头)。
2. `run_settled` 由编排层在释放互斥之后自己发,会话层发的那个被拦下(§1 实现注)。
3. 看门狗只在还没有 `run_end` 时动手(2.x 判据),不是「还没 settle」(§1 实现注)。
4. 共享结构用锁而不是单线程调度器(R14 允许;§4 实现注)。
5. 订阅是「每个控制端一个订阅对象 + 自己的泵」,不是「每个 Run 一个泵」;「先刷新泵」这一步因此不再需要(§2 实现注)。
6. 续跑选择沿用 2.x 的 `pickNextBatch`(promoted 全发 / 否则 FIFO 一条),不用 `RunInputQueues.TakeFollowUps`;单条的批沿用条目 id(§3 实现注)。
7. `activeRuns` 的生成函数放在 `Ncw.Core.Domain`(`ActiveRunIndexBuilder`),客户端与 Agent 共用同一份;`ActiveRunIndex` 追加了 `Subagents` 和 `Rev`;另加了运行期间每 30 秒的兜底重播(§2 实现注)。
8. 用量落账不在编排层(它记在模型层路由里,一次 HTTP 尝试一条);没有全局并发上限(2.x 顶层 Run 没有,§1 实现注)。
9. 新增事件之外的追加项:`RunInputQueues.Restore` / `TakeInternal`(Kernel)、`SessionRepository.GetRunRecord` / `RunIds`(Storage)、`RunLifecycle` / `ActiveRunIndexBuilder` / `ActiveSubagent`(Core)——都是纯追加,没有改任何已有行为。
10. **未实现**:子代理(`Task` 工具、子代理队列、`subagent_*` 事件、回执、后台报告的状态持久化)——后来已实现,见下面的第 10 条(子代理)。**`Abandoned` 之后会话层写库的跳过已补齐**:`RunRequest.IsAbandoned` + `AgentSession` 每个落库点检查 + 诊断 key,边界与残余见 §1 实现注。
11. **Host 接线已完成(M3)**:`Ncw.Agent.Host/Conversation` 把本文的接缝全部接上(`InteractionGate.RunEvent` → `Post`、`Changed` → `NotifyInteractionsChanged`、`IInteractionView` 适配器、`IRunExtension` 做 `OpenRun` / `End` 与 `PermissionRunState` 登记并造 `ToolRuntime`、`IRunRequestFactory`、`ActiveRunIndex` / `QueueUpdate` → 协议 DTO、会话删除时 `SessionDirectory.Remove`),`run/<id>` 主题就是 `Attach`(游标即 `sinceSeq`),见 [09 §8.2](../09-protocol.md)。联调中发现并修掉的一处时序问题:`InteractionGate` 在放行等待者**之前**必须先派发 `interaction_resolved`(原先反过来,Run 一收到答案就继续,事件可能排在后续事件之后);中止路径上取消令牌触发后循环立刻收尾,`interaction_resolved(aborted)` 与 `run_end` 谁先谁后不保证(终态之后的事件被丢弃),所以控制端不依赖它——挂起的交互以 `interactions` 主题的全量状态为准。
10. 子代理(`src/Ncw.Agent.Subagents`,§5 实现注):① 子代理能力经注入的 `ISubagentLauncher` 提供,不套 `ToolContext` 派生类型;② 队列用锁代替单线程调度器,并补两处线程安全修正(锁外摘取消登记、对已中止的父 Run 不再起子 Run + 子 Run 挂父 Run 的取消令牌);
    ③ 回执持久化改成对单条消息的比较并替换,更新早于消息落库时记下并在父会话 `run_settled` 后补写;④ 加载诊断改成「稳定 key + 英文」,不再是中文文案;⑤ `Task` 工具对「子代理被单独停掉、父 Run 还活着」返回清楚的失败结果,只有父 Run 被中止才抛中止;
    ⑥ `subagent_start` 之前(排队中)走 `tool_progress`(带结构化 `Details`),排位文案是英文,界面按 `Details` 翻译;⑦ 子 Run 的权限登记在 `SubagentService.OnCreated`,Host 的 Run 扩展对 `Origin == Subagent` 的 Run 不再登记。
11. 追加项(都是纯追加,没改已有行为):`SubagentReceipt` 加 `Background / ReportStatus / Color / Error`、新增 `SubagentReportPart`(`Ncw.Agent.Llm`);`MessageCodec` 读写这些字段和 `subagent` 块(原先认不出的 `subagent` 块被跳过);
    Core 的 `Subagents.cs`(见 §5 实现注)。唯一改过的已有测试:`CodecTests` 里「未知块被跳过」那条把示例块从 `subagent` 换成 `weird_block`(`subagent` 现在是认得的块)。
12. **计划模式、待办、目标不在编排层**,是 `Ncw.Agent.Planning` 里的一方扩展(§6、§7 的实现注):目标不再是 `Stop` 钩子(Hooks 扩展尚未实现,目标直接挂 `turn_end`);标记改为带外「比较并替换」(没有 `mergeGoalStatusHistory`);检查 / kickoff 经 `DeliverInternal`;
    空闲检查间隔按 2.x 真实行为(基数 30 分钟、下限 60 秒、×1/2/4)改正原稿的「60 秒起指数退避」;`ToolRuntime.WriteFence` 改为可写;`GoalStatusPart` 追加 7 个可选字段;用户手动设目标由 Agent 自己交付 kickoff(2.x 是返回给渲染层再发)。
13. **Host 接线第二批(子代理、计划、待办、目标)已完成**,见 §5「Host 接线清单」与 §6、§7:`PlanningKit` 装配(`kit.Extensions` 进会话扩展、`kit.RunExtensions` 排在造 `ToolRuntime` 的 Host 扩展之后、`kit.Bind(registry)`)、
    `goal.get / set / clear` 与 `goal` 主题、`runs.executePlan`(登记 `ExpectExecution` 后发起)、会话删除 / 退出时 `Goals.EndSession / StopAll`、判定模型与「模型自提目标」策略从 `settings` 读。**追加项**(都是纯追加):`RunRegistry.RememberOptions`(`goal.set` 带来的档位:会话从没发过消息时 kickoff 才有档位可用)、`PlanService.CancelExpectation`。
    **计划工作流对应会话模式 `plan`**(`PlanOptions.PlanWorkflow` 由 Host 回答)。已知缺口:子代理遥测事件(`subagent_update`)没有无损裁剪规则,极长的子代理跑在 2000 条日志硬上限下会把 `subagent_start` 挤出日志(控制端从 `Task` 调用的入参补卡片,见 `OrchestrationReducer.ApplySubagentEvent`);
    回执里没有子会话 id(只随 `subagent_start` 来),所以只靠回执重建的旧卡片点不开。
