/**
 * 主进程状态的访问器。配置、会话与完整转录都落在 SQLite 里(`src/main/db/`)。
 *
 * 这一层刻意保留下来,没有让 handler 直接调 `db/repo`:
 *
 * - 它是**唯一的收口点**。全应用没有第二个地方摸持久化,所以「把 Db 挪进
 *   `utilityProcess`」那天要改的仍然只有这一层的实现(`DatabaseSync` 是同步的,
 *   迟早要挪 —— 理由写在 `db/index.ts` 文件头)。
 * - kv 的键名(`outerTabKey` / `innerTabKey`)属于这一层的词汇,不属于数据库。
 * - 调用点不需要知道各领域对象在 SQLite 中的具体表结构。
 *
 * 当初那句「步骤 6 的迁移只该是『实现换掉』,不该是『调用点全改』」就是为这一刻写的:
 * 这次替换**没有改动任何一个访问器的签名**,同目录的 `__tests__/store.test.ts`
 * 原样跑过。
 */
import type { AgentMessage } from '../../shared/agent/message'
import type { ContextSearchHit } from '../../shared/agent/context-management'
import type { McpServerConfig } from '../../shared/domain/mcp'
import type { ModelAlias, UpstreamProvider } from '../../shared/domain/provider'
import type { ProviderAccountLimit, ProviderQuotaSnapshot } from '../../shared/domain/provider-account'
import type { ModelCatalogDefinition } from '../../shared/domain/model-catalog'
import type { ModelPricing } from '../../shared/domain/pricing'
import type { SearchProviderConfig, SearchProviderId } from '../../shared/domain/search'
import type { AppSettings, AppSettingsPatch } from '../../shared/domain/settings'
import type { InnerTabState, WindowTabState } from '../../shared/domain/tab'
import type { Workspace } from '../../shared/domain/workspace'
import type { ConnectionProfile } from '../../shared/domain/environment'
import type { ScheduledRun, ScheduledTask, ScheduledTaskInput } from '../../shared/domain/scheduled'
import { normalizeScheduledTaskInput, nextScheduledOccurrence } from '../../shared/domain/scheduled'
import type { Session, SessionDetail, SessionListItem, SessionPage, SearchHit } from '../../shared/domain/session'
import type { ChangeSetState, ReviewChangeSet, ReviewFileDiff } from '../../shared/domain/review'
import type {
  UsageActivityStats,
  UsageAttemptRecord,
  UsageDailyBucket,
  UsageDimensionStat,
  UsageRequestLogsPage,
  UsageRequestLogsQuery,
  UsageSummary,
  UsageWindow
} from '../../shared/domain/usage'
import type { SessionCreateInput } from '../db/repo'
import * as repo from '../db/repo'
import * as providerAccounts from '../db/provider-accounts'
import * as usageRollup from '../db/usage-rollup'
import { ulid } from '../../shared/util/id'

/** Skill 全局开关的 kv 键。值是**被关掉**的那些 id。 */
const DISABLED_SKILLS_KEY = 'skills.disabled'
const SKILL_STATS_KEY = 'skills.stats'
/**
 * 命令 / 子代理的开关。同样存**被关掉**的那些名字，理由同 `getDisabledSkillIds`。
 *
 * ★ 存 kv 而不是写进 `.md` 的 frontmatter：「这台机器上我不想用这条」是一个
 * 每机器偏好，写进文件会在共享仓库里产生一个 git diff，替队友做了决定 ——
 * 这和 `settings.local.json` 文件头反对的是同一件事。
 */
const DISABLED_COMMANDS_KEY = 'commands.disabled'
const DISABLED_AGENTS_KEY = 'agents.disabled'
const SCHEDULED_TASKS_KEY = 'scheduled.tasks'
const SCHEDULED_RUNS_KEY = 'scheduled.runs'
let scheduledLegacyMigrated = false
export interface SkillUsageStat { count: number; lastTriggeredAt: number; workspaces: Record<string, number>; lastTriggeredAtByWorkspace?: Record<string, number> }

/** kv 里那张名字表读回来。★ 宽容读：kv 是用户能手改的，一个坏值不该让开关整个失灵。 */
function readNameList(key: string): string[] {
  const raw = repo.getKv<unknown>(key, [])
  return Array.isArray(raw) ? raw.filter((x): x is string => typeof x === 'string') : []
}

function toggleName(current: readonly string[], name: string, enabled: boolean): string[] {
  const now = new Set(current)
  if (enabled) now.delete(name)
  else now.add(name)
  return [...now]
}

/** One-time bridge for builds that stored scheduled data in kv before migration 18. */
function migrateLegacyScheduledData(): void {
  if (scheduledLegacyMigrated) return
  scheduledLegacyMigrated = true
  const legacyTasks = repo.getKv<unknown>(SCHEDULED_TASKS_KEY, [])
  if (Array.isArray(legacyTasks) && repo.listScheduledTasks().length === 0) {
    for (const item of legacyTasks) {
      if (item !== null && typeof item === 'object' && typeof (item as ScheduledTask).id === 'string') repo.putScheduledTask(item as ScheduledTask)
    }
  }
  const legacyRuns = repo.getKv<unknown>(SCHEDULED_RUNS_KEY, [])
  if (Array.isArray(legacyRuns) && repo.listScheduledRuns(undefined, 1).length === 0) {
    for (const item of legacyRuns) {
      if (item !== null && typeof item === 'object' && typeof (item as ScheduledRun).id === 'string') repo.putScheduledRun(item as ScheduledRun)
    }
  }
  if (Array.isArray(legacyTasks) || Array.isArray(legacyRuns)) {
    repo.removeKv(SCHEDULED_TASKS_KEY)
    repo.removeKv(SCHEDULED_RUNS_KEY)
  }
}

export const store = {
  // ── settings ──
  getSettings(): AppSettings {
    return repo.getSettings()
  },
  updateSettings(patch: AppSettingsPatch): AppSettings {
    // 合并规则在 shared/domain/settings.ts 的 mergeSettings —— 纯函数,有测试。
    // 曾经是浅合并,于是连点同一块里的两个开关,第二次写会把第一次的覆盖回去。
    return repo.updateSettings(patch)
  },

  // ── 定时任务 ──
  listScheduledTasks(workspaceId?: string): ScheduledTask[] {
    migrateLegacyScheduledData()
    return repo.listScheduledTasks(workspaceId)
  },
  getScheduledTask(id: string): ScheduledTask | undefined {
    migrateLegacyScheduledData()
    return repo.getScheduledTask(id)
  },
  putScheduledTask(task: ScheduledTask): ScheduledTask {
    return repo.putScheduledTask(task)
  },
  createScheduledTask(input: ScheduledTaskInput): ScheduledTask {
    const now = Date.now()
    const task: ScheduledTask = { id: ulid(now), ...normalizeScheduledTaskInput(input, now) }
    return store.putScheduledTask(task)
  },
  updateScheduledTask(id: string, patch: Partial<ScheduledTaskInput>): ScheduledTask {
    const current = store.getScheduledTask(id)
    if (current === undefined) throw new Error('定时任务不存在')
    const merged: ScheduledTaskInput = {
      name: patch.name ?? current.name,
      prompt: patch.prompt ?? current.prompt,
      workspaceId: patch.workspaceId ?? current.workspaceId,
      model: patch.model ?? current.model,
      modelProviderId: patch.modelProviderId ?? current.modelProviderId,
      schedule: patch.schedule ?? current.schedule,
      timezone: patch.timezone ?? current.timezone,
      repeatWindow: patch.repeatWindow ?? current.repeatWindow,
      enabled: patch.enabled ?? current.enabled
    }
    const next = normalizeScheduledTaskInput(merged, Date.now())
    return store.putScheduledTask({ ...current, ...next, id, createdAt: current.createdAt })
  },
  deleteScheduledTask(id: string): void {
    repo.deleteScheduledTask(id)
  },
  setScheduledTaskEnabled(id: string, enabled: boolean): ScheduledTask {
    const task = store.getScheduledTask(id)
    if (task === undefined) throw new Error('定时任务不存在')
    const now = Date.now()
    return store.putScheduledTask({ ...task, enabled, nextRunAt: enabled ? nextScheduledOccurrence(task.schedule, task.timezone, task.repeatWindow, now) : null, updatedAt: now })
  },
  listScheduledRuns(taskId?: string, limit = 100): ScheduledRun[] {
    migrateLegacyScheduledData()
    return repo.listScheduledRuns(taskId, limit)
  },
  getScheduledRun(id: string): ScheduledRun | undefined { migrateLegacyScheduledData(); return repo.getScheduledRun(id) },
  /** 「这个会话是不是某条定时任务跑出来的」—— 链式排程算深度用。 */
  getScheduledRunBySession(sessionId: string): ScheduledRun | undefined {
    migrateLegacyScheduledData()
    return repo.getScheduledRunBySession(sessionId)
  },
  deleteScheduledRun(id: string): void { repo.deleteScheduledRun(id) },
  putScheduledRun(run: ScheduledRun): ScheduledRun {
    return repo.putScheduledRun(run)
  },

  // ── workspaces ──
  listConnectionProfiles(): ConnectionProfile[] { return repo.listConnectionProfiles() },
  getConnectionProfile(id: string): ConnectionProfile | undefined { return repo.getConnectionProfile(id) },
  putConnectionProfile(profile: ConnectionProfile): ConnectionProfile { return repo.putConnectionProfile(profile) },
  removeConnectionProfile(id: string): void { repo.removeConnectionProfile(id) },
  listWorkspaces(): Workspace[] {
    return repo.listWorkspaces()
  },
  getWorkspace(id: string): Workspace | undefined {
    return repo.getWorkspace(id)
  },
  putWorkspace(w: Workspace): Workspace {
    return repo.putWorkspace(w)
  },
  removeWorkspace(id: string): void {
    // 两条删除要么都生效要么都不生效:只删了工作区却留下内层 Tab 记录的话,
    // 下次建一个同 id 的工作区会莫名带着上一个的 Tab
    repo.tx(() => {
      repo.removeWorkspace(id)
      repo.removeKv(innerTabKey(id))
    })
  },

  // ── 上游供应商 / 模型别名 ──
  /** 按 priority 升序 —— 故障切换按这个顺序挑下一个候选(方案 §5.3) */
  listProviders(): UpstreamProvider[] {
    return repo.listProviders()
  },
  /** 供应商是否由 `providers.json` 管理(见 `db/provider-file.ts`)。 */
  usesProviderFile(): boolean {
    return repo.providerFileStore() !== null
  },
  putProvider(p: UpstreamProvider): UpstreamProvider {
    return repo.putProvider(p)
  },
  /**
   * 别名跟着走。留下一条指向已删 provider 的别名,表现是
   * 「模型还在下拉框里,选了却报『没有已启用的供应商』」—— 症状离这里很远。
   *
   * 级联现在由 `model_aliases` 上的外键做(`db/schema.ts`),不再是这里的一个循环:
   * 数据库保证的不变式不需要每个写入点都记得。
   *
   * ★ 账号行**没有**外键可用(`provider_accounts` 故意不建外键:它还要连带删
   * `credentials` 里的密文,而那件事外键管不着)。所以这里显式收一次 ——
   * 漏掉的表现是删掉供应商后库里留着一串再也读不到的 token。
   */
  removeProvider(id: string): void {
    repo.tx(() => {
      providerAccounts.removeProviderAccountsFor(id)
      repo.removeProvider(id)
    })
  },
  /**
   * ★ 别名的主键是 `(providerId, alias)`,不是 alias。
   * 同一个 alias 由多个 provider 提供**正是别名表存在的理由**(方案 §5.2:
   * 没有它就谈不上「切到下一个」)。用 alias 当主键,故障切换就只剩一个候选。
   */
  listAliases(): ModelAlias[] {
    return repo.listAliases()
  },
  putAlias(a: ModelAlias): ModelAlias {
    return repo.putAlias(a)
  },
  removeAlias(providerId: string, alias: string): void {
    repo.removeAlias(providerId, alias)
  },

  // ── 供应商账号(OAuth 多账号,schema 第 24 条) ──
  /**
   * ★ 这一组是**纯转发**,和上面全部访问器同一个理由:全应用没有第二个地方摸持久化。
   * 读写规则、连带删密文那条不变式都在 `db/provider-accounts.ts` 的文件头。
   *
   * ★ 这里出去的是**元数据**,不带登录态摘要(邮箱 / 是否过期)——
   * 那要解密凭证才知道,由 `ipc/provider-accounts.ts` 在下发前合成。
   */
  listProviderAccounts(providerId?: string): providerAccounts.ProviderAccountRow[] {
    return providerAccounts.listProviderAccounts(providerId)
  },
  getProviderAccount(id: string): providerAccounts.ProviderAccountRow | undefined {
    return providerAccounts.getProviderAccount(id)
  },
  putProviderAccount(account: providerAccounts.ProviderAccountRow): providerAccounts.ProviderAccountRow {
    return providerAccounts.putProviderAccount(account)
  },
  /** 连带删密文 —— 见 `db/provider-accounts.ts` 的不变式 */
  removeProviderAccount(id: string): void {
    providerAccounts.removeProviderAccount(id)
  },
  nextProviderAccountOrder(providerId: string): number {
    return providerAccounts.nextProviderAccountOrder(providerId)
  },
  setProviderAccountOrder(providerId: string, accountIds: readonly string[]): void {
    providerAccounts.setProviderAccountOrder(providerId, accountIds)
  },
  setProviderAccountEnabled(id: string, enabled: boolean): void {
    providerAccounts.setProviderAccountEnabled(id, enabled)
  },
  /** 见 `db/provider-accounts.ts`:凭证变化时同步那个反范式的位,写入点只有 announce */
  setProviderAccountNeedsReauth(id: string, needsReauth: boolean): void {
    providerAccounts.setProviderAccountNeedsReauth(id, needsReauth)
  },
  setProviderAccountLabel(id: string, label: string): void {
    providerAccounts.setProviderAccountLabel(id, label)
  },
  setProviderAccountLimit(id: string, limit: ProviderAccountLimit | null): void {
    providerAccounts.setProviderAccountLimit(id, limit)
  },
  setProviderAccountQuota(id: string, quota: ProviderQuotaSnapshot): void {
    providerAccounts.setProviderAccountQuota(id, quota)
  },
  setCurrentProviderAccount(providerId: string, accountId: string): void {
    providerAccounts.setCurrentProviderAccount(providerId, accountId)
  },
  currentProviderAccount(providerId: string): providerAccounts.ProviderAccountRow | undefined {
    return providerAccounts.currentProviderAccount(providerId)
  },

  // ── usage ledger ──
  recordUsageAttempt(record: UsageAttemptRecord): void {
    repo.recordUsageAttempt(record)
  },
  updateUsageToolsForRun(runId: string, toolCalls: number, toolErrors: number): boolean {
    return repo.updateUsageToolsForRun(runId, toolCalls, toolErrors)
  },
  getUsageSummary(window: UsageWindow): UsageSummary {
    return repo.getUsageSummary(window)
  },
  getUsageRequestLogs(query: UsageRequestLogsQuery): UsageRequestLogsPage {
    return repo.getUsageRequestLogs(query)
  },
  getSessionUsageAttempts(sessionId: string): UsageAttemptRecord[] {
    return repo.getSessionUsageAttempts(sessionId)
  },
  getUsageProviderStats(window: UsageWindow): UsageDimensionStat[] {
    return repo.getUsageProviderStats(window)
  },
  getUsageModelStats(window: UsageWindow): UsageDimensionStat[] {
    return repo.getUsageModelStats(window)
  },
  /**
   * 把新的 usage 行汇总进 `usage_daily`。幂等,由后台定时器和两条概览 IPC 共同调用。
   */
  refreshUsageRollup(): { days: number; rows: number } {
    return usageRollup.refreshUsageRollup()
  },
  getUsageDailySeries(window: UsageWindow): UsageDailyBucket[] {
    return usageRollup.getUsageDailySeries(window)
  },
  getUsageActivityStats(): UsageActivityStats {
    return usageRollup.getUsageActivityStats()
  },

  // ── user model catalogue ──
  // Provider discovery intentionally never calls these methods. A row enters
  // this collection only through the explicit model-catalog IPC actions.
  listUserModelCatalog(): ModelCatalogDefinition[] {
    return repo.listUserModelCatalog()
  },
  putUserModelCatalog(model: ModelCatalogDefinition): ModelCatalogDefinition {
    return repo.putUserModelCatalog(model)
  },
  removeUserModelCatalog(id: string): void {
    repo.removeUserModelCatalog(id)
  },

  // ── synced pricing (models.dev) ──
  listSyncedPricing(): ModelPricing[] {
    return repo.listSyncedPricing()
  },
  replaceSyncedPricing(rows: readonly ModelPricing[]): void {
    repo.replaceSyncedPricing(rows)
  },
  clearSyncedPricing(): void {
    repo.clearSyncedPricing()
  },

  // ── MCP 服务器(设置 › 连接 › MCP) ──
  /**
   * 顺序按 id,没有语义 —— 但要稳定。理由在 `repo.listMcpServers`。
   *
   * ★ 这里出去的是**配置**,不带运行时状态。状态在 `McpManager` 手里
   * (`main/mcp/manager.ts`),两者由 `ipc/mcp.ts` 合成 `McpServerStatus` 再下发。
   * 让这一层去问 manager 会把「持久化」和「进程内运行时」拧在一起,
   * 而 manager 的生命周期比数据库短得多。
   */
  listMcpServers(): McpServerConfig[] {
    return repo.listMcpServers()
  },
  getMcpServer(id: string): McpServerConfig | undefined {
    return repo.getMcpServer(id)
  },
  putMcpServer(c: McpServerConfig): McpServerConfig {
    return repo.putMcpServer(c)
  },
  /** 配置连密钥一起删,在一个事务里 —— 理由在 `repo.removeMcpServer` */
  removeMcpServer(id: string): void {
    repo.removeMcpServer(id)
  },

  // ── 搜索服务(设置 › 连接 › 搜索服务) ──
  /** ★ 返回目录表里的八家**全部**,不只是库里存过的那几行(见 repo) */
  listSearchProviders(): SearchProviderConfig[] {
    return repo.listSearchProviders()
  },
  putSearchProvider(c: SearchProviderConfig): SearchProviderConfig {
    return repo.putSearchProvider(c)
  },
  /** 拖拽排序:整批写、一个事务。半新半旧的 priority 会让「先试哪家」不确定 */
  putSearchProviders(list: readonly SearchProviderConfig[]): void {
    repo.putSearchProviders(list)
  },
  clearSearchCredential(id: SearchProviderId): void {
    repo.clearSearchCredential(id)
  },

  // ── kv(窗口/Tab 布局等易失 UI 状态) ──
  getKv<T>(key: string, fallback: T): T {
    return repo.getKv(key, fallback)
  },
  setKv(key: string, value: unknown): void {
    repo.setKv(key, value)
  },

  // ── Skill 的全局开关 ──
  /**
   * ★ 存的是**关掉的那些**,不是打开的那些。
   *
   * 存「打开的」的话,用户新装一条 Skill 之后它默认不在列表里 ——
   * 表现是「装了但没反应」,而界面上那个开关看起来是开着的。
   * 存「关掉的」则相反:没被点过的一律有效,这也和
   * `SkillRegistry.resolve()` 里「空清单 = 全都要」是同一个取向。
   */
  getDisabledSkillIds(): string[] {
    const raw = repo.getKv<unknown>(DISABLED_SKILLS_KEY, [])
    return Array.isArray(raw) ? raw.filter((x): x is string => typeof x === 'string') : []
  },
  setSkillGlobalEnabled(skillId: string, enabled: boolean): void {
    const now = new Set(store.getDisabledSkillIds())
    if (enabled) now.delete(skillId)
    else now.add(skillId)
    repo.setKv(DISABLED_SKILLS_KEY, [...now])
  },

  // ── 命令 / 子代理的开关（同样存「被关掉的那些」，见 DISABLED_COMMANDS_KEY） ──
  getDisabledCommandNames(): string[] {
    return readNameList(DISABLED_COMMANDS_KEY)
  },
  setCommandEnabled(name: string, enabled: boolean): void {
    repo.setKv(DISABLED_COMMANDS_KEY, toggleName(store.getDisabledCommandNames(), name, enabled))
  },
  getDisabledAgentNames(): string[] {
    return readNameList(DISABLED_AGENTS_KEY)
  },
  setAgentEnabled(name: string, enabled: boolean): void {
    repo.setKv(DISABLED_AGENTS_KEY, toggleName(store.getDisabledAgentNames(), name, enabled))
  },
  recordSkillTrigger(skillId: string, workspaceId?: string): void {
    const raw = repo.getKv<Record<string, SkillUsageStat>>(SKILL_STATS_KEY, {})
    const current = raw && typeof raw === 'object' ? raw : {}
    const prev = current[skillId] ?? { count: 0, lastTriggeredAt: 0, workspaces: {} }
    const workspaces = { ...(prev.workspaces ?? {}) }
    const times = { ...(prev.lastTriggeredAtByWorkspace ?? {}) }
    const now = Date.now()
    if (workspaceId !== undefined && workspaceId !== '') { workspaces[workspaceId] = (workspaces[workspaceId] ?? 0) + 1; times[workspaceId] = now }
    current[skillId] = { count: prev.count + 1, lastTriggeredAt: now, workspaces, lastTriggeredAtByWorkspace: times }
    repo.setKv(SKILL_STATS_KEY, current)
  },
  getSkillStats(workspaceId?: string): Record<string, SkillUsageStat> {
    const raw = repo.getKv<Record<string, SkillUsageStat>>(SKILL_STATS_KEY, {})
    if (workspaceId === undefined) return raw ?? {}
    return Object.fromEntries(Object.entries(raw ?? {}).map(([id, stat]) => [id, {
      ...stat,
      count: stat.workspaces?.[workspaceId] ?? 0,
      lastTriggeredAt: stat.lastTriggeredAtByWorkspace?.[workspaceId] ?? 0
    }]))
  },

  // ── 会话转录(步骤 6 迁到 messages 表) ──
  getHistory(sessionId: string): readonly AgentMessage[] {
    return repo.getHistory(sessionId)
  },
  /** 只取最后一条助手消息 —— 子代理交差正文,不必解析整段子转录 */
  lastAssistantMessage(sessionId: string): AgentMessage | undefined {
    return repo.lastAssistantMessage(sessionId)
  },
  /** 输入框补全:用户亲手发的最近几条 / 最后一条给人看的回复 / 本工作区的历史指令 */
  recentSessionPrompts(sessionId: string, limit: number): AgentMessage[] {
    return repo.recentSessionPrompts(sessionId, limit)
  },
  lastVisibleAssistantMessage(sessionId: string): AgentMessage | undefined {
    return repo.lastVisibleAssistantMessage(sessionId)
  },
  recentWorkspacePrompts(workspaceId: string): string[] {
    return repo.recentWorkspacePrompts(workspaceId)
  },
  /** 最后一条带目标标记的助手消息 —— 目标重载恢复只看它,不解析整段转录 */
  lastGoalStatusMessage(sessionId: string): AgentMessage | undefined {
    return repo.lastGoalStatusMessage(sessionId)
  },
  /** 一条消息仍是读出来时那一份时才换 parts(见 `repo.replaceMessagePartsIfUnchanged`) */
  replaceMessagePartsIfUnchanged(sessionId: string, messageId: string, expectedParts: string, parts: AgentMessage['parts']): boolean {
    return repo.replaceMessagePartsIfUnchanged(sessionId, messageId, expectedParts, parts)
  },
  setHistory(sessionId: string, messages: readonly AgentMessage[]): void {
    const existing = repo.getSession(sessionId)
    if (existing === undefined) {
      // 兼容旧的 renderer：它可能在真正发送前就生成了 sessionId。
      repo.createSession({ id: sessionId, workspaceId: '', rootPathAtCreation: '' })
    }
    repo.replaceHistory(sessionId, messages)
  },

  // ── 会话实体 ──
  getInnerTabs(workspaceId: string): InnerTabState {
    const state = repo.getKv<InnerTabState>(innerTabKey(workspaceId), EMPTY_INNER)
    return { ...state, tabs: state.tabs.map((tab) => {
      // sessionId 为 null = 还没发过消息的草稿 Tab,库里没有它,标题就用 Tab 自己的
      const session = tab.kind === 'chat' && tab.ref.sessionId !== null ? repo.getSession(tab.ref.sessionId) : undefined
      const title = session?.workspaceId === workspaceId ? session.title : undefined
      return title === undefined || title === tab.title ? tab : { ...tab, title }
    }) }
  },
  createSession(input: SessionCreateInput): Session {
    return repo.createSession(input)
  },
  ensureSession(input: SessionCreateInput): Session {
    return repo.ensureSession(input)
  },
  putSession(session: Session): Session {
    return repo.putSession(session)
  },
  getSession(sessionId: string): Session | undefined {
    return repo.getSession(sessionId)
  },
  getSessionDetail(sessionId: string): SessionDetail | undefined {
    return repo.getSessionDetail(sessionId)
  },
  /** 会话元数据 + 消息条数,不读任何消息正文 */
  getSessionSummary(sessionId: string): { session: Session; messageCount: number } | undefined {
    const session = repo.getSession(sessionId)
    return session === undefined ? undefined : { session, messageCount: repo.countMessages(sessionId) }
  },
  /** 转录的一页(见 `SessionPage`)。`messageRuns` 只覆盖本页的消息 */
  getSessionPage(sessionId: string, limit: number, beforeMessageId?: string): SessionPage | undefined {
    const session = repo.getSession(sessionId)
    if (session === undefined) return undefined
    const { messages, hasMore } = repo.getHistoryPage(sessionId, limit, beforeMessageId)
    const messageRuns = repo.messageRunsOf(sessionId, messages.map((message) => message.id))
    return {
      session,
      messages,
      hasMore,
      messageRuns,
      runUsage: repo.runUsageOf(sessionId),
      runModel: repo.runModelOf(sessionId)
    }
  },
  getSessionRunUsage(sessionId: string): ReturnType<typeof repo.runUsageOf> {
    return repo.runUsageOf(sessionId)
  },
  replaceHistory(sessionId: string, messages: readonly AgentMessage[]): void {
    repo.replaceHistory(sessionId, messages)
  },
  sessionAttachmentPaths(sessionId: string): string[] {
    return repo.sessionAttachmentPaths(sessionId)
  },
  getAttachmentRowByOwnerAndFileName(ownerId: string, fileName: string): repo.AttachmentRow | undefined {
    return repo.findAttachmentByOwnerAndFileName(ownerId, fileName)
  },
  listSessions(workspaceId: string, archived?: boolean): SessionListItem[] {
    return repo.listSessions(workspaceId, archived)
  },
  renameSession(sessionId: string, title: string): void {
    repo.renameSession(sessionId, title)
  },
  setSessionArchived(sessionId: string, archived: boolean): void {
    repo.setSessionArchived(sessionId, archived)
  },
  setSessionFavorited(sessionId: string, favorited: boolean): void {
    repo.setSessionFavorited(sessionId, favorited)
  },
  sessionSubtreeIds(sessionId: string): string[] {
    return repo.sessionSubtreeIds(sessionId)
  },
  deleteSession(sessionId: string): string[] {
    // 未发出的输入、以及派生出来的子代理转录,都由 `repo.deleteSession` 一起收 ——
    // 它是整棵子树遍历的那一层,级联必须和遍历在同一处(见那边的注释)。
    return repo.deleteSession(sessionId)
  },
  searchSessions(q: string, workspaceId?: string, limit = 50): SearchHit[] {
    return repo.searchAll(q, workspaceId, limit)
  },
  searchSessionHistory(sessionId: string, q: string, limit = 5): ContextSearchHit[] {
    return repo.searchSessionHistory(sessionId, q, limit)
  },
  commitMessage(sessionId: string, message: AgentMessage, runId?: string): void {
    repo.commitMessage(sessionId, message, runId)
  },
  /**
   * 给定的 messageId 里哪些**已经提交过**(消费回执,schema 第 34 条)。
   *
   * ★ 纯转发 —— SQL 仍然只在 `db/repo.ts`(见本文件头:全应用没有第二个地方
   *   摸持久化)。回执的写入是同层 `commitMessage` 顺手做掉的,不需要单独入口。
   *
   * ★ 这**不是**网络授权 API:调用者仍须自己验证会话归属(谁的 run 在服务哪条
   *   会话),这一层只回答「库里记没记过」。
   */
  committedInputIds(sessionId: string, ids: readonly string[]): string[] {
    return repo.committedInputIds(sessionId, ids)
  },
  setRunRecord(id: string, sessionId: string, status: string, startedAt: number, endedAt?: number): void {
    repo.setRunRecord(id, sessionId, status, startedAt, endedAt)
  },

  // ── 改动快照(审查卡 / 撤销·恢复,schema 第 23 条) ──
  saveFileChangeSet(input: {
    rootRunId: string
    sourceRunId: string
    sessionId: string
    workspaceId: string
    changes: readonly repo.FileChangeInput[]
    at: number
  }): void {
    repo.saveFileChangeSet(input)
  },
  getFileChangeSet(runId: string): ReviewChangeSet | undefined {
    return repo.getFileChangeSet(runId)
  },
  getFileSnapshotDiff(runId: string, path: string): ReviewFileDiff | undefined {
    return repo.getFileSnapshotDiff(runId, path)
  },
  listFileSnapshots(runId: string): repo.FileSnapshotRecord[] {
    return repo.listFileSnapshots(runId)
  },
  getFileChangeSetState(runId: string): ChangeSetState | undefined {
    return repo.getFileChangeSetState(runId)
  },
  setChangeSetState(runId: string, state: ChangeSetState, at: number): void {
    repo.setChangeSetState(runId, state, at)
  },

  // ── 外部来源导入(schema 第 16 条) ──
  /**
   * ★ 这一整组是**纯转发**,和上面所有访问器同一个理由:全应用没有第二个地方
   * 摸持久化。导入服务、同步器、IPC 三处都只认这一层 —— 其中同步器会在
   * 后台线程式的定时器里跑,让它直接 import `db/repo` 就等于给「把 Db 挪进
   * utilityProcess」那天多留一个必须一起改的调用点。
   */
  listImportSources(): repo.ImportSourceRow[] {
    return repo.listImportSources()
  },
  getImportSource(sourceId: string): repo.ImportSourceRow | undefined {
    return repo.getImportSource(sourceId)
  },
  putImportSource(source: repo.ImportSourceRow): repo.ImportSourceRow {
    return repo.putImportSource(source)
  },
  listImportScanCache(sourceId: string): Map<string, repo.ImportScanCacheRow> {
    return repo.listImportScanCache(sourceId)
  },
  putImportScanCache(rows: readonly repo.ImportScanCacheRow[]): void {
    repo.putImportScanCache(rows)
  },
  pruneImportScanCache(sourceId: string, scannedAt: number): number {
    return repo.pruneImportScanCache(sourceId, scannedAt)
  },
  getImportMapping(
    sourceId: string,
    scopeKey: string,
    entityKind: repo.ImportEntityKind,
    sourceItemId: string
  ): repo.ImportMappingRow | undefined {
    return repo.getImportMapping(sourceId, scopeKey, entityKind, sourceItemId)
  },
  listImportMappings(sourceId: string, entityKind?: repo.ImportEntityKind): repo.ImportMappingRow[] {
    return repo.listImportMappings(sourceId, entityKind)
  },
  findImportMappingsByTarget(targetId: string, entityKind: repo.ImportEntityKind): repo.ImportMappingRow[] {
    return repo.findImportMappingsByTarget(targetId, entityKind)
  },
  putImportMapping(mapping: repo.ImportMappingRow): void {
    repo.putImportMapping(mapping)
  },
  /**
   * 「这条会话从此不再跟随源」。返回被改动的行数 —— 调用点据此决定要不要
   * 发一次 `imports:changed`(0 行时发等于每一轮 run 都刷一次设置页)。
   */
  detachImportedSession(sessionId: string, now = Date.now()): number {
    return repo.markImportMappingsByTarget(sessionId, 'session', 'detached', now)
  },
  suppressImportedTarget(targetId: string, entityKind: repo.ImportEntityKind, now = Date.now()): void {
    repo.suppressImportMappingsByTarget(targetId, entityKind, now)
  },
  deleteImportMessageMappings(sessionIds: readonly string[]): void {
    repo.deleteImportMessageMappings(sessionIds)
  },
  createImportBatch(batch: repo.ImportBatchRow): void {
    repo.createImportBatch(batch)
  },
  updateImportBatch(id: string, phase: string, counts: Record<string, number>, endedAt?: number): void {
    repo.updateImportBatch(id, phase, counts, endedAt)
  },
  markInterruptedImportBatches(): number {
    return repo.markInterruptedImportBatches()
  },
  appendImportBatchItem(item: repo.ImportBatchItemRow): void {
    repo.appendImportBatchItem(item)
  },
  listImportBatches(offset: number, limit: number): { rows: repo.ImportBatchRow[]; total: number } {
    return repo.listImportBatches(offset, limit)
  },
  listImportBatchItems(batchId: string, offset: number, limit: number): { rows: repo.ImportBatchItemRow[]; total: number } {
    return repo.listImportBatchItems(batchId, offset, limit)
  },
  sessionExists(id: string): boolean {
    return repo.sessionExists(id)
  },
  /** 导入服务要把「写映射」和「写目标实体」放进同一个事务。 */
  tx<T>(fn: () => T): T {
    return repo.tx(fn)
  },

  /**
   * 测试专用。转录是**跨 run 累积**的,而同一个文件里的用例通常共用一个
   * sessionId —— 不清的话第二个用例会看见第一个用例的对话,
   * 表现是断言里凭空多出几条消息。
   */
  clearHistoriesForTest(): void {
    repo.clearSessionDataForTest()
  }
}

// ─── kv 键名收在这里,不散落 ───

export const outerTabKey = (windowKind: string): string => `tabs.outer.${windowKind}`
export const innerTabKey = (workspaceId: string): string => `tabs.inner.${workspaceId}`

/**
 * 未发出的输入(草稿 + 插入队列)。
 *
 * ★ 复用 `kv` 而不是新建表:查询形状是**整行读、整行写**,每会话一行,
 * 字段还在演进 —— 与 `schema.ts` 给 kv 写的判据逐条对上,拆真列换不到
 * 任何查询能力,却要为 `QueuedInput` 的每次增删字段写一条迁移。
 */
export const sessionInputKey = (sessionId: string): string => `session.input.${sessionId}`

export const EMPTY_OUTER: WindowTabState = { outer: [], activeOuterId: null }
export const EMPTY_INNER: InnerTabState = { tabs: [], activeTabId: null }
