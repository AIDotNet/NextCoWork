/**
 * `nextcowork` —— NextCoWork 插件 API。
 *
 * 作者侧的用法与 VS Code 完全一致:
 *
 * ```ts
 * import * as ncw from 'nextcowork'
 *
 * export function activate(context: ncw.ExtensionContext): void { }
 * export function deactivate(): void { }
 * ```
 *
 * 打包时把 `nextcowork` 标成 **external**(和 VS Code 的 `vscode` 一样)。
 * 运行期由宿主经 import map 注入实现,而那份实现的**身份是写死的** ——
 * `pluginId` 由协议层决定,不是一个参数,插件伪造不了。
 *
 * ## 这份声明里没有的东西,运行期也不会有
 *
 * 这不是一份「文档」,它是**能力上界的一部分**:没在这里出现的 API,
 * 在运行期会被白名单挡在第一道门外(错误码 `unknown_method`)。
 * 所以一个在编译期通过的插件,不会在运行期遇到「这个方法不存在」。
 *
 * 反过来也成立:**清单里没声明的能力,类型对了也会被拒**(第二道门)。
 * 类型系统管不了「用户批没批」,那是 `permissions` 命名空间的事。
 *
 * ## 插件的**界面**在另一份声明里
 *
 * `nextcowork/ui`(宿主控件)和 `nextcowork/view`(视图侧运行时)在
 * `nextcowork-view.d.ts`。下面这行把它一并带进来 —— 分成两个 `declare module`
 * 是因为它们跑在**两个不同的执行环境**里:这一份在插件宿主页(有 preload、
 * 有权限链),那一份在视图 iframe(只有一条 postMessage 通道)。
 * 混在一个模块名下的话,作者会在视图里 `import * as ncw from 'nextcowork'`
 * 然后对着一个恒为 undefined 的 `ncw.workspace` 排查半天。
 */
/// <reference path="./nextcowork-view.d.ts" />

declare module 'nextcowork' {
  /**
   * 这份垫片的版本。**与清单里的 `engines.nextcowork` 不是一回事**,
   * 但两者都指向同一个东西的两半:前者是运行期垫片,后者是宿主声明它实现了
   * 哪一版**插件 API**(当前 `0.3.3`,见 `shared/plugin/api-version.ts`)。
   *
   * ★ `engines.nextcowork` 比的**不是应用版本**。这两个曾被当成同一个,结果是
   * 宿主拿 `app.getVersion()`(2.x)去比 `^0.2.0`,按官方模板写的插件装上一律
   * 「装载失败」。写 `^0.3.0`。
   */
  export const version: string

  /** 本插件的 `publisher.name`。**由宿主写死**,改它不影响任何一次鉴权。 */
  export const extensionId: string

  export class Disposable {
    constructor(callOnDispose: () => void)
    static from(...items: { dispose(): unknown }[]): Disposable
    dispose(): void
  }

  export interface ExtensionContext {
    readonly extensionId: string
    /**
     * 注册进来的东西会在 `deactivate` 时被统一 dispose。
     *
     * ★ 不放进来的 Disposable **宿主管不着**:插件休眠(空闲 5 分钟)之后
     * 那些监听器还挂着,而下一次激活会再注册一遍。
     */
    readonly subscriptions: { dispose(): unknown }[]
  }

  /**
   * 需求：Office 插件通过受控会话而非整文件覆盖修改文档。仅插件宿主页可调用，
   * 不是 iframe 的预览画布 API；需要 >=0.3.2。后台工具必须逐次携带 invoke 的 callId。
   */
  export namespace documents {
    export type Format = 'docx' | 'docm' | 'xlsx' | 'xlsm' | 'pptx' | 'pptm' | 'pdf'
    export interface Scope { callId?: string }
    export interface SessionRequest extends Scope { sessionId: string }
    export interface Snapshot {
      sessionId: string
      format: Format
      status: 'loading' | 'ready' | 'saving' | 'conflict' | 'crashed' | 'recovering' | 'closed'
      generation: number
      modelRevision: number
      savedRevision: number
      diskRevision: string
      seq: number
    }
    export interface Target { generation: number; ref: string }
    export type Operation =
      | { kind: 'text.replace'; target: Target; text: string }
      | { kind: 'text.insert'; target: Target; position: 'before' | 'after' | 'start' | 'end'; text: string }
      | { kind: 'style.apply'; target: Target; style: string }
      | { kind: 'cells.set'; sheet: string; range: string; values: (string | number | boolean | null)[][] }
      | { kind: 'cells.formula'; sheet: string; cell: string; formula: string }
      | { kind: 'sheet.insert'; name: string; index?: number }
      | { kind: 'slide.insert'; index: number; layout?: string }
      | { kind: 'slide.move'; from: number; to: number }
      | { kind: 'object.delete'; target: Target }
      | { kind: 'pdf.annotate'; page: number; rect: [number, number, number, number]; text: string }
      | { kind: 'pdf.formFill'; field: string; value: string }
      /** 按字面文字定位（Word）。expectedCount 与实际命中数不符时整批拒绝，文档不变 */
      | { kind: 'text.findReplace'; find: string; replace: string; matchCase?: boolean; expectedCount?: number }
      | { kind: 'paragraph.style'; find: string; style: string; matchCase?: boolean; expectedCount?: number }
      /** 锚点必须恰好命中一处 */
      | { kind: 'paragraph.insert'; anchor: string; position: 'before' | 'after'; text: string; matchCase?: boolean }
    export type Query =
      | { kind: 'outline' }
      | { kind: 'text'; maxChars?: number }
      | { kind: 'cells'; sheet: string; range: string; maxChars?: number }
      /** 页 / 工作表 / 幻灯片的尺寸与 Writer 页矩形，单位 twips */
      | { kind: 'layout'; part?: number }
    export interface Capabilities {
      format: Format
      engineVersion: string
      operations: Operation['kind'][]
      canSave: boolean
      canExport: Format[]
      canUndo: boolean
      macros: { list: boolean; run: boolean }
      /** 画布交互输入(键盘 / 鼠标 / 输入法)。缺省 = 引擎只能只读预览这份文档 */
      interaction?: { keyboard: boolean; mouse: boolean; textInput: boolean }
    }
    export interface ApplyResult {
      operationId: string
      appliedRevision: number
      dirty: boolean
      warnings: string[]
      undoable: boolean
      /** 与批次逐条对应；按文字定位的操作带命中数。引擎不回报时省略 */
      results?: { matches?: number }[]
    }
    /** 读权限；引擎只能来自自身清单或已声明依赖，不执行文档宏。 */
    export function open(params: Scope & { path: string; engine?: string }): Thenable<{ sessionId: string; viewId: string; path: string; snapshot: Snapshot; capabilities: Capabilities }>
    /** 写权限；成功表示活动模型已改变，不代表写盘。 */
    export function apply(params: SessionRequest & { generation: number; modelRevision: number; operationId: string; operations: Operation[] }): Thenable<ApplyResult>
    export function save(params: SessionRequest): Thenable<{ snapshot: Snapshot }>
    /** 写权限；默认不覆盖，禁止替代 documents.save 写回当前文件。 */
    export function exportDocument(params: SessionRequest & { path: string; format: Format; overwrite?: boolean }): Thenable<{ snapshot: Snapshot; path: string }>
    export function getState(params: SessionRequest): Thenable<Snapshot>
    /** 读权限；读的是活动模型（含未保存修改），结果有字符上限 */
    export function query(params: SessionRequest & { request: Query }): Thenable<{ generation: number; modelRevision: number; result: unknown }>
    export function getOperation(params: SessionRequest & { operationId: string }): Thenable<
      | { status: 'missing' }
      | { status: 'applied'; sessionId: string; result: ApplyResult }
      | { status: 'rejected'; sessionId: string; code: string }
      | { status: 'unknown'; sessionId: string }
    >
    /** 放下调用方租约；脏模型保留，之后 open 可重新加入。 */
    export function close(params: SessionRequest): Thenable<{ closed: boolean; dirty: boolean }>
  }

  // ─────────────────────────── env ───────────────────────────

  export namespace env {
    export function appInfo(): Thenable<{ appName: string; appVersion: string; language: string }>
    /**
     * 交给**系统浏览器**打开一个网址。
     *
     * ★ 只放行 `https:`。`http:` / `file:` / `javascript:` 一律被拒
     * (`invalid_argument`)—— 这条路离开了应用的信任边界,之后没有第二道检查。
     *
     * ★ 想开在**应用内**,用 `tabs.openWebApp`(地址写死在清单里)或
     * `tabs.openBrowser`(动态地址,要 `tabs.browser` 能力 + `hostPermissions`)。
     */
    export function openExternal(url: string): Thenable<{ opened: boolean }>
    export const clipboard: {
      /** 需要 `clipboard` 能力;读取还需要一次性确认。 */
      readText(): Thenable<string>
      writeText(text: string): Thenable<void>
    }
  }

  // ─────────────────────── permissions ───────────────────────

  export type Permission =
    | 'workspace.read' | 'workspace.write' | 'process' | 'net' | 'secrets' | 'storage'
    | 'scm.read' | 'scm.write' | 'agent.intercept' | 'agent.context' | 'clipboard' | 'window.notify'
    /**
     * 在**应用内**打开网页(`tabs.openBrowser`)。
     *
     * ★ 能力只说「可以开网页」,开**哪些**由 `hostPermissions` 逐 URL 说了算。
     * 清单里写死的 `contributes.webApps` 不需要它 —— 那些地址用户安装时就看见了。
     */
    | 'tabs.browser'
    /** 与其它插件通信。目标还必须在你的 `dependencies` 里。 */
    | 'plugins'

  export namespace permissions {
    export function contains(permission: Permission | Permission[]): Thenable<boolean>
    /**
     * 运行期申请能力。
     *
     * ★ **只能要 `permissions ∪ optionalPermissions` 之内的东西。** 之外的
     * 直接返回 `false`,**不弹窗** —— 否则「市场审核看到的能力上界」就不等于
     * 「运行期可获得的能力上界」,而那正是这套模型成立的前提。
     *
     * @param reasonKey **l10n key**,不是句子。它会被宿主 `t()` 之后显示给用户。
     */
    export function request(permissions: Permission[], reasonKey: string): Thenable<boolean>
    export function remove(permissions: Permission[]): Thenable<void>
  }

  /**
   * 工作区类调用的**可选作用域**。`workspace.*` / `process.*` / `scm.*` /
   * `storage.workspace.*` 都接受它。需要 >=0.3.3。
   *
   * ## 为什么需要它
   *
   * 一个 Agent 工具在后台工作区(A)里跑,而用户此刻正看着 B。此前这类调用
   * 一律按**当前聚焦的工作区**定位,于是工具给 A 画的东西**写进了 B**。
   * 带上产生这次调用的 `callId`,宿主就把作用域钉在那次工具调用的工作区上
   * —— 和 `documents.*` 的 callId 是同一套东西。
   *
   * ## 不填会怎样
   *
   * - 这个插件**恰好一次**工具调用在跑 → 按那一次推断(插件无从选择,安全);
   * - **多次**在跑 → 调用被拒,不替你猜一个工作区;
   * - 没有工具调用在跑(菜单、命令面板、视图里的 UI 代码)→ 按发起时的工作区,
   *   与不带这个参数时的旧行为一致。
   *
   * ## 它不能用来指定任意工作区
   *
   * 值必须是**你正在跑**的一次工具调用的 callId。填别的插件的、已经结束的、
   * 或者根本猜的,只会拿到一条 `rejected` / `invalid_argument`——**不会**回退到
   * 当前聚焦的工作区。
   */
  export interface Scope {
    callId?: string
  }

  // ──────────────────────── workspace ────────────────────────

  export namespace workspace {
    export function folders(options?: Scope): Thenable<{ id: string; name: string; path: string }[]>

    export const fs: {
      /**
       * @param path **工作区相对路径**。绝对路径一律被拒 —— 让每次调用都去
       * 判断「这个绝对路径是不是恰好在工作区里」,在软链、大小写不敏感文件系统、
       * UNC 路径上各有各的坑。
       * @returns `revision` 是写回时的乐观锁凭据。
       */
      readFile(path: string, encoding?: 'utf8' | 'base64', options?: Scope): Thenable<{ data: string; revision: number }>
      /**
       * @param options.revision 上次读到的 revision。盘上变过就拒绝写入 ——
       * **Agent 和你可能在同时改同一个文件**。
       * @param options.callId 见 `Scope` —— 在后台工具的调用里写文件时**务必带上**,
       * 否则作用域按当前聚焦的工作区算。
       */
      writeFile(path: string, data: string, options?: { encoding?: 'utf8' | 'base64'; revision?: number } & Scope): Thenable<{ revision: number }>
      delete(path: string, options?: Scope): Thenable<void>
      stat(path: string, options?: Scope): Thenable<{ kind: 'file' | 'dir' | 'missing'; size: number; mtimeMs: number }>
    }

    /** 只支持前缀 glob(`src/**`、`docs/*.md`)。要更强的匹配,取回来自己筛。 */
    export function findFiles(glob: string, limit?: number, options?: Scope): Thenable<string[]>

    /**
     * 工作区里有文件变了。需要 `workspace.read`。
     *
     * ## ★★ 这**不是**文件系统 watcher
     *
     * 它只覆盖**经由应用发生**的变更:编辑器保存、Agent 的写文件工具、
     * 以及插件自己的 `workspace.fs.writeFile` / `delete`。
     *
     * **收不到**:外部编辑器改的文件、`git checkout`、终端里的 `mv`。
     * 宿主刻意没有递归 watcher(大仓库上开销大、macOS 上给不出可靠的重命名事件),
     * 把这条说清楚比给一个半真的 watcher 诚实 —— 否则你会在「为什么我在
     * VS Code 里改了没反应」上耗掉一天。
     *
     * @param options.globs 前缀 glob 过滤(同 `findFiles`)。不给 = 全都要。
     */
    export function onDidChangeFiles(
      handler: (changes: { path: string; kind: 'created' | 'modified' | 'deleted' }[]) => unknown,
      options?: { globs?: string[] }
    ): Disposable
  }

  // ───────────────────────── process ─────────────────────────

  export namespace process {
    /**
     * 跑一条**非交互**命令。
     *
     * ★ `command` 必须命中清单里的白名单(只比对 argv[0] 的基名),
     * 且每次调用都走完整的审批链 —— 和用户手敲一条命令走的是同一条路。
     * ★ 拿不到 `ChildProcess`:回来的只有 code / stdout / stderr。
     */
    export function exec(
      command: string,
      args?: string[],
      options?: { cwd?: string; timeoutMs?: number; callId?: string }
    ): Thenable<{ code: number; stdout: string; stderr: string }>

    /**
     * 同样跑一条命令,但**边跑边给输出**。
     *
     * 需求:构建 / 测试 / 打包跑几十秒,`exec` 的形状让你在那几十秒里一个字都
     * 拿不到,做不出进度,也没法在第一条错误出现时就停。
     *
     * ★ 审批只在**开始时问一次**(和 `exec` 同一道门、同一份白名单),
     * 不逐段问 —— 逐段问的结果是用户为一条命令点二十次「允许」。
     * ★ 输出合批推送(50ms 一批);总量超限之后只推一条 `truncated`,
     * 不再转发正文,但命令本身照常跑完。
     * ★ 返回的是**句柄对象**,不是子进程:没有任何可以继续操作的流。
     */
    export function execStream(
      command: string,
      args?: string[],
      options?: {
        cwd?: string
        timeoutMs?: number
        /** 见 `Scope` —— 后台工具的调用里**务必带上**,否则 cwd 按当前聚焦的工作区算。 */
        callId?: string
        onOutput?: (chunk: { stream: 'stdout' | 'stderr'; chunk: string; truncated: boolean }) => unknown
        onExit?: (result: { code: number; timedOut: boolean }) => unknown
      }
    ): {
      /** 请求中断。中断 / 超时同样会走 `onExit`,`code` 是 124(对齐 `timeout(1)`)。 */
      abort(): Thenable<void>
      done: Thenable<{ code: number; timedOut: boolean }>
    }
  }

  // ─────────────────────────── net ───────────────────────────

  export namespace net {
    /**
     * 发一条 https 请求。
     *
     * ★ 插件的页面**出不了网**(CSP 的 `connect-src` 不给外网),所有请求
     * 都必须走这里 —— 于是「这个插件访问了哪些域名」变成主进程可校验、
     * 可审计、可关闭的一条通道。
     * ★ 逐 URL 匹配清单里的 `hostPermissions`;内网与环回地址一律拒绝。
     * ★ **重定向不自动跟**:每一跳都重过一遍同一道 URL 门,最多 5 跳。
     *   跨 host 的跳转会被拒 —— 跟着走等于让远端替你改掉 `hostPermissions`。
     * ★ 正文按**字节**截到 8MB。
     * ★ 回来的是**数据**,不是 `Response`:没有流句柄可以继续操作。
     * ★ `init.callId` 是**取消凭据**,不是工作区作用域(net.fetch 与工作区无关)。
     *   在工具的调用里带上产生这次调用的 `callId`,用户点停止时已经发出的请求
     *   会被真正中断,而不只是「阻止下一次请求」。见 `Scope` 的 callId 取值规则。
     */
    export function fetch(
      url: string,
      init?: { method?: string; headers?: Record<string, string>; body?: string; callId?: string }
    ): Thenable<{ status: number; headers: Record<string, string>; body: string }>
  }

  // ───────────────────── storage / secrets ───────────────────

  export interface Memento {
    get(key: string): Thenable<string | null>
    set(key: string, value: string | null): Thenable<void>
    keys(): Thenable<string[]>
  }

  export const storage: {
    /** 跟着这台机器走。 */
    global: Memento
    /** 跟着**这次调用**的工作区走(见 `Scope`)。 */
    workspace: {
      get(key: string, options?: Scope): Thenable<string | null>
      set(key: string, value: string | null, options?: Scope): Thenable<void>
      keys(options?: Scope): Thenable<string[]>
    }
  }

  /** `safeStorage` 加密。key 会被宿主自动加上 `plugin:<id>:` 前缀。 */
  export const secrets: {
    get(key: string): Thenable<string | null>
    set(key: string, value: string | null): Thenable<void>
  }

  // ───────────────────── configuration ───────────────────────

  export namespace configuration {
    /**
     * 用户在插件详情页里拨的那些开关,合上清单里的默认值。
     *
     * ★ **只读。** 设置是用户的意思,不是插件的 —— 给了写入口之后,
     * 「我明明关掉了」会变成一个没人能解释的现象。
     */
    export function get(): Thenable<Record<string, boolean | string | number>>
  }

  // ───────────────────────── window ──────────────────────────

  export namespace window {
    /** @param messageKey **l10n key**,不是句子。需要 `window.notify` 能力。 */
    export function showMessage(
      kind: 'info' | 'warn' | 'error',
      messageKey: string,
      params?: Record<string, string | number>
    ): Thenable<void>

    /**
     * 由**宿主**渲染的选择器。
     *
     * ★ 插件弹不了自己的菜单 —— 它的 UI 在一个跨源 iframe 里,碰不到宿主 DOM。
     * 这不是限制,是那条边界本身:插件画不出一个看起来像系统菜单的东西。
     */
    export function showQuickPick(
      items: { id: string; labelKey: string }[],
      placeholderKey?: string
    ): Thenable<string | null>

    /**
     * 挂一格状态栏。`textKey` 传 `null` 摘掉它。
     *
     * ★ 每个插件**最多 3 格**,`textKey` 必须是 l10n key,`command` 必须是
     * 自己贡献过的命令。
     */
    export function setStatusBarItem(
      id: string,
      textKey: string | null,
      options?: { tooltipKey?: string; command?: string }
    ): Thenable<void>

    /**
     * 要一行输入。`*Key` 全是 l10n key,不是句子。
     *
     * ★ 用户直接关掉 = `null`,**不是错误**:没理会一个弹窗不是故障。
     * ★ `password: true` 时宿主用掩码输入,而且那个值不进活动日志。
     */
    export function showInputBox(options: {
      titleKey: string
      placeholderKey?: string
      initial?: string
      password?: boolean
    }): Thenable<string | null>

    /** 要一次确认。取消 = `false`。`danger` 只影响语气色,不改变行为。 */
    export function showConfirm(options: {
      titleKey: string
      detailKey?: string
      danger?: boolean
    }): Thenable<boolean>

    /**
     * 包住一段异步工作,期间在状态栏上显示进度。
     *
     * ★ 给的是**包裹**而不是裸的 start/end:裸的那种一旦中间抛异常,进度条就
     * 永远留在状态栏上转,而没有人再去关它。这里 `finally` 一定会收掉。
     * ★ 单插件同时最多 3 条;插件被禁用时宿主替它把每一条都撤掉。
     */
    export function withProgress<T>(
      options: { titleKey: string; id?: string },
      task: (progress: { report: (update: { fraction?: number; messageKey?: string }) => void }) => Thenable<T> | T
    ): Thenable<T>
  }

  // ────────────────────────── tabs ───────────────────────────

  export namespace tabs {
    /**
     * 为某个工作区文件打开**你自己的**自定义编辑器 Tab。
     *
     * ```ts
     * await ncw.workspace.fs.writeFile('drawings/new.excalidraw', '{}')
     * await ncw.tabs.openCustomEditor('acme.editor', 'drawings/new.excalidraw')
     * ```
     *
     * - `viewType` 必须出现在你的 `contributes.customEditors` 里,否则这次调用
     *   被静默忽略 —— 你开不了别人的编辑器;
     * - `path` 是**工作区相对路径**,跑到根外面会被拒;
     * - 需要 `workspace.read` 能力。
     *
     * ★ 没有配套的关闭方法:关 Tab 是**用户**的动作。
     */
    export function openCustomEditor(viewType: string, path: string): Thenable<void>

    /**
     * 打开你在 `contributes.webApps` 里声明过的一个网页应用。
     *
     * ```jsonc
     * // package.json —— 连 main 都不需要
     * { "kind": "webapp", "contributes": { "webApps": [
     *   { "id": "home", "title": "%app.home%", "icon": "tv", "url": "https://www.bilibili.com/" }
     * ] } }
     * ```
     *
     * ★ **不需要任何能力**:地址写死在清单里,用户安装时就看见了。
     * ★ 装上之后侧边栏自动有一条入口(`entry: "sidebar"`,缺省),
     *   所以大多数「把网站带进来」的插件**一行代码都不用写**。
     */
    export function openWebApp(webAppId: string): Thenable<boolean>

    /**
     * 在应用内打开一个**动态**地址。
     *
     * ★ 两道门都要过:能力 `tabs.browser` + 地址命中 `hostPermissions`。
     * 只有能力没有地址门的话,一个声明「我只访问 bilibili.com」的插件可以
     * 在应用内打开任何网站,而用户在安装界面上看到的域名只有那一个。
     * ★ 站外链接不会留在这个 Tab 里:宿主拦下来交给系统浏览器。
     */
    export function openBrowser(
      url: string,
      options?: { open?: 'tab' | 'right' }
    ): Thenable<boolean>

    /**
     * 在指定工作区的**根目录**开一个终端 Tab,并在里面启动 `command`。
     *
     * ```ts
     * // claude-code 插件:把用户的 baseURL / key / 模型以环境变量带进 CLI
     * const result = await ncw.tabs.openTerminal({
     *   workspaceId, command: 'claude', env: { ANTHROPIC_BASE_URL: baseUrl }
     * })
     * if (result.opened === false) throw new Error(result.reason)
     * ```
     *
     * - 需要 `process` 能力,且 `command` 的基名必须命中清单 `allowedCommands`
     *   —— 与 `process.exec` 同一份白名单,能进交互式终端的命令不比能一次性
     *   执行的更宽;
     * - `env` 会注入终端子进程环境(不落盘、不进终端回滚缓冲),最多 16 项、
     *   单值 ≤4096 字符;
     * - **不接受 `cwd`**:恒为该工作区的根目录;
     * - `title` 是 `%key%` 形式的 l10n key 片段,作为终端 Tab 的标题;
     * - `opened: false` 表示没开成,`reason: 'remote'` = SSH 远程工作区
     *   (一期不支持),`'declined'` = 工作区不存在或参数被拒。
     */
    export function openTerminal(options: {
      workspaceId: string
      command: string
      args?: string[]
      env?: Record<string, string>
      title?: string
    }): Thenable<{ opened: boolean; reason?: 'remote' | 'declined' }>
  }

  // ─────────────────── customEditors ─────────────────────────

  export namespace customEditors {
    /**
     * 报告某份文档有没有没存的改动。
     *
     * ★ 宿主的「关 Tab 之前问一句」全靠这张表。不报的话,你的编辑器里没存的
     * 改动会在用户关 Tab 的那一刻**静默消失** —— 而内置文档是会挽留的,
     * 两者行为不一致更难被发现。
     */
    export function setDirty(documentId: string, path: string, dirty: boolean): Thenable<void>
  }

  // ─────────────────────────── scm ───────────────────────────

  export namespace scm {
    /** 需要 `scm.read`。仓库开不出来(远程工作区 / 没装 git / 不是仓库)时这条调用失败。 */
    export function status(options?: Scope): Thenable<{ branch: string; staged: string[]; unstaged: string[] }>
    /** 一个文件的 diff。**没有「整仓 diff」**:那是一次无上限的输出,而 `status` 已经说了哪些文件变了。 */
    export function diff(path: string, options?: { staged?: boolean } & Scope): Thenable<{ diff: string; binary: boolean; truncated: boolean }>
    export function log(options?: { limit?: number } & Scope): Thenable<{ hash: string; subject: string; author: string; at: number }[]>
    export function branches(options?: Scope): Thenable<{ current: string; branches: string[] }>
    /** 以下四条需要 `scm.write`,并且走和用户手敲命令**同一条**审批链。 */
    export function stage(paths: string[], options?: Scope): Thenable<void>
    export function commit(message: string, options?: Scope): Thenable<string>
    export function createBranch(name: string, options?: { checkout?: boolean } & Scope): Thenable<void>
    export function checkout(name: string, options?: Scope): Thenable<void>
    /*
      ★ 没有 push / pull:它们把本机凭据用到远端,而失败形态(冲突、鉴权、
      远端 hook)不是一个返回值能如实回答的。
    */
  }

  // ────────────────────────── agent ──────────────────────────

  export namespace agent {
    /**
     * 工具调用拦截器。需要 `agent.intercept`。
     *
     * ★ **只能收紧,不能放宽**:返回 `allow` 不会跳过宿主自己的权限链。
     * ★ 超时(3s)= 弃权,只记一条诊断 —— 一个卡住的插件不该把所有工具调用堵死。
     */
    export function registerToolInterceptor(
      handler: (call: { tool: string; input: unknown }) => Thenable<ToolVerdict | void> | ToolVerdict | void
    ): Disposable

    /**
     * 每轮往上下文里注入一小段。需要 `agent.context`。
     *
     * ★ 有长度上限,而且会被宿主**强制包裹**成 `<plugin-context source="…">` ——
     * 模型必须能看出这一段来自插件,而不是来自用户或系统提示词。
     */
    export function registerContextProvider(
      handler: (input: { sessionId: string }) => Thenable<string | void> | string | void
    ): Disposable
  }

  export interface ToolVerdict {
    decision: 'allow' | 'ask' | 'deny'
    /** l10n key,不是句子 —— 它会显示给用户。 */
    reasonKey?: string
  }

  // ──────────────────────── commands ─────────────────────────

  export namespace commands {
    /**
     * @param commandId 必须是清单 `contributes.commands` 里声明过的 ——
     * 没声明的会被静默忽略(菜单、快捷键、命令面板都按 id 分发,
     * 允许运行期注册任意 id 等于让插件能劫持别人的命令)。
     * @param handler 的 `args` 是**宿主调用点上下文**,不是用户输入:
     * `+` 菜单触发的调用带 `{ workspaceId }`(菜单在哪个工作区点的)。
     * 它只是来源信息,命令的语义仍由清单声明。
     */
    export function registerCommand(commandId: string, handler: (args?: unknown) => unknown): Disposable
    /** ★ 只能执行**自己**贡献的命令。跨插件调用会被拒。 */
    export function executeCommand(commandId: string, args?: unknown): Thenable<unknown>
  }

  // ────────────────────────── tools ──────────────────────────

  /** 卡片里状态/键值的语义色调 —— 宿主映射到主题色,插件不能直接给颜色值。 */
  export type CardTone = 'neutral' | 'info' | 'ok' | 'warn' | 'danger'

  /**
   * 声明式卡片的原语块。由宿主的可信渲染器解释,**插件代码不进渲染路径**。
   * 未知块 / 越界字段会被宿主丢弃或钳制(不是报错)。
   */
  export type CardBlock =
    | { type: 'keyValue'; rows: Array<{ label: string; value: string; tone?: CardTone }> }
    | { type: 'table'; columns: string[]; rows: string[][] }
    | { type: 'status'; label: string; tone?: CardTone }
    | { type: 'text'; value: string }
    | { type: 'code'; value: string; language?: string }
    /** dataRef 只接受 `ncw://`(受管附件)或 `data:`(内联)。 */
    | { type: 'image'; dataRef: string; alt?: string }
    | { type: 'progress'; fraction: number; label?: string }
    /** href 只接受 https;宿主经系统默认程序打开,不在应用内导航。 */
    | { type: 'link'; href: string; label?: string }
    /**
     * 交互按钮:点了走反向通道回到**仍在运行**的工具(见 `Tool.invoke` 的 `onAction`)。
     * 只在实时卡片(经 `progress`)上有意义;工具已返回后点它无效果。
     *
     * `confirm.titleKey` 是 **l10n key**:给了就先弹一次宿主的确认框。
     * 危险动作(删东西、覆盖)自己标 —— 宿主替你判断不了哪一条危险。
     */
    | { type: 'button'; actionId: string; label: string; tone?: CardTone; confirm?: { titleKey: string } }
    /**
     * 一段 markdown,由**宿主的**受信渲染器解释(链接同样只走系统浏览器,
     * 不允许裸 HTML)。`text` 只能给一整段没有结构的字,而你最常要表达的
     * 是「几行要点 + 一个链接」。
     */
    | { type: 'markdown'; value: string }
    /** 一列要点。与 `table` 的区别是它**不承诺列对齐** —— 一行一件事。 */
    | { type: 'list'; items: Array<{ label: string; tone?: CardTone; hint?: string }> }
    /** 一个数值格:大字 + 说明 + 可选变化量。别把数字硬塞进 `keyValue`,那一栏的字号读不出重点。 */
    | { type: 'metric'; label: string; value: string; delta?: string; tone?: CardTone }
    /** 分隔线。纯视觉。 */
    | { type: 'divider' }

  /**
   * 工具结果的自定义卡片 —— **只影响 UI,不下发给模型**。
   *
   * - `declarative`:一组白名单原语,安全轻量,覆盖多数场景。
   * - `frame`:用你自己的 `contributes.cardViews` 页面渲染任意 UI;`viewType`
   *   必须在清单里声明过,`data` 会被单向推入卡片 iframe(只读)。
   */
  export type ToolCard =
    | { kind: 'declarative'; blocks: CardBlock[] }
    | { kind: 'frame'; viewType: string; data: unknown }

  export interface ToolResult {
    content: { text: string }[]
    isError?: boolean
    /**
     * 可选的自定义结果卡片。宿主会校验(白名单原语、尺寸上限、scheme、frame viewType
     * 必须已声明);不合法则**静默丢弃卡片、只保留文本**。
     */
    card?: ToolCard
  }

  /** 运行中推进度 / 一张实时卡片。message / card 都是易失的(不进转录)。 */
  export interface ToolProgressUpdate {
    /** 一行进度文字,显示在折叠态工具行右侧 */
    message?: string
    /**
     * 实时卡片(第 2 层)。工具还没返回就先给一张可交互的卡;宿主会像结果卡片一样
     * 校验它(白名单原语、frame viewType 必须已声明)。反复调用 = 覆盖上一张。
     */
    card?: ToolCard
  }

  /** 用户在实时卡片的某个 `button` 上的动作。 */
  export interface CardAction {
    actionId: string
    value?: unknown
  }

  export interface Tool<T = Record<string, unknown>> {
    description: string
    /** JSON Schema,**原样下发给上游**,宿主不解释它。 */
    inputSchema: Record<string, unknown>
    readOnly?: boolean
    destructive?: boolean
    needsNetwork?: boolean
    /**
     * 声明这是一个**交互式**工具:它会推一张带按钮的实时卡片并挂起等用户点。
     * 宿主据此放宽这次调用的超时到交互硬上限(否则会撞上常规 60s 超时);
     * 用户随时可取消(= abort)。
     */
    interactive?: boolean
    invoke(options: {
      input: T
      callId: string
      /** 运行中推进度 / 实时卡片 —— 工具还在跑时调用,见 `ToolProgressUpdate`。 */
      progress: (update: ToolProgressUpdate) => void
      /**
       * 注册用户在实时卡片按钮上的动作回调。典型用法:推一张带按钮的卡,
       * 然后 `await new Promise((r) => onAction((a) => r(a)))` 挂起等点击,再据此返回结果。
       */
      onAction: (handler: (action: CardAction) => void) => void
    }): Thenable<ToolResult | string>
  }

  export namespace tools {
    /**
     * 贡献一个 Agent 工具。
     *
     * ★ `name` 必须在清单 `contributes.tools` 里声明过。工具进的是和内置工具、
     * MCP 工具**同一张表**,宿主会加 `plugin__<id>__` 前缀去重 —— 两个插件
     * 各注册一个 `search` 不会互相顶掉。
     */
    export function registerTool<T>(name: string, tool: Tool<T>): Disposable
  }

  // ─────────────────────── plugins(插件间通信) ───────────────────────

  export namespace plugins {
    /**
     * 对外导出一组可被别的插件调用的方法。**无需权限** —— 提供不是消费。
     * 方法名的集合就是你的 API 表面;调用方经 `connect(你的 id)` 拿到代理来调。
     */
    export function exposeApi(methods: Record<string, (...args: any[]) => unknown>): Disposable

    /**
     * 连接另一个插件导出的 API,拿到一个调用代理:`connect(id).foo(1, 2)` 会跨插件
     * 调到对方 `exposeApi` 里的 `foo`,返回值经 Promise 带回。
     *
     * ★ 需要 `plugins` 能力,且 `pluginId` 必须在你的清单 `dependencies` 里声明过 ——
     * 两道门都过不了则调用返回 `null`。目标在睡会被自动唤醒。
     */
    export function connect<T = Record<string, (...args: any[]) => Promise<any>>>(pluginId: string): T

    /** 松耦合事件总线。需要 `plugins` 能力。topic 建议加命名空间前缀避免撞车。 */
    export namespace events {
      /** 往一个 topic 广播。宿主扇出给订阅者(不含自己),只投给正在运行的。 */
      export function emit(topic: string, payload?: unknown): Thenable<void>
      /** 订阅一个 topic。`from` 是发出者的 pluginId。返回的 Disposable 用于退订。 */
      export function on(topic: string, handler: (payload: unknown, from?: string) => void): Disposable
    }
  }

  // ─────────────────────── diagnostics ───────────────────────

  export namespace diagnostics {
    /** 写一条进插件详情页的「活动」标签。排查用,不是给用户看的。 */
    export function log(level: 'info' | 'warn' | 'error', message: string): Thenable<void>
  }

  // ─────────────────────── appearance ───────────────────────

  /**
   * 宿主的深浅色。**不需要任何权限** —— 同 `env.appInfo`。
   *
   * ★ 这套 API 是给**插件逻辑**(extension.ts)用的。插件的**视图** iframe
   * 里另有一套、且不用调任何 API:宿主会把 24 个颜色 token 写成 `--ncw-<token>`
   * CSS 变量,并在 `<html>` 上同步 `data-theme`。视图侧要在 JS 里跟随的话,
   * 读 `globalThis.__ncwTheme` 或监听 window 上的 `ncw:theme` 事件。
   */
  export namespace appearance {
    export function get(): Thenable<'light' | 'dark'>

    /**
     * 主题变化时回调。返回的 `Disposable` 用来取消。
     *
     * ★ 插件**休眠期间不会收到**回调(空闲五分钟后宿主页面会被销毁)。
     * 这是有意的:没人在用的插件不值得为一次颜色变化叫醒。下次被唤醒时
     * `get()` 拿到的就是最新值,所以别把「收到过几次回调」当状态用。
     */
    export function onDidChange(handler: (appearance: 'light' | 'dark') => unknown): Disposable
  }
}
