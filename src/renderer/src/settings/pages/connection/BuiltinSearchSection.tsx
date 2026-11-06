/**
 * 「设置 › 连接 › 搜索」页最底下那一小节:免 Key 的内置搜索。
 *
 * ## 为什么它不是列表里的第 9 行
 *
 * 上面那张列表的每一行都是「一家要 Key、可开关、可拖排序」的服务,而内置源
 * 三样都不是:它不需要 Key,永远排在全部付费源之后,也没有开关(付费链有结果时
 * 根本轮不到它,没结果时把它关掉只会让用户收获一句拒绝)。混进列表的话,
 * 用户会以为它和 Tavily 是同一类东西,并开始拖它的位置。
 *
 * ## 这一小节承担的是「告知」
 *
 * 这条兜底刻意**不弹窗**(弹窗会打断一次本该无感的兜底),所以
 * 「查询词会发给公共实例」「质量低于专业服务」只在这里说 —— 文案在
 * `i18n/builtin-search.ts`,删减那两句等于把告知去掉了。
 *
 * ## 状态归谁
 *
 * 实例地址是 `AppSettings.builtinSearch.searxngUrl`,**主进程唯一权威**:
 * 这里只读 prop、只用 `patch` 写,不在本地 `useState` 一份镜像(`settings/props.ts`)。
 * 唯一的本地状态是那颗测试按钮的结果 —— 它不是设置,是一次操作的回执。
 */
import { Search } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Button } from '../../../components/arc/button/button'
import { Spinner } from '../../../components/ui/Spinner'
import { useI18n } from '../../../i18n'
import { testBuiltinSearch } from '../../../services/websearch'
import { DraftInput } from '../../DraftInput'
import { SettingField, SettingGroup } from '../../Row'
import type { SettingsPageProps } from '../../props'

export function BuiltinSearchSection({ settings, patch }: Omit<SettingsPageProps, 'sub'>): ReactNode {
  const { t } = useI18n()
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<string | null>(null)

  const test = (): void => {
    setBusy(true)
    setResult(null)
    void testBuiltinSearch()
      .then((r) => {
        if (!r.ok) {
          setResult(r.error.message)
          return
        }
        const { ok, latencyMs, source, message } = r.data
        setResult(
          ok
            ? t('connection.builtinSearch.ok', {
                source: source ?? '',
                latency: latencyMs ?? 0
              })
            : t('connection.builtinSearch.failed', {
                // 全挂时主进程给的是每一层的原话;真的一条都没有时才退到这句
                reason: message ?? t('connection.builtinSearch.unknownError')
              })
        )
      })
      .finally(() => setBusy(false))
  }

  return (
    <SettingGroup>
      <div className="px-4">
        <SettingField
          title={t('connection.builtinSearch.title')}
          description={t('connection.builtinSearch.hint')}
        >
          <p className="text-[12.5px] text-fg">{t('connection.builtinSearch.instanceLabel')}</p>
          <p className="mt-1 text-[12px] leading-[1.5] text-fg-muted">
            {t('connection.builtinSearch.instanceHint')}
          </p>
          <div className="mt-2 flex items-center gap-2">
            <div className="min-w-0 flex-1">
              <DraftInput
                value={settings.builtinSearch.searxngUrl}
                disabled={busy}
                ariaLabel={t('connection.builtinSearch.instanceLabel')}
                placeholder={t('connection.builtinSearch.placeholder')}
                onCommit={(searxngUrl) => patch({ builtinSearch: { searxngUrl } })}
              />
            </div>
            <Button
              type="button"
              variant="secondary"
              size="sm"
              disabled={busy}
              onClick={test}
            >
              {busy ? <Spinner size="xs" /> : <Search size={12} />}
              {busy ? t('connection.builtinSearch.testing') : t('connection.builtinSearch.test')}
            </Button>
          </div>
          {result !== null && (
            <p className="mt-2 text-[11.5px] leading-[1.6] text-fg-muted">{result}</p>
          )}
        </SettingField>
      </div>
    </SettingGroup>
  )
}
