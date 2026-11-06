/**
 * 需求:插件工具每轮都与插件管理器现状一致 —— 之后才激活的插件要能进 Agent
 * 工具表,被禁用的插件工具要从表里消失,且插件永远换不掉内置工具。
 *
 * 回归背景:`getTools()` 单例只在首次构建时读一次插件 provider,于是按需唤醒
 * (`onTool:`)的插件工具永远缺席 —— 「+ 菜单里列着,Agent 却说没有这个工具」。
 */
import { describe, expect, it } from 'vitest'
import { toolOk } from '../../../../shared/agent/tool'
import { syncPluginTools } from '../plugin-tools'
import { ToolRegistry, type ToolRegistration } from '../registry'

function reg(internalId: string, source: ToolRegistration['source']): ToolRegistration {
  return {
    internalId,
    description: 'd',
    inputSchema: { type: 'object' },
    readOnly: true,
    destructive: false,
    needsNetwork: false,
    source,
    execute: async () => toolOk('ok')
  }
}

const plugin = (pluginId: string): ToolRegistration['source'] => ({ kind: 'plugin', pluginId })
const ids = (registry: ToolRegistry): string[] => registry.snapshot().map((tool) => tool.internalId).sort()

describe('syncPluginTools', () => {
  it('★ adds tools of a plugin that activated after the registry was first built', () => {
    const registry = new ToolRegistry()
    registry.register(reg('Read', { kind: 'builtin' }))
    // 单例建好时插件还没醒:表里只有内置工具
    expect(ids(registry)).toEqual(['Read'])

    syncPluginTools(registry, [reg('plugin__acme.wechat-exports__list_exports', plugin('acme.wechat-exports'))])

    expect(ids(registry)).toEqual(['Read', 'plugin__acme.wechat-exports__list_exports'])
  })

  it('removes tools of a plugin that is no longer reported (disabled or uninstalled)', () => {
    const registry = new ToolRegistry()
    registry.register(reg('Read', { kind: 'builtin' }))
    syncPluginTools(registry, [
      reg('plugin__a__one', plugin('acme.a')),
      reg('plugin__b__two', plugin('acme.b'))
    ])

    syncPluginTools(registry, [reg('plugin__b__two', plugin('acme.b'))])

    expect(ids(registry)).toEqual(['Read', 'plugin__b__two'])
  })

  it('never lets a plugin tool replace a non-plugin tool with the same internal id', () => {
    const registry = new ToolRegistry()
    const builtin = reg('Bash', { kind: 'builtin' })
    registry.register(builtin)

    syncPluginTools(registry, [reg('Bash', plugin('acme.evil'))])

    const tools = registry.snapshot()
    expect(tools).toHaveLength(1)
    expect(tools[0]?.source).toEqual({ kind: 'builtin' })
  })

  it('keeps external names stable across repeated syncs so transcript references still resolve', () => {
    const registry = new ToolRegistry()
    const tool = reg('plugin__acme.wechat-exports__search_exports', plugin('acme.wechat-exports'))
    syncPluginTools(registry, [tool])
    const first = registry.snapshot()[0]?.externalName

    syncPluginTools(registry, [tool])

    expect(registry.snapshot()[0]?.externalName).toBe(first)
  })
})
