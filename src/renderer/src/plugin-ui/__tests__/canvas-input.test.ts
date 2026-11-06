/**
 * 办公画布的输入换算与发送队列(`canvas-input.ts`)。
 *
 * 钉住的需求:右键不变中键;同一时刻一批在途、在途时攒批;组字中间态与鼠标移动只留最新;
 * 组字 → 提交 → 组字不被合并错位;发送失败不卡队列;空批次用来拉迟到事件。
 * 迟到事件的补拉:光标动了也要补拉(功能区状态在引擎空闲时才到),重排不堆积,补拉的回执不续补拉。
 */
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { DocumentInputEvent } from '../../../../shared/document-engine/interaction'
import { InputQueue, LatePulls, PULL_AFTER_INPUT_MS, PULL_AFTER_OPEN_MS, keyEvent, keyPlatformOf, lokButtonOf, lokButtons, lokMouseModifier, mergeInto, needsLatePull } from '../canvas-input'

const compose = (text: string): DocumentInputEvent => ({ type: 'text', action: 'compose', text })
const move = (x: number): DocumentInputEvent => ({ type: 'mouse', action: 'move', x, y: 0, count: 1, buttons: 1, modifier: 0 })

describe('mouse and key mapping', () => {
  it('swaps the middle and right button bits between DOM and LibreOffice', () => {
    expect(lokButtons(1)).toBe(1)
    expect(lokButtons(2)).toBe(4)
    expect(lokButtons(4)).toBe(2)
    expect(lokButtons(7)).toBe(7)
    expect([lokButtonOf(0), lokButtonOf(1), lokButtonOf(2)]).toEqual([1, 2, 4])
  })

  it('uses Cmd as the primary modifier on macOS and Ctrl elsewhere for clicks', () => {
    const base = { shiftKey: false, ctrlKey: false, altKey: false, metaKey: false }
    expect(lokMouseModifier({ ...base, metaKey: true }, 'mac')).toBe(0x2000)
    expect(lokMouseModifier({ ...base, ctrlKey: true }, 'mac')).toBe(0x8000)
    expect(lokMouseModifier({ ...base, ctrlKey: true, shiftKey: true }, 'other')).toBe(0x3000)
    expect(keyPlatformOf('MacIntel')).toBe('mac')
    expect(keyPlatformOf('Win32')).toBe('other')
  })

  it('turns a key into a press or release event and drops keys the engine must not see', () => {
    const z = { key: 'z', code: 'KeyZ', shiftKey: false, ctrlKey: true, altKey: false, metaKey: false }
    expect(keyEvent(z, 'press', 'other')).toEqual([{ type: 'key', action: 'press', charCode: 0, keyCode: 537 | 0x2000 }])
    expect(keyEvent({ ...z, ctrlKey: false, key: 'Shift', code: 'ShiftLeft', shiftKey: true }, 'release', 'other')).toEqual([])
  })
})

describe('mergeInto', () => {
  it('keeps only the latest of consecutive visible areas', () => {
    const area = (y: number): DocumentInputEvent => ({ type: 'viewport', x: 0, y, width: 100, height: 100 })
    const queue: DocumentInputEvent[] = []
    mergeInto(queue, [area(0), area(10), area(20)])
    expect(queue).toEqual([area(20)])
    mergeInto(queue, [move(1), area(30)])
    expect(queue).toEqual([area(20), move(1), area(30)])
  })

  it('keeps only the latest of adjacent composition updates and pointer moves', () => {
    const queue: DocumentInputEvent[] = []
    mergeInto(queue, [compose('z'), compose('zh'), compose('zho')])
    mergeInto(queue, [move(1), move(2)])
    expect(queue).toEqual([compose('zho'), move(2)])
  })

  it('never folds a composition across a commit, and keeps an empty compose (a cancel) as its own event', () => {
    const queue: DocumentInputEvent[] = []
    mergeInto(queue, [compose('ni'), { type: 'text', action: 'commit', text: '你' }, compose('h'), compose('ha')])
    mergeInto(queue, [compose('')])
    expect(queue).toEqual([compose('ni'), { type: 'text', action: 'commit', text: '你' }, compose('ha'), compose('')])
  })
})

describe('InputQueue', () => {
  function deferred(): { sent: DocumentInputEvent[][]; resolve: () => void; reject: () => void; queue: InputQueue<number>; results: number[]; errors: unknown[] } {
    const sent: DocumentInputEvent[][] = []
    const waiters: { resolve: (v: number) => void; reject: (e: Error) => void }[] = []
    const results: number[] = []
    const errors: unknown[] = []
    const queue = new InputQueue<number>(
      (events) => { sent.push(events); return new Promise((resolve, reject) => { waiters.push({ resolve, reject }) }) },
      (result) => { results.push(result) },
      (error) => { errors.push(error) }
    )
    return {
      sent, results, errors, queue,
      resolve: () => { waiters.shift()?.resolve(sent.length) },
      reject: () => { waiters.shift()?.reject(new Error('boom')) }
    }
  }
  const tick = (): Promise<void> => new Promise((resolve) => { setTimeout(resolve, 0) })
  const key = (c: string): DocumentInputEvent => ({ type: 'key', action: 'press', charCode: c.charCodeAt(0), keyCode: 0 })

  it('keeps one batch in flight and sends what piled up meanwhile as the next batch', async () => {
    const q = deferred()
    q.queue.push([key('a')])
    q.queue.push([key('b')])
    q.queue.push([key('c')])
    expect(q.sent).toEqual([[key('a')]])
    q.resolve()
    await tick()
    expect(q.sent).toEqual([[key('a')], [key('b'), key('c')]])
    q.resolve()
    await tick()
    expect(q.results).toEqual([1, 2])
  })

  it('sends an empty batch to pull late events, but not on top of a real batch that pulls them anyway', async () => {
    const q = deferred()
    q.queue.push([])
    expect(q.sent).toEqual([[]])
    q.queue.push([])
    q.queue.push([key('x')])
    q.resolve()
    await tick()
    expect(q.sent).toEqual([[], [key('x')]])
  })

  it('reports a failed batch and keeps sending the next one', async () => {
    const q = deferred()
    q.queue.push([key('a')])
    q.queue.push([key('b')])
    q.reject()
    await tick()
    expect(q.errors).toHaveLength(1)
    expect(q.sent).toEqual([[key('a')], [key('b')]])
  })

  it('drops everything after close', async () => {
    const q = deferred()
    q.queue.push([key('a')])
    q.queue.close()
    q.queue.push([key('b')])
    q.resolve()
    await tick()
    expect(q.sent).toEqual([[key('a')]])
    expect(q.results).toEqual([])
  })
})

describe('late pulls', () => {
  afterEach(() => { vi.useRealTimers() })

  it('pulls at every scheduled delay, and a reschedule replaces the pending round instead of stacking', () => {
    vi.useFakeTimers()
    const pulls: number[] = []
    const late = new LatePulls(() => { pulls.push(Date.now()) })
    const start = Date.now()
    late.schedule(PULL_AFTER_INPUT_MS)
    vi.advanceTimersByTime(100)
    late.schedule(PULL_AFTER_INPUT_MS) // 连续打字:重排
    vi.advanceTimersByTime(5000)
    expect(pulls.map((t) => t - start)).toEqual(PULL_AFTER_INPUT_MS.map((ms) => ms + 100))
    late.schedule(PULL_AFTER_OPEN_MS)
    late.cancel()
    vi.advanceTimersByTime(10_000)
    expect(pulls).toHaveLength(PULL_AFTER_INPUT_MS.length)
  })

  it('waits long enough for the states the engine sends when it goes idle', () => {
    // 实测:移动光标后约 0.7 s、打开后约 1.5 s(见 PULL_AFTER_INPUT_MS 的说明)。最后一次补拉要留出余量
    expect(Math.max(...PULL_AFTER_INPUT_MS)).toBeGreaterThanOrEqual(1500)
    expect(Math.max(...PULL_AFTER_OPEN_MS)).toBeGreaterThanOrEqual(3000)
  })

  it('asks for a late pull when the model changed or the cursor, selection or part moved, not for a quiet pull', () => {
    expect(needsLatePull({ modified: true })).toBe(true)
    expect(needsLatePull({ modified: false, cursor: null })).toBe(true)
    expect(needsLatePull({ modified: false, cellCursor: { x: 0, y: 0, width: 1, height: 1 } })).toBe(true)
    expect(needsLatePull({ modified: false, selection: [] })).toBe(true)
    expect(needsLatePull({ modified: false, part: 1 })).toBe(true)
    // 补拉的回执:只有补发的状态 / 光标可见性 —— 不能再续一轮,否则补拉永不停
    expect(needsLatePull({ modified: false })).toBe(false)
  })
})
