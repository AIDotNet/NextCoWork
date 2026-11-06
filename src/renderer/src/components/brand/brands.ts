/**
 * 品牌识别表 —— 从自由文本猜出是哪家模型/供应商。
 *
 * 和 `ProviderIcon.tsx` 分开是有原因的:那边有一堆 `?raw` 导入,只有走 Vite
 * 才解析得了;这边是纯函数,能在普通 vitest 里跑(`vitest.config.ts`
 * 的 environment 是 node)。而这张表恰恰是最需要测的部分 ——
 * **正则顺序错一位,就会有模型显示成别家的 logo,而且没人会报 bug。**
 *
 * ★ 上面这句不是假设。内置预设表铺开之后,真的有两家在目录里挂着**别人**的
 * logo:「Google Gemini(OpenAI 兼容)」和「Cohere(OpenAI 兼容)」都被
 * `openai` 那条规则先接走了 —— 括号里那个 OpenAI 说的是**上游的 API 形状**,
 * 不是这家公司。修法见下面的 `stripProtocolNote`。
 */

/**
 * 运行期也要有这份列表(不只是类型),测试才能断言
 * **每个牌子都至少被一条规则覆盖到** —— 只有类型的话,
 * 「加了牌子但忘了写规则」是个永远匹配不到的死项,没人会发现。
 */
export const BRANDS = [
  'ai302',
  'aihubmix',
  'anthropic',
  'atlascloud',
  'azure',
  'baichuan',
  'baiducloud',
  'bailian',
  'bedrock',
  'claude',
  'cohere',
  'deepinfra',
  'deepseek',
  'doubao',
  'fireworks',
  'gemini',
  'groq',
  'hunyuan',
  'kimi',
  'lmstudio',
  'menlo',
  'meta',
  'minimax',
  'mistral',
  'moonshot',
  'ollama',
  'openai',
  'opencode',
  'openrouter',
  'perplexity',
  'qwen',
  'routin',
  'sensenova',
  'siliconcloud',
  'spark',
  'stepfun',
  'together',
  'vertexai',
  'vllm',
  'volcengine',
  'xai',
  'xiaomimimo',
  'yi',
  'zai',
  'zhipu'
] as const

export type Brand = (typeof BRANDS)[number]

/**
 * ★★ 去掉名字里的「协议兼容」注记**再**匹配。
 *
 * 预设表里「Google Gemini(OpenAI 兼容)」「Cohere(OpenAI 兼容)」这两个括号
 * 描述的是端点形状,不是厂商 —— 而 `openai` 那条规则排在 `gemini` 前面,
 * 于是这两家在「添加供应商」目录里挂着 OpenAI 的 logo。**它不报错,只是错**,
 * 正是这个文件开头警告的那一类。
 *
 * 只吃括号里含「兼容 / compatible」的那一段,别的一个字不动 ——
 * 「Azure OpenAI」没有括号,照旧落到 `openai`(那是对的,用户关心的是模型家族)。
 */
const PROTOCOL_NOTE = /[(（][^)）]*(?:兼容|compatible)[^)）]*[)）]/gi

function stripProtocolNote(name: string): string {
  return name.replace(PROTOCOL_NOTE, ' ')
}

/**
 * **顺序即优先级**,先命中先用。几处顺序是刻意的:
 *
 * - `claude` 在 `anthropic` 前:`claude-*` 是模型,该显示 Claude 的字形。
 * - `kimi` 在 `moonshot` 前:两个字形都有,各显示各的。
 * - `doubao` 在 `volcengine` 前:火山引擎上的豆包模型该显示豆包。
 * - `zai` 在 `zhipu` 前:Z.AI 是智谱的国际品牌,**有自己的字形**,而它的名字里
 *   带着「智谱」二字,不排前面就会显示成智谱的 logo。
 * - `bailian` 在 `qwen` 前:「阿里百炼 / 通义」这张卡片说的是**平台**,
 *   不是模型家族;`qwen-max` 这类**模型名**里没有「百炼」,照旧落到 qwen,
 *   两条轴各自成立。
 * - ★ `ollama` 在 `meta` 前:**「Ollama」里含着「llama」** —— 松散的
 *   `/llama/` 会把 Ollama 匹配成 Meta。这里不改用 `\bllama` 收紧,
 *   是因为那样又会漏掉 `codellama`;靠顺序解决,两边都对。
 *
 * ★ **`null` 是合法的规则目标**,意思是「这个名字**不是**任何一个我们有字形的
 * 牌子」—— 它存在的唯一作用是**挡住后面更松的规则**。目前只有 llama.cpp 用它:
 * 那是 ggerganov 的独立项目,不是 Meta 的东西,而 `/llama/` 会给它挂上 Meta 的
 * ∞。挡掉之后它退回首字母,那是诚实的;挂个 Meta 是个看着挺合理的错。
 *
 * 用正则不用 `includes`,是因为短 token 会误伤 —— `o1`/`o3` 这种两字符的
 * 模型名直接子串匹配会在别的名字中间命中,所以给它们加了 `\b` 边界。
 *
 * 中文也要匹配:设置页里供应商名是用户自己填的,写「深度求索」的人不比
 * 写 `deepseek` 的少。
 */
const RULES: readonly (readonly [RegExp, Brand | null])[] = [
  [/claude/i, 'claude'],
  [/anthropic/i, 'anthropic'],
  [/\b(gpt|o[134])\b|openai|chatgpt/i, 'openai'],
  [/deepseek|深度求索/i, 'deepseek'],
  [/deepinfra/i, 'deepinfra'],
  // ★ 必须在 zhipu 之前:「Z.AI(智谱国际)」里含着「智谱」
  [/z\.ai|智谱国际/i, 'zai'],
  [/glm|zhipu|chatglm|智谱/i, 'zhipu'],
  // ★ 必须在 qwen 之前:「阿里百炼 / 通义」是平台卡片,不是模型
  [/百炼|bailian|dashscope/i, 'bailian'],
  [/qwen|qwq|tongyi|通义|千问/i, 'qwen'],
  [/千帆|qianfan|文心|wenxin|百度/i, 'baiducloud'],
  [/kimi/i, 'kimi'],
  [/moonshot|月之暗面/i, 'moonshot'],
  [/gemini|palm|google|谷歌/i, 'gemini'],
  [/grok|\bxai\b/i, 'xai'],
  [/cohere|command-[ar]/i, 'cohere'],
  // ★ 必须在 meta 之前:'Ollama' 里含着 'llama'
  [/ollama/i, 'ollama'],
  // ★ 同上,而且是条 null 规则:llama.cpp 不是 Meta 的项目(理由见上面的段落)
  [/llama[.\s_-]?cpp/i, null],
  [/llama|\bmeta\b/i, 'meta'],
  [/mistral|codestral|mixtral/i, 'mistral'],
  [/minimax|abab/i, 'minimax'],
  [/doubao|豆包/i, 'doubao'],
  [/volc|火山/i, 'volcengine'],
  [/hunyuan|混元/i, 'hunyuan'],
  // 小米 MiMo。`\bmimo` 只挡左边界:模型 id(`xiaomi/mimo-v2.6-pro`)和显示名
  // (「MiMo V2.6 Pro」)里 mimo 后面跟的都是 `-`/空格,而不设右边界是为了兜住
  // 用户手打的 `mimo7b` 这类连写。排位不是顺序需要 —— 上面没有一条会命中它。
  [/xiaomi|\bmimo|小米/i, 'xiaomimimo'],
  [/spark|讯飞|星火/i, 'spark'],
  // StepFun 的模型就叫 step-1 / step-2,光匹配 'stepfun' 会全漏
  [/\bstep-|stepfun|阶跃/i, 'stepfun'],
  [/baichuan|百川/i, 'baichuan'],
  [/sensenova|商汤|日日新/i, 'sensenova'],
  [/\byi-|零一万物/i, 'yi'],
  [/openrouter/i, 'openrouter'],
  [/silicon|硅基流动/i, 'siliconcloud'],
  [/lm.?studio/i, 'lmstudio'],
  [/\bvllm\b/i, 'vllm'],
  // Jan 是 Menlo Research 的产品,lobehub 的字形也挂在 menlo 名下
  [/\bjan\b/i, 'menlo'],
  [/opencode/i, 'opencode'],
  // 内置上游(`BUILTIN_PROVIDER_ID`),字形不在 lobehub 里,是我们自己的一张 webp。
  // `^routin(-plan)?$` 是为了让**预设 id** 也能命中 ——`ProviderAvatar` 传的是
  // `[名字, id]`,用户把它改名之后就只剩 id 认得出来了。`routin-plan` 是订阅制那条
  // (`/plan/v1`,Codex 系模型),和按量那条**共用同一个 logo** —— 它们是同一家。
  // 收尾的 `ai\b` 是为了躲开「routing」:那个词里 routin 后面接的是 g,配不上 `[\s_-]*ai`。
  // ★ 排在这里不是顺序需要 —— 上面没有一条规则会命中 RoutinAI。它在聚合商这一簇里,
  // 因为它就是一家聚合商。
  [/\broutin[\s_-]*ai\b|^routin(-plan)?$/i, 'routin'],
  [/aihubmix/i, 'aihubmix'],
  [/atlas[\s_-]*cloud/i, 'atlascloud'],
  [/\b302\.?ai\b/i, 'ai302'],
  [/groq/i, 'groq'],
  [/together/i, 'together'],
  [/fireworks/i, 'fireworks'],
  [/perplexity|sonar/i, 'perplexity'],
  [/bedrock/i, 'bedrock'],
  [/vertex/i, 'vertexai'],
  [/azure/i, 'azure']
]

/**
 * 依次试每个候选名,**先给最具体的**:模型别名比供应商名更可能带牌子。
 *
 * ★ 这条不是风格偏好,RoutinAI 就是活例子:它是聚合商,自己有 logo(内置上游),
 * 而它下面的 alias 是 `claude-fable-5-1`。正文流里(Composer / Thread)传
 * `[模型名, 供应商名]`,于是显示 Claude 的字形 —— 用户在那里关心的是**这条消息
 * 是哪个模型答的**;供应商列表里(`ProviderAvatar`)传 `[供应商名, id]`,
 * 于是显示 RoutinAI 自己的 logo。同一个函数,两处顺序不同,各自都对。
 *
 * ★ 命中一条 `null` 规则时**跳到下一个候选**,而不是直接返回 null ——
 * 那条规则说的是「**这个名字**不是牌子」,不是「别再找了」。Composer 传进来的
 * 是 `[模型名, 供应商名]`,模型名被挡掉之后供应商名仍然值得一试。
 *
 * 猜不中返回 null —— 这是正常路径,不是错误。用户可以配任何上游,
 * 我们不可能认得全,认不出就退回首字母(见 `ProviderIcon` 的 `fallback`)。
 */
export function resolveBrand(...names: readonly (string | undefined)[]): Brand | null {
  for (const name of names) {
    if (name === undefined || name === '') continue
    const cleaned = stripProtocolNote(name)
    for (const [pattern, brand] of RULES) {
      if (!pattern.test(cleaned)) continue
      if (brand === null) break
      return brand
    }
  }
  return null
}
