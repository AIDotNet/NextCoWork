/**
 * `screenshot` 形态的详情渲染器 —— **浏览器页面截图**(`browser_screenshot`)的卡片。
 *
 * 需求:Agent 截页面的那张图是「这个页面现在长什么样」的唯一证据,而它此前
 * **用户一个字都看不见**。原因不是渲染器画错了:整族浏览器工具都没有登记展示规则,
 * 于是 `browser_screenshot` 落进 `external` 的通用渲染器,而那个渲染器只读
 * `output.content`、根本不碰 `output.images`(见 `ToolShape` 里 `screenshot` 那段)。
 * 表现是:模型对着截图说「首页有个登录按钮」,对话里只有一行英文回执。
 *
 * ★ 和生图卡片(`ImageGenDetail`)刻意不合并成一张:那边要摆 N 格占位、要逐张换图、
 * 要在底下挂提示词区,因为它的入参里有 `n` 和 `prompt`;截图**没有生成期、没有提示词**,
 * 只有一帧当前视口 —— 复用那张卡会在图旁边留一块永远空着的提示词区,
 * 并在等待时显示「生成中」,而它既没在生成也没有变量可等。
 *
 * ★ 不画「打开原图」「钉到工作区」这类入口:图是内联 data URL(`ToolOutputImage`),
 * 不是磁盘上的文件,灯箱里那个「用别的程序打开」也只在绝对路径上出现
 * (见 `ImageLightbox` 的 `isFilesystemPath`)。要留下这张图,该做的是让 Agent 用
 * `SaveImage` 写进工作区 —— 那是模型的决定,不是这里画一个点了没反应的按钮。
 */
import { useState, type ReactNode } from 'react'
import { Camera, ZoomIn } from 'lucide-react'
import { pick } from '../../../../shared/domain/tool-presenter'
import { ncwPreviewUrl } from '../../../../shared/domain/attachment'
import { useI18n } from '../../i18n'
import { cn } from '../../lib/cn'
import { AgentShimmerText } from './AgentActivity'
import { DETAIL_CARD_CLASS } from './detail-card'
import { ImageLightbox } from './ImageLightbox'
import { OutputBlock, type DetailProps } from './ToolDetail'

export function ScreenshotDetail({ input, output, isError }: DetailProps): ReactNode {
  const { t } = useI18n()
  /** 灯箱开着没有。★ 不必按 callId 重置:这张卡片的实例是**按 callId 挂 key** 的
   *  (时间线用 `item.key`、提出来的块用 `pinned:${callId}`),换一次调用就是一个新实例。 */
  const [zoomed, setZoomed] = useState(false)

  // 失败时只给原因:此刻 `output.images` 里没有图,画一张空卡等于说这次成功了
  if (isError) return <OutputBlock output={output} isError maxLines={20} />

  const images = output?.images ?? []
  const image = images[0]
  const tabId = pick(input, 'tabId')
  // 需求:这张卡是「产物即回答」那一档(见 isPinnedShape),默认展开 ——
  // 但图还没到时它不能是一片空白,否则用户看到的是一个凸出来的空盒子。
  if (image === undefined) {
    return (
      <div
        role="status"
        aria-label={t('chat.tool.title.browserScreenshot')}
        data-testid="screenshot-loading"
        className="image-gen-sweep relative flex h-[180px] w-[320px] max-w-full flex-col items-center justify-center gap-2 overflow-hidden rounded-[8px] bg-tint text-fg-faint"
      >
        <Camera size={20} aria-hidden />
        <span className="text-[11.5px]">
          <AgentShimmerText>{t('chat.screenshot.capturing')}</AgentShimmerText>
        </span>
      </div>
    )
  }

  return (
    <div
      role="group"
      aria-label={t('chat.tool.title.browserScreenshot')}
      data-testid="screenshot-card"
      className={cn(DETAIL_CARD_CLASS, 'w-fit max-w-full p-1.5')}
    >
      {/*
        button 而不是给 img 挂 onClick:键盘要能 Tab 到并回车打开(同 MessageImage
        与生图卡片的理由)。整张图是一个可点区域,和那两处的交互保持一致。
      */}
      <button
        type="button"
        onClick={() => setZoomed(true)}
        aria-label={t('chat.zoomImage')}
        title={t('chat.zoomImage')}
        className="app-no-drag group relative block cursor-zoom-in overflow-hidden rounded-[8px] focus-visible:outline-2 focus-visible:outline-accent"
      >
        <img
          src={ncwPreviewUrl(image.dataRef)}
          alt=""
          data-testid="screenshot-image"
          className="block max-h-[360px] max-w-full object-contain"
        />
        {/* 悬停/聚焦时浮出的放大标 —— 否则「能点开」只剩鼠标指针变形这一个线索 */}
        <span
          aria-hidden
          className="pointer-events-none absolute top-1.5 right-1.5 flex rounded-[6px] border border-stroke bg-surface-raised/85 p-1 text-fg-muted opacity-0 transition-opacity group-hover:opacity-100 group-focus-visible:opacity-100 motion-reduce:transition-none"
        >
          <ZoomIn size={13} />
        </span>
      </button>
      {/*
        标签页那一格:一个工作区里可以同时开着几个页面,而转录里连着几张截图时,
        「这是哪个 tab 的」是唯一能把它们对上的东西。`v0 min-w-full` 让它不参与
        `w-fit` 卡的宽度计算(同生图卡片底部的提示词区)。
      */}
      {tabId !== '' && (
        <div className="w-0 min-w-full px-1 pt-1 pb-0.5">
          <span className="block truncate font-mono text-[11px] text-fg-faint">{tabId}</span>
        </div>
      )}
      {zoomed && <ImageLightbox images={images} startIndex={0} onClose={() => setZoomed(false)} />}
    </div>
  )
}
