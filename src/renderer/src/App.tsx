/**
 * 应用根。只做四件事:握手、订阅主进程推送、起事件泵、渲染外壳。
 *
 * ★ **握手时序照协议 §7**:`window:ready`(send)→ `app:getBootstrap`(invoke)
 * → 首屏 → 增量事件。顺序反了会出现「事件先到、状态还没到」的空窗。
 *
 * ★ **三个退订必须都调用**,否则每次 HMR 叠一层监听器 —— 只在 dev 出现,
 * 表现是一次状态更新触发 N 次重渲染,极难定位(方案 §3 规则 4)。
 *
 * ★ **`startAgentEventPump()` 在这里起一次,不在 ChatView 里**。
 * 每个 chat Tab 起一个泵的话,同一批事件会被 apply N 次,seq 校验立刻炸。
 */
import { useEffect, useMemo, useState } from 'react'
import type { Bootstrap } from '../../shared/domain/bootstrap'
import type { AppSettings, ResolvedTheme } from '../../shared/domain/settings'
import type { Workspace } from '../../shared/domain/workspace'
import { announceReady, getBootstrap } from './services/app'
import { onBrowserChanged } from './services/browser'
import { on } from './services/ipc'
import { AppShell } from './shell/AppShell'
import { AppSkeleton } from './shell/AppSkeleton'
import { WindowControls } from './shell/WindowControls'
import { startAgentEventPump, adoptActiveRuns, adoptActiveSubagents, refreshHydratedSessions, useRunIndex } from './stores/session'
import { setSoundPreferences, startNotificationSounds } from './stores/notification-sounds'
import { setComposerPrefs } from './stores/composer-prefs'
import { useImageThemes } from './stores/imageTheme'
import { usePluginsStore } from './stores/plugins'
import { useWindowStore, startActiveWorkspaceReporting } from './stores/window'
import { useTabsStore } from './stores/tabs'
import { applyTheme } from './theme/apply'
import { translate, useI18n } from './i18n'
import type { ClientAuthState } from '../../shared/domain/client-auth'
import { getClientAuthState, getClientUser } from './services/client-auth'
import { WelcomeView } from './views/WelcomeView'
import { ClientTeamSelectionView } from './views/ClientTeamSelectionView'
import { ToastViewport } from './components/ui/ToastViewport'
import { toast, useToastStore } from './stores/toast'
import { useThemeProfiles } from './stores/themeProfiles'

/**
 * 「这份输入 / 布局还没保住」那条 toast 的固定 key。
 *
 * ★ 必须固定:`push` 对同 key 是**就地替换 + 计数**,而主进程每次刷盘失败都会
 * 推一条 `session:inputPersistence` —— 用随机 key 的话,磁盘满的时候屏幕上
 * 会堆满互相覆盖的同一条提示(上限 3 条也拦不住它反复顶掉别的通知)。
 */
const PERSISTENCE_TOAST_KEY = 'session-input-persistence'

function sessionRouteFromLocation(): { workspaceId: string; sessionId: string | null } | null {
  const raw = window.location.hash.match(/^#\/([^/]+)(?:\/([^/]+))?$/)
  if (raw === null) return null
  return {
    workspaceId: decodeURIComponent(raw[1]!),
    sessionId: raw[2] === undefined ? null : decodeURIComponent(raw[2])
  }
}

export default function App(): React.JSX.Element {
  const [boot, setBoot] = useState<Bootstrap | null>(null)
  const [settings, setSettings] = useState<AppSettings | null>(null)
  const [appearance, setAppearance] = useState<ResolvedTheme | null>(null)
  const [workspaces, setWorkspaces] = useState<Workspace[]>([])
  const [fatal, setFatal] = useState<string | null>(null)
  const [auth, setAuth] = useState<ClientAuthState | null>(null)
  const quickWindow = useMemo(() => new URLSearchParams(window.location.search).get('window') === 'quick', [])
  const initialSessionRoute = useMemo(sessionRouteFromLocation, [])
  const hydrate = useWindowStore((s) => s.hydrate)
  const openWorkspace = useWindowStore((s) => s.openWorkspace)
  const openSession = useTabsStore((s) => s.openSession)
  const syncBrowserTabs = useTabsStore((s) => s.syncBrowserTabs)
  const syncSessionTitle = useTabsStore((s) => s.syncSessionTitle)
  const setRightPanelForWorkspace = useWindowStore((s) => s.setRightPanelForWorkspace)
  const setMaximized = useWindowStore((s) => s.setMaximized)
  const { setLocale, t } = useI18n()

  useEffect(() => {
    if (settings !== null) setLocale(settings.locale)
  }, [settings, setLocale])

  useEffect(() => startAgentEventPump(), [])

  // 需求:设置 › 通用 › 提示音。和事件泵同理只起一次,否则一个 run 跑完响 N 声(见该文件头)
  useEffect(() => startNotificationSounds(), [])
  useEffect(() => {
    if (settings !== null) setSoundPreferences(settings.notifications)
  }, [settings])
  // 输入框补全的开关镜像(理由见 stores/composer-prefs.ts 文件头)
  useEffect(() => {
    if (settings !== null) setComposerPrefs(settings)
  }, [settings])

  /*
    ★ 上报当前工作区。**放在事件泵旁边而不是握手那个 effect 里**:它不依赖
    bootstrap 到没到 —— 订阅先挂上,`hydrate()` 之后那次赋值自然会被捎出去。
  */
  useEffect(() => startActiveWorkspaceReporting(), [])

  useEffect(() => {
    // 先写窗口身份，再开始握手，避免 quick 窗口的早到事件按 main 布局落盘。
    useWindowStore.setState({ windowKind: quickWindow ? 'quick' : 'main' })
    const offSettings = on('settings:changed', setSettings)
    const offTheme = on('theme:changed', ({ resolved }) => setAppearance(resolved))
    const offLibrary = on('theme:libraryChanged', ({ profiles, images }) => {
      useThemeProfiles.setState({ profiles })
      useImageThemes.setState({ uploaded: images })
    })
    const offWorkspaces = on('workspace:changed', ({ workspaces: ws }) => { setWorkspaces(ws); useWindowStore.getState().updateWorkspaces(ws) })
    const offSessions = on('sessions:changed', (change) => {
      const { workspaceId, renamed } = change
      if (workspaceId !== undefined && renamed !== undefined) {
        syncSessionTitle(workspaceId, renamed.sessionId, renamed.title)
        return
      }
      if (change.kind === 'deleted') useTabsStore.getState().removeSessions(change)
      void refreshHydratedSessions(change)
    })
    const offBrowser = onBrowserChanged((change) => {
      // Headless pages intentionally have no workspace tab; their lifecycle is Agent-only.
      const before = useTabsStore.getState().stateOf(change.workspaceId).tabs
      syncBrowserTabs(change.workspaceId, change.tabs.filter((tab) => tab.backend === 'iab'))
      const addedBrowser = quickWindow && useTabsStore.getState().stateOf(change.workspaceId).tabs.some(
        (tab) => tab.kind === 'browser' && !before.some((previous) => previous.id === tab.id)
      )
      if (change.rightPanelOpen === true && (!quickWindow || addedBrowser)) setRightPanelForWorkspace(change.workspaceId, true)
    })
    /*
      ★ **必须排在 announceReady() 之前。** 主进程在 `window:ready` 的 handler 里
      **同步**回推首帧最大化状态(见 main/ipc/index.ts),监听器晚一步登记就收不到,
      表现是 Windows 上从最大化态启动时中间那颗按钮画着「□」而不是还原字形。
    */
    const offMaximized = on('window:maximized', ({ maximized }) => setMaximized(maximized))
    const offClientAuth = on('clientAuth:changed', setAuth)
    /*
      ★ **插件目录的订阅在 App 层,不在插件设置页里。**

      这一条以前只登记在 `views/extensions/plugins/PluginsPanel.tsx` 的 effect 里,
      于是没打开过那一页的会话中,渲染层的 catalog 一直是空的 —— 而
      `stores/plugins.ts` 的 `menuItems()` 是遍历 catalog 出菜单的。表现是:
      「+」菜单里没有插件贡献的入口、`.excalidraw` 双击开出来是空白,
      直到用户点进插件页才「忽然好了」。看上去像插件到那时才启动,
      其实主进程开机就已经唤醒了它(`main/ipc/plugins.ts` 的 onStartup 那段),
      只是没人在听它播出来的 catalog。
    */
    const offPlugins = on('plugins:changed', () => { void usePluginsStore.getState().load() })

    announceReady(quickWindow ? 'quick' : 'main')
    void getBootstrap()
      .then((b) => {
        setBoot(b)
        setSettings(b.settings)
        setWorkspaces(b.workspaces)
        setAppearance(b.resolvedTheme)
        /*
          ★ **首屏刷盘失败要说话。** 主进程为了让这次重载拿到完整的 Tab 布局 /
          草稿,会先把防抖里那些键冲一遍;冲不下去时它照旧把首屏给我们,只在
          `persistenceWarning` 上留一个标记 —— 不报的话,用户看到的是一切正常,
          而他刚拖出来的 Tab 顺序已经没了。

          走 `translate` 而不是上面的 `t`:这里是握手回调,`t` 进了这个 effect 的
          依赖数组就意味着一句文案改动会重跑整段握手。key 固定,重复触发只替换。
        */
        if (b.persistenceWarning === true) {
          toast.error(translate('app.persistenceWarning'), PERSISTENCE_TOAST_KEY)
        }
        /*
          ★ **首屏不等网络。** `getClientAuthState()` 只读主进程里那行 SQLite 快照
          (实测 1ms),拿到就渲染;`getClientUser()` 是一次真实 HTTP 往返
          (实测 2219ms),降级成后台刷新。

          它以前被 `await` 在这条路径上,于是窗口早就 `show()` 出来了、界面却还要
          再等 2.2 秒才有内容 —— 那正是用户说的「刚打开会白屏一会儿」。

          不 setAuth 结果也不丢:主进程两条支路都会推。成功那支写完 KV 调
          `announce(state())`(新头像、新钱包余额),需要选 Team 的 409 那支也调,
          而 `clientAuth:changed` 上面已经订阅了。所以这里只管发起,不管收。
        */
        void getClientAuthState()
          .then((next) => {
            setAuth(next)
            // 校验老客户端建的会话有没有绑 Team —— 服务端答 409 时 getClientUser
            // 会把界面推成选 Team 态。已经是选 Team 态、或者压根没登录就别问了。
            if (next.mode === 'authenticated' && next.contextRequired !== true) {
              void getClientUser().catch((error: unknown) => console.error('[clientAuth] 后台刷新账号失败', error))
            }
          })
          .catch((error: unknown) => setFatal(error instanceof Error ? error.message : String(error)))
        hydrate(b)
        // 在工作区激活与首屏挂载之前建立目标会话，避免空布局先改写 hash。
        if (b.windowKind === 'quick' && initialSessionRoute?.sessionId != null) {
          useTabsStore.getState().openFocusedSession(initialSessionRoute.workspaceId, initialSessionRoute.sessionId)
        }
        // ⌘R 重载后主进程里还活着的 run —— 角标要立刻正确,不能等下一个事件
        adoptActiveRuns(b.activeRuns)
        // A background child may outlive its parent run and still needs a
        // route back to the parent Task card after this renderer reloads.
        adoptActiveSubagents(b.activeSubagents ?? [])
        /*
          路由在 hash 里:`#/{workspaceId}` = 这个工作区(新对话),
          `#/{workspaceId}/{sessionId}` = 指定会话。

          ★ **不再把 hash 清掉。** 它以前是一次性入参(「在新窗口打开这段会话」
          用完即弃),现在是常驻状态 —— 激活哪个 Tab 它就写成什么,见 AppShell
          里那个同步 effect。清掉的话下一帧就被同步回来,中间白闪一次。
        */
        if (initialSessionRoute !== null) {
          const { workspaceId, sessionId } = initialSessionRoute
          void openWorkspace(workspaceId).then((opened) => {
            if (opened && sessionId !== null && !quickWindow) openSession(workspaceId, sessionId)
          })
        }
      })
      .catch((e: unknown) => setFatal(e instanceof Error ? e.message : String(e)))

    // ★ **每一个**退订都必须调用,否则每次 HMR 叠一层监听器
    return () => {
      offSettings()
      offTheme()
      offLibrary()
      offWorkspaces()
      offSessions()
      offBrowser()
      offMaximized()
      offClientAuth()
      offPlugins()
    }
  }, [hydrate, initialSessionRoute, openSession, openWorkspace, quickWindow, setMaximized, setRightPanelForWorkspace, syncBrowserTabs, syncSessionTitle])

  /**
   * 会话输入(草稿 + 队列)这一次落盘成没成。
   *
   * ★ **单独一个 effect,不并进上面那个握手块。** 那一块的依赖数组里是
   * `hydrate` / `openSession` 这些 store 动作和 `t` —— 把这条订阅挂进去,
   * 以后任何一次文案或 store 标识变化都会退订重订整段握手,而这个频道只在
   * 主进程刷盘真的失败时才有事件,重订等于漏掉它。
   *
   * ★ 文案在事件里现取(用 `translate`),不进依赖数组:切语言时 toast 里
   * 已经发出去的那句会留在原语言,但下一次事件会带新语言 —— 而这个订阅
   * 全程不退订,正是它该有的样子。
   *
   * ★ `saved` **只**关掉这条提示,不 `clear()`:用户手上别的 toast(比如
   * 「更新已就绪」)不该被一条保存成功顺带抹掉。
   */
  useEffect(() => on('session:inputPersistence', ({ state }) => {
    if (state === 'saved') {
      const { toasts, dismiss } = useToastStore.getState()
      for (const item of toasts) {
        if (item.key === PERSISTENCE_TOAST_KEY) dismiss(item.id)
      }
      return
    }
    toast.error(
      translate(state === 'retrying' ? 'session.inputPersistence.retrying' : 'session.inputPersistence.blocked'),
      PERSISTENCE_TOAST_KEY
    )
  }), [])

  /**
   * ★ **深浅和颜色是两路来的,必须汇到一处再落地。**
   * 外观模式跟着系统走(`theme:changed` 由主进程 nativeTheme 推),
   * 颜色主题跟着设置走(`settings:changed`)—— 两者各自到达,
   * 谁后到都得用上另一边的最新值。所以这里等两个 state 都有了再写一次 DOM,
   * 而不是在各自的回调里各写各的(那样切外观会把颜色抹回默认)。
   *
   * 两个都还没到的那几帧,界面用的是 `theme.css` 里写死的墨绿 —— 首屏就是对的。
   */
  /**
   * 上传的图片主题。**它和上面两路是第三个来源** —— 选中的那张如果是上传的,
   * 它的 seed 才是整套 token 的出处,所以 `uploaded` 到达之前 `resolveImageTheme`
   * 查不到这个 id,界面会先按颜色主题画一帧,拿到表之后再落一次。
   *
   * ★ 只拉表,没有第二步。底图 URL 跟着表一起来(`source.url` 是 `ncw://` 地址),
   * 原先那套「按需兑现 blob URL」的机制随协议迁移一起删掉了。
   */
  const uploadedThemes = useImageThemes((s) => s.uploaded)
  const profiles = useThemeProfiles((s) => s.profiles)
  const draft = useThemeProfiles((s) => s.draft)
  const fullPreview = useThemeProfiles((s) => s.fullPreview)

  useEffect(() => {
    void useImageThemes.getState().load()
    void useThemeProfiles.getState().load().catch(console.error)
    /*
      ★ 插件目录**开机就拉一次**。`load()` 里的 `applyCatalog()` 顺手把每个插件的
      l10n 注册进 i18n —— 菜单标题(`%cmd.new%`)靠它才显示成人话,而菜单本身
      就是激活事件的来源。等用户点开插件页才拉的话,在那之前插件对界面
      等于不存在。失败不拦首屏:`load()` 自己吞了异常并复位 loading。
    */
    void usePluginsStore.getState().load()
  }, [])

  useEffect(() => {
    if (appearance === null || settings === null) return
    const profile = fullPreview && draft ? draft : profiles.find((p) => p.id === settings.activeThemeProfileId)
    applyTheme(document.documentElement, appearance, settings, uploadedThemes, profile)
  }, [appearance, settings, uploadedThemes, profiles, draft, fullPreview])

  // 运行中角标的数据源是 RunRegistry 的投影,不是任何 UI 状态(方案 §8)
  const runIndex = useRunIndex()
  const runningSessionIds = useMemo(() => new Set(runIndex.map((r) => r.sessionId)), [runIndex])
  const runningWorkspaceIds = useMemo(() => new Set(runIndex.map((r) => r.workspaceId)), [runIndex])

  /*
    ★ 三颗自绘窗口按钮(仅 Windows/Linux)挂在**这里**,而且在下面两个早退分支
    之外 —— 握手失败或者卡在首屏之前时,用户同样得能最小化/关掉这个窗口。
    它自己 portal 到 body 并 fixed 定位,所以放在哪个分支里都不影响布局。
  */
  if (fatal !== null) {
    return (
      <>
        <WindowControls />
        <div className="flex h-full items-center justify-center bg-app p-8">
          <p className="selectable max-w-lg font-mono text-[13px] text-danger">{t('app.handshakeFailed', { error: fatal })}</p>
        </div>
      </>
    )
  }

  /*
    首屏之前显示骨架屏 —— 窗口这时已经 show() 出来了,一块纯色看着像程序坏了。
    轮廓和 AppShell 完全重合,所以真界面接上来是「内容填进来」,不是换一屏。
  */
  if (boot === null || settings === null) {
    return (
      <>
        <WindowControls />
        <AppSkeleton focusMode={quickWindow} />
      </>
    )
  }

  if (auth === null) {
    return <><WindowControls /><AppSkeleton focusMode={quickWindow} /></>
  }
  if (auth.mode === 'undecided') {
    return <><WindowControls /><WelcomeView onComplete={() => { void getClientAuthState().then(setAuth) }} /></>
  }
  if (auth.mode === 'authenticated' && auth.contextRequired === true) {
    return <><WindowControls /><ClientTeamSelectionView auth={auth} onComplete={setAuth} /></>
  }

  return (
    <>
      <WindowControls />
      <AppShell
        settings={settings}
        versions={boot.versions}
        windowKind={boot.windowKind}
        workspaces={workspaces}
        runningSessionIds={runningSessionIds}
        runningWorkspaceIds={runningWorkspaceIds}
        auth={auth}
      />
      {/*
        ★ 只挂在这一个分支里,不跟着 `WindowControls` 铺到每个早退分支上。
        上面那几个分支(握手失败、骨架屏、欢迎页、选 Team)都还没有可供操作的
        界面 —— 没有操作就没有「操作的结果」要报。窗口按钮不一样,那是任何时候
        都得能关窗。
      */}
      <ToastViewport />
    </>
  )
}
