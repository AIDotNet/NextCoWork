/**
 * `getGitDiffSides` —— 对比视图的两侧全文,在真实的临时仓库上跑。
 *
 * 比较基准必须和 `git diff` 一致(工作区:index → 工作区;暂存区:HEAD → index),
 * 而「这一侧不存在」(新文件、删除、空仓库)必须是空串,不是失败;没法按全文比的
 * (二进制、冲突)必须是 null,让界面退回 unified 文本。
 */
import { execFileSync } from 'node:child_process'
import { mkdtempSync, realpathSync, rmSync, unlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ root: '' }))

vi.mock('../../state/store', () => ({
  store: { getWorkspace: (id: string) => (id === 'workspace' ? { rootPath: mocks.root } : undefined) }
}))
vi.mock('../../runtime', () => ({ getCommitMessageGenerator: vi.fn() }))

import { getGitDiffSides } from '../git'

function git(...args: string[]): void {
  execFileSync('git', args, { cwd: mocks.root, stdio: 'ignore' })
}

function write(path: string, content: string | Buffer): void {
  writeFileSync(join(mocks.root, path), content)
}

const sides = (path: string, staged: boolean) => getGitDiffSides({ workspaceId: 'workspace', path, staged })

let temporary = ''

beforeEach(() => {
  temporary = mkdtempSync(join(tmpdir(), 'ncw-git-sides-'))
  mocks.root = realpathSync.native(temporary)
  git('init', '-q')
  git('config', 'user.email', 'test@example.com')
  git('config', 'user.name', 'test')
  git('config', 'commit.gpgsign', 'false')
})

afterEach(() => {
  rmSync(temporary, { recursive: true, force: true })
})

describe('getGitDiffSides', () => {
  it('compares index → worktree for unstaged and HEAD → index for staged', async () => {
    write('a.txt', 'one\n')
    git('add', 'a.txt')
    git('commit', '-q', '-m', 'init')
    write('a.txt', 'two\n')
    git('add', 'a.txt')
    write('a.txt', 'three\n')

    expect(await sides('a.txt', true)).toEqual({ original: 'one\n', modified: 'two\n' })
    expect(await sides('a.txt', false)).toEqual({ original: 'two\n', modified: 'three\n' })
  })

  it('treats a missing side as empty: untracked, unborn HEAD, deleted', async () => {
    write('new.txt', 'hello\n')
    expect(await sides('new.txt', false)).toEqual({ original: '', modified: 'hello\n' })

    // 空仓库里暂存的新文件:HEAD 不存在
    git('add', 'new.txt')
    expect(await sides('new.txt', true)).toEqual({ original: '', modified: 'hello\n' })

    git('commit', '-q', '-m', 'init')
    unlinkSync(join(mocks.root, 'new.txt'))
    expect(await sides('new.txt', false)).toEqual({ original: 'hello\n', modified: '' })

    git('rm', '-q', '--cached', 'new.txt')
    expect(await sides('new.txt', true)).toEqual({ original: 'hello\n', modified: '' })
  })

  it('returns null for binary content', async () => {
    write('bin.dat', Buffer.from([1, 0, 2, 3]))
    git('add', 'bin.dat')
    expect(await sides('bin.dat', true)).toBeNull()
    expect(await sides('bin.dat', false)).toBeNull()
  })

  it('returns null for a conflicted file', async () => {
    write('c.txt', 'base\n')
    git('add', 'c.txt')
    git('commit', '-q', '-m', 'base')
    git('checkout', '-q', '-b', 'other')
    write('c.txt', 'other\n')
    git('commit', '-q', '-am', 'other')
    git('checkout', '-q', '-')
    write('c.txt', 'main\n')
    git('commit', '-q', '-am', 'main')
    try {
      git('merge', '-q', 'other')
    } catch {
      // 冲突时 merge 退出非零,这正是要的状态
    }
    expect(await sides('c.txt', false)).toBeNull()
  })

  it('rejects paths escaping the repository', async () => {
    await expect(sides('../outside.txt', false)).rejects.toThrow('git.invalidPath')
  })
})
