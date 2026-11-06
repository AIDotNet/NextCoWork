/**
 * 插件问用户一句话时弹出来的那个框。
 *
 * ## 为什么这一层在渲染层,而不是主进程
 *
 * 主进程能弹的只有 `dialog.showMessageBox`,它拼的是**裸文本**:不跟随主题、
 * 不跟随语言。而插件给的每一句话都是 l10n key(`shared/plugin/ui-request.ts`),
 * 只有这里 `t()` 得出来。所以主进程只广播 `plugins:interaction`,由这里画,
 * 再经 `plugins:interactionReply` 把答案送回去。
 *
 * ## 三条必须成立的事
 *
 * 1. **一定要回执。** 关掉、按 Esc、点遮罩都算「取消」,而取消同样要回 ——
 *    不回的话插件那边要等满 5 分钟超时,而它可能正挂在一次工具调用里,
 *    用户看到的是「这一轮卡住了」。
 * 2. **一次只画一个。** 主进程侧没有限制插件同时发几条;这里按到达顺序排队,
 *    后到的等前一个答完。同时弹三个框的话,用户根本分不清哪个答案给了谁。
 * 3. **说清是谁在问。** 插件的弹窗是用户没发起过的界面事件 —— 不写来源,
 *    它看起来就像一次故障。
 */
import { useEffect, useState, type ReactNode } from 'react'
import type { PluginInteractionRequest } from '../../../shared/plugin/ui-request'
import { Button } from '../components/arc/button/button'
import { Dialog } from '../components/ui/Dialog'
import { useI18n, type TranslationKey } from '../i18n'
import { on } from '../services/ipc'
import { replyPluginInteraction } from '../services/plugins'

interface PendingAsk {
  requestId: string
  pluginId: string
  request: PluginInteractionRequest
}

export function PluginInteractionHost(): ReactNode {
  const { t } = useI18n()
  /*
    ★ 队列而不是「当前这一条」:后到的覆盖前一条的话,被覆盖的那条**永远不会
    被回执**,插件那边就挂到超时。见文件头第 1 条。
  */
  const [queue, setQueue] = useState<PendingAsk[]>([])
  const [draft, setDraft] = useState('')

  useEffect(
    () =>
      on('plugins:interaction', (event) => {
        setQueue((current) => [...current, { requestId: event.requestId, pluginId: event.pluginId, request: event.request }])
      }),
    []
  )

  const current = queue[0]
  // 换一条问题就把输入框清空 —— 不清的话上一条的答案会成为下一条的初值
  useEffect(() => {
    setDraft(current?.request.kind === 'input' ? (current.request.initial ?? '') : '')
  }, [current?.requestId])

  if (current === undefined) return null

  const answer = (value: unknown): void => {
    void replyPluginInteraction(current.requestId, value).catch(() => undefined)
    setQueue((rest) => rest.slice(1))
  }
  /** 取消值按种类走:确认框是 `false`,其余是 `null`(与主进程侧的判定一致)。 */
  const cancel = (): void => { answer(current.request.kind === 'confirm' ? false : null) }

  /*
    插件的文案是 `%key%`,注册进 i18n 的是 `plugin.<id>.<key>`。查不到时
    `translate` 原样返回 key —— 难看但**看得见**,同 i18n 缺 key 的既有策略。
  */
  const pluginText = (key: string | undefined): string =>
    key === undefined ? '' : t(`plugin.${current.pluginId}.${key.replace(/^%|%$/g, '')}` as TranslationKey)

  const title = current.request.kind === 'quickPick'
    ? t('pluginAsk.title', { plugin: current.pluginId })
    : pluginText(current.request.titleKey)

  return (
    <Dialog
      title={title === '' ? t('pluginAsk.title', { plugin: current.pluginId }) : title}
      description={t('pluginAsk.from', { plugin: current.pluginId })}
      open
      onClose={cancel}
      footer={
        current.request.kind === 'quickPick' ? undefined : (
          <>
            <Button type="button" variant="secondary" onClick={cancel}>{t('pluginAsk.cancel')}</Button>
            <Button
              type="button"
              variant={current.request.kind === 'confirm' && current.request.danger === true ? 'danger' : 'primary'}
              onClick={() => { answer(current.request.kind === 'confirm' ? true : draft) }}
            >
              {current.request.kind === 'confirm' ? t('pluginAsk.confirm') : t('pluginAsk.submit')}
            </Button>
          </>
        )
      }
    >
      {current.request.kind === 'quickPick' && (
        <div className="flex flex-col gap-1">
          {current.request.items.map((item) => (
            <button
              key={item.id}
              type="button"
              className="rounded-[8px] px-3 py-2 text-left text-[13px] text-fg hover:bg-surface-hover"
              onClick={() => { answer(item.id) }}
            >
              {pluginText(item.labelKey)}
            </button>
          ))}
        </div>
      )}
      {current.request.kind === 'input' && (
        <input
          autoFocus
          type={current.request.password === true ? 'password' : 'text'}
          value={draft}
          onChange={(event) => { setDraft(event.target.value) }}
          onKeyDown={(event) => { if (event.key === 'Enter') answer(draft) }}
          placeholder={pluginText(current.request.placeholderKey) || t('pluginAsk.inputPlaceholder')}
          aria-label={title}
          className="h-9 w-full rounded-[8px] border border-hairline bg-surface px-3 text-[13px] text-fg outline-none placeholder:text-fg-faint"
        />
      )}
      {current.request.kind === 'confirm' && current.request.detailKey !== undefined && (
        <p className="text-[13px] text-fg-muted">{pluginText(current.request.detailKey)}</p>
      )}
    </Dialog>
  )
}
