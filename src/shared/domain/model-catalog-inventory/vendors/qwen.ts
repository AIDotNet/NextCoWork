import type { BuiltinModelRecord } from '../types'
import { model, textCapabilities, visionCapabilities, toggleThinking, effortThinking, alwaysThinking, efforts, OPENCODE_GO_SOURCE , videoCapabilities } from '../helpers'

export const QWEN: readonly BuiltinModelRecord[] = [
  model('qwen', 'qwen3.8-max', 'Qwen3.8-Max', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('qwen', 'qwen3.8-flash', 'Qwen3.8-Flash', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('qwen', 'qwen3.8-27b', 'Qwen3.8 27B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('qwen', 'qwen3.8-2.4t-a95b', 'Qwen3.8 2.4T-A95B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: effortThinking('reasoning_effort'),
    reasoningEfforts: efforts,
  }),
  model('qwen', 'qwen3.7-max', 'Qwen3.7-Max', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3.7-plus', 'Qwen3.7-Plus', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3-max', 'Qwen3-Max', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3-max-thinking', 'Qwen3-Max Thinking', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: alwaysThinking(),
    pricingModelId: 'qwen3-max',
  }),
  model('qwen', 'qwen3-plus', 'Qwen3-Plus', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3-turbo', 'Qwen3-Turbo', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3.7-flash', 'Qwen3.7-Flash', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3.6-max-preview', 'Qwen3.6-Max Preview', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3.6-plus', 'Qwen3.6-Plus', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3.6-flash', 'Qwen3.6-Flash', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3.6-35b-a3b', 'Qwen3.6 35B-A3B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3.6-27b', 'Qwen3.6 27B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3.5-plus', 'Qwen3.5 Plus', {
    capabilities: visionCapabilities({
      thinking: true,
      videoInput: true,
      tools: true,
    }),
    contextWindow: 262_144,
    maxOutputTokens: 65_536,
    thinkingConfig: toggleThinking('enable_thinking'),
    source: OPENCODE_GO_SOURCE,
    verificationStatus: 'aggregator-reference',
  }),
  model('qwen', 'qwen3.5-plus-2026-04-20', 'Qwen3.5-Plus 2026-04-20', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
    pricingModelId: 'qwen3.5-plus',
    aliases: ['qwen3.5-plus-20260420'],
  }),
  model('qwen', 'qwen3.5-plus-2026-02-15', 'Qwen3.5-Plus 2026-02-15', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
    pricingModelId: 'qwen3.5-plus',
    aliases: ['qwen3.5-plus-02-15'],
  }),
  model('qwen', 'qwen3.5-flash', 'Qwen3.5-Flash', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3.5-flash-2026-02-23', 'Qwen3.5-Flash 2026-02-23', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
    pricingModelId: 'qwen3.5-flash',
    aliases: ['qwen3.5-flash-02-23'],
  }),
  model('qwen', 'qwen3.5-397b-a17b', 'Qwen3.5 397B-A17B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3.5-122b-a10b', 'Qwen3.5 122B-A10B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3.5-35b-a3b', 'Qwen3.5 35B-A3B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3.5-27b', 'Qwen3.5 27B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3.5-9b', 'Qwen3.5 9B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3-coder-next', 'Qwen3 Coder Next', {
    capabilities: textCapabilities({ thinking: true, tools: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
    verificationStatus: 'official-api',
  }),
  model('qwen', 'qwen3-coder-plus', 'Qwen3 Coder Plus', {
    capabilities: textCapabilities({ thinking: true, tools: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
    verificationStatus: 'official-api',
  }),
  model('qwen', 'qwen3-coder-flash', 'Qwen3 Coder Flash', {
    capabilities: textCapabilities({ thinking: true, tools: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
    verificationStatus: 'official-api',
  }),
  model('qwen', 'qwen3-coder-30b-a3b-instruct', 'Qwen3 Coder 30B-A3B Instruct', { capabilities: textCapabilities({ tools: true }) }),
  model('qwen', 'qwen3-coder', 'Qwen3 Coder', {
    capabilities: textCapabilities({ tools: true }),
  }),
  model('qwen', 'qwen3-vl-235b-a22b-thinking', 'Qwen3-VL 235B-A22B Thinking', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: alwaysThinking(),
  }),
  model('qwen', 'qwen3-vl-235b-a22b-instruct', 'Qwen3-VL 235B-A22B Instruct', {
    capabilities: visionCapabilities(),
  }),
  model('qwen', 'qwen3-vl-32b-instruct', 'Qwen3-VL 32B Instruct', {
    capabilities: visionCapabilities(),
  }),
  model('qwen', 'qwen3-vl-30b-a3b-thinking', 'Qwen3-VL 30B-A3B Thinking', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: alwaysThinking(),
  }),
  model('qwen', 'qwen3-vl-30b-a3b-instruct', 'Qwen3-VL 30B-A3B Instruct', {
    capabilities: visionCapabilities(),
  }),
  model('qwen', 'qwen3-vl-8b-thinking', 'Qwen3-VL 8B Thinking', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: alwaysThinking(),
  }),
  model('qwen', 'qwen3-vl-8b-instruct', 'Qwen3-VL 8B Instruct', {
    capabilities: visionCapabilities(),
  }),
  model('qwen', 'qwen3-next-80b-a3b-thinking', 'Qwen3-Next 80B-A3B Thinking', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: alwaysThinking(),
  }),
  model('qwen', 'qwen3-next-80b-a3b-instruct', 'Qwen3-Next 80B-A3B Instruct', {
    capabilities: textCapabilities(),
  }),
  model('qwen', 'qwen3-235b-a22b-thinking-2507', 'Qwen3 235B-A22B Thinking 2507', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: alwaysThinking(),
  }),
  model('qwen', 'qwen3-235b-a22b-2507', 'Qwen3 235B-A22B 2507', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3-235b-a22b', 'Qwen3 235B-A22B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3-30b-a3b-thinking-2507', 'Qwen3 30B-A3B Thinking 2507', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: alwaysThinking(),
  }),
  model('qwen', 'qwen3-30b-a3b-instruct-2507', 'Qwen3 30B-A3B Instruct 2507', {
    capabilities: textCapabilities(),
  }),
  model('qwen', 'qwen3-30b-a3b', 'Qwen3 30B-A3B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3-32b', 'Qwen3 32B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3-14b', 'Qwen3 14B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen3-8b', 'Qwen3 8B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen-plus', 'Qwen-Plus', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen-plus-2025-07-28', 'Qwen-Plus 2025-07-28', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen-turbo', 'Qwen-Turbo', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('enable_thinking'),
  }),
  model('qwen', 'qwen-max', 'Qwen-Max', { capabilities: visionCapabilities() }),
  model('qwen', 'qwen-long', 'Qwen-Long', {
    capabilities: visionCapabilities({ tools: false }),
  }),
  model('qwen', 'qwen2.5-max', 'Qwen2.5-Max', {
    capabilities: visionCapabilities(),
  }),
  model('qwen', 'qwen2.5-72b-instruct', 'Qwen2.5 72B Instruct', {
    capabilities: textCapabilities(),
  }),
  model('qwen', 'qwen2.5-7b-instruct', 'Qwen2.5 7B Instruct', {
    capabilities: textCapabilities(),
    aliases: ['qwen-2.5-7b-instruct'],
  }),
  model('qwen', 'qwen2.5-vl-72b-instruct', 'Qwen2.5 VL 72B Instruct', {
    capabilities: visionCapabilities({ tools: false }),
  }),
  model('qwen', 'qwen2.5-coder-32b-instruct', 'Qwen2.5 Coder 32B Instruct', {
    capabilities: textCapabilities({ tools: true }),
  }),
  model('qwen', 'qwen2-vl-72b-instruct', 'Qwen2 VL 72B Instruct', {
    capabilities: visionCapabilities({ tools: false }),
  }),
  model('qwen', 'qwq-32b', 'QwQ 32B', {
    capabilities: textCapabilities({ thinking: true }),
    thinkingConfig: { mode: 'always', defaultEnabled: true },
  }),
  model('qwen', 'qwen-math-plus', 'Qwen-Math-Plus', {
    capabilities: textCapabilities({ tools: false }),
  }),
  /*
   * 需求:视频档案表按目录型号查绑定。百炼的 Wan 走**原生异步任务**
   * (`/api/v1/services/aigc/video-generation/video-synthesis`),不是
   * compatible-mode 的 chat 端点 —— 所以它的连接是另一条 provider 记录(id 带
   * `video-` 前缀),而型号仍归 qwen(阿里云)这个家族。
   * ★ 按秒计费,TokenRates 表达不了,查不到价显示「—」。
   */
  model('qwen', 'wan2.7-t2v', 'Wan 2.7 文生视频', {
    modality: 'video',
    capabilities: videoCapabilities(),
    aliases: ['wan2.7-t2v-2026-06-12'],
  }),
  model('qwen', 'wan2.7-i2v', 'Wan 2.7 图生视频', {
    modality: 'video',
    capabilities: videoCapabilities(),
    aliases: ['wan2.7-i2v-2026-06-12'],
  }),
]
