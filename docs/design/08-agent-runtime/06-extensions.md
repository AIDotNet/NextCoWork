# 08.06 · 扩展

参考 pi 的 Extension API,但用在两类扩展上:

- **一方扩展**:C# 实现,随 Agent 发布,默认启用。权限、计划模式、待办、目标、子代理、Hooks、Skills、插件宿主都是一方扩展。
- **插件扩展**:插件 JS 在 Jaunt 里运行(见 [02 A7](../02-agent.md#a7-插件与-js-执行jaunt)),通过插件宿主这个一方扩展间接接入,
  只能订阅白名单里的事件,不能注册 `before_provider_request` 这类高权限扩展点。

## 1. 注册接口

```csharp
public interface IAgentExtension
{
    string Id { get; }
    int Order { get; }                       // 同一扩展点内的执行顺序,小的在前,相同按注册顺序;权限扩展固定在最前
    void Register(IExtensionRegistry r);
}

// 点的类型化标识。组合语义由**点的种类**决定,不由处理器自己决定
public sealed record ExtensionPoint<TEvent>(string Name);          // 通知
public sealed record MutatePoint<T>(string Name) where T : class;  // 就地修改
public sealed record TransformPoint<T>(string Name);               // 链式变换
public sealed record GatePoint<TEvent, TDecision>(string Name, TDecision Default, Func<TDecision, bool> ShortCircuits,
                                                  Func<TDecision, TDecision, TDecision>? Merge = null);   // 闸门
public sealed record LastWinsPoint<T>(string Name) where T : class;                                      // 最后者胜

public interface IExtensionRegistry
{
    void On<TEvent>(ExtensionPoint<TEvent> point, Func<TEvent, ExtensionContext, CancellationToken, ValueTask> handler);
    void Mutate<T>(MutatePoint<T> point, Func<T, ExtensionContext, CancellationToken, ValueTask> handler);
    void Transform<T>(TransformPoint<T> point, Func<T, ExtensionContext, CancellationToken, ValueTask<T>> handler);
    // onFailure:处理器抛异常时代它表态。缺省 = 弃权(失败放行);权限扩展传「拒绝」
    void Gate<TEvent, TDecision>(GatePoint<TEvent, TDecision> point, Func<TEvent, ExtensionContext, CancellationToken, ValueTask<TDecision>> handler,
                                 Func<Exception, TDecision>? onFailure = null);
    void LastWins<T>(LastWinsPoint<T> point, Func<T, ExtensionContext, CancellationToken, ValueTask<T?>> handler);   // 返回 null = 弃权
    // 以下随会话层 / Tools 层定义,循环层不需要:
    void RegisterTool(IAgentTool tool);
    void RegisterCommand(CommandDefinition command);            // 已实现(M4):Kernel 的 IExtensionRegistry 上带默认空实现,ExtensionHost 收集到 RegisteredCommands
    void ContributeSystemSection(string name, Func<SessionInfo, string?> section);
}
```

**会话层的补充(`Ncw.Agent.Session`)**:`RegisterTool` 和 `ContributeSystemSection` 已在 `IExtensionRegistry` 里实现(`ExtensionHost.RegisteredTools` / `CollectSystemSections`;节的异常 = 弃权,记诊断;
名为 `permissions` 的节紧跟 `# Environment`,其余在工具段之后)。会话层定义并触发的扩展点在 `SessionExtensionPoints`:`session_start`(通知,每个会话对象一次)、`input`(链式变换)、
`before_run`(就地修改 `RunDraft`:`AgentPrompt` / `ModePrompt` / 头块和状态块的附加段 / `ToolFilter`)、`before_compact`(闸门:`Proceed` / `Cancel` / `Custom(summary)`)、
`compact_attachments`(就地修改,**新增**:`after_compact` 是通知,要给压缩后附件就得赶在边界消息生成之前,所以拆成两个点)、`after_compact`(通知)、`run_settled`(通知,落库和释放会话锁之后);
`run_end`(`LoopExtensionPoints.RunEnd`)由会话层在落库之后触发。

**实现**(`src/Ncw.Agent.Kernel/Extensions`):`ExtensionHost` 收集处理器并按种类派发(`NotifyAsync` / `MutateAsync` / `TransformAsync` / `GateAsync` / `LastWinsAsync`),
`LoopExtensionPoints` 定义循环层用到的点(`prepare_turn` 的可变草稿 `TurnDraft`、`before_provider_request`、`before_tool`、`after_tool`、`turn_end`、`run_end`),
`ExtensionHost.CreateLoopHooks(...)` 把它们接成 `LoopHooks`(会话层给基础装配、落库、取引导消息)。
相对原稿的变化:原稿的「就地修改」和「最后者胜」没有对应的注册方法,这里补上 `Mutate` / `LastWins`;处理器都带 `CancellationToken`;`ExtensionPoint` 只用于通知。
`before_tool` 的闸门:第一个 `Deny` 短路,`Edit`(改写参数)取最后一个,没人表态 = `Allow`;`turn_end` 的闸门:第一个不是 `Stop` 的决定生效(按 `Order`)。

`ExtensionContext` 提供:当前会话和 Run 信息、`SendMessage(..., deliverAs: Steer | FollowUp)`、`AppendEntry`(扩展自己的持久状态,不进上下文)、
`Interact`(仅顶层 Run)、日志。

## 2. 扩展点与组合语义

| 扩展点 | 组合方式 | 典型使用者 |
|---|---|---|
| `session_start` / `session_shutdown` | 通知 | 目标(恢复状态)、插件宿主 |
| `input` | 链式变换(可以改写或直接处理掉) | 命令展开校验、`UserPromptSubmit` Hook |
| `before_run` | 就地修改运行参数 | 计划模式(工具允许列表、写入围栏)、子代理(权限收窄) |
| `prepare_turn` | 就地修改 | 压缩、工具快照过滤、状态块(待办、计划文件、git) |
| `system_sections` | 收集 | Skills 目录、模式提示词、权限说明、插件的说明 |
| `before_tool`(闸门) | **第一个拒绝即短路**;放行需要所有人不反对 | 权限(固定第一)、`PreToolUse` Hook、插件拦截器(只能收紧) |
| `after_tool` | 链式变换,只允许追加和翻转 `IsError` | `PostToolUse` Hook、变更记录器、读取跟踪 |
| `turn_end`(闸门) | 任一要求继续则继续(按 `Order` 取第一个) | 目标、待办提醒、`Stop` Hook |
| `before_compact` | 闸门:可以取消或提供自定义摘要 | 插件(高级) |
| `after_compact` | 通知 | 压缩后附件 |
| `run_end` / `run_settled` | 通知 | 后台报告、`SubagentStop` Hook、目标暂停 |
| `before_provider_request` | 最后者胜 | **仅一方扩展**(调试、请求补丁) |
| `tool_registry` | 收集 | 插件工具、MCP 工具 |

规则:

- 扩展处理器的异常**不传播到循环**:通知类记日志忽略;闸门类按「放行」处理并记诊断(与 Hook 的失败放行一致)——
  **权限扩展例外**,它的异常按拒绝处理。
- 处理器都带 `CancellationToken`,Run 中止时一起取消。
- 插件扩展点的处理受 Jaunt 预算限制(见 [02 A7](../02-agent.md#a7-插件与-js-执行jaunt)),超时视为弃权。

## 3. 一方扩展清单

| 扩展 | 使用的扩展点 | 能否关闭 |
|---|---|---|
| `Permissions` | `before_tool`(第一位)、`system_sections` | 否 |
| `Hooks` | `input`、`before_run`(记子代理深度)、`after_tool`、`turn_end`(Order 100)、`run_end`;PreToolUse 通过权限扩展的接入口 `IPreToolVerdictSource`(不在 `before_tool` 上另挂处理器,见 [03 §3](03-tools-permissions.md#3-权限)) | 可(没有配置 Hook 时等于不存在) |
| `PlanMode` | `before_run`、`prepare_turn`、工具 | 可(设置里的计划工作流开关) |
| `Todos` | `prepare_turn`(状态块)、`turn_end`(提醒)、工具 | 可 |
| `Goals` | `session_start`、`turn_end`、`run_end`、工具 | 可 |
| `Subagents` | `before_run`、工具(`Task`) | 可 |
| `Skills` | `before_run`(扫描目录,Run 开始时定死快照)、`system_sections`、工具(`Skill`)、`compact_attachments`(Order 300) | 可 |
| `ChangeTracking` | `after_tool` | 否(撤销依赖它) |
| `PluginHost`(**M4 已实现**,§7) | 会话扩展 `plugins`(Order 700,`before_run` 收上下文提供者给的一小段);工具经 `ToolRegistry`(来源 `plugin:<id>`,不经 `RegisterTool`:插件是动态的);拦截器经权限扩展的 `IToolInterceptor` 接入口(只能收紧);Skill 经 `ISkillPluginSource` | 可(无插件时等于不存在;插件逐个启停) |
| `Mcp` | `tool_registry` | 可 |

## 4. 为什么这样拆

- 循环层保持 pi 那样小,可以单独测试:给一个假上游和几个假工具,就能验证回合、中止、引导消息、孤儿修复。
- 每个功能是一个扩展,可以单独测试、单独关闭,也可以按「设备能力」降级(例如云端或手机控制的会话不启用某些功能)。
- 插件和一方功能用同一套扩展点,插件 API 的能力边界由白名单显式定义,而不是「插件能碰到什么就是什么」。

## 5. 实现注:计划模式 / 待办 / 目标(`src/Ncw.Agent.Planning`)

三个一方扩展放在一个项目里(命名空间 `Ncw.Agent.Extensions.Planning`,子命名空间 `.Plans` / `.TodoLists` / `.Goals`),在会话层、编排层、权限与交互扩展之上;设计与不变量见 [05 §6–§7](05-orchestration.md#6-计划模式)。和原稿 §3 的表相比:

| 扩展 | Id / Order | 实际使用的扩展点 / 接缝 |
|---|---|---|
| `PlanModeExtension`(+ `PlanRunExtension`) | `plan-mode` / 400 | `before_run`(工具过滤器、状态块、批准的计划)、`compact_attachments`、`run_end`、工具 `EnterPlanMode` / `ExitPlanMode`;Run 扩展的 `EnrichToolContext`(围栏同步) |
| `GoalExtension`(+ `GoalRunExtension`) | `goals` / 500 | `session_start`(恢复)、`before_run`(绑定 Run、冻结判定模型)、`turn_end`、`run_end`、工具 `ProposeGoal`;Run 扩展的 `OnCreated / OnAbortRequested / OnSettled`;`RunRegistry.DeliverInternal`(检查 / kickoff) |
| `TodoExtension` | `todos` / 600 | `before_run`(状态块)、`prepare_turn`(本回合工具有没有下发、本 Run 的起点)、`turn_end`、`compact_attachments`、`run_end`、工具 `TodoWrite` |

- **Order 约定**:权限 `int.MinValue`;Hooks 预留 100;计划 400;目标 500;待办 600。`turn_end` 闸门按 Order 取第一个不是 `Stop` 的决定,所以(将来)Stop 钩子的阻断先于目标,目标的续跑 / 强停 / 推迟先于待办补报。
  注意闸门里非短路的决定会被丢弃(含 `Notice`):需要带警告的强制结束必须用 `Finish`(空转刹车就是),不能用 `Stop`。
- **一站式装配**:`PlanningKit.Create(PlanningKitOptions)` 造三个扩展和两个 Run 扩展(每个功能可单独关闭,关掉的不注册 = 不存在);`kit.Extensions` 放进 `AgentSessionDeps.Extensions`,`kit.RunExtensions` 放进 `RunRegistryDeps.Extensions` 并且**排在造 `ToolRuntime` 的 Host 扩展之后**,
  注册表造好后 `kit.Bind(registry)`(目标检查的投递、后台工作、会话是否空闲从它读;先 Kit 后注册表是因为注册表要拿 Run 扩展)。
- **注入的接缝**(都是函数 / 接口,没有任何一个依赖 Host):`PlanOptions.PlanWorkflow`(这个 Run 开着计划工作流吗)、`GoalOptions.EvaluatorModel / ProposalPolicy / SessionTokens / ResolveModel`(设置与用量账本)、`IGoalTranscript`(标记落库,生产是 `SessionGoalTranscript`)、`ITranscriptReader`(读转录,生产是 `SessionTranscriptReader`)、
  `PermissionRuns`(Run → 交互句柄 / 围栏)、`InteractionGate`(提案 / 撤提案)。

## 6. 实现注:M4 扩展域(Skills / 命令 / Hooks / MCP)

四个一方扩展各是一个项目(命令放在 Skills 项目的 `Ncw.Agent.Skills.Commands` 子命名空间里),细节见 [03 §11–§12](03-tools-permissions.md)(Hooks、MCP、工具注册表)和 [04 §5](04-session-context.md)(Skills、命令)。Host 装配在 `Ncw.Agent.Host/Extensions/ExtensionDomain.cs`:

| 扩展 | Id / Order | 实际使用的扩展点 / 接缝 | 能否关闭 |
|---|---|---|---|
| `HooksExtension` | `hooks` / 100 | `input`(UserPromptSubmit)、`before_run`、`after_tool`、`turn_end`、`run_end`;PreToolUse = `HooksService.PreToolSource`,作为 `PermissionExtension(hooks: …)` 的接入口 | 可(没有配置钩子时等于不存在) |
| `SkillsExtension` | `skills` / 300 | `before_run`、`system_sections`(`# Available Skills`,只列名字和描述)、`compact_attachments`、工具 `Skill` | 可 |
| `McpService`(不是会话扩展) | — | 工具经 `ToolRegistry`(来源 `mcp:<serverId>`)进每回合快照,**不**经 `RegisterTool`:MCP 工具是动态的(连接、重连、`list_changed`),而 `ExtensionHost.RegisteredTools` 在注册时一次性收集 | 可(服务器逐个启停) |

- **`RegisterCommand`**:设计原稿里有、代码里没有。M4 在 Kernel 的 `IExtensionRegistry` 上补了 `void RegisterCommand(CommandDefinition)`(**带默认空实现**,所以所有已有的假 registry 不受影响),`ExtensionHost` 收集到 `RegisteredCommands`(`CommandDefinition` = 名字 + 描述 + 提示词模板,和目录里的 `commands/<name>.md` 同一种东西:一段提示词模板,不是一个能执行的动作)。命令表 = 内置 `/init` + 一方扩展贡献的(先进表)+ 目录里的(全局 → 项目,后者覆盖;用户写的同名命令永远说了算);Host 用探测用的 `ExtensionHost` 收集扩展贡献的命令。
- **系统提示词节是同步回调**(`Func<SessionInfo, string?>`),不能在里面做 IO:Skills 在 `before_run`(异步)里扫描目录并缓存成**本 Run 的快照**,节只读快照——「技能目录是运行级常量,Run 开始时定死」,保住缓存前缀;失效规则 = 每个 Run 开始重扫。代价:从没发过消息的会话,上下文预览里没有技能目录。`Skill` 工具同样读这份快照,不回落读磁盘。
- **`extensions` 主题**:Skills / 命令 / Hooks 的清单按项目取,所以 Host 只广播三个单调修订号(`ExtensionRevisions`,服务的 `Changed` 事件 → 加一并推送),控制端据此重拉自己持有的清单(见 09 §8.2)。MCP 有自己的全量主题 `mcp`。
- **Order 约定**(补全 §5 的表):权限 `int.MinValue`;Hooks 100;Skills 300;计划 400;目标 500;待办 600。

## 7. 实现注:插件宿主(`Ncw.Agent.Plugins`)

M4 的插件系统。Jaunt 的评估、NativeAOT 实测和 TypeScript 的决定见 [02 A7.1](../02-agent.md#a71-jaunt-评估与实现m4);这里写代码里的约束和不变量。项目只引用 Core / Llm / Kernel / Session / Extensions / Tools / Environments / Skills,**不引用 Protocol / Avalonia / 客户端 / Host**(`ArchitectureTests`);协议映射在 `Ncw.Agent.Host/Plugins/`。Order 约定补全:权限 `int.MinValue`;Hooks 100;Skills 300;计划 400;目标 500;待办 600;**插件 700**。

### 7.1 清单、安装、状态

- **清单**(`PluginManifestParser`,2.x `shared/plugin/manifest.ts` 的移植,单测逐条移植):装载期严格、读不懂**整份拒绝**、永不抛。`main` 必须是单文件 ESM 的 `.js` / `.mjs`;`engines.nextcowork` 比的是**插件 API 版本**(`PluginApiVersion.Current = 0.3.3`,不是应用版本;`^0.2.0` 等旧 range 是 `deprecated`:照常装载、一条 warn);贡献点实现:命令、菜单(数据面)、Agent 工具、Skills(只认 `skills/<name>` 两段,名字走 `SkillLimits.IsValidName`)、配置、斜杠命令 / 快捷键(数据面,转给界面)。**认得字段名但不实现**的(`customEditors` / `views` / `webApps` / `cardViews` / `themes` / `agents` / `modes` / `documentEngines` 与未知键)进 `Unsupported`,装载时逐条出 `plugins.diag.unsupported` 诊断(不静默不生效);`nativeComponents` 整份拒绝;`kind: webapp` 能装,没有运行时。
- **权限**(`PluginPermissions`,2.x 的 14 个能力原样):`required` / `optional` 来自清单,`granted` 来自用户,**三者分开存**。铁律:运行期能要到的 = 清单上界(`CanRequest`),越界**不弹窗**直接 false;授予越界的静默丢弃。升级后必选能力变大(`Escalated`)→ `pending-approval`,不批不激活(`SetEnabled` 也挡);撤掉必选能力 = 插件停下来(状态 `disabled`)。授权是**实时**读的,对正在跑的插件立刻生效。
- **安装**(`PluginPackageInstaller`,经 `IExecutionEnvironment`):zip(≤ 20 MB、≤ 2000 条、展开 ≤ 50 MB 边写边计数、深度 ≤ 12)和目录(不复制 `node_modules` / `.git`、拒绝符号链接)同一组防线:路径穿越 / 绝对路径 / 盘符 / 反斜杠 / 空段 / 大小写重复 / 符号链接条目 / 多个顶层目录 / 顶层目录 ≠ 清单 id / 清单 > 256 KB 一律拒绝;`main`、icon、两种语言的 l10n、Skill(连 `SKILL.md`)必须真的在包里;市场安装带权威 sha256;staging → backup → rename,失败还原旧版本。线上上限 3 MB(`plugins.installPackage`),更大的走市场。新装的插件**禁用、无授权**;覆盖安装 = 升级,用户的决定保留。
- **状态**:kv `plugins.state`(2.x 的键和形状 `{id: {enabled, granted, approvedRequired, installedAt, updatedAt, slug?}}`,2.x 数据被接管之后启停与授权原样可用)、`plugins.config.<id>`(配置)、`plugins.kv.<id>`(插件私有存储,5 MB 配额,全局 / 工作区两个作用域分桶)。快照 `PluginsSnapshot(Rev, Plugins, Interactions)` 是全量权威,`Rev` 单调,`Changed` 在发布锁里按 `Rev` 序触发(首帧与增量同一个类型)。
- **配置**:`getConfiguration` 以清单为准(用户改过、升级后被删的项不再出现);`setConfiguration` 类型对不上 / 不在 `enum` 里 / 键不存在拒绝;`null` = 恢复默认(删掉那一条)。
- **市场与更新**(`IPluginMarketClient` 可注入,`HttpPluginMarketClient`):与 Skills 同一套四步(授权拿权威摘要 → 校验版本与摘要形状 → 下载边读边卡 20 MB → 安装器再校验),没有账户令牌 = `plugins.authRequired`(浏览照常);同一个 slug 的两次安装合流成一次、同时最多两个下载;`CheckUpdates` 把市场整张表翻完(≤ 20 页)、缓存 10 分钟(缓存的是市场列表,不是算好的更新表),新增必选能力按 `approvedRequired` 算;`UpdateAll` 只动市场来源的(`slug` 有值),串行、失败不中断。

### 7.2 运行时与宿主 API

- **一个插件一个引擎**(`PluginRuntime`,仓库里唯一的 `new JsEngine`):`Hardened` + `JsLimits`(`PluginRuntimeLimits`:激活 10 s、命令 30 s、工具 60 s / 交互式 5 min、拦截器与上下文 3 s;指令 5 亿 / 次;累计分配 256 MB;栈 2000;输出 16 MB;源码 8 MB)。一把锁串行化插件内的调用;JS 前导(`PluginPrelude`)把宿主绑定收进闭包、从全局删掉,搭出 `nextcowork`(2.x `nextcowork.d.ts` 的非 UI 部分),并保证一次调用结束前收齐所有异步宿主操作(见 02 A7.1 的 `UnjoinedAsyncWork`)。
- **宿主 API 方法表**(`PluginHostMethods`,全部经 `IExecutionEnvironment` / 注入的 HTTP,不直接碰设备本地;未列出的 API 在运行期不存在):

| 方法 | 能力 | 门 |
|---|---|---|
| `workspace.readFile` / `stat` / `findFiles` / `folders` | `workspace.read` | 只收相对路径 + realpath 守卫(`..`、绝对路径、盘符、指向根外的软链一律拒绝);单次 ≤ 8 MB;`findFiles` 只支持前缀 glob(`*` `**` `?`),跳过 `.git` / `node_modules` |
| `workspace.writeFile` / `deleteFile` | `workspace.write` | 同上 + 乐观锁 `revision`(盘上变过拒绝)+ 计划模式写入围栏 + 同文件串行(`FileMutationQueue`);在 Run 里的文本写入进改动记录(审查 / 撤销);删除不可撤销 |
| `process.exec` | `process` | `argv[0]` 必须命中清单 `allowedCommands`(按基名)、shell 元字符先于白名单判、**每次调用用户批准**(插件交互,带执行环境标识:设备 + 环境),批准之后**复检**(插件还在线、能力还在、调用没被取消),超时夹在 1–120 s;不经 shell |
| `net.fetch` | `net` | 只 https、无凭据、不是内网 / 环回字面量、命中 `hostPermissions`;**不自动跟随重定向**,每一跳重过同一道门、最多 5 跳、**跨 host 的跳转拒绝**;正文按字节截 8 MB |
| `storage.*` / `secrets.*` | `storage` / `secrets` | 私有 kv 5 MB 配额;密钥键由宿主加 `plugin:<id>:` 前缀(NCK1) |
| `permissions.*` / `configuration.get` / `env.appInfo` / `appearance.get` / `diagnostics.log` / `window.*` | 无(`window.showMessage` 要 `window.notify`) | `permissions.request` 越界不弹窗;`window.showConfirm` / `showInputBox` / `showQuickPick` 与运行时权限申请、命令审批都是**插件交互**:进 `plugins` 主题,`plugins.interactionReply` 回答,取消 / 超时(2 分钟)= 取消,不是错误 |

  不做:`scm.*`、`process.execStream`、`env.openExternal` / 剪贴板、`tabs.*` / `customEditors.*` / `documents.*`(界面类)、`plugins.*`(插件间通信)。每次调用记一条活动(时间、方法、人话摘要、裁决 ok / denied / error / timeout、耗时;**不含参数原文**;环形 500 条);被拒的调用另留一条诊断(方法 + 缺的能力)。

### 7.3 接入一方扩展点

- **Agent 工具**(`PluginTool`):只能注册清单 `contributes.tools` 里声明的;进 `ToolRegistry`(`ReplaceSource("plugin:<id>")`,内部 id `plugin__<publisher>_<name>__<tool>`,同 id 别的来源占着就丢弃后来者并留诊断,所以替不掉 `Bash`);描述加 `[Plugin:<名字>]` 前缀并消毒、schema 原样下发。**特征只收紧**:`ReadOnly` 要「声明只读且清单里没有任何写类能力」才算,`Destructive` = 声明的 或 有写类能力,`NeedsNetwork` = 声明的 或 有 `net`——所以自报只读的插件带 `workspace.write` 时仍然走审批。权限完全走权限扩展(规则按内部 id 匹配)。`Details` 带插件 / 工具名、**执行环境标识**和消毒过的卡片(白名单原语、限长、`image` 只认 `data:` / `ncw://`、`link` 只认 https;模型永远看不到卡片)。中止原样传播(不伪装成工具失败);插件 throw / 超预算 / 不在线 = 工具错误。交互式工具(`interactive`):实时进度卡 → `plugins.cardAction` 的按钮动作经通道送进仍在运行的那次调用(`onAction`)。
- **Skills**:`ISkillPluginSource`(`PluginManager.SkillSource`)每次扫描都重新问:启用且(`idle` / `activating` / `active`)的插件、清单里声明的 `skills/<name>` 目录;优先级最低(用户的同名覆盖);禁用 / 卸载 / 待批准 / 故障后下一个 Run 就看不见。
- **Hooks 贡献**:`agent.registerToolInterceptor`(能力 `agent.intercept`)→ `IToolInterceptor`,**类型上没有放行**(返回 `allow` = 弃权,不能关掉审批),拒绝带 `[Plugin …]` 说明,`ask` 直接去问人;`agent.registerContextProvider`(`agent.context`)→ 会话扩展的 `before_run`,每个提供者给一小段,**强制包裹成 `<plugin-context source="…">`**,单次 ≤ 2000 字符、每轮合计 ≤ 8000,进头块(静态,保住缓存前缀)。两者超时(3 s)/ 抛异常 = 弃权并留诊断;注册时没有对应能力 = 拒绝并留诊断。
- **命令**:`plugins.runCommand`(member)只能跑清单声明且已注册的命令;`args` 固定带 `workspaceId`(调用点上下文,调用方改写不了),命令里的文件 / 命令类能力以那个项目为根。

### 7.4 与 2.x / 设计原稿的差异

① 启用即激活,没有激活事件按需唤醒与 5 分钟休眠(见 02 A7.1);② 取消 / 累计分配接近预算 = 透明重建引擎,故障(超预算)= `faulted`,要用户重新启用(2.x 的 iframe 崩了由宿主重建);③ `process.exec` 的审批走插件交互通道而不是 Run 的权限链(插件命令可以不在任何 Run 里);④ `workspace.deleteFile` 直接删除(2.x 进废纸篓),不进改动记录;⑤ 配置的 `enum` 类型额外要求取值在 `enum` 列表里;⑥ 写类能力的插件工具一律按破坏性 / 非只读(2.x 信任插件自报);⑦ 清单里 `nativeComponents`、`kind: webapp` 的运行、文档引擎、插件间通信、`scm.*`、`execStream` 不支持;⑧ 同一个插件内调用串行(Jaunt 引擎同一时刻只有一次根执行),交互式工具等用户点按钮期间同插件的其它调用排队。
