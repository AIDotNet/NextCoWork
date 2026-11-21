# 08.03 · 工具、权限与交互

## 1. 工具协议

```csharp
// 实现:src/Ncw.Agent.Kernel/Tools/ToolProtocol.cs。原稿的 ToolInfo 在实现里叫 ToolSpec(避免和模型层的 Ncw.Agent.Llm.ToolInfo(外部名 + 描述 + schema,即发给模型的那份)重名);
// 外部名不在 ToolSpec 里,而是回合的工具快照 ToolSnapshot 里的 ToolBinding(ExternalName, Tool)。
public sealed record ToolSpec(
    string InternalId,            // 稳定 id:内置名 / mcp__<server>__<tool> / plugin__<publisher>_<name>__<tool>
    string Description,           // 不可信文本:去控制字符,截断到 4096
    JsonSchema InputSchema,       // 源生成器从参数类型导出
    ToolTraits Traits,
    string? PromptSnippet = null, // 系统提示词工具段的一行说明(取自 pi)
    IReadOnlyList<string>? PromptGuidelines = null);   // ToolSource(来源,用于按来源批量注销)随注册表一起做,不在循环层

public sealed record ToolTraits(bool ReadOnly, bool Destructive, bool NeedsNetwork, bool ConcurrencySafe = true);
// ReadOnly / Destructive / NeedsNetwork 必须显式声明,没有默认值(2.x ★:默认值会让下一个联网工具悄悄绕过开关)

public interface IAgentTool
{
    ToolSpec Info { get; }
    bool IsEnabled(ToolContext ctx) => true;
    // 预检第一步:校验参数 + 宽松转换("5" → 5)。默认原样放行;失败 = 给模型看的错误说明,变成工具错误而不是异常
    ToolArgumentCheck PrepareArguments(JsonElement input) => ToolArgumentCheck.Valid(input);
    ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct);   // 中止异常原样传播
}

public sealed record ToolResult(
    IReadOnlyList<Part> Content,  // 给模型:文本和图片
    JsonElement? Details,         // 只给界面:diff、卡片、结构化数据。永远不发给模型(取自 pi)
    bool IsError = false,
    bool Terminate = false,       // 本批所有结果都 Terminate 时结束 Run(对应 2.x 的 stopRun)
    bool Truncated = false, long? OriginalBytes = null,
    SubagentReceipt? Subagent = null);   // 子代理回执(Task 工具),进 tool_result 记录
```

**`ToolContext`**(循环层造基础版:`RunId`、`CallId`、`SessionId`、`Depth`、`Environment` 标识、`Progress`、`FileMutations`;它不是 sealed,会话层派生一个带下面这些能力的子类型,经 `LoopHooks.EnrichToolContext` 换上):
`PermissionMode`(Run 开始时的快照)、
`IExecutionEnvironment Environment`(项目的执行环境,见 [06](../06-environments-ssh.md))、`IProgress<ToolProgress>`、
以及按需提供的能力:`SpawnSubagent`、`Interact`、`Shells`、`Scheduling`、`ImageGen` 等。

- 能力不可用时**直接不提供**(属性为 `null`),不给一个一调就抛异常的桩(2.x ★)。
- 工具拿不到密钥存储和应用路径。
- 工具只通过 `Environment` 访问文件和进程。

**注册表**:按 `InternalId` 存储;同 id 注册是替换(MCP 重连依赖这一点);按来源批量注销。
- 每回合取一次**快照**,同一个快照既用来告诉模型有哪些工具,也用来按名字查找(2.x ★)。
- 快照只能删工具不能加:过滤条件有 `IsEnabled`、联网开关、`depth > 0` 时去掉需要交互的工具、模式和 Agent 定义的允许列表。
- 正在执行的调用持有自己的工具引用,注销后不重新查找。

**命名**:外部名白名单字符,超长或冲突时变成 `前缀_<fnv1a32(internalId)>`。映射在会话内稳定,名字永不回收。

**冲突**:内置工具先注册;后来者与已有 id 冲突时**丢弃后来者**,任何来源都不能替换 `Bash` 等内置工具。

## 2. 内置工具

以 2.x 的工具集为准,吸收 pi 的几处改进:

| 工具 | 要点 |
|---|---|
| `Read` | 默认 2000 行、每行 2000 字符、8MB 上限、二进制嗅探;记录「已读」。超出时提示 `offset` 继续(pi) |
| `Write` | 覆盖已有文件前**必须在本 Run 里读过**(2.x 的读取跟踪);遵守写入围栏(计划模式);记录变更 |
| `Edit` | **一次可以多处替换**(pi):每处必须唯一、互不重叠,都相对原文件匹配;精确匹配失败时退到宽松匹配(NFKC、引号破折号、行尾空白);保留 CRLF 和 BOM;details 里带 diff。仍要求先读过。C# 的 `string.Replace` 没有 `$&` 这类替换模式,2.x 的那个 bug 自然消失 |
| `LS` / `Glob` / `Grep` | 后端依次尝试 `rg`、`git grep`、`grep`,最后是托管实现;200 条结果、5 秒预算;托管正则优先用 `RegexOptions.NonBacktracking`(线性时间,没有灾难性回溯),只有环视 / 反向引用这类只能回溯的写法才先过静态 ReDoS 检查、再带匹配超时;预算耗尽或匹配超时都在结果里**如实说明不完整** |
| `Bash` | 每次新 shell,stdin 关闭;默认 120 秒、最长 600 秒;输出**保留尾部**,完整输出存临时文件并在结果里给出路径(pi);支持 `run_in_background`,没有后台 Shell 能力时显式失败;用户可以只停这一条命令、或把它转去后台(2.x 保留) |
| `BashOutput` / `KillShell` | 后台 Shell;分成两个工具,计划模式下仍能查看构建输出 |
| `TodoWrite` | 无状态,记录本身就是状态;最多 40 项 |
| `WebFetch` / `web_search` | SSRF 防护(协议白名单、拒绝带凭据的 URL、拒绝环回 / 内网 / 链路本地 / 元数据地址、解析后校验并钉死 IP、重定向逐跳校验、跨主机重定向交还给模型);受联网开关控制。**已实现**,见 §13 |
| `Skill` | 返回 Skill 正文,附「不可信内容」边界说明 |
| `AskUserQuestion` | 1–4 个问题,每个最多 8 个选项。**已实现**,见 §13 |
| `EnterPlanMode` / `ExitPlanMode` | 见 [05 §6](05-orchestration.md#6-计划模式) |
| `Task` | 启动子代理,见 [05 §5](05-orchestration.md#5-子代理) |
| `ProposeGoal`、定时任务 4 个、浏览器约 16 个(**已落地**,自研 CDP 客户端,见 §14)、`visualize_*`、图片和视频生成、`Workflow` | 按 2.x 移植;其中会改共享状态的标记 `ConcurrencySafe = false` |

## 3. 权限

权限是**不可关闭的一方扩展**,挂在循环层的 `BeforeTool` 上。

**模式**:`ask`(都问)、`auto`(只有破坏性操作问)、`full`(不问)。运行中可以改(实时值),工具上下文里是开始时的快照。
子代理的模式取 `min(父 Run 的实时值, Agent 定义的值)`,防止提权。

**规则**:`Tool` 或 `Tool(specifier)`,按 `InternalId` 匹配。
- `prefix:*` 前缀匹配;对 `Bash`,前缀之后的部分不能含 `; & | `` ` `` 换行 < > $(`,防止 `Bash(git status:*)` 被拼出第二条命令。
- `*` 通配用线性双指针匹配,不用正则。
- 只比较 `command / file_path / path / pattern / query / url / domain:<host>` 这几个字段。
- 存储:项目级 `<项目>/.next-cowork/settings.local.json`,每类最多 500 条;文件损坏时读为空、拒绝写入;同一文件的写入排队。

**执行时机**:按 pi 的方式(Q17),同一批工具调用**按调用顺序逐个预检**,每个调用的权限决策(包括等待用户回答)完成后才轮到下一个;
全部预检完成后,放行的调用再并行执行。所以审批卡片按顺序一张一张出现。

**决策顺序**(顺序即语义,2.x ★):

```
1. 联网开关关闭且工具在联网工具集合里            → 拒绝(即使是 full 模式)
2. 命中 deny 规则                                → 拒绝,并且不运行任何 Hook
3. PreToolUse Hooks + 插件拦截器                  (拦截器只能收紧:它的 allow 视为弃权)
4. Hook 拒绝                                     → 拒绝(优先于 allow 规则)
5. Hook 要求询问,或命中 ask 规则                  → 直接去问人
6. Hook 放行                                     → 放行
7. 只读工具 / 模式放行                            → 放行
8. 命中 allow 规则                                → 放行
9. auto 模式且破坏性工具                          → AI 审查(无工具、128 token;不明确就交给人)
10. 子代理                                       → 拒绝(子代理不能问人),提示可添加的规则
11. 发起 tool_permission 交互,等待回答
    allow_always(会话 / 项目 / 永久三种作用域)    → 写入对应作用域的 allow 规则;写失败本次仍放行
```

- **步骤 3–6 的钩子不是 `before_tool` 上的另一个处理器**,而是权限扩展在链里**调用**的接入口(`IPreToolVerdictSource`、`IToolInterceptor`):钩子的 deny 要压过 allow 规则、钩子的 ask 要压过档位放行,
  这些都发生在链的中间,而 `before_tool` 的组合语义(第一个拒绝即短路、放行需要所有人不反对)表达不了。拦截器的接入口**类型上就没有 allow**(插件不能关掉审批)。钩子失败 = 弃权并记诊断;权限扩展自己的异常 = 拒绝。
- **规则的比对对象先规范化**(3.0 收紧):路径字段经路径守卫解析(`..`、符号链接、相对 / 绝对写法),allow 规则只比规范写法(相对项目根 + 绝对),deny / ask 规则另外比原始写法;
  Bash 命令拆成子命令(`; && || |`、`$(…)`、反引号、`bash -c`、`sudo / env / VAR=x` 前缀、`find -exec`),deny / ask 对每一段都比一遍,否则 `Bash(rm:*)` 这条 deny 在前面垫一句 `echo hi;` 就绕过去了。
  Bash 前缀规则落在词边界上(`git:*` 不匹配 `gitk`)。
- **「记住本次选择」的作用域**:`Session`(进程内,按会话,会话结束即忘)、`Project`(`<项目>/.next-cowork/settings.local.json`,经 `IExecutionEnvironment` 读写)、`Global`(永久,宿主注入的 `IPermissionRuleStore`,例如落在 Storage)。
  存放处全是注入的接口;读取失败按失败即关闭(拒绝本次调用)。没有项目根时不提供 `Project`。
- **危险命令**:`BashCommand.Classify` 给 Bash 命令一个只读 / 危险 / 未知的粗略分级(2.x 没有)。它**只是事实描述**:进审批卡片、让「以后都允许」对危险命令只建议原样命令(不给 `rm:*` 前缀)、让 `auto` 档对危险命令跳过 AI 审核直接问人。
  它不放行任何命令;「只读 Bash 当作只读工具」是个缺省关的选项(`PermissionOptions.AutoAllowReadOnlyBash`)。
- **计划模式的写入围栏**:`PermissionRunState.PlanFence` 与 `ToolRuntime.WriteFence` 是同一个值(`PermissionRunState.FromRuntime` 从同一处取)。只有 Write / Edit 改的**正是**围栏文件时才当作只读放行(2.x 只按工具名信任,围栏是否真的设上了全靠写工具自己)。

- 系统提示词里描述权限的文字,和闸门用**同一个函数**生成,两者不会不一致。
- 建议规则由 Agent 计算,不由客户端拼。
- 3.0 新增:审批卡片带执行环境标识;`allow_edited` 改写后的参数**重新走一遍规则检查**(2.x 没有,是个漏洞)。

## 4. 交互

```csharp
public abstract record InteractionRequest(InteractionId Id, RunId RunId, DateTimeOffset CreatedAt);
record ToolPermission(string CallId, string ToolName, JsonElement Input, ToolTraits Traits, string? SuggestedRule, EnvironmentRef Env);
record AskUser(IReadOnlyList<Question> Questions);
record PlanApproval(string Plan, string PlanId, string Path);
record GoalProposal(string Condition);
record WorkflowApproval(string CallId, JsonElement Workflow);
record WidgetSubmit(string CallId, string Title);
record SshHostKey(ExecutionEnvironmentRef Env, string HostName, string Algorithm, string Fingerprint);   // 3.0 新增:首次出现的主机指纹确认(指纹变化不是交互,直接拒绝)
record SshCredential(ExecutionEnvironmentRef Env, SshCredentialPromptKind PromptKind, string HostName, string UserName, string? KeyName, string? Instruction,
                     IReadOnlyList<SshCredentialPrompt>? Prompts, bool PreviousRejected, bool CanRemember, bool HasSaved);   // 3.0 新增:密码 / 私钥口令 / 键盘交互式
```

`SshHostKey` / `SshCredential` 是**设备级**交互(06 §10.4):不属于任何 Run,每次提问开一个 `env:<环境 id>:<ulid>` 作用域,问完即关;走同一个闸门(先到先得、`expectedVersion`、取消清理、必带执行环境标识)。
**回答里有秘密**的请求(`SshCredential`)的 `RedactsResponse` 为真:`interaction_resolved` 事件不带回答,只有谁、什么时候。

**交互闸门**:

- 只在 Run 处于运行中时可以发起;发出 `interaction_request`,返回一个等待回答的任务。
- **没有超时**,只有回答或中止能结束它。中止和 `run_end` 时以中止拒绝并发出 `interaction_resolved{aborted}`。
- 回答要校验(数量、非空、选项匹配、修订必须带反馈、`allow_always` 只接受请求里开放的作用域);校验失败不消耗这次交互。
- 可以同时挂起多个(多个 Run、多个子代理的父 Run)。
- 挂起的交互必须**可以查询**,不能只是一个悬挂的任务:客户端重连后通过快照拿回(2.x ★)。
- **多端先到先得**:回答 = `Respond(id, expectedVersion, response, responder)`,在一把锁里原子地「判断仍挂起 → 移出」(进程内等价于 SQLite 的 `UPDATE … WHERE status='pending'`;挂起的交互活不过进程重启,重启后 Run 本来就以「被中断」收尾,
  `IInteractionJournal` 可选地把开 / 结落进 SQLite)。只有一个回答者赢;后到的得到 `Conflict(AlreadyResolved, 谁, 什么时候, 已回答/已中止)`,界面显示「已在另一台设备上处理」。即使答案一模一样,第二次也失败。
  每个交互有登记时的**单调版本号**(闸门修订号,全设备唯一只增),`expectedVersion` 对不上 = `Conflict(StaleVersion)`;结束的交互在有界历史(512 条)里留着去向,更早的是 `NotFound`。
  协议映射:`Accepted` → 成功;`Conflict` → `conflict`;`NotFound` → `not_found`;`Invalid` → `invalid_request`(交互仍挂起)。
- **变更通知用全量类型**:`InteractionsSnapshot { Revision, Pending[] }`,首帧、`interactions.list`、`Changed` 通知是同一个类型,修订号严格递增、串行派发(订阅者不会看到乱序或重入)。
  Run 事件流里的 `interaction_request` / `interaction_resolved` 由 `RunEvent` 给出,编排层分配 `seq` 后并进事件流。
- **子代理不能提问**:`OpenRun(runId, depth)` 的 `depth > 0` 返回 `CanInteract = false` 的句柄,发起任何交互当场抛 `InteractionRefusedException`(而不是挂起);权限扩展对子代理直接拒绝并给出可添加的规则。
- **审批必须带执行环境标识**:`ToolPermission.Env`(设备 + 环境 + 名字 + 类型)必填,草稿缺它被拒。
- 待处理交互数计入 `activeRuns` 广播(含所有子 Run 的),侧边栏的待处理数和手机推送都读这个数。

## 5. Hooks

- 事件:`UserPromptSubmit`、`PreToolUse`、`PostToolUse`、`Notification`、`Stop`、`SubagentStop`。
- 定义:`command`(在**项目的执行环境**里执行)或 `prompt`(调辅助模型判断)。匹配器用权限规则的语法。
- 执行:全局 → 项目 → 运行时(目标模式注册的);顺序执行,第一个阻断即短路;每个事件最多 50 个。
- 命令 Hook:stdin 一行 JSON;退出码 0 读 stdout 的 `{decision, reason, additionalContext}`,2 表示阻断,其他退出码、超时、启动失败一律**放行**并记录诊断。
  输出上限之后仍持续读管道,避免子进程写满管道卡死。
- 效果:
  - `UserPromptSubmit` 阻断 → Run 以错误结束;`additionalContext` 只进发出去的请求,不进记录。
  - `PostToolUse` **只能追加**反馈、翻转 `IsError`,不能改写输出(界面、记录、模型看到的必须一致)。
  - `Stop` 阻断 = 本回合继续;用 `stop_hook_active` 防止无限循环。
  - `Notification`、`SubagentStop` 不等待。

## 6. MCP 与插件工具

- MCP 工具:`ReadOnly=false`、`Destructive=true` **一律保守**,不信任服务器的提示;`NeedsNetwork` 由我们的传输配置决定;
  描述加 `[MCP:<server>]` 前缀;调用超时 60 秒;图片、音频、资源块转成文字说明而不是丢弃。
- 插件工具(Jaunt):只能注册清单里声明的工具;每回合同步一次;与非插件工具重名的跳过。
- MCP 服务器可以配置为「在执行环境中运行」(见 [06 §4](../06-environments-ssh.md#4-其他组件在哪个环境里运行))。

## 7. 不变量清单

- [x] 工具特征必须显式声明(Kernel / Tools 层)
- [ ] 快照每回合一次,广播与查找用同一个(会话层)
- [x] 内置工具不可被替换(`ToolRegistry`,见 §10;`Ncw.Agent.Session.Tests.ToolRegistryTests`)
- [x] deny 规则命中时不运行 Hook;Hook 拒绝优先于 allow 规则;插件拦截器只能收紧(`Ncw.Agent.Extensions`,见 §9 的 P1–P6)
- [x] 子代理不能发起交互(快照过滤〔会话层〕 + 不提供 `Interact` + 审批直接拒绝,三处对称;均已实现并测试:快照过滤在 `Ncw.Agent.Subagents` 的 `SubagentToolFilter`,端到端见 05 §11 O21)
- [x] 交互无超时、可查询、多端先到先得
- [x] PostToolUse 只能追加(Kernel 已校验;Hooks 扩展只返回追加的反馈和 `IsError` 翻转,见 §11)
- [x] MCP 工具一律按破坏性处理(`McpTool` 不采信服务器的 annotations,见 §12;`Ncw.Agent.Mcp.Tests.ToolTests`)

## 8. 实现注(`src/Ncw.Agent.Tools`,M3)

**已随各自模块移植的工具**:`TodoWrite`、`EnterPlanMode` / `ExitPlanMode`、`ProposeGoal` 在 `Ncw.Agent.Planning`(一方扩展注册的工具,见 [06 §5](06-extensions.md#5-实现注计划模式--待办--目标srcncwagentplanning));`Task` 在 `Ncw.Agent.Subagents`。

已实现「基础工具」:`Read`、`Write`、`Edit`、`LS`、`Glob`、`Grep`、`Bash`、`BashOutput`、`KillShell`(`BuiltinTools.Create()`,顺序固定)。
依赖尚未存在的模块的工具(定时任务、图片和视频生成、`Workflow`;`WebFetch` / `web_search` / `AskUserQuestion` 已在 §13,`Skill` 在 `Ncw.Agent.Skills`)随各自的模块移植(`Task` 已在 `Ncw.Agent.Subagents`,见 05 §5)。
依赖规则:只引用 `Ncw.Agent.Kernel` 和 `Ncw.Agent.Environments`;源码里没有 `System.IO` 的文件 API、没有 `Process`(`ArchitectureTests` 机械检查);不产出界面文字。

**工具怎么拿到执行环境**:`ToolContext` 里只有环境的标识(`EnvironmentTag`)。会话层构造一个 `ToolRuntime`(项目的 `IExecutionEnvironment`、根目录、读取跟踪 `ReadTracker`、变更记录 `ChangeRecorder`、
可选的后台 Shell 注册表 `IShellRegistry`、计划模式的写入围栏、Bash 临时目录),用 `ExecutionToolContext.Enricher(runtime)` 接到 `LoopHooks.EnrichToolContext` 上;
工具拿到的上下文就是 `ExecutionToolContext`(`ToolContext` 的派生类型)。没有 `Shells` 能力时属性为 `null`,`BashOutput` / `KillShell` 的 `IsEnabled` 为 false(不下发),`run_in_background` 当场失败而不是降级成前台。

**参数校验**:每个工具的 schema 手写(`S` 构造器,不用反射),`PrepareArguments` 走 `JsonSchemaValidator`(只认 `type / enum / min* / max* / items / properties / required / default`):
宽松转换(`"5"` → 5、`"true"` → true、数字 → 字符串、可选属性的 `null` 视为缺省)、缺省补 `default`、schema 外的属性原样保留;错误带属性路径(`edits.0.old_string: required property is missing`)。直接调用 `ExecuteAsync`(没经过 Loop 预检)时校验会再过一遍(幂等)。

**路径**:一律过 `PathGuard`。**落在项目根外面不是错误**(2.x 的取舍:谁能碰哪个文件由权限档位决定);Details 里有 `outsideWorkspace`,回执里的路径根内用相对写法、根外用绝对写法(绝不出现 `../x`)。
「相对根显示」拿 realpath 之后的根比(用户登记的根常常不是 realpath 形式,如 macOS 的 `/var`、链到外置卷的 `~/code`)。没有工作区时相对路径被拒、绝对路径照样能用,Bash 无处可跑。

**结果**:`Content` 给模型,`Details` 只给界面,每份都带 `environment: {deviceId, environment, name, type}`(审批卡片之外的工具卡片的执行环境标识)和 `kind`(`read / write / edit / ls / glob / grep / bash / bashOutput / killShell`)。
`Edit` 的 Details 带 `diff`(统一 diff 风格,由编辑区间直接生成,相邻的改动合成一个 hunk)、`firstChangedLine`、`fuzzy`;`Bash` 的 Details 带 `outcome`(`exited / timedOut / stopped / detached / background / connectionLost`)、退出码、`outputFile`。

**`Read`**:行号 `cat -n` 格式;CRLF 对模型不可见(行尾 `\r` 去掉,文件以换行结尾时不多出一个空行);BOM 去掉。除 2.x 的 2000 行 / 2000 字符 / 8MB 外,新增 6 万字符的输出预算:到了就停并在末尾给出 `offset=` 继续(而不是让循环层的 64K 截断在中间一刀切掉),结果标 `Truncated`。
**`Write`**:写入走「同目录临时文件 + 改名」(失败不留半截文件),已有文件的权限位保留;覆盖时保留文件原来的 CRLF 和 BOM(模型看不到它们);新建文件按给出的内容原样写。
**`Edit`**:`old_string / new_string / replace_all` 保持和 Claude Code 一致,另可传 `edits` 数组一次改多处(两种形式不能同传);所有编辑都相对**原文件**匹配、互不重叠、任何一处失败则一处都不改;
精确匹配失败才退到宽松匹配(NFKC、弯引号、各种破折号、特殊空格、行尾空白;折叠文本带着每个字符在原文里的区间,所以只替换匹配到的那一段,别处的行尾空白不动);
内容按 LF 归一化后匹配,写回时还原文件的主导换行(混用换行的文件会被整体归一成主导的那一种)和 BOM。
**`Bash`**:输出每路(stdout / stderr)保留**尾部** 3 万字符(两路相加加上说明文字仍低于循环层的 64K 截断线),超过时完整输出(两路按到达顺序交错)写进环境里的临时文件(POSIX `/tmp`,Windows `%USERPROFILE%\AppData\Local\Temp`,权限 0600),结果里给出路径;
输出没超过预算就不创建文件(超过落盘阈值但没截断的,命令结束后删掉)。整轮中止 = 杀整棵进程树并原样抛 `OperationCanceledException`;用户只停这一条(`BackgroundShells.StopCall`)= 一个说明「不是命令坏了、不要重试」的工具错误;
转后台(`DetachCall`)= 把进程连同已收集的输出一起交给注册表,模型拿到 shell id 和「到目前为止的输出」,之后 `BashOutput` 只读新输出。`ConcurrencySafe` 为 true(和 2.x、pi 一致:同批并行)。
**后台 Shell**:`IShellRegistry` 是工具与编排层之间唯一的接触面,默认实现 `BackgroundShells`(进程内,只依赖 `IExecutionEnvironment`):最多 8 个同时运行、跟踪 24 条、每路缓冲 256K 字符(丢最早的并报 `Dropped`)、id 只增不复用、读过即清空、
`Peek`(界面看输出尾巴,不消费)、`Changed` 事件(只在开 / 杀 / 退出时触发,输出增长不触发)、`ShutdownAsync`(会话删除 / 退出时杀光)。
**`Grep`**:原生后端用 `CommandSpec.Exec`(程序 + 参数向量,不经 shell),2.x 里 `quoteShellArg`(含 cmd 下引不了的字符就整个后端顺延)随之消失;后端用不了(没装、起不来、退出码对不上)一律顺延,绝不把失败当成「没有命中」。
托管引擎的 ReDoS 防护见 §2 的 Grep 行;匹配前每行先截到 2000 字符、整文件先做字面量预筛(只许误报不许漏报)、`multiline` 的整文件上限 256K 字符;CRLF 文件的行尾 `\r` 去掉后再匹配。

**相对设计原稿 / 2.x 的偏离**:① `Grep` 的托管 ReDoS 防护由 2.x 的 worker 隔离改为「线性时间引擎优先 + 回溯时静态筛查 + 匹配超时」(.NET 没有 `terminate` 一个正在匹配的线程的办法,但有线性引擎);`(a+)+$` 这类 2.x 一律拒绝的写法,现在能直接得到正确答案;
② `Read` 的 6 万字符预算与 `offset` 续读提示、CRLF 对模型不可见(2.x 把 `\r` 留在行尾);③ `Edit` 的 `edits` 数组与宽松匹配、保留 CRLF / BOM / 权限位(2.x 只有单处精确替换);④ `Write` 覆盖时保留 CRLF / BOM,写入是原子的;
⑤ `Bash` 输出保留尾部 + 完整输出落盘(2.x 保留开头 3 万字符),stdout / stderr 的截断各自独立;⑥ `Glob` 在项目根内时的 pattern 仍相对**项目根**匹配(沿用 2.x;在根外时相对目标目录),描述里已写明;
⑦ 校验器在 Tools 层(02 §10 第 7 条),宽松转换的范围由校验器统一,不是每个工具各写一遍。


## 9. 实现注(`src/Ncw.Agent.Extensions`,M3:权限与交互)

**一方扩展**,只引用 `Ncw.Agent.Kernel`、`Ncw.Agent.Tools`(只用 `ToolRuntime` 的根与写入围栏)、`Ncw.Agent.Environments`;命名空间 `Ncw.Agent.Extensions.Permissions` / `.Interactions`;`ArchitectureTests` 机械检查依赖方向、不碰文件和进程、不用反射序列化、不产出界面文字。

**档位**:`ask / auto / full`(以 2.x 为准;没有 `default / plan / acceptEdits`——计划模式是写入围栏,和档位正交)。`PermissionRunState.Mode` 是实时值,`StartMode` 是快照;子代理 `ForSubagent` = min(父实时值, 定义值),且不能被调得比定义更宽。
**权限扩展**(`PermissionExtension`):`Order = int.MinValue`,挂在 `before_tool`(`onFailure` = 拒绝)、`run_end`(兜底清理交互并摘掉登记)、`system_sections`(`PermissionGate.Facts`,与闸门同一个函数)。它按 `ctx.RunId` 从 `PermissionRuns` 取 Run 级事实(档位、环境、根、能否问人);没登记 = 拒绝。
**交互闸门**(`InteractionGate` + `RunInteractions`):见 §4。种类:`ToolPermission`、`AskUser`(1–4 题、每题 ≤ 8 选项、纯问答必须允许自由作答)、`PlanApproval`、`GoalProposal`(Run 正常结束后仍可回答)、`WorkflowApproval`、`WidgetSubmit`、`SshHostKey`、`SshCredential`(后两种是设备级交互,SSH 连接过程中的指纹确认与凭据提问,见 06 §10.4;`InteractionRequest.RedactsResponse` 让秘密回答不进事件;`SshInteractionTests`)。
**AI 审核**(`LlmPermissionReviewer`):无工具、128 token;任何失败、含糊回答、没配模型都是 `Unknown` → 问人。回答解析比 2.x 严(见 parity)。

### 不变量与测试对照(`tests/Ncw.Agent.Extensions.Tests`)

| # | ★ 不变量 | 测试 |
|---|---|---|
| P1 | 联网开关排第一,`full` 与 allow 规则都放宽不了;只读的联网工具开关关掉时仍拒;拒绝的话术堵死 Bash 里的 curl | `PermissionGateTests`(48 行穷举 + 顺序用例)、`PermissionExtensionTests.Full_mode_allows_but_the_network_switch…`、`An_allow_rule_cannot_reopen…` |
| P2 | 顺序即语义:deny 规则命中不运行任何钩子;钩子 deny 压过 allow 规则与档位;ask 桶 / 钩子 ask 压过 allow 与档位;强制询问跳过 AI 审核 | `PermissionDecisionTests`、`PermissionExtensionTests.Deny_rule_wins…`、`Hook_deny_beats…`、`Hook_ask_forces…`、`An_ask_rule_beats…` |
| P3 | 插件拦截器只能收紧(类型上没有 allow);钩子失败 = 弃权,权限扩展自己失败 / 没有登记状态 = 拒绝 | `PermissionExtensionTests.A_plugin_interceptor…`、`A_failing_hook_abstains…`、`Exceptions_inside…` |
| P4 | 只读工具三档都放行(不然用户学会无脑点允许);`auto` 只对破坏性问;审核器失败 / 含糊 / 崩溃不放行 | `PermissionGateTests`、`Auto_*`、`ReviewerTests` |
| P5 | 子代理:档位 min 且不能提权;审批当场拒绝(不挂起);allow 规则与只读工具仍可用;不提供 `Interact` | `A_subagents_mode_is_the_min…`、`A_subagent_is_denied_on_the_spot…`、`InteractionGateTests.A_subagent_cannot_ask…`、`EndToEndTests.A_subagent_run_is_denied…` |
| P6 | 规则:前缀不能被接续符扩写成第二条命令;线性通配;只比约定字段;建议规则一定匹配它自己这一次调用;读不懂的规则丢弃 | `PermissionRuleTests`、`RuleStoreTests` |
| P7 | 规则文件:未知键原样保留、读不懂拒绝覆盖、并行写入不丢、每桶 500 条、远程每次重读且失败即关闭 | `RuleStoreTests` |
| P8 | 记住本次选择:会话(内存)/ 项目(文件)/ 永久(注入);落盘失败不把允许变成拒绝;无项目根不提供项目作用域 | `PermissionExtensionTests.Allow_always_*`、`A_failed_write…`、`EndToEndTests.Allow_always_for_the_project…` |
| P9 | `allow_edited` 改写后的参数重新走规则检查(联网开关、deny、钩子拒绝) | `Allow_edited_*`、`EndToEndTests.Edited_arguments…` |
| P10 | 审批带执行环境标识(设备 + 环境),与 `tool_start` 的标识一致 | `InteractionGateTests.A_tool_approval_must_carry…`、`EndToEndTests.A_dangerous_command_asks…` |
| P11 | 计划模式围栏:只有改围栏文件才当作只读;与 `ToolRuntime.WriteFence` 同源 | `Plan_mode_trusts…`、`The_fence_comes_from…`、`EndToEndTests.The_plan_fence…` |
| P12 | 路径边界:allow 只认规范写法(`..` 逃不出),deny 认原始 + 规范写法;根外在审批上标出 | `An_allow_rule_matches_the_canonical_path…`、`A_deny_rule_matches_both…`、`Outside_the_project_is_flagged…` |
| P13 | Bash 子命令拆分:deny 不能靠前面垫命令 / `sudo` / `bash -c` / `$()` 绕过;危险命令只建议原样命令、不进 AI 审核 | `BashCommandTests`、`A_deny_rule_is_not_bypassed…`、`Auto_does_not_send_dangerous…` |
| I1 | 交互没有超时,只有回答或中止能结束;取消令牌 / `run_end` 以中止拒绝并发出 `interaction_resolved{aborted}` | `InteractionGateTests.There_is_no_timeout…`、`Cancelling_the_token…`、`Ending_the_run…`、`EndToEndTests.Aborting_the_run…` |
| I2 | 多端先到先得:同时回答只有一个成功,后到的得到谁 / 什么时候;同样的答案第二次也失败;版本号对不上是冲突 | `Two_controllers_answering_at_once…`(40 轮)、`The_same_answer_twice…`、`A_wrong_expected_version…`、`EndToEndTests.Two_controllers…` |
| I3 | 可查询;变更通知是全量快照、修订号严格递增、串行派发;订阅者异常不改变裁决 | `Pending_interactions_are_queryable…`、`Change_notifications_are_the_full_snapshot…`、`Notifications_are_dispatched_serially…` |
| I4 | 回答校验:逐题校验、长度不等整份退回、单选一个、去重、dismiss 放行、修订必须带反馈;校验失败不消耗交互 | `InteractionGateTests`(2.x `interaction-gate.test.ts` 全部用例) |
| I5 | 目标提案不占住 Run:正常结束后仍可回答,中止 / 出错才清理;按会话取消 | `A_goal_proposal_does_not_hold…`、`AbortWhere_*` |
| I6 | 顺序预检:审批卡片按调用顺序一张一张出现 | `EndToEndTests.Approval_cards_appear_one_at_a_time…` |

**相对原稿 / 2.x 的偏离**:① 「记住」从只有工作区一种变成会话 / 项目 / 永久三种(2.x 的 `session` 没有存放处,所以不收)。② 钩子 / 拦截器是权限扩展调用的接入口,不是 `before_tool` 上的并列处理器(见 §3)。
③ 3.0 收紧:规则比对规范化、Bash 子命令拆分、前缀落词边界、`allow_edited` 重新检查、计划围栏核对目标文件、AI 审核回答解析整句锚定;④ 新增:`BashCommand` 分级(2.x 没有)、版本号 / 冲突带「谁 + 什么时候」、全量变更通知、`RunInteractions` 的 `CanInteract`。
⑤ 交互事件不由闸门直接写进 Run 的事件流(循环层没有外部写入口):闸门暴露 `RunEvent`,编排层订阅并分配 `seq`。
⑥ **持久化接线(Host,`Ncw.Agent.Host/Providers/HostStores.cs`)**:「永久」规则存数据库 kv 表 `v3.permissions.global`(`StoredGlobalRules`,读不懂拒绝覆盖、读取失败即关闭、每桶 500 条);`IInteractionJournal` 落 `device_interactions` 表(`InteractionJournalStore`,只记种类 / Run / 版本 / 回答者,**不存工具输入等内容**;启动时把上次进程遗留的 pending 行标成 cancelled——挂起的交互活不过进程重启)。

## 10. 实现注(`ToolRegistry`,M4:统一工具注册表)

`src/Ncw.Agent.Session/Tools/ToolRegistry.cs`(命名空间 `Ncw.Agent.Kernel.Session`,实现 `IToolSource`)。内置工具、MCP 工具、(以后的)插件工具都登记在这里,会话层每回合取一次快照。

- **按 `InternalId` 存储**。同 id **同来源** = 替换(位置和外部名都不变;MCP 重连依赖这一点);同 id **别的来源** = 丢弃后来者并返回 `DroppedConflict`(内置先注册,所以谁也替不掉 `Bash`)。`ReplaceSource(source, tools)` 整批替换一个来源(重连后少掉的工具会消失),`UnregisterSource` 按来源批量注销(MCP 服务器被关 / 删除)。
- **外部名**(模型看到的名字)只含 `[a-zA-Z0-9_-]`:先把别的字符换成 `_`;超过 64、空、或与别的工具冲突时变成 `前缀_<fnv1a32(内部 id) 8 位十六进制>`(前缀截短到总长 64)。映射**永不改变、永不回收**:同一个内部 id 在进程生命周期里永远是同一个外部名,否则同一个会话先后两个回合模型看到的名字会变。
- **禁用**(`SetDisabled`,不要求工具已注册,先禁用后注册同样生效):不进快照,但仍然列在 `List()` 里(设置页要显示「已关」)。`IsEnabled(ctx)` 为假的同样不进快照。
- **快照只删不加**:收窄(子代理的 `SubagentToolFilter`、计划模式的 `ToolFilter`、联网开关、`depth > 0` 去掉要交互的工具)仍由会话层 `RunDraft.ToolFilter` 在它之上做,不在注册表里;正在执行的调用持有自己的工具引用,注销后不重新查找。
- **一方扩展注册的工具**(`Task` / `TodoWrite` / `EnterPlanMode` / `ExitPlanMode` / `ProposeGoal` / `Skill`)仍由会话扩展的 `RegisterTool` 进快照(每个会话一个扩展实例),**不在注册表里**。`tools.list` 要列全部:Host 用一个探测用的 `ExtensionHost`(一份扩展列表,不跑会话)把它们收出来,与注册表合并、按内部 id 去重、注册表优先(`ConversationService.AllTools`)。这解决了 Host 子代理留下的「`tools.list` 只列内置工具」缺口。
- `Version` / `Changed` 在注册表被改过时推进 / 触发(界面据此知道要不要重拉 `tools.list`)。
- 权限规则按 `InternalId` 匹配,不按外部名(`Bash(...)`、`mcp__demo__echo`)。

## 11. 实现注(`src/Ncw.Agent.Hooks`,M4:Hooks)

一方扩展 `hooks`(`Order = 100`),每个工作区一个 `HooksExtension` 实例;`HooksService` 是服务入口(列表 / 诊断 / 保存 / 删除 / 启停 / 试运行、`Changed` 事件)。只引用 Kernel / Session / Extensions / Environments / Core,不引用 Protocol、Avalonia、Host、Storage;环境解析、设置文件位置、辅助模型(`IHookEvaluator`)、时钟都是注入的。

- **配置位置沿用 2.x 的两份文件**(不另起 `hooks.json`):全局 = `<profile 根>/settings.json` 的 `hooks` 段(profile 根与 `providers.json` 同一个,所以 2.x 数据接管之后原有的全局钩子原样可用),项目 = `<项目>/.next-cowork/settings.local.json` 的 `hooks` 段(与权限规则同一份文件)。只读写 `hooks` 段、保留别的键;读不懂的文件拒绝覆盖。已知取舍:`HookConfigStore` 与权限规则的 `ProjectRuleFile` 对同一个文件各用各的锁,存在极小的丢失更新窗口。
- **事件与扩展点**:`PreToolUse` → `HooksService.PreToolSource`(`IPreToolVerdictSource`,交给 `PermissionExtension(hooks: …)`;支持顶层 `updatedInput` 与 Claude Code 的 `hookSpecificOutput.{permissionDecision, permissionDecisionReason, updatedInput, additionalContext}`,改写后的入参重新走一遍权限档位 / 规则 / AI 审核链,`additionalContext` 进 `after_tool` 的 `<hook-feedback>`);`UserPromptSubmit` → `input`(只对顶层 Run、非内部输入;阻断 = 输入不落库、Run 以 `session.inputBlocked` 错误结束,`additionalContext` 进头块不进记录);`PostToolUse` → `after_tool`(只追加反馈、翻转 `IsError`);`PreCompact` → `before_compact` 闸门(传入 `trigger` / `custom_instructions`,退出码 2 或 `deny` 取消压缩);`SubagentStart` → 子代理 `before_run`(传入 `agent_type`,`additionalContext` 注入子代理 `HeadSections`);`Stop` → `turn_end` 闸门(阻断 = 内部消息 `Stop hook feedback:\n<reason>`、本回合继续,`stop_hook_active` 取自 `TurnEnd.StopHookActive` 防无限循环;Order 100 先于目标 500 / 待办 600);`SubagentStop` 与没走到 `turn_end` 的顶层 Stop → `run_end`,不等待;`Notification` 订阅交互闸门的 `tool_permission`(`HooksService.AttachNotifications`)。
- **命令型**经 `IExecutionEnvironment.RunAsync` 在**项目的执行环境**里执行:stdin 一行 JSON 载荷;环境变量 `NCW_HOOK_EVENT` / `NCW_SESSION_ID` / `NCW_RUN_ID` / `NCW_PROJECT_DIR` / `NCW_HOOK_SCOPE`(`NCW_` 前缀保留,保存时拒绝)加用户配的变量;stdout 保留开头 8 KB、stderr 4 KB,超限后仍持续读管道;超时杀整棵进程树;退出码 0 读 stdout 的 `{decision, reason, additionalContext, updatedInput}` 或 `hookSpecificOutput`,2 = 阻断,其它退出码 / 超时 / 启动失败 / 连接中断一律**放行**并记入最近 20 条诊断(失败策略)。
- **环境变量的值是密钥**:只写不读(`hooks.save` 里值为 `null` = 沿用已存的);DTO、诊断、试运行输出里抹掉;**出站前统一抹掉钩子的输出**——脚本可能把变量回显进 stderr 当作阻断原因,而阻断原因会进模型上下文(`HooksService.RunEventAsync` 对每份报告先 `Scrub`;回归测试 `HooksServiceTests.An_env_value_echoed_in_a_blocking_reason_is_scrubbed…`,端到端 `ExtensionsTests.A_PreToolUse_hook_that_exits_2…`)。
- **顺序**全局 → 项目 → 运行期(`RuntimeHookTable`,按会话的内存表),不去重,首个阻断短路(同事件链中 `PreToolUse` 的 `updatedInput` 串联向下传递),合并后每事件 ≤ 50。2.x 的「目标是运行期 Stop 钩子」在 3.0 里不再成立(目标是 `turn_end` 处理器),`RuntimeHookTable` 保留给以后的一方功能,目标不经由它。
- **prompt 型**钩子的判定器是注入接口 `IHookEvaluator`;Host 已装配真实实现 `LlmHookEvaluator`(共用 Claude Code 停止条件判定系统提示词、按回合边界从后往前裁切转录 0.5 → 0.25 重试、无工具、`MaxOutputTokens = 1024`、`CompactBinding.AuxiliaryThinking(Off)`、超时 / 解析失败 fail-open 为 `Skipped`),回放场景 `hook-prompt-type-stop-blocks` 已从 pending 转正并全绿。`hooks.test` 对 prompt 型只预览 `$ARGUMENTS` 展开结果,不真调模型。
- **与权限链的接线**(§3 的第 3–6 步)由 `HookWiringTests` / `ExtensionsTests` 证明:钩子 deny 压过 allow 规则和 `full` 档位;钩子 ask 压过 allow 规则;**deny 规则命中时钩子脚本一次都不运行**(脚本写标记文件断言);钩子失败 = 弃权。

## 12. 实现注(`src/Ncw.Agent.Mcp`,M4:MCP)

### 12.1 官方 C# SDK 评估

结论:**不用官方 SDK,自己实现 MCP 客户端协议层**(约 1500 行,含 HTTP / SSE,零新依赖)。评估对象 NuGet `ModelContextProtocol` 2.4.0,在草稿项目里(`IsAotCompatible` + `PublishAot`,`osx-arm64`,.NET SDK 11.0.100,目标 net10.0)实测:

- 只用 HTTP 传输:`dotnet publish -c Release -r osx-arm64` **零** IL / AOT 警告,二进制可运行;用 `StreamClientTransport` 接内存管道(经 `IEnvProcess` 的 stdio 因此在技术上可行):`ListToolsAsync` / `CallToolAsync` 全程零警告,原生二进制实跑通过。所以**不是 AOT 不行**。
- 不采用的理由:① 2.4.0 说的是草案协议:`McpClient.CreateAsync` 先发 `server/discover`,服务器不回错误只丢弃时会卡到超时;对未知方法回 `{}` 的假服务器直接抛 `JsonException`。② 元包带 Microsoft.Extensions 的 AI / Logging / DI / Hosting / Options / Caching 等 9 个包,AOT 与裁剪面比我们要的协议子集大得多。③ 超时、取消、SSE 事件大小、stderr、同源锁定、密钥脱敏这些边界都得自己兜,包一层并不省事。④ stdio 必须经 `IExecutionEnvironment` 起进程(AGENTS.md §5),SDK 自带的进程传输用不了,要自己实现 `IClientTransport`——那就只剩下 JSON-RPC 核心可复用。
- 自实现已验证:草稿 AOT 应用引用 `Ncw.Agent.Mcp` 走配置 JSON、stdio / HTTP 传输、握手失败、内容展平等主要路径,`dotnet publish -r osx-arm64`(`TreatWarningsAsErrors`)零警告,运行正常。`ncw-agent` 本身的 NativeAOT 发布(`dotnet publish src/Ncw.Agent -c Release -r osx-arm64`)在 M4 全部接线之后同样成功,没有任何 IL / 裁剪警告(唯一的两条是 macOS 工具链的 clang 模块缓存提示)。想换 SDK 的话,把传输换成 `StreamClientTransport` + 自带 `HttpClient` 即可。

### 12.2 实现

一方扩展,不引用 Protocol / Avalonia / Host,命名空间 `Ncw.Agent.Mcp`。

- **端口**全部注入:`IMcpConfigStore`(库里 `mcp_servers` 表,2.x 的结构,一行一个 JSON,不认识的字段原样保留)、`IMcpSecretStore`(NCK1)、`IMcpEnvironments`、`IMcpHttpFactory`、`IDelayer`、`TimeProvider`、`ToolRegistry`。
- **配置与密钥**:配置只有环境变量 / 请求头的**键名**;值整张 map 存在 `credentials` 表的 `mcp:<id>:env|headers` 下(`setSecrets` 整张替换,未声明的键丢弃,删服务器时两个 kind 一起清)。`McpScrubber` 在错误、stderr、工具输出出站前抹掉密钥值和 `Bearer xxx` 里的 token(< 4 个字符的值不抹)。
- **环境**(06 §4):`WorkspaceId` 为空 = 在设备本机环境运行,所有会话可见;非空 = 在该项目的执行环境里运行(以项目根为 cwd,远程服务器经 `OpenTcpAsync`,仅限配置的那个源),且只对该项目的会话可见(快照过滤 + 执行时再挡)。
- **连接生命周期**:每台服务器一个后台循环(解析环境、起传输、握手、`tools/list`、注册、等待关闭)。失败 / 断线按 `ReconnectDelays` 退避(1 / 2 / 4 / 8 / 16 / 32 / 60 / 60 秒,用尽后等手动重连;`IDelayer` 注入,测试不睡真的时间);**不重试的配置性失败**:命令不存在、项目缺失、协议版本不符、401 / 403、启动失败。所有改动经一把异步锁,同 id 同时只有一条连接;断开顺序是先注销工具、再关连接;重连是 `ToolRegistry.ReplaceSource`,外部名不变;`tools/list_changed` 触发重列(合并并发)。`StartAsync` 在后台并行连接,**不阻塞设备就绪**。
- **工具**(`McpTool`,不可信输入的唯一收口):名字过 `^[a-zA-Z0-9_.-]{1,128}$` 后才拼 `mcp__<server>__<tool>`;描述加 `[MCP:<name · 描述>]` 前缀,去控制字符并截断到 4096;schema > 64 KB 的工具被拒绝,不是 `type:object` 的退回 `{"type":"object"}`;**不采信 annotations**:`ReadOnly = false`、`Destructive = true` 一律保守,`NeedsNetwork` 由传输决定(stdio = false,远程 = true);参数原样转发(`PrepareArguments` 要求对象);调用 60 秒上限,到点发 `notifications/cancelled` 并收成工具失败,用户中止原样抛;输出上限 6 万字符(超出置 `Truncated`),图片 / 音频 / 资源块转成英文说明而不是丢弃;`Details` 带 `environment{deviceId, environment, name, type}`。权限完全走权限扩展,审批卡片点名这个工具(外部名 = 内部 id)。被拒绝的工具(名字 / schema / 重名 / 与别的来源冲突)照实列在状态里(`mcp.tool.*` key)。
- **传输**:stdio(经 `IExecutionEnvironment.OpenProcessAsync` + `StdinMode.Pipe`,换行分隔 JSON,单行上限 8 MB,stderr 只留 4 KB 尾部,关闭时先关 stdin、500 ms 宽限后杀进程树);Streamable HTTP(POST + JSON / SSE 响应、`Mcp-Session-Id`、`MCP-Protocol-Version`、GET 事件流、DELETE 结束会话、会话 404 视为过期);旧版 HTTP+SSE(`endpoint` 事件必须同源)。HTTP:只允许 http / https、拒绝带 userinfo 的 URL、不拦内网地址(本地服务器常见,与 2.x 一致)、不跟随重定向、不用 cookie、证书校验保持平台默认,用户配置的头改不了协议头。协议版本协商 2025-06-18 / 2025-03-26 / 2024-11-05。
- **状态**:`Snapshot()` 返回全量快照 + 单调 `Rev`,`Changed` 事件在锁外触发,Host 把它发布到 `mcp` 主题(09 §8.2)。状态里只有稳定 key 和结构化参数,没有服务器的错误原文。
- **未完成**:OAuth 授权的远程服务器、sampling / roots / elicitation(声明了空 capabilities)、进度通知、资源 / 提示的 `list_changed`。
- **偏离**:`testConnection` 不留常驻连接、不动注册表和状态(2.x 的测试会保持连接);项目绑定的服务器启动时和全局的一起连接(2.x 跳过它们);2.x 的 stdio 子进程环境变量只放行 `getDefaultEnvironment()`,3.0 的 `CommandSpec.Environment` 只能追加,子进程继承 Agent 的完整环境。


## 可视化工具(`src/Ncw.Agent.Widgets`:visualize_*、载荷消毒、静态快照、动作)

**范围**:Agent 侧与协议。客户端渲染(WebView 里的外壳、`window.ncw` 宿主桥、手机端静态快照的呈现)**已在 M8 做完**,见 [03 §8](../03-ui-layout.md#8-内联可视化m8webview-评估与渲染宿主);本节「宿主桥契约」是它的来源。2.x 的对应物:`tool/builtin/visualize.ts`、`widget-compile.ts`、`visualize-guidelines/*`、`shared/domain/widget.ts`、`main/widget-host.ts`(动作)、`ipc/widget.ts`、`net/widget-protocol.ts` + `widget-shell/*`(渲染宿主,不在 Agent 里)。依赖:Kernel / Tools / Extensions / Environments;转录与动作执行是注入的接口(Host 实现:`HostWidgetTranscript`、`WidgetActionRunner`)。

**工具**(来源 `builtin`,注册表自带「可被禁用」;名字与 2.x 一致):

| 工具 | 要点 |
|---|---|
| `visualize_read_me` | 按模块返回设计规范原文(`diagram / mockup / interactive / chart / art`,至多两个,共享段落只出现一次;`ReadOnly`,不联网)。规范正文是 C# 常量字符串(`Guidelines/*.cs`,逐字取自 2.x,AOT 下不读资源)。超过循环层 64K 输出线的组合(`diagram + chart`)明确失败并给出每个模块的体积,而不是让规范在中间被截断 |
| `visualize_show_widget` | 收 `html` / `svg` 载荷(≤ 128K 字符),产出内联卡片。`title`(snake_case,≤ 64)、`loading_messages`(1–4)、`interactive`、`actions`(≤ 16)。`ReadOnly`,`NeedsNetwork = true`(它自己不出网,但产出物里的 CDN 脚本会:事实描述,联网开关只管 `WebFetch` / `web_search`,和 2.x 一致),`ConcurrencySafe = false` |
| `visualize_edit_widget` | 按标题在转录里找上一版,精确替换(旧串必须恰好出现一次或 `replace_all`),产出新卡(上一版留在原处;动作继承,交互模式不继承)。编辑作用在**消毒后的源码**上 |
| `visualize_update_widget` | 给已有的卡推一份新数据(≤ 64K 字符),只走卡片(`kind: "widgetData"`),不进模型上下文 |

**卡片 Details**(`kind: "widget"`,只给界面,永远不发给模型;每份带 `environment`):`title`、`format`(`html` / `svg`)、`code`(消毒后,桌面渲染)、`snapshot`(**手机端降级**:无脚本、无 `link`、无外链资源的静态 HTML / SVG)、`size`(根 `<svg>` 的 `width` / `height` / `viewBox` 尺寸提示)、`interactive`、`submitted`(交互模式提交后的值)、`actions`(声明原样)、`findings`(消毒拿掉了什么:规则 id + 例子 + 次数,界面按 id 翻译)。给模型的内容只有「已渲染,不要用文字重复」加上消毒说明(拿掉了什么、怎么改),**不回灌代码**。

**消毒(`Sanitizing/WidgetSanitizer`,3.0 新增:2.x 只靠渲染宿主的 CSP)**——纵深防御的第一层,真正的边界仍是渲染宿主的沙箱 iframe(不透明源,`allow-scripts` 无 `allow-same-origin`)与 CSP。手写分词器 + 白名单(不用正则剥标签,不用反射型库),**没有危险物的载荷逐字不变**(edit 的 `old_string` 靠它对得上):

- 标签:HTML、SVG 各一张白名单,未知标签只去标签、留文本;`iframe / frame / object / embed / applet / base / meta / template / math / xmp / noembed / noframes / noscript / plaintext / foreignObject` 连内容一起拿掉(原始文本元素按 HTML 规则读到第一个结束标签);`html / head / body / title` 只去标签;注释、CDATA、`<?…>` 一律去掉;孤立的 `<` 转义成 `&lt;`(去掉注释 / 标签后不会把相邻文本拼成新标签);`<svg>` 里的 `<script>` 拿掉(脚本放在 HTML 部分);SVG 与 HTML 的上下文切换(HTML 的 breakout 标签、`desc` / `title` 集成点)按规范跟踪,`<style>` 在两种解析模式下结果相同(块内 `<` 转义成 `\3c`);嵌套深度上限 256。
- 属性:所有 `on*` 事件处理属性、`srcdoc / formaction / action / nonce / is / http-equiv`、未知命名空间;**属性值先解码实体**(数字引用不带分号也解、`&Tab; &NewLine; &colon;`)、去掉控制字符和空白、小写,再判方案:`javascript: / vbscript: / livescript: / blob: / file:`、非图片 `data:` 一律拿掉(**所有属性**都过这一道,不只 `href`);`a` 的 `href` 只许 `#` / http(s) / mailto / tel;`img src` 与 SVG `image / feImage` 只许图片 `data:`(外链图片会泄露「谁在看」,拿掉并告诉模型);`use` 等其余 `href` 只许 `#id`;`animate / set` 的 `attributeName` 指向 `href` 或 `on*` 时整个元素拿掉。
- 脚本 / 样式的外链:`<script src>` 与 `<link rel=stylesheet>` 只许 https 的 `cdnjs.cloudflare.com / cdn.jsdelivr.net / unpkg.com / esm.sh`(精确主机名、无用户信息、默认端口;与渲染宿主 CSP 同一张表,设计规范里 Chart.js / D3 / mermaid 的例子引用的就是它们);`<script type>` 只许空 / `text/javascript` / `module` / `application/json`;**内联脚本保留**(widget 的交互靠它;沙箱 + CSP 的 nonce 才是边界,服务端不尝试分析 JS)。
- CSS(`style` 属性与 `<style>` 块):在还原了转义、去掉了注释的文本上判定,`@import`、`expression()`、`behavior` / `-moz-binding`、`image-set()`、`javascript:`、外链或非图片 `data:` 的 `url()` 任一出现就整个属性 / 整个块拿掉(`url(#id)` 与图片 data URL 放行)。
- 静态快照(`SanitizeMode.Static`)再加:所有 `<script>`、`<link>` 一律去掉。
- 规则 id:`element.forbidden / element.unsupported / element.depth`、`attr.event / attr.forbidden`、`url.scheme / url.external`、`script.external / script.type / script.svg / script.static`、`link.external`、`css.unsafe`、`animate.href`。

**交互模式**(`interactive: true`):先把卡片作为实时卡片推出去(`ToolProgress.Details`),再经交互闸门挂起一次 `WidgetSubmit`(`callId` + `title`),等 `widgets.submit`(**先到先得**,和 `interactions.respond` 同一个闸门;值 ≤ 32 KiB,超限由闸门拒绝)。提交值作为这一次工具调用的结果回去,开不了新的一轮;关掉不交 = 明确失败(「不要猜答案」)。**子代理里不提供**(交互模式与 `actions` 都是):在工具里按 `ctx.Depth` 拒绝并说明——可视化工具本身在子代理里仍可用(非交互),所以 `SubagentExtension.InteractiveToolIds` 不变(只含 `AskUserQuestion` / `ExitPlanMode`:那两个整个工具都要摘)。

**动作与 `widgets.runAction`**(2.x `widget-host.ts`):动作 = `{id, label, tool, input, params}`,工具与入参在 `show_widget` 那次调用里定死;参数只有**枚举**与**有界数字**两种(没有自由字符串:参数会被代进命令行),`{{name}}` 只代进字符串叶子;不许做动作的工具:`AskUserQuestion / ExitPlanMode / EnterPlanMode / ProposeGoal / Task / Workflow / visualize_*`(会挂起或派 agent)。`runAction(sessionId, callId, actionId, paramsJson, confirmed)` 的三条不变式(同 2.x):① 动作的定义**只从转录里取**(控制端和 widget 改不了「要跑什么」;交互模式还挂着、结果不在转录里时取调用入参,并再校验一遍);② **非只读动作第一次一定问**(哪怕档位是 `full`),问的是绑定之后真实的工具与入参 + 执行环境标识,确认记在进程内、键 = (会话, 卡片, 工具, 入参),入参一变重新问;③ 输出不进模型上下文。判定走**和 Run 里同一条权限链**(`PermissionExtension.PreflightActionAsync`:联网开关 → deny 规则 → PreToolUse 钩子 / 拦截器 → ask / allow 规则;档位与联网开关取项目设置;没有 AI 审核),工具经项目的 `IExecutionEnvironment`(`ToolRuntime`)运行,5 分钟上限。结果 `WidgetActionResultDto`:`deny`(`Reason` = 稳定 key `widgets.action.undeclared / invalidParams / toolUnavailable / denied / sessionGone / projectGone`)/ `confirm`(`Output` = `{toolName,input,environment}` JSON)/ `result`(`Reason` null = 成功,`widgets.action.failed` = 失败;`Output` = 输出,≤ 64K 字符)。不改线上格式(没有新字段,只给 `Kind` / `Reason` / `Output` 定了取值,黄金向量不变)。

**宿主桥契约(客户端已实现,见 03 §8.4;实现里多了一个 `openUrl(url)`,外链经确认框交系统浏览器,另有 `getData()` / `actions()` 等只读访问器)**:2.x 的 HTML widget 没有宿主 API(`submit` / `runAction` / 状态只在 TSX 里),3.0 去掉 TSX 之后,渲染宿主要给 HTML widget 注入一个 `window.ncw`:`getState() / setState(json)`(↔ `widgets.getState / setState`)、`submit(value)`(↔ `widgets.submit`,仅 `interactive` 卡)、`runAction(id, params)`(↔ `widgets.runAction`,先画 `confirm` 的确认框)、`onData(callback)`(↔ `visualize_update_widget` 的 `widgetData`)。没有 `sendPrompt()`(widget 通往 Agent 主循环的输入通道,2.x 同样不给)。

**偏离 2.x**:① **不做 TSX**:2.x 用 sucrase 在主进程编译 TSX,依赖 JS 工具链(AGENTS.md §6 不允许)和一整套 React 组件库(`nextcowork/widget` / `charts` / `ui`,属于 Electron 渲染层);`format: "tsx"` 与 TSX 特征的代码被明确拒绝并告诉模型改用 html / svg;`visualize_read_me` 去掉 `components` 模块。② 消毒、静态快照、`size`、`findings` 是 3.0 新增。③ `widgets.runAction` 的确认由控制端画、结果经 `Kind` 区分(2.x 的 `ask / deny / result{ok}`);没有 `ok` 字段,失败用 `Reason = widgets.action.failed`。④ 事件处理属性(设计规范里 `onclick="sendPrompt(...)"` 与 `<script onload>` 的例子)2.x 的 CSP 也拦(没有 `unsafe-inline`),这里改成**拿掉并告诉模型用 `addEventListener`**,而不是静默失效。⑤ `complete()`(widget 里单次调模型,2.x `widgetComplete`)与 `download / openFile / openUrl / copy / toast / draft` 这些宿主能力是客户端 / M8 的事,没有协议方法(`widgets.complete` 未做)。⑥ 动作执行不采集文件改动(没有 Run,改动审查卡不覆盖它)、不跑 PostToolUse 钩子。

## 13. 实现注(`src/Ncw.Agent.WebTools` 与 `AskUserQuestionTool`,M4:提问与联网工具)

**`AskUserQuestion`**(`Ncw.Agent.Extensions/Interactions/AskUserQuestionTool.cs`,`ReadOnly`、不联网):参数(1–4 题,每题 ≤ 8 个选项、可多选、可自由作答;`header` ≤ 24 字符)由 schema 挡,工具里再做 2.x 的两道:
既没选项又不许自由作答的题**在挂起之前**就失败(`AskUser.ValidateDraft` 在闸门里还有一道),选项 label 不能空 / 重复。经 `InteractionGate` 的 `AskUser` 交互挂起(无超时、多端先到先得、`expectedVersion`、取消清理),回答按 `header` + 题面一起回给模型(不只按下标),
用户关掉框 = 工具错误「不要自己假设答案」。**子代理三道闸**与 05 O21 对接:`IsEnabled(ctx) => ctx.Depth == 0`(快照里没有它,和 `SubagentToolFilter.InteractiveToolIds` 恰好那两个一致);就算漏过去,子代理的交互作用域 `depth > 0` 发起即拒绝(`CanInteract`);审批直接拒绝是另一条。
Details 只有 `kind` / `environment` / 题数,**不带回答**。装配:`ConversationService` 在内置工具之后 `ToolRegistry.Register(new AskUserQuestionTool(PermissionRuns), builtin)`(可被禁用,`tools.list` 列出)。

**`WebFetch` / `web_search`**(新项目 `Ncw.Agent.WebTools`;依赖 Kernel / Tools / Environments,不引用 Protocol / Storage / Avalonia,`ArchitectureTests` 机械检查:不碰文件和进程、不用反射序列化、网络只经 `Net/WebHttp.cs`、界面文字之外无中文字面量)。两个工具都是 `ReadOnly = true, NeedsNetwork = true`,
**联网不靠 `ReadOnly` 管,靠权限闸那张表的第 1 行**(`PermissionGate.NetworkSwitchTools` = `WebFetch` / `web_search`,「联网搜索」开关;该行排在只读放行之前,`full` 档也放宽不了)。所以:开关开着时**无须逐次审批**(只读),关着时**既不进工具快照也被拒绝**(2.x 的语义不变)。
抓取从 Agent 所在设备发出,不经项目的执行环境(SSH 项目也一样);Details 仍带项目的执行环境标识(`environment: {deviceId, environment, name, type}`),工具卡片与其它工具一致。

> **开关接线(本批补上的缺口)**:`PermissionRunState.WebSearch` 此前没有任何地方设值(恒 false),联网工具会一律被拒。现在 `ConversationRunExtension` 在 Run 开始时读**项目设置**的 `webSearch`(2.x 工作区设置,出厂 `true`,项目不存在 = false)写进 Run 状态(子代理沿父链读);
> 工具快照那一半由 Host 的 `NetworkSwitchExtension`(`before_run`,只过滤、不新增)做——开关关着时模型根本看不到这两个工具,权限扩展的拒绝是第二道。

**SSRF 防护**(03 §2 的「SSRF 防护」展开;2.x `ssrf.ts` 的全部用例移植,**行为有一处刻意相反**):

| 层 | 内容 | 实现 |
|---|---|---|
| 1. URL 字面量 | 协议白名单(只 http / https,`file://` 指路到 `Read`);拒绝带用户名 / 密码的 URL(外发通道,回显不带密码);主机名:`localhost`、`.localhost / .local / .internal / .intranet / .home.arpa / .lan / .localdomain / .corp / .home` 后缀、**单标签主机名**、末段全是数字的写法;IP 字面量按 `AddressPolicy` 判 | `UrlGuard` |
| 2. 地址范围 | IPv4:`0/8`、`10/8`、`100.64/10`(含阿里云元数据)、`127/8`、`169.254/16`(云元数据)、`172.16/12`、`192.0.0/24`、`192.0.2/24`、`192.168/16`、`198.18/15`、`198.51.100/24`、`203.0.113/24`、`224/4` 与 `240/4`;IPv6:`::`、`::1`、`::/96`、`fc00::/7`(含 `fd00:ec2::254`)、`fe80::/10`、`fec0::/10`、`ff00::/8`、`2001:db8::/32`、`100::/64`、Teredo `2001::/32`,**内嵌 IPv4 的形式按内嵌地址判**:`::ffff:a.b.c.d`、NAT64 `64:ff9b::/96`(`64:ff9b:1::/48` 整段拒)、6to4 `2002::/16` | `AddressPolicy.IsBlocked` |
| 3. 花式写法 | 十进制整数 `2130706433`、八进制 `0177.0.0.1`、十六进制 `0x7f.0.0.1`、缩写 `127.1`、末尾的点、`[::1]`、带区域 id 的 IPv6——按 `inet_aton` 的宽容度自己解析(系统解析器对它们也当地址),所以字面量筛查和解析器看到的是同一个地址 | `AddressPolicy.TryParseLiteral` |
| 4. DNS 重绑定 | **解析 → 校验 → 钉死 IP → 连接**在同一个 `SocketsHttpHandler.ConnectCallback` 里:名字只解析一次,校验这次解析出来的**全部**地址(任何一个是内网就整台主机拒绝,不挑「好的那个」),然后**直接用 IP 建立 TCP**;TLS 的 SNI / 证书校验和 `Host` 头仍用原主机名。每条新连接重新走一遍,连接按 2 分钟生命周期换新 | `PinnedConnector` / `GuardedHttp` |
| 5. 重定向 | 不让 HTTP 层自动跟随;**逐跳**重新过 1–4;同主机的跟,**跨主机停下来把新地址交还给模型**(CC / 2.x 的行为);最多 5 跳 | `WebFetchTool` |
| 6. 体量与时间 | content-type 白名单(无 content-type = 未知 = 拒绝);正文**流式**读,读到 4 MiB 就停(不是读完再截),正好等于上限的不算截断;结果最多 10 万字符;整次抓取(含所有跳与读正文)30 秒墙钟;用户停止原样抛出取消异常,不包成普通失败 | `BoundedBody` / `WebFetchLimits` |
| 7. 内容 | 抓回来的内容是**不可信输入**:去 C0 / C1 控制字符、DEL、零宽与双向覆盖字符(`U+200B–200F`、`202A–202E`、`2060–2064`、`2066–2069`、`FEFF`),孤立代理半边换成 U+FFFD | `HtmlText.StripControlChars` |

**与 2.x 的偏离(SSRF)**:2.x 在 2026-09-20 按工作区所有者的要求把「本机 / 内网」那一段筛查关掉了(`allowPrivateAddresses: true`,风险写在 `ssrf.ts`),`WebFetch` 因此能碰本机服务。**3.0 不继承这次豁免**:默认永远拒绝(AGENTS.md 的「不把设备内网交给模型」),
`WebPolicy.AllowPrivateAddresses` 只是测试用的受控策略(端到端用本机假上游),不是设置项、不经协议暴露。2.x 里「本机地址被放行」「云元数据端点也被放行」那两条测试在 3.0 里反过来(被拒)。想让模型碰本机服务,走 Bash(受审批)。
**已知限制**:`GuardedHttp` 不走代理(`UseProxy = false`):代理会让连接回调连到代理而不是目标,校验和钉死 IP 就落空;设置页的代理对联网工具暂不生效(需要「经代理时由代理侧解析、本地只做字面量 + 域名后缀筛查」的第二种模式,未做)。

**HTML 转文本**(`HtmlText`,自己实现,不用反射型库):单趟线性扫描,`script / style / noscript / svg / template` 的内容整段丢掉、注释丢掉、`<br>` 与块级结束标签换行、`<li>` 变 `- `、其它标签变空格,实体(命名 / 十进制 / 十六进制,非法码点原样保留不抛)解码,行内空白压成一个并保留换行、最多一个空行。
与 2.x 的差异:① 2.x 是几条链式正则,其中 `<(script)…>[\s\S]*?</\1>` 在大量未闭合的 `<script>` 上是平方级——3.0 记下「没有闭合标签」的结论,之后同名开标签不再去找,`200 000 个 <script>` 也是线性(有时间断言);② `a < b` 这种 `<` 后不是字母 / `/` / `!` / `?` 的当文本(2.x 会把 `< b … >` 当标签吃掉);③ 相邻的 `<li>` 之间不再多出空行;④ 字符集:响应头的 `charset` 经 .NET 共享框架自带的 CodePages 提供者解码(gbk / gb2312 / big5 / shift_jis / euc-kr 等),认不出的退回 UTF-8(2.x 一律 UTF-8,中文老站会乱码)。

**搜索提供方**(`Ncw.Agent.WebTools/Search`):
- **目录**(8 家,和 2.x 同一份):`tavily / exa / brave / serper / serpapi / metaso` 有适配器;`doubao`(火山方舟没有独立搜索接口)与 `bing`(搜索 API 于 2025-08-11 下线)标 `unavailable`、**不能被启用**(Agent 拒绝,不只是界面禁用),原因用稳定 key `websearch.unavailable.doubao / .bing` 交给控制端翻译。说明文字是界面文字,不在 Agent 里。
- **适配器**照 2.x 逐家核对过的请求 / 响应形状(Tavily Bearer + `search_depth: basic`;Exa `x-api-key` + `contents.text.maxCharacters`;Brave `X-Subscription-Token` + 取 `web.results`;Serper `X-API-KEY` + 只取 `organic`;SerpApi Key 在查询串里,**错误信息里绝不拼 URL**;秘塔完全靠 `Harvest.FindResultArray` 按形状找),字段挖掘 `Harvest` 是纯函数(首选路径 + 候选名兜底,没有 url 的结果丢掉,同 url 去重)。全部用 `Utf8JsonWriter` / `JsonDocument` 手写,HTTP 经 `IWebHttp`。
- **链**(`SearchService`):已启用 + 还能用 + **确实有 Key** 的家按 priority 升序,失败(异常 / 非 2xx / 非 JSON / 零结果)切下一家;一路上的失败**即使最后成功也带着**(填错的 Key 只有在这里才看得见);单家 15 秒超时**只中断这一家**,Run 被中断立刻往上抛;「测试」真发一次 1 条的查询,**只回通不通和延迟,不回搜索结果**(否则它就成了绕过联网开关的搜索入口)。
  工具对「失败」分三种说(没配任何服务 → 去设置里配、换查询词没用;配了但全挂 → 每家的原话;有服务应答了但没结果 → 换查询词值得一试)。
- **存放**:配置在库里 2.x 的 `search_providers` 表(`id + priority 真列 + json {id, enabled, priority}`,一行一家,库里只存「用户动过的」,读时左连接到完整目录),**整批写、一个事务**;API Key 在 NCK1 凭据库,ref `websearch:<id>`(和 2.x 同一个物理 ref,接管 2.x 数据后原有的 Key 直接可用)。**明文 Key 只有一个方向:进**——`websearch.setCredential` 写入之后任何方法都读不回它(没有 reveal),
  工具拿不到 Key(只有 `SearchService` 经 `ISearchConfigSource` 取),`websearch` 主题与工具 Details 里都没有它;清 Key 顺手把这家关掉。解不开的行(主密钥丢了)列表上仍显示「已配置」,真正用到时搜索链如实记「Key 读不出来」。
- **协议**(能力 `websearch`,09 §8.5):`websearch.list`(query,member)、`setEnabled` / `reorder` / `setCredential` / `clearCredential`(command,owner)、`test`(query,owner)、主题 `websearch`(全量 `WebSearchTopicState{Version, Providers[]}`,首帧与 `websearch.changed` 同一个类型)。黄金向量 `GoldenV1WebSearch`。
  与 10 盘点的差异:盘点把 `websearch` 的 7 个通道并入 `providers.*`;实现里拆成独立的 `websearch.*`(搜索提供方没有模型别名 / OAuth / 账号池,和 LLM 供应商共用一张方法表只会让两边的 DTO 互相迁就),`list` + 主题取代了 `list` + `websearch:changed` 事件。
- **未移植**:2.x 的「免 Key 内置源」兜底(`search/builtin/**`:公共 SearxNG 实例清单 + 直抓搜索引擎结果页 + 给结果补正文)和设置里的自建 SearxNG 地址(`DeviceSettings.BuiltinSearxngUrl` 已在设置里,但没有读它的代码)。理由:它抓的是任意第三方域名、解析 SERP 的 HTML 极脆,而且要给每个结果域名走一遍 SSRF 防护——先把「配了 Key 的付费链」做稳;
  代价是一家都没配时 `web_search` 直接说「去配置」,而不是退到免费源。需要时作为 `SearchService` 的下一级兜底加入,不改协议。

**测试**:`tests/Ncw.Agent.WebTools.Tests`(地址 / URL 筛查的 IPv4 / IPv6 / 各种写法 / 内嵌 IPv4 全覆盖,`GuardedHttp` 用假解析器验证钉 IP 与重绑定、用真的本机监听验证整条链路与 Host 头,HTML 转文本含线性时间断言,WebFetch 的 2.x 用例逐条 + 重定向到内网 / 字节流式上限 / 超时 / 字符集,搜索:`harvest.test` / `service.test` 逐条 + 各家请求形状 + 工具三种失败),
`tests/Ncw.Agent.Extensions.Tests/AskUserQuestionToolTests`(2.x `ask-user-tool.test` 逐条 + 抢答 + 子代理三道闸),`tests/Ncw.Agent.Host.Tests/WebToolsTests`(真实 Agent + `DeviceClient` + 假上游 + 假联网出口:两个控制端抢答 AskUserQuestion 只有一个成功且模型拿到赢家的答案、WebFetch 开关开着无须审批且卡片带环境标识、开关关着不下发、内网地址不到达网络、`websearch.*` 配置后 `web_search` 可用且 Key 不出设备),`tests/Ncw.Protocol.Tests/WebSearchDtoTests`(往返 + 冻结向量 + 方法表声明)。


## 14. 实现注(`src/Ncw.Agent.Browser`:浏览器工具)

### 14.1 评估:PuppeteerSharp 在 NativeAOT / 裁剪下不可行

在临时项目里(不进仓库)引用 PuppeteerSharp 25.17.1(当时 NuGet 最新)、`PublishAot=true`、`-r osx-arm64` 发布,`TrimmerSingleWarn=false` 展开警告:`IL2026` / `IL2075` / `IL2090` / `IL2091` / `IL3000` / `IL3050`,
去重后 11 类,且**都在主路径上**:`Cdp.Connection.GetMessage`(`SerializeToUtf8Bytes<ConnectionRequest>`)和 `ProcessMessage`(`Deserialize<ConnectionResponse>`)就是 CDP 收发本身,`JsonHelper.ToObject<T>` 是所有结果的反序列化出口;
`BrowserFetcher` 用 `Assembly.Location`。不满足 AGENTS.md §2「不使用依赖反射的序列化、AOT 零裁剪警告」。第二个独立理由:它自己 `Process.Start` 浏览器,绕过 `IExecutionEnvironment`(AGENTS.md §5);第三个:它的默认路径是自己下载 Chromium。
**决定**:自研最小 CDP 客户端(D15 已改写),零第三方依赖,零 AOT / 裁剪警告(项目开 `IsAotCompatible`)。

**浏览器从哪来**:只用设备上已有的 Chrome / Edge / Chromium(`BrowserLocator`:显式路径 → 环境变量 `NCW_BROWSER_PATH` → 各平台常见安装位置,macOS 含 `~/Applications`,Windows 读 `ProgramFiles` / `ProgramFiles(x86)` / `LocalAppData`,Linux 按 `PATH` 再固定位置;Chrome 优先于 Edge 优先于 Chromium)。
找不到 = 结构化错误 `browser.error.notInstalled`(模型看到的说明里明说「不会自动下载、不要重试」);显式指定的路径不存在 = `browser.error.executableMissing`(**不**悄悄退回别的浏览器)。理由:下载 Chromium 带来网络、完整性校验与签名问题,2.x 的「按需下载」在 3.0 不做;控制端按 `errorKey` 提示用户安装或在设置里指定路径。

### 14.2 结构

- **`Cdp/CdpConnection`**:一条 `ClientWebSocket`(不带 Origin、不保活),**扁平会话**(`Target.attachToTarget` 的 `flatten: true`,所有页面共用这一条连接,命令和事件带 `sessionId`)。请求 `Utf8JsonWriter` 手写、响应 `JsonDocument` 读;`id` 配对、命令超时(30 s)独立于调用方取消(取消照常抛 `OperationCanceledException`);
  `Expect(method, sessionId, predicate)` 先登记后发命令,所以「导航很快、事件先于应答」不会丢;消息上限 32 MiB(截图是最大的合法载荷);坏 JSON / 形状不对的消息忽略而不杀接收循环(对端是不可信的);断开时所有在途命令与等待者以 `CdpDisconnectedException` 失败。
- **`Launch/ProcessBrowserLauncher`**:**只经 `IExecutionEnvironment.OpenProcessAsync`** 起 `--headless=new --remote-debugging-port=0 --user-data-dir=<profile> --no-first-run …`,从 stderr 读 `DevTools listening on ws://…`(两路输出后台排空,管道写满会把浏览器挂住),20 s 内没有就杀掉并给 `browser.error.launchFailed`(带 stderr 尾巴)。Linux 下以 root 运行(容器 / 云端沙箱)才加 `--no-sandbox`。
  可执行文件探测与 profile 目录创建走 `IEnvironmentFs`;读环境变量的函数由装配方注入(项目源码里没有 `Environment.GetEnvironmentVariable`)。**只支持本机环境**:SSH 项目给 `browser.error.remoteEnvironment`(`IExecutionEnvironment.OpenLocalForwardAsync` 已有,远程浏览器 + 端口转发是后续工作,不做半成品)。
- **`BrowserService`**:每个**工作区**一个浏览器进程(同一个 profile 目录不能被两个进程同时打开),profile 在 `<数据根>/browser/<工作区 id 净化后>`(非 `[A-Za-z0-9_-]` 的字符转 `%XXXX`,不会出现分隔符或 `..`),所以 cookie / 登录态按工作区隔离;进程里若干标签页,每个工作区最多 8 个。
  标签归属 = 同工作区且(同一 Run **或同一会话**)——同一会话的新 Run 可以继续操作之前开的标签(2.x 同一条规则),子代理(另一个会话)碰不到父的标签。闲置 10 分钟的标签被回收,没有标签的浏览器再闲置 10 分钟退出;`Dispose`(Agent 关闭)杀掉全部;浏览器进程自己退出 / 崩溃时它的标签一并作废,下次 `browser_open` 重新起。
  `Browser.setDownloadBehavior: deny`:页面触发的下载不会往设备上落文件。JavaScript 对话框(alert / confirm / prompt)自动取消、`beforeunload` 放行,避免页面被挂住。
- **`Pages/BrowserPage`**(移植自 2.x `CdpBrowserPage` / `CdpPageSession` / `BrowserInputController`):页面侧程序(`SnapshotScript`,2.x `snapshot-script.ts` 逐行移植成 JS 常量)跑在**隔离世界**(`Page.createIsolatedWorld`,页面脚本碰不到 ref 表);导航销毁隔离世界;
  ★ ref 动作消费最近一次快照(一次观察只允许一次写),坐标动作要求新截图且拒绝视口外坐标;快照是不可信输入(`PageSnapshot.Normalize`:缺字段 / 非法视口抛错、树 ≤ 80000 字符、标题 ≤ 500)。导航等 `Page.domContentEventFired`(15 s),同文档导航(只改 `#hash`)不等。
- **`BrowserTools`**:`BuiltinTool` 风格(参数 schema 手写 + 宽松转换),来源 builtin、可被禁用、`tools.list` 列出;全部 `ConcurrencySafe = false`(一个页面只有一个状态,同批串行);`ToolTraits` 沿用 2.x:`browser_open` / `browser_navigate` 只读 + 联网,点击类(click / type / press / select / cua_click / cua_drag)写 + 联网,观察类只读不联网。
  Details 带执行环境标识(失败也带)与 `errorKey`;截图作为 `ImagePart`(内联 data URL;2.x 的「落成会话附件」等会话图片仓落地后接,见未完成项)。
- **权限**:沿用 2.x——浏览器工具**不属于**「联网搜索」开关管的那张精确名单(`PermissionGate.NetworkSwitchTools` = `WebFetch` / `web_search`),所以开关关着时浏览器工具照样下发;`ask` 档下只读的 open / navigate / 观察类免审批、点击类要审批(审批卡片带执行环境标识)。
- **URL 筛查**:`IBrowserUrlPolicy`,默认(`DefaultBrowserUrlPolicy`)沿用 2.x 的两条需求:`http` / `https` / `about:blank` 放行,`file://` 放行(2026-09-22:本地生成的 HTML 报告要能直接看),其余协议拒绝,`user:pass@` 一律拒绝;**不拦环回 / 内网**(2026-09-20:要能看本机开发服务器)。
  风险照实记:URL 可能来自被投毒的页面 / MCP 返回值,诱导模型打开本机没鉴权的调试端口或云元数据端点,页面内容随后经快照 / 截图回到上下文;`file://` 等于绕过 `Read` 的提示把任意本地文件读进对话。这和 `WebFetch`(拒绝内网)有意不同;要收紧就换策略(`BrowserOptions.UrlPolicy`),页面自己发起的跳转与子资源请求不在这道闸的范围内。

### 14.3 与 2.x 的对照(`browser.ts` 的 16 个工具 + `src/main/browser/*` 与它们的测试)

| 2.x | 3.0 | 说明 |
|---|---|---|
| `browser_open`(`backend: iab / headless`、`profileId`、`title`) | `browser_open(url)` | 只有无头后端:`iab`(内置浏览器面板里的可见标签)是 3.1;`profileId` 随全局 Profile 注册表不移植(3.0 是每个工作区一个 profile 目录);`title` 不移植 |
| `browser_navigate` / `browser_snapshot` / `browser_click` / `browser_type` / `browser_press` / `browser_select` / `browser_scroll` / `browser_screenshot` / `browser_cua_click` / `browser_cua_drag` / `browser_tabs` / `browser_close` | 同名同参数 | 逐条移植;描述与「观察 → 一次动作 → 再观察」的约束原样沿用 |
| (无) | `browser_wait`(等文本出现 / 固定休眠)、`browser_text`(页面可见文本) | **新增**:SPA 页面常要等渲染;只读正文比整棵可访问性树便宜 |
| `browser_user_tabs` / `browser_claim` | **不移植** | 认领用户在内置浏览器里自己开的标签——依赖内置浏览器(3.1) |
| `browser_profiles` | **不移植** | 2.x 的全局 Profile 注册表(增删、按域名默认、cookie 导入导出,`session.ts`);3.0 一个工作区一个 profile 目录,没有注册表。cookie 导入导出属于内置浏览器的设置界面(3.1) |
| `browser.test.ts`:带 ref 的真实快照 / ref 点击委托并拒绝另一个 run / 截图作为图片 / `file://` 能打开 | `ToolTests`、`ServiceTests`、真实浏览器冒烟 | 全部移植;「截图落成会话附件、`output.images` 只存 `ncw://` 地址」两条**未移植**(3.0 还没有会话图片仓,见未完成);「请求右侧工作台展开」是内置浏览器的事 |
| `input.test.ts`:ref 不存在不派发鼠标事件 / 一次变更后消费快照 / 坐标动作要求截图且拒绝视口外 / 拖拽保留每个路径点 | `PageTests` | 全部移植,另补键盘组合键顺序、输入替换旧值、select、scroll、对话框、上下文重建 |
| `aria-snapshot.test.ts`:规范化 / 预算 / 拒绝缺字段与非法视口 | `SnapshotTests` | 全部移植 |
| `manager.test.ts`:工作区与 run 隔离、同会话新 run 继续操作、file:// 放行、非 http(s) 协议拒绝 | `ServiceTests`、`ToolTests` | 移植;认领用户标签 / `clientTabId` 幂等注册 / Profile 增删 / `openRightPanel` **不移植**(同上,内置浏览器 / Profile 注册表) |
| `bindings.test.ts`(Electron webview 的分区绑定) | **不移植** | 3.0 没有 webview,页面就是 CDP target |
| `headless.ts`(Playwright 持久化 context、`chrome` / `msedge` 渠道、**缺浏览器时下载 Chromium**) | `ProcessBrowserLauncher` + `BrowserLocator` | 持久 profile、优先用已安装的 Chrome / Edge 沿用;**下载 Chromium 不移植**(见 14.1) |
| `shared/domain/browser.ts` 的 `browserPartition`(工作区 × Profile 分区) | `BrowserService.ProfileDirectory` | 工作区分区沿用,Profile 维度不移植 |

### 14.4 测试(`tests/Ncw.Agent.Browser.Tests`)

协议级(`FakeCdpServer`:HttpListener + 真 WebSocket,脚本化应答、记录命令、主动推事件 / 断连):命令配对与乱序应答、params / sessionId 上线格式、错误应答、事件登记先于命令、断连使在途命令与等待者失败、命令超时与取消的区别、坏消息忽略。
页面语义(`PageTests`)、快照校验(`SnapshotTests`)、运行时(`ServiceTests`:归属、工作区隔离、每个工作区一个进程、并发打开只起一次、标签上限、闲置回收、崩溃后重启、远程环境拒绝)、工具(`ToolTests`:URL 筛查、参数校验、traits 与 2.x 一致、Details 带环境标识与 errorKey、取消原样传播)、
浏览器定位(`LocatorTests`)、依赖与源码约束(`ArchitectureTests`:不引用 Protocol / Storage / Avalonia / PuppeteerSharp、不碰文件 / 进程 / 环境变量、不用反射序列化、不下载)。
**真实浏览器冒烟**(`RealBrowserTests`,设备上找得到 Chrome / Edge / Chromium 才跑,否则 `Assert.SkipUnless` 跳过并写明原因;只打本机 `HttpListener` 的页面,不联网):打开、快照、点击、输入、下拉、按键、截图(真 PNG、尺寸等于视口)、坐标点击、导航、关闭;
两个工作区的 cookie 互不可见;`alert` 不会把页面挂住;不存在的主机 = 导航错误且不留标签;`Dispose` 后浏览器进程确实退出。Host 端到端(`Ncw.Agent.Host.Tests.BrowserToolsTests`):`tools.list` 列出、联网开关关着时仍下发、没装浏览器的结构化错误、凭据 / 脚本协议 URL 不会拉起浏览器、写类工具在 `ask` 档走审批。

### 14.5 偏离设计原稿与未完成

- **偏离 D15**:不用 PuppeteerSharp(见 14.1,已改 D15 / 01 / 05)。
- **标签的生命周期**:原定「Run 结束时杀进程」改为「按工作区的进程 + 标签闲置回收 + Agent 关闭时杀」——同一会话的多轮对话要能接着操作之前打开的页面(2.x 同一条规则),Run 一结束就关会让登录流程做不下去;进程不会长期残留(10 分钟闲置)。
- **未完成**:① 远程(SSH)环境上的浏览器(要「在远端起浏览器 + `OpenLocalForwardAsync` 转发 CDP 端口」,目前给结构化错误);② 截图落成会话附件(等会话图片仓,当前内联 data URL,单张上限 8 MiB);③ 内置浏览器面板与 `iab` 后端、用户标签认领、Profile 注册表与 cookie 导入导出(3.1);④ 设置页里「浏览器路径」的入口(`BrowserOptions.ExecutablePath` 已有,尚未接设置;环境变量 `NCW_BROWSER_PATH` 可用);⑤ 与 `Ncw.Agent.WebTools` 的 `UrlGuard` 的对接(`IBrowserUrlPolicy` 是接缝,默认实现保持 2.x 放行内网的语义,由 owner 决定是否收紧)。
