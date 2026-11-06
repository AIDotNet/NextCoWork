/**
 * 「认得字段名、但这一版不实现」的贡献点清单。
 *
 * ## 为什么需要这么一个文件
 *
 * 计划里被推迟的那几样(聊天内嵌渲染、调试适配器、交互式终端)**必须在
 * 插件运行期给出可理解的失败**,而不是静默不生效。
 *
 * 静默不生效是插件系统最糟的失败方式:作者写了 `contributes.chatRenderers`,
 * 装上、启用、什么都没发生,而日志里一个字都没有。他会去怀疑自己的打包、
 * 自己的清单格式、自己的 JSON 有没有写错 —— 唯独不会想到「宿主根本没实现」。
 *
 * 所以这里的每一条都会在装载时变成插件详情页上的**一条诊断**,
 * 照 `kernel/skill/load.ts` 的「永不 throw、一切失败变 diagnostics」。
 *
 * ## 这份清单随版本**收缩**
 *
 * 实现一个就删一行。它不是「将来要做的事」的清单(那在计划文档里),
 * 是「现在会让作者困惑的事」的清单。
 */

export interface UnsupportedContribution {
  /** `contributes` 下的键名 */
  key: string
  /** 给作者看的原因 —— 说清「为什么没有」,而不只是「没有」 */
  reason: string
}

export const UNSUPPORTED_CONTRIBUTIONS: readonly UnsupportedContribution[] = [
  {
    key: 'chatRenderers',
    reason: 'Inline chat renderers are not implemented yet. Contribute a view or a custom editor instead.'
  },
  {
    key: 'debuggers',
    reason: 'Debug adapters are out of scope for this host. There is no planned date.'
  },
  {
    key: 'taskDefinitions',
    reason: 'Task providers are not implemented. Use contributes.tools plus process.exec.'
  },
  {
    key: 'notebooks',
    reason: 'Notebook contributions are out of scope for this host.'
  },
  {
    key: 'terminals',
    reason: 'Interactive terminals are not implemented: their input cannot pass the approval chain. Use process.exec for non-interactive commands.'
  },
  {
    key: 'languages',
    reason: 'Language contributions are not implemented; onLanguage activation does not exist either.'
  }
]

const BY_KEY = new Map(UNSUPPORTED_CONTRIBUTIONS.map((entry) => [entry.key, entry]))

/**
 * 「清单读得懂、文件也装进来了,但宿主这一版**还没把它接上**」的贡献点。
 *
 * ## 为什么要和上面那张表分开
 *
 * `UNSUPPORTED_CONTRIBUTIONS` 里的键宿主**根本不认**(解析时就进 `unsupported`);
 * 这里这几个是**认得、校验过、文件也存在**的 —— 差的只是最后一段接线。
 * 两者给作者的下一步动作不同:上面那张是「别写了」,这张是「写了没白写,
 * 但现在还不生效」。
 *
 * ★ 有这张表,是因为**静默不生效**是插件系统最糟的失败方式(见文件头)。
 * `contributes.skills` 曾经就是这样:装得干干净净,扫描器从不读它,
 * 而作者没有任何线索 —— 这张表当初就是为它建的。
 * (它现在已经接上了,所以下面那一行删了。原委见列表里的说明。)
 *
 * ★ 这张表随实现**收缩**:接上一个删一行。
 */
export const ACCEPTED_BUT_INACTIVE: readonly UnsupportedContribution[] = [
  /*
    `skills` 这一行已删。插件包里的 `skills/<name>/` 现在会在插件**启用**时
    以绝对路径进 Skill 扫描器,禁用即撤 —— 接线在 `ipc/plugins.ts` 的
    `setPluginSkillRootsProvider`,读取在 `kernel/skill/load.ts` 的 `scanPluginRoots`。
    上面那段文件头**保留**它作为这张表存在理由的证据,别一起删掉。
  */
  {
    key: 'agents',
    reason: 'Bundled sub-agents are validated and installed, but the agent scanner does not read plugin packages yet.'
  },
  {
    key: 'modes',
    reason: 'Bundled modes are validated and installed, but the mode scanner does not read plugin packages yet.'
  },
  {
    key: 'themes',
    reason: 'Bundled themes are validated and installed, but the appearance settings do not offer plugin themes yet.'
  },
  {
    /*
      ★ 这一条的 key 不是 `contributes` 下的字段名,而是一个**取值**:
      `views[].location` 为 sidebar / panel 的那些。挂在这张表里是因为症状一样 ——
      作者写了它、包也装干净了,而界面上没有任何地方能打开它。
    */
    key: 'views.location',
    reason: 'Sidebar and panel views are parsed but cannot be opened yet: a plugin view that is not bound to a file has no tab kind. Use contributes.customEditors (location "editor") or contributes.webApps for now.'
  },
  {
    key: 'slashCommands',
    reason: 'Slash commands are parsed and validated, but the composer does not offer plugin commands yet. The command itself still works from the command palette and from contributes.menus.'
  },
  {
    /*
      文档引擎(办公插件的 LibreOffice 承载)。原先原生安装器、helper 进程与视图通道
      都没接上；现在宿主会话/RPC 与 helper 已接通，但视图会话通道仍未实现。
      保留诊断的理由不变：作者装上引擎插件后打开 .docx 仍会落回普通编辑器，不能零提示。
      视图会话通道接上之后删这一行。
    */
    key: 'documentEngines',
    reason: 'Document session RPC and native helpers are available, but the editor view session channel is not implemented yet. Editors bound to a documentEngine still open through the regular custom-editor path.'
  }
]

/**
 * 这份清单里有哪些「接受了但还没生效」的贡献点 —— 装载时逐条转成诊断。
 *
 * 返回的是 `[字段路径, 说明]`,由调用方拼成 `PluginDiagnostic`。
 */
export function inactiveContributions(
  counts: Readonly<Record<string, number>>
): { path: string; message: string }[] {
  return ACCEPTED_BUT_INACTIVE
    .filter((entry) => (counts[entry.key] ?? 0) > 0)
    .map((entry) => ({ path: `contributes.${entry.key}`, message: entry.reason }))
}

/**
 * 认不出的贡献点 → 一条给作者看的诊断文本。
 *
 * ★ 连**这份表里也没有**的键同样要出诊断(只是措辞不同):那多半是一个拼写
 * 错误,而拼错 `contirbutes.commands` 的症状和「没实现」一模一样。
 */
export function explainUnsupported(key: string): string {
  const known = BY_KEY.get(key)
  if (known !== undefined) return known.reason
  return `Unknown contribution point "${key}". It is ignored. Check the spelling against the plugin API docs.`
}
