/**
 * 命令和子代理的**编辑**契约 —— 它们在磁盘上都是一个 `.md`,结构同构,
 * 所以读写走同一套形状,而不是各写一份几乎一样的。
 *
 * ★ 这一层和 `CommandDefinition` / `AgentDefinition` 是**两件事**,不要合并:
 *   那两个是「加载器解读之后的结果」(description 有兜底、prompt 被 clamp、
 *   tools 被归一化),是给运行期用的;这里是「文件里到底写了什么」,
 *   是给编辑器用的。拿加载结果去回写,等于把兜底值写进用户的文件 ——
 *   一个从来没写过 description 的命令，编辑一次就会被塞进一行正文的第一句。
 */

export type MarkdownResourceKind = 'command' | 'agent' | 'mode'
/** `builtin` 不在里面:内置的那几条没有源文件,改不了也删不了。 */
export type MarkdownResourceScope = 'global' | 'project'

export interface MarkdownResourceFile {
  kind: MarkdownResourceKind
  scope: MarkdownResourceScope
  /** 文件名去掉 `.md`。命令名 / 子代理名只认它,不认 frontmatter 里的 `name`。 */
  name: string
  /** 绝对路径,给「在编辑器里打开」用。 */
  path: string
  /**
   * 前置块的**原样**内容 —— 包含本应用不认识的键。
   *
   * ★ 表单只覆盖它认识的那几个键,保存时把整个对象传回来,这样从 Claude Code
   *   粘过来的 `color: blue` 之类才不会被编辑一次就抹掉。
   */
  frontmatter: Record<string, string | string[]>
  body: string
  /**
   * 解析器读不懂、**保存时会丢**的语法(两层以上的嵌套、锚点)。
   *
   * 值本身在 parse 阶段就已经没了,序列化器无从恢复 —— 所以非空时 UI 必须在
   * 保存前当面说清楚,而不是默默写出去。这和 `local-settings.ts` 那条
   * 「读不懂就拒绝写」是同一条原则:那边直接拒绝,这边因为用户明确要编辑,
   * 改成告知 + 确认。
   */
  skipped: string[]
  /**
   * `mtimeMs:size` —— 和 `local-settings.ts` 的缓存键同一种格式。
   *
   * 保存时带回来对不上就拒绝写:两个窗口开着同一个文件、或者用户在外部编辑器
   * 里改过,最后保存的那一次不该把另一次静默盖掉。
   */
  revision: string
}

export interface MarkdownResourceSave {
  kind: MarkdownResourceKind
  scope: MarkdownResourceScope
  name: string
  /** 项目作用域必填;全局作用域忽略。 */
  workspaceId?: string
  frontmatter: Record<string, string | string[]>
  body: string
  /** 新建时省略;编辑时必带,对不上回 `conflict`。 */
  revision?: string
}

/** 列表里的一行。比 `AgentDefinition` 多一个 `enabled`,少那些运行期字段。 */
export interface AgentListItem {
  name: string
  description: string
  scope: 'builtin' | 'global' | 'project'
  /** 绝对路径;内置的是空串。 */
  source: string
  tools?: string[]
  model?: string
  permissionMode?: string
  /** 列表行前面那个小圆点。纯装饰,见 `agent-def.ts` 的 `AgentColor`。 */
  color?: string
  /**
   * ★ 存在 kv 里,**不写进 `.md`**。
   *
   * 「这台机器上我不想用这条」是每机器偏好 —— 写进文件会在共享仓库里产生一个
   * git diff，替队友做了决定。这和 `settings.local.json` 文件头反对的是同一件事。
   */
  enabled: boolean
}

/** 命令列表里那一行额外需要的状态（`CommandDefinition` 是内核产物，不带 UI 状态）。 */
export interface CommandListItem {
  name: string
  description: string
  scope: 'builtin' | 'global' | 'project'
  source: string
  argumentHint?: string
  enabled: boolean
}

export const MARKDOWN_RESOURCE_BODY_MAX = 64 * 1024
