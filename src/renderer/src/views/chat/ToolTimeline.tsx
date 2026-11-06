/**
 * L2:运行中的分组折叠时间线。
 *
 * ★ 它**只负责一段连续的过程块**，不关心自己上方或下方有没有正文。
 * 每个连续工具组在所有调用拿到成功结果后**停 FOLD_HOLD_MS** 再自动收起
 * （先让人看清完成态，延迟与逐帧补偿的理由见 ToolGroup / useFoldAnchor）；
 * 运行中的组仍保持可见。
 * 这条边界让同一个组件能同时服务于「已提交消息」和「还在流的块」两条路径。
 */
import { AnimatePresence, motion } from "motion/react";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { formatDuration } from "../../../../shared/agent/duration";
import type { SubagentState, ToolCallState } from "../../../../shared/agent/transcript";
import {
  groupDuration,
  groupConsecutiveTools,
  groupKey,
  isCompletedToolGroup,
  shapeOfItem,
  statusOfItem,
  type TimelineItem,
} from "../../../../shared/domain/tool-timeline";
import { cn } from "../../lib/cn";
import { useI18n, type TranslationKey } from "../../i18n";
import { motionScale, useMotionLevel } from "../../theme/useMotionLevel";
import { SubagentNode, ThinkingBlock, ToolCallCard } from "./parts";
import { ROW_CLASS, RowChevron } from "./row";
import { ShapeStrip } from "./ToolIcon";
import { AgentShimmerText } from "./AgentActivity";
import { useGroupCollapse } from "./useGroupCollapse";
import { FOLD_HOLD_MS, useFoldAnchor } from "./useFoldAnchor";

export function ToolTimeline({
  items,
  tools,
  subagents = {},
  focusCallId,
}: {
  items: readonly TimelineItem[];
  tools: Readonly<Record<string, ToolCallState>>;
  subagents?: Readonly<Record<string, SubagentState>>;
  /** 从文件审查回跳时定位到的那一行;所在组强制展开并滚入视口 */
  focusCallId?: string | undefined;
}): ReactNode {
  const scale = motionScale(useMotionLevel());
  if (items.length === 0) return null;

  const groups = groupConsecutiveTools(items, tools);
  const autoCollapsed = groups.map((group) => isCompletedToolGroup(group, tools));

  return (
    <div className="flex flex-col gap-0.5" data-testid="tool-timeline">
      {/*
        ★★ **`initial={false}` 是这里唯一重要的那个参数,不是随手加的。**

        入场动画想要的语义是「流式过程中**新冒出来**的那一行往上浮一下」。
        不写 `initial={false}` 的话,`AnimatePresence` 会把**首次挂载时就已经在
        列表里的所有组**也当成新增 —— 于是打开一段跑了两百轮的历史会话,
        满屏几十张卡片一起淡入,像拉开一道帘子。那不是「流畅」,那是开场动画。

        `initial={false}` 精确地表达了这个区别:第一帧就在的,直接到位;
        第一帧之后才进来的,才播入场。历史转录和流式新增因此不需要两条代码路径,
        也就不存在「两边长得不一样」的风险(同 parts.tsx 文件头那一条)。
      */}
      <AnimatePresence initial={false}>
        {groups.map((g, i) => (
          <motion.div
            // ★ key 用**首项**的 key,不用下标。理由见 tool-timeline.ts 的 groupKey:
            // 下标会让「用户展开过第 2 组」的意图在重新分组后漂到别的组身上。
            key={groupKey(g)}
            layout="position"
            initial={{ opacity: 0, y: 6 }}
            animate={{ opacity: 1, y: 0 }}
            exit={{ opacity: 0, height: 0, y: -4 }}
            style={{ overflow: "hidden" }}
            transition={{ duration: 0.18 * scale, ease: [0.32, 0.72, 0, 1] }}
          >
            <ToolGroup
              items={g}
              tools={tools}
              subagents={subagents}
              autoCollapsed={autoCollapsed[i] ?? false}
              focusCallId={focusCallId}
            />
          </motion.div>
        ))}
      </AnimatePresence>
    </div>
  );
}

function ToolGroup({
  items,
  tools,
  subagents,
  autoCollapsed,
  focusCallId,
}: {
  items: readonly TimelineItem[];
  tools: Readonly<Record<string, ToolCallState>>;
  subagents: Readonly<Record<string, SubagentState>>;
  autoCollapsed: boolean;
  focusCallId: string | undefined;
}): ReactNode {
  const scale = motionScale(useMotionLevel());
  const hasError = items.some((it) => statusOfItem(it, tools) === "error");
  const hasFocus =
    focusCallId !== undefined &&
    items.some((it) => it.kind === "tool" && it.callId === focusCallId);

  /*
    需求:组里最后一个调用拿到 ok 之后,先让人把完成态(✓、耗时)看一眼,
    **再**停 FOLD_HOLD_MS 收起。不满足会怎样:结果一到就折,完成态一帧都
    留不住;连续几个组先后完成时正文被连拽几次,表现为「工具跑完界面自己
    抖了几下」。
    ★ 挂载初值直接取 autoCollapsed:历史会话里每个组天生是完成态,走延迟
    的话会先全体展开 400ms 再一起收,满屏闪一次 —— 延迟只服务于「运行中
    翻成完成」这一次跳变。
  */
  const [autoReady, setAutoReady] = useState(autoCollapsed);
  useEffect(() => {
    if (autoCollapsed === autoReady) return;
    if (!autoCollapsed) {
      setAutoReady(false);
      return;
    }
    const timer = setTimeout(() => setAutoReady(true), FOLD_HOLD_MS);
    return () => clearTimeout(timer);
  }, [autoCollapsed, autoReady]);

  const { collapsed, toggle } = useGroupCollapse(
    autoReady,
    hasError || hasFocus,
  );
  const ref = useRef<HTMLDivElement>(null);
  // 单项组永不自动收起(isCompletedToolGroup 要求 >1 个调用),不会发生
  // false→true 的折叠跳变 —— 给它记基线纯属浪费:历史里几十个单项组会在
  // 流式期间每帧各多读一次布局。
  useFoldAnchor(ref, collapsed, undefined, items.length > 1);

  /**
   * 回跳定位。用 `nearest` 而不是 `center` —— `center` 会让**已经在视口里**的
   * 目标行也发生滚动,看起来像界面自己抖了一下。
   */
  useEffect(() => {
    if (!hasFocus) return;
    ref.current?.scrollIntoView({ block: "nearest" });
  }, [hasFocus]);

  // 单项组不套折叠外壳:一行内容加一个「1 项」的标题是纯粹的噪音
  if (items.length === 1 && !collapsed) {
    return (
      <div ref={ref}>
        <TimelineRow item={items[0]!} tools={tools} subagents={subagents} />
      </div>
    );
  }

  return (
    <motion.div ref={ref} layout className="flex flex-col gap-0.5">
      <GroupHeader
        items={items}
        tools={tools}
        collapsed={collapsed}
        onToggle={toggle}
      />
      <AnimatePresence initial={false}>
        {!collapsed && (
          <motion.div
            key="tool-group-content"
            initial={{ height: 0, opacity: 0 }}
            animate={{ height: "auto", opacity: 1 }}
            exit={{ height: 0, opacity: 0 }}
            transition={{ duration: 0.2 * scale, ease: [0.32, 0.72, 0, 1] }}
            style={{ overflow: "hidden" }}
          >
            {/*
              组内各行缩进到组标题的文字下方(21px = 箭头 12/13 + gap 8),
              和 `SURFACE_INDENT` 对齐 —— 无边框之后,缩进是「这些行属于这个组」
              的唯一证据,随手写 `pl-2` 会让同级的块对不齐。
            */}
            <div className="flex flex-col gap-0.5 pl-[21px]">
              {items.map((it) => (
                <TimelineRow key={it.key} item={it} tools={tools} subagents={subagents} />
              ))}
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </motion.div>
  );
}

function GroupHeader({
  items,
  tools,
  collapsed,
  onToggle,
}: {
  items: readonly TimelineItem[];
  tools: Readonly<Record<string, ToolCallState>>;
  collapsed: boolean;
  onToggle: () => void;
}): ReactNode {
  const { t } = useI18n();
  const ms = groupDuration(items, tools);
  const shapes = [...new Set(items.map((it) => shapeOfItem(it, tools)))];
  const counts = new Map<typeof shapes[number], number>();
  for (const shape of items.map((item) => shapeOfItem(item, tools))) {
    counts.set(shape, (counts.get(shape) ?? 0) + 1);
  }
  const title = shapes.map((shape) => t(groupTitleKey(shape), { count: counts.get(shape) ?? 0 })).join(" · ");
  const errorCount = items.filter(
    (it) => statusOfItem(it, tools) === "error",
  ).length;
  const runningCount = items.filter(
    (it) => statusOfItem(it, tools) === "running",
  ).length;

  return (
    <button
      type="button"
      aria-expanded={!collapsed}
      onClick={onToggle}
      data-testid="tool-group"
      data-collapsed={collapsed}
      className={cn(
        // 文本风格:不画描边和底色,收起/展开的差别只靠文字颜色
        // (收起时这一行代表着 N 行内容,所以亮一档)。
        ROW_CLASS,
        "text-[12.5px]",
        collapsed ? "text-fg-muted" : "text-fg-faint",
      )}
    >
      <ShapeStrip shapes={shapes} />
      <span className="min-w-0 flex-1 truncate">{title}</span>
      {runningCount > 0 && (
        <span className="inline-flex shrink-0 items-center gap-1.5 text-accent">
          <AgentShimmerText>{t("chat.tool.runningStatus")}</AgentShimmerText>
        </span>
      )}
      {ms > 0 && (
        <span className="shrink-0 font-mono text-[11.5px] text-fg-faint">
          {formatDuration(ms)}
        </span>
      )}
      {/* 失败标记即使在收起态也必须可见 —— 「这里有个失败被我收起来了」 */}
      {errorCount > 0 && (
        <span className="shrink-0 text-danger">
          {t("chat.failedCount", { count: errorCount })}
        </span>
      )}
      <RowChevron open={!collapsed} />
    </button>
  );
}

const GROUP_TITLE_KEYS = {
  reasoning: "chat.tool.group.reasoning",
  read: "chat.tool.group.read",
  mutate: "chat.tool.group.mutate",
  search: "chat.tool.group.search",
  command: "chat.tool.group.command",
  network: "chat.tool.group.network",
  orchestration: "chat.tool.group.orchestration",
  interaction: "chat.tool.group.interaction",
  widget: "chat.tool.group.widget",
  image: "chat.tool.group.image",
  // 生成视频。同 image:产物不进过程段,但一个组里仍可能混进它 —— 少了这一行
  // 那个组的标题会显示成 key 本身。
  video: "chat.tool.group.video",
  // 页面截图（browser_screenshot）。它不进过程段（见 isPinnedShape），
  // 但一个组里仍可能混进它（比如卡片还没提交、或用户手动展开了工作区）——
  // 少了这一行，那个组的标题会显示成 key 本身。
  screenshot: "chat.tool.group.screenshot",
  // 工作流画布同样不进过程段,理由同上一行
  workflow: "chat.tool.group.workflow",
  external: "chat.tool.group.external",
} as const satisfies Record<ReturnType<typeof shapeOfItem>, TranslationKey>;

function groupTitleKey(shape: ReturnType<typeof shapeOfItem>): TranslationKey {
  return GROUP_TITLE_KEYS[shape];
}

/** 三种块的分派。与改造前 `Thread.tsx` 的 PartBlock 保持同构。 */
function TimelineRow({
  item,
  tools,
  subagents,
}: {
  item: TimelineItem;
  tools: Readonly<Record<string, ToolCallState>>;
  subagents: Readonly<Record<string, SubagentState>>;
}): ReactNode {
  switch (item.kind) {
    case "thinking":
      return <ThinkingBlock text={item.text} streaming={item.streaming} stats={item.stats} />;
    case "subagent":
      return (
        <SubagentNode
          summary={item.summary}
          state={item.state ?? subagents[item.callId]}
          // 参数还在流、subagent_start 还没到 —— 理由见 thread-content.ts 里那段需求注释。
          // 这里再核一次 subagents 表:item 是上一帧算出来的,状态可能刚刚到。
          pending={item.pending === true && item.state === undefined && subagents[item.callId] === undefined}
        />
      );
    case "tool":
      return (
        <ToolCallCard
          call={item.callId === undefined ? undefined : tools[item.callId]}
          name={item.name}
          input={item.input}
        />
      );
  }
}
