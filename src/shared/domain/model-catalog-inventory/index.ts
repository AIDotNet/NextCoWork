/**
 * Built-in model inventory.
 *
 * This is deliberately separate from `ProviderPreset` and from the models a
 * user has configured.  A provider preset describes a connection (URL,
 * protocol and credentials); this file describes the models that exist in the
 * vendor catalogue.  Keeping those axes separate means the model-management
 * page can show every known model before a connection has been added.
 *
 * The inventory is a curated, versioned snapshot.  It is intentionally free
 * of user secrets and runtime provider state.  IDs are the IDs used by the
 * vendors where known; aliases cover the spelling used by compatible gateways.
 *
 * Data is organized one file per vendor under `vendors/`; `AUDIO_MODELS` is the
 * one exception, grouped by modality since speech/audio models span vendors.
 */
export type { BuiltinModelRecord, ModelManufacturer, ReasoningEffort } from './types'
export { MODEL_MANUFACTURERS, manufacturerForModelId } from './manufacturers'
export { MODEL_CATALOG_FETCHED_AT } from './helpers'

/*
 * ★★ Ollama 的思考线形由 `model-binding.ts` 用在「模型绑到 Ollama 系供应商」
 * 这条路径上 —— 它不是某一个条目的私有数据,是那家供应商对**所有**模型的线形,
 * 所以从目录入口导出(见 vendors/ollama.ts 的文件头)。
 */
export { OLLAMA_REASONING_EFFORTS, OLLAMA_STANDARD_THINKING } from './vendors/ollama'

import type { BuiltinModelRecord } from './types'
import type { UpstreamProtocol } from '../provider'
import { OPENAI, OPENAI_MEDIA } from './vendors/openai'
import { OLLAMA } from './vendors/ollama'
import { ANTHROPIC } from './vendors/anthropic'
import { GOOGLE } from './vendors/google'
import { DEEPSEEK } from './vendors/deepseek'
import { ZHIPU } from './vendors/zhipu'
import { QWEN } from './vendors/qwen'
import { MOONSHOT } from './vendors/moonshot'
import { XAI } from './vendors/xai'
import { MISTRAL } from './vendors/mistral'
import { COHERE } from './vendors/cohere'
import { MINIMAX } from './vendors/minimax'
import { HUNYUAN } from './vendors/hunyuan'
import { XIAOMI } from './vendors/xiaomi'
import { MUSE, META } from './vendors/meta'
import { MEITUAN } from './vendors/meituan'
import { OPENCODE_OTHER } from './vendors/other'
import { DOUBAO } from './vendors/doubao'
import { BAIDU } from './vendors/baidu'
import { STEPFUN } from './vendors/stepfun'
import { BAICHUAN } from './vendors/baichuan'
import { SENSENOVA } from './vendors/sensenova'
import { SPARK } from './vendors/spark'
import { PANGU } from './vendors/pangu'
import { YI } from './vendors/yi'
import { MICROSOFT } from './vendors/microsoft'
import { AMAZON } from './vendors/amazon'
import { AUDIO_MODELS } from './vendors/audio'
import { AI21 } from './vendors/ai21'
import { PERPLEXITY } from './vendors/perplexity'
import { INTERNLM } from './vendors/internlm'
import { RUNWAY } from './vendors/runway'
import { LUMA } from './vendors/luma'
import { KLING } from './vendors/kling'
import { FAL } from './vendors/fal'
import { REPLICATE } from './vendors/replicate'
import { SILICONFLOW } from './vendors/siliconflow'

/**
 * The complete built-in catalogue.  Keep this list independent from
 * `PROVIDER_PRESETS`: adding a connection must never be required for a model
 * to appear here.  User-created records are merged by the catalog service.
 */
export const BUILTIN_MODEL_CATALOG: readonly BuiltinModelRecord[] = [
  ...OPENAI,
  ...OLLAMA,
  ...OPENAI_MEDIA,
  ...ANTHROPIC,
  ...GOOGLE,
  ...DEEPSEEK,
  ...ZHIPU,
  ...QWEN,
  ...MOONSHOT,
  ...XAI,
  ...MISTRAL,
  ...COHERE,
  ...MINIMAX,
  ...HUNYUAN,
  ...XIAOMI,
  ...MUSE,
  ...MEITUAN,
  ...OPENCODE_OTHER,
  ...DOUBAO,
  ...BAIDU,
  ...STEPFUN,
  ...BAICHUAN,
  ...SENSENOVA,
  ...SPARK,
  ...PANGU,
  ...YI,
  ...META,
  ...MICROSOFT,
  ...AMAZON,
  ...AUDIO_MODELS,
  ...AI21,
  ...PERPLEXITY,
  ...INTERNLM,
  // ★ 视频厂商放在最后:它们在「模型管理」的左列里排成一组,而这里**不是**
  //   排序的真源(那边按 MODEL_MANUFACTURERS 的顺序),只是让新增厂商不打断
  //   上面那批文本厂商的相对顺序。
  ...RUNWAY,
  ...LUMA,
  ...KLING,
  ...FAL,
  ...REPLICATE,
  ...SILICONFLOW,
]

/** Case-insensitive lookup, including aliases and aggregator-prefixed IDs. */
export function findBuiltinModel(modelId: string): BuiltinModelRecord | undefined {
  const wanted = modelId.trim().toLowerCase()
  return BUILTIN_MODEL_CATALOG.find((entry) => {
    const ids = [entry.id, ...(entry.aliases ?? [])]
    return ids.some((id) => {
      const candidate = id.toLowerCase()
      return wanted === candidate || wanted.endsWith(`/${candidate}`)
    })
  })
}

/**
 * Provider model-list endpoints do not expose a modality field. For image
 * imports we therefore use the bundled catalogue first, then a conservative
 * ID heuristic for preview/private image models that are not catalogued yet.
 *
 * 需求:两个消费方共用这一条判断,别再写第二份 ——
 * 1. 渲染层「导入模型」弹窗的图片模态过滤(`settings/pages/model/import-models.ts`);
 *    分家的表现是同一个 ID 在图片页「拉取列表」时被过滤掉、换个入口又冒出来。
 * 2. 主进程登录灌模型(`ipc/client-auth.ts` 的 `syncClientModels`):平台的
 *    `/v1/models` 不标模态,不补的话登录拿到的生图模型 modality 是 text,
 *    「图片生成」页看不见它,且**全程零报错**。
 *
 * 目录优先、正则兜底:目录认得的以目录为准(有官方核对背书),认不得的
 * 预览/私有 ID 只保守按名字猜 —— 猜漏只是少列一条,猜错会把文本模型塞进图片页。
 */
export function isImageModelId(modelId: string): boolean {
  const wanted = modelId.trim().toLowerCase()
  const known = BUILTIN_MODEL_CATALOG.find((model) =>
    [model.id, ...(model.aliases ?? [])].some((id) => {
      const value = id.toLowerCase()
      return wanted === value || wanted.endsWith(`/${value}`)
    }),
  )
  if (known !== undefined) return known.modality === 'image'
  /*
    ★★ \`seedance\` **不在这张表里**,这是刻意的:它是字节的生**视频**线
    (Seedance),而 \`seedream\` 才是生图线。原先两条正则都收 \`seedance\`,
    于是一个目录没收录的预览版 \`doubao-seedance-2-1-*\` 会**同时**被认成
    图片模型和视频模型 —— 它出现在图片页(选中就失败),又出现在视频页,
    而两处都不报错。目录认得的那几条靠 catalog-first 分支返回,不受影响;
    这条规则管的是目录还没收录的新型号。
  */
  return /(?:^|[/_:-])(image|imagen|dall[-_]?e|dalle|flux|seedream|z[-_]?image|imagegen|imagine[-_]image|wanx|kolors|sdxl|stable[-_]?diffusion|ideogram|midjourney|recraft|qwen[-_]?image|pixart|playground)(?:$|[/:.-])/i.test(wanted)
}

/**
 * 这条 id 是**视频生成模型**吗。
 *
 * 需求:与 `isImageModelId` 同构、同一批消费方(导入弹窗、登录灌模型、
 * 拉列表落别名)—— 分家的表现同样是"能拉下来却在视频页看不见它,且零报错"。
 *
 * ★★ **判据里没有"视频"这个词本身。** 视频型号名和图片型号名大量重叠
 *    (`seedance` 是视频、`seedream` 是图片;两者都是"seed"开头),拿
 *    `/video/` 去猜会漏掉绝大多数,拿 `seed` 去猜会同时命中两边。
 *    所以只有两条路:**目录认得就信目录**,认不得就只认明确的 `-video-` /
 *    官方能确定的前缀。猜漏只是少列一条(用户仍可手动添加);猜错会把文本模型
 *    塞进视频页 —— 而那一栏里的每个选项都要花钱。
 */
export function isVideoModelId(modelId: string): boolean {
  const wanted = modelId.trim().toLowerCase()
  const known = BUILTIN_MODEL_CATALOG.find((model) =>
    [model.id, ...(model.aliases ?? [])].some((id) => {
      const value = id.toLowerCase()
      return wanted === value || wanted.endsWith(`/${value}`)
    }),
  )
  if (known !== undefined) return known.modality === 'video'
  /*
    ★ 兜底只留**官方就是视频线**的那些前缀/形状,不做"含 video 就算"的宽松匹配:

    - `…video…` 段(带分隔符,或整段就是它):`grok-imagine-video-1.5`、
      `hunyuan-video`、`nova-reel-v1`、`wan2.7-t2v` 这类;
    - 明确的视频品牌前缀:`veo-` / `sora-` / `kling-` / `hailuo-` /
      `cogvideo` / `seedance` / `minimax-h` / `ray-`;
    - `wan` 的 t2v / i2v / video 后缀。

    ★ 猜漏只是少列一条(用户仍能在视频页手动添加);猜错会把**文本**模型塞进
      视频页,而那一栏里每个选项都要花钱 —— 两个方向的代价不对称,所以这里
      宁窄勿宽。
  */
  return /(?:^|[/_:.-])(veo|sora|kling|hailuo|cogvideo|seedance|minimax)[-_]?[a-z0-9]/i.test(wanted) ||
    /(?:^|[/_:.-])ray[-_]?\d/i.test(wanted) ||
    /(?:^|[/_:.-])wan\d*\.?\d*[-_]?(t2v|i2v|video)/i.test(wanted) ||
    /(?:^|[/_:.-])video(?:$|[/_:.-])|[/_:.-]video(?:$|[/_:.-])/i.test(wanted) ||
    /(?:^|[/_:.-])nova[-_]?reel/i.test(wanted)
}

/**
 * ★★ 厂商默认线形协议 —— 必须是**数据表**,不能散成各处的 `if (id.startsWith('claude'))`。
 * 「claude 系模型默认走 anthropic 线形」这条规则有三个消费方:内置种子
 * (`runtime.ts` 的 `builtinAlias`)、拉取模型列表(`ipc/provider.ts` 的 `setAliases`)
 * 和老库回填(`runtime.ts` 那段一次性迁移)。各写一遍分支,漏掉一处的表现是
 * 同一个模型在不同入口拿到不同协议,而且不报错。
 *
 * 只登记「线形错了有实质损失」的厂商。目前只有 anthropic:Claude 的思考、
 * prompt 缓存、工具语义只在 anthropic 线形上是完整的,聚合站的 OpenAI 兼容层
 * 常常把 `thinking` / `cache_control` 直接丢掉。别家(gpt、glm…)两种线形差异
 * 小得多,统一钉死反而剥夺了「跟随供应商」这个合理默认。
 */
const MANUFACTURER_DEFAULT_PROTOCOL: Readonly<Record<string, UpstreamProtocol>> = {
  anthropic: 'anthropic'
}

/**
 * 该模型按厂商归属应默认使用的线形协议;目录查不到或厂商未登记时返回 undefined ——
 * 含义是「跟随供应商协议」,不是「没有协议」。
 *
 * 聚合站的 `vendor/` 前缀 ID 与裸名等价(走 `findBuiltinModel` 的同一条匹配,
 * `anthropic/claude-fable-5.1` 命中 `claude-fable-5-1` 那条)。
 */
export function defaultProtocolForModel(modelId: string): UpstreamProtocol | undefined {
  const known = findBuiltinModel(modelId)
  const result = known === undefined ? undefined : MANUFACTURER_DEFAULT_PROTOCOL[known.manufacturerId]
  console.log('[debug-helper]', JSON.stringify(modelId), 'known=', known?.id, known?.manufacturerId, 'result=', result)
  return result
}
