import { act, createElement, type ComponentProps } from 'react'
import { createRoot } from 'react-dom/client'
import { JSDOM } from 'jsdom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { assistantMessage, toolResultMessage, userMessage } from '../../../../../shared/agent/message'
import { emptyTranscript } from '../../../../../shared/agent/transcript'
import { makeQueuedInput } from '../../../../../shared/domain/queued-input'
import { DEFAULT_WORKSPACE_SETTINGS, type Workspace } from '../../../../../shared/domain/workspace'
import type { SessionPage } from '../../../../../shared/domain/session'
import { I18nProvider } from '../../../i18n'
import { pickAttachments, uploadFile } from '../../../services/attachment'
import { AttachmentTray } from '../AttachmentTray'
import { useModelsStore } from '../../../stores/models'
import { releaseSession, sessionStore } from '../../../stores/session'
import { ChatView } from '../ChatView'
import type { Composer } from '../Composer'
import * as content from '../thread-content'

vi.mock('../../../services/app', async (importOriginal) => ({
  ...await importOriginal<typeof import('../../../services/app')>(),
  getInnerTabs: vi.fn(async () => ({ tabs: [], activeTabId: null })),
  persistInnerTabs: vi.fn(),
  persistOuterTabs: vi.fn(),
  getSessionInput: vi.fn(async () => null),
  persistSessionDraft: vi.fn(),
  updateWorkspace: vi.fn()
}))
vi.mock('../../../services/attachment', () => ({
  listSessionAttachments: vi.fn(async () => []),
  pickAttachments: vi.fn(async () => [{ kind: 'path', path: '/workspace/fixture.txt', name: 'fixture.txt' }]),
  removeAttachment: vi.fn(),
  uploadFile: vi.fn()
}))
vi.mock('../Composer', () => ({
  Composer: (props: ComponentProps<typeof Composer>) => createElement('div', null,
    createElement('input', { 'data-testid': 'draft', readOnly: true, value: props.draft }),
    createElement('span', { 'data-testid': 'skillify-available' }, String(props.onSkillifyCommand !== undefined)),
    createElement('button', { 'data-testid': 'attach', onClick: props.onPickAttachment }),
    createElement('button', { 'data-testid': 'attach-unknown-mime', onClick: () => props.onAttachFiles?.([
      new File(['pixels'], 'photo.png', { type: 'application/octet-stream' })
    ]) }),
    createElement('button', { 'data-testid': 'attach-without-path', onClick: () => props.onAttachFiles?.([
      new File(['text'], 'document.txt', { type: 'text/plain' })
    ]) }),
    createElement('output', null, props.attachments?.length ?? 0),
    createElement('span', { 'data-testid': 'attachment-errors' }, props.attachments?.map((item) => item.error ?? '').join('|')),
    createElement(AttachmentTray, { items: props.attachments ?? [], onRemove: props.onRemoveAttachment ?? (() => {}), onRetry: props.onRetryAttachment ?? (() => {}) }))
}))

describe('conversation loading feedback', () => {
  const sessionId = 'loading-feedback'
  const tabId = 'loading-feedback-tab'
  const workspace: Workspace = { id: 'workspace', name: 'Workspace', rootPath: '/workspace',
    createdAt: 1, lastOpenedAt: 1, settings: { ...DEFAULT_WORKSPACE_SETTINGS } }
  const page = (id = sessionId, empty = false): SessionPage => ({
    session: { id, workspaceId: workspace.id, title: 'History', model: '', mode: 'code', thinking: 'auto',
      rootPathAtCreation: '/workspace', status: 'idle', archived: false, favorited: false, createdAt: 1, updatedAt: 2 },
    messages: empty ? [] : [
      userMessage(`${id}-user`, [{ type: 'text', text: `Question ${id}` }], 1),
      assistantMessage(`${id}-answer`, [{ type: 'text', text: `Answer ${id}` }], 2)
    ],
    hasMore: false, messageRuns: {}, runModel: {}, runUsage: {}
  })
  const loadPage = vi.fn<() => Promise<SessionPage>>()
  let resolve!: (value: SessionPage) => void
  let reject!: (error: Error) => void
  let dom: JSDOM
  let root: ReturnType<typeof createRoot>
  let container: HTMLElement
  let models: ReturnType<typeof useModelsStore.getState>

  beforeEach(() => {
    loadPage.mockReset().mockReturnValueOnce(new Promise((res, rej) => { resolve = res; reject = rej }))
    dom = new JSDOM('<html data-theme-motion="off"><body><div id="root"></div></body></html>', { url: 'http://localhost' })
    Object.assign(dom.window, { nextcowork: {
      on: () => () => {},
      invoke: async (channel: string) => ({ ok: true, data: channel === 'shell:listBackground' ? [] : channel === 'sessions:getPage' ? await loadPage()
        : channel === 'sessions:getSummary' ? { session: page().session, messageCount: 2 }
          : channel === 'agent:listInteractions' ? [] : undefined })
    } })
    vi.stubGlobal('window', dom.window)
    vi.stubGlobal('document', dom.window.document)
    vi.stubGlobal('HTMLElement', dom.window.HTMLElement)
    vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
    vi.stubGlobal('requestAnimationFrame', () => 1)
    vi.stubGlobal('cancelAnimationFrame', () => {})
    vi.stubGlobal('ResizeObserver', class { observe(): void {} disconnect(): void {} })
    container = document.getElementById('root')!
    root = createRoot(container)
    models = useModelsStore.getState()
    useModelsStore.setState({ loaded: true })
  })

  afterEach(async () => {
    await act(async () => root.unmount())
    for (const id of [sessionId, 'loading-next', tabId]) releaseSession(id)
    resolve(page())
    await Promise.resolve()
    useModelsStore.setState(models, true)
    dom.window.close()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  const show = async (id: string | null = sessionId, readOnly = false): Promise<void> => {
    await act(async () => root.render(createElement(I18nProvider, { initialLocale: 'en-US', children:
      createElement(ChatView, { key: id ?? tabId, sessionId: id, tabId, workspace, readOnly, fallbackModel: { model: '' } }) })))
  }

  it.each([false, true])('shows history skeleton until the page arrives, readOnly=%s', async (readOnly) => {
    await show(sessionId, readOnly)
    const skeleton = container.querySelector('[data-testid="thread-skeleton"]')
    expect(skeleton?.getAttribute('aria-busy')).toBe('true')
    expect(skeleton?.textContent).toContain('Loading conversation…')
    expect(container.querySelector('h1')).toBeNull()
    const draft = container.querySelector('[data-testid="draft"]')
    expect(draft === null).toBe(readOnly)
    if (!readOnly) await act(async () => sessionStore(sessionId).getState().setDraft('Keep typing'))

    await act(async () => resolve(page()))
    expect(container.querySelector('[data-testid="thread-skeleton"]')).toBeNull()
    expect(container.textContent).toContain(`Answer ${sessionId}`)
    expect(container.querySelector('h1')).toBeNull()
    if (!readOnly) {
      expect(container.querySelector('[data-testid="draft"]')).toBe(draft)
      expect((draft as HTMLInputElement).value).toBe('Keep typing')
    }
  })

  it('keeps a pending active conversation in the loading layout', async () => {
    await show()
    await act(async () => sessionStore(sessionId).setState({ activeRunId: 'restoring' }))
    expect(container.querySelector('[data-testid="thread-skeleton"]')).not.toBeNull()
    expect(container.querySelector('h1')).toBeNull()
  })

  it('shows the welcome screen immediately for drafts and after confirming an empty saved session', async () => {
    await show(null)
    expect(container.querySelector('h1')).not.toBeNull()
    expect(container.querySelector('[data-testid="thread-skeleton"]')).toBeNull()
    loadPage.mockReturnValueOnce(Promise.resolve(page(sessionId, true)))
    await show()
    expect(container.querySelector('h1')).not.toBeNull()
    expect(container.querySelector('[data-testid="thread-skeleton"]')).toBeNull()
  })

  it('replaces a failed load with a retry action and restores the conversation on retry', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {})
    await show()
    await act(async () => reject(new Error('Read failed')))
    expect(container.querySelector('[data-testid="thread-load-error"]')?.textContent).toContain('Unable to load this conversation')
    expect(container.querySelector('[data-testid="thread-skeleton"]')).toBeNull()
    expect(container.querySelector('h1')).toBeNull()

    let finish!: (value: SessionPage) => void
    loadPage.mockReturnValueOnce(new Promise((res) => { finish = res }))
    await act(async () => (container.querySelector('[data-testid="thread-load-error"] button') as HTMLButtonElement).click())
    expect(container.querySelector('[data-testid="thread-skeleton"]')).not.toBeNull()
    await act(async () => finish(page()))
    expect(container.querySelector('[data-testid="thread-load-error"]')).toBeNull()
    expect(container.textContent).toContain(`Answer ${sessionId}`)
  })

  it('keeps the selected conversation loading when the previous conversation resolves late', async () => {
    await show()
    let finish!: (value: SessionPage) => void
    loadPage.mockReturnValueOnce(new Promise((res) => { finish = res }))
    await show('loading-next')
    await act(async () => resolve(page()))
    expect(container.querySelector('[data-testid="thread-skeleton"]')).not.toBeNull()
    expect(container.textContent).not.toContain(`Answer ${sessionId}`)
    await act(async () => finish(page('loading-next')))
    expect(container.querySelector('[data-testid="thread-skeleton"]')).toBeNull()
    expect(container.textContent).toContain('Answer loading-next')
  })
})

/** 需求:元数据未知、正在运行和提炼会话都不能露出必然失败的 /skillify 入口。 */
describe('Skillify command availability', () => {
  it.each([false, true])('waits for source metadata and guards extraction=%s', async (extraction) => {
    const dom = new JSDOM('<div id="root"></div>', { url: 'http://localhost' })
    let resolveMetadata!: (value: unknown) => void
    const metadata = new Promise((resolve) => { resolveMetadata = resolve })
    Object.assign(dom.window, { nextcowork: {
      on: () => () => {},
      // 元数据走 `sessions:getSummary`,转录按页走 `sessions:getPage` —— 两条都等同一份「库里的会话」
      invoke: async (channel: string) => ({ ok: true, data: channel === 'shell:listBackground' ? [] : channel === 'sessions:getSummary' || channel === 'sessions:getPage'
        ? { ...(await metadata as object), hasMore: false, messageCount: 1 }
        : channel === 'agent:listInteractions' ? [] : undefined })
    } })
    vi.stubGlobal('window', dom.window)
    vi.stubGlobal('document', dom.window.document)
    vi.stubGlobal('HTMLElement', dom.window.HTMLElement)
    vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
    vi.stubGlobal('requestAnimationFrame', () => 1)
    vi.stubGlobal('cancelAnimationFrame', () => {})
    vi.stubGlobal('ResizeObserver', class { observe(): void {} disconnect(): void {} })
    const container = document.getElementById('root')!
    const root = createRoot(container)
    const models = useModelsStore.getState()
    useModelsStore.setState({ loaded: true })
    const sessionId = `skillify-availability-${String(extraction)}`
    const session = sessionStore(sessionId)
    const messages = [userMessage('user', [{ type: 'text', text: 'Question' }], 1)]
    session.setState({ transcript: { ...emptyTranscript(), status: 'done', messages } })
    const workspace: Workspace = { id: 'workspace', name: 'Workspace', rootPath: '/workspace',
      createdAt: 1, lastOpenedAt: 1, settings: { ...DEFAULT_WORKSPACE_SETTINGS } }
    const available = (): string | null => container.querySelector('[data-testid="skillify-available"]')?.textContent ?? null
    const banner = (): Element | null => container.querySelector('[data-testid="skill-extraction-banner"]')
    const bannerToggle = (): HTMLButtonElement | null => container.querySelector('[data-testid="skill-extraction-banner-toggle"]')
    const bannerDetails = (): Element | null => container.querySelector('[data-testid="skill-extraction-banner-details"]')
    try {
      await act(async () => root.render(createElement(I18nProvider, { initialLocale: 'en-US', children:
        createElement(ChatView, { sessionId, tabId: 'fixture-tab', workspace, fallbackModel: { model: '' } }) })))
      expect(available()).toBe('false')
      expect(banner()).toBeNull()
      await act(async () => resolveMetadata({ messages, session: { id: sessionId, workspaceId: 'workspace', model: '', mode: 'code',
        title: extraction ? '源会话标题' : '', createdAt: 1_700_000_000_000,
        ...(extraction ? { skillSource: { sessionId: 'source' } } : {}) } }))
      expect(available()).toBe(String(!extraction))
      // 需求:提炼会话的说明卡贴在触发消息下方,能看到源会话标题;普通会话不该出现这张卡。
      if (extraction) {
        expect(banner()).not.toBeNull()
        expect(banner()?.textContent).toContain('Source conversation:')
        expect(banner()?.textContent).toContain('源会话标题')
      } else {
        expect(banner()).toBeNull()
      }
      await act(async () => session.setState({ activeRunId: 'active' }))
      expect(available()).toBe('false')
      await act(async () => session.setState({ activeRunId: null }))
      expect(available()).toBe(String(!extraction))
      // 需求:点击卡片展开完整信息(源会话 ID / 消息数等),收起态不占篇幅。
      if (extraction) {
        expect(bannerToggle()?.getAttribute('aria-expanded')).toBe('false')
        expect(bannerDetails()?.hasAttribute('hidden')).toBe(true)
        await act(async () => bannerToggle()?.click())
        expect(bannerToggle()?.getAttribute('aria-expanded')).toBe('true')
        expect(bannerDetails()?.hasAttribute('hidden')).toBe(false)
        expect(banner()?.textContent).toContain('Source session ID')
        expect(banner()?.textContent).toContain('source')
        expect(banner()?.textContent).toContain('1')
        await act(async () => bannerToggle()?.click())
        expect(bannerToggle()?.getAttribute('aria-expanded')).toBe('false')
        expect(bannerDetails()?.hasAttribute('hidden')).toBe(true)
      }
    } finally {
      resolveMetadata({ messages: [], session: { id: sessionId, model: '', mode: 'code' } })
      await act(async () => root.unmount())
      releaseSession(sessionId)
      useModelsStore.setState(models, true)
      dom.window.close()
      vi.unstubAllGlobals()
    }
  })
})

describe('chat history subscription boundary', () => {
  it('skips history work for draft, attachment and same-length queue edits', async () => {
    const dom = new JSDOM('<div id="root"></div>', { url: 'http://localhost' })
    Object.assign(dom.window, { nextcowork: {
      getPathForFile: () => '',
      on: () => () => {},
      invoke: async (channel: string) => ({ ok: true, data: channel === 'shell:listBackground' ? [] : channel === 'agent:listInteractions' ? []
        : channel === 'goal:get' ? undefined
          : { messages: [], session: { id: 'history-boundary', workspaceId: 'workspace', model: '', mode: 'code' } } })
    } })
    vi.stubGlobal('window', dom.window)
    vi.stubGlobal('document', dom.window.document)
    vi.stubGlobal('HTMLElement', dom.window.HTMLElement)
    vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
    vi.stubGlobal('requestAnimationFrame', () => 1)
    vi.stubGlobal('cancelAnimationFrame', () => {})
    vi.stubGlobal('ResizeObserver', class { observe(): void {} disconnect(): void {} })
    const container = document.getElementById('root')!
    const root = createRoot(container)
    const models = useModelsStore.getState()
    useModelsStore.setState({ loaded: true })
    const session = sessionStore('history-boundary')
    const messages = [
      userMessage('user', [{ type: 'text', text: 'Question' }], 1),
      assistantMessage('answer', [{ type: 'text', text: 'Answer' }], 2)
    ]
    session.setState({ transcript: { ...emptyTranscript(), status: 'done', messages } })
    const workspace: Workspace = { id: 'workspace', name: 'Workspace', rootPath: '/workspace',
      createdAt: 1, lastOpenedAt: 1, settings: { ...DEFAULT_WORKSPACE_SETTINGS } }
    // ★ 长历史隔离之后,`Thread` 不再调 `threadRows`(那是纯函数门面),真正常驻的
    //   记录点是「历史那半有没有重算」—— `threadHistoryRows`。这条用例要钉的正是
    //   它:draft/附件/等长队列更新都不该触发历史重算。
    const rows = vi.spyOn(content, 'threadHistoryRows')
    try {
      await act(async () => root.render(createElement(I18nProvider, { initialLocale: 'en-US', children:
        createElement(ChatView, { sessionId: 'history-boundary', tabId: 'fixture-tab', workspace, fallbackModel: { model: '' } }) })))
      expect(rows).toHaveBeenCalled()
      rows.mockClear()
      for (let index = 0; index < 5; index++) {
        await act(async () => session.getState().setDraft(`Draft ${index}`))
      }
      expect((container.querySelector('[data-testid="draft"]') as HTMLInputElement).value).toBe('Draft 4')
      await act(async () => (container.querySelector('[data-testid="attach"]') as HTMLButtonElement).click())
      expect(container.querySelector('output')?.textContent).toBe('1')
      vi.mocked(pickAttachments).mockResolvedValueOnce([
        { kind: 'error', name: 'drawing.svg', error: { code: 'unknown', retryable: false, message: 'internal fallback',
          messageKey: 'attachment.error.unsupportedImage', messageParams: { mime: 'image/svg+xml' } } },
        { kind: 'path', name: 'other.txt', path: '/workspace/other.txt' }
      ])
      await act(async () => (container.querySelector('[data-testid="attach"]') as HTMLButtonElement).click())
      expect(container.querySelector('output')?.textContent).toBe('3')
      expect(container.querySelector('[data-testid="attachment-errors"]')?.textContent).toContain('Image format "image/svg+xml" is not supported')
      expect(container.textContent).not.toContain('internal fallback')
      const failedChip = container.querySelector('[data-testid="attachment-chip"][data-status="error"]')!
      expect(failedChip.querySelectorAll('button')).toHaveLength(1)
      expect(failedChip.querySelector('[data-testid="attachment-remove"]')).not.toBeNull()
      vi.mocked(uploadFile).mockResolvedValueOnce({ id: 'photo', scope: 'session', ownerId: 'history-boundary',
        displayName: 'photo.png', mime: 'image/png', size: 6, checksum: 'fixture', createdAt: 1,
        url: 'ncw://attachments/sessions/history-boundary/photo.png' })
      await act(async () => (container.querySelector('[data-testid="attach-unknown-mime"]') as HTMLButtonElement).click())
      expect(uploadFile).toHaveBeenCalledWith(expect.objectContaining({ name: 'photo.png', type: 'application/octet-stream' }), 'session', 'history-boundary')
      expect(container.querySelector('output')?.textContent).toBe('4')
      expect(container.querySelector('img[src="ncw://attachments/sessions/history-boundary/photo.png"]')).not.toBeNull()
      await act(async () => (container.querySelector('[data-testid="attach-without-path"]') as HTMLButtonElement).click())
      const failedChips = container.querySelectorAll('[data-testid="attachment-chip"][data-status="error"]')
      expect(failedChips).toHaveLength(2)
      for (const chip of failedChips) expect(chip.querySelectorAll('button')).toHaveLength(1)
      await act(async () => session.setState({ queuedInputs: [...session.getState().queuedInputs] }))
      expect(rows).not.toHaveBeenCalled()
      await act(async () => session.setState({ transcript: {
        ...session.getState().transcript,
        messages: [...messages, userMessage('next', [{ type: 'text', text: 'Continue' }], 3)]
      } }))
      expect(rows).toHaveBeenCalled()
      expect(container.textContent).toContain('Continue')
    } finally {
      await act(async () => root.unmount())
      releaseSession('history-boundary')
      useModelsStore.setState(models, true)
      vi.restoreAllMocks()
      dom.window.close()
      vi.unstubAllGlobals()
    }
  })
})

/**
 * 清单的「还在跑吗」是 ChatView 推出来、交给 TaskChecklist 展示的。
 * 这里钉住三档在真实转录数据下的切换：唯一判据是**当前 run 自己写成功过清单**。
 */
describe('chat task checklist execution', () => {
  it('keeps historical and unconfirmed lists as snapshots and stops only the current confirmed list', async () => {
    const dom = new JSDOM('<div id="root"></div>', { url: 'http://localhost' })
    Object.assign(dom.window, { nextcowork: {
      getPathForFile: () => '',
      on: () => () => {},
      invoke: async (channel: string) => ({ ok: true, data: channel === 'shell:listBackground' ? [] : channel === 'agent:listInteractions' ? []
        : channel === 'goal:get' ? undefined
          // 本用例的转录带着 runId，回合底部的改动审查卡会去拉改动集 —— 这里没有改动集
          : channel === 'review:getChangeSet' ? null
            : { messages: [], session: { id: 'checklist-run', workspaceId: 'workspace', model: '', mode: 'code' } } })
    } })
    vi.stubGlobal('window', dom.window)
    vi.stubGlobal('document', dom.window.document)
    vi.stubGlobal('HTMLElement', dom.window.HTMLElement)
    vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
    vi.stubGlobal('requestAnimationFrame', () => 1)
    vi.stubGlobal('cancelAnimationFrame', () => {})
    vi.stubGlobal('ResizeObserver', class { observe(): void {} disconnect(): void {} })
    const container = document.getElementById('root')!
    const root = createRoot(container)
    const models = useModelsStore.getState()
    useModelsStore.setState({ loaded: true })
    const session = sessionStore('checklist-run')
    const workspace: Workspace = { id: 'workspace', name: 'Workspace', rootPath: '/workspace',
      createdAt: 1, lastOpenedAt: 1, settings: { ...DEFAULT_WORKSPACE_SETTINGS } }
    const legacyCall = assistantMessage('legacy-call', [
      { type: 'tool_call', callId: 'legacy-1', name: 'TodoWrite',
        input: { todos: [{ content: 'Legacy step', activeForm: 'Doing a legacy step', status: 'in_progress' }] } }
    ], 2)
    const legacyResult = toolResultMessage('legacy-result', [
      { type: 'tool_result', callId: 'legacy-1', output: { content: 'ok' }, isError: false }
    ], 3)
    const messages = [userMessage('user', [{ type: 'text', text: 'Do the work' }], 1), legacyCall, legacyResult]
    session.setState({
      transcript: { ...emptyTranscript(), status: 'done', messages, messageRuns: { 'legacy-call': 'run-1', 'legacy-result': 'run-1' } },
      activeRunId: null
    })
    // 清单住在右上角活动胶囊的「任务」栏里(见 `ActivityCapsule`),点开胶囊才渲染
    const lastChecklist = (): Element | null => container.querySelector('[data-testid="activity-tasks"]')
    const executionState = (): string | null => lastChecklist()?.getAttribute('data-execution-state') ?? null
    const spinners = (): number => lastChecklist()?.querySelectorAll('.lucide-loader-circle').length ?? -1
    try {
      await act(async () => root.render(createElement(I18nProvider, { initialLocale: 'en-US', children:
        createElement(ChatView, { sessionId: 'checklist-run', tabId: 'fixture-tab', workspace, fallbackModel: { model: '' } }) })))
      // 胶囊点开时落在有内容的那一栏 —— 这里只有清单,所以是「任务」栏
      await act(async () => container.querySelector<HTMLButtonElement>('[data-activity-capsule]')?.click())
      // 需求：重载的历史没有本窗口的 run_end 事实，只能显示快照，不能报本轮未完成。
      expect(executionState()).toBe('snapshot')
      expect(spinners()).toBe(0)
      expect(lastChecklist()?.textContent).not.toContain('Doing a legacy step')
      expect(lastChecklist()?.textContent).not.toContain('Run ended with')

      await act(async () => session.setState({
        activeRunId: 'run-2',
        transcript: { ...session.getState().transcript, status: 'running', runStartedAt: 3 }
      }))
      expect(executionState()).toBe('snapshot')

      const currentCall = assistantMessage('current-call', [
        { type: 'tool_call', callId: 'current-1', name: 'TodoWrite', input: { todos: [
          { content: 'Wire checklist', activeForm: 'Wiring the checklist', status: 'in_progress' },
          { content: 'Run tests', activeForm: 'Running tests', status: 'pending' },
          { content: 'Write report', activeForm: 'Writing the report', status: 'completed' }
        ] } }
      ], 4)
      const currentResult = toolResultMessage('current-result', [
        { type: 'tool_result', callId: 'current-1', output: { content: 'ok' }, isError: false }
      ], 5)
      // 需求：长工具同批等待时可能只有 call；没有成功回执，不得提前采纳这份清单。
      await act(async () => session.getState().applyEvents([{ type: 'message_commit', message: currentCall }]))
      expect(executionState()).toBe('snapshot')
      expect(spinners()).toBe(0)
      expect(lastChecklist()?.textContent).toContain('Legacy step')

      // 走真实事件入口，确认 tool_result 同样归入当前 run，配对成功后才变成实时进度。
      await act(async () => session.getState().applyEvents([{ type: 'message_commit', message: currentResult }]))
      expect(executionState()).toBe('running')
      expect(spinners()).toBe(1)
      expect(lastChecklist()?.textContent).toContain('Wiring the checklist')

      // run_end 清空 activeRunId，而清单还剩两项没做完。
      await act(async () => session.getState().applyEvents([{ type: 'run_end', status: 'done', at: 6 }]))
      expect(executionState()).toBe('stopped')
      expect(spinners()).toBe(0)
      expect(lastChecklist()?.textContent).not.toContain('Wiring the checklist')
      expect(lastChecklist()?.textContent).toContain('Run ended with 2 unfinished task(s)')

      // 需求：无关的新问题结束后，旧清单仍是快照，不能把旧阻塞算到新一轮头上。
      await act(async () => session.setState({ activeRunId: 'run-3', transcript: {
        ...session.getState().transcript, status: 'running', runStartedAt: 7, runEndedAt: undefined
      } }))
      await act(async () => session.getState().applyEvents([
        { type: 'message_commit', message: userMessage('unrelated-user', [{ type: 'text', text: 'Another question' }], 7) },
        { type: 'message_commit', message: assistantMessage('unrelated-answer', [{ type: 'text', text: 'Another answer' }], 8) },
        { type: 'run_end', status: 'done', at: 9 }
      ]))
      expect(executionState()).toBe('snapshot')
      expect(lastChecklist()?.textContent).not.toContain('Run ended with')
      expect(lastChecklist()?.textContent).toContain('Task checklist · 1/3 completed')

      // 需求:清单搬去右上角之后,输入框上方那一行只剩发送队列,不再有清单或小球
      await act(async () => session.setState({
        queuedInputs: [makeQueuedInput('queued-1', 'Queued follow-up', {
          workspaceId: workspace.id,
          depth: 0,
          mode: 'code',
          thinking: 'auto',
          webSearch: false,
          permissionMode: 'auto',
          model: '',
          skillIds: []
        }, 1)]
      }))
      const notices = container.querySelector('[data-testid="composer-notices"]')
      expect(notices?.querySelector('[data-testid="pending-queue"]')).not.toBeNull()
      expect(notices?.querySelector('[data-testid="task-checklist"]')).toBeNull()
      expect(notices?.querySelector('[data-testid="task-checklist-ball"]')).toBeNull()
    } finally {
      await act(async () => root.unmount())
      releaseSession('checklist-run')
      useModelsStore.setState(models, true)
      vi.restoreAllMocks()
      dom.window.close()
      vi.unstubAllGlobals()
    }
  })
})
