import { openExternalUrl } from '../services/external-opener'
import { createServer, type Server } from 'node:http'
import { createHash, randomBytes } from 'node:crypto'
import type { ClientAuthState, ClientAuthUser, ClientTeamOption, ClientUsageEntry } from '../../shared/domain/client-auth'
import { getHost } from '../runtime'
import { listResolvedModels } from '../state/model-bindings'
import { store } from '../state/store'
import * as repo from '../db/repo'
import { windows } from '../window/registry'
import { CLIENT_PROVIDER_ID } from '../../shared/domain/presets'
import { modelBindingResolver } from '../../shared/domain/model-binding'
import { IMPORTED_ALIAS_DEFAULTS } from '../../shared/domain/provider'
import { defaultProtocolForModel, findBuiltinModel, isImageModelId } from '../../shared/domain/model-catalog-inventory'
import { shutdownConfigSync, stopConfigSync } from './config-sync'
import { prepareAccountSwitch, startSyncForAccount } from '../account-switch'
import {
  claimMigratedLocalWorkspaces,
  migrateLegacyLocalProvidersToCurrentAccount,
  switchConfigProfile
} from '../db/config-profile'

/**
 * 哪些模型在 NextCoWork 内置渠道下固定走 anthropic 协议,而不是继承该供应商的
 * 出厂协议(Responses)。
 *
 * ★★ **anthropic 系(claude)这一支不在这里判,走共享的厂商默认协议表**
 * (`model-catalog-inventory` 的 `defaultProtocolForModel`)。那张表存在的理由正是
 * 「这条规则有三个消费方,各写一遍分支的话同一个模型在不同入口会拿到不同协议,
 * 而且不报错」—— 登录同步就是漏掉的第四个消费方:这里原来自己判一遍厂商,而那份
 * 判断里没有 anthropic,于是同步下来的 claude 别名不带 `protocolOverride`,在路由器
 * 里继承供应商的 Responses(`effectiveModelProtocol`)。表现是编辑弹窗写着
 * 「跟随供应商 / 当前生效:OpenAI Responses」,而 Claude 的思考档位(目录声明的是
 * `thinking.budget_tokens`)与 prompt 缓存语义在兼容层里被丢掉 —— 登录这一路零报错。
 *
 * 下面这四家是**平台侧的要求**,不是目录知识(共享表刻意不登记它们:别家两种线形
 * 差异小得多,统一钉死会剥夺「跟随供应商」这个合理默认),所以留在这里。
 */
function anthropicOverrideFor(modelId: string): 'anthropic' | undefined {
  if (defaultProtocolForModel(modelId) === 'anthropic') return 'anthropic'
  const manufacturerId = findBuiltinModel(modelId)?.manufacturerId
  return manufacturerId === 'deepseek' || manufacturerId === 'zhipu' ||
    manufacturerId === 'xiaomi' || manufacturerId === 'qwen'
    ? 'anthropic' : undefined
}

const API_ROOT = 'https://nextco.work'
const CLIENT_ID = 'nextcowork-desktop'
const ACCESS_REF = 'nextcowork:client-access-token'
const REFRESH_REF = 'nextcowork:client-refresh-token'
const META_KEY = 'client-auth.meta'
type Meta = {
  mode: 'offline' | 'authenticated'
  user: ClientAuthUser | null
  expiresAt: number | null
  teams?: ClientTeamOption[]
  selectedTeamId?: string | null
  contextRequired?: boolean
}
let callbackServer: Server | null = null
let refreshTimer: NodeJS.Timeout | null = null
let refreshInFlight: Promise<void> | null = null

/** Return the short-lived desktop access token for scoped API calls. */
export async function getClientAccessToken(): Promise<string | null> {
  await refreshAccessToken()
  return getHost().secrets.get(ACCESS_REF)
}

function callbackPage(success: boolean): string {
  const title = success ? '登录成功' : '登录未完成'
  const heading = success ? '欢迎回来' : '登录没有完成'
  const detail = success
    ? '你的 NextCoWork 桌面客户端已经连接到账户。现在可以返回应用继续使用。'
    : '授权没有完成，请关闭此页面并回到 NextCoWork 重试。'
  const icon = success ? '✓' : '!'
  const tone = success ? '#78e0a7' : '#f3b36a'
  return `<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="dark"><title>${title} · NextCoWork</title><style>
    :root{font-family:-apple-system,BlinkMacSystemFont,"SF Pro Display","Segoe UI",sans-serif;color:#f4f7f5;background:#151817}
    *{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;overflow:hidden;background:radial-gradient(ellipse 70% 55% at 50% 25%,#254236 0%,#1a2420 42%,#151817 78%)}
    .orb{position:fixed;width:420px;height:420px;border:1px solid rgba(120,224,167,.11);border-radius:50%;box-shadow:0 0 0 42px rgba(120,224,167,.025),0 0 0 84px rgba(120,224,167,.018);top:-205px;left:50%;transform:translateX(-50%)}
    .card{position:relative;width:min(430px,calc(100vw - 40px));padding:42px 38px 36px;text-align:center;border:1px solid rgba(255,255,255,.11);border-radius:24px;background:rgba(28,34,31,.86);box-shadow:0 24px 80px rgba(0,0,0,.32);backdrop-filter:blur(18px)}
    .mark{width:58px;height:58px;margin:0 auto 24px;display:grid;place-items:center;border-radius:18px;background:linear-gradient(145deg,#91efbb,#42b978);color:#10251a;font-size:31px;font-weight:700;box-shadow:0 10px 28px rgba(65,190,123,.24)}
    h1{margin:0;font-size:25px;letter-spacing:-.03em;font-weight:650}p{margin:13px 0 0;color:rgba(244,247,245,.58);font-size:14px;line-height:1.7}.status{margin-top:27px;padding:12px 14px;border-radius:12px;background:rgba(255,255,255,.055);color:${tone};font-size:12px}.back{display:inline-block;margin-top:27px;color:#78e0a7;font-size:13px;text-decoration:none}.back:hover{text-decoration:underline}.foot{margin-top:25px;color:rgba(244,247,245,.3);font-size:11px}
    @media(prefers-reduced-motion:no-preference){.card{animation:rise .45s cubic-bezier(.2,.8,.2,1)}.mark{animation:pop .55s .12s both cubic-bezier(.2,.9,.2,1)}@keyframes rise{from{opacity:0;transform:translateY(12px)}to{opacity:1;transform:none}}@keyframes pop{from{opacity:0;transform:scale(.7)}to{opacity:1;transform:scale(1)}}}
  </style></head><body><div class="orb" aria-hidden="true"></div><main class="card"><div class="mark" aria-hidden="true">${icon}</div><h1>${heading}</h1><p>${detail}</p><div class="status">${success ? '账户已安全连接 · 可以关闭浏览器窗口' : '没有任何账户信息被发送到此页面'}</div><a class="back" href="https://nextco.work/">返回 NextCoWork</a><div class="foot">NextCoWork · 你的工作空间</div></main><script>setTimeout(()=>{if(window.opener)window.close()},1200)</script></body></html>`
}

function announce(next: ClientAuthState): ClientAuthState {
  windows.emitToAll('clientAuth:changed', next)
  return next
}

function ensureRefreshTimer(): void {
  if (refreshTimer !== null) return
  refreshTimer = setInterval(() => { void refreshAccessToken().catch(() => undefined) }, 5 * 60_000)
  refreshTimer.unref()
}

/**
 * 退出时停掉这两条后台轮询。`before-quit` 末尾的 `closeDatabase({ final: true })` 会
 * **封库**,而 `unref()` 过的 interval 在进程真正退出前照样照常触发 —— 5 秒一轮的配置
 * 同步几乎必中一次,拿到的是 `DatabaseClosedError`,并且没有任何人接。
 *
 * ★ 症状只是退出时控制台冒出一条 UnhandledPromiseRejection,数据一个字节都没坏,
 *   所以极容易被当成噪音放过去 —— 但它同时说明**关灯之后还有人在写库**。
 */
export function shutdownClientAuth(): void {
  if (refreshTimer !== null) { clearInterval(refreshTimer); refreshTimer = null }
  shutdownConfigSync()
}

function meta(): Meta | null {
  try {
    const value = store.getKv<Meta | null>(META_KEY, null)
    return value && (value.mode === 'offline' || value.mode === 'authenticated') ? value : null
  } catch { return null }
}

export function prepareStoredAccountScope(): void {
  const saved = meta()
  const accountId = saved?.mode === 'authenticated' ? saved.user?.id : undefined
  if (accountId === undefined) return
  switchConfigProfile(accountId)
  reconcileMigratedWorkspacesForStoredAccount()
  if (migrateLegacyLocalProvidersToCurrentAccount()) {
    repo.setConfigCategoryDirty('providers', accountId, true)
  }
}

/**
 * 为已登录账户完成刚结束的数据迁移工作区重连。
 *
 * 需求：迁移失败后可以在已启动的应用里重试；那时不会再经过启动期的
 * `prepareStoredAccountScope`。没有这个入口，重试虽然写入数据，侧边栏仍会停在空态。
 */
export function reconcileMigratedWorkspacesForStoredAccount(): number {
  const saved = meta()
  const accountId = saved?.mode === 'authenticated' ? saved.user?.id : undefined
  if (accountId === undefined) return 0
  const relinked = claimMigratedLocalWorkspaces(accountId)
  if (relinked > 0) repo.setConfigCategoryDirty('workspaces', accountId, true)
  return relinked
}

/** 数据库恢复后重建账户作用域、内置 provider 与同步会话。调用时 runtime 已初始化。 */
export function resumeStoredAccountAfterDatabaseReplacement(): void {
  prepareStoredAccountScope()
  const saved = meta()
  const accountId = saved?.mode === 'authenticated' ? saved.user?.id : undefined
  if (accountId === undefined) return
  ensureClientProvider()
  startSyncForAccount(accountId)
}

function state(): ClientAuthState {
  const m = meta()
  if (m === null) return { mode: 'undecided', user: null, expiresAt: null }
  return {
    ...m,
    teams: m.teams ?? [],
    selectedTeamId: m.selectedTeamId ?? null,
    contextRequired: m.contextRequired === true
  }
}

async function saveTokens(access: string, refresh: string, m: Meta): Promise<void> {
  await getHost().secrets.set(ACCESS_REF, access)
  await getHost().secrets.set(REFRESH_REF, refresh)
  const accountId = m.user?.id
  if (accountId !== undefined) await prepareAccountSwitch(accountId)
  store.setKv(META_KEY, m)
  ensureClientProvider()
  void syncClientModels(access)
  startSyncForAccount(accountId ?? null)
  ensureRefreshTimer()
  announce(m)
}

async function refreshAccessToken(): Promise<void> {
  if (refreshInFlight) return refreshInFlight
  refreshInFlight = (async () => {
  const refresh = await getHost().secrets.get(REFRESH_REF)
  const m = meta()
    if (!refresh || !m || m.mode !== 'authenticated') return
    if (m.expiresAt !== null && m.expiresAt - Date.now() > 2 * 60_000) return
    const response = await getHost().fetch(`${API_ROOT}/api/client/oauth/token`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ grant_type: 'refresh_token', client_id: CLIENT_ID, refresh_token: refresh }), signal: AbortSignal.timeout(ACCOUNT_TIMEOUT_MS) }).catch(() => null)
    if (!response?.ok) return
    const tokens = await response.json() as { access_token?: string; refresh_token?: string; expires_in?: number }
    if (!tokens.access_token || !tokens.refresh_token || !tokens.expires_in || meta()?.mode !== 'authenticated') return
    await getHost().secrets.set(ACCESS_REF, tokens.access_token)
    await getHost().secrets.set(REFRESH_REF, tokens.refresh_token)
    const next = { ...m, expiresAt: Date.now() + tokens.expires_in * 1000 }
    store.setKv(META_KEY, next)
    announce(next)
  })().finally(() => { refreshInFlight = null })
  return refreshInFlight
}

/**
 * 平台那条上游的出厂协议。平台网关主推 Responses(它才带 `reasoning` 的完整往返
 * 与 `previous_response_id`),chat/completions 只是兼容层。
 */
const DEFAULT_CLIENT_PROTOCOL = 'openai-responses' as const

/** 把老库里那条出厂就是 `openai-chat` 的记录抬到 Responses,只做一次。 */
const RESPONSES_DEFAULT_KEY = 'client-auth.responses-default'

/**
 * 保证这条供应商记录在，且**身份字段**是平台那份。
 *
 * ★★ **只钉身份,不钉配置。** 名称 / 地址 / 凭证引用归登录流程(access token 是发往
 * 平台域名的凭证,地址一旦被改走就等于把它送到别处去);而 `protocol` /
 * `protocolOptions` / `priority` / `enabled` 是**用户在设置页改的**,这里必须原样留着 ——
 * 以前整条重写,表现是用户翻完「API 格式」开关,下一次读登录态就被静默改回 openai-chat。
 *
 * ★ 出厂默认改成 Responses 之后,已经登录过的库里那条还停在 `openai-chat`。
 * `RESPONSES_DEFAULT_KEY` 那段是**一次性**抬升:只认 `openai-chat`(旧出厂值),
 * 且标记落库后永不重跑 —— 用户之后再翻回 chat 不会被第二次改掉。
 */
function ensureClientProvider(): void {
  const identity = {
    id: CLIENT_PROVIDER_ID,
    name: 'NextCoWork',
    baseUrl: `${API_ROOT}/v1`,
    credentialRef: ACCESS_REF
  } as const
  const current = store.listProviders().find((p) => p.id === CLIENT_PROVIDER_ID)
  if (current === undefined) {
    store.setKv(RESPONSES_DEFAULT_KEY, true)
    store.putProvider({ ...identity, protocol: DEFAULT_CLIENT_PROTOCOL, priority: 1, enabled: true })
    return
  }
  const migrated = store.getKv<boolean>(RESPONSES_DEFAULT_KEY, false)
  const protocol = !migrated && current.protocol === 'openai-chat'
    ? DEFAULT_CLIENT_PROTOCOL
    : current.protocol
  if (!migrated) store.setKv(RESPONSES_DEFAULT_KEY, true)
  if (
    protocol !== current.protocol ||
    current.name !== identity.name ||
    current.baseUrl !== identity.baseUrl ||
    current.credentialRef !== identity.credentialRef
  ) {
    store.putProvider({ ...current, ...identity, protocol })
  }
}

/**
 * 已经有别名的老库,只做一次「把 `catalogOverrides` 从 undefined 补成实值」的定型。
 *
 * ★★ 早期的模型同步是裸 `putAlias`,写下来的别名没有 `catalogOverrides`。这类别名
 * 每次被读到都会走 `model-binding.ts` 的历史推断,而当年那份 `/v1/models` 响应里
 * 缺失的能力位是 false —— 推断会把它们当成「用户特意关掉的」。定型一次,让内置目录
 * 重新对这些别名生效(症状:登录拿到的模型在「编辑模型」里显示不支持工具调用)。
 *
 * 只碰内置供应商自己那批别名,且只在 `catalogOverrides` 尚未存在时动手,
 * 所以用户后来在设置页改过的任何一条都不受影响 —— 那些记录早就带上了实值。
 */
function repairLegacyClientAliases(): void {
  const legacy = store.listAliases().filter(
    (a) => a.providerId === CLIENT_PROVIDER_ID && a.catalogOverrides === undefined
  )
  if (legacy.length === 0) return
  const resolver = modelBindingResolver(store.listUserModelCatalog())
  for (const alias of legacy) store.putAlias(resolver.resolve({ ...alias, catalogOverrides: [] }))
  windows.emitToAll('provider:changed', { providers: store.listProviders(), models: listResolvedModels() })
}

/**
 * 一次性 KV 标记,避免下面的补种在每次读登录态时都重跑一遍。**规则每扩一次厂商
 * 就要升一版**(v3 是加入 Qwen,v4 是加入 anthropic 系),否则老库永远补不上 —— 理由见下面那段。
 */
const ANTHROPIC_OVERRIDE_MIGRATION_KEY = 'client-auth.deepseek-zhipu-xiaomi-qwen-anthropic-override-v4'

/**
 * 老库里已经同步过的别名(DeepSeek / 智谱 GLM / 小米 MiMo / 阿里云 Qwen,以及
 * anthropic 系 —— 后者是规则扩到 claude 之后才补上的,见下面那段)早于
 * `anthropicOverrideFor` 这条规则存在,补种一次。只补没有显式协议覆盖的 ——
 * 用户自己在「协议」下拉里选过的不碰,且标记落库后永不重跑,翻回别的协议不会被这里悄悄改回去。
 *
 * ★★ **必须升到 v4 才救得了 claude。** 规则本身改了(anthropic 系改走共享的厂商
 * 默认协议表)不够:老库里那批 claude 别名是**已经同步完**的,`syncClientModels`
 * 见到「这家已经有别名」就早退,永远走不到写 `protocolOverride` 那一行。而 v3 标记
 * 早就落库了,不改版本号这里一次都不会重跑 —— 症状是改了规则、重启、界面纹丝不动。
 * (同样的取舍见 `runtime.ts` 的 `OPENCODE_GO_PROTOCOL_MIGRATION_KEY`。)
 */
function backfillAnthropicOverride(): void {
  if (store.getKv<boolean>(ANTHROPIC_OVERRIDE_MIGRATION_KEY, false)) return
  store.setKv(ANTHROPIC_OVERRIDE_MIGRATION_KEY, true)
  const targets = store.listAliases().filter(
    (a) => a.providerId === CLIENT_PROVIDER_ID && a.protocolOverride === undefined &&
      anthropicOverrideFor(a.upstreamModel) !== undefined
  )
  if (targets.length === 0) return
  for (const alias of targets) store.putAlias({ ...alias, protocolOverride: 'anthropic' })
  windows.emitToAll('provider:changed', { providers: store.listProviders(), models: listResolvedModels() })
}

/**
 * 首次登录时把平台的模型列表灌进去。
 *
 * ★★ **已经有别名就不再灌** —— 这张表在设置页是可编辑的(增删、改名、排序),
 * 而这个函数每次读登录态都会跑。不早退的话它就是一次**整表覆盖**:用户删掉的模型
 * 下次启动自己回来,改过的顺序被重排。自动同步因此只服务冷启动,之后靠设置页
 * 那颗「从服务商拉取模型列表」按钮刷新(它走 `provider:fetchModels` + `provider:setAliases`,
 * 是替换语义、可勾选)。
 *
 * 代价:平台以后新增的模型不会再自动出现在老用户的列表里。这是「删掉的不许自己回来」
 * 的必然对价 —— 两者不可能同时成立,而后者是用户明确要的。
 */
async function syncClientModels(access: string): Promise<void> {
  if (store.listAliases().some((a) => a.providerId === CLIENT_PROVIDER_ID)) {
    repairLegacyClientAliases()
    backfillAnthropicOverride()
    return
  }
  try {
    const response = await getHost().fetch(`${API_ROOT}/v1/models`, { headers: { Authorization: `Bearer ${access}` } })
    if (!response.ok) return
    if (meta()?.mode !== 'authenticated') return
    const body = await response.json() as { data?: Array<{ id: string; display_name?: string; displayName?: string; context_window?: number; max_output_tokens?: number; capabilities?: string[] }> }
    const remote = body.data ?? []
    const resolver = modelBindingResolver(store.listUserModelCatalog())
    remote.forEach((m, index) => {
      const caps = new Set((m.capabilities ?? []).map((x) => x.toLowerCase()))
      /*
        ★ 走 `resolver.resolve` + 空的 `catalogOverrides`,和「从服务商拉取模型列表」
        (`provider.setAliases`)完全同一条路径。

        以前这里是**裸 `putAlias`**,于是 `/v1/models` 那份贫瘠的响应就成了终值:
        平台不返回 `capabilities` 时四个能力位一律写成 false,而编辑弹窗读的正是别名上
        这份 `capabilities` —— 症状是内置供应商登录后拿到的模型在「编辑模型」里
        显示不支持工具调用,同一个模型在「模型管理」里(读的是内置目录)却写着支持。

        更糟的是 `catalogOverrides` 当时是 undefined,别名一被读到就触发历史推断,
        那些 false 会被认成「用户特意关掉的」并永久落进 overrides,此后内置目录再也压不过它。

        平台给的值现在只当**目录查不到该模型时**的兜底:目录命中就以目录为准。
      */
      const platformCapabilities = m.capabilities === undefined
        ? IMPORTED_ALIAS_DEFAULTS.capabilities
        : { ...IMPORTED_ALIAS_DEFAULTS.capabilities,
            tools: caps.has('tools'), vision: caps.has('vision') || caps.has('imageinput'),
            thinking: caps.has('thinking'), caching: caps.has('caching'),
            imageOutput: caps.has('imageoutput') || caps.has('image-output') }
      /*
        ★★ 需求:登录灌进来的**生图模型必须带上 image 模态**,否则「图片生成」页
        (`ImageModelPage` 按 `modality === 'image' || imageOutput` 过滤)看不见它 ——
        平台的 `/v1/models` 不报 modality,目录又只认得已收录的 ID,新上架/预览期的
        图片模型两边都落空,表现是设置页里那家 NextCoWork 只有文本模型,零报错。

        判据两路取或:平台显式标了 imageOutput,或 ID 命中共享的图片模型启发式
        (`isImageModelId`,和图片页「拉取模型列表」的过滤是同一条,别在这写第二份)。
        目录命中时 `resolver.resolve` 会用目录的 modality 覆盖这里写的值,
        所以这里只对**目录不认识**的 ID 起作用 —— 正是要救的那批。
      */
      const imageOutput = platformCapabilities.imageOutput === true || isImageModelId(m.id)
      store.putAlias(resolver.resolve({
        alias: m.id, providerId: CLIENT_PROVIDER_ID, upstreamModel: m.id, priority: index * 10,
        ...(anthropicOverrideFor(m.id) === undefined ? {} : { protocolOverride: anthropicOverrideFor(m.id) }),
        ...(imageOutput ? { modality: 'image' as const } : {}),
        displayName: m.display_name ?? m.displayName,
        capabilities: { ...platformCapabilities, imageOutput },
        contextWindow: Number.isFinite(m.context_window) && (m.context_window ?? 0) > 0 ? m.context_window! : IMPORTED_ALIAS_DEFAULTS.contextWindow,
        maxOutputTokens: Number.isFinite(m.max_output_tokens) && (m.max_output_tokens ?? 0) > 0 ? m.max_output_tokens! : IMPORTED_ALIAS_DEFAULTS.maxOutputTokens,
        catalogOverrides: [], enabled: true
      }))
    })
    windows.emitToAll('provider:changed', { providers: store.listProviders(), models: listResolvedModels() })
  } catch { /* model sync is best effort; user can retry from the model page */ }
}

/**
 * 账号相关的请求统一挂 10 秒超时。`getHost().fetch` 是 `net.fetch` 裸透传,
 * 不带任何 deadline —— 网络挂起(而不是拒绝)时这些 promise 永不 settle,
 * 设置页的账号信息和钱包余额会无限期转圈。写法照抄 ipc/app.ts 的 checkForUpdates。
 */
const ACCOUNT_TIMEOUT_MS = 10_000

async function fetchMe(access: string): Promise<ClientAuthUser> {
  const response = await getHost().fetch(`${API_ROOT}/api/client/account`, { headers: { Authorization: `Bearer ${access}` }, signal: AbortSignal.timeout(ACCOUNT_TIMEOUT_MS) })
  if (!response.ok) throw new ClientAuthHttpError(response.status, `account request failed: ${response.status}`)
  const body = await response.json() as { data?: { user?: ClientAuthUser; wallet?: ClientAuthUser['wallet'] }; user?: ClientAuthUser; wallet?: ClientAuthUser['wallet'] }
  const payload = body.data ?? body
  const user = payload.user
  if (!user) throw new Error('account response invalid')
  return { ...user, wallet: payload.wallet ?? user.wallet }
}

class ClientAuthHttpError extends Error {
  constructor(readonly status: number, message: string) {
    super(message)
    this.name = 'ClientAuthHttpError'
  }
}

type ClientContextResponse = {
  items: ClientTeamOption[]
  selectedTeamId: string | null
}

async function fetchClientContext(access: string): Promise<ClientContextResponse> {
  const response = await getHost().fetch(`${API_ROOT}/api/client/context/options`, {
    headers: { Authorization: `Bearer ${access}` },
    signal: AbortSignal.timeout(ACCOUNT_TIMEOUT_MS)
  })
  if (!response.ok) throw new ClientAuthHttpError(response.status, `Team context request failed: ${response.status}`)
  const body = await response.json() as {
    data?: { items?: ClientTeamOption[]; selectedTeamId?: string | null }
    items?: ClientTeamOption[]
    selectedTeamId?: string | null
  }
  const payload = body.data ?? body
  return {
    items: Array.isArray(payload.items) ? payload.items : [],
    selectedTeamId: payload.selectedTeamId ?? null
  }
}

async function selectRemoteTeam(access: string, teamId: string): Promise<void> {
  const response = await getHost().fetch(`${API_ROOT}/api/client/context/select`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', Authorization: `Bearer ${access}` },
    body: JSON.stringify({ team: teamId })
  })
  if (!response.ok) throw new ClientAuthHttpError(response.status, `Team selection failed: ${response.status}`)
}

async function completeTeamContext(access: string, refresh: string, expiresAt: number, context: ClientContextResponse, teamId: string): Promise<ClientAuthState> {
  const selected = context.items.find((team) => team.id === teamId)
  if (selected === undefined) throw new Error('请选择一个可用 Team 后继续')
  await selectRemoteTeam(access, teamId)
  const user = await fetchMe(access)
  const next: Meta = {
    mode: 'authenticated',
    user,
    expiresAt,
    teams: context.items.map((team) => ({ ...team, isSelected: team.id === teamId })),
    selectedTeamId: teamId,
    contextRequired: false
  }
  await saveTokens(access, refresh, next)
  return state()
}

export function getClientAuthState(): ClientAuthState {
  const current = state()
  if (current.mode === 'authenticated') {
    ensureRefreshTimer()
    const accountId = current.user?.id
    if (accountId !== undefined) {
      void prepareAccountSwitch(accountId)
        .then(async () => {
          if (meta()?.user?.id !== accountId) return
          ensureClientProvider()
          startSyncForAccount(accountId)
          const access = await getHost().secrets.get(ACCESS_REF)
          if (access !== null && meta()?.user?.id === accountId) void syncClientModels(access)
        })
        .catch((error: unknown) => {
          console.warn(`[clientAuth] 恢复账户配置作用域失败:${describeError(error)}`)
        })
    }
  }
  else {
    for (const alias of store.listAliases().filter((a) => a.providerId === CLIENT_PROVIDER_ID)) store.removeAlias(CLIENT_PROVIDER_ID, alias.alias)
    if (store.listProviders().some((p) => p.id === CLIENT_PROVIDER_ID)) store.removeProvider(CLIENT_PROVIDER_ID)
  }
  return current
}

export async function useOffline(): Promise<ClientAuthState> {
  stopConfigSync()
  await prepareAccountSwitch(null)
  const next: Meta = { mode: 'offline', user: null, expiresAt: null }
  store.setKv(META_KEY, next)
  return announce(next)
}

function describeError(error: unknown): string {
  return error instanceof Error && error.message.length > 0 ? error.message : String(error)
}

/**
 * 退出登录是**本地动作**,必须能在离线、撤销接口不可达、甚至本地密文解不开的
 * 情况下走完。
 *
 * ★★ 以前这里每一步都是裸调用,第一个抛出的异常就把后面全部掐掉 —— 而最容易抛的
 * 恰恰是第二步的 `secrets.get(REFRESH_REF)`:它走 `safeStorage.decryptString`,
 * Windows 的 DPAPI 密文一旦换了机器 / 系统账户就直接抛。症状是 token、META、内置
 * 供应商一个都没清掉,界面还显示着已登录,而用户看到的只有一句「操作失败,请重试」
 * —— 重试多少次都卡在同一步。
 *
 * 现在每一步各自兜住并把原因记下来:
 * - 读不出 refresh token 就跳过远端撤销(它本来就是尽力而为),照样删本地密文;
 * - 只有**登录态本身没清掉**(`meta()` 还在)才算真失败,此时带着全部原因抛出去,
 *   渲染层才有话可说;
 * - 别名 / 供应商清理失败只降级成一条 warn —— 登录态已经没了,下一次
 *   `getClientAuthState()` 的 else 分支会把它们补删。
 */
export async function signOutClient(): Promise<ClientAuthState> {
  const failures: string[] = []
  const step = async (label: string, run: () => void | Promise<void>): Promise<void> => {
    try { await run() } catch (error) { failures.push(`${label}: ${describeError(error)}`) }
  }

  await step('configSync.stop', () => { stopConfigSync() })

  const refresh = await getHost().secrets.get(REFRESH_REF).catch((error: unknown) => {
    failures.push(`secrets.readRefreshToken: ${describeError(error)}`)
    return null
  })
  if (refresh) {
    // 撤销失败不该拦住本地登出,超时同理 —— 裸 `net.fetch` 不带 deadline。
    await getHost().fetch(`${API_ROOT}/api/client/oauth/revoke`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ client_id: CLIENT_ID, refresh_token: refresh }), signal: AbortSignal.timeout(ACCOUNT_TIMEOUT_MS) }).catch(() => undefined)
  }

  const remove = getHost().secrets.remove
  if (remove) {
    await step('secrets.removeAccessToken', () => remove(ACCESS_REF))
    await step('secrets.removeRefreshToken', () => remove(REFRESH_REF))
  }
  if (refreshTimer) { clearInterval(refreshTimer); refreshTimer = null }

  await step('store.clearAuthMeta', () => { store.setKv(META_KEY, null) })
  await step('store.removeClientModels', () => {
    for (const alias of store.listAliases().filter((a) => a.providerId === CLIENT_PROVIDER_ID)) store.removeAlias(CLIENT_PROVIDER_ID, alias.alias)
  })
  await step('store.removeClientProvider', () => {
    if (store.listProviders().some((p) => p.id === CLIENT_PROVIDER_ID)) store.removeProvider(CLIENT_PROVIDER_ID)
  })
  await step('configProfile.local', () => prepareAccountSwitch(null))

  if (meta() !== null) {
    throw new Error(`退出登录失败:${failures.length > 0 ? failures.join('; ') : '登录状态未被清除'}`)
  }
  if (failures.length > 0) {
    console.warn(`[clientAuth] 退出登录已完成,但有清理步骤失败:${failures.join('; ')}`)
  }
  return announce(state())
}

export async function startClientLogin(): Promise<ClientAuthState> {
  if (callbackServer) throw new Error('已有登录流程正在进行')
  const stateToken = randomBytes(24).toString('hex')
  const verifier = randomBytes(48).toString('base64url')
  const challenge = createHash('sha256').update(verifier).digest('base64url')
  let redirectUri = ''
  const code = await new Promise<string>((resolve, reject) => {
    const timeout = setTimeout(() => {
      callbackServer?.close()
      callbackServer = null
      reject(new Error('登录等待超时'))
    }, 10 * 60_000)
    const fail = (error: unknown) => {
      clearTimeout(timeout)
      callbackServer?.close()
      callbackServer = null
      reject(error)
    }
    callbackServer = createServer((req, res) => {
      if (req.method !== 'GET') { res.writeHead(405); res.end(); return }
      const url = new URL(req.url ?? '/', 'http://127.0.0.1')
      if (url.pathname !== '/callback') { res.writeHead(404); res.end(); return }
      if (url.searchParams.get('state') !== stateToken) { res.writeHead(400, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' }); res.end(callbackPage(false)); return }
      const authCode = url.searchParams.get('code')
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store', 'x-content-type-options': 'nosniff' })
      res.end(callbackPage(Boolean(authCode)))
      callbackServer?.close(); callbackServer = null
      clearTimeout(timeout)
      if (!authCode) reject(new Error(url.searchParams.get('error') ?? '登录被取消'))
      else resolve(authCode)
    })
    callbackServer.on('error', fail)
    callbackServer.listen(0, '127.0.0.1', () => {
      const address = callbackServer?.address()
      if (!address || typeof address === 'string') { fail(new Error('无法创建本地回调')); return }
      const redirect = `http://127.0.0.1:${address.port}/callback`
      redirectUri = redirect
      const authorize = new URL(`${API_ROOT}/client/authorize`)
      authorize.searchParams.set('client_id', CLIENT_ID)
      authorize.searchParams.set('redirect_uri', redirect)
      authorize.searchParams.set('response_type', 'code')
      authorize.searchParams.set('state', stateToken)
      authorize.searchParams.set('code_challenge', challenge)
      authorize.searchParams.set('code_challenge_method', 'S256')
      /*
        ★ 这一串必须与后端 `ClientOAuth.Scopes` **逐字对应**:那边是**全等匹配**,
        不是子集匹配(`ParseScopes`)。少一个,整串就落到 `LegacyScopes` 上,
        而 `plugins:read` / `plugins:install` 恰好在被切掉的末尾两位 ——
        症状是「装 Skill 正常、装插件报需要登录」:后端在 token 校验事件里
        直接判认证失败(401),客户端只能把它显示成「未登录」。

        ★★ 改这一串之后**必须重新登录**,刷新 token 不会补上新 scope。
      */
      authorize.searchParams.set('scope', 'profile:read wallet:read usage:read models:read inference:write config:read config:write config:secret:read config:secret:write skills:read skills:install plugins:read plugins:install')
      void openExternalUrl(authorize.toString()).catch(fail)
    })
  })
  const response = await getHost().fetch(`${API_ROOT}/api/client/oauth/token`, {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ grant_type: 'authorization_code', client_id: CLIENT_ID, code, redirect_uri: redirectUri, code_verifier: verifier })
  })
  if (!response.ok) throw new Error(`登录失败：${response.status}`)
  const tokens = await response.json() as { access_token: string; refresh_token: string; expires_in: number; user?: ClientAuthUser }
  const expiresAt = Date.now() + tokens.expires_in * 1000
  const context = await fetchClientContext(tokens.access_token)
  if (context.items.length === 0) {
    const remove = getHost().secrets.remove
    if (remove) {
      await remove(ACCESS_REF).catch(() => undefined)
      await remove(REFRESH_REF).catch(() => undefined)
    }
    throw new Error('账户没有可用 Team')
  }

  // A single Team is unambiguous and can be bound immediately. Multiple Teams
  // must be chosen by the user before /account, usage, or gateway calls run.
  const selectedTeamId = context.selectedTeamId ?? (context.items.length === 1 ? context.items[0]!.id : null)
  if (selectedTeamId === null) {
    const pending: Meta = {
      mode: 'authenticated', user: tokens.user ?? null, expiresAt,
      teams: context.items, selectedTeamId: null, contextRequired: true
    }
    await saveTokens(tokens.access_token, tokens.refresh_token, pending)
    return state()
  }
  return completeTeamContext(tokens.access_token, tokens.refresh_token, expiresAt, context, selectedTeamId)
}

export async function selectClientTeam(teamId: string): Promise<ClientAuthState> {
  if (teamId.trim().length === 0) throw new Error('Team 无效')
  await refreshAccessToken()
  const access = await getHost().secrets.get(ACCESS_REF)
  const refresh = await getHost().secrets.get(REFRESH_REF)
  const current = meta()
  if (!access || !refresh || !current || current.mode !== 'authenticated') {
    throw new Error('登录状态已失效，请重新登录')
  }
  const context = await fetchClientContext(access)
  const expiresAt = current.expiresAt ?? Date.now() + 5 * 60_000
  return completeTeamContext(access, refresh, expiresAt, context, teamId)
}

export async function getClientUser(): Promise<ClientAuthUser | null> {
  await refreshAccessToken()
  const access = await getHost().secrets.get(ACCESS_REF)
  if (!access) return null
  try {
    const user = await fetchMe(access)
    const m = meta()
    if (m) {
      store.setKv(META_KEY, { ...m, user, contextRequired: false })
      // ★ 和下面 409 那支对称地推一次。**渲染层不再 await 这个调用**(见 App.tsx
      // 的握手):首屏只认 getClientAuthState() 的本地快照,这次刷新拿到的新头像、
      // 新钱包余额全靠这条推送回去。少了它,余额要等下次重启才更新。
      announce(state())
    }
    return user
  } catch (error) {
    if (error instanceof ClientAuthHttpError && error.status === 409) {
      try {
        const context = await fetchClientContext(access)
        const m = meta()
        if (m) {
          store.setKv(META_KEY, { ...m, teams: context.items, selectedTeamId: context.selectedTeamId, contextRequired: true })
          announce(state())
        }
      } catch { /* keep the cached account state for transient network failures */ }
      return null
    }
    return meta()?.user ?? null
  }
}

export async function getClientUsage(req: { from?: string; to?: string }): Promise<ClientUsageEntry[]> {
  await refreshAccessToken()
  const access = await getHost().secrets.get(ACCESS_REF)
  if (!access) return []
  const url = new URL(`${API_ROOT}/api/client/usage`)
  if (req.from) url.searchParams.set('from', req.from)
  if (req.to) url.searchParams.set('to', req.to)
  const response = await getHost().fetch(url, { headers: { Authorization: `Bearer ${access}` } })
  if (!response.ok) return []
  const body = await response.json() as { data?: { items?: ClientUsageEntry[] }; items?: ClientUsageEntry[] }
  return body.data?.items ?? body.items ?? []
}
