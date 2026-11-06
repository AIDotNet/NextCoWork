/**
 * `visualize_read_me` 的模块表与装配 —— 「模型按需加载哪几段设计规范」的唯一出处。
 *
 * 需求:整套规范有 7 万字,一次性塞进系统提示词等于每一轮都为它付一遍 token。
 * 改成「模型先说它要画什么,我们再返回那几段」,于是提示词保持精简,
 * 而它真动手时手里仍然是完整的规范。这是 Claude 的 read_me 那个设计里
 * 唯一真正必要的一环,其余(工具名、参数名)都只是外壳。
 *
 * ★ **去重是必须的,不是优化。** `interactive` / `chart` / `mockup` 三个模块
 * 共享 UI 组件与配色两段,`art` / `diagram` 共享 SVG setup;一次请求两个模块时
 * 同一段只能出现一次 —— 否则模型会拿到两份一字不差的规范,轻则浪费 token,
 * 重则被其中一份的位置误导(规范里多处写着"见上"/"见下")。
 * 去重按**段落的身份**(常量引用)做,不按文本比较。
 *
 * ★ **`data_viz` 与 `elicitation` 两个模块名在这里是故意缺席的。**
 * Claude 的 read_me schema 里有它们,但会话导出里抓不到对应正文 ——
 * 我们能拿到的只有这 5 个模块。**别为了对齐 schema 把空模块挂上去**:
 * 模型请求一个返回空串的模块不会报错,它只会以为"这个模块没有规范",
 * 然后凭感觉画。宁可让它看见 5 个可选值。
 *
 * ★ 同理由此不带 `platform` 参数:抽取到的正文里没有窄视口那一档。
 */
import { CORE } from './core'
import { SVG_SETUP } from './svg-setup'
import { ART_AND_ILLUSTRATION } from './art'
import { UI_COMPONENTS } from './ui-components'
import { COLOR_PALETTE } from './color-palette'
import { CHARTS_CHART_JS } from './charts'
import { DIAGRAM_TYPES } from './diagram-types'
import { TSX_COMPONENTS } from './components'

/**
 * 模块名清单 —— 顺序就是模型在 schema 里看到的枚举顺序,而顺序会影响它挑哪个,
 * 所以这份清单不是"随便排的",改动要当成产品改动看。
 *
 * ★ **写成显式数组而不是 `Object.keys(MODULE_SECTIONS)`**:`z.enum()` 要的是
 * **字面量元组**(`readonly [string, ...string[]]`),而 `Object.keys` 给回来的是
 * `string[]`,类型当场就不匹配,派出来的元素类型也会退化成 `string`。
 *
 * ★ 它与下面那张表的一致性**由类型保证**:表的类型是
 * `Record<GuidelineModule, …>`,所以漏一个模块、多一个不存在的模块都会在
 * 编译期报错。`index.test.ts` 再钉一遍顺序与段落的装配结果。
 */
/*
  ★ `components` 排第一:它是 TSX 模式的规范,而 TSX 是交互类 widget 的首选写法 ——
  模型看枚举时第一眼就该看到它。
*/
export const AVAILABLE_MODULES = ['components', 'diagram', 'mockup', 'interactive', 'chart', 'art'] as const

export type GuidelineModule = (typeof AVAILABLE_MODULES)[number]

/**
 * 模块 → 段落。映射照抄 Anthropic 的原始定义(见 pi-generative-ui 的 `guidelines.ts`)。
 *
 * 类型写成 `Record<GuidelineModule, …>` 而不是 `as const satisfies …`:
 * 前者让"清单与表必须一一对应"成为**编译期**事实(见 `AVAILABLE_MODULES` 的说明),
 * 而表里每项的顺序不影响任何东西(装配时按请求顺序去重)。
 */
const MODULE_SECTIONS: Record<GuidelineModule, readonly string[]> = {
  // 只有自己这一段:TSX 的颜色、布局全走宿主组件与 CSS 变量,不需要 HTML 那套 class 规范
  components: [TSX_COMPONENTS],
  diagram: [COLOR_PALETTE, SVG_SETUP, DIAGRAM_TYPES],
  mockup: [UI_COMPONENTS, COLOR_PALETTE],
  interactive: [UI_COMPONENTS, COLOR_PALETTE],
  chart: [UI_COMPONENTS, COLOR_PALETTE, CHARTS_CHART_JS],
  art: [SVG_SETUP, ART_AND_ILLUSTRATION]
}

/**
 * 装配返回给模型的那段文本。
 *
 * 段落之间用三个换行分隔、末尾补一个换行 —— 这是原实现的形状,照抄:
 * `##` 标题前面空两行是这套规范自己要求的排版,改了就与正文里的"见上"对不上。
 */
export function getGuidelines(modules: readonly GuidelineModule[]): string {
  let content = CORE
  const seen = new Set<string>()
  for (const mod of modules) {
    for (const section of MODULE_SECTIONS[mod]) {
      if (seen.has(section)) continue
      seen.add(section)
      content += `\n\n\n${section}`
    }
  }
  return `${content}\n`
}

export { CORE } from './core'
export { SVG_SETUP } from './svg-setup'
export { ART_AND_ILLUSTRATION } from './art'
export { UI_COMPONENTS } from './ui-components'
export { COLOR_PALETTE } from './color-palette'
export { CHARTS_CHART_JS } from './charts'
export { DIAGRAM_TYPES } from './diagram-types'
export { TSX_COMPONENTS } from './components'
