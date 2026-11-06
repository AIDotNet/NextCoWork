/**
 * 主题:**一套量出来的基准色板 + 一个纯函数的换色器**。
 *
 * 界面上有三样东西(偏好 → 主题):外观模式(浅/深/跟随系统)、图片主题、颜色主题。
 * 后两者要能把**整套 23 个 token 一起换掉** —— 选「陶土叠影」之后连窗口底、
 * 侧边栏、分隔线都跟着变暖,而不是只换一个强调色。
 *
 * ── 为什么不是「每套主题各写一张 22 色的表」 ──
 *
 * 那样 6 套颜色 × 2 个外观 = 12 张表、264 个色值要手调,而且
 * `theme.css` 里那三条规律(见下)会在第二张表上就开始漂。
 * 这里改成:**基准表是量出来的、唯一的;每套主题只声明「色相往哪挪、彩度乘几」。**
 *
 * ── 换色器必须守住的三条,全部来自 `theme.css` 的注释 ──
 *
 * 1. **明度结构一个字都不能动。** 四层底色的相对明暗全编码在 L 上:
 *    深色 canvas #1e2020 < surface #232726 < app #2a2d2b < chrome #343a37,
 *    浅色 chrome #e8e4dd < app #f2eee6 < surface #f6f4ef < canvas #faf9f5 ——
 *    **chrome 在两个主题里站在相反的一端**,为的是同一件事:把外层 Tab 条从内容里推开。
 *    所以 `retint` **只动 H 和 S**;一旦动了 L,这个左右对称的结构就塌了。
 * 2. **色相不能整体旋转,要按家族分开挪。** 理由**不在基准表里**,在主题声明里:
 *    基准表的两族其实挨得很近(深 153/150,浅 42.2/54.7),但「奢华」深色要的是
 *    紫底 284 + 金色交互态 43,两族差 241°。一个旋转量满足不了这种声明。
 *    挪的是**增量**:`H = 目标家族色相 + (原色相 − 原家族色相)`,
 *    每个 token 相对家族的那点偏移原样保留。
 * 3. **`danger` 不参与换色。** 语义色和强调色无关 —— 这正是「极简」那套
 *    去掉所有颜色、却仍然留着红色警示的原因。
 *
 * ★ 增量式的一个副产品:HSL 在这 34 个色值上**往返零误差**(有测试),
 *   于是「选中你已经在用的那套主题」是**严格恒等**的,不是「几乎一样」。
 *   `BASE.light` 就是「墨绿」的浅色、`BASE.dark` 就是「Claude」的深色,
 *   它们的 spec 是恒等 spec,量出来的数据原样出去。
 */

// ─────────────────────────── token 名单与角色 ───────────────────────────

/**
 * 23 个 token,名字就是 CSS 变量去掉 `--color-` 前缀 ——
 * 于是 `applyTheme` 里是 `--color-${key}`,两边不会漂。
 * 顺序照 `theme.css` 的书写顺序,方便对读。
 */
export const THEME_TOKENS = [
  'app',
  'canvas',
  'surface',
  'chrome',
  'surface-raised',
  'surface-input',
  'surface-field',
  'surface-sunken',
  'tint',
  'tint-hover',
  'tint-strong',
  'border',
  'stroke',
  'hairline',
  'fg',
  'fg-muted',
  'fg-faint',
  'icon',
  'accent',
  'accent-fg',
  'accent-soft',
  'danger',
  'scrim'
] as const

export type ThemeToken = (typeof THEME_TOKENS)[number]
export type ThemeTokens = Record<ThemeToken, string>

/** 外观模式解析后的那个。与 `settings.ts` 的 `ResolvedTheme` 同构,这里不 import 免得两个领域文件互相牵扯。 */
export type Appearance = 'light' | 'dark'

/**
 * token 的四种角色。**加了 token 却没在这里表态,类型检查当场就红** ——
 * 和 `settings.ts` 里 `PATCHABLE_KEYS` 是同一个哨兵手法。
 *
 * - `neutral` 结构灰:窗口底 / 画布 / 侧边栏 / 发丝线。深色里冷,浅色里暖(奶油色)。
 * - `tint`    交互态 + 文字:悬停 / 选中槽 / 描边 / 三级文字。两个外观里都是暖的。
 *   ★ `border` / `stroke` / `surface-field` 归**这一族**不是笔误:深色下
 *     `border === stroke === tint`、`surface-field === tint-hover`,`theme.css` 的注释里写着。
 * - `spec`    主题自己声明的四个色:图标、强调、强调前景、装饰性弱强调。
 * - `keep`    原样保留。只有 `danger`,理由见文件头第 3 条。
 */
type TokenRole = 'neutral' | 'tint' | 'spec' | 'keep'

const ROLE = {
  app: 'neutral',
  canvas: 'neutral',
  surface: 'neutral',
  chrome: 'neutral',
  'surface-raised': 'neutral',
  'surface-input': 'neutral',
  'surface-sunken': 'neutral',
  hairline: 'neutral',
  scrim: 'neutral',

  tint: 'tint',
  'tint-hover': 'tint',
  'tint-strong': 'tint',
  'surface-field': 'tint',
  border: 'tint',
  stroke: 'tint',
  fg: 'tint',
  'fg-muted': 'tint',
  'fg-faint': 'tint',

  icon: 'spec',
  accent: 'spec',
  'accent-fg': 'spec',
  'accent-soft': 'spec',

  danger: 'keep'
} as const satisfies Record<ThemeToken, TokenRole>

// ─────────────────────────── 色彩工具 ───────────────────────────

/** `h` 0–360,`s` / `l` 0–100。 */
export interface Hsl {
  h: number
  s: number
  l: number
}

const clamp = (v: number, lo: number, hi: number): number => (v < lo ? lo : v > hi ? hi : v)

/** 色相环上取模,负数也落回 [0, 360)。 */
const wrapHue = (h: number): number => ((h % 360) + 360) % 360

/**
 * `#rrggbb` → HSL。**不接受简写和 alpha** —— 色板里不该出现那些,
 * 静默接受只会让一个手滑的 `#fff` 一路走到界面上才被看见。
 */
export function hexToHsl(hex: string): Hsl {
  const n = Number.parseInt(hex.slice(1), 16)
  const r = ((n >> 16) & 255) / 255
  const g = ((n >> 8) & 255) / 255
  const b = (n & 255) / 255

  const max = Math.max(r, g, b)
  const min = Math.min(r, g, b)
  const l = (max + min) / 2
  const c = max - min

  if (c === 0) return { h: 0, s: 0, l: l * 100 }

  let h: number
  if (max === r) h = ((g - b) / c) % 6
  else if (max === g) h = (b - r) / c + 2
  else h = (r - g) / c + 4

  return {
    h: wrapHue(h * 60),
    s: (c / (1 - Math.abs(2 * l - 1))) * 100,
    l: l * 100
  }
}

/** HSL → `#rrggbb`。与 `hexToHsl` 严格互逆(有测试)。 */
export function hslToHex({ h, s, l }: Hsl): string {
  const hh = wrapHue(h)
  const ss = clamp(s, 0, 100) / 100
  const ll = clamp(l, 0, 100) / 100

  const c = (1 - Math.abs(2 * ll - 1)) * ss
  const x = c * (1 - Math.abs(((hh / 60) % 2) - 1))
  const m = ll - c / 2

  let rgb: [number, number, number]
  if (hh < 60) rgb = [c, x, 0]
  else if (hh < 120) rgb = [x, c, 0]
  else if (hh < 180) rgb = [0, c, x]
  else if (hh < 240) rgb = [0, x, c]
  else if (hh < 300) rgb = [x, 0, c]
  else rgb = [c, 0, x]

  const hex = rgb
    .map((v) =>
      Math.round((v + m) * 255)
        .toString(16)
        .padStart(2, '0')
    )
    .join('')
  return `#${hex}`
}

/**
 * 任意字符串 → `#rrggbb`(小写),不认识给 `null`。
 *
 * ★ **`hexToHsl` 不校验输入**,这就是这个函数存在的理由:`Number.parseInt('zz', 16)`
 * 是 `NaN`,一路走到 `hslToHex` 吐出来的是 `#NaNNaNNaN` —— 一个写进 CSS 变量
 * 就让整套界面失色、却不会在任何一条日志里露面的值。
 * 而「自定义」那个种子色**两条来路都不可信**:一条是从磁盘读回来的设置,
 * 一条是用户在 hex 输入框里边打边变的半截字符串(`#3` 也会触发一次 onChange)。
 *
 * 收简写(`#abc`)和不带 `#` 的写法 —— 这两种是手打出来的常态,而下游拿到的
 * 一律是规范形式,所以宽进严出在这里是安全的。
 */
export function normalizeHex(input: string): string | null {
  const s = input.trim().replace(/^#/, '').toLowerCase()
  const m = /^([0-9a-f]{3})$|^([0-9a-f]{6})$/.exec(s)
  if (m === null) return null
  return m[2] !== undefined ? `#${m[2]}` : `#${[...s].map((c) => c + c).join('')}`
}

/**
 * 彩度 = max − min(0–255)。
 *
 * ★ 判断「这个像素有没有颜色」**必须用彩度,不能用 HSL 的饱和度**:
 * 饱和度是除以 `1 − |2L − 1|` 之后的比值,于是一个几乎黑的像素
 * (`#020100`)饱和度是 100%,而它在图里就是黑的。拿饱和度筛主色,
 * 取出来的会是阴影里的噪点。
 */
export function chromaOf(hex: string): number {
  const n = Number.parseInt(hex.slice(1), 16)
  const r = (n >> 16) & 255
  const g = (n >> 8) & 255
  const b = n & 255
  return Math.max(r, g, b) - Math.min(r, g, b)
}

// ─────────────────────────── 基准色板 ───────────────────────────

export interface Palette {
  tokens: ThemeTokens
  /**
   * 两个家族的**基准色相**。按彩度加权的**圆周平均**算出来的 ——
   * 不能用算术平均:色相在 0/360 处会绕回去,而彩度 3 的 token
   * 色相基本是舍入噪声,不该和彩度 12 的那个占一样的权重。
   */
  hue: { neutral: number; tint: number }
}

/**
 * ★ **苹果风格基准色板**(feat/apple-style-ui)。取自 macOS 的系统语义色 ——
 * 浅色是 `#f5f5f7` / `#1d1d1f` 那一族(apple.com 与系统设置同源),深色是
 * `systemGray6 #1c1c1e` → `systemGray5 #2c2c2e` → `systemGray4 #3a3a3c` 的阶梯。
 * 结构灰都带一点冷蓝(H≈240),和 macOS 的窗口底一致;换色器按增量平移色相,
 * 所以其他颜色主题仍然能把底色拧到自己的色系。
 *
 * 四层的明暗关系沿用原来的约束(见 `theme.css` §1):canvas 是极值,chrome 站在最远端。
 *
 * ★ **强调色两个外观都是 `#0071e3`,不是系统蓝 `#007aff` / `#0a84ff`。**
 *   白字压在 `#007aff` 上只有 4.0:1、`#0a84ff` 上只有 3.65:1,而可读性护栏
 *   (`theme-profile.ts` 的 `resolveProfile`)要求 `accent-fg` 对 `accent` ≥ 4.5:1 ——
 *   不达标的话护栏会把白字压成近黑,蓝底黑字一眼就不是苹果了。`#0071e3` 是
 *   apple.com 按钮用的那个蓝:白字 4.70:1,压在深色画布上 3.6:1,两头都过线。
 *
 * ★ `fg-muted` 比系统的 secondaryLabel 深/亮一档(浅 `#5f5f64`、深 `#aeaeb2`):
 *   护栏要它对**每一种**底色(含 tint-strong / surface-sunken)都 ≥ 4.5:1。
 */
export const BASE: Record<Appearance, Palette> = {
  dark: {
    tokens: {
      app: '#252527',
      canvas: '#1c1c1e',
      surface: '#202022',
      chrome: '#2c2c2e',
      'surface-raised': '#2c2c2e',
      'surface-input': '#2c2c2e',
      'surface-field': '#3a3a3c',
      'surface-sunken': '#232325',
      tint: '#2c2c2e',
      'tint-hover': '#323234',
      'tint-strong': '#3a3a3c',
      border: '#48484a',
      stroke: '#2e2e30',
      hairline: '#28282a',
      fg: '#f5f5f7',
      'fg-muted': '#aeaeb2',
      'fg-faint': '#8e8e93',
      icon: '#c7c7cc',
      accent: '#0071e3',
      'accent-fg': '#ffffff',
      'accent-soft': '#4a6c99',
      danger: '#ff453a',
      scrim: '#000000'
    },
    hue: { neutral: 240, tint: 240 }
  },
  light: {
    tokens: {
      app: '#ececf0',
      canvas: '#ffffff',
      surface: '#f5f5f7',
      chrome: '#e3e3e8',
      'surface-raised': '#f2f2f7',
      'surface-input': '#ffffff',
      'surface-field': '#ffffff',
      'surface-sunken': '#dcdce1',
      tint: '#ededf2',
      'tint-hover': '#e8e8ed',
      'tint-strong': '#dedee3',
      border: '#c7c7cc',
      stroke: '#e5e5ea',
      hairline: '#ececf0',
      fg: '#1d1d1f',
      'fg-muted': '#5f5f64',
      'fg-faint': '#8e8e93',
      icon: '#6e6e73',
      accent: '#0071e3',
      'accent-fg': '#ffffff',
      'accent-soft': '#86a8d6',
      danger: '#e0352b',
      scrim: '#000000'
    },
    hue: { neutral: 240, tint: 240 }
  }
}

// ─────────────────────────── 换色器 ───────────────────────────

/**
 * 一套主题在**一个外观**下的声明。只有 7 个数,不是 23 个色值。
 */
export interface ThemeSpec {
  /** 结构灰的目标色相 */
  neutral: number
  /** 交互态与文字的目标色相 */
  tint: number
  /** 彩度乘数。1 = 原样;`极简` 用 0(彻底去色);图片主题用 >1 让底色真的读得出来 */
  chroma: number

  icon: string
  accent: string
  accentFg: string
  accentSoft: string
}

/**
 * 基准色板 + 声明 → 23 个色值。**纯函数,`base` 不会被改。**
 *
 * 每个 token:L 原样保留,H 按家族**增量**平移,S 乘上 `chroma`。
 * 白(`#ffffff`)和黑(`#000000`)是自保护的 —— S 已经是 0,乘几都还是 0,
 * 平移色相也不产生颜色。
 */
export function retint(base: Palette, spec: ThemeSpec): ThemeTokens {
  const shift = {
    neutral: spec.neutral - base.hue.neutral,
    tint: spec.tint - base.hue.tint
  }

  const out = {} as ThemeTokens
  for (const key of THEME_TOKENS) {
    // 四个 spec 色直接落位。写成显式分支而不是查表,是为了不用 cast:
    // 查表拿到的值类型是 `string | number` 的并,反而要在这里断言回来。
    if (key === 'icon') {
      out[key] = spec.icon
      continue
    }
    if (key === 'accent') {
      out[key] = spec.accent
      continue
    }
    if (key === 'accent-fg') {
      out[key] = spec.accentFg
      continue
    }
    if (key === 'accent-soft') {
      out[key] = spec.accentSoft
      continue
    }

    const role = ROLE[key]
    if (role === 'keep') {
      out[key] = base.tokens[key]
      continue
    }

    const c = hexToHsl(base.tokens[key])
    out[key] = hslToHex({
      h: c.h + (role === 'neutral' ? shift.neutral : shift.tint),
      s: c.s * spec.chroma,
      l: c.l
    })
  }
  return out
}

// ─────────────────────────── 由种子色派生 ───────────────────────────

/** WCAG 相对亮度。派生强调色时要用它,不能用 HSL 的 L —— 见 `fitLightness`。 */
function relLuminance(hex: string): number {
  const n = Number.parseInt(hex.slice(1), 16)
  const lin = (v: number): number => {
    const c = v / 255
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
  }
  return 0.2126 * lin((n >> 16) & 255) + 0.7152 * lin((n >> 8) & 255) + 0.0722 * lin(n & 255)
}

export function contrastRatio(a: string, b: string): number {
  const la = relLuminance(a)
  const lb = relLuminance(b)
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05)
}

/**
 * 固定色相与饱和度,从 `preferred` 往对比度**变高**的方向走,找第一个达标的明度。
 *
 * ★ 不能直接把 L 写死。**HSL 的 L 不是感知亮度** —— 绿色在亮度公式里的系数是
 * 0.7152,蓝色只有 0.0722,于是同样的 `L=58%`:
 *
 *     hsl(152, 30%, 58%) = #74b496  vs 画布 5.06 : 1   ✓ 墨绿
 *     hsl(351, 30%, 58%)            vs 画布 3.41 : 1   ✗ 雾花柔光那种粉
 *     hsl(240, 62%, 58%)            vs 画布 更低        ✗ 任何偏蓝的图
 *
 * 写死 L 的话,「上传一张偏蓝的图」得到的是一个几乎读不出来的强调色 ——
 * 而那正是自定义主题最容易翻车的地方。所以这里量的是对比度本身。
 *
 * 亮度对 L 单调,所以从 `preferred` 单向走一定收敛;`preferred` 本身就达标时
 * 原样返回 —— 于是参考实现那几个手调过的值不会被这一步挪走。
 */
function fitLightness(
  h: number,
  s: number,
  against: string,
  target: number,
  dir: 'lighter' | 'darker',
  preferred: number
): number {
  const step = dir === 'lighter' ? 1 : -1
  for (let l = preferred; l >= 2 && l <= 98; l += step) {
    if (contrastRatio(hslToHex({ h, s, l }), against) >= target) return l
  }
  return dir === 'lighter' ? 98 : 2
}

/**
 * 强调色上的文字/图标。
 *
 * ★ **两个候选都要「拟合」,不能挑完就算。** 先看白的够不够 —— 深色主题下
 * 压白字是最常见的那一种;不够就把带色相的近黑**继续往黑里压,压到达标为止**。
 * 早先这里是「白 vs 固定 9% 明度的近黑,取对比度高的那个」,于是
 * 「雾花柔光」`#c08a92` 拿到 4.43:1、随机第 0 掷拿到 4.38:1 —— 两个都差一点点,
 * 而 4.5:1 正是正文字号的 WCAG AA 线。**取最大值不等于达标**,这是两回事。
 */
function foregroundOn(accent: string, h: number): string {
  const FG_TARGET = 4.6 // 比 4.5 高一点,留一格舍入余量
  if (contrastRatio('#ffffff', accent) >= FG_TARGET) return '#ffffff'

  const nearBlack = hslToHex({ h, s: 28, l: fitLightness(h, 28, accent, FG_TARGET, 'darker', 9) })
  // 强调色本身是个中间调时两边都到不了 —— 那就退回「取更好的那个」,
  // 但这已经不是默认路径了。`specFromSeed` 里的强调色都是拟合过的,不会落在这儿。
  return contrastRatio(nearBlack, accent) >= contrastRatio('#ffffff', accent)
    ? nearBlack
    : '#ffffff'
}

/**
 * 一个种子色 → 一整套声明。
 *
 * **图片主题、上传的图片、「随机」三者共用这一个函数** —— 于是一张用户自己传的图
 * 和内置的「陶土叠影」渲染出来是同一个质地,不会一眼看出哪个是「正牌」的。
 *
 * 对比度目标(有测试钉着):强调色 vs 画布 ≥ 3.5:1(深)/ ≥ 5:1(浅);
 * 强调前景 vs 强调色 ≥ 4.5:1。
 * `accent-soft` 反过来**刻意不达标**(2.5–3.3:1)—— 它是正文里的装饰性小图标,
 * 参考实现量出来就是 1.98:1(深)/ 3.08:1(浅),本来就不是给人读的。
 */
export function specFromSeed(seed: string, appearance: Appearance): ThemeSpec {
  const { h, s } = hexToHsl(seed)
  const canvas = BASE[appearance].tokens.canvas

  if (appearance === 'dark') {
    const accentS = clamp(s, 30, 62)
    // 52 是参考实现那个亮绿 #36d285 的明度;够用就停在这儿,不够才往亮里走。
    // (旧的 58 是上一版那个橙的明度,那一版已经作废)
    const accent = hslToHex({
      h,
      s: accentS,
      l: fitLightness(h, accentS, canvas, 3.6, 'lighter', 52)
    })
    return {
      neutral: h,
      tint: h,
      chroma: 1.3,
      // ★ **静息图标是中性灰,深浅两个外观都是** —— 新版参考实现量下来 icon #cfcfcf
      // 而 accent #36d285,两者分开了(见 `theme.css` §3)。旧版深色下 icon 就是那个橙,
      // 这里曾经跟着写 `icon: accent`,那个前提已经作废。
      // icon = 「这是个可点的东西」,accent = 「这个是激活的」。
      icon: hslToHex({ h, s: 2, l: 81 }),
      accent,
      accentFg: foregroundOn(accent, h),
      accentSoft: hslToHex({ h, s: clamp(s * 0.5, 24, 32), l: 40 })
    }
  }

  const accentS = clamp(s, 30, 60)
  const accent = hslToHex({
    h,
    s: accentS,
    l: fitLightness(h, accentS, canvas, 5.1, 'darker', 28)
  })
  return {
    neutral: h,
    tint: h,
    chroma: 1.1,
    // 浅色下静息图标是**中性灰**,只有激活的那个才是强调色 —— 同上那条注释
    icon: hslToHex({ h, s: 2, l: 50 }),
    accent,
    accentFg: foregroundOn(accent, h),
    accentSoft: hslToHex({ h, s: clamp(s * 0.25, 8, 14), l: 56 })
  }
}

/**
 * 32 位整数散列(Murmur3 的收尾混合)。只用来抖饱和度和明度 ——
 * 色相不走它,见 `randomSeedColor`。
 */
function hash32(n: number): number {
  let x = n >>> 0
  x ^= x >>> 16
  x = Math.imul(x, 0x7feb352d) >>> 0
  x ^= x >>> 15
  x = Math.imul(x, 0x846ca68b) >>> 0
  x ^= x >>> 16
  return x >>> 0
}

/**
 * 「随机」那套的种子色。同一个 `seed` 永远给同一个色 —— 所以它能被持久化。
 *
 * ★ **色相走黄金角,不走哈希。** 哈希取模只能保证「大概率隔得远」:实测
 * 种子 7 和 8 的色相差正好 20°,肉眼几乎分不出来 —— 而「点了重掷、界面没变」
 * 是用户在这个功能上唯一会注意到的失败。黄金角让相邻两次重掷**必然**隔着
 * 137.5°,且长程不重复(它正是向日葵种子的排布角)。
 * 饱和度和明度仍然走哈希,免得连点几次看出规律。
 */
export function randomSeedColor(seed: number): string {
  const x = hash32(seed)
  return hslToHex({
    h: (seed * 137.508) % 360,
    s: 34 + ((x >>> 9) % 22),
    l: 44 + ((x >>> 20) % 12)
  })
}

// ─────────────────────────── 颜色主题 ───────────────────────────

export interface ColorTheme {
  id: string
  name: string
  description: string
  light: ThemeSpec
  dark: ThemeSpec
}

/** 恒等声明:`retint(BASE[a], identityOf(a))` 逐字节等于 `BASE[a].tokens`。 */
function identityOf(appearance: Appearance): ThemeSpec {
  const p = BASE[appearance]
  return {
    neutral: p.hue.neutral,
    tint: p.hue.tint,
    chroma: 1,
    icon: p.tokens.icon,
    accent: p.tokens.accent,
    accentFg: p.tokens['accent-fg'],
    accentSoft: p.tokens['accent-soft']
  }
}

export const DEFAULT_COLOR_THEME_ID = 'apple'
export const RANDOM_COLOR_THEME_ID = 'random'
export const CUSTOM_COLOR_THEME_ID = 'custom'

/**
 * 第一次点开「自定义」时用的种子色。取参考实现深色那个亮绿 ——
 * 于是从默认的「墨绿」切过去,界面**一帧都不跳**,用户先看到的是
 * 「和刚才一样,但现在这颗色点是我的了」,而不是一次莫名其妙的换色。
 */
export const DEFAULT_CUSTOM_SEED = '#0071e3'

/**
 * 设置里「颜色主题」那一栏存的三个字段。**类型归 theme.ts 所有** ——
 * 认识 `seed` / `custom` 各归谁用的是 `resolveColorTheme`,不是设置层。
 *
 * `seed` 只有「随机」读,`custom` 只有「自定义」读;两个字段都必须落盘,
 * 因为两套主题都是**由一个种子现算**的,不落盘就回不到用户挑中的那一套。
 */
export interface ColorThemeChoice {
  id: string
  seed: number
  custom: string
}

/**
 * 函数侧只有 `id` 是必须的 —— 调用点不必为一个这套主题根本不读的字段
 * 编一个数出来(`tokensOf('dark', { id: 'minimal' }, null)` 就够了)。
 */
export type ColorThemeChoiceLike = Pick<ColorThemeChoice, 'id'> & Partial<ColorThemeChoice>

/**
 * 七套,和界面上的七张卡一一对应。
 *
 * ★ **头尾两套在这张表里只是占位声明** —— 「随机」按种子、「自定义」按用户挑的
 *   那个色,真正的色板都由 `resolveColorTheme` 现算(见那里)。表里仍然要有它们,
 *   因为名字、描述、以及「界面上一共有几张卡」这三件事只该写一遍。
 *
 * ★ **「墨绿」两个外观都是采样数据的原样出口** —— 新版参考实现深浅两套都是绿,
 *   所以 `BASE.light` / `BASE.dark` 的恒等声明现在归同一套主题。
 *   中间那四套全是推出来的。
 *
 * ★ **「Claude」由此从「原样出口」降级成一套保留色板。** 它曾经是深色的恒等声明
 *   (参考实现 v1.1.15 是暖橙),那一版的量测已经作废,但那套橙本身还是好看的,
 *   所以留下来当一套普通主题 —— 深色改成显式声明,数值就是旧 `BASE.dark`。
 *   ★ 别把它改回 `identityOf('dark')`,那样它会变成绿的,和名字对不上。
 *
 * 浅色的结构灰一律留在暖奶油(H≈40) —— 这是参考实现浅色的骨架。
 * 深色的骨架现在是绿灰(H≈153),但除「墨绿」外各套仍按自己的调性声明
 * (霁青 196、Claude 190.4、奢华 284),颜色主题本来就该连底色温度一起换。
 */
export const COLOR_THEMES: readonly ColorTheme[] = [
  {
    id: RANDOM_COLOR_THEME_ID,
    name: '随机',
    description: '每次点击重掷一次色相,掷出来的那个会被记住',
    // 占位:实际声明由 `resolveColorTheme` 按种子现算,见那里的注释
    light: specFromSeed('#0071e3', 'light'),
    dark: specFromSeed('#0071e3', 'dark')
  },
  {
    id: DEFAULT_COLOR_THEME_ID,
    name: 'Apple',
    description: 'macOS 系统色:冷调中性灰底,强调色是 apple.com 的那个蓝',
    light: identityOf('light'),
    dark: identityOf('dark')
  },
  {
    id: 'ink-green',
    name: '墨绿',
    description: '旧版默认:静息中性灰,只有激活态才是绿 —— 浅色墨绿、深色亮绿',
    // 基准表换成苹果灰之后,这套靠增量平移 + 彩度放大把底色拧回暖奶油 / 绿灰
    light: {
      neutral: 42,
      tint: 55,
      chroma: 2.5,
      icon: '#7e7f7e',
      accent: '#2d4739',
      accentFg: '#ffffff',
      accentSoft: '#8e8f8d'
    },
    dark: {
      neutral: 153,
      tint: 150,
      chroma: 2,
      icon: '#cfcfcf',
      accent: '#36d285',
      accentFg: '#0a2615',
      accentSoft: '#5a8a72'
    }
  },
  {
    id: 'celadon',
    name: '霁青',
    description: '雨过天青。冷调,但落在青而不是蓝灰',
    light: {
      neutral: 46,
      tint: 52,
      chroma: 0.85,
      icon: '#7d8082',
      accent: '#295565',
      accentFg: '#ffffff',
      accentSoft: '#889296'
    },
    dark: {
      neutral: 196,
      tint: 190,
      chroma: 1,
      icon: '#65b3c3',
      accent: '#65b3c3',
      accentFg: '#0e1b20',
      accentSoft: '#4b7881'
    }
  },
  {
    id: 'claude',
    name: 'Claude',
    description: '暖橙强调,静息图标同色 —— 参考实现 v1.1.15 的深色,留作一套色板',
    light: {
      neutral: 36,
      tint: 30,
      chroma: 1.1,
      icon: '#7f7c78',
      accent: '#914a27',
      accentFg: '#ffffff',
      accentSoft: '#9f8a7f'
    },
    // 旧 `BASE.dark` 的恒等声明搬了过来。色相是**增量**平移的,所以这几个绝对值
    // 换了基准表照样成立:190.4 把新的绿灰结构拧回冷灰,24.4 把交互态拧回暖橙。
    dark: {
      neutral: 190.4,
      tint: 24.4,
      chroma: 1,
      icon: '#df7e45',
      accent: '#df7e45',
      accentFg: '#241a13',
      accentSoft: '#7f5944'
    }
  },
  {
    id: 'opulent',
    name: '奢华',
    description: '深紫底 + 鎏金强调。唯一一套连底色都换掉色系的',
    light: {
      neutral: 300,
      tint: 296,
      chroma: 0.8,
      icon: '#807d84',
      accent: '#592f60',
      accentFg: '#ffffff',
      accentSoft: '#97849a'
    },
    dark: {
      // 底是紫的、强调是金的 —— 这是全表唯一一处 neutral 与 accent 不同色系,
      // 也正是「奢华」这个词在配色上的意思
      neutral: 284,
      tint: 43,
      chroma: 1.5,
      icon: '#d8b45a',
      accent: '#d8b45a',
      accentFg: '#201122',
      accentSoft: '#8b794b'
    }
  },
  {
    id: 'minimal',
    name: '极简',
    description: '彻底去色,只留红色警示 —— chroma 乘数为 0',
    light: {
      neutral: 0,
      tint: 0,
      chroma: 0,
      icon: '#787878',
      accent: '#1c1c1c',
      accentFg: '#ffffff',
      accentSoft: '#8f8f8f'
    },
    dark: {
      neutral: 0,
      tint: 0,
      chroma: 0,
      icon: '#e0e0e0',
      accent: '#e8e8e8',
      accentFg: '#1c1c1c',
      accentSoft: '#8c8c8c'
    }
  },
  {
    id: CUSTOM_COLOR_THEME_ID,
    name: '自定义',
    description: '自己挑一个强调色,整套界面按它重新配色 —— 和图片主题走的是同一条派生',
    // 占位:实际声明由 `resolveColorTheme` 按 `custom` 现算,见那里的注释
    light: specFromSeed(DEFAULT_CUSTOM_SEED, 'light'),
    dark: specFromSeed(DEFAULT_CUSTOM_SEED, 'dark')
  }
]

/**
 * 「随机」和「自定义」共用的现算路径:一个种子色 → 一整套声明。
 *
 * 名字和描述从表里取,不在这儿再写一遍 —— 界面上那张卡和这里给出来的
 * 必须是同一句话,而抄两份的那一份迟早会忘了跟着改。
 */
function derivedTheme(id: string, seed: string): ColorTheme {
  const decl = COLOR_THEMES.find((t) => t.id === id)
  return {
    id,
    name: decl?.name ?? id,
    description: decl?.description ?? '',
    light: specFromSeed(seed, 'light'),
    dark: specFromSeed(seed, 'dark')
  }
}

/**
 * 设置里那一栏 → 主题。
 *
 * 「随机」与「自定义」都是**种子的纯函数**,不是每次调用都变的东西 ——
 * 否则每次重渲染界面都换一次色,而且重启之后回不到原来那套。
 * 重掷 = 换一个 `seed` 存进设置,挑色 = 换一个 `custom`,都不是在这里摇骰子。
 *
 * ★ `custom` 先过 `normalizeHex`:它是从磁盘读回来的,也可能是用户还没打完的
 *   半截 hex。不过这一关,`specFromSeed` 会把 `NaN` 一路带进 23 个 token。
 */
export function resolveColorTheme(choice: ColorThemeChoiceLike): ColorTheme {
  if (choice.id === RANDOM_COLOR_THEME_ID) {
    return derivedTheme(RANDOM_COLOR_THEME_ID, randomSeedColor(choice.seed ?? 0))
  }
  if (choice.id === CUSTOM_COLOR_THEME_ID) {
    return derivedTheme(
      CUSTOM_COLOR_THEME_ID,
      normalizeHex(choice.custom ?? '') ?? DEFAULT_CUSTOM_SEED
    )
  }
  return (
    COLOR_THEMES.find((t) => t.id === choice.id) ??
    // 落回默认而不是抛:id 来自设置,而设置将来会从磁盘读回来,
    // 降级安装 / 手改配置都可能留下一个不认识的 id
    COLOR_THEMES.find((t) => t.id === DEFAULT_COLOR_THEME_ID) ??
    COLOR_THEMES[1]!
  )
}

// ─────────────────────────── 图片主题 ───────────────────────────

/** 图片怎么进到界面里。界面上是卡片下面那两个小药丸。 */
export type ImageRender = 'blur' | 'overlay'

export interface ThemeWallpaper {
  assetId: string
  thumbnailAssetId?: string
  fit: 'cover' | 'contain'
  position: { x: number; y: number }
  scale: number
  brightness: number
  saturation: number
  blur: number
  opacity: number
  animation: 'auto' | 'static'
  render: ImageRender
  scope: 'desktop' | 'workspace'
  positioning: 'viewport' | 'region'
  crop: 'original' | '16:9' | '4:3' | '1:1'
}

export interface ThemePalette {
  source: 'auto' | 'manual'
  seed: string
  tokens: Partial<Record<ThemeToken, string>>
  light?: Partial<Record<ThemeToken, string>>
  dark?: Partial<Record<ThemeToken, string>>
  /** Preserve the exact legacy engine, including neutral and two-family palettes. */
  base?: ColorThemeChoice
}

export interface SurfaceThemeConfig {
  opacity: number
  mask: number
  solid: boolean
  wallpaper: number
  blur: boolean
}

export interface ThemeSurfaceConfig {
  window: SurfaceThemeConfig
  chrome: SurfaceThemeConfig
  sidebar: SurfaceThemeConfig
  canvas: SurfaceThemeConfig
  rightPanel: SurfaceThemeConfig
  bottomPanel: SurfaceThemeConfig
  content: SurfaceThemeConfig
}

export interface ThemeTypography {
  uiFont: 'system' | 'system-rounded' | 'system-serif'
  codeFont: 'system-mono'
  scale: 'small' | 'standard' | 'large'
  weight: 'standard' | 'compact' | 'comfortable'
}

export interface ThemeMotionConfig {
  level: 'standard' | 'soft' | 'reduced' | 'off'
}

export interface ThemeReadabilityConfig {
  guardrails: boolean
  allowLowContrast: boolean
  textContrast: 'auto' | 'strict' | 'relaxed'
}

/** 独立于 AppSettings 的可复用主题资产。 */
export interface ThemeProfile {
  id: string
  name: string
  version: number
  wallpaper: ThemeWallpaper | null
  palette: ThemePalette
  surfaces: ThemeSurfaceConfig
  typography: ThemeTypography
  motion: ThemeMotionConfig
  readability: ThemeReadabilityConfig
  createdAt: number
  updatedAt: number
  builtin?: boolean
}

export interface ImageTheme {
  id: string
  name: string
  /**
   * 内置的那六张是**渐变配方**,不是图片文件 —— 架子阶段不往仓库里塞位图,
   * 而渐变正好也是这几张卡真正的样子(它们本来就是抽象色块)。
   *
   * 上传的那种带 `url`(`ncw://attachments/themes/…`),渲染层直接
   * `<img src>` / `background-image` 引用它。
   *
   * ★ **`url` 取代了原来那套「assetId → blob: URL」的兑现机制。**
   * 那套机制的全部存在理由是「显示本地图必须先把字节传过来」——
   * 懒兑现、并发去重、revoke 生命周期,三样都是绕这一条限制的成本。
   * 有了协议之后它们一起消失。`assetId` 留着是因为删除仍按它定位。
   */
  source: { kind: 'builtin'; css: string } | { kind: 'uploaded'; assetId: string; url?: string }
  /** 主色。整套 token 由它经 `specFromSeed` 派生 */
  seed: string
  /**
   * 卡片上那几颗色点。**只有上传的那种需要存它** —— 内置的是渐变配方,
   * 配方里的色标本身就是这张图的颜色(见 `paletteOf`)。
   *
   * 导入时由 `extractPalette` 算一次写在这里,之后就不再需要位图了 ——
   * 和 `seed` 同一个道理:文件被挪走、主进程还没把字节递过来的那几帧,
   * 色点照样画得出来。
   */
  palette?: readonly string[]
  /** Metadata used by the theme library without decoding the original again. */
  width?: number
  height?: number
  bytes?: number
  animated?: boolean
  thumbnailUrl?: string
  mime?: string
  fileName?: string
  createdAt?: number
  lastUsedAt?: number
  unavailable?: boolean
}

export const IMAGE_THEMES: readonly ImageTheme[] = [
  {
    id: 'misty-forest',
    name: '雾林深境',
    seed: '#4a6b52',
    source: {
      kind: 'builtin',
      css: 'radial-gradient(120% 90% at 20% 0%, #7d9c83 0%, #4a6b52 45%, #26382c 100%)'
    }
  },
  {
    id: 'clear-sky',
    name: '晴穹蓝构',
    seed: '#5b7fa8',
    source: {
      kind: 'builtin',
      css: 'linear-gradient(155deg, #a9c4dd 0%, #5b7fa8 52%, #2f4763 100%)'
    }
  },
  {
    id: 'terracotta',
    name: '陶土叠影',
    seed: '#b5714a',
    source: {
      kind: 'builtin',
      css: 'linear-gradient(145deg, #e0a882 0%, #b5714a 48%, #6d3f28 100%)'
    }
  },
  {
    id: 'soft-bloom',
    name: '雾花柔光',
    seed: '#c08a92',
    source: {
      kind: 'builtin',
      css: 'radial-gradient(130% 100% at 70% 10%, #efd2d3 0%, #c08a92 50%, #7d525a 100%)'
    }
  },
  {
    id: 'silver-facet',
    name: '银白折面',
    seed: '#9aa0a6',
    source: {
      kind: 'builtin',
      css: 'linear-gradient(125deg, #e6e8ea 0%, #9aa0a6 55%, #5d6469 100%)'
    }
  },
  {
    id: 'jade-wave',
    name: '碧波弧影',
    seed: '#3f8a86',
    source: {
      kind: 'builtin',
      css: 'radial-gradient(110% 110% at 15% 85%, #8ac4bf 0%, #3f8a86 48%, #1f4b49 100%)'
    }
  }
]

/**
 * 设置里存的 `imageTheme.id` → 那张图。**认不出来就当作没选图**,不抛 ——
 * 和 `resolveColorTheme` 同一个道理:id 是从磁盘读回来的,而用户删掉一张
 * 自己上传的图之后,设置里那个 id 就悬空了。此时落回颜色主题是对的,
 * 落回「随便一张内置图」不是。
 *
 * `uploaded` 是用户上传的那些(主进程从 userData 里列出来的),内置的优先 ——
 * 内置 id 是我们自己发的常量,不该被一个碰巧同名的上传文件顶掉。
 */
export function resolveImageTheme(
  id: string | null,
  uploaded: readonly ImageTheme[] = []
): ImageTheme | null {
  if (id === null) return null
  return IMAGE_THEMES.find((t) => t.id === id) ?? uploaded.find((t) => t.id === id) ?? null
}

/**
 * 卡片上那一排色点。
 *
 * ★ **内置那六张的颜色不另存一份。** 渐变配方里的色标就是这张图的颜色,
 * 存两份必然有一份会忘了跟着改 —— 而「色点和卡片对不上」这种错没人会报警。
 * 上传的那种没有配方,取的是导入时算好的 `palette`。
 *
 * 兜底成种子色而不是空数组:配方里一个色标都读不出来时(比如哪天换成
 * `image-set()`),画一颗总比画一排空白强。
 */
export function paletteOf(theme: ImageTheme): string[] {
  const raw =
    theme.source.kind === 'builtin'
      ? (theme.source.css.match(/#[0-9a-f]{6}/gi) ?? []).map((h) => h.toLowerCase())
      : (theme.palette ?? [])
  const uniq = [...new Set(raw)]
  return uniq.length > 0 ? uniq : [theme.seed]
}

// ─────────────────────────── 从图片里取色 ───────────────────────────

/**
 * 从一张(**已经缩小过的**)RGBA 位图里取出几个主色,第 0 个就是种子色。
 *
 * 界面上选中的那张卡下面有四颗色点,就是这个函数的输出;
 * 用户上传的图片也走这里拿到 `seed`,再经 `specFromSeed` 变成整套 token。
 *
 * 做法是**按色相分桶、按彩度加权**,不是 k-means:
 * - 要的是「这张图给人的颜色印象」,而印象由**鲜艳的那部分**主导,
 *   哪怕它只占 5% 的面积。按面积聚类会稳定地返回背景里那片灰。
 * - 桶内色相取**圆周平均**(向量和),不是算术平均 —— 红色在 0° 和 359° 两侧,
 *   算术平均会给出 180°(青)。
 *
 * 调用方负责先把图缩到 ~64×64(canvas `drawImage`);这里再兜一道抽样上限,
 * 免得有人直接把 4K 原图丢进来。
 */
export function extractPalette(rgba: Uint8ClampedArray, count = 4): string[] {
  const BUCKETS = 24
  const buckets = Array.from({ length: BUCKETS }, () => ({
    weight: 0,
    x: 0,
    y: 0,
    s: 0,
    l: 0
  }))

  const pixels = Math.floor(rgba.length / 4)
  const stride = Math.max(1, Math.floor(pixels / 20000))
  let lightnessSum = 0
  let counted = 0

  for (let p = 0; p < pixels; p += stride) {
    const i = p * 4
    const a = rgba[i + 3] ?? 255
    if (a < 128) continue

    const r = rgba[i] ?? 0
    const g = rgba[i + 1] ?? 0
    const b = rgba[i + 2] ?? 0

    const max = Math.max(r, g, b)
    const min = Math.min(r, g, b)
    const l = ((max + min) / 2 / 255) * 100

    lightnessSum += l
    counted++

    // 太暗 / 太亮的像素色相不可信(见 `chromaOf` 的注释),彩度太低的算灰
    const chroma = max - min
    if (chroma < 24 || l < 12 || l > 92) continue

    const hsl = hexToHsl(`#${((r << 16) | (g << 8) | b).toString(16).padStart(6, '0')}`)
    const bucket = buckets[Math.floor(wrapHue(hsl.h) / (360 / BUCKETS)) % BUCKETS]
    if (!bucket) continue

    const rad = (hsl.h * Math.PI) / 180
    bucket.weight += chroma
    bucket.x += Math.cos(rad) * chroma
    bucket.y += Math.sin(rad) * chroma
    bucket.s += hsl.s * chroma
    bucket.l += hsl.l * chroma
  }

  const out = buckets
    .filter((b) => b.weight > 0)
    .sort((a, b) => b.weight - a.weight)
    .slice(0, count)
    .map((b) =>
      hslToHex({
        h: wrapHue((Math.atan2(b.y, b.x) * 180) / Math.PI),
        s: b.s / b.weight,
        l: b.l / b.weight
      })
    )

  // 一张灰度图(或纯色图)一个桶都不会留下 —— 那不是错误,
  // 「银白折面」就该给出一套中性主题。用整图的平均明度补齐。
  const meanL = counted === 0 ? 50 : lightnessSum / counted
  while (out.length < count) {
    out.push(hslToHex({ h: 0, s: 0, l: clamp(meanL + (out.length - 1) * 12, 8, 92) }))
  }
  return out
}

// ─────────────────────────── 对外的一个口子 ───────────────────────────

/**
 * 设置里那三样 → 23 个色值。**界面只该调这一个函数。**
 *
 * 图片主题**盖过**颜色主题:界面上选了一张图之后,颜色主题那一栏就不再生效了
 * (取消选图才回到颜色主题)。这条不是随便定的 —— 两者都要改整套 token,
 * 允许它们叠加的话「我选了墨绿怎么界面是橙的」会变成一个说不清的问题。
 */
export function tokensOf(
  appearance: Appearance,
  color: ColorThemeChoiceLike,
  image: { seed: string } | null
): ThemeTokens {
  const base = BASE[appearance]
  if (image) return retint(base, specFromSeed(image.seed, appearance))
  return retint(base, resolveColorTheme(color)[appearance])
}

/** 界面上那颗色点 / 那个色环。 */
export function swatchOf(theme: ColorTheme, appearance: Appearance): string {
  return theme[appearance].accent
}
