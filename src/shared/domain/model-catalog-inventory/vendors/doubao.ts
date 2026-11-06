import type { BuiltinModelRecord } from '../types'
import { model, textCapabilities, visionCapabilities, imageCapabilities, videoCapabilities, toggleThinking } from '../helpers'

export const DOUBAO: readonly BuiltinModelRecord[] = [
  model('doubao', 'doubao-seed-evolving', '豆包 Seed Evolving', {
    capabilities: visionCapabilities({
      thinking: true,
      tools: true,
      structuredOutput: true,
      caching: true,
    }),
    contextWindow: 1_048_576,
    maxOutputTokens: 262_144,
    thinkingConfig: toggleThinking('thinking.type'),
    verificationStatus: 'official-api',
  }),
  /*
   * 需求:2.1 世代的 260915 版(2026-09-22 从方舟「模型列表」核对)窗口从 256k 抬到
   * 1024k(最大回答 256k 不变),并新增 Lite 档;260628 的 Pro / Turbo 两行已被官方
   * 移出推荐表,按「退役旧行」删掉。
   *
   * 不满足会怎样:窗口停在 256k 时上下文圆环分母、「最大上下文」开关判据
   * (`supportsMaxContext`)与压缩阈值全按旧窗口算 —— 1M 的窗口被静默压小四倍,
   * 且没有任何报错指向目录。
   *
   * 删行的代价写在明处:`doubao-seed-2.1-pro` 这个**产品别名**仍然登记在新行上
   * (别名 = 用户药丸和 `defaultModel` 里看见的字符串),所以老绑定照样命中目录。
   * Turbo 则**没有**后继行,它的别名故意不挂到 Lite 上 —— 挂上去等于用 Lite 的
   * 窗口与价格替一款已退役的模型作答;把带日期的全名当 `upstreamModel` 存过的
   * 两种绑定,会落到兜底元数据。
   */
  model('doubao', 'doubao-seed-2-1-pro-260915', '豆包 Seed 2.1 Pro', {
    capabilities: visionCapabilities({
      thinking: true,
      tools: true,
      structuredOutput: true,
      caching: true,
    }),
    contextWindow: 1_048_576,
    maxOutputTokens: 262_144,
    thinkingConfig: toggleThinking('thinking.type'),
    aliases: ['doubao-seed-2.1-pro', 'doubao-seed-2-1-pro'],
    pricingModelId: 'doubao-seed-2.1-pro',
    verificationStatus: 'official-api',
  }),
  model('doubao', 'doubao-seed-2-1-lite-260915', '豆包 Seed 2.1 Lite', {
    capabilities: visionCapabilities({
      thinking: true,
      tools: true,
      structuredOutput: true,
      caching: true,
    }),
    contextWindow: 1_048_576,
    maxOutputTokens: 262_144,
    thinkingConfig: toggleThinking('thinking.type'),
    aliases: ['doubao-seed-2.1-lite', 'doubao-seed-2-1-lite'],
    pricingModelId: 'doubao-seed-2.1-lite',
    verificationStatus: 'official-api',
  }),
  model('doubao', 'doubao-seed-2-0-pro-260215', '豆包 Seed 2.0 Pro', {
    capabilities: visionCapabilities({
      thinking: true,
      tools: true,
      structuredOutput: true,
      caching: true,
    }),
    contextWindow: 262_144,
    maxOutputTokens: 131_072,
    thinkingConfig: toggleThinking('thinking.type'),
    aliases: ['doubao-seed-2.0-pro', 'doubao-seed-2-0-pro'],
    pricingModelId: 'doubao-seed-2.0-pro',
    verificationStatus: 'official-api',
  }),
  model('doubao', 'doubao-seed-2-0-lite-260428', '豆包 Seed 2.0 Lite', {
    capabilities: visionCapabilities({
      thinking: true,
      tools: true,
      structuredOutput: true,
      caching: true,
    }),
    contextWindow: 262_144,
    maxOutputTokens: 131_072,
    thinkingConfig: toggleThinking('thinking.type'),
    aliases: ['doubao-seed-2.0-lite', 'doubao-seed-2-0-lite', 'doubao-seed-2-0-lite-260215'],
    pricingModelId: 'doubao-seed-2.0-lite',
    verificationStatus: 'official-api',
  }),
  model('doubao', 'doubao-seed-2-0-mini-260428', '豆包 Seed 2.0 Mini', {
    capabilities: visionCapabilities({
      thinking: true,
      tools: true,
      structuredOutput: true,
      caching: true,
    }),
    contextWindow: 262_144,
    maxOutputTokens: 131_072,
    thinkingConfig: toggleThinking('thinking.type'),
    aliases: ['doubao-seed-2.0-mini', 'doubao-seed-2-0-mini', 'doubao-seed-2-0-mini-260215'],
    pricingModelId: 'doubao-seed-2.0-mini',
    verificationStatus: 'official-api',
  }),
  model('doubao', 'doubao-seed-2-0-code-preview-260215', '豆包 Seed 2.0 Code Preview', {
    capabilities: visionCapabilities({
      thinking: true,
      tools: true,
      caching: true,
    }),
    contextWindow: 262_144,
    maxOutputTokens: 131_072,
    thinkingConfig: toggleThinking('thinking.type'),
    aliases: ['doubao-seed-2.0-code', 'doubao-seed-2-0-code'],
    pricingModelId: 'doubao-seed-2.0-code',
    verificationStatus: 'official-api',
  }),
  model('doubao', 'doubao-seed-character-260628', '豆包 Seed Character', {
    capabilities: visionCapabilities({
      thinking: true,
      tools: true,
      structuredOutput: true,
      caching: true,
    }),
    contextWindow: 131_072,
    maxOutputTokens: 32_768,
    thinkingConfig: toggleThinking('thinking.type'),
    aliases: ['doubao-seed-character'],
    pricingModelId: 'doubao-seed-character',
    verificationStatus: 'official-api',
  }),
  model('doubao', 'doubao-seed-code-preview-251028', '豆包 Seed Code Preview', {
    capabilities: visionCapabilities({
      thinking: true,
      tools: true,
      caching: true,
    }),
    contextWindow: 262_144,
    maxOutputTokens: 32_768,
    thinkingConfig: toggleThinking('thinking.type'),
    aliases: ['doubao-seed-code'],
    pricingModelId: 'doubao-seed-code',
    verificationStatus: 'official-api',
  }),
  model('doubao', 'doubao-seed-1-6-flash-250828', '豆包 Seed 1.6 Flash', {
    capabilities: visionCapabilities({
      thinking: true,
      tools: true,
      structuredOutput: true,
      caching: true,
    }),
    contextWindow: 262_144,
    maxOutputTokens: 32_768,
    thinkingConfig: toggleThinking('thinking.type'),
    aliases: ['doubao-seed-1.6-flash', 'doubao-seed-1-6-flash', 'doubao-seed-1-6-flash-250615'],
    pricingModelId: 'doubao-seed-1.6-flash',
    verificationStatus: 'official-api',
  }),
  model('doubao', 'doubao-seed-1-6-vision-250815', '豆包 Seed 1.6 Vision', {
    capabilities: visionCapabilities({
      thinking: true,
      tools: true,
      structuredOutput: true,
      caching: true,
    }),
    contextWindow: 262_144,
    maxOutputTokens: 32_768,
    thinkingConfig: toggleThinking('thinking.type'),
    aliases: ['doubao-seed-1.6-vision', 'doubao-seed-1-6-vision'],
    pricingModelId: 'doubao-seed-1.6-vision',
    verificationStatus: 'official-api',
  }),
  model('doubao', 'doubao-1-5-pro-32k-250115', '豆包 1.5 Pro 32K', {
    capabilities: textCapabilities({ tools: true, caching: true }),
    contextWindow: 131_072,
    maxOutputTokens: 16_384,
    aliases: ['doubao-1.5-pro-32k', 'doubao-1-5-pro-32k'],
    pricingModelId: 'doubao-1.5-pro-32k',
    verificationStatus: 'official-api',
  }),
  model('doubao', 'doubao-1-5-lite-32k-250115', '豆包 1.5 Lite 32K', {
    capabilities: textCapabilities({ tools: true, caching: true }),
    contextWindow: 32_768,
    maxOutputTokens: 12_288,
    aliases: ['doubao-1.5-lite-32k', 'doubao-1-5-lite-32k'],
    pricingModelId: 'doubao-1.5-lite-32k',
    verificationStatus: 'official-api',
  }),
  model('doubao', 'doubao-1.5-vision-pro', '豆包 1.5 Vision Pro', {
    capabilities: visionCapabilities(),
    pricingModelId: 'doubao-1.5-vision-pro',
    verificationStatus: 'official-api',
  }),
  model('doubao', 'doubao-seed-translation-250915', '豆包 Seed Translation', {
    capabilities: textCapabilities({ tools: false }),
    contextWindow: 4_096,
    maxOutputTokens: 3_072,
    aliases: ['doubao-seed-translation'],
    pricingModelId: 'doubao-seed-translation',
    verificationStatus: 'official-api',
  }),
  model('doubao', 'doubao-seed-1-6', '豆包 Seed 1.6', {
    capabilities: visionCapabilities({
      thinking: true,
      tools: true,
      structuredOutput: true,
    }),
    contextWindow: 262_144,
    maxOutputTokens: 32_768,
    thinkingConfig: toggleThinking('thinking.type'),
  }),
  /*
   * 需求:把方舟「模型列表」当前在列的生图/生视频型号补齐(2026-09-22 核对)。
   * 原先只有 seedream-4-0 与 seedance-1-0-pro 两条,5.0 / 2.x 世代在目录里查不到 ——
   * 症状是这几款在「模型管理」里根本不出现,用户想绑也找不到条目。
   *
   * ★ 这几行**只贡献目录条目与模态**,没有能力徽章可给:方舟的生图/生视频不走
   * chat/responses,工具调用、结构化输出这些字段对它们没有意义(传了也不会生效)。
   * ★ `pricingModelId` 一律沿用 `id`:方舟对它们的报价按「张 / 秒 / 分辨率」计,
   * `TokenRates`(每百万 token)表达不了,定价种子表里没有对应行,查不到价显示「—」——
   * 那是本仓库定价表的既定失败方向,不是漏填。
   */
  model('doubao', 'doubao-seedream-5-0-pro-260628', 'Seedream 5.0 Pro', {
    modality: 'image',
    capabilities: imageCapabilities(),
    aliases: ['seedream-5.0-pro'],
  }),
  model('doubao', 'doubao-seedream-5-0-flash-260915', 'Seedream 5.0 Flash', {
    modality: 'image',
    capabilities: imageCapabilities(),
    aliases: ['seedream-5.0-flash'],
  }),
  model('doubao', 'doubao-seedream-5-0-260128', 'Seedream 5.0', {
    modality: 'image',
    capabilities: imageCapabilities(),
    aliases: ['seedream-5.0', 'doubao-seedream-5-0-lite-260128', 'seedream-5.0-lite'],
  }),
  model('doubao', 'doubao-seedream-4-5-251128', 'Seedream 4.5', {
    modality: 'image',
    capabilities: imageCapabilities(),
    aliases: ['seedream-4.5'],
  }),
  model('doubao', 'doubao-seedream-4-0-250828', 'Seedream 4.0', {
    modality: 'image',
    capabilities: imageCapabilities(),
    aliases: ['seedream-4.0', 'doubao-seedream-4-0'],
  }),
  model('doubao', 'doubao-seedance-2-5-260628', 'Seedance 2.5', {
    modality: 'video',
    capabilities: videoCapabilities(),
    aliases: ['seedance-2.5'],
  }),
  model('doubao', 'doubao-seedance-2-0-260128', 'Seedance 2.0', {
    modality: 'video',
    capabilities: videoCapabilities(),
    aliases: ['seedance-2.0'],
  }),
  model('doubao', 'doubao-seedance-2-0-fast-260128', 'Seedance 2.0 Fast', {
    modality: 'video',
    capabilities: videoCapabilities(),
    aliases: ['seedance-2.0-fast'],
  }),
  model('doubao', 'doubao-seedance-2-0-mini-260615', 'Seedance 2.0 Mini', {
    modality: 'video',
    capabilities: videoCapabilities(),
    aliases: ['seedance-2.0-mini'],
  }),
  model('doubao', 'doubao-seedance-1-0-pro-250528', 'Seedance 1.0 Pro', {
    modality: 'video',
    capabilities: videoCapabilities(),
    aliases: ['seedance-1.0-pro', 'doubao-seedance-1-0-pro'],
  }),
  model('doubao', 'doubao-seedance-1-0-pro-fast-251015', 'Seedance 1.0 Pro Fast', {
    modality: 'video',
    capabilities: videoCapabilities(),
    aliases: ['seedance-1.0-pro-fast'],
  }),
]
