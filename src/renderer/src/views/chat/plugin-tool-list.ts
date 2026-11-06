/**
 * 需求：加号菜单只列出当前已启用、可运行插件声明的 Agent 工具。
 * 数据来自 catalog 的权威投影，不从聊天历史推断「曾用过什么工具」；
 * 后者会遗漏尚未调用但当前可用的工具，也会留下已禁用插件的旧条目。
 */
import type { PluginCatalog } from '../../../../shared/plugin/state'
import { isRunnable } from '../../../../shared/plugin/state'

export interface ListedPluginTools {
  pluginId: string
  displayName: string
  tools: { name: string; titleKey: string }[]
}

export function pluginToolList(catalog: PluginCatalog): ListedPluginTools[] {
  return catalog.plugins
    .filter((plugin) => isRunnable(plugin) && plugin.manifest.contributes.tools.length > 0)
    .map((plugin) => ({
      pluginId: plugin.id,
      displayName: plugin.manifest.displayName,
      tools: plugin.manifest.contributes.tools.map((tool) => ({
        name: tool.name,
        titleKey: `plugin.${plugin.id}.${tool.title.slice(1, -1)}`
      }))
    }))
    .sort((a, b) => a.pluginId.localeCompare(b.pluginId))
}
