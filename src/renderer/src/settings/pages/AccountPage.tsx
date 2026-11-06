import { LogOut, RefreshCw, UserCircle } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import type { ClientAuthState, ClientUsageEntry } from '../../../../shared/domain/client-auth'
import { Button } from '../../components/arc/button/button'
import { EmptyState } from '../../components/arc/empty-state/empty-state'
import { getClientAuthState, getClientUsage, getClientUser, signOutClient, startClientLogin } from '../../services/client-auth'
import { ClientTeamSelectionView } from '../../views/ClientTeamSelectionView'
import { useI18n } from '../../i18n'
import type { SettingsPageProps } from '../props'

export function AccountPage(_props: SettingsPageProps): ReactNode {
  const { t } = useI18n()
  const [auth, setAuth] = useState<ClientAuthState | null>(null)
  const [usage, setUsage] = useState<ClientUsageEntry[]>([])
  const [busy, setBusy] = useState(false)
  /**
   * ★ 存的是**原因**,不是一个 boolean。以前是 `.catch(() => setError(true))`,
   * 主进程带回来的 `error.message`(比如「退出登录失败:secrets.removeAccessToken: …」)
   * 在这里被整个丢掉,界面只剩一句「操作失败,请重试」—— 用户和我们都无从判断该重试还是该修。
   */
  const [error, setError] = useState<string | null>(null)
  const fail = (err: unknown): void => {
    console.error('[account] 账户操作失败:', err)
    setError(err instanceof Error && err.message !== '' ? err.message : String(err))
  }
  const load = async (): Promise<void> => {
    const next = await getClientAuthState()
    setAuth(next)
    if (next.mode === 'authenticated') {
      const user = await getClientUser()
      const latest = await getClientAuthState()
      const resolved = { ...latest, user: user ?? latest.user }
      setAuth(resolved)
      if (resolved.contextRequired !== true) setUsage(await getClientUsage())
    }
  }
  useEffect(() => { void load() }, [])
  if (auth === null) return <div className="p-8 text-sm text-fg-muted">{t('common.loading')}</div>
  if (auth.mode === 'authenticated' && auth.contextRequired === true) return <ClientTeamSelectionView auth={auth} onComplete={setAuth} />
  if (auth.mode !== 'authenticated' || !auth.user) return <div className="flex min-h-0 flex-1 flex-col items-center justify-center gap-4"><EmptyState icon={<UserCircle size={28} />} title={t('auth.notSignedIn')} description={t('auth.signInFromWelcome')} /><Button type="button" variant="primary" disabled={busy} onClick={() => { setBusy(true); setError(null); void startClientLogin().then(setAuth).catch(fail).finally(() => setBusy(false)) }}>{busy ? t('auth.openingBrowser') : t('auth.login')}</Button>{error !== null && <p className="text-xs text-danger">{t('auth.loginFailedDetail', { message: error })}</p>}</div>
  return <div className="space-y-5">
    {error !== null && <p className="text-xs text-danger">{t('auth.actionFailedDetail', { message: error })}</p>}
    <div className="flex items-center justify-between border-b border-border pb-5">
      <div className="flex items-center gap-3"><div className="flex size-11 items-center justify-center rounded-full bg-tint text-accent">{auth.user.avatarUrl ? <img alt={t('auth.avatarAlt')} src={auth.user.avatarUrl} className="size-11 rounded-full" /> : <UserCircle size={27} />}</div><div><div className="text-[15px] font-medium">{auth.user.displayName || auth.user.username || auth.user.email}</div><div className="mt-0.5 text-xs text-fg-muted">{auth.user.email}</div></div></div>
      <Button size="sm" type="button" variant="secondary" disabled={busy} onClick={() => { setBusy(true); setError(null); void signOutClient().then(setAuth).catch(fail).finally(() => setBusy(false)) }}><LogOut size={14} />{t('auth.signOut')}</Button>
    </div>
    {auth.user.wallet && <div className="rounded-[10px] bg-tint px-4 py-3"><div className="text-xs text-fg-muted">{t('auth.walletBalance')}</div><div className="mt-1 text-xl font-medium text-fg">{auth.user.wallet.availableBalance.toFixed(2)} <span className="text-xs text-fg-muted">{auth.user.wallet.currency}</span></div><div className="mt-3 grid grid-cols-3 gap-3 border-t border-border/60 pt-3 text-xs"><div><div className="text-fg-faint">{t('auth.cashBalance')}</div><div className="mt-1 text-fg">{auth.user.wallet.cashBalance.toFixed(2)}</div></div><div><div className="text-fg-faint">{t('auth.giftBalance')}</div><div className="mt-1 text-fg">{auth.user.wallet.giftBalance.toFixed(2)}</div></div><div><div className="text-fg-faint">{t('auth.totalConsumed')}</div><div className="mt-1 text-fg">{auth.user.wallet.totalConsumed.toFixed(2)}</div></div></div></div>}
    <div className="flex items-center justify-between"><div><h3 className="text-[13px] font-medium">{t('auth.usageTitle')}</h3><p className="mt-1 text-xs text-fg-muted">{t('auth.usageHint')}</p></div><Button size="sm" type="button" variant="secondary" onClick={() => void load()}><RefreshCw size={14} />{t('common.refresh')}</Button></div>
    {usage.length === 0 ? <div className="py-12 text-center text-xs text-fg-faint">{t('auth.noUsage')}</div> : <div className="divide-y divide-border border-y border-border">{usage.map((item) => <div key={item.id} className="flex items-center justify-between py-3 text-xs"><div><div className="text-fg">{item.model || t('auth.unknownModel')}</div><div className="mt-1 text-fg-faint">{new Date(item.at).toLocaleString()}</div></div><div className="text-right"><div className="text-fg">{item.cost == null ? t('auth.noCost') : `${item.cost.toFixed(4)} ${item.currency || 'USD'}`}</div><div className="mt-1 text-fg-faint">{item.inputTokens + item.outputTokens} {t('auth.tokens')}</div></div></div>)}</div>}
  </div>
}
