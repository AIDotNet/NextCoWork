/**
 * 2.9 的 3.0 升级引导在主进程的装配:单例的 `V3OfferService` + 「去安装 3.0」这一个动作。
 * 纯逻辑在 `v3-offer.ts` / `shared/domain/v3-upgrade.ts`,这里只接 Electron。
 */
import { execFile, spawn } from 'node:child_process'
import { app, net, shell } from 'electron'
import { join } from 'node:path'
import { V3_DOWNLOAD_PAGE } from '../../shared/domain/v3-upgrade'
import { requestQuit } from '../ipc/app'
import { windows } from '../window/registry'
import { V3OfferService } from './v3-offer'
import { V3Installer } from './v3-install'

export const v3Offer = new V3OfferService({
  fetch: (url) => net.fetch(url, { headers: { accept: 'application/json' } }),
  platform: process.platform,
  arch: process.arch,
  emit: (state) => windows.emitToAll('app:v3OfferChanged', state)
})

v3Offer.attachInstaller(new V3Installer({
  fetch: (url, signal) => net.fetch(url, { signal }) as unknown as ReturnType<ConstructorParameters<typeof V3Installer>[0]['fetch']>,
  run: (command, args) => new Promise<void>((resolve, reject) => {
    execFile(command, args, (error) => (error ? reject(error) : resolve()))
  }),
  spawnDetached: (command, args) => {
    const child = spawn(command, args, { detached: true, stdio: 'ignore', windowsHide: true })
    child.on('error', (error) => console.warn('[v3] 安装脚本没能启动:', error))
    child.unref()
  },
  platform: process.platform,
  execPath: process.execPath,
  pid: process.pid,
  home: app.getPath('home'),
  workDir: join(app.getPath('temp'), 'nextcowork-v3-install'),
  onState: (install) => v3Offer.installChanged(install)
}))

/**
 * 「去安装 3.0」:打开官网下载页,然后走**和菜单退出同一条**退出流程。
 *
 * ★ 必须退出:3.0 在 2.x 还开着时拒绝接管数据(它会说「请先退出 NextCoWork 2.x」)。退出流程会收尾运行中的任务、
 *   拦下未保存的文档 —— 用户在那里取消的话,下载页已经打开了,2.x 照常继续用,什么都没丢。
 */
export async function upgradeToV3(): Promise<void> {
  // 应用内已经下好、验过:起脱离本进程的安装脚本(等 2.x 退出再换),然后走同一条退出流程。
  // 退出被取消时进程还活着 —— 过几秒回到「可以再点」;脚本等满超时自己放弃,什么都没动。
  if (v3Offer.prepareInstall()) {
    requestQuit()
    setTimeout(() => v3Offer.installCancelled(), 8_000).unref()
    return
  }
  await shell.openExternal(V3_DOWNLOAD_PAGE)
  requestQuit()
}

/** 应用内下载这个平台的 3.0(进度经 `app:v3OfferChanged` 广播)。 */
export async function downloadV3(): Promise<void> {
  await v3Offer.download()
}

/** 只打开下载页,不退出(「先看看」)。 */
export async function openV3DownloadPage(): Promise<void> {
  await shell.openExternal(V3_DOWNLOAD_PAGE)
}
