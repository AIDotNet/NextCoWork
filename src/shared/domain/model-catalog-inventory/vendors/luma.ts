import type { BuiltinModelRecord } from '../types'
import { model, videoCapabilities } from '../helpers'

/**
 * Luma 的视频型号。
 *
 * ★ **两代 API 是两条连接**:Ray 3.2 在新的 Agents API(`agents.lumalabs.ai/v1`),
 * Ray 2 / Flash 仍在旧的 Dream Machine(`api.lumalabs.ai/dream-machine/v1`)。
 * 目录里它们是不同型号,档案里是不同 profile —— 升级版参数绝不会被发去旧端点。
 */
export const LUMA: readonly BuiltinModelRecord[] = [
  model('luma', 'ray-3.2', 'Luma Ray 3.2', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://docs.agents.lumalabs.ai/', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
  model('luma', 'ray-2', 'Luma Ray 2', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://docs.lumalabs.ai/docs/video-generation', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
  model('luma', 'ray-flash-2', 'Luma Ray 2 Flash', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://docs.lumalabs.ai/docs/video-generation', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
]
