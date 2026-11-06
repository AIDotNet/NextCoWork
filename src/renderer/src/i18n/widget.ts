/**
 * 内置可视化 widget 的文案。
 *
 * 只有一条,但仍然单独成文件:它属于 `visualize_*` 那条链路,而
 * `i18n/index.tsx` 已经三千多行(AGENTS §15.3「新代码别往里加」)。
 * 照 `usage.ts` / `git.ts` 的先例,域一独立就搬出来。
 *
 * ★ 这里**没有**"正在生成""加载中"这类句子:widget 生成期的提示词是
 * **模型给的**(`loading_messages` 参数,语言跟随用户),那是它的产物,
 * 不该由我们翻译 —— 翻译了就会与模型正在写的内容语言不一致。
 *
 * ★ 也**没有**失败文案:失败走的是工具结果本身(`OutputBlock` 渲染
 * `output.content`),那条路径已经有自己的文案体系。
 */

export const widgetZh = {
  /** iframe 的无障碍名字。模型给的 `title` 还没流到时的兜底 —— 空的无障碍名字会被读屏软件念成路径。 */
  'widget.untitled': '可视化内容',
  // ── 宿主能力(`WidgetFrame` 的 handleCall)──
  'widget.context.label': '随下一条消息附上:',
  'widget.context.remove': '不附上',
  'widget.cancel': '取消',
  'widget.action.title': '允许运行「{label}」?',
  'widget.action.body': '对话里的可视化请求执行下面这次工具调用。它是生成这张图时预先声明的;确认后本次对话内同样的调用不再询问。',
  'widget.action.run': '运行',
  'widget.url.title': '打开外部链接?',
  'widget.url.body': '对话里的可视化想在浏览器中打开:',
  'widget.url.open': '打开',
  'widget.complete.title': '允许这个可视化调用模型?',
  'widget.complete.body': '它会用当前对话的模型发起请求(不带工具、不进对话),会产生用量。允许后这张卡片不再询问。',
  'widget.complete.allow': '允许',
  'widget.copied': '已复制',
  'widget.saved': '已保存',
  'widget.drafted': '已填入输入框',
  'widget.toolbar.copySource': '复制源码',
  'widget.toolbar.reload': '重新渲染',
  'widget.submit.waiting': '等你在「{title}」里提交',
  'widget.submit.dismiss': '不提交了'
}

export const widgetEn = {
  'widget.untitled': 'Visualization',
  'widget.context.label': 'Sent with your next message:',
  'widget.context.remove': 'Do not attach',
  'widget.cancel': 'Cancel',
  'widget.action.title': 'Run “{label}”?',
  'widget.action.body': 'A visualization in this conversation wants to make the tool call below. It was declared when the visualization was created; once confirmed, the same call will not ask again in this conversation.',
  'widget.action.run': 'Run',
  'widget.url.title': 'Open external link?',
  'widget.url.body': 'A visualization in this conversation wants to open in your browser:',
  'widget.url.open': 'Open',
  'widget.complete.title': 'Allow this visualization to call the model?',
  'widget.complete.body': 'It will send requests with this conversation’s model (no tools, not added to the conversation), which uses tokens. Once allowed, this card will not ask again.',
  'widget.complete.allow': 'Allow',
  'widget.copied': 'Copied',
  'widget.saved': 'Saved',
  'widget.drafted': 'Added to the message box',
  'widget.toolbar.copySource': 'Copy source',
  'widget.toolbar.reload': 'Re-render',
  'widget.submit.waiting': 'Waiting for you to submit in “{title}”',
  'widget.submit.dismiss': 'Don’t submit'
}
