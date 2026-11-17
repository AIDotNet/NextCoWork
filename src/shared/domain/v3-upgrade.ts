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

/** 一个可以升级到的 3.x 版本。只有这台电脑的平台确实发布了可下载的安装包时才有。 */
export interface V3Offer {
  version: string
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
  | { status: 'available'; offer: V3Offer }

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
export function parseV3Latest(body: unknown, rid: string): V3Offer | null {
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
    return {
      version: item.version,
      ...(typeof item.publishedAt === 'string' ? { publishedAt: item.publishedAt } : {}),
      ...(typeof item.notes === 'string' && item.notes.trim().length > 0 ? { notes: item.notes } : {})
    }
  }
  return null
}
