/**
 * `stores/tabs.ts` ⇄ `stores/window.ts` 之间的**窄 typed port**。
 *
 * ★ 这两个 store 本来是**直接互引**的:`tabs` 读 `window` 的 `windowKind` 和
 * 右侧/底部面板开关,`window` 在关工作区时反过来调 `tabs.forget`。互相 import
 * 在 ESM 下能跑,但它是「谁先初始化」的隐性依赖,而且把两个模块焊成了一坨:
 * 想单独测其中一个就得把另一个拖进来(它背后还有 services、session、plugins……)。
 *
 * 拆法:两边各自**安装自己那一半**到这一个 port 上,谁也不 import 谁。调用方
 * 通过 `shellWindow()` / `shellTabs()` 取那一半 —— 装的是真实现,没装就**明确
 * 报一次错再返回 null**,不静默 no-op。
 *
 * ★ 为什么不是「返回默认空实现」:那会把「没接线」伪装成「接线了但没反应」。
 * 关掉工作区时 `forget` 没被调到,表现是切回该工作区时 Transcript 还在内存里 ——
 * 而控制台里一片干净,没人查得出来。
 */
import type { InnerTabState, WindowKind } from '../../../shared/domain/tab'

export interface ShellWindowPort {
  windowKind: () => WindowKind
  setRightPanelForWorkspace: (workspaceId: string, open: boolean) => void
  setBottomPanelForWorkspace: (workspaceId: string, open: boolean) => void
  /** 结构性变更时落盘内层 Tab 布局(专注窗口除外,判断在安装方那侧)。 */
  persistInnerTabs: (workspaceId: string, state: InnerTabState) => void
}

export interface ShellTabsPort {
  /** 关掉一个工作区的外层标签时,放掉它的内层 Tab 表与转录。 */
  forgetTabsForWorkspace: (workspaceId: string) => void
}

let windowHalf: ShellWindowPort | null = null
let tabsHalf: ShellTabsPort | null = null
let warnedWindow = false
let warnedTabs = false

/** 由 `stores/window.ts` 在模块底部安装。 */
export function installShellWindowPort(port: ShellWindowPort): void {
  windowHalf = port
}

/** 由 `stores/tabs.ts` 在模块底部安装。 */
export function installShellTabsPort(port: ShellTabsPort): void {
  tabsHalf = port
}

export function shellWindow(): ShellWindowPort | null {
  if (windowHalf === null && !warnedWindow) {
    warnedWindow = true
    console.error('[shell-port] 窗口那一半还没安装:内层 Tab 的开关/落盘同步这一步被跳过了')
  }
  return windowHalf
}

export function shellTabs(): ShellTabsPort | null {
  if (tabsHalf === null && !warnedTabs) {
    warnedTabs = true
    console.error('[shell-port] 内层 Tab 那一半还没安装:关工作区时的转录释放这一步被跳过了')
  }
  return tabsHalf
}

/** 仅供测试:清掉已装的两半并复位告警标志。 */
export function resetShellPortsForTests(): void {
  windowHalf = null
  tabsHalf = null
  warnedWindow = false
  warnedTabs = false
}
