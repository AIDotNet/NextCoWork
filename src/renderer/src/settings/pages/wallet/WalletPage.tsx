/**
 * 设置 › 钱包：余额卡片 + 应用内充值。
 *
 * 需求：钱包页重写，并提供「充值」—— 点了在应用内选金额、跳 Stripe 付款、付完余额自己更新。
 * 以前这一页是 `AccountPage` 的 `walletOnly` 分支，只有一张余额卡和一张平铺的用量表。
 *
 * 需求：钱包页不再展示消费记录（逐条明细、按天合计、空态整块撤掉），所以这里也不再调
 * `clientAuth:getUsage`；配套的 `groupUsageByDay` / `formatCost` / `formatCount` 纯逻辑、
 * 它们的单测、以及 `wallet.usage.*` / `wallet.loading` 文案一并删除，不留无人引用的死代码。
 * 需要逐条明细时走「使用统计」页（`settings/pages/usage`），别再在这里长回来。
 *
 * 不变式：
 * - ★ **余额不在这里存一份。** 读 `auth.user.wallet`（`App.tsx` 订阅 `clientAuth:changed`
 *   后经 props 传下来）；刷新只负责**发起** `getClientUser()`，结果由广播回来。
 *   同 `AccountMenu.tsx` 文件头：页面里再存一份余额，迟早复现「这里和菜单里的数字对不上」。
 * - **充值按钮只在服务端说能充时才画**（`rechargeGate`）。不能充时画一句原因，不画一颗
 *   点了必失败的按钮（§5）。
 * - 这一页只在登录后出现（`nav.ts` 的 `requiresSignIn`），未登录的会话被
 *   `resolveSettingsPage` 送去账户页；这里对 `user === null` 的处理只是类型收窄。
 */
import { Plus, RefreshCw, UserCircle, Users, Wallet } from 'lucide-react'
import { useCallback, useEffect, useState } from 'react'
import type { ClientAuthState } from '../../../../../shared/domain/client-auth'
import type { RechargeOptionsState } from '../../../../../shared/domain/recharge'
import { Button } from '../../../components/arc/button/button'
import { EmptyState } from '../../../components/arc/empty-state/empty-state'
import { IconButton } from '../../../components/ui/IconButton'
import { Spinner } from '../../../components/ui/Spinner'
import { useI18n, type Translate } from '../../../i18n'
import { cn } from '../../../lib/cn'
import { openExternal } from '../../../services/app'
import { getClientUser } from '../../../services/client-auth'
import { getRechargeOptions } from '../../../services/recharge'
import { ClientTeamSelectionView } from '../../../views/ClientTeamSelectionView'
import { RechargeDialog } from './RechargeDialog'
import {
  formatBalance,
  rechargeGate,
  WEB_WALLET_URL,
  type RechargeGateReason
} from './wallet-model'

export function WalletPage({
  auth
}: {
  /** App 级登录态快照 —— 余额的唯一真源，见文件头 */
  auth: ClientAuthState
}): React.JSX.Element {
  const { t, locale } = useI18n()
  const user = auth.mode === 'authenticated' ? auth.user : null
  const contextRequired = auth.contextRequired === true
  const [options, setOptions] = useState<RechargeOptionsState | null>(null)
  const [refreshing, setRefreshing] = useState(false)
  const [rechargeOpen, setRechargeOpen] = useState(false)
  /*
    ★ 每次打开充值弹窗都 +1，作为弹窗的 `key`：保证每次都是全新的一次流程，
    上一笔订单的结局（「充值成功」「支付已取消」）不会在下次打开时残留。
    不在打开时重建而在关闭时卸载的话，弹窗的关闭动画会被截掉。
  */
  const [rechargeSession, setRechargeSession] = useState(0)

  /*
    需求：一次刷新同时拉余额和充值资格。两者互不依赖，各自失败各自降级 ——
    充值资格失败会落成 `unavailable`。
    余额的结果不在这里接（见文件头），`getClientUser` 只负责触发广播。
  */
  const refresh = useCallback(async (): Promise<void> => {
    setRefreshing(true)
    const [, nextOptions] = await Promise.allSettled([
      getClientUser(),
      getRechargeOptions()
    ])
    setOptions(nextOptions.status === 'fulfilled' ? nextOptions.value : { kind: 'unavailable', reason: 'network' })
    setRefreshing(false)
  }, [])

  /*
    需求：换 Team（选择 Team 之后 `selectedTeamId` 变化）必须重拉 —— 余额和充值资格
    都按会话 Team 计，不重拉就会把上一个 Team 的数字挂在新 Team 的余额下面。
    ★ 依赖里放 `signedIn` 这个布尔而不是 `user` 对象：刷新会触发 `clientAuth:changed`
    广播，每次回来的都是新的 user 对象 —— 依赖它就成了「刷新 → 新对象 → 再刷新」的死循环。
  */
  const teamId = auth.selectedTeamId ?? null
  const signedIn = user !== null
  useEffect(() => {
    if (!signedIn || contextRequired) return
    void refresh()
  }, [refresh, teamId, signedIn, contextRequired])

  if (user === null) {
    return <EmptyState icon={<UserCircle size={28} />} title={t('auth.notSignedIn')} description={t('auth.signInFromWelcome')} />
  }
  if (contextRequired) {
    /* 选完 Team 后主进程会广播 `clientAuth:changed`，新的 auth 经 props 回来，这里无需接结果。 */
    return <ClientTeamSelectionView auth={auth} onComplete={() => undefined} />
  }

  const wallet = user.wallet ?? null
  const teamName = auth.teams?.find((team) => team.id === teamId)?.name ?? null
  const gate = rechargeGate(options)

  return (
    <div className="flex flex-col gap-6 pb-4">
      <section className="rounded-card border border-stroke bg-surface-raised">
        <div className="flex items-start gap-4 px-5 pt-5">
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2 text-[12px] text-fg-muted">
              <Wallet size={13} className="text-accent-soft" />
              <span>{t('wallet.balance')}</span>
              {teamName !== null && (
                <span
                  className="inline-flex min-w-0 items-center gap-1 rounded-pill bg-tint px-2 py-0.5 text-[11px] text-fg-muted"
                  aria-label={`${t('wallet.teamLabel')}: ${teamName}`}
                >
                  <Users size={11} className="shrink-0" />
                  <span className="truncate">{teamName}</span>
                </span>
              )}
            </div>
            <div className="mt-2 flex items-baseline gap-1.5">
              <span className="text-[32px] leading-none font-medium tracking-tight text-fg tabular-nums">
                {wallet === null ? '—' : formatBalance(wallet.availableBalance, locale)}
              </span>
              {wallet !== null && <span className="text-[13px] text-fg-muted">{wallet.currency}</span>}
            </div>
          </div>
          <div className="flex shrink-0 items-center gap-2">
            <IconButton label={t('wallet.refresh')} disabled={refreshing} onClick={() => void refresh()}>
              {refreshing ? <Spinner size="sm" /> : <RefreshCw size={14} />}
            </IconButton>
            {gate.kind === 'ready' && (
              <Button
                type="button"
                variant="primary"
                onClick={() => {
                  setRechargeSession((n) => n + 1)
                  setRechargeOpen(true)
                }}
              >
                <Plus size={14} />
                {t('wallet.recharge')}
              </Button>
            )}
          </div>
        </div>

        {gate.kind === 'blocked' && <BlockedNotice reason={gate.reason} t={t} />}

        {wallet === null
          ? <p className="mt-5 border-t border-hairline px-5 py-4 text-[12px] text-fg-faint">{t('wallet.unavailable')}</p>
          : (
            <dl className="mt-5 grid grid-cols-3 border-t border-hairline">
              <Stat label={t('wallet.cash')} value={formatBalance(wallet.cashBalance, locale)} />
              <Stat label={t('wallet.gift')} value={formatBalance(wallet.giftBalance, locale)} divided />
              <Stat label={t('wallet.consumed')} value={formatBalance(wallet.totalConsumed, locale)} divided />
            </dl>
          )}
      </section>

      {gate.kind === 'ready' && (
        <RechargeDialog
          key={rechargeSession}
          open={rechargeOpen}
          options={gate.options}
          teamName={teamName}
          onClose={() => setRechargeOpen(false)}
          onCompleted={() => void refresh()}
        />
      )}
    </div>
  )
}

function Stat({ label, value, divided = false }: { label: string; value: string; divided?: boolean }): React.JSX.Element {
  return (
    <div className={cn('px-5 py-4', divided && 'border-l border-hairline')}>
      <dt className="text-[11.5px] text-fg-faint">{label}</dt>
      <dd className="mt-1 text-[14px] text-fg tabular-nums">{value}</dd>
    </div>
  )
}

/** 不能充值时的那一行解释；只有「平台未部署」给网页退路（桌面端先于服务端发版时仍要能充上钱）。 */
function BlockedNotice({ reason, t }: { reason: RechargeGateReason; t: Translate }): React.JSX.Element {
  return (
    <div className="mx-5 mt-4 flex items-center gap-2 rounded-[8px] bg-tint px-3 py-2 text-[12px] text-fg-muted">
      <span className="min-w-0 flex-1">{t(`wallet.blocked.${reason}`)}</span>
      {reason === 'unsupported' && (
        <Button
          type="button"
          variant="secondary"
          onClick={() => {
            void openExternal(WEB_WALLET_URL).catch((error: unknown) => {
              console.warn('[wallet] 打开网页钱包失败:', error)
            })
          }}
        >
          {t('wallet.openWeb')}
        </Button>
      )}
    </div>
  )
}
