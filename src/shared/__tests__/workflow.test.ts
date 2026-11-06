import { describe, expect, it } from 'vitest'
import {
  layoutWorkflow,
  validateWorkflowGraph,
  workflowFromInput,
  workflowLayers,
  WORKFLOW_GAP_X,
  WORKFLOW_NODE_W
} from '../agent/workflow'

const n = (id: string, deps: string[] = []) => ({ id, title: id, subagent_type: 'x', prompt: 'p', depends_on: deps })

describe('workflowLayers', () => {
  it('按最长路径分层,层内保持入参顺序', () => {
    expect(workflowLayers([n('a'), n('c', ['a']), n('b', ['a']), n('d', ['b', 'c']), n('e', ['a'])])).toEqual([
      ['a'],
      ['c', 'b', 'e'],
      ['d']
    ])
  })
  it('有环返回 null', () => {
    expect(workflowLayers([n('a', ['b']), n('b', ['a'])])).toBeNull()
  })
})

describe('validateWorkflowGraph', () => {
  it('合法', () => expect(validateWorkflowGraph([n('a'), n('b', ['a'])])).toBeNull())
  it('重复 id', () => expect(validateWorkflowGraph([n('a'), n('a')])).toContain('Duplicate'))
  it('依赖自己', () => expect(validateWorkflowGraph([n('a', ['a'])])).toContain('itself'))
})

describe('layoutWorkflow', () => {
  it('横向分列,列内竖向居中', () => {
    const state = workflowFromInput({ title: 't', nodes: [n('a'), n('b', ['a']), n('c', ['a']), n('d', ['b', 'c'])] })!
    const { positions, width } = layoutWorkflow(state)
    expect(positions.b!.x).toBe(WORKFLOW_NODE_W + WORKFLOW_GAP_X)
    expect(positions.b!.y).toBeLessThan(positions.c!.y)
    // 单节点那两列落在两行的正中间
    expect(positions.a!.y).toBe((positions.b!.y + positions.c!.y) / 2)
    expect(width).toBe(3 * WORKFLOW_NODE_W + 2 * WORKFLOW_GAP_X)
  })
})

describe('workflowFromInput', () => {
  it('入参推出全部 pending 的结构', () => {
    const s = workflowFromInput({ title: 'T', nodes: [n('a'), n('b', ['a'])] })
    expect(s?.title).toBe('T')
    expect(s?.nodes.map((x) => [x.id, x.status, x.dependsOn])).toEqual([['a', 'pending', []], ['b', 'pending', ['a']]])
  })
  it('不成图的入参返回 null', () => {
    expect(workflowFromInput(null)).toBeNull()
    expect(workflowFromInput({ nodes: [] })).toBeNull()
    expect(workflowFromInput({ nodes: [n('a', ['b']), n('b', ['a'])] })).toBeNull()
  })
})
