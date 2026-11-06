/**
 * 安装进度 → 一行人话。
 *
 * ★ 单独一个文件,是因为这段逻辑现在有**两个**显示位:市场卡片上那颗原地变成
 * 进度条的按钮,和安装弹窗底部那一条。两边各写一份的下场是某天改了下载那一档
 * 的文案、另一处还在报旧的 —— 而用户会在同一次安装里先后看到这两处。
 *
 * ★ `downloading` 而拿不到 `total` 时 ratio 是 `null`,不是 0:前者画斜纹
 * (「在动,但不知道还剩多少」),后者画一条空槽(「一点都没下来」)。
 * 服务端不回 Content-Length 的时候,后者是一句谎话。
 */
import type { Translate } from '../../../i18n'
import type { PluginInstallProgress } from '../../../stores/plugins'

/** 下载完成的比例;报不出百分比时给 `null` */
export function installRatio(progress: PluginInstallProgress): number | null {
  if (progress.phase !== 'downloading') return null
  if (progress.total === undefined || progress.total <= 0) return null
  return Math.min(1, (progress.received ?? 0) / progress.total)
}

/** 进度条上那行字。授权与解压两段没有百分比,报阶段 */
export function installLabel(progress: PluginInstallProgress, t: Translate): string {
  if (progress.phase === 'preparing') return t('plugins.preparing')
  if (progress.phase === 'installing') return t('plugins.installing')
  const ratio = installRatio(progress)
  return ratio === null
    ? t('plugins.downloading')
    : t('plugins.downloadingPercent', { percent: Math.round(ratio * 100) })
}
