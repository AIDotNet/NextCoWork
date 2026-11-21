# 08.04 · 会话与上下文(`Ncw.Agent.Kernel.Session`)

`AgentSession` 负责一次 Run 的「业务外壳」:装配扩展、组装每回合的请求、压缩、落库、修复孤儿调用。它把这些作为 `LoopHooks` 交给循环层。

实现位置:`src/Ncw.Agent.Session`(命名空间 `Ncw.Agent.Kernel.Session`,设计里它是内核的一层,但单独成项目)。理由:循环层(`Ncw.Agent.Kernel`)按设计「只依赖模型层」,
不引用 Storage 和 Environments;会话层要同时碰存储(`SessionRepository`)和执行环境(读项目指令、重读文件、git 状态),所以放在 Kernel 之上,依赖 `Kernel`、`Llm`、`Storage`、`Environments`。
依赖规则不变:下层不引用上层,会话层不知道协议、不引用 Avalonia 和客户端项目、不产出界面文字(错误只带英文诊断 + `MessageKey` + 结构化参数;发给模型的文字是英文常量)。
JSON 全部用 `Utf8JsonWriter` / `JsonDocument` 手写,不走反射序列化。已按本文逐条实现并用真 Storage + 假上游 + 假工具验收(`tests/Ncw.Agent.Session.Tests`,§7 的表把每条不变量对到测试);
与本文最初草稿不同的地方在各节用「实现注」标出,汇总在 §9。

## 1. 会话存储

沿用 2.x 的 SQLite schema(决策 D13),不采用 pi 的 JSONL 会话树(R9)。

| 表 | 用途 |
|---|---|
| `sessions` | 会话;`parent_session_id` 是子代理会话的真实父关系(永远不解析 id 里的 `:sub:`) |
| `messages` | `(session_id, ordinal)` 唯一;`parts` 是 JSON;`run_id`(重写时 COALESCE);`internal` |
| `messages_fts` | 全文搜索 |
| `runs` | Run 记录 |
| `session_input_receipts` | 已消费的输入回执;和消息在**同一个事务**里写;对消息没有外键,所以压缩或编辑后输入也不会被重放 |
| `usage_records` / `usage_daily` | 用量;`cost` 为 NULL 表示未知,0 表示免费 |
| `file_change_sets` / `file_snapshots` | 每个 Run 的文件前后内容,用于撤销 |
| `device_interactions` | 挂起的交互(多端先到先得) |
| 3.0 新增 | `profiles`、`environments`、`known_hosts`、`push_registrations`、`catalog_meta` 等,只做增量迁移。**迁移 39**:`messages.meta`(可空 JSON 文本)存助手消息的来源(api / provider / 上游模型)和停止原因,2.x 没有这些信息 |

- 迁移只追加、永不修改已有迁移。
- **只在消息完成时写**(R6):助手消息在 `message_end` 提交;中止时提交已有部分,丢弃没收尾的工具调用。
- 子代理不单独建表:子会话 id 为 `<父会话>:sub:<子 RunId>`,父会话里 `tool_result.subagent` 是持久回执。
- 待办没有表:从记录里最近一次 `TodoWrite` 推出来。
- 会话分叉:复制前缀消息(到某一轮为止,含紧随提问的工具回执)到一条**新的顶层会话**,不做原地分支;「复制会话」是同一动作取整段。
  与 2.x 一致,分叉**不设** `parent_session_id`:该列是子代理转录的过滤口(列表、搜索、导出、清理都带 `parent_session_id IS NULL`),
  设了它这条分支就会从侧边栏消失。元数据(模型、供应商、模式、思考档位、提炼来源)沿用源会话,消息换新 id,不带 Run 归属、不留消费回执。
  整个分叉是一个事务;托管图片的搬家(改写 `ncw://` 引用)由会话层通过改写回调完成,存储层不读附件文件。

### 1.1 存储层的边界(`Ncw.Agent.Storage.Sessions`)

- **只存取 2.x 兼容的行与 JSON 文本**:`messages.parts` 以原文字符串进出(`MessageRecord.PartsJson`),存储层只在判轮次边界
  (`IsToolResultOnly` / `IsTurnStart`)、全文索引、编辑文字时读 `type` / `text`。与 Llm 的 `Message` / `Part` 互转、
  旧记录的读取适配(例如 Anthropic 透传补协议标签)属于会话层(`MessageCodec`),存储层不引用 `Ncw.Agent.Llm`。
  写出的 JSON 形状是 2.x 的(键名、类型标签、非 ASCII 不转义),读是宽容的(坏 JSON、未知块跳过,不抛);`meta` 列原文进出、Storage 不解释(`MessageRecord.MetaJson`)。
- **作用域收窄写在 SQL 里**:会话没有自己的账户列,归属取决于它挂的工作区(`workspaces.owner`),同一个事实不存两处。
  看不见的会话当作不存在(读返回空 / null / false);写撞上别的作用域(目标工作区、已有的同 id 会话)抛 `StorageException`。
  工作区行不存在、或 id 为空串的会话只有 `local` 作用域可见。
- **所有多语句操作经 `AgentDatabase.InTransaction`**;消息、全文索引、用户消息的消费回执在同一个事务里,附件登记由
  `IMessageWriteObserver` 在同一事务里接进来(附件仓库实现)。
- **按 id 改写**(编辑提问、删一整轮、删一条回复)在 Agent 的完整历史上做,删除单位是「跨度」(`HistoryEdit`),
  并在同一个事务里让导入映射脱离同步;客户端只持有一页,永远不整段回写。
- **孤儿清理**:`ReplaceHistory` 里按会话清理全文索引孤儿(2.x 同);`SweepOrphans` 是 3.0 新增的全库版本
  (索引映射指向已不存在的消息、过期或结构对不上且会话已不在的 `session.input.*`),启动或「整理存储」时调用。
- 默认标题占位 `新对话` 沿用 2.x 的存库值(领域数据,不是界面文字);界面按 `titleSource == "default"` 显示本地化名字。

## 2. 每回合的请求组装

`ContextAssembler.Assemble(AssembleInput)` 是**纯函数**:时钟、工作区根目录、git 信息等全部作为参数传入。输出 `LlmRequest`、`ContextUsage`(含归因)和压缩判据读的那个数。
**实现注**:提示词缓存的断点由模型层编码器放(顶层 `cache_control`,断点在 system 或最后一个工具,01 §3),会话层的职责是**保证前缀稳定**:运行级常量(日期精确到天、环境事实、项目指令、git 状态)在 Run 开始时定死,
同一个 Run 里两个回合的请求,system 逐字相同、消息前缀逐字节相同(`SessionTests.The_prefix_of_the_request_is_byte_identical…`)。

**系统提示词**(稳定部分,按顺序拼接,空段跳过):

1. 基础提示词:按 pi 的思路精简,只写行为规则。
2. 子代理时:`# Your role` + Agent 定义正文(**追加**,不替换)。
3. `# Environment`:工作区根、操作系统、shell、**执行环境**(本机 / SSH 主机事实)、日期、权限说明(与权限闸门同一个函数生成)。
4. 个性化:关于用户、用户的长期指令。
5. 模式提示词(计划模式等)。
6. 工具段:各工具的 `PromptSnippet` 和 `PromptGuidelines`(取自 pi):`Available tools:` 一行一个(只列有 snippet 的),`Guidelines:` 去重后合成一组规则。
7. `# Available Skills`:只列名字和描述,按名字排序,总长 16KB 封顶;**正文永不进系统提示词**。

**实现注**:扩展经 `ContributeSystemSection(name, …)` 贡献节;名为 `permissions` 的紧跟在 `# Environment` 之后(权限说明是环境事实的一部分),其余排在工具段之后(Skills 目录在这里)。
个性化在 Environment 之后、模式提示词之前(模式说「你现在只有只读工具」必须压得住一条「别问了直接改」的全局提示词)。

**消息装饰**(只装饰发出去的副本,存储的记录永不修改):

| 位置 | 内容 | 为什么放这里 |
|---|---|---|
| 第一条用户消息的开头(在开头的工具结果之后) | 项目指令(AGENTS.md)、Hook 和插件给的上下文 | 不随回合变化,保住缓存前缀;放在工具结果前面会 400 |
| **本次 Run 的输入消息**的末尾(不是最后一条消息) | 状态块:计划文件、git 状态、待办 | 放在最后一条消息上,每个工具回合都会破坏缓存。**实现注**:显式锚到输入消息的 id(`ReminderContext.TailAnchor`);2.x 按「最后一条非工具回执的用户消息」扫,中途插进引导消息就会漂,没有锚点(继续跑)时才退回那个扫法 |

- 项目指令(按 pi 的规则兼容读取,Q16):
  - **每个目录只取一个文件**,按 `AGENTS.override.md` → `AGENTS.md` → `CLAUDE.md` 的顺序取第一个存在的。
  - **读哪些目录**:全局目录(应用数据根)→ 从文件系统根到项目根之间的各级祖先目录 → 项目根。项目是 git worktree 时,
    同时检查主仓库根(与 pi 一致)。SSH 执行环境下,祖先目录在远程主机上通过执行环境读取。
  - **拼接顺序**:全局在前,然后由外到内,离项目越近越靠后、优先级越高;各段之间用分隔线注明来源路径和「后面的优先」。
  - **预算**:单文件 64KB;总共 32KB,按由内到外分配(项目根的文件先占预算),超出部分截断并注明。
  - 每个文件只能解析到它自己所在目录之内(realpath 围栏,防止符号链接把别处的文件带进来)。越界的文件跳过并记一条结构化诊断(`instructions.escapesDirectory`),
    **诊断里不回显目标路径**(那是逃逸想探的东西);越界的 `AGENTS.override.md` **不回落**到同目录的 `AGENTS.md`(override 的意图是「用我而不是下一个」)。
  - 内容去控制字符(保留换行)、中和 `<system-reminder>` 标签(全角,不删字)、加不可信边界(pi 不做这一步,3.0 保留 2.x 的做法;边界声明由装配时的指令块带上,「cannot widen your permissions」逐字保留)。
  - 2.x 从其他工具导入的指令副本仍然附在最后,本地文件优先。
  - **永不抛**(连接断开除外:那是环境的故障,不能当成「没有指令」);全局目录在设备本地,不在项目的执行环境里(SSH 项目的全局指令仍然来自本机设备),所以 `GlobalInstructions` 自带环境和路径。
- git 状态每个 Run 只算一次(最多 4 次 git 调用、2 秒超时,经执行环境),标注「截至本次运行开始」;分支名和提交标题和项目指令走同一条消毒路径。读不到(不是仓库 / 没装 git / 超时 / 中断)就没有这一段,不写「Git: 未知」。
- 图片引用 `ncw://` 只在发出去的副本里解析成 data URL;校验属于当前会话、路径围栏、大小、文件头魔数。

**token 估算**:拉丁文字 4 字符 1 token,CJK 1 字符 1 token,每部分 / 消息 / 工具有固定开销,图片 1600。
实际使用 `max(估算值, 上游最近一次返回的真实值 + 之后新增部分的估算)`。**触发压缩和硬性校验读同一个数**。
**实现注**:文字估算和窗口 / 阈值公式在 `Ncw.Core.Domain`(`TokenEstimate`、`ContextWindows`),客户端状态行共用(规则 3)。真值锚点由循环层给:`TurnContext.LastUsage` + `LastUsageAnchor`
(发出那次请求时转录的最后一条消息的 id);会话层在当前上下文里找锚点,找不到(第一回合、压缩之后)就没有真值可用,判据退回纯估算。真值按 `input + cacheRead + cacheCreation` 算
(Anthropic 的 `input_tokens` 不含缓存读写)。硬校验:`判据数 + 本次输出预留 > 协议窗口` 时不发请求,以 `context.tooLong` 错误结束。

## 3. 上下文窗口

| 窗口 | 值 |
|---|---|
| 协议窗口 | 模型的 `contextWindow`(来自模型目录),没有就 200K;只用于硬校验 |
| 有效窗口 | `min(协议窗口, 272K)`,用户打开「最大上下文」时不封顶 |
| 压缩阈值 | `有效窗口 − min(最大输出, 20K) − 13K`,下限为窗口的一半(防止小窗口模型反复压缩) |

## 4. 压缩

触发与流程沿用 2.x,摘要格式采用 pi(R11):

1. **触发**:每回合请求前,超过阈值就压缩;上游返回 `context_length` 且本回合还没有内容时,压缩一次再试(每回合最多一次);
   一个 Run 里连续失败 3 次就停用自动压缩。手动 `/compact [说明]` 只能在会话空闲时用。
2. **准备**:取最后一个边界之后的历史;去掉思考块(签名属于别的模型);图片换成占位;超出摘要模型预算时从头部裁剪,
   切点只能落在不含工具结果的用户消息上(pi 还会在单个超大回合内部切分,分两段摘要再合并)。
3. **请求**:不带工具;系统提示「你负责总结对话」;对话展平成 `[User]: … [Assistant tool calls]: read(path=…)` 的文本(pi 的做法,
   避免模型「接着对话往下说」),工具结果截到 2000 字符;上次的摘要一起喂进去(迭代摘要)。
   **实现注**:请求是**一条**用户消息:`<conversation>…</conversation>` + (有上次摘要时)`<previous-summary>…</previous-summary>` + 提示词(首次用摘要模板,迭代用更新模板,`/compact` 的补充说明接在 `Additional focus:` 之后)。
   有上次边界时只发边界之后的新消息;上一份摘要里的文件清单先拆下来(清单由代码重新追加,不让模型改)。
4. **摘要模板**(pi):目标 / 约束 / 进展(已完成、进行中、受阻)/ 关键决定 / 下一步 / 关键上下文,外加**累积的** `<read-files>` 和 `<modified-files>` 清单。
5. **边界消息**:一条 `internal` 的**用户**消息(不能是助手消息,否则模型会把摘要当成自己说过的话),包含 `CompactBoundaryPart`
   (触发方式、前后 token 数、摘要、摘要模型)、续接说明和摘要正文。
6. **压缩后附件**:最近读写过的最多 5 个文件**从磁盘重新读**(总 50K token、每个 5K,只认成功的调用,只经执行环境,读不到 / 目录 / 超过 1 MB / 二进制静默跳过),最新待办,本会话调用过的 Skill 正文(每个 5K、总 25K)。
   **实现注**:文件重附在 `Compactor` 里;待办和 Skill 正文是对应扩展的事,经 `compact_attachments` 扩展点(就地修改)贡献文字块,各占一个独立的文字块。
7. 压缩成功后丢弃旧的 token 锚点再重新组装。

- 压缩模型:工作区设置 > 全局设置 > 会话模型,模型和供应商成对取;找不到就回退到会话模型并记录(边界上 `FellBack`)。思考档位同样三档,并按压缩模型归一化(关不掉推理的 effort 模型取最低档,不发 `off`)。
- **扩展点**:`before_compact`(闸门:取消或给现成摘要,不调模型)、`compact_attachments`(就地修改:给压缩后附件)、`after_compact`(通知,边界已落库)。
- **熔断**:一个 Run 里连续自动压缩失败 3 次(含被 `before_compact` 取消)后不再自动重试,发 `context_status: disabled`;手动压缩不计数、不熔断。成功清零。
- **被动压缩**:循环层的 `LoopHooks.RecoverContextLength`(见 02):上游报 `ContextLength` 且本回合没有任何内容字节时调用,压缩成功就丢弃这个回合重来;每回合最多一次。
- **手动压缩**:`AgentSession.CompactNowAsync`,占会话锁(会话里有 Run 时抛 `SessionBusyException`),边界之后没有真实对话时返回 `compaction.nothingToCompact`。
- `messagesForModel` 是决定「发什么」的**唯一规则**:从最后一个边界开始;界面专用部分编码为空、计 0 token。

## 5. Skills 与命令

- **Skills**:目录里的 `SKILL.md`(YAML 前言 + 正文)。前言用自写的子集解析器,永不抛异常,解析不了的行记为诊断。
  - 必填:`description`(≤1024)、非空正文(≤64KB);`name` 符合 `^[a-z0-9][a-z0-9-]{0,63}$`。
  - 发现顺序(后者覆盖前者):插件提供的 → 全局 `skills/` → 项目 `.next-cowork/skills/`。最多 200 个。
  - 模型通过 `Skill` 工具取正文(只读工具,计划模式下也可用)。`allowed-tools` 只作提示,**不强制**(改变工具列表会破坏已发出的调用)。
  - 内置 Skill 安装到全局目录并打标记,带标记的才会被更新替换,用户的同名 Skill 不动。
  - SSH 执行环境:非项目 Skill 如果依赖本地附带文件,标记为不可用。
- **命令**:`commands/<name>.md`(全局和项目)。名字只来自文件名;`$ARGUMENTS`、`$1–$9` 替换;**展开在客户端发送前完成**,
  这样手机和桌面看到的输入一致。内置只有 `/init`。

**实现注(`src/Ncw.Agent.Skills`,M4)**:

- **发现**:插件提供的(只留接口 `ISkillPluginSource`,插件宿主以后实现)→ 全局 `<数据根>/skills`(设备本机环境)→ 项目 `.next-cowork/skills`(项目的执行环境),后者覆盖前者,最多 200;`SKILL.md` 的 frontmatter 用 `Ncw.Core.Text.FrontmatterParser`(2.x 的解析器从 Subagents 项目移到了 Core,Skills 与子代理加载器两处共用同一个答案,测试在 `Ncw.Core.Tests.FrontmatterTests`)。realpath 围栏:软链逃逸的条目跳过并记诊断,诊断不回显目标;永不抛(连接断开除外)。SSH 项目里依赖设备本机附带文件的非项目 Skill 判 `ClientAssetsOnly`(不可用)。
- **激活**:全局启停存被关掉的那些(kv `skills.disabled`);按工作区选装存在工作区设置的 `activeSkillIds` + `skillSelectionMode`,**空清单 = 全都要**;「这一轮下发哪几条」的合并只有一个纯函数(`Ncw.Core.Domain.SkillSelection` / `SkillCatalog.IsActive`,规则 3)。
- **扩展**(`SkillsExtension`,Order 300):`before_run` 扫描并把**本 Run 的快照**定死;`# Available Skills` 节只读快照(按名字排序,总长 16 KB 封顶,正文永不进系统提示词);`Skill` 工具(`ReadOnly = true`,计划模式下也可用)返回正文并附「不可信内容」边界声明(复用会话层 `PromptText.UntrustedBoundary`);`compact_attachments` 在压缩前从被压缩的历史里取本会话调用过的 Skill 正文(每个 5K token、总 25K)。用量经 `ISkillUsage` 端口(Host 写 kv `skills.stats`),永不阻断。
- **安装**(只有逻辑,没有 UI):`installZip` 解压全经 `IExecutionEnvironment`(`ZipArchive` 只读内存 `Stream`,不落临时文件);拒绝路径穿越 / 绝对路径 / 盘符 / 反斜杠 / 符号链接条目 / 重复条目 / 保留名;条目数、单文件、总解压体积有上限且**边读边计数**(不信任头里的大小);先解到同级临时目录再原子改名,失败清理;zip 里的可执行位保留(2.x 不保留)。市场:`ISkillMarketClient` 可注入(默认 `https://nextco.work/api/`,sha256 校验、≤ 20 MB);市场安装需账户令牌,`account.attachDelegatedToken` 未做前返回 `skills.authRequired`。内置 Skill(`resources/skills`)安装到全局目录并打标记,带标记的才会被更新替换,用户同名 Skill 不动(`AgentServices.BundledSkillsRoot` 给出源目录才启用,本分支没有该目录)。
- **命令**(`Ncw.Agent.Skills.Commands`):发现 / 启停(kv `commands.disabled`)/ 诊断;展开是 Core 里的纯函数 `CommandExpansion`(`$ARGUMENTS`、`$1–$9`,边界按 ASCII,与 2.x 的 JS `\b` 一致),**由客户端发送前调用**(`CommandsActions.ExpandAsync`,先取最新清单;设备不可达时失败,不会把原始 `/xxx` 发出去)。
- **未做**:从会话提炼 Skill 的流程(2.x `extraction.ts` / `session-digest.ts`)和 `activateWrittenSkills`;插件提供的 Skill 来源的实现;`installZip` 线上上限 3 MB(帧上限 4 MB;更大的走市场)。

## 6. 辅助模型调用(R20)

标题生成、输入建议、压缩、AI 权限审查、目标判定、提交信息生成都走 `AuxiliaryModel`:不带工具、与会话同一个模型和供应商、
思考尽量关闭、独立的 RunId(用量单独记账)、**不使用账户池**(失败不会锁住主账户)。

## 7. 不变量清单

勾选 = 已有测试(`tests/Ncw.Agent.Session.Tests`,除非另注)。

- [x] 装饰只作用于副本,记录永不被修改
- [x] 头块在第一条用户消息(工具结果之后),状态块在本次 Run 的输入消息
- [x] 运行级常量在系统提示词;git 和待办每个 Run 算一次
- [x] Skills 按名字排序,正文不进系统提示词(`Ncw.Agent.Skills.Tests.SkillSectionTests`;端到端 `Ncw.Agent.Host.Tests.ExtensionsTests.An_installed_skill_is_listed_in_the_prompt_without_its_body…`)
- [x] 压缩触发和硬校验读同一个数;阈值下限为窗口一半;压缩后丢弃旧锚点
- [x] 边界消息是内部用户消息;裁剪只在不含工具结果的用户消息处切
- [x] 压缩后文件从磁盘重新读
- [x] 消息与输入回执同一事务;迁移只追加;`cost` 的 NULL 和 0 不同(Storage 层测试)

| # | 不变量(2.x ★ 与 3.0 新增) | 测试 |
|---|---|---|
| S1 | 装饰只作用于发出去的副本:传入数组和库里的 JSON 逐字不变,reminder 不进转录 | `ContextTests.Decoration_never_mutates_…`、`SessionTests.Project_instructions_reach_the_model_…_never_the_database`、`Several_runs_accumulate_…`(落库 JSON 逐字不变) |
| S2 | 头块在第一条用户消息、tool_result 之后(否则 400);状态块在 Run 的输入消息,绝不在工具回执那条;同一个 Run 里两回合的前缀逐字节相同、system 不变 | `ContextTests.The_state_block_goes_on_…`、`The_tail_anchor_pins_…`、`Within_a_run_the_prefix_is_byte_identical…`、`A_head_block_is_placed_after_leading_tool_results…`、`SessionTests.The_prefix_of_the_request_…` |
| S3 | 压缩之后头块仍只有一份(落在边界消息上);找不到用户消息时原样返回,绝不合成消息 | `ContextTests.After_compaction_the_instructions_ride_…`、`No_user_message_means_…`、`SessionCompactionTests.Project_instructions_stay_…` |
| S4 | 占用从装饰后的数组算;归因各档之和恒等于总量;判据取 `max(估算, 真值 + 新增估算)`,真值偏小不放松判据,真值不动 `used` | `ContextTests.Used_is_computed_from_the_decorated…`、`Segments_always_add_up_…`、`The_compaction_verdict_reads_the_larger_…`;`SessionCompactionTests.When_the_upstream_reports…`、`A_real_size_below_the_estimate_…`、`On_the_first_turn_…` |
| S5 | 阈值 = 有效窗口 − min(最大输出, 20K) − 13K,下限窗口一半;有效窗口默认夹在 272K;硬校验读协议窗口,不受「最大上下文」影响 | `Ncw.Core.Tests.ContextWindowsTests`、`SessionTests.The_billing_threshold_clamps_…`、`A_conversation_that_cannot_fit_…` |
| S6 | 压缩后旧锚点作废,判据退回纯估算(锚点不在新上下文里) | `SessionCompactionTests.After_compaction_the_early_history_is_gone_…`(压缩之后请求不再带 BIG,不会因旧真值立刻再压) |
| S7 | 压缩只在最后一个边界之后取历史;边界是 Internal 的**用户**消息;完整转录仍在库里;删掉边界自动退回「没压过」 | `CompactionTests.For_model_*`、`Deleting_the_boundary_…`、`The_result_is_an_internal_user_message_…`、`SessionCompactionTests.The_boundary_is_an_internal_user_message_in_the_database…` |
| S8 | 边界之后没有真实对话时拒绝压缩;摘要为空判失败;非 ContextLength 失败不重试;PTL 重试最多 3 次且每次更短;切点只落在不含工具结果的用户消息处,开头的旧边界保留;输入预算封底到窗口一半 | `CompactionTests.Nothing_to_compact_…`、`An_empty_summary_fails_…`、`A_context_length_failure_…`、`Other_failures_are_not_retried`、`Truncating_the_head_*`、`When_the_output_budget_eats_the_window…` |
| S9 | 摘要请求不带工具、对话展平成文本、思考块去掉、图片换占位;迭代摘要只发新消息;读写文件清单跨压缩累积(失败的调用不算,改过的不在只读清单) | `CompactionTests.The_summary_request_is_one_flattened_…`、`The_conversation_is_flattened_…`、`Prepare_for_summary_…`、`A_second_compaction_feeds_…`、`Read_and_modified_file_lists_accumulate_…` |
| S10 | 压缩后的文件从磁盘现读(不抄转录)、读不了只是少附一个、最多 5 个、总预算、大文件和二进制不读 | `CompactionTests.Restored_file_content_is_read_from_disk_…`、`A_file_that_cannot_be_read_…`、`At_most_five_files_…`、`A_huge_file_is_not_read_…`;`SessionCompactionTests.Files_touched_before_compaction_…` |
| S11 | 压缩模型三档来源、成对取用、解析不到回落会话模型并记进边界、档位按压缩模型归一化(不发 off)、输出额度按压缩模型的窗口夹 | `CompactionTests.*Binding*`(`With_nothing_configured…` 起 8 条)、`SessionCompactionTests.A_configured_compaction_model…`、`A_model_that_cannot_turn_reasoning_off…`、`A_vanished_compaction_model…` |
| S12 | 自动压缩熔断(连续 3 次失败后不再试,相位 disabled,判据照常报);总闸关掉一次都不压;被动压缩每回合一次,压完还被拒就如实报错 | `SessionCompactionTests.A_summary_that_keeps_failing_…`、`With_auto_compact_off_…`、`A_context_length_rejection_with_no_content_…`、`If_compaction_cannot_save_the_turn_…`;`Ncw.Agent.Kernel.Tests.StreamFailureTests.A_context_length_rejection_…` |
| S13 | 只在消息完成时落库(R6):流式增量只是事件;先落库后发 `message_end`;同 id 重放幂等 | `SessionTests.Streaming_deltas_are_events_only_…`、`A_single_turn_commits_…` |
| S14 | 多轮对话落库后重新加载一致(新会话对象只靠库就能还原整段请求);助手消息的来源与停止原因落库 | `SessionTests.Several_runs_accumulate_…`、`A_single_turn_commits_…`、`CodecTests.Source_and_stop_reason_…` |
| S15 | 孤儿修复:加载时幂等地补;Run 开始时在新用户消息**之前**补;中止 / 出错 / 断流后不留孤儿 | `SessionTests.Repairing_on_load_…`、`Orphans_left_by_a_crashed_run_…`、`Aborting_while_a_tool_runs_…`、`A_message_that_only_has_a_cut_off_tool_call_…` |
| S16 | 中止:半截回复保留且标 Aborted(重载后仍在),会话锁释放,会话可以继续;压缩中中止不留边界 | `SessionTests.Aborting_mid_stream_…`、`SessionCompactionTests.An_abort_during_compaction_…` |
| S17 | 会话互斥:同时只有一个 Run,第二个直接 `session.busy`(没有事件);只能在空闲时做的操作抛 `SessionBusyException` | `SessionTests.A_second_run_in_the_same_session_is_refused_…` |
| S18 | `run_settled`(R8)在全部落库、释放会话锁之后才发;`run_end` 扩展点在落库之后、`run_settled` 之前;没开始的 Run 没有 settled;sink 抛异常不影响 Run | `SessionTests.Run_settled_comes_after_…`、`A_sink_that_throws_…`、`A_session_that_does_not_exist_…` |
| S19 | 断流续传(2.x 规则:只认 network、丢弃重来、不重放工具)——循环层实现,会话层验证半截不进库 | `SessionTests.A_stream_that_breaks_…`;`Ncw.Agent.Kernel.Tests.StreamFailureTests` |
| S20 | 2.x 旧记录:Anthropic 透传补 `anthropic` 标签、写回去掉标签;来源未知;2.x 的 `card` 读成 Details;坏 JSON / 未知块不抛 | `CodecTests.*`、`SessionTests.A_legacy_anthropic_thinking_record_…` |
| S21 | 项目指令:每目录一个文件(override > AGENTS > CLAUDE)、由外到内、全局最前、worktree 主仓库根、预算由内到外、软链越界跳过且诊断不泄露目标、永不抛、消毒 | `InstructionsTests.*` |
| S22 | 工具上下文带执行环境句柄、项目根、环境标识;`tool_start` 带环境标识;编排层的派生类型套在会话层外面 | `SessionTests.Tools_get_the_environment_handle_…` |
| S23 | 扩展:贡献工具 / 系统提示词节 / 运行参数,`input` 变换,`session_start` 每个会话对象一次,工具名非法的工具被跳过并记诊断,过滤只收窄不新增 | `SessionTests.Extensions_contribute_…`、`A_before_run_tool_filter_…`、`The_input_extension_point_…`、`Session_start_fires_once_…`、`A_tool_with_an_invalid_name_…` |
| S24 | 文件引用归一化:工作区内 → 相对路径,工作区外保留绝对路径,其他块不动 | `SessionTests.A_dropped_file_inside_the_workspace_…`、`The_public_normaliser_…` |
| S25 | 引导消息:用队列项 id 提交、插在工具结果之后、有消费回执 | `SessionTests.A_steering_message_is_committed_…` |

## 8. AgentSession 的运行语义(编排层的接口)

```csharp
public sealed class AgentSession(string sessionId, string workspaceId, AgentSessionDeps deps)
{
    Task<RunOutcome> RunAsync(RunRequest request, IRunSink sink, CancellationToken ct);   // 不抛;取消 = 中止
    int RepairOrphans();                                                                  // 加载历史时修孤儿(空闲时)
    Task<ManualCompactOutcome> CompactNowAsync(ModelRef model, ThinkingLevel thinking, SessionSettings? settings, string? instructions, CancellationToken ct);
    ValueTask<IReadOnlyList<Part>> NormalizeInputAsync(IReadOnlyList<Part> parts, CancellationToken ct);   // 排队的插队消息入队前调
    bool IsBusy { get; }
    static IReadOnlyList<Message> BuildContext(IReadOnlyList<Message> transcript);        // 转录 → 发给模型的上下文
}
```

- `RunRequest`:`RunId`、`Input`(消息 id 由调用方给)、`Model`、`Thinking`、`MaxContext`、`Depth`、`AgentType`、`MaxTurns`(超过上限时 `PrepareTurnAsync` 以 `session.maxTurns` 收尾)、`WorkspaceRoot`(可覆盖会话级工作根,供 `isolation: worktree` 子代理使用)、`Settings`(Run 开始时的设置快照:`AutoCompact`、`MaxOutputTokens`、压缩模型与工作区覆盖、个性化、文件工具外部名、全局指令、导入指令)、
  `AgentPrompt` / `ModePrompt`、**编排层的两个钩子** `TakeSteering`(引导队列,取走即消费)和 `EnrichToolContext`(派生出带 `Interact` / `SpawnSubagent` 的工具上下文,套在会话层的 `SessionToolContext` 外面)。
- 一个 Run 的顺序:取会话锁(失败 → `session.busy`)→ 读库里整段历史 → `session_start`(每个会话对象一次)→ `input` 变换 + 文件引用归一化 → `before_run` → 读项目指令和 git(Run 开始时定死)→ 交给 `AgentLoop`,
  事件原样转给 `IRunSink`(含会话层自己放进回合计划里的 `context_usage` / `context_status`)→ 循环结束后 `run_end` 扩展点 → **释放会话锁 → 发 `run_settled` → `run_settled` 扩展点**。
- 事件转给 sink 时不带取消令牌(中止之后的收尾事件必须送到);sink 抛异常只记诊断。
- `RunOutcome` 带终态、错误、累计用量和费用(`CostInfo` 三态相加)、工具调用数;`Started = false` 表示根本没开始(没有事件)。
- 不在会话层(属于编排层):`runs` 记录、用量落账、`RunRegistry`、排队 / 引导队列的消费回执合并、子代理、目标、后台任务。

## 9. 相对原稿的实现偏离(汇总)

1. 会话层单独成项目 `Ncw.Agent.Session`(命名空间仍是 `Ncw.Agent.Kernel.Session`),不放进 Kernel:循环层不引用 Storage / Environments(开头)。
2. `messages` 表新增 `meta` 列(迁移 39),存助手消息的来源和停止原因(§1)。
3. 估算和窗口公式放在 `Ncw.Core.Domain`(客户端共用,§2)。
4. 循环层新增两点:`LoopHooks.RecoverContextLength`(被动压缩)和 `TurnContext.LastUsage / LastUsageAnchor`(真值锚点),见 02 §10。
5. 扩展注册接口补上 `RegisterTool` 和 `ContributeSystemSection`;会话层的扩展点:`session_start`、`input`、`before_run`、`before_compact`、`compact_attachments`(新增,06 里 `after_compact` 是通知,这里拆成两个点,要贡献附件就得赶在边界消息生成之前)、`after_compact`、`run_settled`(见 06)。
6. 压缩的摘要请求是**一条**展平文本的用户消息(pi),不是原始对话 + 压缩指令(2.x);边界上的文件清单由代码追加。
7. 尾块锚到输入消息 id,而不是扫「最后一条非工具回执的用户消息」(§2)。
8. 项目指令的诊断是结构化 key,逃逸的 override 不回落;全局层读自设备本地(§2)。
9. **未完成**:pi 的「单个超大回合内部切分、分两段摘要再合并」;历史里「对不上环境的文件引用换占位文字」(`FileRefPart` 还没有来源字段);待办 / 计划 / 目标 / 子代理(扩展与编排层,已随各自模块完成);Skills / Hooks / 命令已在 M4 完成,见 §5 与 06 §6;Run 记录与用量落账(编排层);模型运行时校验(视觉 / 工具能力,在 Llm 路由里)。
