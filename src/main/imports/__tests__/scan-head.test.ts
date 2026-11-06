/**
 * 预览只读头部 —— **省下的必须只是时间,不能是结论**。
 *
 * 这套用例守的是一条很容易被优化顺手破坏的性质:扫描从「全文读」改成
 * 「读前 1MiB」之后,一份**前 1MiB 全被单条巨型工具输出占满**的转录会解析出
 * 0 条消息。而 0 条消息在两个扫描循环里都是终局判决 ——
 * Claude 那边 `continue` 直接把它丢出预览,Codex 那边标成 incompatible。
 * 两者都不抛异常、不记诊断,用户唯一能观察到的现象是「我那个会话不见了」。
 *
 * 实测语料里这种文件真实存在(单个 115MB 的 Codex 转录,前若干 MB 是一条
 * 工具输出),所以这不是假想的边界。
 */
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IMPORT_LIMITS } from '../../../shared/domain/import'
import { closeDatabase, openDatabase } from '../../db/index'
import { store } from '../../state/store'
import * as service from '../service'

vi.mock('../../services/attachments', () => ({
  uploadAttachment: (req: { mime: string }) => ({
    id: 'att', scope: 'session', displayName: 'x', mime: req.mime,
    size: 1, checksum: 'c', createdAt: 0, url: 'ncw://attachments/session/x/att.png'
  })
}))

let dbDir = ''
let sourceDir = ''
let projectDir = ''

beforeEach(() => {
  closeDatabase()
  dbDir = mkdtempSync(join(tmpdir(), 'ncw-head-db-'))
  openDatabase(dbDir)
  sourceDir = mkdtempSync(join(tmpdir(), 'ncw-head-cc-'))
  projectDir = mkdtempSync(join(tmpdir(), 'ncw-head-proj-'))
  mkdirSync(join(sourceDir, 'projects', 'encoded-proj'), { recursive: true })
})

afterEach(() => {
  closeDatabase()
  for (const dir of [dbDir, sourceDir, projectDir]) rmSync(dir, { recursive: true, force: true })
})

/**
 * 一份「头重脚轻」的转录:第一条就是撑满头部预算的工具结果,
 * 真正的对话记录全在它后面。
 */
function headHeavyTranscript(sessionId: string): string {
  const filler = 'x'.repeat(IMPORT_LIMITS.scanHeadBytes + 512 * 1024)
  return [
    {
      uuid: 'u0', parentUuid: null, type: 'user', cwd: projectDir, sessionId,
      timestamp: '2025-01-01T00:00:00.000Z',
      message: { role: 'user', content: [{ type: 'tool_result', tool_use_id: 't0', content: filler, is_error: false }] }
    },
    {
      uuid: 'u1', parentUuid: 'u0', type: 'user', cwd: projectDir, sessionId,
      timestamp: '2025-01-01T00:00:01.000Z',
      message: { role: 'user', content: '这条在头部预算之外' }
    },
    {
      uuid: 'a1', parentUuid: 'u1', type: 'assistant',
      timestamp: '2025-01-01T00:00:02.000Z',
      message: { role: 'assistant', model: 'claude-sonnet-4-5', content: [{ type: 'text', text: '收到。' }] }
    }
  ].map((r) => JSON.stringify(r)).join('\n') + '\n'
}

function seed(sessionId: string, body: string): string {
  writeFileSync(join(sourceDir, 'projects', 'encoded-proj', `${sessionId}.jsonl`), body)
  const now = Date.now()
  store.putImportSource({
    sourceId: 'src-head', kind: 'claude-code', configDir: sourceDir, origin: 'user-picked',
    syncEnabled: false, categories: ['chat', 'project'], projectKeys: [projectDir],
    status: 'off', diagnostics: [], createdAt: now, updatedAt: now
  })
  return 'src-head'
}

async function chatItems(sourceId: string, requestId: string) {
  const preview = await service.buildPreview(sourceId, requestId)
  return service.previewItems({ previewId: preview.previewId, category: 'chat', offset: 0, limit: 50 }).items
}

describe('预览的头部读取', () => {
  it('★★ 前 1MiB 全是一条巨型工具输出时,会话依然出现在预览里', async () => {
    const sourceId = seed('sess-head-heavy', headHeavyTranscript('sess-head-heavy'))
    const items = await chatItems(sourceId, 'head-heavy')

    /*
      没有那条回退的话,这里拿到的是空数组 —— 而且整个流程一声不吭。
      断言写成「至少一条」而不是精确条数,是因为这里要守的是
      「不会被静默丢掉」,不是编码器的具体产出。
    */
    expect(items).toHaveLength(1)
    expect(items[0]?.count).toBeGreaterThan(0)
  })

  it('★ 回退读的是全文,所以条数是准的,不标近似', async () => {
    const sourceId = seed('sess-exact', headHeavyTranscript('sess-exact'))
    const items = await chatItems(sourceId, 'exact')

    /*
      ★ 先断言长度。少了这一句,下面那条在 items 为空时会**真空通过**
      (`items[0]?.x` 对空数组同样是 undefined)—— 摘掉回退跑一遍就会发现
      它照样是绿的,那种用例什么都守不住。
    */
    expect(items).toHaveLength(1)
    // 头部没解析出消息 → 回退 parseAll → partial 为假 → 不该打近似标记。
    expect(items[0]?.countApproximate).toBeUndefined()
  })

  it('★ 真正的空会话仍然被挡在预览之外 —— 回退不是「什么都放进来」', async () => {
    const empty = JSON.stringify({
      uuid: 'm1', parentUuid: null, type: 'summary', cwd: projectDir,
      sessionId: 'sess-empty', timestamp: '2025-01-01T00:00:00.000Z', summary: '只有摘要没有对话'
    }) + '\n'
    const sourceId = seed('sess-empty', empty)
    expect(await chatItems(sourceId, 'empty')).toHaveLength(0)
  })
})
