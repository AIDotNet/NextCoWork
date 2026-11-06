/**
 * 上游供应商与模型别名。配置读取统一解析模型目录和供应商覆盖值。
 *
 * 单独实现这两条,是因为它们是**输入框那颗模型选择器的唯一数据源**:
 * 没有它们,发送行上那颗药丸只能写死一个字符串,而「当前用的是哪个上游、
 * 它支不支持思考」这件事在界面上就永远是假的。
 *
 * 走 `store` 而不是 `getRouter().listModels()`:后者会在别名表之上再叠一层
 * 健康与候选集筛选(方案 §5.3),而下拉框要显示的是**用户配过的全部模型**,
 * 不是「此刻健康的那些」。一个正在冷却的 provider 不该从下拉框里消失 ——
 * 它应该显示出来并标成不可用,否则用户会以为自己的配置丢了。
 *
 * ★ 但 seed 必须先跑。`runtime.getRouter()` 是 seed 的触发点之一,而首屏
 * 拉模型列表时可能还没有任何 run 跑过 —— 于是这里显式调用 `ensureSeeded()`,
 * 否则全新安装点开下拉框是空的,而内置上游明明已经在设置里被指成默认模型了。
 */
import type { AppSettingsPatch } from '../../shared/domain/settings'
import { modelSelectionKey } from '../../shared/domain/model-selection'
import type {
  AnthropicCacheTtl,
  CredentialInfo,
  FetchedModel,
  ModelAlias,
  ProviderProtocolOptions,
  ProviderVideoGeneration,
  RevealedCredential,
  UpstreamProvider
} from '../../shared/domain/provider'
import {
  IMPORTED_ALIAS_DEFAULTS,
  isUpstreamProtocol,
  normalizeAnthropicCacheTtl,
  providerCredentialRef,
  PROTOCOL_LABEL
} from '../../shared/domain/provider'
import { CLIENT_PROVIDER_ID } from '../../shared/domain/presets'
import { removeCredential } from '../db/repo'
import { parseCredential } from '../../shared/domain/credential'
import { modelBindingResolver } from '../../shared/domain/model-binding'
import { catalogDefinitionFromAlias, isModelCatalogDefinition } from '../../shared/domain/model-catalog'
import { defaultProtocolForModel, isImageModelId } from '../../shared/domain/model-catalog-inventory'
import { isVideoAdapterId } from '../../shared/domain/video-generation'
import { listResolvedModels } from '../state/model-bindings'
import {
  modelListErrorMessage,
  modelListRequestFor,
  parseModelList,
  redactSecrets
} from '../kernel/upstream/model-list'
import { opencodeGoProtocolFor } from '../kernel/upstream/opencode-protocol'
import { platformLoginAuth } from '../kernel/upstream/transport'
import { userAgent } from '../kernel/user-agent'
import { ensureSeeded, getHost } from '../runtime'
import { store } from '../state/store'
import { windows } from '../window/registry'

export function listProviders(): UpstreamProvider[] {
  ensureSeeded()
  return store.listProviders()
}

/**
 * `providerId` 省略 = 全部别名。
 *
 * 排序照 provider 的 priority(`listProviders` 已经排好),同一 provider 内
 * 保持配置顺序 —— 下拉框的顺序是用户资产,不该按字典序重排成他不认识的样子。
 */
export function listModels(providerId?: string): ModelAlias[] {
  ensureSeeded()
  return listResolvedModels()
    .filter((a) => providerId === undefined || a.providerId === providerId)
}

/** Update one configured model while preserving provider/alias identity. */
export function updateModel(input: ModelAlias): ModelAlias {
  ensureSeeded()
  if (input.protocolOverride !== undefined && !isUpstreamProtocol(input.protocolOverride)) {
    throw new Error('模型协议无效。')
  }
  const existing = store.listAliases().find(
    (m) => m.providerId === input.providerId && m.alias === input.alias
  )
  if (existing === undefined) throw new Error(`模型不存在: ${input.alias}`)
  const normalized = modelBindingResolver(store.listUserModelCatalog()).update(existing, input)
  if (!isModelCatalogDefinition(catalogDefinitionFromAlias(normalized, {
    manufacturerId: 'provider', manufacturerLabel: 'Provider'
  }))) throw new Error('模型配置无效，请检查能力、输出限制和思考强度。')
  store.putAlias(normalized)
  broadcast()
  return normalized
}

export function removeModel(providerId: string, alias: string): void {
  ensureSeeded()
  store.removeAlias(providerId, alias)
  repointDanglingDefaults()
  broadcast()
}

/** Rename one model alias while keeping its provider binding and runtime configuration intact. */
export function renameModel(
  providerId: string,
  alias: string,
  nextAlias: string,
): ModelAlias {
  ensureSeeded()
  const existing = store
    .listAliases()
    .find((model) => model.providerId === providerId && model.alias === alias)
  if (existing === undefined) throw new Error(`模型不存在: ${alias}`)

  const next = nextAlias.trim()
  if (next === "") throw new Error("模型别名不能为空")
  if (next === alias) return modelBindingResolver(store.listUserModelCatalog()).resolve(existing)
  if (
    store
      .listAliases()
      .some((model) => model.providerId === providerId && model.alias === next)
  ) {
    throw new Error(`模型别名已存在: ${next}`)
  }

  const hasAnotherOriginalAlias = store
    .listAliases()
    .some((model) => model.providerId !== providerId && model.alias === alias)
  store.removeAlias(providerId, alias)
  const renamed = store.putAlias({ ...existing, alias: next })

  const settings = store.getSettings()
  const patch: AppSettingsPatch = {}
  /*
    ★ 设置项**锁定了正是这一家**时,无条件跟着改名 —— 用户改的就是他选中的那一条,
    别处有没有同名别名根本不相干。以前只能靠 `hasAnotherOriginalAlias` 那个折中,
    是因为设置里只存得下一个裸别名、分不出锁的是谁。
  */
  const renames = (model: string, boundTo: string | undefined): boolean =>
    model === alias && (boundTo === providerId || (boundTo === undefined && !hasAnotherOriginalAlias))
  if (renames(settings.defaultModel, settings.defaultModelProviderId)) {
    patch.defaultModel = next
    patch.defaultModelProviderId = settings.defaultModelProviderId
  }
  if (renames(settings.subagent.model, settings.subagent.modelProviderId)) {
    patch.subagent = { model: next, modelProviderId: settings.subagent.modelProviderId }
  }
  if (renames(settings.permissionReviewerModel, settings.permissionReviewerModelProviderId)) {
    patch.permissionReviewerModel = next
    patch.permissionReviewerModelProviderId = settings.permissionReviewerModelProviderId
  }
  if (renames(settings.goalEvaluatorModel, settings.goalEvaluatorModelProviderId)) {
    patch.goalEvaluatorModel = next
    patch.goalEvaluatorModelProviderId = settings.goalEvaluatorModelProviderId
  }
  if (renames(settings.completionModel, settings.completionModelProviderId)) {
    patch.completionModel = next
    patch.completionModelProviderId = settings.completionModelProviderId
  }
  if (renames(settings.inlineCompletionModel, settings.inlineCompletionModelProviderId)) {
    patch.inlineCompletionModel = next
    patch.inlineCompletionModelProviderId = settings.inlineCompletionModelProviderId
  }
  if (Object.keys(patch).length > 0) {
    windows.emitToAll("settings:changed", store.updateSettings(patch))
  }

  broadcast()
  return modelBindingResolver(store.listUserModelCatalog()).resolve(renamed)
}

// ═══════════════════════════════════════════════════════════════
// 写入面(步骤 4)
// ═══════════════════════════════════════════════════════════════

function broadcast(): void {
  windows.emitToAll('provider:changed', {
    providers: store.listProviders(),
    // 广播与 `provider:listModels` 走同一个排序,否则刚拖完在设置页看似没变。
    models: listModels()
  })
}

/**
 * `providers.json` 被外部修改(外部工具、用户手改)并已热加载之后,通知渲染层刷新。
 *
 * ★★ **只广播,不调用 `repointDanglingDefaults`。** 外部写入可能是「先删后加」的中间态,
 * 而 `repointDanglingDefaults` 会把用户的默认模型**永久**改到别处;停用/移除在这里是可逆的
 * (见该函数那段「停用是可逆的」),外部工具重新写回来之后,原来的选择应当自然恢复。
 * 悬空期间最坏的表现是路由找不到候选,不会改写任何设置。
 */
export function onProviderFileChanged(): void {
  broadcast()
}

/**
 * 上游地址的校验。**放在主进程而不是只放在表单里** —— 渲染层的校验是给人看的
 * 提示,不是防线;频道本身可以被直接调用(协议 §3 规则 6 同一个理由)。
 *
 * ★ **只放行 http / https。** 不做白名单也不做黑名单,但 `file:` / `data:`
 * 这类必须挡:baseUrl 会被 `joinUpstreamUrl` 拼进 `net.fetch`,一个
 * `file:///etc/passwd` 的 baseUrl 等于把任意文件读取权交给渲染层,
 * 而这正是 `path-guard.ts` 在文件工具那一侧拦了半天的东西。
 */
function normalizeBaseUrl(raw: string): string {
  const trimmed = raw.trim()
  if (trimmed === '') throw new Error('API 地址不能为空')
  let u: URL
  try {
    u = new URL(trimmed)
  } catch {
    throw new Error(`API 地址不是一个合法的 URL:${trimmed}`)
  }
  if (u.protocol !== 'https:' && u.protocol !== 'http:') {
    throw new Error(`API 地址只能是 http 或 https,收到 ${u.protocol}`)
  }
  return trimmed
}

function record(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined
}

function isAnthropicCacheTtl(value: unknown): value is AnthropicCacheTtl | 'off' {
  // Accept legacy writes; mergeProtocolOptions normalizes `off` to 5m.
  return value === 'off' || value === '5m' || value === '1h'
}

/**
 * Merge JSON-shaped option objects recursively.
 *
 * A protocol option is itself an object and future protocols may add another
 * nested object below it. Treating `protocolOptions` as a shallow object would
 * make a partial update such as `{ future: { transport: { timeout: 30 } } }`
 * silently erase the saved region/auth fields next to `timeout`. Arrays and
 * scalar values remain replacement values; an explicit `undefined` behaves as
 * an omitted legacy field and therefore cannot erase persisted configuration.
 */
function mergeOptionRecords(
  existing: Record<string, unknown>,
  incoming: Record<string, unknown>
): Record<string, unknown> {
  const keys = new Set([...Object.keys(existing), ...Object.keys(incoming)])
  return Object.fromEntries(
    [...keys].flatMap((key) => {
      if (!Object.hasOwn(incoming, key) || incoming[key] === undefined) {
        return Object.hasOwn(existing, key) ? [[key, existing[key]]] : []
      }
      const oldValue = record(existing[key])
      const nextValue = record(incoming[key])
      return [[
        key,
        oldValue !== undefined && nextValue !== undefined
          ? mergeOptionRecords(oldValue, nextValue)
          : incoming[key]
      ]]
    })
  )
}

/** Merge protocol options without letting legacy renderer updates erase them. */
function mergeProtocolOptions(
  existing: ProviderProtocolOptions | undefined,
  incoming: unknown
): ProviderProtocolOptions | undefined {
  if (incoming === undefined) return existing
  const next = record(incoming)
  if (next === undefined) throw new Error('protocolOptions 必须是一个对象')

  const old = record(existing) ?? {}
  const merged = mergeOptionRecords(old, next)
  if (Object.hasOwn(next, 'anthropic')) {
    const raw = next['anthropic']
    if (raw !== undefined) {
      const anthropic = record(raw)
      if (anthropic === undefined) throw new Error('protocolOptions.anthropic 必须是一个对象')
      if (Object.hasOwn(anthropic, 'cacheTtl') && !isAnthropicCacheTtl(anthropic['cacheTtl'])) {
        throw new Error('Anthropic 缓存时长必须是 off、5m 或 1h')
      }
      const mergedAnthropic = record(merged['anthropic']) ?? {}
      merged['anthropic'] = {
        ...mergedAnthropic,
        cacheTtl: normalizeAnthropicCacheTtl(mergedAnthropic['cacheTtl'])
      }
    }
  }
  return Object.keys(merged).length === 0 ? undefined : (merged as ProviderProtocolOptions)
}

/**
 * 校验并规范 `videoGeneration`(视频出网配置)。
 *
 * ★ incoming 为 undefined = 旧版渲染层根本不知道这个字段 —— 保留库里那份,
 *   和 `protocolOptions` 同一条规矩;给了就必须**整体合法**:视频配置描述的是
 *   「把密钥发到哪个地址」,半个字段都含糊不得,校验必须在落库之前完成。
 *
 * ★★ 只重建认识的四个字段(adapter / baseUrl / region / s3),**未知字段一律
 *   不采信** —— 经 IPC 递进来的多余字段落库,等于给未来的读取方埋一份来历
 *   不明的配置。s3 刻意只存桶与前缀,凭据永远在 `credentialRef` 指的槽里。
 *
 * ★ baseUrl 复用 `normalizeBaseUrl`(http/https 白名单的理由见那边),再去掉
 *   尾斜杠 —— `ProviderVideoGeneration.baseUrl` 的规范形式不带尾斜杠,
 *   `kernel/upstream/video/*` 的适配器直接在它后面拼 path。
 *
 * ★ 可选段(region / s3.prefix / s3.region)trim 之后空串视同没填,不落库;
 *   s3.bucket 允许空串 —— 内置 AWS 预设先存空桶,用户选好 region 之后才填桶名。
 *   全对象更新可以替换这些可选段;需要「保留」的只有整个字段省略这一种情况。
 */
function mergeVideoGeneration(
  existing: ProviderVideoGeneration | undefined,
  incoming: unknown
): ProviderVideoGeneration | undefined {
  if (incoming === undefined) return existing
  const raw = record(incoming)
  if (raw === undefined) throw new Error('videoGeneration 必须是一个对象')
  const adapter = raw['adapter']
  if (!isVideoAdapterId(adapter)) throw new Error(`未知的视频适配器:${String(adapter)}`)
  if (typeof raw['baseUrl'] !== 'string') throw new Error('视频 API 地址必须是字符串')
  const baseUrl = normalizeBaseUrl(raw['baseUrl']).replace(/\/+$/, '')

  const regionRaw = raw['region']
  if (regionRaw !== undefined && typeof regionRaw !== 'string') throw new Error('视频地域必须是字符串')
  const region = typeof regionRaw === 'string' ? regionRaw.trim() : undefined

  let s3: ProviderVideoGeneration['s3']
  if (raw['s3'] !== undefined) {
    const s3Raw = record(raw['s3'])
    if (s3Raw === undefined) throw new Error('videoGeneration.s3 必须是一个对象')
    const bucket = s3Raw['bucket']
    if (typeof bucket !== 'string') throw new Error('videoGeneration.s3.bucket 必须是字符串')
    const prefixRaw = s3Raw['prefix']
    if (prefixRaw !== undefined && typeof prefixRaw !== 'string') {
      throw new Error('videoGeneration.s3.prefix 必须是字符串')
    }
    const s3RegionRaw = s3Raw['region']
    if (s3RegionRaw !== undefined && typeof s3RegionRaw !== 'string') {
      throw new Error('videoGeneration.s3.region 必须是字符串')
    }
    const prefix = typeof prefixRaw === 'string' ? prefixRaw.trim() : undefined
    const s3Region = typeof s3RegionRaw === 'string' ? s3RegionRaw.trim() : undefined
    s3 = {
      bucket,
      ...(prefix !== undefined && prefix !== '' ? { prefix } : {}),
      ...(s3Region !== undefined && s3Region !== '' ? { region: s3Region } : {})
    }
  }

  return {
    adapter,
    baseUrl,
    ...(region !== undefined && region !== '' ? { region } : {}),
    ...(s3 === undefined ? {} : { s3 })
  }
}

/**
 * 新建或更新一个上游供应商。
 *
 * ★★ **`credentialRef` 一律不采信渲染层传来的值。**
 * 它是入参 `UpstreamProvider` 的一个字段,所以渲染层**能**填 —— 但填了就意味着
 * 「供应商 A 可以指向供应商 B 的密钥」。渲染层可以在显式动作下查看值,
 * 但本来就不该知道或决定存储引用。所以:
 * - 已存在的:**保留库里那条的 ref**(改名换地址不该弄丢已经存好的 key);
 * - 新建的:主进程自己派生 `provider:<id>`。
 *
 * 内置上游的 ref 因此也稳得住 —— 它建表时用的是别的前缀,改地址不会把它冲掉。
 *
 * ★★ **NextCoWork 那条走「字段级托管」,不是整条拒绝。**
 * 它的 `name` / `baseUrl` / `credentialRef` 归登录流程(`client-auth.ts`),
 * 而 `protocol` / `protocolOptions` / `priority` / `enabled` 归用户 ——
 * 用户要能翻「API 格式」那个开关。地址锁死不是保守:`credentialRef` 里存的是
 * **平台发的 access token**,放开地址等于允许把它发到任意主机去。
 *
 * 托管值取自**库里那条**,不是从 `client-auth.ts` import 常量:那个模块
 * `import { shell } from 'electron'` 是值导入,而本文件被 `provider-write.test.ts`
 * 直接 import、跑在 node 环境里 —— 拉进来整个测试文件就起不来了。
 */
export function upsertProvider(input: UpstreamProvider): UpstreamProvider {
  ensureSeeded()
  const id = input.id.trim()
  if (id === '') throw new Error('供应商 id 不能为空')
  /*
    ★ 斜杠会让 `modelSelectionKey`(`providerId/alias`)歧义:那个复合键靠
    「第一个斜杠是分隔符」来解析,而别名里的斜杠(`openrouter/claude-sonnet-4`)
    正是靠这条规矩才得以保留。id 里再有一个的话,parse 出来的供应商就是半截。
    今天所有 id 都来自预设表的 slug,这一行是把那个隐含前提**变成硬约束**。
  */
  if (id.includes('/')) throw new Error('供应商 id 不能包含斜杠')
  // 合法协议 = `PROTOCOL_LABEL` 的键。不另立一个 UPSTREAM_PROTOCOLS 数组:
  // 两份全集迟早漂开,而漏掉的那个协议会在这里被当成非法值拒掉
  if (!Object.hasOwn(PROTOCOL_LABEL, input.protocol)) {
    throw new Error(`未知的上游协议:${String(input.protocol)}`)
  }

  const existing = store.listProviders().find((p) => p.id === id)
  /*
    ★ 这条只能由登录流程建出来。渲染层凭空 PUT 一个 `nextcowork` 的话,
    它的 `credentialRef` 会被派生成 `provider:nextcowork` —— 一条永远拿不到
    access token、却顶着内置身份(删不掉、密钥栏没有输入框)的死记录。
  */
  const managed = id === CLIENT_PROVIDER_ID
  if (managed && existing === undefined) throw new Error('请先登录 NextCoWork 账号')
  // 托管那条的三个字段一律回落到库里那份;别的供应商 `platform` 是 undefined,照常走入参
  const platform = managed ? existing : undefined
  const name = platform?.name ?? input.name.trim()
  if (name === '') throw new Error('供应商名称不能为空')
  const baseUrl = platform?.baseUrl ?? normalizeBaseUrl(input.baseUrl)

  const protocolOptions = mergeProtocolOptions(existing?.protocolOptions, input.protocolOptions)
  /*
    ★★ 托管那条**不接受传入的视频路由**,一律沿用库里那份。视频配置描述的正是
    「把凭证发往哪个地址」,而 NextCoWork 这条的凭证是平台发的 access token ——
    采信渲染层的 `videoGeneration.baseUrl`,等于允许把这个 token 发往任意主机,
    和地址锁死(`platform?.baseUrl`)是同一条边界,所以入参一个字不看。
  */
  const videoGeneration = managed
    ? existing?.videoGeneration
    : mergeVideoGeneration(existing?.videoGeneration, input.videoGeneration)
  const saved = store.putProvider({
    id,
    name,
    protocol: input.protocol,
    baseUrl,
    credentialRef: existing?.credentialRef ?? providerCredentialRef(id),
    priority: Number.isFinite(input.priority) ? input.priority : 50,
    enabled: input.enabled,
    ...(protocolOptions === undefined ? {} : { protocolOptions }),
    ...(videoGeneration === undefined ? {} : { videoGeneration })
  })
  broadcast()
  return saved
}

/**
 * 删掉一个供应商,连同它的别名。
 *
 * ★ 密钥**一起删**。留着的话下次有人建一个同 id 的供应商,会静默继承上一个的 key ——
 * 用户以为自己在配一个全新的上游,实际用的是早就该失效的凭证。
 */
export function removeProvider(id: string): void {
  ensureSeeded()
  if (id === CLIENT_PROVIDER_ID) throw new Error('内置供应商不能删除')
  const target = store.listProviders().find((p) => p.id === id)
  if (target === undefined) return
  for (const a of store.listAliases().filter((a) => a.providerId === id)) {
    store.removeAlias(a.providerId, a.alias)
  }
  store.removeProvider(id)
  /**
   * ★ 走 `db/repo` 的 removeCredential,**不是** `secrets.set(ref, '')`。
   * 后者需要读取主密钥并做一次无意义的加密,主密钥损坏时会让删除失败;
   * 删现有密文不需要加密能力。
   */
  removeCredential(target.credentialRef)
  repointDanglingDefaults()
  broadcast()
}

/**
 * 删掉供应商之后,把指向已消失别名的两个设置项接回来。
 *
 * ★★ **这必须在主进程做,不能留给设置页。** `defaultModel` 是一个 alias 字符串
 * (方案 §1.4 明确保留这个形状),删掉它所属的供应商之后它就悬空了 —— 而悬空的
 * 表现不是一句报错,是**下一次发送在路由器里找不到候选**。用户刚做的事(删一个
 * 不用的供应商)和看到的现象(对话发不出去)之间没有任何可见的联系。
 *
 * 放在 `removeProvider` 里而不是渲染层,是因为渲染层不是唯一的调用点,而
 * 「删完之后设置仍然自洽」是这条写入本身的一部分,不是界面的责任。
 *
 * 接到**剩下的第一条别名**(`listModels` 已按 provider 的 priority 排好),
 * 一条都不剩就置空 —— `defaultModel: ''` 是合法值,界面显示「跟随对话」。
 *
 * ★ 改完要自己发 `settings:changed`。`store.updateSettings` **不广播** ——
 * 广播在 `ipc/settings.ts` 那个 handler 里,而这条写入不走它。少了这一句,
 * 库里已经接好了、而设置页和输入框那颗药丸还显示着删掉的那个别名。
 */
/**
 * 一个「模型别名 + 供应商」配对在别名表变动之后该怎么修。纯函数,好单测。
 *
 * ★★ **一次都不做跨供应商的静默回退。** 用户选的那家没了,唯一允许的降级是把
 * 供应商抹成 `undefined` —— 那不是「换一家给你」,是回到「没有指定」这个明确状态,
 * 而这个状态的语义(按 priority 择优)全系统只有一份定义。静默换家正是这整套
 * 改动要消灭的那个 bug。
 *
 * ★ 判据用的是 `listAliases()`(**不看供应商启没启用**):停用是可逆的,用户
 * 明天会开回来。这时改写他的选择,等于他重新启用之后发现自己选的东西没了。
 *
 * @returns undefined = 不用改。否则是要写进设置的新配对。
 */
export function repairModelSelection(
  alias: string,
  providerId: string | undefined,
  aliveBindings: ReadonlySet<string>,
  aliveAliases: ReadonlySet<string>,
  fallback: { model: string; modelProviderId: string | undefined }
): { model: string; modelProviderId: string | undefined } | undefined {
  if (alias === '') return undefined
  if (providerId !== undefined) {
    if (aliveBindings.has(modelSelectionKey(providerId, alias))) return undefined
    // 那家没了,但别名还在别处 —— 只解锁,不改名。
    if (aliveAliases.has(alias)) return { model: alias, modelProviderId: undefined }
    return fallback
  }
  return aliveAliases.has(alias) ? undefined : fallback
}

function repointDanglingDefaults(): void {
  const aliases = store.listAliases()
  const aliveAliases = new Set(aliases.map((a) => a.alias))
  const aliveBindings = new Set(aliases.map((a) => modelSelectionKey(a.providerId, a.alias)))
  const before = store.getSettings()
  const patch: AppSettingsPatch = {}

  const first = listModels()[0]
  const fixed = repairModelSelection(before.defaultModel, before.defaultModelProviderId,
    aliveBindings, aliveAliases, { model: first?.alias ?? '', modelProviderId: first?.providerId })
  if (fixed !== undefined) {
    patch.defaultModel = fixed.model
    patch.defaultModelProviderId = fixed.modelProviderId
  }
  // 子代理模型的空串是「跟随主对话」,本来就是合法的,所以悬空时置空即可
  const sub = repairModelSelection(before.subagent.model, before.subagent.modelProviderId,
    aliveBindings, aliveAliases, { model: '', modelProviderId: undefined })
  if (sub !== undefined) patch.subagent = { model: sub.model, modelProviderId: sub.modelProviderId }
  /*
    ★ 审核模型以前**根本没被这个函数照顾**,是个存量漏洞:删掉它所属的供应商之后
    它一样悬空,而症状更隐蔽 —— 「为我批准」档位会静默退回人工审批
    (`reviewSensitiveOperation` 查不到模型就返回 unknown),用户只会觉得
    「怎么又开始问我了」,不会联想到几天前删过一个供应商。
  */
  const reviewer = repairModelSelection(before.permissionReviewerModel,
    before.permissionReviewerModelProviderId, aliveBindings, aliveAliases,
    { model: '', modelProviderId: undefined })
  if (reviewer !== undefined) {
    patch.permissionReviewerModel = reviewer.model
    patch.permissionReviewerModelProviderId = reviewer.modelProviderId
  }
  // 判定模型的空串同样是「回落到本次 run 的模型」,悬空时置空即可
  const goalEvaluator = repairModelSelection(before.goalEvaluatorModel,
    before.goalEvaluatorModelProviderId, aliveBindings, aliveAliases,
    { model: '', modelProviderId: undefined })
  if (goalEvaluator !== undefined) {
    patch.goalEvaluatorModel = goalEvaluator.model
    patch.goalEvaluatorModelProviderId = goalEvaluator.modelProviderId
  }
  // 建议下一步的空串是「跟随输入框的模型」,同判定模型:悬空时置空即可
  const completion = repairModelSelection(before.completionModel,
    before.completionModelProviderId, aliveBindings, aliveAliases,
    { model: '', modelProviderId: undefined })
  if (completion !== undefined) {
    patch.completionModel = completion.model
    patch.completionModelProviderId = completion.modelProviderId
  }
  /*
    ★ 打字续写和视频同一个降级方向:**绑定失效就清空整对**,不解锁成裸别名。
    它每次停顿都发一次请求,静默换到另一家的同名模型,账单会在用户毫无察觉时
    换一家累积;空串在这一栏的意思是「未选择、续写不生效」,正是安全的那一头。
  */
  if (before.inlineCompletionModel !== '') {
    const aliveInline = before.inlineCompletionModelProviderId !== undefined
      ? aliveBindings.has(modelSelectionKey(before.inlineCompletionModelProviderId, before.inlineCompletionModel))
      : aliveAliases.has(before.inlineCompletionModel)
    if (!aliveInline) {
      patch.inlineCompletionModel = ''
      patch.inlineCompletionModelProviderId = undefined
    }
  }
  /*
    ★ 视频模型和上面几个的降级方向不同:**绑定失效就清空整对**,不解锁成裸别名、
    更不换另一家同名模型。视频按秒/分辨率计费,静默换家的代价比对话模型更大
    (见 settings.ts `videoModel` 那段);而且视频出网还要看供应商的
    `videoGeneration` 配置 —— 同名别名挂在别家,多半根本出不了网。所以有明确的
    绑定失效时,唯一允许的降级是回到「没选过」(`videoModel: ''`)。
    `videoModelProviderId` 缺席的旧数据沿用运行时的解析口径:别名还指得到就不动,
    彻底消失才清空。为空(= 没选过)时一个字不动。
  */
  if (before.videoModel !== '') {
    const aliveVideo = before.videoModelProviderId !== undefined
      ? aliveBindings.has(modelSelectionKey(before.videoModelProviderId, before.videoModel))
      : aliveAliases.has(before.videoModel)
    if (!aliveVideo) {
      patch.videoModel = ''
      patch.videoModelProviderId = undefined
    }
  }
  if (Object.keys(patch).length === 0) return

  windows.emitToAll('settings:changed', store.updateSettings(patch))
}

/**
 * 从上游拉「这家有哪些模型」。参考图那颗「从服务商拉取模型列表」。
 *
 * 三处协议差异(路径 / 鉴权头 / 分页)全在 `kernel/upstream/model-list.ts` 里,
 * 那边是纯函数、有测试。这里只负责**取密钥、发请求、把失败翻译成人话**。
 *
 * ★ **不照预设表的 `supportsModelList` 拒绝。** 那个标记是我们某一天实测到的
 * 形状,而用户可能已经把地址改成了自己的中转 —— 照一张快照拒绝,等于让一个
 * 明明能用的端点永远拉不了,且没有任何绕过办法。置灰按钮是**界面**的事
 * (那里还能看见地址有没有被改过),这里试了再说。
 *
 * ★ 没有密钥时**不是直接失败**:预设表里 `modelListPublic` 的那几家
 * (OpenRouter / DeepInfra 实测 200)免鉴权就能拉,于是「key 还没填先看看
 * 有哪些模型」这条路是通的。带不带头的判断在 `modelListRequestFor` 里。
 */
export async function fetchModels(providerId: string): Promise<FetchedModel[]> {
  ensureSeeded()
  const p = store.listProviders().find((x) => x.id === providerId)
  if (p === undefined) throw new Error(`没有这个供应商:${providerId}`)

  /*
    ★ 必须过 `parseCredential`,不能把库里的原始字符串直接当 key 用。
    OAuth 凭证在库里是一段 JSON —— 直接拼进 `Bearer` 会发出一整个 JSON 对象。
    今天走 OAuth 的那家 `supportsModelList` 是 false、走不到这里,但正确性
    不能只在「今天用得到的路径」上成立:哪天多一家 OAuth 且有模型列表端点,
    这里会安静地发出一个畸形请求,而症状是一个看不懂的 401。
  */
  const cred = parseCredential(await getHost().secrets.get(p.credentialRef))
  /*
    ★★ 鉴权形状收口在 `modelListRequestFor` 里,和对话请求共用同一套来源
    (issuer 的 `transport`)。签名式凭证(keypair-binding,今天 ollama-cloud)
    的 accessToken 槽是 **SSH 私钥 PEM** —— 拿 `bearerOf` 拼 Bearer 会得到
    一个非法头值(`Headers.append` 抛 TypeError,私钥整段进错误弹窗,
    2026-09-15 实测事故),这里由 `signRequest` 现场签名解决。
  */
  const { url, headers } = modelListRequestFor(p, cred)
  /*
    ★ 平台那条上游的凭证是登录 JWT,只有 `Authorization: Bearer` 认它 —— 和发对话
    请求走的是同一条规矩(`kernel/upstream/transport.ts`),漏在这里的表现是
    「API 格式」选 Anthropic 时那颗「从服务商拉取模型列表」按钮永远 401。
    ★ 只有 API Key 凭证走这里:平台那条没有 oauthIssuer,登录态也是以 api-key
    形态存的(`ipc/client-auth.ts`),`cred.kind !== 'api-key'` 时不可能是它。
  */
  const platform = cred === null || cred.kind !== 'api-key' ? null : platformLoginAuth(p.id, cred.apiKey)
  if (platform !== null) {
    for (const name of platform.dropHeaders) delete headers[name]
    Object.assign(headers, platform.headers)
  }

  let res: Response
  try {
    /*
      ★ 必须有超时。没有的话,一个不回包的地址(打错的域名、被墙的主机)会让
      导入弹窗一直转下去 —— 用户唯一的出路是关掉应用。20 秒足够慢网络拉一页,
      又短到不至于让人以为卡死了。
    */
    res = await getHost().fetch(url, {
      // `modelListRequest` 是纯函数、拿不到版本号,自报家门在调用点补(kernel/user-agent.ts)
      headers: { ...headers, 'user-agent': userAgent() },
      signal: AbortSignal.timeout(20_000)
    })
  } catch (e) {
    /*
      ★★ `redactSecrets` 是兜底:上面那次事故里一行 `String(e)` 把整段私钥送进了
      弹窗。根治是凭证不进头(modelListRequestFor),但任何未来的「头里带了不该
      带的东西」都先在这里被拦成 `[已隐去的密钥]`。
    */
    const reason =
      e instanceof Error && e.name === 'TimeoutError' ? '超时(20 秒)' : redactSecrets(String(e))
    // cause 留着原始的 fetch 错误:上面那句是给用户看的,而 ECONNREFUSED / 证书失败
    // 这类真正的区别只在原始异常里
    throw new Error(`连不上 ${url} —— ${reason}`, { cause: e })
  }

  if (!res.ok) {
    // ★ body 必须读完,否则连接不会被释放(和 `router.ts` 那处同一个理由)
    const text = await res.text().catch(() => '')
    throw new Error(modelListErrorMessage(res.status, text))
  }

  const payload: unknown = await res.json().catch(() => null)
  const models = parseModelList(p.protocol, payload)
  if (models.length === 0) {
    // 200 但一条都解不出来 —— 报清楚这是「解析不出」而不是「这家没有模型」
    throw new Error(`${url} 返回了 200,但响应里没有能识别的模型列表`)
  }
  return models
}

/**
 * 整表替换这家的模型别名 —— 导入弹窗点「更新列表」时走这条。
 *
 * ★★ **替换语义**:调用完之后这家的别名 = `models` 里那些,一个不多一个不少。
 * 参考图那句「取消勾选会从当前列表删除」就是它。
 *
 * ★ **已有的那条整行留着,只按 `upstreamModel` 认。** 用户可能给它改过别名、
 * 调过上下文窗口(将来那个编辑入口通了之后)—— 重新导入一次就把这些冲掉,
 * 等于每次点「拉取」都在悄悄重置他的配置。所以匹配用 `upstreamModel`,
 * 命中就保留供应商覆盖值；新模型通过模型目录补齐能力和请求配置。
 *
 * ★ 末尾必须 `repointDanglingDefaults()`:取消勾选掉的那个可能正是
 * `settings.defaultModel`,而它悬空的表现不是报错,是**下一次发送在路由器里
 * 找不到候选**(理由见那个函数的文件头)。
 */
export function setAliases(providerId: string, models: readonly string[]): ModelAlias[] {
  ensureSeeded()
  const p = store.listProviders().find((x) => x.id === providerId)
  if (p === undefined) throw new Error(`没有这个供应商:${providerId}`)

  // 去重后再判上限:重复项是渲染层的 bug,不该变成一句「超过 20 个」的假报错
  const wanted: string[] = []
  const seen = new Set<string>()
  for (const raw of models) {
    const m = raw.trim()
    if (m === '' || seen.has(m)) continue
    seen.add(m)
    wanted.push(m)
  }
  // 模型管理控制台支持完整的供应商目录，不再限制为 20 条。

  const before = store.listAliases().filter((a) => a.providerId === providerId)
  const resolver = modelBindingResolver(store.listUserModelCatalog())
  const keep = new Map<string, ModelAlias>()
  for (const a of before) if (!keep.has(a.upstreamModel)) keep.set(a.upstreamModel, a)

  for (const a of before) {
    if (!seen.has(a.upstreamModel) || keep.get(a.upstreamModel) !== a) {
      store.removeAlias(providerId, a.alias)
    }
  }
  for (const [priority, m] of wanted.entries()) {
    const existing = keep.get(m)
    /*
      ★ OpenCode Go 的协议**按模型钉**(见 `kernel/upstream/opencode-protocol.ts`)。
      那家三个端点共用一个 base,出厂协议只能选一个,而它对表里三分之二的模型是错的 ——
      不钉的话,用户拉完列表得到的一批模型里有一批是坏的,且报错是一句
      「Internal server error」,读不出和协议有任何关系。

      ★ 厂商默认协议(claude 系 → anthropic 线形,表在 `model-catalog-inventory`)
      是同一个问题的通用版:聚合站常用一个 openai-chat 端点同时卖 claude 和 gpt,
      不钉的话 claude 那几条继承供应商协议,Claude 的思考/缓存语义在兼容层里
      说没就没。**只在默认值与供应商协议不同**时钉 —— 供应商本来就是 anthropic
      的话,钉一个相同的值只会在「协议」下拉里制造一个看不出差别的显式覆盖。

      ★★ 只钉**新建**的那支。已存在的别名一个字不改 —— 用户可能在「协议」下拉里
      自己选过,而重拉一次列表把他的选择顶掉,和这个函数「命中就保留供应商覆盖值」
      的既有语义直接冲突。老库里那些协议为空的,由 `runtime.ts` 的一次性回填管。
    */
    const manufacturerDefault = defaultProtocolForModel(m)
    const pinned = existing === undefined
      ? opencodeGoProtocolFor(p, m) ??
        (manufacturerDefault !== undefined && manufacturerDefault !== p.protocol ? manufacturerDefault : undefined)
      : undefined
    // ★ 别名默认等于上游模型名(和 seed 那条一致)。这里不加任何前缀/后缀 ——
    // 别名是用户在药丸和 `defaultModel` 里看见的字符串,加工过就对不上他在上游文档里读到的名字
    /*
      ★★ 需求:拉取模型列表时,**生图模型要自动带上 image 模态**。模型列表端点
      一律不报模态,目录又只认得已收录的 ID —— 预览期/私有的生图 ID(如
      `grok-imagine-image-edit`)落下来就是一条普通文本模型:「图片生成」页按
      `modality === 'image' || imageOutput` 过滤,看不见它,零报错,用户会以为
      这家没有生图模型。判据用共享的 `isImageModelId`(目录优先、名字启发式兜底),
      和图片页导入弹窗的过滤是同一条 —— 弹窗里能勾上的,存进来就是图片模型。

      ★ 只钉**新建**的那支,已存在的一个字不改(同上一条协议钉法的理由):
      用户在「编辑模型」里把这个模态改回 text 是显式覆盖,重拉一次列表不许顶掉。
      ★ 目录命中时 `resolver.resolve` 会用目录的 modality/capabilities 覆盖这里
      写的值,所以这里只对目录不认识的 ID 起作用 —— 正是要救的那批;能力位照
      目录里 image 行的形状给(`textOutput: false`、`imageOutput: true`,
      `textInput` 留 true:生图模型收的就是文本 prompt)。
    */
    const imageImported = existing === undefined && isImageModelId(m)
    store.putAlias(resolver.resolve(
      existing === undefined
        ? {
            ...IMPORTED_ALIAS_DEFAULTS,
            alias: m,
            providerId,
            upstreamModel: m,
            priority,
            ...(pinned === undefined ? {} : { protocolOverride: pinned }),
            ...(imageImported
              ? {
                  modality: 'image' as const,
                  capabilities: { ...IMPORTED_ALIAS_DEFAULTS.capabilities, textOutput: false, imageOutput: true }
                }
              : {}),
            catalogOverrides: []
          }
        : { ...existing, priority }
    ))
  }

  repointDanglingDefaults()
  broadcast()
  return listModels(providerId)
}

/**
 * 把库里那个字符串翻译成设置页能看的东西。
 *
 * ★ OAuth 凭证的 `last4` 是 **null**。access token 的后四位每小时都变,
 * 对常规状态展示零识别价值。完整 token 只由显式 `revealCredential` 返回,
 * 这里仍一个字符都不带,避免列表刷新把明文常驻窗口状态。
 */
export function infoFor(plaintext: string | null): CredentialInfo {
  const available = getHost().secrets.available()
  const cred = parseCredential(plaintext)
  if (cred === null) {
    return { hasKey: false, last4: null, encryptionAvailable: available }
  }
  if (cred.kind === 'api-key') {
    // 常规状态只回后四位;完整明文只走显式 revealCredential
    return { hasKey: true, last4: cred.apiKey.slice(-4), encryptionAvailable: available }
  }
  /*
    ★★ 签名凭证(腾讯 TC3 / AWS SigV4)走**另一支**,原因有两条且都不容商量:

    1. `last4` 对它无意义且有害 —— 它会露出 **SecretKey 的尾四位**,而签名凭证的
       身份标识是 `accessKeyId`(那不是秘密)。所以这里回 `accessKeyId`,
       并且**不回** last4。
    2. 它没有 `issuer`/`expiresAt` 这些东西,硬塞进 `auth`(OAuth 的形状)会让
       设置页画出"已登录 / 已过期"那一套对签名凭证根本不存在的状态。
  */
  if (cred.kind === 'signature') {
    return {
      hasKey: true,
      last4: null,
      encryptionAvailable: available,
      signatureScheme: cred.scheme,
      accessKeyId: cred.accessKeyId
    }
  }
  return {
    hasKey: true,
    last4: null,
    encryptionAvailable: available,
    auth: {
      issuer: cred.issuer,
      accountId: cred.accountId,
      ...(cred.email === undefined ? {} : { email: cred.email }),
      ...(cred.planType === undefined ? {} : { planType: cred.planType }),
      expiresAt: cred.expiresAt,
      // ★ 过期与否在这里算完再回传,渲染层不碰时钟(见 CredentialAuthInfo 的注释)
      // ★★ 过期时间未知(null)时一律答「没过期」—— 见那边关于「不知道」的注释
      expired: cred.expiresAt !== null && cred.expiresAt <= getHost().clock.now(),
      needsReauth: cred.needsReauth === true
    }
  }
}

/** 存一把密钥。`isEncryptionAvailable() === false` 时 host 会抛错拒绝,不做明文降级 */
export async function setCredential(providerId: string, apiKey: string): Promise<CredentialInfo> {
  ensureSeeded()
  const p = store.listProviders().find((x) => x.id === providerId)
  if (p === undefined) throw new Error(`没有这个供应商:${providerId}`)
  if (providerId === CLIENT_PROVIDER_ID) throw new Error('NextCoWork 内置提供商的登录凭证不能手动修改')
  const key = apiKey.trim()
  if (key === '') throw new Error('密钥不能为空')
  await getHost().secrets.set(p.credentialRef, key)
  return infoFor(key)
}

export async function getCredentialInfo(providerId: string): Promise<CredentialInfo> {
  ensureSeeded()
  const p = store.listProviders().find((x) => x.id === providerId)
  if (p === undefined) throw new Error(`没有这个供应商:${providerId}`)
  return infoFor(await getHost().secrets.get(p.credentialRef))
}

/**
 * 显式查看凭证。和 `getCredentialInfo` 分开是安全边界:列表刷新永远只拿摘要,
 * 明文只在用户点了查看按钮的那一次请求中跨过 IPC。
 *
 * OAuth 的 access / refresh token 和内置供应商的登录 token 也允许查看 ——
 * 这是产品选择,所以这里不按 providerId 或 credential kind 做额外隐藏。
 */
export async function revealCredential(providerId: string): Promise<RevealedCredential> {
  ensureSeeded()
  const p = store.listProviders().find((x) => x.id === providerId)
  if (p === undefined) throw new Error(`没有这个供应商:${providerId}`)
  const credential = parseCredential(await getHost().secrets.get(p.credentialRef))
  if (credential === null) throw new Error('还没有配置密钥')
  if (credential.kind === 'api-key') return { kind: 'api-key', apiKey: credential.apiKey }
  /*
    ★ 签名凭证也允许显式查看 —— 用户要能确认自己填的是哪一把 AccessKeyId,
    而"密钥栏里的点"对识别它是没有用的。查看动作仍然是显式的、单次的。
  */
  if (credential.kind === 'signature') {
    return {
      kind: 'signature',
      scheme: credential.scheme,
      accessKeyId: credential.accessKeyId,
      secretKey: credential.secretKey,
      region: credential.region,
      ...(credential.sessionToken === undefined ? {} : { sessionToken: credential.sessionToken })
    }
  }
  return {
    kind: 'oauth',
    accessToken: credential.accessToken,
    refreshToken: credential.refreshToken
  }
}
