/**
 * 48 个内置预设各自该显示**谁**的 logo —— 一张钉死的对照表。
 *
 * **为什么值得单独一个文件:** `brands.test.ts` 守的是规则表本身自洽
 * (每个牌子有样本、顺序不打架),但它不知道「添加供应商」目录里到底摆着
 * 哪 48 个名字。而两张表是**分开演进**的:presets.ts 加一家、或者给某家改个
 * 更好听的名字,`brands.ts` 一个字都不用动就能悄悄错掉一个 logo。
 *
 * ★★ 这不是假想。这张表第一次跑出来时,43 家里 **13 家没有图标、3 家挂着
 * 别人的 logo**:
 *   - 「Google Gemini(OpenAI 兼容)」→ OpenAI
 *   - 「Cohere(OpenAI 兼容)」  → OpenAI
 *   - 「llama.cpp server」       → Meta
 * 三个都不报错,只是错。**没有断言的东西就会漂**,所以整表钉死,
 * 加一家预设就必须在这里写清楚它显示谁 —— 下面最后一条会强制这件事。
 *
 * 断言用的候选顺序和界面上**一模一样**(`ProviderAvatar` 传的是
 * `[name, id]`)。名字在前是有意的:`gemini-openai` 这个 **id** 里含着
 * `openai`,拿 id 先匹配又会绕回那个刚修好的错。
 */
import { describe, expect, it } from 'vitest'
import { PROVIDER_PRESETS } from '../../../../../../shared/domain/presets'
import { resolveBrand, type Brand } from '../../../../components/brand/brands'

/**
 * `null` = 我们**故意**不给它图标,退回首字母。
 * 四家都是 lobehub 根本没收字形的(逐个在 `@lobehub/icons-static-svg/icons/`
 * 下找过):Requesty、OhMyGPT、LocalAI,以及 llama.cpp —— 最后这个是 ggerganov 的
 * 独立项目,不是 Meta 的东西。
 *
 * ★ 编一个「差不多的」图标比没有图标更糟:用户看见 Meta 的 ∞ 会以为
 * 自己配的是 llama 官方的什么东西。
 *
 * RoutinAI 曾经也在这一列 —— 它现在是唯一一个走光栅图的牌子
 * (`resources/images/routin-ai.webp`,见 `ProviderIcon` 的 `RASTER`)。
 */
const EXPECTED: Readonly<Record<string, Brand | null>> = {
  // ── 海外 ──
  openai: 'openai',
  // ★ Codex 是 OpenAI 的产品线，走的是 ChatGPT 订阅账号 —— 图标仍是 OpenAI 那个
  codex: 'openai',
  anthropic: 'anthropic',
  // ★ 名字里那个「(OpenAI 兼容)」说的是端点形状,不是这家公司
  'gemini-openai': 'gemini',
  xai: 'xai',
  // ★ Grok Build 是同一家的订阅通道，图标仍是 xAI 那个（同 codex ↔ openai）
  'grok-build': 'xai',
  mistral: 'mistral',
  cohere: 'cohere',
  // ★ Ollama Cloud 和本地那条「Ollama」是同一家的两条线，共用同一个字形
  'ollama-cloud': 'ollama',

  // ── 国内 ──
  deepseek: 'deepseek',
  moonshot: 'moonshot',
  'moonshot-global': 'moonshot',
  'kimi-coding': 'kimi',
  zhipu: 'zhipu',
  'zhipu-coding': 'zhipu',
  // ★ Z.AI 是智谱的国际品牌,有自己的字形 —— 名字里带着「智谱」但不能显示智谱
  zai: 'zai',
  'zai-coding': 'zai',
  minimax: 'minimax',
  'minimax-global': 'minimax',
  // ★ 卡片说的是**百炼这个平台**,不是通义那个模型家族
  dashscope: 'bailian',
  'dashscope-intl': 'bailian',
  // 「火山方舟·豆包」显示豆包 —— 用户是冲着豆包来的,方舟是承载它的平台
  volcengine: 'doubao',
  qianfan: 'baiducloud',
  hunyuan: 'hunyuan',
  stepfun: 'stepfun',
  baichuan: 'baichuan',
  sensenova: 'sensenova',
  spark: 'spark',

  // ── 聚合 ──
  // 内置上游,自家的 webp,不是 lobehub 的字形
  routin: 'routin',
  // 订阅制那条(/plan/v1,Codex 系模型)—— 同一家,同一张 webp。
  // 它的 id 不是光秃秃的 `routin`,靠**名字**里的 RoutinAI 命中;
  // `brands.ts` 的 `^routin(-plan)?$` 是给用户改名之后兜底的第二条路。
  'routin-plan': 'routin',
  openrouter: 'openrouter',
  requesty: null,
  siliconflow: 'siliconcloud',
  'siliconflow-intl': 'siliconcloud',
  together: 'together',
  fireworks: 'fireworks',
  deepinfra: 'deepinfra',
  groq: 'groq',
  'opencode-go': 'opencode',
  aihubmix: 'aihubmix',
  '302ai': 'ai302',
  ohmygpt: null,

  // ── 本地 ──
  ollama: 'ollama',
  lmstudio: 'lmstudio',
  vllm: 'vllm',
  llamacpp: null,
  localai: null,
  // Jan 是 Menlo Research 的产品,lobehub 的字形挂在 menlo 名下
  jan: 'menlo'
}

describe('内置预设的品牌图标', () => {
  it.each(PROVIDER_PRESETS.map((p) => [p.id, p.name] as const))('%s 「%s」', (id, name) => {
    expect(resolveBrand(name, id)).toBe(EXPECTED[id])
  })

  it('★ 每个预设都在对照表里 —— 加了一家却没写显示谁,在这里红', () => {
    const missing = PROVIDER_PRESETS.filter((p) => !Object.hasOwn(EXPECTED, p.id)).map((p) => p.id)
    expect(missing).toEqual([])
  })

  it('对照表里没有多余的 id —— 预设删掉了这里也要跟着删', () => {
    const ids = new Set(PROVIDER_PRESETS.map((p) => p.id))
    expect(Object.keys(EXPECTED).filter((id) => !ids.has(id))).toEqual([])
  })

  /**
   * 认不出的那几家会**并排**显示在同一列表里,退回首字母才能彼此区分 ——
   * 全退回同一颗 `Sparkles` 的话,三家看起来一模一样。这条断言守的是
   * 「null 只有这么几家」:某天规则表改窄了让一堆家一起掉进 null,
   * 这里会红,而界面上只会安静地多出几个字母。
   */
  it('没有图标的就是这四家,不多不少', () => {
    const blank = PROVIDER_PRESETS.filter((p) => resolveBrand(p.name, p.id) === null).map(
      (p) => p.id
    )
    expect(blank).toEqual(['requesty', 'ohmygpt', 'llamacpp', 'localai'])
  })
})
