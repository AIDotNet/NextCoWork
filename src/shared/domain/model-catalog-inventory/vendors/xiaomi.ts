import type { BuiltinModelRecord } from '../types'
import { model, textCapabilities, visionCapabilities, toggleThinking, source, OPENCODE_GO_SOURCE } from '../helpers'

export const XIAOMI: readonly BuiltinModelRecord[] = [
  /*
    需求：2026-09-22 小米发布 MiMo-V2.6 系列(Pro / Flash / Pro-UltraSpeed),补进目录。
    modality/上下文窗口/输出上限取自 OpenRouter `/api/v1/models`
    (`xiaomi/mimo-v2.6-*`,均为 1,048,576 上下文、131,072 输出，input_modalities
    含 image/video/audio)；定价取自官网首页 CNY 卡片，逐行核对与 OpenRouter 换算后的
    USD 价一致(隐含汇率约 6.9，与 V2.5 那两行的换算率相同)——双源吻合。
    thinkingConfig 沿用 V2.5 那一行「官方核实」的 `thinking.type` 开关形状：
    同厂同代 API，尚未见到改版公告，但**没有专门再核实 V2.6 的开关参数本身**，
    所以 verificationStatus 标 `aggregator-reference` 而不是 `official-api`。
  */
  model('xiaomi', 'mimo-v2.6-pro', 'MiMo V2.6 Pro', {
    capabilities: visionCapabilities({
      thinking: true,
      fileInput: true,
      audioInput: true,
      videoInput: true,
      tools: true,
      structuredOutput: true,
      streaming: true,
      caching: true,
    }),
    contextWindow: 1_048_576,
    maxOutputTokens: 131_072,
    thinkingConfig: {
      mode: 'toggle',
      defaultEnabled: true,
      parameterPath: 'thinking.type',
      enabledValue: 'enabled',
      disabledValue: 'disabled',
    },
    aliases: ['xiaomi/mimo-v2.6-pro'],
    source: { url: 'https://mimo.mi.com/', fetchedAt: '2026-09-22' },
    verificationStatus: 'aggregator-reference',
  }),
  model('xiaomi', 'mimo-v2.6-flash', 'MiMo V2.6 Flash', {
    capabilities: visionCapabilities({
      thinking: true,
      fileInput: true,
      audioInput: true,
      videoInput: true,
      tools: true,
      structuredOutput: true,
      streaming: true,
      caching: true,
    }),
    contextWindow: 1_048_576,
    maxOutputTokens: 131_072,
    thinkingConfig: {
      mode: 'toggle',
      defaultEnabled: true,
      parameterPath: 'thinking.type',
      enabledValue: 'enabled',
      disabledValue: 'disabled',
    },
    aliases: ['xiaomi/mimo-v2.6-flash'],
    source: { url: 'https://mimo.mi.com/', fetchedAt: '2026-09-22' },
    verificationStatus: 'aggregator-reference',
  }),
  // 官网原话「兼顾 V2.6-Pro 旗舰性能，提供最高 20 倍输出速度」——同一 checkpoint 的高速档，
  // 不是单独的弱化模型，能力集与 Pro 一致。
  model('xiaomi', 'mimo-v2.6-pro-ultraspeed', 'MiMo V2.6 Pro UltraSpeed', {
    capabilities: visionCapabilities({
      thinking: true,
      fileInput: true,
      audioInput: true,
      videoInput: true,
      tools: true,
      structuredOutput: true,
      streaming: true,
      caching: true,
    }),
    contextWindow: 1_048_576,
    maxOutputTokens: 131_072,
    thinkingConfig: {
      mode: 'toggle',
      defaultEnabled: true,
      parameterPath: 'thinking.type',
      enabledValue: 'enabled',
      disabledValue: 'disabled',
    },
    aliases: ['xiaomi/mimo-v2.6-pro-ultraspeed'],
    source: { url: 'https://mimo.mi.com/', fetchedAt: '2026-09-22' },
    verificationStatus: 'aggregator-reference',
  }),
  model('xiaomi', 'mimo-v2.5-pro', 'MiMo V2.5 Pro', {
    capabilities: textCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    thinkingConfig: {
      mode: 'toggle',
      defaultEnabled: true,
      parameterPath: 'thinking.type',
      enabledValue: 'enabled',
      disabledValue: 'disabled',
    },
    aliases: ['xiaomi/mimo-v2.5-pro'],
    source: source('https://mimo.mi.com/docs/en-US/pricing'),
    verificationStatus: 'official-api',
  }),
  model('xiaomi', 'mimo-v2.5', 'MiMo V2.5', {
    capabilities: visionCapabilities({
      thinking: true,
      fileInput: true,
      tools: true,
      structuredOutput: true,
      streaming: true,
      caching: true,
    }),
    contextWindow: 1_000_000,
    thinkingConfig: {
      mode: 'toggle',
      defaultEnabled: true,
      parameterPath: 'thinking.type',
      enabledValue: 'enabled',
      disabledValue: 'disabled',
    },
    aliases: ['xiaomi/mimo-v2.5'],
    source: source('https://mimo.mi.com/docs/en-US/pricing'),
    verificationStatus: 'official-api',
  }),
  model('xiaomi', 'mimo-v2-pro', 'MiMo V2 Pro', {
    capabilities: textCapabilities({ thinking: true, tools: true }),
    contextWindow: 1_048_576,
    maxOutputTokens: 128_000,
    thinkingConfig: { mode: 'always', defaultEnabled: true },
    source: OPENCODE_GO_SOURCE,
    verificationStatus: 'aggregator-reference',
  }),
  model('xiaomi', 'mimo-v2-omni', 'MiMo V2 Omni', {
    capabilities: visionCapabilities({
      thinking: true,
      fileInput: true,
      audioInput: true,
      tools: true,
    }),
    contextWindow: 262_144,
    maxOutputTokens: 128_000,
    thinkingConfig: { mode: 'always', defaultEnabled: true },
    source: OPENCODE_GO_SOURCE,
    verificationStatus: 'aggregator-reference',
  }),
  model('xiaomi', 'mimo-v2-flash', 'MiMo V2 Flash', {
    capabilities: visionCapabilities({ thinking: true }),
    thinkingConfig: toggleThinking('thinking'),
  }),
  model('xiaomi', 'mimo-vl-7b', 'MiMo-VL 7B', {
    capabilities: visionCapabilities(),
  }),
]
