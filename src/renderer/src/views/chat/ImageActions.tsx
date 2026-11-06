/**
 * 每张产物图右上角那一排动作 —— 复制 / 下载。
 *
 * 需求:生成的图是产物,而它此前**没有任何出口**(为什么它够不着灯箱里那个
 * 「用别的程序打开」,见 `image-export.ts` 文件头)。这里只负责「点了之后显示
 * 什么」:动作本身是两条已有的 IPC 通道(`app:copyImage` / `app:saveImageFile`),
 * 取字节全在 `image-export.ts`。
 *
 * ★ 动作条与图片**必须是兄弟,不能是子节点**:图片整个包在一个 `<button>` 里
 * (键盘要能 Tab 到并回车放大,同 `MessageImage`),而按钮套按钮是非法结构 ——
 * 浏览器会把内层那个弹到外面,表现为「点复制却打开了灯箱」。
 *
 * ★ 默认透明,悬停或焦点进来才浮出(`group-hover` / `group-focus-within`,类名挂在
 * 卡片外层那个 `group` 容器上)。用 `group-focus-within` 而不是 `group-focus-visible`:
 * 后者匹配的是**容器自己**获得键盘焦点,而容器不可聚焦 —— 键盘用户 Tab 到这两颗
 * 按钮时,它们会停在看不见的状态上。
 *
 * ★ 复制的是**图本身**,不是它的地址:用户此刻看着的就是那张图,要把它贴进别的
 * 应用里。地址在工具回执正文里(模型点名用),不是这一步要的东西。
 */
import { Check, Copy, Download } from 'lucide-react'
import type { ReactNode } from 'react'
import type { ToolOutputImage } from '../../../../shared/agent/message'
import { ActionIconButton, useTransientStatus } from '../../components/ui/ActionIconButton'
import { useI18n } from '../../i18n'
import { cn } from '../../lib/cn'
import { copyImage, saveImageFile } from '../../services/app'
import { imageBase64, imageFileName } from './image-export'

export function ImageActions({
  image,
  index
}: {
  image: ToolOutputImage
  /** 这张图在**本次调用里**的位置(从 1 数,与卡片上「第 N 张」同一个数)—— 只用来取默认文件名 */
  index: number
}): ReactNode {
  const { t } = useI18n()
  const [copy, setCopy] = useTransientStatus()
  const [save, setSave] = useTransientStatus()

  const onCopy = (): void => {
    // 读字节与写剪贴板任一步失败都只在这颗按钮上闪一下 —— 卡片其余部分此时没有任何异常
    void imageBase64(image.dataRef)
      .then(copyImage)
      .then(() => setCopy('done'))
      .catch(() => setCopy('failed'))
  }

  const onSave = (): void => {
    void imageBase64(image.dataRef)
      .then((base64) => saveImageFile(imageFileName(index), base64))
      .then((saved) => {
        // null = 用户在系统对话框里按了取消。那不是失败,**不闪任何状态**
        // —— 给取消也标一个「已保存」或「失败」都是在说谎(同 RewardsOverlay 那条)。
        if (saved !== null) setSave('done')
      })
      .catch(() => setSave('failed'))
  }

  return (
    <span
      className={cn(
        'app-no-drag absolute top-1.5 right-1.5 flex items-center gap-0.5 rounded-[6px] border border-stroke bg-surface-raised/85 p-0.5',
        'opacity-0 transition-opacity group-hover:opacity-100 group-focus-within:opacity-100 motion-reduce:transition-none'
      )}
    >
      <ActionIconButton
        label={t(
          copy === 'failed'
            ? 'imageGen.card.imageCopyFailed'
            : copy === 'done'
              ? 'imageGen.card.imageCopied'
              : 'imageGen.card.imageCopy'
        )}
        // 失败要**看得见**,不能只活在悬停提示里:那一刻用户已经点了、手可能已经挪开
        tone={copy === 'failed' ? 'danger' : 'plain'}
        testId="image-action-copy"
        onClick={onCopy}
      >
        {copy === 'done' ? <Check size={12} /> : <Copy size={12} />}
      </ActionIconButton>
      <ActionIconButton
        label={t(
          save === 'failed'
            ? 'imageGen.card.imageSaveFailed'
            : save === 'done'
              ? 'imageGen.card.imageSaved'
              : 'imageGen.card.imageSave'
        )}
        tone={save === 'failed' ? 'danger' : 'plain'}
        testId="image-action-download"
        onClick={onSave}
      >
        {save === 'done' ? <Check size={12} /> : <Download size={12} />}
      </ActionIconButton>
    </span>
  )
}
