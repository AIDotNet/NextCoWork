/**
 * 应用主题 → **widget 规范认得的那套 CSS 变量名**。
 *
 * ## 为什么需要这一层
 *
 * 我们 vendor 的规范正文(`main/kernel/tool/builtin/visualize-guidelines/**`)里,
 * 每一处颜色都写成 `var(--color-text-secondary)` 这类**Claude 的**变量名。
 * 本仓库自己的 token 叫 `--color-fg-muted`。两套名字对不上,于是只有两条路:
 *
 * 1. 把七万字规范里的变量名全换成我们的 —— 改一次就再也不逐字可信了,
 *    而且规范里那句"use CSS variables for theming"会指向一个它没描述过的体系;
 * 2. **把我们的主题翻译成它认得的名字**(这里做的事)。
 *
 * 选 2 的直接好处:模型照着规范写出来的 widget **跟着用户的颜色主题走** ——
 * 换一套「奢华」或「极简」,图里的卡片、描边、强调色一起变,不需要模型知道
 * 这件事存在。
 *
 * ## 映射的依据
 *
 * `--color-*` 那份清单不是照抄 Claude 的 40 个变量,而是**从规范正文里反查出来的**:
 * 正文实际使用到的只有 text/background/border 三族的 primary / secondary /
 * tertiary / info,加上 `--p / --s / --t / --b / --bg2` 这五个短名字
 * (基础样式表里那些 `svg .box` 之类的预置 class 用的就是它们)。
 * 表里没有的变量保持未定义 —— 定义一堆没人读的变量,只会让下一个人以为
 * 它们是契约的一部分。
 *
 * ## 两条刻意的"翻译"而不是"照抄"
 *
 * - **info 族取 accent。** 规范里 `--color-text-info` 用在"可点的 / 被强调的"
 *   地方(链接、推荐卡那道 2px 描边),而在本应用里承担这个角色的是 `accent`
 *   —— 于是 widget 里的链接颜色和界面其余部分一致,并且跟着换色器走。
 * - **边框用 alpha 而不是灰。** Claude 的 `--color-border-*` 就是它文字色的
 *   40% / 30% / 15% 透明版,所以这里也按那个比例从 `--color-fg` 混出来,
 *   而不是另挑三个灰 —— 换主题时边框跟着文字一起暖/冷。
 *
 * ★ `success` / `warning` / `serif` 三处是**推的**:我们的色板里没有这两种
 *   语义色,也没有衬线字族,取值是 Claude 量出来的那一组(见
 *   `anthropics/claude-ai-mcp#202` 里那张 host context 清单)。等本仓库有了
 *   自己的语义色,应该换成它们。
 */

/** 外观。与 `shared/domain/theme.ts` 的 `Appearance` 同构,这里不 import 免得渲染层被主题域牵扯。 */
export type WidgetAppearance = 'light' | 'dark'

/**
 * 从父文档读出来、喂给 `widgetTokens` 的源变量名。
 *
 * 全部是应用**已经在用的** CSS 变量(`styles/theme.css`),所以文案、字体设置
 * (系统/圆体/衬线那个开关)、圆角一次都不用在这里重述 —— 读到什么就是什么。
 */
export const WIDGET_SOURCE_VARS = [
  '--color-canvas',
  '--color-surface',
  '--color-surface-raised',
  '--color-surface-input',
  '--color-fg',
  '--color-fg-muted',
  '--color-fg-faint',
  '--color-icon',
  '--color-border',
  '--color-stroke',
  '--color-hairline',
  '--color-accent',
  '--color-accent-fg',
  '--color-accent-soft',
  '--color-danger',
  '--font-sans',
  '--font-mono',
  '--font-weight-normal',
  '--radius-card',
  '--radius-panel'
] as const

export type WidgetSourceVars = Partial<Record<(typeof WIDGET_SOURCE_VARS)[number], string>>

/** 我们色板里没有、从 Claude 量出来的那几组值。改这里等于与那里脱钩,注释里记着出处。 */
const BORROWED = {
  success: { text: '#3F7A28', background: 'rgba(233, 241, 220, 1)' },
  warning: { text: '#7A5A16', background: 'rgba(246, 238, 223, 1)' },
  serif: 'Georgia, "Times New Roman", serif'
}

/** 取不到就退到调用方给的兜底 —— 变量名写错时不该整块渲染崩掉。 */
function pick(source: WidgetSourceVars, name: keyof WidgetSourceVars, fallback: string): string {
  const value = source[name]
  return value === undefined || value.trim() === '' ? fallback : value
}

/**
 * 源变量 → 规范认得的变量名。
 *
 * 纯函数:同输入必同输出,不读 `document`、不读 store —— 所以它能被单测穷尽
 * (`__tests__/widget-tokens.test.ts`),而"哪几个变量在两套外观下必须不同"
 * 这类容易回归的规则正好能用断言钉住。
 */
export function widgetTokens(appearance: WidgetAppearance, source: WidgetSourceVars): Record<string, string> {
  const fg = pick(source, '--color-fg', appearance === 'light' ? '#1d1d1f' : '#f5f5f7')
  const fgMuted = pick(source, '--color-fg-muted', appearance === 'light' ? '#5f5f64' : '#aeaeb2')
  const fgFaint = pick(source, '--color-fg-faint', appearance === 'light' ? '#8e8e93' : '#8e8e93')
  const accent = pick(source, '--color-accent', appearance === 'light' ? '#0071e3' : '#0071e3')
  const danger = pick(source, '--color-danger', appearance === 'light' ? '#e0352b' : '#ff453a')
  const panel = pick(source, '--color-surface', appearance === 'light' ? '#f5f5f7' : '#202022')
  const card = pick(source, '--color-surface-input', appearance === 'light' ? '#ffffff' : '#2c2c2e')
  const deep = pick(source, '--color-canvas', appearance === 'light' ? '#ffffff' : '#1c1c1e')

  /** Claude 的边框就是文字色的 40/30/15%。见文件头。 */
  const border = (percent: number): string => `color-mix(in srgb, ${fg} ${String(percent)}%, transparent)`

  return {
    // ── 文字 ──
    '--color-text-primary': fg,
    '--color-text-secondary': fgMuted,
    '--color-text-tertiary': fgFaint,
    '--color-text-inverse': pick(source, '--color-accent-fg', appearance === 'light' ? '#ffffff' : '#ffffff'),
    '--color-text-info': accent,
    '--color-text-danger': danger,
    '--color-text-success': BORROWED.success.text,
    '--color-text-warning': BORROWED.warning.text,
    '--color-text-disabled': fgFaint,

    // ── 背景 ──
    // primary = 卡片面(浅色里是白、深色里比底亮一层),secondary = 面板,
    // tertiary = 最底那一层。三者的相对明暗在两个外观下都成立。
    '--color-background-primary': card,
    '--color-background-secondary': panel,
    '--color-background-tertiary': deep,
    '--color-background-inverse': fg,
    '--color-background-ghost': 'transparent',
    '--color-background-info': `color-mix(in srgb, ${accent} 14%, transparent)`,
    '--color-background-danger': `color-mix(in srgb, ${danger} 14%, transparent)`,
    '--color-background-success': BORROWED.success.background,
    '--color-background-warning': BORROWED.warning.background,

    // ── 描边 ──
    '--color-border-primary': border(40),
    '--color-border-secondary': border(30),
    '--color-border-tertiary': border(15),
    '--color-border-info': accent,
    '--color-border-danger': danger,
    '--color-border-success': BORROWED.success.text,
    '--color-border-warning': BORROWED.warning.text,

    // ── 基础样式表里那五个短名字(svg .box / .t / .ts / .arr / .leader 用它们)──
    '--p': fg,
    '--s': fgMuted,
    '--t': fgFaint,
    '--bg2': panel,
    '--b': border(30),

    // ── 排版与圆角 ──
    // 字体取的是应用**当前**的设置(theme.css 里 `[data-theme-font=…]` 会改 --font-sans),
    // 所以 widget 里的字跟随"系统/圆体/衬线"那个开关。
    '--font-sans': pick(source, '--font-sans', 'system-ui, -apple-system, sans-serif'),
    '--font-mono': pick(source, '--font-mono', 'ui-monospace, monospace'),
    '--font-serif': BORROWED.serif,
    '--font-weight-normal': pick(source, '--font-weight-normal', '400'),
    '--font-weight-medium': '500',
    // 规范只允许两个字重(400/500),所以 600/700 故意**不定义** —— 定义了就是在
    // 邀请模型用它,而正文里写着那会让粗细在流式过程中跳一下。
    // md=8 是 Claude 量出来的按钮/输入框圆角,我们色板里没有这一档,原样取用。
    '--border-radius-xs': '4px',
    '--border-radius-sm': '6px',
    '--border-radius-md': '8px',
    '--border-radius-lg': pick(source, '--radius-card', '10px'),
    '--border-radius-xl': pick(source, '--radius-panel', '12px'),
    '--border-radius-full': '9999px',
    '--border-width-regular': '0.5px'
  }
}
