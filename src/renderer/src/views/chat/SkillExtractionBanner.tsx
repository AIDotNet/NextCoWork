/**
 * 提炼会话的说明卡,渲染在**触发消息正下方**(Thread 的 user 行内)。
 *
 * 需求:提炼会话在界面上只有一句触发语,而真正的工作材料(源会话摘要 + 工作流指令)
 * 只注入模型上下文、不进转录 —— 用户看不到任何「材料已附上」的痕迹,会疑惑
 * 「怎么只有一句话就开跑了」。收起态只占两行(标题 + 来源);点击展开后给出完整信息:
 * 注入说明、源会话 ID / 消息数 / 创建时间、产出位置。
 *
 * 数据来源:源会话的元数据与消息条数(`getSessionSummary`,不读正文),
 * 展开内容零额外 IPC。摘要本身(轮数 / token 数 / 是否截断)在主进程逐 run 现算、
 * 不落库,渲染层拿不到 —— 故意不在卡片里假装展示它;写盘结果由既有的改动审查卡展示。
 *
 * 故意不做的:不提供「打开源会话」按钮 —— 源会话可能已被删除,点下去是一个
 * 必然失败的入口;删除后的回退文案由本组件的加载失败分支给出。
 */
import { useEffect, useId, useState, type ReactNode } from 'react'
import { ChevronRight } from 'lucide-react'
import { useI18n } from '../../i18n'
import { cn } from '../../lib/cn'
import { getSessionSummary } from '../../services/sessions'

interface SourceFacts {
  title: string
  messageCount: number
  createdAt: number
}

export function SkillExtractionBanner({ sourceSessionId }: { sourceSessionId: string }): ReactNode {
  const { t } = useI18n()
  const [expanded, setExpanded] = useState(false)
  const contentId = useId()
  // undefined = 还在取;null = 取不到(已删 / 无权限)。两种终态都如实显示,不猜。
  const [source, setSource] = useState<SourceFacts | null | undefined>(undefined)
  useEffect(() => {
    let cancelled = false
    setSource(undefined)
    void getSessionSummary(sourceSessionId).then(
      (detail) => {
        if (cancelled) return
        setSource({ title: detail.session.title, messageCount: detail.messageCount, createdAt: detail.session.createdAt })
      },
      () => { if (!cancelled) setSource(null) }
    )
    return () => { cancelled = true }
  }, [sourceSessionId])
  return (
    <div data-testid="skill-extraction-banner" className="w-full rounded-card border border-border bg-surface-raised">
      <button
        type="button"
        data-testid="skill-extraction-banner-toggle"
        aria-expanded={expanded}
        aria-controls={contentId}
        onClick={() => setExpanded((value) => !value)}
        className="flex w-full items-center justify-between gap-2 rounded-card px-3 py-2 text-left transition-colors hover:bg-tint-hover"
      >
        <span className="min-w-0">
          <span className="block text-[12px] font-medium text-fg">{t('skillify.banner.title')}</span>
          <span className="mt-0.5 block truncate text-[12px] text-fg-muted">
            {source === null
              ? t('skillify.banner.sourceMissing')
              : source === undefined
                ? t('skillify.banner.sourceLoading')
                : t('skillify.banner.source', { title: source.title })}
          </span>
        </span>
        <ChevronRight size={14} className={cn('shrink-0 text-fg-faint transition-transform duration-180', expanded && 'rotate-90')} />
      </button>
      <div id={contentId} data-testid="skill-extraction-banner-details" hidden={!expanded} className="border-t border-border px-3 py-2">
        <p className="text-[12px] text-fg-muted">{t('skillify.banner.body')}</p>
        <dl className="mt-2 flex flex-col gap-1 text-[12px]">
          <div className="flex min-w-0 gap-2">
            <dt className="shrink-0 text-fg-faint">{t('skillify.banner.detail.sourceId')}</dt>
            <dd className="min-w-0 truncate font-mono text-fg-muted">{sourceSessionId}</dd>
          </div>
          {source !== null && source !== undefined && (
            <>
              <div className="flex gap-2">
                <dt className="shrink-0 text-fg-faint">{t('skillify.banner.detail.messages')}</dt>
                <dd className="text-fg-muted">{t('skillify.banner.detail.messagesValue', { count: source.messageCount })}</dd>
              </div>
              <div className="flex gap-2">
                <dt className="shrink-0 text-fg-faint">{t('skillify.banner.detail.created')}</dt>
                <dd className="text-fg-muted">{new Date(source.createdAt).toLocaleString()}</dd>
              </div>
            </>
          )}
          <div className="flex min-w-0 gap-2">
            <dt className="shrink-0 text-fg-faint">{t('skillify.banner.detail.output')}</dt>
            <dd className="min-w-0 truncate font-mono text-fg-muted">.next-cowork/skills/</dd>
          </div>
        </dl>
      </div>
    </div>
  )
}
