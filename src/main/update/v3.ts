/**
 * 2.9 的 3.0 升级引导在主进程的装配:单例的 `V3OfferService` + 「去安装 3.0」这一个动作。
 * 纯逻辑在 `v3-offer.ts` / `shared/domain/v3-upgrade.ts`,这里只接 Electron。
 */
import { net, shell } from 'electron'
import { V3_DOWNLOAD_PAGE } from '../../shared/domain/v3-upgrade'
import { requestQuit } from '../ipc/app'
import { windows } from '../window/registry'
import { V3OfferService } from './v3-offer'

export const v3Offer = new V3OfferService({
  fetch: (url) => net.fetch(url, { headers: { accept: 'application/json' } }),
  platform: process.platform,
  arch: process.arch,
  emit: (state) => windows.emitToAll('app:v3OfferChanged', state)
})

/**
 * 「去安装 3.0」:打开官网下载页,然后走**和菜单退出同一条**退出流程。
 *
 * ★ 必须退出:3.0 在 2.x 还开着时拒绝接管数据(它会说「请先退出 NextCoWork 2.x」)。退出流程会收尾运行中的任务、
 *   拦下未保存的文档 —— 用户在那里取消的话,下载页已经打开了,2.x 照常继续用,什么都没丢。
 */
export async function upgradeToV3(): Promise<void> {
  await shell.openExternal(V3_DOWNLOAD_PAGE)
  requestQuit()
}

/** 只打开下载页,不退出(「先看看」)。 */
export async function openV3DownloadPage(): Promise<void> {
  await shell.openExternal(V3_DOWNLOAD_PAGE)
}
