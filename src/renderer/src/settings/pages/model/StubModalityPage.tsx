import { AudioLines, Image, Mic, Video, type LucideIcon } from "lucide-react";
import type { ReactNode } from "react";
import type { Modality } from "../../../../../shared/domain/pricing";
import { EmptyState } from "../../../components/arc/empty-state/empty-state";
import { useI18n } from "../../../i18n";

/**
 * 四个铺出来但本轮没有内容的模态 Tab。照 `pages/StubPage.tsx` 的规矩:
 * **直说缺的是什么,不写「即将推出」。**
 *
 * ★ 四条理由**刻意不是同一句话**。共同的那半(编解码只有文本一条路)是真的,
 * 但另一半各不相同 —— `PriceTier` / `TokenRates` 的计价单位是「每百万 token」,
 * 它能表达图像的按次计费(`perCall` 字段已经留好),却表达不了视频的按秒、
 * 语音合成的按字符、语音识别的按分钟。把这条差别写出来,是因为将来接这几个
 * Tab 的人第一件要做的事就是**改定价的形状**,而不是再写一套编解码。
 *
 * ★ 只写计费的**单位**,一个价格数字都不写 —— 界面上的价必须来自
 * `pricing-seed.ts` 那张带 `source` 与 `fetchedAt` 的表。
 */
type DeadModality = Exclude<Modality, "text">;

const ICON: Record<DeadModality, LucideIcon> = {
  image: Image,
  video: Video,
  speech: AudioLines,
  transcription: Mic,
};

export function StubModalityPage({
  modality,
}: {
  modality: DeadModality;
}): ReactNode {
  const { t } = useI18n();
  const Icon = ICON[modality];
  return (
    <div className="flex min-h-0 flex-1 items-center justify-center">
      <EmptyState
        icon={<Icon size={26} />}
        title={t(`stub.${modality}.title` as "stub.image.title" | "stub.video.title" | "stub.speech.title" | "stub.transcription.title")}
        description={t(`stub.${modality}.hint` as "stub.image.hint" | "stub.video.hint" | "stub.speech.hint" | "stub.transcription.hint")}
      />
    </div>
  );
}
