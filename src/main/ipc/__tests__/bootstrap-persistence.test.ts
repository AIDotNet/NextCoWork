/**
 * 首屏握手里的**刷盘失败**只报一句话,不顶掉首屏。
 *
 * ## 为什么值得单独一个文件
 *
 * `app:getBootstrap` 是渲染层拿到的第一份数据,而它在返回之前会顺手把 Tab 布局 /
 * 草稿的防抖冲一遍。这个「顺手」有两种坏法,都不是编译期能看见的:
 *
 * 1. **把刷盘失败当成握手失败** —— 磁盘满的用户会看到一屏「首屏握手失败」,
 *    而他真正该做的是去清磁盘;库本身好好的,应用完全能用。
 * 2. **写失败被静默吞掉,或者那份值被当成写过了** —— 「退出前把草稿刷一遍」
 *    是这条链路上唯一一次机会,吞掉异常就是把丢数据描述成保存成功。
 *
 * 所以这一组钉两件事:`app:getBootstrap` 在 `store.setKV` 抛错时仍然回一份完整
 * 首屏(只多一个 `persistenceWarning`),以及 `flushPendingPersists` 抛出原错误、
 * 那份没写进去的值**留在待落盘表里**等下一次。
 *
 * ★ 「多条失败收成 AggregateError」以及逐条会话草稿的收集,都在
 *   `store` 之下那一层(会话运行时的 `flush()`);这里从外面只看得到「抛了没有」
 *   和「键还在不在」。
 *
 * ## 为什么真的 `registerIpc()` 一次
 *
 * `handlers` 那张表是模块私有的 —— 从外面调不到 `app:getBootstrap` 那一条。
 * 而这一组测的正是**接线**(handler 里那段 try/catch),不是它的副本。
 * electron 由下面的 `vi.mock` 顶掉:`ipcMain.handle` / `ipcMain.on` 只把处理器
 * 记下来,存库走真的 `node:sqlite`(同 `db/__tests__` 那套)。没有窗口,
 * 所以 `windows.of()` 会登记一个假 sender —— 只为拿 `ctx.kind`。
 */
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Bootstrap } from '../../../shared/domain/bootstrap'
import type { IpcResult } from '../../../shared/ipc/contract'
import { EMPTY_OUTER, outerTabKey, store } from '../../state/store'
import { closeDatabase, openDatabase } from '../../db/index'

/**
 * 没有窗口的进程里,`windows.of()` 会把 sender 登记成一个主窗上下文 ——
 * 只为拿一个 `ctx.kind`。所以它得长成 `WindowRegistry.register` 能接受的样子
 * (`id` / `once` / `isDestroyed` / `send`),不是光有一个 id。
 */
const SENDER = {
  id: 1,
  once: () => undefined,
  isDestroyed: () => false,
  send: () => undefined
}

vi.mock('electron', () => {
  /** `ipcMain.handle` 登记进来的 invoke handler —— `registerIpc()` 之后从这里取。 */
  const handlers = new Map<string, (event: unknown, req: unknown) => Promise<unknown>>()
  const listeners = new Map<string, (event: unknown, payload: unknown) => void>()
  ;(globalThis as Record<string, unknown>).__ipcHandlers = handlers
  ;(globalThis as Record<string, unknown>).__ipcListeners = listeners
  return {
    app: {
      getPath: () => '/tmp/ncw-bootstrap-persistence-test',
      getVersion: () => '0.0.0',
      on: () => undefined,
      getAppPath: () => '/tmp'
    },
    ipcMain: {
      handle: (channel: string, fn: never) => handlers.set(channel, fn),
      on: (channel: string, fn: never) => listeners.set(channel, fn)
    },
    nativeTheme: { on: () => undefined, shouldUseDarkColors: false, themeSource: 'system' },
    BrowserWindow: class {},
    dialog: {},
    clipboard: {},
    shell: {},
    webUtils: {}
  }
})

/*
  ★ 浏览器的自动化桥在纯 Node 里不存在,而 `registerIpc()` 的中段会装它 ——
  不替掉的话,这一次注册会停在那句「runtime is unavailable」上,
  于是 `app:getBootstrap` 永远不会被登记。
*/
vi.mock('../../browser/runtime', () => ({
  getBrowserAutomationBridge: () => ({ reconcile: () => undefined, setCuaListener: () => undefined })
}))

/*
  ★ 这两张表要**现取**:`vi.mock` 的工厂是在 SUT 第一次 import 'electron' 时才跑的,
  而这个测试模块的顶层代码比那更早 —— 顶层直接读会拿到 undefined。
*/
type InvokeHandler = (event: unknown, req: unknown) => Promise<unknown>
type SendListener = (event: unknown, payload: unknown) => void

const invokeHandler = (channel: string): InvokeHandler | undefined =>
  (globalThis as Record<string, unknown>).__ipcHandlers instanceof Map
    ? ((globalThis as Record<string, unknown>).__ipcHandlers as Map<string, InvokeHandler>).get(channel)
    : undefined

const sendListener = (channel: string): SendListener | undefined =>
  (globalThis as Record<string, unknown>).__ipcListeners instanceof Map
    ? ((globalThis as Record<string, unknown>).__ipcListeners as Map<string, SendListener>).get(channel)
    : undefined

/**
 * handlers / listeners 收到的第一个参数是 `IpcMainInvokeEvent` ——
 * `safeHandle` 读的是它上面的 `sender`。所以这里给的是 `{ sender }`,
 * 不是 sender 本身(传错的表现是 `windows.of(undefined)` 里那句 reading 'id')。
 */
const EVENT = { sender: SENDER }

/** 走真 handler 的 `app:getBootstrap` —— 测的是接线,不是它的副本 */
function getBootstrap(): Promise<IpcResult<Bootstrap>> {
  const handler = invokeHandler('app:getBootstrap')
  if (handler === undefined) throw new Error('app:getBootstrap 没被登记 —— registerIpc() 挂在中途了')
  return handler(EVENT, undefined) as Promise<IpcResult<Bootstrap>>
}

/** 造一份**待落盘**的 Tab 布局:走 `tabs:persistOuter` 那条 send 频道(防抖 500ms) */
function persistPending(state: unknown): void {
  const listener = sendListener('tabs:persistOuter')
  if (listener === undefined) throw new Error('tabs:persistOuter 没被登记')
  listener(EVENT, { kind: 'main', state })
}

let dir = ''
let warn: ReturnType<typeof vi.spyOn>

beforeEach(async () => {
  closeDatabase()
  dir = mkdtempSync(join(tmpdir(), 'nextcowork-bootstrap-persistence-'))
  openDatabase(dir)
  // 每个用例都重跑一遍注册:`ipcMain.handle` 被替成了「往 Map 里写」,重复注册是幂等的
  const index = await import('../index')
  index.registerIpc()
  warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined)
})

afterEach(() => {
  warn.mockRestore()
  closeDatabase()
  rmSync(dir, { recursive: true, force: true })
})

describe('app:getBootstrap · 刷盘失败不顶掉首屏', () => {
  it('★ 待落盘的键写不进去时,首屏照样完整返回,只多一个 persistenceWarning', async () => {
    persistPending({ outer: [{ id: 'tab-1' }], activeOuterId: 'tab-1' })
    // 只让**写**失败;读首屏要用的那几把键照旧读得出来,所以它不可能是「读库失败」
    const setKv = vi.spyOn(store, 'setKv').mockImplementation(() => {
      throw new Error('disk full')
    })

    const result = await getBootstrap()
    setKv.mockRestore()

    expect(result.ok).toBe(true)
    if (!result.ok) return
    // 首屏的每一块都在 —— 这是「刷盘失败 ≠ 读库失败」的另一半
    expect(result.data.windowKind).toBe('main')
    expect(result.data.settings).toBeDefined()
    expect(result.data.versions.app).toBe('0.0.0')
    expect(result.data.persistenceWarning).toBe(true)
    // 失败只记一条日志,不是一条异常
    expect(warn).toHaveBeenCalled()
    expect(String(warn.mock.calls[0]?.[0])).toContain('bootstrap')
  })

  it('★ 没失败就不带这个字段 —— 别让每个正常启动都背上一条假警告', async () => {
    persistPending({ outer: [], activeOuterId: null })

    const result = await getBootstrap()

    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.data.persistenceWarning).toBeUndefined()
    // 正常这一路真的把待落盘那份写进去了
    expect(store.getKv(outerTabKey('main'), EMPTY_OUTER)).toEqual({ outer: [], activeOuterId: null })
  })
})

describe('flushPendingPersists · 失败照旧抛,值留在表里', () => {
  it('★ 写失败时抛原错误,而那份没写进去的值留在待落盘表里等下一次', async () => {
    const { flushPendingPersists } = await import('../index')
    persistPending({ outer: [], activeOuterId: null })

    const real = store.setKv.bind(store)
    let failNext = true
    const setKv = vi.spyOn(store, 'setKv').mockImplementation((key, value) => {
      if (failNext) {
        failNext = false
        throw new Error('disk full')
      }
      real(key, value)
    })

    // 错必须**照旧抛**:显式保存 / 退出这条路上,静默吞掉等于把丢数据说成成功。
    let thrown = 'none'
    try { flushPendingPersists() } catch (error) { thrown = error instanceof Error ? error.message : String(error) }
    setKv.mockRestore()

    // ……而键还在表里,下一次 flush 就补上了 —— 这才是「失败保留」的意义
    const retry = vi.spyOn(store, 'setKv')
    flushPendingPersists()
    const calls = retry.mock.calls.length
    retry.mockRestore()
    expect({ thrown, calls }).toEqual({ thrown: 'disk full', calls: 1 })
  })
})
