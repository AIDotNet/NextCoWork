/**
 * 加密配置同步 v2 的 `usage` category 引擎(使用统计多设备合并)。
 *
 * 需求:使用统计概览合并同一账户下多台设备的数据;请求日志不跨设备。
 * 快照形状与合并规则见 `config-sync-usage-snapshot.ts`,远端数据落库见 `db/usage-remote.ts`。
 * 本模块只管「什么时候拉、什么时候推、冲突怎么解」;会话、密钥、网络由 `config-sync.ts`
 * 以 `UsageSyncContext` 注入 —— 不在这里复制一套会话管理。
 *
 * 与 providers 的三处不同,都是故意的:
 * 1. 顺序是 pull → 组装 → push。providers 先 push 是怕远端快照覆盖本地未上传的改动;
 *    而 usage 本机那片的真源是 `usage_daily`,远端快照永远改不到它,先拉反而少一次冲突。
 * 2. 冲突自动解:把冲突里带回的远端快照落地,再以它为底重新合并、重推。每片只有一个
 *    写者,所以不存在「保留本地 / 使用远端」—— 那会丢掉某一台设备的数据。
 * 3. 节流:每 `USAGE_INTERVAL_MS` 才跑一轮,不是每 5 秒。每次推送都是一整份快照,
 *    而统计不需要秒级新鲜度。
 *
 * 失败隔离:本步骤的网络 / 数据错误**不**上抛到同步面板的错误态,只打诊断日志并等下一轮。
 * 表现上的理由:统计同步出问题(比如服务端还没部署 usage category,整类 400)不该让
 * 供应商配置同步也挂着「同步失败」。会话级错误(换账户、设备被撤销、被锁)照常上抛。
 */
import type { z } from 'zod'
import {
  ConfigSyncError,
  syncPullSchema,
  syncPushSchema,
  type SyncCategory,
  type SyncErrorCode,
  type SyncEvent,
  type SyncVault
} from '../../shared/domain/config-sync'
import { decryptSyncDocument, encryptSyncDocument, syncDigest } from '../config-sync-crypto'
import {
  buildUsageSyncData,
  mergeUsageSlices,
  parseUsageSyncData,
  type UsageSlices
} from '../config-sync-usage-snapshot'
import {
  acknowledgeSyncOutgoing,
  emptySyncCategory,
  finishSyncEvent,
  mutateSyncState,
  readSyncState,
  sealSyncOutgoing,
  stageSyncEvent
} from '../db/config-sync-state'
import { clearRemoteUsage, listRemoteUsage, readLocalUsageSlice, replaceRemoteUsage } from '../db/usage-remote'

const USAGE: SyncCategory = 'usage'

/** 五分钟,与本机汇总表的刷新周期同档:更密只会推出同样的数字。 */
export const USAGE_INTERVAL_MS = 5 * 60 * 1000

/** 一轮里「冲突 → 落地远端 → 重新合并 → 重推」最多几次。两台设备恰好同时推才会冲突,3 次足够;
 *  封顶是为了在服务端异常(每次都回冲突)时不在一轮里无限打请求。 */
const MAX_MERGE_ATTEMPTS = 3

/** 会话级错误:整个同步会话已经不成立,必须交给外层,不能在这里吞掉。 */
const SESSION_ERRORS: ReadonlySet<SyncErrorCode> = new Set([
  'accountChanged', 'deviceRevoked', 'locked', 'signedOut', 'migrationRequired'
])

export interface UsageSyncContext {
  accountId: string
  /** 本机分片的键(`ensureSyncDeviceId()`)。为什么不用会话 deviceId 见快照模块文件头。 */
  originId: string
  vault: SyncVault
  /** 取一份 DEK 副本;★ 调用方用完必须 `fill(0)`。 */
  key: () => Buffer
  request: <T>(path: string, schema: z.ZodType<T>, body?: unknown) => Promise<T>
  assertCurrent: () => void
}

/** 下一轮最早什么时候跑。模块级:同一时刻只有一个同步会话,换会话时由 `resetUsageSyncSchedule` 归零。 */
let nextDueAt = 0

/** 让下一次 tick 立刻跑一轮(新会话、刚打开开关)。 */
export function resetUsageSyncSchedule(): void {
  nextDueAt = 0
}

/** 引擎每个 tick 调用;未到期、未确认或未开启时直接返回。 */
export async function runUsageSync(ctx: UsageSyncContext, now: number = Date.now()): Promise<void> {
  const state = readSyncState(ctx.accountId)
  if (!state.confirmed || !state.selection.usage || now < nextDueAt) return
  nextDueAt = now + USAGE_INTERVAL_MS
  try {
    await syncOnce(ctx)
  } catch (error) {
    if (error instanceof ConfigSyncError && SESSION_ERRORS.has(error.code)) throw error
    console.warn('[config-sync] 使用统计同步失败,下一轮重试:', error)
  }
}

/**
 * 开关。关掉时丢弃本机持有的远端数据与同步进度 —— 概览回到纯本机;
 * 重新打开时从 cursor 0 重拉(服务端只留最新一条,拉一次就是全量)。
 * 云端里本机那一片**不删**:那是真实发生过的用量,其它设备继续展示它。
 */
export function setUsageSyncEnabled(accountId: string, enabled: boolean): void {
  mutateSyncState(accountId, (state) => {
    state.selection.usage = enabled
    if (!enabled) state.categories.usage = emptySyncCategory()
  })
  if (!enabled) clearRemoteUsage(accountId)
  resetUsageSyncSchedule()
}

async function syncOnce(ctx: UsageSyncContext): Promise<void> {
  // 上一轮在「已暂存远端快照、还没落库」处中断(退出 / 崩溃),先补完
  const pending = readSyncState(ctx.accountId).categories.usage.pendingApply
  if (pending !== null) await applyUsageEvent(ctx, pending)
  // 上一轮封好但没收到响应的推送,先推掉(同 mutationId 重放在服务端幂等)
  await pushOutgoing(ctx)
  await pull(ctx)
  for (let attempt = 0; attempt < MAX_MERGE_ATTEMPTS; attempt++) {
    if (!prepare(ctx)) return
    if (await pushOutgoing(ctx)) return
  }
}

/** 远端快照解密、校验、落库。★ 先整份校验再动库,半份坏数据不会写进远端表。 */
async function applyUsageEvent(ctx: UsageSyncContext, event: SyncEvent): Promise<void> {
  const key = ctx.key()
  let slices: UsageSlices
  let fingerprint: string
  try {
    const document = decryptSyncDocument(key, ctx.vault, event)
    if (document.kind !== USAGE) throw new ConfigSyncError('invalidData')
    slices = parseUsageSyncData(document.data)
    fingerprint = syncDigest(key, document)
  } finally {
    key.fill(0)
  }
  ctx.assertCurrent()
  stageSyncEvent(ctx.accountId, event)
  replaceRemoteUsage(ctx.accountId, slices, ctx.originId)
  finishSyncEvent(ctx.accountId, USAGE, event.mutationId, fingerprint)
}

async function pull(ctx: UsageSyncContext): Promise<void> {
  for (;;) {
    const current = readSyncState(ctx.accountId).categories.usage
    const response = await ctx.request(
      `/pull?cursor=${current.cursor}&limit=100&kind=${USAGE}`,
      syncPullSchema
    )
    // 每条事件都是全量快照,一页里只有最新那条有意义
    let latest: SyncEvent | null = null
    for (const event of response.events) {
      if (event.cursor > current.cursor && (latest === null || event.cursor > latest.cursor)) latest = event
    }
    if (latest !== null) {
      if (latest.revision > current.revision) {
        await applyUsageEvent(ctx, latest)
      } else {
        // 自己刚推上去的那条(或过期事件):内容本机已有,只推进游标
        const cursor = latest.cursor
        mutateSyncState(ctx.accountId, (state) => {
          state.categories.usage.cursor = Math.max(state.categories.usage.cursor, cursor)
        })
      }
    }
    if (!response.hasMore) return
  }
}

/**
 * 组装「远端其它设备的片 + 本机最新一片」并封成待推送信封。返回 false = 与云端当前快照相同,不必推。
 *
 * ★ 与上一次已知的云端快照比指纹,而不是与本机上次上传的比:别的设备推过之后
 *   云端内容变了,本机这片即使没变也不需要重推 —— 合并结果与云端相同时指纹相等。
 */
function prepare(ctx: UsageSyncContext): boolean {
  const category = readSyncState(ctx.accountId).categories.usage
  if (category.outgoing !== null) return true
  const slices = mergeUsageSlices(listRemoteUsage(ctx.accountId), ctx.originId, readLocalUsageSlice())
  const document = { version: 2 as const, kind: USAGE, data: buildUsageSyncData(slices) }
  const key = ctx.key()
  try {
    const fingerprint = syncDigest(key, document)
    if (fingerprint === category.digest) return false
    const envelope = encryptSyncDocument(key, ctx.vault, document, category.revision)
    sealSyncOutgoing(ctx.accountId, USAGE, envelope, fingerprint)
    return true
  } finally {
    key.fill(0)
  }
}

/** 推送待发信封。返回 true = 没有待发或已被接受;false = 冲突,远端已落地,调用方应重新组装。 */
async function pushOutgoing(ctx: UsageSyncContext): Promise<boolean> {
  const outgoing = readSyncState(ctx.accountId).categories.usage.outgoing
  if (outgoing === null) return true
  const response = await ctx.request('/push', syncPushSchema, { mutations: [outgoing] })
  const accepted = response.accepted.find((item) => item.mutationId === outgoing.mutationId)
  if (accepted !== undefined) {
    acknowledgeSyncOutgoing(ctx.accountId, USAGE, outgoing.mutationId, accepted.revision)
    return true
  }
  const conflict = response.conflicts.find((item) => item.mutationId === outgoing.mutationId)
  if (conflict === undefined) throw new ConfigSyncError('conflict')

  if (conflict.remote === null) {
    // 两种情况会拿到「冲突但没有远端快照」:重放了一条早已冲突的旧变更(服务端对 usage
    // 只留最新冲突副本),或云端这一类的文档不见了。都按「从头重拉」处理:进度归零、再 pull。
    // ★ 不能只丢掉信封:本地 revision 若比云端大,下一次推送会永远 baseRevision 不匹配。
    mutateSyncState(ctx.accountId, (state) => { state.categories.usage = emptySyncCategory() })
    await pull(ctx)
    return false
  }
  mutateSyncState(ctx.accountId, (state) => {
    state.categories.usage.outgoing = null
    state.categories.usage.outgoingDigest = null
  })
  await applyUsageEvent(ctx, conflict.remote)
  return false
}
