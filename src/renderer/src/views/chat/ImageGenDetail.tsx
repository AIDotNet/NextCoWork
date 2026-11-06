/**
 * `image` 形态的详情渲染器 —— 对话内生图(`generate_image`)的**专属图片卡**。
 *
 * 需求:
 *   1. 生成过程要有自己的样子:按请求张数(`n`)摆 N 格**图片加载格**(光带扫过的
 *      相纸 + 状态字),而不是一行通用的「运行中」;
 *   2. 多张时逐张出图:哪一格先到就先换成图(`ToolCallState.partialImages`,
 *      按格子序号放),其余继续转;
 *   3. 跑完显示成品网格,点任意一张进灯箱、左右翻页;悬停/聚焦时图角浮出放大标,
 *      否则「能点开」这件事只有鼠标指针变形这一个线索;
 *   4. 单张时卡片**收到图的宽度**,不撑满整列 —— 竖图配一张满宽的卡,右边大半是空底色;
 *   5. 提示词是卡片底部独立的一区:标签 + 默认两行 + 超出才给「展开」+ 复制;
 *   6. 每张图自己带**复制 / 下载**(`ImageActions.tsx`):这张图是产物,用户要能把它
 *      贴进别的应用或存到磁盘 —— 在此之前唯一的出口是求 Agent 调一次 `SaveImage`。
 * 「这一刻摆什么」全部由 `image-gen-view.ts` 算,这里只负责摆。
 *
 * ★ 为什么不复用 `MessageImage`:它只画 `ncw://`(附件协议),遇到 `data:` 会
 * 退化成占位 —— 而工具产出的图恰恰全是 data URL(见 `ToolOutputImage`)。
 * 现在跑完的生成图会落成会话附件、`dataRef` 换成 `ncw://`(`kernel/session-images.ts`),
 * 但生成期逐张推来的图(`partialImages`)、存储失败退回内联的那张、以及旧转录仍是
 * data URL,所以这条理由照旧成立;这里用裸 `<img>`,两种 src 都画(CSP 已放行 `ncw:`)。
 * 灯箱 `ImageLightbox` 对 dataRef 不挑,直接复用。
 *
 * ★ 为什么不走 `ToolCard`:图已经在 `output.images` 里(模型也要看它、`latest` 改图
 * 也要找它),再复制一份进 card 等于每张几 MB 的图在转录里存两遍。
 *
 * ★ 故意不做:不画「失败格」。失败的那几张在跑完后直接不占位,只在底部说一句
 * 「成功 3/4 张」——一格永远的叉号比少一格更像界面坏了;失败原因在回执里,模型会转述。
 */
import { Check, Copy, ImageIcon, ZoomIn } from 'lucide-react'
import { motion } from 'motion/react'
import { useCallback, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { pick } from '../../../../shared/domain/tool-presenter'
import { ActionIconButton, useTransientStatus } from '../../components/ui/ActionIconButton'
import { ncwPreviewUrl } from '../../../../shared/domain/attachment'
import { useI18n } from '../../i18n'
import { cn } from '../../lib/cn'
import { copyText } from '../../services/app'
import { useMotionLevel, type MotionLevel } from '../../theme/useMotionLevel'
import { AgentShimmerText } from './AgentActivity'
import { DETAIL_CARD_CLASS } from './detail-card'
import { ImageActions } from './ImageActions'
import { imageGenView, type ImageGenSlot } from './image-gen-view'
import { ImageLightbox } from './ImageLightbox'
import { OutputBlock, type DetailProps } from './ToolDetail'

export function ImageGenDetail({ input, output, isError, partialImages }: DetailProps): ReactNode {
  const { t } = useI18n()
  const level = useMotionLevel()
  /** 灯箱打开时是第几张(在「已有的图」里的位置,不是格子序号);null = 没开 */
  const [zoomed, setZoomed] = useState<number | null>(null)
  // 需求:灯箱在监听键盘的 effect 里依赖 onClose —— 每次渲染换一个新函数会让它在
  // 逐张出图的每一次重渲里都重挂一遍监听、把焦点重抢回关闭钮
  const close = useCallback(() => setZoomed(null), [])

  // 失败时只给原因:此时入参里的 n 格占位全是空承诺(全失败才会走到 isError,见桥的 run)
  if (isError) return <OutputBlock output={output} isError maxLines={20} />

  const view = imageGenView(input, output, partialImages)
  const images = view.slots.flatMap((slot) => (slot.kind === 'image' ? [slot.image] : []))
  const single = view.slots.length === 1
  const settled = output !== undefined
  const prompt = pick(input, 'prompt').trim()
  let imagePosition = 0

  return (
    <div
      role="group"
      aria-label={t('imageGen.card.region')}
      data-testid="image-gen-card"
      data-image-gen-done={view.done}
      data-image-gen-requested={view.requested}
      className={cn(
        DETAIL_CARD_CLASS,
        'p-1.5',
        // 需求 4:单张收到图宽(`w-fit`);`min-w` 是给提示词区留的底线 —— 小图(图标类)
        // 下提示词区只剩几十像素,一行一两个字,读不了。多张仍是定宽网格,格子才对得齐。
        single ? 'w-fit max-w-full min-w-[240px]' : 'max-w-[520px]'
      )}
    >
      <div className={cn(single ? 'flex' : 'grid grid-cols-2 gap-1.5')}>
        {view.slots.map((slot) => {
          if (slot.kind === 'loading') {
            return (
              <LoadingSlot
                key={`slot:${String(slot.index)}`}
                single={single}
                level={level}
                label={t(view.mode === 'edit' ? 'imageGen.card.editing' : 'imageGen.card.generating')}
                ariaLabel={t('imageGen.card.slot', { index: slot.index + 1, total: view.requested })}
              />
            )
          }
          const position = imagePosition
          imagePosition += 1
          return (
            <ImageSlot
              key={`slot:${String(slot.index)}`}
              slot={slot}
              single={single}
              // 只给**生成期到手**的那几张播淡入:跑完/重开历史会话时整片图一起淡入是开场动画,不是反馈
              reveal={!settled}
              label={t('imageGen.card.open', { index: position + 1 })}
              index={position + 1}
              onOpen={() => setZoomed(position)}
            />
          )
        })}
      </div>
      {(prompt !== '' || view.partial) && (
        <PromptFooter
          prompt={prompt}
          partial={view.partial ? t('imageGen.card.partial', { done: view.done, total: view.requested }) : undefined}
        />
      )}
      {zoomed !== null && images.length > 0 && (
        <ImageLightbox images={images} startIndex={Math.min(zoomed, images.length - 1)} onClose={close} />
      )}
    </div>
  )
}

/**
 * 一格加载占位。
 *
 * ★ 动效分三档,各有理由(AGENTS §8):standard/soft 用 CSS 光带(`.image-gen-sweep`);
 * reduced 下 CSS 动画会被主题层压成 0,但「还在画」这件事仍要表达 —— 和
 * `AgentActivityGrid` 同一个办法,用 Motion 做**无位移**的透明度呼吸;off 保持静止,
 * 那时承载信息的是状态字本身。
 */
function LoadingSlot({
  single,
  level,
  label,
  ariaLabel
}: {
  /** 只有一格时用大方格,多格时撑满网格列 */
  single: boolean
  level: MotionLevel
  /** 格子里那句状态字(生成中 / 编辑中) */
  label: string
  /** 读屏念「第 2/4 张」—— 视觉上格子位置已经说明了这件事,读屏没有位置 */
  ariaLabel: string
}): ReactNode {
  const sweep = level === 'standard' || level === 'soft'
  const icon = <ImageIcon size={single ? 22 : 18} aria-hidden />
  return (
    <div
      role="status"
      aria-label={ariaLabel}
      data-testid="image-gen-loading"
      className={cn(
        'relative flex aspect-square flex-col items-center justify-center gap-2 overflow-hidden rounded-[8px] bg-tint text-fg-faint',
        // ★ 单格用定宽而不是 `w-full`:卡片是 `w-fit`(收到内容宽),百分比宽在那里
        // 解析成内容宽 —— 表现为加载格缩成「图标 + 两个字」那么窄
        single ? 'w-[320px] max-w-full' : 'w-full',
        sweep && 'image-gen-sweep'
      )}
    >
      {level === 'reduced' ? (
        <motion.span
          className="flex"
          animate={{ opacity: [1, 0.4, 1] }}
          transition={{ duration: 1.6, repeat: Infinity, ease: 'easeInOut' }}
        >
          {icon}
        </motion.span>
      ) : icon}
      <span className="text-[11.5px]">
        <AgentShimmerText>{label}</AgentShimmerText>
      </span>
    </div>
  )
}

function ImageSlot({
  slot,
  single,
  reveal,
  label,
  index,
  onOpen
}: {
  slot: Extract<ImageGenSlot, { kind: 'image' }>
  /** 单张按原比例显示;多张统一裁成方格,网格才对得齐 —— 原图在灯箱里 */
  single: boolean
  /** 这张是不是刚在生成期到手的 —— 只有它们播淡入 */
  reveal: boolean
  /** 读屏标签「放大查看第 N 张图片」 */
  label: string
  /** 这张在本次调用里的位置(从 1 数)—— 复制 / 下载那排动作据此取名 */
  index: number
  onOpen: () => void
}): ReactNode {
  return (
    /*
      ★ 图片按钮与动作条(复制 / 下载)是**兄弟**:图片整个包在 button 里(键盘要能
      Tab 到并回车放大),而 button 里再放 button 是非法结构 —— 浏览器会把内层那个
      弹到图片外面,表现为「点复制打开了灯箱」。这一层只做两件事:当 `group`
      (动作条默认透明,靠 `group-hover` / `group-focus-within` 浮出)与定位锚点。
      ★ 裁切与圆角留在**按钮自己**身上,不挪到这一层:`overflow-hidden` 会连按钮
      自己的聚焦轮廓一起裁掉 —— 键盘用户 Tab 到图上时那道轮廓是唯一的落点提示。
    */
    <div
      className={cn(
        'group relative block',
        // 生成期到手的那几张才播淡入
        reveal && 'image-gen-reveal'
      )}
    >
      <button
        type="button"
        onClick={onOpen}
        aria-label={label}
        title={label}
        className="app-no-drag block w-full cursor-zoom-in overflow-hidden rounded-[8px] focus-visible:outline-2 focus-visible:outline-accent"
      >
        <img
          src={ncwPreviewUrl(slot.image.dataRef)}
          alt=""
          data-testid="image-gen-image"
          className={cn(
            'block',
            single ? 'max-h-[360px] max-w-full object-contain' : 'aspect-square w-full object-cover'
          )}
        />
      </button>
      {/*
        需求 3:悬停/键盘聚焦时浮出的放大标。**挪到左上角**是因为右上角现在是动作条
        (复制 / 下载),两簇浮层叠在一起会互相压住 —— 而它仍然只是提示:
        真正可点的是整张图(按钮在上面的 aria-label 里已经说清)。
      */}
      <span
        aria-hidden
        className="pointer-events-none absolute top-1.5 left-1.5 flex rounded-[6px] border border-stroke bg-surface-raised/85 p-1 text-fg-muted opacity-0 transition-opacity group-hover:opacity-100 group-focus-within:opacity-100 motion-reduce:transition-none"
      >
        <ZoomIn size={13} />
      </span>
      <ImageActions image={slot.image} index={index} />
    </div>
  )
}

/**
 * 卡片底部的提示词区(需求 5)。
 *
 * prompt 是领域值(模型写的「要画什么」),不翻译;行头只放得下前 48 字,这里给全。
 *
 * ★ 整块用 `w-0 min-w-full`:不参与卡片的内容宽计算,只跟着图宽排版。去掉它,
 * 一段几百字的提示词会把 `w-fit` 的卡片重新撑回满列宽 —— 需求 4 当场失效。
 *
 * ★「展开」只在两行真的装不下时才画(量 scrollHeight),短提示词配一颗点了
 * 没变化的按钮,正是 §5 说的那种会失败的承诺。展开后限高滚动,不让一段长文把
 * 转录推出一屏。
 */
function PromptFooter({
  prompt,
  partial
}: {
  /** 已 trim 的提示词;空串时只画部分失败那句 */
  prompt: string
  /** 「成功生成 3/4 张」的最终文案;全部成功时不传 */
  partial: string | undefined
}): ReactNode {
  const { t } = useI18n()
  const [expanded, setExpanded] = useState(false)
  /** 收起态下两行是否装不下 —— 决定「展开」按钮画不画 */
  const [clamped, setClamped] = useState(false)
  const [copy, setCopy] = useTransientStatus()
  const textRef = useRef<HTMLParagraphElement>(null)

  // 需求:提示词流式变长、卡片随图加载变宽,都会改变「装不装得下」,所以要持续量;
  // 展开态不量(那时没有裁切,量出来恒为 false,会把「收起」按钮也量没)
  useLayoutEffect(() => {
    const el = textRef.current
    if (el === null || expanded) return
    const measure = (): void => setClamped(el.scrollHeight > el.clientHeight + 1)
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(el)
    return () => observer.disconnect()
  }, [prompt, expanded])

  return (
    <div className="w-0 min-w-full px-1 pt-1.5 pb-0.5">
      <div className="flex min-h-6 items-center gap-2 text-[11.5px] text-fg-faint">
        {prompt !== '' && <span className="shrink-0">{t('imageGen.card.prompt')}</span>}
        {partial !== undefined && (
          <span data-testid="image-gen-partial" className="min-w-0 truncate text-danger">
            {partial}
          </span>
        )}
        {prompt !== '' && (
          <div className="ml-auto flex shrink-0 items-center gap-0.5">
            {(clamped || expanded) && (
              <button
                type="button"
                aria-expanded={expanded}
                data-testid="image-gen-prompt-toggle"
                onClick={() => setExpanded(!expanded)}
                className="rounded-[6px] px-1.5 py-0.5 transition-colors hover:bg-tint-hover hover:text-fg-muted motion-reduce:transition-none"
              >
                {t(expanded ? 'imageGen.card.promptCollapse' : 'imageGen.card.promptExpand')}
              </button>
            )}
            <ActionIconButton
              label={t(copy === 'failed' ? 'imageGen.card.promptCopyFailed' : copy === 'done' ? 'imageGen.card.promptCopied' : 'imageGen.card.promptCopy')}
              testId="image-gen-prompt-copy"
              onClick={() => {
                void copyText(prompt).then(() => setCopy('done')).catch(() => setCopy('failed'))
              }}
            >
              {copy === 'done' ? <Check size={12} /> : <Copy size={12} />}
            </ActionIconButton>
          </div>
        )}
      </div>
      {prompt !== '' && (
        <p
          ref={textRef}
          data-testid="image-gen-prompt"
          className={cn(
            'selectable whitespace-pre-wrap break-words text-[12px] leading-relaxed text-fg-muted',
            expanded ? 'scroll-thin max-h-[240px] overflow-auto' : 'line-clamp-2'
          )}
        >
          {prompt}
        </p>
      )}
    </div>
  )
}
