/**
 * 已提交用户输入的**消费回执**(schema 第 34 条)。
 *
 * 这条不变式只有一个用途:一句用户输入**只被执行一次**。插话队列的清理、
 * 转录压缩、重启恢复全都可能再看见同一份输入,而它们能问的判据只有一个 ——
 * 「这条消息提交过没有」。所以下面每一组都在钉住同一件事的两面:
 *
 * - 回执**不跟着消息消失**(压缩 / 编辑历史 / 直接删行都不动它),否则输入被重放;
 * - 回执**只属于它自己的会话**(查询按 session_id 收窄),否则一次读串就放行一条输入。
 *
 * ★ 真文件库、每个用例独立目录:回执的价值有一半在「重开库之后还在」,
 *   内存库测不出这一点。
 */
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { assistantMessage, userMessage, type AgentMessage } from '../../../shared/agent/message'
import { DB_FILENAME, closeDatabase, db, openDatabase } from '../index'
import * as repo from '../repo'
import { MIGRATIONS } from '../schema'
import { store } from '../../state/store'

let dir = ''

beforeEach(() => {
  closeDatabase()
  dir = mkdtempSync(join(tmpdir(), 'nextcowork-committed-inputs-'))
  openDatabase(dir)
})

afterEach(() => {
  closeDatabase()
  rmSync(dir, { recursive: true, force: true })
})

const WS = 'ws'
const AT = 1_700_000_000_000

function session(id: string): void {
  repo.ensureSession({ id, workspaceId: WS, title: 'T' })
}

/** 用 `shared/agent/message` 的构造器 mint 消息 —— 全项目只在那里保证 schemaVersion。 */
const ask = (id: string, text: string, at = AT): AgentMessage => userMessage(id, [{ type: 'text', text }], at)
const say = (id: string, text: string, at = AT): AgentMessage => assistantMessage(id, [{ type: 'text', text }], at)

/** 直接读表:回执行是「有没有」的事实,不能只靠访问器自证。 */
function receiptCount(sessionId: string): number {
  const row = db().prepare('SELECT COUNT(*) AS n FROM session_input_receipts WHERE session_id = ?').get(sessionId) as Record<string, unknown>
  return Number(row['n'] ?? 0)
}

function receiptCommittedAt(sessionId: string, messageId: string): number | undefined {
  const row = db()
    .prepare('SELECT committed_at FROM session_input_receipts WHERE session_id = ? AND message_id = ?')
    .get(sessionId, messageId) as Record<string, unknown> | undefined
  return row === undefined ? undefined : Number(row['committed_at'])
}

describe('提交用户消息留下消费回执', () => {
  it('user 消息提交后有回执,assistant 没有', () => {
    session('s1')
    repo.commitMessage('s1', ask('u1', '你好'))
    repo.commitMessage('s1', say('a1', '在的'))

    expect(receiptCount('s1')).toBe(1)
    // 判定必须**按 id 逐条**回答:assistant 那一半绝不能跟着 user 一起被算成已提交。
    expect(repo.committedInputIds('s1', ['u1', 'a1']).sort()).toEqual(['u1'])
    expect(repo.committedInputIds('s1', ['a1'])).toEqual([])
  })

  it('重复提交同一条只留一条回执,且保留首次提交的时刻', () => {
    session('s1')
    repo.commitMessage('s1', ask('u1', '重放一次', AT))
    // 重放时消息行会被更新(excluded.created_at 覆盖),回执不能跟着漂 ——
    // 「什么时候第一次提交的」是它唯一的语义。
    repo.commitMessage('s1', ask('u1', '重放一次', AT + 5000))

    expect(receiptCount('s1')).toBe(1)
    expect(receiptCommittedAt('s1', 'u1')).toBe(AT)
    expect(repo.committedInputIds('s1', ['u1'])).toEqual(['u1'])
    expect(repo.getHistory('s1')).toHaveLength(1)
  })

  it('没提交过的 id 与空清单都答不出来', () => {
    session('s1')
    repo.commitMessage('s1', ask('u1', '在的'))

    expect(repo.committedInputIds('s1', [])).toEqual([])
    expect(repo.committedInputIds('s1', ['u1', '从没提交过']).sort()).toEqual(['u1'])
  })

  it('队列最多 20 条,但查询**不静默截断**:给了多少就逐条回答', () => {
    session('s1')
    const ids: string[] = []
    for (let i = 0; i < 25; i++) {
      const id = `u-${String(i).padStart(2, '0')}`
      repo.commitMessage('s1', ask(id, `第 ${String(i)} 条`))
      ids.push(id)
    }

    // 少查一条 = 少判一条 = 可能再执行一次,所以这里必须 25 条全中。
    expect(repo.committedInputIds('s1', [...ids, '不存在']).sort()).toEqual([...ids].sort())
  })

  it('★ 别的会话里的同名回执不会被读串', () => {
    session('s1')
    session('s2')
    repo.commitMessage('s1', ask('m-1', '只提交在 s1'))
    repo.commitMessage('s2', ask('m-2', 's2 自己的'))

    // 查询按 session_id 收窄。少了这个条件,s1 的回执会替 s2 的一条输入作证。
    expect(repo.committedInputIds('s2', ['m-1'])).toEqual([])
    expect(repo.committedInputIds('s1', ['m-2'])).toEqual([])
    expect(repo.committedInputIds('s2', ['m-2'])).toEqual(['m-2'])
  })

  it('store 上的访问器是纯转发,答案与 repo 一致(SQL 只在 db 层)', () => {
    session('s1')
    repo.commitMessage('s1', ask('u1', 'hi'))

    expect(store.committedInputIds('s1', ['u1', 'nope'])).toEqual(['u1'])
  })
})

describe('回执与消息在同一个事务里', () => {
  it('整体回滚时,消息与回执都不落盘(不会留下「已落盘却没被消费过」的一条)', () => {
    session('s1')
    /*
      ★ 注入一次**回执写入失败**,而不是在消息写入之前失败。
      把回执表摘掉,提交就必然在「消息已经 INSERT 过、回执写不进去」那一步抛;
      如果回执是另一个事务(或者先提交了消息),这一抛之后消息会留在库里,
      而那正是「重启后再执行一遍」的成因。
    */
    db().exec('DROP TABLE session_input_receipts')

    expect(() => repo.commitMessage('s1', ask('u1', '这次提交必须整体失败')))
      .toThrow(/session_input_receipts/)

    expect(repo.getHistory('s1')).toEqual([])
  })
})

describe('转录变化不动回执', () => {
  it('replaceHistory 移除原来的 user 消息之后,回执仍然在', () => {
    session('s1')
    repo.commitMessage('s1', ask('u1', '要被编辑掉的提问'))
    repo.commitMessage('s1', say('a1', '回答'))

    repo.replaceHistory('s1', [say('a1', '回答')])

    expect(repo.getHistory('s1').map((m) => m.id)).toEqual(['a1'])
    // ★ 消息没了,但「这份输入已经执行过」这件事发生过 —— 抹掉它就是允许重放。
    expect(repo.committedInputIds('s1', ['u1'])).toEqual(['u1'])
  })

  it('★ 直接从 messages 删行(转录压缩的形状)也不带走回执——它没有指向 messages 的外键', () => {
    session('s1')
    repo.commitMessage('s1', ask('u1', '被压缩掉的提问'))

    db().prepare('DELETE FROM messages WHERE id = ?').run('u1')

    expect(repo.getHistory('s1')).toEqual([])
    expect(repo.committedInputIds('s1', ['u1'])).toEqual(['u1'])
  })
})

describe('级联与重启', () => {
  it('删会话时回执一起回收,不留孤儿行', () => {
    session('s1')
    session('s2')
    repo.commitMessage('s1', ask('u1', '在 s1'))
    repo.commitMessage('s2', ask('u2', '在 s2'))

    repo.deleteSession('s1')

    expect(receiptCount('s1')).toBe(0)
    expect(repo.committedInputIds('s1', ['u1'])).toEqual([])
    // 只删它自己的那份
    expect(repo.committedInputIds('s2', ['u2'])).toEqual(['u2'])
  })

  it('重开库之后回执仍在', () => {
    session('s1')
    repo.commitMessage('s1', ask('u1', '跨重启'))

    closeDatabase()
    openDatabase(dir)

    expect(repo.committedInputIds('s1', ['u1'])).toEqual(['u1'])
    expect(repo.getHistory('s1').map((m) => m.id)).toEqual(['u1'])
  })
})

describe('第 34 条迁移', () => {
  it('升级第 33 版的旧库:已提交过的用户消息被回填成回执,assistant 不被回填', () => {
    const legacyDir = mkdtempSync(join(tmpdir(), 'nextcowork-v33-'))
    const raw = new DatabaseSync(join(legacyDir, DB_FILENAME))
    raw.exec('CREATE TABLE migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at INTEGER NOT NULL)')
    const insertMigration = raw.prepare('INSERT INTO migrations (version, name, applied_at) VALUES (?, ?, ?)')
    for (const migration of MIGRATIONS.slice(0, 33)) {
      raw.exec(migration.sql)
      insertMigration.run(migration.version, migration.name, Date.now())
    }
    raw.prepare(
      `INSERT INTO sessions (id, workspace_id, title, model, mode, thinking, root_path_at_creation,
        status, archived, favorited, created_at, updated_at, json)
       VALUES ('s-old', 'w', '旧对话', '', 'normal', 'auto', '', 'idle', 0, 0, 1, 1, '{}')`
    ).run()
    const putMessage = raw.prepare(
      `INSERT INTO messages (id, session_id, ordinal, role, parts, schema_version, created_at)
       VALUES (?, 's-old', ?, ?, '[]', 1, ?)`
    )
    putMessage.run('m-user', 0, 'user', 555)
    putMessage.run('m-assistant', 1, 'assistant', 556)
    raw.close()

    closeDatabase()
    openDatabase(legacyDir)

    try {
      // ★ 不回填的话,升级后第一次重启就会把这些老输入当成「还没提交」再执行一遍 ——
      //   而这个 bug 只出现在升级路径上,新装的库一辈子都测不出来。
      expect(repo.committedInputIds('s-old', ['m-user', 'm-assistant']).sort()).toEqual(['m-user'])
      // committed_at 取的是消息原来的 created_at,不是升级那一刻的时间
      expect(receiptCommittedAt('s-old', 'm-user')).toBe(555)
    } finally {
      closeDatabase()
      rmSync(legacyDir, { recursive: true, force: true })
      openDatabase(dir)
    }
  })
})
