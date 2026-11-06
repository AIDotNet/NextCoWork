import { describe, expect, it } from 'vitest'
import { emptyTranscript, type LiveBlock, type ToolCallState, type TranscriptState } from '../agent/transcript'
import {
  GRIND_STREAK, SLOW_TOOL_MS, SLOW_WAIT_MS,
  activityOf, activitySnapshotOf, whimsyBucketOf, type ActivitySnapshot
} from '../domain/activity'

/**
 * 相位选错了的表现全都很轻:那句装饰文案说的事和眼前的工具对不上。
 * 没有崩溃、没有报错,而且要特定的工具到达顺序才现形 —— 正好是肉眼盯不出来的那一类。
 */

function tool(name: string, status: ToolCallState['status'], startedAt?: number): ToolCallState {
  return { callId: name, name, input: {}, status, ...(startedAt === undefined ? {} : { startedAt }) }
}

function transcript(patch: Partial<TranscriptState>): TranscriptState {
  return { ...emptyTranscript(), ...patch }
}

function live(kind: LiveBlock['kind']): LiveBlock[] {
  return [{ index: 0, kind, text: '' }]
}

describe('activityOf', () => {
  it('工具在跑时用它的展示形态 —— 和时间线的分组是同一套', () => {
    for (const [name, phase] of [
      ['Read', 'read'], ['Grep', 'search'], ['Edit', 'mutate'],
      ['Bash', 'command'], ['WebFetch', 'network'], ['Task', 'orchestration']
    ] as const) {
      expect(activityOf(transcript({ tools: { [name]: tool(name, 'running') } }), false)).toBe(phase)
    }
  })

  it('★ 并行多个工具取最新开跑的那个 —— 而不是卡在第一个慢工具上', () => {
    const state = transcript({
      tools: {
        a: tool('Bash', 'running', 1000),
        b: tool('Read', 'running', 3000),
        c: tool('Grep', 'running', 2000)
      }
    })
    expect(activityOf(state, false)).toBe('read')
  })

  it('没有 startedAt 的旧转录退化成插入序的最后一个,而不是崩', () => {
    const state = transcript({ tools: { a: tool('Bash', 'running'), b: tool('Grep', 'running') } })
    expect(activityOf(state, false)).toBe('search')
  })

  it('只有跑着的算数 —— 已结束和还没开跑的工具都不参与', () => {
    const state = transcript({
      tools: { a: tool('Read', 'ok', 3000), b: tool('Bash', 'pending', 4000), c: tool('Grep', 'running', 1000) },
      live: []
    })
    expect(activityOf(state, false)).toBe('search')
  })

  it('没有工具在跑时看最后一个 live 块:思考 / 正文', () => {
    expect(activityOf(transcript({ live: live('thinking') }), false)).toBe('reasoning')
    expect(activityOf(transcript({ live: live('text') }), false)).toBe('writing')
  })

  it('★ 参数还在流(tool_use)时归通用档 —— 别抢在工具开跑之前描述它', () => {
    expect(activityOf(transcript({ live: live('tool_use') }), false)).toBe('working')
  })

  it('★ 前面的块流完了就不算数 —— 只看最后一个', () => {
    const state = transcript({
      live: [{ index: 0, kind: 'thinking', text: '想完了' }, { index: 1, kind: 'text', text: '在写' }]
    })
    expect(activityOf(state, false)).toBe('writing')
  })

  it('什么都没有时由 waitingForResponse 决定等待还是通用', () => {
    expect(activityOf(emptyTranscript(), true)).toBe('waiting')
    expect(activityOf(emptyTranscript(), false)).toBe('working')
  })
})

/** 同名工具会连着出现好几次,所以 callId 必须单独给 —— 用 `tool()` 会互相覆盖。 */
function call(
  id: string, name: string, status: ToolCallState['status'],
  at: { startedAt?: number; endedAt?: number } = {}
): ToolCallState {
  return { callId: id, name, input: {}, status, ...at }
}

function calls(...list: ToolCallState[]): Record<string, ToolCallState> {
  return Object.fromEntries(list.map((c) => [c.callId, c]))
}

describe('activitySnapshotOf 的场景维度', () => {
  it('连击只数尾部同名的那一串', () => {
    const state = transcript({
      tools: calls(
        call('1', 'Edit', 'ok', { startedAt: 1, endedAt: 2 }),
        call('2', 'Edit', 'ok', { startedAt: 3, endedAt: 4 }),
        call('3', 'Edit', 'running', { startedAt: 5 })
      )
    })
    expect(activitySnapshotOf(state, false).streak).toBe(3)
  })

  it('★ 中间插了别的工具就断 —— 交替排查不算「没完没了」', () => {
    const state = transcript({
      tools: calls(
        call('1', 'Edit', 'ok', { startedAt: 1, endedAt: 2 }),
        call('2', 'Read', 'ok', { startedAt: 3, endedAt: 4 }),
        call('3', 'Edit', 'running', { startedAt: 5 })
      )
    })
    expect(activitySnapshotOf(state, false).streak).toBe(1)
  })

  it('★ 还没开跑的调用不参与连击 —— 它们没有 startedAt,按 0 排会挤到队头把连击算断', () => {
    const state = transcript({
      tools: calls(
        call('1', 'Read', 'ok', { startedAt: 1, endedAt: 2 }),
        call('2', 'Read', 'running', { startedAt: 3 }),
        call('3', 'Bash', 'pending')
      )
    })
    expect(activitySnapshotOf(state, false).streak).toBe(2)
  })

  it('最近一次结束的工具报错了就算在善后,后面跑成功一个就不算了', () => {
    const failed = calls(
      call('1', 'Bash', 'ok', { startedAt: 1, endedAt: 2 }),
      call('2', 'Bash', 'error', { startedAt: 3, endedAt: 4 })
    )
    expect(activitySnapshotOf(transcript({ tools: failed }), false).recovering).toBe(true)
    const recovered = calls(...Object.values(failed), call('3', 'Read', 'ok', { startedAt: 5, endedAt: 6 }))
    expect(activitySnapshotOf(transcript({ tools: recovered }), false).recovering).toBe(false)
  })

  it('上下文压力分三档', () => {
    expect(activitySnapshotOf(emptyTranscript(), false).context).toBe('ok')
    const tight = transcript({ contextUsage: { used: 9, window: 10, shouldCompact: true } })
    expect(activitySnapshotOf(tight, false).context).toBe('tight')
    const compacting = transcript({
      contextUsage: { used: 9, window: 10, shouldCompact: true },
      contextStatus: { phase: 'compacting', trigger: 'auto' }
    })
    expect(activitySnapshotOf(compacting, false).context).toBe('compacting')
  })
})

describe('whimsyBucketOf', () => {
  function snapshot(patch: Partial<ActivitySnapshot> = {}): ActivitySnapshot {
    return { phase: 'working', streak: 1, recovering: false, context: 'ok', ...patch }
  }

  it('认识的工具说得比相位更具体', () => {
    for (const [tool, bucket] of [
      ['Read', 'read.file'], ['LS', 'read.dir'], ['Grep', 'search.grep'], ['Glob', 'search.glob'],
      ['Bash', 'command.bash'], ['Task', 'delegate.subagent'], ['CreateScheduledTask', 'schedule']
    ] as const) {
      expect(whimsyBucketOf(snapshot({ phase: 'working', tool, callId: 'c' }), 0), tool).toBe(bucket)
    }
  })

  it('★ 认不出来的工具(MCP / 插件)退回相位,不必在这里登记', () => {
    const state = snapshot({ phase: 'external', tool: 'mcp__github__create_pr', callId: 'c' })
    expect(whimsyBucketOf(state, 0)).toBe('external')
  })

  it('跑久了压过工具本身 —— 界面看着卡住的那一刻,说「还在跑」比说「在读文件」有用', () => {
    const state = snapshot({ phase: 'read', tool: 'Read', callId: 'c' })
    expect(whimsyBucketOf(state, SLOW_TOOL_MS - 1)).toBe('read.file')
    expect(whimsyBucketOf(state, SLOW_TOOL_MS)).toBe('slow.tool')
  })

  it('久等只说等首字节那一档 —— 思考和写正文本来就该慢慢来', () => {
    expect(whimsyBucketOf(snapshot({ phase: 'waiting' }), SLOW_WAIT_MS)).toBe('slow.wait')
    expect(whimsyBucketOf(snapshot({ phase: 'waiting' }), SLOW_WAIT_MS - 1)).toBe('waiting')
    expect(whimsyBucketOf(snapshot({ phase: 'reasoning' }), SLOW_WAIT_MS * 10)).toBe('reasoning')
  })

  it('刚翻过车、连着第 N 次,都压过工具本身', () => {
    expect(whimsyBucketOf(snapshot({ tool: 'Bash', callId: 'c', recovering: true }), 0)).toBe('recover')
    expect(whimsyBucketOf(snapshot({ tool: 'Edit', callId: 'c', streak: GRIND_STREAK }), 0)).toBe('grind')
    expect(whimsyBucketOf(snapshot({ tool: 'Edit', callId: 'c', streak: GRIND_STREAK - 1 }), 0)).toBe('mutate.edit')
  })

  it('★ 正在压缩压过一切 —— 这一轮此刻根本没在推进', () => {
    const state = snapshot({ phase: 'command', tool: 'Bash', callId: 'c', recovering: true, context: 'compacting' })
    expect(whimsyBucketOf(state, SLOW_TOOL_MS * 10)).toBe('context.compacting')
  })

  it('★ 上下文告急排在最后 —— 它一真就真到压缩为止,放前面会盖掉接下来的十几次调用', () => {
    expect(whimsyBucketOf(snapshot({ tool: 'Read', callId: 'c', context: 'tight' }), 0)).toBe('read.file')
    expect(whimsyBucketOf(snapshot({ phase: 'writing', context: 'tight' }), 0)).toBe('context.tight')
  })
})
