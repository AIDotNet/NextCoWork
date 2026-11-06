import { afterEach, describe, expect, it, vi } from 'vitest'
import { TerminalManager, TERMINAL_INPUT_MAX, type OpenedTerminal, type TerminalEvent, type TerminalProcess } from '../terminals'

class FakeProcess implements TerminalProcess {
  written: string[] = []
  size = { cols: 0, rows: 0 }
  killed = false
  private data: Array<(data: string) => void> = []
  private exit: Array<(event: { exitCode: number }) => void> = []
  write(data: string): void { this.written.push(data) }
  resize(cols: number, rows: number): void { this.size = { cols, rows } }
  kill(): void { this.killed = true; this.finish(137) }
  onData(listener: (data: string) => void) { this.data.push(listener); return { dispose: () => {} } }
  onExit(listener: (event: { exitCode: number }) => void) { this.exit.push(listener); return { dispose: () => {} } }
  output(text: string): void { for (const listener of this.data) listener(text) }
  finish(code: number): void { for (const listener of this.exit) listener({ exitCode: code }) }
}

function setup(over: Partial<ConstructorParameters<typeof TerminalManager>[0]> = {}) {
  const processes: FakeProcess[] = []
  let n = 0
  let now = 1000
  const manager = new TerminalManager({
    open: async () => {
      const process = new FakeProcess(); processes.push(process)
      return { process, cwd: '/ws', shell: 'zsh', title: 'ws' } satisfies OpenedTerminal
    },
    newId: () => `term-${++n}`, now: () => now, ...over
  })
  return { manager, processes, advance: (ms: number) => { now += ms } }
}

afterEach(() => { vi.useRealTimers() })

describe('terminal manager', () => {
  it('batches output into sequenced events and lets a late attacher read the buffer, then continue contiguously', async () => {
    vi.useFakeTimers()
    const { manager, processes } = setup()
    const info = await manager.create('ws', 100, 30)
    processes[0]!.output('he'); processes[0]!.output('llo')
    const early: TerminalEvent[] = []
    const a = manager.attach(info.id, (event) => early.push(event))
    // attach 先冲掉合批里的输出:快照完整,订阅之后才有增量。
    expect(a).toMatchObject({ data: 'hello', seq: 1, truncated: false })
    processes[0]!.output('!')
    vi.advanceTimersByTime(20)
    expect(early).toEqual([{ kind: 'data', seq: 2, chunk: '!' }])
    const late = manager.attach(info.id, () => {})
    expect(late.data).toBe('hello!')
    expect(late.seq).toBe(2)
    a.dispose()
  })

  it('keeps only the last 256KiB and says so, and flushes oversized batches immediately', async () => {
    vi.useFakeTimers()
    const { manager, processes } = setup()
    const info = await manager.create('ws', 80, 24)
    const events: TerminalEvent[] = []
    manager.attach(info.id, (event) => events.push(event))
    processes[0]!.output('x'.repeat(70 * 1024)) // 超过单批上限:不等定时器
    expect(events).toHaveLength(1)
    for (let i = 0; i < 5; i++) processes[0]!.output('y'.repeat(70 * 1024))
    const snapshot = manager.attach(info.id, () => {})
    expect(snapshot.data.length).toBe(256 * 1024)
    expect(snapshot.truncated).toBe(true)
  })

  it('publishes exit as the next sequence number and refuses input afterwards', async () => {
    vi.useFakeTimers()
    const { manager, processes } = setup()
    const info = await manager.create('ws', 80, 24)
    const events: TerminalEvent[] = []
    manager.attach(info.id, (event) => events.push(event))
    processes[0]!.output('bye')
    processes[0]!.finish(3)
    expect(events).toEqual([{ kind: 'data', seq: 1, chunk: 'bye' }, { kind: 'exit', seq: 2, code: 3 }])
    expect(manager.list('ws')[0]).toMatchObject({ alive: false, exitCode: 3 })
    expect(() => manager.write(info.id, 'x')).toThrowError(expect.objectContaining({ code: 'conflict' }))
    expect(() => manager.resize(info.id, 10, 10)).toThrowError(expect.objectContaining({ code: 'conflict' }))
  })

  it('forwards input, resize and kill, and bounds the input size', async () => {
    const { manager, processes } = setup()
    const info = await manager.create('ws', 80, 24)
    manager.write(info.id, 'ls\n')
    manager.resize(info.id, 120, 40)
    expect(processes[0]!.written).toEqual(['ls\n'])
    expect(processes[0]!.size).toEqual({ cols: 120, rows: 40 })
    expect(manager.list('ws')[0]).toMatchObject({ cols: 120, rows: 40 })
    expect(() => manager.write(info.id, '')).toThrowError(expect.objectContaining({ code: 'resource_limit' }))
    expect(() => manager.write(info.id, 'a'.repeat(TERMINAL_INPUT_MAX + 1))).toThrowError(expect.objectContaining({ code: 'resource_limit' }))
    expect(() => manager.write('missing', 'x')).toThrowError(expect.objectContaining({ code: 'not_found' }))
    manager.kill(info.id)
    expect(processes[0]!.killed).toBe(true)
  })

  it('enforces per-workspace and total limits, counting terminals still being opened', async () => {
    const { manager } = setup({ maxPerWorkspace: 2, maxTotal: 3 })
    await manager.create('a', 80, 24); await manager.create('a', 80, 24)
    await expect(manager.create('a', 80, 24)).rejects.toMatchObject({ code: 'resource_limit' })
    await manager.create('b', 80, 24)
    await expect(manager.create('c', 80, 24)).rejects.toMatchObject({ code: 'resource_limit' })
  })

  it('does not count a failed open and does not leak a session for it', async () => {
    let fail = true
    const { manager } = setup({
      open: async () => { if (fail) throw new Error('spawn failed'); return { process: new FakeProcess(), cwd: '/w', shell: 'sh', title: 't' } },
      maxTotal: 1
    })
    await expect(manager.create('a', 80, 24)).rejects.toThrow('spawn failed')
    fail = false
    await expect(manager.create('a', 80, 24)).resolves.toBeTruthy()
    expect(manager.list('a')).toHaveLength(1)
  })

  it('reaps exited terminals after their retention time, but never a running one', async () => {
    const { manager, processes, advance } = setup({ exitedTtlMs: 1000 })
    const dead = await manager.create('ws', 80, 24)
    const live = await manager.create('ws', 80, 24)
    processes[0]!.finish(0)
    advance(999)
    expect(manager.list('ws').map((t) => t.id)).toEqual([dead.id, live.id])
    advance(2)
    expect(manager.list('ws').map((t) => t.id)).toEqual([live.id])
  })

  it('a throwing subscriber cannot break the terminal or other subscribers; shutdown kills everything', async () => {
    vi.useFakeTimers()
    const { manager, processes } = setup()
    const info = await manager.create('ws', 80, 24)
    const seen: TerminalEvent[] = []
    manager.attach(info.id, () => { throw new Error('boom') })
    manager.attach(info.id, (event) => seen.push(event))
    processes[0]!.output('ok')
    vi.advanceTimersByTime(20)
    expect(seen).toHaveLength(1)
    manager.shutdown()
    expect(processes[0]!.killed).toBe(true)
    expect(manager.list('ws')).toEqual([])
  })
})
