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
  'v3.point.install': '3.0 是单独的安装包:点「前往下载并退出」后,在下载页装好 3.0 再打开它即可。迁移时 2.x 必须是关闭的。',
  'v3.point.pairing': '和其他设备的配对需要在 3.0 里重新扫码;2.x 的独立设备服务 3.0 不再支持。',
  'v3.point.after': '迁移完成后,这台电脑上的 2.x 不会再打开这份数据,以免两个版本同时写坏它。',
  'v3.point.rollback': '升级后想回来也可以:在 3.0 的「设置 → 数据」里选「回到 NextCoWork 2.x」,数据会恢复成迁移前的样子(迁移之后在 3.0 里新写的内容不会带回来)。',
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
  'v3.point.install': '3.0 is a separate installer: choose “Download and quit”, install 3.0 from the download page, then open it. 2.x must be closed while your data moves.',
  'v3.point.pairing': 'Pair your other devices again in 3.0; the 2.x standalone device service is not supported in 3.0.',
  'v3.point.after': 'Once your data has moved, 2.x on this computer will no longer open it, so the two versions never write it at the same time.',
  'v3.point.rollback': 'You can come back: in 3.0, Settings → Data → “Go back to NextCoWork 2.x” restores your data to how it was before the move (what you did in 3.0 afterwards does not come back).',
  'v3.upgrade': 'Download and quit',
  'v3.upgrading': 'Quitting…',
  'v3.openDownload': 'Just open the download page',
  'v3.notes': '3.0 release notes'
}
