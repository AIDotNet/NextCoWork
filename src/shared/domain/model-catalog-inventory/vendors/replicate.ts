import type { BuiltinModelRecord } from '../types'
import { model, videoCapabilities } from '../helpers'

/**
 * Replicate 上的视频模型。
 *
 * ★ id 是 `{owner}/{name}` 形态 —— 那是 Replicate 的模型标识,不是我们的别名。
 * ★ 输入由每个模型的 `openapi_schema` 决定,输出可能是 string / array / object,
 * 所以适配器按 schema 映射,目录这里一个能力位都不猜。
 */
export const REPLICATE: readonly BuiltinModelRecord[] = [
  model('replicate', 'minimax/video-01', 'MiniMax Video-01(Replicate)', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://replicate.com/minimax/video-01', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
  model('replicate', 'wan-video/wan-2.2-t2v-fast', 'Wan 2.2 T2V Fast(Replicate)', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://replicate.com/wan-video/wan-2.2-t2v-fast', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
]
