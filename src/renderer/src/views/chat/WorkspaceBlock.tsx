/**
 * L3:任务完成后的「工作区」区块。
 *
 * ★ **展开后露出的是 L2 的分组坍缩态,不是全部工具行。**
 * 两层折叠叠加,一次展开只往下走一级 —— 直接展平会让「展开工作区」这个动作的
 * 结果不可预测:可能是 3 行,也可能是 40 行。用户不敢点的折叠等于没有折叠。
 */
import { LayoutGrid } from "lucide-react";
import {
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { formatDuration } from "../../../../shared/agent/duration";
import type { ToolCallState } from "../../../../shared/agent/transcript";
import {
  summarize,
  type TimelineItem,
} from "../../../../shared/domain/tool-timeline";
import { cn } from "../../lib/cn";
import { useI18n } from "../../i18n";
import { Surface, SurfaceReveal } from "../../components/ui/Surface";
import { ToolTimeline } from "./ToolTimeline";
import { ShapeStrip } from "./ToolIcon";
import { ROW_CLASS, RowChevron } from "./row";

/** 标题行最多画几个形态图标,超出显示 `+n` */
const MAX_SHAPE_ICONS = 4;

/** 收束延迟。理由见下面 useEffect 的说明。 */
const COLLAPSE_DELAY_MS = 400;

export function WorkspaceBlock({
  items,
  tools,
  defaultOpen,
  fileChangeCount = 0,
  scrollRef,
}: {
  items: readonly TimelineItem[];
  tools: Readonly<Record<string, ToolCallState>>;
  /** 有失败时收进来但默认展开 */
  defaultOpen: boolean;
  fileChangeCount?: number;
  /** 滚动容器 —— 收束时要在同一帧做锚点补偿,没有它就会「跳一下」 */
  scrollRef?: React.RefObject<HTMLElement | null>;
}): ReactNode {
  const { t } = useI18n();
  /**
   * ★ **延迟 400ms 再收束,且不加动画。**
   *
   * run 刚结束时最后一段正文可能刚提交。若立即收束,用户会看到一大段内容
   * 突然消失 —— 即使逻辑上正确,观感上像是出错了。400ms 足够让注意力从
   * 「还在跑」切换到「已出结果」,此时收起会被理解为「过程收好了」。
   *
   * 不加动画的理由:收束时用户的视线应该在下方的结论文本上,
   * 一段 300ms 的高度动画会把视线硬拽回上方。
   */
  const [open, setOpen] = useState(true);
  const ref = useRef<HTMLDivElement>(null);
  const pendingCollapse = useRef(false);

  useEffect(() => {
    if (defaultOpen) return;
    const t = setTimeout(() => {
      pendingCollapse.current = true;
      setOpen(false);
    }, COLLAPSE_DELAY_MS);
    return () => clearTimeout(t);
  }, [defaultOpen]);

  /**
   * 滚动锚定补偿。
   *
   * 收束会让容器高度骤减几百像素;若用户此刻贴在底部,浏览器保持 `scrollTop`
   * 不变,视口相对内容就「向下跳」了。这里在**同一帧**记录区块顶边的位移并回补。
   *
   * ★ 锚点取本区块的 `getBoundingClientRect().top`,不取 `scrollHeight` 差值 ——
   * 后者在同时有其他异步高度变化(图片加载、代码高亮)时会算错,
   * 而前者就是用户视线实际停留的那条边。
   */
  useLayoutEffect(() => {
    if (!pendingCollapse.current) return;
    pendingCollapse.current = false;
    const scroller = scrollRef?.current;
    const el = ref.current;
    if (!scroller || !el) return;
    const before = el.getBoundingClientRect().top;
    requestAnimationFrame(() => {
      const after = el.getBoundingClientRect().top;
      const delta = after - before;
      if (delta !== 0) scroller.scrollTop += delta;
    });
  }, [open, scrollRef]);

  const summary = summarize(items, tools, fileChangeCount);
  const normal = [t("chat.workspace.toolCount", { count: summary.toolCount })];
  if (summary.totalMs > 0) {
    normal.push(t("chat.workspace.totalTime", { duration: formatDuration(summary.totalMs) }));
  }
  if (summary.fileChangeCount > 0) {
    normal.push(t("chat.workspace.fileChanges", { count: summary.fileChangeCount }));
  }
  const danger = summary.errorCount > 0
    ? t("chat.failedCount", { count: summary.errorCount })
    : undefined;
  const shownShapes = summary.shapes.slice(0, MAX_SHAPE_ICONS);
  const extraShapes = summary.shapes.length - shownShapes.length;

  return (
    <Surface
      ref={ref}
      data-testid="workspace-block"
      data-open={open}
    >
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
        className={cn(ROW_CLASS, "text-[13px]")}
      >
        <LayoutGrid size={13} className="shrink-0 text-accent-soft" />
        <span className="shrink-0">{t("chat.workspace")}</span>
        <ShapeStrip shapes={shownShapes} />
        {extraShapes > 0 && (
          <span className="shrink-0 text-[12px] text-fg-faint">
            +{extraShapes}
          </span>
        )}

        <span className="min-w-0 flex-1 truncate text-[12.5px] text-fg-faint">
          {normal.join(" · ")}
        </span>
        {danger !== undefined && (
          <span className="shrink-0 text-[12.5px] text-danger">{danger}</span>
        )}
        <RowChevron open={open} />
      </button>

      <SurfaceReveal open={open}>
        {/*
          ★ `running={false}` 是关键:工作区只在 run 结束后出现,
          此时 L2 不该再按「最近 3 项」的窗口规则坍缩 —— 那个规则解决的是
          运行中的刷屏,而运行已经结束了。这里交给用户自己按组展开。
        */}
        <ToolTimeline items={items} tools={tools} />
      </SurfaceReveal>
    </Surface>
  );
}
