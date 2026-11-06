/**
 * 插件贡献的 Skill —— 三处界面共用的一小组文案。
 *
 * ## 为什么单独一个文件
 *
 * 这几条 key 分属 `plugins.*` 和 `skills.*` 两个既有命名空间,按域归类的话该
 * 进 `index.tsx`。但那是一个 3900 多行、所有人都在改的热点文件(AGENTS.md
 * §12、§15.3),而这几条是**同一次需求**的产物:插件能自带 Skill 之后,
 * 市场卡片、插件详情、Skill 列表三处都要说明「这条是谁带来的」。
 * 放一起,下一个人改这件事时看到的是一组,不是散落在三千行里的六个点。
 *
 * ## 领域值不在这里
 *
 * skill 的**名字**、插件 id 都是领域值 —— 它们原样出现在界面上,只有周围的话
 * 翻译(AGENTS.md §6.5)。所以下面凡是要提到具体某个插件的地方,都是拿
 * `{plugin}` 把原值插进去。
 */
type Params = Record<string, string | number>

export const pluginSkillsZh = {
  // ── 市场:卡片与安装确认 ──
  'plugins.marketSkills': '自带 Skill',
  'plugins.marketSkillsHint': '装上后这些 Skill 会进入模型的可用清单,出现在你之后的每一轮对话里。可以在 Skill 页里单独关掉。',
  'plugins.marketPage': ({ page }: Params) => `第 ${page} 页`,
  'plugins.marketPreviousPage': '上一页',
  'plugins.marketNextPage': '下一页',

  // ── 已安装插件的详情页 ──
  'plugins.skills': '提供的 Skill',
  'plugins.skillsHint': '这个插件带来的 Skill。禁用插件,它们会一起从模型的清单里消失。',
  'plugins.skillInactive': '已关闭',
  'plugins.skillMissing': '这一条没有加载成功 —— 可能是缺少描述、与别的 Skill 重名,或者被你自己的同名 Skill 覆盖了。具体原因见 Skill 页的诊断。',

  // ── Skill 列表 ──
  'skills.fromPlugin': '来自插件',
  'skills.fromPluginHint': ({ plugin }: Params) => `由插件 ${String(plugin)} 提供,不能单独卸载。要去掉它,请在插件页里禁用或卸载该插件。`
}

export const pluginSkillsEn = {
  'plugins.marketSkills': 'Bundled Skills',
  'plugins.marketSkillsHint': 'Once installed, these Skills join the model’s catalog and are present in every later turn. You can switch them off individually on the Skills page.',
  'plugins.marketPage': ({ page }: Params) => `Page ${page}`,
  'plugins.marketPreviousPage': 'Previous',
  'plugins.marketNextPage': 'Next',

  'plugins.skills': 'Skills provided',
  'plugins.skillsHint': 'Skills this plugin brings along. Disable the plugin and they leave the model’s catalog with it.',
  'plugins.skillInactive': 'Off',
  'plugins.skillMissing': 'This one failed to load — it may be missing a description, clash with another Skill’s name, or be overridden by one of your own. The Skills page diagnostics say which.',

  'skills.fromPlugin': 'From a plugin',
  'skills.fromPluginHint': ({ plugin }: Params) => `Provided by the plugin ${String(plugin)}, so it cannot be uninstalled on its own. To remove it, disable or uninstall that plugin on the Plugins page.`
}
