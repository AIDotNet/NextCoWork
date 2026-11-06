import { describe, expect, it } from 'vitest'
import { BRANDS, resolveBrand, type Brand } from '../brands'

/**
 * 每个牌子至少一个真实样本。这张表同时干两件事:
 *
 * 1. **覆盖**:`BRANDS` 里加了一项却忘了在 `RULES` 里加规则,
 *    下面「每个 Brand 都至少有一条样本」那条会挂 —— 否则那就是一个
 *    永远匹配不到的死项,不会有任何人发现。
 * 2. **顺序**:几条是刻意挑的对抗样本 —— `claude-*` 不能落到 anthropic、
 *    `kimi-*` 不能落到 moonshot、`Ollama` 不能落到 meta。
 *    正则表顺序错一位,logo 就显示成别家的,而且没有任何人会报这个 bug。
 */
const SAMPLES: readonly (readonly [string, Brand])[] = [
  ['claude-fable-5-1', 'claude'],
  ['Anthropic', 'anthropic'],
  ['gpt-4o', 'openai'],
  ['o3-mini', 'openai'],
  ['deepseek-v3', 'deepseek'],
  ['qwen-max', 'qwen'],
  ['kimi-k2', 'kimi'],
  ['moonshot-v1-128k', 'moonshot'],
  ['glm-4.6', 'zhipu'],
  ['gemini-2.5-pro', 'gemini'],
  ['grok-4', 'xai'],
  ['llama-3.3-70b', 'meta'],
  ['mistral-large', 'mistral'],
  ['abab6.5s', 'minimax'],
  ['doubao-pro-32k', 'doubao'],
  ['火山方舟', 'volcengine'],
  ['hunyuan-turbo', 'hunyuan'],
  ['spark-4.0', 'spark'],
  ['step-2', 'stepfun'],
  ['baichuan4', 'baichuan'],
  ['yi-large', 'yi'],
  ['OpenRouter', 'openrouter'],
  ['SiliconCloud', 'siliconcloud'],
  ['Ollama', 'ollama'],
  ['LM Studio', 'lmstudio'],
  ['Groq', 'groq'],
  ['Together AI', 'together'],
  ['Fireworks AI', 'fireworks'],
  ['sonar-pro', 'perplexity'],
  ['AWS Bedrock', 'bedrock'],
  ['Vertex AI', 'vertexai'],
  ['Azure', 'azure'],
  ['302.AI', 'ai302'],
  ['AiHubMix', 'aihubmix'],
  ['Atlas Cloud', 'atlascloud'],
  ['atlascloud', 'atlascloud'],
  ['百度千帆', 'baiducloud'],
  ['阿里百炼', 'bailian'],
  ['command-r-plus', 'cohere'],
  ['DeepInfra', 'deepinfra'],
  ['Jan', 'menlo'],
  ['OpenCode Go', 'opencode'],
  ['RoutinAI', 'routin'],
  ['商汤日日新', 'sensenova'],
  ['vLLM', 'vllm'],
  ['Z.AI', 'zai'],
  ['mimo-v2.6-pro', 'xiaomimimo'],
  ['xiaomi/mimo-v2.5', 'xiaomimimo'],
  ['小米 MiMo', 'xiaomimimo']
]

describe('resolveBrand', () => {
  it.each(SAMPLES)('%s → %s', (input, expected) => {
    expect(resolveBrand(input)).toBe(expected)
  })

  it('每个 Brand 都至少有一条样本 —— 漏了就是有牌子永远匹配不到', () => {
    const covered = new Set<Brand>(SAMPLES.map(([, b]) => b))
    expect(BRANDS.filter((b) => !covered.has(b))).toEqual([])
  })

  describe('顺序敏感的对抗样本', () => {
    it('claude 排在 anthropic 前 —— 模型显示 Claude 字形而不是 Anthropic 的 A', () => {
      expect(resolveBrand('claude-3-5-sonnet')).toBe('claude')
    })

    it('kimi 排在 moonshot 前 —— 两个字形都有,各显示各的', () => {
      expect(resolveBrand('kimi-latest')).toBe('kimi')
    })

    it('doubao 排在 volcengine 前 —— 火山引擎上的豆包该显示豆包', () => {
      expect(resolveBrand('doubao-1.5-pro')).toBe('doubao')
    })

    it('★ ollama 排在 meta 前 ——「Ollama」里含着「llama」', () => {
      expect(resolveBrand('Ollama')).toBe('ollama')
      // 但 codellama 仍然要落到 meta:所以是靠顺序解决,不是靠 \bllama 收紧
      expect(resolveBrand('codellama-70b')).toBe('meta')
    })

    it('「Azure OpenAI」显示 OpenAI 而不是 Azure —— 用户关心的是模型家族', () => {
      expect(resolveBrand('Azure OpenAI')).toBe('openai')
      expect(resolveBrand('Azure')).toBe('azure')
    })

    it('★ zai 排在 zhipu 前 ——「Z.AI(智谱国际)」里含着「智谱」', () => {
      expect(resolveBrand('Z.AI(智谱国际)按量')).toBe('zai')
      expect(resolveBrand('Z.AI Coding Plan(订阅制)')).toBe('zai')
      // 国内那两条仍然是智谱
      expect(resolveBrand('智谱·按量 API(国内)')).toBe('zhipu')
    })

    it('★ bailian 排在 qwen 前 —— 卡片说的是平台,模型名说的是家族', () => {
      expect(resolveBrand('阿里百炼 / 通义(国内)')).toBe('bailian')
      // 而**模型**名里没有「百炼」,照旧落到 qwen —— 两条轴各自成立
      expect(resolveBrand('qwen3-max')).toBe('qwen')
      expect(resolveBrand('通义千问')).toBe('qwen')
    })
  })

  /**
   * ★★ 这一组守的是**曾经真的错了**的那三条:「添加供应商」目录里
   * Google Gemini 和 Cohere 挂着 OpenAI 的 logo、llama.cpp 挂着 Meta 的 ∞。
   * 三个都不报错,只是错 —— 没有这几条断言,它们会再错回去。
   */
  describe('协议注记不是牌子', () => {
    it('「(OpenAI 兼容)」说的是端点形状,不是这家公司', () => {
      expect(resolveBrand('Google Gemini(OpenAI 兼容)')).toBe('gemini')
      expect(resolveBrand('Cohere(OpenAI 兼容)')).toBe('cohere')
    })

    it('半角括号和英文 compatible 一样吃掉', () => {
      expect(resolveBrand('Gemini (OpenAI compatible)')).toBe('gemini')
    })

    it('括号里**不含**「兼容」的一个字都不动', () => {
      // 这条是上面那个正则「窄」的证据:去掉的只能是协议注记
      expect(resolveBrand('我司网关(转发 OpenAI)')).toBe('openai')
    })
  })

  describe('★ null 规则:挡住后面更松的规则,但不终止查找', () => {
    it('llama.cpp 不是 Meta 的项目 —— 宁可退回首字母,也不挂错 logo', () => {
      expect(resolveBrand('llama.cpp server')).toBeNull()
      expect(resolveBrand('llamacpp')).toBeNull()
      expect(resolveBrand('llama-cpp-python')).toBeNull()
    })

    it('被挡掉的只是**这个**候选,下一个候选照试', () => {
      // Composer 传的是 [模型名, 供应商名];模型名被挡掉,供应商名仍然值得一试
      expect(resolveBrand('llama.cpp server', 'DeepSeek')).toBe('deepseek')
    })

    it('真正的 llama 模型不受影响', () => {
      expect(resolveBrand('llama-3.3-70b')).toBe('meta')
    })
  })

  describe('短 token 的词边界 —— 这是用正则不用 includes 的全部理由', () => {
    it('o1/o3/o4 不在别的名字中间误命中', () => {
      expect(resolveBrand('deepseek-r1')).toBe('deepseek')
      expect(resolveBrand('mono1x')).toBeNull()
      expect(resolveBrand('proto3-model')).toBeNull()
    })

    it('meta 不匹配 metallama 里的 meta(而 llama 规则会先接住它)', () => {
      expect(resolveBrand('metallama')).toBe('meta')
      expect(resolveBrand('metadata-tool')).toBeNull()
    })

    it('★ routin 不误伤「routing」—— 差一个字母,而且是个到处都在的词', () => {
      expect(resolveBrand('routing')).toBeNull()
      expect(resolveBrand('Smart Routing Gateway')).toBeNull()
      // 空格 / 连字符 / 下划线都算同一个名字
      expect(resolveBrand('Routin AI')).toBe('routin')
      expect(resolveBrand('routin-ai')).toBe('routin')
      // 光秃秃的预设 id 也要认得 —— 用户把它改名之后就只剩这个
      expect(resolveBrand('routin')).toBe('routin')
      // 订阅制那条(/plan/v1)是同一家,同一张 webp
      expect(resolveBrand('RoutinAI(订阅)')).toBe('routin')
      expect(resolveBrand('routin-plan')).toBe('routin')
      // ★ 但别把 `^routin(-plan)?$` 放宽成前缀匹配 —— 那样会认下任何 routin- 开头的名字
      expect(resolveBrand('routin-gateway')).toBeNull()
    })
  })

  describe('中文名 —— 设置页里供应商名是用户自己填的', () => {
    it.each([
      ['深度求索', 'deepseek'],
      ['通义千问', 'qwen'],
      ['智谱清言', 'zhipu'],
      ['月之暗面', 'moonshot'],
      ['腾讯混元', 'hunyuan'],
      ['讯飞星火', 'spark'],
      ['百川智能', 'baichuan'],
      ['零一万物', 'yi'],
      ['硅基流动', 'siliconcloud']
    ] as const)('%s → %s', (input, expected) => {
      expect(resolveBrand(input)).toBe(expected)
    })
  })

  describe('多候选', () => {
    it('第一个命中的赢', () => {
      expect(resolveBrand('claude-fable-5-1', 'DeepSeek')).toBe('claude')
    })

    it('provider 认不出时,退到模型别名上', () => {
      expect(resolveBrand('我司内部网关', 'claude-fable-5-1')).toBe('claude')
    })

    /**
     * ★★ RoutinAI 是内置上游,**它自己有 logo**,而它下面跑的是 claude 别名。
     * 于是同一对名字换个顺序传进来,答案该不一样 —— 这不是矛盾,是两个界面在问
     * 两个问题:正文里问「这条是哪个模型答的」,供应商列表里问「这是哪一家」。
     *
     * 写下来是因为它看着像不一致:哪天有人「顺手把调用点的参数顺序统一一下」,
     * 坏掉的是其中一边,而且不报错 —— 只是图标默默换成了另一个。
     */
    it('★ RoutinAI 上的 claude 模型:正文显示 Claude,供应商列表显示 RoutinAI', () => {
      expect(resolveBrand('claude-fable-5-1', 'RoutinAI')).toBe('claude')
      expect(resolveBrand('RoutinAI', 'routin')).toBe('routin')
    })

    it('跳过 undefined 和空串,不当成命中', () => {
      expect(resolveBrand(undefined, '', 'qwen-max')).toBe('qwen')
    })
  })

  describe('认不出是正常路径,不是错误', () => {
    it.each([['我司内部网关'], ['OhMyGPT'], ['']])('%s → null', (input) => {
      expect(resolveBrand(input)).toBeNull()
    })

    it('一个候选都没有也返回 null', () => {
      expect(resolveBrand()).toBeNull()
    })
  })
})
