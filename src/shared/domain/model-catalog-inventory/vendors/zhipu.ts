import type { BuiltinModelRecord } from '../types'
import { model, textCapabilities, visionCapabilities, toggleThinking, effortThinking, budgetThinking, efforts, source , videoCapabilities } from '../helpers'

export const ZHIPU: readonly BuiltinModelRecord[] = [
  /*
   * ★ 2026-09-18 修正:上下文 1M,思考从 toggle 改成 effort 档位(low/high/max,
   * 对齐 flash 那行 official-api 核实的档位表)。之前吃 200_000 兜底 + toggle 线形,
   * 表现有二:窗口被压成 200K;zcode 订阅里选 5.3 只有「开/关」没有强度滑杆
   * (`modelThinkingLevels` 对 toggle 只给 auto/开/关三档)。线上形态由 thinking-adapter
   * 的智谱方言负责:开时 `thinking:{type:'enabled'}` + `reasoning_effort`,按量端点
   * 与 coding 订阅端点同吃这一份目录条目。
   */
  model('zhipu', 'glm-5.3', 'GLM-5.3', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    thinkingConfig: effortThinking('reasoning_effort', 'max'),
    reasoningEfforts: ['low', 'high', 'max'],
  }),
  model('zhipu', 'glm-5.3-flash', 'GLM-5.3-Flash', {
    capabilities: visionCapabilities({
      thinking: true,
      fileInput: true,
      videoInput: true,
      tools: true,
      caching: true,
      structuredOutput: true,
      streaming: true,
    }),
    contextWindow: 1_000_000,
    maxOutputTokens: 128_000,
    thinkingConfig: effortThinking('reasoning_effort', 'max'),
    reasoningEfforts: ['low', 'high', 'max'],
    source: source('https://docs.z.ai/guides/vlm/glm-5.3-flash'),
    verificationStatus: 'official-api',
  }),
  model('zhipu', 'glm-5.2', 'GLM-5.2', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('thinking.type'),
    reasoningEfforts: efforts,
  }),
  model('zhipu', 'glm-5.1', 'GLM-5.1', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('thinking.type'),
    reasoningEfforts: efforts,
  }),
  model('zhipu', 'glm-5', 'GLM-5', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('thinking.type'),
    reasoningEfforts: efforts,
  }),
  model('zhipu', 'glm-4.7', 'GLM-4.7', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('thinking.type'),
    reasoningEfforts: efforts,
  }),
  model('zhipu', 'glm-4.7-flashx', 'GLM-4.7-FlashX', {
    capabilities: textCapabilities({
      thinking: true,
      tools: true,
      caching: true,
      structuredOutput: true,
      streaming: true,
    }),
    thinkingConfig: toggleThinking('thinking.type'),
    source: source('https://docs.z.ai/guides/overview/pricing.md'),
    verificationStatus: 'official-api',
  }),
  model('zhipu', 'glm-4.6', 'GLM-4.6', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('thinking.type'),
    reasoningEfforts: efforts,
  }),
  model('zhipu', 'glm-4.5', 'GLM-4.5', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('thinking.type'),
    reasoningEfforts: efforts,
  }),
  model('zhipu', 'glm-4.5-x', 'GLM-4.5-X', {
    capabilities: textCapabilities({
      thinking: true,
      tools: true,
      caching: true,
      structuredOutput: true,
      streaming: true,
    }),
    thinkingConfig: toggleThinking('thinking.type'),
    source: source('https://docs.z.ai/guides/overview/pricing.md'),
    verificationStatus: 'official-api',
  }),
  model('zhipu', 'glm-4.5-air', 'GLM-4.5-Air', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('thinking.type'),
    reasoningEfforts: efforts,
  }),
  model('zhipu', 'glm-4.5-airx', 'GLM-4.5-AirX', {
    capabilities: textCapabilities({
      thinking: true,
      tools: true,
      caching: true,
      structuredOutput: true,
      streaming: true,
    }),
    thinkingConfig: toggleThinking('thinking.type'),
    source: source('https://docs.z.ai/guides/overview/pricing.md'),
    verificationStatus: 'official-api',
  }),
  model('zhipu', 'glm-4-32b-0414-128k', 'GLM-4 32B 0414 128K', {
    capabilities: textCapabilities({
      tools: true,
      structuredOutput: true,
      streaming: true,
    }),
    contextWindow: 128_000,
    source: source('https://docs.z.ai/guides/overview/pricing.md'),
    verificationStatus: 'official-api',
  }),
  model('zhipu', 'glm-4.6v', 'GLM-4.6V', {
    capabilities: visionCapabilities({
      thinking: true,
      fileInput: true,
      videoInput: true,
      tools: true,
      caching: true,
      structuredOutput: true,
      streaming: true,
    }),
    thinkingConfig: toggleThinking('thinking.type'),
    source: source('https://docs.z.ai/guides/overview/pricing.md'),
    verificationStatus: 'official-api',
  }),
  model('zhipu', 'glm-ocr', 'GLM-OCR', {
    capabilities: visionCapabilities({
      tools: false,
      fileInput: true,
      structuredOutput: true,
      streaming: true,
    }),
    source: source('https://docs.z.ai/guides/overview/pricing.md'),
    verificationStatus: 'official-api',
  }),
  model('zhipu', 'glm-4.6v-flashx', 'GLM-4.6V-FlashX', {
    capabilities: visionCapabilities({
      thinking: true,
      fileInput: true,
      videoInput: true,
      tools: true,
      caching: true,
      structuredOutput: true,
      streaming: true,
    }),
    thinkingConfig: toggleThinking('thinking.type'),
    source: source('https://docs.z.ai/guides/overview/pricing.md'),
    verificationStatus: 'official-api',
  }),
  model('zhipu', 'glm-4.5v', 'GLM-4.5V', {
    capabilities: visionCapabilities({ tools: false, thinking: true }),
    thinkingConfig: toggleThinking('thinking.type'),
  }),
  model('zhipu', 'glm-4v-plus', 'GLM-4V-Plus', {
    capabilities: visionCapabilities({ tools: false }),
  }),
  model('zhipu', 'glm-4-air', 'GLM-4-Air', {
    capabilities: textCapabilities(),
  }),
  model('zhipu', 'glm-4-flash', 'GLM-4-Flash', {
    capabilities: textCapabilities(),
  }),
  model('zhipu', 'glm-z1-air', 'GLM-Z1-Air', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: budgetThinking('thinking.budget_tokens', 16_384),
  }),
  /*
   * 需求:视频档案表按目录型号查绑定。CogVideoX 走 `/api/paas/v4/videos/generations`
   * (异步),Vidu 系是智谱**托管**的第三方型号,参数与 CogVideoX 完全不同 ——
   * 所以是两条 profile。
   * ★ 视频走**按量 API**,Coding Plan 的额度不包含它;目录里可见不代表订阅能用。
   */
  model('zhipu', 'cogvideox-3', 'CogVideoX-3', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: { url: 'https://docs.bigmodel.cn/cn/guide/models/video-generation/cogvideox-3', fetchedAt: '2026-10-05' },
    verificationStatus: 'official-api',
  }),
  model('zhipu', 'cogvideox-2', 'CogVideoX-2', {
    modality: 'video',
    capabilities: videoCapabilities(),
  }),
  model('zhipu', 'cogvideox-flash', 'CogVideoX-Flash', {
    modality: 'video',
    capabilities: videoCapabilities(),
  }),
  model('zhipu', 'viduq1-text', 'Vidu Q1 文生视频', {
    modality: 'video',
    capabilities: videoCapabilities(),
  }),
  model('zhipu', 'viduq1-image', 'Vidu Q1 图生视频', {
    modality: 'video',
    capabilities: videoCapabilities(),
  }),
  model('zhipu', 'viduq1-start-end', 'Vidu Q1 首尾帧', {
    modality: 'video',
    capabilities: videoCapabilities(),
  }),
]
