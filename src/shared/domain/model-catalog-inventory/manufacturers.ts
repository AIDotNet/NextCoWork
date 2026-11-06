import type { ModelManufacturer } from './types'

/**
 * Vendor families shown in the model-management sidebar.  Do not add a
 * configured-provider entry here: this list is about model manufacturers.
 */
export const MODEL_MANUFACTURERS: readonly ModelManufacturer[] = [
  { id: 'openai', label: 'OpenAI', aliases: ['openai', 'gpt', 'chatgpt'] },
  /*
   * Ollama 云/本地两线的模型目录家族 —— 条目只带 `:tag` 形态的名字
   * (见 vendors/ollama.ts 的文件头)。注册成独立家族而不是挂进 openai:
     `gpt-oss:120b` 和 `gpt-oss-120b` 在这边是两个不同端点上的两套行为。
  */
  { id: 'ollama', label: 'Ollama', aliases: ['ollama'] },
  { id: 'anthropic', label: 'Anthropic', aliases: ['anthropic', 'claude'] },
  {
    id: 'google',
    label: 'Google',
    aliases: ['google', 'gemini', 'gemma', 'vertex'],
  },
  { id: 'deepseek', label: 'DeepSeek', aliases: ['deepseek', 'deep-seek'] },
  {
    id: 'zhipu',
    label: '智谱 GLM',
    aliases: ['zhipu', 'z.ai', 'glm', 'chatglm'],
  },
  {
    id: 'qwen',
    label: '阿里云 Qwen',
    aliases: ['qwen', 'qwq', 'tongyi', '通义', 'dashscope'],
  },
  {
    id: 'moonshot',
    label: 'Moonshot / Kimi',
    aliases: ['moonshot', 'kimi', '月之暗面'],
  },
  { id: 'xai', label: 'xAI Grok', aliases: ['xai', 'grok'] },
  {
    id: 'mistral',
    label: 'Mistral AI',
    aliases: ['mistral', 'mixtral', 'codestral', 'devstral', 'pixtral'],
  },
  { id: 'cohere', label: 'Cohere', aliases: ['cohere', 'command'] },
  { id: 'minimax', label: 'MiniMax', aliases: ['minimax', 'abab'] },
  {
    id: 'hunyuan',
    label: '腾讯混元 / Hunyuan',
    aliases: ['hunyuan', '腾讯混元', 'hy-muse'],
  },
  { id: 'xiaomi', label: '小米 MiMo', aliases: ['xiaomi', 'mimo', '小米'] },
  {
    id: 'meituan',
    label: '美团 LongCat',
    aliases: ['meituan', 'longcat', '美团'],
  },
  {
    id: 'doubao',
    label: '字节跳动豆包 / Doubao',
    aliases: ['doubao', 'seed', 'seedream', 'seedance'],
  },
  {
    id: 'baidu',
    label: '百度文心 / ERNIE',
    aliases: ['baidu', 'ernie', 'wenxin', '文心'],
  },
  {
    id: 'stepfun',
    label: '阶跃星辰 StepFun',
    aliases: ['stepfun', 'step-', '阶跃'],
  },
  { id: 'baichuan', label: '百川智能', aliases: ['baichuan', '百川'] },
  {
    id: 'sensenova',
    label: '商汤 SenseNova',
    aliases: ['sensenova', 'sensechat', '商汤', '日日新'],
  },
  {
    id: 'spark',
    label: '讯飞星火',
    aliases: ['spark', 'xfyun', '讯飞', '星火'],
  },
  {
    id: 'pangu',
    label: '华为盘古 / openPangu',
    aliases: ['pangu', 'openpangu', '华为盘古'],
  },
  { id: 'yi', label: '零一万物 Yi', aliases: ['yi-', '01.ai', '零一万物'] },
  { id: 'meta', label: 'Meta', aliases: ['meta', 'llama', 'muse'] },
  // label 原为 'Microsoft Phi';该家族收录 MAI-Code 后 Phi 不再是全量,改成厂商名。
  // 'mai' 别名让 manufacturerForModelId 把 `mai-` 前缀的 id 归进本家而不是 'other'。
  { id: 'microsoft', label: 'Microsoft', aliases: ['microsoft', 'phi', 'mai'] },
  {
    id: 'amazon',
    label: 'Amazon Nova',
    aliases: ['amazon', 'nova', 'bedrock'],
  },
  { id: 'ai21', label: 'AI21 Labs Jamba', aliases: ['ai21', 'jamba'] },
  { id: 'perplexity', label: 'Perplexity', aliases: ['perplexity', 'sonar'] },
  {
    id: 'internlm',
    label: '上海人工智能实验室 InternLM',
    aliases: ['internlm', 'internvl'],
  },
  // ── 视频厂商(本轮接入) ──
  // ★ 这几个家族是**按模型品牌**分的,不是按谁能提供它。Veo 挂 google、
  //   Kling 挂 kling、Wan 挂 qwen(阿里云),而聚合商(fal / Replicate 自己
  //   也是家族)各自成一条 —— 同一个 veo 名字可以由原厂和聚合站同时提供,
  //   绑定里存的是 providerId,品牌这里只用来归属。
  { id: 'runway', label: 'Runway', aliases: ['runway', 'gen4', 'gen3'] },
  { id: 'luma', label: 'Luma AI', aliases: ['luma', 'ray-', 'dream-machine'] },
  { id: 'kling', label: '可灵 Kling', aliases: ['kling', 'klingai', '可灵'] },
  { id: 'fal', label: 'fal.ai', aliases: ['fal-ai', 'fal'] },
  { id: 'replicate', label: 'Replicate', aliases: ['replicate'] },
  { id: 'siliconflow', label: '硅基流动 SiliconFlow', aliases: ['siliconflow', '硅基流动'] },
  { id: 'other', label: '其他厂商', aliases: [] },
]

const MANUFACTURER_BY_ID = new Map(MODEL_MANUFACTURERS.map((m) => [m.id, m]))

/** Resolve a vendor family from a model ID, including aggregator prefixes. */
export function manufacturerForModelId(modelId: string): ModelManufacturer {
  const value = modelId.trim()
  const segments = value.split('/').filter(Boolean)
  for (const segment of segments) {
    const lower = segment.toLowerCase()
    for (const manufacturer of MODEL_MANUFACTURERS) {
      if (manufacturer.id === 'other') continue
      if (
        manufacturer.aliases.some((alias) => {
          const a = alias.toLowerCase()
          if (a === 'step-') return lower.startsWith(a)
          if (a === 'yi-') return lower.startsWith(a)
          return lower === a || lower.startsWith(`${a}-`) || lower.startsWith(`${a}:`)
        })
      ) {
        return manufacturer
      }
    }
  }
  return MANUFACTURER_BY_ID.get('other')!
}

/** Look up a manufacturer by ID, falling back to the catch-all `other` entry. */
export function manufacturer(id: string): ModelManufacturer {
  return MANUFACTURER_BY_ID.get(id) ?? MANUFACTURER_BY_ID.get('other')!
}
