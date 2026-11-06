/**
 * 外层 Tab 条 —— 工作区 Tab 与功能 Tab 混排(方案 §8)。
 *
 * 形状照截图 09e55765 / docs/image-new:**浏览器式标签** —— 激活的那个是 `bg-canvas`,
 * 和它下面的内容面板同色、上圆角、无下边框,读起来是「这张标签就是下面那一页的舌头」。
 * 未激活的**没有底色**,直接落在 `bg-chrome` 的条上,只有文字变灰。
 * (量自 docs/image-new/image.png:激活 Tab x752..870 是 #faf9f5 = canvas,
 *  未激活那几张所在处一律是 #e8e4dd = chrome,没有任何中间色块。)
 *
 * ⚠️ 整条落在 `.app-drag` 里(两个平台的自绘标题栏)。**每个可点元素都必须
 * `.app-no-drag`**,否则 OS 吞掉 pointer 事件,表现是「Tab 拖不动,整个窗口跟着鼠标跑」。
 * 留给窗口拖动的只有 Tab **之间和右侧**的空白。
 */
import { ChevronDown, Folder, PanelBottom, PanelRight, Pencil, Pin, PinOff, Plus, Server, X } from "lucide-react";
import { isLocalEnvironment } from '../../../shared/domain/environment';
import { useEffect, useRef, useState, type ReactNode } from "react";
import type { FeatureKind, OuterTab } from "../../../shared/domain/tab";
import type { Workspace } from "../../../shared/domain/workspace";
import { ContextMenu, type ContextMenuPosition } from "../components/ui/ContextMenu";
import { IconButton } from "../components/ui/IconButton";
import { Menu, MenuItem } from "../components/ui/Menu";
import { cn } from "../lib/cn";
import { FEATURE_ICON } from "./icons";
import { useDragReorder } from "./useDragReorder";
import { TabRenameInput } from "./TabRenameInput";
import { useI18n, type Translate } from "../i18n";
import { Spinner } from '../components/ui/Spinner'
import { Tabs } from "radix-ui";
import { WorkspacePickerDialog } from './WorkspacePickerDialog'

function featureLabel(t: Translate, feature: FeatureKind): string {
  return t(`view.feature.${feature}` as Parameters<Translate>[0]);
}

export function OuterTabBar({
  tabs,
  activeId,
  activeWorkspaceId,
  compact = false,
  workspaces,
  runningWorkspaceIds,
  onActivate,
  onClose,
  onTogglePin,
  onMove,
  onRenameWorkspace,
  onOpenWorkspace,
  onEditWorkspace,
  onPickWorkspace,
  onCreateWorkspace,
  onCreateSshWorkspace,
  rightPanelOpen,
  bottomPanelOpen,
  onToggleRightPanel,
  onToggleBottomPanel,
  updateIndicator,
}: {
  tabs: readonly OuterTab[];
  activeId: string | null;
  /** 当前工作区，即使当前激活的是功能 Tab 也要让切换弹窗标出它。 */
  activeWorkspaceId?: string | null;
  /** 专注会话窗口只展示当前项目，不渲染工作区 Tab 列表与切换入口。 */
  compact?: boolean;
  workspaces: readonly Workspace[];
  /** 有 run 在跑的工作区 —— Tab 上那个小圆点。数据源是 RunRegistry 聚合,不是任何 UI 状态 */
  runningWorkspaceIds: ReadonlySet<string>;
  onActivate: (id: string) => void;
  onClose: (id: string) => void;
  onTogglePin: (id: string) => void;
  onMove: (from: number, to: number) => void;
  /**
   * 双击 / 右键「重命名工作区」的提交口。
   *
   * ★ **可选**:`settings/theme-studio/DesktopPreview.tsx` 也渲染这个组件当静态
   * 示意图,那里一个真工作区都没有。不给就没有改名入口。
   *
   * ★★ 只对 `kind === 'workspace'` 的 Tab 开放 —— 功能 Tab(设置/扩展…)的标题
   * 来自翻译 key,改了在另一种语言下就对不上,永远不可改名。
   */
  onRenameWorkspace?: (workspaceId: string, name: string) => void;
  onOpenWorkspace: (workspaceId: string) => void;
  /** 工作区切换弹窗里每个卡片悬停出现的铅笔 —— 打开 `EditWorkspaceDialog`（名称/默认模型/删除）。 */
  onEditWorkspace: (workspaceId: string) => void;
  onPickWorkspace: () => void;
  onCreateWorkspace: () => void;
  onCreateSshWorkspace: () => void;
  rightPanelOpen: boolean;
  bottomPanelOpen: boolean;
  onToggleRightPanel: () => void;
  onToggleBottomPanel: () => void;
  /**
   * 插在右端那组开关左边的东西 —— 目前只有更新指示器。
   *
   * 走 slot 而不是让这里自己 `<UpdateIndicator />`,是因为主题工作室的
   * `DesktopPreview` 也渲染这条 Tab 条:它是一张**静态示意图**,不该因为恰好
   * 有个新版本就在预览里多冒出一颗图标。这条 Tab 条其余部分全是 props 驱动的,
   * 让它自己去订阅 IPC 会是这里唯一一处有外部状态的地方。
   */
  updateIndicator?: ReactNode;
}): ReactNode {
  const { t } = useI18n();
  const { dragging, onPointerDown, styleFor } = useDragReorder(onMove);
  const [contextMenu, setContextMenu] = useState<{ tabId: string; position: ContextMenuPosition } | null>(null);
  const [hiddenTabIds, setHiddenTabIds] = useState<readonly string[]>([]);
  const [workspacePickerOpen, setWorkspacePickerOpen] = useState(false);
  const stripRef = useRef<HTMLDivElement>(null);
  const contextTab = contextMenu === null ? undefined : tabs.find((tab) => tab.id === contextMenu.tabId);

  useEffect(() => {
    const strip = stripRef.current;
    if (strip === null) return;

    let frame = 0;
    const measure = (): void => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => {
        const bounds = strip.getBoundingClientRect();
        const left = bounds.left;
        const right = bounds.left + strip.clientWidth;
        const hidden = [...strip.querySelectorAll<HTMLElement>('[data-outer-tab-id]')]
          .filter((tab) => {
            const box = tab.getBoundingClientRect();
            return box.left < left - 1 || box.right > right + 1;
          })
          .map((tab) => tab.dataset.outerTabId ?? '')
          .filter((id) => id !== '');
        setHiddenTabIds((current) => current.length === hidden.length && current.every((id, index) => id === hidden[index]) ? current : hidden);
      });
    };

    measure();
    const resizeObserver = new ResizeObserver(measure);
    resizeObserver.observe(strip);
    const mutationObserver = new MutationObserver(measure);
    mutationObserver.observe(strip, { childList: true, subtree: true, characterData: true });
    strip.addEventListener('scroll', measure, { passive: true });
    window.addEventListener('resize', measure);
    return () => {
      cancelAnimationFrame(frame);
      resizeObserver.disconnect();
      mutationObserver.disconnect();
      strip.removeEventListener('scroll', measure);
      window.removeEventListener('resize', measure);
    };
  }, [tabs, workspaces]);

  /* 编辑态提升到条这一层:同一时刻只有一个 Tab 在改名(同 InnerTabBar) */
  const [editingId, setEditingId] = useState<string | null>(null);
  const canRename = (tab: OuterTab): boolean => onRenameWorkspace !== undefined && tab.kind === "workspace";
  const compactWorkspace = activeWorkspaceId === null || activeWorkspaceId === undefined
    ? undefined
    : workspaces.find((workspace) => workspace.id === activeWorkspaceId);
  const CompactIcon = compactWorkspace === undefined || isLocalEnvironment(compactWorkspace.environment) ? Folder : Server;
  const compactLabel = compactWorkspace?.name ?? t("nav.unknownWorkspace");

  return (
    /*
      ★ `self-stretch` 是**几何要件**,不是随手加的。没有它,这个根 div 的交叉轴尺寸
      = 内容高 = 30px(最高的是 Tab),而不是外面那条 34px。于是右端那两颗面板开关的
      `self-center` 就居中在 30px 里,落到窗口 y13..40、盒心 y27 ——
      比左上角那颗「展开侧边栏」和 Windows 那三颗窗口按钮(都是盒心 y25)**低 2px**。
      量参考图 docs/image-new/image copy.png 也是 y25:
        x=997 竖扫 → 药丸底 #dbd8d1 起于 y11 止于 y38,(11+39)/2 = 25
        x=104  竖扫(image.png 收起态的展开键)→ 同样 y11..38
      拉伸到 34px 后 `items-end` 照旧把 Tab / `+` / 拖动空白压在底边(rel y4..34,
      和以前逐像素相同),只有 `self-center` 的那一组挪回真正的条心。
    */
    <div className="flex min-w-0 flex-1 self-stretch items-end gap-0.5">
      {compact ? (
        <div
          className="flex h-[30px] max-w-[55%] min-w-0 items-center gap-2 self-center px-2 text-[13px] text-fg"
          title={compactWorkspace?.rootPath ?? compactLabel}
        >
          <CompactIcon size={13} className="shrink-0 text-icon" />
          <span className="min-w-0 truncate">{compactLabel}</span>
        </div>
      ) : (
        <>
      {/*
        Keep the + trigger outside the scrollable tab list. Previously it shared the
        same non-wrapping flex row as every tab, so a full strip let the last tab's
        close button overlap the trigger. The list can now scroll while + always has
        its own fixed hit target.
      */}
      {/*
        ★ 键盘操作交给 Radix 的 Tabs(只用 Root / List / Trigger,不用 Content):
        tablist 语义、只有一个 Tab 在 Tab 键序里(roving tabindex)、←/→/Home/End 移动焦点,
        都是它的。`activationMode="manual"`:方向键只挪焦点,Enter / 空格才切过去 ——
        自动激活的话,用方向键扫过一排工作区就会依次把它们全部打开一遍。
        Delete 关闭、F2 改名、菜单键开右键菜单是这里补的(`onTabKeyDown`)。
      */}
      <Tabs.Root asChild value={activeId ?? ""} onValueChange={onActivate} activationMode="manual">
      <div className="flex min-w-0 flex-initial">
      <Tabs.List
        ref={stripRef}
        aria-label={t("nav.allTabs")}
        className="app-no-drag tab-strip-scroll flex w-max min-w-0 max-w-full flex-initial items-end gap-0.5 overflow-x-auto overflow-y-hidden"
      >
        {tabs.map((tab, i) => {
        const active = tab.id === activeId;
        const running =
          tab.kind === "workspace" &&
          runningWorkspaceIds.has(tab.ref.workspaceId);
        const ws =
          tab.kind === "workspace"
            ? workspaces.find((w) => w.id === tab.ref.workspaceId)
            : undefined;
        const label =
          tab.kind === "workspace"
            ? (ws?.name ?? t("nav.unknownWorkspace"))
            : featureLabel(t, tab.ref.feature);
        const Icon =
          tab.kind === "workspace" ? (isLocalEnvironment(ws?.environment) ? Folder : Server) : FEATURE_ICON[tab.ref.feature];
        // 分隔线只画在两张未激活的 Tab 之间:挨着激活的那张时,舌头自己就是分界
        const separator = !active && i < tabs.length - 1 && tabs[i + 1]?.id !== activeId;
        const close = (): void => {
          onClose(tab.id);
          // 键盘关掉之后焦点别掉回 body:落到原位置上的那张(没有就是前一张)
          requestAnimationFrame(() => {
            const remaining = stripRef.current?.querySelectorAll<HTMLElement>("[data-outer-tab-id]");
            if (remaining === undefined || remaining.length === 0) return;
            remaining[Math.min(i, remaining.length - 1)]?.focus();
          });
        };

          return (
          <Tabs.Trigger key={tab.id} value={tab.id} asChild>
          <div
            data-outer-tab-id={tab.id}
            style={styleFor(i)}
            onPointerDown={(e) => {
              // 编辑中不接拖排序 —— 否则在输入框里拖选文字会把整张 Tab 拖走
              if (tab.id === editingId) return;
              onPointerDown(e, i);
            }}
            onClick={(e) => {
              if (tab.id === editingId) return;
              onActivate(tab.id);
              // `e.detail >= 2` 判双击,理由同 InnerTabBar(不用计时器消歧)
              if (e.detail >= 2 && canRename(tab)) setEditingId(tab.id);
            }}
            onKeyDown={(e) => {
              // 改名输入框里的按键会冒泡上来,那些不归这里管
              if (e.target !== e.currentTarget) return;
              if (e.key === "Delete" || e.key === "Backspace") {
                e.preventDefault();
                close();
              } else if (e.key === "F2" && canRename(tab)) {
                e.preventDefault();
                setEditingId(tab.id);
              } else if (e.key === "ContextMenu" || (e.key === "F10" && e.shiftKey)) {
                e.preventDefault();
                const box = e.currentTarget.getBoundingClientRect();
                setContextMenu({ tabId: tab.id, position: { x: box.left, y: box.bottom } });
              }
            }}
            onContextMenu={(event) => {
              event.preventDefault();
              setContextMenu({ tabId: tab.id, position: { x: event.clientX, y: event.clientY } });
            }}
            title={ws?.rootPath ?? label}
            className={cn(
              "app-no-drag group relative flex h-[30px] max-w-[200px] min-w-0 shrink-0 items-center",
              "gap-1.5 rounded-t-[10px] text-[13px] select-none",
              // Tab 在横向滚动的容器里,上下会被裁:键盘焦点框往里收,不然只剩两条竖边
              "[--focus-outline-offset:-2px]",
              !dragging && "transition-[transform,background-color]",
              /*
                `--tab-bg` 是这张 Tab 此刻**不透明**的底色。悬停出现的关闭按钮压在文字尾巴上,
                它左侧那段渐隐要渐到这个颜色才看不出接缝 —— 所以悬停色不能直接用
                半透明的 `tint-hover/60`,而是先和条底 `chrome` 混成实色。
              */
              active
                ? "bg-canvas pr-1.5 pl-3 text-fg [--tab-bg:var(--color-canvas)]"
                : cn(
                    "px-3 text-fg-muted [--tab-bg:var(--color-chrome)] hover:text-fg",
                    "hover:bg-(--tab-bg) hover:[--tab-bg:color-mix(in_oklab,var(--color-tint-hover)_60%,var(--color-chrome))]",
                  ),
              /*
                激活 Tab 底部两侧的反向圆角:舌头往两边弯出去,和下面的画布连成一体,
                而不是一个方块直直插进内容区。用画布色的径向渐变挖出四分之一圆。
              */
              active && [
                "before:pointer-events-none before:absolute before:bottom-0 before:-left-2.5 before:size-2.5",
                "before:bg-[radial-gradient(circle_at_0_0,transparent_10px,var(--color-canvas)_10.5px)]",
                "after:pointer-events-none after:absolute after:-right-2.5 after:bottom-0 after:size-2.5",
                "after:bg-[radial-gradient(circle_at_100%_0,transparent_10px,var(--color-canvas)_10.5px)]",
              ],
              separator && [
                "after:pointer-events-none after:absolute after:top-1/2 after:right-[-2px] after:h-3.5 after:w-px",
                "after:-translate-y-1/2 after:bg-hairline after:transition-opacity",
                // 自己或右边那张被悬停时,它的底色就是分界,竖线收掉
                "hover:after:opacity-0 [&:has(+*:hover)]:after:opacity-0",
              ],
            )}
          >
            {/*
              ★ Tab 上这个图标**不用强调色**。量 docs/image-new/image.png:
              未激活那张的文件夹是 #7d7b77(≈ `icon`),激活那张是 #3b3d3b —— 两个都
              **无彩度**(max−min ≤ 2),而强调色 #2d4739 的彩度是 26。
              也就是说激活态不靠「图标变彩」表达,靠的是整张 Tab 挖到画布色 + 文字变实。
            */}
            <Icon
              size={13}
              className={cn("shrink-0", active ? "text-fg" : "text-icon")}
            />
            {tab.pinned === true && <Pin size={11} aria-hidden className="shrink-0 text-accent" />}
            {tab.id === editingId ? (
              <TabRenameInput
                initial={label}
                ariaLabel={t("nav.renameWorkspace")}
                onSubmit={(value) => {
                  setEditingId(null);
                  if (tab.kind === "workspace") onRenameWorkspace?.(tab.ref.workspaceId, value);
                }}
                onCancel={() => setEditingId(null)}
              />
            ) : (
              <span className="min-w-0 flex-1 truncate">{label}</span>
            )}
            {/*
              尾部只有一个槽,不再给「运行中」和「关闭」各留一格:
                - 激活的 Tab:槽常驻,平时是 ×,运行中是转圈,悬停 / 聚焦时转圈换成 ×;
                - 未激活的 Tab:不留槽(原来那 18px 平时是空白)。运行中的转圈照常排在
                  文字后面;× 只在悬停 / 聚焦时**压**在尾部,左边一段渐隐盖住文字尾巴。
              × 不进 Tab 键序:键盘上关闭是 Delete,同一个动作没必要停两次。
            */}
            {active ? (
              <span className="relative flex size-[18px] shrink-0 items-center justify-center">
                {running && (
                  <Spinner
                    size="xs"
                    label={t("chat.taskChecklistRunning")}
                    className="text-accent transition-opacity group-focus-within:opacity-0 group-hover:opacity-0"
                  />
                )}
                <TabCloseButton
                  label={t("nav.closeTab", { label })}
                  onClose={close}
                  className={running ? "absolute inset-0 opacity-0 group-focus-within:opacity-100 group-hover:opacity-100" : undefined}
                />
              </span>
            ) : (
              <>
                {running && (
                  <Spinner size="xs" label={t("chat.taskChecklistRunning")} className="text-accent" />
                )}
                <span
                  className={cn(
                    "pointer-events-none absolute inset-y-0 right-0 flex items-center rounded-tr-[10px] pr-1.5 pl-5",
                    "bg-linear-to-r from-transparent to-(--tab-bg) to-45%",
                    "opacity-0 transition-opacity group-focus-within:opacity-100 group-hover:opacity-100",
                  )}
                >
                  <TabCloseButton
                    label={t("nav.closeTab", { label })}
                    onClose={close}
                    className="pointer-events-auto"
                  />
                </span>
              </>
            )}
          </div>
          </Tabs.Trigger>
          );
        })}
      </Tabs.List>
      </div>
      </Tabs.Root>

      {hiddenTabIds.length > 0 && (
        <Menu
          label={t("nav.allTabs")}
          width={260}
          className="mb-0.5 shrink-0"
          trigger={<ChevronDown size={15} />}
          triggerClassName="flex size-[26px] items-center justify-center rounded-[8px] text-icon transition-colors hover:bg-tint-hover hover:text-fg"
        >
          {(close) => (
            <>
              {hiddenTabIds.map((id) => {
                const tab = tabs.find((candidate) => candidate.id === id);
                if (tab === undefined) return null;
                const ws = tab.kind === "workspace" ? workspaces.find((workspace) => workspace.id === tab.ref.workspaceId) : undefined;
                const label = tab.kind === "workspace" ? (ws?.name ?? t("nav.unknownWorkspace")) : featureLabel(t, tab.ref.feature);
                const Icon = tab.kind === "workspace"
                  ? (isLocalEnvironment(ws?.environment) ? Folder : Server)
                  : FEATURE_ICON[tab.ref.feature];
                return (
                  <MenuItem
                    key={tab.id}
                    icon={<Icon size={14} />}
                    checked={tab.id === activeId}
                    onSelect={() => {
                      onActivate(tab.id);
                      close();
                    }}
                  >
                    {label}
                  </MenuItem>
                );
              })}
            </>
          )}
        </Menu>
      )}

      {!compact && (
        <>
          <IconButton
            label={t("nav.openWorkspace")}
            size={28}
            className="mb-0.5 ml-0.5 shrink-0"
            onClick={() => setWorkspacePickerOpen(true)}
          >
            <Plus size={15} />
          </IconButton>
          <WorkspacePickerDialog
            open={workspacePickerOpen}
            onClose={() => setWorkspacePickerOpen(false)}
            workspaces={workspaces}
            tabs={tabs}
            activeWorkspaceId={activeWorkspaceId}
            onOpenWorkspace={onOpenWorkspace}
            onEditWorkspace={onEditWorkspace}
            onPickWorkspace={onPickWorkspace}
            onCreateWorkspace={onCreateWorkspace}
            onCreateSshWorkspace={onCreateSshWorkspace}
          />
        </>
      )}
        </>
      )}

      {/* ★ 这块弹性空白是**故意**留给窗口拖动的 —— 整条 Tab 区唯一没有 no-drag 的地方 */}
      <div className="h-[30px] min-w-6 flex-1" />

      {/*
        右端这两个面板开关和左上角那颗「展开侧边栏」是**同一种控件**,几何量下来一模一样:
        image copy.png 里激活的 `PanelRight` 盒子 x979..1016(宽 38)、x=997 竖扫 y11..38(高 28)、
        x=985 处只剩 y14..35 —— 又是 r=14 的药丸。所以三处共用 38×28 + `rounded-pill`。

        「工作区文件」打开的那张截图是本项目 icon/accent 双 token 最干净的一处证据:
        同一张图里 `PanelRight` 是 底 #dbd8d1 + 图标 #2d4739,
        而 `PanelBottom` 还是 底 #e8e4dd + 图标 #7e7f7e。

        ★ `strokeWidth={1.5}` 也是量出来的,别删回 lucide 的默认 2。笔画粗细是
        **viewBox 单位**,size=16 时实际渲染 = 2 × 16/24 = 1.33px —— 落不到整数设备
        像素上,1x DPI 下被抹成两列灰,看着比旁边的东西「脏一档」。1.5 × 16/24 = 1.0px,
        正好一列。参考图里这几颗也确实是**单像素**笔画:
          image copy.png y=25 横扫 → PanelBottom 左右边框各只有 x=949 / x=962 一个
          #7e7f7e 像素,下一列就回到底色;image.png x=98 / x=111 同理(#2d4739)。
        在 Windows 上这条尤其要紧:右边紧挨着的三颗窗口按钮是手写 SVG、`strokeWidth: 1`
        配 10×10 viewBox = 实打实 1px 方头(见 WindowControls.tsx 文件头),
        默认 2 的 lucide 摆在它旁边就是两种笔法拼在一行。

        它们**是窗口级的**(见 stores/window.ts 的注释),所以不接受 Tab 参数。
      */}
      <div className="flex shrink-0 items-center gap-1 self-center">
        {updateIndicator}
        <IconButton
          label={t("nav.bottomPanel")}
          size={28}
          width={38}
          active={bottomPanelOpen}
          onClick={onToggleBottomPanel}
          className="rounded-pill"
        >
          <PanelBottom size={16} strokeWidth={1.5} />
        </IconButton>
        <IconButton
          label={t("nav.workspaceFiles")}
          size={28}
          width={38}
          active={rightPanelOpen}
          onClick={onToggleRightPanel}
          className="rounded-pill"
        >
          <PanelRight size={16} strokeWidth={1.5} />
        </IconButton>
      </div>

      {contextMenu !== null && contextTab !== undefined && (
        <ContextMenu
          position={contextMenu.position}
          label={t("nav.tabContextMenu")}
          onClose={() => setContextMenu(null)}
        >
          {(close) => (
            <>
            {canRename(contextTab) && (
              <MenuItem
                icon={<Pencil size={14} />}
                onSelect={() => {
                  setEditingId(contextTab.id);
                  close();
                }}
              >
                {t("nav.renameWorkspace")}
              </MenuItem>
            )}
            <MenuItem
              icon={contextTab.pinned === true ? <PinOff size={14} /> : <Pin size={14} />}
              checked={contextTab.pinned === true}
              onSelect={() => {
                onTogglePin(contextTab.id);
                close();
              }}
            >
              {contextTab.pinned === true ? t("nav.unpinTab") : t("nav.pinTab")}
            </MenuItem>
            </>
          )}
        </ContextMenu>
      )}
    </div>
  );
}

/**
 * Tab 尾部的 ×。按下时拦住冒泡:不拦的话 pointerdown 会先触发外层的拖排序,
 * click 会先激活这张 Tab 再关掉它。
 */
function TabCloseButton({
  label,
  onClose,
  className,
}: {
  label: string;
  onClose: () => void;
  className?: string;
}): ReactNode {
  return (
    <button
      type="button"
      tabIndex={-1}
      aria-label={label}
      onPointerDown={(e) => e.stopPropagation()}
      onMouseDown={(e) => e.stopPropagation()}
      onClick={(e) => {
        e.stopPropagation();
        onClose();
      }}
      className={cn(
        "app-no-drag flex size-[18px] shrink-0 items-center justify-center rounded-[5px]",
        "text-fg-faint transition-opacity hover:bg-tint-strong hover:text-fg",
        className,
      )}
    >
      <X size={12} />
    </button>
  );
}
