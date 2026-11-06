# 上下文压缩：可配置的压缩模型与思考强度

## 1. 为什么要做（根因，已在代码里验证）

压缩请求在两条路径上都硬编码 `thinkingLevel: 'off'`：

- `src/main/kernel/agent-session.ts:982`（自动压缩）
- `src/main/ipc/context.ts:70`（手动 /compact）

这个 `off` 的下场由 `shared/domain/thinking-adapter.ts:261-271` 决定：

1. `router.ts:150` 调 `resolveModelThinking('off', alias.thinkingConfig, …)` → `{ enabled: false, explicit: true }`；
2. `applyThinkingAdapter` 在 `mode === 'effort'` 且 `reasoningEfforts` **不含 `'none'`** 时
   直接抛 `ThinkingAdapterError('该模型不支持关闭推理，不能把 Off 静默还原成默认强度。')`；
3. `router.ts:893-898` 把它包成 `retryable: false` 的 `provider` 错；
4. `compaction/compact.ts:102` 只对 `context_length` 做 PTL 重试，于是**第一次就放弃**；
5. `agent-session.ts:958-964` 连续三次失败触发 `MAX_CONSECUTIVE_COMPACT_FAILURES` 熔断（`disabled`），
   此后整个 run 不再压缩 —— 上下文一路涨到上游报超长。这正是压缩重写前那次事故的翻版。

命中这一条的模型是**真实存在的**：`model-catalog-inventory/vendors/openai.ts:5-49` 的
`gpt-6-astra / gpt-6-sol / gpt-6-luna` 用 `openAiAstraEfforts = ['low','medium','high','xhigh','max']`
（`helpers.ts:48`），没有 `'none'`。`mode: 'always'` 的模型不会炸（adapter 直接 strip），
所以爆的只有「effort 且不可 none」这一类。

同一个崩法还在另外两处旁路调用里：`src/main/goal/evaluate.ts:199`（目标判定）、
`src/main/runtime.ts:2066`（权限审核 AI）。

仓库里**已经有正确答案**：`src/main/session-title.ts:96-102`
（`levels.includes('off') ? 'off' : 最低可用档`，并把 `maxOutputTokens` 夹到 `alias.maxOutputTokens`）。
压缩这条路没走这套。本次把它抽成共用纯函数，四处共用。

跨模型之后还有两个参数必须跟着改（今天没人管）：

- 输出额度固定 20K（`COMPACT_MAX_OUTPUT_TOKENS`），不夹模型上限 —— 8K 输出的模型必 400；
  一旦开了思考，reasoning token 同样吃这个额度，摘要会被截断，而 `compact.ts:112` 把空摘要判成失败。
- `protocolWindow` 读的是**会话模型**的窗口（`agent-session.ts:947`）。压缩模型可另选之后，
  这个预裁分母必须换成压缩模型自己的窗口。

## 2. 已确定的产品决策

| 决策 | 结论 |
|---|---|
| 全局配置落点 | `AppSettings` 顶层三个字段，与 `goalEvaluatorModel` 逐字同构，同步类 `'providers'` |
| 每会话覆盖 | 落在 `WorkspaceSettings`（工作区级，与 `maxContext` 同类），控件画在圆环菜单 `/compact` 旁 |
| 覆盖档数 | 三档：工作区 > 全局 > 会话模型；工作区能**显式退回**「跟随会话模型」以反盖全局 |
| 思考强度默认 | `'inherit'`（跟随会话本轮档位），复用 `INHERIT_THINKING` |
| 跨模型归一化 | 目标档位不可用时**降到最近的可用档**（不落回 `auto`） |
| 输出额度 | 跟随全局 `AppSettings.maxOutputTokens`，按压缩模型的窗口夹 |
| 压缩模型解析不到 | 静默回落会话模型 + `logger.warn`，不判失败、不计熔断 |
| 压缩事实可见性 | 记进 `compact_boundary` 并在 `CompactionDivider` 上显示（含回落标记） |
| 修复范围 | 压缩 + 目标判定 + 权限审核 + 会话标题，共用同一个 helper |

## 3. 数据模型

### 3.1 `src/shared/agent/run-request.ts`

新增强度序常量（`THINKING_LEVELS` 的顺序是 UI 列表序：`auto` 在首、`off` 在末，**不能**拿来比大小）：

```ts
/** 思考强度的**有序**序列。★ 与 THINKING_LEVELS 不是一回事：那个是下拉列表的排序，
 *  auto 在首位、off 在末位；这里是从弱到强，auto 不在轴上（它是「模型自己决定」，无强度）。
 *  需求：旁路请求要在目标档位不可用时降到「最近的可用档」，没有这条序就只能落回 auto。 */
export const THINKING_STRENGTH: readonly ThinkingLevel[] =
  ['off', 'minimal', 'low', 'medium', 'high', 'higher', 'max']
```

### 3.2 `src/shared/domain/settings.ts`

`AppSettings` 顶层新增（紧挨 `goalEvaluatorModel` 那一对）：

```ts
/** 上下文压缩用哪个模型；空字符串 = 跟随会话模型（出厂默认）。 */
compactModel: string
/** 与 `compactModel` 成对，见 `defaultModelProviderId` */
compactModelProviderId?: string
/** 压缩请求的思考强度。'inherit' = 跟随会话本轮档位（出厂默认）。 */
compactThinking: SubagentThinking
```

配套改动（缺一处就编译不过或静默走错）：

- `DEFAULT_SETTINGS`：`compactModel: ''`、`compactThinking: INHERIT_THINKING`。
- `mergeSettings`：加**第五对**成对写（`settings.ts:517-539` 那段注释说的四对变五对）：
  ```ts
  if (patch.compactModel !== undefined) {
    next.compactModel = patch.compactModel
    next.compactModelProviderId = patch.compactModelProviderId
  }
  if (patch.compactThinking !== undefined) {
    // 坏值退回当前值，同 subagent.thinking：静默改成 'inherit' 会让用户以为自己选的档位在生效
    next.compactThinking = isSubagentThinking(patch.compactThinking) ? patch.compactThinking : current.compactThinking
  }
  ```
- `PATCHABLE_KEYS`：加三个键（编译期哨兵）。
- `src/shared/domain/config-sync-registry.ts` 的 `SETTINGS_SYNC_FIELDS`：三项全部 `'providers'`
  （穷尽 Record，漏了当场红）。理由写进注释：它指向某一家供应商的别名，和 `goalEvaluatorModel` 同类，
  不是「这台机器的事实」。
- `src/shared/domain/data.ts` 的 `isAppSettings`：**容忍式**校验（`has(v,'compactModel') && typeof !== 'string'` 这种写法），
  照 `goalEvaluatorModel` 那两行的先例 —— 缺席 = 改动之前的旧存档，必须放行，否则每一份老备份导入时被整份拒绝。
  `compactThinking` 用 `has(v,'compactThinking') && !isSubagentThinking(v.compactThinking)`。

不需要 DB 迁移：`repo.ts:299` 的 `getSettings()` 把库里的行合并到 `DEFAULT_SETTINGS` 上，缺字段自动拿默认值。

### 3.3 `src/shared/domain/workspace.ts`

`WorkspaceSettings` 新增（全部可选，照 `maxContext?: boolean` 的先例 —— 旧库里的工作区 JSON 是裸 `JSON.parse`，
声明成必填会让类型在运行时说谎）：

```ts
/**
 * 这个工作区压缩用哪个模型。三态：
 * - 缺席（undefined）= 跟随全局设置（出厂）
 * - null = **显式**跟随会话模型（用来反盖全局配的压缩模型）
 * - 字符串 = 这个别名
 * ★ 用 null 而不是某个魔法字符串：别名是用户可写的任意字符串，任何哨兵串都可能撞上。
 */
compactModel?: string | null
/** 与 `compactModel` 成对。`compactModel` 不是字符串时这一项无意义。 */
compactModelProviderId?: string
/** 缺席 = 跟随全局设置；'inherit' = 跟随会话本轮档位；其余 = 显式档位。 */
compactThinking?: SubagentThinking
```

`DEFAULT_WORKSPACE_SETTINGS` **不**填这三项 —— 缺席就是「跟随全局」，填上等于把默认值冻进每个新工作区，
以后改全局默认对老工作区不生效。

### 3.4 `src/shared/agent/message.ts` 的 `compact_boundary`

```ts
/**
 * 写这份摘要的是谁。领域值，不翻译（§6.5）。
 * 需求：压缩模型可配置之后，「这份摘要是谁写的」不再等于「会话模型是谁」，
 * 而摘要一旦写坏，用户唯一能行动的信息就是这个。缺席 = 本次改动之前的旧边界。
 */
summaryModel?: {
  model: string
  modelProviderId?: string
  /** 实际下发的档位（已按压缩模型归一化过，不是用户配的那个原值）。 */
  thinking: ThinkingLevel
  /** true = 配置的压缩模型当时解析不到，回落成了会话模型。 */
  fellBack?: boolean
}
```

## 4. 纯逻辑（可单测，不碰路由器）

### 4.1 `src/shared/domain/model-runtime.ts` — 新增两个导出

放在 `normalizeModelThinkingLevel` 旁边（它们回答的是同一类问题：「这个模型接受哪些档位」）。

```ts
/**
 * 旁路请求（压缩 / 目标判定 / 权限审核 / 会话标题）该用哪个档位。
 *
 * 需求：这些请求要么想省钱关掉思考、要么想跟随会话档位，但**都不能因为档位不可用而失败**。
 * 今天压缩硬发 'off'，而 gpt-6 这类 effort 模型的 reasoningEfforts 不含 'none'，
 * thinking-adapter 会直接抛错 —— 表现为压缩每次必败，三次后熔断，上下文再也压不下去，全程零报错。
 *
 * 规则：想要的档位可用就用它；不可用就在该模型**支持的档位**里取「不超过它的最强一档」；
 * 一个都不低于它（模型最低档也比它强）时取最低的非 auto 档；再没有就 'auto'。
 * ★ 绝不抛、绝不返回一个模型不支持的档位。
 */
export function auxiliaryThinkingLevel(wanted: ThinkingLevel, model: ThinkingModel | undefined): ThinkingLevel
```

实现要点：`wanted === 'auto'` 直接返回 `'auto'`；否则按 `THINKING_STRENGTH` 的下标在
`modelThinkingLevels(model)` 里找 ≤ wanted 的最大值。

```ts
/** 旁路请求的输出额度：既不能超过用户设的全局额度，也不能超过模型窗口。
 *  ★ 夹的是 contextWindow 不是 alias.maxOutputTokens —— 与 `resolveMaxOutputTokens`
 *  同一条边界（理由见 run-request.ts:72-86，目录里那条输出上限已不再参与）。 */
```
不新写函数，直接复用 `resolveMaxOutputTokens(settings.maxOutputTokens, alias.contextWindow)`。

### 4.2 新文件 `src/shared/domain/compaction-model.ts`

文件头写清：为什么存在（三档来源必须由**一段**代码决定，否则「设置里写着 A、请求发给 B」且零报错）、
不变式（模型和 providerId 成对、绝不拼出「A 家的别名 + B 家的锁」）、故意不做的（不查绑定是否存在 —— 那要路由器，归主进程）。

```ts
export interface CompactModelChoice {
  model: string
  modelProviderId: string | undefined
  /** true = 这一档就是会话模型（没配，或显式选了「跟随会话模型」）。 */
  followsSession: boolean
}

/**
 * 三档来源，越具体越优先：工作区 > 全局设置 > 会话模型。
 * @param workspace `compactModel === null` = 显式跟随会话（反盖全局）；`undefined` = 没配。
 * @param configured 全局设置。空别名 = 「跟随会话模型」。
 * @param session 这条会话的模型 —— 兜底，也是 `followsSession` 的取值。
 */
export function compactModelSelection(
  workspace: { compactModel?: string | null; compactModelProviderId?: string } | undefined,
  configured: { model: string; modelProviderId?: string },
  session: { model: string; modelProviderId?: string }
): CompactModelChoice

/**
 * 三档来源的档位。返回的是**尚未按压缩模型归一化**的原始意图，
 * 调用方拿到后必须再过一次 `auxiliaryThinkingLevel`（归一化要模型，纯函数够不着）。
 * @param sessionLevel 'inherit' 落到这个值：自动压缩 = 本轮 run 的档位，手动 /compact = `session.thinking`。
 */
export function compactThinkingSelection(
  workspace: SubagentThinking | undefined,
  configured: SubagentThinking,
  sessionLevel: ThinkingLevel
): ThinkingLevel
```

> 为什么不直接复用 `subagentModelSelection`：它的三档语义是「子代理文件 > 设置 > 父 run」，
> 没有「显式跟随」这一态（`null`），硬套会把 `null` 当成「没配」。两个函数各自只有十行，
> 且 `model-selection.ts` 的注释明确要求成对规则**只能由一个函数表达一次**，所以新写一个、
> 在注释里互相指认，而不是给旧的加一个布尔参数（§11：布尔参数到第三个就该拆）。

## 5. 主进程

### 5.1 `src/main/kernel/compaction/compact.ts`

- `CompactInput` 新增：
  ```ts
  /** 摘要请求下发的档位（调用方已按压缩模型归一化）。 */
  summaryThinking: ThinkingLevel
  /** 摘要请求的输出额度（调用方已按压缩模型的窗口夹过）。 */
  summaryMaxOutputTokens: number
  /** 记进边界、给界面看的那一组事实。 */
  summaryModel: { model: string; modelProviderId?: string; fellBack: boolean }
  ```
- `protocolWindow` 的**含义改为压缩模型的协议窗口**，注释同步改（保留原来那句「再往上发必然被拒」的理由）。
- `SummaryRequest` 加 `thinkingLevel: ThinkingLevel`，`summarizeOnce` 透传；`maxOutputTokens` 改用
  `input.summaryMaxOutputTokens`，`COMPACT_MAX_OUTPUT_TOKENS` 在本文件只剩「输入预裁时要给输出留多少」
  这一个用途 —— 改成用实际额度算：
  ```ts
  const inputBudget = input.protocolWindow - input.summaryMaxOutputTokens - estimateTokens(compactPrompt(...))
  ```
  ★ 加一条封底：`inputBudget <= 0`（小窗口模型 + 大输出额度）时退回 `Math.floor(protocolWindow / 2)`，
  否则 while 循环会把请求裁到只剩一条消息，然后必然摘要出垃圾。这条要带需求注释说明症状。
- 边界组装处把 `summaryModel` 写进 `CompactBoundary`（`fellBack` 为 false 时不写这个键，照本仓库
  「可选字段用 `...(x ? {} : {x})`」的既有写法）。
- `COMPACT_MAX_OUTPUT_TOKENS` 本身**不删**：`shared/agent/context-management.ts:83` 的
  `autoCompactThreshold` 仍用它当预留上限（那一层回答的是「正文什么时候该压」，分母是会话模型的窗口，
  与压缩请求实际发多少是两个问题）。在常量注释里补一句区分，防止下一个人把两处合并。

### 5.2 `src/main/kernel/agent-session.ts`

- `SessionDeps` 新增（与 `maxOutputTokens` 同类：run 开始那一刻的设置快照）：
  ```ts
  /** 设置 › 通用 › Agent 的压缩模型那一对 + 档位；缺省 = 纯内核测试没给设置，按「跟随会话模型」。 */
  compaction?: { model: string; modelProviderId?: string; thinking: SubagentThinking }
  /** 这个工作区对上面那组的覆盖（圆环菜单里那两栏）。 */
  workspaceCompaction?: { compactModel?: string | null; compactModelProviderId?: string; compactThinking?: SubagentThinking }
  ```
- 新增私有方法 `resolveSummaryBinding()`：
  1. `compactModelSelection(workspaceCompaction, compaction ?? {model:''}, { model: this.req.model, modelProviderId: this.req.modelProviderId })`
  2. `this.deps.upstream.resolveModel(choice.model, choice.modelProviderId)`；查不到且不是会话模型时
     → `logger.warn('[compact] 配置的压缩模型不可用，已回落到会话模型: …')`，改用会话模型那一对，`fellBack = true`；
     会话模型也查不到 → 返回 `undefined`（压缩照旧走失败路径，行为不变）。
  3. 档位：`compactThinkingSelection(ws, global, this.req.thinking)` → `auxiliaryThinkingLevel(level, alias)`。
  4. 额度：`resolveMaxOutputTokens(this.deps.maxOutputTokens, alias.contextWindow)`。
  5. 窗口：`effectiveContextWindow(alias.contextWindow, true)`（压缩模型自己的协议窗口）。
- `compact()` 里把上述结果塞进 `compactConversation(...)`，并把 `protocolWindow` 从
  `effectiveContextWindow(input.alias?.contextWindow, true)` 改成压缩模型的那个。
  `input.alias`（会话模型）仍然要用 —— 它只负责 `preTokens` 与阈值那一侧。
- `sendSummaryRequest()` 改为收下 binding：`model` / `modelProviderId` / `thinkingLevel` 全部来自 binding，
  不再是 `this.req.model` + `'off'`。★ 保留并改写那条「摘要要和正文同一家」的注释：
  原因（换一家既换口径也换账单）仍然成立，只是现在**由用户显式选择**才会换 —— 不要把这条理由删成一句新描述（§10.2）。

### 5.3 `src/main/ipc/context.ts`（手动 /compact）

同样的解析，差别只有两处：

- `'inherit'` 落到 `session.thinking`（DB 里存着，`shared/domain/session.ts:43`），不是 run 的档位 —— 手动压缩时没有 run。
- 已经有 `store.getWorkspace(session.workspaceId)`，直接取它的 `settings` 里那三项。
- 把解析逻辑抽成本文件的一个小函数还是共用 —— **共用**：在 `src/main/kernel/compaction/binding.ts` 新建
  `resolveCompactBinding(input)`，收 `{ router, settings, workspaceSettings, session: {model, modelProviderId, thinking}, logger }`，
  返回 `{ alias, model, modelProviderId, thinking, maxOutputTokens, protocolWindow, fellBack } | undefined`。
  两条路径（session / ipc）都调它，回落规则和日志只有一份。

### 5.4 共用 helper 的另外三个调用点

| 文件 | 改法 |
|---|---|
| `src/main/goal/evaluate.ts:199` | `thinkingLevel: auxiliaryThinkingLevel('off', alias)`，并把 `MAX_OUTPUT_TOKENS`（1024）夹到 `alias.contextWindow`。意图不变（尽量不思考），只是不再抛 |
| `src/main/runtime.ts:2066` | 同上；`alias` 已在 2041 行解析过，直接用 |
| `src/main/session-title.ts:96-102` | 那三行就是本 helper 的原型 → 改成调 `auxiliaryThinkingLevel('off', alias)`，保留它现有的 `maxOutputTokens` 夹取逻辑与注释 |

★ 这三处的 `wanted` 仍是 `'off'`，**不**跟着压缩改成 `inherit`：它们是短输出的判定/命名请求，
跟随会话档位只会让它们变慢变贵，而这不是本次要改的决定。

## 6. 渲染层

### 6.1 设置页 `src/renderer/src/settings/pages/GeneralPage.tsx`

在 `general.autoCompact` 那一行（202-205）下面加两行 `SettingRow`，与 `goalEvaluatorModel`
（167-181）逐字同构：

- 「压缩上下文模型」：`Select`，选项 = `[{ value: '', label: t('compaction.followSession') }, ...模型列表]`，
  写回走 `patch({ compactModel: alias, compactModelProviderId: modelProviderId })`（成对）。
- 「压缩思考强度」：`Select`，选项 = `SUBAGENT_THINKING_CHOICES`（`inherit` 在首位），
  标签复用 `chat.thinkingLevel.*` + 一个新的 `compaction.thinkingInherit`。
  写回前用 `isSubagentThinking` 把关（照 `isModelProposedGoals` 那一行的先例）。

### 6.2 圆环菜单 `src/renderer/src/views/chat/Composer.tsx` 的 `ContextRing`

在「最大上下文」（1982-1993）与「压缩上下文」（1995-2016）之间插一个 `MenuSeparator` + 两项：

- 「压缩模型」：一个子菜单/下拉，三类取值 → UI 层 value 用 `''`（跟随全局设置）、`'@session'`
  （显式跟随会话模型）、`modelSelectionKey(providerId, alias)`。
  ★ `'@session'` 这个哨兵**只活在下拉的 value 里**，落盘的是 `null` —— 与 `modelSelectionKey`
  文件头那条「只用在下拉和 Map 键，不落盘」的约定一致，注释里要写明这一点。
- 「压缩思考强度」：同一组档位选项，缺省项是「跟随全局设置」。

写回走已经 import 的 `updateWorkspace`（`services/app`），patch 只带这三个键。
`ContextRing` 现有的 props 已经太长，新增用**一个对象 prop**（`compaction: { value, models, onChange }`）而不是三个散 prop。

> Composer.tsx 已 2291 行（§15.3 的超大文件）。所以这一块的**纯逻辑**（三态 value 的编解码、
> 选项表的组装）落到同目录新文件 `views/chat/compaction-选项.ts`（实名 `compact-choice.ts`），
> 组件里只留 JSX —— 这是 §9 指定的拆法，顺带让它可单测。

### 6.3 `src/renderer/src/views/chat/CompactionDivider.tsx`

- 药丸那一行（54-63）在 `trigger` 之后追加 `· <模型名>`，仅当 `boundary.summaryModel !== undefined`。
  模型别名是领域值，不翻译。
- 展开区里加一行：「由 X 写 · 思考 Y」（`chat.thinkingLevel.*` 复用），
  `fellBack === true` 时再加一句提示「配置的压缩模型当时不可用，已改用会话模型」。
- 旧边界（缺 `summaryModel`）一律不画这两处 —— 不做防御式 UI（§5），没有事实就不画字段。

### 6.4 i18n

**新建** `src/renderer/src/i18n/compaction.ts`（照 `git.ts` / `ssh.ts` 的样子导出 `compactionZh` / `compactionEn`，
在 `index.tsx` 里 spread）。§6.3 明确要求新增的域不要再往那三千行里堆。新 key（zh + en 同时补，
`i18n/index.test.ts` 会校验两边键一致）：

```
compaction.model                 压缩上下文模型 / Compaction model
compaction.modelHint             …（说明「默认跟随会话模型」）
compaction.thinking              压缩思考强度 / Compaction thinking effort
compaction.thinkingHint
compaction.followSession         跟随会话模型 / Follow the conversation model
compaction.followGlobal          跟随全局设置 / Follow the global setting
compaction.thinkingInherit       跟随会话强度 / Follow the conversation
chat.compaction.writtenBy        由 {model} 写 · 思考 {level}
chat.compaction.fellBack         配置的压缩模型当时不可用，已改用会话模型
```

★ 已有的 `chat.compaction.*` 留在 `index.tsx` 不动（最小 diff，§12）；新 key 里属于聊天视图的两条
也放新文件，`index.tsx` 只多一行 spread。

## 7. 边界情况清单（每一条都要在实现时对得上）

1. **压缩模型 = 会话模型**（默认路径）：行为必须与今天**逐字一致**，除了档位从硬 `off` 变成 `inherit` 归一化后的值。
2. **配置的压缩模型解析不到**：回落会话模型、`fellBack: true`、warn 一行；**不计失败、不熔断**。
3. **会话模型也解析不到**：维持今天的失败路径（`compactConversation` 照常返回 `{ok:false}`）。
4. **压缩模型 `capabilities.tools === false`**：摘要请求本来就发 `tools: []`，但历史里有 `tool_call` / `tool_result` 块。
   本次**不**额外拍平它们（现状如此，且 OpenAI/Anthropic 都接受历史里的工具块而请求不带 tools）。
   在 `compact.ts` 的文件头记一句「故意不做」，附拆除条件：真的遇到上游拒收再在 `prepareForSummary` 里拍平。
5. **压缩模型窗口比会话模型小**：`protocolWindow` 现在读压缩模型的，`truncateHead` 会正常裁；
   裁到 `undefined` 时仍是失败 —— 这是真实的「这个压缩模型装不下这段对话」，不该假装成功。
6. **`inherit` + 会话档位 `auto`**：`auxiliaryThinkingLevel('auto', alias)` 直接返回 `'auto'`，
   由 `resolveModelThinking` 按模型的 `defaultEnabled` 决定 —— 与正文同口径。
7. **`inherit` + 会话档位 `off` + 压缩模型关不掉**：降到最低可用档（如 `low`），不抛。这是本次要修的那条。
8. **`mode: 'always'` 的压缩模型**：adapter 直接 strip，`auxiliaryThinkingLevel` 返回 `'auto'`（`modelThinkingLevels` 对 always 只给 `['auto']`）。
9. **全局配了压缩模型，某工作区想退回会话模型**：工作区那栏选「跟随会话模型」→ 落盘 `null` → 三档函数返回 `followsSession: true`。
10. **老备份 / 老工作区 JSON 缺这些字段**：全部走「跟随全局 / 跟随会话」，导入不被拒。
11. **老转录里的 `compact_boundary` 缺 `summaryModel`**：分隔线照旧渲染，不画新那两处。
12. **run 跑到一半用户改了设置**：与 `maxOutputTokens` 同口径 —— run 开始时快照，下一个 run 生效。
13. **手动 /compact 时 `'inherit'`** 落到 `session.thinking`，不是工作区的 `defaultThinking`。

## 8. 测试（vitest，`include` 只有 `src/**/*.test.ts`）

新增：

- `src/shared/domain/__tests__/model-runtime.test.ts`（已存在，追加）：
  - `auxiliaryThinkingLevel('off', gpt-6 样式 alias)` → `'low'`（最低可用档），不抛；
  - `'off'` 在可关模型上 → `'off'`；
  - `'high'` 在只有 `['auto','medium','off']` 的模型上 → `'medium'`（降到最近可用）；
  - `'auto'` 恒等；`model === undefined` → `'auto'`。
- `src/shared/domain/__tests__/compaction-model.test.ts`（新）：三档优先级、`null` 显式跟随、
  成对回落（绝不出现「A 家别名 + B 家 providerId」）。
- `src/main/kernel/compaction/__tests__/compact.test.ts`（追加）：
  - 摘要请求带上传入的 `thinkingLevel` 与 `maxOutputTokens`（断言 `send` 收到的 `SummaryRequest`）；
  - `summaryMaxOutputTokens` 大于 `protocolWindow` 时输入预算走封底、不把请求裁成一条；
  - 边界消息带 `summaryModel`，`fellBack` 时带标记。
- `src/main/kernel/__tests__/agent-session.test.ts`（追加）：
  - 配置了压缩模型时，摘要请求发给**那个**模型，正文仍发会话模型（现有 `FakeUpstream` 已经分开记 `summaries`）；
  - 压缩模型解析不到时回落会话模型且 `context_status` 不出现 `failed`。
- 回归用例（本次 bug 的守门人）：`src/main/kernel/upstream/__tests__/thinking-off.test.ts` 旁边新增一条
  —— 一个 `reasoningEfforts` 不含 `'none'` 的 effort 模型，跑完整的 `AgentSession` 自动压缩，
  断言**不**出现 `ThinkingAdapterError`、摘要请求体里带的是一个合法 effort。
- `src/shared/domain/__tests__/settings.test.ts`（追加）：`compactModel` 成对写、`compactThinking` 坏值退回当前值。
- `src/shared/domain/__tests__/config-sync.test.ts`：新字段归 `'providers'`。
- `src/renderer/src/views/chat/__tests__/compact-choice.test.ts`（新）：三态 value 的编解码往返。

## 9. 验证

```bash
npm run typecheck:web   # 渲染层 + shared
npm run typecheck       # 含主进程
npm test                # vitest
npm run lint
```

手工回归（本机）：

1. 会话模型选 `gpt-6-*`，压缩设置保持默认 → 触发一次 `/compact`：不应再出现「该模型不支持关闭推理」，
   分隔线上能看到写摘要的模型与档位。
2. 全局把压缩模型配成一个便宜模型 → `/compact` 的用量记录落在那个模型上（`runId` 后缀 `:compact`）。
3. 工作区那栏选「跟随会话模型」→ 反盖全局生效。
4. 把全局压缩模型指向一个随后被禁用的供应商 → 压缩仍成功，分隔线显示「已改用会话模型」。

## 10. 明确不做

- 不动压缩阈值公式（`autoCompactThreshold`）与它的分母（会话模型的有效窗口）—— 它回答的是
  「正文还装得下吗」，与「用谁来写摘要」是两个问题。
- 不把 `'off'` 改成对所有旁路请求都跟随会话档位（只有压缩这一条是用户选的）。
- 不重排、不重写 `Composer.tsx` / `agent-session.ts` / `i18n/index.tsx` 里与本次无关的既有代码与注释（§12 最小 diff）。
- 不为压缩单独引入模型目录能力校验（`validateModelRuntime`）：摘要请求已剥图、不发工具，
  剩下的失败形态由上游报错反馈，比我们提前猜更准。
