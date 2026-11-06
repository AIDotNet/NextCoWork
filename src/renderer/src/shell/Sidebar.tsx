/**
 * 侧边栏 —— 方案 §8 的核心结构决策落点。
 *
 * **上下两半作用域不同,不能混在一个 store 里:**
 *
 *   上半(功能入口)  作用域 = 全局      新建对话 / 搜索 / 定时任务 / 浏览器 / Skill 管理
 *   下半(会话区)    作用域 = 当前工作区  最近对话 / 归档
 *
 * 这正是你要的那条行为:**切换顶部的工作空间会影响左侧会话列表**。
 * 下半的数据全部由 `activeWorkspaceId` 派生,所以「切了顶部 Tab 但左边没跟着变」
 * 这个 bug 在结构上就写不出来。
 *
 * 上半的导航项与功能外层 Tab 是**同一个东西的两个位置**(截图 4aa68110:
 * 「定时任务」的 Tab 打开时,侧边栏那一项是高亮的),所以 `activeFeature` 从
 * 外层 Tab 表反查,不另存一份。
 *
 * **297px 是量出来的,而且是定宽 —— 别改成可拖的。** 29 张截图里面板体一律占
 * x=8..304,两种窗口宽度(1146 / 1264)下同一个数,所以参考实现这条边不跟窗口走、
 * 也没有分隔条。中途一度以为它在 235~312 之间浮动,那是把设置浮层的左侧导航栏
 * 当成侧边栏量了 —— 浮层盖住了扫描线,量到的是它内部的分栏。
 */
import { Archive, Check, Copy, ExternalLink, Link, MessageSquarePlus, Pin, Search, Settings, SquarePen, Trash2, Pencil, ListChecks, Sparkles } from 'lucide-react'
import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { type FeatureKind, type InnerTab } from '../../../shared/domain/tab'
import type { Workspace } from '../../../shared/domain/workspace'
import type { SessionListItem } from '../../../shared/domain/session'
import type { ClientAuthState } from '../../../shared/domain/client-auth'
// 只借类型:`settings/nav.ts` 是纯数据(零 React 依赖),这里不会把设置页拽进侧边栏的 bundle
import type { SettingsPageId } from '../settings/nav'
import { Mark } from '../components/brand/Mark'
import { EmptyState } from '../components/arc/empty-state/empty-state'
import { IconButton } from '../components/ui/IconButton'
import { cn } from '../lib/cn'
import { IS_MAC } from '../lib/platform'
import { ChevronRight, PanelLeft } from 'lucide-react'
import { FEATURE_ICON, MENU_ICON } from './icons'
import { useI18n, type Translate } from '../i18n'
import { ContextMenu, type ContextMenuPosition } from '../components/ui/ContextMenu'
import { AccountMenu } from './AccountMenu'
import { DeviceSwitcher } from './DeviceSwitcher'
import { Dialog } from '../components/ui/Dialog'
import { Button } from '../components/arc/button/button'
import { duplicateSession, renameSession, setArchived, setFavorited } from '../services/sessions'
import { copyText, openSessionWindow } from '../services/app'
import { Spinner } from '../components/ui/Spinner'
import { useAttentionSessionIds } from '../stores/session'
import { toast } from '../stores/toast'
import { usePluginsStore } from '../stores/plugins'
import { openPluginWebApp } from '../services/plugins'
import { pluginSidebarEntries } from './plugin-sidebar-entries'
import { useWindowStore } from '../stores/window'
import { skillExtractionErrorKey, startSkillExtraction } from '../views/skills/skill-extraction'
import { requestSessionDelete, useRunningSessionDelete, type DeleteSessionFn } from './session-delete'

const NAV_FEATURES: readonly FeatureKind[] = ['scheduled', 'browser', 'git', 'extensions']

export function Sidebar({
  workspace,
  focusMode = false,
  chatTabs,
  sessions,
  activeFeature,
  scheduledUnread = false,
  activeSessionId,
  runningSessionIds,
  onNewChat,
  onSearch,
  onOpenFeature,
  onOpenSettings,
  onSelectSession,
  onDeleteSession,
  onCollapse,
  auth
}: {
  /** 当前工作区。null = 一个都没打开(下半整体降级为空态) */
  workspace: Workspace | null
  /** quick 窗口只保留当前项目的会话入口，隐藏全局导航与账户入口。 */
  focusMode?: boolean
  /** 当前工作区里已打开的对话 Tab —— 步骤 6 接上 SQLite 后换成真正的历史会话表 */
  chatTabs: readonly InnerTab[]
  /** 数据库中的全部会话；未打开的历史会话也应出现在侧边栏。 */
  sessions: readonly SessionListItem[]
  activeFeature: FeatureKind | null
  scheduledUnread?: boolean
  activeSessionId: string | null
  runningSessionIds: ReadonlySet<string>
  onNewChat: () => void
  onSearch: () => void
  onOpenFeature: (f: FeatureKind) => void
  /** 打开设置浮层。**页码是可选的** —— 账户菜单的「余额」要直接落到钱包页 */
  onOpenSettings: (page?: SettingsPageId) => void
  onSelectSession: (sessionId: string) => void
  onDeleteSession: DeleteSessionFn
  onCollapse: () => void
  auth: ClientAuthState
}): ReactNode {
  const { t } = useI18n()
  /*
    需求:右键「提炼为 Skill」—— 另开一条提炼会话,源会话就是被右键的那一条。
    工作区从这一层取(它是侧边栏唯一持有完整 `Workspace` 的地方),往下只传这个回调。
  */
  const extractSkill = (session: SessionListItem): void => {
    if (workspace === null) return
    void startSkillExtraction({ workspace, sourceSessionId: session.id, sourceTitle: session.title, hint: '', t })
      .catch((error: unknown) => toast.error(t(skillExtractionErrorKey(error)), 'skill-extraction'))
  }
  /*
    插件贡献的网页应用入口。
    ★ 订阅走 selector(`catalog`),不是整份 store:整份订阅会让插件活动日志
    刷新也把整条侧边栏重渲一遍(AGENTS.md §9)。派生逻辑在
    `plugin-sidebar-entries.ts`,useMemo 按 catalog 引用缓存。
  */
  const catalog = usePluginsStore((state) => state.catalog)
  const pluginEntries = useMemo(() => pluginSidebarEntries(catalog), [catalog])
  // 版本号走 store 而不是 prop:它是窗口外壳级的常量,透传只会给 Sidebar 和
  // 主题预览各加一层。订阅取字段不取整份 store(§9)。
  const appVersion = useWindowStore((state) => state.appVersion)
  return (
    <aside className={cn('flex shrink-0 flex-col overflow-hidden rounded-panel bg-surface', focusMode ? 'w-[240px]' : 'w-[297px]')}>
      {/*
        macOS hiddenInset 把红绿灯放在窗口左上角,而侧边栏面板正好在那里 ——
        `pl-[74px]` 是给它们让出来的位置,不是随手写的边距。**所以它只给 macOS**:
        Windows/Linux 的三颗按钮在右上角(见 main/window/title-bar.ts),
        这里再留 74px 就是个空洞。收起按钮本来就右对齐,换成常规内边距即可。
        整条 app-drag:这一行没有别的可点内容,让它可以拖窗口。
        高度必须和 AppShell 的外层 Tab 条一致(34px,量自参考图),否则红绿灯和 Tab 底边错位。
      */}
      <div
        className={cn(
          'app-drag flex h-[34px] shrink-0 items-center justify-end pr-1',
          IS_MAC ? 'pl-[74px]' : 'pl-2'
        )}
      >
        {/*
          和 AppShell 收起态那颗是**同一个控件的两个状态**,所以尺寸必须一样,
          否则一收一放图标会跳一下大小。量 image copy 2.png(展开态):
          笔画 x274 / x287 → 字形 14px 宽(= lucide size 16),字形中心 x280.5,
          颜色 #7e7f7e = `icon`,**没有底色**。
          38 宽的盒子右边贴到 x300(pr-1)时字形中心正好落在 281 —— 对上了。
          `active` 留给收起态:参考里只有收起的那颗是挖暗 + 强调色。
        */}
        <IconButton
          label={t('nav.collapseSidebar')}
          size={28}
          width={38}
          onClick={onCollapse}
          className="rounded-pill"
        >
          <PanelLeft size={16} />
        </IconButton>
      </div>

      {!focusMode && (
        <>
          {/*
            版本号靠到品牌行**右端**、和字标同一行 —— 它要回答的是「我现在装的是哪一版」,
            报 bug 时不用先翻设置。单独占一行会在 logo 下面多出一条 22px 的空行
            (导航、卡片、账户那一整列的纵向节奏全被顶下去),而它本来就只有五个字符宽。
          */}
          <div className="flex items-center gap-2 px-4 pt-1 pb-4 text-fg">
            <Mark />
            <span className="font-brand text-[15px] font-bold tracking-tight">NextCoWork</span>
            {appVersion !== '' && (
              <span className="ml-auto font-mono text-[11px] text-fg-faint">
                <span className="sr-only">{t('sidebar.version')}</span>v{appVersion}
              </span>
            )}
          </div>
        </>
      )}

      {/* 专注窗口只保留当前项目的会话动作；主窗口继续展示完整全局导航。 */}
      <nav className={cn('flex gap-1.5 px-2.5', focusMode ? 'items-center pt-2' : 'flex-col')}>
        <NavItem icon={<SquarePen size={16} />} label={t('nav.newChat')} onClick={onNewChat} />
        {focusMode ? (
          <IconButton label={t('nav.search')} size={32} onClick={onSearch} className="shrink-0">
            <Search size={16} />
          </IconButton>
        ) : <NavItem icon={<Search size={16} />} label={t('nav.search')} onClick={onSearch} />}
        {!focusMode && NAV_FEATURES.map((f) => {
          const Icon = FEATURE_ICON[f]
          return (
            <NavItem
              key={f}
              icon={<Icon size={16} />}
              label={t(`feature.${f}` as Parameters<typeof t>[0])}
              active={activeFeature === f}
              badge={
                f === 'scheduled' && scheduledUnread ? (
                  <span className="size-1.5 rounded-full bg-accent" aria-label={t('scheduled.unread')} />
                ) : undefined
              }
              onClick={() => onOpenFeature(f)}
            />
          )
        })}
        {!focusMode && pluginEntries.map((entry) => {
          const Icon = MENU_ICON[entry.icon]
          return (
            <NavItem
              key={entry.id}
              icon={<Icon size={16} />}
              label={t(entry.titleKey as Parameters<typeof t>[0])}
              onClick={() => {
                void openPluginWebApp(entry.pluginId, entry.webAppId).catch(() => {
                  toast.error(t('pluginWebApp.openFailed', { plugin: entry.pluginId }), `plugin-webapp-${entry.id}`)
                })
              }}
            />
          )
        })}
      </nav>

      {/* ── 下半:当前工作区 ── */}
      <div className="scroll-thin mt-4 flex min-h-0 flex-1 flex-col gap-2 overflow-y-auto px-2.5 pb-2">
        {workspace === null ? (
          <EmptyState
            icon={<MessageSquarePlus size={22} />}
            title={t('workspace.none')}
            description={t('workspace.noneHint')}
          />
        ) : (
          <>
            <Section
              title={t('workspace.recentChats')}
              defaultOpen
              revealSessionId={focusMode && sessions.some((session) => !session.archived && session.id === activeSessionId) ? activeSessionId : null}
              action={focusMode ? undefined : (
                <IconButton label={t('nav.newChat')} size={22} onClick={onNewChat}>
                  <SquarePen size={13} />
                </IconButton>
              )}
            >
              {sessions.filter((s) => !s.archived).length === 0 &&
              chatTabs.every((tab) => tab.kind !== 'chat' || sessions.some((s) => s.id === tab.ref.sessionId && s.archived)) ? (
                <EmptyState title={t('workspace.noChats')} description="" className="py-6" />
              ) : (
                <ul className="flex flex-col gap-0.5 pb-1">
                  <SessionGroupList
                    workspaceId={workspace.id}
                    focusMode={focusMode}
                    sessions={sessions}
                    chatTabs={chatTabs}
                    activeSessionId={activeSessionId}
                    runningSessionIds={runningSessionIds}
                    onSelectSession={onSelectSession}
                    onDeleteSession={onDeleteSession}
                    onExtractSkill={extractSkill}
                    t={t}
                  />
                </ul>
              )}
            </Section>

            <Section title={t('workspace.archived')} defaultOpen={false}
              revealSessionId={focusMode && sessions.some((session) => session.archived && session.id === activeSessionId) ? activeSessionId : null}>
              {sessions.filter((s) => s.archived).length === 0 ? (
                <EmptyState title={t('workspace.noArchived')} description="" className="py-6" />
              ) : (
                <ul className="flex flex-col gap-0.5 pb-1">
                  {sessions.filter((s) => s.archived).map((s) => (
                    <ArchivedSessionItem
                      key={s.id}
                      session={s}
                      active={focusMode && s.id === activeSessionId}
                      workspaceId={workspace.id}
                      runningSessionIds={runningSessionIds}
                      onSelectSession={onSelectSession}
                      onDeleteSession={onDeleteSession}
                      onExtractSkill={extractSkill}
                      t={t}
                    />
                  ))}
                </ul>
              )}
            </Section>
          </>
        )}
      </div>

      {/*
        参考实现这里是账户 / 游客模式。NextCoWork 没有账户体系(方案 §10 砍掉了
        钱包 / 云同步 / 每日回顾那一整块商业化面),所以这个位置换成「账户菜单 +
        设置入口」—— 形状留着,含义换掉。

        ★ **两块可点区域必须分开。** 原先整张卡片只有一个 onClick(点哪儿都开设置,
        齿轮只是那个「先问一下 target 是不是 button」的例外),于是头像 / 昵称 /
        邮箱、余额、邀请这些只能摊在设置页里。现在左半是 `AccountMenu` 的菜单
        触发器、右半是齿轮,两块各自有焦点与 hover —— 原来那段规避重复触发的
        `closest('button')` 判断随之取消,因为它防的就是这个形状。
      */}
      {!focusMode && (
        <div className="mx-1.5 mb-1.5 flex shrink-0 items-center gap-1">
          <AccountMenu auth={auth} onOpenSettings={onOpenSettings} />
          <DeviceSwitcher onOpenSettings={onOpenSettings} />
          <IconButton label={t('common.settings')} onClick={() => onOpenSettings()}>
            <Settings size={15} />
          </IconButton>
        </div>
      )}
    </aside>
  )
}

/**
 * 上半那排入口,现在每一项各占一行、撑满宽度,图标 + 文字常驻可见。
 *
 * ★ **折叠态必须有 `aria-label`**:侧边栏折叠时视觉上只剩图标,可访问名不能跟着一起消失。
 */
function NavItem({
  label,
  icon,
  active = false,
  badge,
  onClick
}: {
  label: string
  icon: ReactNode
  active?: boolean
  /** 右侧的状态点(今天只有「定时任务」的未读圆点)。 */
  badge?: ReactNode
  onClick: () => void
}): ReactNode {
  return (
    <button
      type="button"
      aria-current={active ? 'page' : undefined}
      aria-label={label}
      onClick={onClick}
      className={cn(
        'relative flex h-8 w-full min-w-0 items-center gap-2 overflow-hidden rounded-pill px-2 text-left text-[13px] transition-colors',
        // 激活 = 往暗里挖;悬停 = 往暖里偏。两个维度,不会互相盖掉
        active ? 'bg-canvas text-fg' : 'text-fg hover:bg-tint-hover'
      )}
    >
      {/*
        ★ 导航图标**不分激活态**,一律满强度 `icon`。
        原本非激活用 `accent-soft`(#7f5944),放大 6 倍对比参考图才看出来:
        参考里五个图标是同一个强度,而我这边量出来是 #754e3c —— 一整排读起来像是禁用了。
        激活态已经由行底色(`bg-canvas`)表达,不需要图标再表达第二遍。
        用 `icon` 不用 `accent`:深色参考里它是橙的,浅色参考里是中性灰,
        两者是同一个语义的两套取值 —— 见 IconButton 的注释。
      */}
      <span className="shrink-0 text-icon">{icon}</span>
      <span className="min-w-0 flex-1 truncate">{label}</span>
      {badge !== undefined && <span className="absolute top-0.5 right-0.5">{badge}</span>}
    </button>
  )
}

type SidebarI18n = Translate

/**
 * 历史会话按更新时间分组。已打开的 Tab 仍由 session id 去重，
 * 这样点击历史项只会激活现有 Tab，不会在列表里再画一条重复记录。
 */
function SessionGroupList({
  workspaceId,
  focusMode,
  sessions,
  chatTabs,
  activeSessionId,
  runningSessionIds,
  onSelectSession,
  onDeleteSession,
  onExtractSkill,
  t
}: {
  workspaceId: string
  focusMode: boolean
  sessions: readonly SessionListItem[]
  chatTabs: readonly InnerTab[]
  activeSessionId: string | null
  runningSessionIds: ReadonlySet<string>
  onSelectSession: (sessionId: string) => void
  onDeleteSession: DeleteSessionFn
  /** 右键「提炼为 Skill」。失败提示由提供方负责。 */
  onExtractSkill: (session: SessionListItem) => void
  t: SidebarI18n
}): ReactNode {
  /*
    ★ **只列库里真有的会话。**

    这里曾经还有一段「Tab 里有、但库里没有 → 合成一条列表项」的补丁,那是
    `sessions:create` IPC 往返期间的兜底。现在渲染层根本不建会话了(白纸不落库,
    见 `stores/tabs.ts` 的 `makeTab`),那段补丁就成了草稿垃圾的唯一来源 ——
    每开一个空 Tab 这里就凭空多一行「新对话」,而库里什么都没有,右键删也删不掉。

    草稿的去处是顶部那条 Tab 栏,它本来就在那儿。发出第一条消息之后主进程
    `runAgent` 才 `ensureSession`,广播 `sessions:changed`,这一行才出现。
  */
  const allSessions: SessionListItem[] = sessions.filter((session) => !session.archived)
  const startOfToday = new Date().setHours(0, 0, 0, 0)
  const startOfRecent = startOfToday - 6 * 86_400_000
  const [multiSelect, setMultiSelect] = useState(false)
  const [selectedIds, setSelectedIds] = useState<ReadonlySet<string>>(new Set())
  const toggleSelected = (sessionId: string): void => {
    setSelectedIds((current) => {
      const next = new Set(current)
      if (next.has(sessionId)) next.delete(sessionId)
      else next.add(sessionId)
      return next
    })
  }
  /*
    需求:「最近 7 天」是**点开才加载**的一组,默认只留标题那一行。
    不满足会怎样:七天里攒下的会话会全部铺在侧边栏里,把「今天」和下面的
    「归档」挤出可视区 —— 用户要的那几条今天的对话反而要滚动才看得到。

    `today` / `earlier` 仍是默认展开:「今天」是当前在用的(必须一进来就在),
    「更早」是用户自己往下翻才会碰到的一档 —— 只有中间这档是纯粹的噪音。
  */
  const groups = [
    { key: 'today', title: t('workspace.today'), defaultOpen: true, items: allSessions.filter((s) => s.updatedAt >= startOfToday) },
    { key: 'recent', title: t('workspace.last7Days'), defaultOpen: false, items: allSessions.filter((s) => s.updatedAt < startOfToday && s.updatedAt >= startOfRecent) },
    { key: 'earlier', title: t('workspace.earlier'), defaultOpen: true, items: allSessions.filter((s) => s.updatedAt < startOfRecent) }
  ] as const

  return (
    <>
      {groups.map((group) => {
        const visible = group.items.filter((s) => !chatTabs.some((tab) => tab.kind === 'chat' && tab.ref.sessionId === s.id))
        const openTabs = group.items.filter((s) => chatTabs.some((tab) => tab.kind === 'chat' && tab.ref.sessionId === s.id))
        const items = [...openTabs, ...visible]
        if (items.length === 0) return null
        return (
          <SessionGroupBlock
            /*
              需求:分组的展开态是**每个工作区各一份**的。`SessionGroupList` 在切工作区时
              不重挂,光靠 `defaultOpen` 的话「最近 7 天」只会在第一个工作区里收起 ——
              用户点开过一次,之后每换一个工作区看到的都是铺开的列表。
              不满足会怎样:表现为「这个功能时灵时不灵」,而且没有任何报错。
              key 里带 workspaceId 顺带把上下文菜单也收掉(菜单里存的是上一个工作区的
              会话),否则切完工作区菜单还开着、点下去操作的是别的项目里的对话。
            */
            key={`${workspaceId}:${group.key}`}
            title={group.title}
            defaultOpen={group.defaultOpen}
            revealActive={focusMode && items.some((session) => session.id === activeSessionId)}
            items={items}
            chatTabs={chatTabs}
            activeSessionId={activeSessionId}
            runningSessionIds={runningSessionIds}
            onSelectSession={onSelectSession}
            onDeleteSession={onDeleteSession}
            onExtractSkill={onExtractSkill}
            t={t}
            multiSelect={multiSelect}
            selectedIds={selectedIds}
            toggleSelected={toggleSelected}
            onToggleMultiSelect={() => {
              setMultiSelect((value) => !value)
              setSelectedIds(new Set())
            }}
            workspaceId={workspaceId}
          />
        )
      })}
    </>
  )
}

function SessionGroupBlock({
  title,
  defaultOpen,
  revealActive = false,
  items,
  chatTabs,
  activeSessionId,
  runningSessionIds,
  onSelectSession,
  onDeleteSession,
  onExtractSkill,
  t,
  multiSelect,
  selectedIds,
  toggleSelected,
  onToggleMultiSelect,
  workspaceId
}: {
  title: string
  /**
   * 进来时的展开态。**只在首次挂载生效** —— 之后 `open` 归这一行自己所有,
   * 所以用户手动点开「最近 7 天」之后,列表因 `sessions:changed` 重拉
   * (发了一条消息、改了条会话)不会把它又打回收起态。
   */
  defaultOpen: boolean
  revealActive?: boolean
  items: readonly SessionListItem[]
  chatTabs: readonly InnerTab[]
  activeSessionId: string | null
  runningSessionIds: ReadonlySet<string>
  onSelectSession: (sessionId: string) => void
  onDeleteSession: DeleteSessionFn
  /** 右键「提炼为 Skill」。见 `Sidebar` 里 `extractSkill` 的说明。 */
  onExtractSkill: (session: SessionListItem) => void
  t: SidebarI18n
  multiSelect: boolean
  selectedIds: ReadonlySet<string>
  toggleSelected: (sessionId: string) => void
  onToggleMultiSelect: () => void
  workspaceId: string
}): ReactNode {
  const [open, setOpen] = useState(defaultOpen || revealActive)
  // 没在看的会话停下来等审批 / 等回答:应用内标一下,不强制切过去
  const attentionSessionIds = useAttentionSessionIds()
  useEffect(() => {
    if (revealActive) setOpen(true)
  }, [activeSessionId, revealActive])
  const [menu, setMenu] = useState<{ session: SessionListItem; position: ContextMenuPosition } | null>(null)
  const [dialog, setDialog] = useState<{ kind: 'rename'; session: SessionListItem } | null>(null)
  const [confirmingDeleteId, setConfirmingDeleteId] = useState<string | null>(null)
  /*
    ★ 删除被运行中的 Agent 挡下时要弹窗,不能只让菜单悄悄合上 —— 而且要给出路:
    「停止并删除」。是否在跑由主进程按真实 run 状态判定(见 `session-delete.tsx`)。
  */
  const runningDelete = useRunningSessionDelete(onDeleteSession, t)
  const [renameDraft, setRenameDraft] = useState('')
  const run = async (action: () => Promise<void>): Promise<void> => {
    try { await action() } catch (error) { console.error('[sessions] 操作失败', error) }
    setMenu(null)
    setConfirmingDeleteId(null)
  }
  return (
    <li className="pt-1 first:pt-0">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((value) => !value)}
        className="flex w-full items-center gap-2 px-2.5 py-1 text-left text-[11.5px] text-fg-faint"
      >
        <ChevronRight size={12} className={cn('transition-transform duration-180', open && 'rotate-90')} />
        <span>{title}</span>
      </button>
      {open && (
        <ul className="flex flex-col gap-0.5">
          {items.map((session) => {
            const openTab = chatTabs.find((tab) => tab.kind === 'chat' && tab.ref.sessionId === session.id)
            const active = session.id === activeSessionId
            return (
              <li key={session.id}>
                <button
                  type="button"
                  onClick={() => multiSelect ? toggleSelected(session.id) : onSelectSession(session.id)}
                  onContextMenu={(event) => {
                    event.preventDefault()
                    setMenu({ session, position: { x: event.clientX, y: event.clientY } })
                  }}
                  className={cn(
                    'flex w-full items-center gap-2 rounded-[8px] px-2.5 py-1.5 text-left text-[12.5px] transition-colors',
                    active ? 'bg-canvas text-fg' : 'text-fg-muted hover:bg-tint-hover hover:text-fg'
                  )}
                >
                      {multiSelect && <span className={cn('flex size-4 shrink-0 items-center justify-center rounded-[4px] border', selectedIds.has(session.id) ? 'border-accent bg-accent text-canvas' : 'border-border')}>
                        {selectedIds.has(session.id) && <Check size={11} />}
                      </span>}
                      <span className="min-w-0 flex-1 truncate">{openTab?.title ?? session.title}</span>
                      {/*
                        需求:这颗转圈只认 `runningSessionIds`(主进程 RunRegistry 的投影,
                        由 bootstrap 起头、`agent:activeRuns` 广播维持)。
                        ★ 原先这里还 `|| session.running`,而那是 `sessions:list` 返回时的
                        **快照**:run 结束时主进程不发 `sessions:changed`,列表就不会重拉,
                        那个 true 会一直挂着 —— 表现为对话早就跑完了、消息也停了,
                        侧边栏这一行还在转圈,直到用户恰好改了某条会话(改名/归档)才消。
                        一个事实只有一处真源(§9),那一处就是运行中索引。
                      */}
                  {attentionSessionIds.has(session.id) ? (
                    <span title={t('chat.pendingInteraction')} aria-label={t('chat.pendingInteraction')} className="size-1.5 shrink-0 rounded-full bg-warning" />
                  ) : runningSessionIds.has(session.id) && (
                    <Spinner size="xs" label={t('chat.taskChecklistRunning')} className="text-accent" />
                  )}
                  {session.favorited && <span className="shrink-0 text-accent">★</span>}
                </button>
              </li>
            )
          })}
        </ul>
      )}
      {menu !== null && (
        <ContextMenu position={menu.position} label={t('session.menu')} onClose={() => {
          setMenu(null)
          setConfirmingDeleteId(null)
        }} width={220}>
          {(close) => (
            <>
              <MenuAction icon={<ExternalLink size={15} />} label={t('session.openNewWindow')} onSelect={() => {
                void run(() => openSessionWindow(workspaceId, menu.session.id))
              }} />
              <MenuAction icon={<Pencil size={15} />} label={t('session.rename')} onSelect={() => {
                setRenameDraft(menu.session.title)
                setConfirmingDeleteId(null)
                setDialog({ kind: 'rename', session: menu.session })
                close()
              }} />
              <MenuAction icon={<Copy size={15} />} label={t('session.copy')} onSelect={() => {
                void run(async () => {
                  await duplicateSession(menu.session.id, t('session.copyTitle', { title: menu.session.title }))
                })
              }} />
              {/* 提炼会话自己不给这一项:主进程会拒绝「提炼的提炼」 */}
              {menu.session.skillExtraction !== true && !runningSessionIds.has(menu.session.id) && (
                <MenuAction icon={<Sparkles size={15} />} label={t('skillify.menu')} onSelect={() => {
                  void run(async () => { onExtractSkill(menu.session) })
                }} />
              )}
              <MenuAction icon={<Link size={15} />} label={t('session.copyLink')} onSelect={() => {
                void run(async () => {
                  await copyText(`${window.location.href.split('#')[0]}#session=${encodeURIComponent(workspaceId)}/${encodeURIComponent(menu.session.id)}`)
                })
              }} />
              <div role="separator" className="my-1 h-px bg-border" />
              <MenuAction icon={<Pin size={15} />} label={menu.session.favorited ? t('session.unpin') : t('session.pin')} onSelect={() => void run(() => setFavorited(menu.session.id, !menu.session.favorited))} />
              <MenuAction icon={<Archive size={15} />} label={menu.session.archived ? t('session.unarchive') : t('session.archive')} onSelect={() => void run(() => setArchived(menu.session.id, !menu.session.archived))} />
              <MenuAction icon={<ListChecks size={15} />} label={multiSelect ? t('session.multiSelectDone') : t('session.multiSelect')} onSelect={() => { setConfirmingDeleteId(null); close(); onToggleMultiSelect() }} />
              <div role="separator" className="my-1 h-px bg-border" />
              <MenuAction
                danger
                icon={<Trash2 size={15} />}
                label={confirmingDeleteId === menu.session.id ? t('common.confirmDelete') : t('session.delete')}
                onSelect={() => {
                  if (confirmingDeleteId !== menu.session.id) {
                    setConfirmingDeleteId(menu.session.id)
                    return
                  }
                  const sessionId = menu.session.id
                  void run(async () => {
                    if (await requestSessionDelete(onDeleteSession, sessionId) === 'running') runningDelete.askStopAndDelete(sessionId)
                  })
                }}
              />
            </>
          )}
        </ContextMenu>
      )}
      {runningDelete.dialog}
      <Dialog
        open={dialog?.kind === 'rename'}
        onClose={() => setDialog(null)}
        title={t('session.rename')}
        width={420}
        footer={
          <>
            <Button variant="secondary" type="button" size="sm" onClick={() => setDialog(null)}>{t('common.cancel')}</Button>
            <Button type="button" size="sm" variant="primary" disabled={renameDraft.trim().length === 0} onClick={() => {
              if (dialog?.kind !== 'rename') return
              void run(() => renameSession(dialog.session.id, renameDraft)).then(() => setDialog(null))
            }}>{t('common.save')}</Button>
          </>
        }
      >
        <label className="block text-[12px] text-fg-muted" htmlFor="session-rename-input">{t('session.renamePrompt')}</label>
        <input id="session-rename-input" autoFocus value={renameDraft} onChange={(event) => setRenameDraft(event.target.value)} className="apple-field selectable mt-2 h-9 w-full rounded-[8px] border border-border bg-surface-field px-2.5 text-[13px] text-fg outline-none focus:border-fg-faint" />
      </Dialog>
    </li>
  )
}

function MenuAction({ icon, label, danger = false, onSelect }: { icon: ReactNode; label: string; danger?: boolean; onSelect: () => void }): ReactNode {
  return <button type="button" role="menuitem" onClick={onSelect} className={cn('flex w-full items-center gap-2.5 rounded-[8px] px-2.5 py-[7px] text-left text-[13px] transition-colors hover:bg-tint-strong', danger ? 'text-danger' : 'text-fg')}><span className={danger ? 'text-danger' : 'text-accent-soft'}>{icon}</span><span className="truncate">{label}</span></button>
}

/**
 * 归档列表里的一行。曾经这里只是个裸 `<button>`,没有右键菜单 ——
 * 「取消归档」「删除」两个动作都挂在 `SessionGroupBlock` 的上下文菜单里,
 * 而归档区渲染的是另一段 JSX,压根没接那套菜单,于是归档会话删不掉、
 * 也无法一键恢复回「最近对话」。这里补一份缩小版菜单,复用同样的 IPC。
 */
function ArchivedSessionItem({
  session,
  active = false,
  workspaceId,
  runningSessionIds,
  onSelectSession,
  onDeleteSession,
  onExtractSkill,
  t
}: {
  session: SessionListItem
  active?: boolean
  workspaceId: string
  /** 和 SessionGroupBlock 同一份运行中集合 —— 归档会话也可能挂着正在跑的 run。 */
  runningSessionIds: ReadonlySet<string>
  onSelectSession: (sessionId: string) => void
  onDeleteSession: DeleteSessionFn
  /** 归档的会话同样可以提炼 —— 做完归档正是「这段经验该沉淀了」的时刻。 */
  onExtractSkill: (session: SessionListItem) => void
  t: SidebarI18n
}): ReactNode {
  const [menu, setMenu] = useState<ContextMenuPosition | null>(null)
  const [confirmingDelete, setConfirmingDelete] = useState(false)
  const runningDelete = useRunningSessionDelete(onDeleteSession, t)
  const run = async (action: () => Promise<void>): Promise<void> => {
    try { await action() } catch (error) { console.error('[sessions] 操作失败', error) }
    setMenu(null)
    setConfirmingDelete(false)
  }
  return (
    <li>
      <button
        type="button"
        onClick={() => onSelectSession(session.id)}
        onContextMenu={(event) => {
          event.preventDefault()
          setMenu({ x: event.clientX, y: event.clientY })
        }}
        aria-current={active ? 'page' : undefined}
        className={cn('flex w-full items-center gap-2 rounded-[8px] px-2.5 py-1.5 text-left text-[12.5px] transition-colors',
          active ? 'bg-canvas text-fg' : 'text-fg-muted hover:bg-tint-hover hover:text-fg')}
      >
        <span className="min-w-0 flex-1 truncate">{session.title}</span>
        {session.favorited && <span className="shrink-0 text-accent">★</span>}
      </button>
      {menu !== null && (
        <ContextMenu position={menu} label={t('session.menu')} onClose={() => { setMenu(null); setConfirmingDelete(false) }} width={220}>
          {() => (
            <>
              <MenuAction icon={<ExternalLink size={15} />} label={t('session.openNewWindow')} onSelect={() => {
                void run(() => openSessionWindow(workspaceId, session.id))
              }} />
              <MenuAction icon={<Copy size={15} />} label={t('session.copy')} onSelect={() => {
                void run(async () => { await duplicateSession(session.id, t('session.copyTitle', { title: session.title })) })
              }} />
              {session.skillExtraction !== true && !runningSessionIds.has(session.id) && (
                <MenuAction icon={<Sparkles size={15} />} label={t('skillify.menu')} onSelect={() => {
                  void run(async () => { onExtractSkill(session) })
                }} />
              )}
              <div role="separator" className="my-1 h-px bg-border" />
              <MenuAction icon={<Archive size={15} />} label={t('session.unarchive')} onSelect={() => void run(() => setArchived(session.id, false))} />
              <div role="separator" className="my-1 h-px bg-border" />
              <MenuAction
                danger
                icon={<Trash2 size={15} />}
                label={confirmingDelete ? t('common.confirmDelete') : t('session.delete')}
                onSelect={() => {
                  if (!confirmingDelete) {
                    setConfirmingDelete(true)
                    return
                  }
                  void run(async () => {
                    if (await requestSessionDelete(onDeleteSession, session.id) === 'running') runningDelete.askStopAndDelete(session.id)
                  })
                }}
              />
            </>
          )}
        </ContextMenu>
      )}
      {runningDelete.dialog}
    </li>
  )
}

/** 可折叠卡片 —— 截图里下半三块都是这个形状 */
function Section({
  title,
  children,
  action,
  defaultOpen,
  revealSessionId = null
}: {
  title: string
  children: ReactNode
  /**
   * 标题右侧那个按钮。**这里刻意没有「装饰性图标」那一档** ——
   * 曾经有,结果「最近对话」把同一支笔画了两遍(卡片自己的 kind 图标一支、
   * 新建对话按钮一支),挤在一起看着像个渲染 bug。参考实现这个位置**只放动作**,
   * 没有动作就空着(截图里「归档」右侧就是空的)。
   */
  action?: ReactNode
  defaultOpen: boolean
  revealSessionId?: string | null
}): ReactNode {
  const [open, setOpen] = useState(defaultOpen || revealSessionId !== null)
  useEffect(() => {
    if (revealSessionId !== null) setOpen(true)
  }, [revealSessionId])
  return (
    <section className="shrink-0 overflow-hidden rounded-card bg-surface-raised">
      <div className="flex items-center gap-2 px-2.5 py-2">
        <button
          type="button"
          aria-expanded={open}
          onClick={() => setOpen((v) => !v)}
          className="flex min-w-0 flex-1 items-center gap-2 text-left"
        >
          <ChevronRight
            size={13}
            className={cn(
              // 180 而不是跟着卡片的 220:同一条曲线下,位移小的东西要更短的时间
              // 才显得是「同一下动作」—— 箭头转 90° 用 220ms 会拖在卡片后面。
              'shrink-0 text-icon transition-transform duration-180 ease-panel',
              open && 'rotate-90'
            )}
          />
          <span className="min-w-0 flex-1 truncate text-[12.5px] text-fg">{title}</span>
        </button>
        {action}
      </div>
      {/*
        ★ 展开高度是 `grid-template-rows: 0fr → 1fr`,不是 `height: 0 → auto`。
        后者根本动不了 —— CSS 不能在长度和关键字之间插值,`auto` 那一头没有数,
        所以「测一遍内容高度、写死像素、播完再改回 auto」是老写法要多一次
        测量+两次重排的原因。fr 两头都是数,浏览器自己会插值,而且内容改了
        (新开一个会话、列表加一行)不用重新测。

        里外必须是两层:动的是外层的行高,`overflow-hidden` 挂在里层 ——
        少一层的话内容会跟着行高一起被压扁,文字在这 280ms 里疯狂重排。
        内容**始终挂载**(不是 `{open && ...}`):收起时高度为 0,要留着才有得裁。
      */}
      <div
        className={cn(
          // 220 而不是面板的 280:这里展开的是 70~200px 的一小块,
          // 用整块侧边栏(297px)的时长会显得黏。曲线是同一条,手感才是一套。
          'grid transition-[grid-template-rows] duration-220 ease-panel',
          open ? 'grid-rows-[1fr]' : 'grid-rows-[0fr]'
        )}
        // 收起后里面还是可聚焦的 DOM —— 不挡住的话 Tab 键会跳进一个看不见的列表
        inert={!open}
      >
        <div className="overflow-hidden">
          <div className="px-1.5 pb-1">{children}</div>
        </div>
      </div>
    </section>
  )
}