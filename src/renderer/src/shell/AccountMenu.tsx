/**
 * 侧边栏左下角的账户菜单 —— 从原来那张「信息 + 齿轮」合体卡片里拆出来的左半。
 *
 * 需求：原来整张卡片是一个可点区域，点哪儿都开设置，于是「看 / 管自己的账户」
 * 在界面上没有入口（设置 → 账户页里有，但没人知道要走那儿）。现在左边这颗点开
 * 菜单：头像 / 昵称 / 邮箱 + 一组账户动作；右边那颗齿轮仍然直接开设置 ——
 * 它和别处是**同一颗按钮**，所以留在 `Sidebar` 里由调用方持有，不进这个文件。
 *
 * ## 两件「这里做不到」的事，别照直觉补
 *
 * ★ **邀请这一项现在打开应用内的「奖励中心」浮层，不再跳浏览器 ——
 * 但原因那条约束仍然成立，别在这个文件里编码或金额。**
 * 原先这里写的是：邀请链接的后端形态是 `<origin>/login?ref=<12 位小写 hex>`
 * （CoWork `PortalEndpoints.cs`：`$"/login?ref={code}"`，码由 `app_user.referral_code`
 * 生成），而码只从 `GET /api/portal/referral` 返回 —— 那条路由要**网页会话
 * cookie**，桌面端令牌带着 `client_id`，会被 CoWork `AuthenticationRegistration.cs`
 * 里那张路径白名单直接判失败；于是只能 `openExternal` 跳浏览器。
 * 那段话里说的另一条出路（「要么平台新增一条客户端可读的邀请接口」）现在实现了：
 * `GET /api/client/referral`（同一张白名单里复用 `profile:read`），码、链接、金额
 * 和记录都由它返回，界面走 `views/rewards/RewardsOverlay.tsx`。
 * **仍然不要**在这里拼链接、写死码或金额 —— 它们只有服务端知道。
 *
 * ★ **余额不在这里请求，读 `auth.user.wallet`。** 主进程 `getClientUser()` 写回
 * KV 之后会广播 `clientAuth:changed`（`App.tsx:93` 订阅它），这条 props 链就是
 * 唯一真源；菜单里再存一份余额 state 的话，迟早复现「设置页刷新出来的数字和
 * 菜单里的对不上」。那颗 ⟳ 只负责**发起**刷新，结果由广播回来。
 */
import { ChevronRight, Gem, Gift, LifeBuoy, LogOut, RotateCw } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import type { ClientAuthState } from '../../../shared/domain/client-auth'
import { Mark } from '../components/brand/Mark'
import { IconButton } from '../components/ui/IconButton'
import { Menu, MenuItem, MenuSeparator } from '../components/ui/Menu'
import { Spinner } from '../components/ui/Spinner'
import { useI18n } from '../i18n'
import { updateErrorKey } from '../lib/update-error'
import type { SettingsPageId } from '../settings/nav'
import { openExternal, updateCheck } from '../services/app'
import { getClientUser, signOutClient } from '../services/client-auth'
import { toast } from '../stores/toast'
import { useWindowStore } from '../stores/window'

/**
 * 帮助与反馈的落点：官网文档站。
 *
 * ★ 这是**与平台对齐后的选择，不是从代码里推出来的** —— CoWork 的公开站有
 * /docs、/privacy、/pricing，没有反馈页；NextCoWork 这边此前也没有任何帮助入口。
 * 平台以后给出反馈表单 / 邮箱，改这里一行即可。
 */
const HELP_URL = 'https://nextco.work/docs'

export function AccountMenu({
  auth,
  onOpenSettings
}: {
  auth: ClientAuthState
  /** 带页码：菜单里「余额」那一行要直接落到设置的钱包页，而不是上一次停的那页 */
  onOpenSettings: (page?: SettingsPageId) => void
}): ReactNode {
  const { t, locale } = useI18n()
  const [refreshing, setRefreshing] = useState(false)
  /*
    奖励中心的开关住在 window store（和设置浮层同一处），这里只取那个 action ——
    取整个 store 会让账户菜单跟着任何一次 Tab 变化重渲。
  */
  const openRewards = useWindowStore((s) => s.openRewards)

  const user = auth.mode === 'authenticated' ? auth.user : null
  /*
    昵称 / 邮箱 / 头像 / 余额全部从这一份快照读。四个字段各自判空一次，是因为它们
    在服务端都可能缺（`/api/client/account` 里 wallet 可以为 null），而缺哪个就
    少画哪个 —— 不给「未知用户」这类占位。
  */
  const name = user === null
    ? t('sidebar.localMode')
    : user.displayName || user.username || user.email || t('sidebar.signedIn')
  const hint = user === null ? t('sidebar.localModeHint') : t('sidebar.signedInHint')
  const email = user === null ? null : user.email ?? null
  const avatarUrl = user === null ? null : user.avatarUrl ?? null
  const wallet = user === null ? null : user.wallet ?? null
  /*
    千分位 + 两位小数，和参考图里那一行一致。**不用 `toFixed(2)`** —— 它不分组，
    五位数的余额会变成一长串数字，这是余额行最容易被读错的地方。

    单位**必须跟着数字一起画**：钱包是美元计价的（平台侧 `PLATFORM_CURRENCY = "USD"`，
    接口里就是 `wallet.currency`），只写「5,699.04」会被读成本地货币。取值用
    `wallet.currency` 而不是在这里写死 `USD` —— 那是**协议给的字段**，平台换币种时
    这里不该跟着改代码（同 §6.5：币种是领域值，不进 i18n）。
  */
  const balance = wallet === null
    ? null
    : new Intl.NumberFormat(locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 })
        .format(wallet.availableBalance)

  const refreshBalance = (): void => {
    setRefreshing(true)
    void getClientUser()
      .catch((error: unknown) => {
        toast.error(t('accountMenu.balanceRefreshFailed', { message: describeError(error) }))
      })
      .finally(() => setRefreshing(false))
  }

  const checkUpdates = (): void => {
    void updateCheck()
      .then((result) => {
        if (result.state === 'available') {
          toast.info(t('accountMenu.updateAvailable', { version: result.update.version }))
          return
        }
        if (result.state === 'error') {
          toast.error(t(updateErrorKey(result.code)))
          return
        }
        /*
          未打包的构建里 `check()` 在 `configure()` 之后直接返回 `disabled`
          （main/update/update-service.ts:105-108）。这里**不能**落到「已是最新」
          那一支：开发模式下说「当前是最新版」是假话，用户会以为是网络或账号问题。
        */
        toast.info(t(result.state === 'disabled' ? 'about.updates.devDisabled' : 'accountMenu.updateLatest'))
      })
      .catch(() => toast.error(t('accountMenu.updateFailed')))
  }

  const signOut = (): void => {
    void signOutClient().catch((error: unknown) => {
      toast.error(t('accountMenu.signOutFailed', { message: describeError(error) }))
    })
  }

  const openLink = (url: string): void => {
    void openExternal(url).catch((error: unknown) => {
      toast.error(t('auth.actionFailedDetail', { message: describeError(error) }))
    })
  }

  return (
    <Menu
      label={t('accountMenu.label')}
      width={268}
      className="min-w-0 flex-1"
      triggerClassName="flex w-full items-center gap-2 rounded-card px-2 py-1.5 text-left transition-colors hover:bg-tint-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/50 motion-reduce:transition-none"
      trigger={
        <>
          <Avatar url={avatarUrl} size={28} />
          <span className="min-w-0 flex-1 leading-tight">
            <span className="block truncate text-[12.5px] text-fg">{name}</span>
            <span className="block truncate text-[11px] text-fg-faint">{hint}</span>
          </span>
        </>
      }
    >
      {(close) => (
        <>
          {/* 面板头部：同一份身份信息的展开态，比触发按钮多一行邮箱 */}
          <div className="flex items-center gap-2.5 px-2.5 pt-2 pb-3">
            <Avatar url={avatarUrl} size={36} />
            <span className="min-w-0 flex-1 leading-tight">
              <span className="block truncate text-[13px] text-fg">{name}</span>
              {email !== null && (
                <span className="block truncate text-[11px] text-fg-faint">{email}</span>
              )}
            </span>
          </div>

          {balance !== null && wallet !== null && (
            <BalanceRow
              balance={balance}
              currency={wallet.currency}
              refreshing={refreshing}
              onOpen={() => {
                onOpenSettings('wallet')
                close()
              }}
              onRefresh={refreshBalance}
            />
          )}

          {user !== null && (
            <MenuItem
              icon={<Gift size={14} />}
              description={t('accountMenu.inviteHint')}
              onSelect={() => {
                /*
                  ★ 先关菜单再开浮层。反过来的话，浮层的 focus trap 会在菜单
                  卸载的同一帧抢焦点，菜单的「关掉自己时把焦点还给触发按钮」
                  紧接着又抢回去 —— 表现是浮层开着但键盘焦点留在侧边栏，
                  Esc 关掉的是菜单而不是浮层。
                */
                close()
                openRewards()
              }}
            >
              {t('accountMenu.invite')}
            </MenuItem>
          )}

          <MenuSeparator />

          {/* 「检查更新」「帮助反馈」两态都画：它们不依赖登录状态 */}
          <MenuItem
            icon={<RotateCw size={14} />}
            onSelect={() => {
              close()
              checkUpdates()
            }}
          >
            {t('accountMenu.checkUpdates')}
          </MenuItem>
          <MenuItem
            icon={<LifeBuoy size={14} />}
            onSelect={() => {
              openLink(HELP_URL)
              close()
            }}
          >
            {t('accountMenu.help')}
          </MenuItem>

          {user !== null && (
            <>
              <MenuSeparator />
              <MenuItem
                icon={<LogOut size={14} />}
                danger
                onSelect={() => {
                  close()
                  signOut()
                }}
              >
                {t('auth.signOut')}
              </MenuItem>
            </>
          )}
        </>
      )}
    </Menu>
  )
}

/**
 * 余额行：左边「点了就开钱包页」，右边那颗 ⟳ 是**第二个独立的可点区域**。
 *
 * ★ 不能写成 `MenuItem`：它整行就是一个 `<button>`，装不下第二颗按钮（嵌套
 * `button` 是无效 HTML，两个 `onClick` 还会互相抢事件）。所以照 `OuterTabBar.tsx`
 * 工作区行那个先例 —— 外层 `<div>` 只当布局容器，两个动作各自是按钮。
 */
function BalanceRow({
  balance,
  currency,
  refreshing,
  onOpen,
  onRefresh
}: {
  /** 已经格式化好的数字（千分位 + 两位小数） */
  balance: string
  /** 钱包币种，来自接口（今天是 `USD`）。**不翻译、不写死** */
  currency: string
  refreshing: boolean
  onOpen: () => void
  onRefresh: () => void
}): ReactNode {
  const { t } = useI18n()
  return (
    <div className="flex items-center gap-1 rounded-[8px] transition-colors hover:bg-tint-strong motion-reduce:transition-none">
      <button
        type="button"
        role="menuitem"
        onClick={onOpen}
        className="flex min-w-0 flex-1 items-center gap-2.5 rounded-[8px] px-2.5 py-[7px] text-left text-[13px] text-fg"
      >
        <span className="shrink-0 text-accent-soft">
          <Gem size={14} />
        </span>
        <span className="min-w-0 flex-1 truncate">{t('accountMenu.balance')}</span>
        {/*
          单位跟数字绑成一组（`items-baseline`），不参与前面那排 gap-2.5 ——
          分开的话「5,699.04」和「USD」之间会被拉开 10px，读起来像两个字段。
        */}
        <span className="flex shrink-0 items-baseline gap-1">
          <span className="tabular-nums text-[12.5px] text-fg-muted">{balance}</span>
          <span className="text-[10.5px] text-fg-faint">{currency}</span>
        </span>
        <ChevronRight size={14} className="shrink-0 text-fg-faint" />
      </button>
      <IconButton
        label={t('accountMenu.balanceRefresh')}
        size={26}
        disabled={refreshing}
        className="mr-1 shrink-0"
        onClick={onRefresh}
      >
        {refreshing ? <Spinner size="xs" /> : <RotateCw size={13} />}
      </IconButton>
    </div>
  )
}

/**
 * 圆形头像。
 *
 * ★ **没有 `avatarUrl` 时回落到品牌标识，不是通用人形。** 侧边栏这一小块从
 * 一开始画的就是 NextCoWork 标识（离线态尤其如此），换成 `UserCircle` 会让
 * 「本地模式」看起来像另一个产品。
 */
function Avatar({ url, size }: { url: string | null; size: number }): ReactNode {
  const { t } = useI18n()
  return (
    <span
      className="flex shrink-0 items-center justify-center overflow-hidden rounded-full bg-tint-strong text-fg"
      style={{ width: size, height: size }}
    >
      {url === null
        ? <Mark size={Math.round(size / 2)} />
        : <img alt={t('auth.avatarAlt')} src={url} className="size-full object-cover" />}
    </span>
  )
}

/**
 * 主进程的错误文案原样带上来。
 *
 * 同 `AccountPage.tsx:22` 那段：主进程的 `signOutClient` 会把失败原因拼进
 * message（「退出登录失败：secrets.removeAccessToken: …」），渲染层只显示
 * 「操作失败」的话，用户和我们都无从判断该重试还是该修。
 */
function describeError(error: unknown): string {
  return error instanceof Error && error.message !== '' ? error.message : String(error)
}
