import type { BuiltinModelRecord } from '../types'
import { model, textCapabilities, visionCapabilities, source , videoCapabilities } from '../helpers'

export const MINIMAX: readonly BuiltinModelRecord[] = [
  model('minimax', 'MiniMax-M3', 'MiniMax M3', {
    capabilities: visionCapabilities({ thinking: true, videoInput: true }),
    thinkingConfig: {
      mode: 'toggle',
      defaultEnabled: true,
      parameterPath: 'thinking',
      enabledValue: { type: 'adaptive' },
      disabledValue: { type: 'disabled' },
    },
    aliases: ['minimax-m3'],
    source: source('https://platform.minimax.io/docs/api-reference/text-chat-openai'),
    verificationStatus: 'official-api',
  }),
  model('minimax', 'MiniMax-M2.7', 'MiniMax M2.7', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: { mode: 'always', defaultEnabled: true },
    aliases: ['minimax-m2.7'],
    source: source('https://platform.minimax.io/docs/api-reference/text-chat-openai'),
    verificationStatus: 'official-api',
  }),
  model('minimax', 'MiniMax-M2.7-highspeed', 'MiniMax M2.7 Highspeed', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: { mode: 'always', defaultEnabled: true },
    aliases: ['minimax-m2.7-highspeed'],
    source: source('https://platform.minimax.io/docs/guides/pricing-paygo'),
    verificationStatus: 'official-api',
  }),
  model('minimax', 'MiniMax-M2.5', 'MiniMax M2.5', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: { mode: 'always', defaultEnabled: true },
    aliases: ['minimax-m2.5'],
    source: source('https://platform.minimax.io/docs/api-reference/text-chat-openai'),
    verificationStatus: 'official-api',
  }),
  model('minimax', 'MiniMax-M2.5-highspeed', 'MiniMax M2.5 Highspeed', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: { mode: 'always', defaultEnabled: true },
    aliases: ['minimax-m2.5-highspeed'],
    source: source('https://platform.minimax.io/docs/guides/pricing-paygo'),
    verificationStatus: 'official-api',
  }),
  model('minimax', 'MiniMax-M2.1', 'MiniMax M2.1', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: { mode: 'always', defaultEnabled: true },
    aliases: ['minimax-m2.1'],
    source: source('https://platform.minimax.io/docs/api-reference/text-chat-openai'),
    verificationStatus: 'official-api',
  }),
  model('minimax', 'MiniMax-M2.1-highspeed', 'MiniMax M2.1 Highspeed', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: { mode: 'always', defaultEnabled: true },
    aliases: ['minimax-m2.1-highspeed'],
    source: source('https://platform.minimax.io/docs/guides/pricing-paygo'),
    verificationStatus: 'official-api',
  }),
  model('minimax', 'MiniMax-M2', 'MiniMax M2', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: { mode: 'always', defaultEnabled: true },
    aliases: ['minimax-m2'],
    source: source('https://platform.minimax.io/docs/api-reference/text-chat-openai'),
    verificationStatus: 'official-api',
  }),
  model('minimax', 'MiniMax-M2-her', 'MiniMax M2 her', {
    capabilities: textCapabilities({ tools: false, thinking: true }),
    thinkingConfig: { mode: 'always', defaultEnabled: true },
    aliases: ['minimax-m2-her'],
  }),
  model('minimax', 'MiniMax-M1', 'MiniMax M1', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: { mode: 'always', defaultEnabled: true },
    aliases: ['minimax-m1'],
  }),
  model('minimax', 'abab6.5s-chat', 'abab 6.5s', {
    capabilities: visionCapabilities(),
    aliases: ['abab6.5s'],
  }),
  model('minimax', 'abab6.5-chat', 'abab 6.5', {
    capabilities: textCapabilities({ tools: false }),
  }),
  model('minimax', 'MiniMax-VL-01', 'MiniMax VL 01', {
    capabilities: visionCapabilities(),
  }),
  model('minimax', 'MiniMax-Text-01', 'MiniMax Text 01', {
    capabilities: textCapabilities(),
  }),
  model('minimax', 'MiniMax-01', 'MiniMax 01', {
    capabilities: textCapabilities({ tools: false }),
  }),
  /*
   * 需求:视频档案表按目录型号查绑定。H3 / H3 Max 走 `/v2/video_generation`,
   * Hailuo 系列走 v1(成功后还要按 file_id 再取一次下载地址)—— 两条 profile,
   * 这里只负责让型号在目录里认得出。
   */
  model('minimax', 'MiniMax-H3', 'MiniMax H3', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://platform.minimax.io/docs/api-reference/video-generation-v2-create', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
  model('minimax', 'MiniMax-H3-Max', 'MiniMax H3 Max', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://platform.minimax.io/docs/api-reference/video-generation-v2-create', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
  model('minimax', 'MiniMax-Hailuo-2.3', 'MiniMax Hailuo 2.3', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://platform.minimax.io/docs/api-reference/video-generation-t2v', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
  model('minimax', 'MiniMax-Hailuo-02', 'MiniMax Hailuo 02', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://platform.minimax.io/docs/api-reference/video-generation-t2v', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
  model('minimax', 'T2V-01', 'MiniMax T2V-01', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://platform.minimax.io/docs/api-reference/video-generation-t2v', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
  model('minimax', 'T2V-01-Director', 'MiniMax T2V-01 Director', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://platform.minimax.io/docs/api-reference/video-generation-t2v', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
]
