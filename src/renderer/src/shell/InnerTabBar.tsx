/**
 * 内层 Tab 条 —— **同一个组件供三条 Tab 条使用**:主区那条、底部面板那条、
 * 右侧面板那条。
 *
 * ★ 底部不是「终端面板」,右侧也不是「文件面板」,它们各是一条内层 Tab 条
 * (见 shared/domain/tab.ts 的 `InnerTabBase.pane`)。参考实现里三处的 `+` 菜单
 * 是同一套东西的三个子集,右侧那颗 `+` 的 tooltip 甚至直接写着「添加右侧工作台标签」。
 * 所以差异全被收进 `menu` 和 `trailing` 两个参数,而不是抄三份组件 ——
 * 抄的代价是:三条 Tab 条的悬停态、关闭按钮、拖动手感会慢慢长歪。
 *
 * 形状和外层**故意不一样**:外层是浏览器式舌头(和内容面板连成一片),
 * 内层是**药丸**(激活的那颗底色 `tint`:新版参考实现深色量到 #2b2e2d,
 * 比 canvas #1e2020 **亮**,是凸出来的;浅色反过来是凹下去的,见 `theme.css` §4)。
 * 两层 Tab 长得一样的话,「这个 Tab 属于哪一层」就只能靠位置猜。
 *
 * 这一条不在 `.app-drag` 区里,所以不需要逐个 `.app-no-drag` ——
 * 但拖动重排用的是同一个 hook,行为和外层一致。
 */
import {
  ArrowLeftToLine,
  ArrowRightToLine,
  ChevronDown,
  ListX,
  Pencil,
  Plus,
  SquareX,
  X,
} from "lucide-react";
import {
  Fragment,
  useEffect,
  useRef,
  useState,
  type ReactNode,
} from "react";
import type { InnerTab } from "../../../shared/domain/tab";
import { paneOf } from "../../../shared/domain/tab";
import {
  needsSeparator,
  type MergedMenu,
  type TabMenuItem,
} from "../../../shared/plugin/contribution";
import {
  Menu,
  MenuItem,
  MenuLabel,
  MenuSeparator,
} from "../components/ui/Menu";
import { prettyAccelerator } from "../lib/accelerator";
import { cn } from "../lib/cn";
import { INNER_TAB_ICON, MENU_ICON } from "./icons";
import { useDragReorder } from "./useDragReorder";
import { ContextMenu, type ContextMenuPosition } from "../components/ui/ContextMenu";
import { TabRenameInput } from "./TabRenameInput";
import { tabRenameTarget } from "./tab-rename";
import { useI18n, type TranslationKey } from "../i18n";
import { documentKey, isDocumentDirty, useDocumentsStore } from '../stores/documents';
import { DOCK_TAB_MIME } from './dock-layout';
import { Spinner } from '../components/ui/Spinner'
import { useAttentionSessionIds } from '../stores/session'

function revealTab(strip: HTMLDivElement | null, id: string | null): void {
  if (strip === null || id === null) return;
  const activeTab = [...strip.querySelectorAll<HTMLElement>("[data-inner-tab-id]")]
    .find((tab) => tab.dataset.innerTabId === id);
  activeTab?.scrollIntoView?.({ block: "nearest", inline: "nearest" });
}

/**
 * `+` 菜单项的图标。带 `iconUrl`(插件 `iconFile` 经主进程转出的 data URL)时
 * 画品牌图,否则走名字闭集查 lucide。
 *
 * ★ `<img>` 是**非脚本上下文**,svg 里的脚本不会执行;尺寸钉在 14px,与
 * lucide 同框不突兀。改名/主题换色都不影响它 —— 品牌图本来就是彩色的。
 */
function menuIcon(item: TabMenuItem): ReactNode {
  if (item.iconUrl !== undefined) {
    return <img src={item.iconUrl} alt="" draggable={false} className="size-[14px] rounded-[3px] object-contain" />;
  }
  const Icon = MENU_ICON[item.icon];
  return <Icon size={14} />;
}

export function InnerTabBar({
  tabs,
  groupId,
  workspaceId,
  canDragTab,
  activeId,
  runningSessionIds,
  menu,
  trailing,
  className,
  onActivate,
  onClose,
  onMove,
  onOpen,
  onRename,
}: {
  tabs: readonly InnerTab[];
  groupId?: string;
  workspaceId?: string;
  canDragTab?: (tab: InnerTab) => boolean;
  activeId: string | null;
  runningSessionIds: ReadonlySet<string>;
  /**
   * `+` 菜单的内容 —— 内置项与插件贡献项**已经合并好**(见 `shell/tab-menu.ts`)。
   * 三条 Tab 条共用这一个组件,差异全在这个参数里。
   */
  menu: MergedMenu;
  /** 条右端那个按钮:主区是「全部标签页」,底部是「关闭面板」 */
  trailing?: ReactNode;
  className?: string;
  onActivate: (id: string) => void;
  onClose: (id: string) => void;
  /** 下标是**本条内**的下标 —— 两条 Tab 条共用一张表,见 reorderInPane */
  onMove: (from: number, to: number) => void;
  /**
   * ★ 收到的是**整个菜单项**,不是一个 `kind`。
   *
   * 原来这里是 `onOpen(kind)`,于是 `kind` 同时是身份、图标键和动作 ——
   * 插件项没有 `InnerTabKind`,三处全卡死。改成传项之后,调用方按
   * `item.action` 分发:内置是开一个 Tab,插件是执行它自己的命令。
   */
  onOpen: (item: TabMenuItem) => void;
  /**
   * 双击 / 右键「重命名」的提交口。
   *
   * ★ **可选**:这个组件被三条 Tab 条共用,而 `shell/__tests__` 里的几处
   * 快照式渲染并不关心改名。不给就没有改名入口,双击退化成两次单击。
   */
  onRename?: (tab: InnerTab, value: string) => void;
}): ReactNode {
  const { t } = useI18n();
  const drafts = useDocumentsStore((state) => state.entries);
  // 没在看的会话停下来等审批 / 等回答:应用内标一下,不强制切过去
  const attentionSessionIds = useAttentionSessionIds();
  const { dragging, onPointerDown, styleFor } = useDragReorder(onMove);
  const mainChatCount = tabs.filter((tab) => tab.kind === 'chat' && paneOf(tab) === 'main').length;
  const stripRef = useRef<HTMLDivElement>(null);
  const activeIdRef = useRef(activeId);
  activeIdRef.current = activeId;
  const [scrollEdges, setScrollEdges] = useState({ left: false, right: false });
  /*
    ★ 编辑态提升到**条**这一层,不是每个 Tab 各存一个:同一时刻只能有一个
    标签在改名,分散存的话点第二个标签时第一个的输入框还留在那里,两个都
    focus 不到,看起来像卡住了。
  */
  const [editingId, setEditingId] = useState<string | null>(null);
  const [contextMenu, setContextMenu] = useState<{ tabId: string; position: ContextMenuPosition } | null>(null);
  const contextTab = tabs.find((tab) => tab.id === contextMenu?.tabId);
  const canRename = (tab: InnerTab): boolean => onRename !== undefined && tabRenameTarget(tab) !== null;
  /*
    ★ 批量关闭复用**同一个** `onClose` 逐个关 —— 它已经带着 doc/preview 的保存确认、
    Dock 清理,以及「主区最后一个对话不可关」那道护栏(见 stores/tabs.ts 的 closeDockTab)。
    自己在这里另写一套关闭,那三样迟早会漏。
  */
  const closeMany = (targets: readonly InnerTab[]): void => {
    for (const target of targets) onClose(target.id);
  };

  useEffect(() => {
    const strip = stripRef.current;
    if (strip === null) return;

    let frame = 0;
    const measure = (): void => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => {
        const maxScrollLeft = Math.max(0, strip.scrollWidth - strip.clientWidth);
        const next = {
          left: strip.scrollLeft > 1,
          right: strip.scrollLeft < maxScrollLeft - 1,
        };
        setScrollEdges((current) =>
          current.left === next.left && current.right === next.right ? current : next,
        );
      });
    };
    const scrollHorizontally = (event: WheelEvent): void => {
      if (event.ctrlKey || Math.abs(event.deltaX) >= Math.abs(event.deltaY) || event.deltaY === 0) return;
      if (strip.scrollWidth <= strip.clientWidth) return;
      const scale = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? strip.clientWidth : 1;
      const before = strip.scrollLeft;
      strip.scrollLeft += event.deltaY * scale;
      if (strip.scrollLeft !== before) event.preventDefault();
    };
    const layoutChanged = (): void => {
      revealTab(strip, activeIdRef.current);
      measure();
    };

    layoutChanged();
    const resizeObserver = new ResizeObserver(layoutChanged);
    resizeObserver.observe(strip);
    const mutationObserver = new MutationObserver(layoutChanged);
    mutationObserver.observe(strip, { childList: true, subtree: true, characterData: true });
    strip.addEventListener("scroll", measure, { passive: true });
    strip.addEventListener("wheel", scrollHorizontally, { passive: false });
    window.addEventListener("resize", layoutChanged);
    return () => {
      cancelAnimationFrame(frame);
      resizeObserver.disconnect();
      mutationObserver.disconnect();
      strip.removeEventListener("scroll", measure);
      strip.removeEventListener("wheel", scrollHorizontally);
      window.removeEventListener("resize", layoutChanged);
    };
  }, []);

  useEffect(() => revealTab(stripRef.current, activeId), [activeId]);

  return (
    <div
      data-dock-tabbar={groupId}
      className={cn(
        "flex h-10 shrink-0 items-center gap-1 border-b border-hairline px-2",
        className,
      )}
    >
      <div
        ref={stripRef}
        className={cn(
          "tab-strip-scroll flex min-w-0 flex-1 items-center gap-1 overflow-x-auto overflow-y-hidden",
          scrollEdges.left && scrollEdges.right
            ? "tab-strip-fade-both"
            : scrollEdges.left
              ? "tab-strip-fade-left"
              : scrollEdges.right && "tab-strip-fade-right",
        )}
      >
        {tabs.map((tab, i) => {
          const active = tab.id === activeId;
          const running =
            tab.kind === "chat" &&
            tab.ref.sessionId !== null &&
            runningSessionIds.has(tab.ref.sessionId);
          const waiting =
            tab.kind === "chat" &&
            tab.ref.sessionId !== null &&
            attentionSessionIds.has(tab.ref.sessionId);
          const Icon = INNER_TAB_ICON[tab.kind];
          const draft = workspaceId && (tab.kind === 'doc' || tab.kind === 'preview') ? drafts[documentKey(workspaceId, tab.ref.path)] : undefined;
          const dirty = draft !== undefined && isDocumentDirty(draft);
          const closeDisabled = tab.kind === 'chat' && paneOf(tab) === 'main' && mainChatCount <= 1;
          const editing = tab.id === editingId;
          // 文件类标签改的是盘上的文件名 —— 默认选区要跳过扩展名
          const selectStem = tabRenameTarget(tab)?.kind === 'file';
          return (
            <div
              key={tab.id}
              data-inner-tab-id={tab.id}
              draggable={editing !== true && groupId !== undefined && (canDragTab?.(tab) ?? true)}
              data-dock-tab-id={groupId === undefined ? undefined : tab.id}
              onDragStart={(event) => {
                if (groupId === undefined || (canDragTab !== undefined && !canDragTab(tab))) return;
                event.dataTransfer.setData(DOCK_TAB_MIME, JSON.stringify({ tabId: tab.id, groupId, workspaceId }));
                event.dataTransfer.effectAllowed = 'move';
              }}
              style={styleFor(i)}
              // Dock 分组使用原生拖放来同时支持同组排序、跨组移动和边缘拆分。
              // 外层 Tab 仍使用 pointer reorder，因为它位于 Electron 自绘标题栏中。
              onPointerDown={(e) => {
                if (editing) return;
                if (groupId === undefined) onPointerDown(e, i);
              }}
              onClick={(e) => {
                if (editing) return;
                onActivate(tab.id);
                // ★ `e.detail >= 2` 判双击,**不用 200~300ms 计时器消歧** ——
                // 计时器会给每一次单击都压上一段延迟,而本仓库明确拒绝过那个做法
                // (同 `components/ui/Menu.tsx`)。第一击照常激活,第二击再进编辑态。
                if (e.detail >= 2 && canRename(tab)) setEditingId(tab.id);
              }}
              onContextMenu={(event) => {
                event.preventDefault();
                setContextMenu({ tabId: tab.id, position: { x: event.clientX, y: event.clientY } });
              }}
              role="tab"
              aria-selected={active}
              title={tab.title}
              className={cn(
                "app-no-drag group flex h-7 max-w-[190px] min-w-0 shrink-0 items-center gap-1.5 rounded-[8px]",
                "pr-1 pl-2.5 text-[12.5px] select-none",
                !dragging && "transition-[transform,background-color]",
                active
                  ? "bg-tint text-fg"
                  : "text-fg-muted hover:bg-tint/50 hover:text-fg",
              )}
            >
              <Icon size={13} className="shrink-0 text-fg-faint" />
              {editing ? (
                <TabRenameInput
                  initial={tab.title}
                  ariaLabel={t("nav.renameTab")}
                  selectStem={selectStem}
                  onSubmit={(value) => {
                    setEditingId(null);
                    onRename?.(tab, value);
                  }}
                  onCancel={() => setEditingId(null)}
                />
              ) : (
                <span className="min-w-0 flex-1 truncate">{tab.title}</span>
              )}
              {dirty && <span title={t('document.unsaved')} aria-label={t('document.unsaved')} className="size-1.5 shrink-0 rounded-full bg-accent" />}
              {waiting ? (
                <span title={t('chat.pendingInteraction')} aria-label={t('chat.pendingInteraction')} className="size-1.5 shrink-0 rounded-full bg-warning" />
              ) : running && (
                <Spinner size="xs" label={t('chat.taskChecklistRunning')} className="text-accent" />
              )}
              <button
                type="button"
                disabled={closeDisabled}
                aria-label={t("nav.closeTab", { label: tab.title })}
                onPointerDown={(e) => e.stopPropagation()}
                onClick={(e) => {
                  e.stopPropagation();
                  onClose(tab.id);
                }}
                className={cn(
                  "flex size-[17px] shrink-0 items-center justify-center rounded-[5px]",
                  "text-fg-faint opacity-0 transition-opacity group-hover:opacity-100",
                  "hover:bg-tint-strong hover:text-fg focus-visible:opacity-100 disabled:pointer-events-none disabled:opacity-0",
                )}
              >
                <X size={11} />
              </button>
            </div>
          );
        })}
      </div>

      <Menu
        className="shrink-0"
        label={t("nav.newTab")}
        width={230}
        trigger={<Plus size={15} />}
        triggerClassName="flex size-7 items-center justify-center rounded-[8px] text-icon transition-colors hover:bg-tint-hover hover:text-fg"
      >
        {(close) => (
          <>
            {menu.items.map((item, index) => (
              <Fragment key={item.id}>
                {/*
                  ★ 分隔线由 **group 边界自动生成**,不是数据里的 `separatorBefore`,
                  更不是渲染时的 `i === 3`。插件项落在哪个 group 就跟着哪条边界走,
                  加一项不需要手工挪任何标记。
                */}
                {needsSeparator(menu.items[index - 1], item) && <MenuSeparator />}
                <MenuItem
                  icon={menuIcon(item)}
                  accelerator={prettyAccelerator(item.accelerator)}
                  onSelect={() => {
                    onOpen(item);
                    close();
                  }}
                >
                  {t(item.titleKey as TranslationKey)}
                </MenuItem>
              </Fragment>
            ))}
            {/*
              单插件超过 3 项时折叠。**不丢弃** —— 丢弃意味着作者写了 5 项、
              装上只看见 3 项,而没有任何地方告诉他另外两项去哪了。
            */}
            {menu.overflow.map((group) => (
              <Fragment key={group.pluginId}>
                <MenuSeparator />
                <MenuLabel>{group.pluginId}</MenuLabel>
                {group.items.map((item) => (
                  <MenuItem
                    key={item.id}
                    icon={menuIcon(item)}
                    onSelect={() => {
                      onOpen(item);
                      close();
                    }}
                  >
                    {t(item.titleKey as TranslationKey)}
                  </MenuItem>
                ))}
              </Fragment>
            ))}
          </>
        )}
      </Menu>

      {trailing}

      {/*
        ★ 每一条双击路径都配一个**键盘可达**的入口。双击是鼠标独有的动作,
        只给双击等于这个功能对键盘和辅助技术用户不存在。
      */}
      {contextMenu !== null && contextTab !== undefined && (() => {
        // 这几条「关闭右侧 / 左侧」按**本条 Tab 的显示顺序**算,所以要拿到右键那颗
        // 在条里的下标;条 = 一个 Dock 组,tabs 已经是这一组的顺序视图。
        const idx = tabs.findIndex((tab) => tab.id === contextTab.id);
        // 和右上角那颗 `×` 同一条判据:主区剩最后一个对话时不给关。
        const closeSelfDisabled =
          contextTab.kind === "chat" && paneOf(contextTab) === "main" && mainChatCount <= 1;
        const others = tabs.filter((tab) => tab.id !== contextTab.id);
        const toRight = idx < 0 ? [] : tabs.slice(idx + 1);
        const toLeft = idx <= 0 ? [] : tabs.slice(0, idx);
        return (
          <ContextMenu
            position={contextMenu.position}
            label={t("nav.tabContextMenu")}
            onClose={() => setContextMenu(null)}
          >
            {(close) => (
              <>
                {canRename(contextTab) && (
                  <>
                    <MenuItem
                      icon={<Pencil size={14} />}
                      onSelect={() => {
                        setEditingId(contextTab.id);
                        close();
                      }}
                    >
                      {t("nav.renameTab")}
                    </MenuItem>
                    <MenuSeparator />
                  </>
                )}
                <MenuItem
                  icon={<X size={14} />}
                  disabled={closeSelfDisabled}
                  onSelect={() => {
                    onClose(contextTab.id);
                    close();
                  }}
                >
                  {t("nav.closeThisTab")}
                </MenuItem>
                <MenuItem
                  icon={<SquareX size={14} />}
                  disabled={others.length === 0}
                  onSelect={() => {
                    closeMany(others);
                    close();
                  }}
                >
                  {t("nav.closeOtherTabs")}
                </MenuItem>
                <MenuItem
                  icon={<ArrowRightToLine size={14} />}
                  disabled={toRight.length === 0}
                  onSelect={() => {
                    closeMany(toRight);
                    close();
                  }}
                >
                  {t("nav.closeTabsToRight")}
                </MenuItem>
                <MenuItem
                  icon={<ArrowLeftToLine size={14} />}
                  disabled={toLeft.length === 0}
                  onSelect={() => {
                    closeMany(toLeft);
                    close();
                  }}
                >
                  {t("nav.closeTabsToLeft")}
                </MenuItem>
                <MenuSeparator />
                <MenuItem
                  icon={<ListX size={14} />}
                  disabled={tabs.length === 0}
                  onSelect={() => {
                    closeMany(tabs);
                    close();
                  }}
                >
                  {t("nav.closeAllTabs")}
                </MenuItem>
              </>
            )}
          </ContextMenu>
        );
      })()}
    </div>
  );
}

/**
 * 主区那条 Tab 条**最右端**的 `⌄`(参考截图 2 里就在条的尽头,和 `+` 分列两端)。
 *
 * 它不是装饰。Tab 是 `max-w-[190px]` 的药丸且 `shrink-0`,开到七八个就会被推出
 * 可视区 —— 那时候这颗菜单是唯一还能切过去的入口。所以它列的是**本条的全部 Tab**,
 * 不是「最近打开的文件」之类另一份数据:一旦两者能不一致,这颗按钮就没用了。
 *
 * `align="end"` 是必须的:它贴着条的右边,菜单往左展开才不会溢出窗口。
 */
export function AllTabsMenu({
  tabs,
  activeId,
  onActivate,
}: {
  tabs: readonly InnerTab[];
  activeId: string | null;
  onActivate: (id: string) => void;
}): ReactNode {
  const { t } = useI18n();
  return (
    <Menu
      className="shrink-0"
      label={t("nav.allTabs")}
      width={240}
      align="end"
      trigger={<ChevronDown size={15} />}
      triggerClassName="flex size-[26px] items-center justify-center rounded-[8px] text-icon transition-colors hover:bg-tint-hover hover:text-fg"
    >
      {(close) => (
        <>
          {tabs.length === 0 && <MenuLabel>{t("nav.noTabs")}</MenuLabel>}
          {tabs.map((tab) => {
            const Icon = INNER_TAB_ICON[tab.kind];
            return (
              <MenuItem
                key={tab.id}
                icon={<Icon size={14} />}
                // 三态里只用两态:`checked` 给 undefined 的话勾位不占地方,
                // 激活项一勾整列就横向跳一下
                checked={tab.id === activeId}
                onSelect={() => {
                  onActivate(tab.id);
                  close();
                }}
              >
                {tab.title}
              </MenuItem>
            );
          })}
        </>
      )}
    </Menu>
  );
}
