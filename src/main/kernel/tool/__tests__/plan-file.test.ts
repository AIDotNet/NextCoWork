import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { PLAN_FILE_MAX_BYTES } from '../../../../shared/domain/plan-file'
import { createWorkspacePaths } from '../../../environment/paths'
import { activePlanFor, leavePlan, planToolAllowList } from '../../plan-run'
import { nodeHost } from '../../host'
import { editTool, writeTool } from '../builtin/fs'
import { enterPlanModeTool, exitPlanModeTool } from '../builtin/plan-file'
import type { ToolContext } from '../registry'

const WORKFLOW_TOOLS = ['EnterPlanMode', 'Write', 'Edit', 'ExitPlanMode', 'Read']

let root = ''
let runId = ''
let sessionId = ''

beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), 'nextcowork-plan-'))
  runId = `run-${Date.now()}`
  sessionId = `session-${Date.now()}`
})

afterEach(() => {
  leavePlan({ runId, sessionId })
  rmSync(root, { recursive: true, force: true })
})

function context(overrides: Partial<ToolContext> = {}): ToolContext {
  const base = nodeHost()
  return {
    workspaceRoot: root,
    signal: new AbortController().signal,
    permissionMode: 'full',
    depth: 0,
    callId: 'call-1',
    runId,
    sessionId,
    workspaceId: 'workspace-1',
    host: { ...base, path: createWorkspacePaths(base.fs, process.platform) },
    emit: () => undefined,
    ...overrides
  }
}

describe('file-backed Plan mode', () => {
  it('creates one Markdown file and changes the available workflow tools by phase', async () => {
    const before = planToolAllowList({ runId, sessionId }, WORKFLOW_TOOLS)
    expect(before).toEqual(['EnterPlanMode', 'Read'])

    // Local runs provide KernelHost without WorkspaceHost.path; this is the default desktop path.
    const result = await enterPlanModeTool.execute({ reason: 'Requirements are clear.' }, context({ host: nodeHost() }))
    expect(result.isError).toBe(false)
    const active = activePlanFor({ runId, sessionId })
    expect(active?.path).toMatch(/^\.plan\/[0-9A-HJKMNP-TV-Z]{26}\.md$/)
    expect(readFileSync(active!.absolutePath, 'utf8')).toBe('')
    expect(planToolAllowList({ runId, sessionId }, WORKFLOW_TOOLS))
      .toEqual(['Write', 'Edit', 'ExitPlanMode', 'Read'])

    const second = await enterPlanModeTool.execute({}, context())
    expect(second.isError).toBe(true)
    expect(second.output.content).toContain('already active')
  })

  it('keeps the plan and its write tools available in later runs of the same session', async () => {
    await enterPlanModeTool.execute({}, context())
    const active = activePlanFor({ runId, sessionId })!

    // 下一轮用户消息 = 新的 runId,同一条会话
    const nextRunId = `${runId}-next`
    expect(activePlanFor({ runId: nextRunId, sessionId })?.planId).toBe(active.planId)
    expect(planToolAllowList({ runId: nextRunId, sessionId }, WORKFLOW_TOOLS))
      .toEqual(['Write', 'Edit', 'ExitPlanMode', 'Read'])

    // 另一条会话不受影响 —— 它还没有计划文件
    expect(activePlanFor({ runId: nextRunId, sessionId: 'other-session' })).toBeUndefined()
    expect(planToolAllowList({ runId: nextRunId, sessionId: 'other-session' }, WORKFLOW_TOOLS))
      .toEqual(['EnterPlanMode', 'Read'])
  })

  it('hard-fences Write and Edit to the active plan path', async () => {
    await enterPlanModeTool.execute({}, context())
    const active = activePlanFor({ runId, sessionId })!
    const fenced = context({ writeFileRestriction: active.absolutePath })

    const blocked = await writeTool.execute({ file_path: join(root, 'src.ts'), content: 'no' }, fenced)
    expect(blocked.isError).toBe(true)
    expect(blocked.output.content).toContain('only modify its active Markdown plan')

    const written = await writeTool.execute({ file_path: active.absolutePath, content: '# Plan\n\n1. Implement.' }, fenced)
    expect(written.isError).toBe(false)
    const edited = await editTool.execute({
      file_path: active.absolutePath,
      old_string: 'Implement.',
      new_string: 'Implement and verify.'
    }, fenced)
    expect(edited.isError).toBe(false)
    expect(readFileSync(active.absolutePath, 'utf8')).toContain('Implement and verify.')
  })

  it.each([
    { name: 'short plan', plan: '# Approved plan' },
    { name: '83062-byte UTF-8 plan', plan: '# Plan\n\n' + '步'.repeat(27_684) + '\n\n' },
    { name: '1 MiB plan', plan: 'x'.repeat(1024 * 1024) }
  ])('presents the saved $name and stops after approval', async ({ plan }) => {
    await enterPlanModeTool.execute({}, context())
    const active = activePlanFor({ runId, sessionId })!
    const written = await writeTool.execute({ file_path: active.absolutePath, content: plan }, context({ writeFileRestriction: active.absolutePath }))
    expect(written.isError).toBe(false)

    let presented: unknown
    const result = await exitPlanModeTool.execute({}, context({
      interact: async (request) => {
        presented = request
        return { id: 'interaction-1', kind: 'plan_approval', action: 'approve_current' }
      }
    }))
    expect(result.isError).toBe(false)
    expect(presented).toMatchObject({
      kind: 'plan_approval',
      planId: active.planId,
      path: active.path,
      plan
    })
    expect(result.stopRun).toBe(true)
    expect(JSON.parse(result.output.content)).toMatchObject({
      type: 'plan_file',
      planId: active.planId,
      path: active.path,
      action: 'approve_current'
    })
    // 批准即终点:下一条计划要重新 EnterPlanMode
    expect(activePlanFor({ runId, sessionId })).toBeUndefined()
  })

  it.each([
    { name: 'ASCII', plan: 'x'.repeat(PLAN_FILE_MAX_BYTES + 1) },
    { name: 'UTF-8', plan: '步'.repeat(Math.floor(PLAN_FILE_MAX_BYTES / 3) + 1) }
  ])('rejects an oversized $name plan before presenting it', async ({ plan }) => {
    await enterPlanModeTool.execute({}, context())
    const active = activePlanFor({ runId, sessionId })!
    let presented: unknown
    const ctx = context({
      interact: async (request) => {
        presented = request
        return { id: 'interaction-1', kind: 'plan_approval', action: 'approve_current' }
      }
    })
    await ctx.host.fs.writeFile(active.absolutePath, plan)

    const result = await exitPlanModeTool.execute({}, ctx)
    expect(result.isError).toBe(true)
    expect(result.output.content).toBe(`The plan file is too large (${Buffer.byteLength(plan)} bytes; limit ${PLAN_FILE_MAX_BYTES}).`)
    expect(presented).toBeUndefined()
    expect(result.stopRun).not.toBe(true)
    expect(activePlanFor({ runId, sessionId })?.planId).toBe(active.planId)
  })

  it('rejects a plan that exceeds the size limit while review is open', async () => {
    await enterPlanModeTool.execute({}, context())
    const active = activePlanFor({ runId, sessionId })!
    const ctx = context({
      interact: async () => {
        await ctx.host.fs.writeFile(active.absolutePath, 'x'.repeat(PLAN_FILE_MAX_BYTES + 1))
        return { id: 'interaction-1', kind: 'plan_approval', action: 'approve_current' }
      }
    })
    await ctx.host.fs.writeFile(active.absolutePath, '# Draft plan')

    const result = await exitPlanModeTool.execute({}, ctx)
    expect(result.isError).toBe(true)
    expect(result.output.content).toContain('became invalid before review completed')
    expect(result.stopRun).not.toBe(true)
    expect(activePlanFor({ runId, sessionId })?.planId).toBe(active.planId)
  })

  it('continues the same plan when the user requests a revision', async () => {
    await enterPlanModeTool.execute({}, context())
    const active = activePlanFor({ runId, sessionId })!
    await writeTool.execute({ file_path: active.absolutePath, content: '# Draft plan' }, context({ writeFileRestriction: active.absolutePath }))
    const result = await exitPlanModeTool.execute({}, context({
      interact: async () => ({ id: 'interaction-1', kind: 'plan_approval', action: 'request_revision', feedback: 'Add tests.' })
    }))
    expect(result.stopRun).toBe(false)
    expect(result.output.content).toContain('Add tests.')
    expect(activePlanFor({ runId, sessionId })?.planId).toBe(active.planId)
  })

  it('releases the plan when its file disappeared, so a new one can be started', async () => {
    await enterPlanModeTool.execute({}, context())
    const active = activePlanFor({ runId, sessionId })!
    rmSync(active.absolutePath, { force: true })

    const result = await exitPlanModeTool.execute({}, context({
      interact: async () => ({ id: 'interaction-1', kind: 'plan_approval', action: 'approve_current' })
    }))
    expect(result.isError).toBe(true)
    expect(result.output.content).toContain('no longer exists')
    expect(activePlanFor({ runId, sessionId })).toBeUndefined()
    expect(planToolAllowList({ runId, sessionId }, WORKFLOW_TOOLS)).toEqual(['EnterPlanMode', 'Read'])
  })
})
