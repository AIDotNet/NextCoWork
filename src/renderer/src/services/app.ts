/**
 * 应用级服务:握手、设置、工作区、Tab 布局持久化。
 *
 * 协议 §7 的握手时序在这里成型:
 *   send('window:ready') → invoke('app:getBootstrap') → 首屏 → on('...') 增量
 */
import type { Bootstrap } from '../../../shared/domain/bootstrap'
import type { DirListing, FileSuggestion } from '../../../shared/domain/file-tree'
import type { SessionInputState } from '../../../shared/domain/queued-input'
import type { AppSettings, AppSettingsPatch } from '../../../shared/domain/settings'
import type { UpdateState } from '../../../shared/domain/update'
import type { InnerTabState, WindowKind, WindowTabState } from '../../../shared/domain/tab'
import type { Workspace, WorkspaceSettings } from '../../../shared/domain/workspace'
import { invoke, send } from './ipc'

// ─── 握手 ───

export function announceReady(kind: WindowKind = 'main'): void {
  send('window:ready', { kind })
}

export function getBootstrap(): Promise<Bootstrap> {
  return invoke('app:getBootstrap', undefined)
}

export function openExternal(url: string): Promise<void> {
  return invoke('app:openExternal', { url })
}

export function checkForUpdates() {
  return invoke('app:checkForUpdates', undefined)
}

export function updateCheck(): Promise<UpdateState> {
  return invoke('app:updateCheck', undefined)
}

export function updateDownload(): Promise<UpdateState> {
  return invoke('app:updateDownload', undefined)
}

export function updateInstall(): Promise<void> {
  return invoke('app:updateInstall', undefined)
}

export function updateGetState(): Promise<UpdateState> {
  return invoke('app:updateGetState', undefined)
}

export function copyText(text: string): Promise<void> {
  return invoke('app:copyText', { text })
}

/** 把一张图写进系统剪贴板。`base64` 是**原图字节**,不带 `data:` 前缀。 */
export function copyImage(base64: string): Promise<void> {
  return invoke('app:copyImage', { base64 })
}

/** 另存为。`defaultName` 只是建议名,落点由主进程弹出的系统对话框决定;取消返回 null。 */
export function saveTextFile(defaultName: string, text: string): Promise<{ path: string } | null> {
  return invoke('app:saveTextFile', { defaultName, text })
}

/**
 * 另存一张图片。`base64` **不带** `data:image/png;base64,` 前缀;取消返回 null。
 * 扩展名由主进程按字节判 —— 调用方给的 `defaultName` 只是名字,不必带对扩展名。
 */
export function saveImageFile(defaultName: string, base64: string): Promise<{ path: string } | null> {
  return invoke('app:saveImageFile', { defaultName, base64 })
}

export function openSessionWindow(workspaceId: string, sessionId: string): Promise<void> {
  return invoke('app:openSessionWindow', { workspaceId, sessionId })
}

/**
 * 用户已经在「有未保存的改动」对话框里选完了(保存或丢弃),重走一次退出。
 *
 * ★ 只在**响应退出**时才调(`DocumentDialogs` 的 `confirmUnload`),不要拿它当
 * 通用退出入口:它不做任何询问,直接让主进程开始关窗 —— 这一轮里那个
 * `beforeunload` 已经不会再挡(渲染层把 `allowUnload` 置上了)。
 */
export function quitConfirmed(): void {
  send('app:quitConfirmed', {})
}

// ─── 设置 ───

export function getSettings(): Promise<AppSettings> {
  return invoke('settings:get', undefined)
}

/**
 * ★ 嵌套块**只给要改的那一个属性**,别自己 `{ ...settings.gateway, x }` 组装 ——
 * `settings` 是 prop,在广播回来之前它是旧的,组装等于把兄弟属性回滚。
 * 主进程侧是深合并(shared/domain/settings.ts 的 mergeSettings)。
 */
export function updateSettings(patch: AppSettingsPatch): Promise<AppSettings> {
  return invoke('settings:update', patch)
}

// ─── 工作区 ───

export function listWorkspaces(): Promise<Workspace[]> {
  return invoke('workspace:list', undefined)
}

/** 主进程弹目录选择框;渲染层永不指定路径(方案 §9)。取消时返回 null。 */
export function pickWorkspace(): Promise<Workspace | null> {
  return invoke('workspace:pick', undefined)
}

export function updateWorkspace(req: {
  id: string
  name?: string
  settings?: Partial<WorkspaceSettings>
}): Promise<Workspace> {
  return invoke('workspace:update', req)
}

export function closeWorkspace(id: string): Promise<void> {
  return invoke('workspace:close', { id })
}

/**
 * 列一层目录。`path` 是**工作区相对**路径,`''` = 工作区根。
 * 树是懒加载的,展开一个目录才调一次 —— 别在这里写递归。
 */
export function listDir(workspaceId: string, path: string): Promise<DirListing> {
  return invoke('workspace:listDir', { workspaceId, path })
}

/**
 * 输入框 `@` 的文件检索。**排序在主进程做**,回来的已经是排好序的十几条 ——
 * 渲染层不该自己再排一遍(那就有两套顺序了,见 `shared/domain/fuzzy-path.ts`)。
 */
export function searchWorkspaceFiles(
  workspaceId: string,
  query: string,
  limit?: number
): Promise<FileSuggestion[]> {
  return invoke('workspace:searchFiles', { workspaceId, query, limit })
}

// ─── Tab 布局 ───

export function getInnerTabs(workspaceId: string): Promise<InnerTabState> {
  return invoke('tabs:getInner', { workspaceId })
}

/**
 * ★ 用 send 不用 invoke:拖动排序时每帧都在变,渲染层不需要返回值。
 * 主进程侧防抖 500ms 再落盘。
 */
export function persistOuterTabs(kind: WindowKind, state: WindowTabState): void {
  send('tabs:persistOuter', { kind, state })
}

export function persistInnerTabs(workspaceId: string, state: InnerTabState): void {
  send('tabs:persistInner', { workspaceId, state })
}

// ─── 未发出的输入(草稿 + 插入队列) ───

/**
 * 返回 null = 没有存档,或存档已失效(版本不符 / 超 30 天)。
 * **校验在主进程侧做**,这里拿到的要么可用要么是 null,不需要二次判断。
 */
export function getSessionInput(sessionId: string): Promise<SessionInputState | null> {
  return invoke('session:getInput', { sessionId })
}

/**
 * 草稿落盘。★ `immediate` 两档:按键走主进程 500ms 防抖;绑定会话、撤回到输入框这类
 * 离散动作立即写。**只有草稿** —— 队列由主进程独占(见 `services/agent.ts` 的
 * `queueSessionInput`),渲染层整份覆盖会把已经续跑掉的排队消息写回去。
 */
export function persistSessionDraft(
  sessionId: string,
  draft: string,
  immediate = false
): void {
  send('session:persistDraft', { sessionId, draft, immediate })
}
