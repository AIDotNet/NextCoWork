/**
 * 「改动审查」tab —— 某一轮(顶层 run)改了哪些文件 + 每个文件的 diff。
 *
 * 文件选择收进顶部工具栏，正文独占整列宽度；大文件默认只画改动 hunk 和邻近
 * 上下文，切文件后回到首个 hunk。数据全部来自 `review:*` IPC，完整文件由编辑器打开。
 */
import { ChevronLeft, ChevronRight, ExternalLink, FileDiff } from "lucide-react";
import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import type { InnerTab } from "../../../../shared/domain/tab";
import type { Workspace } from "../../../../shared/domain/workspace";
import type { ReviewChangeSet, ReviewFileDiff, ReviewFileEntry } from "../../../../shared/domain/review";
import { IconButton } from "../../components/ui/IconButton";
import { Select } from "../../components/ui/Select";
import { Spinner } from "../../components/ui/Spinner";
import { CodeDiffViewer } from "../../components/diff/CodeDiffViewer";
import { useI18n, type Translate } from "../../i18n";
import { getReviewChangeSet, getReviewFileDiff } from "../../services/review";
import { useTabsStore } from "../../stores/tabs";

type ChangesTab = Extract<InnerTab, { kind: "changes" }>;

/** 新建/删除是文件级状态，修改文件不额外占标签空间。 */
function fileTag(file: ReviewFileEntry, t: Translate): string | null {
  if (file.changeKind === "created") return t("chat.review.created");
  if (file.changeKind === "deleted") return t("chat.review.deleted");
  return null;
}

/** 下拉项用一段紧凑文本保留原文件列表的增删摘要。 */
function fileDelta(file: ReviewFileEntry, t: Translate): string | undefined {
  if (file.oversize === true) return t("chat.review.oversize");
  const parts = [
    file.additions > 0 ? `+${file.additions}` : null,
    file.deletions > 0 ? `-${file.deletions}` : null,
  ].filter((part): part is string => part !== null);
  return parts.length === 0 ? undefined : parts.join(" ");
}

/** 下拉项用完整路径避免同名文件混淆，并把状态与增删数留在同一行。 */
function fileLabel(file: ReviewFileEntry, t: Translate): string {
  const details = [fileTag(file, t), fileDelta(file, t)].filter((part): part is string => part !== null && part !== undefined);
  return details.length === 0 ? file.path : `${file.path} · ${details.join(" · ")}`;
}

export function ChangeReviewTab({ tab, workspace }: { tab: ChangesTab; workspace: Workspace }): ReactNode {
  const { t } = useI18n();
  const targetRef = tab.ref;
  const runId = targetRef.runId;
  // undefined = 加载中;null = 无改动/加载失败
  const [set, setSet] = useState<ReviewChangeSet | null | undefined>(undefined);
  const [selected, setSelected] = useState<string | null>(null);
  const [diff, setDiff] = useState<ReviewFileDiff | null | undefined>(undefined);
  const selectedRef = useRef<string | null>(null);
  const diffScrollerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let alive = true;
    getReviewChangeSet(runId)
      .then((nextSet) => {
        if (!alive) return;
        setSet(nextSet);
        setDiff(undefined);
        if (nextSet === null || nextSet.files.length === 0) {
          selectedRef.current = null;
          setSelected(null);
          return;
        }
        // 点的文件可能是同一轮刚新增的，也可能撤销后已消失；必须用这次的新快照定位。
        const wanted = targetRef.selectedPath;
        const hit = wanted === undefined ? undefined : nextSet.files.find((file) => file.path === wanted);
        const next = hit?.path ?? nextSet.files[0]!.path;
        selectedRef.current = next;
        setSelected(next);
      })
      .catch(() => {
        if (!alive) return;
        setSet(null);
        setDiff(null);
      });
    return () => {
      alive = false;
    };
  }, [runId, targetRef]);

  useEffect(() => {
    if (selected === null || set === undefined || set === null) {
      setDiff(null);
      return;
    }
    let alive = true;
    setDiff(undefined);
    getReviewFileDiff(runId, selected)
      .then((d) => {
        if (alive) setDiff(d);
      })
      .catch(() => {
        if (alive) setDiff(null);
      });
    return () => {
      alive = false;
    };
  }, [runId, selected, set]);

  useLayoutEffect(() => {
    const scroller = diffScrollerRef.current;
    if (scroller === null) return;
    // 需求：切文件或从回合卡重新定位时必须回到首个 hunk；沿用旧滚动位置会直接越过改动。
    scroller.scrollTop = 0;
    scroller.scrollLeft = 0;
  }, [selected, set]);

  if (set === undefined) return <div className="flex min-h-0 flex-1 bg-canvas" />;
  if (set === null || set.files.length === 0) {
    return (
      <div className="flex min-h-0 flex-1 items-center justify-center bg-canvas text-[12px] text-fg-faint">
        {t("chat.review.empty")}
      </div>
    );
  }

  const selectedIndex = selected === null ? -1 : set.files.findIndex((file) => file.path === selected);
  const selectedFile = selectedIndex < 0 ? undefined : set.files[selectedIndex];
  const fileOptions = set.files.map((file) => ({ value: file.path, label: fileLabel(file, t) }));

  const selectFile = (path: string): void => {
    if (path === selectedRef.current) return;
    // 需求：文件标题一切换就收起旧 diff，不能让旧内容顶着新文件名闪一帧。
    selectedRef.current = path;
    setDiff(undefined);
    setSelected(path);
  };
  const selectAt = (index: number): void => {
    const file = set.files[index];
    if (file !== undefined) selectFile(file.path);
  };
  const openFile = (): void => {
    // 需求：已删除的路径没有可成功打开的编辑器入口，因此不承诺这个操作。
    if (selectedFile === undefined || selectedFile.changeKind === "deleted") return;
    useTabsStore.getState().openFile(workspace.id, selectedFile.path);
  };

  return (
    <div className="flex min-h-0 flex-1 flex-col bg-canvas">
      <div className="flex shrink-0 items-center gap-1.5 border-b border-border bg-surface/40 px-2.5 py-2">
        <FileDiff aria-hidden size={14} className="shrink-0 text-accent-soft" />
        <Select
          value={selected ?? ""}
          options={fileOptions}
          onValueChange={selectFile}
          ariaLabel={t("chat.review.chooseFile")}
          disabled={selectedFile === undefined}
          className="min-w-0 flex-1"
        />
        <span className="shrink-0 px-1 font-mono text-[11px] tabular-nums text-fg-faint">
          {selectedIndex + 1} / {set.files.length}
        </span>
        <IconButton
          label={t("chat.review.previousFile")}
          size={28}
          disabled={selectedIndex <= 0}
          onClick={() => selectAt(selectedIndex - 1)}
        >
          <ChevronLeft aria-hidden size={14} />
        </IconButton>
        <IconButton
          label={t("chat.review.nextFile")}
          size={28}
          disabled={selectedIndex < 0 || selectedIndex >= set.files.length - 1}
          onClick={() => selectAt(selectedIndex + 1)}
        >
          <ChevronRight aria-hidden size={14} />
        </IconButton>
        {selectedFile !== undefined && selectedFile.changeKind !== "deleted" && (
          <IconButton label={t("chat.review.openFile")} size={28} onClick={openFile}>
            <ExternalLink aria-hidden size={13} />
          </IconButton>
        )}
      </div>

      <div ref={diffScrollerRef} className="scroll-thin flex min-h-0 min-w-0 flex-1 flex-col overflow-auto">
        {selectedFile === undefined ? (
          <div className="flex min-h-full items-center justify-center p-6 text-[12px] text-fg-faint">
            {t("chat.review.selectFile")}
          </div>
        ) : diff === undefined ? (
          <div role="status" className="flex min-h-full items-center justify-center gap-2 p-6 text-[12px] text-fg-faint">
            <Spinner size="sm" />
            {t("chat.review.loading")}
          </div>
        ) : diff === null ? (
          <div className="flex min-h-full items-center justify-center p-6 text-[12px] text-fg-faint">
            {t("chat.review.loadFailed")}
          </div>
        ) : diff.oversize === true ? (
          <div className="flex min-h-full items-center justify-center p-6 text-[12px] text-fg-faint">
            {t("chat.review.oversize")}
          </div>
        ) : diff.before === diff.after ? (
          <div className="flex min-h-full items-center justify-center p-6 text-[12px] text-fg-faint">
            {t("chat.review.noTextChanges")}
          </div>
        ) : (
          /* 语法高亮的语言从路径推;对比视图自己滚动,切文件时它会回到顶部 */
          <CodeDiffViewer original={diff.before} modified={diff.after} path={selectedFile.path} />
        )}
      </div>
    </div>
  );
}
