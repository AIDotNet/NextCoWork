/**
 * 子代理卡片的 DOM 级回归 —— 钉的是「点标题行就地展开,但停止/打开记录不受展开影响」。
 *
 * 这些都抓不到的东西:纯函数测试。它们全是**某块 UI 在或不在**:
 *
 * 1. 标题行是展开开关(`aria-expanded`),点一下展开区出现、再点收起。
 * 2. 「完整记录」是**独立按钮**,把动作交给 `openSubagent`,不和展开抢同一次点击。
 * 3. **停止按钮在不展开的情况下就能按。** 它以前藏在默认折叠的面板里 —— 想掐掉
 *    一个卡住的子代理,得先点开一张从外面完全看不出异常的卡片。
 *
 * 样板抄 `thread-content.test.ts` 的 `renderThread`(JSDOM 手搓,没有全局
 * jsdom 环境,也没有 testing-library —— 见 `vitest.config.ts`)。
 */
import { act, createElement } from 'react'
import { createRoot } from 'react-dom/client'
import { JSDOM } from 'jsdom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { SubagentState } from '../../../../../shared/agent/transcript'
import { I18nProvider } from '../../../i18n'
import { SubagentNode } from '../parts'
import { SubagentOpenProvider } from '../subagent-open'

vi.mock('../../../services/agent', () => ({ abortRun: vi.fn() }))
import { abortRun } from '../../../services/agent'

const CHILD_RUN = 'child-run'

function subagent(patch: Partial<SubagentState> = {}): SubagentState {
  return {
    callId: 'task-1',
    childRunId: CHILD_RUN,
    childSessionId: `parent:sub:${CHILD_RUN}`,
    status: 'running',
    description: '查配置读取处',
    subagentType: 'general-purpose',
    color: 'blue',
    toolCalls: 7,
    toolErrors: 0,
    startedAt: Date.now() - 60_000,
    ...patch
  }
}

let teardown: (() => Promise<void>) | null = null

afterEach(async () => {
  await teardown?.()
  teardown = null
  vi.clearAllMocks()
})

/** 渲染一张卡片,返回容器和点开动作的探针 */
async function renderCard(state: SubagentState): Promise<{
  container: HTMLElement
  opened: SubagentState[]
}> {
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
  const opened: SubagentState[] = []
  await act(async () => root.render(createElement(I18nProvider, {
    initialLocale: 'zh-CN',
    children: createElement(SubagentOpenProvider, {
      open: (s: SubagentState) => { opened.push(s) },
      children: createElement(SubagentNode, { summary: undefined, state })
    })
  })))
  teardown = async () => {
    await act(async () => root.unmount())
    dom.window.close()
    vi.unstubAllGlobals()
  }
  return { container, opened }
}

const click = async (el: Element | null): Promise<void> => {
  if (el === null) throw new Error('要点的那个节点不在 —— 断言前先确认它渲染了')
  await act(async () => {
    el.dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true }))
  })
}

describe('子代理卡片 · 点标题行就地展开', () => {
  it('★ 标题行是展开开关:点一下出现详情,再点收起', async () => {
    const { container } = await renderCard(subagent({ phase: 'tool', toolCalls: 7 }))
    const toggle = container.querySelector('[data-testid="subagent-toggle"]')
    expect(toggle?.getAttribute('aria-expanded')).toBe('false')
    expect(container.querySelector('[data-testid="subagent-detail"]')).toBeNull()
    expect(container.textContent).toContain('查配置读取处')

    await click(toggle)
    expect(toggle?.getAttribute('aria-expanded')).toBe('true')
    const detail = container.querySelector('[data-testid="subagent-detail"]')
    expect(detail).not.toBeNull()
    expect(detail?.textContent).toContain('调用工具')
    expect(detail?.textContent).toContain('7 次工具调用')

    await click(toggle)
    expect(toggle?.getAttribute('aria-expanded')).toBe('false')
  })

  it('★ 「完整记录」是独立按钮:交给 openSubagent,而且不会顺带展开卡片', async () => {
    const state = subagent()
    const { container, opened } = await renderCard(state)

    await click(container.querySelector('[data-testid="subagent-open"]'))

    expect(opened).toHaveLength(1)
    expect(opened[0]?.childSessionId).toBe(state.childSessionId)
    expect(container.querySelector('[data-testid="subagent-detail"]')).toBeNull()
  })

  /**
   * ★★ 这条是这次改动的由头。
   *
   * 「不展开就能按」在 DOM 上的判据只有一个:停止按钮**此刻就在文档里**,
   * 而在它之前没有任何需要先点一下的东西。所以断言写成「一渲染完就查得到」,
   * 而不是「点了某个东西之后查得到」。
   */
  it('★★ 运行中时,停止按钮不展开就在那儿,按下去掐的是子 run', async () => {
    const { container } = await renderCard(subagent())
    const stop = container.querySelector('[data-testid="subagent-stop"]')
    expect(stop).not.toBeNull()

    await click(stop)
    // 第二个参数 true = 这是子 run,不要连父 run 一起收
    expect(abortRun).toHaveBeenCalledWith(CHILD_RUN, true)
  })

  it('运行卡片收起时不显示阶段和工具调用次数(展开后才有)', async () => {
    const { container } = await renderCard(subagent({ phase: 'starting', toolCalls: 7 }))
    expect(container.textContent).not.toContain('启动中')
    expect(container.textContent).not.toContain('7 次工具调用')
  })

  /**
   * 身份色原先染的是整张卡(描边 + 底色 + 左侧内阴影)。改成文本风格后没有卡可染,
   * 颜色挂在图标和类型名上 —— 所以这条断言跟着搬到图标那一格,而**要守的事没变**:
   * 「配了颜色的子代理,界面上必须看得出它是哪一个」。
   */
  it('使用子代理配置的颜色标记那一行', async () => {
    const { container } = await renderCard(subagent({ color: 'blue' }))
    const card = container.querySelector('[data-testid="subagent-node"]') as HTMLElement | null
    const icon = container.querySelector('[data-testid="subagent-icon"]') as HTMLElement | null
    expect(card?.dataset.subagentColor).toBe('blue')
    expect(icon?.style.color).not.toBe('')
  })

  /**
   * 卡片要能回答「这个子代理跑在哪条模型上」。
   *
   * 别名是主进程随 `subagent_start` 带来的(`runtime.ts` 发的是
   * `childRequestFor` 算出的那个),转录一直存着 `SubagentState.model` ——
   * 在这次改动之前它没有任何渲染消费者,所以这条测试是它唯一的守卫。
   */
  it('副标题同时显示子代理类型和它实际使用的模型别名', async () => {
    const { container } = await renderCard(subagent({ model: 'claude-fable-5-1' }))
    expect(container.textContent).toContain('general-purpose')
    expect(container.textContent).toContain('claude-fable-5-1')
  })

  /**
   * ★ 别名和类型之间那个 `·` 是**独立的兄弟节点**,不是拼进文本里的。
   *
   * 旧转录没有 `model`(这一格是随 `subagent_start` 才有的),拼字符串的写法会在
   * 那里留下一个悬空的分隔符 —— 看起来像「类型后面本来还该有点什么」。
   */
  it('没有模型时(旧转录)不画那一格,也不留下悬空的分隔符', async () => {
    const { container } = await renderCard(subagent({ model: undefined }))
    expect(container.textContent).toContain('general-purpose')
    expect(container.textContent).not.toContain('·')
  })

  it('跑完了就没有停止按钮 —— 没有可掐的东西', async () => {
    const { container } = await renderCard(subagent({ status: 'done', endedAt: Date.now() }))
    expect(container.querySelector('[data-testid="subagent-stop"]')).toBeNull()
  })

  /**
   * `childSessionId` 是随这次改动才加进 `subagent_start` 的,所以**旧转录没有**。
   * 点开会是一个空面板 —— 一张点了没反应的卡片比一张明确不能点的卡片难解释得多。
   */
  it('旧转录(没有 childSessionId)没有「完整记录」按钮,但卡片照样能展开', async () => {
    const { container, opened } = await renderCard(subagent({ childSessionId: undefined }))
    expect(container.querySelector('[data-testid="subagent-open"]')).toBeNull()
    await click(container.querySelector('[data-testid="subagent-toggle"]'))
    expect(container.querySelector('[data-testid="subagent-detail"]')).not.toBeNull()
    expect(opened).toHaveLength(0)
  })

  /**
   * ★★ 「汇报」只对后台子代理成立。
   *
   * 前台子代理的结果就是它那条 `tool_result`,同步回到主代理 —— 根本没有
   * 「回传」这一步。以前的兜底是「没写 reportStatus 且不在跑 → reported」,
   * 而前台在 `runtime.ts` 里恰恰从不写这个字段,于是**每一张跑完的前台卡片**
   * 都挂着一句「结果已汇报给主代理」,把后台专属的信号摊派给了所有人。
   */
  it('★★ 前台子代理跑完,不显示任何「已汇报」横幅', async () => {
    const { container } = await renderCard(subagent({ status: 'done', endedAt: Date.now() }))
    expect(container.textContent).not.toContain('汇报')
    expect(container.textContent).not.toContain('后台')
  })

  it('后台子代理跑完,「待汇报」和「后台」标记都在', async () => {
    const { container } = await renderCard(subagent({ status: 'done', endedAt: Date.now(), background: true }))
    expect(container.textContent).toContain('后台')
    expect(container.textContent).toContain('结果待汇报给主代理')
  })

  /** 失败原因留一行在卡片上 —— 这一条是详情整体搬走时**唯一**的例外 */
  it('出错时,原因直接写在卡片上,不必开面板', async () => {
    const { container } = await renderCard(subagent({
      status: 'error',
      endedAt: Date.now(),
      error: { code: 'provider', message: '上游 502', retryable: false, status: 502 }
    }))
    const box = container.querySelector('[data-testid="subagent-error"]')
    expect(box).not.toBeNull()
    expect(box?.textContent).toContain('502')
  })
})
