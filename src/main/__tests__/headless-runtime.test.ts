import { mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { closeDatabase, openDatabase } from '../db'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../shared/domain/workspace'
import type { RunRequest } from '../../shared/agent/run-request'
import { DEMO_ALIAS, DEMO_ALIASES, DEMO_PROVIDER, demoHost } from '../kernel/upstream/demo'
import { runs } from '../kernel/run-registry'
import { store } from '../state/store'

// 不是 Electron mock：只要执行链尝试加载它，测试就必须失败。
vi.mock('electron', () => { throw new Error('Headless runtime imported Electron') })
import { installHost, resetRuntimeForTest, runAgent, shutdownEnvironments, shutdownMcp } from '../runtime'

let root = ''
beforeEach(() => {
  resetRuntimeForTest()
  closeDatabase()
  root = mkdtempSync(join(tmpdir(), 'ncw-headless-runtime-'))
  const workspace = join(root, 'workspace')
  mkdirSync(workspace)
  openDatabase(join(root, 'data'), { durability: 'full' })
  installHost(demoHost({ paths: {
    userData: () => join(root, 'data'), attachments: () => join(root, 'attachments'), temp: () => root
  } }, { chunkDelayMs: 0 }))
  store.putProvider(DEMO_PROVIDER)
  for (const alias of DEMO_ALIASES) store.putAlias(alias)
  store.putWorkspace({ id: 'workspace', name: 'Isolated workspace', rootPath: workspace,
    environment: { kind: 'local' }, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
})
afterEach(async () => {
  runs.abortAll()
  await shutdownMcp()
  await shutdownEnvironments()
  resetRuntimeForTest()
  closeDatabase()
  if (root) rmSync(root, { recursive: true, force: true })
})

describe('headless production agent runtime', () => {
  it('runs the real agent and commits tool results with no Electron or UI observer', async () => {
    const request: RunRequest = {
      runId: 'headless-run', sessionId: 'headless-session', workspaceId: 'workspace', depth: 0,
      input: [{ type: 'text', text: '看一下这个工程的结构' }], mode: 'normal', thinking: 'off',
      webSearch: false, permissionMode: 'full', model: DEMO_ALIAS, skillIds: []
    }
    const handle = runs.create(request)
    const release = runs.retainSessionForRun(handle)
    try { await runAgent(handle, request) } finally { release() }
    expect(handle.status).toBe('done')
    const messages = store.getHistory(request.sessionId)
    expect(messages.some((message) => message.parts.some((part) => part.type === 'tool_result'))).toBe(true)
    expect(messages.at(-1)?.parts.some((part) => part.type === 'text' && part.text.includes('演示值:text'))).toBe(true)
    // 控制台重连要读的就是同一份落盘转录，而不是渲染层的内存副本。
    closeDatabase()
    openDatabase(join(root, 'data'), { durability: 'full' })
    expect(store.getHistory(request.sessionId)).toEqual(messages)
  }, 20_000)
})
