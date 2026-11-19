/**
 * 2.9 的「升级到 NextCoWork 3.0」引导文案(标题栏指示器的卡片 + 设置 · 关于)。
 */
type Params = Record<string, string | number>

export const v3UpgradeZh = {
  'v3.title': ({ version }: Params) => `NextCoWork ${version} 已发布`,
  'v3.badge': '3.0',
  'v3.indicatorLabel': '升级到 NextCoWork 3.0',
  'v3.lead': '这是 NextCoWork 2.x 的最后一个版本。3.0 是重新打造的新一代桌面应用,以后的新功能都只在 3.0 里提供。',
  'v3.point.data': '你的项目、会话、设置和供应商配置会在第一次打开 3.0 时自动迁移,迁移前会先做一份完整备份;个别没能自动迁移的密钥,3.0 会列出来请你重新填写。',
  'v3.point.install': '3.0 是单独的安装包:点「下载 3.0」,下载并校验完成后点「重启并安装」,2.x 会退出并自动换成 3.0 再打开它。迁移时 2.x 必须是关闭的。',
  'v3.point.pairing': '和其他设备的配对需要在 3.0 里重新扫码;2.x 的独立设备服务 3.0 不再支持。',
  'v3.point.after': '迁移完成后,这台电脑上的 2.x 不会再打开这份数据,以免两个版本同时写坏它。',
  'v3.point.rollback': '升级后想回来也可以:在 3.0 的「设置 → 数据」里选「回到 NextCoWork 2.x」,数据会恢复成迁移前的样子(迁移之后在 3.0 里新写的内容不会带回来)。',
  'v3.download': '下载 3.0',
  'v3.downloading': ({ percent }: Params) => `正在下载 ${percent}%`,
  'v3.verifying': '正在校验…',
  'v3.install': '重启并安装 3.0',
  'v3.installing': '正在安装…',
  'v3.retry': '重试下载',
  'v3.error.network': '下载没有完成,请检查网络后重试。',
  'v3.error.checksum': '下载的文件校验没通过,已丢弃。请重试,或者只打开下载页。',
  'v3.error.extract': '安装包解开失败。请重试,或者只打开下载页手动安装。',
  'v3.error.signature': '安装包的签名校验没通过,没有安装。请只打开下载页手动安装。',
  'v3.error.permission': '没有权限替换当前的应用(它所在的文件夹不可写)。请只打开下载页手动安装。',
  'v3.error.unsupported': '这个平台暂不支持应用内安装。请只打开下载页手动安装。',
  'v3.upgrade': '前往下载并退出',
  'v3.upgrading': '正在退出…',
  'v3.openDownload': '只打开下载页',
  'v3.notes': '3.0 更新说明'
}

export const v3UpgradeEn: Record<keyof typeof v3UpgradeZh, string | ((params: Params) => string)> = {
  'v3.title': ({ version }: Params) => `NextCoWork ${version} is here`,
  'v3.badge': '3.0',
  'v3.indicatorLabel': 'Upgrade to NextCoWork 3.0',
  'v3.lead': 'This is the last version of NextCoWork 2.x. 3.0 is a rebuilt desktop app, and new features will only ship in 3.0.',
  'v3.point.data': 'Your projects, conversations, settings and providers move over automatically the first time you open 3.0, after a full backup; any key that can’t be moved is listed in 3.0 for you to re-enter.',
  'v3.point.install': '3.0 is a separate app: choose “Download 3.0”, and once it is downloaded and verified choose “Restart and install”; 2.x quits and is replaced by 3.0, which then opens. 2.x must be closed while your data moves.',
  'v3.point.pairing': 'Pair your other devices again in 3.0; the 2.x standalone device service is not supported in 3.0.',
  'v3.point.after': 'Once your data has moved, 2.x on this computer will no longer open it, so the two versions never write it at the same time.',
  'v3.point.rollback': 'You can come back: in 3.0, Settings → Data → “Go back to NextCoWork 2.x” restores your data to how it was before the move (what you did in 3.0 afterwards does not come back).',
  'v3.download': 'Download 3.0',
  'v3.downloading': ({ percent }: Params) => `Downloading ${percent}%`,
  'v3.verifying': 'Verifying…',
  'v3.install': 'Restart and install 3.0',
  'v3.installing': 'Installing…',
  'v3.retry': 'Retry download',
  'v3.error.network': 'The download did not finish. Check your connection and try again.',
  'v3.error.checksum': 'The downloaded file failed its checksum and was discarded. Try again, or just open the download page.',
  'v3.error.extract': 'The installer could not be unpacked. Try again, or open the download page and install manually.',
  'v3.error.signature': 'The installer’s signature check failed, so nothing was installed. Open the download page and install manually.',
  'v3.error.permission': 'No permission to replace the running app (its folder is not writable). Open the download page and install manually.',
  'v3.error.unsupported': 'In-app install is not supported on this platform. Open the download page and install manually.',
  'v3.upgrade': 'Download and quit',
  'v3.upgrading': 'Quitting…',
  'v3.openDownload': 'Just open the download page',
  'v3.notes': '3.0 release notes'
}
