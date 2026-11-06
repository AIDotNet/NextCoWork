/**
 * 安装进度 → 一行人话。**插件和 Skill 共用这一份。**
 *
 * ★ 两边的主进程推的是**同一个形状**的事件(`plugins:installProgress` /
 * `skills:installProgress`),于是这段「哪一档该显示什么」的判断也只该有一份。
 * 各写一份的下场是某天改了下载那一档的措辞、另一处还在报旧的 —— 而这两处
 * 在界面上挨着,用户一眼就能看出它们不一致。
 *
 * ★ 唯一分岔的是**文案的命名空间**:同样的中文,插件那边挂在 `plugins.*`、
 * Skill 那边挂在 `skills.*`。共用一个 key 的话,「插件下载中」有一天要改成
 * 「插件包下载中」,Skill 那边会跟着变,而没有任何地方提醒你它还有第二个读者。
 *
 * ★ `downloading` 而拿不到 `total` 时 ratio 是 `null`,不是 0:前者画斜纹
 * (「在动,但不知道还剩多少」),后者画一条空槽(「一点都没下来」)。
 * 服务端不回 Content-Length 的时候,后者是一句谎话。
 */
import type { Translate } from '../i18n'

/** 一次安装正在进行到哪一步。装完 / 失败之后这条就从表里消失 */
export interface InstallProgress {
  phase: 'preparing' | 'downloading' | 'installing'
  received?: number
  total?: number
  /** 兜底清理用 —— 主进程崩了的话,终态那一帧永远不会来 */
  startedAt: number
}

/** 文案的命名空间。两边的键名逐个对齐,只差这个前缀 */
export type InstallNamespace = 'plugins' | 'skills'

/** 下载完成的比例;报不出百分比时给 `null` */
export function installRatio(progress: InstallProgress): number | null {
  if (progress.phase !== 'downloading') return null
  if (progress.total === undefined || progress.total <= 0) return null
  return Math.min(1, (progress.received ?? 0) / progress.total)
}

/** 进度条上那行字。授权与解压两段没有百分比,报阶段 */
export function installLabel(
  progress: InstallProgress,
  t: Translate,
  namespace: InstallNamespace = 'plugins'
): string {
  if (progress.phase === 'preparing') return t(`${namespace}.preparing`)
  if (progress.phase === 'installing') return t(`${namespace}.installing`)
  const ratio = installRatio(progress)
  return ratio === null
    ? t(`${namespace}.downloading`)
    : t(`${namespace}.downloadingPercent`, { percent: Math.round(ratio * 100) })
}
