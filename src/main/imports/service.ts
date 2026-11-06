/**
 * 导入服务 —— 扫描、预览快照、逐项提交、作业与历史。
 *
 * ## 三条把这个文件定形的约束
 *
 * 1. **预览是不可变快照,提交只认快照里的 id。** 渲染层递进来的永远只有
 *    `previewId + itemIds`,不能递路径、不能递内容。否则这条频道就是一个
 *    任意文件读取入口(方案 §9 反对的正是这个)。
 * 2. **按单项提交,不合并成一个长事务。** 一百个会话一个事务,意味着第 99 个
 *    失败会把前 98 个一起丢掉 —— 而那 98 个是真的成功了。
 * 3. **永不覆盖用户改过的东西。** 目标指纹对不上基线就记 `conflict`,
 *    绝不写入。这一条没有开关。
 *
 * ## 本地 id 为什么是**推导**出来的,而不是随机 mint 再存一张表
 *
 * 计划里写的是「持久化源 UUID 到本地 ID 的映射」。这里改成了
 * `id = hash(转换器版本, sourceId, 源会话 uuid[, 源消息 uuid])` ——
 * 纯函数,同样保证「导入两次仍一份」和「重试不重复」,但:
 *
 * - **消息级不用落 10000 行。** 验收标准里那条「100 会话 × 100 消息」按
 *   一条消息一行算就是上万行映射,它们唯一的内容是在复述一个纯函数。
 * - **崩溃窗口消失了。** 「先 mint id、再写实体、再写映射」中间崩溃会留下
 *   一个没有映射的孤儿会话,下一轮扫描会再导一份。推导法没有这个中间态:
 *   重跑算出同一个 id,直接落在同一行上。
 *
 * 会话级映射仍然**照落**(`import_mappings` 的 `entity_kind = 'session'`)——
 * 它承载的是纯函数给不出的东西:同步状态、指纹基线、脱离标记。
 */
import { createHash } from 'node:crypto'
import { basename, join, resolve } from 'node:path'
import { realpath, stat } from 'node:fs/promises'
import type { AgentMessage, ContentPart } from '../../shared/agent/message'
import type {
  ImportApplyRequest,
  ImportBatchItem,
  ImportBatchItemsPage,
  ImportCategory,
  ImportCounts,
  ImportDiagnostic,
  ImportHistoryPage,
  ImportJobStatus,
  ImportPreview,
  ImportPreviewItem,
  ImportPreviewPage,
  ImportPreviewQuery,
  ImportProjectCandidate,
  ImportResultCode,
  ImportSourceKind,
  ImportSourceState,
  ImportSyncPatch,
  ImportTargetKind
} from '../../shared/domain/import'
import { EMPTY_IMPORT_COUNTS, IMPORT_CATEGORIES, IMPORT_LIMITS } from '../../shared/domain/import'
import type { McpServerConfig } from '../../shared/domain/mcp'
import type { ModelAlias } from '../../shared/domain/provider'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../shared/domain/workspace'
import { isLocalEnvironment } from '../../shared/domain/environment'
import type { HookDefinition } from '../../shared/domain/hook'
import { normalizeLocalSettings } from '../../shared/domain/local-settings'
import { prefixedId } from '../../shared/util/id'
import { databaseDirectory } from '../db'
import type { ImportEntityKind, ImportMappingRow, ImportSourceRow } from '../db/repo'
import { store } from '../state/store'
import { canonicalRoot, findLocalWorkspaceByRoot } from '../state/workspace-root'
import type { ImportScanCacheRow } from '../db/repo'
import { getHost } from '../runtime'
import { globalSettingsPath, localSettingsPath } from '../kernel/local-settings'
import { hookListFrom } from '../kernel/hook/load'
import {
  CLAUDE_LAYOUT,
  detectSource,
  listAssetNames,
  listTranscripts,
  locateGlobalConfig,
  mapMcpServer,
  readGlobalConfig,
  readInstructions,
  readProjectMcp,
  readTranscriptHead,
  readTranscriptLines,
  resolveWithinRoot,
  type MappedMcpServer,
  type TranscriptFile
} from './claude-code'
import {
  cleanupStaging,
  currentFingerprint,
  fingerprint,
  publishSkillPackage,
  publishTextFile,
  readTextBounded,
  stagingDirFor
} from './assets'
import { managedInstructionsPath } from './managed-paths'
import { parseTranscript, TRANSFORMER_VERSION, type ImportedMessage, type ParsedTranscript } from './transcript'
import {
  detectCodexSource,
  listCodexHooks,
  listCodexProviders,
  listCodexSessions,
  listCodexSkills,
  listCodexProjectRoots,
  readCodexSessionIndex,
  type CodexHookEntry,
  type CodexProviderConfig,
  type CodexSessionFile,
  type CodexSkillEntry
} from './codex'
import { parseCodexTranscript } from './codex-transcript'
import { aliasSurface, mapCodexProvider, providerSurface } from './codex-provider'
import { mapCodexHook } from './codex-hooks'
import {
  detectOpencodeSource,
  listOpencodeAssets,
  listOpencodeMcp,
  listOpencodePlugins,
  listOpencodeProviders,
  listOpencodeSessions,
  opencodeConfigDir,
  readOpencodeAuthNames,
  readOpencodeConfig,
  readOpencodeSession
} from './opencode'
import { parseOpencodeSession } from './opencode-transcript'
import { mapOpencodeProvider, type OpencodeProviderConfig } from './opencode-provider'
import {
  detectOpencoworkSource,
  listOpencoworkAssets,
  listOpencoworkHooks,
  listOpencoworkMcp,
  listOpencoworkProviders,
  listOpencoworkSessions,
  listOpencoworkSkills,
  readOpencoworkSession
} from './opencowork'
import { parseOpencoworkSession } from './opencowork-transcript'
import { mapOpencoworkProvider, type OpencoworkProviderConfig } from './opencowork-provider'
import { mapOpencoworkHook, type OpencoworkHookEntry } from './opencowork-hooks'

const SOURCE_KIND = 'claude-code' as const

/*
  ★ `MANAGED_INSTRUCTIONS_DIR` / `managedInstructionsPath` 已经搬到
  `./managed-paths.ts`(纯路径 helper),这里**原样再导出**,对外兼容。

  搬走是为了断掉 `imports/service ⇄ runtime` 的值依赖环:runtime 只用它们拼一条
  路径,却因为它们在 service 里而把整条导入流水线拉进了自己的依赖图。读写两半
  共用同一个实现,分叉的表现是「导入成功了,但模型读不到」。
*/
export { MANAGED_INSTRUCTIONS_DIR, managedInstructionsPath } from './managed-paths'

// ═══════════════════════════════════════════════════════════════
// 模块状态
// ═══════════════════════════════════════════════════════════════

interface Snapshot {
  previewId: string
  sourceId: string
  configDir: string
  createdAt: number
  expiresAt: number
  items: ImportPreviewItem[]
  payloads: Map<string, ItemPayload>
  projects: ImportProjectCandidate[]
  diagnostics: ImportDiagnostic[]
}

type ItemPayload =
  | { kind: 'project'; projectKey: string; sourcePath: string }
  | { kind: 'chat'; file: TranscriptFile; projectKey: string; contentHash: string; title?: string; model?: string }
  | { kind: 'skill'; name: string; dir: string }
  | { kind: 'agent'; name: string; file: string }
  | { kind: 'command'; name: string; file: string }
  | { kind: 'instructions'; scope: 'global' | 'project'; text: string; projectKey?: string }
  | { kind: 'mcp'; server: MappedMcpServer; scope: 'global' | 'project'; projectKey?: string }
  | { kind: 'provider'; provider: CodexProviderConfig; alias?: ModelAlias }
  | { kind: 'hook'; hook: CodexHookEntry }
  | { kind: 'codex-chat'; file: CodexSessionFile; contentHash: string; title?: string; model?: string; modelProvider?: string; archived: boolean }
  | { kind: 'codex-skill'; skill: CodexSkillEntry }
  | { kind: 'opencode-chat'; sessionId: string; contentHash: string; title?: string; model?: string; modelProvider?: string }
  | { kind: 'opencode-provider'; provider: OpencodeProviderConfig; alias?: ModelAlias }
  | { kind: 'opencowork-chat'; sessionId: string; contentHash: string; title?: string; model?: string; modelProvider?: string }
  | { kind: 'opencowork-provider'; provider: OpencoworkProviderConfig; alias?: ModelAlias }
  | { kind: 'opencowork-hook'; hook: OpencoworkHookEntry }

interface JobState {
  status: ImportJobStatus
  batchId: string
  cancelled: boolean
  /** 防双击重入:同一个 requestId 返回同一个 job。 */
  requestId: string
}

const snapshots = new Map<string, Snapshot>()
/** ★ 每个来源**单飞**。手动导入与自动同步共用这张表,天然防止两者打架。 */
const jobsBySource = new Map<string, JobState>()
const jobsById = new Map<string, JobState>()

/**
 * 「这个来源里有多少项目、多少会话」——**派生值,不是状态**。
 *
 * ★★ 它必须缓存,否则界面上恒显示 0。踩过的顺序是这样的:
 * 页面挂载 → `detect()` 回来带着真计数 → 界面显示 13/205 →
 * 但 `detect()` 自己也 `announce()` 了一次 → 200ms 后 `imports:changed` 到达 →
 * 页面改调 `getState()` → 那条路径没有 detection 参数 → **计数被 0 覆盖**。
 * 症状是「明明有会话却扫不到」,而其实扫到了,只是被自己的事件清掉了。
 *
 * 为什么不进 `import_sources` 表:它是一次目录遍历的结果,和授权范围、同步状态
 * 这些**用户的决定**不是一回事。落表就得为每次扫描写一次库,还要多一条迁移。
 * 为什么不在 `getState` 里现算:那是一次上千文件的遍历,而 `getState` 会被
 * 导入期间每 200ms 一次的事件打到。
 */
const sourceCounts = new Map<string, { projectCount: number; sessionCount: number }>()

let changeSeq = 0
let notify: ((payload: { sourceId: string; jobId?: string; seq: number }) => void) | null = null
let pendingNotify: NodeJS.Timeout | null = null
let pendingSourceId = ''
let pendingJobId: string | undefined

export function setImportChangeListener(
  fn: (payload: { sourceId: string; jobId?: string; seq: number }) => void
): void {
  notify = fn
}

/**
 * 状态变化通知。★ **限频 ~200ms** —— 一次一百个会话的导入会产生上百次变化,
 * 每次都推一遍就是让设置页在导入期间每秒重渲染几十次。
 */
function announce(sourceId: string, jobId?: string): void {
  pendingSourceId = sourceId
  pendingJobId = jobId
  if (pendingNotify !== null) return
  pendingNotify = setTimeout(() => {
    pendingNotify = null
    changeSeq += 1
    notify?.({
      sourceId: pendingSourceId,
      ...(pendingJobId === undefined ? {} : { jobId: pendingJobId }),
      seq: changeSeq
    })
  }, 200)
  pendingNotify.unref?.()
}

/**
 * 启动时调一次。★ 把上次没跑完的批次标成 `interrupted`,而不是留在 `importing` ——
 * 留着的话界面上会永远显示一个「正在导入」的进度条,而那个 job 的进程早没了。
 */
export function initImports(): void {
  store.markInterruptedImportBatches()
}

export function shutdownImports(): void {
  if (pendingNotify !== null) clearTimeout(pendingNotify)
  pendingNotify = null
  for (const job of jobsById.values()) job.cancelled = true
}

// ═══════════════════════════════════════════════════════════════
// 来源状态
// ═══════════════════════════════════════════════════════════════

function stateOf(row: ImportSourceRow, detection?: {
  availability: ImportSourceState['detection']['availability']
  diagnostics: ImportDiagnostic[]
  projectCount?: number
  sessionCount?: number
}): ImportSourceState {
  const job = jobsBySource.get(row.sourceId)
  // ★ 没传 detection 时回落到缓存,**不是回落到 0** —— 见 `sourceCounts` 的注释。
  const counted = sourceCounts.get(row.sourceId)
  return {
    detection: {
      sourceId: row.sourceId,
      kind: row.kind as ImportSourceState['detection']['kind'],
      availability: detection?.availability ?? (row.configDir === '' ? 'not-found' : 'detected'),
      configDir: row.configDir,
      origin: row.origin as ImportSourceState['detection']['origin'],
      projectCount: detection?.projectCount ?? counted?.projectCount ?? 0,
      sessionCount: detection?.sessionCount ?? counted?.sessionCount ?? 0,
      ...(row.lastCheckAt === undefined ? {} : { lastScanAt: row.lastCheckAt }),
      diagnostics: detection?.diagnostics ?? []
    },
    sync: {
      enabled: row.syncEnabled,
      status: row.status as ImportSourceState['sync']['status'],
      categories: row.categories.filter((c): c is ImportCategory =>
        (IMPORT_CATEGORIES as readonly string[]).includes(c)
      ),
      projectKeys: row.projectKeys,
      ...(row.lastCheckAt === undefined ? {} : { lastCheckAt: row.lastCheckAt }),
      ...(row.lastSyncAt === undefined ? {} : { lastSyncAt: row.lastSyncAt }),
      diagnostics: row.diagnostics as ImportDiagnostic[]
    },
    job: job?.status ?? null
  }
}

/** 来源没登记过时的空状态。★ 不建行 —— 用户还没授权任何东西。 */
function unregisteredState(availability: ImportSourceState['detection']['availability'], diagnostics: ImportDiagnostic[], kind: ImportSourceState['detection']['kind'] = SOURCE_KIND): ImportSourceState {
  return {
    detection: {
      sourceId: '',
      kind,
      availability,
      configDir: '',
      origin: 'auto',
      projectCount: 0,
      sessionCount: 0,
      diagnostics
    },
    sync: { enabled: false, status: 'off', categories: [], projectKeys: [], diagnostics: [] },
    job: null
  }
}

export async function detectImportSource(pickedDir?: string, kind: ImportSourceState['detection']['kind'] = SOURCE_KIND): Promise<ImportSourceState> {
  const detected = kind === 'codex' ? await detectCodexSource(pickedDir) : kind === 'opencode' ? await detectOpencodeSource(pickedDir) : kind === 'opencowork' ? await detectOpencoworkSource(pickedDir) : await detectSource(pickedDir)
  if (detected.availability !== 'detected') {
    return unregisteredState(detected.availability, detected.diagnostics, kind)
  }

  const now = Date.now()
  const existing = store.getImportSource(detected.sourceId)
  const row = store.putImportSource({
    sourceId: detected.sourceId,
    kind,
    configDir: detected.configDir,
    origin: detected.origin,
    syncEnabled: existing?.syncEnabled ?? false,
    // ★ 默认**不预选任何类别**。用户没点过全选就自动授权七类,等于替他做了决定。
    categories: existing?.categories ?? [],
    projectKeys: existing?.projectKeys ?? [],
    lastCheckAt: now,
    ...(existing?.lastSyncAt === undefined ? {} : { lastSyncAt: existing.lastSyncAt }),
    status: existing?.status ?? 'off',
    diagnostics: [],
    createdAt: existing?.createdAt ?? now,
    updatedAt: now
  })

  const counted = await countSource(detected.configDir, kind)
  sourceCounts.set(row.sourceId, counted)
  announce(row.sourceId)
  return stateOf(row, { availability: 'detected', diagnostics: [], ...counted })
}

async function countSource(configDir: string, kind: ImportSourceState['detection']['kind']): Promise<{ projectCount: number; sessionCount: number }> {
  if (kind === 'codex') {
    const files = await listCodexSessions(configDir)
    const projects = await listCodexProjectRoots(configDir)
    return { projectCount: projects.length, sessionCount: files.length }
  }
  if (kind === 'opencode') {
    const sessions = await listOpencodeSessions(configDir)
    const projects = new Set(sessions.map((s) => s.directory).filter((d) => d !== ''))
    return { projectCount: projects.size, sessionCount: sessions.length }
  }
  const { files } = await listTranscripts(configDir)
  return { projectCount: new Set(files.map((f) => f.encodedDir)).size, sessionCount: files.length }
}

export function getImportSourceState(sourceId: string): ImportSourceState {
  const row = store.getImportSource(sourceId)
  if (row === undefined) return unregisteredState('not-found', [{ code: 'source.not-found' }])
  return stateOf(row)
}

export function updateImportSync(sourceId: string, patch: ImportSyncPatch): ImportSourceState {
  const row = store.getImportSource(sourceId)
  if (row === undefined) throw new Error(`导入来源不存在: ${sourceId}`)
  const enabled = patch.enabled ?? row.syncEnabled
  const next = store.putImportSource({
    ...row,
    syncEnabled: enabled,
    categories: patch.categories ?? row.categories,
    projectKeys: patch.projectKeys ?? row.projectKeys,
    // ★ 关掉同步**不删任何已导入数据**,只是不再跟随源。
    status: enabled ? 'idle' : 'off',
    updatedAt: Date.now()
  })
  announce(sourceId)
  return stateOf(next)
}

// ═══════════════════════════════════════════════════════════════
// 扫描与预览
// ═══════════════════════════════════════════════════════════════

/** 同一份内容的稳定项 id。分类前缀让 `switch (payload.kind)` 和 id 不会对不上。 */
function itemId(category: ImportCategory, key: string): string {
  return `${category}:${key}`
}

/**
 * 推导本地实体 id。见文件头「本地 id 为什么是推导出来的」。
 * ★ 版本号进哈希:转换规则一改,旧的导入结果不会被当成同一条更新掉。
 */
function derivedId(prefix: string, ...parts: string[]): string {
  const digest = createHash('sha256')
    .update([`v${String(TRANSFORMER_VERSION)}`, ...parts].join(' '))
    .digest('hex')
  return `${prefix}${digest.slice(0, 24)}`
}

export async function buildPreview(sourceId: string, _requestId: string): Promise<ImportPreview> {
  const row = store.getImportSource(sourceId)
  if (row === undefined) throw new Error(`导入来源不存在: ${sourceId}`)
  if (row.configDir === '') throw new Error('来源目录不可用,请重新检测或手动选择')

  const scanned = await scanSource(row)
  const now = Date.now()
  const preview: Snapshot = {
    previewId: prefixedId('imp'),
    sourceId,
    configDir: row.configDir,
    createdAt: now,
    expiresAt: now + IMPORT_LIMITS.previewTtlMs,
    items: scanned.items,
    payloads: scanned.payloads,
    projects: scanned.projects,
    diagnostics: scanned.diagnostics
  }
  snapshots.set(preview.previewId, preview)
  sweepSnapshots(now)

  store.putImportSource({ ...row, lastCheckAt: now, updatedAt: now })

  return {
    previewId: preview.previewId,
    sourceId,
    sourceKind: row.kind as ImportSourceKind,
    createdAt: preview.createdAt,
    expiresAt: preview.expiresAt,
    counts: countPreview(scanned.items),
    projects: scanned.projects,
    diagnostics: scanned.diagnostics
  }
}

function sweepSnapshots(now: number): void {
  for (const [id, snapshot] of snapshots) {
    if (snapshot.expiresAt <= now) snapshots.delete(id)
  }
}

function countPreview(items: readonly ImportPreviewItem[]): ImportPreview['counts'] {
  const byCategory: ImportPreview['counts']['byCategory'] = {}
  const byStatus: ImportPreview['counts']['byStatus'] = {}
  for (const item of items) {
    byCategory[item.category] = (byCategory[item.category] ?? 0) + 1
    byStatus[item.status] = (byStatus[item.status] ?? 0) + 1
  }
  return { byCategory, byStatus, total: items.length }
}

export function previewItems(query: ImportPreviewQuery): ImportPreviewPage {
  const snapshot = snapshots.get(query.previewId)
  if (snapshot === undefined) throw new Error('预览已过期,请重新扫描')
  const q = query.q?.trim().toLowerCase() ?? ''
  const filtered = snapshot.items.filter((item) => {
    if (query.category !== undefined && item.category !== query.category) return false
    if (query.projectKey !== undefined && item.projectKey !== query.projectKey) return false
    if (q === '') return true
    return item.title.toLowerCase().includes(q) || item.sourcePath.toLowerCase().includes(q)
  })
  return {
    items: filtered.slice(query.offset, query.offset + query.limit),
    total: filtered.length,
    offset: query.offset
  }
}

interface ScanResult {
  items: ImportPreviewItem[]
  payloads: Map<string, ItemPayload>
  projects: ImportProjectCandidate[]
  diagnostics: ImportDiagnostic[]
}

/**
 * 一次完整扫描。
 *
 * ★ 会话那一段用「先比 size/mtime,变了才解析」—— 一个跑了半年的
 * Claude Code 目录有几百份转录,每次打开预览都全量解析一遍的话,
 * 那颗「扫描」按钮要转好几秒,而其中绝大多数文件根本没动过。
 */
/**
 * 一条聊天在**预览阶段**需要的全部元数据。
 *
 * ★ `contentHash` 为 `''` 表示这一趟只读了头部,没算过全文哈希。调用方
 * 必须自己判断要不要紧 —— 判据见 `readChatMeta`。
 */
interface ChatMeta {
  sessionId: string
  cwd: string
  title?: string
  model?: string
  modelProvider?: string
  /** partial 为真时是下界,不是精确值。 */
  messages: number
  contentHash: string
  startedAt?: number
  sourceUpdatedAt: number
  partial: boolean
  diagnostics: ImportDiagnostic[]
}

/** 两个文件型来源的 parser 形状一致,统一成这个签名喂给 `readChatMeta`。 */
type TranscriptParser = (lines: readonly string[]) => {
  sessionId: string
  cwd: string
  title?: string
  model?: string
  modelProvider?: string
  messages: readonly unknown[]
  startedAt?: number
  updatedAt?: number
  diagnostics: ImportDiagnostic[]
}

/**
 * 读一条聊天的元数据,**能不读全文就不读全文**。
 *
 * ★★ 这是整个预览的性能命门。三条判据叠在一起:
 *
 * 1. **缓存先行。** `change_token`(size:mtime)没变就整行复用,一个字节都不读。
 *    缓存挂在 `import_scan_cache` 而不是 `import_mappings` 上 —— 后者只有
 *    导入过的会话才有,实测 1063 个转录里仅 47 条,等于对 96% 的文件不生效。
 *
 * 2. **哈希按需算。** `statusOfChat` 里没有映射直接返回 new,`contentHash`
 *    压根不参与。所以只有**已经导入过**的会话才值得为了哈希读满全文。
 *
 * 3. **其余只读头部。** 实测 1063 份真实转录(2.1GB):全读全解析 5.19 秒,
 *    封顶 1MiB 是 1.51 秒。并发 8 路只降到 3.97 秒 —— 瓶颈是 JSON.parse
 *    的 CPU 和内存带宽,不是磁盘延迟,所以加并发解决不了这件事。
 *    「读到 4MiB 但跳过超大行」也试过:1.79 秒**更慢**,且 title 反而掉到
 *    21/24 —— 被跳掉的大行里就有带标题的那条。
 *
 * ★ 头部读**不允许**改变结论,只允许改变代价。所以解析不出消息时会回退到
 * 全文读(见函数末尾),否则「前 1MiB 是一条巨型工具输出」的文件会被误判成
 * 空会话而静默消失。
 *
 * 缓存未命中又确实需要哈希时会读两趟(头部 + 全文)。那是「没缓存且已导入」
 * 这个少见组合,实测 1063 份里只有 47 条有映射,不值得为它把代码写复杂。
 */
async function readChatMeta(input: {
  sourceId: string
  path: string
  changeToken: string
  fileUpdatedAt: number
  cache: ReadonlyMap<string, ImportScanCacheRow>
  parse: TranscriptParser
  /** 已知需要精确哈希(调用方已从路径查到映射)时直接置真,省一趟头部读。 */
  needsHash?: boolean
  /** 拿到 sessionId 之后才能判断要不要哈希 —— Codex 的会话 id 在文件内容里。 */
  hashNeededFor?: (sessionId: string) => boolean
}): Promise<ChatMeta> {
  const cached = input.cache.get(input.path)
  const parseAll = async (): Promise<ChatMeta> => {
    const read = await readTranscriptLines(input.path)
    const parsed = input.parse(read.lines)
    return {
      sessionId: parsed.sessionId,
      cwd: parsed.cwd,
      ...(parsed.title === undefined ? {} : { title: parsed.title }),
      ...(parsed.model === undefined ? {} : { model: parsed.model }),
      ...(parsed.modelProvider === undefined ? {} : { modelProvider: parsed.modelProvider }),
      messages: parsed.messages.length,
      contentHash: hashTranscript(parsed as Parameters<typeof hashTranscript>[0]),
      ...(parsed.startedAt === undefined ? {} : { startedAt: parsed.startedAt }),
      sourceUpdatedAt: parsed.updatedAt ?? input.fileUpdatedAt,
      partial: false,
      diagnostics: [...read.diagnostics, ...parsed.diagnostics]
    }
  }

  if (cached !== undefined && cached.changeToken === input.changeToken) {
    const stillNeedsHash = input.needsHash === true
      || (input.hashNeededFor?.(cached.sessionId) ?? false)
    // 缓存是头部读来的,而这次真要哈希 —— 只有这种情况下才回头读全文。
    if (!(stillNeedsHash && cached.contentHash === '')) {
      return {
        sessionId: cached.sessionId,
        cwd: cached.cwd,
        ...(cached.title === '' ? {} : { title: cached.title }),
        ...(cached.model === '' ? {} : { model: cached.model }),
        ...(cached.modelProvider === '' ? {} : { modelProvider: cached.modelProvider }),
        messages: cached.messages,
        contentHash: cached.contentHash,
        sourceUpdatedAt: cached.sourceUpdatedAt,
        partial: cached.partial,
        diagnostics: []
      }
    }
    return parseAll()
  }

  if (input.needsHash === true) return parseAll()

  // ── 头部快路径 ──
  const head = await readTranscriptHead(input.path, IMPORT_LIMITS.scanHeadBytes)
  const parsed = input.parse(head.lines)
  // 整个文件都在头部里 = 这趟其实是全读,哈希和条数都是准的。
  if (head.complete) {
    return {
      sessionId: parsed.sessionId,
      cwd: parsed.cwd,
      ...(parsed.title === undefined ? {} : { title: parsed.title }),
      ...(parsed.model === undefined ? {} : { model: parsed.model }),
      ...(parsed.modelProvider === undefined ? {} : { modelProvider: parsed.modelProvider }),
      messages: parsed.messages.length,
      contentHash: hashTranscript(parsed as Parameters<typeof hashTranscript>[0]),
      ...(parsed.startedAt === undefined ? {} : { startedAt: parsed.startedAt }),
      sourceUpdatedAt: parsed.updatedAt ?? input.fileUpdatedAt,
      partial: false,
      diagnostics: parsed.diagnostics
    }
  }
  if (input.hashNeededFor?.(parsed.sessionId) === true) return parseAll()

  /*
    ★ 头部里 0 条消息 **不等于** 空会话 —— 也可能是前 1MiB 全被一条巨型
    工具输出占满(实测语料里真有 115MB 的单文件)。而「空」在两个扫描循环里
    都是终局判决:Claude 那边 `continue` 直接把会话丢出预览,Codex 那边标成
    incompatible 且不默认勾选。两者都不报错,用户只会发现会话「少了」。

    所以不确定的时候读全文再下结论。有了这条回退,`scanHeadBytes` 就只是
    性能旋钮而不是正确性参数 —— 把它调到 1 字节,结果依然对,只是变慢。
  */
  if (parsed.messages.length === 0) return parseAll()

  return {
    sessionId: parsed.sessionId,
    cwd: parsed.cwd,
    ...(parsed.title === undefined ? {} : { title: parsed.title }),
    ...(parsed.model === undefined ? {} : { model: parsed.model }),
    ...(parsed.modelProvider === undefined ? {} : { modelProvider: parsed.modelProvider }),
    /*
      ★ 头部里的条数是**下界**。它只用来回答「这个会话是不是空的」——
      那个判断只关心 0 与非 0,不关心具体值。界面上的条数同样标成近似。
    */
    messages: parsed.messages.length,
    contentHash: '',
    ...(parsed.startedAt === undefined ? {} : { startedAt: parsed.startedAt }),
    sourceUpdatedAt: input.fileUpdatedAt,
    partial: true,
    diagnostics: parsed.diagnostics
  }
}

/** `ChatMeta` → 待写回的缓存行。 */
function scanCacheRow(
  sourceId: string,
  path: string,
  changeToken: string,
  meta: ChatMeta,
  scannedAt: number
): ImportScanCacheRow {
  return {
    sourceId,
    sourcePath: path,
    changeToken,
    sessionId: meta.sessionId,
    contentHash: meta.contentHash,
    cwd: meta.cwd,
    title: meta.title ?? '',
    model: meta.model ?? '',
    modelProvider: meta.modelProvider ?? '',
    messages: meta.messages,
    partial: meta.partial,
    sourceUpdatedAt: meta.sourceUpdatedAt,
    scannedAt
  }
}

async function scanSource(row: ImportSourceRow): Promise<ScanResult> {
  if (row.kind === 'codex') return scanCodexSource(row)
  if (row.kind === 'opencode') return scanOpencodeSource(row)
  if (row.kind === 'opencowork') return scanOpencoworkSource(row)
  const items: ImportPreviewItem[] = []
  const payloads = new Map<string, ItemPayload>()
  const diagnostics: ImportDiagnostic[] = []
  const configDir = row.configDir

  // ── 全局配置 ──
  const located = await locateGlobalConfig(configDir)
  const global = await readGlobalConfig(located.path)
  diagnostics.push(...global.diagnostics)

  // ── 会话与项目 ──
  const listed = await listTranscripts(configDir)
  diagnostics.push(...listed.diagnostics)

  const mappings = new Map(
    store.listImportMappings(row.sourceId, 'session').map((m) => [m.sourceItemId, m] as const)
  )
  const projectSessions = new Map<string, { count: number; last: number }>()
  const workspaces = store.listWorkspaces().filter((w) => isLocalEnvironment(w.environment))

  const scanCache = store.listImportScanCache(row.sourceId)
  const scannedAt = Date.now()
  const cacheWrites: ImportScanCacheRow[] = []

  for (const file of listed.files) {
    const changeToken = `${String(file.size)}:${String(Math.round(file.mtimeMs))}`
    // Claude Code 的映射键就是文件名,不必解析文件就知道这条导没导过。
    const mapping = mappings.get(file.sessionId)
    const meta = await readChatMeta({
      sourceId: row.sourceId,
      path: file.path,
      changeToken,
      fileUpdatedAt: file.mtimeMs,
      cache: scanCache,
      parse: (lines) =>
        parseTranscript(lines, {
          fallbackSessionId: file.sessionId,
          maxMessages: IMPORT_LIMITS.maxMessagesPerSession
        }),
      needsHash: mapping !== undefined
    })
    cacheWrites.push(scanCacheRow(row.sourceId, file.path, changeToken, meta, scannedAt))

    const cwd = meta.cwd
    const title = meta.title
    const model = meta.model
    const count = meta.messages
    const contentHash = meta.contentHash
    const updatedAt = meta.sourceUpdatedAt
    const itemDiagnostics: ImportDiagnostic[] = [...meta.diagnostics]

    if (count === 0) continue // 空会话不进列表 —— 导进来是一条永远空白的对话

    // ★ 归一化后才当键:转录里的 cwd 是原样的,可能经符号链接 / 大小写不同,
    //   不归一就会和 Codex / OpenCode 的同一个目录各建一个工作区。
    const projectKey = canonicalRoot(cwd)
    const bucket = projectSessions.get(projectKey) ?? { count: 0, last: 0 }
    projectSessions.set(projectKey, {
      count: bucket.count + 1,
      last: Math.max(bucket.last, updatedAt)
    })

    const id = itemId('chat', file.sessionId)
    const target = projectKey === '' ? undefined : findLocalWorkspaceByRoot(projectKey, workspaces)
    const status = statusOfChat(mapping, contentHash, target !== undefined, itemDiagnostics)
    items.push({
      id,
      category: 'chat',
      title: title ?? '新对话',
      sourcePath: file.path,
      status,
      ...(projectKey === '' ? {} : { projectKey }),
      ...(target === undefined ? {} : { targetWorkspaceId: target.id }),
      scope: 'project',
      count,
      ...(meta.partial ? { countApproximate: true } : {}),
      bytes: file.size,
      sourceUpdatedAt: updatedAt,
      diagnostics: itemDiagnostics,
      defaultSelected: status === 'new' || status === 'update'
    })
    payloads.set(id, {
      kind: 'chat',
      file,
      projectKey,
      contentHash,
      ...(title === undefined ? {} : { title }),
      ...(model === undefined ? {} : { model })
    })
  }

  // ── 项目候选 ──
  const projectPaths = new Set<string>([
    ...Object.keys(global.config.projects).map(canonicalRoot).filter((path) => path !== ''),
    ...[...projectSessions.keys()].filter((key) => key !== '')
  ])
  const projects: ImportProjectCandidate[] = []
  for (const path of [...projectPaths].sort()) {
    const bucket = projectSessions.get(path) ?? { count: 0, last: 0 }
    const accessible = await isDirectory(path)
    const target = findLocalWorkspaceByRoot(path, workspaces)
    const candidate: ImportProjectCandidate = {
      key: path,
      sourcePath: path,
      accessible,
      ...(target === undefined ? {} : { targetWorkspaceId: target.id, targetWorkspaceName: target.name }),
      sessionCount: bucket.count,
      ...(bucket.last === 0 ? {} : { lastActivityAt: bucket.last }),
      diagnostics: accessible ? [] : [{ code: 'project.needs-workspace', detail: path }]
    }
    projects.push(candidate)

    const id = itemId('project', path)
    items.push({
      id,
      category: 'project',
      title: basename(path) || path,
      sourcePath: path,
      status: target !== undefined ? 'exists' : accessible ? 'new' : 'needs-target',
      projectKey: path,
      ...(target === undefined ? {} : { targetWorkspaceId: target.id }),
      scope: 'project',
      count: bucket.count,
      diagnostics: candidate.diagnostics,
      defaultSelected: target === undefined && accessible
    })
    payloads.set(id, { kind: 'project', projectKey: path, sourcePath: path })
  }

  // ── 资产 ──
  await scanAssets(row, configDir, items, payloads)

  // ── 说明 ──
  const instructions = await readInstructions(configDir)
  if (instructions !== null && instructions.text.trim() !== '') {
    const id = itemId('instructions', 'global')
    const existing = store.getImportMapping(row.sourceId, '', 'instructions', 'global')
    items.push({
      id,
      category: 'instructions',
      title: CLAUDE_LAYOUT.instructions,
      sourcePath: join(configDir, CLAUDE_LAYOUT.instructions),
      status: existing === undefined ? 'new' : 'update',
      scope: 'global',
      bytes: instructions.text.length,
      diagnostics: instructions.diagnostics,
      defaultSelected: true
    })
    payloads.set(id, { kind: 'instructions', scope: 'global', text: instructions.text })
  }

  // ── MCP ──
  scanMcp(row, 'global', undefined, global.config.mcpServers, items, payloads)
  for (const [path, project] of Object.entries(global.config.projects)) {
    if (project.mcpServers === undefined) continue
    scanMcp(row, 'project', path, project.mcpServers, items, payloads)
  }
  for (const path of projectPaths) {
    if (!(await isDirectory(path))) continue
    const projectMcp = await readProjectMcp(path)
    diagnostics.push(...projectMcp.diagnostics)
    scanMcp(row, 'project', path, projectMcp.servers, items, payloads)
  }

  /*
    ★ 每次完整扫描都刷新一遍计数缓存 —— 放在这里(而不是只在 `detect` 里)是因为
    自动同步那条路径也走 `scanSource`,而它不经过 `detect`。少这一行的表现是
    「后台同步导进来几条,而来源那一行的数字还停在几小时前」。
  */
  sourceCounts.set(row.sourceId, {
    projectCount: projects.length,
    sessionCount: items.filter((item) => item.category === 'chat').length
  })

  commitScanCache(row.sourceId, cacheWrites, scannedAt)
  return { items, payloads, projects, diagnostics }
}

/**
 * 写回本轮的扫描缓存,并清掉源侧已经消失的行。
 *
 * ★ 一次批量写、一个事务:一轮扫描上千行,逐行提交在 WAL 上就是上千次写放大,
 * 而那正是这套缓存本来要省掉的开销。
 * ★ 缓存写失败**不能**让整次预览失败 —— 它是加速手段,不是数据。丢了最多下次慢一点。
 */
function commitScanCache(
  sourceId: string,
  rows: readonly ImportScanCacheRow[],
  scannedAt: number
): void {
  try {
    store.putImportScanCache(rows)
    store.pruneImportScanCache(sourceId, scannedAt)
  } catch {
    // 故意吞掉,理由见上。
  }
}

async function scanCodexSource(row: ImportSourceRow): Promise<ScanResult> {
  const items: ImportPreviewItem[] = []
  const payloads = new Map<string, ItemPayload>()
  const diagnostics: ImportDiagnostic[] = []
  const profiles = store.getKv<string[]>('imports.profiles:' + row.sourceId, [])
  const providers = await listCodexProviders(row.configDir, diagnostics, profiles)
  for (const provider of providers) {
    const key = 'provider:' + provider.profile + ':' + provider.id
    const id = itemId('provider', key)
    const itemDiagnostics = [...provider.diagnostics]
    let mapped: ReturnType<typeof mapCodexProvider> | undefined
    try { mapped = mapCodexProvider(row.sourceId, provider) } catch { itemDiagnostics.push({ code: 'provider.needs-manual-setup', detail: 'base_url' }) }
    const mapping = store.getImportMapping(row.sourceId, '', 'provider', key)
    const target = mapped ? store.listProviders().find((candidate) => candidate.id === mapped.provider.id) : undefined
    let status = mapped ? codexStatus(mapping, provider.fingerprint, target ? providerSurface(target) : null, itemDiagnostics) : 'incompatible'
    if (mapped?.alias && status !== 'conflict' && status !== 'incompatible') {
      const aliasMapping = store.getImportMapping(row.sourceId, '', 'alias', `${key}:${mapped.alias.alias}`)
      const aliasTarget = store.listAliases().find((candidate) => candidate.providerId === mapped.provider.id && candidate.alias === mapped.alias?.alias)
      const aliasStatus = codexStatus(aliasMapping, aliasSurface(mapped.alias), aliasTarget ? aliasSurface(aliasTarget) : null, itemDiagnostics)
      if (aliasStatus === 'conflict') status = 'conflict'
      else if (status === 'exists' && (aliasStatus === 'new' || aliasStatus === 'update')) status = 'update'
    }
    items.push({ id, category: 'provider', title: provider.name, sourcePath: provider.sourcePath, status, scope: 'global', diagnostics: itemDiagnostics, defaultSelected: status === 'new',
      provider: { protocol: mapped?.provider.protocol ?? 'openai-responses', baseUrl: provider.baseUrl, profile: provider.profile, model: provider.defaultModel, envKey: provider.envKey } })
    if (mapped) payloads.set(id, { kind: 'provider', provider, ...(mapped.alias ? { alias: mapped.alias } : {}) })
  }

  const titles = await readCodexSessionIndex(row.configDir, diagnostics)
  const files = await listCodexSessions(row.configDir, diagnostics)
  const projectStats = new Map<string, { count: number; last: number }>()
  // ★ config.toml 里的路径和会话 cwd 走同一套归一化,否则一个来源内部就能
  //   为同一个目录排出两条项目候选。
  for (const path of await listCodexProjectRoots(row.configDir, diagnostics)) projectStats.set(canonicalRoot(path), { count: 0, last: 0 })
  const seenSessions = new Set<string>()
  /*
    ★ 映射一次性拉齐,不在循环里一条一条 `getImportMapping` —— 513 个文件
    就是 513 次 SQL。它同时是「要不要为哈希读全文」的判据来源。
  */
  const sessionMappings = new Map(
    store.listImportMappings(row.sourceId, 'session').map((m) => [m.sourceItemId, m] as const)
  )
  const scanCache = store.listImportScanCache(row.sourceId)
  const scannedAt = Date.now()
  const cacheWrites: ImportScanCacheRow[] = []

  for (const file of files) {
    try {
      const changeToken = `${String(file.size)}:${String(Math.round(file.mtimeMs))}`
      /*
        ★ Codex 的会话 id 在**文件内容**里(文件名是 `rollout-*`),所以
        「这条导没导过」要等解析出 id 才知道 —— 用回调而不是预先算好的布尔值。
      */
      const meta = await readChatMeta({
        sourceId: row.sourceId,
        path: file.path,
        changeToken,
        fileUpdatedAt: file.mtimeMs,
        cache: scanCache,
        parse: (lines) =>
          parseCodexTranscript(lines, {
            fallbackSessionId: file.sessionId,
            maxMessages: IMPORT_LIMITS.maxMessagesPerSession
          }),
        hashNeededFor: (sessionId) => sessionMappings.has('session:' + sessionId)
      })
      cacheWrites.push(scanCacheRow(row.sourceId, file.path, changeToken, meta, scannedAt))

      if (seenSessions.has(meta.sessionId)) continue
      seenSessions.add(meta.sessionId)
      file.sessionId = meta.sessionId
      const title = titles.get(meta.sessionId) ?? meta.title
      const key = 'session:' + meta.sessionId
      const id = itemId('chat', key)
      const projectKey = canonicalRoot(meta.cwd)
      const itemDiagnostics = [...meta.diagnostics]
      if (meta.modelProvider && !providers.some((provider) => provider.id === meta.modelProvider)) {
        itemDiagnostics.push({ code: 'model-provider-unresolved', detail: meta.modelProvider })
      }
      const mapping = sessionMappings.get(key)
      const workspaceId = resolveWorkspace(row.sourceId, projectKey, new Map())
      const status = meta.messages === 0 ? 'incompatible' : statusOfChat(mapping, meta.contentHash, workspaceId !== null, itemDiagnostics)
      if (projectKey) {
        const stats = projectStats.get(projectKey) ?? { count: 0, last: 0 }
        projectStats.set(projectKey, { count: stats.count + 1, last: Math.max(stats.last, meta.sourceUpdatedAt) })
      }
      items.push({ id, category: 'chat', title: title ?? meta.sessionId, sourcePath: file.path, status, scope: 'project', ...(projectKey ? { projectKey } : {}), ...(workspaceId ? { targetWorkspaceId: workspaceId } : {}), count: meta.messages, ...(meta.partial ? { countApproximate: true } : {}), bytes: file.size, sourceUpdatedAt: meta.sourceUpdatedAt, diagnostics: itemDiagnostics, defaultSelected: status === 'new' || status === 'update' })
      payloads.set(id, { kind: 'codex-chat', file, contentHash: meta.contentHash, ...(title ? { title } : {}), ...(meta.model ? { model: meta.model } : {}), ...(meta.modelProvider ? { modelProvider: meta.modelProvider } : {}), archived: file.archived })
    } catch { diagnostics.push({ code: 'source.unreadable', detail: file.path }) }
  }
  commitScanCache(row.sourceId, cacheWrites, scannedAt)
  const projects: ImportProjectCandidate[] = []
  for (const [path, stats] of projectStats) {
    const accessible = await isDirectory(path)
    const workspaceId = resolveWorkspace(row.sourceId, path, new Map())
    const workspace = workspaceId ? store.getWorkspace(workspaceId) : undefined
    const projectDiagnostics: ImportDiagnostic[] = accessible ? [] : [{ code: 'project.needs-workspace', detail: path }]
    projects.push({ key: path, sourcePath: path, accessible, ...(workspace ? { targetWorkspaceId: workspace.id, targetWorkspaceName: workspace.name } : {}), sessionCount: stats.count, lastActivityAt: stats.last, diagnostics: projectDiagnostics })
    const id = itemId('project', path)
    items.push({ id, category: 'project', title: basename(path) || path, sourcePath: path, status: workspace ? 'exists' : accessible ? 'new' : 'needs-target', projectKey: path, ...(workspace ? { targetWorkspaceId: workspace.id } : {}), scope: 'project', count: stats.count, diagnostics: projectDiagnostics, defaultSelected: !workspace && accessible })
    payloads.set(id, { kind: 'project', projectKey: path, sourcePath: path })
  }
  const projectRoots = projects.filter((project) => project.accessible).map((project) => project.sourcePath)
  const skills = await listCodexSkills(row.configDir, projectRoots, diagnostics)
  for (const skill of skills) {
    const key = 'skill:' + skill.scope + ':' + (skill.projectKey ?? '') + ':' + skill.name
    const id = itemId('skill', key)
    const scopeKey = skill.projectKey ?? ''
    const workspaceId = skill.projectKey ? resolveWorkspace(row.sourceId, skill.projectKey, new Map()) : null
    const workspace = workspaceId ? store.getWorkspace(workspaceId) : undefined
    const targetPath = skill.scope === 'project' ? join(workspace?.rootPath ?? scopeKey, '.next-cowork', 'skills', skill.name) : join(databaseDirectory(), 'skills', skill.name)
    const mapping = store.getImportMapping(row.sourceId, scopeKey, 'skill', key)
    const itemDiagnostics = [...skill.diagnostics]
    if (skill.compatibilityPath) itemDiagnostics.push({ code: 'skill.compatibility-path' })
    if (skill.disabled) itemDiagnostics.push({ code: 'skill.disabled-in-source' })
    let status = skill.fingerprint === '' || skill.diagnostics.length ? 'incompatible' as const : codexStatus(mapping, skill.fingerprint, await currentFingerprint(targetPath), itemDiagnostics)
    if (status === 'new' && skill.scope === 'project' && !workspace) status = 'needs-target'
    items.push({ id, category: 'skill', title: skill.name, sourcePath: skill.dir, status, scope: skill.scope, ...(scopeKey ? { projectKey: scopeKey } : {}), ...(workspaceId ? { targetWorkspaceId: workspaceId } : {}), diagnostics: itemDiagnostics, defaultSelected: status === 'new' })
    payloads.set(id, { kind: 'codex-skill', skill })
  }
  for (const hook of await listCodexHooks(row.configDir, projectRoots, diagnostics)) {
    const id = itemId('hook', hook.sourceKey)
    const mapped = mapCodexHook(row.sourceId, hook)
    const scopeKey = hook.projectKey ?? ''
    const workspaceId = hook.projectKey ? resolveWorkspace(row.sourceId, hook.projectKey, new Map()) : null
    const workspace = workspaceId ? store.getWorkspace(workspaceId) : undefined
    const path = hook.scope === 'global' ? globalSettingsPath(getHost().paths.userData()) : workspace ? localSettingsPath(workspace.rootPath) : ''
    const target = mapped.hook && path !== '' ? await targetHook(path, mapped.hook.id, hook.scope) : undefined
    const mapping = store.getImportMapping(row.sourceId, scopeKey, 'hook', hook.sourceKey)
    const itemDiagnostics = [...mapped.diagnostics]
    let status = mapped.hook ? codexStatus(mapping, fingerprint(JSON.stringify(hook)), target ? hookSurface(target) : null, itemDiagnostics) : 'incompatible' as const
    if (status === 'update' && target?.enabled) { itemDiagnostics.push({ code: 'target.locally-modified' }); status = 'conflict' }
    if (status === 'new' && hook.scope === 'project' && !workspace) status = 'needs-target'
    items.push({ id, category: 'hook', title: hook.event, sourcePath: hook.sourcePath, status, scope: hook.scope, ...(scopeKey ? { projectKey: scopeKey } : {}), ...(workspaceId ? { targetWorkspaceId: workspaceId } : {}), diagnostics: itemDiagnostics, defaultSelected: status === 'new',
      hook: { event: hook.event, matcher: hook.matcher, command: hook.command, timeoutMs: mapped.hook?.timeoutMs } })
    payloads.set(id, { kind: 'hook', hook })
  }
  sourceCounts.set(row.sourceId, { projectCount: projects.length, sessionCount: seenSessions.size })
  return { items, payloads, projects, diagnostics }
}

async function scanOpencodeSource(row: ImportSourceRow): Promise<ScanResult> {
  const items: ImportPreviewItem[] = []
  const payloads = new Map<string, ItemPayload>()
  const diagnostics: ImportDiagnostic[] = []
  const configDir = opencodeConfigDir()

  // ── provider(opencode.jsonc `provider` + auth.json,只搬键名不搬值) ──
  const { config, path: configPath } = await readOpencodeConfig(configDir, diagnostics)
  const authNames = await readOpencodeAuthNames(row.configDir)
  const providers = listOpencodeProviders(config, authNames, configPath)
  for (const provider of providers) {
    const key = 'provider:' + provider.id
    const id = itemId('provider', key)
    const itemDiagnostics = [...provider.diagnostics]
    let mapped: ReturnType<typeof mapOpencodeProvider> | undefined
    try { mapped = mapOpencodeProvider(row.sourceId, provider) } catch { itemDiagnostics.push({ code: 'provider.needs-manual-setup', detail: 'base_url' }) }
    const mapping = store.getImportMapping(row.sourceId, '', 'provider', key)
    const target = mapped ? store.listProviders().find((candidate) => candidate.id === mapped.provider.id) : undefined
    let status = mapped ? codexStatus(mapping, provider.fingerprint, target ? providerSurface(target) : null, itemDiagnostics) : 'incompatible'
    if (mapped?.alias && status !== 'conflict' && status !== 'incompatible') {
      const aliasMapping = store.getImportMapping(row.sourceId, '', 'alias', `${key}:${mapped.alias.alias}`)
      const aliasTarget = store.listAliases().find((candidate) => candidate.providerId === mapped.provider.id && candidate.alias === mapped.alias?.alias)
      const aliasStatus = codexStatus(aliasMapping, aliasSurface(mapped.alias), aliasTarget ? aliasSurface(aliasTarget) : null, itemDiagnostics)
      if (aliasStatus === 'conflict') status = 'conflict'
      else if (status === 'exists' && (aliasStatus === 'new' || aliasStatus === 'update')) status = 'update'
    }
    items.push({ id, category: 'provider', title: provider.name, sourcePath: provider.sourcePath, status, scope: 'global', diagnostics: itemDiagnostics, defaultSelected: status === 'new',
      provider: { protocol: mapped?.provider.protocol ?? provider.protocol, baseUrl: provider.baseUrl, profile: 'default', model: provider.defaultModel } })
    if (mapped) payloads.set(id, { kind: 'opencode-provider', provider, ...(mapped.alias ? { alias: mapped.alias } : {}) })
  }

  // ── mcp ──
  scanMcp(row, 'global', undefined, listOpencodeMcp(config), items, payloads)

  // ── plugin:本地跑不了 npm 插件,只报告,不建 payload ──
  for (const plugin of listOpencodePlugins(config)) {
    const id = itemId('hook', 'plugin:' + plugin)
    items.push({ id, category: 'hook', title: plugin, sourcePath: configPath, status: 'incompatible', scope: 'global', diagnostics: [{ code: 'hook.plugin-skipped', detail: plugin }], defaultSelected: false })
  }

  // ── chats + projects(SQLite) ──
  const sessions = await listOpencodeSessions(row.configDir, diagnostics)
  const projectStats = new Map<string, { count: number; last: number }>()
  const seenSessions = new Set<string>()
  for (const meta of sessions) {
    const raw = await readOpencodeSession(row.configDir, meta.id)
    if (!raw) { diagnostics.push({ code: 'source.unreadable', detail: meta.id }); continue }
    const parsed = parseOpencodeSession(raw, { maxMessages: IMPORT_LIMITS.maxMessagesPerSession })
    if (seenSessions.has(parsed.sessionId)) continue
    seenSessions.add(parsed.sessionId)
    const key = 'session:' + parsed.sessionId
    const id = itemId('chat', key)
    const projectKey = canonicalRoot(parsed.cwd)
    const itemDiagnostics = [...parsed.diagnostics]
    if (parsed.modelProvider && !providers.some((provider) => provider.id === parsed.modelProvider)) itemDiagnostics.push({ code: 'model-provider-unresolved', detail: parsed.modelProvider })
    const contentHash = hashTranscript(parsed)
    const mapping = store.getImportMapping(row.sourceId, '', 'session', key)
    const workspaceId = resolveWorkspace(row.sourceId, projectKey, new Map())
    const status = parsed.messages.length === 0 ? 'incompatible' : statusOfChat(mapping, contentHash, workspaceId !== null, itemDiagnostics)
    if (projectKey) {
      const stats = projectStats.get(projectKey) ?? { count: 0, last: 0 }
      projectStats.set(projectKey, { count: stats.count + 1, last: Math.max(stats.last, parsed.updatedAt ?? meta.updatedAt) })
    }
    items.push({ id, category: 'chat', title: parsed.title ?? parsed.sessionId, sourcePath: `opencode.db#${parsed.sessionId}`, status, scope: 'project', ...(projectKey ? { projectKey } : {}), ...(workspaceId ? { targetWorkspaceId: workspaceId } : {}), count: parsed.messages.length, sourceUpdatedAt: parsed.updatedAt ?? meta.updatedAt, diagnostics: itemDiagnostics, defaultSelected: status === 'new' || status === 'update' })
    payloads.set(id, { kind: 'opencode-chat', sessionId: parsed.sessionId, contentHash, ...(parsed.title ? { title: parsed.title } : {}), ...(parsed.model ? { model: parsed.model } : {}), ...(parsed.modelProvider ? { modelProvider: parsed.modelProvider } : {}) })
  }
  const projects: ImportProjectCandidate[] = []
  for (const [path, stats] of projectStats) {
    const accessible = await isDirectory(path)
    const workspaceId = resolveWorkspace(row.sourceId, path, new Map())
    const workspace = workspaceId ? store.getWorkspace(workspaceId) : undefined
    const projectDiagnostics: ImportDiagnostic[] = accessible ? [] : [{ code: 'project.needs-workspace', detail: path }]
    projects.push({ key: path, sourcePath: path, accessible, ...(workspace ? { targetWorkspaceId: workspace.id, targetWorkspaceName: workspace.name } : {}), sessionCount: stats.count, lastActivityAt: stats.last, diagnostics: projectDiagnostics })
    const id = itemId('project', path)
    items.push({ id, category: 'project', title: basename(path) || path, sourcePath: path, status: workspace ? 'exists' : accessible ? 'new' : 'needs-target', projectKey: path, ...(workspace ? { targetWorkspaceId: workspace.id } : {}), scope: 'project', count: stats.count, diagnostics: projectDiagnostics, defaultSelected: !workspace && accessible })
    payloads.set(id, { kind: 'project', projectKey: path, sourcePath: path })
  }

  // ── 资产(agent/command/skill,存在才有;本机为空则降级为 0 项) ──
  const assets = await listOpencodeAssets(configDir)
  for (const skill of assets.skills) {
    const id = itemId('skill', skill.name)
    const targetPath = join(databaseDirectory(), 'skills', skill.name)
    const mapping = store.getImportMapping(row.sourceId, '', 'skill', skill.name)
    const assetDiagnostics = await inspectSkill(skill.dir)
    const status = assetStatus(mapping, await currentFingerprint(targetPath), skill.name, assetDiagnostics)
    items.push({ id, category: 'skill', title: skill.name, sourcePath: skill.dir, status, scope: 'global', diagnostics: assetDiagnostics, defaultSelected: status === 'new' })
    payloads.set(id, { kind: 'skill', name: skill.name, dir: skill.dir })
  }
  for (const entry of [{ kind: 'agent' as const, dir: 'agents', list: assets.agents }, { kind: 'command' as const, dir: 'commands', list: assets.commands }]) {
    for (const asset of entry.list) {
      const id = itemId(entry.kind, asset.name)
      const targetPath = join(databaseDirectory(), entry.dir, `${asset.name}.md`)
      const mapping = store.getImportMapping(row.sourceId, '', entry.kind, asset.name)
      const inspected = await inspectMarkdownAsset(entry.kind, asset.file)
      const status = assetStatus(mapping, await currentFingerprint(targetPath), asset.name, inspected.diagnostics)
      items.push({ id, category: entry.kind, title: asset.name, sourcePath: asset.file, status, scope: 'global', diagnostics: inspected.diagnostics, defaultSelected: status === 'new' })
      payloads.set(id, { kind: entry.kind, name: asset.name, file: asset.file })
    }
  }

  sourceCounts.set(row.sourceId, { projectCount: projects.length, sessionCount: seenSessions.size })
  return { items, payloads, projects, diagnostics }
}

async function scanOpencoworkSource(row: ImportSourceRow): Promise<ScanResult> {
  const items: ImportPreviewItem[] = []
  const payloads = new Map<string, ItemPayload>()
  const diagnostics: ImportDiagnostic[] = []

  // ── provider(ai-provider/*.json,只搬结构不搬 apiKey) ──
  const providers = await listOpencoworkProviders(row.configDir)
  for (const provider of providers) {
    const key = 'provider:' + provider.id
    const id = itemId('provider', key)
    const itemDiagnostics = [...provider.diagnostics]
    let mapped: ReturnType<typeof mapOpencoworkProvider> | undefined
    try { mapped = mapOpencoworkProvider(row.sourceId, provider) } catch { itemDiagnostics.push({ code: 'provider.needs-manual-setup', detail: 'base_url' }) }
    const mapping = store.getImportMapping(row.sourceId, '', 'provider', key)
    const target = mapped ? store.listProviders().find((candidate) => candidate.id === mapped.provider.id) : undefined
    let status = mapped ? codexStatus(mapping, provider.fingerprint, target ? providerSurface(target) : null, itemDiagnostics) : 'incompatible'
    if (mapped?.alias && status !== 'conflict' && status !== 'incompatible') {
      const aliasMapping = store.getImportMapping(row.sourceId, '', 'alias', `${key}:${mapped.alias.alias}`)
      const aliasTarget = store.listAliases().find((candidate) => candidate.providerId === mapped.provider.id && candidate.alias === mapped.alias?.alias)
      const aliasStatus = codexStatus(aliasMapping, aliasSurface(mapped.alias), aliasTarget ? aliasSurface(aliasTarget) : null, itemDiagnostics)
      if (aliasStatus === 'conflict') status = 'conflict'
      else if (status === 'exists' && (aliasStatus === 'new' || aliasStatus === 'update')) status = 'update'
    }
    items.push({ id, category: 'provider', title: provider.name, sourcePath: provider.sourcePath, status, scope: 'global', diagnostics: itemDiagnostics, defaultSelected: status === 'new',
      provider: { protocol: mapped?.provider.protocol ?? provider.protocol, baseUrl: provider.baseUrl, profile: 'default', model: provider.defaultModel } })
    if (mapped) payloads.set(id, { kind: 'opencowork-provider', provider, ...(mapped.alias ? { alias: mapped.alias } : {}) })
  }

  // ── mcp ──
  scanMcp(row, 'global', undefined, await listOpencoworkMcp(row.configDir), items, payloads)

  // ── chats + projects(SQLite) ──
  const sessions = await listOpencoworkSessions(row.configDir, diagnostics)
  const projectStats = new Map<string, { count: number; last: number }>()
  const seenSessions = new Set<string>()
  for (const meta of sessions) {
    const raw = await readOpencoworkSession(row.configDir, meta.id)
    if (!raw) { diagnostics.push({ code: 'source.unreadable', detail: meta.id }); continue }
    const parsed = parseOpencoworkSession(raw, { maxMessages: IMPORT_LIMITS.maxMessagesPerSession })
    if (seenSessions.has(parsed.sessionId)) continue
    seenSessions.add(parsed.sessionId)
    const key = 'session:' + parsed.sessionId
    const id = itemId('chat', key)
    const projectKey = canonicalRoot(parsed.cwd)
    const itemDiagnostics = [...parsed.diagnostics]
    if (parsed.modelProvider && !providers.some((provider) => provider.id === parsed.modelProvider)) itemDiagnostics.push({ code: 'model-provider-unresolved', detail: parsed.modelProvider })
    const contentHash = hashTranscript(parsed)
    const mapping = store.getImportMapping(row.sourceId, '', 'session', key)
    const workspaceId = resolveWorkspace(row.sourceId, projectKey, new Map())
    const status = parsed.messages.length === 0 ? 'incompatible' : statusOfChat(mapping, contentHash, workspaceId !== null, itemDiagnostics)
    if (projectKey) {
      const stats = projectStats.get(projectKey) ?? { count: 0, last: 0 }
      projectStats.set(projectKey, { count: stats.count + 1, last: Math.max(stats.last, parsed.updatedAt ?? meta.updatedAt) })
    }
    items.push({ id, category: 'chat', title: parsed.title ?? parsed.sessionId, sourcePath: `data.db#${parsed.sessionId}`, status, scope: 'project', ...(projectKey ? { projectKey } : {}), ...(workspaceId ? { targetWorkspaceId: workspaceId } : {}), count: parsed.messages.length, sourceUpdatedAt: parsed.updatedAt ?? meta.updatedAt, diagnostics: itemDiagnostics, defaultSelected: status === 'new' || status === 'update' })
    payloads.set(id, { kind: 'opencowork-chat', sessionId: parsed.sessionId, contentHash, ...(parsed.title ? { title: parsed.title } : {}), ...(parsed.model ? { model: parsed.model } : {}), ...(parsed.modelProvider ? { modelProvider: parsed.modelProvider } : {}) })
  }
  const projects: ImportProjectCandidate[] = []
  for (const [path, stats] of projectStats) {
    const accessible = await isDirectory(path)
    const workspaceId = resolveWorkspace(row.sourceId, path, new Map())
    const workspace = workspaceId ? store.getWorkspace(workspaceId) : undefined
    const projectDiagnostics: ImportDiagnostic[] = accessible ? [] : [{ code: 'project.needs-workspace', detail: path }]
    projects.push({ key: path, sourcePath: path, accessible, ...(workspace ? { targetWorkspaceId: workspace.id, targetWorkspaceName: workspace.name } : {}), sessionCount: stats.count, lastActivityAt: stats.last, diagnostics: projectDiagnostics })
    const id = itemId('project', path)
    items.push({ id, category: 'project', title: basename(path) || path, sourcePath: path, status: workspace ? 'exists' : accessible ? 'new' : 'needs-target', projectKey: path, ...(workspace ? { targetWorkspaceId: workspace.id } : {}), scope: 'project', count: stats.count, diagnostics: projectDiagnostics, defaultSelected: !workspace && accessible })
    payloads.set(id, { kind: 'project', projectKey: path, sourcePath: path })
  }

  // ── 资产(agent/command,存在才有;skill 复用 ~/.agents/skills 全局目录) ──
  const skills = await listOpencoworkSkills()
  for (const skill of skills) {
    const id = itemId('skill', skill.name)
    const targetPath = join(databaseDirectory(), 'skills', skill.name)
    const mapping = store.getImportMapping(row.sourceId, '', 'skill', skill.name)
    const assetDiagnostics = await inspectSkill(skill.dir)
    const status = assetStatus(mapping, await currentFingerprint(targetPath), skill.name, assetDiagnostics)
    items.push({ id, category: 'skill', title: skill.name, sourcePath: skill.dir, status, scope: 'global', diagnostics: assetDiagnostics, defaultSelected: status === 'new' })
    payloads.set(id, { kind: 'skill', name: skill.name, dir: skill.dir })
  }
  const assets = await listOpencoworkAssets(row.configDir)
  for (const entry of [{ kind: 'agent' as const, dir: 'agents', list: assets.agents }, { kind: 'command' as const, dir: 'commands', list: assets.commands }]) {
    for (const asset of entry.list) {
      const id = itemId(entry.kind, asset.name)
      const targetPath = join(databaseDirectory(), entry.dir, `${asset.name}.md`)
      const mapping = store.getImportMapping(row.sourceId, '', entry.kind, asset.name)
      const inspected = await inspectMarkdownAsset(entry.kind, asset.file)
      const status = assetStatus(mapping, await currentFingerprint(targetPath), asset.name, inspected.diagnostics)
      items.push({ id, category: entry.kind, title: asset.name, sourcePath: asset.file, status, scope: 'global', diagnostics: inspected.diagnostics, defaultSelected: status === 'new' })
      payloads.set(id, { kind: entry.kind, name: asset.name, file: asset.file })
    }
  }

  // ── hook(hooks.json,全局,事件名与本项目直接同名交集) ──
  for (const hook of await listOpencoworkHooks(row.configDir, diagnostics)) {
    const id = itemId('hook', hook.sourceKey)
    const mapped = mapOpencoworkHook(row.sourceId, hook)
    const path = globalSettingsPath(getHost().paths.userData())
    const target = mapped.hook ? await targetHook(path, mapped.hook.id, 'global') : undefined
    const mapping = store.getImportMapping(row.sourceId, '', 'hook', hook.sourceKey)
    const itemDiagnostics = [...mapped.diagnostics]
    let status = mapped.hook ? codexStatus(mapping, fingerprint(JSON.stringify(hook)), target ? hookSurface(target) : null, itemDiagnostics) : 'incompatible' as const
    if (status === 'update' && target?.enabled) { itemDiagnostics.push({ code: 'target.locally-modified' }); status = 'conflict' }
    items.push({ id, category: 'hook', title: hook.event, sourcePath: hook.sourcePath, status, scope: 'global', diagnostics: itemDiagnostics, defaultSelected: status === 'new',
      hook: { event: hook.event, matcher: hook.matcher, command: hook.command, timeoutMs: mapped.hook?.timeoutMs } })
    payloads.set(id, { kind: 'opencowork-hook', hook })
  }

  sourceCounts.set(row.sourceId, { projectCount: projects.length, sessionCount: seenSessions.size })
  return { items, payloads, projects, diagnostics }
}

/** 资产条目的预览状态。与 `scanAssets` 的判定同源:目标存在但非我们所导 → 默认跳过。 */
function assetStatus(mapping: ImportMappingRow | undefined, targetFingerprint: string | null, name: string, diagnostics: ImportDiagnostic[]): ImportPreviewItem['status'] {
  if (targetFingerprint !== null && mapping === undefined) { diagnostics.push({ code: 'target.name-conflict', detail: name }); return 'exists' }
  if (mapping === undefined) return 'new'
  if (targetFingerprint !== null && targetFingerprint !== mapping.targetFingerprint) { diagnostics.push({ code: 'target.locally-modified', detail: name }); return 'conflict' }
  return 'update'
}

function codexStatus(mapping: ImportMappingRow | undefined, sourceHash: string, targetHash: string | null, diagnostics: ImportDiagnostic[]): ImportPreviewItem['status'] {
  if (mapping?.syncState === 'detached') { diagnostics.push({ code: 'target.detached' }); return 'conflict' }
  if (mapping?.syncState === 'suppressed' || (mapping && targetHash === null)) { diagnostics.push({ code: 'target.deleted' }); return 'conflict' }
  if (!mapping && targetHash !== null) { diagnostics.push({ code: 'target.name-conflict' }); return 'exists' }
  if (!mapping) return 'new'
  if (targetHash !== mapping.targetFingerprint) { diagnostics.push({ code: 'target.locally-modified' }); return 'conflict' }
  return mapping.sourceFingerprint === sourceHash ? 'exists' : 'update'
}

async function targetHook(path: string, id: string, scope: 'global' | 'project'): Promise<HookDefinition | undefined> {
  const text = await readTextBounded(path, 2 * 1024 * 1024)
  if (text === null) return undefined
  try {
    const hooks = normalizeLocalSettings(JSON.parse(text)).hooks
    return hookListFrom(hooks, scope, path).find((hook) => hook.id === id)
  } catch { return undefined }
}

function hookSurface(hook: HookDefinition): string {
  // Preserve the existing command fingerprint so upgrades do not create false import conflicts.
  if (hook.type === 'command') {
    return fingerprint(JSON.stringify([hook.id, hook.event, hook.command, hook.matcher ?? '', hook.timeoutMs, hook.description ?? '']))
  }
  return fingerprint(JSON.stringify([hook.id, hook.event, hook.type, hook.prompt, hook.model ?? '',
    hook.modelProviderId ?? '', hook.matcher ?? '', hook.timeoutMs, hook.description ?? '']))
}

/** 转录的规范化内容指纹。★ 按**已归一化的消息**算,不是按文件字节。 */
function hashTranscript(parsed: ParsedTranscript): string {
  return fingerprint(
    JSON.stringify(parsed.messages.map((m) => [m.sourceId, m.role, m.parts]))
  )
}

function statusOfChat(
  mapping: ImportMappingRow | undefined,
  contentHash: string,
  hasTarget: boolean,
  diagnostics: ImportDiagnostic[]
): ImportPreviewItem['status'] {
  if (diagnostics.some((d) => d.code === 'transcript.branch-unresolvable')) return 'incompatible'
  if (mapping?.syncState === 'detached') {
    diagnostics.push({ code: 'target.detached' })
    return 'conflict'
  }
  if (mapping?.syncState === 'suppressed') {
    diagnostics.push({ code: 'target.deleted' })
    return 'conflict'
  }
  if (!hasTarget) return 'needs-target'
  if (mapping === undefined) return 'new'
  if (mapping.sourceFingerprint === contentHash) return 'exists'
  return 'update'
}

async function scanAssets(
  row: ImportSourceRow,
  configDir: string,
  items: ImportPreviewItem[],
  payloads: Map<string, ItemPayload>
): Promise<void> {
  const spec = [
    { kind: 'skill' as const, dirKey: 'skills' as const, targetDir: 'skills' },
    { kind: 'agent' as const, dirKey: 'agents' as const, targetDir: 'agents' },
    { kind: 'command' as const, dirKey: 'commands' as const, targetDir: 'commands' }
  ]

  for (const entry of spec) {
    const names = await listAssetNames(configDir, entry.dirKey)
    for (const rawName of names) {
      const name = entry.kind === 'skill' ? rawName : rawName.slice(0, -'.md'.length)
      const source = await resolveWithinRoot(configDir, entry.dirKey, rawName)
      if (source === null) continue

      const targetPath = join(databaseDirectory(), entry.targetDir, entry.kind === 'skill' ? name : `${name}.md`)
      const existingMapping = store.getImportMapping(row.sourceId, '', entry.kind, name)
      const targetFingerprint = await currentFingerprint(targetPath)
      const diagnostics: ImportDiagnostic[] = []

      let status: ImportPreviewItem['status']
      if (targetFingerprint !== null && existingMapping === undefined) {
        // 目标已存在但不是我们导入的 —— 默认跳过,不覆盖用户自己写的那一份。
        diagnostics.push({ code: 'target.name-conflict', detail: name })
        status = 'exists'
      } else if (existingMapping === undefined) {
        status = 'new'
      } else if (targetFingerprint !== null && targetFingerprint !== existingMapping.targetFingerprint) {
        diagnostics.push({ code: 'target.locally-modified', detail: name })
        status = 'conflict'
      } else {
        status = 'update'
      }

      const id = itemId(entry.kind, name)
      const inspected = entry.kind === 'skill'
        ? { diagnostics: await inspectSkill(source) }
        : await inspectMarkdownAsset(entry.kind, source)
      diagnostics.push(...inspected.diagnostics)

      items.push({
        id,
        category: entry.kind,
        title: name,
        sourcePath: source,
        status,
        scope: 'global',
        diagnostics,
        defaultSelected: status === 'new'
      })
      payloads.set(
        id,
        entry.kind === 'skill'
          ? { kind: 'skill', name, dir: source }
          : { kind: entry.kind, name, file: source }
      )
    }
  }
}

/**
 * 技能包里那些本地**还没实现**的约束。
 *
 * ★ 不能静默丢掉后照常启用 —— `disable-model-invocation` 的含义是
 * 「模型不许自己调用这条技能」,丢掉它等于把一条用户明确限制过的能力放开了。
 */
async function inspectSkill(dir: string): Promise<ImportDiagnostic[]> {
  const text = await readTextBounded(join(dir, 'SKILL.md'), 256 * 1024)
  if (text === null) return [{ code: 'source.unreadable', detail: 'SKILL.md' }]
  const diagnostics: ImportDiagnostic[] = []
  for (const key of ['disable-model-invocation', 'user-invocable', 'allowed-directories']) {
    if (new RegExp(`^\\s*${key}\\s*:`, 'mi').test(text)) {
      diagnostics.push({ code: 'skill.unsupported-constraint', detail: key })
    }
  }
  return diagnostics
}

/** Agent / Command 的 frontmatter 检查。工具别名归一化复用内核那张表。 */
async function inspectMarkdownAsset(
  kind: 'agent' | 'command',
  file: string
): Promise<{ diagnostics: ImportDiagnostic[] }> {
  const text = await readTextBounded(file, 128 * 1024)
  if (text === null) return { diagnostics: [{ code: 'source.unreadable', detail: basename(file) }] }
  const diagnostics: ImportDiagnostic[] = []

  if (kind === 'command') {
    // `!`(cmd)` 动态 shell 插值:**永不执行**,标为待调整。
    if (/!`[^`]+`/.test(text)) diagnostics.push({ code: 'command.dynamic-shell' })
    return { diagnostics }
  }

  const { parseFrontmatter, fmList, fmString } = await import('../kernel/frontmatter')
  const { normalizeToolList } = await import('../kernel/agent/tool-alias')
  const fm = parseFrontmatter(text)
  const tools = fmList(fm, 'tools')
  if (tools !== undefined) {
    const normalized = normalizeToolList(tools)
    if (normalized.unknown.length > 0) {
      diagnostics.push({ code: 'agent.unknown-tools', detail: normalized.unknown.join(', ') })
    }
  }
  const mode = fmString(fm, 'permissionMode') ?? fmString(fm, 'permission-mode')
  if (mode !== undefined && !['ask', 'auto', 'plan', 'acceptEdits', 'bypassPermissions'].includes(mode)) {
    diagnostics.push({ code: 'agent.unsupported-permission', detail: mode })
  }
  const model = fmString(fm, 'model')
  if (model !== undefined && model !== '' && !/^(inherit|sonnet|opus|haiku)$/i.test(model)) {
    // 解析不出来就继承本地当前模型,并留一条诊断 —— 不静默指向一个不存在的 provider。
    diagnostics.push({ code: 'agent.model-unresolved', detail: model })
  }
  return { diagnostics }
}

function scanMcp(
  row: ImportSourceRow,
  scope: 'global' | 'project',
  projectKey: string | undefined,
  servers: Record<string, unknown>,
  items: ImportPreviewItem[],
  payloads: Map<string, ItemPayload>
): void {
  for (const [name, raw] of Object.entries(servers)) {
    const mapped = mapMcpServer(name, raw)
    if (mapped === null) continue
    const scopeKey = projectKey ?? ''
    const key = `${scope}:${scopeKey}:${name}`
    const id = itemId('mcp', key)
    if (payloads.has(id)) continue // 同名同作用域只取一次(项目 .mcp.json 与 .claude.json 会重叠)

    const existing = store.getImportMapping(row.sourceId, scopeKey, 'mcp', name)
    const blocked = mapped.diagnostics.some(
      (d) => d.code === 'mcp.unsupported-transport' || d.code === 'mcp.missing-type'
    )
    items.push({
      id,
      category: 'mcp',
      title: name,
      sourcePath: scope === 'global' ? CLAUDE_LAYOUT.globalConfig : join(scopeKey, CLAUDE_LAYOUT.projectMcp),
      status: blocked ? 'incompatible' : existing === undefined ? 'new' : 'update',
      ...(projectKey === undefined ? {} : { projectKey }),
      scope,
      diagnostics: mapped.diagnostics,
      defaultSelected: !blocked && existing === undefined
    })
    payloads.set(id, {
      kind: 'mcp',
      server: mapped,
      scope,
      ...(projectKey === undefined ? {} : { projectKey })
    })
  }
}

async function isDirectory(path: string): Promise<boolean> {
  try {
    return (await stat(path)).isDirectory()
  } catch {
    return false
  }
}

// ═══════════════════════════════════════════════════════════════
// 提交
// ═══════════════════════════════════════════════════════════════

export function jobStatusFor(sourceId: string): ImportJobStatus | null {
  return jobsBySource.get(sourceId)?.status ?? null
}

export function cancelImportJob(jobId: string): void {
  const job = jobsById.get(jobId)
  if (job === undefined) return
  // ★ 只置标志。真正停下来发生在**下一个单项提交边界** ——
  //   在半截里停会留下一个写了一半的会话。
  job.cancelled = true
  announce(job.status.sourceId, jobId)
}

export async function applyImport(request: ImportApplyRequest): Promise<ImportJobStatus> {
  const snapshot = snapshots.get(request.previewId)
  if (snapshot === undefined) throw new Error('预览已过期,请重新扫描')
  if (snapshot.expiresAt <= Date.now()) {
    snapshots.delete(request.previewId)
    throw new Error('预览已过期,请重新扫描')
  }

  const running = jobsBySource.get(snapshot.sourceId)
  if (running !== undefined && !isTerminal(running.status.phase)) {
    // 防双击:同一个 requestId 回同一个 job,不同的则明确拒绝(单飞)。
    if (running.requestId === request.requestId) return running.status
    throw new Error('该来源已有导入任务在运行')
  }

  // ★ 只接受快照内的 id。渲染层递一个不认识的 id 进来就当它不存在,不去猜。
  const selected = request.itemIds
    .map((id) => snapshot.items.find((item) => item.id === id))
    .filter((item): item is ImportPreviewItem => item !== undefined)
  if (selected.length === 0) throw new Error('没有可导入的项')

  const targets = new Map(request.workspaceTargets.map((t) => [t.projectKey, t.workspaceId] as const))
  const job = startJob(snapshot.sourceId, 'manual', selected.length, request.requestId)

  // 不 await —— 「导入过程中可关闭设置页,任务继续」。
  void runJob(job, snapshot, selected, targets).catch((err) => {
    job.status = {
      ...job.status,
      phase: 'failed',
      endedAt: Date.now(),
      diagnostics: [{ code: 'source.unreadable', detail: err instanceof Error ? err.message : String(err) }]
    }
    store.updateImportBatch(job.batchId, 'failed', countsRecord(job.status.counts), job.status.endedAt)
    announce(job.status.sourceId, job.status.jobId)
  })

  return job.status
}

function isTerminal(phase: ImportJobStatus['phase']): boolean {
  return phase === 'done' || phase === 'partial' || phase === 'failed' || phase === 'cancelled' || phase === 'interrupted'
}

function startJob(
  sourceId: string,
  trigger: 'manual' | 'auto',
  total: number,
  requestId: string
): JobState {
  const now = Date.now()
  const jobId = prefixedId('impjob')
  const batchId = prefixedId('impbatch')
  const job: JobState = {
    batchId,
    cancelled: false,
    requestId,
    status: {
      jobId,
      sourceId,
      trigger,
      phase: 'importing',
      done: 0,
      total,
      counts: { ...EMPTY_IMPORT_COUNTS },
      startedAt: now,
      diagnostics: []
    }
  }
  store.createImportBatch({
    id: batchId,
    sourceId,
    sourceKind: store.getImportSource(sourceId)?.kind ?? SOURCE_KIND,
    trigger,
    phase: 'importing',
    startedAt: now,
    counts: countsRecord(EMPTY_IMPORT_COUNTS)
  })
  jobsBySource.set(sourceId, job)
  jobsById.set(jobId, job)
  announce(sourceId, jobId)
  return job
}

async function runJob(
  job: JobState,
  snapshot: Snapshot,
  selected: readonly ImportPreviewItem[],
  targets: ReadonlyMap<string, string>
): Promise<void> {
  const stagingRoot = stagingDirFor(databaseDirectory(), job.status.jobId)
  const sessionIds = new Map<string, Set<string>>()
  let workspacesChanged = false

  try {
    // ★ 项目先行:聊天要落地必须先有工作区映射。
    const ordered = [...selected].sort((a, b) => order(a.category) - order(b.category))

    for (let index = 0; index < ordered.length; index += 1) {
      const item = ordered[index]
      if (item === undefined) continue
      if (job.cancelled) {
        recordItem(job, index, item, 'cancelled', [], undefined)
        job.status.counts.skipped += 1
        continue
      }

      job.status.currentTitle = item.title
      const payload = snapshot.payloads.get(item.id)
      if (payload === undefined) {
        recordItem(job, index, item, 'failed', [{ code: 'source.unreadable' }], undefined)
        job.status.counts.failed += 1
      } else {
        const outcome = await applyOne(snapshot, job, payload, item, targets, stagingRoot)
        recordItem(job, index, item, outcome.result, outcome.diagnostics, outcome.target)
        bumpCount(job.status.counts, outcome.result)
        if (outcome.target?.kind === 'workspace' && outcome.result !== 'skipped') workspacesChanged = true
        if (outcome.target?.kind === 'session' && outcome.target.workspaceId !== undefined) {
          const bucket = sessionIds.get(outcome.target.workspaceId) ?? new Set<string>()
          bucket.add(outcome.target.id)
          sessionIds.set(outcome.target.workspaceId, bucket)
        }
      }

      job.status.done = index + 1
      announce(job.status.sourceId, job.status.jobId)
    }

    const source = store.getImportSource(job.status.sourceId)
    if (source !== undefined && job.status.counts.imported + job.status.counts.updated > 0) {
      store.putImportSource({ ...source, lastSyncAt: Date.now(), updatedAt: Date.now() })
    }
  } finally {
    /*
      ★★ **收尾顺序有意义:先把副作用广播出去,最后才把阶段标成终态。**

      反过来写(先标 done、再在 finally 里广播)会留下一个真实的竞态窗口 ——
      `cleanupStaging` 是异步的,所以轮询 `imports:status` 的一方会先看到
      `done`,然后在广播到达**之前**去读工作区列表,读到的是旧的那份。
      症状是「导入完成了,可切换器里还是没有新项目」,刷新一下又有了。
      这不是测试的假象:任何按「等到 done 再读」写的调用点都会撞上它。
    */
    await cleanupStaging(stagingRoot)
    if (workspacesChanged) workspaceNotifier?.()
    notifySessions(sessionIds)

    const counts = job.status.counts
    const phase: ImportJobStatus['phase'] = job.cancelled
      ? 'cancelled'
      : counts.failed > 0 || counts.conflict > 0
        ? 'partial'
        : 'done'
    job.status = { ...job.status, phase, endedAt: Date.now() }
    store.updateImportBatch(job.batchId, phase, countsRecord(counts), job.status.endedAt)
    announce(job.status.sourceId, job.status.jobId)
  }
}

/** 类别提交顺序。项目 → 聊天 → 其余;聊天依赖项目产出的工作区。 */
function order(category: ImportCategory): number {
  if (category === 'project') return 0
  if (category === 'chat') return 1
  return 2
}

/**
 * `ImportCounts` → 落盘用的普通记录。
 *
 * ★ 逐字段列出来,不是 `as Record<string, number>` —— 断言会让「往
 * `ImportCounts` 加一个字段却忘了它不会被持久化」这件事完全无声,
 * 而这里少一个字段的表现是历史页上某一列永远显示 0。
 */
function countsRecord(counts: ImportCounts): Record<string, number> {
  return {
    imported: counts.imported,
    updated: counts.updated,
    skipped: counts.skipped,
    conflict: counts.conflict,
    failed: counts.failed,
    incompatible: counts.incompatible
  }
}

function bumpCount(counts: ImportCounts, result: ImportResultCode): void {  if (result === 'imported') counts.imported += 1
  else if (result === 'updated') counts.updated += 1
  else if (result === 'skipped' || result === 'cancelled') counts.skipped += 1
  else if (result === 'conflict') counts.conflict += 1
  else if (result === 'incompatible') counts.incompatible += 1
  else counts.failed += 1
}

function recordItem(
  job: JobState,
  seq: number,
  item: ImportPreviewItem,
  result: ImportResultCode,
  diagnostics: readonly ImportDiagnostic[],
  target: { kind: ImportTargetKind; id: string; workspaceId?: string } | undefined
): void {
  store.appendImportBatchItem({
    batchId: job.batchId,
    seq,
    category: item.category,
    title: item.title,
    sourcePath: item.sourcePath,
    result,
    ...(target === undefined ? {} : { targetKind: target.kind, targetId: target.id }),
    ...(target?.workspaceId === undefined ? {} : { targetWorkspaceId: target.workspaceId }),
    diagnostics: [...diagnostics]
  })
}

/**
 * 按工作区合并一次 `sessions:changed`。
 *
 * ★ 不逐条广播:一次导入一百个会话就是一百次全应用事件,每一次都会让
 * 侧边栏重新拉一遍列表。合并成每工作区一条,列表刷新一次就够了。
 */
let sessionNotifier: ((workspaceId: string, sessionIds: string[]) => void) | null = null

export function setImportSessionNotifier(fn: (workspaceId: string, sessionIds: string[]) => void): void {
  sessionNotifier = fn
}

/**
 * 导入建了工作区 → 广播一次 `workspace:changed`。
 *
 * ★★ 少这一跳的表现很具体:导入报告说「新增了项目」,而左上角那个工作区切换器
 * 里**没有它** —— 因为渲染层那份工作区列表是靠这个事件更新的(`ipc/workspace.ts`
 * 的 `pickWorkspace` 每次建完都会 announce 一次)。写进了库但没人知道,
 * 用户要重启应用才看得见,而重启之后它又确实在,于是看起来像「时好时坏」。
 *
 * ★ 整个 job 结束后**只发一次**,不是每建一个工作区发一次:那个事件带着
 * 全量工作区列表,发 N 次就是 N 次全量重算。
 */
let workspaceNotifier: (() => void) | null = null

export function setImportWorkspaceNotifier(fn: () => void): void {
  workspaceNotifier = fn
}

function notifySessions(map: ReadonlyMap<string, Set<string>>): void {
  for (const [workspaceId, ids] of map) sessionNotifier?.(workspaceId, [...ids])
}

interface ApplyOutcome {
  result: ImportResultCode
  diagnostics: ImportDiagnostic[]
  target?: { kind: ImportTargetKind; id: string; workspaceId?: string }
}

async function applyOne(
  snapshot: Snapshot,
  job: JobState,
  payload: ItemPayload,
  item: ImportPreviewItem,
  targets: ReadonlyMap<string, string>,
  stagingRoot: string
): Promise<ApplyOutcome> {
  try {
    switch (payload.kind) {
      case 'project':
        return applyProject(snapshot.sourceId, payload, targets)
      case 'chat':
        return await applyChat(snapshot.sourceId, payload, targets)
      case 'skill':
      case 'agent':
      case 'command':
        return await applyAsset(snapshot.sourceId, payload, item, stagingRoot, job)
      case 'instructions':
        return await applyInstructions(snapshot.sourceId, payload, targets, stagingRoot)
      case 'mcp':
        return applyMcp(snapshot.sourceId, payload, targets, item)
      case 'provider':
        return applyCodexProvider(snapshot.sourceId, payload, item)
      case 'hook':
        return applyCodexHook(snapshot.sourceId, payload, item, targets)
      case 'codex-skill':
        return await applyCodexSkill(snapshot.sourceId, payload, item, stagingRoot, targets)
      case 'codex-chat':
        return await applyCodexChat(snapshot.sourceId, payload, targets)
      case 'opencode-chat':
        return await applyOpencodeChat(snapshot.sourceId, payload, targets)
      case 'opencode-provider':
        return applyOpencodeProvider(snapshot.sourceId, payload, item)
      case 'opencowork-chat':
        return await applyOpencoworkChat(snapshot.sourceId, payload, targets)
      case 'opencowork-provider':
        return applyOpencoworkProvider(snapshot.sourceId, payload, item)
      case 'opencowork-hook':
        return await applyOpencoworkHook(snapshot.sourceId, payload, item)
    }
  } catch (err) {
    return {
      result: 'failed',
      diagnostics: [{ code: 'source.unreadable', detail: err instanceof Error ? err.message : String(err) }]
    }
  }
}

function applyCodexProvider(
  sourceId: string,
  payload: Extract<ItemPayload, { kind: 'provider' }>,
  item: ImportPreviewItem
): ApplyOutcome {
  const mapped = mapCodexProvider(sourceId, payload.provider)
  const sourceItemId = `provider:${payload.provider.profile}:${payload.provider.id}`
  const now = Date.now()
  const existing = store.listProviders().find((candidate) => candidate.id === mapped.provider.id)
  const mapping = store.getImportMapping(sourceId, '', 'provider', sourceItemId)
  const diagnostics = [...item.diagnostics]
  const status = codexStatus(mapping, payload.provider.fingerprint, existing ? providerSurface(existing) : null, diagnostics)
  if (status === 'conflict') return { result: 'conflict', diagnostics }
  if (existing && !mapping) return { result: 'skipped', diagnostics }
  const aliasKey = mapped.alias ? `${sourceItemId}:${mapped.alias.alias}` : undefined
  const existingAlias = mapped.alias ? store.listAliases().find((candidate) => candidate.providerId === mapped.provider.id && candidate.alias === mapped.alias?.alias) : undefined
  if (mapped.alias && aliasKey) {
    const aliasMapping = store.getImportMapping(sourceId, '', 'alias', aliasKey)
    const aliasStatus = codexStatus(aliasMapping, aliasSurface(mapped.alias), existingAlias ? aliasSurface(existingAlias) : null, diagnostics)
    if (aliasStatus === 'conflict') return { result: 'conflict', diagnostics }
    if (existingAlias && !aliasMapping) return { result: 'skipped', diagnostics }
  }
  store.tx(() => {
    const saved = store.putProvider(existing === undefined ? mapped.provider : { ...mapped.provider, credentialRef: existing.credentialRef, enabled: existing.enabled })
    if (mapped.alias && aliasKey) {
      const alias = store.putAlias({ ...mapped.alias, enabled: existingAlias?.enabled ?? false })
      writeMapping(sourceId, '', 'alias', aliasKey, {
        targetId: `${mapped.provider.id}/${mapped.alias.alias}`, targetPath: '', targetWorkspaceId: '',
        sourceFingerprint: aliasSurface(mapped.alias), targetFingerprint: aliasSurface(alias), now
      })
    }
    writeMapping(sourceId, '', 'provider', sourceItemId, {
      targetId: mapped.provider.id, targetPath: payload.provider.sourcePath, targetWorkspaceId: '',
      sourceFingerprint: payload.provider.fingerprint, targetFingerprint: providerSurface(saved), now,
      meta: { profile: payload.provider.profile, codexProviderId: payload.provider.id, envKey: payload.provider.envKey ?? '', requiresOpenaiAuth: payload.provider.requiresOpenaiAuth ?? false }
    })
  })
  return { result: existing === undefined ? 'imported' : 'updated', diagnostics: item.diagnostics, target: { kind: 'provider', id: mapped.provider.id } }
}

async function applyCodexHook(
  sourceId: string,
  payload: Extract<ItemPayload, { kind: 'hook' }>,
  item: ImportPreviewItem,
  targets: ReadonlyMap<string, string>
): Promise<ApplyOutcome> {
  const mapped = mapCodexHook(sourceId, payload.hook)
  if (!mapped.hook) return { result: 'incompatible', diagnostics: mapped.diagnostics }
  const workspaceId = payload.hook.scope === 'project' ? resolveWorkspace(sourceId, payload.hook.projectKey ?? '', targets) : undefined
  if (payload.hook.scope === 'project' && workspaceId === null) return { result: 'skipped', diagnostics: [{ code: 'project.needs-workspace' }] }
  const workspace = workspaceId ? store.getWorkspace(workspaceId) : undefined
  const path = payload.hook.scope === 'global' ? globalSettingsPath(getHost().paths.userData()) : localSettingsPath(workspace?.rootPath ?? payload.hook.projectKey ?? '')
  const sourceItemId = payload.hook.sourceKey
  const scopeKey = payload.hook.projectKey ?? ''
  const mapping = store.getImportMapping(sourceId, scopeKey, 'hook', sourceItemId)
  const existing = await targetHook(path, mapping?.targetId ?? mapped.hook.id, payload.hook.scope)
  const surface = fingerprint(JSON.stringify(payload.hook))
  const diagnostics = [...mapped.diagnostics]
  const status = codexStatus(mapping, surface, existing ? hookSurface(existing) : null, diagnostics)
  if (status === 'conflict') return { result: 'conflict', diagnostics }
  if (existing && !mapping) return { result: 'skipped', diagnostics }
  if (existing && mapping && existing.enabled && mapping.sourceFingerprint !== surface) {
    return { result: 'conflict', diagnostics: [...mapped.diagnostics, { code: 'target.locally-modified' }] }
  }
  const { saveHook } = await import('../ipc/hooks')
  const saved = await saveHook({ scope: payload.hook.scope, ...(workspaceId ? { workspaceId } : {}), hook: { ...mapped.hook, enabled: existing?.enabled ?? false } })
  const now = Date.now()
  const targetSurface = hookSurface(saved)
  writeMapping(sourceId, scopeKey, 'hook', sourceItemId, { targetId: saved.id, targetPath: saved.sourcePath, targetWorkspaceId: workspaceId ?? '', sourceFingerprint: surface, targetFingerprint: targetSurface, now })
  return { result: existing ? 'updated' : 'imported', diagnostics: item.diagnostics, target: { kind: 'hook', id: saved.id, ...(workspaceId ? { workspaceId } : {}) } }
}

async function applyCodexSkill(sourceId: string, payload: Extract<ItemPayload, { kind: 'codex-skill' }>, item: ImportPreviewItem, stagingRoot: string, targets: ReadonlyMap<string, string>): Promise<ApplyOutcome> {
  const scopeKey = payload.skill.projectKey ?? ''
  const workspaceId = scopeKey ? resolveWorkspace(sourceId, scopeKey, targets) : null
  const workspace = workspaceId ? store.getWorkspace(workspaceId) : undefined
  if (scopeKey && workspace === undefined) return { result: 'skipped', diagnostics: [{ code: 'project.needs-workspace', detail: scopeKey }] }
  const targetPath = workspace ? join(workspace.rootPath, '.next-cowork', 'skills', payload.skill.name) : join(databaseDirectory(), 'skills', payload.skill.name)
  const sourceItemId = `skill:${payload.skill.scope}:${payload.skill.projectKey ?? ''}:${payload.skill.name}`
  const mapping = store.getImportMapping(sourceId, scopeKey, 'skill', sourceItemId)
  const existing = await currentFingerprint(targetPath)
  const diagnostics = [...item.diagnostics]
  if (codexStatus(mapping, payload.skill.fingerprint, existing, diagnostics) === 'conflict') return { result: 'conflict', diagnostics }
  if (existing !== null && mapping === undefined) return { result: 'skipped', diagnostics: [{ code: 'target.name-conflict', detail: payload.skill.name }] }
  if (existing !== null && mapping && existing !== mapping.targetFingerprint) return { result: 'conflict', diagnostics: [{ code: 'target.locally-modified', detail: payload.skill.name }] }
  const published = await publishSkillPackage({ sourceDir: payload.skill.dir, targetDir: targetPath, stagingDir: stagingRoot, expectBaseline: existing === null ? null : mapping?.targetFingerprint ?? null })
  if (!published.ok) return { result: 'conflict', diagnostics: published.diagnostics }
  const now = Date.now()
  writeMapping(sourceId, scopeKey, 'skill', sourceItemId, { targetId: payload.skill.name, targetPath, targetWorkspaceId: workspace?.id ?? '', sourceFingerprint: published.fingerprint, targetFingerprint: published.fingerprint, now })
  if (payload.skill.disabled) {
    if (workspace) {
      store.putWorkspace({ ...workspace, settings: { ...workspace.settings, activeSkillIds: workspace.settings.activeSkillIds.filter((id) => id !== payload.skill.skillId), skillSelectionMode: 'explicit' } })
    } else {
      store.setSkillGlobalEnabled(payload.skill.skillId, false)
    }
  }
  return { result: existing === null ? 'imported' : 'updated', diagnostics: item.diagnostics, target: { kind: 'skill', id: payload.skill.name, ...(workspace ? { workspaceId: workspace.id } : {}) } }
}

async function applyCodexChat(sourceId: string, payload: Extract<ItemPayload, { kind: 'codex-chat' }>, targets: ReadonlyMap<string, string>): Promise<ApplyOutcome> {
  const read = await readTranscriptLines(payload.file.path)
  const parsed = parseCodexTranscript(read.lines, { fallbackSessionId: payload.file.sessionId, maxMessages: IMPORT_LIMITS.maxMessagesPerSession })
  const workspaceId = resolveWorkspace(sourceId, parsed.cwd, targets)
  if (workspaceId === null) return { result: 'skipped', diagnostics: [{ code: 'project.needs-workspace', detail: parsed.cwd }] }
  const sourceItemId = `session:${payload.file.sessionId}`
  const mapping = store.getImportMapping(sourceId, '', 'session', sourceItemId)
  if (mapping?.syncState === 'detached') return { result: 'skipped', diagnostics: [{ code: 'target.detached' }] }
  if (mapping?.syncState === 'suppressed') return { result: 'skipped', diagnostics: [{ code: 'target.deleted' }] }
  const targetSessionId = mapping?.targetId || derivedId('cs', sourceId, payload.file.sessionId)
  const messages = await materializeMessages(parsed.messages, sourceId, payload.file.sessionId, targetSessionId)
  const now = Date.now()
  /*
    ★ 指纹用**这一趟重读**算出来的,不是 `payload.contentHash`(预览时算的)。
    两个理由:预览可能是五分钟前的,源侧还在写;而预览为了快,对没导过的会话
    只读头部、根本不算哈希(见 `readChatMeta`),那时 payload 里是空串 ——
    空指纹存进映射,下次扫描就会把这条会话永远显示成「有更新」。
  */
  const contentHash = hashTranscript(parsed)
  const workspace = store.getWorkspace(workspaceId)
  const providerId = parsed.modelProvider
    ? store.listImportMappings(sourceId, 'provider').find((mapping) => mapping.meta['codexProviderId'] === parsed.modelProvider)?.targetId
      ?? store.listProviders().find((provider) => provider.name === parsed.modelProvider || provider.id === parsed.modelProvider)?.id
    : undefined
  const providerDiagnostics = parsed.modelProvider && providerId === undefined
    ? [{ code: 'model-provider-unresolved' as const, detail: parsed.modelProvider }]
    : []
  store.tx(() => {
    store.ensureSession({ id: targetSessionId, workspaceId, title: parsed.title ?? payload.file.sessionId, model: parsed.model ?? '', ...(providerId ? { modelProviderId: providerId } : {}), rootPathAtCreation: workspace?.rootPath ?? parsed.cwd, createdAt: parsed.startedAt ?? now })
    store.replaceHistory(targetSessionId, messages)
    if (payload.archived) store.setSessionArchived(targetSessionId, true)
    writeMapping(sourceId, '', 'session', sourceItemId, { targetId: targetSessionId, targetPath: payload.file.path, targetWorkspaceId: workspaceId, sourceFingerprint: contentHash, targetFingerprint: contentHash, now, meta: { cwd: parsed.cwd, archived: payload.archived, messages: messages.length } })
  })
  return { result: mapping ? 'updated' : 'imported', diagnostics: [...read.diagnostics, ...parsed.diagnostics, ...providerDiagnostics], target: { kind: 'session', id: targetSessionId, workspaceId } }
}

async function applyOpencodeChat(sourceId: string, payload: Extract<ItemPayload, { kind: 'opencode-chat' }>, targets: ReadonlyMap<string, string>): Promise<ApplyOutcome> {
  const source = store.getImportSource(sourceId)
  const raw = source ? await readOpencodeSession(source.configDir, payload.sessionId) : null
  if (!raw) return { result: 'failed', diagnostics: [{ code: 'source.unreadable', detail: payload.sessionId }] }
  const parsed = parseOpencodeSession(raw, { maxMessages: IMPORT_LIMITS.maxMessagesPerSession })
  const workspaceId = resolveWorkspace(sourceId, parsed.cwd, targets)
  if (workspaceId === null) return { result: 'skipped', diagnostics: [{ code: 'project.needs-workspace', detail: parsed.cwd }] }
  const sourceItemId = `session:${payload.sessionId}`
  const mapping = store.getImportMapping(sourceId, '', 'session', sourceItemId)
  if (mapping?.syncState === 'detached') return { result: 'skipped', diagnostics: [{ code: 'target.detached' }] }
  if (mapping?.syncState === 'suppressed') return { result: 'skipped', diagnostics: [{ code: 'target.deleted' }] }
  if (parsed.messages.length === 0) return { result: 'incompatible', diagnostics: [...parsed.diagnostics, { code: 'transcript.empty' }] }
  const contentHash = hashTranscript(parsed)
  const targetSessionId = mapping?.targetId || derivedId('cs', sourceId, payload.sessionId)
  if (mapping !== undefined && mapping.sourceFingerprint === contentHash && store.sessionExists(targetSessionId)) {
    return { result: 'skipped', diagnostics: parsed.diagnostics, target: { kind: 'session', id: targetSessionId, workspaceId } }
  }
  const messages = await materializeMessages(parsed.messages, sourceId, payload.sessionId, targetSessionId)
  const now = Date.now()
  const workspace = store.getWorkspace(workspaceId)
  const providerId = parsed.modelProvider
    ? store.listImportMappings(sourceId, 'provider').find((entry) => entry.meta['opencodeProviderId'] === parsed.modelProvider)?.targetId
      ?? store.listProviders().find((provider) => provider.name === parsed.modelProvider || provider.id === parsed.modelProvider)?.id
    : undefined
  const providerDiagnostics = parsed.modelProvider && providerId === undefined
    ? [{ code: 'model-provider-unresolved' as const, detail: parsed.modelProvider }]
    : []
  store.tx(() => {
    store.ensureSession({ id: targetSessionId, workspaceId, title: parsed.title ?? payload.sessionId, model: parsed.model ?? '', ...(providerId ? { modelProviderId: providerId } : {}), rootPathAtCreation: workspace?.rootPath ?? parsed.cwd, createdAt: parsed.startedAt ?? now })
    store.replaceHistory(targetSessionId, messages)
    writeMapping(sourceId, '', 'session', sourceItemId, { targetId: targetSessionId, targetPath: `opencode.db#${payload.sessionId}`, targetWorkspaceId: workspaceId, sourceFingerprint: contentHash, targetFingerprint: contentHash, now, meta: { cwd: parsed.cwd, messages: messages.length } })
  })
  return { result: mapping ? 'updated' : 'imported', diagnostics: [...parsed.diagnostics, ...providerDiagnostics], target: { kind: 'session', id: targetSessionId, workspaceId } }
}

function applyOpencodeProvider(sourceId: string, payload: Extract<ItemPayload, { kind: 'opencode-provider' }>, item: ImportPreviewItem): ApplyOutcome {
  const mapped = mapOpencodeProvider(sourceId, payload.provider)
  const sourceItemId = `provider:${payload.provider.id}`
  const now = Date.now()
  const existing = store.listProviders().find((candidate) => candidate.id === mapped.provider.id)
  const mapping = store.getImportMapping(sourceId, '', 'provider', sourceItemId)
  const diagnostics = [...item.diagnostics]
  const status = codexStatus(mapping, payload.provider.fingerprint, existing ? providerSurface(existing) : null, diagnostics)
  if (status === 'conflict') return { result: 'conflict', diagnostics }
  if (existing && !mapping) return { result: 'skipped', diagnostics }
  const aliasKey = mapped.alias ? `${sourceItemId}:${mapped.alias.alias}` : undefined
  const existingAlias = mapped.alias ? store.listAliases().find((candidate) => candidate.providerId === mapped.provider.id && candidate.alias === mapped.alias?.alias) : undefined
  if (mapped.alias && aliasKey) {
    const aliasMapping = store.getImportMapping(sourceId, '', 'alias', aliasKey)
    const aliasStatus = codexStatus(aliasMapping, aliasSurface(mapped.alias), existingAlias ? aliasSurface(existingAlias) : null, diagnostics)
    if (aliasStatus === 'conflict') return { result: 'conflict', diagnostics }
    if (existingAlias && !aliasMapping) return { result: 'skipped', diagnostics }
  }
  store.tx(() => {
    const saved = store.putProvider(existing === undefined ? mapped.provider : { ...mapped.provider, credentialRef: existing.credentialRef, enabled: existing.enabled })
    if (mapped.alias && aliasKey) {
      const alias = store.putAlias({ ...mapped.alias, enabled: existingAlias?.enabled ?? false })
      writeMapping(sourceId, '', 'alias', aliasKey, {
        targetId: `${mapped.provider.id}/${mapped.alias.alias}`, targetPath: '', targetWorkspaceId: '',
        sourceFingerprint: aliasSurface(mapped.alias), targetFingerprint: aliasSurface(alias), now
      })
    }
    writeMapping(sourceId, '', 'provider', sourceItemId, {
      targetId: mapped.provider.id, targetPath: payload.provider.sourcePath, targetWorkspaceId: '',
      sourceFingerprint: payload.provider.fingerprint, targetFingerprint: providerSurface(saved), now,
      meta: { profile: 'default', opencodeProviderId: payload.provider.id }
    })
  })
  return { result: existing === undefined ? 'imported' : 'updated', diagnostics: item.diagnostics, target: { kind: 'provider', id: mapped.provider.id } }
}

async function applyOpencoworkChat(sourceId: string, payload: Extract<ItemPayload, { kind: 'opencowork-chat' }>, targets: ReadonlyMap<string, string>): Promise<ApplyOutcome> {
  const source = store.getImportSource(sourceId)
  const raw = source ? await readOpencoworkSession(source.configDir, payload.sessionId) : null
  if (!raw) return { result: 'failed', diagnostics: [{ code: 'source.unreadable', detail: payload.sessionId }] }
  const parsed = parseOpencoworkSession(raw, { maxMessages: IMPORT_LIMITS.maxMessagesPerSession })
  const workspaceId = resolveWorkspace(sourceId, parsed.cwd, targets)
  if (workspaceId === null) return { result: 'skipped', diagnostics: [{ code: 'project.needs-workspace', detail: parsed.cwd }] }
  const sourceItemId = `session:${payload.sessionId}`
  const mapping = store.getImportMapping(sourceId, '', 'session', sourceItemId)
  if (mapping?.syncState === 'detached') return { result: 'skipped', diagnostics: [{ code: 'target.detached' }] }
  if (mapping?.syncState === 'suppressed') return { result: 'skipped', diagnostics: [{ code: 'target.deleted' }] }
  if (parsed.messages.length === 0) return { result: 'incompatible', diagnostics: [...parsed.diagnostics, { code: 'transcript.empty' }] }
  const contentHash = hashTranscript(parsed)
  const targetSessionId = mapping?.targetId || derivedId('cs', sourceId, payload.sessionId)
  if (mapping !== undefined && mapping.sourceFingerprint === contentHash && store.sessionExists(targetSessionId)) {
    return { result: 'skipped', diagnostics: parsed.diagnostics, target: { kind: 'session', id: targetSessionId, workspaceId } }
  }
  const messages = await materializeMessages(parsed.messages, sourceId, payload.sessionId, targetSessionId)
  const now = Date.now()
  const workspace = store.getWorkspace(workspaceId)
  const providerId = parsed.modelProvider
    ? store.listImportMappings(sourceId, 'provider').find((entry) => entry.meta['opencoworkProviderId'] === parsed.modelProvider)?.targetId
      ?? store.listProviders().find((provider) => provider.name === parsed.modelProvider || provider.id === parsed.modelProvider)?.id
    : undefined
  const providerDiagnostics = parsed.modelProvider && providerId === undefined
    ? [{ code: 'model-provider-unresolved' as const, detail: parsed.modelProvider }]
    : []
  store.tx(() => {
    store.ensureSession({ id: targetSessionId, workspaceId, title: parsed.title ?? payload.sessionId, model: parsed.model ?? '', ...(providerId ? { modelProviderId: providerId } : {}), rootPathAtCreation: workspace?.rootPath ?? parsed.cwd, createdAt: parsed.startedAt ?? now })
    store.replaceHistory(targetSessionId, messages)
    writeMapping(sourceId, '', 'session', sourceItemId, { targetId: targetSessionId, targetPath: `data.db#${payload.sessionId}`, targetWorkspaceId: workspaceId, sourceFingerprint: contentHash, targetFingerprint: contentHash, now, meta: { cwd: parsed.cwd, messages: messages.length } })
  })
  return { result: mapping ? 'updated' : 'imported', diagnostics: [...parsed.diagnostics, ...providerDiagnostics], target: { kind: 'session', id: targetSessionId, workspaceId } }
}

function applyOpencoworkProvider(sourceId: string, payload: Extract<ItemPayload, { kind: 'opencowork-provider' }>, item: ImportPreviewItem): ApplyOutcome {
  const mapped = mapOpencoworkProvider(sourceId, payload.provider)
  const sourceItemId = `provider:${payload.provider.id}`
  const now = Date.now()
  const existing = store.listProviders().find((candidate) => candidate.id === mapped.provider.id)
  const mapping = store.getImportMapping(sourceId, '', 'provider', sourceItemId)
  const diagnostics = [...item.diagnostics]
  const status = codexStatus(mapping, payload.provider.fingerprint, existing ? providerSurface(existing) : null, diagnostics)
  if (status === 'conflict') return { result: 'conflict', diagnostics }
  if (existing && !mapping) return { result: 'skipped', diagnostics }
  const aliasKey = mapped.alias ? `${sourceItemId}:${mapped.alias.alias}` : undefined
  const existingAlias = mapped.alias ? store.listAliases().find((candidate) => candidate.providerId === mapped.provider.id && candidate.alias === mapped.alias?.alias) : undefined
  if (mapped.alias && aliasKey) {
    const aliasMapping = store.getImportMapping(sourceId, '', 'alias', aliasKey)
    const aliasStatus = codexStatus(aliasMapping, aliasSurface(mapped.alias), existingAlias ? aliasSurface(existingAlias) : null, diagnostics)
    if (aliasStatus === 'conflict') return { result: 'conflict', diagnostics }
    if (existingAlias && !aliasMapping) return { result: 'skipped', diagnostics }
  }
  store.tx(() => {
    const saved = store.putProvider(existing === undefined ? mapped.provider : { ...mapped.provider, credentialRef: existing.credentialRef, enabled: existing.enabled })
    if (mapped.alias && aliasKey) {
      const alias = store.putAlias({ ...mapped.alias, enabled: existingAlias?.enabled ?? false })
      writeMapping(sourceId, '', 'alias', aliasKey, {
        targetId: `${mapped.provider.id}/${mapped.alias.alias}`, targetPath: '', targetWorkspaceId: '',
        sourceFingerprint: aliasSurface(mapped.alias), targetFingerprint: aliasSurface(alias), now
      })
    }
    writeMapping(sourceId, '', 'provider', sourceItemId, {
      targetId: mapped.provider.id, targetPath: payload.provider.sourcePath, targetWorkspaceId: '',
      sourceFingerprint: payload.provider.fingerprint, targetFingerprint: providerSurface(saved), now,
      meta: { profile: 'default', opencoworkProviderId: payload.provider.id }
    })
  })
  return { result: existing === undefined ? 'imported' : 'updated', diagnostics: item.diagnostics, target: { kind: 'provider', id: mapped.provider.id } }
}

/** OpenCoWork 的 hook 全局唯一(没有项目级 hooks.json),所以比 `applyCodexHook` 少一层 workspace 判定。 */
async function applyOpencoworkHook(sourceId: string, payload: Extract<ItemPayload, { kind: 'opencowork-hook' }>, item: ImportPreviewItem): Promise<ApplyOutcome> {
  const mapped = mapOpencoworkHook(sourceId, payload.hook)
  if (!mapped.hook) return { result: 'incompatible', diagnostics: mapped.diagnostics }
  const path = globalSettingsPath(getHost().paths.userData())
  const sourceItemId = payload.hook.sourceKey
  const mapping = store.getImportMapping(sourceId, '', 'hook', sourceItemId)
  const existing = await targetHook(path, mapping?.targetId ?? mapped.hook.id, 'global')
  const surface = fingerprint(JSON.stringify(payload.hook))
  const diagnostics = [...mapped.diagnostics]
  const status = codexStatus(mapping, surface, existing ? hookSurface(existing) : null, diagnostics)
  if (status === 'conflict') return { result: 'conflict', diagnostics }
  if (existing && !mapping) return { result: 'skipped', diagnostics }
  if (existing && mapping && existing.enabled && mapping.sourceFingerprint !== surface) {
    return { result: 'conflict', diagnostics: [...mapped.diagnostics, { code: 'target.locally-modified' }] }
  }
  const { saveHook } = await import('../ipc/hooks')
  const saved = await saveHook({ scope: 'global', hook: { ...mapped.hook, enabled: existing?.enabled ?? false } })
  const now = Date.now()
  const targetSurface = hookSurface(saved)
  writeMapping(sourceId, '', 'hook', sourceItemId, { targetId: saved.id, targetPath: saved.sourcePath, targetWorkspaceId: '', sourceFingerprint: surface, targetFingerprint: targetSurface, now })
  return { result: existing ? 'updated' : 'imported', diagnostics: item.diagnostics, target: { kind: 'hook', id: saved.id } }
}

// ─── 项目 ───

/**
 * 建立或复用工作区。★ 只建映射,**不复制仓库、不建 worktree** ——
 * 「导入项目」的含义是「让这些聊天有个落脚的工作区」,不是把代码搬一份。
 */
function applyProject(
  sourceId: string,
  payload: Extract<ItemPayload, { kind: 'project' }>,
  targets: ReadonlyMap<string, string>
): ApplyOutcome {
  const projectKey = canonicalRoot(payload.projectKey) || payload.projectKey
  const rootPath = canonicalRoot(payload.sourcePath) || payload.sourcePath
  const explicit = targets.get(projectKey) ?? targets.get(payload.projectKey)
  const now = Date.now()

  if (explicit !== undefined) {
    const workspace = store.getWorkspace(explicit)
    if (workspace === undefined) {
      return { result: 'failed', diagnostics: [{ code: 'project.needs-workspace', detail: payload.projectKey }] }
    }
    writeMapping(sourceId, '', 'workspace', projectKey, {
      targetId: workspace.id,
      targetPath: workspace.rootPath,
      targetWorkspaceId: workspace.id,
      sourceFingerprint: fingerprint(payload.sourcePath),
      now
    })
    return { result: 'updated', diagnostics: [], target: { kind: 'workspace', id: workspace.id } }
  }

  /*
    复用按**规范化路径**匹配的工作区,不按显示名 —— 两个不同目录可以同名。
    ★ 匹配时把库里那一行也归一化:存量工作区是按旧规则(原样 cwd / 目录选择器的
    native realpath)写进去的,只比字符串就会为同一个目录再建一个。
  */
  const existing = findLocalWorkspaceByRoot(rootPath)
  if (existing !== undefined) {
    writeMapping(sourceId, '', 'workspace', projectKey, {
      targetId: existing.id,
      targetPath: existing.rootPath,
      targetWorkspaceId: existing.id,
      sourceFingerprint: fingerprint(payload.sourcePath),
      now
    })
    return { result: 'skipped', diagnostics: [], target: { kind: 'workspace', id: existing.id } }
  }

  const created = store.putWorkspace({
    id: prefixedId('ws'),
    name: basename(rootPath) || rootPath,
    rootPath,
    settings: structuredClone(DEFAULT_WORKSPACE_SETTINGS),
    createdAt: now,
    lastOpenedAt: now
  })
  writeMapping(sourceId, '', 'workspace', projectKey, {
    targetId: created.id,
    targetPath: created.rootPath,
    targetWorkspaceId: created.id,
    sourceFingerprint: fingerprint(payload.sourcePath),
    now
  })
  return { result: 'imported', diagnostics: [], target: { kind: 'workspace', id: created.id } }
}

// ─── 聊天 ───

async function applyChat(
  sourceId: string,
  payload: Extract<ItemPayload, { kind: 'chat' }>,
  targets: ReadonlyMap<string, string>
): Promise<ApplyOutcome> {
  const sourceSessionId = payload.file.sessionId
  const mapping = store.getImportMapping(sourceId, '', 'session', sourceSessionId)

  // ★ 这三种状态**先于一切**检查。一旦续聊过,源侧后来的任何改动都不该落地。
  if (mapping?.syncState === 'detached') {
    return { result: 'skipped', diagnostics: [{ code: 'target.detached' }] }
  }
  if (mapping?.syncState === 'suppressed') {
    return { result: 'skipped', diagnostics: [{ code: 'target.deleted' }] }
  }

  const workspaceId = resolveWorkspace(sourceId, payload.projectKey, targets)
  if (workspaceId === null) {
    return { result: 'skipped', diagnostics: [{ code: 'project.needs-workspace', detail: payload.projectKey }] }
  }

  // ★ 提交前**重读一遍源**。预览可能是五分钟前扫的,而 Claude Code 还在写。
  const read = await readTranscriptLines(payload.file.path)
  const parsed = parseTranscript(read.lines, {
    fallbackSessionId: sourceSessionId,
    maxMessages: IMPORT_LIMITS.maxMessagesPerSession
  })
  const diagnostics = [...read.diagnostics, ...parsed.diagnostics]

  if (parsed.messages.length === 0) {
    return { result: 'incompatible', diagnostics: [...diagnostics, { code: 'transcript.empty' }] }
  }
  if (diagnostics.some((d) => d.code === 'transcript.branch-unresolvable')) {
    return { result: 'incompatible', diagnostics }
  }

  const contentHash = hashTranscript(parsed)
  const targetSessionId = mapping?.targetId !== undefined && mapping.targetId !== ''
    ? mapping.targetId
    : derivedId('cs', sourceId, sourceSessionId)

  if (mapping !== undefined && mapping.sourceFingerprint === contentHash && store.sessionExists(targetSessionId)) {
    return {
      result: 'skipped',
      diagnostics,
      target: { kind: 'session', id: targetSessionId, workspaceId }
    }
  }

  const workspace = store.getWorkspace(workspaceId)
  const now = Date.now()
  const fresh = !store.sessionExists(targetSessionId)

  // ★ 图片先落盘再进事务:附件写的是文件,包不进 SQLite 事务里。
  //   去重按 checksum,所以重复导入不会多占一份磁盘。
  const messages = await materializeMessages(parsed.messages, sourceId, sourceSessionId, targetSessionId)

  store.tx(() => {
    store.ensureSession({
      id: targetSessionId,
      workspaceId,
      title: parsed.title ?? '新对话',
      rootPathAtCreation: workspace?.rootPath ?? payload.projectKey,
      createdAt: parsed.startedAt ?? now
    })
    // 源侧标题变了要跟上,但**用户改过的本地标题归用户**(记了 titleOverridden)。
    const existing = store.getSession(targetSessionId)
    if (existing !== undefined && mapping?.meta['titleOverridden'] !== true && parsed.title !== undefined) {
      if (existing.title !== parsed.title) store.renameSession(targetSessionId, parsed.title)
    }
    store.replaceHistory(targetSessionId, messages)
    writeMapping(sourceId, '', 'session', sourceSessionId, {
      targetId: targetSessionId,
      targetPath: payload.file.path,
      targetWorkspaceId: workspaceId,
      sourceFingerprint: contentHash,
      targetFingerprint: contentHash,
      now,
      meta: {
        cwd: parsed.cwd,
        changeToken: `${String(payload.file.size)}:${String(Math.round(payload.file.mtimeMs))}`,
        messages: parsed.messages.length,
        ...(parsed.title === undefined ? {} : { title: parsed.title }),
        ...(parsed.model === undefined ? {} : { model: parsed.model }),
        ...(mapping?.meta['titleOverridden'] === true ? { titleOverridden: true } : {})
      }
    })
  })

  return {
    result: fresh ? 'imported' : 'updated',
    diagnostics,
    target: { kind: 'session', id: targetSessionId, workspaceId }
  }
}

/**
 * 已规范化的消息 → 可落盘的 `AgentMessage[]`,内联图片变成受管附件。
 *
 * ★ 本地 id 全部**推导**(见文件头),所以这个函数是幂等的:同一份源跑两遍
 * 得到逐字节相同的结果,`replaceHistory` 的增量对账因此什么都不会写。
 */
async function materializeMessages(
  source: readonly ImportedMessage[],
  sourceId: string,
  sourceSessionId: string,
  targetSessionId: string
): Promise<AgentMessage[]> {
  const { uploadAttachment } = await import('../services/attachments')
  const result: AgentMessage[] = []

  for (const message of source) {
    let parts: ContentPart[] = message.parts
    if (message.images.length > 0) {
      parts = [...message.parts]
      for (const image of message.images) {
        const part = parts[image.partIndex]
        if (part?.type !== 'image') continue
        try {
          const bytes = Uint8Array.from(Buffer.from(image.base64, 'base64')) as Uint8Array<ArrayBuffer>
          const attachment = uploadAttachment({
            scope: 'session',
            ownerId: targetSessionId,
            displayName: `imported-${String(image.partIndex)}`,
            mime: image.mime,
            bytes
          })
          parts[image.partIndex] = { type: 'image', mime: image.mime, dataRef: attachment.url }
        } catch {
          // 落不了盘就留一行可读说明,**不留一个指向空处的图片引用**。
          parts[image.partIndex] = { type: 'text', text: '[图片:导入失败]' }
        }
      }
    }
    result.push({
      id: derivedId('cm', sourceId, sourceSessionId, message.sourceId),
      role: message.role,
      parts,
      createdAt: message.createdAt,
      schemaVersion: 1
    })
  }
  return result
}

function resolveWorkspace(
  sourceId: string,
  projectKey: string,
  targets: ReadonlyMap<string, string>
): string | null {
  if (projectKey === '') return null
  /*
    ★ 调用方给的可能是原样的 `cwd`(见 applyCodexChat / applyOpencodeChat),
    而映射与 targets 的键都是扫描时归一化过的 —— 这里不归一化,就会明明有工作区
    却判成没有,聊天被整批跳过并报 `project.needs-workspace`。
  */
  const key = canonicalRoot(projectKey)
  if (key === '') return null
  const explicit = targets.get(key) ?? targets.get(projectKey)
  if (explicit !== undefined && store.getWorkspace(explicit) !== undefined) return explicit
  const mapped = store.getImportMapping(sourceId, '', 'workspace', key)
    ?? store.getImportMapping(sourceId, '', 'workspace', projectKey)
  if (mapped !== undefined && mapped.targetId !== '' && store.getWorkspace(mapped.targetId) !== undefined) {
    return mapped.targetId
  }
  return findLocalWorkspaceByRoot(key)?.id ?? null
}

// ─── 资产 ───

async function applyAsset(
  sourceId: string,
  payload: Extract<ItemPayload, { kind: 'skill' | 'agent' | 'command' }>,
  item: ImportPreviewItem,
  stagingRoot: string,
  _job: JobState
): Promise<ApplyOutcome> {
  const kind: ImportEntityKind = payload.kind
  const dir = payload.kind === 'skill' ? 'skills' : payload.kind === 'agent' ? 'agents' : 'commands'
  const targetPath = join(
    databaseDirectory(),
    dir,
    payload.kind === 'skill' ? payload.name : `${payload.name}.md`
  )
  const mapping = store.getImportMapping(sourceId, '', kind, payload.name)
  const now = Date.now()

  // ★ 目标已存在但不是我们导入的 → **默认跳过**,不覆盖。
  //   用户要两份可以显式另存新名,但那是用户的动作,不是默认策略。
  const existingFingerprint = await currentFingerprint(targetPath)
  if (existingFingerprint !== null && mapping === undefined) {
    return { result: 'skipped', diagnostics: [{ code: 'target.name-conflict', detail: payload.name }] }
  }
  if (existingFingerprint !== null && mapping !== undefined && existingFingerprint !== mapping.targetFingerprint) {
    return { result: 'conflict', diagnostics: [{ code: 'target.locally-modified', detail: payload.name }] }
  }

  const baseline = mapping === undefined ? null : mapping.targetFingerprint === '' ? null : mapping.targetFingerprint
  const published = payload.kind === 'skill'
    ? await publishSkillPackage({
        sourceDir: payload.dir,
        targetDir: targetPath,
        stagingDir: stagingRoot,
        expectBaseline: existingFingerprint === null ? null : baseline
      })
    : await publishMarkdownAsset(payload.file, targetPath, stagingRoot, existingFingerprint === null ? null : baseline)

  if (!published.ok) return { result: 'conflict', diagnostics: published.diagnostics }

  writeMapping(sourceId, '', kind, payload.name, {
    targetId: payload.name,
    targetPath,
    targetWorkspaceId: '',
    /*
      ★ 源指纹与目标指纹**是同一个值**,因为发布出去的就是源的逐字节副本。
      两者在这里合一不是偷懒:让它们分开取值反而会制造一种假象,好像
      我们对内容做过转换 —— 而资产这条路径上确实没有任何转换。
      (聊天那条路径不同,那边源是 JSONL、目标是规范化消息,两份指纹必须分开。)
    */
    sourceFingerprint: published.fingerprint,
    targetFingerprint: published.fingerprint,
    now
  })
  return {
    result: existingFingerprint === null ? 'imported' : 'updated',
    diagnostics: item.diagnostics,
    target: { kind: payload.kind, id: payload.name }
  }
}

async function publishMarkdownAsset(
  sourceFile: string,
  targetPath: string,
  stagingDir: string,
  expectBaseline: string | null
): Promise<{ ok: boolean; fingerprint: string; diagnostics: ImportDiagnostic[] }> {
  const text = await readTextBounded(sourceFile, 128 * 1024)
  if (text === null) {
    return { ok: false, fingerprint: '', diagnostics: [{ code: 'source.unreadable', detail: basename(sourceFile) }] }
  }
  return publishTextFile({ targetPath, content: text, stagingDir, expectBaseline })
}

// ─── 说明 ───

/**
 * 说明落成**受管副本**,不碰原生 `AGENTS.md`。
 *
 * ★ 两条都要:不覆盖本地的 `AGENTS.md`(那是用户自己写的),也不修改源
 * `CLAUDE.md`(只读承诺)。所以它去第三个位置:
 * `<userData>/imports/claude-code/<sourceId>/instructions/`,由
 * `loadManagedInstructions` 在组装时拼进去,且**排在原生规则之后** ——
 * 原生优先。
 */
async function applyInstructions(
  sourceId: string,
  payload: Extract<ItemPayload, { kind: 'instructions' }>,
  targets: ReadonlyMap<string, string>,
  stagingRoot: string
): Promise<ApplyOutcome> {
  const scopeKey = payload.scope === 'global'
    ? ''
    : (resolveWorkspace(sourceId, payload.projectKey ?? '', targets) ?? '')
  if (payload.scope === 'project' && scopeKey === '') {
    return { result: 'skipped', diagnostics: [{ code: 'project.needs-workspace' }] }
  }

  const targetPath = managedInstructionsPath(sourceId, scopeKey)
  const mappingKey = payload.scope === 'global' ? 'global' : (payload.projectKey ?? '')
  const mapping = store.getImportMapping(sourceId, scopeKey, 'instructions', mappingKey)
  const existingFingerprint = await currentFingerprint(targetPath)
  const now = Date.now()

  const published = await publishTextFile({
    targetPath,
    content: payload.text,
    stagingDir: stagingRoot,
    expectBaseline: existingFingerprint === null ? null : mapping?.targetFingerprint ?? null
  })
  if (!published.ok) return { result: 'conflict', diagnostics: published.diagnostics }

  writeMapping(sourceId, scopeKey, 'instructions', mappingKey, {
    targetId: mappingKey,
    targetPath,
    targetWorkspaceId: scopeKey,
    sourceFingerprint: published.fingerprint,
    targetFingerprint: published.fingerprint,
    now
  })
  return {
    result: existingFingerprint === null ? 'imported' : 'updated',
    diagnostics: [],
    target: { kind: 'instructions', id: mappingKey, ...(scopeKey === '' ? {} : { workspaceId: scopeKey }) }
  }
}

// `managedInstructionsPath` 由 `./managed-paths.ts` 提供,见该文件与上面那段再导出注释。

// ─── MCP ───

/**
 * ★ 三条铁律都在这十几行里:`enabled: false`、只搬键名、id 走本地稳定生成。
 * 其中 `enabled: false` 是**防执行边界** —— `runtime` 启动会
 * `connectEnabledInBackground`,导入一条 enabled 的配置等于替用户启动了一个进程。
 */
function applyMcp(
  sourceId: string,
  payload: Extract<ItemPayload, { kind: 'mcp' }>,
  targets: ReadonlyMap<string, string>,
  item: ImportPreviewItem
): ApplyOutcome {
  const server = payload.server
  if (item.status === 'incompatible') {
    return { result: 'incompatible', diagnostics: server.diagnostics }
  }

  const scopeKey = payload.scope === 'global'
    ? ''
    : (resolveWorkspace(sourceId, payload.projectKey ?? '', targets) ?? '')
  if (payload.scope === 'project' && scopeKey === '') {
    return { result: 'skipped', diagnostics: [{ code: 'project.needs-workspace' }] }
  }

  const mapping = store.getImportMapping(sourceId, scopeKey, 'mcp', server.sourceName)
  const existing = store.getMcpServer(server.id)
  const now = Date.now()

  if (existing !== undefined && mapping === undefined) {
    return { result: 'skipped', diagnostics: [{ code: 'target.name-conflict', detail: server.sourceName }] }
  }

  const surface = fingerprint(
    JSON.stringify([server.transport, server.command ?? '', server.args ?? [], server.url ?? '', server.cwd ?? ''])
  )
  if (existing !== undefined && mapping !== undefined && existing.enabled && mapping.sourceFingerprint !== surface) {
    // ★ 已启用的服务器,执行入口变了 → 进待确认,**不悄悄替换**。
    //   后台同步替掉一个正在用的服务器的 command,等于替用户换了一个要执行的程序。
    store.putImportMapping({ ...mapping, syncState: 'conflict', updatedAt: now })
    return { result: 'conflict', diagnostics: [{ code: 'mcp.command-changed', detail: server.sourceName }] }
  }

  const config: McpServerConfig = server.transport === 'stdio'
    ? {
        id: server.id,
        name: server.sourceName,
        enabled: false,
        ...(scopeKey === '' ? {} : { workspaceId: scopeKey }),
        transport: 'stdio',
        command: server.command ?? '',
        args: server.args ?? [],
        envNames: server.secretNames,
        ...(server.cwd === undefined ? {} : { cwd: server.cwd })
      }
    : {
        id: server.id,
        name: server.sourceName,
        // ★ 用户补过的 enabled 是**本地拥有字段**,源更新不能把它关回去。
        enabled: existing?.enabled ?? false,
        ...(scopeKey === '' ? {} : { workspaceId: scopeKey }),
        transport: server.transport,
        url: server.url ?? '',
        headerNames: server.secretNames
      }

  store.tx(() => {
    store.putMcpServer(
      existing === undefined ? { ...config, enabled: false } : { ...config, enabled: existing.enabled }
    )
    writeMapping(sourceId, scopeKey, 'mcp', server.sourceName, {
      targetId: server.id,
      targetPath: '',
      targetWorkspaceId: scopeKey,
      sourceFingerprint: surface,
      targetFingerprint: surface,
      now
    })
  })

  return {
    result: existing === undefined ? 'imported' : 'updated',
    diagnostics: server.diagnostics,
    target: { kind: 'mcp', id: server.id, ...(scopeKey === '' ? {} : { workspaceId: scopeKey }) }
  }
}

// ─── 映射写入 ───

function writeMapping(
  sourceId: string,
  scopeKey: string,
  entityKind: ImportEntityKind,
  sourceItemId: string,
  input: {
    targetId: string
    targetPath: string
    targetWorkspaceId: string
    sourceFingerprint: string
    targetFingerprint?: string
    now: number
    meta?: Record<string, unknown>
  }
): void {
  const existing = store.getImportMapping(sourceId, scopeKey, entityKind, sourceItemId)
  store.putImportMapping({
    sourceId,
    scopeKey,
    entityKind,
    sourceItemId,
    targetId: input.targetId,
    targetPath: input.targetPath,
    targetWorkspaceId: input.targetWorkspaceId,
    sourceFingerprint: input.sourceFingerprint,
    targetFingerprint: input.targetFingerprint ?? '',
    transformerVersion: TRANSFORMER_VERSION,
    // ★ 重新导入**不会**把 detached 拉回 linked —— 那正是脱离的含义。
    syncState: existing?.syncState === 'detached' ? 'detached' : 'linked',
    meta: { ...(existing?.meta ?? {}), ...(input.meta ?? {}) },
    createdAt: existing?.createdAt ?? input.now,
    updatedAt: input.now
  })
}

// ═══════════════════════════════════════════════════════════════
// 历史
// ═══════════════════════════════════════════════════════════════

export function importHistory(offset: number, limit: number): ImportHistoryPage {
  const page = store.listImportBatches(offset, Math.min(limit, IMPORT_LIMITS.pageSize))
  return {
    batches: page.rows.map((row) => ({
      id: row.id,
      sourceId: row.sourceId,
      sourceKind: row.sourceKind as ImportSourceState['detection']['kind'],
      trigger: row.trigger as 'manual' | 'auto',
      startedAt: row.startedAt,
      ...(row.endedAt === undefined ? {} : { endedAt: row.endedAt }),
      phase: row.phase as ImportJobStatus['phase'],
      counts: { ...EMPTY_IMPORT_COUNTS, ...row.counts }
    })),
    total: page.total,
    offset
  }
}

export function importHistoryItems(batchId: string, offset: number, limit: number): ImportBatchItemsPage {
  const page = store.listImportBatchItems(batchId, offset, Math.min(limit, IMPORT_LIMITS.pageSize))
  const items: ImportBatchItem[] = page.rows.map((row) => {
    // ★ 目标已被删除时标记不可打开,**不复活数据**。
    const missing = row.targetKind === 'session' && row.targetId !== undefined
      ? !store.sessionExists(row.targetId)
      : false
    return {
      batchId: row.batchId,
      category: row.category as ImportCategory,
      title: row.title,
      sourcePath: row.sourcePath,
      result: row.result as ImportResultCode,
      ...(row.targetKind === undefined ? {} : { targetKind: row.targetKind as ImportTargetKind }),
      ...(row.targetId === undefined ? {} : { targetId: row.targetId }),
      ...(row.targetWorkspaceId === undefined ? {} : { targetWorkspaceId: row.targetWorkspaceId }),
      ...(missing ? { targetMissing: true } : {}),
      diagnostics: row.diagnostics as ImportDiagnostic[]
    }
  })
  return { items, total: page.total, offset }
}

// ═══════════════════════════════════════════════════════════════
// 供同步器复用
// ═══════════════════════════════════════════════════════════════

export const __internal = {
  scanSource,
  startJob,
  runJob,
  snapshots,
  jobsBySource,
  isTerminal,
  announce,
  itemId
}

export type { Snapshot, ItemPayload }
export { resolve as resolvePath, realpath as realpathOf }
