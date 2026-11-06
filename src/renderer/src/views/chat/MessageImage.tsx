/**
 * 转录里的图片 —— 已经发出去的那些。
 *
 * ## 三种 dataRef,只有一种能直接显示
 *
 * - **`ncw://`**(本应用上传的)→ 协议直供,`<img>` 拿到就能画。
 * - **绝对路径**(Agent 产出的图、从别处引用的)→ **画不出来**。
 *   渲染层不能加载 `file://`(那要关掉 `webSecurity`,等于拆掉整个沙箱),
 *   所以只能显示一个带文件名的占位。这不是缺陷,是安全边界的必然结果 ——
 *   要让它们也能显示,正确做法是把它们也纳入附件管理,而不是放开 file://。
 * - **失效的引用**(文件被删、转录从别的机器导入)→ 协议回 404,
 *   走 `onError` 显示占位。
 *
 * ## 灯箱的分组由调用方给
 *
 * 一条消息里的多张图应当能左右翻页,而单张图不该出现翻页按钮。
 * 「哪些图算一组」是**消息**这一层才知道的事,所以 `siblings` 由 `UserBubble`
 * 传进来 —— 组件自己猜不出来。
 */
import { ImageOff } from "lucide-react";
import { useState, type ReactNode } from "react";
import { NCW_SCHEME, ncwPreviewUrl } from "../../../../shared/domain/attachment";
import { cn } from "../../lib/cn";
import { useI18n } from "../../i18n";
import { ImageLightbox, type LightboxImage } from "./ImageLightbox";

export function MessageImage({
  mime,
  dataRef,
  siblings,
  index = 0,
  compact = false,
  workspaceId,
}: {
  mime: string;
  dataRef: string;
  /** 同一条消息里的全部可显示图片。缺省时按单张处理 */
  siblings?: readonly LightboxImage[];
  /** 本图在 siblings 里的位置 */
  index?: number;
  /** 用户消息下方的附件卡片限制缩略图高度，不改变助手图片尺寸。 */
  compact?: boolean;
  /**
   * 这张图属于哪个工作区 —— 灯箱据此决定要不要给「用别的程序打开」。
   *
   * ★ 缺省 = 不画。**只读的子代理面板**拿不到工作区上下文(与
   *   `MessageFileRef.onOpen` 同一条规矩):那种情况下画一枚点了没反应的入口,
   *   比干脆不画更难解释。
   */
  workspaceId?: string;
}): ReactNode {
  const { t } = useI18n();
  const [failed, setFailed] = useState(false);
  const [zoomed, setZoomed] = useState(false);
  const displayable = dataRef.startsWith(`${NCW_SCHEME}://`);

  if (!displayable) {
    // 外部绝对路径:显示文件名而不是整条路径 —— 路径可能很长,
    // 而用户真正需要辨认的是「哪一张图」。
    return (
      <ImagePlaceholder
        label={fileNameOf(dataRef)}
        hint={t("chat.externalFile")}
      />
    );
  }

  if (failed) {
    return <ImagePlaceholder label={t("chat.imageMissing")} hint={mime} />;
  }

  const group = siblings ?? [{ mime, dataRef }];

  return (
    <>
      {/*
        用 button 包住而不是给 img 挂 onClick:键盘用户要能 Tab 到它并回车打开,
        而 `<img onClick>` 不可聚焦、读屏也不会报它是可交互的。
      */}
      <button
        type="button"
        onClick={() => {
          setZoomed(true);
        }}
        aria-label={t("chat.zoomImage")}
        className="block cursor-zoom-in"
      >
        <img
          src={ncwPreviewUrl(dataRef)}
          alt=""
          data-testid="message-image"
          loading="lazy"
          onError={() => {
            setFailed(true);
          }}
          /*
            ★ 尺寸上限是必须的:一张 4000px 宽的截图会把消息气泡撑破,
            而 `max-w-full` 只管宽度 —— 竖长图仍会占满整屏往下推。
            两个方向都要限,`object-contain` 保证不变形。用户附件另收紧到 96px 高，
            不影响助手消息的 320px 上限。
          */
          className={cn(
            "max-w-full rounded-card border border-stroke object-contain",
            compact ? "max-h-24 max-w-36" : "my-1 max-h-[320px]"
          )}
        />
      </button>

      {zoomed && (
        <ImageLightbox
          images={group}
          startIndex={index}
          {...(workspaceId === undefined ? {} : { workspaceId })}
          onClose={() => {
            setZoomed(false);
          }}
        />
      )}
    </>
  );
}

function ImagePlaceholder({
  label,
  hint,
}: {
  label: string;
  hint: string;
}): ReactNode {
  return (
    <div
      data-testid="message-image-placeholder"
      className={cn(
        "my-1 inline-flex items-center gap-2 rounded-card border border-stroke",
        "bg-tint/50 px-2.5 py-2 text-[12px] text-fg-faint",
      )}
    >
      <ImageOff size={14} className="shrink-0" />
      <span className="max-w-[240px] truncate">{label}</span>
      <span className="shrink-0 text-fg-faint/70">{hint}</span>
    </div>
  );
}

function fileNameOf(path: string): string {
  const i = Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\"));
  return i < 0 ? path : path.slice(i + 1);
}
