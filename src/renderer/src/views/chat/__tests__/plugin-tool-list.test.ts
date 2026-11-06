/** 需求：加号菜单仅展示可运行插件当前声明的工具，不保留禁用后的旧目录。 */
import { describe, expect, it } from 'vitest'
import { parsePluginManifest } from '../../../../../shared/plugin/manifest'
import type { InstalledPlugin, PluginCatalog } from '../../../../../shared/plugin/state'
import { pluginToolList } from '../plugin-tool-list'

function plugin(id: string, enabled: boolean, status: InstalledPlugin['status'], withTools = true): InstalledPlugin {
  const [publisher, name] = id.split('.')
  const parsed = parsePluginManifest({
    name, publisher, version: '0.1.0', engines: { nextcowork: '>=0.3.1' }, main: 'dist/extension.js',
    contributes: { tools: withTools ? [{ name: 'read_export', title: '%tool.read%' }] : [] }
  })
  if (!parsed.ok) throw new Error(JSON.stringify(parsed.errors))
  return {
    id, manifest: parsed.manifest, status, enabled, scope: 'global', path: id,
    permissions: { required: [], optional: [], granted: [] }, diagnostics: [], unsupported: [],
    installedAt: 0, updatedAt: 0, statusBar: []
  }
}

describe('pluginToolList', () => {
  it('lists only enabled runnable plugins with tools, grouped by plugin', () => {
    const catalog: PluginCatalog = {
      hostVersion: '2.2.6',
      plugins: [
        plugin('acme.reader', true, 'idle'),
        plugin('acme.disabled', false, 'disabled'),
        plugin('acme.failed', true, 'error'),
        plugin('acme.empty', true, 'active', false)
      ]
    }
    expect(pluginToolList(catalog)).toEqual([{
      pluginId: 'acme.reader', displayName: 'reader',
      tools: [{ name: 'read_export', titleKey: 'plugin.acme.reader.tool.read' }]
    }])
  })
})
