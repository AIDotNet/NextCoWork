/**
 * 加密配置同步 v2 的 `providers` 快照。
 *
 * 服务端只看到 `config-sync-crypto.ts` 产生的 AES-GCM 信封;本模块处理的明文
 * 只存在于主进程内存。快照把 provider / alias 与它们的凭证放在**同一个原子文档**里,
 * 避免 A 设备先收到配置、下一轮才收到 key 而进入一个暂时必失败的状态。
 *
 * ★ `credentialRef` 仍不上传。云端 map 的键是 provider id;落到另一台设备时
 *   始终用 `providerCredentialRef(id)` 重新派生本机引用,防止远端让 A 指向 B 的密钥。
 * ★ NextCoWork 内置 provider 不参与。它的 credentialRef 指向当前设备登录 JWT,
 *   同步出去既会复制登录身份,也会让另一台设备绕过自己的 OAuth 登录。
 */
import type { ModelAlias, UpstreamProvider } from '../shared/domain/provider'
import { normalizeUpstreamProvider, providerCredentialRef } from '../shared/domain/provider'
import { providerAccountCredentialRef } from '../shared/domain/provider-account'
import { OAUTH_ISSUER_IDS } from '../shared/domain/oauth-issuer'
import { isModelAlias, isProvider } from '../shared/domain/data'
import { MODEL_SYNC_FIELDS } from '../shared/domain/config-sync-registry'
import { CLIENT_PROVIDER_ID } from '../shared/domain/presets'
import type { KernelHost } from './kernel/host'
import * as repo from './db/repo'
import {
  listProviderAccounts,
  putProviderAccount,
  removeProviderAccount
} from './db/provider-accounts'
import { tx, txAsync } from './db'

export interface ProviderSyncDocumentData {
  providers: Array<Omit<UpstreamProvider, 'credentialRef'>>
  aliases: ModelAlias[]
  /** provider id → serializeCredential 后的明文字符串;整个 document 在出站前加密 */
  credentials: Record<string, string>
  /**
   * OAuth 多账号的**元数据**(schema 第 24 条)。只在这家真的挂了**两个以上**
   * 账号时才出现 —— 见 `captureProviderSyncData` 里那段关于旧客户端的说明。
   */
  accounts?: ProviderSyncAccount[]
  /** 本地账号 id → 明文凭证。★ ref 在落地那一侧重新派生,云端不存 ref */
  accountCredentials?: Record<string, string>
}

/**
 * 同步文档里的一个账号。
 *
 * ★★ **没有限流闸门、没有额度快照**,理由和导出包那边逐字相同:它们是
 * 「那台机器在那一刻观察到的上游状态」,同步过来的表现是另一台机器上
 * 一个好端端的账号显示「限流中 · 还有 3 小时」,而用户无从知道那是别处搬来的。
 */
export interface ProviderSyncAccount {
  id: string
  providerId: string
  issuer: string
  label?: string
  order: number
  enabled: boolean
  current: boolean
}

const MAX_SYNC_PROVIDERS = 500
const MAX_SYNC_ALIASES = 10_000
const MAX_CREDENTIAL_BYTES = 1024 * 1024
const PORTABLE_PROVIDER_FIELDS = new Set([
  'id', 'name', 'protocol', 'baseUrl', 'priority', 'enabled', 'protocolOptions'
])
const MODEL_FIELDS = new Set(Object.keys(MODEL_SYNC_FIELDS))

/**
 * v1 云文档 → providers 快照。v1 payload 是各历史版本的 provider/alias JSON,
 * 可能带着早已删除的字段 —— 这里**只挑白名单字段**,而不是像 v2 快照那样整对象
 * 拒绝:迁移的目标是保住用户配置,过时字段本来就该丢。
 * v1 云端从不存密钥,credentials 恒为空。
 */
export function legacyProviderDocumentData(
  documents: ReadonlyArray<{ kind: string; payload: unknown }>
): ProviderSyncDocumentData {
  const providers: ProviderSyncDocumentData['providers'] = []
  const aliases: ModelAlias[] = []
  for (const document of documents) {
    const payload = record(document.payload)
    if (payload === null) continue
    if (document.kind === 'provider') {
      const portable: Record<string, unknown> = {}
      for (const field of PORTABLE_PROVIDER_FIELDS) {
        if (Object.hasOwn(payload, field)) portable[field] = payload[field]
      }
      if (parseProvider(portable) !== null) providers.push(portable as ProviderSyncDocumentData['providers'][number])
    } else if (document.kind === 'modelAlias') {
      const portable: Record<string, unknown> = {}
      for (const field of MODEL_FIELDS) {
        if (Object.hasOwn(payload, field)) portable[field] = payload[field]
      }
      if (parseAlias(portable, new Set(providers.map((provider) => provider.id))) !== null) {
        aliases.push(portable as unknown as ModelAlias)
      }
    }
  }
  return { providers, aliases, credentials: Object.create(null) }
}

export interface ParsedProviderSyncData {
  providers: UpstreamProvider[]
  aliases: ModelAlias[]
  credentials: Record<string, string>
  /** `undefined` = 文档里没有这一段,**不是**「远端把账号都删了」(见 `applyProviderSyncData`) */
  accounts?: ProviderSyncAccount[]
  accountCredentials?: Record<string, string>
}

/**
 * 首次接入云端时做并集,本机同 id/别名优先。用户是在**当前设备**上确认同步,
 * 把他刚配好的 key 被云端旧快照静默覆盖掉比产生一轮新版本更危险。
 */
export function mergeProviderSyncData(
  remoteInput: unknown,
  localInput: unknown
): ProviderSyncDocumentData {
  const remote = parseProviderSyncData(remoteInput)
  const local = parseProviderSyncData(localInput)
  const providers = new Map(remote.providers.map((provider) => [provider.id, provider]))
  for (const provider of local.providers) providers.set(provider.id, provider)

  const aliases = new Map(
    remote.aliases.map((alias) => [`${alias.providerId}\u0000${alias.alias}`, alias])
  )
  for (const alias of local.aliases) aliases.set(`${alias.providerId}\u0000${alias.alias}`, alias)

  return {
    providers: [...providers.values()].map(({ credentialRef: _local, ...provider }) => provider),
    aliases: [...aliases.values()].filter((alias) => providers.has(alias.providerId)),
    credentials: { ...remote.credentials, ...local.credentials },
    /*
      账号同样做并集、本机优先(和上面两张表同一条理由:用户是在**这台**设备上
      确认接入云端的,把他刚登录好的号被云端旧快照顶掉比多产生一轮版本危险得多)。

      ★ 两边都没有这一段时整个字段缺席 —— 不要写成空数组:空数组的意思是
      「远端说一个账号都没有」,而那会让 `applyProviderSyncData` 去清本地的账号。
    */
    ...mergeAccountSections(remote, local, providers)
  }
}

/** 见 `mergeProviderSyncData` 里那段注释。两边都没有账号段时返回空对象 */
function mergeAccountSections(
  remote: ParsedProviderSyncData,
  local: ParsedProviderSyncData,
  providers: ReadonlyMap<string, UpstreamProvider>
): Pick<ProviderSyncDocumentData, 'accounts' | 'accountCredentials'> {
  if (remote.accounts === undefined && local.accounts === undefined) return {}
  const merged = new Map((remote.accounts ?? []).map((account) => [account.id, account]))
  for (const account of local.accounts ?? []) merged.set(account.id, account)
  const accounts = [...merged.values()].filter((account) => providers.has(account.providerId))
  const ids = new Set(accounts.map((account) => account.id))
  const credentials: Record<string, string> = Object.create(null)
  for (const [id, value] of Object.entries({
    ...(remote.accountCredentials ?? {}),
    ...(local.accountCredentials ?? {})
  })) {
    if (ids.has(id)) credentials[id] = value
  }
  return { accounts, accountCredentials: credentials }
}

function record(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null
}

function parseProvider(value: unknown): UpstreamProvider | null {
  const item = record(value)
  if (
    item === null ||
    Object.keys(item).some((key) => !PORTABLE_PROVIDER_FIELDS.has(key)) ||
    typeof item['id'] !== 'string' || item['id'] === CLIENT_PROVIDER_ID || item['id'].length > 128 || item['id'].includes('/') ||
    typeof item['name'] !== 'string' || item['name'].trim() === '' || item['name'].length > 512 ||
    typeof item['baseUrl'] !== 'string' || item['baseUrl'].length > 4096
  ) return null
  const candidate = { ...item, credentialRef: providerCredentialRef(item['id']) }
  if (!isProvider(candidate)) return null
  try {
    const url = new URL(candidate.baseUrl)
    if (url.protocol !== 'https:' && url.protocol !== 'http:') return null
  } catch {
    return null
  }
  return normalizeUpstreamProvider(candidate)
}

function parseAlias(value: unknown, providerIds: ReadonlySet<string>): ModelAlias | null {
  if (
    !isModelAlias(value) ||
    Object.keys(value).some((key) => !MODEL_FIELDS.has(key)) ||
    !providerIds.has(value.providerId) ||
    value.alias.length > 512 ||
    value.upstreamModel.length > 1024
  ) return null
  return structuredClone(value)
}

/** 读取明文后立刻交给加密调用方;返回值不得落库、打日志或发送未加密 HTTP。 */
export async function captureProviderSyncData(host: KernelHost): Promise<ProviderSyncDocumentData> {
  const providers = repo.listProviders().filter((provider) => provider.id !== CLIENT_PROVIDER_ID)
  const providerIds = new Set(providers.map((provider) => provider.id))
  const aliases = repo.listAliases().filter((alias) => providerIds.has(alias.providerId))
  const credentials: Record<string, string> = Object.create(null)
  for (const provider of providers) {
    const value = await host.secrets.get(provider.credentialRef)
    if (value !== null) credentials[provider.id] = value
  }

  /*
    ★★ **多账号只在真的用上时才进同步文档。**

    `parseProviderSyncData` 对**未知顶层键是拒绝的**(那条 `Object.keys(...).some`),
    而它同时也是**旧版本客户端**手里的校验器 —— 所以一旦这个文档里出现
    `accounts`,装着旧版本的那台设备会把**整份 providers 文档**判为无效,
    表现是「另一台电脑上供应商配置同步不过去,报一句结构无效」。

    于是判据定成「这家挂了两个以上账号」:
    - 0 个或 1 个账号(绝大多数用户)= 文档和多账号上线之前**逐字节相同**,
      旧客户端照常工作。那一个账号的凭证本来就在 `credentials[providerId]` 里
      (旧槽镜像,见 `ipc/provider-accounts.ts` 的 `syncLegacyMirror`)。
    - ≥2 个 = 用户确实在用多账号,这时带上全部账号(产品决策 D10),
      代价是旧版本设备在这一项上会报错。**这个取舍必须留在这里**,
      将来所有设备都升上来之后可以把条件去掉。
  */
  const accountRows = listProviderAccounts().filter((row) => providerIds.has(row.providerId))
  if (accountRows.length < 2) {
    return {
      providers: providers.map(({ credentialRef: _local, ...provider }) => structuredClone(provider)),
      aliases: structuredClone(aliases),
      credentials
    }
  }

  const accounts: ProviderSyncAccount[] = accountRows.map((row) => ({
    id: row.id,
    providerId: row.providerId,
    issuer: row.issuer,
    ...(row.label === undefined ? {} : { label: row.label }),
    order: row.order,
    enabled: row.enabled,
    current: row.current
  }))
  const accountCredentials: Record<string, string> = Object.create(null)
  for (const row of accountRows) {
    const value = await host.secrets.get(providerAccountCredentialRef(row.providerId, row.id))
    if (value !== null && value !== '') accountCredentials[row.id] = value
  }

  return {
    providers: providers.map(({ credentialRef: _local, ...provider }) => structuredClone(provider)),
    aliases: structuredClone(aliases),
    credentials,
    accounts,
    accountCredentials
  }
}

/**
 * 用远端完整快照替换当前账户的 provider 配置。凭证删除与写入也在同一 SQLite
 * 事务内；`host.secrets` 的 Electron 实现只做同步 fs/crypto/SQLite 工作并返回
 * resolved Promise,所以 `txAsync` 能保证中途失败时配置与凭证一起回滚。
 */
export function parseProviderSyncData(input: unknown): ParsedProviderSyncData {
  const value = record(input)
  if (
    value === null ||
    Object.keys(value).some(
      (key) =>
        key !== 'providers' && key !== 'aliases' && key !== 'credentials' &&
        key !== 'accounts' && key !== 'accountCredentials'
    ) ||
    !Array.isArray(value['providers']) || value['providers'].length > MAX_SYNC_PROVIDERS ||
    !Array.isArray(value['aliases']) || value['aliases'].length > MAX_SYNC_ALIASES
  ) {
    throw new Error('同步的供应商快照结构无效')
  }
  const credentialValues = record(value['credentials'])
  if (credentialValues === null) throw new Error('同步的凭证快照结构无效')

  const providers = value['providers'].map(parseProvider)
  if (providers.some((provider) => provider === null)) throw new Error('同步的供应商记录无效')
  const parsedProviders = providers as UpstreamProvider[]
  if (new Set(parsedProviders.map((provider) => provider.id)).size !== parsedProviders.length) {
    throw new Error('同步的供应商记录重复')
  }
  const ids = new Set(parsedProviders.map((provider) => provider.id))
  const aliases = value['aliases'].map((alias) => parseAlias(alias, ids))
  if (aliases.some((alias) => alias === null)) throw new Error('同步的模型记录无效')
  const parsedAliases = aliases as ModelAlias[]
  const aliasKeys = parsedAliases.map((alias) => `${alias.providerId}\u0000${alias.alias}`)
  if (new Set(aliasKeys).size !== aliasKeys.length) throw new Error('同步的模型记录重复')

  const credentials: Record<string, string> = Object.create(null)
  for (const [id, credential] of Object.entries(credentialValues)) {
    if (
      !ids.has(id) ||
      typeof credential !== 'string' || credential === '' ||
      Buffer.byteLength(credential, 'utf8') > MAX_CREDENTIAL_BYTES
    ) {
      throw new Error('同步的凭证记录无效')
    }
    credentials[id] = credential
  }

  /*
    账号段是**可选**的:缺席 = 对端还没升级到多账号,或者它那边只有一个账号
    (见 `captureProviderSyncData` 里那段关于旧客户端的说明)。
    缺席**不等于**「远端把账号都删了」—— 那个区别在 `applyProviderSyncData`
    里,搞错的表现是升级早的那台设备每同步一次就把自己的第二个账号弄丢。
  */
  const accounts = parseSyncAccounts(value['accounts'], ids)
  const accountCredentials = parseAccountCredentials(
    value['accountCredentials'],
    accounts === undefined ? undefined : new Set(accounts.map((account) => account.id))
  )

  return {
    providers: parsedProviders,
    aliases: parsedAliases,
    credentials,
    ...(accounts === undefined ? {} : { accounts }),
    ...(accountCredentials === undefined ? {} : { accountCredentials })
  }
}

const MAX_SYNC_ACCOUNTS = 200

/** `undefined` = 文档里没有这一段(不是「空列表」,两者在落地时的处理不同) */
function parseSyncAccounts(
  input: unknown,
  providerIds: ReadonlySet<string>
): ProviderSyncAccount[] | undefined {
  if (input === undefined) return undefined
  if (!Array.isArray(input) || input.length > MAX_SYNC_ACCOUNTS) {
    throw new Error('同步的账号快照结构无效')
  }
  const accounts: ProviderSyncAccount[] = []
  for (const raw of input) {
    const item = record(raw)
    if (
      item === null ||
      typeof item['id'] !== 'string' || item['id'] === '' || item['id'].length > 128 ||
      typeof item['providerId'] !== 'string' || !providerIds.has(item['providerId']) ||
      typeof item['issuer'] !== 'string' || !OAUTH_ISSUER_IDS.includes(item['issuer'] as never) ||
      (item['label'] !== undefined && typeof item['label'] !== 'string') ||
      typeof item['order'] !== 'number' || !Number.isFinite(item['order']) ||
      typeof item['enabled'] !== 'boolean' ||
      typeof item['current'] !== 'boolean'
    ) {
      throw new Error('同步的账号记录无效')
    }
    accounts.push({
      id: item['id'],
      providerId: item['providerId'],
      issuer: item['issuer'],
      ...(item['label'] === undefined ? {} : { label: item['label'] as string }),
      order: item['order'],
      enabled: item['enabled'],
      current: item['current']
    })
  }
  if (new Set(accounts.map((account) => account.id)).size !== accounts.length) {
    throw new Error('同步的账号记录重复')
  }
  return accounts
}

function parseAccountCredentials(
  input: unknown,
  accountIds: ReadonlySet<string> | undefined
): Record<string, string> | undefined {
  if (input === undefined) return undefined
  const values = record(input)
  if (values === null) throw new Error('同步的账号凭证结构无效')
  const credentials: Record<string, string> = Object.create(null)
  for (const [id, credential] of Object.entries(values)) {
    // ★ 认不出归属的凭证一律拒:它会变成一条谁也读不到的密文,
    //   而「读不到」在界面上表现为一个已登录却发不出请求的账号
    if (
      accountIds === undefined || !accountIds.has(id) ||
      typeof credential !== 'string' || credential === '' ||
      Buffer.byteLength(credential, 'utf8') > MAX_CREDENTIAL_BYTES
    ) {
      throw new Error('同步的账号凭证记录无效')
    }
    credentials[id] = credential
  }
  return credentials
}

export async function applyProviderSyncData(host: KernelHost, input: unknown): Promise<void> {
  const parsed = parseProviderSyncData(input)
  const ids = new Set(parsed.providers.map((provider) => provider.id))
  /*
    ★★ **账号行的去留分三种情况,不能合并成一句。**

    1. 文档带了 `accounts` → 远端是权威,整表替换(和 provider/alias 同一条规矩);
    2. 文档**没带**这一段 → 对端还没升级,或它那边只有一个账号。此时
       **保留本地账号行**,只清掉那些所属 provider 已经消失的 ——
       当成「远端把账号删光了」的表现是:升级早的那台设备每同步一次
       就把自己的第二个账号弄丢一次,而用户完全看不出是同步干的。
    3. 无论哪种,provider 被删掉时它名下的账号与密文都要跟着走(孤儿密文)。
  */
  const localAccounts = listProviderAccounts()
  const removedAccounts = parsed.accounts === undefined
    ? localAccounts.filter((row) => !ids.has(row.providerId))
    : localAccounts
  const applyConfig = (): UpstreamProvider[] => {
    const localProviders = repo.listProviders().filter((provider) => provider.id !== CLIENT_PROVIDER_ID)
    for (const alias of repo.listAliases()) {
      if (alias.providerId !== CLIENT_PROVIDER_ID) repo.removeAlias(alias.providerId, alias.alias)
    }
    // ★ 账号行先删:`repo.removeProvider` 不做账号级联(级联住在 `store.removeProvider`,
    //   而这条同步路径刻意只用 repo —— 见本文件头「只处理明文、不碰更高层」的取向)
    for (const row of removedAccounts) removeProviderAccount(row.id)
    for (const provider of localProviders) repo.removeProvider(provider.id)
    for (const provider of parsed.providers) repo.putProvider(provider)
    for (const alias of parsed.aliases) repo.putAlias(alias)
    for (const account of parsed.accounts ?? []) {
      const local = localAccounts.find((row) => row.id === account.id)
      putProviderAccount({
        id: account.id,
        providerId: account.providerId,
        issuer: account.issuer as ReturnType<typeof listProviderAccounts>[number]['issuer'],
        ...(account.label === undefined ? {} : { label: account.label }),
        order: account.order,
        enabled: account.enabled,
        current: account.current,
        // 这台机器自己观察到的那两样不跟远端走(远端根本没传,见 ProviderSyncAccount)
        needsReauth: local?.needsReauth ?? false,
        ...(local?.limit === undefined ? {} : { limit: local.limit }),
        ...(local?.quota === undefined ? {} : { quota: local.quota }),
        createdAt: local?.createdAt ?? Date.now(),
        updatedAt: Date.now()
      })
    }
    return localProviders
  }

  const accountRefs = (parsed.accounts ?? []).map((account) => ({
    ref: providerAccountCredentialRef(account.providerId, account.id),
    credential: parsed.accountCredentials?.[account.id]
  }))
  const removedAccountRefs = removedAccounts.map((row) =>
    providerAccountCredentialRef(row.providerId, row.id)
  )

  if (host.secrets.setSync !== undefined && host.secrets.removeSync !== undefined) {
    repo.withSyncApply(() => tx(() => {
      const localProviders = applyConfig()
      for (const provider of localProviders) host.secrets.removeSync?.(provider.credentialRef)
      for (const ref of removedAccountRefs) host.secrets.removeSync?.(ref)
      for (const id of ids) {
        const credential = parsed.credentials[id]
        if (credential !== undefined) host.secrets.setSync?.(providerCredentialRef(id), credential)
      }
      for (const { ref, credential } of accountRefs) {
        if (credential !== undefined) host.secrets.setSync?.(ref, credential)
      }
    }))
    return
  }

  await repo.withSyncApplyAsync(() => txAsync(async () => {
    const localProviders = applyConfig()
    for (const provider of localProviders) await host.secrets.remove?.(provider.credentialRef)
    for (const ref of removedAccountRefs) await host.secrets.remove?.(ref)
    for (const id of ids) {
      const credential = parsed.credentials[id]
      if (credential !== undefined) await host.secrets.set(providerCredentialRef(id), credential)
    }
    for (const { ref, credential } of accountRefs) {
      if (credential !== undefined) await host.secrets.set(ref, credential)
    }
  }))
}
