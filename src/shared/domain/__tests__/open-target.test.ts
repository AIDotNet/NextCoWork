/**
 * 文件树右键菜单「在 X 中打开」与「打开方式 ›」的取舍规则。
 *
 * 这几条都不会以报错的形式坏掉,只会表现为菜单第一行悄悄变成别的程序、
 * 或者目录上出现一个会把 IDE 工作区换掉的编辑器 —— 所以写成断言钉住。
 */
import { describe, expect, it } from 'vitest'
import {
  DEFAULT_APP_TARGET_ID,
  REVEAL_TARGET_ID,
  TERMINAL_TARGET_ID,
  isOpenTargetPreference,
  pickPrimaryTarget,
  submenuTargets,
  targetsForEntry,
  type OpenTarget
} from '../open-target'

const TARGETS: OpenTarget[] = [
  { id: REVEAL_TARGET_ID, label: '', icon: 'file-manager' },
  { id: DEFAULT_APP_TARGET_ID, label: '', icon: 'default-app' },
  { id: TERMINAL_TARGET_ID, label: '', icon: 'terminal' },
  { id: 'vscode', label: 'Visual Studio Code', icon: 'vscode' },
  { id: 'zed', label: 'Zed', icon: 'zed' }
]

const ids = (list: readonly OpenTarget[]): string[] => list.map((target) => target.id)

describe('targetsForEntry', () => {
  it('目录上只留文件管理器与终端 —— 编辑器和默认应用都不列', () => {
    expect(ids(targetsForEntry(TARGETS, true))).toEqual([REVEAL_TARGET_ID, TERMINAL_TARGET_ID])
  })

  it('文件上原样全部列出', () => {
    expect(ids(targetsForEntry(TARGETS, false))).toEqual(ids(TARGETS))
  })
})

describe('pickPrimaryTarget', () => {
  it('用户在设置里指定了且本机有 → 用它', () => {
    expect(pickPrimaryTarget(TARGETS, 'zed', false)?.id).toBe('zed')
  })

  it('没指定时落到第一个探测到的编辑器,而不是文件管理器', () => {
    expect(pickPrimaryTarget(TARGETS, '', false)?.id).toBe('vscode')
  })

  it('★ 指定的那个已卸载时退回落点,而不是让第一行消失', () => {
    expect(pickPrimaryTarget(TARGETS, 'rider', false)?.id).toBe('vscode')
  })

  it('一台编辑器都没有时退到系统默认应用', () => {
    const generic = TARGETS.filter((target) => target.label === '')
    expect(pickPrimaryTarget(generic, '', false)?.id).toBe(DEFAULT_APP_TARGET_ID)
  })

  it('★ 目录上即使设置里选的是编辑器,第一行也是文件管理器', () => {
    expect(pickPrimaryTarget(TARGETS, 'zed', true)?.id).toBe(REVEAL_TARGET_ID)
    expect(pickPrimaryTarget(TARGETS, TERMINAL_TARGET_ID, true)?.id).toBe(TERMINAL_TARGET_ID)
  })

  it('探测结果为空时返回 null,调用方据此不画这一行', () => {
    expect(pickPrimaryTarget([], 'zed', false)).toBeNull()
  })
})

describe('submenuTargets', () => {
  it('顺序照参考截图:默认那个打头,然后默认应用 / 文件管理器 / 终端,最后其余编辑器', () => {
    const primary = pickPrimaryTarget(TARGETS, 'zed', false)
    expect(ids(submenuTargets(TARGETS, primary))).toEqual([
      'zed', DEFAULT_APP_TARGET_ID, REVEAL_TARGET_ID, TERMINAL_TARGET_ID, 'vscode'
    ])
  })

  it('默认的那一个不会在子菜单里出现两次', () => {
    const primary = pickPrimaryTarget(TARGETS, REVEAL_TARGET_ID, false)
    const list = ids(submenuTargets(TARGETS, primary))
    expect(list.filter((id) => id === REVEAL_TARGET_ID)).toHaveLength(1)
    expect(list).toHaveLength(TARGETS.length)
  })
})

describe('isOpenTargetPreference', () => {
  it('空串(自动)与 id 形状的短串放行', () => {
    for (const value of ['', 'vscode', 'default-app', 'androidstudio']) expect(isOpenTargetPreference(value)).toBe(true)
  })

  it('非字符串、带路径或空白、超长的一律拒', () => {
    for (const value of [null, 42, {}, '/Applications/Zed.app', 'VS Code', 'a'.repeat(65)]) {
      expect(isOpenTargetPreference(value)).toBe(false)
    }
  })
})
