/**
 * 需求:让全局工具单例里的**插件工具**每轮都与插件管理器的现状一致。
 *
 * ## 为什么需要它
 *
 * `runtime.getTools()` 是懒建的单例,插件工具经 `registerToolProvider` 进
 * `builtinTools()` —— 但 `builtinTools()` **只在单例首次构建时被读一次**。
 * 于是:
 *
 * - 之后才激活的插件(声明 `onTool:` 的那一类,本来就是按需唤醒)注册的工具,
 *   永远进不了单例 —— 症状是「插件装了、+ 菜单里列着,Agent 却说没有这个
 *   工具」,且全程零报错;
 * - 先前在的插件被禁用/卸载后,它的工具反而一直赖在单例里,模型还能调到。
 *
 * ## 不变式
 *
 * 调用之后,单例里 `source.kind === 'plugin'` 的工具**恰好**等于 `registrations`
 * (减去与非插件工具重名的那些)。非插件来源的工具一个都不碰。
 *
 * ## 故意不做的
 *
 * 不做增量 diff:插件工具最多几十个,整批撤下再注册的代价可以忽略,而增量
 * 版本要额外维护「哪个 id 属于哪个插件」的第二份账,正是会跟真源分叉的那种。
 */
import type { ToolRegistration, ToolRegistry } from './registry'

export function syncPluginTools(registry: ToolRegistry, registrations: readonly ToolRegistration[]): void {
  // ★ 先整批撤下,再注册:只追加的话,被禁用/卸载的插件工具会留在表里 ——
  //   模型照样能调到它,是一次静默越权。
  const stale = new Set<string>()
  for (const tool of registry.snapshot()) {
    if (tool.source.kind === 'plugin') stale.add(tool.source.pluginId)
  }
  for (const pluginId of stale) registry.unregisterBySource({ kind: 'plugin', pluginId })

  // ★ 与已在表里的(内置/MCP/Skill)工具重名的一律跳过,与 `builtinTools()` 的先来
  //   先得同一立场:注册表按 internalId **幂等替换**,放行就是一条让插件把真
  //   Bash 换成自己实现的提权路径。
  const taken = new Set(registry.snapshot().map((tool) => tool.internalId))
  for (const registration of registrations) {
    if (registration.source.kind !== 'plugin') continue
    if (taken.has(registration.internalId)) continue
    taken.add(registration.internalId)
    registry.register(registration)
  }
}
