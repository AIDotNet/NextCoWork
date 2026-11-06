import type { BuiltinModelRecord } from '../types'
import { model, videoCapabilities } from '../helpers'

/**
 * 可灵的视频型号。
 *
 * ★ 官方 Quick Start(本次核对)给的是 **Bearer API Key** + `api-singapore.klingai.com`;
 * 网上流传的 AK/SK JWT 是旧版教程。这里按当前口径登记。
 * ★ 逐型号的请求路径与参数**本次没有核对到正文** —— 对应 profile 标 `unverified`,
 * 设置页显示「接口待核对」,而不是给用户一个点了撞 404 的选项。
 */
export const KLING: readonly BuiltinModelRecord[] = [
  model('kling', 'kling-video-3.0', 'Kling 3.0', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://kling.ai/document-api/guides/get-started/overview', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-model-card',
  }),
  model('kling', 'kling-video-3.0-omni', 'Kling 3.0 Omni', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://kling.ai/document-api/guides/get-started/overview', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-model-card',
  }),
  model('kling', 'kling-video-3.0-turbo', 'Kling 3.0 Turbo', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://kling.ai/document-api/guides/get-started/overview', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-model-card',
  }),
]
