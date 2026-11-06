/**
 * 提炼会话的 `skillSource` —— 只活在 json 列里,没有提列(照 `modelProviderId` 的先例)。
 * 需求:它决定每个 run 要不要注入源会话摘要,以及侧边栏要不要画「提炼为 Skill」。
 */
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { DB_FILENAME, closeDatabase, openDatabase } from '../index'
import * as repo from '../repo'

let dir = ''

beforeEach(() => {
  closeDatabase()
  dir = mkdtempSync(join(tmpdir(), 'nextcowork-skill-source-db-'))
  openDatabase(dir)
})

afterEach(() => {
  closeDatabase()
  rmSync(dir, { recursive: true, force: true })
})

describe('会话上的 skillSource', () => {
  it('round-trips skillSource through the session json without a column', () => {
    repo.createSession({ id: 'src', workspaceId: 'w', title: '改价格规则' })
    repo.createSession({ id: 'x', workspaceId: 'w', title: '提炼', skillSource: { sessionId: 'src' } })
    expect(repo.getSession('x')?.skillSource).toEqual({ sessionId: 'src' })
    expect(repo.getSession('src')?.skillSource).toBeUndefined()
  })

  it('marks only extraction sessions in the sidebar list', () => {
    repo.createSession({ id: 'src', workspaceId: 'w', title: '改价格规则' })
    repo.createSession({ id: 'x', workspaceId: 'w', title: '提炼', skillSource: { sessionId: 'src' } })
    const items = repo.listSessions('w')
    expect(items.find((item) => item.id === 'x')?.skillExtraction).toBe(true)
    expect(items.find((item) => item.id === 'src')).not.toHaveProperty('skillExtraction')
  })

  it('drops a malformed skillSource instead of throwing', () => {
    repo.createSession({ id: 'x', workspaceId: 'w', skillSource: { sessionId: 'src' } })
    const db = new DatabaseSync(join(dir, DB_FILENAME))
    const row = db.prepare('SELECT json FROM sessions WHERE id = ?').get('x') as { json: string }
    const broken = { ...(JSON.parse(row.json) as Record<string, unknown>), skillSource: { sessionId: 42 } }
    db.prepare('UPDATE sessions SET json = ? WHERE id = ?').run(JSON.stringify(broken), 'x')
    db.close()
    expect(repo.getSession('x')?.skillSource).toBeUndefined()
  })
})
