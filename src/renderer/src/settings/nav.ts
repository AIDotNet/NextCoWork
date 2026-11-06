/**
 * 设置浮层的导航表 + 可搜索行目录。
 *
 * **纯数据,不 import 任何 React/lucide** —— 图标在同目录的 `icons.ts`
 * (照 `shell/icons.ts` 的先例)。这样这个文件连同 `matchRows` 能在
 * vitest 的 node 环境里直接测:`vitest.config.ts` 只匹配 `.ts`,没有 jsdom。
 *
 * ★ **十项是照参考图铺的,不是照「我们实现了什么」铺的。** 其中账户 / 钱包 /
 * 每日回顾在方案 §10 是**明确砍掉**的(侧边栏左下角那块之所以从「账户」换成
 * 「设置入口」就是因为这个),电脑操作是步骤 9。这四页在界面上直说被砍了或
 * 排在第几步,不做成「即将推出」—— 后者是在骗自己。
 */

export type SettingsPageId =
  | 'account'
  | 'wallet'
  | 'general'
  | 'import'
  | 'preference'
  | 'model'
  | 'usage'
  | 'review'
  | 'connection'
  | 'devices'
  | 'computer'
  | 'data'
  | 'experimental'
  | 'about'

export interface SettingsSub {
  id: string
  label: string
}

export interface SettingsPage {
  id: SettingsPageId
  label: string
  /** 子 Tab(参考图里「通用」下的 应用|Agent|任务)。没有就是单页 */
  subs?: readonly SettingsSub[]
  /**
   * 只在登录 NextCoWork 账户后出现在导航与搜索里。
   * 需求：钱包的一切（余额、充值、消费记录）都属于账户，本地模式下这一页没有任何能做的事 ——
   * 画出来就是一个点进去只剩「请登录」的入口。过滤只在 `visibleSettingsPages` 一处做。
   */
  requiresSignIn?: true
}

export const SETTINGS_PAGES: readonly SettingsPage[] = [
  { id: 'account', label: '账户' },
  { id: 'wallet', label: '钱包', requiresSignIn: true },
  {
    id: 'general',
    label: '通用',
    subs: [
      { id: 'app', label: '应用' },
      { id: 'agent', label: 'Agent' },
      { id: 'task', label: '任务' }
    ]
  },
  /**
   * ★ 第十一页,**不是**照参考图铺的 —— 参考图里没有它。
   *
   * 它和「数据」页那个「导入数据」是**两回事**:那边读的是 NextCoWork 自己
   * 导出的整库备份,这边读的是别的 AI 应用留在本机的目录。合进「数据」页的话,
   * 页面上会同时出现两颗都叫「导入」的按钮,而误点的代价是一份外部数据
   * 覆盖掉整套设置。分成两页是让这两件事在界面上**永远不挨着**。
   *
   * 没有子 Tab:首版只有一个来源(Claude Code),铺一个只有一项的切换器
   * 是在假装还有别的。
   */
  { id: 'import', label: '导入' },
  {
    id: 'preference',
    label: '偏好',
    /**
     * ★ 前两个 Tab 是把**已有的两组**拆开,不是新加内容 —— 这一页原来就是
     * 「主题三栏 + 快捷键一行」上下堆着,而快捷键那一行滚到屏幕外之后,
     * 整页读起来就只剩主题。第三个才是新的。
     */
    subs: [
      { id: 'theme', label: '主题' },
      { id: 'shortcut', label: '快捷键' },
      { id: 'personalization', label: '个性化' }
    ]
  },
  {
    id: 'model',
    label: '模型',
    /**
     * ★ 五个 Tab **不是五种能力**,是「照参考图铺满 + 诚实标注哪几个是空的」——
     * 本轮只有 `text` 真能用(范围决策:全铺,只有文本真能用)。
     *
     * 五个 id 和 `shared/domain/pricing.ts` 的 `Modality` **逐字相同**,
     * 对不上就是静默筛出空表(定价表按模态过滤会永远空),
     * 所以 `pages/model/tabs.ts` 的测试守着这条。
     */
    subs: [
      { id: 'text', label: '文本生成' },
      { id: 'image', label: '图像生成' },
      { id: 'video', label: '视频生成' },
      { id: 'speech', label: '语音生成' },
      { id: 'transcription', label: '语音识别' }
    ]
  },
  /**
   * ★ 第十二页。它曾经是「模型」底下的第六个子 Tab,现在独立成页 ——
   * 那五个子 Tab 是**按模态选模型**(选完了要保存到设置里),而这一页
   * 一个设置都不写,只读历史账。挂在同一排切换器上,「模型」这一页就同时
   * 是配置面板和报表,而两者的进出方式完全不同:配置是改完就走,
   * 报表是切范围反复看。
   */
  { id: 'usage', label: '使用统计' },
  { id: 'review', label: '每日回顾' },
  {
    id: 'connection',
    label: '连接',
    /**
     * ★ **七项照参考图铺,顺序也照抄** —— 其中只有 MCP / 搜索服务 / 网络
     * 三项背后有真运行时(方案的步骤 10、以及本轮新接的搜索与代理)。
     * 开放网关的 HTTP 壳是步骤 13,那一页照实标注「未监听」;
     * 连接器 / 插件 / 机器人对话在方案里**没有对应子系统**,三页各一句直说,
     * 不编占位数据 —— 同 `StubPage.tsx` 的规矩。
     */
    subs: [
      { id: 'ssh', label: 'SSH' },
      { id: 'connector', label: '连接器' },
      { id: 'mcp', label: 'MCP' },
      { id: 'plugin', label: '插件' },
      { id: 'search', label: '搜索服务' },
      { id: 'bot', label: '机器人对话' },
      { id: 'gateway', label: '开放网关' },
      { id: 'network', label: '网络' }
    ]
  },
  /**
   * 第十三页:控制端本地的「已配对设备」。它和「连接」页的 SSH/MCP 是两回事 —— 那些是这台设备
   * 去连别的东西,这一页是这台电脑去控制别的 NextCoWork 设备。
   */
  { id: 'devices', label: '设备' },
  { id: 'computer', label: '电脑操作' },
  { id: 'data', label: '数据' },
  /**
   * 尚未定型的功能,每一项都出厂关闭(见 `ExperimentalSettings`)。
   * 放在「关于」前面、所有常规页之后:它不该是用户找东西时先看到的那一页。
   */
  { id: 'experimental', label: '实验' },
  { id: 'about', label: '关于' }
]

export const DEFAULT_SETTINGS_PAGE: SettingsPageId = 'general'

/** 当前登录态下导航里该出现的页。`SETTINGS_PAGES` 仍是全集（搜索目录、文案校验都按全集）。 */
export function visibleSettingsPages(signedIn: boolean): SettingsPage[] {
  return SETTINGS_PAGES.filter((p) => signedIn || p.requiresSignIn !== true)
}

/**
 * 真正要渲染的页。需求：未登录时即便有人请求钱包页（登录在浮层开着时失效、
 * 或旧的深链），也不能落在一个导航里看不见的页上 —— 送去账户页，那里有登录入口。
 */
export function resolveSettingsPage(page: SettingsPageId, signedIn: boolean): SettingsPageId {
  const def = SETTINGS_PAGES.find((p) => p.id === page)
  return def?.requiresSignIn === true && !signedIn ? 'account' : page
}

export const PAGE_LABEL: Readonly<Record<SettingsPageId, string>> = Object.fromEntries(
  SETTINGS_PAGES.map((p) => [p.id, p.label])
) as Record<SettingsPageId, string>

export interface SettingsRow {
  page: SettingsPageId
  /** 所属子 Tab 的 id。搜索命中后跳过去要连子 Tab 一起切 */
  sub?: string
  title: string
  /**
   * 额外的可搜索词。中英双写是这里唯一有价值的东西 ——
   * 用户十有八九会打 `proxy` 而不是「代理」,打 `theme` 而不是「主题」。
   */
  keywords?: readonly string[]
}

/**
 * ★ **这张表是唯一事实来源,页面组件从它取标题渲染。**
 * 表和界面各写一份的话,搜出来点过去会落在一个没有那一行的页面上,
 * 而这种漂移没有任何机制会报警。
 */
export const SETTINGS_INDEX: readonly SettingsRow[] = [
  {
    page: 'connection',
    sub: 'ssh',
    title: 'SSH',
    keywords: ['ssh', 'remote', 'server', '服务器', '远程']
  },
  // ── 通用 ──
  { page: 'general', sub: 'app', title: '界面语言', keywords: ['language', 'locale', '语言'] },
  {
    page: 'general',
    sub: 'app',
    title: '任务完成提示音',
    keywords: ['notification', 'sound', '通知']
  },
  {
    page: 'general',
    sub: 'app',
    title: '权限审批提示音',
    keywords: ['notification', 'sound', '通知']
  },
  {
    page: 'general',
    sub: 'app',
    title: '计划审批提示音',
    keywords: ['notification', 'sound', '通知']
  },
  {
    page: 'general',
    sub: 'agent',
    title: '默认权限档位',
    keywords: ['permission', '审批', '权限']
  },
  {
    page: 'general',
    sub: 'agent',
    title: '智能上下文管理',
    keywords: ['context', 'memory', '上下文', '笔记']
  },
  {
    page: 'general',
    sub: 'agent',
    title: '自动上下文压缩',
    keywords: ['compact', 'compression', '压缩']
  },
  {
    page: 'general',
    sub: 'agent',
    title: '回合结束后建议下一步',
    keywords: ['suggestion', 'autocomplete', 'ghost', 'tab', 'prompt', '补全', '建议', '灰字', '提示']
  },
  {
    page: 'general',
    sub: 'agent',
    title: '历史与文件补全',
    keywords: ['history', 'file', 'autocomplete', 'tab', '补全', '历史', '文件']
  },
  {
    page: 'general',
    sub: 'agent',
    title: '打字时续写',
    keywords: ['inline', 'autocomplete', 'completion', 'copilot', 'tab', '续写', '补全']
  },
  {
    page: 'general',
    sub: 'agent',
    // ★ 标题是**域名词**「Shell」,不翻译(界面那一行也写 Shell);用户打的是
    //   shell 名或 shell 本身,所以关键词把八个取值全收进来,外加 `sheel` 这个
    //   常见拼错 —— 搜不到就等于这一项不存在。
    title: 'Shell',
    keywords: [
      'shell',
      'sheel',
      'bash',
      'zsh',
      'fish',
      'sh',
      'powershell',
      'pwsh',
      'cmd',
      'terminal',
      '终端',
      '命令'
    ]
  },
  {
    page: 'general',
    sub: 'task',
    title: '单对话子代理上限',
    keywords: ['subagent', '子代理', '并发']
  },
  {
    page: 'general',
    sub: 'task',
    title: '子代理并发上限',
    keywords: ['subagent', '子代理', '并发']
  },

  // ── 导入 ──
  // ★ 每一行都必须真的在那一页上(见 SETTINGS_INDEX 顶上那条约定)。
  //   「自动同步」「同步内容」「选择导入」「导入历史」是导入页上真实存在的四块。
  {
    page: 'import',
    title: '自动同步',
    keywords: ['sync', 'auto', 'claude', '同步', '自动']
  },
  {
    page: 'import',
    title: '同步内容',
    keywords: ['sync', 'category', 'claude', '同步', '内容', '类别']
  },
  {
    page: 'import',
    title: '从其他 AI 应用导入',
    keywords: ['import', 'claude', 'claude code', 'migrate', '导入', '迁移', '其他']
  },
  {
    page: 'import',
    title: '导入历史',
    keywords: ['import', 'history', 'batch', '导入', '历史', '批次']
  },

  // ── 偏好 ──
  {
    page: 'preference',
    sub: 'theme',
    title: '外观模式',
    keywords: ['theme', 'dark', 'light', '深色', '浅色', '主题']
  },
  {
    page: 'preference',
    sub: 'theme',
    title: '图片主题',
    keywords: ['image', 'wallpaper', 'theme', '图片', '壁纸', '主题']
  },
  {
    page: 'preference',
    sub: 'theme',
    title: '颜色主题',
    keywords: ['color', 'palette', 'theme', '配色', '颜色', '主题']
  },
  {
    page: 'preference',
    sub: 'shortcut',
    title: '打开设置',
    keywords: ['shortcut', 'keybinding', '快捷键']
  },
  // ★ 这三行是**唯一**会被拼进系统提示词的设置项,所以关键词里要收
  // 「prompt / 提示词 / 指令」—— 想改模型口吻的人搜的是这几个词,
  // 而不是「偏好」或「个性化」。
  {
    page: 'preference',
    sub: 'personalization',
    title: '姓名',
    keywords: ['name', 'profile', '姓名', '名字', '个性化']
  },
  {
    page: 'preference',
    sub: 'personalization',
    title: '工作描述',
    keywords: ['about', 'background', 'role', 'job', '背景', '职业', '工作', '个性化']
  },
  {
    page: 'preference',
    sub: 'personalization',
    title: '全局提示词',
    keywords: ['prompt', 'instruction', 'system', '提示词', '指令', '自定义', '个性化']
  },

  // ── 模型 ──
  // ★ 只有「文本生成」这个子 Tab 有真行。其余五个今天是占位,**不给它们编行** ——
  // 上面那句「这张表是唯一事实来源」的代价就是:搜出来点过去,那一行必须真的在。
  // 供应商的行随步骤 4 的写入面补进来;定价与用量在方案里没有编号,随内容一起补。
  { page: 'model', sub: 'text', title: '默认模型', keywords: ['model', '模型'] },
  {
    page: 'model',
    sub: 'text',
    title: '默认子代理模型',
    keywords: ['subagent', 'model', '子代理']
  },
  {
    page: 'model',
    sub: 'text',
    title: '启用的模型',
    keywords: ['provider', 'model', '供应商', '模型']
  },
  {
    page: 'model',
    sub: 'text',
    title: '供应商目录',
    keywords: ['provider', 'preset', 'openrouter', '供应商', '预设', '添加']
  },
  {
    page: 'model',
    sub: 'text',
    title: '模型优先级',
    // 「拉取 / 同步 / fetch」都收进来:用户想找的是那颗按钮,而按钮上写的是「拉取」
    keywords: ['model', 'fetch', 'sync', 'import', '拉取', '同步', '导入', '模型列表']
  },

  // ── 使用统计 ──
  // ★ 四行对应页面上真实存在的四块面板(见 SETTINGS_INDEX 顶上那条约定)。
  //   关键词收 `cost` / `token` / `spend`:想查账的人搜的是这几个词,不是「统计」。
  {
    page: 'usage',
    title: 'Token 活动',
    keywords: ['usage', 'token', 'activity', 'heatmap', '活跃', '热力图', '统计']
  },
  {
    page: 'usage',
    title: '每日 Token 趋势',
    keywords: ['usage', 'token', 'trend', 'chart', '趋势', '图表', '统计']
  },
  {
    page: 'usage',
    title: '模型费用',
    keywords: ['cost', 'spend', 'price', 'billing', '费用', '花费', '账单', '价格']
  },
  {
    page: 'usage',
    title: '请求日志',
    keywords: ['usage', 'log', 'request', 'latency', '日志', '请求', '延迟']
  },

  // ── 连接 ──
  // ★ 连接器 / 插件 / 机器人对话三个子 Tab **没有行** —— 它们背后没有子系统,
  // 编几行出来会让「搜出来点过去必须真的在」这条约定当场破掉。
  {
    page: 'connection',
    sub: 'mcp',
    title: '添加 MCP 服务器',
    keywords: ['mcp', 'server', '服务器']
  },
  { page: 'connection', sub: 'mcp', title: 'MCP 服务器', keywords: ['mcp', 'tool', '工具'] },
  {
    page: 'connection',
    sub: 'search',
    title: '搜索服务',
    keywords: ['search', 'web', '联网', '搜索']
  },
  {
    page: 'connection',
    sub: 'search',
    title: '搜索服务优先级',
    keywords: ['search', 'priority', 'order', '优先级', '排序']
  },
  { page: 'connection', sub: 'gateway', title: '启用本地网关', keywords: ['gateway', '网关'] },
  { page: 'connection', sub: 'gateway', title: '期望端口', keywords: ['port', 'gateway', '端口'] },
  { page: 'connection', sub: 'gateway', title: '故障切换', keywords: ['failover', '切换'] },
  { page: 'connection', sub: 'network', title: '启用代理', keywords: ['proxy', '代理', '网络'] },
  {
    page: 'connection',
    sub: 'network',
    title: '代理服务器',
    keywords: ['proxy', 'host', 'port', '代理', '地址', '端口']
  },
  {
    page: 'connection',
    sub: 'network',
    title: '代理身份验证',
    keywords: ['proxy', 'auth', '认证', '密码']
  },
  {
    page: 'connection',
    sub: 'network',
    title: '直连白名单',
    keywords: ['proxy', 'bypass', 'whitelist', '白名单', '直连']
  },

  // ── 数据 ──
  { page: 'data', title: '设置云同步', keywords: ['cloud', 'sync', '云端', '同步'] },
  { page: 'data', title: '导出', keywords: ['export', '迁移', 'backup', '导出'] },
  { page: 'data', title: '导入数据', keywords: ['import', '迁移', '导入'] },
  { page: 'data', title: '备份目录', keywords: ['backup', 'directory', '备份', '目录'] },
  { page: 'data', title: '备份频率', keywords: ['backup', 'frequency', '备份', '频率'] },
  { page: 'data', title: '上次备份', keywords: ['backup', '备份'] },
  { page: 'data', title: '从备份文件恢复', keywords: ['restore', 'backup', '恢复', '备份'] },
  { page: 'data', title: '数据库大小', keywords: ['storage', 'database', '存储'] },
  { page: 'data', title: '对话文件', keywords: ['storage', 'database', '存储'] },
  { page: 'data', title: '对话数量', keywords: ['storage', '统计'] },
  { page: 'data', title: '消息数量', keywords: ['storage', '统计'] },
  { page: 'data', title: '数据目录', keywords: ['storage', 'directory', '存储', '目录'] },
  { page: 'data', title: '优化存储', keywords: ['vacuum', 'storage', '清理'] },
  { page: 'data', title: '清理附件目录', keywords: ['attachment', 'cleanup', '清理', '附件'] },
  { page: 'data', title: '清理范围', keywords: ['cleanup', 'range', '清理', '范围'] },
  { page: 'data', title: '清空对话历史', keywords: ['delete', 'history', '清空', '对话'] },
  { page: 'data', title: '删除并退出', keywords: ['delete', 'reset', '退出', '删除'] },

  // ── 实验 ──
  {
    page: 'experimental',
    title: 'Workflow 工作流',
    keywords: ['experimental', 'labs', 'beta', 'workflow', 'canvas', '实验', '工作流', '画布', '编排']
  },
  {
    page: 'experimental',
    title: '运行前确认',
    keywords: ['workflow', 'confirm', 'approval', '工作流', '确认', '审批']
  },

  // ── 关于 ──
  { page: 'about', title: '版本', keywords: ['version', 'about', '版本'] }
]

/**
 * 跨页过滤。纯函数 —— 空查询返回空数组(调用点据此决定「显示正常页面」
 * 而不是「显示全部结果」;返回全表的话一打开设置就是一屏搜索结果)。
 */
export function matchRows(query: string): SettingsRow[] {
  const q = query.trim().toLowerCase()
  if (q === '') return []
  return SETTINGS_INDEX.filter((r) => {
    if (r.title.toLowerCase().includes(q)) return true
    if (PAGE_LABEL[r.page].toLowerCase().includes(q)) return true
    return (r.keywords ?? []).some((k) => k.toLowerCase().includes(q))
  })
}

/**
 * 页面本身命中(用户打「关于」时那一页没有任何行,但页名该出现)。
 * `signedIn` 必填而不给默认值:漏传就会把钱包页搜给本地模式的用户,而那一页导航里看不见。
 */
export function matchPages(query: string, signedIn: boolean): SettingsPage[] {
  const q = query.trim().toLowerCase()
  if (q === '') return []
  return visibleSettingsPages(signedIn).filter((p) => p.label.toLowerCase().includes(q))
}
