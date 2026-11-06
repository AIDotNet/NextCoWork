/**
 * 闸门放行判据的时序测试。
 *
 * 这一条判据错的唯一形态是「渲染层比主进程启动快」—— 起一次 Electron 也未必撞得上,
 * 所以这里把几种启动时序直接摆出来:哪些情况下**不能**挂 App(挂了就是
 * 「首屏握手失败: No handler registered for 'app:getBootstrap'」)。
 */
import { describe, expect, it } from 'vitest'
import type { MigrationState } from '../../../../shared/domain/data-migration'
import { decideMigrationGate } from '../migration-release'

/** 只写关心的那几位,其余按「什么都没发生」补全。 */
function state(over: Partial<MigrationState>): MigrationState {
  return {
    phase: 'idle',
    steps: [],
    completed: [],
    current: null,
    ratio: null,
    failure: null,
    merged: null,
    undoAvailable: false,
    startupFailure: null,
    ipcReady: false,
    ...over
  }
}

describe('decideMigrationGate', () => {
  it('★ 主进程还没跑完 registerIpc 时,idle 显示骨架但不放行 App', () => {
    expect(decideMigrationGate(state({ phase: 'idle', ipcReady: false }), false, false)).toBe('waiting')
  })

  it('首份快照未到时显示骨架，超时后显示启动诊断', () => {
    expect(decideMigrationGate(null, false, false)).toBe('waiting')
    expect(decideMigrationGate(null, false, true)).toBe('startup-failed')
  })

  it('idle 等不到 IPC 就绪时从骨架切到启动诊断', () => {
    expect(decideMigrationGate(state({ phase: 'idle', ipcReady: false }), false, true)).toBe('startup-failed')
  })

  it('主进程在就绪前报告启动异常时立即显示诊断', () => {
    expect(decideMigrationGate(state({
      phase: 'running',
      startupFailure: 'database is corrupt'
    }), false, false)).toBe('startup-failed')
  })

  it('主进程能应答之后直接放行，并忽略之前的超时或陈旧错误', () => {
    expect(decideMigrationGate(state({ phase: 'idle', ipcReady: true }), false, true)).toBe('app')
    expect(decideMigrationGate(state({
      phase: 'idle',
      ipcReady: true,
      startupFailure: 'late background failure'
    }), false, false)).toBe('app')
  })

  it('迁移进行中照常画那一屏,与主进程能不能应答无关', () => {
    expect(decideMigrationGate(state({ phase: 'running', ipcReady: false }), false, true)).toBe('gate')
    expect(decideMigrationGate(state({ phase: 'running', ipcReady: true }), false, false)).toBe('gate')
  })

  it('失败页照常画;「跳过并继续」在主进程能应答之前点了也不生效', () => {
    expect(decideMigrationGate(state({ phase: 'failed', ipcReady: false }), false, false)).toBe('gate')
    expect(decideMigrationGate(state({ phase: 'failed', ipcReady: false }), true, false)).toBe('gate')
    expect(decideMigrationGate(state({ phase: 'failed', ipcReady: true }), true, false)).toBe('app')
  })

  it('「已跳过」要用户确认；确认后等待 IPC，超时则显示诊断', () => {
    expect(decideMigrationGate(state({ phase: 'skipped', ipcReady: false }), false, true)).toBe('gate')
    expect(decideMigrationGate(state({ phase: 'skipped', ipcReady: false }), true, false)).toBe('waiting')
    expect(decideMigrationGate(state({ phase: 'skipped', ipcReady: false }), true, true)).toBe('startup-failed')
    expect(decideMigrationGate(state({ phase: 'skipped', ipcReady: true }), true, false)).toBe('app')
  })
})
