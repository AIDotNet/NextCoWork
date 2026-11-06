/**
 * 内置工具行的文案表(动作标签 / 右侧摘要 / 兜底标签)。
 *
 * 为了满足「一切用户可见文案走 i18n」这条硬规矩而建:原先这些句子硬编码在
 * `shared/domain/tool-presenter.ts` 的注册表里,英文界面下工具卡片也全是中文。
 * 那边的注册表现在只产出 key + 参数,由本表给出两种语言的句子;
 * `index.tsx` 在模块加载时把 `translate` 注入回注册表,切语言自动生效。
 *
 * ★ `Record<PresenterCopyKey, …>` 是完整性检查:shared 那边新增一个 key
 * 而这里漏翻,**编译期**就挂;两张表也都会被 spread 进 ZH/EN,所以
 * `translate()` 对它们生效,i18n 的键一致性测试顺带覆盖。
 *
 * ★★ `chat.tool.title.*` 现在是**纯动作标签**,不再是「动词 + {target}」的句子。
 * 原先它们是一族 `verbTitle('读取')` 小函数,负责在目标缺席时补成「读取…」;
 * 那个职责随「工具行改成 标签 / 目标 / 目录 三段分色」一起搬走了 ——
 * 目标由 `ToolLine.target` 单独给,缺席时**行里就只剩标签**,不再拼省略号
 * (省略号原本是为了让一句话读起来完整,而现在它不是一句话)。
 * 所以这里一律写成短语,别再往里塞 `{target}`:塞进去的话,渲染层会把同一个
 * 目标显示两遍 —— 一遍在标签里,一遍在目标格里。
 */
import type { PresenterCopyKey } from '../../../shared/domain/tool-presenter'
import type { MessageValue } from './index'

type Params = Record<string, string | number>

// 星期以数字串传入('135',0=周日…6=周六,与 Date.prototype.getDay() 同值域)
const WEEKDAY_ZH = ['日', '一', '二', '三', '四', '五', '六']
const WEEKDAY_EN = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

const weekdayZh = (days: string): string =>
  days.split('').map((d) => WEEKDAY_ZH[Number(d)] ?? '').join('')

const weekdayEn = (days: string): string =>
  days.split('').map((d) => WEEKDAY_EN[Number(d)] ?? '').filter((name) => name !== '').join(', ')

export const toolPresenterZh: Record<PresenterCopyKey, MessageValue> = {
  'chat.tool.title.read': '读取',
  'chat.tool.title.ls': '列目录',
  'chat.tool.title.write': '写入',
  'chat.tool.title.edit': '编辑',
  'chat.tool.title.glob': '查找',
  'chat.tool.title.grep': '搜索',
  'chat.tool.title.bash': '终端',
  'chat.tool.title.bashOutput': '读后台输出',
  'chat.tool.title.killShell': '停止后台命令',
  'chat.tool.title.webFetch': '抓取',
  'chat.tool.title.webSearch': '搜索网络',
  'chat.tool.title.skill': '技能',
  'chat.tool.title.task': '子代理',
  'chat.tool.title.scheduleCreate': '新建定时任务',
  'chat.tool.title.scheduleUpdate': '修改定时任务',
  'chat.tool.title.scheduleDelete': '删除定时任务',
  'chat.tool.title.todo': '更新任务清单',
  'chat.tool.title.scheduleList': '查看定时任务',
  'chat.tool.title.askUser': '向你提问',
  'chat.tool.title.proposeGoal': '提议完成条件',
  'chat.tool.title.planReview': '提交计划待审',
  // target 是模型写的 snake_case 标识(已去下划线),属领域值,由 ToolLine.target 给
  'chat.tool.title.widget': '画图',
  'chat.tool.title.widgetEdit': '改图',
  'chat.tool.title.widgetUpdate': '更新图表数据',
  'chat.tool.title.workflow': '工作流',
  'chat.tool.title.readMe': '加载可视化规范',
  'chat.tool.title.generateImage': '生成图片',
  'chat.tool.title.editImage': '编辑图片',
  'chat.tool.title.saveImage': '保存图片',
  'chat.tool.title.generateVideo': '生成视频',
  'chat.tool.title.checkVideo': '查询视频任务',
  'chat.tool.title.cancelVideo': '取消视频任务',
  'chat.tool.title.saveVideo': '保存视频',
  /*
    浏览器那一族(16 个 `browser_*`)。★ 全族此前没有动作标签,于是每一行都走
    `humanize(internalId)` 兜底 —— 中文界面上一列「browser click / browser type」。
    主语(ref、按键、主机名、坐标、tabId)由 `ToolLine.target` / `context` 给,
    这里只写动作词(同本表其余各条,见文件头那段)。
  */
  'chat.tool.title.browserOpen': '打开页面',
  'chat.tool.title.browserNavigate': '跳转',
  'chat.tool.title.browserSnapshot': '读取页面结构',
  'chat.tool.title.browserScreenshot': '页面截图',
  'chat.tool.title.browserClick': '点击',
  'chat.tool.title.browserType': '输入',
  'chat.tool.title.browserPress': '按键',
  'chat.tool.title.browserSelect': '选择',
  'chat.tool.title.browserScroll': '滚动',
  'chat.tool.title.browserCuaClick': '坐标点击',
  'chat.tool.title.browserCuaDrag': '坐标拖拽',
  'chat.tool.title.browserTabs': '列出标签页',
  'chat.tool.title.browserUserTabs': '列出用户标签页',
  'chat.tool.title.browserClaim': '接管标签页',
  'chat.tool.title.browserProfiles': '列出浏览器配置',
  'chat.tool.title.browserClose': '关闭标签页',
  'chat.tool.fallback': '工具调用',
  'chat.tool.summary.lines': '{count} 行',
  'chat.tool.summary.items': '{count} 项',
  'chat.tool.summary.nodes': '{count} 个节点',
  'chat.tool.summary.files': '{count} 个文件',
  'chat.tool.summary.matches': '{count} 处',
  'chat.tool.summary.results': '{count} 条',
  'chat.tool.summary.outputLines': '{count} 行输出',
  'chat.tool.summary.createdLines': '新建 {count} 行',
  'chat.tool.summary.created': '新建',
  'chat.tool.summary.replaced': '替换 {count} 处',
  'chat.tool.summary.exitCode': '退出码 {code}',
  'chat.tool.summary.noOutput': '无输出',
  'chat.tool.summary.running': '运行中',
  'chat.tool.summary.stopped': '已停止',
  'chat.tool.summary.tasks': '{count} 条',
  'chat.tool.summary.questions': '{count} 道题',
  'chat.tool.summary.scheduleDaily': '每天 {time}',
  'chat.tool.summary.scheduleWeekly': ({ days, time }: Params) =>
    `周${weekdayZh(String(days ?? ''))} ${String(time ?? '')}`
}

export const toolPresenterEn: Record<PresenterCopyKey, MessageValue> = {
  'chat.tool.title.read': 'Read',
  'chat.tool.title.ls': 'List',
  'chat.tool.title.write': 'Write',
  'chat.tool.title.edit': 'Edit',
  'chat.tool.title.glob': 'Find',
  'chat.tool.title.grep': 'Search',
  'chat.tool.title.bash': 'Terminal',
  'chat.tool.title.bashOutput': 'Shell output',
  'chat.tool.title.killShell': 'Stop shell',
  'chat.tool.title.webFetch': 'Fetch',
  'chat.tool.title.webSearch': 'Web search',
  'chat.tool.title.skill': 'Skill',
  'chat.tool.title.task': 'Subagent',
  'chat.tool.title.scheduleCreate': 'New scheduled task',
  'chat.tool.title.scheduleUpdate': 'Edit scheduled task',
  'chat.tool.title.scheduleDelete': 'Delete scheduled task',
  'chat.tool.title.todo': 'Update todo list',
  'chat.tool.title.scheduleList': 'View scheduled tasks',
  'chat.tool.title.askUser': 'Asking you',
  'chat.tool.title.proposeGoal': 'Proposing a goal',
  'chat.tool.title.planReview': 'Submitting the plan',
  'chat.tool.title.widget': 'Visualize',
  'chat.tool.title.widgetEdit': 'Edit visual',
  'chat.tool.title.widgetUpdate': 'Update visual data',
  'chat.tool.title.workflow': 'Workflow',
  'chat.tool.title.readMe': 'Loading visual guidelines',
  'chat.tool.title.generateImage': 'Generate image',
  'chat.tool.title.editImage': 'Edit image',
  'chat.tool.title.saveImage': 'Save image',
  'chat.tool.title.generateVideo': 'Generate video',
  'chat.tool.title.checkVideo': 'Check video job',
  'chat.tool.title.cancelVideo': 'Cancel video job',
  'chat.tool.title.saveVideo': 'Save video',
  'chat.tool.title.browserOpen': 'Open page',
  'chat.tool.title.browserNavigate': 'Navigate',
  'chat.tool.title.browserSnapshot': 'Read page tree',
  'chat.tool.title.browserScreenshot': 'Page screenshot',
  'chat.tool.title.browserClick': 'Click',
  'chat.tool.title.browserType': 'Type',
  'chat.tool.title.browserPress': 'Press',
  'chat.tool.title.browserSelect': 'Select',
  'chat.tool.title.browserScroll': 'Scroll',
  'chat.tool.title.browserCuaClick': 'Coordinate click',
  'chat.tool.title.browserCuaDrag': 'Coordinate drag',
  'chat.tool.title.browserTabs': 'List tabs',
  'chat.tool.title.browserUserTabs': 'List user tabs',
  'chat.tool.title.browserClaim': 'Claim tab',
  'chat.tool.title.browserProfiles': 'List browser profiles',
  'chat.tool.title.browserClose': 'Close tab',
  'chat.tool.fallback': 'Tool call',
  'chat.tool.summary.lines': '{count} lines',
  'chat.tool.summary.items': '{count} items',
  'chat.tool.summary.nodes': '{count} nodes',
  'chat.tool.summary.files': '{count} files',
  'chat.tool.summary.matches': '{count} matches',
  'chat.tool.summary.results': '{count} results',
  'chat.tool.summary.outputLines': '{count} lines of output',
  'chat.tool.summary.createdLines': 'Created {count} lines',
  'chat.tool.summary.created': 'Created',
  'chat.tool.summary.replaced': ({ count }: Params) =>
    count === 1 ? '1 replacement' : `${String(count)} replacements`,
  'chat.tool.summary.exitCode': 'Exit code {code}',
  'chat.tool.summary.noOutput': 'No output',
  'chat.tool.summary.running': 'Running',
  'chat.tool.summary.stopped': 'Stopped',
  'chat.tool.summary.tasks': '{count} tasks',
  'chat.tool.summary.questions': ({ count }: Params) =>
    count === 1 ? '1 question' : `${String(count)} questions`,
  'chat.tool.summary.scheduleDaily': 'Daily at {time}',
  'chat.tool.summary.scheduleWeekly': ({ days, time }: Params) =>
    `${weekdayEn(String(days ?? ''))} ${String(time ?? '')}`
}
