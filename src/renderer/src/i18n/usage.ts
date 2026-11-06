/**
 * 使用统计页的文案 —— 请求日志表 + 图表化概览。
 *
 * 从 `index.tsx` 搬出来,照 `ssh.ts` / `extensions.ts` / `editor.ts` 的先例:
 * 概览区把这一块从 68 条推到一百多条,继续堆在那三千行里只会让谁都不敢动它。
 *
 * ★ `usageEn` **不要**标 `Record<keyof typeof usageZh, string>` —— 带插值的条目
 * 值是函数不是 string,标了整张表都不匹配。zh/en 的键对齐由
 * `index.test.ts` 在测试里守着,那是运行时逐键比对,比这个类型管得更准。
 */

/**
 * 带参数文案的入参类型。★ 必须显式标出来:这个文件拿不到 `index.tsx` 里
 * `Messages` 的上下文,不标的话参数是 implicit any,spread 进 `ZH` 时整张表
 * 都不再匹配 `Messages`。
 */
type Params = Record<string, string | number>

export const usageZh = {
  'usage.range.24h': '24h',
  'usage.range.7d': '近 7 天',
  'usage.range.30d': '近 30 天',
  'usage.range.all': '全部',
  'usage.rangeLabel': '统计时间范围',
  'usage.sectionLabel': '统计分区',
  'usage.section.requests': '请求日志',
  'usage.section.providers': '供应商统计',
  'usage.section.models': '模型统计',
  'usage.section.tools': '工具统计',
  'usage.seconds': ({ value }: Params) => `${value} 秒`,
  'usage.milliseconds': ({ value }: Params) => `${value} 毫秒`,
  'usage.loadFailed': '使用统计加载失败，请重试。',
  'usage.totalRequests': '总请求',
  'usage.requestSummary': ({ success, failed }: Params) => `成功 ${success} · 失败 ${failed}`,
  'usage.averageLatencySummary': ({ latency, ttft }: Params) =>
    `平均 ${latency} · 首 Token ${ttft}`,
  'usage.totalCost': '总费用',
  'usage.frozenPriceHint': '按请求发生时命中的定价冻结',
  'usage.unpricedHint': '缺少定价的记录显示为「—」',
  'usage.totalTokens': '总 Token',
  'usage.inputOutputSummary': ({ input, output }: Params) => `输入 ${input} / 输出 ${output}`,
  'usage.thinkingSummary': ({ tokens, estimated }: Params) =>
    `思考 ${tokens} · ${estimated} 条为估算`,
  'usage.cacheHitRate': '缓存命中率',
  'usage.byToken': ({ rate }: Params) => `按 Token ${rate}`,
  'usage.cacheSummary': ({ read, write }: Params) => `读取 ${read} / 写入 ${write}`,
  'usage.cacheWrite1hSummary': ({ tokens }: Params) => `其中 1 小时写入 ${tokens}`,
  'usage.searchPlaceholder': '搜索模型、供应商或 Run ID…',
  'usage.searchLabel': '搜索请求日志',
  'usage.statusFilterLabel': '按请求状态筛选',
  'usage.status.all': '全部状态',
  'usage.status.success': '成功',
  'usage.status.failed': '失败',
  'usage.showDetails': '详细记录',
  'usage.recordCount': ({ count }: Params) => `共 ${count} 条记录`,
  'usage.time': '时间',
  'usage.provider': '供应商',
  'usage.tokens': 'Token',
  'usage.cost': '费用',
  'usage.latency': '延迟',
  'usage.status': '状态',
  'usage.empty': '所选时间范围内还没有请求记录',
  'usage.noMatch': '没有符合当前筛选条件的请求',
  'usage.pageRange': ({ start, end, total }: Params) => `${start}–${end} / 共 ${total} 条`,
  'usage.previousPage': '上一页',
  'usage.nextPage': '下一页',
  'usage.collapseDetails': '收起请求详情',
  'usage.expandDetails': '展开请求详情',
  'usage.inputTokens': '输入 Token',
  'usage.cacheReadTokens': '缓存读取 Token',
  'usage.cacheWriteTokens': '缓存写入 Token',
  'usage.outputTokens': '输出 Token',
  'usage.thinkingTokens': '思考 Token',
  'usage.firstTokenLatency': '首 Token 延迟',
  'usage.runId': 'Run ID',
  'usage.endpoint': '请求端点',
  'usage.response': '响应',
  'usage.attemptNumber': ({ number }: Params) => `第 ${number} 次尝试`,
  'usage.error': '错误',
  'usage.pricingRule': '计价规则',
  'usage.pricingTier': ({ tier }: Params) => `第 ${tier} 档`,
  'usage.thinkingEstimatedHint':
    '≈ 表示供应商未返回独立思考 Token，本值由可见思考文本估算，不用于计费。',
  'usage.requests': '请求数',
  'usage.successRate': '成功率',
  'usage.tokenBreakdown': '输入 / 缓存 / 输出',
  'usage.averageLatency': '平均延迟',
  'usage.compactTokenBreakdown': ({ input, cache, output }: Params) =>
    `${input} / ${cache} / ${output}`,
  'usage.toolCalls': '工具调用',
  'usage.toolErrors': '工具错误',
  'usage.noToolCalls': '所选时间范围内没有工具调用',

  // ── 概览区 ──
  'usage.overview.title': '概览',
  'usage.overview.refresh': '刷新统计',
  'usage.overview.empty': '所选时间范围内还没有用量记录',
  // 使用统计云同步:概览合并其它设备,请求日志只含本机 —— 口径差必须说出来
  'usage.sync.merged': ({ count }: Params) =>
    `概览已合并另外 ${count} 台设备同步来的用量；请求日志与明细统计只含本机。`,
  'usage.sync.toggle': '同步使用统计',
  'usage.sync.toggleHint': '在已登录同一账号的设备间合并使用统计概览；请求日志不会上传。',

  // 指标卡(参考图顶部那一排)
  'usage.metric.totalTokens': '累计 Token 数',
  'usage.metric.totalCost': '累计费用',
  'usage.metric.peakTokens': '峰值 Token 数',
  'usage.metric.longestChat': '最长聊天时长',
  'usage.metric.currentStreak': '当前连续天数',
  'usage.metric.longestStreak': '最长连续天数',
  'usage.metric.days': ({ days }: Params) => `${days} 天`,
  'usage.metric.onDay': ({ day }: Params) => `出现在 ${day}`,
  'usage.metric.acrossModels': ({ count }: Params) => `覆盖 ${count} 个模型`,
  // ★ 说明「最长聊天时长」会随清理历史而变小 —— 数字变小了却没有解释,
  //   用户只会认为统计坏了
  'usage.metric.chatHint': '按会话内相邻消息间隔 30 分钟分段;清理对话历史后此值会变小',

  // Token 活动热力图
  'usage.activity.title': 'Token 活动',
  'usage.activity.less': '少',
  'usage.activity.more': '多',
  'usage.activity.none': '无活动',
  'usage.activity.summary': ({ days }: Params) => `过去一年有 ${days} 天有活动`,
  'usage.activity.tooltip': ({ tokens, turns }: Params) => `${tokens} tokens · ${turns} 轮消息`,
  'usage.activity.weekday.mon': '周一',
  'usage.activity.weekday.wed': '周三',
  'usage.activity.weekday.fri': '周五',
  'usage.granularity.daily': '每日',
  'usage.granularity.weekly': '每周',
  'usage.granularity.cumulative': '累计',
  'usage.granularityLabel': '统计粒度',

  // Token 趋势。★ 标题不带「每日」:粒度切到每周 / 累计时它就是错的
  'usage.trend.title': 'Token 趋势',
  'usage.trend.tokens': 'Token 数',
  'usage.trend.requests': '请求数',
  'usage.trend.empty': '所选时间范围内没有数据',

  // 模型用量环形图
  'usage.models.title': '模型用量',
  'usage.models.total': '总计',
  'usage.models.others': '其他',
  'usage.models.empty': '所选时间范围内没有模型用量',
  'usage.models.requests': ({ count }: Params) => `${count} 次请求`,
  'usage.models.legendLabel': '模型图例',

  // 费用统计
  'usage.cost.title': '模型费用',
  'usage.cost.model': '模型',
  'usage.cost.amount': '费用',
  'usage.cost.share': '占比',
  'usage.cost.unpriced': '未计价',
  'usage.cost.empty': '所选时间范围内没有产生费用',
  // ★ 「少算的钱」和「省下的钱」在界面上长得一模一样,必须点破
  'usage.cost.unpricedHint': ({ count }: Params) => `${count} 条请求未匹配到定价,其费用未计入合计`,
  'usage.cost.currencyNote': '不同币种分别合计,不做汇率换算',

  // 时长。★ 不满一分钟走秒档 —— 「最长聊天 0 分钟」读起来像功能坏了
  'usage.duration.hm': ({ hours, minutes }: Params) => `${hours} 小时 ${minutes} 分`,
  'usage.duration.m': ({ minutes }: Params) => `${minutes} 分钟`,
  'usage.duration.s': ({ seconds }: Params) => `${seconds} 秒`
}

export const usageEn = {
  'usage.range.24h': '24h',
  'usage.range.7d': 'Last 7 days',
  'usage.range.30d': 'Last 30 days',
  'usage.range.all': 'All',
  'usage.rangeLabel': 'Usage time range',
  'usage.sectionLabel': 'Breakdown',
  'usage.section.requests': 'Request logs',
  'usage.section.providers': 'Provider stats',
  'usage.section.models': 'Model stats',
  'usage.section.tools': 'Tool stats',
  'usage.seconds': ({ value }: Params) => `${value}s`,
  'usage.milliseconds': ({ value }: Params) => `${value}ms`,
  'usage.loadFailed': 'Usage statistics could not be loaded. Try again.',
  'usage.totalRequests': 'Total requests',
  'usage.requestSummary': ({ success, failed }: Params) => `Success ${success} · Failed ${failed}`,
  'usage.averageLatencySummary': ({ latency, ttft }: Params) =>
    `Average ${latency} · First token ${ttft}`,
  'usage.totalCost': 'Total cost',
  'usage.frozenPriceHint': 'Frozen using the pricing matched at request time',
  'usage.unpricedHint': 'Records without pricing are shown as “—”',
  'usage.totalTokens': 'Total tokens',
  'usage.inputOutputSummary': ({ input, output }: Params) => `Input ${input} / output ${output}`,
  'usage.thinkingSummary': ({ tokens, estimated }: Params) =>
    `Thinking ${tokens} · ${estimated} estimated records`,
  'usage.cacheHitRate': 'Cache hit rate',
  'usage.byToken': ({ rate }: Params) => `By token ${rate}`,
  'usage.cacheSummary': ({ read, write }: Params) => `Read ${read} / write ${write}`,
  'usage.cacheWrite1hSummary': ({ tokens }: Params) => `1-hour writes ${tokens}`,
  'usage.searchPlaceholder': 'Search model, provider, or Run ID…',
  'usage.searchLabel': 'Search request logs',
  'usage.statusFilterLabel': 'Filter by request status',
  'usage.status.all': 'All statuses',
  'usage.status.success': 'Success',
  'usage.status.failed': 'Failed',
  'usage.showDetails': 'Detailed records',
  'usage.recordCount': ({ count }: Params) => `${count} records`,
  'usage.time': 'Time',
  'usage.provider': 'Provider',
  'usage.tokens': 'Tokens',
  'usage.cost': 'Cost',
  'usage.latency': 'Latency',
  'usage.status': 'Status',
  'usage.empty': 'No requests in the selected time range',
  'usage.noMatch': 'No requests match the current filters',
  'usage.pageRange': ({ start, end, total }: Params) => `${start}–${end} of ${total}`,
  'usage.previousPage': 'Previous',
  'usage.nextPage': 'Next',
  'usage.collapseDetails': 'Collapse request details',
  'usage.expandDetails': 'Expand request details',
  'usage.inputTokens': 'Input tokens',
  'usage.cacheReadTokens': 'Cache-read tokens',
  'usage.cacheWriteTokens': 'Cache-write tokens',
  'usage.outputTokens': 'Output tokens',
  'usage.thinkingTokens': 'Thinking tokens',
  'usage.firstTokenLatency': 'First-token latency',
  'usage.runId': 'Run ID',
  'usage.endpoint': 'Endpoint',
  'usage.response': 'Response',
  'usage.attemptNumber': ({ number }: Params) => `Attempt ${number}`,
  'usage.error': 'Error',
  'usage.pricingRule': 'Pricing rule',
  'usage.pricingTier': ({ tier }: Params) => `Tier ${tier}`,
  'usage.thinkingEstimatedHint':
    '≈ means the provider did not report a separate thinking-token count. This value is estimated from visible thinking text and is not used for billing.',
  'usage.requests': 'Requests',
  'usage.successRate': 'Success rate',
  'usage.tokenBreakdown': 'Input / cache / output',
  'usage.averageLatency': 'Average latency',
  'usage.compactTokenBreakdown': ({ input, cache, output }: Params) =>
    `${input} / ${cache} / ${output}`,
  'usage.toolCalls': 'Tool calls',
  'usage.toolErrors': 'Tool errors',
  'usage.noToolCalls': 'No tool calls in the selected time range',

  // ── Overview ──
  'usage.overview.title': 'Overview',
  'usage.overview.refresh': 'Refresh stats',
  'usage.overview.empty': 'No usage recorded in the selected time range',
  'usage.sync.merged': ({ count }: Params) =>
    `Overview includes usage synced from ${count} other ${Number(count) === 1 ? 'device' : 'devices'}; request logs and breakdowns cover this device only.`,
  'usage.sync.toggle': 'Sync usage statistics',
  'usage.sync.toggleHint': 'Merge the usage overview across devices signed in to the same account. Request logs are never uploaded.',

  'usage.metric.totalTokens': 'Total tokens',
  'usage.metric.totalCost': 'Total cost',
  'usage.metric.peakTokens': 'Peak daily tokens',
  'usage.metric.longestChat': 'Longest chat',
  'usage.metric.currentStreak': 'Current streak',
  'usage.metric.longestStreak': 'Longest streak',
  'usage.metric.days': ({ days }: Params) => `${days} days`,
  'usage.metric.onDay': ({ day }: Params) => `on ${day}`,
  'usage.metric.acrossModels': ({ count }: Params) => `across ${count} models`,
  'usage.metric.chatHint':
    'Split on 30-minute gaps between messages in a session; clearing chat history lowers this value',

  'usage.activity.title': 'Token activity',
  'usage.activity.less': 'Less',
  'usage.activity.more': 'More',
  'usage.activity.none': 'No activity',
  'usage.activity.summary': ({ days }: Params) => `${days} active days in the past year`,
  'usage.activity.tooltip': ({ tokens, turns }: Params) => `${tokens} tokens · ${turns} messages`,
  'usage.activity.weekday.mon': 'Mon',
  'usage.activity.weekday.wed': 'Wed',
  'usage.activity.weekday.fri': 'Fri',
  'usage.granularity.daily': 'Daily',
  'usage.granularity.weekly': 'Weekly',
  'usage.granularity.cumulative': 'Cumulative',
  'usage.granularityLabel': 'Granularity',

  'usage.trend.title': 'Token trend',
  'usage.trend.tokens': 'Tokens',
  'usage.trend.requests': 'Requests',
  'usage.trend.empty': 'No data in the selected time range',

  'usage.models.title': 'Model usage',
  'usage.models.total': 'Total',
  'usage.models.others': 'Others',
  'usage.models.empty': 'No model usage in the selected time range',
  'usage.models.requests': ({ count }: Params) => `${count} requests`,
  'usage.models.legendLabel': 'Model legend',

  'usage.cost.title': 'Cost by model',
  'usage.cost.model': 'Model',
  'usage.cost.amount': 'Cost',
  'usage.cost.share': 'Share',
  'usage.cost.unpriced': 'Unpriced',
  'usage.cost.empty': 'No cost incurred in the selected time range',
  'usage.cost.unpricedHint': ({ count }: Params) =>
    `${count} requests had no matching price and are excluded from the total`,
  'usage.cost.currencyNote': 'Currencies are totalled separately; no conversion is applied',

  'usage.duration.hm': ({ hours, minutes }: Params) => `${hours}h ${minutes}m`,
  'usage.duration.m': ({ minutes }: Params) => `${minutes} min`,
  'usage.duration.s': ({ seconds }: Params) => `${seconds}s`
}
