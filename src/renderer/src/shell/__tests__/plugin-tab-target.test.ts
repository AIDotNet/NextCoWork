/**
 * `placePluginTab` 的测试 —— 一条 `plugins:openTab` 报文落到哪种 Tab。
 *
 * 需求:插件终端(`tabs.openTerminal`)的 terminalId 必须**原样**进 Tab 的 ref,
 * 换 id 的症状是 create 起一个裸 shell、插件注入的 env 静默丢失,零报错。
 * 这条规则用一条测试钉住,谁改了落 Tab 的路径谁就看得见红灯。
 */
import { describe, expect, it } from 'vitest'
import { placePluginTab } from '../plugin-tab-target'
import type { PluginTabTarget } from '../../../../shared/plugin/ui-request'

const terminalTarget: Extract<PluginTabTarget, { kind: 'terminal' }> = {
  kind: 'terminal',
  terminalId: 'spec-1',
  workspaceId: 'ws-1',
  title: '%cmd.launch%'
}

describe('placePluginTab · 插件终端', () => {
  it('落在主区,terminalId 原样透传给 Tab 的 init', () => {
    const placement = placePluginTab(terminalTarget, 'acme.claude-code', 'Claude Code')
    expect(placement).toEqual({
      kind: 'terminal',
      pane: 'main',
      init: { title: 'Claude Code', terminalId: 'spec-1' }
    })
  })

  it('title 缺省时 init 不带 title,Tab 落「终端」默认文案', () => {
    const placement = placePluginTab({ ...terminalTarget, title: undefined }, 'acme.codex', undefined)
    expect(placement.init.title).toBeUndefined()
    expect(placement.init.terminalId).toBe('spec-1')
  })

  it('webapp 的落格不受 terminal 分支影响(既有行为不回归)', () => {
    const placement = placePluginTab(
      { kind: 'webapp', webAppId: 'home', url: 'https://example.com/', title: '%app.home%', open: 'tab' },
      'ncw.bilibili',
      '哔哩哔哩'
    )
    expect(placement).toEqual({
      kind: 'webapp',
      pane: 'main',
      init: { title: '哔哩哔哩', url: 'https://example.com/', pluginId: 'ncw.bilibili', webAppId: 'home' }
    })
  })
})
