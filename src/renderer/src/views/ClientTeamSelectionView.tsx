import { ArrowRight, Building2, UserRound } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import type { ClientAuthState, ClientTeamOption } from '../../../shared/domain/client-auth'
import { Button } from '../components/arc/button/button'
import { Mark } from '../components/brand/Mark'
import { useI18n } from '../i18n'
import { selectClientTeam } from '../services/client-auth'
import { Spinner } from '../components/ui/Spinner'

export function ClientTeamSelectionView({ auth, onComplete }: { auth: ClientAuthState; onComplete: (next: ClientAuthState) => void }): ReactNode {
  const { t } = useI18n()
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const teams = auth.teams ?? []

  const choose = async (team: ClientTeamOption): Promise<void> => {
    if (busy !== null) return
    setBusy(team.id)
    setError(null)
    try {
      onComplete(await selectClientTeam(team.id))
    } catch {
      setError(t('auth.selectTeamFailed'))
    } finally {
      setBusy(null)
    }
  }

  return (
    <main className="relative flex h-full items-center justify-center overflow-hidden bg-[#1c1c1e] text-white">
      <div className="app-drag absolute inset-x-0 top-0 h-10" aria-hidden="true" />
      <div className="relative flex w-[min(520px,calc(100%-32px))] flex-col px-6 py-10">
        <div className="mb-8 flex items-center gap-3 text-[24px] font-semibold tracking-[-0.04em]">
          <Mark size={30} className="text-white" />
          <span>NextCoWork</span>
        </div>
        <h1 className="text-[24px] font-medium tracking-[-0.03em]">{t('auth.selectTeamTitle')}</h1>
        <p className="mt-2 text-[13px] leading-6 text-white/50">{t('auth.selectTeamHint')}</p>
        <div className="mt-7 grid gap-2">
          {teams.map((team) => (
            <Button
              key={team.id}
              type="button"
              variant="secondary"
              disabled={busy !== null}
              onClick={() => void choose(team)}
              className="group flex h-auto w-full items-center justify-start gap-3 rounded-[12px] border border-white/10 bg-white/[.045] px-4 py-3 text-left text-white hover:border-accent/50 hover:bg-white/[.09]"
            >
              <span className="flex size-9 shrink-0 items-center justify-center rounded-[8px] bg-white/[.08] text-accent">
                {team.type === 'Personal' ? <UserRound size={17} /> : <Building2 size={17} />}
              </span>
              <span className="min-w-0 flex-1">
                <span className="block truncate text-[13px] font-medium text-white">{team.name}</span>
                <span className="mt-1 block text-[11px] text-white/40">{t('auth.selectTeamRole', { role: team.role })} · {t('auth.selectTeamMembers', { count: team.memberCount })}</span>
              </span>
              {busy === team.id ? <Spinner size="sm" className="text-white/50" /> : <ArrowRight size={15} className="text-white/25 transition group-hover:translate-x-0.5 group-hover:text-accent" />}
            </Button>
          ))}
        </div>
        {teams.length === 0 && <p className="mt-7 rounded-[10px] border border-danger/30 bg-danger/10 px-3 py-3 text-[12px] text-danger">{t('auth.noAvailableTeam')}</p>}
        {error !== null && <p role="alert" className="mt-4 text-[12px] text-danger">{error}</p>}
      </div>
    </main>
  )
}
