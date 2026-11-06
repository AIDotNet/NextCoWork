import type { BuiltinModelRecord } from '../types'
import { model, videoCapabilities } from '../helpers'

/**
 * fal.ai 的托管视频 endpoint。
 *
 * ★★ **这里的每一条 id 都是一个 endpoint 路径,不是一个"模型家族"** ——
 * `fal-ai/veo3` 与 `fal-ai/kling-video/v2/master/text-to-video` 是两条独立
 * 可调用的路径。目录登记它们是为了让用户在视频页看得见并显式绑定;具体输入
 * schema 由 `video-profiles.ts` 的队列协议负责,不进目录。
 * ★ 只收**本次核对过在售**的那几条;fal 有上千个 endpoint,穷举既无意义也会腐烂。
 */
export const FAL: readonly BuiltinModelRecord[] = [
  model('fal', 'fal-ai/veo3', 'Veo 3(fal)', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://fal.ai/models/fal-ai/veo3', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
  model('fal', 'fal-ai/kling-video/v2/master/text-to-video', 'Kling v2 Master(fal)', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://fal.ai/models/fal-ai/kling-video', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
  model('fal', 'fal-ai/minimax/hailuo-02/standard/text-to-video', 'Hailuo 02 Standard(fal)', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://fal.ai/models/fal-ai/minimax/hailuo-02', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
]
