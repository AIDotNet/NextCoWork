/**
 * 插件市场的客户端一侧 —— 浏览与安装。
 *
 * ## 四步流程与 Skill **原样相同**,一步都不能省
 *
 * ```
 * ① POST /api/client/plugins/{slug}/install   ← 拿授权 + **权威** sha256 + version
 * ② 校验返回的 version 与请求一致、sha256 形如 64 位 hex
 * ③ GET  .../versions/{version}/download      ← 下载,20MB 上限
 * ④ installer 校验 sha256 → 解压 → 写入插件目录
 * ```
 *
 * ★ 第 ① 步不是「多一次往返」:它在服务端强制 `plugins:install` scope,
 * 并给出**权威摘要**。跳过它、直接下载再自己算哈希,等于「摘要对得上」这句话
 * 只证明了下载到的文件和它自己一致 —— 中间被换掉的包一样能装上。
 *
 * ★ 第 ② 步校验 version 一致是防一种具体的错位:请求 1.2.0、服务端给 1.3.0 的
 * 摘要,而客户端下载 1.2.0 —— 校验必然失败,但失败信息会指向「包损坏」,
 * 而真正的原因是两边说的不是同一个版本。
 */
import { promises as fs } from 'node:fs'
import { join } from 'node:path'
import type { PluginCatalog } from '../../shared/plugin/state'
import type { PluginMarketItem, PluginMarketPage, PluginUpdate, PluginUpdateResult } from '../../shared/plugin/market'
import { addedPermissions, isPluginPermission } from '../../shared/plugin/permission'
import { hasNewerVersion } from '../../shared/plugin/manifest'
import { getHost } from '../runtime'
import { getClientAccessToken, getClientAuthState } from './client-auth'
import { emitInstallProgress, listPlugins, pluginManager } from './plugins'
import { IpcError } from './errors'

const MARKET_ORIGIN = process.env.NEXTCOWORK_MARKET_ORIGIN ?? 'https://nextco.work'
const MARKET_API_BASE = `${MARKET_ORIGIN}/api/`
const MAX_PACKAGE_BYTES = 20 * 1024 * 1024

async function marketRequest(path: string): Promise<unknown> {
  const response = await getHost().fetch(new URL(path.replace(/^\//, ''), MARKET_API_BASE).href)
  if (!response.ok) throw new IpcError('network', `plugins.marketFailed:${String(response.status)}`)
  const body = (await response.json()) as { data?: unknown }
  return body.data ?? body
}

export async function listMarketPlugins(req: { q?: string; category?: string; page?: number }): Promise<PluginMarketPage> {
  const page = Number.isInteger(req.page) && (req.page ?? 0) > 0 ? req.page as number : 1
  const params = new URLSearchParams({ page: String(page), pageSize: '12' })
  if (req.q !== undefined && req.q !== '') params.set('q', req.q.slice(0, 100))
  if (req.category !== undefined && req.category !== '') params.set('category', req.category)
  /*
    ★ **带上本机版本**。市场按 `engines` 过滤之后,列表里就不会再出现
    这台机器根本装不上的插件 —— 否则用户会点安装、失败,而市场那一侧
    从没提过这件事(计划 §10.2 第 3 条)。
  */
  params.set('client', hostVersion())
  const payload = (await marketRequest(`/plugins?${params.toString()}`)) as { items?: PluginMarketItem[]; hasNext?: boolean }
  return {
    items: (payload.items ?? []).map(normalizeItem),
    hasNext: payload.hasNext === true,
    page
  }
}

export async function listMarketPluginCategories(): Promise<string[]> {
  const payload = (await marketRequest('/plugins/categories')) as { categories?: string[] }
  return payload.categories ?? []
}

// ─────────────────────────── 更新检测 ───────────────────────────

/**
 * 把市场**整张表**翻完。
 *
 * ★★ 浏览页那条 `listMarketPlugins` 拿到的只有第一页 —— 服务端默认
 * `pageSize: 20`,而它连 `hasNext` 都没解构出来。拿它做更新检测的话,
 * 市场长到第 21 条的那天开始静默漏报:用户的插件有新版,客户端不说,
 * 而**没有任何一处会报错**。
 *
 * ★ 单独一个函数而不是让浏览页也翻全部:浏览页是给人看的,首屏不该为了
 * 一件后台的事多等几个往返。
 *
 * ★ 20 页硬上限不是"够用了",是**防服务端把 `hasNext` 永远置 true** ——
 * 那种情况下没有上限就是一个打满网络的死循环,而它的症状(应用变慢)
 * 和本地哪里写错了完全分不开。
 */
async function fetchAllMarketPlugins(): Promise<PluginMarketItem[]> {
  const out: PluginMarketItem[] = []
  for (let page = 1; page <= MAX_MARKET_PAGES; page += 1) {
    const params = new URLSearchParams({ client: hostVersion(), page: String(page), pageSize: '100' })
    const payload = (await marketRequest(`/plugins?${params.toString()}`)) as {
      items?: PluginMarketItem[]
      hasNext?: boolean
    }
    const items = payload.items ?? []
    out.push(...items.map(normalizeItem))
    if (payload.hasNext !== true || items.length === 0) break
  }
  return out
}

const MAX_MARKET_PAGES = 20
const MARKET_CACHE_TTL_MS = 10 * 60 * 1000
let marketCache: { at: number; items: PluginMarketItem[] } | null = null

export async function checkPluginUpdates(req: { force?: boolean }): Promise<PluginUpdate[]> {
  const manager = pluginManager()
  if (manager === null) throw new IpcError('unknown', 'plugins.notRunning')
  const now = Date.now()
  /*
    ★ 缓存的是**市场列表这次网络结果**,不是算好的这张更新表。

    反过来缓存的话,更新完一个插件,横幅上还写着「1 个插件可更新」,
    一直到 TTL 过期为止 —— 而用户刚刚亲手把它更新掉。每次都拿缓存的
    列表和**当前** catalog 重新 join,计数立刻就减下去了。
  */
  if (req.force === true || marketCache === null || now - marketCache.at > MARKET_CACHE_TTL_MS) {
    marketCache = { at: now, items: await fetchAllMarketPlugins() }
  }

  const byPluginId = new Map(marketCache.items.map((item) => [item.pluginId, item]))
  const updates: PluginUpdate[] = []
  for (const plugin of listPlugins().plugins) {
    const item = byPluginId.get(plugin.id)
    /*
      ★ 市场列表已经按 `client=<本机版本>` 过滤过,所以一个插件把 engines
      提到高于本机版本时,它会**整条从列表里消失** —— 这里查不到,于是
      不提示更新。这是有意的(提示了也装不上),但记在这:以后有人报
      「市场明明有新版却不提示」,答案多半在这一行,不是漏报。
    */
    if (item === undefined) continue
    if (!hasNewerVersion(plugin.manifest.version, item.version)) continue
    updates.push({
      pluginId: plugin.id,
      slug: item.slug,
      displayName: plugin.manifest.displayName,
      currentVersion: plugin.manifest.version,
      latestVersion: item.version as string,
      /*
        ★ 拿 `approvedRequired` 算,不是 `permissions.granted`(后者是超集,
        见 `PluginUpdate.escalatedPermissions` 的注释)。市场那边报的
        `versions[].permissionEscalated` 也不能用 —— 它是相对**上一个市场
        版本**算的,而用户手上装的可能比那还老。两个口径混用的结果是
        「说要重新授权、装完其实不用」或者反过来。
      */
      escalatedPermissions: addedPermissions(
        manager.approvedRequiredOf(plugin.id),
        item.permissions.filter(isPluginPermission)
      ),
      fromMarket: manager.slugOf(plugin.id) !== undefined
    })
  }
  return updates
}

/** 串行、失败不中断。见契约里 `plugins:updateAll` 的注释 */
export async function updateAllPlugins(): Promise<PluginUpdateResult> {
  if (updateAllTask !== null) return updateAllTask
  updateAllTask = runUpdateAll().finally(() => { updateAllTask = null })
  return updateAllTask
}

let updateAllTask: Promise<PluginUpdateResult> | null = null

async function runUpdateAll(): Promise<PluginUpdateResult> {
  // 只动市场来源的那些 —— 本地包装的那份可能是用户自己改过的构建
  const targets = (await checkPluginUpdates({ force: true })).filter((update) => update.fromMarket)
  const updated: string[] = []
  const failed: { pluginId: string; messageKey: string }[] = []
  for (const target of targets) {
    try {
      await installMarketPlugin({ slug: target.slug, version: target.latestVersion })
      updated.push(target.pluginId)
    } catch (error) {
      /*
        ★ 一个失败不中断后面的。中途 abort 的话,前面成功的那几个**已经
        落盘了**,而返回值里没有它们 —— 界面上的数字和实际状态对不上,
        比「3 个成功 1 个失败」更难看懂。
      */
      failed.push({ pluginId: target.pluginId, messageKey: messageKeyOf(error) })
    }
  }
  return { updated, failed }
}

export async function marketPluginDetail(req: { slug: string }): Promise<PluginMarketItem & { versions?: { version: string; changelog?: string; permissionEscalated?: boolean }[] }> {
  const payload = (await marketRequest(`/plugins/${encodeURIComponent(req.slug)}`)) as {
    plugin?: PluginMarketItem
    versions?: { version: string; changelog?: string; permissionEscalated?: boolean }[]
  }
  return { ...normalizeItem(payload.plugin ?? ({} as PluginMarketItem)), ...(payload.versions === undefined ? {} : { versions: payload.versions }) }
}

export async function installMarketPlugin(req: { slug: string; version?: string }): Promise<PluginCatalog> {
  /*
    ★ **同一个 slug 的两次安装必须合流成一次。**

    不是为了防手抖:`PluginManager.records` 和 KV 的 read-modify-write 之间
    没有锁,两次 `install()` 交叠时,A 的 `disable()` 会落在 B 的 getKv 和
    load 之间 —— 结果就是 B 读到 A 写的「已禁用」,插件被静默关掉。
    (`manager.install()` 里那条注释说的是同一件事的单线程版本。)
  */
  const key = `market:${req.slug}`
  const running = inflight.get(key)
  if (running !== undefined) return running
  const task = runMarketInstall(key, req).finally(() => { inflight.delete(key) })
  inflight.set(key, task)
  return task
}

const inflight = new Map<string, Promise<PluginCatalog>>()

async function runMarketInstall(key: string, req: { slug: string; version?: string }): Promise<PluginCatalog> {
  const release = await acquireInstallSlot()
  try {
    emitInstallProgress(key, 'preparing')
    const manager = pluginManager()
    if (manager === null) throw new IpcError('unknown', 'plugins.notRunning')
    if (getClientAuthState().mode !== 'authenticated') throw new IpcError('auth', 'plugins.authRequired')
    const access = await getClientAccessToken()
    if (access === null) throw new IpcError('auth', 'plugins.authRequired')

    const detail = await marketPluginDetail({ slug: req.slug })
    const version = req.version ?? detail.version ?? detail.versions?.[0]?.version
    if (version === undefined || version === null) throw new IpcError('unknown', 'plugins.versionUnavailable')

    // ① 授权 + 权威摘要
    const grant = await getHost().fetch(`${MARKET_API_BASE}client/plugins/${encodeURIComponent(req.slug)}/install`, {
      method: 'POST',
      headers: { Authorization: `Bearer ${access}`, 'content-type': 'application/json' },
      body: JSON.stringify({ version })
    })
    if (!grant.ok) {
      throw new IpcError(
        grant.status === 401 ? 'auth' : 'unknown',
        grant.status === 403 ? 'plugins.scopeRequired' : grant.status === 401 ? 'plugins.authRequired' : grant.status === 404 ? 'plugins.versionUnavailable' : 'plugins.marketFailed'
      )
    }
    const grantBody = (await grant.json()) as { data?: GrantBody } & GrantBody
    const granted = grantBody.data ?? grantBody

    // ② 两项一起校验:版本对得上,摘要形状合法
    if (granted.version !== version || typeof granted.sha256 !== 'string' || !/^[a-f0-9]{64}$/i.test(granted.sha256)) {
      throw new IpcError('unknown', 'plugins.digestMismatch')
    }

    // ③ 下载
    const response = await getHost().fetch(`${MARKET_API_BASE}plugins/${encodeURIComponent(req.slug)}/versions/${encodeURIComponent(version)}/download`)
    if (!response.ok) throw new IpcError('unknown', response.status === 404 ? 'plugins.versionUnavailable' : 'plugins.marketFailed')
    /*
      content-length 拿不到时退回详情页报的 fileSize —— 两个都没有的话
      `total` 就是 undefined,渲染层据此画一条没有百分比的条子。
      **不要在这里编一个估计值**:一条走到 80% 就停下不动的进度条,
      比一条明说「不知道还有多久」的更让人以为卡死了。
    */
    const declared = (detail.versions ?? []).find((v) => v.version === version) as { fileSize?: number } | undefined
    const total = sizeOf(response.headers.get('content-length')) ?? declared?.fileSize
    emitInstallProgress(key, 'downloading', { received: 0, ...(total === undefined ? {} : { total }) })
    const bytes = await readDownload(response, (received) => {
      emitInstallProgress(key, 'downloading', { received, ...(total === undefined ? {} : { total }) })
    })

    // ④ 落到临时文件再交给 installer —— 它会**再校验一次** sha256 并做解压防线
    emitInstallProgress(key, 'installing')
    const tempDir = await fs.mkdtemp(join(getHost().paths.temp(), 'nextcowork-plugin-'))
    const temp = join(tempDir, `plugin-v${version.replace(/[^0-9A-Za-z.+-]/g, '')}.zip`)
    try {
      await fs.writeFile(temp, bytes)
      // ★ 把 slug 一起交下去 —— 「这个插件跟不跟市场走」只有这里知道
      await manager.install(temp, granted.sha256, req.slug)
      const catalog = listPlugins()
      emitInstallProgress(key, 'done')
      return catalog
    } catch (error) {
      throw new IpcError('unknown', (error as Error).message)
    } finally {
      await fs.rm(tempDir, { recursive: true, force: true }).catch(() => undefined)
    }
  } catch (error) {
    emitInstallProgress(key, 'failed', { messageKey: messageKeyOf(error) })
    throw error
  } finally {
    release()
  }
}

/** 主进程抛的是 key 不是句子;认不出来的退回一句通用的,别把英文诊断漏到界面上 */
function messageKeyOf(error: unknown): string {
  return error instanceof IpcError && error.message.startsWith('plugins.') ? error.message : 'plugins.marketFailed'
}

function sizeOf(header: string | null): number | undefined {
  const value = Number(header)
  return Number.isFinite(value) && value > 0 ? value : undefined
}

/**
 * 同时最多跑两个市场安装。
 *
 * ★ 不是怕用户点太快,是 `readDownload` 把**整个包读进内存**:20MB 上限
 * × N 个并发,全都常驻主进程。两个是个折中 —— 连点两张卡片的人能看到
 * 两条都在动,而第三条排队时那张卡片停在「正在准备」,不会假装在下载。
 */
const MAX_CONCURRENT_INSTALLS = 2
let installsRunning = 0
const installQueue: (() => void)[] = []

async function acquireInstallSlot(): Promise<() => void> {
  if (installsRunning >= MAX_CONCURRENT_INSTALLS) {
    await new Promise<void>((resolve) => installQueue.push(resolve))
  }
  installsRunning += 1
  let released = false
  return () => {
    if (released) return
    released = true
    installsRunning -= 1
    installQueue.shift()?.()
  }
}

interface GrantBody {
  version?: string
  sha256?: string
  permissionEscalated?: boolean
}

/**
 * 边读边卡上限,顺带把已收字节报出去。
 *
 * ★★ `onProgress` **必须节流**。16MB 的包在 Chromium 网络栈下会切成几百到
 * 几千个 chunk,每个 chunk 推一条 IPC = 一次安装刷几千条事件穿过结构化
 * 克隆、再触发几千次 React 重渲染。这条路上没有任何一环会报错,它只是
 * 让整个界面在下载期间变卡 —— 而那看起来像「装插件把应用搞慢了」。
 *
 * 节流的三条规则各有各的用处:
 * - **首字节立刻报**:让条子马上动起来。等 120ms 的话,快网下用户先看到
 *   的是一条停在 0% 的进度条。
 * - 之后 ≥120ms **或** ≥256KB 才报:前者管慢网(按时间出稿),后者管快网
 *   (按进度出稿)。只留一条的话,另一种网速下要么太密要么太稀。
 * - **收尾强制报一次**:否则最后那几十 KB 被节流吃掉,条子永远停在 97%,
 *   而「差一点点就是不到头」比没有进度条更让人起疑。
 */
async function readDownload(response: Response, onProgress?: (received: number) => void): Promise<Buffer> {
  if (Number(response.headers.get('content-length')) > MAX_PACKAGE_BYTES) throw new IpcError('unknown', 'plugins.packageTooLarge')
  if (response.body === null) throw new IpcError('network', 'plugins.marketFailed')
  const reader = response.body.getReader()
  const chunks: Uint8Array[] = []
  let size = 0
  let reportedAt = 0
  let reportedBytes = 0
  try {
    for (;;) {
      const chunk = await reader.read()
      if (chunk.done) break
      size += chunk.value.byteLength
      // ★ 边读边卡上限,不是读完再看:读完再看意味着一个声称 1MB、实际 2GB
      //   的响应已经把内存吃光了。
      if (size > MAX_PACKAGE_BYTES) throw new IpcError('unknown', 'plugins.packageTooLarge')
      chunks.push(chunk.value)
      const now = Date.now()
      if (reportedAt === 0 || now - reportedAt >= PROGRESS_MIN_INTERVAL_MS || size - reportedBytes >= PROGRESS_MIN_BYTES) {
        reportedAt = now
        reportedBytes = size
        onProgress?.(size)
      }
    }
  } finally {
    await reader.cancel().catch(() => undefined)
  }
  if (reportedBytes !== size) onProgress?.(size)
  return Buffer.concat(chunks)
}

const PROGRESS_MIN_INTERVAL_MS = 120
const PROGRESS_MIN_BYTES = 256 * 1024

function normalizeItem(item: PluginMarketItem & { author?: unknown }): PluginMarketItem {
  const author = item.author
  return {
    ...item,
    permissions: Array.isArray(item.permissions) ? item.permissions : [],
    /*
      ★ 老服务端**不返回**这个字段,所以要兜成空数组,不能让 `undefined` 进去。

      不兜的话,渲染层那句 `skills.length > 0` 会当场抛
      「Cannot read properties of undefined」—— 而这条路只在连到一个尚未升级的
      市场时才走得到,也就是最不容易在开发机上复现的那种。
      同上面 `permissions` 那行,一模一样的理由。

      逐条 `String()`:这几个名字要直接画到卡片上,而服务端返回的是 JSON,
      里面混进数字或 null 不会有任何东西拦它。
    */
    skills: Array.isArray(item.skills) ? item.skills.map((name) => String(name)) : [],
    iconUrl: resolveIconUrl(item.iconUrl),
    author: author !== null && typeof author === 'object' ? String((author as { name?: unknown }).name ?? '') : String(author ?? '')
  }
}

/** 服务端给的是路径,渲染层跑在本地 origin 上 —— 在这一侧解析成绝对 URL。 */
function resolveIconUrl(value: string | null | undefined): string | null {
  if (typeof value !== 'string' || value.trim() === '') return null
  try {
    const url = new URL(value.trim(), MARKET_API_BASE)
    return url.protocol === 'https:' || url.protocol === 'http:' ? url.href : null
  } catch {
    return null
  }
}

function hostVersion(): string {
  return listPlugins().hostVersion
}
