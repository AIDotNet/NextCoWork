/**
 * 转录里的文件引用 —— `ContentPart` 的 `file_ref`。
 *
 * 与 `MessageImage.tsx` 里的 `ImagePlaceholder` 同一个理由:只显示文件名,
 * 完整路径可能很长,糊进气泡文本会撑出横向滚动条(真实发生过的 bug)。
 *
 * 原先这里只读 —— 当时没有任何「打开它」的入口。现在点了会在右侧工作台打开
 * (见 `onOpen` 与 `file-reference-actions.ts`);删除/重试仍然没有,
 * 那两个动作属于草稿态的 `AttachmentChip`。
 */
import { FileText } from "lucide-react";
import type { ReactNode } from "react";
import { cn } from "../../lib/cn";

/** chip 的外观。可点开的那一份额外叠 hover/focus —— 见 `onOpen`。 */
const CHIP_CLASS =
  "flex h-8 max-w-full items-center gap-1.5 rounded-[8px] border border-stroke bg-tint px-2 text-[11.5px] text-fg-muted";

export function MessageFileRef({
  name,
  path,
  onOpen,
}: {
  name: string;
  path: string;
  /**
   * 打开这条引用(右侧工作台的一个 Tab)。
   *
   * ★ 不给就**不画按钮**:只读的子代理面板拿不到工作区上下文 ——
   * 画一枚点了没反应的 chip,比干脆不画更难解释。
   */
  onOpen?: (path: string) => void;
}): ReactNode {
  const content = (
    <>
      <FileText size={13} className="shrink-0 text-fg-faint" />
      <span className="min-w-0 truncate">{name}</span>
    </>
  );
  if (onOpen === undefined) {
    return (
      <div data-testid="message-file-ref" title={path} className={CHIP_CLASS}>
        {content}
      </div>
    );
  }
  return (
    <button
      type="button"
      data-testid="message-file-ref"
      title={path}
      onClick={() => onOpen(path)}
      className={cn(
        CHIP_CLASS,
        // 键盘路径与鼠标路径走同一个 `<button>`;`cursor-pointer` 必须显式写,
        // Tailwind v4 的 preflight 把按钮的 cursor 重置成了 default。
        "cursor-pointer hover:bg-tint-hover hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/50",
      )}
    >
      {content}
    </button>
  );
}
