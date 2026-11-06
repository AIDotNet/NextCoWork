/**
 * 奖励中心的三张记录表：奖励任务 / 邀请记录 / 奖励记录。
 *
 * 需求：横幅之外这一屏全是「我到底邀到了谁、钱到没到」，参考图里就是这三块。
 * 从 `RewardsOverlay.tsx` 拆出来是为了让浮层那个文件停在 400 行以内（§5），
 * 拆的边界是**表格**这一整类东西，不是按 prop 数量硬切。
 *
 * ★ **「奖励任务」是固定空态，这是已知的临时形态。** CoWork 至今没有任务这个
 * 概念（只有邀请事件与赠送批次），桌面端编不出任务列表，也不该替平台定协议。
 * 拆除条件：平台给出任务接口后，把数据加进 `shared/domain/referral.ts` 的
 * `ReferralCenter`，这里换成和另外两张表同样的 `rows` 渲染即可 —— 表头和空态
 * 文案都已经就位。**在那之前不要给它造一个假的本地任务源。**
 */
import { CheckSquare, Gift, Users } from 'lucide-react'
import type { ReactNode } from 'react'
import type { ReferralCenter } from '../../../../shared/domain/referral'
import { EmptyState } from '../../components/arc/empty-state/empty-state'
import { useI18n } from '../../i18n'
import { cn } from '../../lib/cn'
import { formatDay, formatMoment, formatMoney, inviteStatusKey, rewardSideKey } from './rewards-view'

export function RewardsTables({ center }: { center: ReferralCenter }): ReactNode {
  const { t, locale } = useI18n()
  return (
    <>
      <Section title={t('rewards.tasksTitle')}>
        <Row header columns={['2fr', '1fr', '1fr']}>
          <span>{t('rewards.colTask')}</span>
          <span>{t('rewards.colProgress')}</span>
          <span>{t('rewards.colReward')}</span>
        </Row>
        <EmptyState icon={<CheckSquare size={18} />} title={t('rewards.tasksEmpty')} description="" />
      </Section>

      <Section title={t('rewards.invitesTitle')}>
        <Row header columns={INVITE_COLUMNS}>
          <span>{t('rewards.colFriend')}</span>
          <span>{t('rewards.colInviteStatus')}</span>
          <span>{t('rewards.colFriendReward')}</span>
          <span>{t('rewards.colInviteTime')}</span>
        </Row>
        {center.invites.length === 0 ? (
          <EmptyState icon={<Users size={18} />} title={t('rewards.invitesEmpty')} description="" />
        ) : (
          center.invites.map((invite) => (
            /*
              key 用「好友名 + 绑定时刻」：接口不回传事件 id（那是运营侧的东西，
              见 CoWork `ReferralInviteItem` 的注释），而同一个人不可能在同一毫秒
              被绑定两次 —— 这比挂 index 稳：列表按时间倒序，新增一条会让所有
              index 往后挪一位，React 会把每一行的内容都当成「变了」。
            */
            <Row key={`${invite.name}-${invite.at}`} columns={INVITE_COLUMNS}>
              <span className="truncate text-fg">{invite.name}</span>
              <span className="text-fg-muted">{t(inviteStatusKey(invite.status))}</span>
              <span className="tabular-nums text-fg-muted">
                {/* 未发放时接口给 0 —— 画成「$0.00」像是奖励被吞了，用破折号 */}
                {invite.amount > 0 ? formatMoney(invite.amount, center.currency, locale) : '—'}
              </span>
              <span className="tabular-nums text-fg-faint">{formatMoment(invite.at, locale)}</span>
            </Row>
          ))
        )}
      </Section>

      <Section title={t('rewards.rewardsTitle')}>
        <Row header columns={REWARD_COLUMNS}>
          <span>{t('rewards.colRewardSource')}</span>
          <span>{t('rewards.colRewardAmount')}</span>
          <span>{t('rewards.colRewardRemaining')}</span>
          <span>{t('rewards.colRewardTime')}</span>
        </Row>
        {center.rewards.length === 0 ? (
          <EmptyState icon={<Gift size={18} />} title={t('rewards.rewardsEmpty')} description="" />
        ) : (
          center.rewards.map((reward) => (
            <Row key={`${reward.side}-${reward.at}`} columns={REWARD_COLUMNS}>
              <span className="flex min-w-0 items-center gap-2">
                <span className="truncate text-fg">{t(rewardSideKey(reward.side))}</span>
                {/* 撤销过的批次照样列出来（一笔钱悄悄消失最招投诉），用角标说明 */}
                {reward.revoked && (
                  <span className="shrink-0 rounded-pill bg-danger/10 px-1.5 py-0.5 text-[10.5px] text-danger">
                    {t('rewards.revoked')}
                  </span>
                )}
              </span>
              <span className="tabular-nums text-fg-muted">{formatMoney(reward.amount, center.currency, locale)}</span>
              <span className="tabular-nums text-fg-muted">
                {formatMoney(reward.remainingAmount, center.currency, locale)}
              </span>
              <span className="flex flex-col text-fg-faint">
                <span className="tabular-nums">{formatMoment(reward.at, locale)}</span>
                <span className="text-[10.5px]">
                  {reward.expireAt === null
                    ? t('rewards.neverExpires')
                    : t('rewards.expiresAt', { date: formatDay(reward.expireAt, locale) })}
                </span>
              </span>
            </Row>
          ))
        )}
      </Section>
    </>
  )
}

/** 四列的栅格。两张表列数相同、语义不同，所以各留一份常量而不是共用一个名字。 */
const INVITE_COLUMNS = ['2fr', '1fr', '1fr', '1.2fr']
const REWARD_COLUMNS = ['1.6fr', '1fr', '1fr', '1.4fr']

function Section({ title, children }: { title: string; children: ReactNode }): ReactNode {
  return (
    <section className="flex shrink-0 flex-col gap-2">
      <h3 className="text-[13px] text-fg">{title}</h3>
      <div className="overflow-hidden rounded-panel bg-surface">{children}</div>
    </section>
  )
}

/**
 * 表格行。用 CSS grid 而不是 `<table>`：这几张表没有跨行跨列、不需要列宽自适应，
 * 而 grid 能让表头和数据行**共用同一份列定义**（下面 `columns` 那个参数），
 * 于是不存在「改了表头忘了改数据行」的错位。
 */
function Row({
  children,
  columns,
  header = false
}: {
  children: ReactNode
  columns: readonly string[]
  header?: boolean
}): ReactNode {
  return (
    <div
      style={{ gridTemplateColumns: columns.join(' ') }}
      className={cn(
        'grid items-center gap-3 px-4 text-[12.5px]',
        header
          ? 'h-[38px] border-b border-hairline text-[11.5px] text-fg-faint'
          : 'h-[44px] border-b border-hairline last:border-b-0'
      )}
    >
      {children}
    </div>
  )
}
