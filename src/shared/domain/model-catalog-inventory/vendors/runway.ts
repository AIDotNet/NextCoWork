import type { BuiltinModelRecord } from '../types'
import { model, videoCapabilities } from '../helpers'

/**
 * Runway 的视频型号。
 *
 * ★ 目录只收**官方当前在售**的 id。官方文档明确点名 `gen3a_turbo` 与
 * `gen4_aleph` 已下线、请求直接报错 —— 把它们写进目录等于给用户画一个点了必炸的选项。
 * ★ 请求体是**按型号区分的可辨识联合**(每个型号的 ratio / duration 合法值都不同),
 * 所以能力与参数的真相在 `shared/domain/video-profiles.ts`,这里只贡献目录条目与模态。
 */
export const RUNWAY: readonly BuiltinModelRecord[] = [
  model('runway', 'gen4.5', 'Runway Gen-4.5', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://docs.dev.runwayml.com/guides/models.md', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
  model('runway', 'gen4_turbo', 'Runway Gen-4 Turbo', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://docs.dev.runwayml.com/guides/models.md', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
]
