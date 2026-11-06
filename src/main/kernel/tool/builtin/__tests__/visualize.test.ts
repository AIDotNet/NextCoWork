/**
 * 可视化那一对工具的单测。
 *
 * ★ 这里最重要的一条是**反向断言**:`content` 里不含 `widget_code`。
 * 模型刚写完那段代码,把它原样回灌等于为同一份内容付两遍窗口钱,而模型
 * 看到自己刚写的东西在结果里还会想再改一遍。这条同时钉住"卡片只走 UI 轨"
 * 那条不变式 —— 代码从 `card` 出去,不从 `content` 出去。
 */
import { describe, expect, it } from 'vitest'
import { nodeHost } from '../../../host'
import type { ToolContext } from '../../registry'
import { visualizeEditWidgetTool, visualizeReadMeTool, visualizeShowWidgetTool, visualizeUpdateWidgetTool } from '../visualize'

function ctx(): ToolContext {
  return {
    workspaceRoot: '/tmp/does-not-matter',
    signal: new AbortController().signal,
    permissionMode: 'auto',
    depth: 0,
    callId: 'call_1',
    runId: 'run_1',
    host: nodeHost(),
    emit: () => {}
  }
}

const CODE = '<svg viewBox="0 0 10 10"><circle cx="5" cy="5" r="4" /></svg>'

async function showWidget(input: Record<string, unknown>) {
  return visualizeShowWidgetTool.execute(
    { title: 'compound_interest', widget_code: CODE, loading_messages: ['画个圆'], ...input },
    ctx()
  )
}

describe('visualize_show_widget', () => {
  it('产出一张 widget 卡片,代码只走卡片', async () => {
    const result = await showWidget({})
    expect(result.isError).toBe(false)
    expect(result.output.card).toEqual({ kind: 'widget', title: 'compound_interest', code: CODE, format: 'svg' })
    expect(result.output.content).not.toContain('<svg')
    expect(result.output.content).not.toContain('circle')
  })

  /**
   * ★ 必须告诉模型"别再复述一遍":它训练里"解释清楚"的默认动作就是把刚画出来的
   * 东西用文字再讲一遍,而那会让用户在同一屏上看到同一份信息两遍。
   */
  it('结果里明确要求不要把已渲染的内容用文字重复', async () => {
    const result = await showWidget({})
    expect(result.output.content).toMatch(/do not duplicate/i)
  })

  it('title 必须是可自解释的 snake_case 标识', async () => {
    const bad = await showWidget({ title: 'Q4 Revenue Chart' })
    expect(bad.isError).toBe(true)
    expect(bad.output.content).toContain('snake_case')
  })

  it('loading_messages 要 1–4 条,空数组与五条都被拒', async () => {
    expect((await showWidget({ loading_messages: [] })).isError).toBe(true)
    expect((await showWidget({ loading_messages: ['a', 'b', 'c', 'd', 'e'] })).isError).toBe(true)
  })

  it('widget_code 为空被拒 —— 一张什么都没有的卡片不如一次失败', async () => {
    const result = await showWidget({ widget_code: '' })
    expect(result.isError).toBe(true)
  })

  it('widget_code 超过 128K 被拒,且失败信息说明是长度问题', async () => {
    const result = await showWidget({ widget_code: 'x'.repeat(128 * 1024 + 1) })
    expect(result.isError).toBe(true)
    expect(result.output.content).toContain('widget_code')
  })

  /** 见 `visualize.ts` 文件头第 2 条:它自己不出网,但它产出的东西会。 */
  it('★ 申报 needsNetwork —— widget 里的 CDN 脚本是一条出网路径', () => {
    expect(visualizeShowWidgetTool.needsNetwork).toBe(true)
    expect(visualizeShowWidgetTool.readOnly).toBe(true)
    expect(visualizeShowWidgetTool.destructive).toBe(false)
  })
})

describe('visualize_read_me', () => {
  it('返回规范正文', async () => {
    const result = await visualizeReadMeTool.execute({ modules: ['interactive'] }, ctx())
    expect(result.isError).toBe(false)
    expect(result.output.content).toContain('# Imagine')
  })

  it('不存在的模块名被 schema 拦下(枚举来自 AVAILABLE_MODULES)', async () => {
    const result = await visualizeReadMeTool.execute({ modules: ['nope'] }, ctx())
    expect(result.isError).toBe(true)
  })

  /**
   * ★ 超限时**明确失败**而不是让它被截断 —— 失败信息里带着两个模块各自的体积,
   * 模型下一轮就会只挑一个。见 `visualize.ts` 里那段长注释。
   */
  it('组合会越过输出截断线时明确失败,并提示改成单模块', async () => {
    const result = await visualizeReadMeTool.execute({ modules: ['diagram', 'chart'] }, ctx())
    expect(result.isError).toBe(true)
    expect(result.output.content).toContain('single module')
    expect(result.output.content).toContain('diagram')
  })

  it('不联网 —— 规范是本地的那七万字', () => {
    expect(visualizeReadMeTool.needsNetwork).toBe(false)
  })
})

const TSX = `import { useState } from 'react'
import { Stack, Text } from 'nextcowork/widget'
type Props = { n?: number }
export default function Widget({ n = 1 }: Props) {
  const [count] = useState<number>(n)
  return <Stack gap={2}><Text>{count}</Text></Stack>
}`

describe('visualize_show_widget · tsx', () => {
  it('在主进程编译:卡片同时带源码与 CJS 产物', async () => {
    const result = await showWidget({ format: 'tsx', widget_code: TSX })
    expect(result.isError).toBe(false)
    const card = result.output.card
    expect(card?.kind).toBe('widget')
    if (card?.kind !== 'widget') return
    expect(card.format).toBe('tsx')
    expect(card.code).toBe(TSX)
    expect(card.compiled).toContain('require(\'nextcowork/widget\')')
    expect(card.compiled).toContain('react/jsx-runtime')
    expect(card.compiled).not.toContain('Props')
  })

  it('不给 format 时按 import 认出 tsx', async () => {
    const result = await showWidget({ widget_code: TSX })
    expect(result.output.card).toMatchObject({ format: 'tsx' })
  })

  /** ★ 编译错误要在同一轮回到模型手上,带位置和那一行 */
  it('语法错误给出位置与出错的那一行', async () => {
    const result = await showWidget({ format: 'tsx', widget_code: 'export default function W() {\n  return <div>\n}' })
    expect(result.isError).toBe(true)
    expect(result.output.content).toMatch(/TSX syntax error/)
    expect(result.output.content).toMatch(/\(\d+:\d+\)/)
  })

  it('白名单之外的 import 被拒,并列出能用的模块', async () => {
    const result = await showWidget({ format: 'tsx', widget_code: "import _ from 'lodash'\nexport default function W() { return null }" })
    expect(result.isError).toBe(true)
    expect(result.output.content).toContain('"lodash"')
    expect(result.output.content).toContain('nextcowork/widget')
  })

  it('没有默认导出被拒', async () => {
    const result = await showWidget({ format: 'tsx', widget_code: "import { Stack } from 'nextcowork/widget'\nexport function W() { return null }" })
    expect(result.isError).toBe(true)
    expect(result.output.content).toContain('default export')
  })
})

describe('visualize_show_widget · actions', () => {
  const run = { id: 'run_tests', label: 'Run tests', tool: 'Bash', input: { command: 'npm test -- {{suite}}' }, params: { suite: { type: 'enum', values: ['a', 'b'] } } }

  it('合法声明随卡片走', async () => {
    const result = await showWidget({ actions: [run] })
    expect(result.isError).toBe(false)
    expect(result.output.card).toMatchObject({ actions: [run] })
  })

  it('占位符与参数必须一一对应', async () => {
    const missing = await showWidget({ actions: [{ ...run, params: {} }] })
    expect(missing.isError).toBe(true)
    expect(missing.output.content).toContain('{{suite}}')
    const unused = await showWidget({ actions: [{ ...run, input: { command: 'npm test' } }] })
    expect(unused.isError).toBe(true)
  })

  it('会挂起或派 agent 的工具不能做动作', async () => {
    const result = await showWidget({ actions: [{ id: 'ask', label: 'Ask', tool: 'AskUserQuestion', input: {} }] })
    expect(result.isError).toBe(true)
  })

  it('id 不能重复', async () => {
    const result = await showWidget({ actions: [run, run] })
    expect(result.isError).toBe(true)
  })
})

describe('visualize_show_widget · interactive', () => {
  it('先推实时卡片,再挂起等提交;提交值作为结果回给模型', async () => {
    const emitted: unknown[] = []
    const result = await visualizeShowWidgetTool.execute(
      { title: 'pick_db', widget_code: CODE, loading_messages: ['x'], interactive: true },
      {
        ...ctx(),
        emit: (progress) => emitted.push(progress),
        interact: async (draft) => {
          expect(draft).toMatchObject({ kind: 'widget_submit', callId: 'call_1', title: 'pick_db' })
          return { id: 'i1', kind: 'widget_submit', value: '{"db":"postgres"}' }
        }
      }
    )
    expect(emitted).toHaveLength(1)
    expect(emitted[0]).toMatchObject({ card: { kind: 'widget', interactive: true } })
    expect(result.isError).toBe(false)
    expect(result.output.content).toContain('{"db":"postgres"}')
    expect(result.output.card).toMatchObject({ interactive: true, submitted: '{"db":"postgres"}' })
  })

  it('用户关掉不交 → 明确失败,不让模型自己猜', async () => {
    const result = await visualizeShowWidgetTool.execute(
      { title: 'pick_db', widget_code: CODE, loading_messages: ['x'], interactive: true },
      { ...ctx(), interact: async () => ({ id: 'i1', kind: 'widget_submit', value: null }) }
    )
    expect(result.isError).toBe(true)
    expect(result.output.content).toMatch(/without submitting/)
  })

  /** 子代理没有人能回答它的挂起 —— 见 `registry.ts` 的 noInteraction */
  it('子代理里不可用', async () => {
    const result = await visualizeShowWidgetTool.execute(
      { title: 'pick_db', widget_code: CODE, loading_messages: ['x'], interactive: true },
      { ...ctx(), depth: 1, interact: async () => ({ id: 'i1', kind: 'widget_submit', value: '1' }) }
    )
    expect(result.isError).toBe(true)
  })
})

function historyWith(card: Record<string, unknown>): ToolContext['messages'] {
  return [
    {
      id: 'm1',
      role: 'tool',
      parts: [{ type: 'tool_result', callId: 'c0', isError: false, output: { content: 'ok', card } }],
      createdAt: 0,
      schemaVersion: 1
    }
  ] as unknown as ToolContext['messages']
}

describe('visualize_edit_widget', () => {
  const base = { kind: 'widget', title: 'chart', code: '<div class="a">Q1</div><div class="a">Q2</div>', format: 'html' }

  it('按标题找到上一版,精确替换后产出新卡', async () => {
    const result = await visualizeEditWidgetTool.execute(
      { title: 'chart', edits: [{ old_string: 'Q1', new_string: 'Jan' }] },
      { ...ctx(), messages: historyWith(base) }
    )
    expect(result.isError).toBe(false)
    expect(result.output.card).toMatchObject({ kind: 'widget', title: 'chart', code: '<div class="a">Jan</div><div class="a">Q2</div>' })
  })

  it('旧串出现多次且没给 replace_all → 失败而不是猜', async () => {
    const result = await visualizeEditWidgetTool.execute(
      { title: 'chart', edits: [{ old_string: 'class="a"', new_string: 'class="b"' }] },
      { ...ctx(), messages: historyWith(base) }
    )
    expect(result.isError).toBe(true)
    expect(result.output.content).toContain('2 places')
  })

  it('找不到这张图', async () => {
    const result = await visualizeEditWidgetTool.execute(
      { title: 'nope', edits: [{ old_string: 'a', new_string: 'b' }] },
      { ...ctx(), messages: historyWith(base) }
    )
    expect(result.isError).toBe(true)
  })
})

describe('visualize_update_widget', () => {
  it('数据只走卡片', async () => {
    const result = await visualizeUpdateWidgetTool.execute(
      { title: 'chart', data: { progress: 0.5 } },
      { ...ctx(), messages: historyWith({ kind: 'widget', title: 'chart', code: '<div/>' }) }
    )
    expect(result.isError).toBe(false)
    expect(result.output.card).toEqual({ kind: 'widget_data', title: 'chart', data: { progress: 0.5 } })
    expect(result.output.content).not.toContain('0.5')
  })
})
