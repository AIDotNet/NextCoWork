/**
 * 汇报行展开时**现取全文** —— 钉的是「这一行不再只给你 240 个字」。
 *
 * 消息里存下来的 `summary` 是主进程切的(`runtime.ts` 两处 `slice(0, 240)`),
 * 它生来是卡片上那一行预览。后台那条路拿它当汇报正文,于是一份长报告
 * 在界面上断在半个标识符中间,而且没有任何东西说明这不是全部。
 *
 * 三条分别钉住:展开后换成全文、取不到时明说、旧转录(没有 childSessionId)
 * 不去发那个注定失败的请求。
 */
import { act, createElement } from 'react'
import { createRoot } from 'react-dom/client'
import { JSDOM } from 'jsdom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { assistantMessage } from '../../../../../shared/agent/message'
import type { SubagentState } from '../../../../../shared/agent/transcript'
import { I18nProvider } from '../../../i18n'
import { SubagentReportRow } from '../parts'

vi.mock('../../../services/agent', () => ({ abortRun: vi.fn() }))
vi.mock('../../../services/sessions', () => ({ getLastAssistantMessage: vi.fn() }))
import { getLastAssistantMessage } from '../../../services/sessions'

const CHILD_SESSION = 'parent:sub:child-run'
/** 主进程切完之后剩下的那一截 —— 注意它断在半个标识符上 */
const BRIEF = '八个文件都已修改并验证。改动如下:1. contracts.ts —— ModelStatus.state 增加 unavailable;新增可选 ModelStatusC'
const FULL = `${BRIEF}ache 字段。\n\n## 值得知道的环境异常\n\n这一轮里 Edit 有四次报告「已替换」但实际没落盘。`

function subagent(patch: Partial<SubagentState> = {}): SubagentState {
  return {
    callId: 'task-bg', childRunId: 'child-run', childSessionId: CHILD_SESSION,
    status: 'done', description: '调整模型界面独立加载', subagentType: 'code-editor',
    background: true, reportStatus: 'reported', toolCalls: 12, toolErrors: 0,
    startedAt: 0, endedAt: 1, summary: BRIEF, ...patch
  }
}

let teardown: (() => Promise<void>) | null = null
afterEach(async () => {
  await teardown?.()
  teardown = null
  vi.clearAllMocks()
})

async function renderRow(state: SubagentState): Promise<HTMLElement> {
  const dom = new JSDOM('<div id="root"></div>', { url: 'http://localhost' })
  Object.assign(dom.window, { nextcowork: { on: () => () => {} } })
  vi.stubGlobal('window', dom.window)
  vi.stubGlobal('document', dom.window.document)
  vi.stubGlobal('HTMLElement', dom.window.HTMLElement)
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('requestAnimationFrame', () => 1)
  vi.stubGlobal('cancelAnimationFrame', () => {})
  vi.stubGlobal('ResizeObserver', class { observe(): void {} disconnect(): void {} })

  const container = document.getElementById('root')!
  const root = createRoot(container)
  await act(async () => root.render(createElement(I18nProvider, {
    initialLocale: 'zh-CN',
    children: createElement(SubagentReportRow, { summary: state.summary, state })
  })))
  teardown = async () => {
    await act(async () => root.unmount())
    dom.window.close()
    vi.unstubAllGlobals()
  }
  return container
}

const expand = async (container: HTMLElement): Promise<void> => {
  const toggle = container.querySelector('[data-testid="subagent-report-toggle"]')
  if (toggle === null) throw new Error('展开按钮不在 —— 断言前先确认这一行渲染了')
  await act(async () => {
    toggle.dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true }))
  })
  // 取全文是一次 IPC,等微任务队列排空
  await act(async () => { await Promise.resolve() })
}

describe('后台汇报行 · 展开取全文', () => {
  it('★★ 展开后显示的是子会话里的全文,不是那 240 字的摘要', async () => {
    // ★ 只取子会话最后一条助手消息,不读整段子转录
    vi.mocked(getLastAssistantMessage).mockResolvedValue(assistantMessage('a', [{ type: 'text', text: FULL }], 1))
    const container = await renderRow(subagent())
    // 收着的时候不发请求 —— 大部分汇报行用户根本不会点开
    expect(getLastAssistantMessage).not.toHaveBeenCalled()

    await expand(container)

    expect(getLastAssistantMessage).toHaveBeenCalledWith(CHILD_SESSION)
    // ★ 断在 `ModelStatusC` 之后的那一截,只有全文里才有
    expect(container.textContent).toContain('ache 字段')
    expect(container.textContent).toContain('值得知道的环境异常')
    expect(container.querySelector('[data-testid="subagent-report-truncated"]')).toBeNull()
  })

  it('★★ 取不到全文时明说,而不是默默摆着断掉的摘要', async () => {
    vi.mocked(getLastAssistantMessage).mockRejectedValue(new Error('会话已删除'))
    const container = await renderRow(subagent())
    await expand(container)

    // 摘要还在(有胜于无),但旁边有一句话交代它不是全部
    expect(container.textContent).toContain('ModelStatusC')
    expect(container.querySelector('[data-testid="subagent-report-truncated"]')?.textContent)
      .toContain('完整结果读取失败')
  })

  it('旧转录没有 childSessionId —— 不发那个注定失败的请求,直接说明情况', async () => {
    const container = await renderRow(subagent({ childSessionId: undefined }))
    await expand(container)

    expect(getLastAssistantMessage).not.toHaveBeenCalled()
    expect(container.querySelector('[data-testid="subagent-report-truncated"]')?.textContent)
      .toContain('旧版本')
  })
})
