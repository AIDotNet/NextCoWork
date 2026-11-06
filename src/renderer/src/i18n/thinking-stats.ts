/**
 * 思考卡片标题行右端的用时与 token 读数。
 *
 * 需求:思考卡片要回答「想了多久、想了多少」。数字本身是领域值,这里只翻它周围的话。
 *
 * ★ 真值(上游报的 `reasoningTokens`)和按正文估的约数**画成同一个样子** ——
 * 按需求不加「≈」之类的标记。两者只在悬停提示里分开(`tokensReportedHint` /
 * `tokensEstimatedHint`),机器可读的那个标记是卡片上的 `data-estimated`。
 * 估算本身的误差量级见 `shared/agent/token-estimate.ts`。
 */
type Params = Record<string, string | number>

export const thinkingStatsZh = {
  'chat.thinkingStats.tokens': ({ count }: Params) => `${count} tokens`,
  'chat.thinkingStats.tokensReportedHint': '上游报告的思考 token 数',
  'chat.thinkingStats.tokensEstimatedHint': '按思考正文估算的 token 数，仅供参考',
  'chat.thinkingStats.durationHint': '思考用时'
}

export const thinkingStatsEn = {
  'chat.thinkingStats.tokens': ({ count }: Params) => `${count} tokens`,
  'chat.thinkingStats.tokensReportedHint': 'Thinking tokens reported by the provider',
  'chat.thinkingStats.tokensEstimatedHint': 'Estimated from the thinking text; approximate',
  'chat.thinkingStats.durationHint': 'Thinking time'
}
