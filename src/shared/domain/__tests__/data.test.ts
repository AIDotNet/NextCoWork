import { describe, expect, it } from 'vitest'
import {
  DATA_EXPORT_TYPE,
  DATA_EXPORT_VERSION,
  cutoffForAge,
  dataMergeDecision,
  isDataExport
} from '../data'
import { DEFAULT_SETTINGS } from '../settings'

function minimalExport(): Record<string, unknown> {
  return {
    type: DATA_EXPORT_TYPE,
    version: DATA_EXPORT_VERSION,
    exportedAt: '2026-09-05T00:00:00.000Z',
    settings: structuredClone(DEFAULT_SETTINGS),
    workspaces: [],
    sessions: [],
    providers: [],
    aliases: [],
    mcpServers: [],
    searchProviders: [],
    disabledSkillIds: []
  }
}

describe('dataMergeDecision', () => {
  it('本地不存在时直接新增', () => {
    expect(dataMergeDecision(undefined, { id: 'new' })).toBe('new')
  })

  it('双方时间都有效且导入更新时覆盖', () => {
    expect(dataMergeDecision({ updatedAt: 10 }, { updatedAt: 11 })).toBe('overwrite')
  })

  it.each([
    [{ updatedAt: 10 }, { updatedAt: 10 }],
    [{ updatedAt: 11 }, { updatedAt: 10 }],
    [{}, { updatedAt: 10 }],
    [{ updatedAt: 10 }, {}],
    [{ updatedAt: Number.NaN }, { updatedAt: 20 }],
    [{ updatedAt: 10 }, { updatedAt: Number.POSITIVE_INFINITY }]
  ])('相等、较旧、缺失或非法时间都保留本地 %#', (local, incoming) => {
    expect(dataMergeDecision(local, incoming)).toBe('skip')
  })

  it('规则对别名及各种配置记录保持一致', () => {
    const records = [
      { providerId: 'p', alias: 'm', updatedAt: 2 },
      { id: 'provider', updatedAt: 2 },
      { id: 'mcp', updatedAt: 2 },
      { id: 'search', updatedAt: 2 },
      { id: 'workspace', updatedAt: 2 }
    ]
    for (const incoming of records) {
      expect(dataMergeDecision({ ...incoming, updatedAt: 1 }, incoming)).toBe('overwrite')
      expect(dataMergeDecision({ ...incoming, updatedAt: 3 }, incoming)).toBe('skip')
    }
  })
})

describe('cutoffForAge', () => {
  it('15d 按日历日回退 15 天并保留时分秒', () => {
    const now = new Date(2026, 8, 5, 13, 14, 15, 123).getTime()
    expect(new Date(cutoffForAge(now, '15d'))).toEqual(new Date(2026, 7, 21, 13, 14, 15, 123))
  })

  it('7d 按日历日回退 7 天并保留时分秒', () => {
    const now = new Date(2026, 8, 5, 13, 14, 15, 123).getTime()
    expect(new Date(cutoffForAge(now, '7d'))).toEqual(new Date(2026, 7, 29, 13, 14, 15, 123))
  })

  it.each([1, 3, 6, 12] as const)('按 %s 个日历月计算并保留时分秒', (months) => {
    const now = new Date(2026, 8, 5, 13, 14, 15, 123).getTime()
    const expected = new Date(2026, 8 - months, 5, 13, 14, 15, 123).getTime()
    expect(cutoffForAge(now, months)).toBe(expected)
  })

  it('月末会夹到目标月最后一天，不会溢出并多删几天', () => {
    const may31 = new Date(2025, 4, 31, 8, 9, 10, 11).getTime()
    expect(new Date(cutoffForAge(may31, 3))).toEqual(new Date(2025, 1, 28, 8, 9, 10, 11))

    const leapMay31 = new Date(2024, 4, 31, 8, 9, 10, 11).getTime()
    expect(new Date(cutoffForAge(leapMay31, 3))).toEqual(new Date(2024, 1, 29, 8, 9, 10, 11))
  })
})

describe('isDataExport', () => {
  it('accepts valid model protocol overrides and rejects unknown values', () => {
    const valid = minimalExport()
    valid.aliases = [{
      alias: 'm', providerId: 'p', upstreamModel: 'm', protocolOverride: 'anthropic',
      capabilities: { tools: true, vision: false, thinking: false, caching: false },
      contextWindow: 1000, maxOutputTokens: 100
    }]
    expect(isDataExport(valid)).toBe(true)
    const invalid = structuredClone(valid)
    ;((invalid.aliases as unknown[])[0] as Record<string, unknown>).protocolOverride = 'unknown'
    expect(isDataExport(invalid)).toBe(false)
  })
  it('接受当前格式和缺少 data 设置块的旧格式', () => {
    const current = minimalExport()
    expect(isDataExport(current)).toBe(true)

    const legacy = minimalExport()
    const settings = legacy.settings as Record<string, unknown>
    delete settings.data
    expect(isDataExport(legacy)).toBe(true)
  })

  /**
   * `shell` 也是后加的可选项(「执行 Shell」),改动**之前**导出的备份里都没有它。
   * 校验器把它当必填的话,每一份旧存档都会在导入时被整份拒掉;而它带了却是个
   * 认不出来的取值,则相反:那条壳名最终会被合并函数丢掉,所以宁可在这里就拒。
   */
  it('接受缺少 shell 的旧存档,认不出来的 shell 仍然拒掉', () => {
    const legacy = minimalExport()
    delete (legacy.settings as Record<string, unknown>).shell
    expect(isDataExport(legacy)).toBe(true)

    const known = minimalExport()
    ;(known.settings as Record<string, unknown>).shell = 'zsh'
    expect(isDataExport(known)).toBe(true)

    const unknown = minimalExport()
    ;(unknown.settings as Record<string, unknown>).shell = 'tcsh'
    expect(isDataExport(unknown)).toBe(false)

    const wrongType = minimalExport()
    ;(wrongType.settings as Record<string, unknown>).shell = 42
    expect(isDataExport(wrongType)).toBe(false)
  })

  /**
   * 存量存档里**一个 `*ProviderId` 都没有** —— 那三对字段是后加的。
   * 校验器把它们当必填的话,每一份旧存档都会在导入时被整份拒绝。
   */
  it('接受不带任何模型供应商字段的旧存档', () => {
    const old = minimalExport()
    old.workspaces = [{
      id: 'w', name: 'A', rootPath: '/a', settings: {
        permissionMode: 'auto', defaultModel: 'gpt-5.5', defaultMode: 'normal',
        defaultThinking: 'auto', webSearch: true, activeSkillIds: []
      }, createdAt: 1, lastOpenedAt: 1
    }]
    expect(isDataExport(old)).toBe(true)
  })

  /**
   * `maxContext`(最大上下文开关)是后加的可选项 —— 改动**之前**导出的每一份备份里
   * 都没有它。校验器那行若写成 `isBoolean` 而不是 `optionalBoolean`,整份 DataExport
   * 会在导入时被拒(isWorkspaceSettings → isWorkspace → 全份失败),而用户看到的只是
   * 一句"文件格式不对"。这条用例是 CI 里唯一挡得住那次改写的东西。
   */
  it('接受缺少 maxContext 的旧工作区,带了但类型不对仍然拒绝', () => {
    const legacy = minimalExport()
    legacy.workspaces = [{
      id: 'w', name: 'A', rootPath: '/a', settings: {
        permissionMode: 'auto', defaultModel: 'gpt-5.5', defaultMode: 'normal',
        defaultThinking: 'auto', webSearch: true, activeSkillIds: []
      }, createdAt: 1, lastOpenedAt: 1
    }]
    expect(isDataExport(legacy)).toBe(true)

    const settingsOf = (e: Record<string, unknown>): Record<string, unknown> =>
      ((e.workspaces as unknown[])[0] as Record<string, unknown>).settings as Record<string, unknown>

    const enabled = structuredClone(legacy)
    settingsOf(enabled).maxContext = true
    expect(isDataExport(enabled)).toBe(true)

    const wrongType = structuredClone(legacy)
    settingsOf(wrongType).maxContext = 'yes'
    expect(isDataExport(wrongType)).toBe(false)
  })

  /**
   * 目标判定那一对(`goalEvaluatorModel` / `…ProviderId`)和 `modelProposedGoals`
   * 都是后加的 —— 改动**之前**导出的每一份备份里都没有它们,校验器把它们当必填的
   * 话,每一份旧存档都会在导入时被整份拒绝(用户只看到一句「文件格式不对」)。
   */
  it('接受缺少目标判定设置的旧存档,带了但类型/取值不对仍然拒绝', () => {
    const settingsOf = (e: Record<string, unknown>): Record<string, unknown> =>
      e.settings as Record<string, unknown>

    const legacy = minimalExport()
    delete settingsOf(legacy).goalEvaluatorModel
    delete settingsOf(legacy).modelProposedGoals
    expect(isDataExport(legacy)).toBe(true)

    const badModel = minimalExport()
    settingsOf(badModel).goalEvaluatorModel = 42
    expect(isDataExport(badModel)).toBe(false)

    const badProvider = minimalExport()
    settingsOf(badProvider).goalEvaluatorModelProviderId = 7
    expect(isDataExport(badProvider)).toBe(false)

    const badMode = minimalExport()
    settingsOf(badMode).modelProposedGoals = 'sometimes'
    expect(isDataExport(badMode)).toBe(false)
  })

  it('带了但类型不对的一律拒掉,而不是存进去等以后炸', () => {
    const bad = minimalExport()
    ;(bad.settings as Record<string, unknown>).defaultModelProviderId = 123
    expect(isDataExport(bad)).toBe(false)

    const badWorkspace = minimalExport()
    badWorkspace.workspaces = [{
      id: 'w', name: 'A', rootPath: '/a', settings: {
        permissionMode: 'auto', defaultModel: 'gpt-5.5', defaultModelProviderId: 7,
        defaultMode: 'normal', defaultThinking: 'auto', webSearch: true, activeSkillIds: []
      }, createdAt: 1, lastOpenedAt: 1
    }]
    expect(isDataExport(badWorkspace)).toBe(false)
  })

  it('拒绝未来版本以外的结构损坏、重复主键与空凭证引用', () => {
    const duplicate = minimalExport()
    duplicate.workspaces = [
      {
        id: 'w', name: 'A', rootPath: '/a', settings: {
          permissionMode: 'auto', defaultModel: '', defaultMode: 'normal',
          defaultThinking: 'auto', webSearch: true, activeSkillIds: []
        }, createdAt: 1, lastOpenedAt: 1
      },
      {
        id: 'w', name: 'B', rootPath: '/b', settings: {
          permissionMode: 'auto', defaultModel: '', defaultMode: 'normal',
          defaultThinking: 'auto', webSearch: true, activeSkillIds: []
        }, createdAt: 2, lastOpenedAt: 2
      }
    ]
    expect(isDataExport(duplicate)).toBe(false)

    const emptyRef = minimalExport()
    emptyRef.providers = [{
      id: 'p',
      name: 'P',
      protocol: 'anthropic',
      baseUrl: 'https://example.invalid',
      credentialRef: '',
      priority: 0,
      enabled: true
    }]
    expect(isDataExport(emptyRef)).toBe(false)

    expect(isDataExport({ ...minimalExport(), sessions: 'not-an-array' })).toBe(false)
  })
})
