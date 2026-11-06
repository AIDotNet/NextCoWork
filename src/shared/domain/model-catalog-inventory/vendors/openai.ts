import type { BuiltinModelRecord } from '../types'
import { model, textCapabilities, visionCapabilities, imageCapabilities, videoCapabilities, effortThinking, efforts, openAiModernEfforts, openAiAstraEfforts } from '../helpers'

export const OPENAI: readonly BuiltinModelRecord[] = [
  /*
   * 需求:2026-09-29 用户点名收录 gpt-6.1-sol。窗口 / 最大输出 / effort 集合这次
   * 有**一手依据**(官方 model 页当日核对):1,050,000 / 128,000 /
   * reasoning.effort = low|medium(默认)|high|xhigh|max,不支持 none / minimal
   * —— 与 openAiAstraEfforts 逐值相同。不满足会怎样见下面 gpt-6-sol 那条注释。
   * 费率录在 `pricing-seed.ts` 的 OpenAI 段。
   */
  model('openai', 'gpt-6.1-sol', 'GPT-6.1 Sol', {
    capabilities: visionCapabilities({
      thinking: true,
      webSearch: true,
      batch: true,
    }),
    contextWindow: 1_050_000,
    maxOutputTokens: 128_000,
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: openAiAstraEfforts,
    source: { url: 'https://developers.openai.com/api/docs/models/gpt-6.1-sol', fetchedAt: '2026-09-29' },
    verificationStatus: 'official-api',
  }),
  model('openai', 'gpt-6-astra', 'GPT-6 Astra', {
    capabilities: visionCapabilities({
      thinking: true,
      webSearch: true,
      batch: true,
    }),
    contextWindow: 1_050_000,
    maxOutputTokens: 128_000,
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: openAiAstraEfforts,
    verificationStatus: 'official-api',
  }),
  /*
   * 需求:2026-09-23 收录 GPT-6 的两个平价型号,id 与费率来自官方 pricing 页
   * (用户提供的截图,费率录在 `pricing-seed.ts` 的 OpenAI 段)。
   * ★ 窗口 / 最大输出 / effort 集合**没有一手依据**,是按同代 gpt-6-astra 推的 ——
   * 同族同代是本仓库既有的推法(Anthropic 段的 id 也是这么推的)。
   * 不满足会怎样:effort 集合里出现上游不认的取值,表现为一次可读的 400;
   * 窗口推大了才会静默(上游 400 由 `validateModelRuntime` 兜,不是无声失败),
   * 所以真要改这两项,先拿官方 model 页来核,别照本文件其他行「对齐」。
   */
  model('openai', 'gpt-6-sol', 'GPT-6 Sol', {
    capabilities: visionCapabilities({
      thinking: true,
      webSearch: true,
      batch: true,
    }),
    contextWindow: 1_050_000,
    maxOutputTokens: 128_000,
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: openAiAstraEfforts,
    verificationStatus: 'official-api',
  }),
  model('openai', 'gpt-6-luna', 'GPT-6 Luna', {
    capabilities: visionCapabilities({
      thinking: true,
      webSearch: true,
      batch: true,
    }),
    contextWindow: 1_050_000,
    maxOutputTokens: 128_000,
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: openAiAstraEfforts,
    verificationStatus: 'official-api',
  }),
  model('openai', 'gpt-5.6-sol', 'GPT-5.6 Sol', {
    capabilities: visionCapabilities({
      thinking: true,
      webSearch: true,
      batch: true,
    }),
    contextWindow: 1_050_000,
    maxOutputTokens: 128_000,
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: openAiModernEfforts,
    aliases: ['gpt-5.6'],
    verificationStatus: 'official-api',
  }),
  model('openai', 'gpt-5.6-terra', 'GPT-5.6 Terra', {
    capabilities: visionCapabilities({
      thinking: true,
      webSearch: true,
      batch: true,
    }),
    contextWindow: 1_050_000,
    maxOutputTokens: 128_000,
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: openAiModernEfforts,
    verificationStatus: 'official-api',
  }),
  model('openai', 'gpt-5.6-luna', 'GPT-5.6 Luna', {
    capabilities: visionCapabilities({
      thinking: true,
      webSearch: true,
      batch: true,
    }),
    contextWindow: 1_050_000,
    maxOutputTokens: 128_000,
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: openAiModernEfforts,
    verificationStatus: 'official-api',
  }),
  model('openai', 'gpt-5.6-cyber', 'GPT-5.6 Cyber', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    contextWindow: 400_000,
    maxOutputTokens: 128_000,
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: openAiAstraEfforts,
    verificationStatus: 'official-api',
  }),
  model('openai', 'gpt-5.5', 'GPT-5.5', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5.5-pro', 'GPT-5.5 Pro', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5.4', 'GPT-5.4', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5.4-mini', 'GPT-5.4 mini', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5.4-nano', 'GPT-5.4 nano', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5.4-pro', 'GPT-5.4 Pro', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5.2', 'GPT-5.2', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5.2-pro', 'GPT-5.2 Pro', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5.3-codex', 'GPT-5.3 Codex', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5.3-chat-latest', 'GPT-5.3 Chat', {
    capabilities: visionCapabilities({ webSearch: true }),
  }),
  model('openai', 'gpt-5.2-codex', 'GPT-5.2 Codex', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5.2-chat-latest', 'GPT-5.2 Chat', {
    capabilities: visionCapabilities({ webSearch: true }),
  }),
  model('openai', 'gpt-5.1', 'GPT-5.1', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5.1-codex', 'GPT-5.1 Codex', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5.1-codex-mini', 'GPT-5.1 Codex Mini', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5.1-codex-max', 'GPT-5.1 Codex Max', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5.1-chat-latest', 'GPT-5.1 Chat', {
    capabilities: visionCapabilities({ webSearch: true }),
  }),
  model('openai', 'gpt-5', 'GPT-5', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5-mini', 'GPT-5 mini', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5-nano', 'GPT-5 nano', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5-pro', 'GPT-5 Pro', {
    capabilities: visionCapabilities({ thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5-codex', 'GPT-5 Codex', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-5-chat-latest', 'GPT-5 Chat', {
    capabilities: visionCapabilities({ webSearch: true }),
  }),
  model('openai', 'gpt-4.1', 'GPT-4.1', {
    capabilities: visionCapabilities({ webSearch: true }),
  }),
  model('openai', 'gpt-4.1-mini', 'GPT-4.1 mini', {
    capabilities: visionCapabilities({ webSearch: true }),
  }),
  model('openai', 'gpt-4.1-nano', 'GPT-4.1 nano', {
    capabilities: visionCapabilities({ webSearch: true }),
  }),
  model('openai', 'gpt-4o', 'GPT-4o', {
    capabilities: visionCapabilities({ webSearch: true }),
  }),
  model('openai', 'gpt-4o-mini', 'GPT-4o mini', {
    capabilities: visionCapabilities({ webSearch: true }),
  }),
  model('openai', 'o1', 'o1', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'o1-pro', 'o1 Pro', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'o3', 'o3', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'o3-pro', 'o3 Pro', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'o3-mini', 'o3 mini', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'o3-deep-research', 'o3 Deep Research', {
    capabilities: visionCapabilities({ tools: false, thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'o4-mini', 'o4 mini', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'o4-mini-deep-research', 'o4 Mini Deep Research', {
    capabilities: visionCapabilities({ tools: false, thinking: true, webSearch: true }),
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'o1-mini', 'o1 Mini', {
    capabilities: textCapabilities({ tools: false, thinking: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'o1-preview', 'o1 Preview', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'gpt-4-turbo', 'GPT-4 Turbo', {
    capabilities: visionCapabilities(),
  }),
  model('openai', 'gpt-4-turbo-2024-04-09', 'GPT-4 Turbo 2024-04-09', {
    capabilities: visionCapabilities(),
  }),
  model('openai', 'gpt-4', 'GPT-4'),
  model('openai', 'gpt-4-0613', 'GPT-4 0613'),
  model('openai', 'gpt-3.5-turbo', 'GPT-3.5 Turbo'),
  model('openai', 'gpt-oss-120b', 'GPT OSS 120B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: ['low', 'medium', 'high'],
  }),
  model('openai', 'gpt-oss-20b', 'GPT OSS 20B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: ['low', 'medium', 'high'],
  }),
  model('openai', 'codex-mini-latest', 'Codex Mini', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: effortThinking('reasoning.effort'),
    reasoningEfforts: efforts,
  }),
  model('openai', 'computer-use-preview', 'Computer Use Preview', {
    capabilities: visionCapabilities({ tools: true }),
  }),
  model('openai', 'gpt-4o-search-preview', 'GPT-4o Search Preview', {
    capabilities: visionCapabilities({ tools: false, webSearch: true }),
  }),
  model('openai', 'gpt-4o-mini-search-preview', 'GPT-4o Mini Search Preview', {
    capabilities: visionCapabilities({ tools: false, webSearch: true }),
  }),
]

export const OPENAI_MEDIA: readonly BuiltinModelRecord[] = [
  /*
   * 需求:把 GPT-Image-2.5 世代补进目录(2026-09-25 从官方模型页核对)。
   * 没有这两行的症状:「模型管理」里查不到它们,登录/拉列表拿到这两个 ID 时
   * `model-binding` 目录落空 —— modality 保持 text、imageOutput 保持 false,
   * 于是它们既不出现在「图片生成」页,也不会被识别成生图模型。
   *
   * ★ 两款只走 `v1/images/generations` 与 `v1/images/edits`,**chat/completions
   * 与 responses 都是 Not supported**(官方 Endpoints 表)—— 目录不记协议,
   * 发错端点的表现是上游一句 404/405,读不出和「这模型不走对话端点」的关系。
   * ★ 计价按 token(图输出 $30/1M),不是按张;`pricingModelId` 沿用 id。
   */
  model('openai', 'gpt-image-2.5-sunburst', 'GPT Image 2.5 Sunburst', {
    modality: 'image',
    capabilities: imageCapabilities({
      vision: true,
      visionInput: true,
      fileInput: true,
    }),
    source: { url: 'https://developers.openai.com/api/docs/models/gpt-image-2.5-sunburst', fetchedAt: '2026-09-25' },
    verificationStatus: 'official-api',
  }),
  model('openai', 'gpt-image-2.5-flare', 'GPT Image 2.5 Flare', {
    modality: 'image',
    capabilities: imageCapabilities({
      vision: true,
      visionInput: true,
      fileInput: true,
    }),
    source: { url: 'https://developers.openai.com/api/docs/models/gpt-image-2.5-flare', fetchedAt: '2026-09-25' },
    verificationStatus: 'official-api',
  }),
  model('openai', 'gpt-image-2', 'GPT Image 2', {
    modality: 'image',
    capabilities: imageCapabilities({
      vision: true,
      visionInput: true,
      fileInput: true,
    }),
    verificationStatus: 'official-api',
  }),
  model('openai', 'gpt-image-1.5', 'GPT Image 1.5', {
    modality: 'image',
    capabilities: imageCapabilities({
      vision: true,
      visionInput: true,
      fileInput: true,
    }),
    verificationStatus: 'official-api',
  }),
  model('openai', 'gpt-image-1', 'GPT Image 1', {
    modality: 'image',
    capabilities: imageCapabilities({
      vision: true,
      visionInput: true,
      fileInput: true,
    }),
    verificationStatus: 'official-api',
  }),
  model('openai', 'gpt-image-1-mini', 'GPT Image 1 Mini', {
    modality: 'image',
    capabilities: imageCapabilities({
      vision: true,
      visionInput: true,
      fileInput: true,
    }),
    verificationStatus: 'official-api',
  }),
  model('openai', 'chatgpt-image-latest', 'ChatGPT Image', {
    modality: 'image',
    capabilities: imageCapabilities({
      vision: true,
      visionInput: true,
      fileInput: true,
    }),
    verificationStatus: 'official-api',
  }),
  model('openai', 'sora-2', 'Sora 2', {
    modality: 'video',
    capabilities: videoCapabilities({
      vision: true,
      visionInput: true,
      audioOutput: true,
    }),
    verificationStatus: 'official-api',
  }),
  model('openai', 'sora-2-pro', 'Sora 2 Pro', {
    modality: 'video',
    capabilities: videoCapabilities({
      vision: true,
      visionInput: true,
      audioOutput: true,
    }),
    verificationStatus: 'official-api',
  }),
]
