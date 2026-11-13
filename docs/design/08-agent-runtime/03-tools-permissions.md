# 08.03 · 工具、权限与交互

## 1. 工具协议

```csharp
public sealed record ToolInfo(
    string InternalId,            // 稳定 id:内置名 / mcp__<server>__<tool> / plugin__<publisher>_<name>__<tool>
    string Description,           // 不可信文本:去控制字符,截断到 4096
    JsonSchema InputSchema,       // 源生成器从参数类型导出
    ToolTraits Traits,
    string? PromptSnippet = null, // 系统提示词工具段的一行说明(取自 pi)
    IReadOnlyList<string>? PromptGuidelines = null,
    ToolSource Source = default);

public sealed record ToolTraits(bool ReadOnly, bool Destructive, bool NeedsNetwork, bool ConcurrencySafe = true);
// ReadOnly / Destructive / NeedsNetwork 必须显式声明,没有默认值(2.x ★:默认值会让下一个联网工具悄悄绕过开关)

public interface IAgentTool
{
    ToolInfo Info { get; }
    bool IsEnabled(ToolContext ctx) => true;
    ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct);
}

public sealed record ToolResult(
    IReadOnlyList<Part> Content,  // 给模型:文本和图片
    JsonElement? Details,         // 只给界面:diff、卡片、结构化数据。永远不发给模型(取自 pi)
    bool IsError = false,
    bool Terminate = false,       // 本批所有结果都 Terminate 时结束 Run(对应 2.x 的 stopRun)
    bool Truncated = false, long? OriginalBytes = null);
```

**`ToolContext`**:`RunId`、`CallId`、`SessionId`、`Depth`、`PermissionMode`(Run 开始时的快照)、
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
| `LS` / `Glob` / `Grep` | 后端依次尝试 `rg`、`git grep`、`grep`,最后是托管实现;200 条结果、5 秒预算;正则先做静态 ReDoS 检查,托管正则用 `Regex` 的匹配超时 |
| `Bash` | 每次新 shell,stdin 关闭;默认 120 秒、最长 600 秒;输出**保留尾部**,完整输出存临时文件并在结果里给出路径(pi);支持 `run_in_background`,没有后台 Shell 能力时显式失败 |
| `BashOutput` / `KillShell` | 后台 Shell;分成两个工具,计划模式下仍能查看构建输出 |
| `TodoWrite` | 无状态,记录本身就是状态;最多 40 项 |
| `WebFetch` / `web_search` | SSRF 防护(协议白名单、拒绝带凭据的 URL、跨主机重定向交还给模型);受联网开关控制 |
| `Skill` | 返回 Skill 正文,附「不可信内容」边界说明 |
| `AskUserQuestion` | 1–4 个问题,每个最多 8 个选项 |
| `EnterPlanMode` / `ExitPlanMode` | 见 [05 §6](05-orchestration.md#6-计划模式) |
| `Task` | 启动子代理,见 [05 §5](05-orchestration.md#5-子代理) |
| `ProposeGoal`、定时任务 4 个、浏览器约 16 个、`visualize_*`、图片和视频生成、`Workflow` | 按 2.x 移植;其中会改共享状态的标记 `ConcurrencySafe = false` |

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
    allow_always(仅工作区范围)                   → 写入 allow 规则;写失败本次仍放行
```

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
record SshHostKey(EnvironmentRef Env, string Fingerprint, bool Changed);            // 3.0 新增
record SshCredential(EnvironmentRef Env, CredentialPrompt Prompt);                  // 3.0 新增
```

**交互闸门**:

- 只在 Run 处于运行中时可以发起;发出 `interaction_request`,返回一个等待回答的任务。
- **没有超时**,只有回答或中止能结束它。中止和 `run_end` 时以中止拒绝并发出 `interaction_resolved{aborted}`。
- 回答要校验(数量、非空、选项匹配、修订必须带反馈、`allow_always` 只接受工作区范围)。
- 可以同时挂起多个(多个 Run、多个子代理的父 Run)。
- 挂起的交互必须**可以查询**,不能只是一个悬挂的任务:客户端重连后通过快照拿回(2.x ★)。
- **多端先到先得**:挂起的交互持久化在 SQLite,回答用原子的 `UPDATE … WHERE status='pending'`,影响行数不为 1 就返回冲突。
  后到的客户端看到「已在另一台设备上处理」。
- 待处理交互数计入 `activeRuns` 广播(含所有子 Run 的),项目栏角标和手机推送都读这个数。

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

- [ ] 工具特征必须显式声明
- [ ] 快照每回合一次,广播与查找用同一个
- [ ] 内置工具不可被替换
- [ ] deny 规则命中时不运行 Hook;Hook 拒绝优先于 allow 规则;插件拦截器只能收紧
- [ ] 子代理不能发起交互(快照过滤 + 不提供 `Interact` + 审批直接拒绝,三处对称)
- [ ] 交互无超时、可查询、多端先到先得
- [ ] PostToolUse 只能追加
- [ ] MCP 工具一律按破坏性处理
