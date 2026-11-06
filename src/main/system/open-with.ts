/**
 * `main/system/editors.ts` 的**I/O 那一半**:真实文件系统、真实注册表、真实进程。
 *
 * 分成两个文件不是洁癖 —— 探测的判定逻辑(装了什么、菜单该出哪几项)是最容易
 * 写错、也最值得写测试的部分,而它一旦和 `spawn` / `execFile` / Electron 混在
 * 一起,就只能起一个 Electron 才测得了(`vitest.config.ts` 的 environment 是 node)。
 *
 * ## 三个必须记住的平台事实
 *
 * 1. **macOS 的应用目录不止 `/Applications`。** `/System/Applications` 装着系统
 *    自带的那些(终端就在里面),`~/Applications` 是用户自己放的,Toolbox 装的
 *    JetBrains 全家则在 `~/Library/Application Support/JetBrains/Toolbox/apps`
 *    下面的软链里。少扫一个的表现是「我明明装了,菜单里没有」。
 * 2. **Windows 的 PATH 上找可执行文件要补扩展名。** `which('code')` 在 Windows 上
 *    必须试 `code`、`code.exe`、`code.cmd` —— VS Code 装的是 `bin\code.cmd`,
 *    而 `subl` 是 `subl.exe`。
 * 3. **`.app` 要用 `open -a <完整路径>` 起**,不是 `open <文件> -a <名字>`:
 *    后者让 LaunchServices 按**名字**找,两个同名 app 时它会挑一个我们没扫到过的。
 */
import { execFileSync, spawn } from 'node:child_process'
import { existsSync, readdirSync } from 'node:fs'
import { homedir } from 'node:os'
import { delimiter, join } from 'node:path'
import type { OpenTarget } from '../../shared/domain/open-target'
import {
  buildTargets,
  detectTargets,
  detectTerminal,
  type DetectedEditor,
  type DetectionDeps,
  type TerminalDefinition
} from './editors'

/** 探测结果缓存 —— 扫描要列目录、要问注册表,一次菜单点开不值得重跑一遍。 */
let cache: { targets: OpenTarget[]; editors: readonly DetectedEditor[]; terminal: TerminalDefinition | null } | null = null

/** 探测里所有外部命令的等待上限。它们都是「问一句就答」的,慢就是有问题。 */
const PROBE_TIMEOUT_MS = 4000

/**
 * PATH 上找可执行文件。
 *
 * ★ 自己走一遍 `PATH` 而不是 `which` / `where`:那两个是外部命令(Windows 上
 *   还不一定有),而这条路径在**每一次**菜单点开时都可能跑到。自己走一遍还能
 *   顺手把「Windows 要补 `.exe` / `.cmd` / `.bat`」这条规则写在一处。
 */
function whichBin(bin: string): string | null {
  const path = process.env.PATH ?? ''
  const suffixes = process.platform === 'win32' ? ['', '.exe', '.cmd', '.bat'] : ['']
  for (const dir of path.split(delimiter)) {
    if (dir === '') continue
    for (const suffix of suffixes) {
      const full = join(dir, `${bin}${suffix}`)
      try {
        if (existsSync(full)) return full
      } catch {
        // PATH 里可能有个读不到的目录(网络盘掉线)。跳过它,别让整次探测失败。
      }
    }
  }
  return null
}

/** 注册表读一个值。`reg query` 的输出是 `<key>    <type>    <value>`,值在最后一列。 */
function registryValue(key: string): string | null {
  if (process.platform !== 'win32') return null
  try {
    const output = execFileSync('reg', ['query', key, '/ve'], { encoding: 'utf8', timeout: PROBE_TIMEOUT_MS, windowsHide: true })
    for (const line of output.split(/\r?\n/)) {
      const match = /^\s{4}\S+\s+REG_(?:SZ|EXPAND_SZ)\s+(.+?)\s*$/.exec(line)
      if (match !== null) return match[1] ?? null
    }
    return null
  } catch {
    // 键不存在 / reg 不可用 / 超时 —— 对调用方都是「这里没有」
    return null
  }
}

/** macOS 上要扫的应用目录。顺序即优先级:用户自己放的排最后。 */
function appDirs(): string[] {
  if (process.platform !== 'darwin') return []
  const home = homedir()
  return [
    '/Applications',
    '/System/Applications',
    '/System/Applications/Utilities',
    join(home, 'Applications'),
    // JetBrains Toolbox:每个 IDE 一个 `<产品>/<channel>/<hash>/<产品>.app` 的软链
    join(home, 'Library/Application Support/JetBrains/Toolbox/apps'),
    // Setapp 用户装在这里,不算小众
    join(home, 'Library/Application Support/Setapp/Applications')
  ]
}

/** Toolbox 的目录是两三层嵌套,`readdir` 要递归一层才看得到 `.app`。 */
function listApps(dir: string): readonly string[] {  try {
    const entries = readdirSync(dir)
    if (!dir.includes('Toolbox/apps') && !dir.includes('Setapp/Applications')) return entries
    const nested: string[] = []
    for (const entry of entries) {
      for (const channel of ['stable', 'current', 'bin']) {
        for (const inner of safeReaddir(join(dir, entry, channel))) {
          for (const candidate of safeReaddir(join(dir, entry, channel, inner))) nested.push(candidate)
        }
      }
    }
    return [...entries, ...nested]
  } catch {
    return []
  }
}

function safeReaddir(dir: string): readonly string[] {
  try {
    return readdirSync(dir)
  } catch {
    return []
  }
}

/**
 * Windows 上「没走注册表」的安装根目录。
 *
 * ★ 这是**兜底**而不是主路径:注册表 App Paths 是安装器自己写的,它比猜目录准。
 *   这一层挡的是「绿色版 / 解压即用」这类没跑安装器的装法,以及
 *   `%LOCALAPPDATA%\Programs` 下的**用户级安装**(VS Code / Cursor 默认就装那儿)。
 */
function programRoots(): string[] {
  if (process.platform !== 'win32') return []
  const local = process.env.LOCALAPPDATA
  return [
    ...(local === undefined ? [] : [join(local, 'Programs'), join(local, 'JetBrains')]),
    'C:\\Program Files',
    'C:\\Program Files (x86)',
    'C:\\Program Files\\JetBrains'
  ]
}

function detectionDeps(): DetectionDeps {
  return {
    platform: process.platform,
    exists: (path) => existsSync(path),
    which: whichBin,
    registry: registryValue,
    appDirs: appDirs(),
    listApps,
    programRoots: programRoots()
  }
}

function probe(): { targets: OpenTarget[]; editors: readonly DetectedEditor[]; terminal: TerminalDefinition | null } {
  cache ??= (() => {
    const deps = detectionDeps()
    const editors = detectTargets(deps)
    const terminal = detectTerminal(deps)
    return { editors, terminal, targets: buildTargets(editors, terminal) }
  })()
  return cache
}

/** 菜单要画的那几项。文件管理器恒在,其余按实际探测。 */
export function listOpenTargets(): OpenTarget[] {
  return probe().targets
}

/**
 * 用某个具名目标打开一个文件。**只认 `listOpenTargets()` 给过的 id** ——
 * 渲染层递进来的 id 只用来查表,查不到即拒,它不携带路径也不携带命令。
 */
export async function openWithTarget(targetId: string, file: string): Promise<void> {
  const { editors, terminal } = probe()
  if (targetId === 'terminal') {
    if (terminal === null) throw new Error('no terminal available')
    launch(terminal.file, terminal.args(dirOf(file)), terminal.cwd ? dirOf(file) : undefined)
    return
  }
  const editor = editors.find((entry) => entry.id === targetId)
  if (editor === undefined) throw new Error(`unknown open target: ${targetId}`)
  if (editor.launch.kind === 'app') {
    launch('open', ['-a', editor.launch.app, file])
    return
  }
  launch(editor.launch.bin, [file])
}

function dirOf(file: string): string {
  const cut = Math.max(file.lastIndexOf('/'), file.lastIndexOf('\\'))
  return cut <= 0 ? file : file.slice(0, cut)
}

/**
 * 起一个**不托管**的子进程。
 *
 * ★ 必须 `detached` + `stdio: 'ignore'` + `unref()`:我们要的是「把文件交给
 *   编辑器,然后忘掉它」。托管的话,`open -a` 会活到用户关掉 VS Code 为止 ——
 *   而那时我们早就不管它了,只是白留一个句柄;更糟的是 Node 退出时它会跟着
 *   被收走,表现成「点了打开,编辑器一闪就没了」。
 */
function launch(file: string, args: string[], cwd?: string): void {
  const child = spawn(file, args, {
    detached: true,
    stdio: 'ignore',
    windowsHide: process.platform !== 'win32',
    ...(cwd === undefined ? {} : { cwd })
  })
  child.on('error', () => {
    // 起不来(被卸载 / 权限)。调用方那边已经返回了,失败只能靠下一次点击暴露 ——
    // 所以这里不做静默重试,把缓存清掉让下一次重新探测。
    cache = null
  })
  child.unref()
}

/** 测试与「装了新 IDE 想立刻看见」共用。目前只有测试调它。 */
export function resetOpenTargetCache(): void {
  cache = null
}
