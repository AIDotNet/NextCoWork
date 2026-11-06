/**
 * models.dev `api.json` → 模型目录候选 —— 纯函数。
 *
 * ★ **新增与更新分开,更新必须逐条确认。** 内置目录的每一行都按官方文档核实过;
 * models.dev 是聚合源。目录里没有的 → `added`,标 `aggregator-reference` 入用户目录;
 * 目录里已有的 → 只对比 models.dev 能给出的字段,有差异就进 `updates`,带逐字段差异,
 * 由用户勾选后才落成「覆盖层」(内置行用 `overrideBuiltin`,不改内置数据本身)。
 * 更新只改有差异的字段,思考配置 / 请求适配 / 别名等一律保留。
 *
 * ★ 思考参数是**厂商方言**(`reasoning.effort` / `reasoning_effort` /
 * `output_config.effort` …),models.dev 不提供。写错的后果是请求被上游 400,
 * 所以只从**同厂商已核实的内置行**借配置;借不到就按不支持思考处理,宁可少一个开关。
 */
import { BUILTIN_MODEL_CATALOG, findBuiltinModel } from './model-catalog-inventory'
import { isModelCatalogDefinition } from './model-catalog'
import { manufacturer } from './model-catalog-inventory/manufacturers'
import { MODELS_DEV_URL } from './models-dev'
import type { ModelCatalogDefinition } from './model-catalog'
import type { ModelCapabilities, ReasoningEffort, ThinkingConfig } from './provider'

/** models.dev provider id → 本应用的 manufacturerId */
export const CATALOG_SYNC_VENDORS: Readonly<Record<string, string>> = {
  anthropic: 'anthropic',
  openai: 'openai',
  google: 'google',
  xai: 'xai',
  deepseek: 'deepseek',
  mistral: 'mistral',
  cohere: 'cohere',
  minimax: 'minimax',
  moonshotai: 'moonshot',
  zhipuai: 'zhipu',
  alibaba: 'qwen',
  xiaomi: 'xiaomi',
  meta: 'meta'
}

const KNOWN_EFFORTS: readonly ReasoningEffort[] = ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max']

const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)
const strings = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : [])
const posInt = (v: unknown, fallback: number): number => (typeof v === 'number' && Number.isFinite(v) && v > 0 ? Math.floor(v) : fallback)

/** 同厂商里第一条已核实、且支持思考的内置行 —— 它的思考配置就是这家的方言 */
function donorThinking(manufacturerId: string): { config: ThinkingConfig; efforts?: readonly ReasoningEffort[] } | undefined {
  const donor = BUILTIN_MODEL_CATALOG.find(
    (m) => m.manufacturerId === manufacturerId && m.thinkingConfig.mode !== 'unsupported' && m.verificationStatus !== 'unverified'
  )
  return donor === undefined ? undefined : { config: donor.thinkingConfig, ...(donor.reasoningEfforts === undefined ? {} : { efforts: donor.reasoningEfforts }) }
}

/** 一处字段差异。`field` 是界面用的稳定键,`from` / `to` 已是可直接展示的值 */
export type ModelFieldKey =
  | 'contextWindow'
  | 'maxOutputTokens'
  | 'vision'
  | 'fileInput'
  | 'videoInput'
  | 'audioInput'
  | 'structuredOutput'
  | 'tools'
  | 'reasoningEfforts'

export interface ModelFieldChange {
  field: ModelFieldKey
  from: string | number | boolean | undefined
  to: string | number | boolean
}

export interface ModelCatalogUpdateItem {
  id: string
  displayName: string
  manufacturerLabel: string
  /** 当前生效的行(用户覆盖层优先,否则内置) */
  current: ModelCatalogDefinition
  /** 应用后写入用户目录的整行 = 当前行 + 有差异的字段 */
  incoming: ModelCatalogDefinition
  changes: ModelFieldChange[]
}

export interface ModelsDevCatalogParse {
  added: ModelCatalogDefinition[]
  updates: ModelCatalogUpdateItem[]
}

const sameList = (a: readonly string[] | undefined, b: readonly string[]): boolean =>
  a !== undefined && a.length === b.length && a.every((v, i) => v === b[i])

/**
 * 已有模型 vs models.dev。只比 models.dev 真正提供的字段:
 * - 上下文 / 输出上限:数值不同才算。
 * - 输入模态与结构化输出:models.dev 有明确的 modalities 才比;`structured_output` 缺失 = 无信息,不比。
 * - `tools`:**只允许 false → true。** 反方向会让 agent 丢掉整份工具 schema 而不报任何错
 *   (见 `textCapabilities` 里那段),聚合源的一个 `tool_call: false` 不足以做这个决定。
 * - 档位:只在 models.dev 明确给出 effort 档位且与当前不同时才改。
 */
function diffExisting(
  current: ModelCatalogDefinition,
  m: Record<string, unknown>,
  vendor: string,
  fetchedAt: string
): ModelCatalogUpdateItem | null {
  const modalities = isRecord(m['modalities']) ? m['modalities'] : {}
  const input = strings(modalities['input'])
  const limit = isRecord(m['limit']) ? m['limit'] : {}
  const changes: ModelFieldChange[] = []
  const caps: Partial<ModelCapabilities> = {}

  const ctx = posInt(limit['context'], 0)
  if (ctx > 0 && ctx !== current.contextWindow) changes.push({ field: 'contextWindow', from: current.contextWindow, to: ctx })
  const out = posInt(limit['output'], 0)
  if (out > 0 && out !== current.maxOutputTokens) changes.push({ field: 'maxOutputTokens', from: current.maxOutputTokens, to: out })

  const flag = (field: ModelFieldKey, key: keyof ModelCapabilities, now: boolean | undefined, next: boolean): void => {
    if (now === next || (now === undefined && next === false)) return
    changes.push({ field, from: now, to: next })
    caps[key] = next
  }
  if (input.length > 0) {
    flag('vision', 'visionInput', current.capabilities.visionInput ?? current.capabilities.vision, input.includes('image'))
    flag('fileInput', 'fileInput', current.capabilities.fileInput, input.includes('pdf'))
    flag('videoInput', 'videoInput', current.capabilities.videoInput, input.includes('video'))
    flag('audioInput', 'audioInput', current.capabilities.audioInput, input.includes('audio'))
  }
  if (typeof m['structured_output'] === 'boolean') flag('structuredOutput', 'structuredOutput', current.capabilities.structuredOutput, m['structured_output'])
  if (m['tool_call'] === true && !current.capabilities.tools) flag('tools', 'tools', current.capabilities.tools, true)

  const effortOption = Array.isArray(m['reasoning_options'])
    ? m['reasoning_options'].find((o): o is Record<string, unknown> => isRecord(o) && o['type'] === 'effort')
    : undefined
  const efforts = effortOption === undefined ? [] : strings(effortOption['values']).filter((v): v is ReasoningEffort => (KNOWN_EFFORTS as readonly string[]).includes(v))
  const tc = current.thinkingConfig
  // 新档位若会让当前默认档落到列表外(或让 adaptive 丢掉必需的 none),这条就不改 ——
  // 静默换默认档比保留旧档位更糟
  const effortsFit =
    (tc.mode !== 'effort' || (tc.defaultEffort !== undefined && efforts.includes(tc.defaultEffort))) &&
    !(tc.anthropicAdaptive === true && tc.defaultEnabled === false && !efforts.includes('none'))
  const effortsChanged = efforts.length > 0 && tc.mode !== 'unsupported' && effortsFit && !sameList(current.reasoningEfforts, efforts)
  if (effortsChanged) changes.push({ field: 'reasoningEfforts', from: current.reasoningEfforts?.join(' / '), to: efforts.join(' / ') })

  if (changes.length === 0) return null
  if (caps.visionInput !== undefined) caps.vision = caps.visionInput
  const get = (f: ModelFieldKey): number | undefined => {
    const c = changes.find((x) => x.field === f)
    return typeof c?.to === 'number' ? c.to : undefined
  }
  const incoming: ModelCatalogDefinition = {
    ...current,
    contextWindow: get('contextWindow') ?? current.contextWindow,
    maxOutputTokens: get('maxOutputTokens') ?? current.maxOutputTokens,
    capabilities: { ...current.capabilities, ...caps },
    ...(effortsChanged ? { reasoningEfforts: efforts } : {}),
    // 内置行只能以覆盖层的方式更新,不动内置数据本身
    overrideBuiltin: true,
    source: { url: `${MODELS_DEV_URL}#${vendor}/${current.id}`, fetchedAt },
    verificationStatus: 'aggregator-reference'
  }
  return { id: current.id, displayName: current.displayName, manufacturerLabel: current.manufacturerLabel, current, incoming, changes }
}

/**
 * 把借来的思考配置套到 models.dev 给出的档位上。目录校验要求 effort 模式的
 * `defaultEffort` 必须落在 `reasoningEfforts` 里 —— 档位变了而默认档没跟着变,
 * 整行就是「格式无效」,而一批里只要有一行无效整批都写不进去。
 * 默认档优先保留,其次 medium,再次取列表中间那一档。
 */
function fitThinking(config: ThinkingConfig, efforts: readonly ReasoningEffort[] | undefined): ThinkingConfig {
  if (config.mode !== 'effort' || efforts === undefined || efforts.length === 0) return config
  if (config.defaultEffort !== undefined && efforts.includes(config.defaultEffort)) return config
  const fallback = efforts.includes('medium') ? 'medium' : efforts[Math.floor((efforts.length - 1) / 2)]!
  return { ...config, defaultEffort: fallback }
}

export function parseModelsDevCatalog(
  json: unknown,
  fetchedAt: string,
  /** 用户目录里已有的行:同 id 的当作「已有」,更新时以它为当前值 */
  userRows: readonly ModelCatalogDefinition[] = []
): ModelsDevCatalogParse {
  if (!isRecord(json)) throw new Error('models.dev 返回的不是对象。')
  const userById = new Map(userRows.map((r) => [r.id.trim().toLowerCase(), r]))
  const seen = new Set<string>()
  const added: ModelCatalogDefinition[] = []
  const updates: ModelCatalogUpdateItem[] = []
  for (const [vendor, manufacturerId] of Object.entries(CATALOG_SYNC_VENDORS)) {
    const provider = json[vendor]
    if (!isRecord(provider) || !isRecord(provider['models'])) continue
    const label = manufacturer(manufacturerId).label
    for (const [key, m] of Object.entries(provider['models'])) {
      if (!isRecord(m)) continue
      const id = typeof m['id'] === 'string' && m['id'] !== '' ? m['id'] : key
      const modalities = isRecord(m['modalities']) ? m['modalities'] : {}
      const input = strings(modalities['input'])
      const output = strings(modalities['output'])
      // 只收「只输出文本」的对话模型;能输出图像 / 视频 / 语音的(如 Nano Banana)
      // 走各自的页签,字段也不一样,放进文本目录会被当成普通对话模型
      if (output.length !== 1 || output[0] !== 'text' || !input.includes('text')) continue
      const lower = id.toLowerCase()
      if (seen.has(lower)) continue
      seen.add(lower)

      const existing = userById.get(lower) ?? findBuiltinModel(id)
      if (existing !== undefined) {
        const update = diffExisting(existing, m, vendor, fetchedAt)
        if (update !== null && isModelCatalogDefinition(update.incoming)) updates.push(update)
        continue
      }

      const reasoning = m['reasoning'] === true
      const donor = reasoning ? donorThinking(manufacturerId) : undefined
      const limit = isRecord(m['limit']) ? m['limit'] : {}
      const capabilities: ModelCapabilities = {
        tools: m['tool_call'] === true,
        vision: input.includes('image'),
        visionInput: input.includes('image'),
        thinking: donor !== undefined,
        caching: false,
        textInput: true,
        fileInput: input.includes('pdf'),
        videoInput: input.includes('video'),
        audioInput: input.includes('audio'),
        textOutput: true,
        imageOutput: false,
        videoOutput: false,
        audioOutput: false,
        webSearch: false,
        structuredOutput: m['structured_output'] === true,
        streaming: false,
        batch: false
      }
      const effortOption = Array.isArray(m['reasoning_options'])
        ? m['reasoning_options'].find((o): o is Record<string, unknown> => isRecord(o) && o['type'] === 'effort')
        : undefined
      const efforts = effortOption === undefined ? [] : strings(effortOption['values']).filter((v): v is ReasoningEffort => (KNOWN_EFFORTS as readonly string[]).includes(v))
      const reasoningEfforts = donor === undefined ? undefined : efforts.length > 0 ? efforts : donor.efforts

      const candidate: ModelCatalogDefinition = {
        id,
        manufacturerId,
        manufacturerLabel: label,
        displayName: typeof m['name'] === 'string' && m['name'] !== '' ? m['name'] : id,
        modality: 'text',
        capabilities,
        contextWindow: posInt(limit['context'], 200_000),
        // 目录校验要求输出上限不超过上下文窗口;聚合源偶尔两者写反
        maxOutputTokens: Math.min(posInt(limit['output'], 16_384), posInt(limit['context'], 200_000)),
        thinkingConfig: donor === undefined ? { mode: 'unsupported', defaultEnabled: false } : fitThinking(donor.config, reasoningEfforts),
        ...(reasoningEfforts === undefined ? {} : { reasoningEfforts }),
        source: { url: `${MODELS_DEV_URL}#${vendor}/${id}`, fetchedAt },
        verificationStatus: 'aggregator-reference'
      }
      // 最后一道闸:任何一行过不了目录校验就丢掉它,而不是让整批写入失败
      if (isModelCatalogDefinition(candidate)) added.push(candidate)
    }
  }
  return { added, updates }
}

/** `modelCatalog:syncPreview` 的返回 */
export interface ModelCatalogSyncPreview {
  fetchedAt: string
  /** 目录里还没有的 */
  items: ModelCatalogDefinition[]
  /** 目录里已有、但 models.dev 给出不同值的 */
  updates: ModelCatalogUpdateItem[]
}
