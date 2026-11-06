import { describe, expect, it } from 'vitest'
import { formatDuration } from '../agent/duration'
import type { ToolCallState } from '../agent/transcript'
import {
  computeAutoCollapsed,
  decideWorkspace,
  groupDuration,
  groupConsecutiveTools,
  groupItems,
  groupKey,
  groupTitle,
  isCompletedToolGroup,
  summarize,
  TOOL_WINDOW_SIZE,
  workspaceTitleParts,
  type TimelineItem
} from '../domain/tool-timeline'

/**
 * 折叠逻辑的 bug 全是「偶发」的:少一组、多折一层、失败被折进去 ——
 * 都要特定的工具排列顺序才现形。这一组把每条规则单独钉住。
 */

let seq = 0
function tool(
  name: string,
  status?: ToolCallState['status']
): Extract<TimelineItem, { kind: 'tool' }> & { callId: string } {
  seq += 1
  const callId = `c${String(seq)}`
  toolTable[callId] = {
    callId,
    name,
    input: {},
    status: status ?? 'ok',
    startedAt: 1000,
    endedAt: 1500
  }
  return { key: callId, kind: 'tool', callId, name, input: {} }
}

let toolTable: Record<string, ToolCallState> = {}

function reset(): void {
  seq = 0
  toolTable = {}
}

const thinking = (key: string): TimelineItem => ({
  key,
  kind: 'thinking',
  text: '…',
  streaming: false
})

describe('groupItems', () => {
  it('连续同形态合成一组', () => {
    reset()
    const items = [tool('Read'), tool('Read'), tool('LS')]
    const groups = groupItems(items, toolTable)
    expect(groups).toHaveLength(1)
    expect(groups[0]).toHaveLength(3)
  })

  it('形态变化处断开', () => {
    reset()
    const items = [tool('Read'), tool('Bash'), tool('Read')]
    const groups = groupItems(items, toolTable)
    expect(groups.map((g) => g.length)).toEqual([1, 1, 1])
  })

  it('★ Read 与 Grep 不同组 —— 它们都是只读,但详情形态不同', () => {
    reset()
    const groups = groupItems([tool('Read'), tool('Grep')], toolTable)
    expect(groups).toHaveLength(2)
  })

  it('thinking 与工具不同组', () => {
    reset()
    const groups = groupItems([thinking('t1'), tool('Read')], toolTable)
    expect(groups.map((g) => g.length)).toEqual([1, 1])
  })

  it('空输入返回空数组,不返回 [[]]', () => {
    expect(groupItems([], {})).toEqual([])
  })

  it('★ 组 key 取首项 —— 追加同形态新项时 key 不变,用户的展开意图才不会漂移', () => {
    reset()
    const a = tool('Read')
    const b = tool('Read')
    expect(groupKey(groupItems([a], toolTable)[0]!)).toBe(a.key)
    expect(groupKey(groupItems([a, b], toolTable)[0]!)).toBe(a.key)
  })
})

describe('groupConsecutiveTools', () => {
  it('merges adjacent tools even when their display shapes differ', () => {
    reset()
    const items = [tool('Read'), tool('Bash'), tool('Grep')]
    expect(groupConsecutiveTools(items).map((group) => group.length)).toEqual([3])
  })

  it('keeps thinking as a boundary between tool runs', () => {
    reset()
    const items = [tool('Read'), thinking('t'), tool('Bash')]
    expect(groupConsecutiveTools(items).map((group) => group.length)).toEqual([1, 1, 1])
  })

  it('starts a new group after a completed group instead of reopening it', () => {
    reset()
    const completed = [tool('Read'), tool('Bash')]
    const running = tool('Grep', 'running')
    const groups = groupConsecutiveTools([...completed, running], toolTable)
    expect(groups.map((group) => group.map((item) => item.key))).toEqual([
      completed.map((item) => item.key),
      [running.key]
    ])
    expect(isCompletedToolGroup(groups[0]!, toolTable)).toBe(true)
    expect(isCompletedToolGroup(groups[1]!, toolTable)).toBe(false)

    toolTable[running.callId]!.status = 'ok'
    expect(groupConsecutiveTools([...completed, running], toolTable).map((group) => group.length)).toEqual([3])
  })

  it('only collapses a multi-tool group after every call succeeds', () => {
    reset()
    const complete = groupConsecutiveTools([tool('Read'), tool('Bash')])[0]!
    expect(isCompletedToolGroup(complete, toolTable)).toBe(true)
    reset()
    const pending = groupConsecutiveTools([tool('Read'), tool('Bash', 'running')])[0]!
    expect(isCompletedToolGroup(pending, toolTable)).toBe(false)
  })
})

describe('computeAutoCollapsed', () => {
  it('run 结束后一律不坍缩 —— 收束交给 L3', () => {
    reset()
    const items = Array.from({ length: 10 }, () => tool('Read'))
    const groups = groupItems(items, toolTable)
    expect(computeAutoCollapsed({ groups, tools: toolTable, running: false })).toEqual([false])
  })

  it('运行中:窗口内的组展开,更早的坍缩', () => {
    reset()
    // 5 个不同形态 → 5 组,每组 1 项;窗口 3 → 后 3 组展开
    const items = [tool('Read'), tool('Bash'), tool('Grep'), tool('Write'), tool('WebFetch')]
    const groups = groupItems(items, toolTable)
    const collapsed = computeAutoCollapsed({ groups, tools: toolTable, running: true })
    expect(collapsed).toEqual([true, true, false, false, false])
  })

  it('项数不足窗口时全部展开', () => {
    reset()
    const groups = groupItems([tool('Read'), tool('Bash')], toolTable)
    expect(computeAutoCollapsed({ groups, tools: toolTable, running: true })).toEqual([
      false,
      false
    ])
  })

  it('★ 含失败的组强制展开,哪怕它在窗口之外', () => {
    reset()
    const bad = tool('Bash', 'error')
    const items = [
      bad,
      tool('Read'),
      tool('Grep'),
      tool('Write'),
      tool('WebFetch'),
      tool('Skill')
    ]
    const groups = groupItems(items, toolTable)
    const collapsed = computeAutoCollapsed({ groups, tools: toolTable, running: true })
    expect(collapsed[0]).toBe(false) // 失败组:展开
  })

  it('★ 失败项不占窗口名额 —— 否则早期失败会把窗口钉死,看不到当前进度', () => {
    reset()
    const bad = tool('Bash', 'error')
    const r1 = tool('Read')
    const r2 = tool('Grep')
    const r3 = tool('Write')
    const groups = groupItems([bad, r1, r2, r3], toolTable)
    const collapsed = computeAutoCollapsed({ groups, tools: toolTable, running: true })
    // 4 组:失败组展开(逃逸),其余 3 个非失败项正好占满窗口 → 也都展开
    expect(collapsed).toEqual([false, false, false, false])
  })

  it('★ 失败逃逸后,窗口仍然只给非失败项 3 个名额', () => {
    reset()
    const bad = tool('Bash', 'error')
    const items = [
      bad,
      tool('Read'),
      tool('Grep'),
      tool('Write'),
      tool('WebFetch'),
      tool('Skill')
    ]
    const groups = groupItems(items, toolTable)
    const collapsed = computeAutoCollapsed({ groups, tools: toolTable, running: true })
    // 6 组;失败组展开;非失败共 5 项,后 3 项所在组展开,中间 2 组坍缩
    expect(collapsed).toEqual([false, true, true, false, false, false])
  })

  it('窗口大小可配', () => {
    reset()
    const items = [tool('Read'), tool('Bash'), tool('Grep'), tool('Write')]
    const groups = groupItems(items, toolTable)
    const collapsed = computeAutoCollapsed({
      groups,
      tools: toolTable,
      running: true,
      windowSize: 1
    })
    expect(collapsed).toEqual([true, true, true, false])
  })

  it('默认窗口是 3', () => {
    expect(TOOL_WINDOW_SIZE).toBe(3)
  })
})

describe('groupTitle / groupDuration', () => {
  it('按形态与数量组词', () => {
    reset()
    const reads = groupItems([tool('Read'), tool('LS')], toolTable)[0]!
    expect(groupTitle(reads, toolTable)).toBe('读取了 2 个文件')

    reset()
    const one = groupItems([tool('Bash')], toolTable)[0]!
    expect(groupTitle(one, toolTable)).toBe('执行了 1 条命令')
  })

  it('累计组内耗时', () => {
    reset()
    const g = groupItems([tool('Read'), tool('LS')], toolTable)[0]!
    expect(groupDuration(g, toolTable)).toBe(1000) // 每项 500ms
  })

  it('thinking 组不计工具耗时', () => {
    expect(groupDuration([thinking('t')], {})).toBe(0)
  })
})

describe('summarize', () => {
  it('统计工具数、累计耗时、失败数与形态集合', () => {
    reset()
    const items = [thinking('t'), tool('Read'), tool('Bash', 'error')]
    const s = summarize(items, toolTable, 3)
    expect(s.toolCount).toBe(2) // thinking 不算工具
    expect(s.totalMs).toBe(1000)
    expect(s.errorCount).toBe(1)
    expect(s.fileChangeCount).toBe(3)
    expect(s.shapes).toEqual(['reasoning', 'read', 'command'])
  })

  it('形态按首次出现顺序去重', () => {
    reset()
    const s = summarize([tool('Read'), tool('Bash'), tool('LS')], toolTable)
    expect(s.shapes).toEqual(['read', 'command', 'read'].filter((v, i, a) => a.indexOf(v) === i))
  })

  it('空输入是零摘要', () => {
    const s = summarize([], {})
    expect(s).toEqual({
      toolCount: 0,
      totalMs: 0,
      errorCount: 0,
      fileChangeCount: 0,
      shapes: []
    })
  })
})

describe('decideWorkspace', () => {
  const d = (o: Parameters<typeof decideWorkspace>[0]): ReturnType<typeof decideWorkspace> =>
    decideWorkspace(o)

  it('正常结束 + 有结尾正文 + 项数够 → 收束', () => {
    expect(d({ outcome: 'ok', itemCount: 5, hasTrailingText: true })).toEqual({
      collapse: true,
      defaultOpen: false
    })
  })

  it('★ 没有结尾正文时不收束 —— 否则界面上只剩一个空壳,结论被藏没了', () => {
    expect(d({ outcome: 'ok', itemCount: 5, hasTrailingText: false }).collapse).toBe(
      false
    )
  })

  it('★ 被中断时不收束 —— 用户正要看「跑到哪一步停的」', () => {
    expect(d({ outcome: 'aborted', itemCount: 9, hasTrailingText: true }).collapse).toBe(
      false
    )
    expect(d({ outcome: 'error', itemCount: 9, hasTrailingText: true }).collapse).toBe(
      false
    )
  })

  it('run 还在跑时不收束', () => {
    expect(d({ outcome: 'running', itemCount: 9, hasTrailingText: true }).collapse).toBe(
      false
    )
  })

  it('★ 只有一项时也收束 —— 完成后的过程统一归入摘要行', () => {
    expect(d({ outcome: 'ok', itemCount: 1, hasTrailingText: true }).collapse).toBe(true)
  })

  it('★ 曾有失败工具调用也不再强制默认展开 —— 否则后台任务(如更新检查)失败一次,「用时」面板从此永久展开', () => {
    expect(d({ outcome: 'ok', itemCount: 5, hasTrailingText: true })).toEqual({
      collapse: true,
      defaultOpen: false
    })
  })
})

describe('workspaceTitleParts', () => {
  it('指标顺序固定:数量 → 时间 → 产出 → 异常', () => {
    const parts = workspaceTitleParts(
      { toolCount: 8, totalMs: 12_400, errorCount: 2, fileChangeCount: 3, shapes: [] },
      formatDuration
    )
    expect(parts.normal).toEqual(['8 个工具', '累计 12s', '3 个文件变更'])
    expect(parts.danger).toBe('2 个失败')
  })

  it('零值指标不出现', () => {
    const parts = workspaceTitleParts(
      { toolCount: 1, totalMs: 0, errorCount: 0, fileChangeCount: 0, shapes: [] },
      formatDuration
    )
    expect(parts.normal).toEqual(['1 个工具'])
    expect(parts.danger).toBeUndefined()
  })
})
