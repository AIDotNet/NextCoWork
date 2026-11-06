/**
 * 内置定价种子表 —— 方案 §4.6。**纯数据,规则在 `pricing.ts`。**
 *
 * ★★ **这张表就是一份带日期的快照,不是真理。** 每一行都带 `source` 与 `fetchedAt`,
 * 界面上逐行显示,顶部固定一句「录入于 YYYY-MM,以供应商最终结算为准」——
 * 那不是免责套话,是这个设计的一部分:**价格会过期,但不会静默错。**
 *
 * 数据来自两份子代理调研报告(`~/.claude/plans/` 下,和方案同前缀):
 * `…-agent-a9c3ffd522bfa929f.md`(国际,1214 行,四方比对)与
 * `…-agent-a80a2d0ffcb64cbf5.md`(国内,DeepSeek 官方中英双页直读)。
 *
 * ─────────────────────────────────────────────────────────────
 * ★ 三条录入纪律,以及它们各自砍掉了什么
 * ─────────────────────────────────────────────────────────────
 *
 * **1. 绝不编造数字。拿不准的宁可不收。**
 * 一个错的价格会让费用统计**静默失真**,而用户不会发现;一个缺失的价格显示成「—」,
 * 而且会出现在定价页顶部那张「用过但查不到定价」的表里 —— **两种错的可见性差着天。**
 * 所以这张表的取舍一律偏向「不收」。被砍掉的见文件末尾 `NOT_SEEDED`,
 * 每一条都写了为什么 —— 免得下一个人以为是漏了,顺手「补全」回来。
 *
 * **2. 两次搜索命中同一个数,不算交叉验证。**
 * 调研实证:摘要器会把「Gemini 3.7 Flash」和「3.1 Flash-Lite」的查询都返回
 * 3.8 Flash 那一段价格。得是两个**独立来源**(官方页 / 官方 PDF / LiteLLM / OpenRouter)。
 *
 * **3. 聚合源必须交叉校验,它们会错,而且错得看不出来。**
 * 光 OpenRouter 一家就抓到三个独立的错:Sonnet 4/4.5 挂着官方已取消的 >200K 加价档
 * (**凭空多收**)、`gpt-5.6-sol` 的价恰好是官方的一半(**少收一半**)、
 * Gemini 的缓存值是 Google 每小时存储费 ÷ 12(**量纲错,少收 12 倍**)。
 * 三个错方向还不一致,所以连「偏保守」都谈不上。
 *
 * ─────────────────────────────────────────────────────────────
 * ★★ 时段计费的录入方向:**基准价录空闲价,窗口录高峰 `multiplier: 2`**
 * ─────────────────────────────────────────────────────────────
 *
 * DeepSeek 官方的表述是反过来的 ——「空闲时段价格为高峰时段价格的一半」,
 * 照抄的话基准价 = 高峰价、窗口 = 高峰段 `multiplier: 1`,绕且反直觉。
 *
 * 两种录法**算出来完全一样**,选后者是因为**错的方向不一样**:
 * 万一时区或星期判断有 bug、窗口没命中,费用会落在**便宜**的那一侧 ——
 * 少算用户的钱,而不是多算。在一个「用户不会去核对」的数字上,
 * 这是唯一可接受的失败方向。
 *
 * 顺带这也符合「base 是常态」的直觉:高峰只有工作日 7 小时,
 * **其余时间(含整个周末)全是空闲价**,常态本来就是便宜那档。
 */
import type { Currency, ModelPricing, PriceTier, TokenRates } from './pricing'

/** 历史快照的默认采集日期。新增或复核的行通过 extra.fetchedAt 记录实际日期。 */
const FETCHED_AT = '2026-09-05'

/** 单档(绝大多数模型)。多于一档才是阶梯,见下面 OpenAI / Gemini / xAI */
const one = (rate: TokenRates): readonly PriceTier[] => [{ upToInputTokens: null, rate }]

/**
 * 两档长上下文。**末档必须无上界** —— 上界写成具体数字的话,
 * 超过它的请求会一档都命不中,`priceOf` 返回 null,费用列变成「—」。
 */
const two = (upTo: number, low: TokenRates, high: TokenRates): readonly PriceTier[] => [
  { upToInputTokens: upTo, rate: low },
  { upToInputTokens: null, rate: high },
]

/** 三档长上下文；仍然是整次请求命中一套价格，不做累进分段。 */
const three = (firstUpTo: number, low: TokenRates, secondUpTo: number, medium: TokenRates, high: TokenRates): readonly PriceTier[] => [
  { upToInputTokens: firstUpTo, rate: low },
  { upToInputTokens: secondUpTo, rate: medium },
  { upToInputTokens: null, rate: high },
]

/**
 * 按厂商造行。`providerId` 的取值直接用 `presets.ts` 里的预设 id ——
 * 两张表用同一套 id,`findPricing` 才能拿运行时的 `provider.id` 查到覆盖价。
 */
const maker =
  (providerId: string | null, currency: Currency, source: string) =>
  (modelId: string, displayName: string, tiers: readonly PriceTier[], extra: Partial<ModelPricing> = {}): ModelPricing => ({
    providerId,
    modelId,
    displayName,
    currency,
    modality: 'text',
    tiers,
    source,
    fetchedAt: FETCHED_AT,
    ...extra,
  })

/* ══════════════════════════ Anthropic ══════════════════════════ */

/**
 * 证据强度最高的一组:**官方费率卡 PDF**,首页注明 Prices Effective 2026-08-31,
 * 子代理本机下载(HTTP 200 / 97,819 bytes / 14 页)后逐行提取核对,
 * 再与独立拉取的 OpenRouter 逐值吻合。
 *
 * ★ **Anthropic 当前没有长上下文溢价档。** 对 14 页全文检索过:`CONTEXT WINDOW`
 * 一列只出现 `All` 与 `≤200K` 两种取值,**全文档没有任何一行 `>200K`**。
 * 所以那 15 个 SKU 全部是单档 —— 这与 OpenAI / Gemini / xAI 都不同。
 * (OpenRouter 至今还给 Sonnet 4/4.5 挂着 `min_prompt_tokens: 200000` 的加价档,
 * 那是陈旧数据,照它算会**凭空多收用户的钱**。以 PDF 为准。)
 * ★ 唯一例外是 2026-10-08 补录的 `claude-haiku-5-5`(不在那份 PDF 里):官方定价页
 * 按 prompt 长度分两档,**阈值 100K**(不是别家的 200K / 272K),超档后五个计费项
 * 一律 ×5 —— 见那一行的注释。
 *
 * ★★ **模型 ID 的可信度低于价格。** PDF 只给展示名("Claude Sonnet 4.6"),
 * 不给 API id。下面有七个 id 是有一手依据的(运行环境 / 用户直接给出 / 官方对比表):
 * `claude-fable-5-1` / `claude-opus-5` / `claude-sonnet-5` /
 * `claude-haiku-4-5-20251001` / `claude-opus-5-5` / `claude-sonnet-5-5` /
 * `claude-haiku-5-5` ——
 * 注意**版本号里的点写成连字符**,
 * 且 4.5 世代带日期后缀而新世代不带。其余九个按这条已证实的构词法推出来。
 *
 * 推错了的代价是**查不到定价**(费用列「—」,并进定价页顶部那张待补表),
 * **不是算错价** —— 而且同族价格本来就一样(Opus 4.5–4.8 全是 5/25)。
 * 所以这里推比不收好:两者的失败表现相同,推对了还能省用户一次手工录入。
 *
 * ★ `claude-opus-5-5` 是本表**继 MAI-Code 之后的第二次破例**(2026-09-23):
 * 费率是用户直接给的口径($4/$20,缓存命中 0.2、5m 写 5、1h 写 8),
 * 没有第二个独立来源 —— 违反文件头第 2 条,收它是因为缺价的可见性比错价更差,
 * 而且这行的价格由用户自己核。两处刻意的不一致:
 * ① `source` 指向官方定价页而不是上面那份 2026-08-31 费率卡 —— 我们没有在
 *    那份 PDF 里核到这个型号,挂它的链接等于伪造证据;这个 URL 是「下次去哪核」,
 *    不是「依据」。② `fetchedAt` 沿用全表快照日(结构测试钉死全表一致),
 *    真实录入日以本注释为准。要改这行必须带着官方价来。
 */
const anth = maker(null, 'USD', 'https://www-cdn.anthropic.com/files/4zrzovbb/website/9e03129acc36d31970777d336f969bb53dc84355.pdf')

/** 五列一次写全,顺序同费率卡:输入 / 输出 / 5m 写 / 1h 写 / 读 */
const claude = (input: number, output: number, w5m: number, w1h: number, read: number): TokenRates => ({
  input,
  output,
  cacheRead: read,
  cacheWrite: w5m,
  cacheWrite1h: w1h,
})

const ANTHROPIC: readonly ModelPricing[] = [
  // ★ 这两行的缓存读是 $0.25(0.025×输入),不是别人的 0.1× —— 见文件头
  anth('claude-mythos-5-1', 'Claude Mythos 5.1', one(claude(10, 50, 12.5, 20, 0.25))),
  anth('claude-fable-5-1', 'Claude Fable 5.1', one(claude(10, 50, 12.5, 20, 0.25))),
  /*
    ★ 第三个「缓存读 ≠ 0.1× 输入」的例外:这里是 **0.05×**(0.2 / 4)——
    比 0.1× 还低一半,而同族 Opus 5 是 0.5/5(0.1×)。数字是用户口径
    (2026-09-23,见段首注释),不是抄错;结构测试里钉了这个例外的因子。
    写/读的族内规律倒是照旧成立:5m 写 = 1.25×、1h 写 = 2×、输出 = 5×。
  */
  anth('claude-opus-5-5', 'Claude Opus 5.5', one(claude(4, 20, 5, 8, 0.2)), { source: 'https://www.anthropic.com/pricing' }),
  anth('claude-opus-5', 'Claude Opus 5', one(claude(5, 25, 6.25, 10, 0.5))),
  /*
    需求:2026-09-29 用户点名收录 Sonnet 5.5,费率随官方定价页截图一起给的
    (输入 2 / 输出 10 / 5m 写 2.5 / 1h 写 4 / 命中 0.2)。五个数与同族 Sonnet 5
    **逐值相同** —— 这里仍按各行存自己的绝对价,不和下面那行联动。
    ★ 单源但一手:截图是官方定价页,**不是**下面那份 2026-08-31 费率卡 PDF
    (我们没在那份 PDF 里核到这个型号)。所以 `source` 指向官方定价页 ——
    挂一份没核过的 PDF 等于伪造证据,那个 URL 是「下次去哪核」,不是「依据」
    (同 claude-opus-5-5 那条的两处刻意不一致)。
    截图里那句「Batch API 输入输出 5 折」不收:批处理在 NOT_SEEDED 有排除记录。
  */
  anth('claude-sonnet-5-5', 'Claude Sonnet 5.5', one(claude(2, 10, 2.5, 4, 0.2)), { source: 'https://www.anthropic.com/pricing' }),
  anth('claude-sonnet-5', 'Claude Sonnet 5', one(claude(2, 10, 2.5, 4, 0.2))),
  /*
    需求:2026-10-08 用户点名收录 Haiku 5.5。五个数取自官方定价页(同日直读,
    Models overview 对比表的「From $0.10 / $0.50」与之吻合)。
    ★ Anthropic 段**唯一的两档行**:prompt ≤100K 与 >100K 分价,阈值 100K 含在低档
    (`upToInputTokens` 是含的,官方原文 "up to 100,000 tokens")。高档五项一律 = 低档 ×5,
    族内倍率(5m 写 1.25×、1h 写 2×、读 0.1×、输出 5×)在两档里各自成立。
    `source` 指官方定价页而不是 2026-08-31 费率卡 PDF,理由同 Opus / Sonnet 5.5。
    批处理价不收:批处理在 NOT_SEEDED 有排除记录。
  */
  anth('claude-haiku-5-5', 'Claude Haiku 5.5', two(100_000, claude(0.1, 0.5, 0.125, 0.2, 0.01), claude(0.5, 2.5, 0.625, 1, 0.05)), { source: 'https://www.anthropic.com/pricing' }),
  // 上一代同价位,但缓存读贵 4 倍($1.00)—— 正是「存绝对价、不存倍率」的证据
  anth('claude-mythos-5', 'Claude Mythos 5', one(claude(10, 50, 12.5, 20, 1.0))),
  anth('claude-fable-5', 'Claude Fable 5', one(claude(10, 50, 12.5, 20, 1.0))),
  anth('claude-opus-4-8', 'Claude Opus 4.8', one(claude(5, 25, 6.25, 10, 0.5))),
  anth('claude-opus-4-7', 'Claude Opus 4.7', one(claude(5, 25, 6.25, 10, 0.5))),
  anth('claude-opus-4-6', 'Claude Opus 4.6', one(claude(5, 25, 6.25, 10, 0.5))),
  anth('claude-sonnet-4-6', 'Claude Sonnet 4.6', one(claude(3, 15, 3.75, 6, 0.3))),
  anth('claude-opus-4-5', 'Claude Opus 4.5', one(claude(5, 25, 6.25, 10, 0.5))),
  anth('claude-sonnet-4-5', 'Claude Sonnet 4.5', one(claude(3, 15, 3.75, 6, 0.3))),
  anth('claude-haiku-4-5-20251001', 'Claude Haiku 4.5', one(claude(1, 5, 1.25, 2, 0.1))),
]

/* ══════════════════════════ OpenAI ══════════════════════════ */

/**
 * ★ **长上下文阈值是 272K,而且倍率不对称:输入 / 缓存读 / 缓存写 ×2,输出 ×1.5。**
 * 这条被 LiteLLM 与 OpenRouter 两份原始 JSON 逐值验证一致。
 * 「一个模型一个折扣系数」的设计在这里就崩了 —— 所以 `PriceTier.rate` 是一整套费率。
 *
 * ★ **GPT-5.6 世代开始收缓存写入费(1.25× 输入)。** 老型号(4.1 / 4o / o 系列)
 * 仍是「缓存读打折、写入不收费」的经典模式 —— 那些行**不写 `cacheWrite`**,
 * 而不是填 0(填 0 会被算成「写入免费」,是个具体的错)。
 *
 * 只给现代六款(astra、5.6 的 sol / terra / luna、6 世代的 sol / luna)配 272K 双档:
 * 前四款的缓存写入价被双源确认过,是报告里明确归入新计费结构的那一批;
 * `gpt-6-sol` / `gpt-6-luna`(2026-09-23)来自**官方 pricing 页本身**(用户截图),
 * 是单源但一手 —— 官方页正是这条纪律要的更硬的那个来源,而且两行的越档倍率
 * (输入/缓存 ×2、输出 ×1.5)与族内比值(0.1× / 1.25× / 5×)全中,
 * 由 `pricing-seed.test.ts` 的结构断言复核。
 * 老型号没有逐款确认过是否适用 272K 档,按单档收 —— 猜错的方向是**少收**,
 * 符合文件头第 1 条。★ 截图里那句「区域处理端点 +10%」不收:区域倍率在
 * `NOT_SEEDED` 里有明确排除记录(§4.5:账户级设置,应用无从得知)。
 */
const oai = maker(null, 'USD', 'https://openai.com/api/pricing')

const OPENAI: readonly ModelPricing[] = [
  oai('gpt-6-astra', 'GPT-6 Astra', two(272_000, { input: 10, output: 50, cacheRead: 1.0, cacheWrite: 12.5 }, { input: 20, output: 75, cacheRead: 2.0, cacheWrite: 25 })),
  /*
    ★ 2026-09-29 收录,费率核自官方 pricing 页 + 官方 model 页(同日两页互证):
    低档 输入 / 缓存读 / 缓存写 / 输出 = 2 / 0.1 / 2.5 / 10,高档 4 / 0.2 / 5 / 15。
    ★ 缓存读是 **0.05× 输入**(官方页原话:cached input 按 uncached 输入价的 5% 计),
    不是族内 gpt-6-sol 的 0.1× —— 按 0.1×「对齐」会凭空多收一倍,别顺手统一;
    越档倍率(输入/缓存 ×2、输出 ×1.5)照旧成立,由 `pricing-seed.test.ts` 复核。
    fetchedAt 沿用全表快照日(结构测试钉死全表一致),真实录入日以本注释为准。
  */
  oai('gpt-6.1-sol', 'GPT-6.1 Sol', two(272_000, { input: 2, output: 10, cacheRead: 0.1, cacheWrite: 2.5 }, { input: 4, output: 15, cacheRead: 0.2, cacheWrite: 5 })),
  /*
    ★ 2026-09-23 收录,费率逐格抄自官方 pricing 页的截图(用户提供的那张):
    低档 输入 / 缓存读 / 缓存写 / 输出 = 2 / 0.2 / 2.5 / 10,高档 4 / 0.4 / 5 / 15;
    Luna 是整行 1/20:0.1 / 0.01 / 0.125 / 0.5 与 0.2 / 0.02 / 0.25 / 0.75。
    ★ 两行都满足族内比值(缓存读 = 0.1× 输入、缓存写 = 1.25×、输出 = 5×)
    与越档倍率(输入/缓存 ×2、输出 ×1.5)—— 所以列序按
    [输入, 缓存读, 缓存写, 输出]×两档 解读是自洽的,敲错一列这些关系必挂。
    fetchedAt 沿用全表快照日(结构测试钉死全表一致),真实录入日以本注释为准。
  */
  oai('gpt-6-sol', 'GPT-6 Sol', two(272_000, { input: 2, output: 10, cacheRead: 0.2, cacheWrite: 2.5 }, { input: 4, output: 15, cacheRead: 0.4, cacheWrite: 5 })),
  oai('gpt-6-luna', 'GPT-6 Luna', two(272_000, { input: 0.1, output: 0.5, cacheRead: 0.01, cacheWrite: 0.125 }, { input: 0.2, output: 0.75, cacheRead: 0.02, cacheWrite: 0.25 })),
  /*
    ★ 收的是**促销价**($4/$20),不是业务页上那个 $5/$30 的标准价 ——
    收「实际被扣的那个」。促销据官方口径**至少**持续到 2026-11-21,
    是个下界不是上界,所以这里**不写 `effectiveUntil`**:
    写了就等于替 OpenAI 宣布 11-22 涨价,而那是我们编的。
    促销真的结束时这一行会偏低,靠 `fetchedAt` 和顶部那句提示兜住。
  */
  oai('gpt-5.6-sol', 'GPT-5.6 Sol(促销价,标准价 $5/$30)', two(272_000, { input: 4, output: 20, cacheRead: 0.4, cacheWrite: 5 }, { input: 8, output: 30, cacheRead: 0.8, cacheWrite: 10 }), { source: 'https://openai.com/api/pricing#promo-until-2026-11-21' }),
  oai('gpt-5.6-terra', 'GPT-5.6 Terra', two(272_000, { input: 2, output: 12, cacheRead: 0.2, cacheWrite: 2.5 }, { input: 4, output: 18, cacheRead: 0.4, cacheWrite: 5 })),
  oai('gpt-5.6-luna', 'GPT-5.6 Luna', two(272_000, { input: 0.2, output: 1.2, cacheRead: 0.02, cacheWrite: 0.25 }, { input: 0.4, output: 1.8, cacheRead: 0.04, cacheWrite: 0.5 })),
  oai('gpt-5.5', 'GPT-5.5', one({ input: 5, output: 30, cacheRead: 0.5 })),
  oai('gpt-5.5-pro', 'GPT-5.5 Pro', one({ input: 30, output: 180 })),
  oai('gpt-5.4', 'GPT-5.4', one({ input: 2.5, output: 15, cacheRead: 0.25 })),
  oai('gpt-5.4-mini', 'GPT-5.4 mini', one({ input: 0.75, output: 4.5, cacheRead: 0.075 })),
  oai('gpt-5.4-nano', 'GPT-5.4 nano', one({ input: 0.2, output: 1.25, cacheRead: 0.02 })),
  oai('gpt-5.4-pro', 'GPT-5.4 Pro', one({ input: 30, output: 180 })),
  oai('gpt-5.2', 'GPT-5.2', one({ input: 1.75, output: 14, cacheRead: 0.175 })),
  oai('gpt-5.2-pro', 'GPT-5.2 Pro', one({ input: 21, output: 168 })),
  oai('gpt-5.1', 'GPT-5.1', one({ input: 1.25, output: 10, cacheRead: 0.125 })),
  oai('gpt-5', 'GPT-5', one({ input: 1.25, output: 10, cacheRead: 0.125 })),
  oai('gpt-5-mini', 'GPT-5 mini', one({ input: 0.25, output: 2, cacheRead: 0.025 })),
  oai('gpt-5-nano', 'GPT-5 nano', one({ input: 0.05, output: 0.4, cacheRead: 0.005 })),
  oai('gpt-5-pro', 'GPT-5 Pro', one({ input: 15, output: 120 })),
  oai('gpt-4.1', 'GPT-4.1', one({ input: 2, output: 8, cacheRead: 0.5 })),
  oai('gpt-4.1-mini', 'GPT-4.1 mini', one({ input: 0.4, output: 1.6, cacheRead: 0.1 })),
  oai('gpt-4.1-nano', 'GPT-4.1 nano', one({ input: 0.1, output: 0.4, cacheRead: 0.025 })),
  oai('gpt-4o', 'GPT-4o', one({ input: 2.5, output: 10, cacheRead: 1.25 })),
  oai('gpt-4o-mini', 'GPT-4o mini', one({ input: 0.15, output: 0.6, cacheRead: 0.075 })),
  oai('o1', 'o1', one({ input: 15, output: 60, cacheRead: 7.5 })),
  oai('o1-pro', 'o1-pro', one({ input: 150, output: 600 })),
  oai('o3', 'o3', one({ input: 2, output: 8, cacheRead: 0.5 })),
  oai('o3-pro', 'o3-pro', one({ input: 20, output: 80 })),
  oai('o3-mini', 'o3-mini', one({ input: 1.1, output: 4.4, cacheRead: 0.55 })),
  oai('o4-mini', 'o4-mini', one({ input: 1.1, output: 4.4, cacheRead: 0.275 })),
  oai('gpt-4-turbo-2024-04-09', 'GPT-4 Turbo', one({ input: 10, output: 30 })),
  oai('gpt-4-0613', 'GPT-4', one({ input: 30, output: 60 })),
  oai('gpt-3.5-turbo', 'GPT-3.5 Turbo', one({ input: 0.5, output: 1.5 })),
]

/* ══════════════════════════ Google Gemini ══════════════════════════ */

/**
 * ★ 下列 Standard Paid Tier 全部从 Google 官方定价页逐型号直接核实。
 * Gemini 3.8 / 3.7 / 3.6 Flash 按公告中的 2026-12-31 / 2027-01-01
 * 分界保存为互不重叠的生效区间。
 *
 * ★ 阈值是 **200K,不是 128K** —— 128K 是 Gemini 1.5 时代的,现行世代统一 200K。
 * 倍率:输入 ×2、缓存读 ×2、**输出 ×1.5**(又一处不对称)。
 * **只有 Pro 系列按输入长度分档,Flash 全系不按长度分档。**
 * 3.1 Flash-Lite、3 Flash Preview、2.5 Flash / Flash-Lite 的音频输入
 * 另有更高费率；当前文本请求链路使用这里保存的 text/image/video 费率，
 * 音频差价在 `NOT_SEEDED` 留有显式记录，不能误当成同价。
 *
 * ★ 缓存写入**故意不写**:Google 是按「每百万 token 每小时」收**存储费**,
 * 那是一条量纲不同的计费线(费用 ∝ token × 持有小时数),塞进 `cacheWrite`
 * 只在「恰好持有 5 分钟」时才对,持有 1 小时会低估 12 倍。方案 §4.5 明确不建模它。
 */
const gem = maker(null, 'USD', 'https://ai.google.dev/gemini-api/docs/pricing')

const GEMINI: readonly ModelPricing[] = [
  gem('gemini-4-argon', 'Gemini 4 Argon', one({ input: 2, output: 10, cacheRead: 0.1 }), {
    effectiveUntil: '2026-11-30',
    source: 'https://ai.google.dev/gemini-api/docs/pricing#gemini-4-argon',
  }),
  gem('gemini-4-argon', 'Gemini 4 Argon', one({ input: 4, output: 20, cacheRead: 0.2 }), {
    effectiveFrom: '2026-12-01',
    source: 'https://ai.google.dev/gemini-api/docs/pricing#gemini-4-argon',
  }),
  gem('gemini-3.8-flash', 'Gemini 3.8 Flash', one({ input: 0.75, output: 3.75, cacheRead: 0.075 }), {
    effectiveUntil: '2026-12-31',
    source: 'https://ai.google.dev/gemini-api/docs/pricing#gemini-3.8-flash',
  }),
  gem('gemini-3.8-flash', 'Gemini 3.8 Flash', one({ input: 1.5, output: 7.5, cacheRead: 0.15 }), {
    effectiveFrom: '2027-01-01',
    source: 'https://ai.google.dev/gemini-api/docs/pricing#gemini-3.8-flash',
  }),
  gem('gemini-3.7-flash', 'Gemini 3.7 Flash', one({ input: 0.75, output: 3.75, cacheRead: 0.075 }), {
    effectiveUntil: '2026-12-31',
    source: 'https://ai.google.dev/gemini-api/docs/pricing#gemini-3.7-flash',
  }),
  gem('gemini-3.7-flash', 'Gemini 3.7 Flash', one({ input: 1.5, output: 7.5, cacheRead: 0.15 }), {
    effectiveFrom: '2027-01-01',
    source: 'https://ai.google.dev/gemini-api/docs/pricing#gemini-3.7-flash',
  }),
  gem('gemini-3.6-flash', 'Gemini 3.6 Flash', one({ input: 0.75, output: 3.75, cacheRead: 0.075 }), {
    effectiveUntil: '2026-12-31',
    source: 'https://ai.google.dev/gemini-api/docs/pricing#gemini-3.6-flash',
  }),
  gem('gemini-3.6-flash', 'Gemini 3.6 Flash', one({ input: 1.5, output: 7.5, cacheRead: 0.15 }), {
    effectiveFrom: '2027-01-01',
    source: 'https://ai.google.dev/gemini-api/docs/pricing#gemini-3.6-flash',
  }),
  gem('gemini-3.5-flash', 'Gemini 3.5 Flash', one({ input: 1.5, output: 9, cacheRead: 0.15 }), {
    source: 'https://ai.google.dev/gemini-api/docs/pricing#gemini-3.5-flash',
  }),
  gem('gemini-3.5-flash-lite', 'Gemini 3.5 Flash-Lite', one({ input: 0.3, output: 2.5, cacheRead: 0.03 }), {
    source: 'https://ai.google.dev/gemini-api/docs/pricing#gemini-3.5-flash-lite',
  }),
  gem('gemini-3.1-flash-lite', 'Gemini 3.1 Flash-Lite', one({ input: 0.25, output: 1.5, cacheRead: 0.025 }), {
    source: 'https://ai.google.dev/gemini-api/docs/pricing#gemini-3.1-flash-lite',
  }),
  gem('gemini-3-flash-preview', 'Gemini 3 Flash Preview', one({ input: 0.5, output: 3, cacheRead: 0.05 }), {
    source: 'https://ai.google.dev/gemini-api/docs/pricing#gemini-3-flash-preview',
  }),
  gem('gemini-2.5-pro', 'Gemini 2.5 Pro', two(200_000, { input: 1.25, output: 10, cacheRead: 0.125 }, { input: 2.5, output: 15, cacheRead: 0.25 }), { source: 'https://ai.google.dev/gemini-api/docs/pricing#gemini-2.5-pro' }),
  gem('gemini-2.5-flash', 'Gemini 2.5 Flash', one({ input: 0.3, output: 2.5, cacheRead: 0.03 }), {
    source: 'https://ai.google.dev/gemini-api/docs/pricing#gemini-2.5-flash',
  }),
  gem('gemini-2.5-flash-lite', 'Gemini 2.5 Flash-Lite', one({ input: 0.1, output: 0.4, cacheRead: 0.01 }), {
    source: 'https://ai.google.dev/gemini-api/docs/pricing#gemini-2.5-flash-lite',
  }),
  gem('gemini-3.1-pro-preview', 'Gemini 3.1 Pro', two(200_000, { input: 2, output: 12, cacheRead: 0.2 }, { input: 4, output: 18, cacheRead: 0.4 }), {
    source: 'https://ai.google.dev/gemini-api/docs/pricing#gemini-3.1-pro-preview',
  }),
]

/* ══════════════════════════ xAI Grok ══════════════════════════ */

/**
 * 规则最干净的一家:**统一阈值 200K,>200K 档所有计费项一律 ×2** ——
 * 唯一一家倍率在各计费项之间完全统一的厂商。LiteLLM 与 OpenRouter 逐值吻合。
 *
 * ★ xAI 的文档正是 `pricing.ts` 里「整单重定价」那条规则的出处,
 * 也点明了**缓存 token 计入阈值判定**:一个很大的缓存前缀会把请求推过 200K,
 * 导致整单涨价 —— 和「缓存应该省钱」的直觉正好相反。
 */
const xai = maker(null, 'USD', 'https://cdn.jsdelivr.net/gh/BerriAI/litellm@v1.99.0/model_prices_and_context_window.json')

// 需求：grok-4.7 定价按产品方确认「与 grok-4.6 完全一致」直接抄档，
// 不是本文件其余行要求的双源核对 —— 4.7 还没有独立的 LiteLLM/OpenRouter 条目。
const XAI: readonly ModelPricing[] = [xai('grok-4.3', 'Grok 4.3', two(200_000, { input: 1.25, output: 2.5, cacheRead: 0.2 }, { input: 2.5, output: 5, cacheRead: 0.4 })), xai('grok-4.5', 'Grok 4.5', two(200_000, { input: 2, output: 6, cacheRead: 0.3 }, { input: 4, output: 12, cacheRead: 0.6 })), xai('grok-4.6', 'Grok 4.6', two(200_000, { input: 2, output: 6, cacheRead: 0.5 }, { input: 4, output: 12, cacheRead: 1.0 })), xai('grok-4.7', 'Grok 4.7', two(200_000, { input: 2, output: 6, cacheRead: 0.5 }, { input: 4, output: 12, cacheRead: 1.0 }))]

/* ══════════════════════════ 智谱 / Z.AI(USD) ══════════════════════════ */

/**
 * ★ **只收美元价,作为厂商通用官方价。** 模型目录独立于用户是否已经配置
 * `zai` / `zhipu` 连接，所以不能把厂商价藏在某一条连接覆盖价下面。
 * 官方国际站页面明示 "All prices are in USD";而
 * `open.bigmodel.cn` 的人民币价走需要 `Authorization` 的接口,匿名一律
 * `1001 Header中未收到Authorization参数` —— **人民币价格完全未核实,一个数字都不收。**
 * 所以不为国内站预设 `zhipu` 猜人民币价格；运行时没有连接覆盖价时回退这里的
 * 官方 USD 基础价。
 *
 * ★ 官方缓存文档写「通常为标准价格的 50%」,但价目表实际是输入价的 ~19%
 * ($0.26 vs $1.4)—— **以表为准**,这也是不照公式生成的又一个例子。
 *
 * ★ 智谱确实有峰谷定价,但**只作用于 GLM Coding Plan 订阅**(按积分计),
 * 与 API 现付无关,**不要混进 `windows`**。
 */
const glm = maker(null, 'USD', 'https://docs.z.ai/guides/overview/pricing.md')

const ZHIPU: readonly ModelPricing[] = [
  glm('glm-5.3', 'GLM-5.3', one({ input: 1.4, output: 4.4, cacheRead: 0.26 })),
  glm('glm-5.2', 'GLM-5.2', one({ input: 1.4, output: 4.4, cacheRead: 0.26 })),
  glm('glm-5.1', 'GLM-5.1', one({ input: 1.4, output: 4.4, cacheRead: 0.26 })),
  glm('glm-5', 'GLM-5', one({ input: 1, output: 3.2, cacheRead: 0.2 })),
  glm('glm-4.7', 'GLM-4.7', one({ input: 0.6, output: 2.2, cacheRead: 0.11 })),
  glm('glm-4.7-flashx', 'GLM-4.7-FlashX', one({ input: 0.07, output: 0.4, cacheRead: 0.01 })),
  glm('glm-4.6', 'GLM-4.6', one({ input: 0.6, output: 2.2, cacheRead: 0.11 })),
  glm('glm-4.5', 'GLM-4.5', one({ input: 0.6, output: 2.2, cacheRead: 0.11 })),
  glm('glm-4.5-x', 'GLM-4.5-X', one({ input: 2.2, output: 8.9, cacheRead: 0.45 })),
  glm('glm-4.5-air', 'GLM-4.5-Air', one({ input: 0.2, output: 1.1, cacheRead: 0.03 })),
  glm('glm-4.5-airx', 'GLM-4.5-AirX', one({ input: 1.1, output: 4.5, cacheRead: 0.22 })),
  glm('glm-4-32b-0414-128k', 'GLM-4 32B 0414 128K', one({ input: 0.1, output: 0.1 })),
  glm('glm-4.6v', 'GLM-4.6V', one({ input: 0.3, output: 0.9, cacheRead: 0.05 })),
  glm('glm-ocr', 'GLM-OCR', one({ input: 0.03, output: 0.03 })),
  glm('glm-4.6v-flashx', 'GLM-4.6V-FlashX', one({ input: 0.04, output: 0.4, cacheRead: 0.004 })),
  glm('glm-4.5v', 'GLM-4.5V', one({ input: 0.6, output: 1.8, cacheRead: 0.11 })),

  /*
    ★★ **全表唯一一处用上 `effectiveFrom` / `effectiveUntil` 的地方,而且它是有据可查的**:
    官方页写明 GLM-5.3-Flash 的促销价于 **2026-09-09 24:00(UTC+8)** 结束,并同时印出原价。
    两行、日期区间不重叠 —— 到点自动切换,而不是等用户某天发现统计全错。

    Gemini 3.8 Flash 的同类调价公告现已从 Google 官方定价页直接核实,
    因而也以两行日期区间收录。

    ⚠️ 日期是**按 UTC+8 的自然日**记的,而 `effectiveAt` 比较的是 UTC 日期串。
    9 月 9 日 24:00(UTC+8)= 9 月 9 日 16:00 UTC,落在 UTC 的 9 月 9 日内,
    所以按 UTC 日期切在 09-09 / 09-10 之间只差最后那 8 小时 —— 那 8 小时会**按原价算**,
    偏贵。这是本表唯一一处方向偏贵的近似,记在这里免得被当成 bug 查。
  */
  glm('glm-5.3-flash', 'GLM-5.3-Flash(促销价)', one({ input: 0.075, output: 0.25, cacheRead: 0.015 }), {
    effectiveUntil: '2026-09-09',
  }),
  glm('glm-5.3-flash', 'GLM-5.3-Flash', one({ input: 0.15, output: 0.5, cacheRead: 0.03 }), {
    effectiveFrom: '2026-09-10',
  }),
]

/* ══════════════════════════ DeepSeek(时段计费) ══════════════════════════ */

/**
 * ★ **全表唯一用上 `windows` 的一组。** 官方中英双页直读、互相印证,
 * 再与 OpenRouter 的逐窗口 `overrides` 吻合 —— 证据强度和 Anthropic 并列最高。
 *
 * 官方原文(抄在这里,因为下一个人也会想当然):
 *
 * > 英文:*"Off-peak rates are half of the peak rates. Peak hours are
 * > **01:00 - 04:00 and 06:00 - 10:00 UTC, Monday through Friday**
 * > (all other hours are off-peak)."*
 * >
 * > 中文:「空闲时段价格为高峰时段价格的一半。高峰时段为**北京时间周一至周五
 * > 9:00 - 12:00、14:00 - 18:00**(其余为空闲时段)。」
 *
 * 两版换算后自洽(北京 = UTC+8)。**流传甚广的「16:30–00:30 UTC 错峰优惠」是错的** ——
 * 那来自第三方站点,且「错峰优惠」这个方案本身已于 2026-08-16 被「高峰/空闲」取代,
 * 当前官方页上「错峰」二字出现 0 次。
 *
 * ★★ **必须有星期维度。** 高峰只在**周一至周五**,周末整个是空闲价。
 * 少了 `daysOfWeek`,「周六凌晨 2 点」会被算成高峰 —— 贵一倍,而且没人会发现。
 *
 * ★ 折扣**同时作用于输入、输出、缓存命中三项,统一 5 折**(用实际报价表反算验证过是
 * 精确 2 倍关系),所以一个 `multiplier: 2` 就够,不必写整套 `rates`。
 *
 * ★ **DeepSeek 没有缓存写入费** —— 价目表只有「命中 / 未命中」两档输入价。
 * 所以 `cacheWrite` **缺省不写**,不是填 0。
 *
 * 币种:官方英文价目表直接提供 USD,因此这里保存为**通用美元价**。这也让尚未配置
 * DeepSeek 连接的模型目录能够展示官方价格。中文站另有独立人民币报价,但预设连接
 * 并不携带账户结算币种,不能把人民币价作为 `deepseek` 覆盖价强加给所有账户。
 *
 * 官方当前页未声明这些价格的 `effectiveFrom` / `effectiveUntil`,所以只用 `fetchedAt`
 * 标记快照日期,不根据模型版本号猜一个生效日。
 */
const PEAK_WINDOWS = [
  {
    timezone: 'UTC',
    daysOfWeek: [1, 2, 3, 4, 5],
    start: '01:00',
    end: '04:00',
    multiplier: 2,
    label: '高峰时段(北京时间 09:00–12:00)',
  },
  {
    timezone: 'UTC',
    daysOfWeek: [1, 2, 3, 4, 5],
    start: '06:00',
    end: '10:00',
    multiplier: 2,
    label: '高峰时段(北京时间 14:00–18:00)',
  },
] as const

const ds = maker(null, 'USD', 'https://api-docs.deepseek.com/quick_start/pricing/')

/** 基准 = **空闲价**(见文件头那段论证) */
const DEEPSEEK: readonly ModelPricing[] = [
  ds('deepseek-v4.1-flash-1017', 'DeepSeek V4.1 Flash 1017', one({ input: 0.15, output: 0.6, cacheRead: 0.003 }), {
    windows: PEAK_WINDOWS,
    fetchedAt: '2026-10-19',
  }),
  ds('deepseek-v4-flash', 'DeepSeek V4 Flash', one({ input: 0.22, output: 0.66, cacheRead: 0.007 }), {
    windows: PEAK_WINDOWS,
  }),
  ds('deepseek-v4.1-flash-ultrafast', 'DeepSeek V4.1 Flash Ultrafast', one({ input: 0.3, output: 1.2, cacheRead: 0.006 }), {
    source: 'https://nextco.work/models/deepseek-v4.1-flash-ultrafast',
  }),
  ds('deepseek-v4-pro', 'DeepSeek V4 Pro', one({ input: 0.66, output: 1.98, cacheRead: 0.022 }), {
    windows: PEAK_WINDOWS,
  }),
  ds('deepseek-v4-flash-vision-exp', 'DeepSeek V4 Flash Vision(实验)', one({ input: 0.22, output: 0.66, cacheRead: 0.007 }), { windows: PEAK_WINDOWS }),
]

/* ══════════════════════════ Moonshot / Kimi ══════════════════════════ */

/**
 * 官方中英双站直读。**上下文分档已彻底取消** —— 旧的 `moonshot-v1-8k/32k/128k`
 * 三个 SKU 不再存在,现在是每模型单一价 + 缓存命中/未命中输入价二分,
 * 结构和 DeepSeek 相同(但没有时段计费)。缓存自动、无写入费、无存储费。
 *
 * ★ 中美两站各录各的：国际站 USD 作为模型目录稳定展示的通用官方价，国内站 CNY
 * 作为 `moonshot` 连接覆盖价。又一个「不是换算关系」的实证:
 * k3 的中美比值恒为 6.667,而 k2.6 是 6.875 / 6.84 / 6.75。
 */
const kimiCny = maker('moonshot', 'CNY', 'https://platform.moonshot.cn/docs/pricing')
const kimiUsd = maker(null, 'USD', 'https://platform.moonshot.ai/docs/pricing')

const MOONSHOT: readonly ModelPricing[] = [
  kimiCny('kimi-k3', 'Kimi K3', one({ input: 20, output: 100, cacheRead: 2.0 })),
  kimiCny('kimi-k2.6', 'Kimi K2.6', one({ input: 6.5, output: 27, cacheRead: 1.1 })),
  kimiCny('kimi-k2.7-code', 'Kimi K2.7 Code', one({ input: 6.5, output: 27, cacheRead: 1.3 })),
  // `-highspeed` 是**同一个模型**卖吞吐,整体 2 倍价
  kimiCny('kimi-k2.7-code-highspeed', 'Kimi K2.7 Code 高速版', one({ input: 13, output: 54, cacheRead: 2.6 })),

  kimiUsd('kimi-k3', 'Kimi K3', one({ input: 3.0, output: 15, cacheRead: 0.3 })),
  kimiUsd('kimi-k2.6', 'Kimi K2.6', one({ input: 0.95, output: 4.0, cacheRead: 0.16 })),
  kimiUsd('kimi-k2.7-code', 'Kimi K2.7 Code', one({ input: 0.95, output: 4.0, cacheRead: 0.19 })),
  kimiUsd('kimi-k2.7-code-highspeed', 'Kimi K2.7 Code 高速版', one({ input: 1.9, output: 8.0, cacheRead: 0.38 })),
]

/* ══════════════════════════ 阿里百炼 Qwen ══════════════════════════ */

/**
 * 国际站官方页明确写明 K = 1,000、M = 1,000,000，并明确说明命中档位后整次请求
 * 全量按该档计费，因此可以无损录入 Qwen3 Max / Plus / Flash / Coder 的阶梯。
 * 国际站 Standard USD 作为模型目录的通用官方价；百炼国内站已有的 CNY 数字继续
 * 作为 `dashscope` 连接覆盖价保留。
 *
 * `qwen-plus` / `qwen-turbo` 的输出价仍按思考/非思考模式分两列，而 `TokenRates`
 * 暂时没有这一维，所以这两款继续不录通用价，避免默认选一列造成静默错账。
 *
 * ★ 缓存也不收:Qwen 的缓存价是**以输入价的百分比**表达的(显式创建 ~125%、
 * 命中 ~10%;隐式创建 100%、命中 ~20%),而我们存绝对价。两套缓存机制取哪套
 * 取决于用户怎么调,应用无从得知。
 */
const qwenUsd = maker(null, 'USD', 'https://www.alibabacloud.com/help/en/model-studio/model-pricing')
const qwenCny = maker('dashscope', 'CNY', 'https://help.aliyun.com/zh/model-studio/model-pricing')

const QWEN: readonly ModelPricing[] = [
  qwenUsd('qwen3.8-max', 'Qwen3.8-Max', one({ input: 2, output: 6 })),
  qwenUsd('qwen3.8-flash', 'Qwen3.8-Flash', one({ input: 0.15, output: 0.47 })),
  qwenUsd('qwen3.7-max', 'Qwen3.7-Max', one({ input: 2.5, output: 7.5 })),
  qwenUsd('qwen3.7-plus', 'Qwen3.7-Plus', two(256_000, { input: 0.4, output: 1.6 }, { input: 1.2, output: 4.8 })),
  qwenUsd('qwen3.6-max-preview', 'Qwen3.6-Max Preview', two(128_000, { input: 1.3, output: 7.8 }, { input: 2, output: 12 })),
  qwenUsd('qwen3-max', 'Qwen3-Max', [
    { upToInputTokens: 32_000, rate: { input: 1.2, output: 6 } },
    { upToInputTokens: 128_000, rate: { input: 2.4, output: 12 } },
    { upToInputTokens: null, rate: { input: 3, output: 15 } },
  ]),
  qwenUsd('qwen3.6-plus', 'Qwen3.6-Plus', two(256_000, { input: 0.5, output: 3 }, { input: 2, output: 6 })),
  qwenUsd('qwen3.5-plus', 'Qwen3.5-Plus', two(256_000, { input: 0.4, output: 2.4 }, { input: 0.5, output: 3 })),
  qwenUsd('qwen3.7-flash', 'Qwen3.7-Flash', [
    { upToInputTokens: 32_000, rate: { input: 0.03, output: 0.13 } },
    { upToInputTokens: 256_000, rate: { input: 0.1, output: 0.4 } },
    { upToInputTokens: null, rate: { input: 0.2, output: 0.8 } },
  ]),
  qwenUsd('qwen3.6-flash', 'Qwen3.6-Flash', two(256_000, { input: 0.25, output: 1.5 }, { input: 1, output: 4 })),
  qwenUsd('qwen3.5-flash', 'Qwen3.5-Flash', one({ input: 0.1, output: 0.4 })),
  qwenUsd('qwen3-coder-next', 'Qwen3-Coder-Next', [
    { upToInputTokens: 32_000, rate: { input: 0.3, output: 1.5 } },
    { upToInputTokens: 128_000, rate: { input: 0.5, output: 2.5 } },
    { upToInputTokens: null, rate: { input: 0.8, output: 4 } },
  ]),
  qwenUsd('qwen3-coder-plus', 'Qwen3-Coder-Plus', [
    { upToInputTokens: 32_000, rate: { input: 1, output: 5 } },
    { upToInputTokens: 128_000, rate: { input: 1.8, output: 9 } },
    { upToInputTokens: 256_000, rate: { input: 3, output: 15 } },
    { upToInputTokens: null, rate: { input: 6, output: 60 } },
  ]),
  qwenUsd('qwen3-coder-flash', 'Qwen3-Coder-Flash', [
    { upToInputTokens: 32_000, rate: { input: 0.3, output: 1.5 } },
    { upToInputTokens: 128_000, rate: { input: 0.5, output: 2.5 } },
    { upToInputTokens: 256_000, rate: { input: 0.8, output: 4 } },
    { upToInputTokens: null, rate: { input: 1.6, output: 9.6 } },
  ]),
  qwenUsd('qwen3-coder-30b-a3b-instruct', 'Qwen3-Coder 30B-A3B Instruct', [
    { upToInputTokens: 32_000, rate: { input: 0.45, output: 2.25 } },
    { upToInputTokens: 128_000, rate: { input: 0.75, output: 3.75 } },
    { upToInputTokens: null, rate: { input: 1.2, output: 6 } },
  ]),
  qwenUsd('qwen-max', 'Qwen-Max', one({ input: 1.6, output: 6.4 })),

  qwenCny('qwen3.8-max', 'Qwen3.8-Max', one({ input: 12, output: 36 })),
  qwenCny('qwen-max', 'Qwen-Max', one({ input: 2.4, output: 9.6 })),
  qwenCny('qwen-long', 'Qwen-Long', one({ input: 0.5, output: 2 })),
]

/* ══════════════════════════ MiniMax ══════════════════════════ */

/** 国际站 Pay-as-you-go Standard USD；Priority 是请求级 service_tier，本轮不混入基础价。 */
const minimax = maker(null, 'USD', 'https://platform.minimax.io/docs/guides/pricing-paygo')

const MINIMAX: readonly ModelPricing[] = [
  minimax('MiniMax-M3', 'MiniMax M3', two(512_000, { input: 0.3, output: 1.2, cacheRead: 0.06 }, { input: 0.6, output: 2.4, cacheRead: 0.12 })),
  minimax('MiniMax-M2.7', 'MiniMax M2.7', one({ input: 0.3, output: 1.2, cacheRead: 0.06, cacheWrite: 0.375 })),
  minimax('MiniMax-M2.7-highspeed', 'MiniMax M2.7 Highspeed', one({ input: 0.6, output: 2.4, cacheRead: 0.06, cacheWrite: 0.375 })),
  minimax('MiniMax-M2.5', 'MiniMax M2.5', one({ input: 0.3, output: 1.2, cacheRead: 0.03, cacheWrite: 0.375 })),
  minimax('MiniMax-M2.5-highspeed', 'MiniMax M2.5 Highspeed', one({ input: 0.6, output: 2.4, cacheRead: 0.03, cacheWrite: 0.375 })),
  minimax('MiniMax-M2.1', 'MiniMax M2.1', one({ input: 0.3, output: 1.2, cacheRead: 0.03, cacheWrite: 0.375 })),
  minimax('MiniMax-M2.1-highspeed', 'MiniMax M2.1 Highspeed', one({ input: 0.6, output: 2.4, cacheRead: 0.03, cacheWrite: 0.375 })),
  minimax('MiniMax-M2', 'MiniMax M2', one({ input: 0.3, output: 1.2, cacheRead: 0.03, cacheWrite: 0.375 })),
]

/* ══════════════════════════ Xiaomi MiMo ══════════════════════════ */

/** 海外 Pay-as-you-go 官方 USD；国内 CNY 是独立区域价，不由汇率反推。 */
const mimo = maker(null, 'USD', 'https://mimo.mi.com/docs/en-US/pricing')

/*
  需求：V2.6-Pro/Flash 的 CNY 官网价与 V2.5-Pro/V2.5 逐档相同(¥0.025/3/6、¥0.02/1/2)，
  换成 USD 后也和 OpenRouter 挂的 `xiaomi/mimo-v2.6-*` 价格一致 —— 双源吻合，非编造。
  UltraSpeed 是 V2.6 才有的新档(¥0.25/30/60)，独立入表。
  ★ 不给这三行单独传 `fetchedAt`——`pricing-seed.test.ts` 的
  「fetchedAt 全表一致」断言钉死了这张表只能有一个采集日期(`FETCHED_AT` 常量)，
  这里沿用它，不是漏填；真要标「今天」核实过，得整表批量刷新那个常量，而那个常量
  同时是好几个按周判定的时段计价用例的锚点，不该为了新增三行模型顺带牵动。
*/
const MIMO: readonly ModelPricing[] = [
  mimo('mimo-v2.6-pro', 'MiMo V2.6 Pro', one({ input: 0.435, output: 0.87, cacheRead: 0.0036 })),
  mimo('mimo-v2.6-flash', 'MiMo V2.6 Flash', one({ input: 0.14, output: 0.28, cacheRead: 0.0028 })),
  mimo('mimo-v2.6-pro-ultraspeed', 'MiMo V2.6 Pro UltraSpeed', one({ input: 4.35, output: 8.7, cacheRead: 0.036 })),
  mimo('mimo-v2.5-pro', 'MiMo V2.5 Pro', one({ input: 0.435, output: 0.87, cacheRead: 0.0036 })),
  mimo('mimo-v2.5', 'MiMo V2.5', one({ input: 0.14, output: 0.28, cacheRead: 0.0028 })),
]

/* ══════════════════════════ 字节跳动豆包 / Doubao ══════════════════════════ */

/** 火山方舟常规在线推理官方 CNY；缓存存储按 Token×小时计费，未混入这里。 */
const doubao = maker(null, 'CNY', 'https://docs.volcengine.com/docs/82379/1544106')

/*
  需求:2.1 世代按方舟 2026-09-22 的「在线推理(常规)」表重新核对 ——
  Pro 由 ¥6/30 降到 ¥3/15(缓存命中 1.2 不变),新增 Lite ¥0.8/2.7(缓存 0.16)。
  Turbo 整档已从当前价格表消失,而它的目录行同时被官方移出推荐表,
  两件事对上,所以价目行与目录行一起退役(不是漏抄)。
  ★ 退役是**删行**:留一行查不到现价的旧价,等于给用户一个看着挺像样的错数字。
*/
const DOUBAO: readonly ModelPricing[] = [
  doubao('doubao-seed-evolving', '豆包 Seed Evolving', one({ input: 6, output: 30, cacheRead: 1.2 })),
  doubao('doubao-seed-2.1-pro', '豆包 Seed 2.1 Pro', one({ input: 3, output: 15, cacheRead: 1.2 })),
  doubao('doubao-seed-2.1-lite', '豆包 Seed 2.1 Lite', one({ input: 0.8, output: 2.7, cacheRead: 0.16 })),
  doubao('doubao-seed-2.0-pro', '豆包 Seed 2.0 Pro', three(32_000, { input: 3.2, output: 16, cacheRead: 0.64 }, 128_000, { input: 4.8, output: 24, cacheRead: 0.96 }, { input: 9.6, output: 48, cacheRead: 1.92 })),
  doubao('doubao-seed-2.0-lite', '豆包 Seed 2.0 Lite', three(32_000, { input: 0.6, output: 3.6, cacheRead: 0.12 }, 128_000, { input: 0.9, output: 5.4, cacheRead: 0.18 }, { input: 1.8, output: 10.8, cacheRead: 0.36 })),
  doubao('doubao-seed-2.0-mini', '豆包 Seed 2.0 Mini', three(32_000, { input: 0.2, output: 2, cacheRead: 0.04 }, 128_000, { input: 0.4, output: 4, cacheRead: 0.08 }, { input: 0.8, output: 8, cacheRead: 0.16 })),
  doubao('doubao-seed-2.0-code', '豆包 Seed 2.0 Code', three(32_000, { input: 3.2, output: 16, cacheRead: 0.64 }, 128_000, { input: 4.8, output: 24, cacheRead: 0.96 }, { input: 9.6, output: 48, cacheRead: 1.92 })),
  doubao('doubao-seed-character', '豆包 Seed Character', two(32_000, { input: 0.8, output: 2, cacheRead: 0.16 }, { input: 1.2, output: 6, cacheRead: 0.16 })),
  doubao('doubao-seed-code', '豆包 Seed Code', three(32_000, { input: 1.2, output: 8, cacheRead: 0.24 }, 128_000, { input: 1.4, output: 12, cacheRead: 0.24 }, { input: 2.8, output: 16, cacheRead: 0.24 })),
  doubao('doubao-seed-1.6-flash', '豆包 Seed 1.6 Flash', three(32_000, { input: 0.15, output: 1.5, cacheRead: 0.03 }, 128_000, { input: 0.3, output: 3, cacheRead: 0.03 }, { input: 0.6, output: 6, cacheRead: 0.03 })),
  doubao('doubao-seed-1.6-vision', '豆包 Seed 1.6 Vision', three(32_000, { input: 0.8, output: 8, cacheRead: 0.16 }, 128_000, { input: 1.2, output: 16, cacheRead: 0.16 }, { input: 2.4, output: 24, cacheRead: 0.16 })),
  doubao('doubao-seed-translation', '豆包 Seed Translation', one({ input: 1.2, output: 3.6 })),
  doubao('doubao-1.5-pro-32k', '豆包 1.5 Pro 32K', one({ input: 0.8, output: 2, cacheRead: 0.16 })),
  doubao('doubao-1.5-lite-32k', '豆包 1.5 Lite 32K', one({ input: 0.3, output: 0.6, cacheRead: 0.06 })),
  doubao('doubao-1.5-vision-pro', '豆包 1.5 Vision Pro', one({ input: 3, output: 9 })),
]

/* ══════════════════════════ 百度千帆 / ERNIE ══════════════════════════ */

/** 千帆预置服务常规在线推理官方 CNY；页面按千 Token 报价，统一换算为每百万。 */
const baidu = maker(null, 'CNY', 'https://cloud.baidu.com/doc/qianfan/s/wmh4sv6ya')

const BAIDU: readonly ModelPricing[] = [baidu('ernie-5.1', 'ERNIE 5.1', two(32_000, { input: 4, output: 18 }, { input: 6, output: 22 })), baidu('ernie-5.0', 'ERNIE 5.0', two(32_000, { input: 6, output: 24 }, { input: 10, output: 40 })), baidu('ernie-4.5-turbo', 'ERNIE 4.5 Turbo', one({ input: 0.8, output: 3.2, cacheRead: 0.2 })), baidu('ernie-4.5-turbo-vl', 'ERNIE 4.5 Turbo VL', one({ input: 3, output: 9, cacheRead: 0.75 })), baidu('ernie-x1.1-preview', 'ERNIE X1.1 Preview', one({ input: 1, output: 4 })), baidu('internvl3-38b', 'InternVL 3 38B', one({ input: 8, output: 24 }))]

/* ══════════════════════════ 腾讯混元 / Hunyuan ══════════════════════════ */

/** 腾讯云 TokenHub 当前语言模型常规在线推理官方 CNY。 */
const hunyuan = maker(null, 'CNY', 'https://cloud.tencent.com/document/product/1823/130055')

const HUNYUAN: readonly ModelPricing[] = [hunyuan('hy4-preview', 'HY 4 Preview', one({ input: 6, output: 18, cacheRead: 0.3 })), hunyuan('hy3', 'HY 3', one({ input: 1, output: 4, cacheRead: 0.25 })), hunyuan('hy-mt2-pro', 'HY-MT2 Pro', one({ input: 0.5, output: 2 })), hunyuan('hy-mt2-plus', 'HY-MT2 Plus', one({ input: 0.5, output: 2 })), hunyuan('hy-mt2-lite', 'HY-MT2 Lite', one({ input: 0.3, output: 1.2 })), hunyuan('hunyuan-role-latest', 'HY Role Latest', one({ input: 2.4, output: 9.6 })), hunyuan('hy-role', 'HY Role', one({ input: 2.4, output: 9.6 }))]

/* ══════════════════════════ StepFun 阶跃星辰 ══════════════════════════ */

/** 官方开放平台按量价；页面明确 1M = 1,000,000 tokens。 */
const stepfun = maker(null, 'CNY', 'https://platform.stepfun.com/docs/zh/guides/pricing/details.md')

const STEPFUN: readonly ModelPricing[] = [stepfun('step-3.7-flash', 'Step 3.7 Flash', one({ input: 1.35, output: 8.1, cacheRead: 0.27 })), stepfun('step-3.5-flash', 'Step 3.5 Flash', one({ input: 0.7, output: 2.1, cacheRead: 0.14 })), stepfun('step-3.5-flash-2603', 'Step 3.5 Flash 2603', one({ input: 0.7, output: 2.1, cacheRead: 0.14 })), stepfun('step-1o-turbo-vision', 'Step-1o Turbo Vision', one({ input: 2.5, output: 8, cacheRead: 0.5 }))]

/* ══════════════════════════ 百川智能 ══════════════════════════ */

/**
 * 官方页以每千 tokens 报价；这里统一乘以 1,000，保存为领域模型要求的每百万 Token。
 * 标注“包含输入和输出”的型号无法拆出不同单价，因而将同一官方费率分别保存到
 * input/output。搜索增强和医疗搜索是按次收费，不混入 Token 费率。
 */
const baichuan = maker(null, 'CNY', 'https://platform.baichuan-ai.com/prices')

const BAICHUAN: readonly ModelPricing[] = [baichuan('Baichuan4', 'Baichuan 4', one({ input: 100, output: 100 })), baichuan('Baichuan4-Turbo', 'Baichuan 4 Turbo', one({ input: 15, output: 15 })), baichuan('Baichuan4-Air', 'Baichuan 4 Air', one({ input: 0.98, output: 0.98 })), baichuan('Baichuan3-Turbo', 'Baichuan 3 Turbo', one({ input: 12, output: 12 })), baichuan('Baichuan3-Turbo-128k', 'Baichuan 3 Turbo 128K', one({ input: 24, output: 24 })), baichuan('Baichuan2-Turbo', 'Baichuan 2 Turbo', one({ input: 8, output: 8 })), baichuan('Baichuan-M3-Plus', 'Baichuan M3 Plus', one({ input: 5, output: 9 })), baichuan('Baichuan-M3', 'Baichuan M3', one({ input: 10, output: 30 })), baichuan('Baichuan-M2-Plus', 'Baichuan M2 Plus', one({ input: 10, output: 30 })), baichuan('Baichuan-M2', 'Baichuan M2', one({ input: 2, output: 20 }))]

/* ══════════════════════════ 商汤 SenseNova ══════════════════════════ */

/** 日日新主定价页当前 Tokens 按量后付费 CNY 价；未公布缓存价、阶梯或分时时段。 */
const sensenova = maker(null, 'CNY', 'https://www.sensecore.cn/help/docs/model-as-a-service/nova/pricing')

const SENSENOVA: readonly ModelPricing[] = [
  sensenova('SenseNova-V6-5-Pro', 'SenseNova V6.5 Pro', one({ input: 3, output: 9 })),
  sensenova('SenseNova-V6-5-Turbo', 'SenseNova V6.5 Turbo', one({ input: 1.5, output: 4.5 })),
  sensenova('SenseNova-V6-Pro', 'SenseNova V6 Pro', one({ input: 3, output: 9 })),
  sensenova('SenseNova-V6-Turbo', 'SenseNova V6 Turbo', one({ input: 1.5, output: 4.5 })),
  sensenova('SenseNova-V6-Reasoner', 'SenseNova V6 Reasoner', one({ input: 4, output: 16 })),
  sensenova('SenseChat-Vision', 'SenseChat Vision', one({ input: 10, output: 60 })),
  sensenova('SenseChat-Character-Pro', 'SenseChat Character Pro', one({ input: 15, output: 15 })),
  sensenova('SenseChat-Character', 'SenseChat Character', one({ input: 12, output: 12 })),
]

/* ══════════════════════════ 讯飞星火 / 星辰 MaaS ══════════════════════════ */

/**
 * 星辰 MaaS 当前公开的按量人民币价。X2 / X2 Flash 的 Token Plan 另按订阅积分
 * 结算，不能把积分费率或 0.8 波谷系数混进这里。X2.5 两款的 0 元是当前官方页
 * 明示价格；X2.5 4B 同时标注“限时免费”，但官方没有公布截止日期，所以不猜
 * `effectiveUntil`，只通过逐行来源与抓取日期说明它是价格快照。
 */
const spark = maker(null, 'CNY', 'https://maas.xfyun.cn/modelSquare')

const SPARK: readonly ModelPricing[] = [
  spark('spark-x2', 'Spark X2', one({ input: 3, output: 3 }), {
    source: 'https://maas.xfyun.cn/modelSquare/base/2610636408864769',
  }),
  spark('spark-x2-flash', 'Spark X2 Flash', one({ input: 1, output: 2 }), {
    source: 'https://maas.xfyun.cn/modelSquare/base/2611845619399681',
  }),
  spark('spark-x2.5-4b', 'Spark X2.5 4B（当前限时免费）', one({ input: 0, output: 0, cacheRead: 0 }), {
    source: 'https://maas.xfyun.cn/modelSquare/base/2624338172569611',
  }),
  spark('spark-x2.5-1.7b', 'Spark X2.5 1.7B（当前免费）', one({ input: 0, output: 0, cacheRead: 0 }), {
    source: 'https://maas.xfyun.cn/modelSquare/base/2624338063517705',
  }),
]

/* ══════════════════════════ 华为 openPangu ══════════════════════════ */

/** 华为云 MaaS 官方人民币按量价；Pro 的阶梯维度当前无法无损表达，见 NOT_SEEDED。 */
const pangu = maker(null, 'CNY', 'https://support.huaweicloud.com/price-maas/price-maas-0002.html')

const PANGU: readonly ModelPricing[] = [
  pangu('openpangu-2.0-flash', 'openPangu 2.0 Flash', one({ input: 0.8, output: 1.6, cacheRead: 0.2 })),
]

/* ══════════════════════════ 美团 LongCat ══════════════════════════ */

/** 官方国际站 USD 限时折扣价；页面未公布结束日期，不能擅自填写 effectiveUntil。 */
const longcat = maker(null, 'USD', 'https://longcat.chat/platform/docs/pricing/longcat-2.0')

const LONGCAT: readonly ModelPricing[] = [longcat('LongCat-2.0', 'LongCat 2.0', one({ input: 0.3, output: 1.2, cacheRead: 0.006 }))]

/* ══════════════════════════ Meta Muse / OpenCode Go ══════════════════════════ */

/** Meta 官方基础价；OpenCode Go contributor SKU 是供应商专属覆盖价。 */
const muse = maker(null, 'USD', 'https://ai.developer.meta.com/docs/models/muse-spark-1.3')
const museGo = maker('opencode-go', 'USD', 'https://opencode.ai/docs/go/')

const MUSE: readonly ModelPricing[] = [muse('muse-spark-1.3', 'Muse Spark 1.3', one({ input: 1.25, output: 4.25, cacheRead: 0.15 })), muse('muse-spark-1.2', 'Muse Spark 1.2', one({ input: 1.25, output: 4.25, cacheRead: 0.15 }), { source: 'https://ai.developer.meta.com/docs/models/muse-spark-1.2' }), museGo('muse-spark-1.3-contributor', 'Muse Spark 1.3 Contributor', one({ input: 0.1, output: 0.2, cacheRead: 0.002 })), museGo('muse-spark-1.2-contributor', 'Muse Spark 1.2 Contributor', one({ input: 0.1, output: 0.2, cacheRead: 0.002 }))]

/* ══════════════════════════ Microsoft ══════════════════════════ */

/*
 * 需求:2026-09-20 用户点名收录 MAI-Code 1.1 Flash,并明示「定价与 GPT-5.6
 * Luna 完全一致」(用户口径,当时无官方定价页可核)。这违反文件头第 2 条的
 * 双源纪律,当时是本表唯一一次破例(2026-09-23 的 `claude-opus-5-5` 是第二次,
 * 理由与两处刻意的不一致见 Anthropic 段)—— 数字照 OPENAI 那边 `gpt-5.6-luna` 一行
 * 逐值抄,含 272K 长上下文双档与缓存读写价。
 * 用户随后要求**独立成行、不依赖 Luna**:这里存绝对价,目录行的
 * pricingModelId 也是自身 id。此后两条价目各自演进 —— Luna 调价不会自动
 * 带到这条,反过来也一样;要改这条必须带着微软官方价来。
 * fetchedAt 沿用全表快照日(结构测试钉死全表一致),真实录入日以本注释为准。
 */
const microsoft = maker(null, 'USD', 'https://learn.microsoft.com/azure/ai-foundry/')

const MICROSOFT: readonly ModelPricing[] = [
  microsoft('mai-code-1.1-flash', 'MAI-Code 1.1 Flash', two(272_000, { input: 0.2, output: 1.2, cacheRead: 0.02, cacheWrite: 0.25 }, { input: 0.4, output: 1.8, cacheRead: 0.04, cacheWrite: 0.5 })),
]

/**
 * 种子表全量。**顺序 = 界面默认顺序**(国际在前、国内在后,各自按厂商聚簇)。
 */
export const PRICING_SEED: readonly ModelPricing[] = [...ANTHROPIC, ...OPENAI, ...GEMINI, ...XAI, ...ZHIPU, ...DEEPSEEK, ...MOONSHOT, ...QWEN, ...MINIMAX, ...MIMO, ...DOUBAO, ...BAIDU, ...HUNYUAN, ...STEPFUN, ...BAICHUAN, ...SENSENOVA, ...SPARK, ...PANGU, ...LONGCAT, ...MUSE, ...MICROSOFT]

/**
 * ★★ **故意没收进来的东西,以及为什么。**
 *
 * 写成一份可导出的清单而不是散落的注释,是因为「这一条是漏了还是砍了」
 * 只能由记录回答 —— 而**下一个人默认会假设是漏了**,然后「顺手补全」回来。
 *
 * 界面上不显示它,它的读者是改这张表的人。
 */
export const NOT_SEEDED: readonly { what: string; why: string }[] = [
  {
    what: '华为 openPangu 2.0 Pro 阶梯价',
    why: '官方价格表公开了 0≤Token<32K 与 Token≥32K 两档费率，但档位维度写的是“单次请求的Token数”，没有明确等同于现有 PriceTier 只使用的输入 Token 数。即使把严格小于 32K 的整数边界写成含上界 31,999，也仍会在选档维度上产生未经证实的语义转换，因此在官方澄清前不启用自动计价。',
  },
  {
    what: '上海人工智能实验室 InternLM 官方托管 API',
    why: '当前官方目录公开了 Intern S2、Intern S1 与 InternVL 3.5 的可调用型号、上下文和月度 Token 配额，但没有公开按量输入、输出或缓存单价，也没有公布计费币种。不能把配额制公测服务推断为零价或沿用第三方托管价格。',
  },
  {
    what: 'Mistral 全系(8 款)',
    why: '**仅 OpenRouter 单源**,未与官方页比对。而 OpenRouter 在本次调研里被抓到三个独立的价格错(其中一个是旗舰模型上 2 倍的误差),单源不够 —— 文件头第 2、3 条。',
  },
  {
    what: 'Gemini 3.1 Flash-Lite / 3 Flash Preview / 2.5 Flash / 2.5 Flash-Lite 的音频输入差价',
    why: 'Google 官方 Standard 价对 audio input 单独加价，而 `TokenRates` 目前不能按输入模态区分。本表只保存当前文本链路适用的 text/image/video 价；在增加输入模态计费维度前，绝不能用该基础价结算音频输入。',
  },
  {
    what: 'MiniMax M2-her / M1 / abab / VL-01 / Text-01 / 01',
    why: 'MiniMax 当前国际站 Pay-as-you-go 官方页只列 M3、M2.7、M2.5、M2.1、M2 及部分 highspeed SKU；这些旧型号没有当前可核实的标准 Token 价，不能沿用同族价格。',
  },
  {
    what: '豆包输出长度分档、音频输入、缓存存储与媒体生成费用',
    why: '已收录当前可以按输入 Token 档位无损表达的豆包文本模型，并通过官方接入点 alias 映射价格。音频输入有独立单价(2.1 Lite 是 ¥6/百万)，缓存存储按 Token×小时，生图与视频又按张或秒计费(Seedream 5.0、Seedance 2.0/2.5 一律如此)，现有 TokenRates 无法准确表达这些维度 —— 目录里那 11 行 Seedream/Seedance 因此没有价目行，是查不到价显示「—」，不是漏抄。',
  },
  {
    what: '百度千帆按次搜索、批量折扣、量包与未公开标准价型号',
    why: '已收录 ERNIE 5.1、5.0、4.5 Turbo、4.5 Turbo VL、X1.1 Preview 和 InternVL3-38B 的常规在线推理 Token 价。搜索增强按次、批量推理和量包属于不同服务等级；ERNIE X1.1 正式版等型号未在当前标准按量表中单列价格，不能从 Preview 或旧型号推算。',
  },
  {
    what: '腾讯混元旧平台型号、媒体与不同计费单位',
    why: '已收录 TokenHub 当前七款混元语言模型的常规在线 Token 价。hunyuan-turbo、hunyuan-pro、hunyuan-large 与 hunyuan-t1 已停服，hunyuan-a13b 属于即将全面停服的旧平台，不能冒充当前 TokenHub 价格；图像、视频、语音和多模态向量模型还按张、秒、字符或输入模态计费，现有文本 TokenRates 无法无损表达。',
  },
  {
    what: '智谱人民币价格',
    why: '`open.bigmodel.cn` 的价格接口要 `Authorization`,匿名探测一律 `1001 Header中未收到Authorization参数`,文档站索引里也没有价格页 —— **一个数字都没核实到**。美元价(国际站 docs.z.ai)已收,挂在 `zai` 下。',
  },
  {
    what: 'Qwen Plus/Turbo 的模式差异价、上下文缓存价及未在国际站按量页列价的开放权重型号',
    why: '阶梯款已依据国际站明确的 K=1000 规则录入；但 `qwen-plus` / `qwen-turbo` 输出价按思考/非思考模式分列，缓存还分显式与隐式创建，`TokenRates` 暂无这些维度。开放权重型号也不能拿托管别名价格代替。',
  },
  {
    what: 'StepFun 旧型号与非 Token 计费能力',
    why: '已收录官方当前页明确列价的 step-3.7-flash、两款 step-3.5-flash 与 step-1o-turbo-vision；旧目录型号没有当前同名价格。语音按小时或字符、生图按张、搜索与文件存储按次/容量计费，这些非 Token 费用不能塞入文本 Token 费率。',
  },
  {
    what: '百川搜索费用、媒体与未建模型号',
    why: '已收录官方价格页中能与目录精确对应的十个文本 Token 型号。搜索增强与医疗搜索按次收费，Baichuan-Omni-1.5 没有可无损对应的文本 Token 价，因此不并入基础输入输出费率。',
  },
  {
    what: 'SenseNova 实时、语音、兼容页旧型号与未单列价能力',
    why: '已收录日日新主定价页当前八个 Token 型号。SenseNova-V6-Omni 按分钟、Audio Fusion 按字符、nova-tts-1 仅写限时免费；兼容页四个旧/次级型号与 SenseChat-FunctionCall 未在主定价页单列当前价格，因此不能继承同族价格。',
  },
  {
    what: '讯飞星火旧直连型号、Token Plan 与 Coding Plan',
    why: '已收录星辰 MaaS 当前公开按量价的 X2、X2 Flash、X2.5 4B 与 X2.5 1.7B。旧直连 Lite/Pro/Max/Ultra 与星辰 MaaS 是不同渠道，官方当前页没有可无损对应的同渠道现行按量价；Token Plan 和 Coding Plan 使用订阅积分，也不是按量货币价格。',
  },
  {
    what: 'grok-4.20 系列 / grok-build-0.1',
    why: '仅见于 OpenRouter,LiteLLM 里没有 —— 单源。4.3/4.5/4.6 是双源吻合才收的。',
  },
  {
    what: '订阅制额度(Kimi / GLM / Z.AI Coding Plan、OpenCode Go 月度额度)',
    why: '按积分或按月额度计,不是按 token 计费,和这张表不是同一种东西。方案 §5.3:订阅额度不计入总费用；OpenCode Go 文档另行公开的 contributor token 参考价已作为 provider 覆盖价收录。',
  },
  {
    what: '批处理 / 服务等级 / 区域倍率',
    why: '方案 §4.5 明确不建模。我们不发批处理请求;区域与优先级是**账户级**设置,应用无从得知。需要的用户走 provider 级覆盖价。',
  },
  {
    what: '按小时计费的缓存存储(Google / 豆包 / 智谱)',
    why: '方案 §4.5:要算它得知道缓存条目建于何时、持有了多久,而流式响应里根本没有这个信息,硬凑一个持有时长等于编数字。★ OpenRouter 就是这么错的 —— 它把 Google 的每小时费率 ÷ 12 塞进了一次性写入费字段,**量纲错**,持有 1 小时会少收 12 倍。',
  },
  {
    what: 'Claude Managed Agents 的 $0.08 / session-hour',
    why: '一条**与 token 无关**的计费线。我们的统计只有 token,这部分本来就会漏算 —— 收进来也算不出。',
  },
]
