/**
 * `nextcowork/view` —— 插件**视图侧**的运行时(跑在视图 iframe 里)。
 *
 * ## 和 `nextcowork` 不是一回事
 *
 * `nextcowork` 是**插件逻辑**(`main` 指向的那个模块)用的,跑在一个隐藏的宿主
 * 页面里,有 preload、有权限链、能读写文件跑命令。
 *
 * 这一套是**视图**用的。视图 iframe 没有 preload,和宿主之间只有一条 postMessage
 * 通道 —— 它能做的事只有:拿到自己绑定的那个文件、存回去、报告脏状态、跟随主题。
 * 想做别的(读别的文件、跑命令、联网),让插件逻辑去做,视图通过你自己的
 * 消息约定跟它说话。
 *
 * ## 模块从哪来
 *
 * 宿主经 import map 下发,**不要装进 devDependencies**,也不要打进你的 bundle:
 * 打进去就是第二份实例,而它和宿主之间那条通道不会工作。
 * 打包时把这几个名字标成 external:
 *
 * ```js
 * external: ['react', 'react-dom', 'react-dom/client', 'react/jsx-runtime', 'nextcowork/ui', 'nextcowork/view']
 * ```
 */
declare module 'nextcowork/view' {
  import type { ReactNode } from 'react'

  /** 宿主送过来的那份文档。 */
  export interface PluginDocument {
    /** 工作区相对路径。**只读** —— 保存时不需要、也不可以带上它。 */
    path: string
    /** 文本文件是正文;图片是 `data:` URL(可直接喂 `<img>` / canvas)。 */
    data: string
    /** 只在图片分支有。 */
    mime?: string
  }

  /**
   * 订阅这个视图绑定的文档,返回退订函数。
   *
   * ★ 调用它本身就会向宿主要一次文档,所以**只调一次**;
   * 在 React 里放进 `useEffect`,并且把返回值放进 cleanup。
   */
  export function onDocument(handler: (doc: PluginDocument) => void): () => void

  /**
   * 把内容存回这个视图绑定的文件。
   *
   * 失败最常见的原因是**别人在你编辑期间改了同一个文件**(宿主用 revision 挡的)。
   * 这种时候正确的做法是提示用户,不是重试 —— 重试会把对方的改动盖掉。
   *
   * `encoding: 'base64'` 是图片支线;宿主只允许用它覆写已分类为 image 的文件。
   */
  export function saveDocument(data: string, options?: { encoding?: 'base64' }): Promise<void>

  /**
   * 报告「我有 / 没有没存的改动」。
   *
   * ★ 宿主的「关 Tab 之前问一句」**全靠它**。不报的话,用户没存的改动会在关
   * Tab 那一刻静默消失 —— 而内置文档是会挽留的,两者行为不一致更难被发现。
   */
  export function setDirty(dirty: boolean): void

  export interface PluginTheme {
    appearance: 'light' | 'dark'
    motion: 'standard' | 'soft' | 'reduced' | 'off'
  }

  /**
   * 跟随宿主的深浅色 / 动效档位。
   *
   * ★ 大多数情况**用不到** —— `nextcowork/ui` 的控件和下发的那份 CSS 已经自己
   * 跟着走了。只有你自己画 canvas、或者用 Motion 写动画时才需要读它
   * (CSS 的 `prefers-reduced-motion` 管不住 WAAPI)。
   */
  export function useTheme(): PluginTheme

  /**
   * 挂载视图的根组件。
   *
   * ★ 不只是省三行:它保证根节点**在 document 里**。宿主注入的主题变量写在
   * `<html>` 上,挂在一个游离容器上的 React 树继承不到那些变量 ——
   * 症状是控件全无颜色,且零报错。
   *
   * 默认开 `StrictMode`:视图最常见的 bug 是 effect 里登记了监听却没退订,
   * 双次挂载当场就能让它暴露。
   */
  export function mount(node: ReactNode, options?: { strict?: boolean; container?: HTMLElement }): void

  // ─────────── 文档引擎画布(编辑器在清单里声明了 `documentEngine` 时用) ───────────

  /** 文档坐标里的矩形。单位 twips(1/1440 英寸) */
  export interface DocumentRect { x: number; y: number; width: number; height: number; part?: number }

  /** 会话状态。`dirty` = 有没保存的改动(画布、Agent 的都算) */
  export interface EngineDocumentState {
    status: 'loading' | 'ready' | 'saving' | 'conflict' | 'crashed' | 'recovering' | 'closed'
    generation: number
    modelRevision: number
    savedRevision: number
    dirty: boolean
    seq: number
  }

  export interface EngineDocumentOpened {
    /** 工作区相对路径,只用于展示 */
    path: string
    state: EngineDocumentState
    /** 引擎能力。`interaction` 缺省 = 只读预览,不要画可编辑的光标 */
    capabilities: {
      format: string
      engineVersion: string
      canSave: boolean
      canExport: string[]
      /** `visibleArea` = 引擎收 `viewport` 事件(旧版引擎没有;没有时别发,会整批被拒) */
      interaction?: { keyboard: boolean; mouse: boolean; textInput: boolean; visibleArea?: boolean }
      /** 这份文档可用的功能区命令 id。只画这里有的按钮 */
      commands?: EngineCommandId[]
    }
  }

  /**
   * 功能区命令 id。参数:`format.fontName` / `style.paragraph` 要 `{ name }`,`format.fontSize` 要 `{ size }`(磅),
   * `format.color` / `format.highlight` 要 `{ color }`(0xRRGGBB,-1 = 自动),`insert.table` 要 `{ rows, columns }`。
   * 状态值除了 "true" / "false" / 当前值,还可能是 "enabled" / "disabled"(撤销、重做、幻灯片管理此刻能不能用)。
   */
  export type EngineCommandId =
    | 'edit.undo' | 'edit.redo' | 'edit.selectAll'
    | 'format.bold' | 'format.italic' | 'format.underline' | 'format.strikethrough' | 'format.superscript' | 'format.subscript' | 'format.clear'
    | 'format.fontName' | 'format.fontSize' | 'format.color' | 'format.highlight'
    | 'paragraph.alignLeft' | 'paragraph.alignCenter' | 'paragraph.alignRight' | 'paragraph.justify' | 'paragraph.indent' | 'paragraph.outdent'
    | 'list.bullets' | 'list.numbering'
    | 'style.paragraph'
    | 'insert.pageBreak' | 'insert.table'
    | 'cells.merge' | 'cells.wrap' | 'cells.formatCurrency' | 'cells.formatPercent'
    /** 公式栏:`{ text }` 写进当前单元格(以 = 开头即公式;空串清空) */
    | 'cells.enter'
    /** 名称框:`{ ref }` 跳到单元格 / 区域(A1、$B$3:C10) */
    | 'cells.goto'
    /** 演示:在当前幻灯片中央插入文本框(随即进入文字编辑)/ 矩形 / 椭圆 */
    | 'insert.textBox' | 'insert.rectangle' | 'insert.ellipse'
    /** 演示:在当前幻灯片之后新建 / 复制一张(并切到它)、删除 / 上移 / 下移当前这张 */
    | 'slides.new' | 'slides.duplicate' | 'slides.delete' | 'slides.moveUp' | 'slides.moveDown'
    /** 演示:`{ layout }` 给当前幻灯片换版式:0 标题页、1 标题 + 内容、3 两栏内容、19 仅标题、20 空白、32 居中文字 */
    | 'slides.layout'

  /** 一块渲染好的 tile。`pixels` 是非预乘 RGBA,可直接 `new ImageData(pixels, width, height)` */
  export interface EngineTile {
    width: number
    height: number
    generation: number
    modelRevision: number
    /** 底层是普通 ArrayBuffer(ImageData 只收这种) */
    pixels: Uint8ClampedArray<ArrayBuffer>
  }

  /** 交给引擎的输入。坐标是 twips,键码用 `lokKeyOf` 从 KeyboardEvent 换算 */
  export type EngineInputEvent =
    | { type: 'key'; action: 'press' | 'release'; charCode?: number; keyCode?: number }
    | { type: 'mouse'; action: 'down' | 'up' | 'move'; x: number; y: number; count?: number; buttons?: number; modifier?: number }
    /** compose = 输入法组字中;commit = 提交;空串组字 = 取消。组字进行中只能发 text 事件 */
    | { type: 'text'; action: 'compose' | 'commit'; text: string }
    /** 切换工作表 / 幻灯片 */
    | { type: 'part'; part: number }
    /** 客户端可见区域(twips):引擎按它翻页。`DocumentCanvas` 已经在发,自己画画布时才需要 */
    | { type: 'viewport'; x: number; y: number; width: number; height: number }

  export interface EngineInputResult {
    /** 这批输入改没改文档 */
    modified: boolean
    /** 这批之后是否仍在组字(组字期间 Agent 的读写会被挡下) */
    composing?: boolean
    /** 要重画的区域(twips);`all` = 整体重画 */
    invalidations: { all: boolean; rects: DocumentRect[] }
    /** 只在变过时出现;`null` = 现在没有 */
    cursor?: DocumentRect | null
    cursorVisible?: boolean
    selection?: DocumentRect[]
    cellCursor?: DocumentRect | null
    documentSizeChanged?: boolean
    /** 用户当前的工作表 / 幻灯片(变过时出现) */
    part?: number
    /** 工作表 / 幻灯片数(变过时出现):缩略图栏、工作表标签据此重取 */
    parts?: number
    /** 变过的命令状态:命令 id → 值("true" / "false" / 字体名 / 字号)。画布打开后第一次拉取带全量 */
    states?: Partial<Record<EngineCommandId, string>>
    /** 表格:当前单元格的公式原文(公式栏)与地址(名称框)。只在变过时出现 */
    cellFormula?: string
    cellAddress?: string
    /** 表格:行高列宽变了,行列头要重取 */
    headersChanged?: boolean
    generation: number
    modelRevision: number
  }

  /**
   * 带码的失败。常见的码:
   * - `busy`:用户正在输入法组字,稍后原样重试;
   * - `stale_generation`:引擎重启过,重新取版面再算坐标;
   * - `session_closed`:文档已关(Tab 关了、账户切了);
   * - `result_unknown`:引擎没能确认输入是否生效,先 `state()` 再决定。
   */
  export class EngineDocumentError extends Error {
    readonly code: string
  }

  export interface EngineDocument {
    /** 打开完成时 resolve;打不开时 reject(`EngineDocumentError`) */
    ready: Promise<EngineDocumentOpened>
    /** 按区域要像素。区域用 twips,像素尺寸由你按缩放与 DPR 决定(见 `tileRequest`) */
    render(request: { x: number; y: number; tileWidth: number; tileHeight: number; width: number; height: number; part?: number }): Promise<EngineTile>
    /** `generation` 是你算坐标时用的那一代版面 */
    input(generation: number, events: EngineInputEvent[]): Promise<EngineInputResult>
    /** 功能区命令,在用户光标 / 选区处执行。表外的命令一律被拒(没有保存、没有宏:保存用 `save()`) */
    command(generation: number, command: EngineCommandId, args?: Record<string, string | number>): Promise<EngineInputResult>
    /** 功能区下拉框的数据:引擎里的字体族名 / 文档的段落样式名 */
    list(kind: 'fonts' | 'styles' | 'parts'): Promise<string[]>
    /** 表格当前工作表的行列头:可见区域(twips)里每行 / 列的结束位置(twips)与标签,第一项是区域起点 */
    headers(area: { x: number; y: number; width: number; height: number }): Promise<{ rows: [number, string][]; columns: [number, string][] }>
    /** 每一次 input / command 的回执(谁发起的都算)。功能区据此更新按钮的按下状态 */
    onResult(handler: (result: EngineInputResult) => void): () => void
    /** 版面(twips):文档尺寸,Writer 另给每页矩形 */
    layout(part?: number): Promise<{ generation: number; modelRevision: number; layout: unknown }>
    state(): Promise<EngineDocumentState>
    save(): Promise<EngineDocumentState>
    /** 会话状态变了(Agent 改了、保存了、崩了)。收到后用 `input(generation, [])` 取回要重画的区域 */
    onChange(handler: (state: EngineDocumentState) => void): () => void
    dispose(): void
  }

  /**
   * 打开这个视图绑定的文档(文件由宿主从 Tab 决定,视图不能也不需要指定)。
   *
   * ★ **只调一次**,放进 useEffect,cleanup 里 `dispose()`:每调一次宿主就重开一次会话视图。
   */
  export function openEngineDocument(): EngineDocument

  /**
   * KeyboardEvent → 引擎键码。返回 null = 不要交给引擎(组字中、单独修饰键、系统键)。
   * `platform` 传 `'mac'` 时 Cmd 是主修饰键。
   */
  export function lokKeyOf(
    event: { key: string; code: string; shiftKey: boolean; ctrlKey: boolean; altKey: boolean; metaKey: boolean; isComposing?: boolean },
    platform: 'mac' | 'other'
  ): { charCode: number; keyCode: number } | null

  /** tile 网格:缩放(1 = 100%)、设备像素比、每块 tile 的设备像素边长 */
  export interface TileGrid { zoom: number; dpr: number; tilePx: number }
  export function cssPxToTwips(px: number, zoom: number): number
  export function twipsToCssPx(twips: number, zoom: number): number
  export function twipsRectToCss(rect: DocumentRect, zoom: number): { left: number; top: number; width: number; height: number }
  /** 一块 tile 的渲染请求;相邻 tile 共用边界,不留缝 */
  export function tileRequest(tile: { col: number; row: number }, grid: TileGrid): { x: number; y: number; tileWidth: number; tileHeight: number; width: number; height: number }
  /** 与 twips 矩形相交的 tile(失效区域 → 要重画哪几块) */
  export function tilesCovering(rect: DocumentRect, grid: TileGrid, limit: { width: number; height: number }): { col: number; row: number }[]
  /**
   * 宿主提供的文档画布:按块渲染、只重画失效区域、画光标与选区、把键鼠和输入法交给引擎、
   * Agent 改了文档时自动重画。插件只需要在它周围画自己的 Ribbon / 侧栏。
   *
   * ★ 用 `onShortcut` 截走保存(Ctrl/Cmd+S)等要由插件处理的键,否则它们会被送进引擎。
   */
  export function DocumentCanvas(props: {
    doc: EngineDocument
    opened: EngineDocumentOpened
    /** 1 = 100% */
    zoom?: number
    /** 工作表 / 幻灯片;Writer 省略 */
    part?: number
    /** 无障碍名字(插件自己翻译好) */
    label: string
    className?: string
    onError?: (error: EngineDocumentError) => void
    onPartChange?: (part: number) => void
    /** 返回 true = 插件处理了这次按键,不送引擎 */
    onShortcut?: (event: KeyboardEvent) => boolean
    /** 控制把手:点完功能区按钮后调 `focus()` 把焦点还给画布 */
    controller?: { current: { focus: () => void } | null }
    /** 可见区域变了(滚动、尺寸、缩放):`css` 是滚动后的 CSS px,`twips` 是同一块文档区域 */
    onViewport?: (viewport: {
      css: { left: number; top: number; width: number; height: number }
      twips: { x: number; y: number; width: number; height: number }
    }) => void
    /** 把文档居中放在留白里(演示的幻灯片)。缺省贴左上角 */
    centered?: boolean
  }): ReactNode

  /** 可见区域(CSS px,已含滚动)覆盖的 tile */
  export function tilesInView(view: { left: number; top: number; width: number; height: number }, grid: TileGrid, limit: { width: number; height: number }): { col: number; row: number }[]
}

/**
 * `nextcowork/ui` —— **宿主自己的那套控件**,给插件视图直接用。
 *
 * ## 为什么用它,而不是自己画
 *
 * 这里每一个组件都是宿主界面上正在用的**同一份实现**,不是仿制品。于是:
 * 尺寸、圆角、hover 态、焦点环、无障碍属性、动效档位全都自动跟宿主一致,
 * 而且宿主改版时你的视图跟着变 —— 自己照着截图画的那一份不会。
 *
 * 样式由宿主随视图 HTML 一并注入(`/__ui.css`),**你不需要引任何 CSS**。
 *
 * ## 边界
 *
 * 这些组件**只画界面**。要改文件、跑命令、读剪贴板,走 `nextcowork/view` 的
 * 文档通道或者你自己插件逻辑侧的 `nextcowork` API —— 这个模块没有给 iframe
 * 开任何新口子。
 *
 * ## 缺了什么
 *
 * Toast 不在这里:它要宿主窗口的那个单例 store,打进视图只会得到一个永远空的
 * 列表。要提示用户,在插件逻辑里调 `ncw.window.showMessage()`。
 */
declare module 'nextcowork/ui' {
  import type { ReactNode } from 'react'

  export type Tone = 'accent' | 'danger' | 'ghost'

  export function Button(props: {
    children: ReactNode
    onClick?: () => void
    /** 主动作 accent / 危险 danger / 中性 ghost(默认) */
    variant?: Tone
    size?: 'sm' | 'md'
    icon?: ReactNode
    disabled?: boolean
    className?: string
  }): ReactNode

  export function IconButton(props: {
    children: ReactNode
    /** 无障碍名字。图标按钮没有可见文字,**不给等于读屏用户听到一句"按钮"**。 */
    label: string
    onClick?: () => void
    size?: number
    active?: boolean
    /** 开关按钮的按下状态(读屏读「已按下」)。缺省 = 不是开关 */
    pressed?: boolean
    disabled?: boolean
    className?: string
  }): ReactNode

  export function TextInput(props: {
    value: string
    onChange: (value: string) => void
    placeholder?: string
    disabled?: boolean
    className?: string
  }): ReactNode

  export function TextArea(props: {
    value: string
    onChange: (value: string) => void
    placeholder?: string
    rows?: number
    className?: string
  }): ReactNode

  /**
   * 数字框。失焦 / 回车时才 `onCommit`(打字过程中不回调)。
   * (此前这里写的是 `onChange` / 可选的 min、max —— 与宿主的实现不符,照着写的插件拿不到回调。)
   */
  export function NumberInput(props: {
    value: number
    onCommit: (value: number) => void
    min: number
    max: number
    width?: number
    ariaLabel: string
    disabled?: boolean
  }): ReactNode

  export function Toggle(props: { checked: boolean; onChange: (next: boolean) => void; disabled?: boolean }): ReactNode

  export function Slider(props: {
    value: number
    onChange: (value: number) => void
    min?: number
    max?: number
    step?: number
    className?: string
  }): ReactNode

  export function Segmented<T extends string>(props: {
    value: T
    options: { value: T; label: ReactNode }[]
    onChange: (value: T) => void
    className?: string
  }): ReactNode

  export type SelectOption = { value: string; label: string }
  /**
   * 下拉选择。(此前这里写的是 `onChange` 且缺 `ariaLabel` —— 与宿主的实现不符,
   * 照着写的插件选了也没有回调。)
   */
  export function Select(props: {
    value: string
    options: readonly SelectOption[]
    onValueChange: (value: string) => void
    ariaLabel: string
    className?: string
    disabled?: boolean
    /** 放在 `Dialog` 里时必须打开 */
    inModal?: boolean
  }): ReactNode

  /**
   * 模态对话框。
   *
   * ★ 焦点陷阱与 Esc 关闭已经内建 —— 自己实现的模态十有八九会漏掉 Tab 键
   * 跑到背后那层去,而那是键盘用户唯一的出路。
   */
  export function Dialog(props: {
    title: string
    description?: string
    open: boolean
    onClose: () => void
    footer?: ReactNode
    width?: number
    children: ReactNode
  }): ReactNode

  export function EmptyState(props: { icon?: ReactNode; title: string; hint?: string; action?: ReactNode }): ReactNode
  export function ProgressBar(props: { value: number; className?: string }): ReactNode
  export function Spinner(props: { size?: number; className?: string }): ReactNode
  export function Tooltip(props: { label: string; children: ReactNode }): ReactNode

  /**
   * 菜单:`trigger` 是触发按钮的内容(按钮由 Menu 渲染),`children` 是渲染函数,拿到 `close`。
   * (此前这里写的是 open / onClose / anchor 的受控形状 —— 与宿主的实现不符。)
   */
  export function Menu(props: {
    trigger: ReactNode
    children: (close: () => void) => ReactNode
    label: string
    align?: 'start' | 'end'
    width?: number
    className?: string
    triggerClassName?: string
    disabled?: boolean
  }): ReactNode
  export function MenuItem(props: {
    children: ReactNode
    onSelect?: () => void
    icon?: ReactNode
    checked?: boolean
    danger?: boolean
    disabled?: boolean
  }): ReactNode
  export function MenuLabel(props: { children: ReactNode }): ReactNode
  export function MenuSeparator(): ReactNode

  /**
   * 条件类名合并(twMerge:后写的赢)。
   *
   * ★ 覆盖上面组件的样式时**用它**,别用字符串拼接:拼出来的 class 上会同时
   * 存在 `px-3` 和 `px-2`,谁赢取决于 CSS 生成顺序 ——
   * 症状是同样的代码在两次构建后表现不同。
   */
  export function cn(...parts: unknown[]): string

  export type MotionLevel = 'standard' | 'soft' | 'reduced' | 'off'
  /** 当前动效档位。自己用 Motion 写动画时必须读它。 */
  export function useMotionLevel(): MotionLevel
  /** 按档位缩放一个时长/位移。`off` 档返回 0。 */
  export function motionScale(level: MotionLevel, value: number): number
}
