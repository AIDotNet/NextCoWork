# 子代理模型支持可选思考深度

交接给下一个 agent。**这份文档是可独立执行的**:不需要上一位 agent 的对话上下文。
读完先跑一遍「已完成」那一节的验证命令,再按「待办步骤」往下做。

## 需求与已定的设计决定

用户原话:「子代理模型需要支持可选择思考深度」。当时的状态是子 run 的档位**硬继承**父 run
(`src/main/runtime.ts` 里 `childRequestFor` 的 `thinking: parentReq.thinking`),没有任何地方
能单独给子代理选档。

已经和用户确认过的两个选择(不要再改回单选题,这是本次需求的范围本身):

1. **两处都要**:设置里的全局一栏(`settings.subagent.thinking`)**加上** `agents/<name>.md`
   frontmatter 的 `thinking:`(子代理编辑器里可改)。不是二选一。
2. **三档来源,越具体的越优先**:子代理文件声明的 > 设置里那一栏 > 父 run 这一轮的档位。
   这和 `shared/domain/model-selection.ts` 的 `subagentModelSelection`(模型那三档)是**一对**,
   顺序的理由逐字相同:它排在父 run 前面才是那一栏存在的意义(子代理是拿来跑量的,
   用户配它就是为了让那几路别跟着主力模型走)。

另外两个**必须**保留的判断(下一位很容易当成冗余删掉):

- **「跟随本轮对话」必须是一个独立取值 `'inherit'`,不能复用 `'auto'`。** `'auto'` 已经是
  「模型自己的默认档」这个另一个意思;挤在一起就没有任何一处能表达「跟着父 run 走」,
  而那正是引入这一栏之前的行为,老用户升级后必须一个字都不变。
- **取出的档位要按子 run 自己的模型归一化一次**(`normalizeModelThinkingLevel`)。档位是模型的
  属性,而子 run 的模型未必和父 run 是同一个(默认子代理模型、或子代理自己声明的模型)。
  不归一化的症状:适配器要到**发请求那一刻**才抛「模型不支持推理强度「high」」,
  于是每一次派发全数失败,而父代理只会转述一句「子代理失败了」—— 用户看不到那条真正的原因。
  归一化是静默降级 + 一条 `logger.warn`,与同文件 `availableSubagentModel` 同一个取舍。

## 已完成(上一位 agent 的改动,`npm run typecheck:web` 已通过,未跑测试)

1. **新增 `src/shared/domain/subagent-thinking.ts`**(完成,勿改语义):导出
   `INHERIT_THINKING = 'inherit'`、`type SubagentThinking = ThinkingLevel | 'inherit'`、
   `SUBAGENT_THINKING_CHOICES`(`['inherit', ...THINKING_LEVELS]`)、`isSubagentThinking`、
   `subagentThinkingSelection(declared, configured, parent)`。文件头与每条的「为什么」都写在里面。
   **待办里两个下拉的选项表都用 `SUBAGENT_THINKING_CHOICES`,别各自拼一份。**
2. **`src/shared/domain/settings.ts`**(完成):`AppSettings.subagent` 新增
   `thinking: SubagentThinking`;`DEFAULT_SETTINGS.subagent` 为 `thinking: INHERIT_THINKING`;
   `mergeSettings` 的 `subagent` 分支里 spread 之后加了一句
   `if (!isSubagentThinking(next.subagent.thinking)) next.subagent.thinking = current.subagent.thinking`。
   读路径安全:`repo.getSettings()` 是 `mergeSettings(DEFAULT_SETTINGS, patch)`,**嵌套对象是逐块深合并**,
   所以盘上缺 `thinking` 的旧记录读到的是 `'inherit'`,不会出现 `undefined`。
3. `git status`:工作区只剩这两个文件是本次改动。改之前**重新读一遍**,别按这份文档的记忆打补丁。

## 待办步骤

### 1. `src/shared/domain/data.ts` — 导入校验

`isSubagentSettings`(搜索 `function isSubagentSettings`)里加一个可选的枚举校验,理由同该文件其它
「后加的字段」:`缺席 = 旧存档放行,在场就必须合法`。

```ts
return (
  typeof value.model === 'string' &&
  // ★ 思考档位是后加的:缺席 = 这份导出早于这一栏,放行并回落到默认('inherit');
  //   在场就必须是已知档位 —— 坏值会被 merger 丢掉,静默接受一份坏导出只会掩盖它坏了。
  (value.thinking === undefined || isSubagentThinking(value.thinking)) &&
  optionalString(value, 'modelProviderId') &&
  ...
)
```

同时 `import { isSubagentThinking } from './subagent-thinking'`。

### 2. `src/shared/domain/agent-def.ts` — 定义字段

`AgentDefinition` 加字段(放在 `modelProviderId` 之后),`import type { ThinkingLevel } from '../agent/run-request'`:

```ts
/**
 * 思考档位。省略 = 跟随「子代理思考深度」那一栏,再退回父 run 这一轮的档位。
 *
 * ★ 和 `model` **不是**同一个决定:换一个便宜模型跑量的子代理,照样可能需要在
 *   难题上想深一点。三档来源与归一化的理由都在 `shared/domain/subagent-thinking.ts`。
 */
thinking?: ThinkingLevel
```

### 3. `src/main/kernel/agent/load.ts` — 读 frontmatter

在 `const modelProviderId = ...` 之后、`rawColor` 之前插入。**认不出就当作没写**,
**不作废整条**(和 `color` 同一档判断:它不是安全字段;而 `permissionMode` 是,那个才作废)。
诊断要说清「照什么跑」,否则用户改完文件看不出这一行被忽略了。

```ts
/*
  ★ `thinking:` 读不懂就当没写,**不作废**(同 `color`):它只影响花多少钱和想多深,
  而上面那两个 `'invalid'` 分支各自都有非装饰的理由。诊断里必须说清「照哪一档跑」 ——
  只说「忽略了这一行」的话,用户改完文件也看不出子代理实际用的是哪一档。
  `inherit` 不是合法的文件取值:文件里「跟随」表达为**没有这一行**。
*/
const rawThinking = fmString(fm, 'thinking')?.trim().toLowerCase()
let thinking: ThinkingLevel | undefined
if (rawThinking !== undefined) {
  if ((THINKING_LEVELS as readonly string[]).includes(rawThinking)) thinking = rawThinking as ThinkingLevel
  else {
    diagnostics.push({
      path: file,
      message:
        `thinking "${rawThinking}" 不是 ${THINKING_LEVELS.join(' / ')} 之一,已忽略这一行 —— ` +
        '这个子代理照「默认子代理思考深度」那一栏跑(该栏为「跟随对话」时再退回父 run 的档位)。'
    })
  }
}
```

返回值里加 `...(thinking !== undefined ? { thinking } : {})`(和其它可选字段同一种「缺席即省略」的写法,
**不要**写 `thinking: undefined`)。需要的 import:`THINKING_LEVELS`、`type ThinkingLevel`
(from `../../../shared/agent/run-request`)。

`fmString` 已存在(`../frontmatter`),`.trim().toLowerCase()` 与 `permissionMode` / `color` 一致。

### 4. `src/main/runtime.ts` — 落地到子 run

import 区(`subagentModelSelection` 就在附近,第 85 行)加:

```ts
import { normalizeModelThinkingLevel } from '../shared/domain/model-runtime'
import { subagentThinkingSelection, type SubagentThinking } from '../shared/domain/subagent-thinking'
```

**4a. `childRequestFor` 多收一个参数**,并在函数体第一行把模型选择提成一个局部常量
(为避免第二次调 `declaredSubagentModel(def)` —— 那个函数在「摘锁」时会打 warn,调两次会打两条):

```ts
function childRequestFor(
  parentReq: RunRequest,
  parent: RunHandle,
  def: AgentDefinition,
  childRunId: string,
  prompt: string,
  configuredModel: { model: string; modelProviderId?: string },
  configuredThinking: SubagentThinking
): RunRequest {
  /*
    ★ 别名和供应商必须成对决定 —— 三档来源的优先级连同理由都在
    `subagentModelSelection` 里。`configuredModel` 已经过可用性校验(见调用点)。
  */
  const selection = subagentModelSelection(declaredSubagentModel(def), configuredModel, parentReq)
  return {
    ...
    thinking: childThinkingFor(def, configuredThinking, parentReq, selection),
    ...
    ...selection,   // ← 原来是 ...subagentModelSelection(...),位置不动
```

**4b. 新增 `childThinkingFor`**(放在 `childRequestFor` 紧邻处,照 `declaredSubagentModel` 的
「模块级函数 + `getHost().logger`」写法):

```ts
/**
 * 子 run 这一轮用哪个思考档位。
 *
 * 三档来源和模型那一套逐字相同(声明的 > 设置里那一栏 > 父 run),理由见
 * `subagent-thinking.ts`。取完之后**还要按子 run 自己的模型归一化一次** ——
 * 档位是模型的属性,而子 run 的模型和父 run 未必是同一个。
 *
 * ★ 不归一化会怎样:模型不认这个档位时,适配器要到**发请求那一刻**才抛
 * 「模型不支持推理强度「high」」,于是每一次派发全数失败,而父代理只会转述一句
 * 「子代理失败了」—— 用户看不到那条真正的原因,也无从知道该改哪一栏。
 * 归一化是**静默降级**(档位是偏好,不是硬约束;硬约束是药丸上那个显式选择),
 * 与 `availableSubagentModel` 同一个取舍,降级时留一条 warn,免得连日志里都查不到。
 */
function childThinkingFor(
  def: AgentDefinition,
  configured: SubagentThinking,
  parentReq: RunRequest,
  selection: { model: string; modelProviderId?: string }
): ThinkingLevel {
  const wanted = subagentThinkingSelection(def.thinking, configured, parentReq.thinking)
  const alias = getRouter().resolveModel(selection.model, selection.modelProviderId)
  const effective = normalizeModelThinkingLevel(wanted, alias)
  if (effective !== wanted) {
    getHost().logger.warn(
      `[subagent] ${def.name} 的思考档位 ${wanted} 在 ${selection.model} 上不可用,按 ${effective} 跑`
    )
  }
  return effective
}
```

`ThinkingLevel` 已从 `../shared/agent/run-request` 进了吗?runtime 现在只 import 了
`type RunRequest` 和 `MAX_DEPTH`(第 17–18 行)—— 需要**补** `type ThinkingLevel` 的 import。

**4c. 调用点**(`spawnSubagentFor` 里,`const childReq = childRequestFor(...)` 那一处)多传一参:
`configured.thinking`(`configured` 就是上面一行的 `store.getSettings().subagent`,不用另取)。

**注意**:不要动 `subagent_start` 事件的字段 —— 档位目前没有渲染层要显示它的地方,
加字段是「画出来的控件都要有消费方」的反面。

### 5. 渲染层 · 设置页一栏

`src/renderer/src/settings/pages/GeneralPage.tsx`,在 `sub === 'agent'` 分支里
**紧接「默认子代理」那一行之后**(现在约 101–113 行)加一行 `SettingRow wide`,不要放到
`sub === 'task'`(那一页是资源调度/并发,不是模型怎么跑):

```tsx
<SettingRow title={t('general.subagentThinking')} description={t('general.subagentThinkingHint')} wide>
  <Select
    value={settings.subagent.thinking}
    options={SUBAGENT_THINKING_CHOICES.map((value) => ({
      value,
      label: value === INHERIT_THINKING ? t('models.followConversation') : t(`chat.thinkingLevel.${value}`)
    }))}
    ariaLabel={t('general.subagentThinking')}
    onValueChange={(value) => {
      // ★ 只认枚举,认不出的值一个都不许落库(和其它枚举行的写法一致)
      if (!isSubagentThinking(value)) return
      patch({ subagent: { thinking: value } })
    }}
  />
</SettingRow>
```

- import:`SUBAGENT_THINKING_CHOICES`、`INHERIT_THINKING`、`isSubagentThinking`
  from `../../../../shared/domain/subagent-thinking`。
- `t(\`chat.thinkingLevel.${value}\`)` 这种模板串取键在本仓库有先例(`EditWorkspaceDialog.tsx`
  的 `thinkingOptions`)。
- **不要**只显示这个模型支持的档位(像输入框药丸那样过滤):这一栏的模型可能是「跟随对话」,
  在设置页根本定不下来,过滤只会让选项随模型跳来跳去;而「不支持的模型会自动忽略此设置」
  是这个应用已有的既定契约(`shared/agent/run-request.ts` 里那段注释),由运行时归一化兜底。

### 6. 渲染层 · 子代理编辑器

**6a. `src/renderer/src/views/extensions/agents/agent-form.ts`**:`AgentForm` 加
`thinking: string`(`''` = 文件里没有这一行 = 跟随),`formFromFile` 用
`readField(fm, 'thinking')` 之后**仿 `color` 那一行的归一化**——认不出的值当作没写
(`isSubagentThinking(raw) && raw !== INHERIT_THINKING ? raw : ''`),否则下拉会显示一个空选项;
`fileFromForm` 加 `fm = setField(fm, 'thinking', form.thinking.trim())`
(`setField` 空串 = 删键,正是「跟随」的磁盘表示)。

**6b. `src/renderer/src/views/extensions/agents/AgentEditor.tsx`**:在「模型」那一行
(`Row label={t('ext.field.model')}`,约 370 行)**之后**加一行同样形状的 `Row` + `Select`:

```tsx
<Row label={t('ext.field.thinking')}>
  <Select
    value={form.thinking === '' ? INHERIT_THINKING : form.thinking}
    options={SUBAGENT_THINKING_CHOICES.map((value) => ({
      value,
      label: value === INHERIT_THINKING ? t('ext.field.inheritDefault') : t(`chat.thinkingLevel.${value}`)
    }))}
    onValueChange={(v) => setForm({ ...form, thinking: v === INHERIT_THINKING ? '' : v })}
    ariaLabel={t('ext.field.thinking')}
    className="max-w-[320px]"
  />
</Row>
```

`ext.field.inheritDefault`(「继承默认」)已经存在,和模型那一行用的是同一条文案。

**6c. 不动 `markdown/frontmatter-form.ts` 的 `FORM_KEYS`**:那张表的 `agent` 项本来就不含
`modelProviderId` / `color`,而且全仓库没有消费方;`thinking` 靠 `setField` 走「未知键原样带回」
那条路即可。

### 7. i18n(硬规则:中文英文必须同时补,`i18n/index.test.ts` 会校验)

- `src/renderer/src/i18n/agent.ts`(和 `chat.thinkingLevel.*` 挨着,这个文件小、不是热点):
  - `'general.subagentThinking'`: `'子代理思考深度'` / `'Subagent thinking depth'`
  - `'general.subagentThinkingHint'`: 说清两件事 —— 缺省项的含义、以及不支持该档位的模型会自动忽略。
    建议中文:「子代理默认用哪一档思考；「跟随对话」= 用本轮对话的档位。不支持该参数的模型会自动忽略。」,
    英文同义。
- `src/renderer/src/i18n/extensions.ts`(和 `ext.field.model` / `ext.field.modelHint` 挨着):
  - `'ext.field.thinking'`: `'思考深度'` / `'Thinking depth'`
  - `'ext.field.thinkingHint'`: `'留空 = 用设置里的子代理思考深度'` / `'Empty = use the subagent thinking depth from settings'`

  `extensions.ts` 用 `Params` 显式标注带参文案(见文件内既有写法),纯字符串的两条不需要。

### 8. 测试(全部落在既有文件/既有惯例上)

**8a. 新增 `src/shared/domain/__tests__/subagent-thinking.test.ts`** —— 纯函数三档:
声明胜过设置、设置胜过父 run、`'inherit'` 落回父 run、`isSubagentThinking` 的四种边界
(合法档位 / `'inherit'` / 未知串 / 非字符串)。

**8b. `src/shared/domain/__tests__/settings.test.ts`** —— 现有两条会因为 `subagent` 多了一个键
**必须同步改**(这是本次改动的必然代价,不是「顺手改测试」):
- 「六个嵌套块都走深合并」里的 `expect(s.subagent).toEqual({ model: '', perSessionLimit: 4, globalLimit: 8 })`
  → 期望值里加 `thinking: 'inherit'`。
- 「★ subagent 的浅合并不能把旧供应商漏下来」里断言了 `perSessionLimit` 是兄弟属性,无需改,
  但可以顺手补一条新用例:合法档位落库、坏值(如 `'deep'`)退回**当前值**而不是 `'inherit'`。

**8c. `src/main/kernel/__tests__/agent-load.test.ts`**:
- 「读出名字、描述、角色提示词」那条断言了「三个可选字段都没写」—— 加 `expect(a?.thinking).toBeUndefined()`,
  并把注释里的「三个」改成「四个」(这是本次改动让注释失效的情形,§10.2 的改法)。
- 新增:合法档位落到定义上(如 `thinking: HIGH` 大小写不敏感 → `'high'`);认不出的值
  (`thinking: on`)→ **子代理仍然加载**,诊断里提到 `thinking`,且 `thinking` 为 `undefined`。

**8d. `src/main/ipc/__tests__/subagent-wiring.test.ts`** —— 接线验收,用文件里既有的探针启动器
(`installChildRunLauncher` + 自己那份 `captureChildReq`,该文件里每个 describe 各有一份,跟随这个做法)。
建议三条:
1. 设置里那一栏 = `'high'` → `seen[0]?.thinking` 是 `'high'`;
2. 默认(`'inherit'`)→ 子 run 拿到父 run 的档位(父用 `req({ thinking: 'low' })`);
3. 归一化:装一个**只支持 low/medium** 的别名给子代理用,然后把这一栏设成 `'high'` ——
   期望子 run 的档位被归一化成 `'auto'`,而不是把 `'high'` 原样发出去。
   (演示别名 `DEMO_ALIASES[0]` 是 `thinking: true` 且没有 `reasoningEfforts`,
   `modelThinkingLevels` 对它会返回全档,所以**必须**新造一个带
   `thinkingConfig: { mode: 'effort', defaultEnabled: true }` + `reasoningEfforts: ['low','medium']` 的别名,
   否则这条用例根本触发不到归一化。)
4. 子代理文件声明了档位时,它盖过设置里那一栏(`store.putAlias` 造一条 `thinking` 的 agent 文件不方便 ——
   这份测试的 agents 来自真实扫描目录;如果太绕,就把这条放进 `agent-load.test.ts` 那一层,
   接线只用上面 1–3 条。)

## 测试与验证

改完按顺序跑(仓库 CI 跑的就是这一套):

```bash
npm run typecheck:web   # 渲染层 + shared
npm run typecheck:node  # 主进程 + shared
npm test                # vitest
npm run lint            # eslint
```

- **`npm run typecheck:node` 当前有 9 条与本次需求无关的既有报错**
  (`src/main/document-engine/__tests__/native-host.test.ts` 的 4 条 `render`、
  `document-engine/__tests__/provider-registry.test.ts` 与
  `plugin/__tests__/document-rpc.test.ts` / `document-scope.test.ts` 的缺模块与多余属性)。
  **不要去修它们**,只确认自己的文件没有新报错。
- vitest 的 `include` 只收 `src/**/*.test.ts`,**`.test.tsx` 会被静默跳过**。
- 绝不手敲 `tsc -p tsconfig.web.json`(不带 `--noEmit`),产物会盖住源码。

## 边界(不要越界)

- 不改 `RunRequest.thinking` 的类型与语义(`ThinkingLevel` 不变),不新增 IPC 字段,不改 `subagent_start`
  事件形状,不在子代理卡片上显示档位。
- 不动 `SUBAGENT_THINKING_CHOICES` 的排序(`'inherit'` 在最前 = 缺省项),不改 `'inherit'` 这个字符串值
  (它已落盘,改了就丢用户的选择)。
- 不改输入框药丸(父 run)那一套的档位选择与过滤逻辑。
- 顺路发现、**只报不改**:`src/renderer/src/settings/nav.ts` 里「默认模型」「默认子代理模型」两条
  仍然写着 `page: 'model', sub: 'text'`,而这两项早在之前就搬到了「通用 › Agent」—— 这正是该文件
  自己警告过的「表和界面漂移」,但它属于另一件事。
- 并发:同一个仓库可能有别的 agent 在改。改既有文件一律**精确替换**,不整篇覆盖;提交前只看
  `git status` 第一列,只 add 自己改的路径。
