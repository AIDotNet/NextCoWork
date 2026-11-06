/**
 * 子代理表单 ⇄ 文件的双向映射 —— 纯函数,理由同 `markdown/frontmatter-form.ts`
 * (vitest 的 include 只收 `.test.ts`,`.tsx` 里的东西测不到)。
 *
 * ★ 承接那个文件的核心约束:**表单只覆盖它认识的那几个键,其余原样带回去**。
 *   `permissionMode` 就是靠这条活下来的:界面上没有它的控件(新建时一律写成
 *   `full`,见 `AgentsPanel`),但文件里已经写着的那个值编辑一次不能被改掉。
 *   所以这里所有的写入都走 `setField`/`setListField`,而不是自己拼一个新对象 ——
 *   从 Claude Code 粘过来的文件里那些本应用不认识的键,编辑一次不能被抹掉。
 */
import type { AgentColor, AgentDraft } from '../../../../../shared/domain/agent-def'
import { AGENT_NAME_RE, AGENT_TOOL_CHOICES, isAgentColor } from '../../../../../shared/domain/agent-def'
export { AGENT_COLOR_HEX, agentColorHex } from '../../../../../shared/domain/agent-def'
import { INHERIT_THINKING, isSubagentThinking } from '../../../../../shared/domain/subagent-thinking'
import { readField, readListField, setField, setListField, type Frontmatter } from '../markdown/frontmatter-form'

/**
 * 表单的全部状态。
 *
 * ★ `toolsMode` 是**表单独有**的,文件里没有对应的键:磁盘上「继承全部工具」
 *   表达为**没有 `tools` 这一行**,而界面上必须把它变成一个看得见的选择 ——
 *   「留空 = 继承全部」只写在 placeholder 里的话,没人读得到,而这正是绝大多数
 *   人要的默认值。
 */
export interface AgentForm {
  name: string
  description: string
  /** 正文 = 角色提示词。 */
  prompt: string
  /** `''` = 继承默认子代理模型(删掉 `model:` 这一行)。 */
  model: string
  /**
   * 钉死给哪一家发。`''` = 不钉,由路由器按优先级择优。
   *
   * ★ 只在 `model` 有值时才写得出去 —— 没别名却锁着一家在下游没有定义,
   *   `fileFromForm` 会顺手把它删掉。
   */
  modelProviderId: string
  /** `''` = 跟随「子代理思考深度」那一栏(文件里没有 `thinking:` 这一行)。 */
  thinking: string
  /** `''` = 不标颜色。 */
  color: string
  toolsMode: 'all' | 'custom'
  /** 只在 `toolsMode === 'custom'` 时有意义;切回 `all` 时**保留**,免得误切一下就全没了。 */
  tools: string[]
}

/** 文件 → 表单。`name` 以**文件名**为准,理由见 `fileFromForm`。 */
export function formFromFile(name: string, fm: Frontmatter, body: string): AgentForm {
  const tools = readListField(fm, 'tools')
  const color = readField(fm, 'color').trim().toLowerCase()
  const thinking = readField(fm, 'thinking').trim().toLowerCase()
  return {
    name,
    description: readField(fm, 'description'),
    prompt: body,
    model: readField(fm, 'model'),
    modelProviderId: readField(fm, 'modelProviderId'),
    // 认不出的颜色当作没标 —— 同 `agent/load.ts`,它纯装饰,不该让表单显示一个空选项。
    color: isAgentColor(color) ? color : '',
    // 同 `color`:认不出就当没写。`inherit` 不是合法的文件取值 —— 文件里的
    // 「跟随」表达为没有这一行,所以这里也把它当作没写处理。
    thinking: isSubagentThinking(thinking) && thinking !== INHERIT_THINKING ? thinking : '',
    toolsMode: tools.length > 0 ? 'custom' : 'all',
    // 文件里写的名字可能不在白名单里(比如 MCP 工具的全名),那些格子勾不出来,
    // 但也**不能丢** —— 丢了就是用户编辑一次就少了一个工具。留在这里由
    // `fileFromForm` 原样写回去。
    tools
  }
}

/**
 * 表单 → 文件。`base` 是读进来的那份 frontmatter,未知键从它身上带走。
 *
 * ★ 同时写 `name:` 和文件名两处,且必须一致:`agent/load.ts` 里 frontmatter 的
 *   `name` 胜过文件名,两者不一致时会留下一条诊断。表单里改名字改的是**两者**,
 *   所以这里无条件把 `name` 对齐到文件名,而不是让它保留文件里的旧值。
 */
export function fileFromForm(form: AgentForm, base: Frontmatter): { frontmatter: Frontmatter; body: string } {
  let fm = base
  fm = setField(fm, 'name', form.name)
  fm = setField(fm, 'description', form.description.trim())
  const model = form.model.trim()
  fm = setField(fm, 'model', model)
  // 选了「继承默认」就把锁一起删掉,不留一个孤零零的供应商。
  fm = setField(fm, 'modelProviderId', model === '' ? '' : form.modelProviderId.trim())
  fm = setField(fm, 'thinking', form.thinking.trim())
  fm = setField(fm, 'color', form.color.trim())
  // ★ `all` 档**删掉** `tools` 键,而不是写一张全表:写全表的话,以后新增一个工具,
  //   这条子代理不会拿到它 —— 而用户当初选的是「默认全部」。
  fm = setListField(fm, 'tools', form.toolsMode === 'all' ? [] : form.tools)
  return { frontmatter: fm, body: form.prompt }
}

/** 保存前的校验。返回 i18n key,`null` = 可以存。 */
export function validateAgentForm(form: AgentForm): string | null {
  if (!AGENT_NAME_RE.test(form.name.trim())) return 'ext.error.agentBadName'
  if (form.description.trim() === '') return 'ext.error.agentNeedsDescription'
  if (form.prompt.trim() === '') return 'ext.error.emptyBody'
  /*
    ★ 勾了「自定义」却一个都没勾,正是 `agent/load.ts` 里那条把**整条子代理作废**
    的形状(空工具表跑起来的子代理不会报错,它会编一个答案交回去)。在这里拦下来,
    比让用户存完之后发现它消失了要好得多。
  */
  if (form.toolsMode === 'custom' && form.tools.length === 0) return 'ext.error.agentNoTools'
  return null
}

/** 白名单之外的工具(比如手写的 MCP 全名)—— 复选格显示不了它们,得另外说一声。 */
export function unknownTools(form: AgentForm): string[] {
  const known = new Set<string>(AGENT_TOOL_CHOICES)
  return form.tools.filter((tool) => !known.has(tool))
}

/**
 * 把生成结果并进表单。
 *
 * ★ 是**整份覆盖**,不是逐字段填空:用户点「生成」表达的就是「这一份我不要了,
 *   照我说的重来」。留着上一轮的半截描述配这一轮的提示词,得到的是一份两不像的
 *   东西,而它看起来完全正常。作用域不在草稿里,它不动。
 */
export function applyDraft(form: AgentForm, draft: AgentDraft): AgentForm {
  const color: AgentColor | '' = draft.color ?? ''
  return {
    ...form,
    name: draft.name,
    description: draft.description,
    prompt: draft.prompt,
    color,
    toolsMode: draft.tools === undefined ? 'all' : 'custom',
    tools: draft.tools === undefined ? form.tools : [...draft.tools]
  }
}
