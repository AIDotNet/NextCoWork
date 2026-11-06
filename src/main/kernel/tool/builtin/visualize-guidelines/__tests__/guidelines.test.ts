/**
 * 规范装配的单测。
 *
 * 两条最值钱的用例:
 * - **单模块装配结果必须短于工具输出的截断线**(否则规范会在中间断掉,
 *   而模型会照着前半截画出错的图);
 * - **至少存在一个两模块组合会超限** —— 它钉住的是"工具里那道长度检查
 *   为什么不是多余的"。哪天有人觉得那段代码多余删掉,这条会红。
 */
import { describe, expect, it } from 'vitest'
import { MAX_TOOL_OUTPUT_CHARS } from '../../../../../../shared/agent/message'
import {
  ART_AND_ILLUSTRATION,
  AVAILABLE_MODULES,
  CHARTS_CHART_JS,
  COLOR_PALETTE,
  CORE,
  DIAGRAM_TYPES,
  SVG_SETUP,
  UI_COMPONENTS,
  getGuidelines
} from '../index'

describe('AVAILABLE_MODULES', () => {
  it('每个模块都能装配出一段比 core 更长的规范', () => {
    for (const module of AVAILABLE_MODULES) {
      expect(getGuidelines([module]).length, module).toBeGreaterThan(CORE.length)
    }
  })

  it('顺序是模型看到的枚举顺序,改动要当成产品改动看', () => {
    expect([...AVAILABLE_MODULES]).toEqual(['components', 'diagram', 'mockup', 'interactive', 'chart', 'art'])
  })
})

describe('getGuidelines', () => {
  it('返回的正文以 core 开头、以换行结尾', () => {
    const text = getGuidelines(['diagram'])
    expect(text.startsWith(CORE)).toBe(true)
    expect(text.endsWith('\n')).toBe(true)
  })

  /**
   * ★ 去重是必须的,不是优化:一次请求两个模块时,共享的段落只能出现一次 ——
   * 否则模型会拿到两份一字不差的规范,而正文里满是"见上/见下"的交叉引用。
   */
  it('两个模块共享的段落只出现一次', () => {
    const text = getGuidelines(['chart', 'mockup'])
    expect(countOccurrences(text, UI_COMPONENTS)).toBe(1)
    expect(countOccurrences(text, COLOR_PALETTE)).toBe(1)
    // chart 独有的一段仍然在
    expect(text).toContain(CHARTS_CHART_JS)
    expect(text).not.toContain(DIAGRAM_TYPES)
  })

  it('重复请求同一个模块不会让正文变长', () => {
    expect(getGuidelines(['art', 'art', 'art'])).toBe(getGuidelines(['art']))
  })

  it('段落之间空两行 —— 正文里的标题排版依赖这个形状', () => {
    const text = getGuidelines(['mockup'])
    expect(text).toContain(`\n\n\n${UI_COMPONENTS}`)
  })

  /**
   * ★ 这条钉的是 `visualize.ts` 里那道长度检查的存在理由。
   * 单模块都安全(上一条),但 diagram + chart 装配出来会越过工具输出的截断线 ——
   * 被截断的表现是规范从中间断掉、末尾补一句"输出已截断",而模型不会觉得
   * 少了什么,它会照着前半截把图画出来,而且画得很有信心。
   */
  it('单模块都不超截断线', () => {
    for (const module of AVAILABLE_MODULES) {
      expect(getGuidelines([module]).length, module).toBeLessThanOrEqual(MAX_TOOL_OUTPUT_CHARS)
    }
  })

  it('diagram + chart 会超截断线 —— 所以工具必须明确失败而不是让它断掉', () => {
    expect(getGuidelines(['diagram', 'chart']).length).toBeGreaterThan(MAX_TOOL_OUTPUT_CHARS)
  })

  it('每个模块都带上了它该有的那几段', () => {
    expect(getGuidelines(['art'])).toContain(SVG_SETUP)
    expect(getGuidelines(['art'])).toContain(ART_AND_ILLUSTRATION)
    expect(getGuidelines(['diagram'])).toContain(DIAGRAM_TYPES)
  })
})

function countOccurrences(haystack: string, needle: string): number {
  return haystack.split(needle).length - 1
}
