/**
 * 「客户端资产不可用」的技能不得被启用。
 *
 * ★ 闸门必须在**主进程**。渲染层有三个入口能打开这个开关(列表行 Toggle、卡片网格 Toggle、
 * 详情弹窗「使用」),历史上只有第一个做了判断 —— 另外两个能把一条永远不会生效的 id 写进
 * `activeSkillIds`,界面显示「已启用」,而 `runtime.ts` 的 `activeSkills()` 和
 * `tool/builtin/skill.ts` 都会把它静默排除。界面在撒谎比功能缺失更糟。
 */
import { describe, expect, it, vi } from 'vitest'
import type { Workspace } from '../../../shared/domain/workspace'

const mocks = vi.hoisted(() => ({
  scanned: [] as Array<{ id: string; name?: string; scope?: string; source?: { path: string }; unavailableReason?: string }>,
  workspace: undefined as Workspace | undefined,
  put: vi.fn(),
  emit: vi.fn()
}))

vi.mock('electron', () => ({ dialog: {} }))
vi.mock('../../runtime', () => ({
  refreshSkills: async () => mocks.scanned,
  getEnvironments: () => { throw new Error('unused') },
  getHost: () => { throw new Error('unused') },
  getWorkspaceEnvironment: () => { throw new Error('unused') }
}))
vi.mock('../../state/store', () => ({
  store: {
    getWorkspace: () => mocks.workspace,
    listWorkspaces: () => [],
    putWorkspace: mocks.put
  }
}))
vi.mock('../../window/registry', () => ({ windows: { emitToAll: mocks.emit } }))

const { activateWrittenSkills, setSkillWorkspaceActive } = await import('../skills')

function workspace(activeSkillIds: string[]): Workspace {
  return { id: 'workspace', name: 'w', rootPath: '/w', createdAt: 0, updatedAt: 0,
    settings: { activeSkillIds, skillSelectionMode: 'explicit' } } as unknown as Workspace
}

describe('技能的工作区开关', () => {
  it('refuses to enable a Skill whose client assets are unavailable', async () => {
    mocks.scanned = [{ id: 'needs-client', unavailableReason: 'client-assets' }]
    mocks.workspace = workspace([])
    mocks.put.mockClear()
    await expect(setSkillWorkspaceActive({ skillId: 'needs-client', workspaceId: 'workspace', active: true }))
      .rejects.toThrow('skills.clientAssetsUnavailable')
    expect(mocks.put, '被拒绝时不得写进 activeSkillIds').not.toHaveBeenCalled()
  })

  it('still allows turning such a Skill off', async () => {
    mocks.scanned = [{ id: 'needs-client', unavailableReason: 'client-assets' }]
    mocks.workspace = workspace(['needs-client'])
    mocks.put.mockClear()
    // 关掉必须始终可行 —— 否则历史上误开的那条永远摘不掉
    await setSkillWorkspaceActive({ skillId: 'needs-client', workspaceId: 'workspace', active: false })
    expect(mocks.put).toHaveBeenCalledTimes(1)
    expect((mocks.put.mock.calls[0]![0] as Workspace).settings.activeSkillIds).toEqual([])
  })

  it('leaves an available Skill alone', async () => {
    mocks.scanned = [{ id: 'plain' }]
    mocks.workspace = workspace([])
    mocks.put.mockClear()
    await setSkillWorkspaceActive({ skillId: 'plain', workspaceId: 'workspace', active: true })
    expect((mocks.put.mock.calls[0]![0] as Workspace).settings.activeSkillIds).toEqual(['plain'])
  })

  it('activates a project Skill by its written directory, not only frontmatter name', async () => {
    mocks.scanned = [{
      id: 'pricing-rules', name: 'pricing-rules', scope: 'project',
      source: { path: '/repo/.next-cowork/skills/pricing-rule/SKILL.md' }
    }]
    mocks.workspace = workspace(['existing'])
    mocks.put.mockClear()
    await activateWrittenSkills({ workspaceId: 'workspace', names: ['pricing-rule'] })
    expect((mocks.put.mock.calls[0]![0] as Workspace).settings.activeSkillIds).toEqual(['existing', 'pricing-rules'])
    expect(mocks.emit).toHaveBeenCalledWith('workspace:changed', { workspaces: [] })
  })

  it('does not write settings when all Skills are implicitly active', async () => {
    mocks.scanned = [{ id: 'pricing-rules', name: 'pricing-rules', scope: 'project', source: { path: '/repo/.next-cowork/skills/pricing-rule/SKILL.md' } }]
    mocks.workspace = { ...workspace([]), settings: { ...workspace([]).settings, skillSelectionMode: 'all' } }
    mocks.put.mockClear()
    await activateWrittenSkills({ workspaceId: 'workspace', names: ['pricing-rule'] })
    expect(mocks.put).not.toHaveBeenCalled()
  })
})
