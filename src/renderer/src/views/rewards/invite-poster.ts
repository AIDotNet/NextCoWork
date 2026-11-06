/**
 * 邀请海报的生成逻辑 —— 按**某一个用户的邀请码**拼出一张 1920×1080 的 SVG。
 *
 * 需求：奖励中心里光给一条链接，用户要去别处做图才发得出去。这里直接出一张
 * 带他自己二维码的海报，点一下存成 PNG 就能丢进微信群。
 *
 * ★ **二维码里编的是邀请链接本身**（`https://nextco.work/login?ref=<码>`），
 * 不是官网首页、也不是邀请码文本 —— 扫码落到带 ref 的注册页，邀请关系才会绑上
 * （CoWork `PortalEndpoints` 的 `/referral/bind` 认的就是这个参数）。编错内容的
 * 症状是：朋友确实注册了，但谁都拿不到奖励，而且全程没有任何报错。
 *
 * ★ **文案从外面传进来，这个文件一个中文字符串都不写。** 它跑在组件之外，
 * 拿不到 `useI18n()`；把默认文案写死在这里的话，切英文时海报还是中文（§6.4）。
 *
 * ★ **不画 logo 位图。** 这一层是纯函数（同目录 `.test.ts` 要能直测，见 §9/§13），
 * 而 logo 是一个打包产物 URL，取它的字节要异步 IO。所以海报给 logo 留了一块
 * 固定矩形（`POSTER_LOGO_RECT`），由导出那一步在画布上补 —— 谁改了这里的版式，
 * 那个常量必须跟着改，否则 logo 会飘在别处。
 */
import qrcode from 'qrcode-generator'

/** 海报画布尺寸。16:9，微信/微博直接发不会被裁。 */
export const POSTER_WIDTH = 1920
export const POSTER_HEIGHT = 1080

/** logo 的落点，导出时由画布补画（见文件头）。 */
export const POSTER_LOGO_RECT = { x: 120, y: 96, size: 96 } as const

/** 二维码在海报上的位置与边长（白底卡片内的可扫区域）。 */
const QR_RECT: QrRect = { x: 1224, y: 648, size: 304 }

/** 二维码的落点。★ 显式类型，不靠 `as const` 推断 —— 那会把参数类型钉成字面量，测试里换个位置就编译不过。 */
export interface QrRect {
  x: number
  y: number
  size: number
}

export interface InvitePosterCopy {
  /** 顶部小字，例如「好东西，和朋友分享」。 */
  eyebrow: string
  /** 主标题两行 —— 分两行是版式要求，SVG 的 `<text>` 不会自动折行。 */
  titleLine1: string
  titleLine2: string
  /** 主标题下面那句解释。 */
  subtitle: string
  /** 三条卖点。多于三条会溢出版心，调用方自己截断。 */
  bullets: readonly string[]
  /** 二维码旁的「微信扫码」类主句。 */
  scanTitle: string
  /** 二维码旁的补充说明。 */
  scanHint: string
  /** 邀请码那一行的前缀，例如「邀请码」。 */
  codeLabel: string
  /** 产品名下面那行副标题。 */
  tagline: string
}

export interface InvitePosterInput {
  /** 用户自己的邀请码，原样显示（领域值，不翻译、不大小写转换）。 */
  code: string
  /** 绝对邀请链接。二维码编的就是它。 */
  inviteUrl: string
  copy: InvitePosterCopy
  /** 站点地址，页脚显示用。 */
  site?: string
}

/**
 * 二维码矩阵 → 一条 SVG path。
 *
 * ★ 一个模块一个 `<rect>` 会生出上千个节点（41×41 的码就是 1681 个），
 * 光栅化和另存都会明显变慢；合成一条 path 只有一个节点。
 *
 * ★ **纠错等级取 M、边距 4 个模块**。M 在「够小」和「印出来被手指挡住一角还能扫」
 * 之间；边距少于 4 个模块是最常见的「截图能扫、打印扫不出」的原因（QR 规范要求的
 * 静区就是 4）。
 */
export function qrPathData(text: string, rect: QrRect = QR_RECT): { path: string; moduleCount: number } {
  const qr = qrcode(0, 'M')
  qr.addData(text)
  qr.make()
  const modules = qr.getModuleCount()
  const margin = 4
  const total = modules + margin * 2
  const cell = rect.size / total
  let path = ''
  for (let row = 0; row < modules; row++) {
    for (let col = 0; col < modules; col++) {
      if (!qr.isDark(row, col)) continue
      const x = rect.x + (col + margin) * cell
      const y = rect.y + (row + margin) * cell
      // 每格多画 0.5px 的重叠，避免光栅化时格子之间透出白缝（摩尔纹一样的细线，
      // 扫码器会把它当成模块边界，近距离扫反而失败）
      path += `M${round(x)} ${round(y)}h${round(cell + 0.5)}v${round(cell + 0.5)}h-${round(cell + 0.5)}z`
    }
  }
  return { path, moduleCount: modules }
}

function round(value: number): number {
  return Math.round(value * 100) / 100
}

/** XML 文本转义。用户昵称/邀请码进 SVG 之前必须过一遍，否则一个 `&` 就让整张图打不开。 */
function esc(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
}

const FONT = 'PingFang SC, Hiragino Sans GB, Microsoft YaHei, Helvetica Neue, sans-serif'

export function buildInvitePosterSvg(input: InvitePosterInput): string {
  const { copy } = input
  const qr = qrPathData(input.inviteUrl)
  const site = input.site ?? 'nextco.work'
  const bullets = copy.bullets.slice(0, 3)
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${POSTER_WIDTH}" height="${POSTER_HEIGHT}" viewBox="0 0 ${POSTER_WIDTH} ${POSTER_HEIGHT}">
<defs>
<linearGradient id="bg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#101413"/><stop offset="0.55" stop-color="#16201b"/><stop offset="1" stop-color="#0d1110"/></linearGradient>
<linearGradient id="card" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#2a2f2c"/><stop offset="1" stop-color="#1c211e"/></linearGradient>
</defs>
<rect width="${POSTER_WIDTH}" height="${POSTER_HEIGHT}" fill="url(#bg)"/>
<g opacity="0.16" stroke="#36d285" fill="none">
<circle cx="1520" cy="230" r="230" stroke-width="1.5"/>
<circle cx="1520" cy="230" r="350" stroke-width="1"/>
<circle cx="1520" cy="230" r="480" stroke-width="0.8"/>
</g>
<rect x="0" y="0" width="14" height="${POSTER_HEIGHT}" fill="#36d285"/>
<text x="240" y="160" font-family="${FONT}" font-size="40" font-weight="600" fill="#f4efe9" letter-spacing="1">NextCoWork</text>
<text x="240" y="196" font-family="${FONT}" font-size="22" fill="#5a8a72">${esc(copy.tagline)}</text>
<text x="120" y="330" font-family="${FONT}" font-size="30" fill="#36d285" letter-spacing="4">${esc(copy.eyebrow)}</text>
<text x="120" y="450" font-family="${FONT}" font-size="104" font-weight="700" fill="#f4efe9" letter-spacing="2">${esc(copy.titleLine1)}</text>
<text x="120" y="580" font-family="${FONT}" font-size="104" font-weight="700" fill="#f4efe9" letter-spacing="2">${esc(copy.titleLine2)}</text>
<rect x="120" y="640" width="120" height="6" rx="3" fill="#36d285"/>
<text x="120" y="722" font-family="${FONT}" font-size="38" fill="#ececec">${esc(copy.subtitle)}</text>
<g font-family="${FONT}" font-size="28" fill="#959897">
${bullets.map((line, index) => {
    const y = 794 + index * 60
    return `<circle cx="132" cy="${y}" r="6" fill="#36d285"/><text x="160" y="${y + 10}">${esc(line)}</text>`
  }).join('\n')}
</g>
<text x="120" y="1002" font-family="Helvetica Neue, sans-serif" font-size="26" fill="#7b7d7c" letter-spacing="2">${esc(site)}</text>
<g transform="translate(1330 280) rotate(-9)">
<rect x="-130" y="-165" width="260" height="330" rx="26" fill="url(#card)" stroke="#3b423e" stroke-width="2"/>
<path d="M -18 -60 L -62 18 L -8 18 L -26 88 L 36 -8 L -16 -8 Z" fill="#36d285"/>
</g>
<g transform="translate(1570 306) rotate(7)">
<rect x="-130" y="-165" width="260" height="330" rx="26" fill="#f4efe9"/>
<rect x="-70" y="-26" width="140" height="104" rx="9" fill="#33383a"/>
<rect x="-82" y="-62" width="164" height="42" rx="9" fill="#33383a"/>
<rect x="-11" y="-62" width="22" height="140" fill="#f4efe9"/>
<path d="M -11 -62 C -54 -106 -106 -80 -11 -62 Z" fill="#33383a"/>
<path d="M 11 -62 C 54 -106 106 -80 11 -62 Z" fill="#33383a"/>
</g>
<rect x="1180" y="604" width="620" height="396" rx="28" fill="#1c211e" stroke="#2f3733" stroke-width="2"/>
<rect x="${QR_RECT.x - 8}" y="${QR_RECT.y - 8}" width="${QR_RECT.size + 16}" height="${QR_RECT.size + 16}" rx="18" fill="#ffffff"/>
<path d="${qr.path}" fill="#111111" shape-rendering="crispEdges"/>
<text x="1576" y="712" font-family="${FONT}" font-size="34" font-weight="600" fill="#f4efe9">${esc(copy.scanTitle)}</text>
<text x="1576" y="762" font-family="${FONT}" font-size="24" fill="#959897">${esc(copy.scanHint)}</text>
<rect x="1576" y="796" width="60" height="4" rx="2" fill="#36d285"/>
<text x="1576" y="858" font-family="${FONT}" font-size="22" fill="#5a8a72">${esc(copy.codeLabel)}</text>
<text x="1576" y="906" font-family="Menlo, Consolas, monospace" font-size="30" fill="#36d285" letter-spacing="2">${esc(input.code)}</text>
</svg>`
}

/** 另存时的文件名建议。带上邀请码，批量生成/收集时一眼看得出是谁那张。 */
export function invitePosterFileName(code: string): string {
  const safe = code.replace(/[^A-Za-z0-9_-]/g, '').slice(0, 24)
  return safe === '' ? 'nextcowork-invite.png' : `nextcowork-invite-${safe}.png`
}
