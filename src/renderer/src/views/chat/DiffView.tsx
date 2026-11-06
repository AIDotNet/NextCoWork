/**
 * 聊天展开区里那份 diff 的**外框** —— 卡壳、标题、截断提示。
 *
 * 行怎么画已经不在这里了:那一份实现在 `components/diff/DiffLines.tsx`。
 * (「改动审查」整页和 Git 面板画的是 `components/diff/CodeDiffViewer`,一个基于
 * CodeMirror 的对比视图 —— 那边要横向滚动、并排和折叠,这张小卡片不需要。)
 * 这个文件只剩 chat 侧自己的事:套哪张卡(`detail-card.ts` 规定展开区所有产物块
 * 必须长成同一张)、渲染前截断多少行。
 */
import { useMemo, type ReactNode } from "react";
import { DiffLines } from "../../components/diff/DiffLines";
import { computeDiff } from "../../components/diff/compute";
import { useI18n } from "../../i18n";
import { cn } from "../../lib/cn";
import { DETAIL_CARD_CLASS } from "./detail-card";

/**
 * 把 old / new 两段文本渲染成一份统一 diff,改动的行逐词高亮。
 *
 * `label` 省略时不画标题；`maxRows` 之外的行折叠成一句「省略 N 行」。
 */
export function DiffBlock({
  oldStr,
  newStr,
  language = "",
  label,
  maxRows = 24,
}: {
  oldStr: string;
  newStr: string;
  /** 语法高亮的语言(通常是扩展名)。空串 = 只有增删底色,没有语法色 */
  language?: string;
  label?: string;
  maxRows?: number;
}): ReactNode {
  const { t } = useI18n();
  /*
    需求：工具卡片的 diff 现在**在参数还在流的时候就画**（见 ToolDetail 的 MutateDetail），
    也就是每一个流式帧都会走一次这里。LCS 是 O(n·m)，不 memo 的话一次几百行的
    Edit 会把它按帧重算 —— 表现为流式写文件时整个界面发涩，而 CPU profile 里
    只看得到一片 diff 计算，看不出是谁在反复触发。
    入参没变时这层直接跳过；入参在变时无可避免，那一份预览本来就被
    `MAX_PARTIAL_JSON_CHARS` 截在 64KB 以内。
  */
  const all = useMemo(() => computeDiff(oldStr, newStr), [oldStr, newStr]);
  /*
    需求:截断发生在**渲染之前**,而且切出来的数组必须稳定 —— 它现在是
    `DiffLines` 的 prop,顺带也是语法高亮 memo 的 key;每帧新建一个等价数组
    会让高亮每帧重算一次(表现为流式 Edit 里代码颜色一直在闪)。
  */
  const lines = useMemo(() => all.slice(0, maxRows), [all, maxRows]);
  const omitted = all.length - lines.length;
  return (
    <div className="mt-1.5 first:mt-0">
      {label !== undefined && (
        <p className="mb-0.5 text-[11px] text-fg-faint">{label}</p>
      )}
      <div className={cn(DETAIL_CARD_CLASS, "scroll-thin max-h-72 overflow-auto py-1.5")}>
        <DiffLines lines={lines} language={language} className="text-[12.5px] leading-[1.6]" />
        {omitted > 0 && (
          <div className="px-2.5 pt-0.5 font-mono text-[12.5px] text-fg-faint">
            {t("chat.tool.linesOmitted", { count: omitted }).trim()}
          </div>
        )}
      </div>
    </div>
  );
}
