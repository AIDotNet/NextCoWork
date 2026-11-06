import type { BuiltinModelRecord } from '../types'
import type { ModelCapabilities } from '../../provider'
import { model, visionCapabilities, effortThinking, source , videoCapabilities } from '../helpers'

const geminiStandardCapabilities = (fileInput = true): ModelCapabilities =>
  visionCapabilities({
    thinking: true,
    fileInput,
    videoInput: true,
    audioInput: true,
    tools: true,
    caching: true,
    webSearch: true,
    structuredOutput: true,
    streaming: true,
    batch: true,
  })

export const GOOGLE: readonly BuiltinModelRecord[] = [
  model('google', 'gemini-4-argon', 'Gemini 4 Argon', {
    capabilities: geminiStandardCapabilities(),
    contextWindow: 1_048_576,
    maxOutputTokens: 65_536,
    thinkingConfig: effortThinking('reasoning_effort', 'medium'),
    reasoningEfforts: ['low', 'medium', 'high'],
    source: source('https://ai.google.dev/gemini-api/docs/models/gemini-4-argon'),
    verificationStatus: 'official-api',
  }),
  model('google', 'gemini-3.8-flash', 'Gemini 3.8 Flash', {
    capabilities: geminiStandardCapabilities(),
    contextWindow: 1_048_576,
    maxOutputTokens: 65_536,
    thinkingConfig: effortThinking('reasoning_effort', 'medium'),
    reasoningEfforts: ['low', 'medium', 'high'],
    source: source('https://ai.google.dev/gemini-api/docs/models/gemini-3.8-flash'),
    verificationStatus: 'official-api',
  }),
  model('google', 'gemini-3.7-flash', 'Gemini 3.7 Flash', {
    capabilities: geminiStandardCapabilities(),
    contextWindow: 1_048_576,
    maxOutputTokens: 65_536,
    thinkingConfig: effortThinking('reasoning_effort', 'medium'),
    reasoningEfforts: ['low', 'medium', 'high'],
    source: source('https://ai.google.dev/gemini-api/docs/models/gemini-3.7-flash'),
    verificationStatus: 'official-api',
  }),
  model('google', 'gemini-3.6-flash', 'Gemini 3.6 Flash', {
    capabilities: geminiStandardCapabilities(),
    contextWindow: 1_048_576,
    maxOutputTokens: 65_536,
    thinkingConfig: effortThinking('reasoning_effort', 'medium'),
    reasoningEfforts: ['minimal', 'low', 'medium', 'high'],
    source: source('https://ai.google.dev/gemini-api/docs/models/gemini-3.6-flash'),
    verificationStatus: 'official-api',
  }),
  model('google', 'gemini-3.5-flash', 'Gemini 3.5 Flash', {
    capabilities: geminiStandardCapabilities(),
    contextWindow: 1_048_576,
    maxOutputTokens: 65_536,
    thinkingConfig: effortThinking('reasoning_effort', 'medium'),
    reasoningEfforts: ['minimal', 'low', 'medium', 'high'],
    source: source('https://ai.google.dev/gemini-api/docs/models/gemini-3.5-flash'),
    verificationStatus: 'official-api',
  }),
  model('google', 'gemini-3.5-flash-lite', 'Gemini 3.5 Flash-Lite', {
    capabilities: geminiStandardCapabilities(),
    contextWindow: 1_048_576,
    maxOutputTokens: 65_536,
    thinkingConfig: effortThinking('reasoning_effort', 'minimal'),
    reasoningEfforts: ['minimal', 'low', 'medium', 'high'],
    source: source('https://ai.google.dev/gemini-api/docs/models/gemini-3.5-flash-lite'),
    verificationStatus: 'official-api',
  }),
  model('google', 'gemini-3.1-pro-preview', 'Gemini 3.1 Pro Preview', {
    capabilities: geminiStandardCapabilities(),
    contextWindow: 1_048_576,
    maxOutputTokens: 65_536,
    thinkingConfig: effortThinking('reasoning_effort', 'high'),
    reasoningEfforts: ['low', 'medium', 'high'],
    aliases: ['gemini-3.1-pro'],
    source: source('https://ai.google.dev/gemini-api/docs/models/gemini-3.1-pro-preview'),
    verificationStatus: 'official-api',
  }),
  model('google', 'gemini-3.1-flash-lite', 'Gemini 3.1 Flash-Lite', {
    capabilities: geminiStandardCapabilities(),
    contextWindow: 1_048_576,
    maxOutputTokens: 65_536,
    thinkingConfig: effortThinking('reasoning_effort', 'minimal'),
    reasoningEfforts: ['minimal', 'low', 'medium', 'high'],
    source: source('https://ai.google.dev/gemini-api/docs/models/gemini-3.1-flash-lite'),
    verificationStatus: 'official-api',
  }),
  model('google', 'gemini-3-flash-preview', 'Gemini 3 Flash Preview', {
    capabilities: geminiStandardCapabilities(),
    contextWindow: 1_048_576,
    maxOutputTokens: 65_536,
    thinkingConfig: effortThinking('reasoning_effort', 'high'),
    reasoningEfforts: ['minimal', 'low', 'medium', 'high'],
    source: source('https://ai.google.dev/gemini-api/docs/models/gemini-3-flash-preview'),
    verificationStatus: 'official-api',
  }),
  model('google', 'gemini-2.5-pro', 'Gemini 2.5 Pro', {
    capabilities: geminiStandardCapabilities(),
    contextWindow: 1_048_576,
    maxOutputTokens: 65_536,
    thinkingConfig: effortThinking('reasoning_effort', 'medium'),
    reasoningEfforts: ['low', 'medium', 'high'],
    source: source('https://ai.google.dev/gemini-api/docs/models/gemini-2.5-pro'),
    verificationStatus: 'official-api',
  }),
  model('google', 'gemini-2.5-flash', 'Gemini 2.5 Flash', {
    capabilities: geminiStandardCapabilities(),
    contextWindow: 1_048_576,
    maxOutputTokens: 65_536,
    thinkingConfig: effortThinking('reasoning_effort', 'medium'),
    reasoningEfforts: ['none', 'low', 'medium', 'high'],
    source: source('https://ai.google.dev/gemini-api/docs/models/gemini-2.5-flash'),
    verificationStatus: 'official-api',
  }),
  model('google', 'gemini-2.5-flash-lite', 'Gemini 2.5 Flash-Lite', {
    capabilities: geminiStandardCapabilities(),
    contextWindow: 1_048_576,
    maxOutputTokens: 65_536,
    thinkingConfig: {
      mode: 'effort',
      defaultEnabled: false,
      defaultEffort: 'medium',
      parameterPath: 'reasoning_effort',
    },
    reasoningEfforts: ['none', 'low', 'medium', 'high'],
    source: source('https://ai.google.dev/gemini-api/docs/models/gemini-2.5-flash-lite'),
    verificationStatus: 'official-api',
  }),
  model('google', 'gemma-3-27b-it', 'Gemma 3 27B', {
    capabilities: visionCapabilities({ tools: false }),
    source: source('https://ai.google.dev/gemma/docs/core/model_card_3'),
    verificationStatus: 'official-model-card',
  }),
  model('google', 'gemma-3-12b-it', 'Gemma 3 12B', {
    capabilities: visionCapabilities({ tools: false }),
    source: source('https://ai.google.dev/gemma/docs/core/model_card_3'),
    verificationStatus: 'official-model-card',
  }),
  model('google', 'gemma-3-4b-it', 'Gemma 3 4B', {
    capabilities: visionCapabilities({ tools: false }),
    source: source('https://ai.google.dev/gemma/docs/core/model_card_3'),
    verificationStatus: 'official-model-card',
  }),
  /*
   * 需求:视频档案表(`shared/domain/video-profiles.ts`)按**目录型号**查绑定,所以
   * Veo 的规范 id 必须在目录里。原先全表没有任何 modality: 'video' 的 Google 条目 ——
   * 症状是设置页视频那一栏认不出用户绑的 veo-* 别名,把它当文本模型。
   *
   * ★ 这几行**只贡献目录条目与模态**:Veo 走原生的 predictLongRunning,不是
   * chat/responses,工具调用、结构化输出对它们没有意义。按「秒 / 分辨率」计费,
   * TokenRates(每百万 token)表达不了 —— 定价种子表查不到价显示「—」,是既定方向。
   * ★ `gemini-omni-flash` 是**文本与视频共用**的型号名,但视频走 Interactions API。
   * 这里只登记它的视频线(单独一条 video profile),文本那条仍由既有的
   * gemini-* 条目负责 —— 两者靠 provider 连接区分,不靠名字。
   */
  model('google', 'veo-3.1-generate-preview', 'Veo 3.1', {
    modality: 'video',
    capabilities: videoCapabilities(),
    aliases: ['veo-3.1'],
    source: source('https://ai.google.dev/gemini-api/docs/veo'),
    verificationStatus: 'official-api',
  }),
  model('google', 'veo-3.1-fast-generate-preview', 'Veo 3.1 Fast', {
    modality: 'video',
    capabilities: videoCapabilities(),
    aliases: ['veo-3.1-fast'],
    source: source('https://ai.google.dev/gemini-api/docs/veo'),
    verificationStatus: 'official-api',
  }),
  model('google', 'veo-3.1-lite-generate-preview', 'Veo 3.1 Lite', {
    modality: 'video',
    capabilities: videoCapabilities(),
    aliases: ['veo-3.1-lite'],
    source: source('https://ai.google.dev/gemini-api/docs/veo'),
    verificationStatus: 'official-api',
  }),
  model('google', 'gemini-omni-flash', 'Gemini Omni Flash', {
    modality: 'video',
    capabilities: videoCapabilities(),
    source: source('https://ai.google.dev/gemini-api/docs/video'),
    verificationStatus: 'official-api',
  }),
]
