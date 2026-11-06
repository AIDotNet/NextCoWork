/**
 * 适配器注册表 —— `VideoAdapterId` → 实现。
 *
 * ★ `Record<VideoAdapterId, …>` 而不是 `Partial<…>`:加一个新适配器 id 却忘了
 *   在这里登记,会让**编译期**报错,而不是运行时得到一句
 *   "no adapter for xyz"(那会表现为"这个供应商选了没反应")。
 *
 * ★ 未接通的适配器(硅基流动那条)**刻意不在这里** —— 它的 profile 是
 *   `actions: []`,永远走不到适配器。留一个抛错的桩会掩盖"其实还没实现",
 *   而缺席让它从一开始就不可选。
 */
import type { VideoAdapterId } from '../../../../shared/domain/video-generation'
import type { VideoAdapter } from './contract'
import { googleVeoAdapter } from './google'
import { xaiVideoAdapter } from './xai'
import { arkVideoAdapter } from './ark'
import { dashscopeVideoAdapter } from './dashscope'
import { minimaxVideoAdapter } from './minimax'
import { bigmodelVideoAdapter } from './bigmodel'
import { runwayVideoAdapter } from './runway'
import { lumaAgentsAdapter, lumaLegacyAdapter } from './luma'
import { falQueueAdapter } from './fal'
import { replicateAdapter } from './replicate'
import { awsBedrockVideoAdapter } from './aws'

/**
 * ★ 只有**本轮真的接通**的适配器进这张表。
 *   腾讯(vclm)有签名与鉴权骨架但没有核到字段形状,和硅基一样
 *   以 `actions: []` 表达"待核对",不在这里放一个发不出去的桩。
 */
export const VIDEO_ADAPTERS: Partial<Record<VideoAdapterId, VideoAdapter>> = {
  'google-veo': googleVeoAdapter,
  'xai-video': xaiVideoAdapter,
  'ark-video': arkVideoAdapter,
  'dashscope-video': dashscopeVideoAdapter,
  'minimax-video': minimaxVideoAdapter,
  'bigmodel-video': bigmodelVideoAdapter,
  'runway-video': runwayVideoAdapter,
  'luma-video': lumaAgentsAdapter,
  'luma-legacy-video': lumaLegacyAdapter,
  'fal-queue': falQueueAdapter,
  'replicate-predictions': replicateAdapter,
  'aws-bedrock-video': awsBedrockVideoAdapter
}

export function videoAdapterFor(id: VideoAdapterId): VideoAdapter | undefined {
  return VIDEO_ADAPTERS[id]
}

export { videoAdapterFor as adapterFor }
