import type { BrowserChange, BrowserProfile, BrowserTab } from '../../shared/domain/browser'
import { EnvironmentError, isLocalEnvironment } from '../../shared/domain/environment'
import { ulid } from '../../shared/util/id'
import { store } from '../state/store'

export function assertLocalBrowserWorkspace(workspaceId: string): void {
  const workspace = store.getWorkspace(workspaceId)
  if (!workspace) throw new EnvironmentError('unbound')
  if (!isLocalEnvironment(workspace.environment)) throw new EnvironmentError('unsupported')
}

const PROFILES_KEY = 'browser.profiles'
const DEFAULT_PROFILE_ID = 'default'

export type BrowserOpenInput = {
  workspaceId: string
  url: string
  title?: string
  profileId?: string
  backend?: BrowserTab['backend']
  openRightPanel?: boolean
} & (
  | { source: 'user'; ownerRunId?: never; ownerSessionId?: never; clientTabId?: string }
  | { source: 'agent'; ownerRunId: string; ownerSessionId?: string; clientTabId?: never }
)

export interface BrowserActor {
  workspaceId: string
  runId: string
  sessionId?: string
}

export interface BrowserManagerListener {
  (change: BrowserChange): void
}

/*
  需求：工作区所有者要求浏览器能打开 `file://` 本地页面（2026-09-22，与放开
  内网地址同一批要求，风险记在 `kernel/tool/builtin/ssrf.ts` 的 `allowFileUrls`）。
  所以协议闸放行 http/https/file 三种；`javascript:`、`data:` 等照旧拒。

  ★ 凭证拦截不受影响：file://user:pass@ 同样拒（理由见 ssrf.ts 的同名检查）。
*/
function normalizeUrl(raw: string): string {
  const value = raw.trim()
  let parsed: URL
  try {
    parsed = new URL(value)
  } catch {
    throw new Error('浏览器只支持完整的 http://、https:// 或 file:// 地址')
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:' && parsed.protocol !== 'file:') {
    throw new Error('浏览器只支持 http://、https:// 或 file:// 地址')
  }
  if (parsed.username !== '' || parsed.password !== '') {
    throw new Error('浏览器地址不能包含用户名或密码')
  }
  return parsed.href
}

/**
 * 未给标题时从地址推一个。★ `file://` 的 hostname 是空串 —— 直接用它当标题
 * 会得到一个没有文字的标签，症状是「右侧工作台开了个空白格子，认不出是哪一页」。
 * 所以空 hostname 时退回路径末段（`file:///a/b.html` → `b.html`）。
 */
function titleFromUrl(url: string): string {
  const parsed = new URL(url)
  if (parsed.hostname !== '') return parsed.hostname
  const last = parsed.pathname.split('/').filter(Boolean).pop()
  if (last === undefined || last === '') return url
  // 路径里可能有非法的 % 序列（`%zz`），decodeURIComponent 会抛 URIError ——
  // 标题推导不该让整个 open() 失败，解不动就用原样。
  try {
    return decodeURIComponent(last)
  } catch {
    return last
  }
}

function copyTab(tab: BrowserTab): BrowserTab {
  return {
    ...tab,
    ...(tab.viewport === undefined ? {} : { viewport: { ...tab.viewport } })
  }
}

/**
 * 进程内浏览器会话目录。它不持有 Electron BrowserWindow，因此 Agent 工具
 * 和渲染层可以共享同一份可验证状态，而不会把 webContents 泄露给工具层。
 */
export class BrowserManager {
  private readonly tabs = new Map<string, BrowserTab>()
  private profiles: BrowserProfile[] | null = null
  private listener: BrowserManagerListener | null = null

  constructor(private readonly assertWorkspace: (workspaceId: string) => void = () => {}) {}

  setListener(listener: BrowserManagerListener | null): void {
    this.listener = listener
  }

  listProfiles(): BrowserProfile[] {
    this.ensureProfiles()
    return this.profiles!.map((profile) => ({ ...profile, domains: [...profile.domains] }))
  }

  createProfile(name: string, domains: readonly string[] = [], startUrl?: string): BrowserProfile {
    this.ensureProfiles()
    const cleanName = name.trim()
    if (cleanName === '') throw new Error('Profile 名称不能为空')
    if (cleanName.length > 80) throw new Error('Profile 名称不能超过 80 个字符')
    const cleanStartUrl = startUrl?.trim() ?? ''
    let normalizedStartUrl: string | undefined
    if (cleanStartUrl !== '') {
      const parsed = new URL(cleanStartUrl)
      if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') throw new Error('默认打开 URL 只支持 http:// 或 https:// 地址')
      if (parsed.username !== '' || parsed.password !== '') throw new Error('默认打开 URL 不能包含用户名或密码')
      normalizedStartUrl = parsed.href
    }
    const profile: BrowserProfile = {
      id: ulid(),
      name: cleanName,
      isDefault: false,
      domains: [...new Set(domains.map((domain) => domain.trim().toLowerCase()).filter(Boolean))].slice(0, 30),
      ...(normalizedStartUrl === undefined ? {} : { startUrl: normalizedStartUrl }),
      createdAt: Date.now(),
      lastUsedAt: Date.now()
    }
    this.profiles = [...this.profiles!, profile]
    this.persistProfiles()
    return { ...profile, domains: [...profile.domains] }
  }

  deleteProfile(id: string): void {
    this.ensureProfiles()
    const profile = this.profiles!.find((item) => item.id === id)
    if (profile === undefined) throw new Error('Profile 不存在')
    if (profile.isDefault || profile.id === DEFAULT_PROFILE_ID) throw new Error('默认浏览器不能删除')
    this.profiles = this.profiles!.filter((item) => item.id !== id)
    const affectedWorkspaces = new Set<string>()
    for (const [tabId, tab] of this.tabs) {
      if (tab.profileId !== id) continue
      this.tabs.delete(tabId)
      affectedWorkspaces.add(tab.workspaceId)
    }
    this.persistProfiles()
    for (const workspaceId of affectedWorkspaces) this.emit(workspaceId)
  }

  list(workspaceId: string): BrowserTab[] {
    return [...this.tabs.values()]
      .filter((tab) => tab.workspaceId === workspaceId)
      .sort((a, b) => a.createdAt - b.createdAt)
      .map(copyTab)
  }

  get(id: string): BrowserTab | undefined {
    const tab = this.tabs.get(id)
    return tab === undefined ? undefined : copyTab(tab)
  }

  open(input: BrowserOpenInput): BrowserTab {
    this.assertWorkspace(input.workspaceId)
    if (input.profileId !== undefined && !this.listProfiles().some((profile) => profile.id === input.profileId)) {
      throw new Error('指定的浏览器 Profile 不存在')
    }
    const now = Date.now()
    const url = normalizeUrl(input.url)
    if (input.source === 'user' && input.clientTabId !== undefined) {
      const existing = [...this.tabs.values()].find(
        (tab) =>
          tab.source === 'user' &&
          tab.workspaceId === input.workspaceId &&
          tab.clientTabId === input.clientTabId
      )
      if (existing !== undefined) return copyTab(existing)
    }
    if (input.profileId !== undefined) {
      this.ensureProfiles()
      this.profiles = this.profiles!.map((profile) =>
        profile.id === input.profileId ? { ...profile, lastUsedAt: now } : profile
      )
      this.persistProfiles()
    }
    const tab: BrowserTab = {
      id: ulid(),
      workspaceId: input.workspaceId,
      ...(input.clientTabId === undefined ? {} : { clientTabId: input.clientTabId }),
      ...(input.ownerRunId === undefined ? {} : { ownerRunId: input.ownerRunId }),
      ...(input.ownerSessionId === undefined ? {} : { ownerSessionId: input.ownerSessionId }),
      ...(input.profileId === undefined ? {} : { profileId: input.profileId }),
      source: input.source,
      backend: input.backend ?? 'iab',
      url,
      title: input.title?.trim() || titleFromUrl(url),
      status: 'loading',
      createdAt: now,
      updatedAt: now
    }
    this.tabs.set(tab.id, tab)
    this.emit(tab.workspaceId, input.openRightPanel === true)
    return copyTab(tab)
  }

  navigate(id: string, url: string, actor?: BrowserActor): BrowserTab {
    const tab = this.requireOwned(id, actor)
    this.assertWorkspace(tab.workspaceId)
    const next: BrowserTab = {
      ...tab,
      url: normalizeUrl(url),
      status: 'loading',
      updatedAt: Date.now()
    }
    this.tabs.set(id, next)
    this.emit(next.workspaceId)
    return copyTab(next)
  }

  update(
    id: string,
    patch: { url?: string; title?: string; status?: BrowserTab['status']; viewport?: BrowserTab['viewport'] },
    actor?: BrowserActor
  ): BrowserTab {
    const tab = this.requireOwned(id, actor)
    this.assertWorkspace(tab.workspaceId)
    const next: BrowserTab = {
      ...tab,
      ...(patch.url === undefined ? {} : { url: normalizeUrl(patch.url) }),
      ...(patch.title === undefined ? {} : { title: patch.title.trim() || tab.title }),
      ...(patch.status === undefined ? {} : { status: patch.status }),
      ...(patch.viewport === undefined ? {} : { viewport: { ...patch.viewport } }),
      updatedAt: Date.now()
    }
    this.tabs.set(id, next)
    this.emit(next.workspaceId)
    return copyTab(next)
  }

  claim(id: string, actor: BrowserActor): BrowserTab {
    const tab = this.tabs.get(id)
    if (tab === undefined) throw new Error(`浏览器标签不存在: ${id}`)
    if (tab.workspaceId !== actor.workspaceId) throw new Error('浏览器标签不属于当前工作区')
    const sameSession = actor.sessionId !== undefined && tab.ownerSessionId === actor.sessionId
    if (tab.ownerRunId !== undefined && tab.ownerRunId !== actor.runId && !sameSession) {
      throw new Error('浏览器标签已被另一个 Agent 会话认领')
    }
    if (tab.source !== 'user') throw new Error('只能认领用户打开的浏览器标签')
    if (tab.ownerRunId === actor.runId) return copyTab(tab)
    const next: BrowserTab = {
      ...tab,
      ownerRunId: actor.runId,
      ...(actor.sessionId === undefined ? {} : { ownerSessionId: actor.sessionId }),
      updatedAt: Date.now()
    }
    this.tabs.set(id, next)
    this.emit(next.workspaceId)
    return copyTab(next)
  }

  close(id: string, actor?: BrowserActor): void {
    const tab = this.requireOwned(id, actor)
    this.tabs.delete(id)
    this.emit(tab.workspaceId)
  }

  closeWorkspace(workspaceId: string): void {
    let changed = false
    for (const [id, tab] of this.tabs) {
      if (tab.workspaceId === workspaceId) {
        this.tabs.delete(id)
        changed = true
      }
    }
    if (changed) this.emit(workspaceId)
  }

  private requireOwned(id: string, actor?: BrowserActor): BrowserTab {
    const tab = this.tabs.get(id)
    if (tab === undefined) throw new Error(`浏览器标签不存在: ${id}`)
    if (
      actor !== undefined &&
      (
        tab.workspaceId !== actor.workspaceId ||
        (tab.ownerRunId !== actor.runId && (actor.sessionId === undefined || tab.ownerSessionId !== actor.sessionId))
      )
    ) {
      throw new Error('只能操作当前 Agent 打开或认领的浏览器标签')
    }
    return tab
  }

  /**
   * 配置作用域变了:关掉所有 Tab,并丢掉缓存的那份 Profile 清单。
   *
   * ★ Tab 属于上一个账户的工作区,而工作区**已经不在**新作用域里 ——
   * 留着它们就是「A 的页面还开在 B 的界面上」,而面板给的还是能点的。
   *
   * ★ Profile 清单缓存(`this.profiles`)存的是 `browser.profiles` 这个 kv 键,
   * 而那个键**在白名单里**(它随作用域走)。不清缓存的话,下一次 `listProfiles()`
   * 直接返回 A 的那份,连读都不读 —— 它的 `ensureProfiles()` 只在 null 时才读。
   *
   * ★★ **不碰 cookies。** 每个 Profile 的 partition 是
   * `persist:ncw-workspace-<workspaceId>-profile-<profileId>`,而工作区 id 是
   * ULID、跨作用域不会重号 —— 所以 A 的登录态在 B 里本来就取不到。
   * 反过来「切走时清一遍」是**有破坏性**的:A 的 cookies 会被删掉,
   * 而用户只是登了个 B 的账户。
   */
  resetForConfigScopeChange(): void {
    const affected = new Set([...this.tabs.values()].map((tab) => tab.workspaceId))
    this.tabs.clear()
    this.profiles = null
    for (const workspaceId of affected) this.emit(workspaceId)
  }

  private emit(workspaceId: string, rightPanelOpen = false): void {
    this.listener?.({ workspaceId, tabs: this.list(workspaceId), ...(rightPanelOpen ? { rightPanelOpen: true } : {}) })
  }

  private ensureProfiles(): void {
    if (this.profiles !== null) return
    const fallback: BrowserProfile = {
      id: DEFAULT_PROFILE_ID,
      name: 'Default browser',
      isDefault: true,
      domains: [],
      createdAt: Date.now(),
      lastUsedAt: Date.now()
    }
    try {
      const raw = store.getKv<unknown>(PROFILES_KEY, null)
      if (Array.isArray(raw)) {
        const valid = raw.filter((item): item is BrowserProfile => {
          if (typeof item !== 'object' || item === null) return false
          const value = item as Record<string, unknown>
          return typeof value.id === 'string' && typeof value.name === 'string' && typeof value.isDefault === 'boolean' && Array.isArray(value.domains)
        })
        if (valid.some((item) => item.isDefault && item.id === DEFAULT_PROFILE_ID)) {
          this.profiles = valid.map((item) => ({ ...item, domains: item.domains.filter((domain): domain is string => typeof domain === 'string') }))
          return
        }
      }
    } catch {
      // The in-memory fallback is useful for headless tool tests before SQLite opens.
    }
    this.profiles = [fallback]
    this.persistProfiles()
  }

  private persistProfiles(): void {
    try {
      store.setKv(PROFILES_KEY, this.profiles)
    } catch {
      // Ignore persistence errors; browser sessions remain usable for this run.
    }
  }
}

export const browserManager = new BrowserManager(assertLocalBrowserWorkspace)

export function setBrowserChangeListener(listener: BrowserManagerListener | null): void {
  browserManager.setListener(listener)
}
