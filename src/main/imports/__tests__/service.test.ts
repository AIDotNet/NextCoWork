/**
 * 导入闭环 —— 计划第 3 步要求「最先证明」的那三件事:
 *
 * 1. **导入两次仍一份**(会话数、消息数、目标 id 都不变);
 * 2. **可继续**(落进 `messages` 表,`store.getHistory` 读得到,不是只画在 UI 上);
 * 3. **继续之后源更新不覆盖**(detached 是永久的)。
 *
 * 用真库文件不用 `:memory:`,和 `roundtrip.test.ts` 同一个理由:这里要断言的
 * 恰恰是跨越提交边界之后还成立的东西。
 */
import { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import { closeDatabase, openDatabase } from '../../db/index'
import { store } from '../../state/store'
/*
  ★ **静态 import,而且 afterEach 里绝不 `vi.resetModules()`。**

  服务经由 `state/store` → `db/repo` → `db/index` 拿到的是那一个**模块级**的
  数据库句柄。resetModules 会让 `await import('../service')` 得到一整棵新的
  模块树,里面那个 `db/index` 从来没被 `openDatabase` 过 —— 症状是
  「刚写进去的来源行查不到」,而看起来完全像是 SQL 写错了。
*/
import * as service from '../service'

/*
  内联图片的落盘单独由附件服务测试覆盖；导入闭环使用固定附件回执。
  mock 必须跟随生产依赖路径，不能在服务提取后继续 mock 已不使用的 IPC 模块。
*/
vi.mock('../../services/attachments', () => ({
  uploadAttachment: (req: { mime: string }) => ({
    id: 'att',
    scope: 'session',
    displayName: 'x',
    mime: req.mime,
    size: 1,
    checksum: 'c',
    createdAt: 0,
    url: 'ncw://attachments/session/x/att.png'
  })
}))

let dbDir = ''
let sourceDir = ''
let projectDir = ''
/** 单个用例自己开的临时目录,afterEach 一并清掉。 */
let extraDirs: string[] = []

/** 最小往返:一轮文本 + 一对工具。第 1 步验收的那条夹具,这里复用形状。 */
function transcript(sessionId: string, extra: unknown[] = []): string {
  const records: unknown[] = [
    {
      uuid: 'u1',
      parentUuid: null,
      type: 'user',
      cwd: projectDir,
      sessionId,
      timestamp: '2025-01-01T00:00:00.000Z',
      message: { role: 'user', content: '看一下 README' }
    },
    {
      uuid: 'a1',
      parentUuid: 'u1',
      type: 'assistant',
      timestamp: '2025-01-01T00:00:01.000Z',
      message: {
        role: 'assistant',
        model: 'claude-sonnet-4-5',
        content: [
          { type: 'text', text: '好的。' },
          { type: 'tool_use', id: 'toolu_1', name: 'Read', input: { file_path: 'README.md' } }
        ]
      }
    },
    {
      uuid: 'r1',
      parentUuid: 'a1',
      type: 'user',
      timestamp: '2025-01-01T00:00:02.000Z',
      message: {
        role: 'user',
        content: [{ type: 'tool_result', tool_use_id: 'toolu_1', content: '# 标题', is_error: false }]
      }
    },
    ...extra
  ]
  return records.map((r) => JSON.stringify(r)).join('\n') + '\n'
}

beforeEach(() => {
  closeDatabase()
  dbDir = mkdtempSync(join(tmpdir(), 'ncw-import-db-'))
  openDatabase(dbDir)

  sourceDir = mkdtempSync(join(tmpdir(), 'ncw-cc-'))
  projectDir = mkdtempSync(join(tmpdir(), 'ncw-proj-'))
  mkdirSync(join(sourceDir, 'projects', 'encoded-proj'), { recursive: true })
})

afterEach(() => {
  closeDatabase()
  for (const dir of [dbDir, sourceDir, projectDir, ...extraDirs]) rmSync(dir, { recursive: true, force: true })
  extraDirs = []
  vi.useRealTimers()
})

/** 登记一个来源行并写好转录。返回 sourceId。 */
function seedSource(sessionId = 'sess-1', extra: unknown[] = []): string {
  writeFileSync(join(sourceDir, 'projects', 'encoded-proj', `${sessionId}.jsonl`), transcript(sessionId, extra))
  const now = Date.now()
  store.putImportSource({
    sourceId: 'src-test',
    kind: 'claude-code',
    configDir: sourceDir,
    origin: 'user-picked',
    syncEnabled: false,
    categories: ['chat', 'project'],
    projectKeys: [projectDir],
    status: 'off',
    diagnostics: [],
    createdAt: now,
    updatedAt: now
  })
  return 'src-test'
}

/** 建一个指向源项目路径的本地工作区 —— 聊天必须有目标工作区才能落地。 */
function seedWorkspace(): string {
  const now = Date.now()
  const ws = store.putWorkspace({
    id: 'ws-test',
    name: 'proj',
    rootPath: projectDir,
    settings: structuredClone(DEFAULT_WORKSPACE_SETTINGS),
    createdAt: now,
    lastOpenedAt: now
  })
  return ws.id
}

/** 跑一次完整的预览 + 全选提交,等作业结束。 */
async function importAll(sourceId: string): Promise<void> {
  const preview = await service.buildPreview(sourceId, `req-${String(Date.now())}`)
  const page = service.previewItems({ previewId: preview.previewId, offset: 0, limit: 100 })
  const chosen = page.items.filter((item) => item.status === 'new' || item.status === 'update')
  if (chosen.length === 0) return
  await service.applyImport({
    previewId: preview.previewId,
    itemIds: chosen.map((item) => item.id),
    workspaceTargets: [],
    requestId: `apply-${String(Date.now())}`
  })
  // applyImport 故意不 await 作业(关掉设置页任务要继续跑),这里等它收尾。
  await waitForIdle(sourceId)
}

async function waitForIdle(sourceId: string): Promise<void> {
  for (let i = 0; i < 200; i += 1) {
    const status = service.jobStatusFor(sourceId)
    if (status !== null && ['done', 'partial', 'failed', 'cancelled'].includes(status.phase)) return
    await new Promise((resolve) => setTimeout(resolve, 10))
  }
  throw new Error('导入作业没有在预期时间内结束')
}

describe('聊天导入闭环', () => {
  it('★ 源转录能编码成本地消息,并真的落进 messages 表', async () => {
    const sourceId = seedSource()
    const workspaceId = seedWorkspace()
    await importAll(sourceId)

    const sessions = store.listSessions(workspaceId)
    expect(sessions).toHaveLength(1)

    // ★ 走 `store.getHistory` —— 这正是 `runAgent` 读历史的那条路径。
    //   它读得到,才说明「可继续」不是一句 UI 上的假象。
    const history = store.getHistory(sessions[0]!.id)
    expect(history.map((m) => m.role)).toEqual(['user', 'assistant', 'user'])
    expect(history[1]?.parts.some((p) => p.type === 'tool_call')).toBe(true)
    expect(history[2]?.parts[0]).toMatchObject({ type: 'tool_result', callId: 'toolu_1' })
  })

  it('★★ 导入两次仍一份 —— 会话数、消息数、目标 id 全都不变', async () => {
    const sourceId = seedSource()
    const workspaceId = seedWorkspace()

    await importAll(sourceId)
    const first = store.listSessions(workspaceId)
    const firstIds = store.getHistory(first[0]!.id).map((m) => m.id)

    await importAll(sourceId)
    const second = store.listSessions(workspaceId)

    expect(second).toHaveLength(1)
    expect(second[0]!.id).toBe(first[0]!.id)
    expect(store.getHistory(second[0]!.id).map((m) => m.id)).toEqual(firstIds)
  })

  it('源没变时第二次是 skipped,不白写一遍', async () => {
    const sourceId = seedSource()
    seedWorkspace()
    await importAll(sourceId)

    const preview = await service.buildPreview(sourceId, 'again')
    const page = service.previewItems({ previewId: preview.previewId, category: 'chat', offset: 0, limit: 10 })
    expect(page.items[0]?.status).toBe('exists')
  })

  it('★★ 续聊之后源更新不覆盖 —— detached 是永久的', async () => {
    const sourceId = seedSource()
    const workspaceId = seedWorkspace()
    await importAll(sourceId)

    const sessionId = store.listSessions(workspaceId)[0]!.id
    const before = store.getHistory(sessionId).length

    // 模拟一次「接受顶层 run」:`ipc/agent.startRun` 在启动入口调的就是这个。
    expect(store.detachImportedSession(sessionId)).toBeGreaterThan(0)

    // 源侧后来又长了一轮
    seedSource('sess-1', [
      {
        uuid: 'a2',
        parentUuid: 'r1',
        type: 'assistant',
        timestamp: '2025-01-01T00:00:03.000Z',
        message: { role: 'assistant', content: [{ type: 'text', text: '源侧新增的一句' }] }
      }
    ])

    await importAll(sourceId)

    const after = store.getHistory(sessionId)
    expect(after).toHaveLength(before)
    expect(JSON.stringify(after)).not.toContain('源侧新增的一句')
  })

  it('★ 本地删除留 tombstone,下一轮不会把会话复活', async () => {
    const sourceId = seedSource()
    const workspaceId = seedWorkspace()
    await importAll(sourceId)

    const sessionId = store.listSessions(workspaceId)[0]!.id
    store.deleteSession(sessionId)
    expect(store.listSessions(workspaceId)).toHaveLength(0)

    await importAll(sourceId)
    expect(store.listSessions(workspaceId)).toHaveLength(0)
  })

  it('没有目标工作区时不猜、不落地 —— 状态是 needs-target', async () => {
    const sourceId = seedSource()
    // 刻意不建工作区
    const preview = await service.buildPreview(sourceId, 'no-ws')
    const page = service.previewItems({ previewId: preview.previewId, category: 'chat', offset: 0, limit: 10 })
    expect(page.items[0]?.status).toBe('needs-target')
    expect(page.items[0]?.defaultSelected).toBe(false)
  })

  it('★ 建了工作区就要通知出去,否则切换器里看不到新项目', async () => {
    /*
      导入确实把工作区写进了库,`workspace:list` 也查得到 —— 但渲染层那份列表
      只靠 `workspace:changed` 更新。少这一跳的表现是「导入报告说新增了项目,
      左上角切换器里却没有」,重启之后又有了,看起来像时好时坏。
    */
    const sourceId = seedSource()
    let notified = 0
    service.setImportWorkspaceNotifier(() => {
      notified += 1
    })
    try {
      await importAll(sourceId)
      expect(notified).toBeGreaterThan(0)
    } finally {
      service.setImportWorkspaceNotifier(() => {})
    }
  })

  it('★★ getState 不能把 detect 刚算出来的计数清成 0', async () => {
    /*
      真机上报出来的 bug:目录和转录都在,界面却显示「0 个项目 · 0 个会话」。

      顺序是这样的 —— 页面挂载调 `detect()`,拿到真计数并显示;而 `detect()`
      自己也 announce 了一次,200ms 后 `imports:changed` 到达,页面改调
      `getState()`,那条路径没有 detection 参数,计数被 0 覆盖。
      「扫不到」是假象,扫到了,被自己的事件清掉了。
    */
    seedSource()
    const detected = await service.detectImportSource(sourceDir)
    expect(detected.detection.sessionCount).toBe(1)

    const followUp = service.getImportSourceState(detected.detection.sourceId)
    expect(followUp.detection.sessionCount).toBe(detected.detection.sessionCount)
    expect(followUp.detection.projectCount).toBe(detected.detection.projectCount)
  })

  it('批次与明细都落了库,历史页读得到', async () => {
    const sourceId = seedSource()
    seedWorkspace()
    await importAll(sourceId)

    const history = service.importHistory(0, 50)
    expect(history.total).toBe(1)
    const batch = history.batches[0]!
    expect(batch.trigger).toBe('manual')
    expect(batch.counts.imported).toBeGreaterThan(0)

    const items = service.importHistoryItems(batch.id, 0, 50)
    expect(items.items.some((item) => item.category === 'chat' && item.targetKind === 'session')).toBe(true)
  })

  it('预览过期之后提交被拒,不拿一份可能已经变了的快照往下写', async () => {
    const sourceId = seedSource()
    seedWorkspace()
    const preview = await service.buildPreview(sourceId, 'ttl')
    const page = service.previewItems({ previewId: preview.previewId, offset: 0, limit: 100 })

    vi.setSystemTime(Date.now() + 10 * 60 * 1000)
    await expect(
      service.applyImport({
        previewId: preview.previewId,
        itemIds: page.items.map((i) => i.id),
        workspaceTargets: [],
        requestId: 'expired'
      })
    ).rejects.toThrow()
    vi.useRealTimers()
  })
})

describe('项目目录去重', () => {
  /*
    真机上的症状:切换器里并排两个同名项目,一个有会话一个空的。

    成因是同一个目录在三个来源里长得不一样 —— Claude Code 的转录写的是会话启动时
    的 `cwd`(原样,可能是符号链接),Codex 的 `config.toml` 写的是用户手敲的路径,
    目录选择器给的是 `realpathSync.native` 之后的路径。只比字符串,就会各建一份。
  */

  /** 写一条 `cwd` 指定的转录,并登记一个独立的 claude-code 来源。 */
  function seedSourceWithCwd(sourceId: string, cwd: string): string {
    const dir = mkdtempSync(join(tmpdir(), 'ncw-cc-alt-'))
    extraDirs.push(dir)
    mkdirSync(join(dir, 'projects', 'encoded-proj'), { recursive: true })
    const records = [
      {
        uuid: 'u1', parentUuid: null, type: 'user', cwd, sessionId: sourceId,
        timestamp: '2025-01-01T00:00:00.000Z',
        message: { role: 'user', content: '看一下 README' }
      },
      {
        uuid: 'a1', parentUuid: 'u1', type: 'assistant', timestamp: '2025-01-01T00:00:01.000Z',
        message: { role: 'assistant', model: 'claude-sonnet-4-5', content: [{ type: 'text', text: '好的。' }] }
      }
    ]
    writeFileSync(
      join(dir, 'projects', 'encoded-proj', `${sourceId}.jsonl`),
      records.map((r) => JSON.stringify(r)).join('\n') + '\n'
    )
    const now = Date.now()
    store.putImportSource({
      sourceId, kind: 'claude-code', configDir: dir, origin: 'user-picked', syncEnabled: false,
      categories: ['chat', 'project'], projectKeys: [], status: 'off', diagnostics: [],
      createdAt: now, updatedAt: now
    })
    return sourceId
  }

  /** 指向 `projectDir` 的符号链接 —— 和真实路径是同一个目录。 */
  function linkToProject(): string {
    const dir = mkdtempSync(join(tmpdir(), 'ncw-link-'))
    extraDirs.push(dir)
    const link = join(dir, 'proj')
    symlinkSync(projectDir, link)
    return link
  }

  it('★★ 经符号链接记录的 cwd,复用已有工作区而不是再建一个', async () => {
    seedWorkspace() // rootPath = 真实路径
    const sourceId = seedSourceWithCwd('src-link', linkToProject())

    await importAll(sourceId)

    const workspaces = store.listWorkspaces()
    expect(workspaces).toHaveLength(1)
    expect(workspaces[0]!.id).toBe('ws-test')
    // 会话要真的落进那个复用的工作区,而不是被判成「没有目标」整条跳过。
    expect(store.listSessions('ws-test')).toHaveLength(1)
  })

  it('★★ 两个来源指向同一个目录,只建一个工作区', async () => {
    // 谁都没有现成工作区:第一个来源建,第二个来源必须认出来。
    const first = seedSourceWithCwd('src-real', projectDir)
    await importAll(first)
    // 头一趟的预览是在「还没有工作区」时算的,聊天那条是 needs-target;
    // 工作区已经由项目那条建出来了,再扫一次它才有落脚点。
    await importAll(first)
    expect(store.listWorkspaces()).toHaveLength(1)

    await importAll(seedSourceWithCwd('src-alias', linkToProject()))

    const workspaces = store.listWorkspaces()
    expect(workspaces).toHaveLength(1)
    expect(store.listSessions(workspaces[0]!.id)).toHaveLength(2)
  })
})
