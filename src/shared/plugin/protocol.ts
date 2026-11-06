/**
 * 插件 ↔ 主进程的 RPC 协议。
 *
 * ## ★ 为什么**不复用** `shared/ipc/contract.ts`
 *
 * 那套三张白名单是给**受信任的主窗口**用的。`preload/index.ts` 的注释写得很明白:
 * 「被攻破的渲染层等价于任意文件系统访问」—— 主窗口是我们自己的代码,那份信任
 * 是设计前提。插件宿主**不受信任**:它跑的是第三方代码,而且是从市场下载的。
 *
 * 所以这里是**另一条通道**:独立协议、独立白名单、独立能力校验。两套混在一起的话,
 * 任何一次给主窗口加频道都会顺带给插件开一个口子,而那个口子不会有人注意到。
 *
 * ## 两道检查,顺序不能换
 *
 * ```
 * ① 白名单:这个方法存在吗        ← 不存在直接拒,连参数都不解析
 * ② 能力  :这个插件被批准了吗     ← 查 PluginPermissionState
 * ③ 参数  :收窄(路径/域名/命令)   ← 在 capabilities.ts
 * ④ 行为  :既有八层权限链          ← 和模型自己调工具同一条路
 * ```
 *
 * ①在②之前是有意的:先回答「有没有这个方法」再回答「你能不能用」,
 * 否则未知方法会走到能力表里查不到、然后以「没有权限」的名义被拒 ——
 * 插件作者拼错一个方法名会收到一条完全误导的错误。
 */
import type { PluginPermission } from './permission'
import type { DocumentApplyResult, DocumentCapabilities, DocumentFormat, DocumentOperation, DocumentQuery } from '../document-engine/protocol'
import type { DocumentSessionSnapshot } from '../document-engine/session'

/** 需求：callId 只是查找受信工具上下文的索引，插件不能直接指定 workspaceId。 */
type DocumentSessionParams = { sessionId: string; callId?: string }

/**
 * 每次调用可选的**工作区作用域凭据**。工作区类方法(`workspace.*` / `process.*` /
 * `scm.*` / `storage.*`)都可以带上它。
 *
 * ## 它解决什么
 *
 * Agent 在后台工作区跑一个工具(`ctx.workspaceId = A`),而用户此刻正看着 B。
 * 此前这类 RPC 一律按「当前聚焦的工作区」定位,于是插件给 A 画的图**写进了 B**,
 * 而用户看到的是「我明明在 A 里点的新建」。这条凭据让那次调用绑定在**产生它的
 * 那次工具调用**的工作区上 —— 与 `documents.*` 的 callId 是同一套东西。
 *
 * ## ★ 它不能用来指定任意工作区
 *
 * 值必须是**本插件正在跑**的一次工具调用的 callId(宿主核对主人、存活、
 * 以及那次调用有没有本地工作区)。插件填一个不存在的 callId 只会收到一条
 * `rejected` / `invalid_argument`,**不会**回退到当前聚焦的工作区。
 *
 * ## 不给也能工作
 *
 * 没有 callId 时:本插件**恰好一次**工具调用在跑 → 按那一次推断(安全:插件
 * 无从选择);**多次**在跑 → 拒绝,不随机挑一个。没有任何工具调用在跑(菜单、
 * 命令面板、视图里的 UI 代码)→ 仍按「发起时的工作区」语义,与旧行为一致。
 */
export type PluginScopeParam = { callId?: string }

// ─────────────────────────── 方法表 ───────────────────────────

/**
 * 插件能调的全部方法。**每一条都在这里逐个列出**,没有通配。
 *
 * `permission: null` = 不需要任何能力(但仍然要过白名单)。
 */
export interface PluginMethodMap {
  // env —— 只读的宿主身份,不含任何用户数据
  'env.appInfo': { params: Record<string, never>; result: { appName: string; appVersion: string; language: string } }
  'env.openExternal': { params: { url: string }; result: { opened: boolean } }
  'env.clipboardRead': { params: Record<string, never>; result: { text: string } }
  'env.clipboardWrite': { params: { text: string }; result: Record<string, never> }

  /*
    appearance —— 宿主当前的深浅色。
    **不挂权限**(同 `env.appInfo`):它不含用户数据,也不扩展任何访问面,
    而一个连主题都要申请才能读的插件系统,结果是所有插件都申请,权限表失去意义。

    `subscribe` 之后主题变化经 `kind: 'event'` 反向推送(见 `PluginInvocation`)。
    没有 unsubscribe:插件停用时整个宿主页面就销毁了,订阅跟着一起没,
    多一条只能被漏调的 API。
  */
  'appearance.get': { params: Record<string, never>; result: { appearance: 'light' | 'dark' } }
  'appearance.subscribe': { params: Record<string, never>; result: Record<string, never> }

  // l10n / 权限 —— 插件自查,不碰别人
  'permissions.contains': { params: { permissions: PluginPermission[] }; result: { granted: boolean } }
  'permissions.request': { params: { permissions: PluginPermission[]; reasonKey: string }; result: { granted: boolean } }
  'permissions.remove': { params: { permissions: PluginPermission[] }; result: Record<string, never> }

  // storage —— 独立 kv,单插件配额。`scope: 'workspace'` 的键按调用作用域的工作区分桶
  'storage.get': { params: PluginScopeParam & { scope: 'global' | 'workspace'; key: string }; result: { value: string | null } }
  'storage.set': { params: PluginScopeParam & { scope: 'global' | 'workspace'; key: string; value: string | null }; result: Record<string, never> }
  'storage.keys': { params: PluginScopeParam & { scope: 'global' | 'workspace' }; result: { keys: string[] } }

  // secrets —— 程序主密钥加密,key 强制前缀
  'secrets.get': { params: { key: string }; result: { value: string | null } }
  'secrets.set': { params: { key: string; value: string | null }; result: Record<string, never> }

  // workspace —— 全部限在工作区根内,过 path-guard。可选 callId 把作用域钉在产生它的那次工具调用上
  'workspace.folders': { params: PluginScopeParam; result: { folders: { id: string; name: string; path: string }[] } }
  'workspace.readFile': { params: PluginScopeParam & { path: string; encoding?: 'utf8' | 'base64' }; result: { data: string; revision: number } }
  'workspace.writeFile': { params: PluginScopeParam & { path: string; data: string; encoding?: 'utf8' | 'base64'; revision?: number }; result: { revision: number } }
  'workspace.deleteFile': { params: PluginScopeParam & { path: string }; result: Record<string, never> }
  'workspace.stat': { params: PluginScopeParam & { path: string }; result: { kind: 'file' | 'dir' | 'missing'; size: number; mtimeMs: number } }
  'workspace.findFiles': { params: PluginScopeParam & { glob: string; limit?: number }; result: { paths: string[] } }

  // process —— 非交互,argv[0] 查白名单,走权限链
  'process.exec': { params: PluginScopeParam & { command: string; args: string[]; cwd?: string; timeoutMs?: number }; result: { code: number; stdout: string; stderr: string } }
  /**
   * 流式跑一条命令 —— 输出经 `kind: 'event'` 边跑边推,而不是等它结束。
   *
   * 需求:构建、测试、打包这类命令跑几十秒,`process.exec` 的形状让插件在那
   * 几十秒里一个字都拿不到,只能在结束后一次性倒出来。
   *
   * ★ **审批只在 start 时问一次**(同 `process.exec`),不逐 chunk 问 ——
   * 逐 chunk 问的结果是用户为了一条命令点二十次「允许」。
   */
  'process.execStream': { params: PluginScopeParam & { command: string; args: string[]; cwd?: string; timeoutMs?: number }; result: { execId: string } }
  'process.execAbort': { params: { execId: string }; result: Record<string, never> }

  /*
    net —— 逐 URL 匹配 hostPermissions,response 剥成数据。

    ★ 这里的 `callId` **不是**工作区作用域(net.fetch 与工作区无关,作用域是
    `hostPermissions`),而是**取消凭据**:带上产生这次调用的工具调用的 callId,
    宿主就把宿主的 `ToolContext.signal` 接给这次请求 —— 用户点停止时,已经发出去
    的请求立刻断,而不只是「阻止下一次调用」。携带与作用域同一个 `PluginScopeParam`,
    是为了让「哪些方法认 callId」在我们这边只有一套形状。
  */
  'net.fetch': { params: PluginScopeParam & { url: string; method?: string; headers?: Record<string, string>; body?: string }; result: { status: number; headers: Record<string, string>; body: string } }

  // scm —— 读类要 scm.read;写类要 scm.write,并且走既有审批链(isMutatingPermission)
  'scm.status': { params: PluginScopeParam; result: { branch: string; staged: string[]; unstaged: string[] } }
  /**
   * 一个文件的 diff。
   *
   * ★ `path` 是**必填**的,没有「整仓 diff」这一档:整仓 diff 在大仓库上是一次
   * 无上限的输出,而「哪些文件变了」`scm.status` 已经回答了。这条也因此能原样
   * 落在 `ipc/git.ts` 的 `getGitDiff` 上(含未跟踪文件那条支线),不必另起一套。
   */
  'scm.diff': { params: PluginScopeParam & { path: string; staged?: boolean }; result: { diff: string; binary: boolean; truncated: boolean } }
  'scm.log': { params: PluginScopeParam & { limit?: number }; result: { commits: { hash: string; subject: string; author: string; at: number }[] } }
  'scm.branches': { params: PluginScopeParam; result: { current: string; branches: string[] } }
  'scm.stage': { params: PluginScopeParam & { paths: string[] }; result: Record<string, never> }
  'scm.commit': { params: PluginScopeParam & { message: string }; result: { hash: string } }
  /** `checkout: true` = 建完就切过去(`git switch -c`)。 */
  'scm.createBranch': { params: PluginScopeParam & { name: string; checkout?: boolean }; result: Record<string, never> }
  'scm.checkout': { params: PluginScopeParam & { name: string }; result: Record<string, never> }
  /*
    ★ **没有 push / pull。** 它们会把本机凭据用到远端,而失败形态(冲突、鉴权、
    远端 hook 拒绝)不是一条 RPC 的返回值能如实回答的 —— 插件只看到「失败了」,
    而用户看到的是一次自己没发起、也无从处理的远端操作。
  */

  // tabs(网页/视图)—— 主进程只校验与广播,Tab 是渲染层的概念,见 `tabs.openCustomEditor`
  /**
   * 打开清单里声明过的一个网页应用(`contributes.webApps`)。
   *
   * ★ **不需要 `tabs.browser`**:URL 在清单里写死,用户安装时就看得见,
   * 运行期再问一次是在问一个已经回答过的问题。动态地址走 `tabs.openBrowser`。
   */
  'tabs.openWebApp': { params: { webAppId: string }; result: { opened: boolean } }
  /**
   * 打开一个任意地址(仍然逐 URL 匹配 `hostPermissions`,且只认 https)。
   *
   * ★ 能力 `tabs.browser` + 参数门两道都要过。只有能力没有参数门的话,一个
   * 「B 站插件」可以在应用内打开任何网站,而用户在安装界面上看到的域名只有 B 站。
   */
  'tabs.openBrowser': { params: { url: string; open?: 'tab' | 'feature' | 'right' }; result: { opened: boolean } }
  /**
   * 在**当前工作区根目录**开一个终端 Tab,并在里面启动插件指定的命令。
   *
   * 需求:claude-code / codex 这类 CLI 插件的落点 —— 「点一下菜单,工作区里
   * 起一个带着我的配置的交互式 CLI」。`process.exec` 是一次性取回输出的,
   * 给不了交互式 TTY,所以这是另一条方法,而不是一个参数。
   *
   * ★ 能力 `process` + 参数门(`capabilities.ts` 的 `narrowCommand`,argv[0]
   * 必须命中清单 `allowedCommands`)两道都要过,与 `process.exec` 同一套白名单
   * —— 放行到交互式终端的命令不会比一次性执行更宽松。`env` 的键数与单值长度
   * 有上限(防借环境变量夹带),`cwd` **不接受插件指定**:恒为工作区根,
   * 由宿主解析。「cwd 想指哪就指哪」等于绕过工作区边界。
   *
   * ★ 一期仅本地工作区:远程环境返回 `{ opened: false, reason: 'remote' }`,
   * env 无法直接注入远程 pty(见 `main/terminal-host.ts` 的 launchSpecs)。
   */
  'tabs.openTerminal': {
    params: {
      workspaceId: string
      command: string
      args?: string[]
      env?: Record<string, string>
      /** `%key%` 形式的 l10n key 片段,渲染层拼 `plugin.<id>.<key>` 后作为 Tab 标题 */
      title?: string
    }
    result: { opened: boolean; reason?: 'remote' | 'declined' }
  }
  /*
    ★ **没有 `tabs.openView`。** `contributes.views` 里 location 为 sidebar/panel 的
    视图这一版还打不开,理由见 `shared/plugin/ui-request.ts` 里 `PluginTabTarget`:
    现有的 `custom` Tab 从定义上就是「某个文件的编辑器」,塞进去只会开出一个
    读不到任何文档的空编辑器。声明了这类视图的插件会在详情页看到一条诊断,
    而不是一条点了没反应的入口。
  */

  // workspace 变更订阅 —— 反向通道走 `kind: 'event'`,见 `PluginInvocation`
  /**
   * 订阅工作区文件变更。
   *
   * ★ **这不是文件系统 watcher。** 它只覆盖**经由应用发生**的变更(编辑器保存、
   * Agent 工具写入、插件自己的 `workspace.writeFile`)。外部编辑器、`git checkout`
   * 改的文件**不会**触发 —— 仓库刻意没有递归 watcher(见 `ipc/workspace-search.ts`
   * 的说明),把这条说清楚比给一个半真的 watcher 诚实。
   */
  'workspace.subscribeChanges': { params: { globs?: string[] }; result: Record<string, never> }
  'workspace.unsubscribeChanges': { params: Record<string, never>; result: Record<string, never> }

  // window —— 宿主渲染的 UI,插件不接触宿主 DOM
  'window.showMessage': { params: { kind: 'info' | 'warn' | 'error'; messageKey: string; params?: Record<string, string | number> }; result: Record<string, never> }
  'window.showQuickPick': { params: { items: { id: string; labelKey: string }[]; placeholderKey?: string }; result: { id: string | null } }
  /**
   * 一行输入。`*Key` 是 l10n key 不是文案(同 `showMessage`)。
   *
   * ★ 用户直接关掉 = `{ value: null }`,不是错误:没理会一个弹窗不是故障。
   * `password: true` 时渲染层用掩码输入,并且**回执不进活动日志**。
   */
  'window.showInputBox': { params: { titleKey: string; placeholderKey?: string; initial?: string; password?: boolean }; result: { value: string | null } }
  /** 一次确认。`danger` 只影响渲染层的语气色,不改变行为。 */
  'window.showConfirm': { params: { titleKey: string; detailKey?: string; danger?: boolean }; result: { confirmed: boolean } }
  /**
   * 长任务进度。三条一组(start/update/end),`id` 由插件自己起,同一 id 重复 start 视为更新。
   *
   * ★ 没有「自动结束」:插件崩了 / 被禁用时由宿主清空它的全部进度条,
   * 而不是让一条永远转下去的进度留在状态栏上。
   */
  'window.progressStart': { params: { id: string; titleKey: string }; result: Record<string, never> }
  'window.progressUpdate': { params: { id: string; fraction?: number; messageKey?: string }; result: Record<string, never> }
  'window.progressEnd': { params: { id: string }; result: Record<string, never> }
  'window.setStatusBarItem': { params: { id: string; textKey: string | null; tooltipKey?: string; command?: string }; result: Record<string, never> }

  // commands / tools —— 注册与自查
  'commands.register': { params: { commandId: string }; result: Record<string, never> }
  'commands.unregister': { params: { commandId: string }; result: Record<string, never> }
  'commands.execute': { params: { commandId: string; args?: unknown }; result: { value: unknown } }
  'tools.register': { params: { name: string; description: string; inputSchema: unknown; readOnly: boolean; destructive: boolean; needsNetwork: boolean; interactive?: boolean }; result: Record<string, never> }
  'tools.unregister': { params: { name: string }; result: Record<string, never> }

  // agent —— 拦截器与上下文提供者的注册(裁决本身走 invocation 反向通道)
  'agent.registerInterceptor': { params: Record<string, never>; result: Record<string, never> }
  'agent.registerContextProvider': { params: Record<string, never>; result: Record<string, never> }
  /*
    ★ **没有 `agent.registerSlashCommand`。** 一条斜杠命令背后就是一条**命令**
    (`contributes.slashCommands[].command` 必填),它走的是已经存在的
    `commands.register` + `command.run`。再给它一条独立的注册通道,等于同一件事
    有两个注册点 —— 而插件作者只会注册其中一个,另一个静默失效。
    菜单贡献(`contributes.menus`)用的也是同一条路。
  */

  // customEditors
  'customEditors.register': { params: { viewType: string }; result: Record<string, never> }
  /**
   * 让宿主为某个文件打开这个插件的自定义编辑器 Tab。
   *
   * ★ **插件说不了「打开哪个 Tab」,只能说「打开哪个文件」。** viewType 必须是
   * 它自己 `contributes.customEditors` 里声明过的(主进程核对),path 过工作区
   * 收窄 —— 于是一个插件既开不了别人的编辑器,也指不到工作区之外的文件。
   *
   * ★ 没有配套的 `tabs.close`:关 Tab 是**用户**的动作。给了关闭入口之后,
   * 「我的标签页自己没了」会变成一个没人能解释的现象(同 `configuration` 只读的理由)。
   */
  'tabs.openCustomEditor': { params: { viewType: string; path: string }; result: Record<string, never> }
  /**
   * 脏状态的**唯一来源**。
   *
   * ★ 必须带 `path`:宿主要靠它回答「关掉这个 Tab 之前有没有没存的东西」,
   * 而那个判断是按**文件**做的(见 `stores/documents.ts` 的
   * `confirmDocumentChanges`)。只给一个 documentId 的话,宿主知道「有东西脏了」
   * 却不知道是哪个文件 —— 于是要么每次关 Tab 都拦一下,要么干脆不拦。
   * 后者就是「关 Tab 静默丢图」。
   */
  'customEditors.setDirty': { params: { documentId: string; path: string; dirty: boolean }; result: Record<string, never> }

  // documents —— 类型描述调用契约，document-rpc 仍逐项校验运行时的不可信输入。
  'documents.open': {
    params: { path: string; engine?: string; callId?: string }
    result: { sessionId: string; viewId: string; path: string; snapshot: DocumentSessionSnapshot; capabilities: DocumentCapabilities }
  }
  'documents.apply': {
    params: DocumentSessionParams & { generation: number; modelRevision: number; operationId: string; operations: DocumentOperation[] }
    result: DocumentApplyResult
  }
  'documents.save': { params: DocumentSessionParams; result: { snapshot: DocumentSessionSnapshot } }
  'documents.export': {
    params: DocumentSessionParams & { path: string; format: DocumentFormat; overwrite?: boolean }
    result: { snapshot: DocumentSessionSnapshot; path: string }
  }
  'documents.getState': { params: DocumentSessionParams; result: DocumentSessionSnapshot }
  'documents.query': {
    params: DocumentSessionParams & { request: DocumentQuery }
    result: { generation: number; modelRevision: number; result: unknown }
  }
  'documents.getOperation': {
    params: DocumentSessionParams & { operationId: string }
    result: { status: 'missing' } | { status: 'applied'; sessionId: string; result: DocumentApplyResult } | { status: 'rejected'; sessionId: string; code: string } | { status: 'unknown'; sessionId: string }
  }
  'documents.close': { params: DocumentSessionParams; result: { closed: boolean; dirty: boolean } }

  /**
   * 设置项 —— 用户在插件详情页里拨的那些开关。
   *
   * ★ 只读,**没有 `configuration.set`**:设置是**用户**的意思,不是插件的。
   * 给了写入口之后,「我明明关掉了」会变成一个没人能解释的现象。
   */
  'configuration.get': { params: Record<string, never>; result: { values: Record<string, boolean | string | number> } }

  // 诊断 —— 插件自己往活动日志里写一条,便于作者排查
  'diagnostics.log': { params: { level: 'info' | 'warn' | 'error'; message: string }; result: Record<string, never> }

  /**
   * 运行中的工具推一条进度 / 一张实时卡片(第 2 层交互式)。
   *
   * ★ **无权限**:它只作用于**这个工具自己这次调用**(按 callId 定位),推不到别处。
   * message / card 都是**易失的**(不进转录),card 经 `sanitizeToolCard` 消毒后
   * 由宿主经 `tool_progress` 事件转发给渲染层。工具已结束或 callId 对不上时静默丢弃。
   */
  'tool.progress': { params: { callId: string; message?: string; card?: unknown }; result: Record<string, never> }

  // ───────── 第 5 层:插件间通信 ─────────

  /** 声明本插件对外导出的 API 方法名(无权限 —— 提供不是消费)。用于 UI/诊断与路由校验。 */
  'plugins.expose': { params: { methods: string[] }; result: Record<string, never> }
  /**
   * 调用另一个插件导出的 API。权限 `plugins` + 目标必须在本清单 `dependencies` 里。
   * 宿主做 broker:唤醒目标 → 转发到它的 `api.call` → 把返回值带回来。
   */
  'plugins.invoke': { params: { target: string; method: string; args: unknown[] }; result: { value: unknown } }
  /** 往一个 topic 广播事件。权限 `plugins`。宿主扇出给该 topic 的订阅者(不含自己)。 */
  'plugins.emitEvent': { params: { topic: string; payload: unknown }; result: Record<string, never> }
  /** 订阅 / 退订一个 topic。权限 `plugins`。 */
  'plugins.subscribeEvent': { params: { topic: string }; result: Record<string, never> }
  'plugins.unsubscribeEvent': { params: { topic: string }; result: Record<string, never> }
}

export type PluginMethod = keyof PluginMethodMap
export type PluginParams<M extends PluginMethod> = PluginMethodMap[M]['params']
export type PluginResult<M extends PluginMethod> = PluginMethodMap[M]['result']

/**
 * 运行时白名单。
 *
 * `satisfies Record<PluginMethod, PluginPermission | null>` 是**双向**约束,
 * 与 `shared/ipc/contract.ts` 的三张白名单同一手法:
 * - 方法表里加一个却忘了在这里给能力 → Record 缺 key,编译报错
 * - 这里多写一个 → 多余属性检查,编译报错
 *
 * 所以「每个方法都明确声明了它要哪条能力」是编译期可执行的保证。
 */
export const PLUGIN_METHOD_PERMISSION = {
  'env.appInfo': null,
  'env.openExternal': null,
  'env.clipboardRead': 'clipboard',
  'env.clipboardWrite': 'clipboard',

  'appearance.get': null,
  'appearance.subscribe': null,

  'permissions.contains': null,
  'permissions.request': null,
  'permissions.remove': null,

  'storage.get': 'storage',
  'storage.set': 'storage',
  'storage.keys': 'storage',

  'secrets.get': 'secrets',
  'secrets.set': 'secrets',

  'workspace.folders': 'workspace.read',
  'workspace.readFile': 'workspace.read',
  'workspace.writeFile': 'workspace.write',
  'workspace.deleteFile': 'workspace.write',
  'workspace.stat': 'workspace.read',
  'workspace.findFiles': 'workspace.read',

  'process.exec': 'process',
  'process.execStream': 'process',
  'process.execAbort': 'process',

  'net.fetch': 'net',

  'scm.status': 'scm.read',
  'scm.diff': 'scm.read',
  'scm.log': 'scm.read',
  'scm.branches': 'scm.read',
  'scm.stage': 'scm.write',
  'scm.commit': 'scm.write',
  'scm.createBranch': 'scm.write',
  'scm.checkout': 'scm.write',

  /*
    ★ `openWebApp` / `openView` 不挂能力:它们只能指向**这个插件自己清单里
    声明过的**条目(主进程逐条核对),而那份清单用户在安装时看过。
    `openBrowser` 收的是任意 URL,所以要能力 + 参数门两道。
  */
  'tabs.openWebApp': null,
  'tabs.openBrowser': 'tabs.browser',
  /*
    ★ 与 `process.exec` 同一条能力、同一份 `allowedCommands` 白名单:能被开进
    交互式终端的命令,没有理由比能一次性执行的更宽。两处门若各挂一份清单,
    「exec 批了、terminal 没批」这类不一致迟早出现。
  */
  'tabs.openTerminal': 'process',

  'workspace.subscribeChanges': 'workspace.read',
  'workspace.unsubscribeChanges': 'workspace.read',

  'window.showMessage': 'window.notify',
  'window.showQuickPick': null,
  'window.showInputBox': null,
  'window.showConfirm': null,
  'window.progressStart': null,
  'window.progressUpdate': null,
  'window.progressEnd': null,
  'window.setStatusBarItem': null,

  'commands.register': null,
  'commands.unregister': null,
  'commands.execute': null,
  'tools.register': null,
  'tools.unregister': null,

  'agent.registerInterceptor': 'agent.intercept',
  'agent.registerContextProvider': 'agent.context',

  'customEditors.register': 'workspace.read',
  'customEditors.setDirty': null,

  'documents.open': 'workspace.read',
  'documents.apply': 'workspace.write',
  'documents.save': 'workspace.write',
  'documents.export': 'workspace.write',
  'documents.getState': 'workspace.read',
  'documents.query': 'workspace.read',
  'documents.getOperation': 'workspace.read',
  'documents.close': 'workspace.read',

  /*
    读能力就够:它打开的是一个**只读到文件内容**的编辑器视图。
    真正的写发生在视图保存时,那条路由渲染层按 Tab 绑定的文件代发,
    并且会再过一次 `workspace:writeFile` 的既有校验。
  */
  'tabs.openCustomEditor': 'workspace.read',

  'configuration.get': null,

  'diagnostics.log': null,

  'tool.progress': null,

  'plugins.expose': null,
  'plugins.invoke': 'plugins',
  'plugins.emitEvent': 'plugins',
  'plugins.subscribeEvent': 'plugins',
  'plugins.unsubscribeEvent': 'plugins'
} satisfies Record<PluginMethod, PluginPermission | null>

export function isPluginMethod(value: unknown): value is PluginMethod {
  return typeof value === 'string' && Object.hasOwn(PLUGIN_METHOD_PERMISSION, value)
}

// ─────────────────────────── 报文 ───────────────────────────

export interface PluginRequest {
  id: number
  method: string
  params: unknown
}

export type PluginErrorCode =
  /** 方法不在白名单里 —— 拼错方法名会收到这个,而不是「没有权限」 */
  | 'unknown_method'
  /** 声明了但用户没批 / 清单里根本没声明 */
  | 'permission_denied'
  /** 参数门:路径越界、域名不在 hostPermissions、命令不在白名单 */
  | 'invalid_argument'
  /** 行为门:既有权限链拒了(用户点了拒绝,或规则命中 deny) */
  | 'rejected'
  | 'timeout'
  | 'aborted'
  /** 插件自己的代码抛了 */
  | 'plugin_error'
  /** 宿主侧出错 */
  | 'internal_error'
  /** 这一版还没实现(见 unsupported.ts) */
  | 'not_implemented'

export type PluginResponse =
  | { id: number; ok: true; data: unknown }
  | { id: number; ok: false; error: { code: PluginErrorCode; message: string } }

/** 宿主 → 插件的反向调用。插件侧用同一个 id 回一条 `PluginInvocationResult`。 */
export interface PluginInvocation {
  id: number
  kind:
    | 'activate'
    | 'deactivate'
    | 'tool.execute'
    | 'tool.abort'
    /** 用户点了实时卡片上的按钮 —— 送给仍在运行的那次工具调用(第 2 层) */
    | 'tool.action'
    | 'command.run'
    | 'event'
    | 'interceptor.willInvoke'
    | 'interceptor.didInvoke'
    | 'context.provide'
    | 'customEditor.load'
    | 'customEditor.save'
    /** 宿主 → 目标插件:调用它导出的 API 方法(第 5 层) */
    | 'api.call'
    /** 宿主 → 订阅者:投递一条插件事件(第 5 层) */
    | 'plugins.event'
  payload: unknown
}

export type PluginInvocationResult =
  | { id: number; ok: true; data: unknown }
  | { id: number; ok: false; error: { code: PluginErrorCode; message: string } }

export type PluginMessage =
  | { type: 'request'; request: PluginRequest }
  | { type: 'response'; response: PluginResponse }
  | { type: 'invoke'; invocation: PluginInvocation }
  | { type: 'invokeResult'; result: PluginInvocationResult }
  /** 插件宿主页面握手完成,告诉主进程它已经可以收 invocation 了 */
  | { type: 'ready'; pluginId: string }

// ─────────────────────────── 超时预算 ───────────────────────────

/**
 * 这几个数字与既有机制对齐,不是拍的:
 *
 * - `TOOL` 对齐 `mcp/bridge.ts` 的 `CALL_TOOL_TIMEOUT_MS`;
 * - `INTERCEPTOR` 对齐 `kernel/hook/run.ts` 的裁决超时,而且**超时 = 弃权**
 *   (fail-open,只记诊断)—— 一个卡住的插件不该把所有工具调用堵死;
 * - `ABORT_GRACE` 之后按工具失败返回,run 继续。
 */
export const PLUGIN_TIMEOUT = {
  ACTIVATE_MS: 10_000,
  TOOL_MS: 60_000,
  /**
   * 交互式工具的硬上限(第 2 层)——工具挂起等用户点按钮,60s 太短。
   *
   * ★ 这是**宿主的最后一道闸**(fork B):不信插件会自律地超时,取消随时可用
   * (= abort),但一个声明了 interactive 却永不返回的工具最多把这一轮钉 5 分钟。
   */
  INTERACTIVE_TOOL_MS: 5 * 60_000,
  INTERCEPTOR_MS: 3_000,
  CONTEXT_MS: 3_000,
  COMMAND_MS: 30_000,
  REQUEST_MS: 30_000,
  ABORT_GRACE_MS: 2_000
} as const

/** `agent.registerContextProvider` 的注入上限(计划 §14 第 4 条)。 */
export const PLUGIN_CONTEXT_LIMIT = {
  /** 单次注入的字符上限 */
  PER_CALL: 2000,
  /** 每轮所有插件加起来的字符上限 */
  PER_TURN: 8000
} as const

/** 单插件的 kv 配额。 */
export const PLUGIN_STORAGE_QUOTA_BYTES = 5 * 1024 * 1024
