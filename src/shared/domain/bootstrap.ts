/**
 * 首屏握手载荷 —— 协议 §7 的时序:
 * `window:ready` → `app:getBootstrap` → 首屏 → 增量事件。
 *
 * 一次拿全,而不是让渲染层开局打七八个 invoke —— 那样会出现「设置到了但工作区还没到」
 * 的中间态,每个组件都得写一遍 loading 分支。
 */
import type { ActiveRunEntry, RunStatus } from '../agent/event'
import type { AppSettings, ResolvedTheme } from './settings'
import type { WindowKind, WindowTabState } from './tab'
import type { Workspace } from './workspace'

export interface Bootstrap {
  windowKind: WindowKind
  settings: AppSettings
  /** 主进程 nativeTheme 解析后的实际主题,渲染层直接写 data-theme */
  resolvedTheme: ResolvedTheme
  workspaces: Workspace[]
  /** 上次退出时的外层 Tab 布局(含**用户拖出来的顺序**),从 kv 表读 */
  tabState: WindowTabState
  /**
   * ★ 启动时仍在跑的 run。正常冷启动是空的 —— 「永不恢复运行中状态」(方案 §9)。
   * 非空只发生在渲染层重载(⌘R)时:主进程没重启,run 还活着。
   *
   * 首帧之后的变化由 `agent:activeRuns` 广播接手 —— 同一个类型,同一份口径。
   */
  activeRuns: ActiveRunEntry[]
  /** Background child runs survive a renderer reload and are restored into their parent Task card. */
  activeSubagents?: Array<{
    runId: string
    parentRunId: string
    sessionId: string
    workspaceId: string
    status: RunStatus
    startedAt?: number
  }>
  versions: {
    app: string
    electron: string
    chrome: string
    node: string
  }
  /**
   * ★ **首屏刷盘没刷成功。** 渲染层重载可能赶在 Tab 布局 / 草稿的防抖到期之前,
   * 于是 `app:getBootstrap` 会先把那些待落盘的键冲一遍 —— 这一次失败的话,
   * 内存里那份还会由各自的运行时按后续动作再写一遍,但**这一次**确实没落到盘上。
   *
   * 它只表示「有没成功刷盘的输入 / 布局」这一个事实,不带原因,更不带异常 ——
   * 异常的正文里有绝对路径与库文件名,而这条字段是要进渲染层的(方案 §9)。
   * 真读库失败(例如 `store.getSettings()`)仍然照旧抛出去,由渲染层的
   * 握手失败分支显示 —— 那是两回事,不能混成一句「可能没保存」。
   */
  persistenceWarning?: boolean
}
