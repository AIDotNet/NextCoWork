/**
 * 按字符数估 token —— 主进程的上下文估算和渲染层的思考卡片共用这一份。
 *
 * 需求:思考卡片要在上游没报 `reasoningTokens` 时(Anthropic 一律不单列、流式中途
 * 谁都还没报)显示一个约数,而渲染层不能 import `main/**`(工程约定 §1)。
 * 原先它住在 `main/kernel/context-assembler.ts`,那边现在从这里 import 并原样再导出,
 * 所以主进程里所有调用点一行不用改。
 *
 * ★ 两处必须是**同一个函数**:同一段思考,上下文压力条按它算、卡片上也按它算;
 * 各写一份的话,两个数迟早对不上。
 */

/**
 * ★ 这是**估算**,不是真值。真值在 `message_end.usage` 里,session 收到后
 * 应当用它覆盖。但压力条必须在**请求发出前**就画出来 —— 那时唯一能有的就是估算。
 *
 * 误差量级:英文约 ±15%,中文约 ±25%。够画一根进度条,不够做计费。
 * 所以 UI 上它是一根**条**,不是一个数字 —— 显示「128,431 / 200,000」会让人
 * 以为那是精确的,然后在它和账单对不上时来提 bug。
 *
 * (随思考卡片搬到 shared 后补一句:卡片上确实要显示一个数,但那里写的是**紧凑读数**
 * (`1.2K`),不写精确到个位的整数 —— 上面那条顾虑换一种方式满足。按需求估算值不与
 * 上游真值做视觉区分,两个来源在卡片上只剩悬停提示和 `data-estimated` 不同。
 * 上游报了真值的块走 `ContentPart.tokens`,不经过这里。)
 */
const CHARS_PER_TOKEN_LATIN = 4
const TOKENS_PER_CJK_CHAR = 1

function isCjk(cp: number): boolean {
  return (
    (cp >= 0x2e80 && cp <= 0x9fff) || // 部首、假名、CJK 统一表意
    (cp >= 0xac00 && cp <= 0xd7af) || // 谚文
    (cp >= 0xf900 && cp <= 0xfaff) || // 兼容表意
    (cp >= 0xff00 && cp <= 0xff60) || // 全角
    (cp >= 0x20000 && cp <= 0x3ffff) //  扩展 B 及以上
  )
}

export function estimateTokens(text: string): number {
  let cjk = 0
  let total = 0
  // for...of 按码点迭代 —— 用 text.length 会把一个 emoji 算成两个字符
  for (const ch of text) {
    total++
    if (isCjk(ch.codePointAt(0) ?? 0)) cjk++
  }
  return Math.ceil(cjk * TOKENS_PER_CJK_CHAR + (total - cjk) / CHARS_PER_TOKEN_LATIN)
}
