/**
 * 切账户 = 关掉插件系统、再起一个新的 `ElectronPluginRuntime`。
 *
 * 症状(这条用例存在的原因):登录时 `prepareAccountSwitch → startPlugins` 抛
 * `Attempted to register a second handler for 'plugin:rpc'`,整条登录失败 —— 旧实例的
 * IPC 接线从没人摘。真 Electron 对同一频道第二次 `handle` 就是直接抛,下面的假 ipcMain 照抄这一点。
 */
import { describe, expect, it, vi } from 'vitest'

const ipc = vi.hoisted(() => {
  const handlers = new Map<string, unknown>()
  const listeners = new Map<string, Set<unknown>>()
  return {
    handlers,
    listeners,
    ipcMain: {
      handle(channel: string, fn: unknown): void {
        if (handlers.has(channel)) throw new Error(`Attempted to register a second handler for '${channel}'`)
        handlers.set(channel, fn)
      },
      removeHandler(channel: string): void { handlers.delete(channel) },
      on(channel: string, fn: unknown): void {
        if (!listeners.has(channel)) listeners.set(channel, new Set())
        listeners.get(channel)?.add(fn)
      },
      removeListener(channel: string, fn: unknown): void { listeners.get(channel)?.delete(fn) }
    }
  }
})

vi.mock('electron', () => ({ ipcMain: ipc.ipcMain, BrowserWindow: class {} }))
vi.mock('../protocol', () => ({ setPluginRootResolver: () => undefined, PLUGIN_HOST_PARTITION: 'persist:test' }))

import { ElectronPluginRuntime } from '../host-window'

describe('plugin runtime IPC wiring', () => {
  it('can be constructed again after a restart without double-registering its channels', () => {
    const handle = vi.fn()
    new ElectronPluginRuntime(handle, '/preload.js')
    expect(() => new ElectronPluginRuntime(handle, '/preload.js')).not.toThrow()

    expect(ipc.handlers.size).toBe(1)
    // 监听器不叠层:每个频道只剩最新那个实例的一份
    for (const set of ipc.listeners.values()) expect(set.size).toBe(1)
  })
})
