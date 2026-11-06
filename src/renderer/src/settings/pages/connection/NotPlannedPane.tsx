/**
 * 连接器 / 插件 / 机器人对话 —— 参考图里有这三个子 Tab,方案里**没有**对应子系统。
 *
 * ★ **直说没有,不写「即将推出」。** 同 `StubPage.tsx` 的规矩,只是这里连一个
 * 步骤号都编不出来:那三块不在 §1–§14 的任何一步里。给个假进度条或者摆几条
 * 占位数据,下一个接手的人要读完整份方案才能确认那是假的 —— 而这一句话
 * 一秒钟就能核对。
 */
import { Blocks, Bot, Plug } from 'lucide-react'
import type { ReactNode } from 'react'
import { EmptyState } from '../../../components/arc/empty-state/empty-state'
import { useI18n } from '../../../i18n'

const ICON = { connector: <Plug size={26} />, plugin: <Blocks size={26} />, bot: <Bot size={26} /> } as const

export function NotPlannedPane({ sub }: { sub: string }): ReactNode {
  const { t } = useI18n()
  if (!(sub in ICON)) return null
  const key = sub as keyof typeof ICON
  return (
    <div className="flex min-h-0 flex-1 items-center justify-center">
      <EmptyState
        icon={ICON[key]}
        title={t(`connection.notPlanned.${key}.title` as 'connection.notPlanned.connector.title' | 'connection.notPlanned.plugin.title' | 'connection.notPlanned.bot.title')}
        description={t(`connection.notPlanned.${key}.hint` as 'connection.notPlanned.connector.hint' | 'connection.notPlanned.plugin.hint' | 'connection.notPlanned.bot.hint')}
      />
    </div>
  )
}
