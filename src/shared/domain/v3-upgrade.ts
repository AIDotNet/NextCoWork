/**
 * 2.9 的「升级到 NextCoWork 3.0」引导(2.x 的最后一个版本)。
 *
 * ## 为什么只是「引导」,不在这里装 3.0
 *
 * 3.0 是另一套程序(Avalonia + 内置 Agent),不经 electron-updater 发布:它有自己的安装包和更新源
 * (`/api/client/updates/v3/*`)。3.0 第一次启动时自己接管 2.x 的数据(一致性备份 → 所有权标记 →
 * 增量迁移),所以 2.9 要做的只有两件事:告诉用户 3.0 出来了、带他去下载页;以及在数据**已经**被 3.0
 * 接管之后,不再打开这份数据(见 `main/db/v3-ownership.ts`)。
 *
 * ★ 3.0 在 2.x 还开着时拒绝接管,所以「去安装 3.0」这个动作就是「打开下载页 + 走正常的退出流程」——
 * 退出流程本身会把运行中的任务收好。
 */

/** 官网下载页:它按访客的系统自动选包,2.9 不自己挑安装包。 */
export const V3_DOWNLOAD_PAGE = 'https://nextco.work/download'

/** 3.x 各平台当前版本的汇总(后端 `ClientReleaseV3Endpoints` 的 `/v3/latest`)。2.x 只有 stable 通道。 */
export const V3_LATEST_URL = 'https://nextco.work/api/client/updates/v3/latest?channel=stable'

/** 后端 `/v3/latest` 里列出的一个可下载文件。`url` 可以是相对路径(相对 nextco.work)。 */
export interface V3File {
  fileName: string
  /** 后端的分类:`portable`(zip)/ `velopack-setup`(Windows 安装器)/ `appimage` 等 */
  kind: string
  sha256: string
  size: number
  url: string
}

/** 这台电脑的 2.9 能在应用内直接装的文件种类;其余平台 / 种类只能引导去下载页。 */
export function v3FileKindOf(platform: string): string | null {
  return platform === 'darwin' ? 'portable' : platform === 'win32' ? 'velopack-setup' : platform === 'linux' ? 'appimage' : null
}

/** 一个可以升级到的 3.x 版本。只有这台电脑的平台确实发布了可下载的安装包时才有。 */
export interface V3Offer {
  version: string
  /** 应用内安装用的那个文件;没有(平台不支持 / 后端没给校验和)就只能引导去下载页 */
  file?: V3File
  /** ISO 时间;后端没给就缺省 */
  publishedAt?: string
  /** 3.x 的更新说明(Markdown);只作展示 */
  notes?: string
}

/**
 * 渲染层看到的整份状态。`unknown` = 还没问过 / 问不到(离线、后端没部署):界面什么都不显示,
 * 而不是说「没有 3.0」—— 问不到和确实没有是两回事,但对用户都不该打扰。
 */
export type V3OfferState =
  | { status: 'unknown' }
  | { status: 'none' }
  | { status: 'available'; offer: V3Offer; install: V3InstallState }

/**
 * 应用内下载 / 安装 3.0 的进度,和 `offer` 一起整份下发(同一个类型,首帧和广播一致)。
 * `ready` = 已下载、校验、(macOS)解开并验过签名,随时可以「重启并安装」。
 */
export type V3InstallState =
  | { phase: 'idle' }
  | { phase: 'downloading'; received: number; total: number }
  | { phase: 'verifying' }
  | { phase: 'ready' }
  | { phase: 'installing' }
  | { phase: 'error'; code: V3InstallErrorCode }

export type V3InstallErrorCode = 'network' | 'checksum' | 'extract' | 'signature' | 'permission' | 'unsupported'

export const V3_INSTALL_IDLE: V3InstallState = { phase: 'idle' }

/** Electron 的 `process.platform` / `process.arch` → 3.x 的 RID。不支持的组合返回 null。 */
export function v3RidOf(platform: string, arch: string): string | null {
  const os = platform === 'darwin' ? 'osx' : platform === 'win32' ? 'win' : platform === 'linux' ? 'linux' : null
  if (os === null || (arch !== 'x64' && arch !== 'arm64')) return null
  return `${os}-${arch}`
}

/** 主版本号。读不懂返回 null。 */
function majorOf(version: string): number | null {
  const match = /^v?(\d+)\./.exec(version.trim())
  return match ? Number(match[1]) : null
}

/**
 * 解析 `/v3/latest` 的响应(`/api` 组统一包成 `{ code, message, data }`),取出这台电脑能装的那一项。
 *
 * ★ 只认**确实有可下载安装包**、主版本 ≥ 3 的条目:清单里只有更新器用的 nupkg、没有安装器的平台,
 * 引导过去下载页也是空的 —— 那还不如不提。任何看不懂的形状都当「没有」,不抛错。
 */
export function parseV3Latest(body: unknown, rid: string, kind: string | null = null): V3Offer | null {
  if (typeof body !== 'object' || body === null) return null
  const data = (body as { data?: unknown }).data
  if (typeof data !== 'object' || data === null) return null
  const items = (data as { items?: unknown }).items
  if (!Array.isArray(items)) return null
  for (const raw of items) {
    if (typeof raw !== 'object' || raw === null) continue
    const item = raw as Record<string, unknown>
    if (item.rid !== rid || typeof item.version !== 'string') continue
    const major = majorOf(item.version)
    if (major === null || major < 3) continue
    if (!Array.isArray(item.downloads) || item.downloads.length === 0) continue
    const file = pickV3File(item.downloads, kind)
    return {
      version: item.version,
      ...(file !== null ? { file } : {}),
      ...(typeof item.publishedAt === 'string' ? { publishedAt: item.publishedAt } : {}),
      ...(typeof item.notes === 'string' && item.notes.trim().length > 0 ? { notes: item.notes } : {})
    }
  }
  return null
}

/** 取 `kind` 对应的那一个文件。字段不全(尤其没有 sha256)的不要:没法校验就不在应用内装。 */
function pickV3File(downloads: unknown[], kind: string | null): V3File | null {
  if (kind === null) return null
  for (const raw of downloads) {
    if (typeof raw !== 'object' || raw === null) continue
    const d = raw as Record<string, unknown>
    if (d.kind !== kind) continue
    if (typeof d.fileName !== 'string' || typeof d.url !== 'string' || typeof d.sha256 !== 'string') continue
    if (!/^[0-9a-f]{64}$/i.test(d.sha256) || typeof d.size !== 'number' || !(d.size > 0)) continue
    if (!/^[\w.-]+$/.test(d.fileName)) continue
    return { fileName: d.fileName, kind, sha256: d.sha256.toLowerCase(), size: d.size, url: d.url }
  }
  return null
}
