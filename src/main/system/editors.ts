/**
 * 「这台机器上能用什么打开这个文件」—— 数据表与判定逻辑,**不碰 Electron、不碰
 * 真实文件系统**。
 *
 * 探测要面对的是三套完全不同的世界(macOS 的 `.app` 目录、Windows 的注册表
 * App Paths、Linux 的 PATH 与 `.desktop`),而它们**唯一需要被测的部分**是
 * 「拿到一份文件系统/注册表的只读视图之后,该给出哪几项」。所以那部分被抽成
 * `detectTargets(deps)` 的纯函数:一个假 `deps` 就能在普通 vitest 里跑,
 * 真正的 `fs` / `reg.exe` 调用全在 `open-with.ts` 里,只在应用里跑。
 *
 * ## 三条刻意的取舍
 *
 * 1. **只认我们列得出的编辑器。** 不去问系统「这个文件类型的默认打开方式是谁」
 *    —— 那会把「用什么写代码」这件事交给系统里某个不知道什么时候被改过的关联,
 *    而我们这张表是可以逐行读完、可以写测试的。
 * 2. **探测结果不含任何用户数据**,只有 `{ id, label, icon }` 三项。菜单是渲染层
 *    画的,而绝对路径不进渲染层(`shared/domain/workspace-file.ts` 那条约定)。
 * 3. **`label` 是产品名,不翻译。** 和模型名、供应商名同一条规矩。
 */
import type { OpenTarget, OpenTargetIcon } from '../../shared/domain/open-target'
import { DEFAULT_APP_TARGET_ID, REVEAL_TARGET_ID, TERMINAL_TARGET_ID } from '../../shared/domain/open-target'

/** 一个编辑器在各平台上怎么找、怎么起。缺席的字段 = 这个平台上不考虑它。 */
export interface EditorDefinition {
  id: string
  /** 产品名,不翻译 */
  label: string
  icon: OpenTargetIcon
  /** 菜单里的固定顺序,小的在前 */
  rank: number
  darwin?: {
    /** `.app` 的名字(不含 `.app`)。Toolbox / 手动安装的写法都列在这里 */
    apps: readonly string[]
    /** PATH 上的命令行入口,按顺序试第一个存在的 */
    cli?: readonly string[]
  }
  win32?: {
    /** App Paths 注册表里的可执行名,以及常见安装目录下的相对路径 */
    exe: readonly string[]
  }
  linux?: {
    /** PATH 上的可执行名 */
    bins: readonly string[]
  }
}

/**
 * ★ **顺序即优先级**,先命中先用 —— 和 `brands.ts` 那张表同一条规矩:
 *   两个 id 指向同一个可执行名时(比如 JetBrains 的 `idea` 同时可能是
 *   IntelliJ 和 Android Studio 的入口),靠顺序定夺,而不是靠运气。
 *
 * `apps` 里每一项都要**完整写出** macOS 上真实存在的那个名字 ——
 * 「IntelliJ IDEA CE」和「IntelliJ IDEA」是两个不同的 bundle,
 * 少写一个的表现是装了社区版的用户看不到这一项(而不是看到错的那一项)。
 */
export const EDITORS: readonly EditorDefinition[] = [
  {
    id: 'vscode',
    label: 'Visual Studio Code',
    icon: 'vscode',
    rank: 10,
    darwin: { apps: ['Visual Studio Code', 'Visual Studio Code - Insiders'], cli: ['code', 'code-insiders'] },
    win32: { exe: ['Code.exe', 'Microsoft VS Code\\Code.exe'] },
    linux: { bins: ['code', 'code-insiders'] }
  },
  {
    id: 'cursor',
    label: 'Cursor',
    icon: 'cursor',
    rank: 20,
    darwin: { apps: ['Cursor'], cli: ['cursor'] },
    win32: { exe: ['Cursor.exe'] },
    linux: { bins: ['cursor'] }
  },
  {
    id: 'windsurf',
    label: 'Windsurf',
    icon: 'windsurf',
    rank: 30,
    darwin: { apps: ['Windsurf'], cli: ['windsurf'] },
    win32: { exe: ['Windsurf.exe'] },
    linux: { bins: ['windsurf'] }
  },
  {
    id: 'zed',
    label: 'Zed',
    icon: 'zed',
    rank: 40,
    darwin: { apps: ['Zed'], cli: ['zed'] },
    win32: { exe: ['Zed.exe'] },
    linux: { bins: ['zed'] }
  },
  {
    id: 'sublime',
    label: 'Sublime Text',
    icon: 'sublime',
    rank: 50,
    darwin: { apps: ['Sublime Text'], cli: ['subl'] },
    win32: { exe: ['sublime_text.exe', 'Sublime Text\\sublime_text.exe'] },
    linux: { bins: ['subl'] }
  },
  {
    id: 'intellij',
    label: 'IntelliJ IDEA',
    icon: 'intellij',
    rank: 60,
    darwin: { apps: ['IntelliJ IDEA', 'IntelliJ IDEA CE', 'IntelliJ IDEA Ultimate', 'IntelliJ IDEA Community Edition'], cli: ['idea'] },
    win32: { exe: ['idea64.exe', 'JetBrains\\IntelliJ IDEA\\bin\\idea64.exe'] },
    linux: { bins: ['idea'] }
  },
  {
    id: 'pycharm',
    label: 'PyCharm',
    icon: 'pycharm',
    rank: 70,
    darwin: { apps: ['PyCharm', 'PyCharm CE', 'PyCharm Professional', 'PyCharm Community Edition'], cli: ['pycharm'] },
    win32: { exe: ['pycharm64.exe', 'JetBrains\\PyCharm\\bin\\pycharm64.exe'] },
    linux: { bins: ['pycharm'] }
  },
  {
    id: 'webstorm',
    label: 'WebStorm',
    icon: 'webstorm',
    rank: 80,
    darwin: { apps: ['WebStorm'], cli: ['webstorm'] },
    win32: { exe: ['webstorm64.exe', 'JetBrains\\WebStorm\\bin\\webstorm64.exe'] },
    linux: { bins: ['webstorm'] }
  },
  {
    id: 'goland',
    label: 'GoLand',
    icon: 'goland',
    rank: 90,
    darwin: { apps: ['GoLand'], cli: ['goland'] },
    win32: { exe: ['goland64.exe', 'JetBrains\\GoLand\\bin\\goland64.exe'] },
    linux: { bins: ['goland'] }
  },
  {
    id: 'clion',
    label: 'CLion',
    icon: 'clion',
    rank: 100,
    darwin: { apps: ['CLion'], cli: ['clion'] },
    win32: { exe: ['clion64.exe', 'JetBrains\\CLion\\bin\\clion64.exe'] },
    linux: { bins: ['clion'] }
  },
  {
    id: 'rider',
    label: 'Rider',
    icon: 'rider',
    rank: 110,
    darwin: { apps: ['Rider'], cli: ['rider'] },
    win32: { exe: ['rider64.exe', 'JetBrains\\JetBrains Rider\\bin\\rider64.exe'] },
    linux: { bins: ['rider'] }
  },
  {
    id: 'phpstorm',
    label: 'PhpStorm',
    icon: 'phpstorm',
    rank: 120,
    darwin: { apps: ['PhpStorm'], cli: ['phpstorm'] },
    win32: { exe: ['phpstorm64.exe', 'JetBrains\\PhpStorm\\bin\\phpstorm64.exe'] },
    linux: { bins: ['phpstorm'] }
  },
  {
    id: 'rubymine',
    label: 'RubyMine',
    icon: 'rubymine',
    rank: 130,
    darwin: { apps: ['RubyMine'], cli: ['rubymine'] },
    win32: { exe: ['rubymine64.exe', 'JetBrains\\RubyMine\\bin\\rubymine64.exe'] },
    linux: { bins: ['rubymine'] }
  },
  {
    id: 'androidstudio',
    label: 'Android Studio',
    icon: 'androidstudio',
    rank: 140,
    darwin: { apps: ['Android Studio'], cli: ['studio'] },
    win32: { exe: ['studio64.exe', 'Android\\Android Studio\\bin\\studio64.exe'] },
    linux: { bins: ['studio'] }
  },
  {
    id: 'xcode',
    label: 'Xcode',
    icon: 'xcode',
    rank: 150,
    // Xcode 的 CLI 是 `xed`(它自己不做 PATH 安装,但系统一直带着这个包装)
    darwin: { apps: ['Xcode'], cli: ['xed'] }
  }
]

/**
 * 探测要用到的那几件事 —— 全部是只读的、可注入的。
 *
 * ★ `which` 与 `exists` **分开**,因为它们在 Windows 上不是一回事:注册表里
 *   给出的绝对路径要用 `exists`,而 `code` 这种命令行入口要在 PATH 上找。
 *   合成一个的话,Windows 那条分支就只能二选一。
 */
export interface DetectionDeps {
  platform: NodeJS.Platform
  exists: (path: string) => boolean
  /** PATH 上的可执行文件 → 绝对路径;找不到返回 null */
  which: (bin: string) => string | null
  /** 注册表读一个值;读不到返回 null。非 Windows 平台恒 null */
  registry: (key: string) => string | null
  /** macOS 的应用目录,按顺序找 */
  appDirs: readonly string[]
  /** 列一个目录下的条目名;读不到返回空数组。只有 macOS 那条分支用得到 */
  listApps?: (dir: string) => readonly string[]
  /**
   * Windows 上「没走注册表」时的安装根目录。
   *
   * ★ 由调用方给而不是写死在这里:真正需要的那两个根里有一个在环境变量里
   *   (`%LOCALAPPDATA%\Programs` —— VS Code / Cursor 的**用户级安装**都落在那儿,
   *   而那恰好是最常见的装法)。写死的话,那些用户只能靠注册表命中,
   *   而绿色版和解压即用的版本压根没写注册表。
   */
  programRoots: readonly string[]
}

/**
 * 一个编辑器在这台机器上的**启动入口**。两种形态:
 *
 * - `app`:macOS 的 `.app` 包,用 `open -a <app> <file>` 起
 * - `bin`:可执行文件,直接把文件当 argv 递过去
 *
 * ★ 用 `open -a <完整路径>` 而不是 `open -b <bundleId>`:bundleId 是我手写的,
 *   写错一个字母的表现是「菜单里有这一项,点了什么也不发生」;而路径是我们
 *   刚刚**扫到过**的那一个,不存在猜错的可能。
 */
export type EditorLaunch =
  | { kind: 'app'; app: string }
  | { kind: 'bin'; bin: string }

export interface DetectedEditor extends OpenTarget {
  launch: EditorLaunch
}

/** macOS 上一个 `.app` 是不是我们要找的那一个。**大小写不敏感**,别的要求全等。 */
function appMatches(name: string, wanted: string): boolean {
  return name.toLowerCase() === `${wanted.toLowerCase()}.app`
}

/**
 * 纯函数:给定一份只读视图,给出菜单上该出现的那几项。
 *
 * ★ 文件管理器**恒在**:它走 `shell.showItemInFolder`,不需要任何可执行文件。
 * ★ 终端与 IDE 都只在**探测到了**才出现 —— 菜单少一项,而不是多一颗点了没反应的按钮。
 */
export function detectTargets(deps: DetectionDeps): DetectedEditor[] {
  const found: DetectedEditor[] = []
  for (const editor of EDITORS) {
    const launch = locate(editor, deps)
    if (launch !== null) {
      found.push({ id: editor.id, label: editor.label, icon: editor.icon, launch })
    }
  }
  return found.sort((a, b) => rankOf(a.id) - rankOf(b.id))
}

function rankOf(id: string): number {
  return EDITORS.find((editor) => editor.id === id)?.rank ?? Number.MAX_SAFE_INTEGER
}

function locate(editor: EditorDefinition, deps: DetectionDeps): EditorLaunch | null {
  if (deps.platform === 'darwin') {
    const spec = editor.darwin
    if (spec === undefined) return null
    for (const dir of deps.appDirs) {
      for (const app of spec.apps) {
        const name = `${app}.app`
        /*
          ★ 先问 `exists(完整路径)`,再退回「列目录 + 大小写不敏感比对」。
          拼出来的路径问一次是快路径;而 `appMatches` 那一步挡的是**大小写**:
          macOS 默认不区分大小写,但 `exists` 在某些挂载参数下是区分的,而
          bundle 名里的空格和大小写用户改不了 —— 列目录比对是那一步的兜底。
        */
        if (deps.exists(`${dir}/${name}`)) return { kind: 'app', app: `${dir}/${name}` }
        const actual = deps.listApps?.(dir).find((entry) => appMatches(entry, app))
        if (actual !== undefined) return { kind: 'app', app: `${dir}/${actual}` }
      }
    }
    // 退一步:`.app` 不在标准位置(Toolbox 的软链、用户自己挪过),但 CLI 装过
    for (const bin of spec.cli ?? []) {
      const hit = deps.which(bin)
      if (hit !== null) return { kind: 'bin', bin: hit }
    }
    return null
  }
  if (deps.platform === 'win32') {
    for (const exe of editor.win32?.exe ?? []) {
      // 带路径分隔符的按「安装目录探测」,否则先问注册表 App Paths 再问 PATH
      if (exe.includes('\\')) {
        for (const root of deps.programRoots) {
          const full = `${root}\\${exe}`
          if (deps.exists(full)) return { kind: 'bin', bin: full }
        }
        continue
      }
      const registered = deps.registry(`HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\${exe}`)
        ?? deps.registry(`HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\${exe}`)
      if (registered !== null && deps.exists(registered)) return { kind: 'bin', bin: registered }
      const hit = deps.which(exe)
      if (hit !== null) return { kind: 'bin', bin: hit }
    }
    return null
  }
  for (const bin of editor.linux?.bins ?? []) {
    const hit = deps.which(bin)
    if (hit !== null) return { kind: 'bin', bin: hit }
  }
  return null
}

/** 一个终端模拟器的启动方式。`cwd` 为真表示「用 spawn 的 cwd 带目录」,否则靠参数。 */
export interface TerminalDefinition {
  /** 可执行名或绝对路径 */
  file: string
  args: (dir: string) => string[]
  /** 直接把 `dir` 当子进程的 cwd(CLI 终端用) */
  cwd: boolean
}

/**
 * 终端候选,按顺序试第一个存在的。
 *
 * ★ **打开的是目录,不是文件。** 「用终端打开 a.ts」不是一个动作 —— 终端不认
 *   文件参数(认的那几个是把它当脚本跑,那是另一回事,而且是危险的默认值)。
 *   所以菜单项写的是「在此文件所在目录打开终端」,文案与行为对齐。
 *
 * ★ 最后一项是**系统自带的那个**,永远在。前几项是「装了就用你装的那个」。
 */
export function terminalCandidates(platform: NodeJS.Platform): readonly TerminalDefinition[] {
  if (platform === 'darwin') {
    return [
      // Ghostty 与 iTerm 认「开在这个目录」,Warp 与 Terminal.app 认「打开这个目录」
      { file: '/Applications/Ghostty.app', args: (dir) => ['-a', 'Ghostty', dir], cwd: false },
      { file: '/Applications/iTerm.app', args: (dir) => ['-a', 'iTerm', dir], cwd: false },
      { file: '/Applications/Warp.app', args: (dir) => ['-a', 'Warp', dir], cwd: false },
      { file: 'wezterm', args: (dir) => ['start', '--cwd', dir], cwd: false },
      { file: 'kitty', args: (dir) => ['--directory', dir], cwd: false },
      { file: 'alacritty', args: (dir) => ['--working-directory', dir], cwd: false },
      { file: '/Applications/Hyper.app', args: (dir) => ['-a', 'Hyper', dir], cwd: false },
      // 系统终端:没有它的话 macOS 上「终端」这一项就没了,而它其实一直在
      { file: '/System/Applications/Utilities/Terminal.app', args: (dir) => ['-a', 'Terminal', dir], cwd: false }
    ]
  }
  if (platform === 'win32') {
    /*
      ★ Windows 上不挑模拟器,直接开一个**系统 shell 窗口**并把 cwd 设成那个目录。
      `detached: true` 在 Windows 上隐含 CREATE_NEW_CONSOLE,所以会真的弹一个窗口;
      `windowsHide` 必须显式给 false,否则它会安静地在后台起来然后立刻退出 ——
      表现是「点了终端什么也没发生」。
    */
    return [
      { file: 'wt.exe', args: (dir) => ['-d', dir], cwd: false },
      { file: 'powershell.exe', args: () => [], cwd: true },
      { file: 'cmd.exe', args: () => [], cwd: true }
    ]
  }
  return [
    { file: 'gnome-terminal', args: (dir) => [`--working-directory=${dir}`], cwd: false },
    { file: 'konsole', args: (dir) => ['--workdir', dir], cwd: false },
    { file: 'xfce4-terminal', args: (dir) => [`--working-directory=${dir}`], cwd: false },
    { file: 'wezterm', args: (dir) => ['start', '--cwd', dir], cwd: false },
    { file: 'kitty', args: (dir) => ['--directory', dir], cwd: false },
    { file: 'alacritty', args: (dir) => ['--working-directory', dir], cwd: false },
    { file: 'x-terminal-emulator', args: () => [], cwd: true }
  ]
}

/** 探测到的终端;`null` 表示这台机器上一个都没有(理论上只可能出现在精简的 Linux 上)。 */
export function detectTerminal(deps: DetectionDeps): TerminalDefinition | null {
  for (const candidate of terminalCandidates(deps.platform)) {
    // macOS 那几项给的是 `.app` 包,用 `open` 起;其余按可执行文件找
    if (candidate.file.startsWith('/')) {
      if (deps.exists(candidate.file)) return candidate
      continue
    }
    if (deps.which(candidate.file) !== null) return candidate
  }
  return null
}

/**
 * 菜单最终该画哪几项。
 *
 * ★ 顺序是**刻意的**:文件管理器 → 终端 → 编辑器(按 rank)。前两项是「这个文件
 *   在磁盘上的位置」,后一组是「用谁写它」;把编辑器混在中间会让「在文件管理器里
 *   显示」这种高频动作每次都要在列表里找位置。
 *   默认应用排在文件管理器之后、终端之前:它同样不属于「用谁写它」那一组,
 *   是「交给系统」的通用出口(见 `shared/domain/open-target.ts` 的 `DEFAULT_APP_TARGET_ID`)。
 */
export function buildTargets(editors: readonly DetectedEditor[], terminal: TerminalDefinition | null): OpenTarget[] {
  const targets: OpenTarget[] = [
    { id: REVEAL_TARGET_ID, label: '', icon: 'file-manager' },
    // 系统默认应用和文件管理器一样不需要探测:`shell.openPath` 各平台都有(见 open-target.ts)
    { id: DEFAULT_APP_TARGET_ID, label: '', icon: 'default-app' }
  ]
  if (terminal !== null) targets.push({ id: TERMINAL_TARGET_ID, label: '', icon: 'terminal' })
  for (const editor of editors) targets.push({ id: editor.id, label: editor.label, icon: editor.icon })
  return targets
}
