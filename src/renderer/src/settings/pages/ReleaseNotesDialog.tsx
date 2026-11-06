/**
 * 「更新说明」弹窗。
 *
 * 需求:更新说明是从 `CHANGELOG.md` 截下来的 **Markdown**(`### 新增` / `**加粗**` /
 * `- 列表`,见 `.github/workflows/release.yml` 里那段 `awk`),得按 Markdown 渲染。
 * 不满足的症状是整段按纯文本铺开:界面上原样出现 `### 改动` 和 `**macOS 发布包启用
 * 签名与公证**`,而它读起来像一份坏掉的文档 —— 没有报错、没有红字。
 *
 * ## 为什么不直接用 `components/markdown`
 *
 * `AgentMarkdown` 的传递依赖是 streamdown + katex + mermaid,单独一个 chunk 就有
 * 1.0MB。设置浮层是 `AppShell` 的静态依赖,从这里静态引它等于把那一坨拽回主
 * bundle —— 正是 `views/registry.tsx` 文件头那段反复警告的翻车方式。所以这里走
 * `lazy(() => import('../../components/markdown'))`:chunk 只在用户**点开弹窗**时
 * 才拉,而设置页里点一下是一次网络/磁盘往返都算不上慢的交互。
 *
 * ★ **`fallback` 必须画得出内容,不能只画一个转圈。** 这块文字在 chunk 到达之前
 * 就已经在手里了(`releaseNotes` 是主进程随 feed 一起给的),先按纯文本铺开,
 * 换到 Markdown 那一刻才跳版式;转圈的话用户在慢盘上先看到的是一个空弹窗,
 * 而他会以为「更新说明是空的」。chunk 加载失败时 React 会保留已提交的 fallback,
 * 所以这条兜底同时也是「加载失败」时的最终形态 —— 它得是能读的文字。
 *
 * 需求:`open` 为 false 时**不挂载** `lazy` 那一侧。`lazy` 的加载在它首次渲染时
 * 就发起,常挂的话 About 页一打开就会替所有人拉那 1.0MB。
 */
import { lazy, Suspense, type ReactNode } from 'react'
import { Button } from '../../components/arc/button/button'
import { Dialog } from '../../components/ui/Dialog'
import { useI18n } from '../../i18n'

const AgentMarkdown = lazy(() => import('../../components/markdown').then((m) => ({ default: m.AgentMarkdown })))

export function ReleaseNotesDialog({
  notes,
  open,
  onClose
}: {
  /** 主进程给的原始 Markdown。空串由调用方判掉,这里假定非空 */
  notes: string
  open: boolean
  onClose: () => void
}): ReactNode {
  const { t } = useI18n()
  return (
    <Dialog
      title={t('about.updates.releaseNotes')}
      open={open}
      onClose={onClose}
      footer={<Button type="button" variant="secondary" size="sm" onClick={onClose}>{t('common.close')}</Button>}
    >
      {open && (
        <Suspense fallback={<PlainNotes notes={notes} />}>
          <AgentMarkdown content={notes} variant="compact" className="text-[12.5px] leading-[1.6]" />
        </Suspense>
      )}
    </Dialog>
  )
}

/**
 * Markdown 渲染器到达之前(以及加载失败之后)的那一份。
 *
 * 更新说明是要被读、也可能被复制去贴 issue 的整段文字,不是标签 ——
 * 全局 `user-select: none` 在这里得 opt-in,理由和 AboutPage 里那串版本号一样。
 */
function PlainNotes({ notes }: { notes: string }): ReactNode {
  return <div className="selectable whitespace-pre-wrap text-[12.5px] leading-[1.6] text-fg-muted">{notes}</div>
}
