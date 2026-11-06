/**
 * 工具 → 展示契约的注册表 —— 设计文档 `docs/tool-timeline-design.md` §1。
 *
 * ★ **为什么是注册表而不是 `switch (name)`。**
 * MCP 工具在编译期不可枚举,`switch` 必然带一个 `default`;而 `default` 一旦存在,
 * 新增内置工具时忘记加分支**不会有任何编译错误** —— 它会静默落进兜底,
 * 表现为「新工具长得和 MCP 工具一模一样」,而没有人会去查一个不报错的地方。
 * 写成数据之后,`index.test.ts` 里一条 `builtinTools().every(t => t.internalId in REGISTRY)`
 * 就能让遗漏在测试期炸出来。
 *
 * ★ **这里的函数全是纯函数,入参只有 `(input, output)`,不许碰任何 store。**
 * 这是它们能被单测穷尽的前提,也是它们能同时服务于「已提交的 parts」和
 * 「还在流的 live 块」两条路径的前提。
 *
 * ★ **`input` 是 `unknown`,而且经常不是完整对象。** 流式中的 `tool_call_delta`
 * 会先被工具卡片投影成「目前可读出的部分对象」；畸形前缀仍会退回原始字符串。
 * 所有 `pick` 因此都必须安全收窄 —— 字段没到时标题要退化成可读短语,不能崩溃或显示 "undefined"。
 *
 * ★ **查表的键是 `externalName`**(转录里存的那个),不是 `internalId`。
 * 内置工具两者一致,MCP 超长名的差异由 `parseMcpId` 吸收 —— 详见那里的说明。
 *
 * ★ **这里不产 UI 文案,只产 key + 参数**(见下面「文案层」):文案住在渲染层
 * i18n(`renderer/i18n/tool-presenter.ts`),由渲染层启动时把 translate 注入回来。
 * 在这个文件里写中文字符串等于让英文界面永远显示中文 —— 历史上正是这样。
 */
import type { ToolOutput } from '../agent/message'
import { countLineDiff } from './line-diff'

/**
 * 展示形态。判据是**「展开后详情区该用哪种渲染器」**,不是工具的功能领域。
 *
 * 功能相近但形态不同的必须分开:`Read` 与 `Write` 都是 fs,但一个是只读预览、
 * 一个是写入摘要。功能不同但形态相同的可以合并:`Glob` 与 `Grep` 都是命中列表。
 *
 * 刻意**不复用** `ToolInfo.readOnly` / `destructive`(`tool.ts`)来分类 ——
 * 那两个是**权限维度**,与展示维度正交:`Read` 和 `Grep` 都是 readOnly,
 * 详情长相却完全不同。
 */
export type ToolShape =
  | 'reasoning'
  | 'read'
  | 'mutate'
  | 'search'
  | 'command'
  | 'network'
  | 'orchestration'
  /**
   * 等用户表态的那几个(`AskUserQuestion` / `ProposeGoal` / `ExitPlanMode`)。
   *
   * 需求:它们的入参**就是给人读的题面**,而不是给工具用的参数 —— 详情区要按题面渲染,
   * 而且要在参数还在流的时候就能读(见 `views/chat/interaction-preview.ts`)。
   * 归进 `orchestration` 的话,一道四选项的问题会被摊成一坨 JSON,
   * 而用户接下来正要在下面那张卡里回答它。
   *
   * ★ 这里画出来的一切**都不可作答**:可作答的题面只有主进程待决表一个来源。
   */
  | 'interaction'
  /**
   * 自己画一张图的那一个(`visualize_show_widget`)。
   *
   * 需求:它展开后不是一份数据,而是一个**正在长出来的 HTML/SVG**
   * (`views/chat/WidgetDetail.tsx` → `views/chat/WidgetFrame.tsx`)。参数还在流的时候
   * 就要开始渲染,所以它不能落进 `external` —— 那会先把半截 JSON 摊成一屏花括号,
   * 等参数收尾才整块换成图,而"边写边渲染"正是这个功能本身。
   *
   * ★ **这一档只有内置工具能选。** 插件侧的形态清单是另一份内联字面量
   * (`plugin/manifest.ts` 的 `PluginToolShape`,刻意不 import 这个类型),
   * 里面没有 `widget`,而且不该有:插件工具的入参是**模型生成的**,于是
   * "让插件声明一个 widget 形态"就等于把"往宿主渲染的 iframe 里塞任意 HTML"
   * 这条路开给了「插件描述 → 模型 → 用户内容」那一串不可信输入。
   * 给插件形态清单加这一项之前,先读 `shared/agent/tool-card.ts` 里
   * `kind: 'widget'` 上那段说明。
   */
  | 'widget'
  /**
   * 对话内生图那一个(`generate_image`)。
   *
   * 需求:它展开后是一张**图片卡**(`views/chat/ImageGenDetail.tsx`):生成期按 `n`
   * 画几格专属加载动画、逐张换成真图,跑完是可点开放大的图片网格。原先它归在
   * `network`,而 `NetworkDetail` 成功时只把 `output.content` 画成代码块、根本不读
   * `output.images` —— 表现为「模型说画好了,卡片里只有一句英文」。
   *
   * ★ 与 `widget` 分开:那一档画的是模型**写出来的代码**(边流边渲染 HTML),
   * 这一档画的是工具**取回来的图**(入参里只有 prompt 和张数),两套生命周期。
   * ★ 它的产物不随过程折叠收起 —— 见 `isPinnedShape`。
   */
  | 'image'
  /**
   * 浏览器页面的截图(`browser_screenshot`)。
   *
   * 需求:截图是**模型看得见、用户看不见**的那一类结果 —— 它落在 `output.images`
   * 里,而这一档原先没有形态,于是整族浏览器工具落进 `external` 的通用 JSON 渲染器,
   * 那里根本不读 `output.images`。表现为「让 Agent 去看看这个页面长什么样」,
   * 对话里只有一行英文回执,图只进了模型的上下文。
   *
   * ★ 与 `image` 分开,虽然两者展开后都是「一张图」:判据是生命周期不同,
   * 而形态类的判据本来就是「用哪种渲染器」而不是「产物是不是图」。
   * 生图那张卡要摆 N 格占位、逐张换图、底下挂提示词区(入参里有 `n` 与 `prompt`);
   * 截图**没有生成期、没有提示词**,只有一帧视口 —— 复用生图卡的话,
   * 它会显示「生成中」并在图旁边留一块永远空着的提示词区。
   *
   * ★ 同样不进过程折叠(见 `isPinnedShape`):它是「那个页面此刻长什么样」的
   * 唯一证据,而 Agent 一大半的浏览器动作(text/click)的结果都是**它**。
   */
  | 'screenshot'
  /**
   * 对话内生成视频那一个(`generate_video`)。
   *
   * 需求:它展开后是一张**视频卡**:一张 `<video controls>` 加一条任务状态行。
   * 原先归在 `network`,`NetworkDetail` 成功时只把 `output.content` 画成代码块、
   * 完全不读 `output.videos` —— 表现为"模型说好了,卡片里只有一句英文回执"。
   *
   * ★ 与 `image` 分开:图片卡是"按格出图 + 提示词区",而视频卡的核心是
   * **一条异步任务的生命周期**(排队/生成/下载/完成),那在图片上不存在。
   */
  | 'video'
  /**
   * 编排一张子代理依赖图的那一个(`Workflow`,实验功能)。
   *
   * 需求:它展开后是一张**画布**(`views/chat/WorkflowCanvas.tsx`):节点 + 依赖连线,
   * 每个节点的状态实时变。归进 `orchestration` 的话会和 `Task` 一样画成一行摘要,
   * 而「哪些在并行、谁在等谁」正是这张图要给人看的东西。
   * ★ 不进过程折叠(见 `isPinnedShape`):图就是这一步的产物。
   */
  | 'workflow'
  | 'external'

/**
 * 折叠态那一行的**结构化字段**。
 *
 * ★★ 需求:工具行要读起来像一份活动日志,而不是一串句子 ——
 * 「编辑 · CHANGELOG.md · src/docs/」三段各有各的亮度,扫一眼就能分出
 * 「做了什么 / 对谁做的 / 在哪儿」。原先这里只有一个拼好的 `title` 字符串
 * (`'读取 index.ts'`),于是**目录、文件类型、命令原文在拼接的那一刻就丢了**,
 * 渲染层再想分色显示只能去反向切字符串 —— 那种切法对中英文和 MCP 名各错一次。
 *
 * ★ 三段的职责固定,不要按工具临时改用途:
 *   label   已翻译的动作词。**永不为空**,参数还在流时行里至少有它。
 *   target  这次动作的主语(文件名 / 命令 / 查询词)。领域值,**不翻译**。
 *   context 主语的从属信息(所在目录 / 命令原文 / MCP server)。次要,可省。
 */
export interface ToolLine {
  label: string
  target?: string
  context?: string
  /** target 按等宽渲染(命令、glob 模式、id);context 一律等宽,不受它影响 */
  mono?: boolean
  /** 这一行指向的文件路径 —— 渲染层据此画文件类型标记。没有文件就别给 */
  path?: string
}

/**
 * 这次调用改了多少行 —— 行右端那个 `+7 −1`。
 *
 * ★★ 需求:「编辑了 CHANGELOG.md」回答不了「改动大不大」,而那恰恰是用户决定
 * 「要不要展开看」的依据。数字**只能来自这次调用自己的数据**(Edit 的
 * old/new_string、Write 新建时的 content),不许去问改动审查那份按文件聚合的统计 ——
 * 同一个文件在一轮里可能被改五次,聚合数字挂到每一行上,五行会显示同一个总数。
 *
 * ★ 算不出来就**不给**:覆写已有文件时旧内容不在入参里,硬报一个「−0」等于
 * 告诉用户这次没删过东西。宁可这一格空着。
 */
export interface ToolLineStats {
  additions: number
  deletions: number
}

export interface ToolPresenter {
  shape: ToolShape
  /**
   * 折叠态那一行。★ 拿不到入参时**必须仍返回一个可读的 label**,不能返回空 ——
   * 流式中途每一帧都会经过这里。
   */
  line: (input: unknown) => ToolLine
  /** 折叠态右侧摘要。信息不足时返回 `undefined`,调用方据此不渲染那一格。 */
  summary?: (input: unknown, output: ToolOutput | undefined) => string | undefined
  /** 行右端的增删行数。算不准就返回 undefined —— 见 `ToolLineStats`。 */
  stats?: (input: unknown, output: ToolOutput | undefined) => ToolLineStats | undefined
  /**
   * 运行中能不能被用户**单独**停掉(`shell:stopToolCall`)。
   *
   * ★ 这是一条**能力声明,不是偏好**:主进程只为 `Bash` 寄存停止句柄
   * (见 `main/agent-shells.ts`),别的工具画了按钮也停不掉。写成注册表里的一行、
   * 而不是卡片里的 `toolName === 'Bash'`,是为了让「哪些工具可停」和
   * 「哪些工具长什么样」住在同一张表里 —— 下一个可停的工具只要在这儿加一个字段。
   */
  stoppable?: boolean
}

// ─────────────────── 文案层(由渲染层 i18n 注入) ───────────────────

/**
 * 内置工具卡片全部用户可见文案的 key,一张表同时是类型和清单。
 *
 * 需求:标题/摘要是 UI 文案,按仓库规矩必须住在渲染层 i18n 表里(AGENTS.md §6);
 * 而这个注册表是 shared 纯函数,不能反向 import 渲染层。于是这里只产出
 * **key + 抽好的参数**,字符串拼接由注入进来的翻译函数完成 —— 与插件 presenter
 * 的注入层(`renderer/stores/plugins.ts`)是同一个模式。参数名全表统一用
 * `target` / `count` / `code` / `days` / `time`,文案表因此能写成一族小函数。
 *
 * ★ `chat.tool.title.*` 这一族现在是**纯动作标签**(「读取」「编辑」),不再带
 * `{target}` —— 目标由 `ToolLine.target` 单独给,拼接发生在渲染层的版式里而不是
 * 文案里。key 名保持不变是为了不动已落地的两份 catalog 和插件文案前缀检查;
 * 改名会让这次改动的 diff 淹没在重命名里(§12 最小 diff)。
 */
export const PRESENTER_COPY_KEYS = [
  // 折叠态动作标签(「读取」「执行」;目标是 ToolLine.target,不进这张表)
  'chat.tool.title.read',
  'chat.tool.title.ls',
  'chat.tool.title.write',
  'chat.tool.title.edit',
  'chat.tool.title.glob',
  'chat.tool.title.grep',
  'chat.tool.title.bash',
  'chat.tool.title.bashOutput',
  'chat.tool.title.killShell',
  'chat.tool.title.webFetch',
  'chat.tool.title.webSearch',
  'chat.tool.title.skill',
  'chat.tool.title.task',
  'chat.tool.title.scheduleCreate',
  'chat.tool.title.scheduleUpdate',
  'chat.tool.title.scheduleDelete',
  'chat.tool.title.todo',
  'chat.tool.title.scheduleList',
  // 等用户表态的三个(shape: 'interaction')。标题是静态的:它们的入参是题面本身,
  // 塞进标题只会把一整道题截断成一行省略号。
  'chat.tool.title.askUser',
  'chat.tool.title.proposeGoal',
  'chat.tool.title.planReview',
  /*
    可视化那一对。`readMe` 是静态标题(它的入参只有一串模块名,那是摘要的活);
    `widget` 带 `target` —— 那个 target 是模型写的 `title`(snake_case 标识)
    去掉下划线之后的短语,所以它**不翻译**(同模型名、文件名,见 AGENTS §6.5)。
  */
  'chat.tool.title.widget',
  'chat.tool.title.widgetEdit',
  'chat.tool.title.widgetUpdate',
  'chat.tool.title.readMe',
  // 工作流(实验)。target 是模型写的 `title`,领域值,不翻译
  'chat.tool.title.workflow',
  // 对话内生图。行的主语是模型收到的 prompt 片段(领域值,不翻译)
  'chat.tool.title.generateImage',
  // 改图那半:入参带了源图(`image`)就是它,否则是上面那个
  'chat.tool.title.editImage',
  // 把对话里的图写进工作区(SaveImage)。行的主语是目标文件,同 Write
  'chat.tool.title.saveImage',
  /*
    对话内生成视频。三个动作各有标签:提交/查询/取消 —— 行的主语是 prompt 片段
    (提交)或什么都不给(查询/取消,因为它们的入参里只有 job_id,对人没有信息量)。
  */
  'chat.tool.title.generateVideo',
  'chat.tool.title.checkVideo',
  'chat.tool.title.cancelVideo',
  // 把对话里的视频写进工作区(SaveVideo)。主语同 SaveImage,是目标文件
  'chat.tool.title.saveVideo',
  /*
    浏览器那一族的动作标签(`browser_*`)。
    ★ 需求:这一族此前**全都**没登记,兜底路径把 internalId 的可读化结果直接画到行里
    —— 中文界面上是一列「browser click / browser type / browser screenshot」。
    这一族也是「新增内置工具忘了登记展示规则」那条不变式唯一没守住的缺口
    (清单在 `__tests__/tool-presenter.test.ts` 的 BUILTIN_IDS,原先把它们整族漏了)。
  */
  'chat.tool.title.browserOpen',
  'chat.tool.title.browserNavigate',
  'chat.tool.title.browserSnapshot',
  'chat.tool.title.browserScreenshot',
  'chat.tool.title.browserClick',
  'chat.tool.title.browserType',
  'chat.tool.title.browserPress',
  'chat.tool.title.browserSelect',
  'chat.tool.title.browserScroll',
  'chat.tool.title.browserCuaClick',
  'chat.tool.title.browserCuaDrag',
  'chat.tool.title.browserTabs',
  'chat.tool.title.browserUserTabs',
  'chat.tool.title.browserClaim',
  'chat.tool.title.browserProfiles',
  'chat.tool.title.browserClose',
  // 认不出工具名时的最终兜底标题
  'chat.tool.fallback',
  // 折叠态右侧摘要
  'chat.tool.summary.lines',
  'chat.tool.summary.items',
  'chat.tool.summary.nodes',
  'chat.tool.summary.files',
  'chat.tool.summary.matches',
  'chat.tool.summary.results',
  'chat.tool.summary.outputLines',
  'chat.tool.summary.createdLines',
  'chat.tool.summary.created',
  'chat.tool.summary.replaced',
  'chat.tool.summary.exitCode',
  'chat.tool.summary.noOutput',
  'chat.tool.summary.running',
  'chat.tool.summary.stopped',
  'chat.tool.summary.tasks',
  'chat.tool.summary.questions',
  'chat.tool.summary.scheduleDaily',
  'chat.tool.summary.scheduleWeekly'
] as const

export type PresenterCopyKey = (typeof PRESENTER_COPY_KEYS)[number]

export type PresenterTranslate = (
  key: PresenterCopyKey,
  params?: Record<string, string | number>
) => string

/**
 * ★ 默认**原样回显 key**:渲染层还没接上(以及 shared 单测没注入替身)时,
 * 标题会显示成 `chat.tool.title.read` —— 难看但可见、不崩,和渲染层 i18n
 * 缺 key 时显示 key 本身(`interpolate` 的 missingKey)是同一条策略。
 */
let presenterCopy: PresenterTranslate = (key) => key

/**
 * 渲染层 i18n 在模块加载时调一次(`i18n/index.tsx`)。
 *
 * ★ 注入的是翻译函数**本体**而不是预先算好的字符串:每次渲染取 title 时才现查
 * 当前 locale,切换语言不需要重建任何 presenter —— 已提交的转录卡片也因此自动跟换。
 */
export function setPresenterTranslate(t: PresenterTranslate): void {
  presenterCopy = t
}

// ─────────────────────────── 取值原语(全部对 unknown 安全) ───────────────────────────

/** 安全取字符串字段;不是对象、字段缺失、类型不符 → `''` */
export function pick(input: unknown, key: string): string {
  if (typeof input !== 'object' || input === null) return ''
  const v = (input as Record<string, unknown>)[key]
  return typeof v === 'string' ? v : ''
}

/** 安全取数组字段;拿不到 → 空数组 */
function pickArray(input: unknown, key: string): readonly unknown[] {
  if (typeof input !== 'object' || input === null) return []
  const v = (input as Record<string, unknown>)[key]
  return Array.isArray(v) ? v : []
}

/**
 * 路径尾段。**同时按 `/` 和 `\` 切** —— Windows 上传进来的是反斜杠路径,
 * 只切正斜杠会让整条 `C:\a\b\c.ts` 原样显示,把标题撑爆。
 */
export function base(path: string): string {
  if (path === '') return ''
  const trimmed = path.replace(/[/\\]+$/, '')
  const at = Math.max(trimmed.lastIndexOf('/'), trimmed.lastIndexOf('\\'))
  const name = at >= 0 ? trimmed.slice(at + 1) : trimmed
  return name === '' ? path : name
}

/**
 * 截断到 n 字符并加省略号。
 *
 * 顺手把换行折成空格:多行 shell 命令直接进标题会把单行布局撑成三行,
 * 而标题行的高度是折叠列表能否保持整齐的关键。
 */
export function clip(s: string, n: number): string {
  const flat = s.replace(/\s+/g, ' ').trim()
  return flat.length <= n ? flat : `${flat.slice(0, n)}…`
}

/** `create_pull_request` → `create pull request` —— MCP 工具名的可读化 */
export function humanize(s: string): string {
  return s.replace(/[_-]+/g, ' ').trim()
}

/**
 * `mcp__github-enterprise__create_pr` → `{ server: 'github-enterprise', tool: 'create_pr' }`
 *
 * ★ **入参实际是 `externalName`,不是 `internalId`。**
 *
 * `agent-session.ts` 发 `tool_start` 时带的是 `tool.externalName`,而且有一条测试
 * 专门钉住这件事(`agent-session.test.ts` 的「tool_start 用的是 externalName,
 * 与转录里的名字一致」)—— 因为**已落盘的转录里存的就是 externalName**,
 * 改发射端会让所有历史转录的工具名对不上。所以适配放在这一侧,不动发射端。
 *
 * 对内置工具两者相同(`Read` / `Bash` 都短且合法,`ToolNamer.allocate` 原样返回),
 * 查表不受影响。只有超长的 MCP 名会被截断并追加 8 位 FNV 哈希,
 * 于是这里要把那个后缀剥掉,否则标题会显示成 `create pull request 3a7f21b9`。
 */
export function parseMcpId(internalId: string): { server: string; tool: string } | null {
  const m = /^mcp__([^_].*?)__(.+)$/.exec(internalId)
  if (!m) return null
  const server = m[1]
  const rawTool = m[2]
  if (server === undefined || server === '' || rawTool === undefined || rawTool === '') return null
  return { server, tool: stripNameHash(internalId, rawTool) }
}

/**
 * 剥掉 `ToolNamer` 为去重追加的 `_xxxxxxxx` 后缀。
 *
 * ★ **只在名字确实接近长度上限时才剥**。哈希只有在原名超过 64 字符
 * (`EXTERNAL_NAME_MAX`)时才会被追加,所以短名一律原样返回 ——
 * 否则一个真名就叫 `sync_1a2b3c4d` 的工具会被无辜切掉尾巴。
 */
function stripNameHash(fullName: string, tool: string): string {
  if (fullName.length < 55) return tool
  const stripped = tool.replace(/_[0-9a-f]{8}$/, '')
  return stripped === '' ? tool : stripped
}

/** URL 的主机名;解析不了就原样截断(流式中途的半截 URL 走这条) */
function hostOf(url: string): string {
  if (url === '') return ''
  try {
    return new URL(url).hostname
  } catch {
    return clip(url, 30)
  }
}

/** 输出的行数。空输出算 0 行,末尾换行不重复计数。 */
function lineCount(output: ToolOutput | undefined): number | undefined {
  if (output === undefined) return undefined
  const c = output.content
  if (c === '') return 0
  return c.replace(/\n$/, '').split('\n').length
}

/**
 * 路径的目录部分,带尾斜杠;过深时只留最后三段并前置省略号。
 *
 * 需求:工具行右边那一格要回答「这个文件在哪儿」,而入参里的路径是**绝对路径**
 * (`/Users/x/code/proj/src/main/db/a.ts`)。整条画出来会把行撑满,读者真正认得出的
 * 也只是末尾那几段。
 *
 * ★ `root` 给了就先按工作区根裁成**相对路径**(`src/main/db/`)—— 那是用户脑子里
 * 真正在用的坐标系,绝对路径的前四段对他没有任何信息量。★ 但**根外的文件必须
 * 仍然显示绝对路径**:读一个仓库外的文件时,把它画成相对路径等于说谎。
 * root 是渲染层传下来的(workspace.rootPath),这个模块本身不碰 store。
 */
export function dirOf(path: string, root?: string): string {
  if (path === '') return ''
  const at = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'))
  if (at <= 0) return ''
  const dir = path.slice(0, at)
  const sep = dir.includes('\\') && !dir.includes('/') ? '\\' : '/'
  const inRoot = insideRoot(dir, root)
  const parts = (inRoot ?? dir).split(/[/\\]/).filter((p) => p !== '')
  if (parts.length === 0) return ''
  const tail = parts.slice(-3)
  const prefix = parts.length > tail.length ? `…${sep}` : ''
  return `${prefix}${tail.join(sep)}${sep}`
}

/**
 * `dir` 在 `root` 里面时返回去掉根之后的那一段,否则 null。
 *
 * ★ 比较前把尾斜杠去掉,并要求边界正好落在分隔符上 —— 否则
 * `/w/proj` 会把 `/w/project-b/src` 也认成自己的子目录(前缀匹配的经典坑),
 * 表现为另一个项目的文件被画成本项目的相对路径。
 */
function insideRoot(dir: string, root: string | undefined): string | null {
  if (root === undefined || root === '') return null
  const base = root.replace(/[/\\]+$/, '')
  if (dir === base) return ''
  const next = dir[base.length]
  if (!dir.startsWith(base) || (next !== '/' && next !== '\\')) return null
  return dir.slice(base.length + 1)
}

/**
 * 把一行拍平成一个字符串 —— 给「只有一格位置」的地方用(测试断言、将来的
 * tooltip / 命令面板)。渲染层的工具行**不该**用它:那正是要分色显示的三段。
 */
export function toolLineText(line: ToolLine): string {
  const target = line.target === undefined ? '' : line.target.trim()
  return target === '' ? line.label : `${line.label} ${target}`
}

// ─────────────────────────── 各形态的摘要提取 ───────────────────────────

function readSummary(_i: unknown, o: ToolOutput | undefined): string | undefined {
  const n = lineCount(o)
  return n === undefined ? undefined : presenterCopy('chat.tool.summary.lines', { count: n })
}

function lsSummary(_i: unknown, o: ToolOutput | undefined): string | undefined {
  const n = lineCount(o)
  return n === undefined ? undefined : presenterCopy('chat.tool.summary.items', { count: n })
}

/**
 * Write 的输出形如 `Created a.ts (12 lines, 340 bytes)` / `Overwrote …`。
 *
 * 正则失败就退回 undefined 而不是猜 —— 摘要错了比没有更糟:
 * 用户会拿它当真,而它恰恰是最不该被信错的那类数字。
 */
function writeSummary(_i: unknown, o: ToolOutput | undefined): string | undefined {
  if (o === undefined) return undefined
  const created = /^Created\b/i.test(o.content)
  const m = /\((\d+)\s+lines?/i.exec(o.content)
  const lines = m?.[1]
  if (lines === undefined) return created ? presenterCopy('chat.tool.summary.created') : undefined
  const count = Number(lines)
  return created
    ? presenterCopy('chat.tool.summary.createdLines', { count })
    : presenterCopy('chat.tool.summary.lines', { count })
}

/** Edit 的输出形如 `Edited a.ts: replaced 3 occurrence(s).` */
function editSummary(_i: unknown, o: ToolOutput | undefined): string | undefined {
  if (o === undefined) return undefined
  const m = /replaced\s+(\d+)\s+occurrence/i.exec(o.content)
  const n = m?.[1]
  return n === undefined ? undefined : presenterCopy('chat.tool.summary.replaced', { count: Number(n) })
}

/**
 * Edit 的 `+X −Y`:两侧都在入参里,所以**流式中途就能算**,不必等工具跑完。
 * 两侧都空(参数还没到)时不给数字 —— 一行「+0 −0」会被读成「这次什么也没改」。
 */
function editStats(i: unknown): ToolLineStats | undefined {
  const oldStr = pick(i, 'old_string')
  const newStr = pick(i, 'new_string')
  if (oldStr === '' && newStr === '') return undefined
  return countLineDiff(oldStr, newStr)
}

/**
 * Write 只在**新建**时给数字。
 *
 * ★ 判据是输出里的 `Created`,不是「入参里有 content」:覆写时旧内容不在任何
 * 入参里,算出来的 `−0` 是假的(`fs.ts` 的输出文案区分 Created / Overwrote)。
 * 所以流式期间这一格是空的,跑完是新建才填上 —— 宁可晚一点,不要一个错的数。
 */
function writeStats(i: unknown, o: ToolOutput | undefined): ToolLineStats | undefined {
  if (o === undefined || !/^Created\b/i.test(o.content)) return undefined
  const content = pick(i, 'content')
  return content === '' ? undefined : countLineDiff(null, content)
}

function globSummary(_i: unknown, o: ToolOutput | undefined): string | undefined {
  const n = lineCount(o)
  return n === undefined ? undefined : presenterCopy('chat.tool.summary.files', { count: n })
}

function grepSummary(_i: unknown, o: ToolOutput | undefined): string | undefined {
  const n = lineCount(o)
  return n === undefined ? undefined : presenterCopy('chat.tool.summary.matches', { count: n })
}

/**
 * TodoWrite 的摘要从**入参**算,不从输出算。
 *
 * 理由:入参就是完整清单(工具契约明确要求「ALWAYS send the WHOLE list」),
 * 而输出只是一句确认。从入参算既准确又不依赖输出文案的措辞。
 */
function todoSummary(i: unknown): string | undefined {
  const todos = pickArray(i, 'todos')
  if (todos.length === 0) return undefined
  let done = 0
  for (const t of todos) {
    if (typeof t === 'object' && t !== null && (t as Record<string, unknown>).status === 'completed')
      done += 1
  }
  return `${String(done)}/${String(todos.length)}`
}

function webFetchSummary(_i: unknown, o: ToolOutput | undefined): string | undefined {
  if (o === undefined) return undefined
  const bytes = o.originalBytes ?? o.content.length
  return bytes >= 1024 ? `${(bytes / 1024).toFixed(1)}KB` : `${String(bytes)}B`
}

function webSearchSummary(_i: unknown, o: ToolOutput | undefined): string | undefined {
  const n = lineCount(o)
  return n === undefined ? undefined : presenterCopy('chat.tool.summary.results', { count: n })
}

/**
 * Bash:失败时优先显示退出码(`Command exited with code 127.`),
 * 成功时显示输出行数。
 *
 * 退出码是排查 shell 失败**唯一最有信息量的一个数字** —— 127 是命令不存在、
 * 130 是被中断、1 是通用失败,它们指向完全不同的下一步。把它放在折叠态右侧,
 * 用户不展开就能分辨。
 */
function bashSummary(_i: unknown, o: ToolOutput | undefined): string | undefined {
  if (o === undefined) return undefined
  const m = /exited with code (\d+)/i.exec(o.content)
  const code = m?.[1]
  if (code !== undefined) return presenterCopy('chat.tool.summary.exitCode', { code })
  if (o.content.startsWith('(command succeeded with no output)')) return presenterCopy('chat.tool.summary.noOutput')
  const n = lineCount(o)
  return n === undefined ? undefined : presenterCopy('chat.tool.summary.outputLines', { count: n })
}

/**
 * `BashOutput`:折叠态右侧显示那条后台 shell 的**状态**,不是行数。
 *
 * 一次回读最值得一眼看见的是「它还活着吗 / 退出码是几」—— 行数会随轮询忽多忽少,
 * 而状态正好是模型下一步要据以决定的那件事。输出首行形如
 * `Shell bash_1 (npm run dev) is still running.`
 */
function bashOutputSummary(_i: unknown, o: ToolOutput | undefined): string | undefined {
  if (o === undefined) return undefined
  if (/is still running/.test(o.content)) return presenterCopy('chat.tool.summary.running')
  if (/was killed/.test(o.content)) return presenterCopy('chat.tool.summary.stopped')
  const m = /exited with code (\d+)/i.exec(o.content)
  const code = m?.[1]
  return code === undefined ? undefined : presenterCopy('chat.tool.summary.exitCode', { code })
}

/** MCP / 未知工具:拿输出首行当摘要 —— 这是唯一通用且几乎总有意义的东西 */
function firstLineSummary(_i: unknown, o: ToolOutput | undefined): string | undefined {
  if (o === undefined) return undefined
  const first = o.content.split('\n', 1)[0] ?? ''
  const text = clip(first, 32)
  return text === '' ? undefined : text
}

/**
 * 定时任务的规则摘要,从**入参**算。
 *
 * ★ 折叠态右侧那一格是用户唯一不展开就能看见「到底定在什么时候」的地方 ——
 * 而「模型把任务定错了时间」正是这四个工具最需要被一眼看穿的失误。
 * 规则形状对不上(流式中途的半截 JSON)就返回 undefined,不猜。
 *
 * 星期以**数字串**传给文案表(`days: '135'`,0=周日…6=周六,与
 * `Date.prototype.getDay()` 同值域):「周一三五」还是「Mon, Wed, Fri」
 * 属于 UI 文案,由 i18n 按语言拼,这里只给事实。
 */
function scheduleSummary(i: unknown): string | undefined {
  if (typeof i !== 'object' || i === null) return undefined
  const raw = (i as Record<string, unknown>)['schedule']
  if (typeof raw !== 'object' || raw === null) return undefined
  const rule = raw as Record<string, unknown>
  const time = typeof rule['time'] === 'string' ? rule['time'] : ''
  if (rule['kind'] === 'once') {
    const at = typeof rule['at'] === 'string' ? rule['at'] : ''
    return at === '' ? undefined : at.replace('T', ' ')
  }
  if (rule['kind'] === 'daily') return time === '' ? undefined : presenterCopy('chat.tool.summary.scheduleDaily', { time })
  if (rule['kind'] === 'weekly') {
    const days = Array.isArray(rule['weekdays']) ? rule['weekdays'] : []
    const digits = days.filter((d): d is number => typeof d === 'number' && d >= 0 && d < 7).map((d) => String(d)).join('')
    if (digits === '' || time === '') return undefined
    return presenterCopy('chat.tool.summary.scheduleWeekly', { days: digits, time })
  }
  return undefined
}

/** `ListScheduledTasks` 的输出是一段 JSON,里面的 `count` 就是条数。 */
function scheduledListSummary(_i: unknown, o: ToolOutput | undefined): string | undefined {
  if (o === undefined) return undefined
  const m = /"count":\s*(\d+)/.exec(o.content)
  const n = m?.[1]
  return presenterCopy('chat.tool.summary.tasks', { count: n === undefined ? 0 : Number(n) })
}

/**
 * `AskUserQuestion` 的摘要是**题数**,而且从入参算。
 *
 * 需求:多道题的卡片折叠起来只有一行,不写清「一共几道」的话,用户看到下面那张
 * 待决卡只显示第一题,会以为就问了这一件事。输出那边是回答的 JSON,算不出题数。
 * 半截 JSON 里只长出了一道题时就报一道 —— 它说的是「此刻已经写出来几道」,不是预言。
 */
function askSummary(i: unknown): string | undefined {
  const questions = pickArray(i, 'questions')
  return questions.length === 0
    ? undefined
    : presenterCopy('chat.tool.summary.questions', { count: questions.length })
}

// ─────────────────────────── 注册表 ───────────────────────────

/**
 * 文件类工具的三段行:动作 + 文件名 + 目录。
 *
 * 需求:行里要能一眼分出「哪个文件」和「在哪儿」,所以文件名和目录是**两段**,
 * 不是拼好的一条路径 —— 拼成一条之后,渲染层只能把整条压暗或整条提亮,
 * 而用户扫的是文件名。`path` 原样带上,给渲染层画文件类型标记。
 *
 * ★ 路径还没流到(`path === ''`)时只留动作标签:半个路径比没有路径更误导。
 */
function fileLine(key: PresenterCopyKey, path: string): ToolLine {
  const label = presenterCopy(key)
  if (path === '') return { label }
  return { label, target: base(path), context: dirOf(path), path }
}

/**
 * 动作 + 一个领域值(命令、模式、id、查询词)。`mono` 决定它是不是等宽。
 */
function valueLine(key: PresenterCopyKey, target: string, mono = false): ToolLine {
  const label = presenterCopy(key)
  return target === '' ? { label } : { label, target, mono }
}

/**
 * 一条**浏览器动作行**:动作 + 主语 + 所属标签页。
 *
 * 需求:一个工作区里可以同时开着几个页面(`browser_tabs` 会列出来),而每一次
 * 点击/输入都是「对**哪一个**页面做的」—— 行里不写这一格的话,转录里连着出现的
 * 「点击 e12」指代不明,而同一时刻用户屏幕上可能开着三个 tab。
 *
 * ★ 主语一律 `mono`:ref(`e12`)、按键组合、坐标、选项值都是**标识**不是句子。
 * ★ 标签页落在 `context` 那一格(次要、可省)—— 和文件行里「目录」是同一个位置。
 */
function browserLine(key: PresenterCopyKey, target: string, tabId: string): ToolLine {
  return {
    ...valueLine(key, target, true),
    ...(tabId === '' ? {} : { context: tabId })
  }
}

/** 字符串数组字段的安全拼接 —— 按键组合(`["Control","Enter"]`)、选中的选项值。 */
function listOf(input: unknown, key: string, sep: string): string {
  return pickArray(input, key)
    .filter((v): v is string => typeof v === 'string' && v !== '')
    .join(sep)
}

function recordOf(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined
}

/**
 * `(x, y)` 坐标那一格。**两个数都到齐才给** —— 半个坐标比没有更误导(同 `fileLine`
 * 对半个路径的处理),而流式半截 JSON 里最常见的残留正是一个写了一半的数。
 */
function pointText(value: unknown): string {
  const source = recordOf(value)
  const x = source?.['x']
  const y = source?.['y']
  if (typeof x !== 'number' || typeof y !== 'number' || !Number.isFinite(x) || !Number.isFinite(y)) return ''
  return `(${String(Math.round(x))}, ${String(Math.round(y))})`
}

/**
 * 一条 shell 命令的「它到底在干什么」那一截。
 *
 * ★★ 需求:模型省掉 `description` 时,行里不能是
 * `cd /Users/token/Desktop/code/NextCoWork && grep -n "text-\[1…` ——
 * 前四十个字符全是路径,真正的动作被挤出了可视范围。所以:
 *
 *   1. 剥掉**前置的 `cd <路径> &&`**(可以连着好几段);它是每条命令的仪式,
 *      不是这一步做的事。剥完什么都不剩(命令真的只是 `cd`)就退回原文。
 *   2. 再截到 48 字 —— 剩下的部分在展开后的终端里,一个字符都不少。
 *
 * ★ 只剥**行首**的 `cd`,不碰管道后面的:`git log | cd` 这种写法不存在,
 * 但 `find … -exec cd …` 存在,剥错了会让一行显示成另一件事。
 */
export function commandGist(command: string): string {
  const flat = command.replace(/\s+/g, ' ').trim()
  if (flat === '') return ''
  let rest = flat
  // 允许带引号的路径:`cd "/a b" && …`
  const CD = /^cd\s+(?:"[^"]*"|'[^']*'|[^\s&|;]+)\s*&&\s*/
  while (CD.test(rest)) rest = rest.replace(CD, '')
  return clip(rest === '' ? flat : rest, 48)
}

/**
 * ★ 键是 `internalId`(`Read` / `Bash` / `web_search` …),与
 * `kernel/tool/builtin/index.ts` 的清单一一对应。
 * 新增内置工具必须在这里加一行,否则 `index.test.ts` 会失败。
 */
const REGISTRY: Record<string, ToolPresenter> = {
  Read: {
    shape: 'read',
    line: (i) => fileLine('chat.tool.title.read', pick(i, 'file_path')),
    summary: readSummary
  },
  LS: {
    shape: 'read',
    line: (i) => fileLine('chat.tool.title.ls', pick(i, 'path')),
    summary: lsSummary
  },
  Write: {
    shape: 'mutate',
    line: (i) => fileLine('chat.tool.title.write', pick(i, 'file_path')),
    summary: writeSummary,
    stats: writeStats
  },
  Edit: {
    shape: 'mutate',
    line: (i) => fileLine('chat.tool.title.edit', pick(i, 'file_path')),
    summary: editSummary,
    stats: (i) => editStats(i)
  },
  Glob: {
    shape: 'search',
    line: (i) => ({
      ...valueLine('chat.tool.title.glob', clip(pick(i, 'pattern'), 48), true),
      // 搜索范围是「在哪儿找」,和文件行的目录是同一格,所以走 context
      context: dirContext(pick(i, 'path'))
    }),
    summary: globSummary
  },
  Grep: {
    shape: 'search',
    line: (i) => ({
      ...valueLine('chat.tool.title.grep', clip(pick(i, 'pattern'), 48), true),
      context: dirContext(pick(i, 'path') || pick(i, 'glob'))
    }),
    summary: grepSummary
  },
  Bash: {
    shape: 'command',
    /**
     * 优先用模型自己写的 `description`(工具 schema 里要求「5-10 words」),
     * 它比命令本身更接近「这一步在干什么」。
     *
     * ★ 有 description 时**收起态完全不画命令**。命令动辄几百字符(ssh、管道、
     * 内嵌 PowerShell 都常见),在一行里只显示得下一截毫无意义的前缀
     * (`cd /Users/token/… && s…`),既读不懂又把描述挤没了。
     * 命令完整地在展开后的终端里(`views/chat/TerminalBlock.tsx`)——
     * 那里才有它需要的宽度和换行。
     *
     * ★★ **`description` 是可省参数,所以兜底必须也说人话。** 省掉它的时候,
     * 原样显示命令的结果就是一行 `cd /Users/token/Desktop/code/NextCoWork && grep -n "…`
     * —— 前 40 个字符全是与「它干了什么」无关的路径。所以兜底走 `commandGist`:
     * 先剥掉 `cd … &&` 这类前置,再截短。完整命令仍在展开的终端里。
     */
    line: (i) => {
      const desc = pick(i, 'description')
      if (desc !== '') return { label: presenterCopy('chat.tool.title.bash'), target: clip(desc, 60) }
      return valueLine('chat.tool.title.bash', commandGist(pick(i, 'command')), true)
    },
    summary: bashSummary,
    stoppable: true
  },
  BashOutput: {
    shape: 'command',
    line: (i) => valueLine('chat.tool.title.bashOutput', pick(i, 'bash_id'), true),
    summary: bashOutputSummary
  },
  KillShell: {
    shape: 'command',
    line: (i) => valueLine('chat.tool.title.killShell', pick(i, 'shell_id'), true)
  },
  WebFetch: {
    shape: 'network',
    line: (i) => valueLine('chat.tool.title.webFetch', hostOf(pick(i, 'url'))),
    summary: webFetchSummary
  },
  web_search: {
    shape: 'network',
    line: (i) => valueLine('chat.tool.title.webSearch', clip(pick(i, 'query'), 48)),
    summary: webSearchSummary
  },
  TodoWrite: {
    shape: 'orchestration',
    line: () => ({ label: presenterCopy('chat.tool.title.todo') }),
    summary: (i) => todoSummary(i)
  },
  Task: {
    shape: 'orchestration',
    line: (i) => {
      const desc = pick(i, 'description')
      const type = pick(i, 'subagent_type')
      if (desc === '') return valueLine('chat.tool.title.task', type)
      return { label: presenterCopy('chat.tool.title.task'), target: clip(desc, 40), context: type }
    },
    summary: (i) => {
      const t = pick(i, 'subagent_type')
      return t === '' ? undefined : t
    }
  },
  Workflow: {
    shape: 'workflow',
    line: (i) => valueLine('chat.tool.title.workflow', clip(pick(i, 'title'), 40)),
    summary: (i) => {
      const nodes = (i as { nodes?: unknown } | null)?.nodes
      return Array.isArray(nodes) ? presenterCopy('chat.tool.summary.nodes', { count: nodes.length }) : undefined
    }
  },
  Skill: {
    shape: 'orchestration',
    line: (i) => valueLine('chat.tool.title.skill', pick(i, 'name'))
  },
  ListScheduledTasks: {
    shape: 'orchestration',
    line: () => ({ label: presenterCopy('chat.tool.title.scheduleList') }),
    summary: scheduledListSummary
  },
  CreateScheduledTask: {
    shape: 'orchestration',
    line: (i) => valueLine('chat.tool.title.scheduleCreate', clip(pick(i, 'name'), 32)),
    summary: (i) => scheduleSummary(i)
  },
  UpdateScheduledTask: {
    shape: 'orchestration',
    /** 改名时显示新名字,只改时间时退回 id —— 两种都比只显示动词有用 */
    line: (i) => {
      const name = pick(i, 'name')
      return name === ''
        ? valueLine('chat.tool.title.scheduleUpdate', clip(pick(i, 'task_id'), 18), true)
        : valueLine('chat.tool.title.scheduleUpdate', clip(name, 32))
    },
    summary: (i) => scheduleSummary(i)
  },
  DeleteScheduledTask: {
    shape: 'orchestration',
    line: (i) => valueLine('chat.tool.title.scheduleDelete', clip(pick(i, 'task_id'), 18), true)
  },
  /*
    等用户表态的三个。**标签静态、摘要克制**:入参是题面,它在详情区整块渲染
    (`views/chat/InteractionPreview.tsx`),标题行只负责说清这是哪一类表态。
  */
  AskUserQuestion: {
    shape: 'interaction',
    line: () => ({ label: presenterCopy('chat.tool.title.askUser') }),
    summary: (i) => askSummary(i)
  },
  ProposeGoal: {
    shape: 'interaction',
    line: () => ({ label: presenterCopy('chat.tool.title.proposeGoal') })
  },
  /*
    ★ `ExitPlanMode` 的 schema 是**空对象** —— 计划正文在文件里,工具开跑之后才读。
    所以它没有任何可以提前预览的入参,这里只换一个说人话的标签;
    `previewOf()` 对它返回 null,卡片也就不会自动展开一个空详情区。
  */
  ExitPlanMode: {
    shape: 'interaction',
    line: () => ({ label: presenterCopy('chat.tool.title.planReview') })
  },
  echo: {
    shape: 'external',
    line: () => ({ label: 'echo' }),
    summary: firstLineSummary
  },
  /*
    可视化那一对。

    `visualize_show_widget` 的主语取模型写的 `title`(规范要求它是
    `q4_revenue_by_product_line` 这种能自解释的标识),`humanize` 把下划线换成空格
    —— 它不翻译,是领域值。**没有 `summary`**:折叠态右端那一格在同一行里,
    而这里唯一还能一眼看懂的数就是代码体积,它对用户没有意义。

    `visualize_read_me` 是静态标签 + "加载了哪几段"的摘要 ——
    规范正文本身有七万字,进不了折叠态那一格,摘要是这里唯一能给出的信息。
  */
  visualize_show_widget: {
    shape: 'widget',
    line: (i) => valueLine('chat.tool.title.widget', humanize(pick(i, 'title')))
  },
  /*
    改图与推数据。改图的产物是一张新 widget,形态同 show_widget;推数据没有自己的画面
    (数据由同名那张图读),所以是一行普通的 `external`。
  */
  visualize_edit_widget: {
    shape: 'widget',
    line: (i) => valueLine('chat.tool.title.widgetEdit', humanize(pick(i, 'new_title') || pick(i, 'title')))
  },
  visualize_update_widget: {
    shape: 'external',
    line: (i) => valueLine('chat.tool.title.widgetUpdate', humanize(pick(i, 'title')))
  },
  visualize_read_me: {
    shape: 'external',
    line: () => ({ label: presenterCopy('chat.tool.title.readMe') }),
    summary: (i) => {
      const modules = pickArray(i, 'modules').filter((m): m is string => typeof m === 'string')
      return modules.length === 0 ? undefined : modules.join(' · ')
    }
  },
  /*
    对话内生图。形态 `image`(见 `ToolShape` 那一档的说明:图片卡 + 专属加载动画);
    行 = 动作 + prompt 片段 —— prompt 是「要画什么」,是这一行唯一有信息量的主语。
    (原先是 `network`:它确实出网,但判据是「展开后用哪种渲染器」,不是功能领域,
    而 network 的渲染器画不出图。)
    ★ 不给 summary:张数与进度由卡片本身和进度 chip(`2/4`)说,再猜一个只会是噪声。
  */
  generate_video: {
    shape: 'video',
    line: (i) =>
      valueLine(
        // 提交/查询/取消是三种动作,行标签跟着 action 走(缺省 = 生成)
        pick(i, 'action') === 'status'
          ? 'chat.tool.title.checkVideo'
          : pick(i, 'action') === 'cancel'
            ? 'chat.tool.title.cancelVideo'
            : 'chat.tool.title.generateVideo',
        clip(pick(i, 'prompt'), 48)
      )
  },
  SaveVideo: {
    shape: 'mutate',
    line: (i) => fileLine('chat.tool.title.saveVideo', pick(i, 'file_path'))
  },
  generate_image: {
    shape: 'image',
    line: (i) =>
      valueLine(
        // 带了源图就是改图 —— 同一工具两种动作,行标签跟着入参走
        pick(i, 'image') === '' ? 'chat.tool.title.generateImage' : 'chat.tool.title.editImage',
        clip(pick(i, 'prompt'), 48)
      )
  },
  /*
    把对话里的图写进工作区。形态 `mutate`:它和 `Write` 一样是「改了哪个文件」,行的主语是
    目标文件(`file_path`),不是那串 `ncw://` 地址 —— 地址对人没有信息量。
    ★ 不给 stats:写的是二进制,没有「行」可数;硬报 `+0` 等于说这次什么都没写。
  */
  SaveImage: {
    shape: 'mutate',
    line: (i) => fileLine('chat.tool.title.saveImage', pick(i, 'file_path'))
  },
  /*
    浏览器那一族(16 个 `browser_*`)。整族此前**没有任何登记**,于是每一行都走
    兜底路径 `humanize(internalId)` —— 界面上一列 `browser click` / `browser
    screenshot`。这一组现在只做两件事:给出中文动作标签,以及**行的主语**。

    ★ 形态怎么分的(判据始终是「展开后用哪种渲染器」,不是功能领域):
      · 导航类(`open` / `navigate` / `claim` / `tabs` / `user_tabs` / `profiles` /
        `close`)→ `network`:它们的结果都是「一份关于页面的信息」,`NetworkDetail`
        给的正是 URL + 正文,和 WebFetch 落在同一档;
      · 动作类(`click` / `type` / `press` / `select` / `scroll` / 两个 `cua_*`)
        → `network` 同上(结果是页面状态的变化说明);
      · 页面快照(`snapshot`)→ `read`(结果是一棵可逐行读的树);
      · **截图(`screenshot`)→ 专属的 `screenshot` 形态**,它是这一族里唯一
        产出**图**的:原先它落进 `external` 的通用 JSON 渲染器,而那里根本不读
        `output.images` —— 表现为模型看得见那张图、用户一个字也看不见。

    ★ 主语尽量取**人认得的东西**:导航取主机名(同 WebFetch),动作取 ref / 按键 /
    坐标,标签页落在 `context` 那一格。`tabId` 单独一格是因为一个工作区里可以同时
    开着几个页面,「点了哪一个」不写出来就指代不明。

    ★ **`browser_type` 刻意不回显输入的文字**:Agent 常被要求往表单里填账号密码,
    而转录是会被翻、会被导出的。行里只留 ref,完整入参仍在展开区(那是用户主动
    点开的,和「一屏行里随手可见」不是一回事)。
  */
  browser_open: {
    shape: 'network',
    line: (i) => valueLine('chat.tool.title.browserOpen', hostOf(pick(i, 'url')))
  },
  browser_navigate: {
    shape: 'network',
    line: (i) => valueLine('chat.tool.title.browserNavigate', hostOf(pick(i, 'url')))
  },
  browser_snapshot: {
    shape: 'read',
    line: (i) => browserLine('chat.tool.title.browserSnapshot', pick(i, 'tabId'), ''),
    // 快照的行数就是这棵树的规模 —— 一个几千行的快照值得在折叠态就看出「别展开」
    summary: readSummary
  },
  browser_screenshot: {
    shape: 'screenshot',
    line: (i) => browserLine('chat.tool.title.browserScreenshot', pick(i, 'tabId'), '')
  },
  browser_click: {
    shape: 'network',
    line: (i) => browserLine('chat.tool.title.browserClick', pick(i, 'ref'), pick(i, 'tabId'))
  },
  browser_type: {
    shape: 'network',
    line: (i) => browserLine('chat.tool.title.browserType', pick(i, 'ref'), pick(i, 'tabId'))
  },
  browser_press: {
    shape: 'network',
    line: (i) => browserLine('chat.tool.title.browserPress', listOf(i, 'keys', '+'), pick(i, 'tabId'))
  },
  browser_select: {
    shape: 'network',
    // 选中的值比 ref 有意义(用户要核对的就是「选了什么」),ref 留在展开区
    line: (i) => browserLine('chat.tool.title.browserSelect', clip(listOf(i, 'values', ' · '), 40), pick(i, 'tabId'))
  },
  browser_scroll: {
    shape: 'network',
    line: (i) => {
      const y = recordOf(i)?.['scrollY']
      const amount = typeof y === 'number' && Number.isFinite(y) ? `y=${String(Math.round(y))}` : ''
      return browserLine('chat.tool.title.browserScroll', amount, pick(i, 'tabId'))
    }
  },
  browser_cua_click: {
    shape: 'network',
    // 坐标还没流到时退回「只有标签」,不画半个 `(12, )`
    line: (i) => browserLine('chat.tool.title.browserCuaClick', pointText(recordOf(i)), pick(i, 'tabId'))
  },
  browser_cua_drag: {
    shape: 'network',
    line: (i) => {
      const path = pickArray(i, 'path')
      const from = pointText(recordOf(path[0]))
      const to = pointText(recordOf(path[path.length - 1]))
      // 两端都认得出来才画箭头:半个坐标连不成一条路径
      const points = from === '' || to === '' ? '' : `${from} → ${to}`
      return browserLine('chat.tool.title.browserCuaDrag', points, pick(i, 'tabId'))
    }
  },
  browser_tabs: {
    shape: 'network',
    line: () => ({ label: presenterCopy('chat.tool.title.browserTabs') }),
    summary: lsSummary
  },
  browser_user_tabs: {
    shape: 'network',
    line: () => ({ label: presenterCopy('chat.tool.title.browserUserTabs') }),
    summary: lsSummary
  },
  browser_claim: {
    shape: 'network',
    line: (i) => browserLine('chat.tool.title.browserClaim', pick(i, 'tabId'), '')
  },
  browser_profiles: {
    shape: 'network',
    line: () => ({ label: presenterCopy('chat.tool.title.browserProfiles') }),
    summary: lsSummary
  },
  browser_close: {
    shape: 'network',
    line: (i) => browserLine('chat.tool.title.browserClose', pick(i, 'tabId'), '')
  }
}

/**
 * 搜索范围那一格。传进来的可能是目录、也可能是 glob(`**\/*.ts`)——
 * 目录要按 `dirOf` 缩短,glob 原样留着(它本身就是要读的那个模式)。
 */
function dirContext(scope: string): string | undefined {
  if (scope === '') return undefined
  return scope.includes('*') ? clip(scope, 32) : `${scope.replace(/[/\\]+$/, '')}/`
}

/** 名字完全认不出来时的兜底。保持现状行为:通用 JSON 详情。 */
const FALLBACK: ToolPresenter = {
  shape: 'external',
  line: () => ({ label: presenterCopy('chat.tool.fallback') }),
  summary: firstLineSummary
}

// ─────────────── 结果区能画成任务清单的那几个工具 ───────────────

/**
 * 「这次调用的入参/结果其实是一份任务清单」的工具表 —— 与 `shape` **正交**。
 *
 * ★ 为什么不能靠 `shape` 判断:同一个 `orchestration` 形态下还有 `Task` / `Skill` /
 * 四个定时任务工具,它们的结果都不是清单;而真正决定「怎么画」的是**这次调用的
 * 数据里有没有清单**,那件事只有跑完才知道(`previewTodos` / `narrowTodos`)。
 * 所以这里是一张**数据表**,不是 `if (name === 'TodoWrite')` —— 下一个自带清单的
 * 工具(比如某个插件工具)只要往这里加一行,渲染层一行都不用改。
 *
 * 渲染层用它做两件事:`views/chat/todo-history.tsx` 决定「要不要找上一份清单来
 * 算增量」,`ToolDetail` 决定「结果块画成清单还是画成输出原文」。
 */
const TODO_LIST_PRESENTERS: Record<string, true> = {
  TodoWrite: true
}

/**
 * 这个工具的结果区是否该按任务清单渲染。
 *
 * ★ 查表键与 `presenterOf` 一样是 **externalName**(转录里存的那个):内置工具两者
 * 相同,撞名后带哈希后缀的名字会查不到 —— 那时退回通用渲染,而不是拿一张空清单
 * 去画一个「0/0 已完成」的假面板。
 */
export function isTodoListTool(name: string): boolean {
  return TODO_LIST_PRESENTERS[name] === true
}

// ─────────────── 产物不随过程折叠收起的那几档形态 ───────────────

/**
 * 「这一档的产物就是回答本身」的形态表 —— 工具卡默认展开,且**不进**过程折叠。
 *
 * 需求:生成的图片是用户要的东西,不是过程。原先生图卡片和 Read/Grep 一样住在过程段里,
 * 于是 (1) 连续几个调用都成功后整组自动收起(`isCompletedToolGroup`);(2) 一轮收尾后
 * 整段过程折进一行「用时」(`decideWorkspace`)—— 表现为模型说「画好了」,图却被
 * 藏在两层折叠底下。渲染层据此把这类调用提成独立块(`thread-content.ts` 的
 * `assistantSegments`,以及 `Thread.tsx` 里和 goal_status 同一处的提出逻辑)。
 *
 * ★ `Record<ToolShape, boolean>` 而不是 `Set`:新增一种形态时编译期就要求在这里表态,
 * 忘了写的代价是「新产物类工具的结果被折叠吞掉」,而那不会有任何报错。
 * ★ `widget` 暂为 false:可视化卡片目前按原行为留在过程段里(它的默认展开由
 * `parts.tsx` 自己判),要不要一并提出来是另一个产品决定。
 * ★ `screenshot` 为 true,理由与 `image` 同一句但更强一层:截图**没有正文**
 * —— 图就是这次调用的全部结果,折进过程段等于这次调用在界面上只剩下一个标题。
 */
const PINNED_SHAPES: Record<ToolShape, boolean> = {
  reasoning: false,
  read: false,
  mutate: false,
  search: false,
  command: false,
  network: false,
  orchestration: false,
  interaction: false,
  widget: false,
  image: true,
  video: true,
  screenshot: true,
  workflow: true,
  external: false
}

export function isPinnedShape(shape: ToolShape): boolean {
  return PINNED_SHAPES[shape]
}

// ─────────────────────────── 插件贡献的 presenter(注入层) ───────────────────────────
//
// ★ REGISTRY 之上唯一的可变层。插件的 presenter **算不出来**(依赖清单里的 shape/card
// 模板 + i18n),所以由渲染层在 catalog 加载时**构建好闭包再注入**——把 t()/locale 留在
// 渲染层,这个 shared 模块仍然不碰 store。注入是**全量替换**(与 plugins store 的替换式
// 一致):每次拿整份 catalog 重建,避免残留已卸载插件的条目。
//
// ★ 带 version + 订阅:`presenterOf` 是命令式查表,注入这张 Map 不会触发 React 重渲;
// 已提交(静态)的工具卡片若 presenter 迟到,不订阅就会永久停在兜底标题。消费方
// (parts.tsx)用 `useSyncExternalStore` 订阅 `subscribePluginPresenters` 拿到重渲。
const pluginPresenters = new Map<string, ToolPresenter>()
let pluginPresentersVersion = 0
const pluginPresenterListeners = new Set<() => void>()

/** 全量替换插件 presenter 表。键是 externalName(转录里存的那个)。 */
export function registerPluginPresenters(entries: ReadonlyArray<readonly [string, ToolPresenter]>): void {
  pluginPresenters.clear()
  for (const [externalName, presenter] of entries) pluginPresenters.set(externalName, presenter)
  pluginPresentersVersion += 1
  for (const listener of pluginPresenterListeners) listener()
}

/** 主要给测试用:清空注入层,`beforeEach` 复位,避免用例间串状态。 */
export function clearPluginPresenters(): void {
  if (pluginPresenters.size === 0) return
  pluginPresenters.clear()
  pluginPresentersVersion += 1
  for (const listener of pluginPresenterListeners) listener()
}

/** `useSyncExternalStore` 的快照。 */
export function pluginPresentersSnapshot(): number {
  return pluginPresentersVersion
}

/** 订阅注入层变更 —— 见上方注释里「静态卡片 presenter 迟到」那段。 */
export function subscribePluginPresenters(listener: () => void): () => void {
  pluginPresenterListeners.add(listener)
  return () => pluginPresenterListeners.delete(listener)
}

/**
 * 四级查找:内置注册表 → MCP 拆名 → 插件注入 → 可读名兜底。
 *
 * 每一级都会返回一个可用的 presenter,**永远不返回 undefined** ——
 * 调用方不需要写空判断,这是让 `parts.tsx` 里那段渲染保持平铺直叙的前提。
 */
export function presenterOf(name: string): ToolPresenter {
  const hit = REGISTRY[name]
  if (hit !== undefined) return hit

  const mcp = parseMcpId(name)
  if (mcp !== null) {
    return {
      shape: 'external',
      // server 是「谁提供的」,工具名才是这次做的事 —— 正好落进 label/target 两格,
      // 原先那个 `server · tool` 的拼接串在行里只能整条压暗。
      line: () => ({ label: mcp.server, target: clip(humanize(mcp.tool), 32) }),
      summary: firstLineSummary
    }
  }

  // 插件贡献的 presenter。放在 MCP 之后、humanize 兜底之前:插件工具的
  // externalName 不匹配 `mcp__` 拆名规则,会一路落到这里;注入了就用注入的,
  // 没注入(catalog 还没加载 / 这个工具没声明卡片)则继续走可读名兜底。
  const injected = pluginPresenters.get(name)
  if (injected !== undefined) return injected

  // 认不出来但名字本身可读时,显示名字比显示「工具调用」有用得多。
  // Skill 提供的工具、以及将来任何新来源都会落在这里。
  //
  // ★ **必须回填 FALLBACK**:`humanize` 会把下划线全换成空格,
  // 于是一个叫 `____` 的工具(消毒后的中文名就长这样,见 naming.ts 的
  // sanitizeToolName)会得到一个空标签 —— 界面上是一行只有图标的空白,
  // 看着像渲染坏了。单测里那条「永不返回空标签」钉的就是这里。
  const readable = clip(humanize(name), 32)
  if (readable !== '') {
    return {
      shape: 'external',
      line: () => ({ label: readable }),
      summary: firstLineSummary
    }
  }
  return FALLBACK
}

/** 测试与「新增工具忘了登记」检查用:注册表已知的全部 internalId。 */
export function registeredToolIds(): readonly string[] {
  return Object.keys(REGISTRY)
}

/** 某个 internalId 是否已在注册表里(MCP 与未知名不算)。 */
export function isRegisteredTool(internalId: string): boolean {
  return internalId in REGISTRY
}
