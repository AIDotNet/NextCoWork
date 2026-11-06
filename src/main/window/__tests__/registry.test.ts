/**
 * 帧(WebFrameMain)先于 webContents 死掉的那个窗口期,必须**立刻** forget。
 *
 * ★ 这里的关键不是「send 抛了要接住」,而是**它根本不抛**:Electron 44 的
 * `WebContents.send` 直接转给 `mainFrame.send`,后者自己 try/catch 住
 * "Render frame was disposed" 再 `console.error` 出来。所以这些 fake 的
 * `send` 一律**不抛** —— 让它抛就等于把真实环境里唯一会出问题的那条路
 * 从测试里抹掉了(线上表现:同一个死订阅者每 flush 一次刷一屏栈)。
 */
import { describe, expect, it, vi } from 'vitest'
import type { BrowserWindow, WebContents } from 'electron'
import { installWindowBackend, runTopic, windows } from '../registry'

// 注册表不再导入 Electron,窗口后端由入口注入。
// listWindows 靠 fromWebContents 把 webContents 换回窗口;假 sender 自带一扇假窗,
// 没带的(上面那几个只测推送的)换不回来,于是自然不出现在结果里。
installWindowBackend({
  getFocusedWindow: () => null,
  fromWebContents: (sender: WebContents) => ((sender as unknown as { window?: BrowserWindow }).window ?? null)
})

/** Electron 语义的替身:帧死了以后 send 静默无效,判死靠 mainFrame 自己。 */
function fakeSender(id: number, frame: unknown): {
  id: number
  isDestroyed: () => boolean
  mainFrame: unknown
  once: () => void
} {
  return {
    id,
    isDestroyed: () => false,
    get mainFrame() {
      if (frame instanceof Error) throw frame
      return frame
    },
    once: () => {}
  }
}

const envelope = (seq: number): { runId: string; seq: number; events: [] } => ({
  runId: 'r',
  seq,
  events: []
})

describe('WindowRegistry.send', () => {
  it('帧还活着:照常投递', () => {
    const send = vi.fn()
    const topic = runTopic('alive')
    windows.subscribe(topic, fakeSender(1, { isDestroyed: () => false, detached: false, send }) as never)

    windows.emitToTopic(topic, 'agent:event', envelope(1))
    windows.emitToTopic(topic, 'agent:event', envelope(2))
    expect(send).toHaveBeenCalledTimes(2)
  })

  it('帧已销毁(webContents 还没翻转)时一次都不发,并且立刻 forget', () => {
    const send = vi.fn()
    const topic = runTopic('disposed')
    windows.subscribe(topic, fakeSender(2, { isDestroyed: () => true, detached: false, send }) as never)

    windows.emitToTopic(topic, 'agent:event', envelope(1))
    expect(send).not.toHaveBeenCalled()
    // forget 掉了 —— 泵这时会看到「没人订阅」,于是整批丢弃而不是继续空转。
    expect(windows.hasSubscribers(topic)).toBe(false)
  })

  it('帧 detached(导航把它挤掉了)同样当死处理', () => {
    const send = vi.fn()
    const topic = runTopic('detached')
    windows.subscribe(topic, fakeSender(3, { isDestroyed: () => false, detached: true, send }) as never)

    windows.emitToTopic(topic, 'agent:event', envelope(1))
    expect(send).not.toHaveBeenCalled()
    expect(windows.hasSubscribers(topic)).toBe(false)
  })

  it('连 .mainFrame 这个 getter 都抛的时候也要 forget', () => {
    const topic = runTopic('getter-throws')
    const boom = new Error('Render frame was disposed before WebFrameMain could be accessed')
    windows.subscribe(topic, fakeSender(4, boom) as never)

    expect(() => windows.emitToTopic(topic, 'agent:event', envelope(1))).not.toThrow()
    expect(windows.hasSubscribers(topic)).toBe(false)
  })
})

/**
 * 「这扇窗还在不在」与「它还收不收得到消息」是两个问题,答案来自两张表。
 * 混用的症状:⌘R 重载的那一瞬间点 Dock,应用**再开一扇一模一样的窗**
 * (唤回路径以为一扇都没有了);退出时这扇窗也没人关,只能等 6 秒兜底。
 */
describe('WindowRegistry.listWindows', () => {
  function registeredWindow(id: number, kind: 'main' | 'quick') {
    const window = { isDestroyed: () => false, isMinimized: () => false, show: vi.fn(), focus: vi.fn(), restore: vi.fn() }
    let destroyed = (): void => {}
    const sender = {
      ...fakeSender(id, { isDestroyed: () => false, detached: false, send: vi.fn() }),
      window,
      once: (_event: string, handler: () => void) => { destroyed = handler }
    }
    windows.register(sender as never, kind)
    return { window, sender, destroy: () => destroyed() }
  }

  it('唤回主窗口时不会选中更早注册的专注会话窗口', () => {
    const quick = registeredWindow(20, 'quick')
    const main = registeredWindow(21, 'main')
    try {
      expect(windows.listWindows('quick')).toEqual([quick.window])
      expect(windows.listWindows('main')).toEqual([main.window])
      windows.showMainWindow()
      expect(main.window.show).toHaveBeenCalledOnce()
      expect(main.window.focus).toHaveBeenCalledOnce()
      expect(quick.window.show).not.toHaveBeenCalled()
    } finally {
      quick.destroy()
      main.destroy()
    }
  })

  it('专注窗口重载后、重新握手之前仍保留 quick 身份', () => {
    const quick = registeredWindow(22, 'quick')
    try {
      windows.forget(quick.sender.id)
      expect(windows.listWindows('quick')).toEqual([quick.window])
      expect(windows.listWindows('main')).toEqual([])
      expect(windows.of(quick.sender as never).kind).toBe('quick')
      expect(windows.markReady(quick.sender as never, 'quick').kind).toBe('quick')
      expect(windows.listWindows('quick')).toEqual([quick.window])
    } finally {
      quick.destroy()
    }
  })

  it('markReady 更新身份时存活窗口的筛选结果同步更新', () => {
    const quick = registeredWindow(23, 'quick')
    try {
      windows.markReady(quick.sender as never, 'main')
      expect(windows.listWindows('quick')).toEqual([])
      expect(windows.listWindows('main')).toEqual([quick.window])
    } finally {
      quick.destroy()
    }
  })

  it('★ keeps a reloading window listed after send() forgot its dead frame', () => {
    const window = { isDestroyed: () => false }
    let destroyed = (): void => {}
    const sender = {
      id: 10,
      isDestroyed: () => false,
      window,
      // 重载中的窗口:帧已经死了,webContents 还活着。
      mainFrame: { isDestroyed: () => true, detached: false, send: () => {} },
      once: (_event: string, handler: () => void) => {
        destroyed = handler
      }
    }
    windows.register(sender as never, 'main')
    const topic = runTopic('reloading')
    windows.subscribe(topic, sender as never)

    windows.emitToTopic(topic, 'agent:event', envelope(1))
    // 推送这一侧照旧摘掉它 —— 一个收不到消息的订阅者就该摘掉。
    expect(windows.hasSubscribers(topic)).toBe(false)
    // 但窗口本身还在,唤回和退出关窗都必须看得见它。
    expect(windows.listWindows()).toEqual([window])
    expect(windows.listWindows('main')).toEqual([window])
    expect(windows.listWindows('quick')).toEqual([])

    destroyed()
    expect(windows.listWindows()).toEqual([])
    expect(windows.listWindows('main')).toEqual([])
  })
})
