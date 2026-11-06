/**
 * 应用外壳 —— 悬浮面板布局。
 *
 * 窗口四周留 8px,侧边栏与右侧内容区是**两块独立的圆角面板**,中间也是 8px 缝
 * (截图里能看见 `--color-app` 从缝里透出来)。四边和中缝**同一个 8px**,是一套
 * 栅格不是四个凑出来的数 —— 量自 4aa68110:面板体 8..304 / 313..1255,
 * 上下同样 8..1132,窗口边框自己占 1px。这不是装饰:面板边缘就是分区边界,
 * 所以全局基本不用 box-shadow,靠底色差分层。
 *
 * ★ **`app-drag` 与 Tab 拖动排序正面冲突**(方案 §8)。两个平台顶部这条 34px 都落在
 * 自绘标题栏里(macOS 是 `titleBarStyle: 'hiddenInset'`,Windows/Linux 是 `'hidden'`),
 * 而那块是 `-webkit-app-region: drag` —— **OS 会吞掉这个区域里所有 pointer 事件**,
 * 表现是「Tab 拖不动,整个窗口跟着鼠标跑」。所以 drag 只给 Tab **之间的空白**,
 * 每个 Tab 元素自己显式 `app-no-drag`(见 OuterTabBar)。
 *
 * ★ Windows/Linux 那三颗窗口按钮画在这条的**右端**,但它们是自绘的普通 DOM
 * (`WindowControls`,portal 到 body 的悬浮层),不是原生区域 —— 所以不存在
 * 「看得见点不着」那类坑,这条只需要用 `pr-window-controls` 让出宽度。
 *
 * ★ **设置是模态浮层,不是一个 Tab**(截图 06cd7b3c 是盖在界面上的面板)。
 * 做成 feature Tab 的话,「关掉设置」和「关掉一个工作区」就成了同一个动作。
 */
import { PanelLeft } from "lucide-react";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import type { Bootstrap } from "../../../shared/domain/bootstrap";
import type { AppSettings } from "../../../shared/domain/settings";
import type { Workspace } from "../../../shared/domain/workspace";
import type { WindowKind } from "../../../shared/domain/tab";
import type { SessionListItem } from "../../../shared/domain/session";
import type { ClientAuthState } from "../../../shared/domain/client-auth";
import { IconButton } from "../components/ui/IconButton";
import { cn } from "../lib/cn";
import { IS_MAC } from "../lib/platform";
import { useI18n } from "../i18n";
import { usePresence } from "../lib/usePresence";
import { closeWorkspace, pickWorkspace } from "../services/app";
import { killTerminal, listTerminals } from "../services/terminal";
import { deleteSession, listSessions } from "../services/sessions";
import { on } from "../services/ipc";
import { onScheduledChanged } from "../services/scheduled";
import { SettingsOverlay } from "../settings/SettingsOverlay";
import { DEFAULT_SETTINGS_PAGE } from "../settings/nav";
import { useTabsStore } from "../stores/tabs";
import { useWindowStore } from "../stores/window";
import { FeatureView } from "../views/registry";
import { BrowserFeature } from "../views/browser/BrowserFeature";
import { RewardsOverlay } from "../views/rewards/RewardsOverlay";
import { OuterTabBar } from "./OuterTabBar";
import { UpdateIndicator } from "./UpdateIndicator";
import { Sidebar } from "./Sidebar";
import { SearchPalette } from "./SearchPalette";
import { StatusBar } from "./StatusBar";
import { mergeCommands, usePluginCommands, type Command } from "./commands";
import { useCommandShortcuts } from "./useCommandShortcuts";
import { confirmDocumentChanges, useDocumentsStore } from '../stores/documents';
import { DocumentDialogs } from '../views/files/DocumentDialogs';
import { OverwriteConfirmDialog } from './OverwriteConfirmDialog';
import { submitWorkspaceRename } from './tab-rename-actions';
import { placePluginTab } from './plugin-tab-target';
import { PluginInteractionHost } from './PluginInteractionHost';
import { toast } from '../stores/toast';
import { DockRoot } from './Dock';
import type { DockNode } from '../../../shared/domain/dock';
import { ConnectionDialogs } from './ConnectionDialogs';
import { CreateSshWorkspaceDialog } from './CreateSshWorkspaceDialog';
import { EditWorkspaceDialog } from './EditWorkspaceDialog';

/**
 * 面板开合的时长。**侧边栏与右/底部两条 dock 边必须同一个数** ——
 * 侧边栏收起的同时,主面板的左边界在往左长、Tab 条的左内边距在往右推、
 * 那颗展开按钮在等着淡入,dock 边的轨道也在同时收/放,几条曲线只要有一条
 * 不同步,看着就是「分好几批到位」。改这里,别在某个组件里单独写一个 duration
 * (右/底部那两条的 transition 写在 Dock.tsx,改这个数记得同步 duration-280)。
 *
 * 280 而不是 200:侧边栏是 297px 宽的一大块,200ms 下人眼几乎只看到首尾两帧,
 * 「快」和「闪」是一回事。位移越大需要的时间越长(同样一条曲线,一个图标转 90°
 * 150ms 就够),这一档是当前布局下能明显看出「它在走」的下限。
 * 上限在 350 附近 —— 再长就开始觉得点了没反应。
 */
const PANEL_MS = 280;

export function AppShell({
  settings,
  versions,
  windowKind,
  workspaces,
  runningSessionIds,
  runningWorkspaceIds,
  auth,
}: {
  settings: AppSettings;
  /** 「关于」页那四个版本号。来自 bootstrap,不是 preload 的 `versions()` */
  versions: Bootstrap["versions"];
  windowKind: WindowKind;
  workspaces: readonly Workspace[];
  /** 来自 RunRegistry 的聚合,不是任何 UI 状态(方案 §8) */
  runningSessionIds: ReadonlySet<string>;
  runningWorkspaceIds: ReadonlySet<string>;
  auth: ClientAuthState;
}): ReactNode {
  const { t } = useI18n();
  const {
    outer,
    activeOuterId,
    activeWorkspaceId,
    activeStandaloneFeature,
    sidebarCollapsed,
    rightPanelOpen,
    bottomPanelOpen,
    settingsPage,
    scheduledUnread,
    rewardsOpen,
  } = useWindowStore();
  const win = useWindowStore();
  const quickWindow = windowKind === 'quick';
  useEffect(() => onScheduledChanged((event) => {
    if (event.kind === 'run' && (event.status === 'success' || event.status === 'error' || event.status === 'skipped') && useWindowStore.getState().activeStandaloneFeature !== 'scheduled') useWindowStore.setState({ scheduledUnread: true })
  }), []);
  const tabs = useTabsStore();
  const ensureTabs = useTabsStore((s) => s.ensure);

  /*
    插件请求打开它的自定义编辑器。

    ★ 订阅放在 AppShell 而不是插件设置页:发起这件事的是 `+` 菜单里的一项,
    而那个菜单在任何一屏都点得到。挂在设置页上意味着「没开过设置页就打不开」——
    那会是一条没有任何错误信息的失败。

    ★ 安全判断已经在主进程做完(viewType 是这个插件声明过的、path 在工作区内),
    这里只决定放哪一格:跟着当前活动工作区,开在主区。
  */
  useEffect(() => on('plugins:openCustomEditor', ({ pluginId, viewType, path }) => {
    const workspaceId = useWindowStore.getState().activeWorkspaceId;
    if (workspaceId === null) return;
    useTabsStore.getState().open(workspaceId, 'custom', 'main', {
      pluginId,
      viewType,
      path,
      // 标题用文件名 —— 它是用户自己起的名字,不进翻译表(同 `i18n/themes.ts` 的范式)
      title: path.split('/').pop() ?? path
    });
  }), []);

  /*
    插件请求打开一个网页应用 / 一个网页地址(`contributes.webApps`、`tabs.openBrowser`)。

    ★ 订阅与上面那条同处一室,理由也一样:发起它的可能是侧边栏入口、命令面板,
    也可能是插件自己 —— 挂在任何一个页面上都会变成「没开过那一页就打不开」。

    ★ 落点的判断在 `plugin-tab-target.ts`(纯函数,可直测),这里只负责拿到
    当前工作区、翻译标题、开 Tab。安全判断主进程已经做完了。
  */
  useEffect(() => on('plugins:openTab', ({ pluginId, target }) => {
    /*
      ★ 插件终端开在**目标工作区**(菜单在哪个工作区点的,target.workspaceId
      就是哪个),不是「当前活动」的那个 —— 后者只做其余目标的兜底。
    */
    const workspaceId = target.kind === 'terminal' ? target.workspaceId : useWindowStore.getState().activeWorkspaceId;
    if (workspaceId === null) return;
    /*
      标题:webapp 的 title 是 `%key%`,注册进 i18n 的是 `plugin.<id>.<key>`。
      ★ 这里就 `t()` 掉,因为 Tab 标题会**跟着布局落盘** —— 落一个 key 进去的话,
      重启之后 Tab 条上写的就是 `plugin.ncw.bilibili.app.home`。
      插件终端同理;title 缺省时交给 makeTab 落「终端」默认文案。
    */
    const title = target.kind === 'terminal'
      ? (target.title !== undefined
          ? t(`plugin.${pluginId}.${target.title.replace(/^%|%$/g, '')}` as Parameters<typeof t>[0])
          : undefined)
      : target.kind === 'webapp'
        ? t(`plugin.${pluginId}.${target.title.replace(/^%|%$/g, '')}` as Parameters<typeof t>[0])
        : new URL(target.url).host;
    const placement = placePluginTab(target, pluginId, title);
    useTabsStore.getState().open(workspaceId, placement.kind, placement.pane, placement.init);
    /*
      ★ 降级要**说出来**。`open: 'feature'`(独立外层 Tab)这一版还没实现,
      静默按内层 Tab 开的话,作者会以为自己写错了清单 —— 而他没写错。
    */
    if (placement.degradedFrom !== undefined) {
      toast.info(t('pluginWebApp.featureFallback'), `plugin-open-${pluginId}`);
    }
  }), [t]);

  /*
    ★ **拆包的配套预热。** ChatView 现在是 lazy 的(见 views/registry.tsx 文件头:
    它替主 bundle 背走了 streamdown + katex),代价是「点开一段会话」多了一次 chunk
    往返。外壳一挂上首屏就已经画完了,此后到用户真去点之间那段空闲白白浪费 ——
    在这里把它预取掉,等用户点的时候 lazy 已经能同步解析,Suspense 的占位一帧都不出现。

    只预热 ChatView:它是每次启动几乎必开的那个。终端 / 文档不是,替它们抢带宽
    反而会拖慢真正要用的那个。

    `requestIdleCallback` 没有就退回一个短 timeout(Chromium 里其实一定有,
    这行是给 jsdom 下的单测用的)。返回的清理函数取消掉还没跑的那次。
  */
  useEffect(() => {
    const preload = (): void => { void import('../views/chat/ChatView') };
    if (typeof requestIdleCallback !== 'function') {
      const timer = setTimeout(preload, 200);
      return () => clearTimeout(timer);
    }
    const handle = requestIdleCallback(preload, { timeout: 2000 });
    return () => cancelIdleCallback(handle);
  }, []);

  /**
   * 面板的开关都是条件性的,直接 `{open && <Panel/>}` 收起时节点当场消失,
   * 没有东西可以播退场 —— 所以统一过一遍 usePresence(它的文件头写了为什么)。
   * `shown` 驱动尺寸,`mounted` 决定还渲不渲染,`animating` 只在开合那一下为真,
   * 拖分隔条时是假的(否则每拖一帧都排一次插值,手感像拉皮筋)。
   *
   * ★ 右/底部两条 dock 边也各过一遍,三个信号交给 Dock 的 DockSplitView 映射:
   * `mounted` → 开合期间保留 ghost 结构(不摊平、两侧子树不重挂载),
   * `shown` → 轨道几何的起点/终点(开的第一帧仍是关的几何,双帧起跳,
   * 和侧边栏同理),`animating` → 只在这 280ms 里给 grid 挂 transition。
   * 手动点、切工作区、Agent 打开文件掀开右栏 —— 这些都只改 `rightPanelOpen`
   * 一个事实,所以都走同一条动画,不需要各接一条。
   */
  const sidebar = usePresence(!sidebarCollapsed, PANEL_MS);
  const rightEdge = usePresence(rightPanelOpen, PANEL_MS);
  const bottomEdge = usePresence(bottomPanelOpen, PANEL_MS);

  const activeOuter = outer.find((t) => t.id === activeOuterId);
  const workspace = workspaces.find((w) => w.id === activeWorkspaceId);
  const firstGroupId = (node: DockNode): string | null => node.type === 'group' ? node.id : firstGroupId(node.first);
  const toggleDockEdge = (edge: 'bottom' | 'right'): void => {
    if (activeWorkspaceId === null) return;
    const opening = edge === 'bottom' ? !bottomPanelOpen : !rightPanelOpen;
    if (edge === 'bottom') win.toggleBottomPanel(); else win.toggleRightPanel();
    if (!opening) return;
    const dock = tabs.dockOf(activeWorkspaceId);
    const hasEdge = dock.tabs.some((tab) => tab.pane === edge);
    if (hasEdge) return;
    const base = firstGroupId(dock.root);
    if (base === null) return;
    tabs.splitAndOpenDock(activeWorkspaceId, base, edge === 'bottom' ? 'down' : 'right', edge === 'bottom' ? 'terminal' : 'files', edge);
  };
  const [sessionItems, setSessionItems] = useState<SessionListItem[]>([]);
  const [createSshOpen, setCreateSshOpen] = useState(false);
  const [editWorkspaceId, setEditWorkspaceId] = useState<string | null>(null);
  const [searchOpen, setSearchOpen] = useState(false);

  useEffect(() => {
    if (activeWorkspaceId === null) {
      setSessionItems([]);
      return;
    }
    let alive = true;
    let latest = 0;
    const load = (): void => {
      const request = ++latest;
      void listSessions(activeWorkspaceId)
        .then((items) => {
          if (!alive || request !== latest) return;
          setSessionItems(items);
          for (const item of items) useTabsStore.getState().syncSessionTitle(activeWorkspaceId, item.id, item.title);
        })
        .catch((err: unknown) => console.error("[sessions] 加载列表失败", err));
    };
    load();
    const off = on("sessions:changed", (event) => {
      if (
        event.workspaceId === undefined ||
        event.workspaceId === activeWorkspaceId
      )
        load();
    });
    return () => {
      alive = false;
      off();
    };
  }, [activeWorkspaceId]);

  // 工作区一露面就保证它至少有一个对话 Tab —— 空的内层 Tab 条没有任何可做的事。
  // 专注窗口已由 App 建立目标会话；缺少会话路由时 ensure 只补临时草稿。
  useEffect(() => {
    if (activeWorkspaceId !== null) ensureTabs(activeWorkspaceId);
  }, [activeWorkspaceId, ensureTabs]);

  /*
    打开设置 —— 走渲染层的全局 keydown,不走主进程应用菜单；组合键由设置页配置。
    主进程现在**完全没有** `Menu` / `globalShortcut`,为一个快捷键就得补一整套
    菜单模板 + 一条 main→renderer 命令频道 + 契约白名单条目,不划算。
    代价照实记:窗口没聚焦时不响应,也不出现在 macOS 菜单栏里
    (「偏好」页那一行的描述文字就是这句话,别只写在这儿)。

    依赖取 action 和当前组合键，不取整个 store —— `openSettings` 引用是稳定的。
  */
  const openSettings = useWindowStore((s) => s.openSettings);
  const openSettingsShortcut = settings.shortcuts.openSettings;

  /*
    ★ 命令注册表 —— 内置的那几条在这里现拼(这里才拿得到 store 的 action),
    插件的经 `usePluginCommands` 并进来。三处消费者共用这一份:
    命令面板、快捷键分发、将来的 `commandPalette` 菜单贡献点。

    ★ **内置排在前面**,而 `mergeCommands` / `buildKeymap` 都是先到先得 ——
    于是插件既顶不掉一条同名命令,也抢不走一个已经被内置占着的组合键。
  */
  const pluginCommands = usePluginCommands();
  const commands = useMemo<Command[]>(() => {
    const builtin: Command[] = [];
    if (!quickWindow) {
      builtin.push({
        id: "builtin.openSettings",
        titleKey: "feature.settings",
        icon: "settings",
        accelerator: openSettingsShortcut,
        run: openSettings
      });
    }
    builtin.push({
      id: "builtin.search",
      titleKey: "nav.search",
      icon: "search",
      run: () => setSearchOpen(true)
    });
    return mergeCommands(builtin, quickWindow ? [] : pluginCommands);
  }, [openSettings, openSettingsShortcut, pluginCommands, quickWindow]);
  useCommandShortcuts(commands);

  const inner =
    activeWorkspaceId === null ? null : tabs.stateOf(activeWorkspaceId);
  const activeInner = inner?.tabs.find((t) => t.id === inner.activeTabId);
  const routeSessionId =
    activeInner?.kind === "chat" ? activeInner.ref.sessionId : null;

  /*
    地址栏跟着主区走。`#/{workspaceId}` = 这个工作区,`#/{workspaceId}/{sessionId}`
    = 正在看的那一段会话。

    ★ **只有主区绑定了会话的 chat Tab 才带 sessionId。** 草稿对话还没有 id
    (那正是「新对话不落库」的可见证据)、终端/文档/浏览器压根不是会话 ——
    这几种一律只写工作区那一段。

    ★ **replaceState,不是 pushState。** 这个应用里没有前进/后退入口,push 只会
    在窗口的历史里攒出一串谁也走不回去的条目,还会让 ⌘R 落在中途某一格上。
  */
  useEffect(() => {
    if (activeWorkspaceId === null) return;
    const next =
      `#/${encodeURIComponent(activeWorkspaceId)}` +
      (routeSessionId === null ? "" : `/${encodeURIComponent(routeSessionId)}`);
    if (window.location.hash === next) return;
    window.history.replaceState(null, "", next);
  }, [activeWorkspaceId, routeSessionId]);

  /**
   * 把主区拉回当前工作区 —— **侧边栏下半每一个「带我去这段对话」的动作都得先走这里。**
   *
   * 主区的渲染是三层优先级(见下面 `<main>`):`activeStandaloneFeature`(浏览器页)
   * 盖过外层功能 Tab(定时任务 / Skills / 每日回顾),后者又盖过工作区内容。而会话区
   * 操作的是**内层** Tab —— 两套状态各管各的,谁也不会顺手清掉谁。
   *
   * 于是曾经的表现是:浏览器页开着的时候点左边一条会话,列表那一行**高亮跟着变了**
   * (`activeSessionId` 从内层派生),主区却纹丝不动还是浏览器 —— 看着就像点击丢了。
   * 高亮和主区是同一个意图的两处呈现,不能只动一处。
   *
   * 用 `openWorkspace` 而不是只 `closeStandaloneFeature()`:后者只解开第一层,
   * 停在「定时任务」Tab 上时照样不动。`openWorkspace` 会激活这个工作区的外层 Tab
   * (已存在就复用),两层一起归位。
   */
  const revealWorkspace = async (): Promise<string | null> => {
    if (activeWorkspaceId === null) return null;
    if (activeStandaloneFeature !== null || activeOuter?.kind !== "workspace") {
      if (!(await win.openWorkspace(activeWorkspaceId))) return null;
    }
    const current = useWindowStore.getState();
    return current.activeWorkspaceId === activeWorkspaceId && current.pendingActivation === null
      && current.activeStandaloneFeature === null ? activeWorkspaceId : null;
  };

  /**
   * 带我去某段会话 —— Sidebar 的会话列表和 SearchPalette 的搜索结果共用这一份逻辑
   * (原来是内联在 `onSelectSession` 里的,现在两处都要调,拆出来)。
   */
  const selectSession = async (sessionId: string): Promise<void> => {
    const target = await revealWorkspace();
    if (target === null) return;
    const currentTabs = useTabsStore.getState();
    const t = currentTabs.stateOf(target).tabs.find(
      (x) => x.kind === "chat" && x.ref.sessionId === sessionId,
    );
    if (t !== undefined) currentTabs.activate(target, t.id);
    else {
      const item = sessionItems.find((x) => x.id === sessionId);
      if (quickWindow && currentTabs.stateOf(target).tabs.length === 0) currentTabs.openFocusedSession(target, sessionId, item?.title);
      else currentTabs.openSession(target, sessionId, item?.title);
    }
  };

  const pickLocalWorkspace = async (): Promise<void> => {
    const selected = await pickWorkspace();
    if (!selected) return;
    const state = useWindowStore.getState();
    state.updateWorkspaces([...Object.values(state.workspaceTargets).filter((item) => item.id !== selected.id), selected]);
    await state.openWorkspace(selected.id);
  };

  /**
   * 关掉面板里的一个 Tab。**关掉最后一个 = 收起这个面板。**
   *
   * `tabs.close` 那边只对主区做「关光了补一个空对话」,底部/右侧空掉是合法状态 ——
   * 合法但没意义:一条只剩 `+` 的 Tab 条占着 220px 还什么都不显示。收起来才是
   * 用户的本意。两个 store 的写在同一个事件里,React 批成一次 render,
   * 所以 `useSeedPane` 那边看到的是「已经关了」,不会又补一个回来。
   */

  const closeOuterTab = async (id: string): Promise<void> => {
    const target = useWindowStore.getState().outer.find((tab) => tab.id === id);
    if (target?.kind === 'workspace') {
      if (!(await confirmDocumentChanges(target.ref.workspaceId))) return;
    }
    await win.close(id);
    if (target?.kind === 'workspace' && !useWindowStore.getState().outer.some((tab) => tab.id === id)) {
      useDocumentsStore.getState().release(target.ref.workspaceId);
      // 关掉工作区 Tab 后终端不会再有界面可看,留着只会变成后台孤儿进程。
      void listTerminals(target.ref.workspaceId)
        .then((terminals) => Promise.all(terminals.map((terminal) => killTerminal(terminal.id))))
        .catch(() => undefined);
    }
  };

  /**
   * 「编辑工作区」弹窗里的删除 —— 从**记录**里移除，和「关掉外层 Tab」是两回事
   * (`ipc/workspace.ts` 文件头那条区分)。这里要把两件事按顺序接起来：
   *
   * 1. 这个工作区如果开着外层 Tab（`openWorkspace` 保证最多一张），先走
   *    `closeOuterTab` 收掉它 —— 未保存的文档改动会在这一步弹出确认，
   *    用户选了「取消」的话 Tab 还在，工作区记录也不能删。
   * 2. 记录本身的删除交给 `workspace:close`（`services/app.ts` 的 `closeWorkspace`，
   *    命名撞车是主进程那边历史遗留的，语义见 `ipc/workspace.ts:82` 的注释）。
   *
   * 返回 `false` 时弹窗留在原地并显示错误，而不是假装删除成功。
   */
  const deleteWorkspace = async (id: string): Promise<boolean> => {
    const outerTab = useWindowStore.getState().outer.find((tab) => tab.kind === 'workspace' && tab.ref.workspaceId === id);
    if (outerTab !== undefined) {
      await closeOuterTab(outerTab.id);
      if (useWindowStore.getState().outer.some((tab) => tab.id === outerTab.id)) return false;
    }
    try {
      await closeWorkspace(id);
      return true;
    } catch {
      return false;
    }
  };

  return (
    <div className="app-ground flex h-full bg-app p-2">
      {/*
        ★ 中缝的 8px 从根上的 `gap-2` 挪到了侧边栏自己的 `mr-2`。
        gap 是**父元素**的属性,不会因为孩子宽度变成 0 就消失 —— 留着它的话,
        侧边栏收干净以后主面板左边还硬顶着 8+8=16px,比展开态还靠右,
        收起动画的最后一帧会「顿」一下。宽度和外边距一起归零才是连续的。

        包一层而不是直接给 <aside> 加 transition:里面那层是**定宽 297 的**,
        尺寸动的只有外面这个壳,内容被裁掉、而不是被挤扁 —— 否则 297px 的版式
        (nav、卡片、文字)会在这 280ms 里一路重排,文字换行满天飞。
        `rounded-panel` 也得挂在壳上,不然裁切边在动画期间是方角。
      */}
      {sidebar.mounted && (
        <div
          data-theme-region="sidebar"
          className={cn(
            "flex shrink-0 overflow-hidden rounded-panel",
            "transition-[width,margin-right] duration-280 ease-panel",
            sidebar.shown ? (quickWindow ? "mr-2 w-[240px]" : "mr-2 w-[297px]") : "mr-0 w-0",
          )}
        >
          <Sidebar
            auth={auth}
            focusMode={quickWindow}
            workspace={workspace ?? null}
            chatTabs={inner?.tabs.filter((t) => t.kind === "chat") ?? []}
            sessions={sessionItems}
            activeFeature={
              activeStandaloneFeature ??
              (activeOuter?.kind === "feature" ? activeOuter.ref.feature : null)
            }
            scheduledUnread={scheduledUnread}
            activeSessionId={routeSessionId}
            runningSessionIds={runningSessionIds}
            // ★ `newChat` 不是 `open`:已经有一个没用过的对话就切过去,不再攒一排
            // 一模一样的「新对话」。Tab 条上那颗 `+` 仍走 `open`,它问的是
            // 「再给我一个」—— 见 stores/tabs.ts 的 newChat
            onNewChat={async () => {
              const target = await revealWorkspace();
              if (target !== null) useTabsStore.getState().newChat(target);
            }}
            onSearch={() => {
              if (activeWorkspaceId === null) return;
              setSearchOpen(true);
            }}
            onOpenFeature={win.openFeature}
            // 直接透传:`openSettings(page?)` 的页码被账户菜单的「余额」用着,
            // 包一层 `() => …` 会把那个参数丢掉(齿轮那条路径不需要页码)。
            onOpenSettings={win.openSettings}
            onSelectSession={selectSession}
            onDeleteSession={deleteSession}
            onCollapse={win.toggleSidebar}
          />
        </div>
      )}

      <main data-theme-region="canvas" className="app-canvas flex min-w-0 flex-1 flex-col overflow-hidden rounded-panel bg-canvas">
        {activeStandaloneFeature === "scheduled" ? (
          <FeatureView feature="scheduled" onClose={win.closeStandaloneFeature} />
        ) : activeStandaloneFeature === "browser" ? (
          <BrowserFeature onClose={win.closeStandaloneFeature} />
        ) : activeStandaloneFeature === "extensions" ? (
          <FeatureView feature="extensions" onClose={win.closeStandaloneFeature} />
        ) : activeStandaloneFeature === "git" ? (
          <FeatureView feature="git" onClose={win.closeStandaloneFeature} />
        ) : (
          <>
            {/*
          34px 的条 + `items-end` 让 30px 的 Tab 顶边正好离条顶 4px —— 这两个数是
          量出来的,不是调出来的:参考图三张都是「窗口边 8px、条 8..41、激活 Tab 12..41」。
          原本这里是 38px,Tab 就沉到离条顶 8px,整条 Tab 看着比参考低一截。
          改这个数之前先去量图(scripts/crop.mjs + 竖线扫描),别凭手感。
          侧边栏表头是同一个数,两边必须一起改,否则红绿灯和 Tab 底边错位。
        */}
            <div
              className={cn(
                // ★ `chrome` 不是 `surface`:深色下两者同值,浅色下外层 Tab 条(#e8e4dd)
                // **比侧边栏(#f6f4ef)更暗** —— 量自 docs/image-new。用 surface 会让整条
                // Tab 在浅色主题下浮起来,和参考实现的层次正好相反。
                "app-drag flex h-[34px] shrink-0 items-end gap-1.5 bg-chrome px-2",
                /*
                  ★ Windows/Linux 上自绘的三颗窗口按钮(WindowControls)悬浮在这条的
                  右端 —— 它 portal 到 body、fixed 定位,**不占这条的流**,所以得靠
                  内边距实打实地让出宽度,否则「工作区文件」和「底部面板」两颗开关
                  会被压在按钮下面。宽度那个 128 的推导在 theme.css 的 token 上。
                  macOS 右上角什么都没有,留这块就是个空洞,所以只给非 mac。
                */
                !IS_MAC && "pr-window-controls",
                // 侧边栏收起时红绿灯落到这条上,得给它让出位置。**仅 macOS** ——
                // 别的平台左上角是空的(按钮在右上角,见 WindowControls),
                // 这里再留 78px 就是个空洞。
                // 78 = 参考里按钮盒左边 x86 减去主面板左边 x8(见下面那段量数)
                // 内边距和侧边栏宽度同时同速地走,红绿灯下面才不会先空出一块再被填上
                "transition-[padding-left] duration-280 ease-panel",
                IS_MAC && sidebarCollapsed && "pl-[78px]",
              )}
            >
              {sidebarCollapsed && (
                /*
              ★ 这颗按钮的四个参数全是量出来的,别按手感调 —— 用户就是拿它跟参考对不齐
              提的意见。量 docs/image-new/image.png(收起态):

                y=25 横扫  红灯 x21..32 / 黄灯 x41..52 / 绿灯 x61..72,
                          按钮盒 x86..123 → **宽 38**,底色 #dbd8d1,笔画 #2d4739
                x=104 竖扫 盒子 y11..38 → **高 28**;x=91 处却只有 y14..35,
                          正是 r=14(=半高)的**药丸**轮廓,不是 8px 小圆角方块
                x=26 竖扫  红灯核心 y21..28 → 灯心 ≈ y24.5,而盒心 (11+38)/2 = 24.5
                          → 两者**居中对齐**,不是底对齐

              原先写的是 26×26 方块 + `items-end` + `mb-1`(盒心 y24、宽窄了 12px、
              没有底色),所以看着既偏左又偏小。`active` 也不是可选项:参考里这颗
              在收起态**就是**挖暗+强调色,它在报告「侧边栏现在是收起的」。
              对照组是同一套图里展开态的那颗(image copy 2.png x=274):无底色、
              笔画 #7e7f7e = `icon` —— 证明这套 token 的差别就是「开着 / 没开」。
            */
                <IconButton
                  label={t("nav.expandSidebar")}
                  size={28}
                  width={38}
                  active
                  onClick={win.toggleSidebar}
                  // reveal-delayed:延迟到侧边栏收完再淡入,否则和侧边栏里那颗
                  // 「收起」按钮会同屏出现 280ms —— 它俩是同一个控件的两个位置。
                  className="reveal-delayed self-center rounded-pill"
                >
                  {/* 笔画 1.5 —— 和 Tab 条右端那两颗同一条理由,见 OuterTabBar 那段注释 */}
                  <PanelLeft size={16} strokeWidth={1.5} />
                </IconButton>
              )}
              <OuterTabBar
                tabs={outer}
                activeId={activeOuterId}
                activeWorkspaceId={activeWorkspaceId}
                compact={quickWindow}
                workspaces={workspaces}
                runningWorkspaceIds={runningWorkspaceIds}
                onActivate={win.activate}
                onClose={(id) => { void closeOuterTab(id); }}
                onTogglePin={win.togglePin}
                onMove={win.move}
                onRenameWorkspace={(workspaceId, name) => { void submitWorkspaceRename(workspaceId, name); }}
                onOpenWorkspace={win.openWorkspace}
                onEditWorkspace={setEditWorkspaceId}
                onPickWorkspace={() => { void pickLocalWorkspace(); }}
                onCreateWorkspace={() => { void pickLocalWorkspace(); }}
                onCreateSshWorkspace={() => setCreateSshOpen(true)}
                rightPanelOpen={rightPanelOpen}
                bottomPanelOpen={bottomPanelOpen}
                onToggleRightPanel={() => toggleDockEdge('right')}
                onToggleBottomPanel={() => toggleDockEdge('bottom')}
                updateIndicator={quickWindow ? undefined : <UpdateIndicator />}
              />
            </div>

            {/*
          内容区分成「左列 + 右栏」,底部面板只压在**左列**下面 ——
          和编辑器类应用一致:右侧文件栏是通栏的,终端不该把它顶掉。
        */}
            <div data-theme-region="content" className="flex min-h-0 flex-1">
              {activeOuter?.kind === "feature" ? (
                <FeatureView feature={activeOuter.ref.feature} />
              ) : workspace === undefined || activeWorkspaceId === null ? (
                <div className="flex min-h-0 flex-1 items-center justify-center text-[13px] text-fg-faint">
                  {t("app.openWorkspace")}
                </div>
              ) : (
                <DockRoot workspace={workspace} fallbackModel={{ model: settings.defaultModel, modelProviderId: settings.defaultModelProviderId }} maxOutputTokens={settings.maxOutputTokens} runningSessionIds={runningSessionIds} right={rightEdge} bottom={bottomEdge} />
              )}
            </div>
          </>
        )}
        <ConnectionDialogs workspace={workspace} />
      </main>
      {/*
        ★ 状态栏在 `<main>` **之外**、根 div 之内 —— 它是窗口级的一条,
        不跟着工作区内容区滚动,也不该被 feature 页整条换掉
        (那几页换掉的是 34px 的 Tab 条,不是窗口底边)。
        一格都没有时它自己不渲染,见 `shell/StatusBar.tsx`。
      */}
      {!quickWindow && <StatusBar />}
      {createSshOpen && <CreateSshWorkspaceDialog hidden={settingsPage !== null} onClose={() => setCreateSshOpen(false)} onCreated={async (created) => {
        const state = useWindowStore.getState();
        state.updateWorkspaces([...Object.values(state.workspaceTargets).filter((item) => item.id !== created.id), created]);
        return state.openWorkspace(created.id);
      }} />}
      <DocumentDialogs />
      <OverwriteConfirmDialog />
      {/*
        插件要问用户一句话时弹出来的框(`window.showQuickPick` / `showInputBox` /
        `showConfirm`)。挂在外壳上而不是某一页:发问的可能是任何一个插件,
        而它不知道用户此刻在看哪一屏。
      */}
      <PluginInteractionHost />
      <EditWorkspaceDialog
        workspace={workspaces.find((w) => w.id === editWorkspaceId) ?? null}
        onClose={() => setEditWorkspaceId(null)}
        onDelete={deleteWorkspace}
      />

      <SearchPalette
        open={searchOpen}
        onClose={() => setSearchOpen(false)}
        workspaceId={activeWorkspaceId}
        commands={commands}
        onSelectSession={(sessionId) => { void selectSession(sessionId); }}
      />

      {/*
        ★ 渲染在根 div **之内**,不 portal —— 见 SettingsOverlay 文件头:
        portal 到 body 下就够不着 `.app-no-drag`,浮层压住标题栏的那一条会被
        OS 吞掉 pointer 事件。
      */}
      <SettingsOverlay
        open={settingsPage !== null}
        page={settingsPage ?? DEFAULT_SETTINGS_PAGE}
        settings={settings}
        versions={versions}
        auth={auth}
        onNavigate={win.openSettings}
        onClose={win.closeSettings}
      />

      {/*
        奖励中心（账户菜单 →「邀请好友」）。和设置浮层同处一层、同样不 portal ——
        理由见 `RewardsOverlay.tsx` 文件头：它同样盖住那条 `app-drag` 的标题栏。
      */}
      <RewardsOverlay open={rewardsOpen} onClose={win.closeRewards} />
    </div>
  );
}
