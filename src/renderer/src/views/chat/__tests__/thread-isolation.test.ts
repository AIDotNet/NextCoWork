/**
 * 长历史的**渲染隔离** —— 一个流式 token 不该动到历史。
 *
 * ★ 要钉的是「工作量的位置」,不是「跑多快」:每个 token 到达时,历史行的构造
 * (`threadHistoryRows`)、历史分组(`threadTurnGroups`)都**不该再跑**;唯一应该
 * 变的是尾部那一行。以前 `rows = threadRows(messages, live, …)` 每 token 重扫全部
 * 消息,`threadTurnGroups(rows)` 跟着重建几百个组对象,于是满屏历史回合一起 reconcile。
 *
 * 所以断言是**调用次数**与**DOM 节点身份**:比「耗时从 X 降到 Y」稳,也不受 CI 机器忙闲影响。
 * 真实体感(几百轮下拖动/流式不再一顿一顿)要在真机上测,这里不报百分比。
 */
import { act, createElement } from 'react'
import { createRoot } from 'react-dom/client'
import { JSDOM } from 'jsdom'
import { describe, expect, it, vi } from 'vitest'
import { assistantMessage, toolResultMessage, userMessage } from '../../../../../shared/agent/message'
import { emptyTranscript, toolsFromMessages, type TranscriptState } from '../../../../../shared/agent/transcript'
import { I18nProvider } from '../../../i18n'
import { Thread } from '../Thread'
import * as content from '../thread-content'
import * as navigation from '../turn-navigation'

const TURNS = 200

function messages(): ReturnType<typeof userMessage>[] {
  const all: ReturnType<typeof userMessage>[] = []
  for (let index = 0; index < TURNS; index++) {
    all.push(
      userMessage(`u${index}`, [{ type: 'text', text: `Question ${index}` }], index * 3 + 1),
      assistantMessage(`a${index}`, [
        { type: 'text', text: `Answer ${index}` },
        { type: 'tool_call', callId: `read${index}`, name: 'Read', input: {} }
      ], index * 3 + 2),
      toolResultMessage(`r${index}`, [{ type: 'tool_result', callId: `read${index}`, output: { content: 'ok' }, isError: false }], index * 3 + 3)
    )
  }
  return all
}

describe('长历史渲染隔离', () => {
  it('200 回合下,流式 token 只更新 live 尾部 —— 历史行与分组都不重算', async () => {
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
    // ★ 这一份 `messages` **必须跨渲染保持同一引用** —— 每个 token 都换一份的话,
    //   历史 memo 必然失效,那测的就是 React 而不是我们这层隔离了。
    const all = messages()
    const base: TranscriptState = { ...emptyTranscript(), messages: all, tools: toolsFromMessages(all), status: 'running' }
    const historySpy = vi.spyOn(content, 'threadHistoryRows')
    const groupSpy = vi.spyOn(navigation, 'threadTurnGroups')

    const render = async (current: TranscriptState): Promise<void> => {
      await act(async () => root.render(createElement(I18nProvider, { initialLocale: 'en-US', children:
        createElement(Thread, { transcript: current, runId: 'run-1', lastSeq: 0, queued: 0, model: undefined, providerName: undefined }) })))
    }

    try {
      await render({ ...base, live: [{ index: 0, kind: 'text', text: 'token-0' }] })
      expect(container.textContent).toContain('token-0')
      const historyCalls = historySpy.mock.calls.length
      const groupCalls = groupSpy.mock.calls.length
      expect(historyCalls).toBeGreaterThan(0)
      const firstTurn = container.querySelectorAll('[data-testid="assistant-turn"]')[0]
      expect(firstTurn).not.toBeNull()

      for (let token = 1; token <= 20; token++) {
        await render({ ...base, live: [{ index: 0, kind: 'text', text: `token-${token}` }] })
      }

      // live 确实在动
      expect(container.textContent).toContain('token-20')
      expect(container.textContent).not.toContain('token-19')
      // 历史侧一个都没多跑:行没重建、分组没重建
      expect(historySpy.mock.calls.length).toBe(historyCalls)
      expect(groupSpy.mock.calls.length).toBe(groupCalls)
      // 而且历史那一轮的 DOM 节点还是同一个 —— 没被卸载重建
      expect(container.querySelectorAll('[data-testid="assistant-turn"]')[0]).toBe(firstTurn)
    } finally {
      await act(async () => root.unmount())
      vi.restoreAllMocks()
      dom.window.close()
      vi.unstubAllGlobals()
    }
  }, 20_000)
})
