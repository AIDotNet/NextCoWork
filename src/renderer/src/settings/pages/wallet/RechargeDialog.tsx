/**
 * 钱包页的充值弹窗：选档位 → 创建订单（主进程打开 Stripe 收银页）→ 轮询订单直到有结局。
 *
 * 需求：用户在应用内点「充值」就能付款，付完切回来余额自己变。支付本身在系统浏览器里的
 * Stripe 收银台完成 —— 收银地址不经过渲染层（见 `main/ipc/recharge.ts` 文件头），
 * 这里只拿订单号去轮询。
 *
 * 不变式：
 * - **每次打开都是一次全新的流程。** 调用方用 `key` 在每次打开时重建本组件（见 WalletPage），
 *   所以这里不做「打开时重置状态」—— 上一笔订单的结局不会残留到下一次打开。
 * - **关掉弹窗 = 停止轮询**（effect cleanup）。付款若在关掉之后完成，余额等下一次刷新；
 *   这是刻意的：后台偷偷轮询一个用户已经不看的订单，只会在别的页面上突然弹出结果。
 *
 * 故意不做的事：不在这里判定「支付成功」以外的余额变化，余额以 `/api/client/account` 为准，
 * 由 `onCompleted` 触发刷新、经 `clientAuth:changed` 广播回来。
 */
import { CircleCheck, CircleX, Clock } from 'lucide-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import type { RechargeCheckoutResult, RechargeOptions } from '../../../../../shared/domain/recharge'
import { Button } from '../../../components/arc/button/button'
import { Dialog } from '../../../components/ui/Dialog'
import { Spinner } from '../../../components/ui/Spinner'
import { useI18n, type Translate } from '../../../i18n'
import { cn } from '../../../lib/cn'
import { openExternal } from '../../../services/app'
import { createRechargeCheckout, getRechargeOrder } from '../../../services/recharge'
import {
  formatAmount,
  isSettledPhase,
  ORDER_POLL_DEADLINE_MS,
  ORDER_POLL_INTERVAL_MS,
  orderPhaseOf,
  WEB_WALLET_URL,
  type OrderPhase
} from './wallet-model'

type Step =
  | { kind: 'pick'; error: string | null; offerWeb: boolean }
  | { kind: 'creating' }
  | { kind: 'order'; orderNo: string; amount: number; currency: string; phase: OrderPhase; timedOut: boolean }

export function RechargeDialog({
  open,
  options,
  teamName,
  onClose,
  onCompleted
}: {
  open: boolean
  /** 服务端给的档位与币种 —— 只有 `rechargeGate` 判定为 ready 时才会打开这个弹窗 */
  options: RechargeOptions
  /** 会话绑定的 Team 名（领域值，不翻译）；拿不到就用不带名字的说明句 */
  teamName: string | null
  onClose: () => void
  /** 订单到账后调用一次，由钱包页刷新余额与消费记录 */
  onCompleted: () => void
}): React.JSX.Element {
  const { t, locale } = useI18n()
  const [amount, setAmount] = useState<number>(options.amounts[0] ?? 0)
  const [step, setStep] = useState<Step>({ kind: 'pick', error: null, offerWeb: false })
  /** 「刷新状态」把它加一，重新起一轮带新 deadline 的轮询。 */
  const [pollRound, setPollRound] = useState(0)

  /*
    需求：到账回调只能触发一次。
    放进 ref 而不是 effect 依赖：父组件每次重渲都给一个新函数，而它的刷新会经广播让
    父组件重渲 —— 放进依赖就成了「刷新 → 重渲 → 新函数 → 再刷新」的死循环。
  */
  const onCompletedRef = useRef(onCompleted)
  useEffect(() => { onCompletedRef.current = onCompleted }, [onCompleted])

  const orderNo = step.kind === 'order' ? step.orderNo : null
  useEffect(() => {
    if (orderNo === null) return
    let cancelled = false
    let settled = false
    // ★ 防并发：focus 触发的立即查询可能撞上定时那一次。不拦的话会起两条定时链，
    // 表现是轮询频率越切窗口越高。
    let inFlight = false
    let timer: ReturnType<typeof setTimeout> | undefined
    const deadline = Date.now() + ORDER_POLL_DEADLINE_MS
    const poll = async (): Promise<void> => {
      if (cancelled || settled || inFlight) return
      inFlight = true
      clearTimeout(timer)
      const state = await getRechargeOrder(orderNo).catch(() => null)
      inFlight = false
      if (cancelled) return
      const phase = state === null ? null : orderPhaseOf(state)
      if (phase !== null) setStep((s) => (s.kind === 'order' ? { ...s, phase } : s))
      if (phase !== null && isSettledPhase(phase)) {
        settled = true
        if (phase === 'completed') onCompletedRef.current()
        return
      }
      if (Date.now() >= deadline) {
        settled = true
        setStep((s) => (s.kind === 'order' ? { ...s, timedOut: true } : s))
        return
      }
      timer = setTimeout(() => void poll(), ORDER_POLL_INTERVAL_MS)
    }
    // 需求：用户从浏览器付完款切回应用的那一刻最可能已到账，立即查一次，不等下一拍。
    const onFocus = (): void => { void poll() }
    window.addEventListener('focus', onFocus)
    timer = setTimeout(() => void poll(), ORDER_POLL_INTERVAL_MS)
    return () => {
      cancelled = true
      clearTimeout(timer)
      window.removeEventListener('focus', onFocus)
    }
  }, [orderNo, pollRound])

  const pay = (): void => {
    if (step.kind === 'creating') return
    setStep({ kind: 'creating' })
    void createRechargeCheckout(amount)
      .catch((): RechargeCheckoutResult => ({ kind: 'unavailable', reason: 'network' }))
      .then((result) => {
        if (result.kind === 'opened') {
          setStep({ kind: 'order', orderNo: result.orderNo, amount: result.amount, currency: result.currency, phase: 'waiting', timedOut: false })
          return
        }
        if (result.kind === 'rejected') {
          setStep({
            kind: 'pick',
            error: result.message === null || result.message === ''
              ? t('wallet.recharge.rejected')
              : t('wallet.recharge.rejectedDetail', { message: result.message }),
            offerWeb: false
          })
          return
        }
        setStep({ kind: 'pick', error: t(`wallet.recharge.unavailable.${result.reason}`), offerWeb: result.reason === 'unsupported' })
      })
  }

  const openWeb = (): void => {
    void openExternal(WEB_WALLET_URL).catch((error: unknown) => {
      console.warn('[wallet] 打开网页钱包失败:', error)
    })
  }

  const busy = step.kind === 'creating'
  const payLabel = t('wallet.recharge.pay', { amount: formatAmount(amount, options.currency, locale) })

  const footer = step.kind === 'order'
    ? <OrderFooter step={step} t={t} onClose={onClose}
        onCheckAgain={() => { setStep({ ...step, timedOut: false }); setPollRound((n) => n + 1) }}
        onRetry={() => setStep({ kind: 'pick', error: null, offerWeb: false })}
        onOpenWeb={openWeb} />
    : <>
        <Button type="button" variant="secondary" onClick={onClose}>{t('common.cancel')}</Button>
        <Button type="button" variant="primary" disabled={busy || amount <= 0} onClick={pay}>
          {busy ? <Spinner size="sm" /> : undefined}
          {busy ? t('wallet.recharge.creating') : payLabel}
        </Button>
      </>

  return (
    <Dialog
      open={open}
      onClose={onClose}
      width={460}
      title={t('wallet.recharge.title')}
      description={teamName === null
        ? t('wallet.recharge.description')
        : t('wallet.recharge.descriptionTeam', { team: teamName })}
      footer={footer}
    >
      {step.kind === 'order'
        ? <OrderStatus step={step} t={t} locale={locale} />
        : (
          <div className="flex flex-col gap-3">
            <div role="group" aria-label={t('wallet.recharge.amountLabel')} className="grid grid-cols-4 gap-2">
              {options.amounts.map((value) => {
                const on = value === amount
                return (
                  <button
                    key={value}
                    type="button"
                    aria-pressed={on}
                    disabled={busy}
                    onClick={() => setAmount(value)}
                    className={cn(
                      'h-12 rounded-card border text-[14px] tabular-nums transition-colors motion-reduce:transition-none',
                      'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/50 disabled:opacity-40',
                      on ? 'border-accent bg-accent/10 text-fg' : 'border-stroke bg-surface-raised text-fg-muted hover:bg-tint-hover hover:text-fg'
                    )}
                  >
                    {formatAmount(value, options.currency, locale)}
                  </button>
                )
              })}
            </div>
            {step.kind === 'pick' && step.error !== null && (
              <div role="alert" className="flex flex-wrap items-center gap-2 text-[12px] text-danger">
                <span className="min-w-0 flex-1">{step.error}</span>
                {step.offerWeb && <Button type="button" variant="secondary" onClick={openWeb}>{t('wallet.openWeb')}</Button>}
              </div>
            )}
          </div>
        )}
    </Dialog>
  )
}

type OrderStep = Extract<Step, { kind: 'order' }>

/** 订单阶段 → 图标 + 标题 + 说明。等待中的两种带转圈，其余是结局。 */
function OrderStatus({ step, t, locale }: { step: OrderStep; t: Translate; locale: string }): React.JSX.Element {
  const amount = formatAmount(step.amount, step.currency, locale)
  const view = step.timedOut
    ? { icon: <Clock size={26} className="text-fg-faint" />, title: t('wallet.order.timeoutTitle'), hint: t('wallet.order.timeoutHint') }
    : orderView(step.phase, amount, t)
  return (
    <div className="flex flex-col items-center gap-2 py-4 text-center" role="status" aria-live="polite">
      <div className="mb-1 flex size-12 items-center justify-center rounded-full bg-tint">{view.icon}</div>
      <p className="text-[14px] text-fg">{view.title}</p>
      <p className="max-w-[340px] text-[12px] leading-relaxed text-fg-muted">{view.hint}</p>
      <p className="mt-1 text-[11px] text-fg-faint tabular-nums">{t('wallet.order.orderNo', { orderNo: step.orderNo })}</p>
    </div>
  )
}

function orderView(phase: OrderPhase, amount: string, t: Translate): { icon: ReactNode; title: string; hint: string } {
  switch (phase) {
    case 'waiting':
      return { icon: <Spinner size="md" />, title: t('wallet.order.waitingTitle'), hint: t('wallet.order.waitingHint') }
    case 'paid':
      return { icon: <Spinner size="md" />, title: t('wallet.order.paidTitle'), hint: t('wallet.order.paidHint') }
    case 'completed':
      return { icon: <CircleCheck size={26} className="text-accent" />, title: t('wallet.order.completedTitle'), hint: t('wallet.order.completedHint', { amount }) }
    case 'cancelled':
      return { icon: <CircleX size={26} className="text-fg-faint" />, title: t('wallet.order.cancelledTitle'), hint: t('wallet.order.endedHint') }
    case 'failed':
      return { icon: <CircleX size={26} className="text-danger" />, title: t('wallet.order.failedTitle'), hint: t('wallet.order.endedHint') }
    case 'refunded':
      return { icon: <CircleX size={26} className="text-fg-faint" />, title: t('wallet.order.refundedTitle'), hint: t('wallet.order.endedHint') }
    case 'not-found':
      return { icon: <CircleX size={26} className="text-fg-faint" />, title: t('wallet.order.notFoundTitle'), hint: t('wallet.order.notFoundHint') }
    case 'signed-out':
      return { icon: <CircleX size={26} className="text-fg-faint" />, title: t('wallet.order.signedOutTitle'), hint: t('wallet.order.signedOutHint') }
  }
}

/** 每个阶段只给当下有意义的那一两颗按钮。 */
function OrderFooter({
  step,
  t,
  onClose,
  onCheckAgain,
  onRetry,
  onOpenWeb
}: {
  step: OrderStep
  t: Translate
  onClose: () => void
  onCheckAgain: () => void
  onRetry: () => void
  onOpenWeb: () => void
}): React.JSX.Element {
  if (step.timedOut) {
    return <>
      <Button type="button" variant="secondary" onClick={onClose}>{t('common.close')}</Button>
      <Button type="button" variant="primary" onClick={onCheckAgain}>{t('wallet.order.checkAgain')}</Button>
    </>
  }
  switch (step.phase) {
    case 'completed':
      return <Button type="button" variant="primary" onClick={onClose}>{t('common.done')}</Button>
    case 'cancelled':
    case 'failed':
    case 'refunded':
      return <>
        <Button type="button" variant="secondary" onClick={onClose}>{t('common.close')}</Button>
        <Button type="button" variant="primary" onClick={onRetry}>{t('wallet.order.retry')}</Button>
      </>
    case 'not-found':
      return <>
        <Button type="button" variant="secondary" onClick={onClose}>{t('common.close')}</Button>
        <Button type="button" variant="primary" onClick={onOpenWeb}>{t('wallet.openWeb')}</Button>
      </>
    case 'waiting':
    case 'paid':
    case 'signed-out':
      return <Button type="button" variant="secondary" onClick={onClose}>{t('common.close')}</Button>
  }
}
