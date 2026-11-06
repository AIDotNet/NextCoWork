/**
 * 草稿附件区 —— 输入框上沿那排 chip(设计 §8)。
 *
 * ## 为什么 chip 直接 `<img src={ncw://…}>`
 *
 * 这**就是**自定义协议存在的意义。在此之前显示一张本地图要走 IPC 传字节 →
 * `new Blob` → `createObjectURL`,而 object URL 有生命周期:忘了 revoke 就泄漏,
 * revoke 早了图就裂。现在这些全部消失,`<img>` 拿到的是一个普通 URL,
 * 缓存、解码、失败重试都由 Chromium 负责。
 *
 * ## 三种状态都要看得见
 *
 * 上传中、成功、失败。★ **失败的 chip 保留,有原文件时可重试,不静默消失** ——
 * 用户拖了 5 个文件进来,其中一个太大被拒,如果它悄悄不见了,
 * 用户只会以为自己少拖了一个。
 */
import { AlertCircle, FileText, RotateCw, Upload, X } from "lucide-react";
import { useState, type ReactNode } from "react";
import type { Attachment, FileReferenceSource } from "../../../../shared/domain/attachment";
import { isImageMime } from "../../../../shared/domain/attachment";
import { useI18n } from "../../i18n";
import { cn } from "../../lib/cn";
import { ImageLightbox } from "./ImageLightbox";

/**
 * 托盘里的一项。上传是异步的,所以「一个 chip」在拿到 `Attachment` 之前
 * 就要存在 —— 它的身份是本地 mint 的 `key`,而不是附件 id。
 */
export interface TrayItem {
  key: string;
  name: string;
  status: "uploading" | "awaiting-upload" | "done" | "error";
  attachment?: Attachment;
  /** 非图片文件的真实磁盘路径——不经过上传，与 attachment 二选一 */
  path?: string;
  source?: FileReferenceSource;
  error?: string;
  /** 没有原 File 的选图失败项只能移除后重新选择。 */
  canRetry?: boolean;
}

export function AttachmentTray({
  items,
  onRemove,
  onRetry,
}: {
  items: TrayItem[];
  onRemove: (key: string) => void;
  onRetry: (key: string) => void;
}): ReactNode {
  const { t } = useI18n();
  if (items.length === 0) return null;

  return (
    <div
      className="flex flex-wrap gap-1.5 px-3 pt-3"
      data-testid="attachment-tray"
      data-count={items.length}
    >
      {items.map((item) => (
        <AttachmentChip
          key={item.key}
          item={item}
          onRemove={() => {
            onRemove(item.key);
          }}
          onRetry={() => {
            onRetry(item.key);
          }}
          labels={{
            uploading: t("chat.uploading"),
            upload: t("ssh.uploadToServer"),
            retry: t("accessibility.retry"),
            remove: t("accessibility.remove"),
            preview: t("chat.zoomImage"),
          }}
        />
      ))}
    </div>
  );
}

function AttachmentChip({
  item,
  onRemove,
  onRetry,
  labels,
}: {
  item: TrayItem;
  onRemove: () => void;
  onRetry: () => void;
  labels: { uploading: string; upload: string; retry: string; remove: string; preview: string };
}): ReactNode {
  const [previewing, setPreviewing] = useState(false);
  const a = item.attachment;
  const isImage = a !== undefined && isImageMime(a.mime);
  // ★ 协议直供。没有 blob URL,也就没有 revoke 的生命周期问题。
  const thumbnail = isImage && a !== undefined ? (
    <img
      src={a.url}
      alt=""
      className="h-6 w-6 shrink-0 rounded-[4px] object-cover"
      // 文件被外部删除 → 协议回 404 → 退化成文件图标,而不是裂图
      onError={(e) => { e.currentTarget.style.display = "none"; }}
    />
  ) : null;

  return (
    <div
      data-testid="attachment-chip"
      data-status={item.status}
      title={item.status === "error" ? item.error : item.name}
      className={cn(
        "group relative flex h-9 max-w-[180px] items-center gap-1.5 rounded-[8px] border pr-1 pl-1.5 text-[11.5px]",
        item.status === "error"
          ? "border-danger/40 bg-danger/8 text-danger"
          : "border-border bg-tint text-fg-muted",
      )}
    >
      {item.status === "error" ? (
        <AlertCircle size={13} className="shrink-0" />
      ) : item.status === "done" && thumbnail !== null ? (
        // 需求：只有上传完成的图片可预览；上传中的缩略图保持只读。
        <button
          type="button"
          onClick={() => setPreviewing(true)}
          aria-label={labels.preview}
          className="app-no-drag shrink-0 rounded-[4px] cursor-zoom-in focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/50"
        >
          {thumbnail}
        </button>
      ) : thumbnail !== null ? thumbnail : (
        <FileText size={13} className="shrink-0 text-fg-faint" />
      )}

      <span className="min-w-0 truncate">{item.name}</span>

      {item.status === "uploading" && (
        <span className="shrink-0 text-fg-faint">{labels.uploading}</span>
      )}

      {item.status === "awaiting-upload" && <button type="button" onClick={onRetry} title={labels.upload} aria-label={labels.upload} className="shrink-0 rounded-[5px] p-1 text-accent hover:bg-tint-hover"><Upload size={13} /></button>}

      {item.status === "error" && item.canRetry !== false && (
        <button
          type="button"
          onClick={onRetry}
          aria-label={labels.retry}
          className="shrink-0 rounded-[5px] p-1 transition-colors hover:bg-danger/15"
        >
          <RotateCw size={12} />
        </button>
      )}

      <button
        type="button"
        onClick={onRemove}
        aria-label={labels.remove}
        data-testid="attachment-remove"
        className="shrink-0 rounded-[5px] p-1 text-fg-faint transition-colors hover:bg-tint-hover hover:text-fg"
      >
        <X size={12} />
      </button>
      {previewing && item.status === "done" && isImage && a !== undefined && (
        <ImageLightbox
          images={[{ mime: a.mime, dataRef: a.url }]}
          startIndex={0}
          onClose={() => setPreviewing(false)}
        />
      )}
    </div>
  );
}
