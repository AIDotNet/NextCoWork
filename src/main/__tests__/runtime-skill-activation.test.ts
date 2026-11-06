/**
 * 提炼会话写完 SKILL.md 之后的**激活**与**下一轮**之间的次序 —— 回归测试。
 *
 * 这条链路上有三个只会静默失效的点,每一个都在这里钉住:
 * 1. 等待点必须在 run_end **之前**挂上。挂晚了,同一条会话排着队的下一轮会在激活
 *    完成前开跑 —— 症状是「Skill 写好了,紧接着的追问里它还是不存在」,且零报错。
 * 2. 激活失败(监听器抛错)也必须放走等待者,否则下一轮永远卡在等待点上,
 *    用户看到的是「消息发出去了,一直没有反应」。
 * 3. 提炼会话的白名单以工作区设置为准、普通 run 仍用 `req` 快照。前者错了就是两份
 *    真源打架;后者错了则是用户跑到一半改设置会改变这一轮的工具目录。
 *
 * 用的是**真实磁盘**(临时工作区 + 真实 SKILL.md):要证明的正是「下一轮扫到的目录里
 * 有刚写的那条」,换成假的 fs 就把要测的东西自己替掉了。
 */
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { ContentPart } from '../../shared/agent/message'
import type { RunRequest } from '../../shared/agent/run-request'
import type { WorkspaceSettings } from '../../shared/domain/workspace'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../shared/domain/workspace'
import { closeDatabase } from '../db'
import { nodeHost, type KernelHost } from '../kernel/host'
import { RunHandle, runs } from '../kernel/run-registry'
import { recordChange, resetChangeRecorderForTest } from '../kernel/tool/builtin/change-recorder'
import { chunk, sse } from '../kernel/upstream/__tests__/openai-fixtures'
import { installHost, resetRuntimeForTest, runAgent, setSkillWrittenListener } from '../runtime'
import { store } from '../state/store'

const WORKSPACE = 'workspace'
const EXTRACTION_SESSION = 'extraction'
const CHAT_SESSION = 'chat'
const MODEL = 'deepseek-skill-test'

let root = ''

/** 工作区里的一条项目 Skill。真写盘 —— 扫描器读的就是它。 */
function writeProjectSkill(name: string, description: string): void {
  mkdirSync(join(root, '.next-cowork', 'skills', name), { recursive: true })
  writeFileSync(
    skillFile(name),
    `---\nname: ${name}\ndescription: ${description}\n---\n\n# ${name}\n\nUse ${name} when the task matches.\n`
  )
}

function skillFile(name: string): string {
  return join(root, '.next-cowork', 'skills', name, 'SKILL.md')
}

/**
 * 模拟 Write/Edit 的写盘记录。`change-recorder` 是写盘的唯一收口,提炼那条路完全靠它
 * (`writtenProjectSkillNames`)—— 用它而不是真让模型走一次函数调用:这里要钉的是
 * runtime 收到改动之后的行为,不是 SSE 解码器能不能解析一次 tool_call。
 */
function recordSkillWrite(runId: string, name: string): void {
  recordChange(runId, {
    abs: skillFile(name), relPath: `.next-cowork/skills/${name}/SKILL.md`,
    before: null, after: '---\n', inWorkspace: true
  })
}

/** 模拟 `ipc/skills.ts` 的 `activateWrittenSkills`:把刚写的 Skill 写进显式白名单。 */
function activateInWorkspace(name: string): void {
  const workspace = store.getWorkspace(WORKSPACE)
  if (workspace === undefined) throw new Error('工作区不存在')
  const ids = workspace.settings.activeSkillIds.filter((id) => id !== name)
  store.putWorkspace({
    ...workspace,
    settings: { ...workspace.settings, activeSkillIds: [...ids, name], skillSelectionMode: 'explicit' }
  })
}

/** 系统提示词里那份 Skill 目录 —— 每行是 ``- `name` — 描述``,与提炼头块里的清单不同形。 */
function catalogOf(system: string): string[] {
  return [...system.matchAll(/^- `([^`]+)` — /gmu)].map((match) => match[1] ?? '')
}

function input(): ContentPart[] {
  return [{ type: 'text', text: '把刚才那段改动提炼成 Skill。' }]
}

function request(overrides: Partial<RunRequest> = {}): RunRequest {
  return {
    runId: 'run', sessionId: CHAT_SESSION, workspaceId: WORKSPACE, model: MODEL, depth: 0,
    input: input(), thinking: 'off', mode: 'normal', permissionMode: 'ask', webSearch: false,
    skillIds: [], ...overrides
  }
}

function extractionRequest(overrides: Partial<RunRequest> = {}): RunRequest {
  return request({ runId: 'extraction-run', sessionId: EXTRACTION_SESSION, mode: 'code', ...overrides })
}

function childRequest(parentRunId: string, runId: string, parentSessionId: string): RunRequest {
  return {
    runId, sessionId: `${parentSessionId}:sub:${runId}`, workspaceId: WORKSPACE,
    parentRunId, parentSessionId, depth: 1, input: input(), mode: 'code', thinking: 'off',
    permissionMode: 'full', webSearch: false, skillIds: [], model: MODEL, agentType: 'general-purpose'
  }
}

/** 放几个宏任务过去 —— 用来断言「这段时间里**没有**发生某件事」。 */
async function settle(): Promise<void> {
  await new Promise<void>((resolve) => setTimeout(resolve, 60))
}

async function setup(settings: Partial<WorkspaceSettings> = {}): Promise<{ systems: string[]; host: KernelHost }> {
  root = mkdtempSync(join(tmpdir(), 'ncw-skill-activation-'))
  writeProjectSkill('alpha', 'Alpha applies to pricing work.')
  writeProjectSkill('beta', 'Beta applies to billing work.')

  const base = nodeHost()
  const systems: string[] = []
  const host = nodeHost({
    // 全局层与受管说明都落在临时目录里 —— 否则会扫到开发机上真实的那一份。
    paths: { ...base.paths, userData: () => join(root, 'userdata'), temp: () => join(root, 'tmp') },
    spawn: async () => ({ code: 1, stdout: '', stderr: '' }),
    fetch: vi.fn(async (_url, init) => {
      const body = JSON.parse(String(init?.body)) as { messages?: Array<{ role?: string; content?: unknown }> }
      systems.push((body.messages ?? [])
        .filter((message) => message.role === 'system' && typeof message.content === 'string')
        .map((message) => String(message.content))
        .join('\n'))
      return sse(chunk({ content: '好的。' }, 'stop'), '[DONE]')
    })
  })
  installHost(host)
  await host.secrets.set('skill-test-key', 'not-a-real-api-key')

  store.putWorkspace({
    id: WORKSPACE, name: 'Test', rootPath: root, createdAt: 1, lastOpenedAt: 1,
    settings: { ...DEFAULT_WORKSPACE_SETTINGS, activeSkillIds: ['alpha'], skillSelectionMode: 'explicit', ...settings }
  })
  store.putProvider({
    id: 'skill-provider', name: 'Test', protocol: 'openai-chat', baseUrl: 'https://skill-test.invalid',
    credentialRef: 'skill-test-key', priority: 0, enabled: true
  })
  store.putAlias({
    alias: MODEL, upstreamModel: MODEL, providerId: 'skill-provider',
    capabilities: { tools: true, vision: false, thinking: true, caching: false },
    contextWindow: 128_000, maxOutputTokens: 8_192
  })
  // 标题是手写的 ⇒ 标题生成器不插手,`systems` 里因此只有正文请求。
  store.createSession({ id: EXTRACTION_SESSION, workspaceId: WORKSPACE, title: '提炼 Skill', skillSource: { sessionId: 'missing-source' } })
  store.createSession({ id: CHAT_SESSION, workspaceId: WORKSPACE, title: '普通对话' })
  return { systems, host }
}

beforeEach(() => {
  resetRuntimeForTest()
  closeDatabase()
  resetChangeRecorderForTest()
})

afterEach(() => {
  resetRuntimeForTest()
  closeDatabase()
  resetChangeRecorderForTest()
  if (root !== '') rmSync(root, { recursive: true, force: true })
  root = ''
})

describe('提炼会话的 Skill 激活屏障', () => {
  it('does not strand the workspace barrier when extraction startup is cancelled', async () => {
    const { systems, host } = await setup()
    let releaseScan!: () => void
    const stalled = new Promise<void>((resolve) => { releaseScan = resolve })
    let scanning = false
    const exists = vi.spyOn(host.fs, 'exists').mockImplementationOnce(async () => {
      scanning = true
      await stalled
      return false
    })
    const first = extractionRequest({ runId: 'cancel-startup' })
    const firstHandle = new RunHandle(first)
    const firstRun = runAgent(firstHandle, first)
    try {
      await vi.waitFor(() => expect(scanning).toBe(true))
      firstHandle.abort({ by: 'user' })
      await firstRun
      expect(firstHandle.status).toBe('aborted')
    } finally {
      releaseScan()
      exists.mockRestore()
    }
    const next = request({ runId: 'ordinary-after-cancel' })
    const nextHandle = new RunHandle(next)
    const nextRun = runAgent(nextHandle, next)
    try {
      await vi.waitFor(() => expect(systems).toHaveLength(1), { timeout: 1_000 })
      await nextRun
    } finally {
      nextHandle.abort({ by: 'user' })
      await nextRun
    }
  })

  it('does not register an unowned barrier when startup throws before the Agent exists', async () => {
    const { systems } = await setup()
    const history = vi.spyOn(store, 'getHistory').mockImplementationOnce(() => { throw new Error('startup failed') })
    const first = extractionRequest({ runId: 'failed-startup' })
    const firstHandle = new RunHandle(first)
    try {
      await expect(runAgent(firstHandle, first)).rejects.toThrow('startup failed')
      // IPC 启动器会在 driver 拒绝后 finish;这里重放同一条路径。
      firstHandle.finish('error')
    } finally {
      history.mockRestore()
    }
    const next = request({ runId: 'ordinary-after-error' })
    const nextHandle = new RunHandle(next)
    const nextRun = runAgent(nextHandle, next)
    try {
      await vi.waitFor(() => expect(systems).toHaveLength(1), { timeout: 1_000 })
      await nextRun
    } finally {
      nextHandle.abort({ by: 'user' })
      await nextRun
    }
  })

  it('holds the next primary run until the extraction activation finishes', async () => {
    const { systems } = await setup()
    let unblock!: () => void
    const gate = new Promise<void>((resolve) => { unblock = resolve })
    const activations: Array<{ sessionId: string; names: string[] }> = []
    setSkillWrittenListener(async (change) => {
      activations.push({ sessionId: change.sessionId, names: change.names })
      await gate
      activateInWorkspace('beta')
    })

    // 第一轮:提炼会话写了 beta/SKILL.md(子 run 代写的场景见下面那条用例)。
    recordSkillWrite('run-1', 'beta')
    const first = extractionRequest({ runId: 'run-1' })
    const firstHandle = new RunHandle(first)
    const second = extractionRequest({ runId: 'run-2', skillIds: ['alpha'], skillSelectionMode: 'explicit' })
    const secondHandle = new RunHandle(second)
    let secondRun: Promise<void> | undefined
    // 需求:收到 run_end 立刻启动,不能等激活监听器已进入后才启动,否则测不出登记过晚。
    const unsubscribe = firstHandle.on((event) => {
      if (event.type === 'run_end') secondRun = runAgent(secondHandle, second)
    })
    const firstRun = runAgent(firstHandle, first)
    try {
      await vi.waitFor(() => expect(activations).toEqual([{ sessionId: EXTRACTION_SESSION, names: ['beta'] }]))
      expect(catalogOf(systems[0] ?? ''), 'beta 还没激活').toEqual(['alpha'])
      await settle()
      expect(secondRun).toBeDefined()
      expect(systems, '下一轮在激活完成前就发了上游请求').toHaveLength(1)
      expect(secondHandle.status).toBe('running')
    } finally {
      unsubscribe()
      unblock()
      await Promise.all([firstRun, secondRun])
    }
    expect(systems).toHaveLength(2)
    expect(catalogOf(systems[1] ?? ''), '激活之后的目录要包含刚写的 beta').toEqual(['alpha', 'beta'])
  })

  it('releases the wait when activation fails so the next run is not stuck', async () => {
    const { systems } = await setup()
    setSkillWrittenListener(async () => { throw new Error('activation exploded') })

    recordSkillWrite('run-1', 'beta')
    const first = extractionRequest({ runId: 'run-1' })
    await runAgent(new RunHandle(first), first)

    // 不挂死:激活抛了,等待者照样被放走(否则这行会一路超时)。
    const second = extractionRequest({ runId: 'run-2' })
    await runAgent(new RunHandle(second), second)
    expect(systems).toHaveLength(2)
    expect(catalogOf(systems[1] ?? ''), '激活失败只该少一条 Skill,不该改变别的').toEqual(['alpha'])
  })

  it('reads the workspace selection for the extraction line only', async () => {
    const { systems } = await setup({ activeSkillIds: ['alpha', 'beta'] })

    // 普通 run:req 那份快照说了算,用户跑到一半改设置不该改变这一轮的工具目录。
    const ordinary = request({ runId: 'run-chat', skillIds: ['alpha'], skillSelectionMode: 'explicit' })
    await runAgent(new RunHandle(ordinary), ordinary)
    expect(catalogOf(systems[0] ?? '')).toEqual(['alpha'])

    // 提炼会话:工作区设置说了算 —— 渲染层还没收到 `workspace:changed` 时 req 是旧的。
    const extraction = extractionRequest({ runId: 'run-extraction', skillIds: ['alpha'], skillSelectionMode: 'explicit' })
    await runAgent(new RunHandle(extraction), extraction)
    expect(catalogOf(systems[1] ?? '')).toEqual(['alpha', 'beta'])
  })

  it('activates for the extraction root when a subagent run wrote the Skill', async () => {
    const { systems } = await setup()
    const seen: Array<{ sessionId: string; names: string[] }> = []
    setSkillWrittenListener(async (change) => { seen.push({ sessionId: change.sessionId, names: change.names }) })

    // 父 handle 在注册表里:改动要 roll-up 到它,归属判定则靠落盘的 parentSessionId。
    runs.create(extractionRequest({ runId: 'run-root' }))
    const child = childRequest('run-root', 'run-root:sub:1', EXTRACTION_SESSION)
    recordSkillWrite(child.runId, 'beta')
    await runAgent(new RunHandle(child), child)

    expect(seen).toEqual([{ sessionId: EXTRACTION_SESSION, names: ['beta'] }])
    expect(systems).toHaveLength(1)
  })

  it('does not activate for a subagent of an ordinary conversation', async () => {
    await setup()
    const seen: unknown[] = []
    setSkillWrittenListener(async (change) => { seen.push(change) })

    runs.create(request({ runId: 'run-chat' }))
    const child = childRequest('run-chat', 'run-chat:sub:1', CHAT_SESSION)
    recordSkillWrite(child.runId, 'beta')
    await runAgent(new RunHandle(child), child)

    expect(seen, '普通会话派出去的子代理写 Skill 不该触发自动启用').toEqual([])
  })

  it('does not make a subagent wait on the barrier its own parent registered', async () => {
    const { systems } = await setup()
    let unblock!: () => void
    const gate = new Promise<void>((resolve) => { unblock = resolve })
    const seen: string[][] = []
    setSkillWrittenListener(async (change) => {
      seen.push([...change.names])
      // 只有父 run 那一笔挂住;子 run 那一笔立刻返回。
      if (change.names.includes('alpha')) await gate
    })

    recordSkillWrite('run-root', 'alpha')
    const parent = extractionRequest({ runId: 'run-root' })
    runs.create(parent)
    const parentRun = runAgent(new RunHandle(parent), parent)
    await vi.waitFor(() => expect(seen).toEqual([['alpha']]))

    /*
      ★ 父 run 的等待点还挂着(run_end 已经发出、激活没做完)。子 run 若也去等同一个
      工作区的等待点,就是父子互等 —— 表现为整条会话卡死到用户点停止为止。
    */
    const child = childRequest('run-root', 'run-root:sub:1', EXTRACTION_SESSION)
    recordSkillWrite(child.runId, 'beta')
    await runAgent(new RunHandle(child), child)
    expect(seen).toEqual([['alpha'], ['beta']])

    unblock()
    await parentRun
    expect(systems).toHaveLength(2)
  })
})
