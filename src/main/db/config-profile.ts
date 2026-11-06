/**
 * 本机账户配置隔离。
 *
 * ## 一句话
 *
 * 一台机器上只有**一个** SQLite 库,但可以有多个配置作用域:
 * `'local'`(未登录)和每个登录过的账户。切账户 = **同步归档当前作用域的配置、
 * 恢复目标作用域的配置**,整件事在一个 SQLite 事务里。
 *
 * ## 为什么是「归档 + 恢复」而不是「每账户一个库」
 *
 * 因为会话、消息、运行记录、附件和用量是**这台机器的数据**,不是某个账户的
 * 云端配置。换库就得把所有表一起复制、还得处理跨库外键;而这里要的只有一件事:
 * 「A 的供应商密钥不能被 B 读到」。所以只搬**配置**,数据和表结构原地不动。
 *
 * ★ **绝不删除任何会话 / 消息 / 工作区行。** 切作用域改的是**看得见哪些行** ——
 * 工作区靠 `workspaces.owner` 收窄,会话跟着它归属的工作区走。
 * `oldlocal` 那份副本因此永远可以切回来:它一直在 `config_profiles` 里。
 *
 * ## 三样东西的分界
 *
 * | 类别 | 例子 | 存放 | 切作用域 |
 * |---|---|---|---|
 * | **配置** | settings / providers / aliases / MCP / 搜索 / 连接 / 定时任务 | 各自的表 | 归档 + 恢复 |
 * | **配置里的易失 UI 状态** | tabs / 会话草稿输入 / 各开关的禁用清单 | `kv` 白名单 | 归档 + 恢复 |
 * | **机器级** | 代理、网关端口、备份目录、凭据密文 | settings 的真字段 / `credentials` 表 | **原地保留** |
 *
 * 判定凭据归属的办法是**引用前缀**而不是另一张表:`ref` 在库里存的物理键
 * 由 `physicalCredentialRef()` 加作用域前缀派生。`local` 沿用裸 `ref`
 * (这一列存在之前的所有行都归 `local`,不需要回填);
 * 账户作用域用 `<accountId>\u0000<logicalRef>`。于是「同 providerId 在 A、B
 * 用同一个逻辑 ref」在物理上就是两个键 —— 不存在读串的可能。
 *
 * ★ **平台账户的显式键永不加前缀**:`nextcowork:client-access-token` /
 * `nextcowork:client-refresh-token` / `config-sync:*` 描述的是「这台机器当前
 * 登录了谁」,它们必须在任何作用域下都能被同一段代码读到。加前缀的话,
 * 切到账户作用域之后刷新 token 就再也找不到自己刚写进去的那一行。
 */
import { createHash } from 'node:crypto'
import { mkdirSync } from 'node:fs'
import { join } from 'node:path'
import type { SQLOutputValue } from 'node:sqlite'
import type { EnvironmentRef } from '../../shared/domain/environment'
import { normalizeEnvironmentRef } from '../../shared/domain/environment'
import type { AppSettings, AppSettingsPatch } from '../../shared/domain/settings'
import { DEFAULT_SETTINGS, mergeSettings } from '../../shared/domain/settings'
import { providerAccountCredentialRef } from '../../shared/domain/provider-account'
import { ulid } from '../../shared/util/id'
import { sameRoot } from '../state/workspace-root-path'
import { stmt, tx } from './index'
import {
  MIGRATION_CLAIM_KEY,
  MIGRATION_UNDO_KEY,
  mergeMigrationClaims,
  migrationClaimFromUndo,
  parseMigrationClaim
} from './legacy-merge'

/** 未登录作用域。也是 `workspaces.owner` 的默认值。 */
export const LOCAL_CONFIG_SCOPE = 'local'

/** 当前作用域落在 kv 的哪一行。**不在白名单里**,所以它自己不会被切掉。 */
const SCOPE_KEY = 'config-profile.scope'
/** 账户隔离上线前的 provider/key 只允许自动归属给一个账户。 */
const LEGACY_PROVIDER_MIGRATION_OWNER_KEY = 'config-profile.legacy-provider-migration-owner'

/** 账户专属的文件根在数据根下的位置。 */
const PROFILES_DIRNAME = 'config-profiles'

const PROFILE_VERSION = 1

/**
 * 作用域隔离被违反时的错误。
 *
 * ★ 带 `code`,不带中文句子:渲染层的文案一律走 i18n,主进程拼好的中文
 * 到了界面上就是一条**没法翻译**的字符串(见 AGENTS.md 的前端规则)。
 */
export type ConfigProfileErrorCode = 'workspaceOwned' | 'importConflict' | 'importNotAllowed'

export class ConfigProfileError extends Error {
  constructor(readonly code: ConfigProfileErrorCode) {
    super(`configProfile.${code}`)
    this.name = 'ConfigProfileError'
  }
}

/**
 * 随作用域整体归档 / 恢复的表。
 *
 * ★ 顺序就是**恢复顺序**:`model_aliases.provider_id` 上有指向 `providers` 的外键,
 * 别名先插进去会直接 `FOREIGN KEY constraint failed`。
 */
const PROFILE_TABLES = [
  'providers',
  'model_aliases',
  /*
    ★★ OAuth 账号必须随作用域走(schema 第 24 条)。漏掉的表现很具体:
    A 账户登录的 Codex 账号会出现在 B 账户的设置页里 —— 而它的**密文**是
    按作用域隔离的(`physicalCredentialRef` 加前缀),于是 B 看到一行「已登录」、
    一发消息却报「还没有配置密钥」,两边都不报错。

    ★ 排在 `providers` 之后:虽然这张表**没建外键**(它还要连带删 credentials
    里的密文,而那件事外键管不着),但恢复顺序保持和依赖关系一致,
    将来真给它加上外键时这里一行都不用动。
  */
  'provider_accounts',
  'mcp_servers',
  'search_providers',
  'connection_profiles',
  'scheduled_tasks'
] as const

/**
 * `runtime.ts` 的 `seed()` 种默认模型 / 默认子代理的一次性标记。
 *
 * 需求:这两项只在「这个作用域从没种过」时种一次,之后用户选什么(包括空串
 * 「跟随对话」)都原样保留 —— 以前按「值为空」判,重启就把「跟随对话」顶回 deepseek。
 *
 * ★ 必须随作用域走(所以登记在下面的 `PROFILE_KV_KEYS`):留在全局的话,
 * 切到一个新账户时标记还在,新账户的空作用域拿不到种子,首屏模型选择器是空的。
 */
export const MODEL_DEFAULTS_SEEDED_KEY = 'settings.model-defaults-seeded'

/**
 * 随作用域走的 kv 键 —— **白名单,不是黑名单**。
 *
 * ★ 白名单是这里唯一安全的写法。黑名单意味着「以后任何人往 kv 里多写一个键,
 * 它就自动变成跨账户共享」,而那是一类**默认错误**的假设:
 * kv 里现在装着窗口布局、草稿输入、各模块的禁用清单,全都带着「这台机器上
 * 我现在的样子」的语义。反过来,漏进白名单的代价只是多了一条不随账户变的
 * 机器级偏好 —— 可见、可改、不泄漏。
 */
const PROFILE_KV_KEYS = [
  'model-catalog.custom',
  'browser.profiles',
  'skills.disabled',
  'agents.disabled',
  'commands.disabled',
  // client 供应商的一次性迁移标记。跟着供应商走,否则切回账户时
  // 那个「把出厂 openai-chat 抬到 Responses」的一次性动作会重跑一次并覆盖用户的选择。
  'client-auth.responses-default',
  'client-auth.deepseek-zhipu-xiaomi-qwen-anthropic-override-v3',
  MODEL_DEFAULTS_SEEDED_KEY
] as const

const PROFILE_KV_PREFIXES = ['tabs.outer.', 'tabs.inner.', 'session.input.', 'session.widget.'] as const

/** 物理 ref 的分隔符。U+0000 不可能出现在任何一个 ref 构造器里。 */
const SCOPE_SEPARATOR = '\u0000'

/**
 * 平台账户自己的显式键。**任何作用域下都读同一个物理键** —— 见文件头。
 */
const GLOBAL_CREDENTIAL_REFS = new Set([
  'nextcowork:client-access-token',
  'nextcowork:client-refresh-token'
])
const GLOBAL_CREDENTIAL_PREFIXES = ['config-sync:', 'nextcowork:sync-']

export function isGlobalCredentialRef(ref: string): boolean {
  return (
    GLOBAL_CREDENTIAL_REFS.has(ref) ||
    GLOBAL_CREDENTIAL_PREFIXES.some((prefix) => ref.startsWith(prefix))
  )
}

// ═══════════════════════════════════════════════════════════════
// 当前作用域
// ═══════════════════════════════════════════════════════════════

/** 账户 id → 作用域名。`null` / 空串都是「未登录」。 */
export function configScopeForAccount(accountId: string | null): string {
  const id = accountId?.trim() ?? ''
  return id === '' ? LOCAL_CONFIG_SCOPE : id
}

/**
 * 当前作用域。**每次现读**,不缓存在模块变量里 ——
 * 缓存会跨 `closeDatabase()` 活下来,于是一个测试用例的账户作用域会漏进下一个用例,
 * 而症状是「另一个用例的密钥读得到」这种最不该出现的形态。
 */
export function currentConfigScope(): string {
  const row = stmt('SELECT json FROM kv WHERE key = ?').get(SCOPE_KEY)
  if (row === undefined) return LOCAL_CONFIG_SCOPE
  try {
    const value: unknown = JSON.parse(String(row['json']))
    return typeof value === 'string' && value.length > 0 ? value : LOCAL_CONFIG_SCOPE
  } catch {
    return LOCAL_CONFIG_SCOPE
  }
}

function writeScope(scope: string): void {
  stmt('INSERT INTO kv (key, json) VALUES (?, ?) ON CONFLICT (key) DO UPDATE SET json = excluded.json')
    .run(SCOPE_KEY, JSON.stringify(scope))
}

/**
 * 账户专属的文件根(skills / commands / agents / settings.json / themes)。
 *
 * `local` 用的就是数据根本身 —— 这一列存在之前,所有那些文件都在这里,
 * 搬家会把用户自己写的 Skill 一次性弄丢。其它作用域各占一个
 * `<root>/config-profiles/<sha256(account)>`:**用哈希不用账户 id 原文**,
 * 因为 id 来自服务端,直接拼进路径就等于让远端决定本机写哪儿。
 */
export function configProfileDirectory(root: string): string {
  return profileDirectoryForScope(root, currentConfigScope())
}

/** 作用域专属目录的分段名。会话清理等「按目录排除」的地方要认得它。 */
export const PROFILE_DIRECTORY_SEGMENT = PROFILES_DIRNAME

/**
 * 某个作用域的「默认工作区」id。
 *
 * ★ 必须是**派生**的,不能是常量:`workspaces.id` 是主键,`local` 那一行
 * 已经叫 `ws-default` 了 —— 账户作用域再用同一个 id 就会被 `putWorkspace`
 * (正确地)以 `workspaceOwned` 拒掉,于是新登录的账户一个工作区都没有。
 *
 * ★ 只取哈希前 16 位:它只是个 id,不需要抗碰撞到密码学强度,
 * 而短一些在日志和 URL 里都好认。
 */
export function defaultWorkspaceIdForScope(scope: string): string {
  if (scope === LOCAL_CONFIG_SCOPE) return 'ws-default'
  return `ws-default-${createHash('sha256').update(scope).digest('hex').slice(0, 16)}`
}

export function profileDirectoryForScope(root: string, scope: string): string {
  if (scope === LOCAL_CONFIG_SCOPE) return root
  const dir = join(root, PROFILES_DIRNAME, createHash('sha256').update(scope).digest('hex'))
  // 调用点(宿主 paths.userData)按约定返回一个**已经存在**的目录。
  // 账户目录是第一次切换时凭空出现的,所以这里自己建一次。
  mkdirSync(dir, { recursive: true })
  return dir
}

// ═══════════════════════════════════════════════════════════════
// 凭据作用域
// ═══════════════════════════════════════════════════════════════

/** 逻辑 ref → 库里那行的物理键。平台显式键在任何作用域下都不加前缀。 */
export function physicalCredentialRef(logicalRef: string, scope: string = currentConfigScope()): string {
  if (scope === LOCAL_CONFIG_SCOPE || isGlobalCredentialRef(logicalRef)) return logicalRef
  return `${scope}${SCOPE_SEPARATOR}${logicalRef}`
}

/** 库里那行的物理键 → 逻辑 ref。不属于该作用域时返回 null。 */
export function logicalCredentialRef(physicalRef: string, scope: string): string | null {
  if (scope === LOCAL_CONFIG_SCOPE) {
    // ★ 含分隔符的行属于**某个账户作用域**,不是 local 的。不挡掉的话,
    // `listCredentials` 在 local 下会把其它账户的密钥原样导出。
    if (physicalRef.includes(SCOPE_SEPARATOR)) return null
    return isGlobalCredentialRef(physicalRef) ? null : physicalRef
  }
  const prefix = `${scope}${SCOPE_SEPARATOR}`
  return physicalRef.startsWith(prefix) ? physicalRef.slice(prefix.length) : null
}

/** `DELETE ... WHERE substr(ref, 1, length(?)) = ?` 用的前缀。`local` 是空串(即「全部」)。 */
export function credentialScopePrefix(scope: string = currentConfigScope()): string {
  return scope === LOCAL_CONFIG_SCOPE ? '' : `${scope}${SCOPE_SEPARATOR}`
}

// ═══════════════════════════════════════════════════════════════
// 工作区归属
// ═══════════════════════════════════════════════════════════════

/** 工作区行的归属作用域;行不存在时返回 null。 */
export function workspaceScope(workspaceId: string): string | null {
  const row = stmt('SELECT owner FROM workspaces WHERE id = ?').get(workspaceId)
  return row === undefined ? null : String(row['owner'])
}

/**
 * 当前作用域能不能看见这个工作区。
 *
 * ★ 两种「无归属」分开处理,这一条是整个隔离里最容易写错的地方:
 * - `''`(还没绑定工作区的会话)只有 `local` 能看 —— 它就是本地测试和
 *   渲染层先生成 id 的兜底,给账户作用域开这个口子等于给了一条绕过归属的通道;
 * - **行不存在**的 workspaceId 也只在 `local` 可见。账户作用域下它必然是一个
 *   已经被删掉的、或者属于别的库的 id,放行就是「跨账户查出结果」。
 */
export function workspaceScopeVisible(workspaceId: string): boolean {
  if (workspaceId === '') return currentConfigScope() === LOCAL_CONFIG_SCOPE
  const owner = workspaceScope(workspaceId)
  if (owner === null) return currentConfigScope() === LOCAL_CONFIG_SCOPE
  return owner === currentConfigScope()
}

/**
 * 给启动数据整理迁入的会话建立当前账户专属的工作区副本。
 *
 * 需求：行级合并在 `openDatabase()` 之前跑，不能读取当前账户作用域，新插入行只能
 * 先使用 `workspaces.owner` 的默认值 `local`。登录账户不重连这些会话时，迁移明明
 * 显示成功，账户侧边栏却没有一条会话可显示。
 *
 * ★ 不能直接改原工作区的 owner：`ws-default` 仍是 local 的保留 id，挪走后 local
 * 下次启动无法重新种默认工作区。这里仅复制归属清单引用的 local 工作区，并重连
 * 清单里的会话；真正的未登录数据和其他账户的数据都留在原作用域。
 */
export function claimMigratedLocalWorkspaces(accountId: string): number {
  const target = configScopeForAccount(accountId)
  if (target === LOCAL_CONFIG_SCOPE) return 0
  return tx(() => {
    const row = stmt('SELECT json FROM kv WHERE key = ?').get(MIGRATION_CLAIM_KEY)
    const storedClaim = row === undefined ? null : parseMigrationClaim(row['json'])
    const legacyClaim = migrationClaimFromUndo(
      stmt('SELECT json FROM kv WHERE key = ?').get(MIGRATION_UNDO_KEY)?.['json']
    )
    const claim = legacyClaim === null
      ? storedClaim
      : mergeMigrationClaims(storedClaim, legacyClaim)
    if (claim === null) return 0

    const claimedBy = claim.claimedBy ?? null
    // 需求：一份迁入数据只属于第一个认领它的账户；不满足会怎样：切换账户后，
    // 第二个账户会看到本不属于它的历史会话，而且没有任何跨账户访问报错。
    if (claimedBy !== null && claimedBy !== target) return 0
    if (claimedBy === target && claim.pending === false) return 0

    const workspaceMap = { ...(claim.workspaceMap ?? {}) }
    const sessions = migrationSessions(claim.sessions)
    const workspaceIds = new Set<string>(claim.workspaces)
    for (const session of sessions) {
      workspaceIds.add(sourceWorkspaceIdFor(session.workspaceId, workspaceMap))
    }

    let changed = 0
    let manifestChanged = false
    const unavailableMappings = new Set<string>()
    for (const workspaceId of workspaceIds) {
      const mapped = workspaceMap[workspaceId]
      if (mapped !== undefined) {
        const targetWorkspace = stmt('SELECT owner FROM workspaces WHERE id = ?').get(mapped)
        if (targetWorkspace !== undefined && String(targetWorkspace['owner']) === target) continue
        // 需求：用户删掉账户里的迁入工作区后，普通登录不能复活一个空副本；但一次
        // 新迁移会重新设 pending，必须让新增会话有可见归宿。旧会话仍保持删除态。
        if (claim.pending === true) {
          delete workspaceMap[workspaceId]
          manifestChanged = true
        } else {
          unavailableMappings.add(workspaceId)
          continue
        }
      }

      const source = stmt('SELECT last_opened_at, json, owner FROM workspaces WHERE id = ?').get(workspaceId)
      if (source === undefined) continue
      const owner = String(source['owner'])
      if (owner === target) {
        workspaceMap[workspaceId] = workspaceId
        manifestChanged = true
        continue
      }
      if (owner !== LOCAL_CONFIG_SCOPE) continue

      const sourceWorkspace = workspaceRecord(source['json'])
      if (sourceWorkspace === null) {
        console.warn(`[config-profile] 启动迁移工作区记录损坏，未认领:${workspaceId}`)
        continue
      }
      const sourceEnvironment = normalizeEnvironmentRef(sourceWorkspace['environment'])
      const rootPath = typeof sourceWorkspace['rootPath'] === 'string' ? sourceWorkspace['rootPath'] : ''
      if (sourceEnvironment.kind === 'local' && rootPath !== '') {
        const existing = workspaceIdForRoot(target, rootPath)
        if (existing !== null) {
          workspaceMap[workspaceId] = existing
          manifestChanged = true
          continue
        }
      }

      // 需求：连接配置不随行级数据迁入账户作用域。远端会话仍需可见，但不能因为
      // 复用同名本机路径而改在本机执行，所以副本明确降级成 unbound。
      const copiedEnvironment: EnvironmentRef | undefined = sourceEnvironment.kind === 'local'
        ? undefined
        : { kind: 'unbound' }
      const copiedId = ulid()
      const json = workspaceJsonWithId(sourceWorkspace, copiedId, copiedEnvironment)
      stmt('INSERT INTO workspaces (id, last_opened_at, json, owner) VALUES (?, ?, ?, ?)')
        .run(copiedId, Number(source['last_opened_at'] ?? Date.now()), json, target)
      workspaceMap[workspaceId] = copiedId
      manifestChanged = true
      changed += 1
    }

    for (const session of sessions) {
      const sourceWorkspaceId = sourceWorkspaceIdFor(session.workspaceId, workspaceMap)
      if (unavailableMappings.has(sourceWorkspaceId)) continue
      const targetWorkspaceId = workspaceMap[sourceWorkspaceId]
      if (targetWorkspaceId === undefined || targetWorkspaceId === session.workspaceId) continue
      const json = sessionJsonWithWorkspaceId(session.json, targetWorkspaceId)
      const updated = json === null
        ? stmt('UPDATE sessions SET workspace_id = ? WHERE id = ? AND workspace_id = ?')
          .run(targetWorkspaceId, session.id, session.workspaceId)
        : stmt('UPDATE sessions SET workspace_id = ?, json = ? WHERE id = ? AND workspace_id = ?')
          .run(targetWorkspaceId, json, session.id, session.workspaceId)
      if (Number(updated.changes ?? 0) === 0) continue
      changed += 1
      stmt('UPDATE usage_records SET workspace_id = ? WHERE session_id = ? AND workspace_id = ?')
        .run(targetWorkspaceId, session.id, session.workspaceId)
      stmt('UPDATE file_change_sets SET workspace_id = ? WHERE session_id = ? AND workspace_id = ?')
        .run(targetWorkspaceId, session.id, session.workspaceId)
    }

    if (Object.keys(workspaceMap).length > 0 &&
      (claimedBy !== target || manifestChanged || claim.pending !== false)) {
      stmt('INSERT INTO kv (key, json) VALUES (?, ?) ON CONFLICT (key) DO UPDATE SET json = excluded.json')
        .run(MIGRATION_CLAIM_KEY, JSON.stringify({ ...claim, claimedBy: target, workspaceMap, pending: false }))
    }
    return changed
  })
}

interface MigrationSession {
  id: string
  workspaceId: string
  json: unknown
}

function migrationSessions(ids: readonly string[]): MigrationSession[] {
  const sessions: MigrationSession[] = []
  for (const id of ids) {
    const row = stmt('SELECT id, workspace_id, json FROM sessions WHERE id = ?').get(id)
    if (row === undefined) continue
    const workspaceId = String(row['workspace_id'] ?? '')
    if (workspaceId === '') continue
    sessions.push({ id: String(row['id']), workspaceId, json: row['json'] })
  }
  return sessions
}

/** 已重连过的会话存的是副本 id，重跑时反查回 source id 才不会再复制一份。 */
function sourceWorkspaceIdFor(workspaceId: string, map: Record<string, string>): string {
  for (const [source, target] of Object.entries(map)) {
    if (target === workspaceId) return source
  }
  return workspaceId
}

function workspaceIdForRoot(owner: string, rootPath: string): string | null {
  for (const row of stmt('SELECT id, json FROM workspaces WHERE owner = ?').all(owner)) {
    const workspace = workspaceRecord(row['json'])
    // 需求：本机迁入会话只能复用本机工作区；同名远端路径不能把工具执行位置悄悄切到 SSH。
    if (workspace === null || normalizeEnvironmentRef(workspace['environment']).kind !== 'local') continue
    const candidate = typeof workspace['rootPath'] === 'string' ? workspace['rootPath'] : null
    if (candidate !== null && sameRoot(candidate, rootPath)) return String(row['id'])
  }
  return null
}

function workspaceRecord(json: unknown): Record<string, unknown> | null {
  try {
    const value: unknown = JSON.parse(String(json))
    return typeof value === 'object' && value !== null && !Array.isArray(value)
      ? value as Record<string, unknown>
      : null
  } catch {
    return null
  }
}

function workspaceJsonWithId(
  workspace: Record<string, unknown>,
  id: string,
  environment: EnvironmentRef | undefined
): string {
  return JSON.stringify({
    ...workspace,
    id,
    ...(environment === undefined ? {} : { environment })
  })
}

function sessionJsonWithWorkspaceId(json: unknown, workspaceId: string): string | null {
  try {
    const value: unknown = JSON.parse(String(json))
    if (typeof value !== 'object' || value === null || Array.isArray(value)) return null
    return JSON.stringify({ ...value, workspaceId })
  } catch {
    return null
  }
}

// ═══════════════════════════════════════════════════════════════
// 快照
// ═══════════════════════════════════════════════════════════════

type Row = Record<string, SQLOutputValue>

interface ProfileSnapshot {
  version: number
  settings: AppSettings | null
  tables: Record<string, Row[]>
  kv: Record<string, unknown>
}

/**
 * 归档行 = 快照 + **不参与恢复的记账**。
 *
 * `importedLocal` / `workspaceMap` 必须和快照住在同一行:它们描述的是
 * 「这个作用域和 local 之间发生过什么」,而每次切走都会重写快照 ——
 * 分开存就一定会有一半在切换时丢掉。
 */
interface ProfileRecord extends Partial<ProfileSnapshot> {
  importedLocal?: boolean
  /** 首次账户登录时只复制过旧 provider/model/credential 资产。 */
  migratedLegacyProviders?: boolean
  workspaceMap?: Record<string, string>
}

function readProfileRecord(scope: string): ProfileRecord | null {
  const row = stmt('SELECT json FROM config_profiles WHERE scope = ?').get(scope)
  if (row === undefined) return null
  try {
    const value: unknown = JSON.parse(String(row['json']))
    return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as ProfileRecord) : null
  } catch {
    // 归档行损坏 = 该作用域「没有快照」。比抛错好:抛错会让用户**连 local 都切不回去**,
    // 而那一份很可能还好好的。
    console.warn('[config-profile] 归档行损坏,按无快照处理')
    return null
  }
}

function writeProfileRecord(scope: string, record: ProfileRecord): void {
  stmt('INSERT INTO config_profiles (scope, json) VALUES (?, ?) ON CONFLICT (scope) DO UPDATE SET json = excluded.json')
    .run(scope, JSON.stringify(record))
}

function dumpTable(table: string): Row[] {
  // 表名来自本文件顶部的常量表,不是外部输入。
  return stmt(`SELECT * FROM ${table}`).all() as unknown as Row[]
}

function dumpKv(): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  for (const key of PROFILE_KV_KEYS) {
    const row = stmt('SELECT json FROM kv WHERE key = ?').get(key)
    if (row !== undefined) out[key] = JSON.parse(String(row['json']))
  }
  for (const prefix of PROFILE_KV_PREFIXES) {
    for (const row of stmt('SELECT key, json FROM kv WHERE substr(key, 1, length(?)) = ?').all(prefix, prefix)) {
      const r = row as Record<string, unknown>
      out[String(r['key'])] = JSON.parse(String(r['json']))
    }
  }
  return out
}

function captureSnapshot(): ProfileSnapshot {
  const tables: Record<string, Row[]> = {}
  for (const table of PROFILE_TABLES) tables[table] = dumpTable(table)
  const row = stmt('SELECT json FROM settings WHERE id = 1').get()
  let settings: AppSettings | null = null
  if (row !== undefined) {
    try {
      settings = JSON.parse(String(row['json'])) as AppSettings
    } catch {
      settings = null
    }
  }
  return { version: PROFILE_VERSION, settings, tables, kv: dumpKv() }
}

function clearProfileScopedRows(): void {
  // 先删别名再删供应商:反过来的话级联会先删掉别名,这里的 DELETE 就成了空转 ——
  // 结果一样,但这个顺序不依赖「级联开着」这个前提。
  for (const table of [...PROFILE_TABLES].reverse()) stmt(`DELETE FROM ${table}`).run()
  for (const key of PROFILE_KV_KEYS) stmt('DELETE FROM kv WHERE key = ?').run(key)
  for (const prefix of PROFILE_KV_PREFIXES) {
    stmt('DELETE FROM kv WHERE substr(key, 1, length(?)) = ?').run(prefix, prefix)
  }
}

function writeSettings(settings: AppSettings): void {
  stmt('INSERT INTO settings (id, json) VALUES (1, ?) ON CONFLICT (id) DO UPDATE SET json = excluded.json')
    .run(JSON.stringify(settings))
}

function restoreTable(table: string, rows: readonly Row[]): void {
  for (const row of rows) {
    const columns = Object.keys(row)
    if (columns.length === 0) continue
    stmt(
      `INSERT INTO ${table} (${columns.join(', ')}) VALUES (${columns.map(() => '?').join(', ')})`
    ).run(...columns.map((column) => row[column] ?? null))
  }
}

/**
 * 机器级设置 —— 切换时**原地保留**的那几项。
 *
 * 判定:它的值描述的是「这台机器怎么连出去」,而不是「这个用户喜欢什么」。
 * 代理和网关端口属于前者(换账户不改网络拓扑);个性化、默认模型、
 * 子代理档位属于后者(那正是隔离要分开的东西)。
 *
 * ★ `data.backupDirectory` 是**路径**,换账户之后同一个路径当然还是有效的
 * —— 把它随账户换掉,用户会看到备份悄悄写到另一个目录去。
 */
function machineLevelPatch(settings: AppSettings): AppSettingsPatch {
  return {
    gateway: structuredClone(settings.gateway),
    proxy: structuredClone(settings.proxy),
    shell: settings.shell,
    // 和 shell 同理:描述的是这台机器上装了哪些程序,不随账户换
    defaultOpenTarget: settings.defaultOpenTarget,
    data: { backupDirectory: settings.data.backupDirectory }
  }
}

/**
 * 空作用域的初始设置。
 *
 * ★ 只带 `locale` / `theme` 两项过来:它们是「界面还能用」的最低要求
 * (一个新账户突然变成英文/亮色,用户会以为程序坏了)。个性化、默认模型、
 * 快捷键一律缺席 —— 那些是**上一个用户的**选择。
 */
function freshProfileSettings(carried: AppSettings): AppSettings {
  return mergeSettings(DEFAULT_SETTINGS, {
    locale: carried.locale,
    theme: carried.theme,
    ...machineLevelPatch(carried)
  })
}

function applySnapshot(record: ProfileRecord, carried: AppSettings): void {
  for (const table of PROFILE_TABLES) restoreTable(table, record.tables?.[table] ?? [])
  const kv = record.kv ?? {}
  for (const [key, value] of Object.entries(kv)) {
    stmt('INSERT INTO kv (key, json) VALUES (?, ?) ON CONFLICT (key) DO UPDATE SET json = excluded.json')
      .run(key, JSON.stringify(value ?? null))
  }
  const restored = record.settings
  writeSettings(restored === null || typeof restored !== 'object'
    ? freshProfileSettings(carried)
    : mergeSettings(restored, machineLevelPatch(carried)))
}

function applyEmptyProfile(carried: AppSettings): void {
  writeSettings(freshProfileSettings(carried))
}

// ═══════════════════════════════════════════════════════════════
// 切换
// ═══════════════════════════════════════════════════════════════

/**
 * 切到 `accountId` 的作用域。`null` = 切回 `local`。
 *
 * ★ **一个事务**:归档写不下去就不能清表,清完表恢复失败就得连归档一起回滚 ——
 * 否则一次失败的切换会留下一个「配置表空着、快照也丢了」的库,那是不可恢复的。
 * DDL 之外的 DML 在 SQLite 里本来就是事务性的,`tx()` 把整段括起来即可。
 *
 * ★ 同一个作用域之间切换是**恒等操作**:`getClientAuthState()` 每次读登录态
 * 都会调到这里,不做早退的话每读一次登录态就把配置表整表重写一次。
 */
export function switchConfigProfile(accountId: string | null): void {
  const target = configScopeForAccount(accountId)
  const current = currentConfigScope()
  if (target === current) return
  tx(() => {
    // 机器级字段必须在**清表之前**读:清完之后当前设置已经不属于这个作用域了。
    const carried = currentSettings()
    const archive = readProfileRecord(current) ?? {}
    // `...archive` 让 `importedLocal` / `workspaceMap` 这两项记账跨归档活下来 ——
    // 它们不在快照里,少这一行就会在第一次切走时被丢掉。
    writeProfileRecord(current, { ...archive, ...captureSnapshot() })
    clearProfileScopedRows()
    const restored = readProfileRecord(target)
    if (restored === null) applyEmptyProfile(carried)
    else applySnapshot(restored, carried)
    writeScope(target)
  })
}

function currentSettings(): AppSettings {
  const row = stmt('SELECT json FROM settings WHERE id = 1').get()
  if (row === undefined) return structuredClone(DEFAULT_SETTINGS)
  try {
    const value: unknown = JSON.parse(String(row['json']))
    return typeof value === 'object' && value !== null && !Array.isArray(value)
      ? mergeSettings(DEFAULT_SETTINGS, value as AppSettingsPatch)
      : structuredClone(DEFAULT_SETTINGS)
  } catch {
    return structuredClone(DEFAULT_SETTINGS)
  }
}

// ═══════════════════════════════════════════════════════════════
// 显式导入 local
// ═══════════════════════════════════════════════════════════════

/**
 * `local` 那份配置**是不是有东西值得导入**。
 *
 * 当前就在 `local` 时看的是活表(还没被归档过);否则看归档行 ——
 * 切到账户的那一刻 `local` 已经被归档,之后它不再变化。
 */
export function hasLocalConfigProfile(): boolean {
  if (currentConfigScope() === LOCAL_CONFIG_SCOPE) {
    if (preparedRowCount('providers') > 0) return true
    for (const table of ['model_aliases', 'mcp_servers', 'search_providers', 'connection_profiles', 'scheduled_tasks'] as const) {
      if (preparedRowCount(table) > 0) return true
    }
    return Object.keys(dumpKv()).length > 0
  }
  return readProfileRecord(LOCAL_CONFIG_SCOPE) !== null
}

function preparedRowCount(table: string): number {
  const row = stmt(`SELECT COUNT(*) AS n FROM ${table}`).get()
  return Number(row?.['n'] ?? 0)
}

/**
 * **显式**把 `local` 的配置导入当前账户作用域。
 *
 * ★ 不自动跑。登录不导入 —— `local` 那份配置不是「任意账户的」,
 * 它属于这台机器的未登录状态;把它悄悄塞进第一个登录的账户,
 * 用户看到的是「我只是登了个号,它就把我的会话结构改了一遍」。
 *
 * ★ 只导**配置**,不导会话与运行历史:那些留在 `local` 空间里,
 * 切回去一个字节都不会少。
 *
 * ★ 工作区**换个新 id 复制**,并记下 mapping:`local` 那份继续归 `local`,
 * 两个作用域各有一份、互不影响。`mcpServers.workspaceId` /
 * `scheduledTasks.workspaceId` 跟着 mapping 一起改写 —— 不改写的话它们会指向
 * **当前账户看不见的那个工作区 id**,表现在界面上是「定时任务点开是空的」。
 *
 * ★ 冲突一律拒绝(抛 `ConfigProfileError('importConflict')`),
 * 不静默覆盖:同 id 的两条配置谁赢没有任何正确答案,而「悄悄用 local 那份盖掉
 * 账户里已有的」正是最坏的那种猜法。
 */
export function migrateLegacyLocalProvidersToCurrentAccount(): boolean {
  const target = currentConfigScope()
  if (target === LOCAL_CONFIG_SCOPE) return false
  const existing = readProfileRecord(target) ?? {}
  if (existing.migratedLegacyProviders === true || existing.importedLocal === true) return false
  const ownerRow = stmt('SELECT json FROM kv WHERE key = ?').get(LEGACY_PROVIDER_MIGRATION_OWNER_KEY)
  let owner: string | null = null
  if (ownerRow !== undefined) {
    try {
      const parsed: unknown = JSON.parse(String(ownerRow['json']))
      owner = typeof parsed === 'string' && parsed !== '' ? parsed : null
    } catch { /* 损坏标记按未迁移处理,真正的数据冲突仍由下面逐项守卫 */ }
  }
  if (owner !== null && owner !== target) {
    writeProfileRecord(target, { ...existing, migratedLegacyProviders: true })
    return false
  }
  const source = readProfileRecord(LOCAL_CONFIG_SCOPE)
  if (source === null) {
    writeProfileRecord(target, { ...existing, migratedLegacyProviders: true })
    return false
  }

  const providers = (source.tables?.['providers'] ?? [])
    .filter((row) => String(row['id'] ?? '') !== 'nextcowork')
  const providerRefs = new Map<string, string>()
  for (const row of providers) {
    const id = String(row['id'] ?? '')
    const ref = parseObject(row['json'])['credentialRef']
    if (id !== '' && typeof ref === 'string' && ref !== '' && !isGlobalCredentialRef(ref)) {
      providerRefs.set(id, ref)
    }
  }
  const providerIds = new Set(providerRefs.keys())
  const aliases = (source.tables?.['model_aliases'] ?? [])
    .filter((row) => providerIds.has(String(row['provider_id'] ?? '')))
  /*
    ★★ 账号行与它们的密文要和 provider 一起过来(schema 第 24 条)。
    漏掉的表现是:账户隔离上线**之前**就已经登录了多个号的用户,首次登录账户之后
    只剩下当前那一个 —— 另外几个的密文还在 local 作用域里躺着,而界面上
    连一行都不显示,他只会以为自己被登出了。
  */
  const accounts = (source.tables?.['provider_accounts'] ?? [])
    .filter((row) => providerIds.has(String(row['provider_id'] ?? '')))

  return tx(() => {
    const insertedProviders = new Set<string>()
    for (const row of providers) {
      const id = String(row['id'] ?? '')
      if (id === '' || providerExists(id)) continue
      restoreTable('providers', [row])
      insertedProviders.add(id)
    }
    for (const row of aliases) {
      const providerId = String(row['provider_id'] ?? '')
      const alias = String(row['alias'] ?? '')
      if (!insertedProviders.has(providerId) || alias === '') continue
      const exists = stmt('SELECT 1 FROM model_aliases WHERE provider_id = ? AND alias = ?').get(providerId, alias)
      if (exists === undefined) restoreTable('model_aliases', [row])
    }
    for (const [providerId, logical] of providerRefs) {
      if (!insertedProviders.has(providerId)) continue
      const targetProvider = stmt('SELECT json FROM providers WHERE id = ?').get(providerId)
      const targetRef = parseObject(targetProvider?.['json'])['credentialRef']
      // 目标已有同 id 但引用不同 = 它是目标账户自己的配置,不把 local key 塞进去。
      if (targetRef !== logical) continue
      const next = physicalCredentialRef(logical, target)
      if (credentialsHas(next)) continue
      const row = stmt('SELECT blob FROM credentials WHERE ref = ?').get(logical)
      const blob = row?.['blob']
      if (blob instanceof Uint8Array) stmt('INSERT INTO credentials (ref, blob) VALUES (?, ?)').run(next, blob)
    }
    /*
      账号行 + 它们的密文。★ 只搬那些 provider 刚被插进来的(`insertedProviders`):
      目标账户已经有同 id provider 时,它名下的账号是**它自己的**,
      把 local 的账号混进去会让两个账户的登录态串在一起。
    */
    for (const row of accounts) {
      const providerId = String(row['provider_id'] ?? '')
      const accountId = String(row['id'] ?? '')
      if (!insertedProviders.has(providerId) || accountId === '') continue
      if (stmt('SELECT id FROM provider_accounts WHERE id = ?').get(accountId) !== undefined) continue
      restoreTable('provider_accounts', [row])
      const logical = providerAccountCredentialRef(providerId, accountId)
      const next = physicalCredentialRef(logical, target)
      if (credentialsHas(next)) continue
      const credential = stmt('SELECT blob FROM credentials WHERE ref = ?').get(logical)
      const blob = credential?.['blob']
      if (blob instanceof Uint8Array) stmt('INSERT INTO credentials (ref, blob) VALUES (?, ?)').run(next, blob)
    }
    writeProfileRecord(target, { ...existing, migratedLegacyProviders: true })
    stmt('INSERT INTO kv (key, json) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET json = excluded.json')
      .run(LEGACY_PROVIDER_MIGRATION_OWNER_KEY, JSON.stringify(target))
    return insertedProviders.size > 0
  })
}

function providerExists(id: string): boolean {
  return stmt('SELECT id FROM providers WHERE id = ?').get(id) !== undefined
}

export function importLocalConfigProfile(): void {
  const target = currentConfigScope()
  if (target === LOCAL_CONFIG_SCOPE) throw new ConfigProfileError('importNotAllowed')
  const existing = readProfileRecord(target) ?? {}
  if (existing.importedLocal === true) throw new ConfigProfileError('importNotAllowed')
  /*
    `local` 的快照在**切到账户的那一刻**就已经落好了(switchConfigProfile 的归档那一步),
    之后它不再变化。所以这里读归档行而不是现采一份 —— 现采会把账户作用域的表
    当成 local 的,那是把两个作用域搅在一起。
  */
  const source = readProfileRecord(LOCAL_CONFIG_SCOPE)
  if (source === null) throw new ConfigProfileError('importNotAllowed')

  const sourceTables = source.tables ?? {}
  const workspaceMap = planWorkspaceMapping(sourceTables['workspaces'] ?? [])
  const planned = planRows(sourceTables, workspaceMap)
  assertNoConflicts(planned)
  assertNoCredentialConflicts(target)

  tx(() => {
    for (const table of PROFILE_TABLES) for (const row of planned[table] ?? []) restoreTable(table, [row])
    copyLocalWorkspaces(sourceTables['workspaces'] ?? [], workspaceMap)
    copyKv(source.kv ?? {})
    // 设置整块搬过来(用户显式点的),机器级字段仍以本机为准。
    const carried = currentSettings()
    const imported = source.settings === null || typeof source.settings !== 'object' || source.settings === undefined
      ? undefined
      : mergeSettings(source.settings, machineLevelPatch(carried))
    if (imported !== undefined) writeSettings(imported)
    copyCredentials()
    writeProfileRecord(target, { ...existing, importedLocal: true, workspaceMap })
  })
}

/**
 * 快照里的行 → 真正要插进目标作用域的行。
 *
 * 三处改写:
 * - 平台那条 `nextcowork` 供应商/别名**不导入**:它每个账户各有一份,
 *   由登录流程自己保证存在;搬过去反而会覆盖刚建立的那条。
 * - MCP 的 `workspaceId` 跟着 mapping 走;指不到任何目标工作区时**整个字段去掉**
 *   (降级成账户级的 MCP),而不是留一个悬空 id —— 悬空 id 在界面上表现为
 *   「这条服务器明明在,却用它跑不起来」。
 * - 定时任务的 `workspaceId` 同理,只是它是必填列,指不到就保留原值。
 */
function planRows(sourceTables: Record<string, Row[]>, workspaceMap: Record<string, string>): Record<string, Row[]> {
  const out: Record<string, Row[]> = {}
  out['providers'] = (sourceTables['providers'] ?? []).filter((row) => String(row['id'] ?? '') !== 'nextcowork')
  out['model_aliases'] = (sourceTables['model_aliases'] ?? []).filter((row) => String(row['provider_id'] ?? '') !== 'nextcowork')
  out['mcp_servers'] = (sourceTables['mcp_servers'] ?? []).map((row) => {
    const json = parseObject(row['json'])
    const workspaceId = typeof json['workspaceId'] === 'string' ? json['workspaceId'] : ''
    if (workspaceId === '') return row
    const mapped = workspaceMap[workspaceId]
    const next = { ...json }
    if (mapped === undefined) delete next['workspaceId']
    else next['workspaceId'] = mapped
    return { ...row, json: JSON.stringify(next) }
  })
  out['search_providers'] = [...(sourceTables['search_providers'] ?? [])]
  /*
    ★ 账号行跟着它的 provider 走,平台那条同样排除(理由同上面两行:
    `nextcowork` 每个账户各有一份,由登录流程自己保证)。
    密文不在这里 —— 它由 `migrateLegacyLocalProvidersToCurrentAccount` 那条
    凭证搬运路径按 ref 处理(见本文件里 `physicalCredentialRef` 那一节)。
  */
  out['provider_accounts'] = (sourceTables['provider_accounts'] ?? [])
    .filter((row) => String(row['provider_id'] ?? '') !== 'nextcowork')
  out['connection_profiles'] = [...(sourceTables['connection_profiles'] ?? [])]
  out['scheduled_tasks'] = (sourceTables['scheduled_tasks'] ?? []).map((row) => remapScheduledTask(row, workspaceMap))
  /* 只在冲突判定里用到:`PROFILE_TABLES` 不含 workspaces(它们靠 owner 复制,不整表搬)。 */
  out['workspaces'] = [...(sourceTables['workspaces'] ?? [])]
  return out
}

/** `local` 的工作区 → 目标作用域里的新 id。local 自己那份不动。 */
function planWorkspaceMapping(rows: readonly Row[]): Record<string, string> {
  const map: Record<string, string> = {}
  for (const row of rows) {
    const id = String(row['id'] ?? '')
    if (id === '') continue
    map[id] = ulid()
  }
  return map
}

function copyLocalWorkspaces(rows: readonly Row[], map: Record<string, string>): void {
  const target = currentConfigScope()
  for (const row of rows) {
    const id = String(row['id'] ?? '')
    const nextId = map[id]
    if (nextId === undefined) continue
    const json = parseObject(row['json'])
    // ★ `rootPath` **沿用**而不是新造:导进来的是一个真实存在的目录,
    //   而用户点「导入」的意思正是「就用我本地这个」。
    const next = { ...json, id: nextId }
    stmt('INSERT INTO workspaces (id, last_opened_at, json, owner) VALUES (?, ?, ?, ?)')
      .run(nextId, Number(row['last_opened_at'] ?? Date.now()), JSON.stringify(next), target)
  }
}

function remapScheduledTask(row: Row, map: Record<string, string>): Row {
  const json = parseObject(row['json'])
  const workspaceId = String(row['workspace_id'] ?? '')
  const mapped = map[workspaceId] ?? workspaceId
  return {
    ...row,
    workspace_id: mapped,
    json: JSON.stringify({ ...json, workspaceId: mapped })
  }
}

function copyKv(kv: Record<string, unknown>): void {
  for (const [key, value] of Object.entries(kv)) {
    // 目标作用域已有的同名键不覆盖(冲突检查在上一步已经拒绝过大部分)。
    const row = stmt('SELECT key FROM kv WHERE key = ?').get(key)
    if (row !== undefined) continue
    stmt('INSERT INTO kv (key, json) VALUES (?, ?)').run(key, JSON.stringify(value ?? null))
  }
}

/** 导入前把目标作用域里已经存在的实体找出来落库。 */
function targetIds(table: string, column = 'id'): Set<string> {
  return new Set(stmt(`SELECT ${column} AS id FROM ${table}`).all().map((row) => String((row as Record<string, unknown>)['id'])))
}

function assertNoConflicts(planned: Record<string, Row[]>): void {
  const conflicts: string[] = []
  for (const row of planned['providers'] ?? []) {
    const id = String(row['id'] ?? '')
    if (id !== '' && targetIds('providers').has(id)) conflicts.push(`provider:${id}`)
  }
  for (const row of planned['model_aliases'] ?? []) {
    const providerId = String(row['provider_id'] ?? '')
    const alias = String(row['alias'] ?? '')
    const existing = stmt('SELECT alias FROM model_aliases WHERE provider_id = ?').all(providerId)
    if (existing.some((item) => String((item as Record<string, unknown>)['alias']) === alias)) {
      conflicts.push(`modelAlias:${providerId}/${alias}`)
    }
  }
  for (const table of ['mcp_servers', 'search_providers', 'connection_profiles', 'scheduled_tasks'] as const) {
    const ids = targetIds(table)
    for (const row of planned[table] ?? []) {
      const id = String(row['id'] ?? '')
      if (id !== '' && ids.has(id)) conflicts.push(`${table}:${id}`)
    }
  }
  // 工作区换新 id,但**同一个 rootPath 已经在目标里**说明两边其实是同一个目录,
  // 复制过去会得到两个指向同一处的工作区(界面上一模一样的两行)。
  const roots = new Set(
    stmt('SELECT json FROM workspaces WHERE owner = ?').all(currentConfigScope())
      .map((row) => String(parseObject((row as Record<string, unknown>)['json'])['rootPath'] ?? ''))
  )
  for (const row of planned['workspaces'] ?? []) {
    const root = String(parseObject(row['json'])['rootPath'] ?? '')
    if (root !== '' && roots.has(root)) conflicts.push(`workspace:${root}`)
  }
  if (conflicts.length > 0) throw new ConfigProfileError('importConflict')
}

/** 同一条逻辑 ref 在目标作用域里已经有密文时必须拒绝 —— 不能悄悄盖掉。 */
function assertNoCredentialConflicts(target: string): void {
  for (const { logical } of localCredentialRefs()) {
    const physical = physicalCredentialRef(logical, target)
    if (physical !== logical && credentialsHas(physical)) throw new ConfigProfileError('importConflict')
  }
}

function credentialsHas(ref: string): boolean {
  return stmt('SELECT ref FROM credentials WHERE ref = ?').get(ref) !== undefined
}

/** `local` 作用域里所有**非全局**的密文 ref。 */
function localCredentialRefs(): Array<{ physical: string; logical: string }> {
  const rows = stmt('SELECT ref FROM credentials ORDER BY ref').all()
  const out: Array<{ physical: string; logical: string }> = []
  for (const row of rows) {
    const ref = String((row as Record<string, unknown>)['ref'])
    if (isGlobalCredentialRef(ref)) continue
    out.push({ physical: ref, logical: ref })
  }
  return out
}

/**
 * 把 `local` 的密文**按字节**复制到目标作用域的物理键上。
 *
 * ★ 复制的是 NCK1 AES-GCM 密文,不是明文 —— 同机所有作用域共用数据库目录下
 * 那一把主密钥,所以换物理 ref 不需要解密重加密。
 * 这也意味着导入**只有在同一台机器上**才有意义,而它的语义正是「同一台机器上
 * 把未登录时的配置归到这个账户」,与跨机迁移无关。
 *
 * ★ 平台/全局/同步密钥一律跳过:它们要么是账户自己的,要么描述设备身份
 * (`config-sync:*` 里有同步密钥和设备身份,搬进账户作用域等于把它复制一份)。
 */
function copyCredentials(): void {
  const target = currentConfigScope()
  for (const { physical, logical } of localCredentialRefs()) {
    const next = physicalCredentialRef(logical, target)
    if (next === physical) continue
    if (credentialsHas(next)) throw new ConfigProfileError('importConflict')
    const row = stmt('SELECT blob FROM credentials WHERE ref = ?').get(physical)
    const blob = row?.['blob']
    if (!(blob instanceof Uint8Array)) continue
    stmt('INSERT INTO credentials (ref, blob) VALUES (?, ?)').run(next, blob)
  }
}

function parseObject(json: unknown): Record<string, unknown> {
  try {
    const value: unknown = JSON.parse(String(json))
    return typeof value === 'object' && value !== null && !Array.isArray(value)
      ? (value as Record<string, unknown>)
      : {}
  } catch {
    return {}
  }
}
