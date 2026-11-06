/**
 * 用户自己添加的供应商(+ 模型别名 + 密钥)的文件存储: `~/.next-cowork/providers.json`。
 *
 * 为什么是文件而不是库:外部工具(本地网关、脚本、用户手改)需要一个**能读能写、
 * 不依赖 AES 密文**的入口 —— 和 Claude Code 的 `settings.json` 是同一类东西。
 * 应用本身在这个文件变化时热加载,所以「外部写一条、界面马上出现」。
 *
 * ★ 本文件**零 electron、零 sqlite import**:纯 `node:fs`,无头测试直接跑。
 *
 * 格式(version 1) —— provider 与模型都用「id → 对象」的 map,外部工具可以按 key 路径
 * (`providers.<id>`)整条增删,不用管数组下标:
 *
 * ```json
 * {
 *   "version": 1,
 *   "providers": {
 *     "my-gw": {
 *       "name": "My Gateway",
 *       "protocol": "openai-chat",          // anthropic | openai-chat | openai-responses
 *       "baseUrl": "http://127.0.0.1:8080/v1",
 *       "apiKey": "sk-...",                 // 明文;OAuth/签名凭证用 "credential": {...}
 *       "priority": 50,                     // 缺省 50
 *       "enabled": true,                    // 缺省 true
 *       "models": {
 *         "gpt-4o": { "upstreamModel": "gpt-4o", "displayName": "GPT-4o" }
 *       }
 *     }
 *   }
 * }
 * ```
 *
 * ★★ **没改动的条目必须原样保留**(包括不认识的字段和排版之外的一切),只在
 * 「这一条的有效内容真的变了」时才改写那一条。外部工具会记下它写进去的值,
 * 事后用「当前值是否还等于我写的」判断用户有没有改过 —— 我们这边一次无谓的
 * 「规范化回写」就会让它误判成被改动。
 *
 * ★★ **解析失败不覆盖。** 文件当前是坏的(用户改到一半)时,内存里保留上一份好的,
 * 并拒绝任何写入 —— 否则一次自动保存就会把用户还没改完的内容整个抹掉。
 */
import { createHash } from 'node:crypto'
import {
  chmodSync,
  existsSync,
  mkdirSync,
  readFileSync,
  realpathSync,
  renameSync,
  unlinkSync,
  watch,
  writeFileSync,
  type FSWatcher
} from 'node:fs'
import { basename, dirname } from 'node:path'
import { parseCredential, serializeCredential } from '../../shared/domain/credential'
import {
  IMPORTED_ALIAS_DEFAULTS,
  isUpstreamProtocol,
  normalizeUpstreamProvider,
  providerCredentialRef,
  type ModelAlias,
  type UpstreamProvider
} from '../../shared/domain/provider'

export const PROVIDER_FILE_NAME = 'providers.json'
export const PROVIDER_FILE_VERSION = 1
/** 托管供应商,永远在库里,不进文件。 */
export const MANAGED_PROVIDER_ID = 'nextcowork'

const DEFAULT_PRIORITY = 50
const WATCH_DEBOUNCE_MS = 150
/** `fs.watch` 在满载、网络盘、inotify 额度耗尽时会丢事件;低频兜底重读(读一个小文件 + 比哈希)。 */
const WATCH_POLL_MS = 5000

type JsonObject = Record<string, unknown>

export interface ProviderFileDiagnostic {
  providerId?: string
  message: string
}

export interface ProviderFileChange {
  providers: UpstreamProvider[]
  aliases: ModelAlias[]
}

export interface ProviderFileLegacyData {
  providers: readonly UpstreamProvider[]
  aliases: readonly ModelAlias[]
  /** providerId → 已序列化的明文凭证(`serializeCredential` 的输出) */
  credentials: Readonly<Record<string, string>>
}

interface ParsedProvider {
  provider: UpstreamProvider
  /** 已序列化的凭证(裸 key 或 JSON 串);没配置 = undefined */
  credential: string | undefined
  aliases: ModelAlias[]
}

function isRecord(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function nonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() !== '' ? value : undefined
}

function positiveInt(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? Math.floor(value) : undefined
}

function validProviderId(id: string): boolean {
  // 不用正则:NUL 在 `provider:<id>` 之类的物理 ref 里是分隔符,必须拒绝
  return id.trim() !== '' && !id.includes('\0') && !id.includes('/') && !/\s/.test(id)
}

function clone<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}

function sha256(text: string): string {
  return createHash('sha256').update(text).digest('hex')
}

// ─── 文件条目 ⇄ 领域对象 ─────────────────────────────────────────────────────

/** 模型条目 → ModelAlias。缺省字段按「导入新模型」那套保守默认补齐。 */
function parseModel(providerId: string, alias: string, raw: unknown): ModelAlias {
  const m: JsonObject = isRecord(raw) ? raw : {}
  const capabilities = isRecord(m['capabilities']) ? (m['capabilities'] as Partial<ModelAlias['capabilities']>) : {}
  const out: ModelAlias = {
    alias,
    providerId,
    upstreamModel: nonEmptyString(m['upstreamModel']) ?? alias,
    capabilities: { ...IMPORTED_ALIAS_DEFAULTS.capabilities, ...capabilities },
    contextWindow: positiveInt(m['contextWindow']) ?? IMPORTED_ALIAS_DEFAULTS.contextWindow,
    maxOutputTokens: positiveInt(m['maxOutputTokens']) ?? IMPORTED_ALIAS_DEFAULTS.maxOutputTokens
  }
  const displayName = nonEmptyString(m['displayName']) ?? nonEmptyString(m['name'])
  if (displayName !== undefined) out.displayName = displayName
  if (isUpstreamProtocol(m['protocolOverride'])) out.protocolOverride = m['protocolOverride']
  if (typeof m['priority'] === 'number' && Number.isFinite(m['priority'])) out.priority = m['priority']
  if (typeof m['modality'] === 'string') out.modality = m['modality'] as NonNullable<ModelAlias['modality']>
  if (typeof m['enabled'] === 'boolean') out.enabled = m['enabled']
  if (isRecord(m['thinkingConfig'])) out.thinkingConfig = m['thinkingConfig'] as unknown as NonNullable<ModelAlias['thinkingConfig']>
  if (Array.isArray(m['reasoningEfforts'])) out.reasoningEfforts = m['reasoningEfforts'] as NonNullable<ModelAlias['reasoningEfforts']>
  if (isRecord(m['requestAdapter'])) out.requestAdapter = m['requestAdapter'] as unknown as NonNullable<ModelAlias['requestAdapter']>
  if (isRecord(m['source'])) out.source = m['source'] as unknown as NonNullable<ModelAlias['source']>
  if (isRecord(m['video'])) out.video = m['video'] as unknown as NonNullable<ModelAlias['video']>
  if (Array.isArray(m['catalogOverrides'])) out.catalogOverrides = m['catalogOverrides'] as NonNullable<ModelAlias['catalogOverrides']>
  return out
}

/** ModelAlias → 文件里的模型条目(不含 alias / providerId,它们由 map 的键与父级给出)。 */
function serializeModel(a: ModelAlias): JsonObject {
  const { alias: _alias, providerId: _providerId, upstreamModel, ...rest } = a
  return clone({ upstreamModel, ...rest }) as JsonObject
}

function credentialOf(entry: JsonObject): { credential: string | undefined; error?: string } {
  if (isRecord(entry['credential'])) {
    const serialized = JSON.stringify(entry['credential'])
    const parsed = parseCredential(serialized)
    if (parsed === null || parsed.kind === 'api-key') {
      return { credential: undefined, error: 'credential 不是有效的 oauth / signature 凭证' }
    }
    return { credential: serializeCredential(parsed) }
  }
  const key = nonEmptyString(entry['apiKey'])
  return { credential: key?.trim() }
}

function parseProvider(id: string, raw: unknown, diagnostics: ProviderFileDiagnostic[]): ParsedProvider | undefined {
  if (!validProviderId(id)) {
    diagnostics.push({ providerId: id, message: `供应商 id「${id}」不合法(不能为空、含空白或 "/"),已忽略` })
    return undefined
  }
  if (id === MANAGED_PROVIDER_ID) {
    diagnostics.push({ providerId: id, message: `「${MANAGED_PROVIDER_ID}」是内置托管供应商,不能写在 ${PROVIDER_FILE_NAME} 里,已忽略` })
    return undefined
  }
  if (!isRecord(raw)) {
    diagnostics.push({ providerId: id, message: '条目不是对象,已忽略' })
    return undefined
  }
  const baseUrl = nonEmptyString(raw['baseUrl'])
  if (baseUrl === undefined) {
    diagnostics.push({ providerId: id, message: '缺少 baseUrl,已忽略' })
    return undefined
  }
  if (!isUpstreamProtocol(raw['protocol'])) {
    diagnostics.push({ providerId: id, message: 'protocol 必须是 anthropic / openai-chat / openai-responses,已忽略' })
    return undefined
  }
  const { credential, error } = credentialOf(raw)
  if (error !== undefined) diagnostics.push({ providerId: id, message: error })

  const provider: UpstreamProvider = normalizeUpstreamProvider({
    id,
    name: nonEmptyString(raw['name']) ?? id,
    protocol: raw['protocol'],
    baseUrl: baseUrl.trim(),
    credentialRef: providerCredentialRef(id),
    priority: typeof raw['priority'] === 'number' && Number.isFinite(raw['priority']) ? raw['priority'] : DEFAULT_PRIORITY,
    enabled: typeof raw['enabled'] === 'boolean' ? raw['enabled'] : true,
    ...(isRecord(raw['protocolOptions']) ? { protocolOptions: raw['protocolOptions'] as UpstreamProvider['protocolOptions'] } : {}),
    ...(isRecord(raw['videoGeneration']) ? { videoGeneration: raw['videoGeneration'] as unknown as UpstreamProvider['videoGeneration'] } : {})
  })

  const aliases: ModelAlias[] = []
  if (raw['models'] !== undefined && !isRecord(raw['models'])) {
    diagnostics.push({ providerId: id, message: 'models 必须是 { 别名: 配置 } 的对象,已忽略模型列表' })
  } else if (isRecord(raw['models'])) {
    for (const [alias, model] of Object.entries(raw['models'])) {
      if (alias.trim() === '') continue
      aliases.push(parseModel(id, alias, model))
    }
  }
  return { provider, credential, aliases }
}

/** 文件里 provider 条目的「有效内容」签名 —— 用来判断一次 put 是不是真的改了什么。 */
function providerSignature(p: UpstreamProvider): string {
  const { credentialRef: _ref, ...rest } = normalizeUpstreamProvider(p)
  return canonical(rest)
}

/** 键序无关的稳定序列化(用于「内容是否相同」的比较)。 */
function canonical(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`
  if (isRecord(value)) {
    return `{${Object.keys(value).sort().map((k) => `${JSON.stringify(k)}:${canonical(value[k])}`).join(',')}}`
  }
  return JSON.stringify(value) ?? 'null'
}

// ─── Store ──────────────────────────────────────────────────────────────────

export class ProviderFileStore {
  private doc: JsonObject = { version: PROVIDER_FILE_VERSION, providers: {} }
  private parsed = new Map<string, ParsedProvider>()
  private lastHash: string | undefined
  /** 文件当前不可解析时的原因;非 undefined 时一切写入都被拒绝。 */
  private loadError: string | undefined
  private diagnostics: ProviderFileDiagnostic[] = []
  /** 密钥比供应商先到(同步/导入的顺序不保证)时的暂存区,供应商一出现就并入。 */
  private pendingCredentials = new Map<string, string | null>()
  private watcher: FSWatcher | undefined
  private debounce: NodeJS.Timeout | undefined
  private poll: NodeJS.Timeout | undefined
  private listeners = new Set<(change: ProviderFileChange) => void>()

  constructor(readonly path: string) {}

  // ── 读 ──

  /** 从磁盘(重新)载入。文件不存在 = 空配置;损坏 = 保留上一份并记下原因。 */
  load(): { ok: boolean; error?: string } {
    return this.reload(false)
  }

  get error(): string | undefined {
    return this.loadError
  }

  get problems(): readonly ProviderFileDiagnostic[] {
    return this.diagnostics
  }

  exists(): boolean {
    return existsSync(this.path)
  }

  listProviders(): UpstreamProvider[] {
    return [...this.parsed.values()]
      .map((p) => clone(p.provider))
      .sort((a, b) => a.priority - b.priority || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))
  }

  listAliases(): ModelAlias[] {
    return [...this.parsed.values()]
      .flatMap((p) => p.aliases.map((a) => clone(a)))
      .sort((a, b) =>
        a.providerId < b.providerId ? -1 : a.providerId > b.providerId ? 1 : a.alias < b.alias ? -1 : a.alias > b.alias ? 1 : 0
      )
  }

  hasProvider(id: string): boolean {
    return this.parsed.has(id)
  }

  /** 已序列化的凭证(裸 key 或 JSON 串);没有 = null。 */
  getCredential(providerId: string): string | null {
    return this.parsed.get(providerId)?.credential ?? null
  }

  // ── 写 ──

  putProvider(p: UpstreamProvider): UpstreamProvider {
    const normalized = normalizeUpstreamProvider(p)
    if (normalized.id === MANAGED_PROVIDER_ID) throw new Error('托管供应商不写入文件')
    if (!validProviderId(normalized.id)) throw new Error(`供应商 id 不合法:${normalized.id}`)
    this.mutate((providers) => {
      const existing = this.parsed.get(normalized.id)
      const raw = isRecord(providers[normalized.id]) ? (providers[normalized.id] as JsonObject) : undefined
      if (existing !== undefined && raw !== undefined && providerSignature(existing.provider) === providerSignature(normalized)) {
        return false // 有效内容没变:一个字节都不动
      }
      const next: JsonObject = {
        ...(raw ?? {}),
        name: normalized.name,
        protocol: normalized.protocol,
        baseUrl: normalized.baseUrl,
        priority: normalized.priority,
        enabled: normalized.enabled
      }
      // 这两个可选字段被清掉时,文件里也要清掉,不能残留旧值
      if (normalized.protocolOptions === undefined) delete next['protocolOptions']
      else next['protocolOptions'] = clone(normalized.protocolOptions)
      if (normalized.videoGeneration === undefined) delete next['videoGeneration']
      else next['videoGeneration'] = clone(normalized.videoGeneration)
      const pending = this.pendingCredentials.get(normalized.id)
      if (pending !== undefined) {
        this.applyCredential(next, pending)
        this.pendingCredentials.delete(normalized.id)
      }
      providers[normalized.id] = next
      return true
    })
    return this.parsed.get(normalized.id)?.provider ?? normalized
  }

  /** 删供应商,连同它的模型与密钥(它们都在同一个条目里)。 */
  removeProvider(id: string): void {
    this.pendingCredentials.delete(id)
    this.mutate((providers) => {
      if (!(id in providers)) return false
      delete providers[id]
      return true
    })
  }

  putAlias(a: ModelAlias): ModelAlias {
    if (!this.parsed.has(a.providerId)) throw new Error(`没有这个供应商:${a.providerId}`)
    this.mutate((providers) => {
      const entry = providers[a.providerId]
      if (!isRecord(entry)) return false
      const models: JsonObject = isRecord(entry['models']) ? (entry['models'] as JsonObject) : {}
      const current = this.parsed.get(a.providerId)?.aliases.find((x) => x.alias === a.alias)
      if (current !== undefined && models[a.alias] !== undefined && canonical(serializeModel(current)) === canonical(serializeModel(a))) {
        return false
      }
      models[a.alias] = serializeModel(a)
      entry['models'] = models
      return true
    })
    return a
  }

  removeAlias(providerId: string, alias: string): void {
    this.mutate((providers) => {
      const entry = providers[providerId]
      if (!isRecord(entry) || !isRecord(entry['models']) || !(alias in (entry['models'] as JsonObject))) return false
      delete (entry['models'] as JsonObject)[alias]
      return true
    })
  }

  /** 写凭证(已序列化)。供应商还不存在时先暂存,putProvider 时并入。 */
  setCredential(providerId: string, serialized: string): void {
    if (parseCredential(serialized) === null) throw new Error('凭证格式无效')
    if (!this.parsed.has(providerId)) {
      this.pendingCredentials.set(providerId, serialized)
      return
    }
    this.mutate((providers) => {
      const entry = providers[providerId]
      if (!isRecord(entry)) return false
      if (this.getCredential(providerId) === serialized) return false
      this.applyCredential(entry, serialized)
      return true
    })
  }

  removeCredential(providerId: string): void {
    this.pendingCredentials.delete(providerId)
    if (!this.parsed.has(providerId)) return
    this.mutate((providers) => {
      const entry = providers[providerId]
      if (!isRecord(entry) || (entry['apiKey'] === undefined && entry['credential'] === undefined)) return false
      this.applyCredential(entry, null)
      return true
    })
  }

  /**
   * 把旧库里的数据并入文件(库 → 文件迁移)。**只补文件里还没有的供应商**,
   * 已有的条目一个字节都不动 —— 用户或外部工具在升级之前就手写过 `providers.json`
   * 是完全可能的,此时文件是权威,库里的只是缺的那几条。返回新增的供应商数。
   */
  importLegacy(data: ProviderFileLegacyData): number {
    this.reload(false)
    if (this.loadError !== undefined) throw new Error(this.loadError)
    const providers = (this.doc['providers'] ??= {}) as JsonObject
    let added = 0
    for (const p of data.providers) {
      if (p.id === MANAGED_PROVIDER_ID || !validProviderId(p.id) || p.id in providers) continue
      const entry: JsonObject = {
        name: p.name,
        protocol: p.protocol,
        baseUrl: p.baseUrl,
        priority: p.priority,
        enabled: p.enabled
      }
      if (p.protocolOptions !== undefined) entry['protocolOptions'] = clone(p.protocolOptions)
      if (p.videoGeneration !== undefined) entry['videoGeneration'] = clone(p.videoGeneration)
      const credential = data.credentials[p.id]
      if (credential !== undefined && parseCredential(credential) !== null) this.applyCredential(entry, credential)
      const models: JsonObject = {}
      for (const a of data.aliases.filter((x) => x.providerId === p.id)) models[a.alias] = serializeModel(a)
      if (Object.keys(models).length > 0) entry['models'] = models
      providers[p.id] = entry
      added += 1
    }
    if (added > 0) {
      this.persist()
      this.rebuild()
    }
    return added
  }

  // ── 监听 ──

  /**
   * 监听磁盘上的外部修改。监听的是**目录**而不是文件:编辑器与外部工具常用
   * 「写临时文件再 rename」,对文件本身的 watch 会在 rename 之后悄悄失效。
   * 自己写入引起的回声靠内容哈希挡掉(见 `reload`)。
   */
  onExternalChange(listener: (change: ProviderFileChange) => void): () => void {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  startWatching(): void {
    if (this.watcher !== undefined || this.poll !== undefined) return
    const dir = dirname(this.path)
    mkdirSync(dir, { recursive: true })
    const name = basename(this.path)
    try {
      // Windows 临时目录可能带 RUNNER~1 等 8.3 短名,事件却返回长名;
      // 统一成真实路径,避免 libuv 的 fs-event.c 路径前缀断言直接终止进程。
      this.watcher = watch(realpathSync.native(dir), { persistent: false }, (_event, filename) => {
        if (filename !== null && filename !== undefined && filename.toString() !== name) return
        if (this.debounce !== undefined) clearTimeout(this.debounce)
        this.debounce = setTimeout(() => {
          this.debounce = undefined
          this.reload(true)
        }, WATCH_DEBOUNCE_MS)
        this.debounce.unref?.()
      })
      this.watcher.on('error', () => this.stopWatching())
    } catch {
      // 目录不可监听(权限/文件系统不支持):靠下面的轮询兜底,不抛
      this.watcher = undefined
    }
    this.poll = setInterval(() => this.reload(true), WATCH_POLL_MS)
    this.poll.unref?.()
  }

  stopWatching(): void {
    if (this.debounce !== undefined) clearTimeout(this.debounce)
    this.debounce = undefined
    if (this.poll !== undefined) clearInterval(this.poll)
    this.poll = undefined
    this.watcher?.close()
    this.watcher = undefined
  }

  // ── 内部 ──

  /** 载入磁盘内容。`notify` = 来自监听,内容有变化时通知订阅者。 */
  private reload(notify: boolean): { ok: boolean; error?: string } {
    let text: string | undefined
    try {
      text = existsSync(this.path) ? readFileSync(this.path, 'utf8') : undefined
    } catch (error) {
      return this.fail(error instanceof Error ? error.message : String(error))
    }
    const hash = text === undefined ? 'missing' : sha256(text)
    if (hash === this.lastHash && this.loadError === undefined) return { ok: true }

    let doc: JsonObject = { version: PROVIDER_FILE_VERSION, providers: {} }
    if (text !== undefined && text.trim() !== '') {
      try {
        const value: unknown = JSON.parse(text.charCodeAt(0) === 0xfeff ? text.slice(1) : text)
        if (!isRecord(value)) throw new Error('顶层必须是对象')
        if (value['providers'] !== undefined && !isRecord(value['providers'])) throw new Error('providers 必须是 { id: 配置 } 的对象')
        doc = { version: PROVIDER_FILE_VERSION, ...value, providers: isRecord(value['providers']) ? value['providers'] : {} }
      } catch (error) {
        this.lastHash = hash
        return this.fail(`${PROVIDER_FILE_NAME} 解析失败:${error instanceof Error ? error.message : String(error)}`)
      }
    }
    this.lastHash = hash
    this.loadError = undefined
    this.doc = doc
    const before = notify ? canonical({ p: this.listProviders(), a: this.listAliases(), c: this.credentialsSnapshot() }) : ''
    this.rebuild()
    if (notify && canonical({ p: this.listProviders(), a: this.listAliases(), c: this.credentialsSnapshot() }) !== before) {
      this.emit()
    }
    return { ok: true }
  }

  private fail(message: string): { ok: false; error: string } {
    this.loadError = message
    return { ok: false, error: message }
  }

  private credentialsSnapshot(): Record<string, string | null> {
    return Object.fromEntries([...this.parsed.entries()].map(([id, p]) => [id, p.credential ?? null]))
  }

  private rebuild(): void {
    const diagnostics: ProviderFileDiagnostic[] = []
    const parsed = new Map<string, ParsedProvider>()
    const providers = (this.doc['providers'] ?? {}) as JsonObject
    for (const [id, raw] of Object.entries(providers)) {
      const p = parseProvider(id, raw, diagnostics)
      if (p !== undefined) parsed.set(id, p)
    }
    this.parsed = parsed
    this.diagnostics = diagnostics
  }

  private emit(): void {
    const change: ProviderFileChange = { providers: this.listProviders(), aliases: this.listAliases() }
    for (const listener of this.listeners) {
      try {
        listener(change)
      } catch {
        // 一个订阅者出错不能挡住其余订阅者
      }
    }
  }

  private applyCredential(entry: JsonObject, serialized: string | null): void {
    delete entry['apiKey']
    delete entry['credential']
    if (serialized === null || serialized === '') return
    const parsed = parseCredential(serialized)
    if (parsed === null) return
    if (parsed.kind === 'api-key') entry['apiKey'] = parsed.apiKey
    else entry['credential'] = clone(parsed)
  }

  /**
   * 读-改-写。写之前先按磁盘现状重读一次,因为外部工具可能刚改过而监听事件还在防抖里;
   * 当前文件是坏的就拒绝(见文件头)。`fn` 返回 false 表示没有任何改动,不落盘。
   */
  private mutate(fn: (providers: JsonObject) => boolean): void {
    this.reload(false)
    if (this.loadError !== undefined) throw new Error(this.loadError)
    const providers = (this.doc['providers'] ??= {}) as JsonObject
    if (!fn(providers)) return
    this.persist()
    this.rebuild()
  }

  private persist(): void {
    mkdirSync(dirname(this.path), { recursive: true })
    const text = `${JSON.stringify(this.doc, null, 2)}\n`
    const tmp = `${this.path}.${process.pid}.tmp`
    try {
      writeFileSync(tmp, text, { mode: 0o600 })
      renameSync(tmp, this.path)
    } catch (error) {
      try {
        unlinkSync(tmp)
      } catch {
        // 临时文件本来就没写出来
      }
      throw error
    }
    try {
      chmodSync(this.path, 0o600) // 里面是明文密钥;Windows 上是 no-op
    } catch {
      // 不支持 chmod 的文件系统
    }
    this.lastHash = sha256(text)
    this.loadError = undefined
  }
}
