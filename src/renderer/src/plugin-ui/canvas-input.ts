/**
 * 办公画布的输入换算与发送队列 —— `DocumentCanvas.tsx` 的纯逻辑部分。
 *
 * ## 为了什么需求建的
 *
 * 画布把 DOM 的键盘 / 鼠标 / 输入法事件换成引擎事件交给文档会话(计划 §7.2)。
 * 两件事若写在组件里就只能起 DOM 测,而它们都是看不见的回归:
 *
 * - **鼠标键位换算。** DOM 的 `buttons` 位是 左1 / 右2 / 中4,LibreOffice 是 左1 / 中2 / 右4;
 *   原样透传的表现是右键菜单变成了中键粘贴。
 * - **发送队列。** 引擎一次只处理一批,而打字、拖选会在一次往返里产生几十个事件。
 *   每个事件各发一次请求,回执乱序、引擎排队;全部并成一批又会让组字的中间态丢失。
 *   这里的规则:同一时刻只有一批在途;在途期间攒下的事件并成下一批;连续的组字更新只留
 *   最后一个(中间态没有意义),连续的鼠标移动、连续的可见区域只留最后一个。
 *
 * ## 故意不做的
 *
 * - 不决定哪些键留给宿主(关标签等):iframe 里的按键本来就到不了宿主。
 */
import { lokKeyOf, LOK_MODIFIER, type DomKeyLike, type KeyPlatform } from '../../../shared/document-engine/keys'
import type { DocumentInputEvent } from '../../../shared/document-engine/interaction'

/** DOM `MouseEvent.buttons` → LibreOffice 的按键位(见文件头) */
export function lokButtons(domButtons: number): number {
  let out = 0
  if ((domButtons & 1) !== 0) out |= 1
  if ((domButtons & 4) !== 0) out |= 2
  if ((domButtons & 2) !== 0) out |= 4
  return out
}

/** DOM `MouseEvent.button`(单个键:0 左 / 1 中 / 2 右)→ LibreOffice 按键位 */
export function lokButtonOf(domButton: number): number {
  if (domButton === 1) return 2
  if (domButton === 2) return 4
  return 1
}

/** 鼠标事件的修饰位:与键盘同一套(SHIFT / MOD1 / MOD2 / MOD3) */
export function lokMouseModifier(event: { shiftKey: boolean; ctrlKey: boolean; altKey: boolean; metaKey: boolean }, platform: KeyPlatform): number {
  let out = 0
  if (event.shiftKey) out |= LOK_MODIFIER.SHIFT
  if (platform === 'mac' ? event.metaKey : event.ctrlKey) out |= LOK_MODIFIER.MOD1
  if (event.altKey) out |= LOK_MODIFIER.MOD2
  if (platform === 'mac' && event.ctrlKey) out |= LOK_MODIFIER.MOD3
  return out
}

/** 一次按键 → 引擎事件;不该交给引擎时返回空数组(调用方据此决定要不要 preventDefault) */
export function keyEvent(event: DomKeyLike, action: 'press' | 'release', platform: KeyPlatform): DocumentInputEvent[] {
  const key = lokKeyOf(event, platform)
  if (key === null) return []
  return [{ type: 'key', action, charCode: key.charCode, keyCode: key.keyCode }]
}

/** `navigator.platform` / userAgent → 主修饰键在哪个平台约定上 */
export function keyPlatformOf(platform: string): KeyPlatform {
  return /mac|iphone|ipad/i.test(platform) ? 'mac' : 'other'
}

/**
 * 把新事件并进待发批次。连续的组字更新、连续的鼠标移动只留最后一个(见文件头)。
 * ★ 只合并**相邻**的同类事件:组字更新之间夹着一次提交时,两边都必须保留,
 *   否则提交会落在错误的组字内容上。
 */
export function mergeInto(queue: DocumentInputEvent[], next: readonly DocumentInputEvent[]): void {
  for (const event of next) {
    const last = queue[queue.length - 1]
    const replaceable =
      last !== undefined &&
      ((event.type === 'text' && event.action === 'compose' && last.type === 'text' && last.action === 'compose' && event.text !== '') ||
        (event.type === 'mouse' && event.action === 'move' && last.type === 'mouse' && last.action === 'move') ||
        (event.type === 'viewport' && last.type === 'viewport'))
    if (replaceable) queue[queue.length - 1] = event
    else queue.push(event)
  }
}

/**
 * 一次只让一批在途的发送队列。`send` 失败不会卡住队列:错误交给 `onError`,后面的事件照发。
 * `flush([])`(空批次)用来拉取输入之后才到的迟到事件(Agent 修改后的失效区等)。
 */
export class InputQueue<R> {
  private pending: DocumentInputEvent[] = []
  /** 有没有人要一次空批次拉取 */
  private pull = false
  private busy = false
  private closed = false

  constructor(
    private readonly send: (events: DocumentInputEvent[]) => Promise<R>,
    private readonly onResult: (result: R) => void,
    private readonly onError: (error: unknown) => void
  ) {}

  push(events: readonly DocumentInputEvent[]): void {
    if (this.closed) return
    if (events.length === 0) this.pull = true
    else mergeInto(this.pending, events)
    this.pump()
  }

  close(): void {
    this.closed = true
    this.pending = []
    this.pull = false
  }

  private pump(): void {
    if (this.busy || this.closed || (this.pending.length === 0 && !this.pull)) return
    const batch = this.pending
    this.pending = []
    // 一批非空输入的回执本身就带迟到事件,不必再单独拉一次
    this.pull = false
    this.busy = true
    this.send(batch).then(
      (result) => { if (!this.closed) this.onResult(result) },
      (error: unknown) => { if (!this.closed) this.onError(error) }
    ).finally(() => {
      this.busy = false
      this.pump()
    })
  }
}

/**
 * 补拉迟到事件的时刻(ms)。
 * ★ LibreOffice 的重绘失效与**功能区状态**都在空闲时才发,晚于那一批回执。实测(本机,LibreOffice 26.8):
 *   改了模型后的失效约晚一拍;移动光标 / 点到别的单元格之后,加粗、字体等状态约晚 0.7 s;
 *   打开文档后第一批状态约晚 1.5 s(演示约 0.7 s)。只在「改了模型」之后补拉的话,移动光标后
 *   按钮停在上一个位置的状态,打开时字体框是空的 —— 直到用户随便改一下什么。
 */
export const PULL_AFTER_INPUT_MS: readonly number[] = [250, 1000, 2000]
export const PULL_AFTER_OPEN_MS: readonly number[] = [500, 1500, 3000]

/**
 * 迟到事件的补拉排程。每次 `schedule` 取消上一轮未到的补拉再重排:连续打字 / 拖选只在停下后补拉,
 * 不会堆积请求。补拉本身不改模型也不动光标,不会让调用方再排一轮,所以不会连环触发。
 */
export class LatePulls {
  private timers: ReturnType<typeof setTimeout>[] = []

  constructor(private readonly pull: () => void) {}

  schedule(delays: readonly number[]): void {
    this.cancel()
    this.timers = delays.map((ms) => setTimeout(this.pull, ms))
  }

  cancel(): void {
    for (const timer of this.timers) clearTimeout(timer)
    this.timers = []
  }
}

/**
 * 这份回执之后要不要补拉:改了模型,或光标 / 选区 / 当前部分动了(状态会在之后到)。
 * ★ 不看 `cursorVisible` 与 `states`:它们在补拉的回执里也会出现,看它们会让补拉一直续下去。
 */
export function needsLatePull(result: { modified: boolean; cursor?: unknown; cellCursor?: unknown; selection?: unknown; part?: unknown }): boolean {
  return result.modified || result.cursor !== undefined || result.cellCursor !== undefined || result.selection !== undefined || result.part !== undefined
}
