/**
 * WindowRegistry —— 方案 §3 规则 5:事件按订阅**定向推送**,绝不广播。
 *
 * ⌥Space 快捷小窗不该收到主窗的 token 流。即使现在只有一个主窗,这也是**正确性问题**,
 * 不是多窗口特性 —— 等真开了第二个窗再来补,要动每一个 emit 点。
 *
 * 规则在这里是**类型级**的,不是约定:
 * - `emitToTopic` 只接受 TargetedEventChannel(run / 终端流)
 * - `emitTo`      也接受它 —— 窗口自身状态(最大化)是「一个窗口一份」,同样不能广播
 * - `emitToAll`   只接受 GlobalEventChannel(设置、主题、工作区列表这类真·全局变更)
 * 想拿 emitToAll 推 agent:event,编译期就过不去。
 */
import type { BrowserWindow, WebContents } from 'electron'
import type { EventChannel, IpcEventMap } from '../../shared/ipc/contract'
import type { WindowKind } from '../../shared/domain/tab'

/**
 * 窗口后端:注册表需要向 Electron 问两件事(焦点窗口、webContents 对应的窗口)。
 *
 * ★ 由**桌面入口**在启动时注入,而不是在这里 `import { BrowserWindow } from 'electron'`:
 *   那是值导入,会把 Electron 拖进每个只想广播一条全局事件的模块 —— 包括无头设备服务里复用的
 *   IPC 逻辑。没有后端(服务、测试)时窗口表本来就是空的,广播是空操作,这两个查询答「没有」。
 */
export interface WindowBackend {
  getFocusedWindow(): BrowserWindow | null
  fromWebContents(contents: WebContents): BrowserWindow | null
}
let windowBackend: WindowBackend | null = null
export function installWindowBackend(backend: WindowBackend | null): void {
  windowBackend = backend
}

/**
 * 必须定向推送的频道:一个窗口只该收到它自己在看的那个 run / 终端,
 * 以及**它自己的**窗口状态(`window:maximized` —— 主窗最大化了,
 * ⌥Space 快捷窗的还原按钮不该跟着换字形)。
 */
export type TargetedEventChannel =
  'agent:event' | 'terminal:data' | 'terminal:exit' | 'window:maximized' | 'window:visibility' | 'connection:auth' | 'documentEngine:changed'
/** 真·全局状态变更,所有窗口都该知道 */
export type GlobalEventChannel = Exclude<EventChannel, TargetedEventChannel>

export interface WindowContext {
  id: number
  kind: WindowKind
  sender: WebContents
}

export const runTopic = (runId: string): string => `run:${runId}`
export const terminalTopic = (terminalId: string): string => `term:${terminalId}`

class WindowRegistry {
  private readonly windows = new Map<number, WindowContext>()
  /** topic → 订阅了它的 webContents id */
  private readonly topics = new Map<string, Set<number>>()
  /**
   * 已经挂过 'destroyed' 的 webContents。
   *
   * ★ register 不是「一个窗口一次」:帧死掉时 `send` 会把这个窗口 forget 掉,
   * 而 webContents 本身还活着(⌘R 重载就是这样),重载完的 `window:ready`
   * 又会重新 register 一次。不去重的话每次重载多挂一个 once,十次之后
   * Node 开始报 MaxListenersExceededWarning。
   */
  private readonly destroyHooked = new WeakSet<WebContents>()

  /**
   * 应用自己的窗口(主窗 / ⌥Space 快捷窗)的 webContents。**只有窗口真的销毁时才移除。**
   *
   * ★ 和上面那张 `windows` 表的区别正是**移除时机**:`windows` 会被 `send()` 在
   * 「帧已经死了」的那一刻顺手 forget 掉(⌘R 重载就是这样,重载完 `window:ready`
   * 再 register 回来)。那对推送是对的 —— 一个收不到消息的订阅者就该摘掉;
   * 但「这扇窗还在不在」不能拿它来答:重载的那一瞬间会答成「一扇都没有」,
   * 于是点 Dock 又开出一扇一模一样的新窗、退出时这扇窗没人关只能等 6 秒兜底。
   */
  private readonly liveWindows = new Map<number, WindowContext>()

  register(sender: WebContents, kind: WindowKind): WindowContext {
    const ctx: WindowContext = { id: sender.id, kind, sender }
    this.windows.set(sender.id, ctx)
    this.liveWindows.set(sender.id, ctx)
    // 窗口销毁时把它从所有 topic 里摘掉,否则 topics 会无限增长,
    // 且每次 emit 都要对着一堆死 webContents 做 isDestroyed 判断
    if (!this.destroyHooked.has(sender)) {
      this.destroyHooked.add(sender)
      sender.once('destroyed', () => {
        this.liveWindows.delete(sender.id)
        this.forget(sender.id)
      })
    }
    return ctx
  }

  /** 握手第一步 window:ready 时调用,确认这个窗口的 kind */
  markReady(sender: WebContents, kind: WindowKind): WindowContext {
    const existing = this.windows.get(sender.id)
    if (existing) {
      existing.kind = kind
      return existing
    }
    return this.register(sender, kind)
  }

  /** handler 里拿到 event.sender 后换成上下文;未注册的一律当主窗 */
  of(sender: WebContents): WindowContext {
    return this.windows.get(sender.id) ?? this.register(sender, this.liveWindows.get(sender.id)?.kind ?? 'main')
  }

  forget(id: number): void {
    this.windows.delete(id)
    this.activeWorkspace.delete(id)
    for (const [topic, ids] of this.topics) {
      ids.delete(id)
      if (ids.size === 0) this.topics.delete(topic)
    }
  }

  // ─── 当前工作区 ───

  /**
   * 每个窗口**此刻停在**哪个工作区。由渲染层经 `workspace:setActive` 上报。
   *
   * ★ 和 `last_opened_at` 不是一回事:后者记的是「何时最后一次打开」,在已开的
   * 几个工作区之间切 Tab 不动它。需要「现在在哪」的地方(插件的路径类能力)
   * 用那个时间戳会把文件写进上一次打开的工作区。
   *
   * ★ 只在内存里,不落库:它描述的是这次运行中某个窗口的即时状态,重启之后
   * 由渲染层重新上报。落库反而要处理「上次关机时那条还在,但窗口还没起来」。
   */
  private readonly activeWorkspace = new Map<number, string>()

  setActiveWorkspace(windowId: number, workspaceId: string | null): void {
    if (workspaceId === null) this.activeWorkspace.delete(windowId)
    else this.activeWorkspace.set(windowId, workspaceId)
  }

  activeWorkspaceOf(windowId: number): string | undefined {
    return this.activeWorkspace.get(windowId)
  }

  /**
   * 焦点窗口报的那一条;没有焦点窗口(应用在后台)时退到**任意一个**有上报的
   * 主窗。返回 `undefined` = 没有任何窗口报过,调用方自己决定怎么兜底。
   */
  activeWorkspaceOfFocused(): string | undefined {
    const focused = windowBackend?.getFocusedWindow() ?? null
    if (focused !== null) {
      const reported = this.activeWorkspace.get(focused.webContents.id)
      if (reported !== undefined) return reported
    }
    for (const [id, ctx] of this.windows) {
      if (ctx.kind !== 'main') continue
      const reported = this.activeWorkspace.get(id)
      if (reported !== undefined) return reported
    }
    return undefined
  }

  // ─── 订阅 ───

  subscribe(topic: string, sender: WebContents): void {
    let set = this.topics.get(topic)
    if (!set) {
      set = new Set()
      this.topics.set(topic, set)
    }
    set.add(sender.id)
    this.of(sender)
  }

  unsubscribe(topic: string, sender: WebContents): void {
    const set = this.topics.get(topic)
    if (!set) return
    set.delete(sender.id)
    if (set.size === 0) this.topics.delete(topic)
  }

  /** 这个主题从此不会再有事件(run 已被回收):所有窗口的订阅一起摘掉,主题表不随 run 数增长 */
  dropTopic(topic: string): void {
    this.topics.delete(topic)
  }

  /**
   * 这个窗口不再看任何 run 的正文(最小化 / 隐藏)。
   *
   * ★ 只摘 run 主题,终端主题不动:终端流本来就只在终端 Tab 挂着时订阅,而它的
   * 回滚缓冲由主进程封顶;摘掉的话恢复可见时终端会缺一段输出,且没有补齐路径。
   * run 正文有 —— 窗口回来时按快照 + 历史重建(渲染层 `startAgentEventPump`)。
   */
  unsubscribeRuns(sender: WebContents): void {
    for (const [topic, ids] of this.topics) {
      if (!topic.startsWith('run:')) continue
      ids.delete(sender.id)
      if (ids.size === 0) this.topics.delete(topic)
    }
  }

  /**
   * 把 `from` 这个主题的订阅者**原样复制**给 `to`。子 run 的订阅就是这么来的。
   *
   * ★ 不做这一步的表现极其隐蔽:一切正常、没有任何报错、只是界面上什么都不发生。
   * 因为 `RunPump.flush()` 在 `!hasSubscribers(topic)` 时**整批丢弃**事件 ——
   * 子 run 有自己的 runId,于是有自己的主题,而没有任何窗口订阅过那个主题。
   *
   * 是**复制而不是别名**:父 run 结束后它的订阅可能被清掉,而子 run 还在跑;
   * 别名会让子 run 跟着一起失聪。代价是父 run 之后新增的订阅者
   * (⌘R 重载后重新 attach 的窗口)看不到子 run —— 那条路由
   * `attachRun` 会走 `snapshot().children`,不靠这里。
   */
  inherit(from: string, to: string): void {
    const src = this.topics.get(from)
    if (!src || src.size === 0) return
    let dst = this.topics.get(to)
    if (!dst) {
      dst = new Set()
      this.topics.set(to, dst)
    }
    for (const id of src) dst.add(id)
  }

  /** 一个 run / 终端还有没有人在看。没人看时可以停掉合批泵,但**不停 run 本身**。 */
  hasSubscribers(topic: string): boolean {
    return (this.topics.get(topic)?.size ?? 0) > 0
  }

  isSubscribed(topic: string, sender: WebContents): boolean {
    return this.topics.get(topic)?.has(sender.id) === true
  }

  /**
   * Bring the existing main window back when a background notification is clicked.
   *
   * ★ 只在**本注册表**里找,不用 `BrowserWindow.getAllWindows()`:后者还包含每个
   * 插件一扇的隐藏宿主窗(`plugin/host-window.ts`,`show:false`,页面是空壳)。
   * 主窗已经销毁的那几个时刻(退出第一段刚关掉它、`forceClose()` 之后)取「第一扇
   * 没销毁的窗」拿到的正是它,于是点一下通知弹出来的是**一扇纯白空窗**,
   * 而要唤回的界面根本没出现。`listWindows()` 只有主窗与 ⌥Space 快捷窗,且已滤掉已销毁的。
   */
  showMainWindow(): void {
    const [win] = this.listWindows('main')
    if (win === undefined) return
    if (win.isMinimized()) win.restore()
    win.show()
    win.focus()
  }

  // ─── 推送 ───

  emitToTopic<K extends TargetedEventChannel>(
    topic: string,
    channel: K,
    payload: IpcEventMap[K]
  ): void {
    const ids = this.topics.get(topic)
    if (!ids) return
    for (const id of ids) this.send(id, channel, payload)
  }

  emitToAll<K extends GlobalEventChannel>(channel: K, payload: IpcEventMap[K]): void {
    for (const id of this.windows.keys()) this.send(id, channel, payload)
  }

  emitTo<K extends EventChannel>(sender: WebContents, channel: K, payload: IpcEventMap[K]): void {
    this.send(sender.id, channel, payload)
  }

  /**
   * ★ 每次 send 前判 isDestroyed()(方案 §3 规则 4)。
   * 窗口刚关掉、事件泵还有一批在路上 —— 不判就是一个 "Object has been destroyed"。
   *
   * isDestroyed() 判的是 webContents,不是它当前的渲染帧:重载/关闭过程中
   * 帧可能已经先一步销毁,而 webContents 本身要等 'destroyed' 事件才翻转。
   * 这个窗口期必须**自己判帧**,不能指望 try/catch —— Electron 44 实测:
   *
   *   WebContents.send  = function (ch, ...a) { return this.mainFrame.send(ch, ...a) }
   *   WebFrameMain.send = function (ch, ...a) { try { return this._send(!1, ch, a) }
   *                                             catch (e) { console.error('Error sending from webFrameMain: ', e) } }
   *
   * 也就是说 "Render frame was disposed" 被 Electron **自己吞掉再打印**,
   * 外层一个 catch 都接不到。于是死掉的订阅者永远等不到 forget,run 每
   * flush 一次就往 stderr 刷一屏一模一样的栈,直到 'destroyed' 真的来。
   * 改成先问帧自己死没死:isDestroyed()/detached 读的是 C++ 侧的标志位,
   * 不走 CheckRenderFrame,所以它们**不抛**。死了就当这个窗口没了。
   *
   * 帧刚没的一瞬间连 `.mainFrame` 这个 getter 都会抛(消息里的
   * "before WebFrameMain could be accessed" 就是它),所以整段仍留在 try 里。
   */
  private send(id: number, channel: string, payload: unknown): void {
    const ctx = this.windows.get(id)
    if (!ctx) return
    if (ctx.sender.isDestroyed()) {
      this.forget(id)
      return
    }
    try {
      const frame = ctx.sender.mainFrame
      if (frame.isDestroyed() || frame.detached) {
        this.forget(id)
        return
      }
      frame.send(channel, payload)
    } catch {
      this.forget(id)
    }
  }

  list(): WindowContext[] {
    return [...this.windows.values()]
  }

  /**
   * 注册表里的窗口,取回 `BrowserWindow` —— 退出流程要关它们(`main/quit-flow.ts`),
   * 唤回路径要显示它们(`showMainWindow`)。
   *
   * ★ **插件宿主窗不在这里**,它们住在 `plugin/host-window.ts` 自己的表里,这是
   * 有意的:退出时不去关第三方代码的窗口,那等于让它的 `beforeunload` 有机会
   * 拖住退出,而它连我们自己的「有未保存的改动」对话框都调不出来。
   *
   * ★ 遍历的是 `liveWindows` 而不是 `windows`:后者会被 `send()` 在帧死掉的那一刻
   * forget 掉,而那一刻窗口其实还在(见 `liveWindows` 的注释)。原先遍历
   * `windows` 时,渲染层重载的那一瞬间这里会返回空数组。
   *
   * `fromWebContents` 可能返回 null(窗口刚销毁、上下文还留着),所以逐个判,
   * 不假设一定拿得到。
   */
  listWindows(kind?: WindowKind): BrowserWindow[] {
    const found: BrowserWindow[] = []
    for (const context of this.liveWindows.values()) {
      const { sender } = context
      if (sender.isDestroyed()) continue
      if (kind !== undefined && context.kind !== kind) continue
      const win = windowBackend?.fromWebContents(sender) ?? null
      if (win !== null && !win.isDestroyed()) found.push(win)
    }
    return found
  }
}

export const windows = new WindowRegistry()
