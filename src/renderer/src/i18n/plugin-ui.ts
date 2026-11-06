/**
 * 插件带进来的**界面**那一侧的文案:网页应用、交互弹窗、进度。
 *
 * 单独一个文件而不是往 `index.tsx` 那三千多行里塞 —— 照 `git.ts` / `ssh.ts` 的先例
 * (AGENTS.md §6.3)。
 *
 * ★ 这里的文案说的是**宿主**的那一半:「这个插件不在了」「插件想问你一句话」。
 * 插件自己的文案走 `plugin-messages.ts`,前缀 `plugin.<id>.`,两者不混。
 */

/**
 * 带参数的文案那一个入参类型。★ 必须显式标出来:这个文件没有 `Messages` 的
 * 上下文(它在 `index.tsx` 里),不标的话参数会被推断成 implicit any,
 * spread 进 `ZH` 时整张表都不再匹配 `Messages`。
 */
type Params = Record<string, string | number>

export const pluginUiZh = {
  // ── 网页应用 ──
  'pluginWebApp.unavailable': '这个网页应用打不开了',
  'pluginWebApp.unavailableHint': (p: Params): string =>
    `插件 ${String(p.plugin)} 已被卸载、禁用,或者装载失败。在扩展页里把它装回来或重新启用。`,
  'pluginWebApp.openFailed': (p: Params): string => `打不开 ${String(p.plugin)} 的页面`,
  'pluginWebApp.openedExternally': (p: Params): string => `${String(p.url)} 不在这个插件声明的域名里,已交给系统浏览器打开`,
  /*
    ★ 这一条是**降级提示**,不是错误:`open: "feature"`(独立外层 Tab)这一版还没
    实现,页面按内层 Tab 开了。不说的话,作者会以为自己的清单写错了 —— 而他没写错。
  */
  'pluginWebApp.featureFallback': '这个插件想开一个独立标签页,当前版本先在工作区里打开',

  // ── Agent 插件工具目录（仅展示，不更改工具授权） ──
  'pluginTools.list': '插件工具列表',
  'pluginTools.back': '返回',
  'pluginTools.hint': '已启用插件提供的 Agent 工具',

  // ── 插件要问你一句话 ──
  'pluginAsk.title': (p: Params): string => `${String(p.plugin)} 想问你`,
  'pluginAsk.confirm': '确定',
  'pluginAsk.cancel': '取消',
  'pluginAsk.submit': '提交',
  'pluginAsk.pickPlaceholder': '选一项',
  'pluginAsk.inputPlaceholder': '输入内容',
  /*
    ★ 这句解释「为什么会弹出这个框」。插件的弹窗是用户没发起过的界面事件,
    不说清是谁、为什么弹,它看起来就像一次故障。
  */
  'pluginAsk.from': (p: Params): string => `来自插件 ${String(p.plugin)}`,

  // ── 进度 ──
  'pluginProgress.running': (p: Params): string => `${String(p.plugin)} 正在处理`
}

export const pluginUiEn = {
  'pluginWebApp.unavailable': 'This web app can no longer be opened',
  'pluginWebApp.unavailableHint': (p: Params): string =>
    `The plugin ${String(p.plugin)} was uninstalled, disabled, or failed to load. Reinstall or re-enable it on the Extensions page.`,
  'pluginWebApp.openFailed': (p: Params): string => `Could not open the page from ${String(p.plugin)}`,
  'pluginWebApp.openedExternally': (p: Params): string =>
    `${String(p.url)} is outside the domains this plugin declared, so it opened in your system browser`,
  'pluginWebApp.featureFallback': 'This plugin asked for a standalone tab; for now it opens inside the workspace',

  'pluginTools.list': 'Plugin tools',
  'pluginTools.back': 'Back',
  'pluginTools.hint': 'Agent tools from enabled plugins',

  'pluginAsk.title': (p: Params): string => `${String(p.plugin)} is asking`,
  'pluginAsk.confirm': 'Confirm',
  'pluginAsk.cancel': 'Cancel',
  'pluginAsk.submit': 'Submit',
  'pluginAsk.pickPlaceholder': 'Pick one',
  'pluginAsk.inputPlaceholder': 'Type here',
  'pluginAsk.from': (p: Params): string => `From the plugin ${String(p.plugin)}`,

  'pluginProgress.running': (p: Params): string => `${String(p.plugin)} is working`
}
