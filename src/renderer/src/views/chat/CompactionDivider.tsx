/**
 * 消息流里标记「上下文在这里被压缩过」的一条线。
 *
 * ## 需求
 *
 * 压缩边界现在是转录里的**一条消息**(带 `compact_boundary` 块,见
 * `shared/agent/compaction.ts`),它自己就带着这次压缩的全部事实:触发方式、
 * 压缩前后的占用、摘要正文、重附回来的文件。所以这一处不再需要向主进程
 * 二次拉取任何东西 —— 原先那套「检查点表 + `context:window` 投影 + 笔记编辑」
 * 整个删掉了,它描述的是已经不存在的机械压缩。
 *
 * ★ **它标的是位置,不是一条消息。** 所以做成一条贯穿的细线加一枚居中的药丸,
 *   而不是又一个气泡 —— 气泡会被读成「有人说了句话」,而这里发生的事情是
 *   更早的历史从此不再原样发给模型。
 *
 * ★ 只复用已有的 token 和结构:线是 `h-px bg-stroke`(装饰性收边,不是控件轮廓 ——
 *   见 `theme.css` 里 `--color-stroke` 那段),药丸是 `rounded-pill bg-tint`,
 *   展开区走公共的 `Surface`。不为这一处新起一套设计语言。
 *
 * ★ 摘要用 `AgentMarkdown` 的 `compact` 变体渲染:它是模型按九节模板写出来的
 *   Markdown,当纯文本铺出来会是一整屏没有层次的 `##`。
 */
import { useState, type ReactNode } from 'react'
import { ChevronDown } from 'lucide-react'
import type { CompactBoundary } from '../../../../shared/agent/compaction'
import { useI18n } from '../../i18n'
import { AgentMarkdown } from '../../components/markdown'
import { Surface, SurfaceReveal } from '../../components/ui/Surface'
import { cn } from '../../lib/cn'

export function CompactionDivider({
  boundary,
  foldedCount
}: {
  boundary: CompactBoundary
  /** 上一条线到这条线之间的可见消息数 —— 这一刀切掉的范围。 */
  foldedCount: number
}): ReactNode {
  const { t } = useI18n()
  const [open, setOpen] = useState(false)
  const trigger = t(boundary.trigger === 'manual' ? 'chat.compaction.manual' : 'chat.compaction.auto')

  return (
    <div className="flex flex-col gap-2" data-testid="compaction-divider" data-trigger={boundary.trigger}>
      <div className="flex items-center gap-2">
        <span aria-hidden className="h-px flex-1 bg-border" />
        <button
          type="button"
          onClick={() => setOpen((value) => !value)}
          aria-expanded={open}
          aria-label={t('chat.compaction.toggle')}
          className="inline-flex shrink-0 items-center gap-1.5 rounded-pill bg-tint px-2.5 py-1 text-[11px] text-fg-faint transition hover:text-fg"
        >
          <ChevronDown size={12} aria-hidden className={cn('transition-transform', !open && '-rotate-90')} />
          <span>{t('chat.compaction.label')}</span>
          <span aria-hidden className="text-fg-faint/50">·</span>
          <span>{trigger}</span>
          {/*
            需求:压缩模型可以单独配置之后,「这份摘要是谁写的」不再等于「会话模型是谁」。
            药丸上只放模型名(别名是领域值,不翻译 §6.5);档位和回落说明放进展开区 ——
            这一行在窄窗口下已经要和 `foldedCount` 抢位置了。
            ★ 旧边界没有这个字段,那时一个字都不画:没有事实就不画字段(§5 不做防御式 UI)。
          */}
          {boundary.summaryModel !== undefined && (
            <>
              <span aria-hidden className="text-fg-faint/50">·</span>
              <span className="max-w-[14ch] truncate">{boundary.summaryModel.model}</span>
            </>
          )}
          {foldedCount > 0 && (
            <>
              <span aria-hidden className="text-fg-faint/50">·</span>
              <span>{t('chat.compaction.folded', { count: foldedCount })}</span>
            </>
          )}
        </button>
        <span aria-hidden className="h-px flex-1 bg-stroke" />
      </div>

      <SurfaceReveal open={open}>
        <Surface className="pb-2">
          <TokenDelta before={boundary.preTokens} after={boundary.postTokens} />
          {/*
            ★ 这一句必须留着:用户会把这条线读成「上面的对话被删了」。
            聊天记录一个字都没少,变的只是**下一次请求发出去的范围**。
          */}
          <p className="mb-2 text-[11px] text-fg-faint">{t('chat.compaction.hint')}</p>
          {/*
            谁写的、想了多深。★ 回落那一句**必须显示**:静默回落是我们刻意的设计
            (配置过期不该让压缩失败),但用户得能知道「我配的那个模型这次没用上」,
            否则他只会觉得配置没生效。
          */}
          {boundary.summaryModel !== undefined && (
            <p className="mb-2 text-[11px] text-fg-faint" data-testid="compaction-summary-model">
              {t('chat.compaction.writtenBy', {
                model: boundary.summaryModel.model,
                level: t(`chat.thinkingLevel.${boundary.summaryModel.thinking}`)
              })}
              {boundary.summaryModel.fellBack === true && (
                <span className="ml-1 text-warning">{t('chat.compaction.fellBack')}</span>
              )}
            </p>
          )}
          {boundary.restoredFiles !== undefined && boundary.restoredFiles.length > 0 && (
            <div className="mb-2 flex flex-col gap-0.5" data-testid="compaction-restored">
              <span className="text-[11px] text-fg-faint">
                {t('chat.compaction.restored', { count: boundary.restoredFiles.length })}
              </span>
              {/* 文件路径是领域值,不翻译(§6.5)。 */}
              {boundary.restoredFiles.map((path) => (
                <span key={path} className="truncate font-mono text-[11px] text-fg-muted">{path}</span>
              ))}
            </div>
          )}
          {boundary.instructions !== undefined && boundary.instructions !== '' && (
            <p className="mb-2 text-[11.5px] text-fg-muted" data-testid="compaction-instructions">
              {t('chat.compaction.instructions')}: {boundary.instructions}
            </p>
          )}
          {/*
            需求:摘要限高。它是九节模板写出来的整篇文档,不限高时一展开就是一整屏,
            把这条线前后的消息全部顶出视口 —— 分隔线的语义是「标位置」,不该占据整屏。
            限高值跟 TerminalBlock / Thread 里滚动区的既有档位一致(min(50vh,420px)),
            内部滚动,`pr-1` 给滚动条留槽,不压正文右缘。
          */}
          <div className="scroll-thin max-h-[min(50vh,420px)] overflow-y-auto pr-1">
            <AgentMarkdown content={boundary.summary} variant="compact" />
          </div>
        </Surface>
      </SurfaceReveal>
    </div>
  )
}

/**
 * 压缩前后的 token 对比。
 *
 * ★ **压缩前的读数为 0 就整块不画。** 那意味着这一轮一次上游真值都没拿到
 * (会话刚从别处导入、或第一轮就被动压缩),此时单独显示一个数字会被读成
 * 「压缩后就这么多」,而它恰恰是压缩**前**的。
 */
function TokenDelta({ before, after }: { before: number; after: number }): ReactNode {
  const { t } = useI18n()
  if (before <= 0) return null
  const ratio = Math.min(1, Math.max(0, after / before))
  return (
    <div className="mb-2 flex items-center gap-2 text-[11px] text-fg-faint">
      <span>{t('chat.compaction.tokens', { before, after })}</span>
      <div className="h-[3px] w-16 overflow-hidden rounded-pill bg-tint">
        <div className="h-full rounded-pill bg-fg-faint" style={{ width: `${ratio * 100}%` }} />
      </div>
    </div>
  )
}
