/**
 * 奖励中心 —— 左下角账户菜单「邀请好友」点开的**全屏浮层**。
 *
 * 需求：邀请这件事以前只能跳浏览器（邀请码只有网页会话读得到），用户得离开应用
 * 才拿得到自己的链接。平台补了客户端可读的接口之后，这一整块搬进应用内：
 * 横幅 + 邀请链接/邀请码 + 累计统计 + 三张记录表，右上角一颗关闭。
 *
 * ★ **满屏而不是居中面板。** 设置浮层是 1058×720 的居中卡片（它是「改一个值就
 * 回去干活」的东西），奖励中心是「看一屏内容」的页面，三张表在 720 高的卡片里
 * 只能各露两行。所以这里 `inset-0` 铺满，并且底色是**不透明**的 `bg-canvas` ——
 * 不留遮罩下的半透背景，否则表格文字会和底下的聊天内容叠在一起。
 *
 * ★ **不 portal，根节点必须 `app-no-drag`。** 同 `SettingsOverlay.tsx` 文件头那条：
 * 浮层盖住顶部 34px 自绘标题栏，而那块是 `-webkit-app-region: drag`，OS 会吞掉
 * 该区域里所有 pointer 事件 —— 不加的话表现是「右上角关闭点不动，一按住整个窗口
 * 跟着鼠标跑」。z-100 也是同一套约定（theme.css 末尾的 z 轴：50 面板内，100 模态）。
 *
 * ★ **数据是「打开时拉一次」，没有订阅。** 邀请数据由别人的注册和消费改变，
 * 本地没有任何事件能预告它（见 `shared/ipc/contract.ts` 的 `referral:get`）。
 * 用户要看最新的就按标题右边那颗刷新。
 */
import { Gift, ImageDown, RotateCw, X } from 'lucide-react'
import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import type { ReferralCenter, ReferralState } from '../../../../shared/domain/referral'
/*
  ★ 海报上的标用的是**带米色圆角底的应用图标**（`resources/icon.png` 的 256px 版），
  不是 `assets/mark.png`。后者是透明底的深色符号 —— 画在海报的墨绿底上几乎看不见，
  而且它是 1254px/481KB，为了一个 96px 的落点背这么大一张也不合算。
*/
import brandIcon from '../../assets/brand-icon-256.png'
import bannerDark from '../../assets/rewards-banner-dark.jpg'
import bannerLight from '../../assets/rewards-banner-light.jpg'
import { Button } from '../../components/arc/button/button'
import { EmptyState } from '../../components/arc/empty-state/empty-state'
import { IconButton } from '../../components/ui/IconButton'
import { Spinner } from '../../components/ui/Spinner'
import { useFocusTrap } from '../../components/ui/useFocusTrap'
import { useI18n } from '../../i18n'
import { cn } from '../../lib/cn'
import { IS_MAC } from '../../lib/platform'
import { usePresence } from '../../lib/usePresence'
import { copyText, openExternal, saveImageFile } from '../../services/app'
import { getReferralCenter } from '../../services/referral'
import { toast } from '../../stores/toast'
import { useAppearance } from '../../theme/useAppearance'
import { RewardsTables } from './RewardsTables'
import { invitePosterFileName } from './invite-poster'
import { renderInvitePosterPng } from './poster-export'
import { bannerRewardLine, giftValidityLine, inviteShareText, pickShareNote, summaryStats, unavailableKey } from './rewards-view'

/** 和设置浮层同一档开合时长 —— 两个模态用不同的节奏会显得是两个产品。 */
const OVERLAY_MS = 280

export function RewardsOverlay({ open, onClose }: { open: boolean; onClose: () => void }): ReactNode {
  const { t, locale } = useI18n()
  const presence = usePresence(open, OVERLAY_MS)
  const panelRef = useRef<HTMLDivElement>(null)
  const appearance = useAppearance()
  const [state, setState] = useState<ReferralState | null>(null)
  const [loading, setLoading] = useState(false)
  const [savingPoster, setSavingPoster] = useState(false)

  const load = useCallback((): void => {
    setLoading(true)
    void getReferralCenter()
      .then(setState)
      // 服务不会 reject（失败已在主进程翻译成 unavailable），这一支只兜 IPC 本身挂掉
      .catch(() => setState({ kind: 'unavailable', reason: 'network' }))
      .finally(() => setLoading(false))
  }, [])

  /*
    ★ 每次**打开**都重拉，而不是只拉一次。浮层关掉后组件还挂着（要播退场动画），
    留着上次的快照的话，用户邀请成功后再打开看到的还是旧数字，且零报错。
  */
  useEffect(() => {
    if (open) load()
  }, [open, load])

  /*
    不给 `initial` 焦点目标：浮层根节点自己 `tabIndex={-1}`，焦点先落在它身上，
    第一次 Tab 才走到刷新/关闭。指定关闭按钮的话，打开的那一刻焦点环就套在
    「关闭」上，看着像默认动作是把刚打开的东西关掉。
  */
  useFocusTrap(panelRef, open && presence.mounted)

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent): void => {
      // 先让下层消费者说话（菜单关自己时会 preventDefault），同 SettingsOverlay
      if (e.key !== 'Escape' || e.defaultPrevented) return
      e.preventDefault()
      onClose()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [open, onClose])

  /*
    复制本身：写剪贴板 + 界面确认。
    ★ 成功仍然走 toast：复制进剪贴板的那段文本当中有半句是写给**收到链接的人**看的
    邀请文案（`inviteShareText`），拿它当界面确认是错位的 —— 它里面根本没有「已复制」。
  */
  const copy = (text: string): void => {
    void copyText(text)
      .then(() => toast.success(t('rewards.copied'), 'rewards-copy'))
      .catch(() => toast.error(t('rewards.copyFailed'), 'rewards-copy'))
  }

  /**
   * 生成这个用户自己的邀请海报并另存为 PNG。
   *
   * ★ 文案在**这里**取（组件里才有 `useI18n()`），再整份传给纯函数 —— 生成器
   * 自己不认识 i18n，见 `invite-poster.ts` 文件头。切成英文再点一次，出的就是英文海报。
   *
   * ★ 用户在系统对话框里按取消时 `saveImageFile` 返回 null：这不是失败，
   * **不弹任何提示**。给取消也弹一条「已保存」或「失败」都是在说谎。
   */
  const savePoster = (center: ReferralCenter): void => {
    setSavingPoster(true)
    const input = {
      code: center.code,
      inviteUrl: center.inviteUrl,
      copy: {
        tagline: t('rewards.poster.tagline'),
        eyebrow: t('rewards.poster.eyebrow'),
        titleLine1: t('rewards.poster.titleLine1'),
        titleLine2: t('rewards.poster.titleLine2'),
        subtitle: t('rewards.poster.subtitle'),
        bullets: [t('rewards.poster.bullet1'), t('rewards.poster.bullet2'), t('rewards.poster.bullet3')],
        scanTitle: t('rewards.poster.scanTitle'),
        scanHint: t('rewards.poster.scanHint'),
        codeLabel: t('rewards.poster.codeLabel')
      }
    }
    void renderInvitePosterPng(input, brandIcon)
      .then((base64) => saveImageFile(invitePosterFileName(center.code), base64))
      .then((saved) => {
        if (saved !== null) toast.success(t('rewards.posterSaved', { path: saved.path }), 'rewards-poster')
      })
      .catch((error: unknown) => {
        console.error('[rewards] 生成邀请海报失败', error)
        toast.error(t('rewards.posterFailed'), 'rewards-poster')
      })
      .finally(() => setSavingPoster(false))
  }

  if (!presence.mounted) return null

  const center = state?.kind === 'ready' ? state.center : null
  const banner = bannerRewardLine(
    center ?? { enabled: false, inviterAmount: 0, inviteeAmount: 0, currency: 'USD' },
    locale
  )

  return (
    <div
      ref={panelRef}
      tabIndex={-1}
      role="dialog"
      aria-modal="true"
      aria-label={t('rewards.title')}
      className={cn(
        'app-no-drag fixed inset-0 z-100 flex flex-col overflow-hidden bg-canvas outline-none',
        'transition-opacity duration-280 ease-panel motion-reduce:transition-none',
        presence.shown ? 'opacity-100' : 'pointer-events-none opacity-0'
      )}
    >
      {/*
        标题条压在顶部那条 34px 自绘标题栏上，所以两端都得给窗口按钮让位：
        macOS 的红绿灯在左（78px，和 `AppShell` 收起态那个数同源），
        Windows/Linux 自绘的三颗在右（`pr-window-controls`，它是悬浮层不占流）。
        让错一边的症状是「关闭按钮被窗口按钮压住，点下去是最小化」。
      */}
      <header
        className={cn(
          'flex h-[52px] shrink-0 items-center gap-2 px-4',
          IS_MAC ? 'pl-[86px]' : 'pr-window-controls'
        )}
      >
        <Gift size={15} className="shrink-0 text-accent-soft" />
        <h2 className="text-[14px] text-fg">{t('rewards.title')}</h2>
        <IconButton label={t('rewards.refresh')} size={26} disabled={loading} onClick={load}>
          {loading ? <Spinner size="xs" /> : <RotateCw size={13} />}
        </IconButton>
        <span className="flex-1" />
        <IconButton label={t('rewards.close')} onClick={onClose}>
          <X size={15} />
        </IconButton>
      </header>

      <div className="scroll-thin flex min-h-0 flex-1 flex-col gap-6 overflow-y-auto px-8 pt-2 pb-10">
        <section className="relative flex min-h-[220px] shrink-0 overflow-hidden rounded-panel">
          {/*
            横幅插画。深浅两张分别是在各自主题下量着画的（深色是墨绿渐变，
            浅色是暖白），跟着 `data-theme` 换 —— 只留一张的话，另一套主题下
            这块会是整屏唯一一处对比度不对的地方。

            `object-right` 是构图要求：插画的卡片在右、左半边是留白，文字压在留白上。
            窄窗口下裁的是右边的卡片，而不是把字盖住。
          */}
          <img
            src={appearance === 'light' ? bannerLight : bannerDark}
            alt={t('rewards.bannerArtAlt')}
            className="absolute inset-0 size-full object-cover object-right"
          />
          <div className="relative flex max-w-[60%] flex-col justify-center gap-3 px-8 py-8">
            <span className="text-[12px] text-white/70">{t('rewards.bannerEyebrow')}</span>
            {/*
              ★ 横幅上的文字固定用白色，**不用 `text-fg`**：它压的是插画而不是
              界面底色，浅色主题下跟着 token 走会变成浅灰字压浅色插画。
              插画左半边两套都是深的，所以白字在两套主题下都成立。
            */}
            <h3 className="text-[26px] leading-tight font-semibold text-white">{t('rewards.bannerTitle')}</h3>
            <p className="text-[12.5px] text-white/80">{t(banner.key, banner.params)}</p>
          </div>
        </section>

        {state === null ? (
          <EmptyState icon={<Spinner size="sm" />} title={t('rewards.loading')} description="" />
        ) : state.kind === 'unavailable' ? (
          <EmptyState
            icon={<Gift size={22} />}
            title={t(unavailableKey(state.reason))}
            description=""
            action={
              // 「没登录」重试没有意义（要去登录），另外两种给一颗重试
              state.reason === 'signed-out' ? undefined : (
                <Button variant="secondary" type="button" size="sm" onClick={load} disabled={loading}>{t('common.retry')}</Button>
              )
            }
          />
        ) : (
          <>
            <InviteCard
              center={state.center}
              onCopy={copy}
              onOpen={(url) => {
                void openExternal(url).catch((error: unknown) => {
                  toast.error(t('auth.actionFailedDetail', { message: error instanceof Error ? error.message : String(error) }))
                })
              }}
              savingPoster={savingPoster}
              onSavePoster={() => savePoster(state.center)}
            />
            <div className="grid grid-cols-4 gap-3">
              {summaryStats(state.center, locale, t).map((stat) => (
                <div key={stat.key} className="flex flex-col gap-1 rounded-card bg-surface px-4 py-3">
                  <span className="text-[11.5px] text-fg-faint">{t(stat.key)}</span>
                  <span className="text-[18px] tabular-nums text-fg">{stat.value}</span>
                </div>
              ))}
            </div>
            <RewardsTables center={state.center} />
          </>
        )}
      </div>
    </div>
  )
}

/**
 * 邀请链接 / 邀请码那张卡。
 *
 * ★ 链接与码**两个都给**，不是二选一：链接用于聊天里直接发出去，码用于口头
 * 转述或在别人已经打开的注册页里填。只给一个就总有一半场景要用户自己拆字符串。
 */
function InviteCard({
  center,
  onCopy,
  onOpen,
  onSavePoster,
  savingPoster
}: {
  center: ReferralCenter
  onCopy: (text: string) => void
  onOpen: (url: string) => void
  /** 生成并另存这个用户自己的邀请海报 */
  onSavePoster: () => void
  savingPoster: boolean
}): ReactNode {
  const { t } = useI18n()

  /*
    需求：复制出去的链接后面要跟一句邀请文案（池子见 `pickShareNote`），否则朋友
    收到的是一条光秃秃的 URL，看不出这是什么、也没说清对他有什么好处。
    ★ 抽签在**点击这一刻**发生，所以连点两次拿到的是两句不同的。
  */
  const copyLink = (): void => onCopy(inviteShareText(center.inviteUrl, pickShareNote(), t))

  return (
    <section className="flex flex-col gap-3 rounded-panel bg-surface px-5 py-4">
      <div className="flex flex-wrap items-center gap-3">
        <span className="shrink-0 text-[12px] text-fg-faint">{t('rewards.inviteLink')}</span>
        {/*
          链接是**领域值**（用户要原样复制出去），所以 `selectable` + 等宽，
          不参与任何翻译或省略号截断之外的加工。
        */}
        <span className="selectable min-w-0 flex-1 truncate font-mono text-[12.5px] text-fg">{center.inviteUrl}</span>
        <Button variant="secondary" type="button" size="sm" onClick={copyLink}>{t('rewards.copyLink')}</Button>
        <Button type="button" size="sm" variant="secondary" onClick={() => onOpen(center.inviteUrl)}>{t('rewards.openInBrowser')}</Button>
      </div>
      <div className="flex flex-wrap items-center gap-3">
        <span className="shrink-0 text-[12px] text-fg-faint">{t('rewards.inviteCode')}</span>
        <span className="selectable min-w-0 flex-1 truncate font-mono text-[12.5px] tracking-wider text-fg">{center.code}</span>
        {/*
          ★ 邀请码**不跟文案**，原样复制（和链接不同）。它是要被填进注册页输入框的
          值，多一个字就填不进去 —— 没人会先手动删干净再粘贴。
        */}
        <Button variant="secondary" type="button" size="sm" onClick={() => onCopy(center.code)}>{t('rewards.copyCode')}</Button>
        {/*
          ★ 海报是「这个用户自己那一张」：二维码编的是他的邀请链接，
          所以按钮只能出现在拿到了 `center` 的地方 —— 没有码就没有海报可出，
          这也是它不做成账户菜单一项的原因。
        */}
        <Button type="button" size="sm" variant="primary" disabled={savingPoster} onClick={onSavePoster}>
          <ImageDown size={14} />
          {savingPoster ? t('rewards.posterSaving') : t('rewards.savePoster')}
        </Button>
      </div>
      <p className="text-[11.5px] text-fg-faint">
        {t('rewards.qualifyHint', { calls: center.qualifyMinPaidCalls })} · {giftValidityLine(center, t)}
      </p>
    </section>
  )
}
