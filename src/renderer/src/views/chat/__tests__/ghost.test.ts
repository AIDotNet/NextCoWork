import { describe, expect, it } from 'vitest'
import { userMessage } from '../../../../../shared/agent/message'
import type { GitOverview } from '../../../../../shared/domain/git'
import {
  createBackoff,
  fileGhost,
  ghostDisplay,
  gitPathToWorkspacePath,
  historyGhost,
  hotFilesFrom,
  inlineDebounceMs,
  inlineEligible,
  mentionedFilesOf,
  nextWordChunk,
  remainderOf,
  resolveGhostKey,
  type Ghost
} from '../ghost'

const visible = { visible: true, mac: true }

describe('resolveGhostKey', () => {
  it.each([
    [{ key: 'Tab' }, 'accept'],
    [{ key: 'ArrowRight' }, 'accept'],
    [{ key: 'ArrowRight', metaKey: true }, 'acceptWord'],
    [{ key: 'Escape' }, 'dismiss'],
    // ★ Enter 永远不接受灰字 —— 接受和发送是两个动作
    [{ key: 'Enter' }, null],
    [{ key: 'Tab', shiftKey: true }, null],
    [{ key: 'ArrowRight', altKey: true }, null],
    [{ key: 'ArrowRight', ctrlKey: true }, null],
    [{ key: 'a' }, null]
  ] as const)('mac %o → %s', (e, want) => {
    expect(resolveGhostKey(e, visible)).toBe(want)
  })

  it('非 mac 用 Ctrl+→ 按词接受', () => {
    expect(resolveGhostKey({ key: 'ArrowRight', ctrlKey: true }, { visible: true, mac: false })).toBe('acceptWord')
    expect(resolveGhostKey({ key: 'ArrowRight', metaKey: true }, { visible: true, mac: false })).toBeNull()
  })

  it('组词中、或者没有灰字时一律不碰', () => {
    expect(resolveGhostKey({ key: 'Tab', isComposing: true }, visible)).toBeNull()
    // Safari:compositionend 抢在确认键之前,那一下 isComposing 已是 false,只剩 229
    expect(resolveGhostKey({ key: 'Tab', keyCode: 229 }, visible)).toBeNull()
    expect(resolveGhostKey({ key: 'Tab' }, { visible: false, mac: true })).toBeNull()
  })
})

describe('remainderOf(跟打)', () => {
  const g: Ghost = { source: 'suggest', base: '', text: '跑一下测试', key: 'k' }
  it('打的字和灰字一致时只剩后半截', () => {
    expect(remainderOf(g, '')).toBe('跑一下测试')
    expect(remainderOf(g, '跑一')).toBe('下测试')
  })
  it('打了别的、或已经补完 → null', () => {
    expect(remainderOf(g, '提交')).toBeNull()
    expect(remainderOf(g, '跑一下测试')).toBeNull()
  })
})

describe('nextWordChunk', () => {
  it('英文一次一个词', () => {
    const full = 'run the unit tests'
    expect(nextWordChunk(full, 'run'.length)).toBe(' the')
  })
  it('中文按词典切,不是整句', () => {
    const full = '给 update-service 补上重试逻辑的单元测试'
    const cut = '给 update-service 补'.length
    const chunk = nextWordChunk(full, cut)
    expect(chunk.length).toBeGreaterThan(0)
    expect(chunk.length).toBeLessThan(full.length - cut)
    expect(full.slice(cut).startsWith(chunk)).toBe(true)
  })
  it('不会切开一枚 mention', () => {
    const full = '看看 [a.ts](src/a.ts) 的改动'
    expect(nextWordChunk(full, '看看 '.length)).toBe('[a.ts](src/a.ts)')
  })
})

describe('ghostDisplay', () => {
  it('mention 画成 @名,只取第一行', () => {
    expect(ghostDisplay(' [a.ts](src/a.ts) 的改动\n第二行')).toBe(' @a.ts 的改动')
  })
})

describe('historyGhost', () => {
  const prompts = ['跑一下测试并修复失败的用例', '跑一下 lint', '看看 [a.ts](src/a.ts) 的改动']
  it('取最近的那条逐字前缀', () => {
    expect(historyGhost('跑一下', prompts)?.text).toBe('测试并修复失败的用例')
    expect(historyGhost('跑一下 l', prompts)?.text).toBe('int')
  })
  it('太短、空白、换行结尾、或对不上 → 不补', () => {
    expect(historyGhost('跑一', prompts)).toBeNull()
    expect(historyGhost('   ', prompts)).toBeNull()
    expect(historyGhost('跑一下\n', prompts)).toBeNull()
    expect(historyGhost('Run', prompts)).toBeNull()
  })
  it('切点落在 mention 原文中间时跳过', () => {
    expect(historyGhost('看看 [a.t', prompts)).toBeNull()
  })
})

describe('fileGhost', () => {
  const files = [
    { name: 'update-service.ts', path: 'src/main/update/update-service.ts' },
    { name: 'README.md', path: 'README.md' },
    { name: 'index.ts', path: 'src/main/index.ts' },
    { name: 'index.css', path: 'src/renderer/index.css' }
  ]
  it('唯一命中:补出文件名剩下的部分,不分大小写', () => {
    const g = fileGhost('看看 update-se', files)
    expect(g?.text).toBe('rvice.ts')
    expect(g?.file).toEqual({ name: 'update-service.ts', path: 'src/main/update/update-service.ts', tokenStart: 3 })
    expect(fileGhost('readm', files)?.text).toBe('E.md')
  })
  it('多个命中、太短、或前面是 @ / 路径 / 反引号 → 不补', () => {
    expect(fileGhost('改 index', files)).toBeNull()
    expect(fileGhost('改 up', files)).toBeNull()
    expect(fileGhost('@update-se', files)).toBeNull()
    expect(fileGhost('src/update-se', files)).toBeNull()
    expect(fileGhost('`update-se', files)).toBeNull()
  })
})

describe('git 路径 → 工作区路径', () => {
  it('同根原样;子目录剥前缀;子目录外和对不上的丢掉', () => {
    expect(gitPathToWorkspacePath('/r', '/r', 'src/a.ts')).toBe('src/a.ts')
    expect(gitPathToWorkspacePath('/r', '/r/app/', 'app/src/a.ts')).toBe('src/a.ts')
    expect(gitPathToWorkspacePath('/r', '/r/app', 'lib/b.ts')).toBeNull()
    expect(gitPathToWorkspacePath('/r', '/other', 'src/a.ts')).toBeNull()
  })

  it('热文件:提到过的在前,删掉的不要,按路径去重', () => {
    const overview = {
      available: true, root: '/r', branch: 'main', detached: false, unborn: false, upstream: '', ahead: 0, behind: 0,
      filesTruncated: false,
      files: [
        { path: 'src/a.ts', index: 'M', worktree: '.', staged: true, unstaged: false, untracked: false, conflicted: false },
        { path: 'src/gone.ts', index: 'D', worktree: '.', staged: true, unstaged: false, untracked: false, conflicted: false },
        { path: 'src/m.ts', index: '.', worktree: 'M', staged: false, unstaged: true, untracked: false, conflicted: false }
      ]
    } as unknown as GitOverview
    const hot = hotFilesFrom(overview, '/r', [{ name: 'm.ts', path: 'src/m.ts' }])
    expect(hot.map((f) => f.path)).toEqual(['src/m.ts', 'src/a.ts'])
  })

  it('从用户消息里收集 @ 过的文件,新的在前', () => {
    const files = mentionedFilesOf([
      userMessage('1', [{ type: 'text', text: '看 [a.ts](src/a.ts)' }], 1),
      userMessage('2', [{ type: 'text', text: '再看 [b.ts](src/b.ts) 和 [a.ts](src/a.ts)' }], 2)
    ])
    expect(files.map((f) => f.path)).toEqual(['src/b.ts', 'src/a.ts'])
  })
})

describe('inlineEligible', () => {
  const ok = { enabled: true, composing: false, caretAtEnd: true, popupOpen: false, hasLocalGhost: false, dismissed: false, pasteHold: false, remote: false }
  it('够长、没说完、不像密钥 → 发', () => {
    expect(inlineEligible('给 update-service 补', ok)).toBe(true)
  })
  it.each([
    ['太短', '补上', ok],
    ['已经说完', '提交并推送。', ok],
    ['像密钥', 'key=sk-proj-1234567890abcdefXYZ 设一下', ok],
    ['没开', '给 update-service 补', { ...ok, enabled: false }],
    ['组词中', '给 update-service 补', { ...ok, composing: true }],
    ['有本地灰字', '给 update-service 补', { ...ok, hasLocalGhost: true }],
    ['设备模式', '给 update-service 补', { ...ok, remote: true }]
  ] as const)('%s → 不发', (_name, draft, s) => {
    expect(inlineEligible(draft, s)).toBe(false)
  })
  it('连续被无视后放慢', () => {
    expect(inlineDebounceMs(0)).toBe(300)
    expect(inlineDebounceMs(10)).toBe(1000)
  })
})

describe('退避', () => {
  it('连续 10 次没被用 → 每 3 轮才请求一次;用一次就复位', () => {
    const b = createBackoff()
    for (let i = 0; i < 10; i++) b.note('ignored')
    expect([b.shouldRequest(), b.shouldRequest(), b.shouldRequest()]).toEqual([false, false, true])
    b.note('accepted')
    expect(b.shouldRequest()).toBe(true)
  })
})
