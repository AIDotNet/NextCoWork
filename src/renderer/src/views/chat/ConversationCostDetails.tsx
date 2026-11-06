/*
 * 会话费用的悬停详情。需求：按实际请求模型展示费用与输入/输出/缓存读写分项；
 * 仅在浮层挂载时读取账目，不把全量请求日志带进流式对话的热路径。
 * 分项是依据当前定价对冻结总额的估算，不把它标作精确历史费率。
 */
import { useEffect, useState, type ReactNode } from 'react'
import type { RunCost } from '../../../../shared/domain/pricing'
import { formatCostMicros } from '../../../../shared/domain/pricing'
import type { UsageAttemptRecord } from '../../../../shared/domain/usage'
import { useI18n, type TranslationKey } from '../../i18n'
import { getSessionUsageAttempts } from '../../services/usage'
import { summarizeModelCosts, type ModelCostDetail } from './conversation-cost'

const CATEGORIES: readonly { key: keyof ModelCostDetail['tokens']; label: TranslationKey }[] = [
  { key: 'input', label: 'chat.cost.input' },
  { key: 'output', label: 'chat.cost.output' },
  { key: 'cacheRead', label: 'chat.cost.cacheRead' },
  { key: 'cacheWrite', label: 'chat.cost.cacheWrite' },
  { key: 'cacheWrite1h', label: 'chat.cost.cacheWrite1h' }
]

export function ConversationCostDetails({ sessionId, cost, revision }: {
  /** 当前会话 id；草稿期没有落盘请求，不查询。 */
  sessionId: string | null
  /** 状态行已经使用的累计费用，详情不能用不同口径覆盖它。 */
  cost?: RunCost | null
  /** Token 也驱动刷新：未计价会话的 cost 始终为 null，不能只看金额变化。 */
  revision: string
}): ReactNode {
  const { t, locale } = useI18n()
  const [rows, setRows] = useState<UsageAttemptRecord[] | null>(null)
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    if (sessionId === null) { setRows([]); return }
    let active = true
    getSessionUsageAttempts(sessionId).then((result) => {
      if (active) { setRows(result); setFailed(false) }
    }).catch(() => {
      if (active) setFailed(true)
    })
    // 需求：切会话或关浮层后旧查询不能把上一个会话的账目灌进新卡片。
    return () => { active = false }
  }, [sessionId, revision])

  const models = rows === null ? [] : summarizeModelCosts(rows)
  // 需求：混合币种或缺价时不把各模型的数字硬加成一笔看似准确的会话费用。
  const combined = cost === undefined || cost === null || models.some((model) =>
    model.currency !== cost.currency || model.parts === null || model.unpriced)
    ? null : CATEGORIES.map(({ key }) => models.reduce((sum, model) => sum + (model.parts?.[key] ?? 0), 0))
  const format = (micros: number | null, currency: ModelCostDetail['currency']): string =>
    micros === null || currency === null ? '—' : formatCostMicros(micros, currency)

  return <div className="w-[300px] tabular-nums">
    <div className="flex justify-between gap-4 font-medium">
      <span>{t('chat.conversationUsageCost')}</span>
      <span>{cost === undefined || cost === null ? '—' : formatCostMicros(cost.micros, cost.currency)}</span>
    </div>
    {failed ? <div className="mt-2 text-fg-muted">{t('chat.cost.loadFailed')}</div> :
      rows === null ? <div className="mt-2 text-fg-muted">{t('common.loading')}</div> :
      models.length === 0 ? <div className="mt-2 text-fg-muted">{t('chat.cost.noRecords')}</div> : <>
        <div className="mt-2 border-t border-stroke pt-2">
          {CATEGORIES.map(({ key, label }, index) => <div key={key} className="mt-1 flex justify-between gap-2">
            <span className="text-fg-muted">{t(label)} · {models.reduce((sum, model) => sum + model.tokens[key], 0).toLocaleString(locale)}</span>
            <span>{format(combined?.[index] ?? null, cost?.currency ?? null)}</span>
          </div>)}
          {models.some((model) => (model.parts?.other ?? 0) !== 0) && <div className="mt-1 flex justify-between gap-2">
            <span className="text-fg-muted">{t('chat.cost.other')}</span>
            <span>{format(combined === null ? null : models.reduce((sum, model) => sum + (model.parts?.other ?? 0), 0), cost?.currency ?? null)}</span>
          </div>}
        </div>
        {models.map((model) => <div key={model.key} className="mt-2 border-t border-stroke pt-2">
          <div className="flex justify-between gap-2 font-medium">
            <span className="min-w-0 break-all">{model.model}</span>
            <span className="shrink-0">{format(model.micros, model.currency)}</span>
          </div>
          <div className="text-fg-muted">{model.provider}</div>
          {CATEGORIES.map(({ key, label }) => <div key={key} className="mt-1 flex justify-between gap-2">
            <span className="text-fg-muted">{t(label)} · {model.tokens[key].toLocaleString(locale)}</span>
            <span>{format(model.parts?.[key] ?? null, model.currency)}</span>
          </div>)}
          {model.parts !== null && model.parts.other !== 0 && <div className="mt-1 flex justify-between gap-2">
            <span className="text-fg-muted">{t('chat.cost.other')}</span>
            <span>{format(model.parts.other, model.currency)}</span>
          </div>}
          {model.unpriced && <div className="mt-1 text-fg-muted">{t('chat.cost.unpriced')}</div>}
          {model.mixedCurrency && <div className="mt-1 text-fg-muted">{t('chat.cost.mixedCurrency')}</div>}
        </div>)}
        <div className="mt-2 border-t border-stroke pt-2 text-fg-muted">{t('chat.cost.estimateNote')}</div>
      </>}
  </div>
}
