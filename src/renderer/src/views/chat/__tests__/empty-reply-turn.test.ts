/**
 * 回复被删光之后的空末轮。
 *
 * ★ 只剩一条提问时,`threadHistoryRows` 照旧在末尾补一个空助手行(给流式内容顶上用)。
 * 它以前会画出「供应商 / 模型」页眉,下面什么都没有 —— 看起来就是删除没删干净。
 * 停止 / 出错的空回合不同:那时它是「重新生成」唯一的落脚点,必须留着。
 */
import { act, createElement } from 'react'
import { createRoot } from 'react-dom/client'
import { JSDOM } from 'jsdom'
import { describe, expect, it, vi } from 'vitest'
import { userMessage } from '../../../../../shared/agent/message'
import { emptyTranscript, type TranscriptState } from '../../../../../shared/agent/transcript'
import { I18nProvider } from '../../../i18n'
import { Thread } from '../Thread'

async function turnsFor(transcript: TranscriptState): Promise<number> {
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
  try {
    await act(async () => root.render(createElement(I18nProvider, { initialLocale: 'en-US', children:
      createElement(Thread, { transcript, runId: null, lastSeq: 0, queued: 0, model: 'some-model', providerName: 'SomeProvider' }) })))
    expect(container.textContent).toContain('Hello')
    return container.querySelectorAll('[data-testid="assistant-turn"]').length
  } finally {
    await act(async () => root.unmount())
    dom.window.close()
    vi.unstubAllGlobals()
  }
}

describe('删光回复后的空末轮', () => {
  const messages = [userMessage('u1', [{ type: 'text', text: 'Hello' }], 1)]

  it('正常收尾的空回合不画页眉', async () => {
    expect(await turnsFor({ ...emptyTranscript(), messages, status: 'done' })).toBe(0)
  })

  it('停止的空回合照旧画出来 —— 重新生成要挂在它上面', async () => {
    expect(await turnsFor({ ...emptyTranscript(), messages, status: 'aborted' })).toBe(1)
  })
})
