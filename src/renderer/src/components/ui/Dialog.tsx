/**
 * 模态弹窗 —— Arc 的 `Dialog` / `DialogContent`,外加本应用外壳的几条约束。
 *
 * 动画、遮罩、焦点陷阱、Esc、点外面关闭、标题与关闭按钮全是 Arc(Radix)的。
 * 留这一层是因为下面几条约束对 49 个调用点都成立,不该散落在每一处:
 *
 * 1. **`app-no-drag`。** 顶部那条标题栏是 `-webkit-app-region: drag`,OS 吞掉该区域里
 *    所有 pointer 事件 —— 不加的话弹窗上半部分点不动,一按住整个窗口跟着鼠标跑。
 * 2. **z 轴。** Arc 弹窗自带 z 50/51,而本应用模态是 100(设置浮层就在这一档)——
 *    从设置里打开的弹窗会整块压在浮层背后。`.ncw-dialog` 这个类就是给
 *    `styles/arc-integration.css` 认的,那边把整组 portal 抬到 100。
 * 3. **Esc 只关这一层。** 设置浮层在 `document` 上挂着冒泡阶段的 Esc 监听;Radix 的
 *    Esc 监听在捕获阶段,在这里 `stopPropagation` 就拦得住它,否则一次 Esc 弹窗和
 *    浮层一起没了。
 * 4. **宽度。** 调用点按内容给宽度(默认 520);Arc 默认 440。用行内样式给,
 *    CSS Modules 不在层里,Tailwind 类压不住它。
 *
 * ★ **类型选择器请用 `SegmentedControl`,不要用 `Menu`** —— `ModelPage.tsx:9-13`
 * 记着 Menu 在滚动区里会被裁掉。
 */
import type { ReactNode } from 'react'
import { Dialog as ArcDialog, DialogContent } from '../arc/dialog/dialog'

export function Dialog({
  title,
  description,
  open,
  onClose,
  footer,
  width = 520,
  children
}: {
  title: string
  /** 标题下那行小字。不给就不占位 */
  description?: string
  open: boolean
  onClose: () => void
  /** 右下角那排按钮。由调用方给 —— 「保存」的可用性只有表单自己知道 */
  footer?: ReactNode
  width?: number
  children: ReactNode
}): ReactNode {
  return (
    <ArcDialog
      open={open}
      onOpenChange={(next) => {
        if (!next) onClose()
      }}
    >
      <DialogContent
        title={title}
        description={description}
        className="ncw-dialog app-no-drag"
        // Radix 用 aria-labelledby 指向标题;再挂一份 aria-label 是给探针脚本和测试按标题找弹窗的
        aria-label={title}
        style={{ width: `min(calc(100vw - 20px), ${String(width)}px)` }}
        onEscapeKeyDown={(event) => event.stopPropagation()}
      >
        {children}
        {footer !== undefined && <div className="mt-5 flex items-center justify-end gap-2">{footer}</div>}
      </DialogContent>
    </ArcDialog>
  )
}
