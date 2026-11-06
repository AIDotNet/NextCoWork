/**
 * 「打开方式」探测的判定逻辑。
 *
 * 这里钉的是**菜单上该出现哪几项、每项指向什么** —— 那是这套东西里最容易
 * 悄悄坏掉的一段,而且坏了不报错:
 *
 * - 少扫一个应用目录 → 装了 IDE 的用户看不见它(看起来像"没装");
 * - 注册表/程序目录两条路的顺序反了 → 绿色版用户看到的是另一个版本的可执行文件;
 * - 终端候选的顺序反了 → 用户明明装了 Ghostty,点开的是系统终端;
 * - 目录也被接上编辑器 → 点一下目录,VS Code 整个工作区根换掉了。
 *
 * 所以 `detectTargets` / `detectTerminal` 是纯函数,一个假 deps 就能把上面四条
 * 各钉一遍。真实的 `exists` / `reg` / `PATH` 在 `system/open-with.ts`,不在这里。
 */
import { describe, expect, it } from 'vitest'
import {
  buildTargets,
  detectTargets,
  detectTerminal,
  terminalCandidates,
  type DetectionDeps
} from '../editors'
import { listOpenTargets } from '../open-with'
import { DEFAULT_APP_TARGET_ID, REVEAL_TARGET_ID, TERMINAL_TARGET_ID } from '../../../shared/domain/open-target'

/** 一份可控的只读视图:存在的路径、PATH 上的可执行名、注册表里的键值各给一张表。 */
function deps(overrides: Partial<DetectionDeps> = {}): DetectionDeps {
  const files = new Set<string>()
  const bins = new Map<string, string>()
  const registry = new Map<string, string>()
  return {
    platform: 'darwin',
    exists: (path) => files.has(path),
    which: (bin) => bins.get(bin) ?? null,
    registry: (key) => registry.get(key) ?? null,
    appDirs: ['/Applications'],
    listApps: () => [],
    programRoots: ['C:\\Program Files', 'C:\\Program Files (x86)'],
    ...overrides,
    // 下面这几条让测试可以顺着 `overrides` 之外的入口塞数据
  }
}

describe('detectTargets', () => {
  it('空机器上一台编辑器都探测不到 —— 菜单里只剩不需要探测的通用目标', () => {
    const found = detectTargets(deps())
    expect(found).toEqual([])
    expect(buildTargets(found, null)).toEqual([
      { id: REVEAL_TARGET_ID, label: '', icon: 'file-manager' },
      { id: DEFAULT_APP_TARGET_ID, label: '', icon: 'default-app' }
    ])
  })

  it('macOS 上按 `.app` 命中,并且启动用的是完整路径而不是 bundleId', () => {
    const found = detectTargets(deps({
      exists: (path) => path === '/Applications/Visual Studio Code.app'
    }))
    expect(found.map((entry) => entry.id)).toEqual(['vscode'])
    // ★ 完整路径:按名字或 bundleId 起的话,两个同名 app 时会挑到我们没扫到的那个
    expect(found[0]?.launch).toEqual({ kind: 'app', app: '/Applications/Visual Studio Code.app' })
  })

  it('大小写/空格对不上时靠列目录兜底,而不是当成没装', () => {
    const found = detectTargets(deps({
      listApps: (dir) => dir === '/Applications' ? ['visual studio code.app'] : []
    }))
    expect(found.map((entry) => entry.id)).toEqual(['vscode'])
    expect(found[0]?.launch).toEqual({ kind: 'app', app: '/Applications/visual studio code.app' })
  })

  it('`.app` 不在标准位置但装了 CLI 时,退回到命令行入口', () => {
    const found = detectTargets(deps({
      which: (bin) => bin === 'code' ? '/usr/local/bin/code' : null
    }))
    expect(found[0]?.launch).toEqual({ kind: 'bin', bin: '/usr/local/bin/code' })
  })

  it('菜单顺序是 rank 而不是探测顺序 —— JetBrains 全家排在 VS Code 之后', () => {
    const present = new Set([
      '/Applications/Rider.app',
      '/Applications/WebStorm.app',
      '/Applications/Visual Studio Code.app',
      '/Applications/Cursor.app'
    ])
    const found = detectTargets(deps({ exists: (path) => present.has(path) }))
    expect(found.map((entry) => entry.id)).toEqual(['vscode', 'cursor', 'webstorm', 'rider'])
  })

  it('Windows 先问注册表 App Paths,注册表没有才落到 PATH', () => {
    const registered = 'C:\\Users\\me\\AppData\\Local\\Programs\\Microsoft VS Code\\Code.exe'
    const fromRegistry = detectTargets(deps({
      platform: 'win32',
      registry: (key) => key.includes('App Paths\\Code.exe') ? registered : null,
      exists: (path) => path === registered,
      which: () => 'C:\\somewhere\\else\\code.exe'
    }))
    expect(fromRegistry[0]?.launch).toEqual({ kind: 'bin', bin: registered })

    const fromPath = detectTargets(deps({
      platform: 'win32',
      which: (bin) => bin === 'Code.exe' ? 'C:\\tools\\Code.exe' : null
    }))
    expect(fromPath[0]?.launch).toEqual({ kind: 'bin', bin: 'C:\\tools\\Code.exe' })
  })

  it('Windows 上带目录的候选走安装目录兜底(绿色版没写注册表)', () => {
    const green = 'C:\\Program Files\\Sublime Text\\sublime_text.exe'
    const found = detectTargets(deps({
      platform: 'win32',
      exists: (path) => path === green
    }))
    expect(found[0]?.launch).toEqual({ kind: 'bin', bin: green })
  })

  it('Linux 只认 PATH,不做目录猜测', () => {
    const found = detectTargets(deps({
      platform: 'linux',
      which: (bin) => bin === 'cursor' ? '/usr/bin/cursor' : null
    }))
    expect(found.map((entry) => entry.id)).toEqual(['cursor'])
  })

  it('Xcode 只在 macOS 上存在 —— 别的平台连候选都不该有', () => {
    for (const platform of ['win32', 'linux'] as const) {
      const found = detectTargets(deps({ platform, which: () => '/usr/bin/xed' }))
      expect(found.map((entry) => entry.id)).not.toContain('xcode')
    }
  })
})

describe('detectTerminal', () => {
  it('装了 Ghostty 就用 Ghostty —— 候选表里它排第一', () => {
    const terminal = detectTerminal(deps({
      exists: (path) => path === '/Applications/Ghostty.app'
    }))
    expect(terminal?.file).toBe('/Applications/Ghostty.app')
  })

  it('macOS 上一个第三方终端都没有时仍有系统终端兜底', () => {
    const terminal = detectTerminal(deps({
      exists: (path) => path === '/System/Applications/Utilities/Terminal.app'
    }))
    expect(terminal?.file).toBe('/System/Applications/Utilities/Terminal.app')
  })

  it('★ 终端拿到的参数是**目录**,不是文件 —— 每个平台都验一遍', () => {
    const dir = '/Users/me/project/src'
    for (const platform of ['darwin', 'win32', 'linux'] as const) {
      for (const candidate of terminalCandidates(platform)) {
        const args = candidate.args(dir)
        // 两种形态都合法:目录单独一个 argv(Ghostty / wt),或拼进 `--working-directory=…`
        if (args.length > 0) {
          expect(args.some((arg) => arg === dir || arg.includes(dir)), `${platform} ${candidate.file}`).toBe(true)
        } else {
          expect(candidate.cwd, `${platform} ${candidate.file}`).toBe(true)
        }
      }
    }
  })
})

describe('buildTargets', () => {
  it('顺序是「文件管理器 → 默认应用 → 终端 → 编辑器」,不是按探测顺序拼起来', () => {
    const editors = detectTargets(deps({
      exists: (path) => path === '/Applications/Rider.app'
    }))
    const targets = buildTargets(editors, { file: 'kitty', args: () => [], cwd: false })
    expect(targets.map((target) => target.id)).toEqual([REVEAL_TARGET_ID, DEFAULT_APP_TARGET_ID, TERMINAL_TARGET_ID, 'rider'])
  })

  it('两个通用目标的 label 是空串 —— 名字由渲染层按 i18n 出,不是产品名', () => {
    const targets = buildTargets([], null)
    expect(targets[0]?.label).toBe('')
  })
})

/**
 * 真探测跑一次本机。
 *
 * ★ 这里**只钉类型契约**,不钉「装了什么」—— 后者在 CI 上必然是另一台机器。
 *   而契约里那一条恰好是会静默坏掉的:菜单内容由主进程下发,一旦有人把
 *   绝对路径也塞进来(比如为了省一次 IPC),「绝对路径不进渲染层」这条约定
 *   就没了,而界面上看不出任何区别。
 */
describe('listOpenTargets', () => {
  it('文件管理器恒在,且返回项里没有任何路径', () => {
    const targets = listOpenTargets()
    expect(targets[0]?.id).toBe(REVEAL_TARGET_ID)
    for (const target of targets) {
      expect(target.label).not.toContain('/')
      expect(target.icon).not.toContain('/')
      expect(JSON.stringify(target)).not.toContain(process.env.HOME ?? '\u0000')
    }
  })
})
