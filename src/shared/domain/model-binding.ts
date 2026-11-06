import type { ModelAlias, ModelCapabilities, ModelCatalogOverride } from './provider'
import { IMPORTED_ALIAS_DEFAULTS, MODEL_METADATA_FIELDS } from './provider'
import { findCatalogModel, mergeModelCatalog, type ModelCatalogDefinition } from './model-catalog'
import { BUILTIN_MODEL_CATALOG, OLLAMA_REASONING_EFFORTS, OLLAMA_STANDARD_THINKING } from './model-catalog-inventory'
import { OLLAMA_PROVIDER_IDS } from './presets'
import { anthropicThinkingForModel } from './model-catalog-inventory/vendors/anthropic'

/** JSON metadata equality independent of object property insertion order. */
function same(a: unknown, b: unknown): boolean {
  if (Object.is(a, b)) return true
  if (a === null || b === null || typeof a !== 'object' || typeof b !== 'object') return false
  if (Array.isArray(a) || Array.isArray(b)) {
    return Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((v, i) => same(v, b[i]))
  }
  const left = Object.entries(a).filter(([, v]) => v !== undefined)
  const right = Object.entries(b).filter(([, v]) => v !== undefined)
  return left.length === right.length && left.every(([key, v]) => same(v, (b as Record<string, unknown>)[key]))
}

/**
 * 某个能力位取这个值时,**不能当成「用户显式设过」** —— 它可能只是从旧版目录抄来的。
 *
 * ★★ 下面那段推断的判据是「这个值和它可能继承来的**任何**默认值都不同,所以只能是人设的」。
 * 这条判据依赖一份完整的「可能来源」清单,而**目录默认值改过之后,旧的那个值也得留在清单里**。
 *
 * `tools` 的目录默认值从 `false` 翻成了 `true`(理由见 `model-catalog-inventory.ts`)。
 * 不登记这条历史默认值的话,当年照着旧默认抄下 `false` 的每一条旧记录,都会在升级后
 * 被读成「用户特意关掉了工具」,并被永久写进 `catalogOverrides` —— 症状是升级完
 * Agent 照样没有工具,而且**再也修不回来了**,因为它此刻已经算用户设置,目录再也压不过它。
 */
const HISTORICAL_CATALOG_DEFAULTS: Partial<Record<keyof ModelCapabilities, boolean>> = {
  tools: false
}

/** One resolver for imports, settings, the composer and the runtime router. */
export function modelBindingResolver(custom: readonly ModelCatalogDefinition[] = []) {
  const catalog = mergeModelCatalog({ builtin: BUILTIN_MODEL_CATALOG, custom })

  function resolve(raw: ModelAlias): ModelAlias {
    const definition = findCatalogModel(catalog, raw.upstreamModel)
    const builtin = findCatalogModel(BUILTIN_MODEL_CATALOG, raw.upstreamModel)
    const inferred = definition === undefined &&
      !(OLLAMA_PROVIDER_IDS as readonly string[]).includes(raw.providerId)
      ? anthropicThinkingForModel(raw.upstreamModel) : undefined
    const overrides = new Set<ModelCatalogOverride>(raw.catalogOverrides)
    // Old Anthropic catalogue copies must follow the new adaptive declaration;
    // a modified budget or an explicit override still belongs to the user.
    const oldAnthropicThinking = builtin?.manufacturerId === 'anthropic' &&
      builtin.thinkingConfig.anthropicAdaptive === true && same(raw.thinkingConfig, {
        mode: 'budget', defaultEnabled: true,
        defaultBudgetTokens: builtin.id.includes('-opus-') ? 64_000 : builtin.id.includes('-haiku-') ? 16_000 : 32_000,
        parameterPath: 'thinking.budget_tokens'
      })
    if (raw.catalogOverrides === undefined) {
      // Old imports stored generic placeholders; older explicit catalogue binds
      // stored a copy of the definition. Neither is a provider customization.
      for (const field of MODEL_METADATA_FIELDS) {
        const value = raw[field]
        const fallback = field in IMPORTED_ALIAS_DEFAULTS
          ? IMPORTED_ALIAS_DEFAULTS[field as 'contextWindow' | 'maxOutputTokens'] : undefined
        if (value !== undefined && !same(value, fallback) &&
          !same(value, definition?.[field]) && !same(value, builtin?.[field]) &&
          !(field === 'thinkingConfig' && (oldAnthropicThinking || same(value, inferred?.thinkingConfig))) &&
          !(field === 'reasoningEfforts' && same(value, inferred?.reasoningEfforts))) overrides.add(field)
      }
      for (const key of Object.keys(raw.capabilities) as (keyof ModelCapabilities)[]) {
        const value = raw.capabilities[key]
        if (value !== undefined && value !== IMPORTED_ALIAS_DEFAULTS.capabilities[key] &&
          value !== HISTORICAL_CATALOG_DEFAULTS[key] &&
          value !== definition?.capabilities[key] && value !== builtin?.capabilities[key] &&
          !(key === 'thinking' && inferred !== undefined && value === true)) {
          overrides.add(`capabilities.${key}`)
        }
      }
      if (overrides.has('thinkingConfig')) overrides.add('reasoningEfforts')
    }
    const result = structuredClone(raw)
    delete result.runtimeProtocol
    if (result.protocolOverride === undefined) delete result.protocolOverride
    result.catalogOverrides = [...overrides]
    if (definition !== undefined) {
      for (const field of MODEL_METADATA_FIELDS) {
        if (!overrides.has(field)) Object.assign(result, { [field]: structuredClone(definition[field]) })
      }
      result.capabilities = { ...definition.capabilities }
      for (const key of Object.keys(raw.capabilities) as (keyof ModelCapabilities)[]) {
        const value = raw.capabilities[key]
        if (overrides.has(`capabilities.${key}`) && value !== undefined) result.capabilities[key] = value
      }
    }
    // Unknown versioned Claude IDs get a conservative adaptive fallback.
    // Custom catalogues, explicit restrictions and Ollama remain authoritative.
    if (inferred !== undefined && !overrides.has('thinkingConfig') &&
      !overrides.has('capabilities.thinking')) {
      result.thinkingConfig = inferred.thinkingConfig
      if (!overrides.has('reasoningEfforts')) result.reasoningEfforts = inferred.reasoningEfforts
    }
    // A legacy capability toggle is still an explicit provider restriction.
    if (definition !== undefined && overrides.has('capabilities.thinking') && !overrides.has('thinkingConfig')) {
      result.thinkingConfig = { mode: raw.capabilities.thinking ? 'toggle' : 'unsupported', defaultEnabled: false }
    }
    if (result.thinkingConfig !== undefined) result.capabilities.thinking = result.thinkingConfig.mode !== 'unsupported'
    if (overrides.has('capabilities.vision') && !overrides.has('capabilities.visionInput')) {
      result.capabilities.visionInput = raw.capabilities.vision
    }
    /*
     * ★★ **Ollama 系供应商上,「别家目录条目」的思考线形就地改写。**
     *
     * 起因:`glm-5.3`、`kimi-k3` 这类**不带 tag 的名字**命中的是智谱/Moonshot 的
     * 目录条目,它们的 thinkingConfig 是那两家**官方 API** 的方言(`thinking.type`、
     * custom 路径)。绑到 Ollama 上时那些字段会被兼容层静默丢弃(Go json 丢弃未知
     * 键)—— 用户切「关」也什么都发不出去,thinking 始终跟随模型默认。
     *
     * 覆盖成 Ollama 自己的线形(`reasoning_effort` + standardWire,见 vendors/ollama.ts):
     * 开关真实生效,档位也拿得到(官方文档:多数模型接受 low/medium/high/max)。
     *
     * 四条边界,缺一条都会伤到别的渠道:
     * - **只认 Ollama 系 providerId**(本地 + 订阅两条线,常量在 presets.ts);
     * - **只碰已确认的思考模型**(`definition.capabilities.thinking`)—— 否则连
     *   llama3.3 这种非思考模型都会长出思考开关;
     * - **不碰已是 ollama 厂商的条目**(gpt-oss 的档位表与别家不同,见下);
     * - **用户显式改过就不覆盖**。例外是「读回来的正是我们自己写的配置」——
     *   旧记录(升级前落下、`catalogOverrides` 还是 undefined)的检测循环会把它
     *   误判成用户自定义,不认这条的话第一次 resolve 之后覆盖就永久失效。
     */
    if (
      (OLLAMA_PROVIDER_IDS as readonly string[]).includes(raw.providerId) &&
      definition !== undefined &&
      definition.manufacturerId !== 'ollama' &&
      definition.capabilities.thinking === true &&
      !overrides.has('capabilities.thinking') &&
      (!overrides.has('thinkingConfig') || same(result.thinkingConfig, OLLAMA_STANDARD_THINKING))
    ) {
      result.thinkingConfig = structuredClone(OLLAMA_STANDARD_THINKING)
      result.reasoningEfforts = [...OLLAMA_REASONING_EFFORTS]
      result.capabilities.thinking = true
    }
    for (const field of MODEL_METADATA_FIELDS) if (result[field] === undefined) delete result[field]
    if (result.thinkingConfig !== undefined) {
      result.thinkingConfig = Object.fromEntries(Object.entries(result.thinkingConfig)
        .filter(([, value]) => value !== undefined)) as unknown as NonNullable<ModelAlias['thinkingConfig']>
    }
    return result
  }

  function update(raw: ModelAlias, input: ModelAlias): ModelAlias {
    const current = resolve(raw)
    const overrides = new Set(current.catalogOverrides)
    for (const field of MODEL_METADATA_FIELDS) {
      // Omitted properties from an older renderer do not clear saved settings.
      if (Object.hasOwn(input, field) && !same(input[field], current[field])) overrides.add(field)
    }
    for (const key of Object.keys(input.capabilities) as (keyof ModelCapabilities)[]) {
      if (!same(input.capabilities[key], current.capabilities[key])) overrides.add(`capabilities.${key}`)
    }
    if (!same(input.thinkingConfig ?? current.thinkingConfig, current.thinkingConfig) ||
      !same(input.reasoningEfforts ?? current.reasoningEfforts, current.reasoningEfforts)) {
      overrides.add('thinkingConfig')
      overrides.add('reasoningEfforts')
    }
    const next = {
      ...current, ...input,
      alias: raw.alias, providerId: raw.providerId, upstreamModel: raw.upstreamModel,
      capabilities: { ...current.capabilities, ...input.capabilities },
      catalogOverrides: [...overrides]
    }
    // Older renderers omit this field entirely; preserve their current value.
    // The protocol editor sends an own property with `undefined` to explicitly
    // clear the persisted override.
    if (Object.hasOwn(input, 'protocolOverride') && input.protocolOverride === undefined) {
      delete next.protocolOverride
    }
    // Editing the legacy Think capability also changes the detailed declaration.
    if (input.capabilities.thinking !== current.capabilities.thinking && same(input.thinkingConfig, current.thinkingConfig)) {
      next.thinkingConfig = { mode: input.capabilities.thinking ? 'toggle' : 'unsupported', defaultEnabled: false }
      next.catalogOverrides = [...new Set([...overrides, 'thinkingConfig', 'reasoningEfforts'] as ModelCatalogOverride[])]
    }
    return resolve(next)
  }

  return { resolve, update }
}
