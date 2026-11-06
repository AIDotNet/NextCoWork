import { describe, expect, it } from 'vitest'
import { assistantMessage, userMessage } from '../../../shared/agent/message'
import { DEFAULT_SETTINGS, type AppSettings } from '../../../shared/domain/settings'
import type { Session } from '../../../shared/domain/session'
import { SuggestionJobs, createComposerIpc, type ComposerIpcDeps, type ComposerWindow } from '../composer'

function deferred(): { promise: Promise<string>; resolve: (v: string) => void } {
  let resolve!: (v: string) => void
  const promise = new Promise<string>((r) => { resolve = r })
  return { promise, resolve }
}

function win(id: number): ComposerWindow & { destroy: () => void } {
  const listeners: Array<() => void> = []
  return {
    id,
    sender: { once: (_event, listener) => { listeners.push(listener) } },
    destroy: () => { for (const l of listeners) l() }
  }
}

describe('SuggestionJobs', () => {
  it('同一会话同一轮:两个等待者合流,只跑一次', async () => {
    const jobs = new SuggestionJobs()
    const d = deferred()
    let runs = 0
    const work = (): Promise<string> => { runs += 1; return d.promise }
    const a = jobs.run('s1', 100, '1:aaaaaaaa', work)
    const b = jobs.run('s1', 100, '2:bbbbbbbb', work)
    d.resolve('跑一下测试')
    expect(await a).toBe('跑一下测试')
    expect(await b).toBe('跑一下测试')
    expect(runs).toBe(1)
  })

  it('新的一轮让旧的作废:旧等待者拿到空串、旧请求被 abort', async () => {
    const jobs = new SuggestionJobs()
    let firstSignal: AbortSignal | undefined
    const first = jobs.run('s1', 100, '1:aaaaaaaa', (signal) => { firstSignal = signal; return new Promise(() => {}) })
    const second = jobs.run('s1', 200, '1:bbbbbbbb', () => Promise.resolve('继续'))
    expect(firstSignal?.aborted).toBe(true)
    expect(await second).toBe('继续')
    // 旧的那份永远不会 resolve —— 只验证它被 abort 了;等待者拿到什么由 settle 决定
    void first
  })

  it('最后一个等待者取消才真正 abort', async () => {
    const jobs = new SuggestionJobs()
    let signal: AbortSignal | undefined
    const d = deferred()
    const a = jobs.run('s1', 100, '1:aaaaaaaa', (s) => { signal = s; return d.promise })
    const b = jobs.run('s1', 100, '2:bbbbbbbb', () => d.promise)
    jobs.cancel('1:aaaaaaaa')
    expect(signal?.aborted).toBe(false)
    jobs.cancel('2:bbbbbbbb')
    expect(signal?.aborted).toBe(true)
    d.resolve('x')
    expect(await a).toBe('')
    expect(await b).toBe('')
  })

  it('窗口关掉:它的等待者一起摘掉', async () => {
    const jobs = new SuggestionJobs()
    let signal: AbortSignal | undefined
    const pending = jobs.run('s1', 100, '7:aaaaaaaa', (s) => { signal = s; return new Promise(() => {}) })
    jobs.dropWindow(7)
    expect(signal?.aborted).toBe(true)
    void pending
  })
})

type Overrides = Partial<Omit<ComposerIpcDeps, 'settings' | 'session'>> & { prefs?: Partial<AppSettings>; session?: Partial<Session> }

function setup(over: Overrides = {}): {
  ipc: ReturnType<typeof createComposerIpc>
  suggested: unknown[]
  completed: unknown[]
} {
  const suggested: unknown[] = []
  const completed: unknown[] = []
  const session: Session = { id: 's1', workspaceId: 'ws', title: 'T', createdAt: 1, updatedAt: 1, mode: 'code', ...over.session } as Session
  const { prefs, session: _s, ...rest } = over
  const ipc = createComposerIpc({
    settings: () => ({ ...DEFAULT_SETTINGS, ...prefs }),
    session: (id) => (id === session.id ? session : undefined),
    hasActiveGoal: () => false,
    recentSessionPrompts: () => [userMessage('u1', [{ type: 'text', text: '修一下，然后跑测试' }], 10)],
    lastVisibleAssistantMessage: () => assistantMessage('a1', [{ type: 'text', text: '修好了，测试还没跑。' }], 20),
    recentWorkspacePrompts: () => ['跑一下测试'],
    suggest: async (input) => { suggested.push(input); return '跑一下测试' },
    complete: async (input) => { completed.push(input); return '上单元测试' },
    logger: { warn: () => {} },
    ...rest
  })
  return { ipc, suggested, completed }
}

const SUGGEST = { requestId: 'req-00000001', workspaceId: 'ws', sessionId: 's1', triggerKey: 100, model: 'm', modelProviderId: 'p' }

describe('composer:suggestNext', () => {
  it('跟随输入框的模型,不换家', async () => {
    const { ipc, suggested } = setup()
    expect(await ipc.suggestNext(SUGGEST, win(1))).toEqual({ requestId: 'req-00000001', text: '跑一下测试' })
    expect(suggested[0]).toMatchObject({ model: 'm', modelProviderId: 'p', sessionId: 's1', workspaceId: 'ws' })
  })

  it('设置里点名了模型就用它(整对)', async () => {
    const { ipc, suggested } = setup({ prefs: { completionModel: 'cheap', completionModelProviderId: 'p2' } })
    await ipc.suggestNext(SUGGEST, win(1))
    expect(suggested[0]).toMatchObject({ model: 'cheap', modelProviderId: 'p2' })
  })

  it.each([
    ['开关关着', { prefs: { promptSuggestions: false } }],
    ['plan 模式', { session: { mode: 'plan' } }],
    ['子代理会话', { session: { parentSessionId: 'parent' } }],
    ['Skill 提炼会话', { session: { skillSource: { sessionId: 'src' } } }],
    ['别的工作区', { session: { workspaceId: 'other' } }],
    ['目标还挂着', { hasActiveGoal: () => true }],
    ['还没有回复', { lastVisibleAssistantMessage: () => undefined }]
  ] as const)('%s → 不请求', async (_name, over) => {
    const { ipc, suggested } = setup(over as never)
    expect((await ipc.suggestNext(SUGGEST, win(1))).text).toBe('')
    expect(suggested).toHaveLength(0)
  })

  it('畸形的 requestId 直接返回空', async () => {
    const { ipc, suggested } = setup()
    expect((await ipc.suggestNext({ ...SUGGEST, requestId: 'x' }, win(1))).text).toBe('')
    expect(suggested).toHaveLength(0)
  })
})

describe('composer:history', () => {
  it('本地补全关着时返回空', () => {
    expect(setup().ipc.history({ workspaceId: 'ws' }).prompts).toEqual(['跑一下测试'])
    expect(setup({ prefs: { localCompletion: false } }).ipc.history({ workspaceId: 'ws' }).prompts).toEqual([])
  })
})

describe('composer:complete', () => {
  const COMPLETE = { requestId: 'req-00000002', workspaceId: 'ws', sessionId: 's1', draft: '给 X 补', context: { project: 'P', files: ['a.ts'], commands: [] } }
  const ON = { prefs: { inlineCompletion: true, inlineCompletionModel: 'fast' } }

  it('开关和点名的模型缺一不可', async () => {
    expect((await setup({ prefs: { inlineCompletion: true } }).ipc.complete(COMPLETE, win(1))).text).toBe('')
    expect((await setup({ prefs: { inlineCompletionModel: 'fast' } }).ipc.complete(COMPLETE, win(1))).text).toBe('')
    const { ipc, completed } = setup(ON)
    expect((await ipc.complete(COMPLETE, win(1))).text).toBe('上单元测试')
    expect(completed[0]).toMatchObject({ model: 'fast', sessionId: 's1', draft: '给 X 补' })
  })

  it('同一个槽里新请求让旧的立刻作废', async () => {
    const signals: AbortSignal[] = []
    const { ipc } = setup({
      ...ON,
      complete: (_input, signal) => { signals.push(signal); return new Promise<string>((resolve) => { signal.addEventListener('abort', () => resolve('')) }) }
    })
    const w = win(1)
    const first = ipc.complete(COMPLETE, w)
    void ipc.complete({ ...COMPLETE, requestId: 'req-00000003' }, w)
    expect(signals[0]?.aborted).toBe(true)
    expect((await first).text).toBe('')
  })

  it('取消与窗口销毁都会 abort 在途的续写', async () => {
    const signals: AbortSignal[] = []
    const { ipc } = setup({ ...ON, complete: (_input, signal) => { signals.push(signal); return new Promise(() => {}) } })
    const w = win(3)
    void ipc.complete(COMPLETE, w)
    ipc.cancel({ requestId: 'req-00000002' }, w)
    expect(signals[0]?.aborted).toBe(true)
    void ipc.complete({ ...COMPLETE, requestId: 'req-00000004' }, w)
    w.destroy()
    expect(signals[1]?.aborted).toBe(true)
  })

  it('草稿为空或超长不请求', async () => {
    const { ipc, completed } = setup(ON)
    await ipc.complete({ ...COMPLETE, draft: '' }, win(1))
    await ipc.complete({ ...COMPLETE, draft: 'x'.repeat(2001) }, win(1))
    expect(completed).toHaveLength(0)
  })
})
