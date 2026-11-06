/**
 * Codex(ChatGPT 订阅通道)的额度快照 —— **从响应头里搭便车读来的那一份。**
 *
 * ## 为了什么需求建的
 *
 * 订阅额度按账号、按窗口(5 小时 / 一周)算。用户要在设置页看见「这个号还剩多少」,
 * 而多账号的自动切换也要用它**提前**换号(额度跑满就落闸,不等那次注定失败的 429)。
 *
 * ## ★★ 数据只从**已经发生的对话请求**的响应头里来
 *
 * 不主动探针、不后台轮询(产品决策 D6)。理由不是省流量,是**不该由我们去消耗
 * 用户的订阅额度** —— 一个为了画进度条而发出去的请求,在按次计费的窗口里
 * 和一次真实对话没有区别。代价是:刚登录、或者很久没用的账号,额度条是空的。
 * 那个状态界面上必须说出来(「尚未获取,发一条消息后更新」),不能画成 0%。
 *
 * ## ★★★ 头名**尚未实测定死**(计划 §11 的第 1 条)
 *
 * 下面这组名字来自 Codex CLI 的已知实现,但本仓库**还没有**拿真实响应验证过。
 * 所以这里的策略是:
 * - 认多组候选名(下划线 / 连字符、`resets-in` / `reset-after` 两种写法);
 * - **缺任何一个字段就判这个窗口没有**,绝不编数;
 * - 解析不出来一律返回 `null`,让界面显示「尚未获取」而不是一个假的 0%。
 *
 * 拿到实测结果之后,请把**看到的原始头 + 日期**补在这段注释下面,并把用不上的
 * 候选名删掉 —— 留着一组没人验证过的别名,下一个人会以为它们都被确认过。
 *
 * ## 故意不做什么
 *
 * - **不碰 `Response`**:入参是一个 `(name) => string | null` 的取头函数。
 *   于是这个文件零依赖、可直测,而 router 那边只多一行。
 * - **不判「该不该落闸」**:那是 `AccountPool.reportQuota` 的事(它要读设置、要写库)。
 *   这里只把头翻译成一个结构。
 */
import type { ProviderQuotaSnapshot, ProviderQuotaWindow } from '../../../shared/domain/provider-account'

/** 取头的函数。★ 名字一律小写传进来,调用方负责 `headers.get` 的大小写不敏感 */
export type HeaderReader = (name: string) => string | null

/**
 * 一个窗口的三个字段各自的候选头名。
 *
 * ★ 顺序有意义:**排在前面的先认**。把最可能的那个(Codex CLI 现在发的形状)
 * 放第一位,后面几个是防御性的别名。
 */
interface WindowHeaderNames {
  usedPercent: readonly string[]
  windowMinutes: readonly string[]
  resetsInSeconds: readonly string[]
}

function namesFor(slot: 'primary' | 'secondary'): WindowHeaderNames {
  return {
    usedPercent: [`x-codex-${slot}-used-percent`, `x-codex-${slot}-used_percent`],
    windowMinutes: [`x-codex-${slot}-window-minutes`, `x-codex-${slot}-window_minutes`],
    resetsInSeconds: [
      `x-codex-${slot}-resets-in-seconds`,
      `x-codex-${slot}-reset-after-seconds`,
      `x-codex-${slot}-resets_in_seconds`
    ]
  }
}

/**
 * 读第一个存在且是**有限数字**的头。
 *
 * ★ 空串、`unknown`、`null` 这类值当作「没有」而不是 0 —— 0 是一个**确切的**
 * 读数(「这个窗口一点都没用」),把一个读不懂的值说成 0 就是在编数。
 */
function readNumber(headers: HeaderReader, names: readonly string[]): number | undefined {
  for (const name of names) {
    const raw = headers(name)
    if (raw === null) continue
    const trimmed = raw.trim()
    if (trimmed === '') continue
    const value = Number(trimmed)
    if (Number.isFinite(value)) return value
  }
  return undefined
}

function windowFrom(
  headers: HeaderReader,
  slot: 'primary' | 'secondary',
  now: number
): ProviderQuotaWindow | undefined {
  const names = namesFor(slot)
  const usedPercent = readNumber(headers, names.usedPercent)
  const windowMinutes = readNumber(headers, names.windowMinutes)
  const resetsInSeconds = readNumber(headers, names.resetsInSeconds)
  /*
    ★★ **三个字段同进同出。** 只拿到百分比、不知道窗口多长也不知道什么时候重置的话,
    这条数据在界面上画不出一行有意义的东西(「已用 62%,什么的 62% 不知道」),
    而在 `AccountPool` 那边更危险:没有 `resetsAt` 的「跑满」会变成一次
    **永不到期**的落闸 —— 那个账号从此再也不会被选中,且没有任何提示说明为什么。
  */
  if (usedPercent === undefined || windowMinutes === undefined || resetsInSeconds === undefined) {
    return undefined
  }
  if (windowMinutes <= 0 || resetsInSeconds < 0) return undefined
  return {
    // 夹紧:上游报过 >100 的数(超额宽限),而进度条会溢出圆角
    usedPercent: Math.max(0, Math.min(100, usedPercent)),
    windowMinutes,
    /*
      ★ 相对秒数**当场折成绝对时间戳**。和 `OAuthCredential.expiresAt` 同一条规矩:
      相对值一旦落盘就开始腐烂 —— 重启之后没人知道那 7200 秒是从哪一刻算起的,
      而这个数直接决定界面上那句「14:30 重置」和账号什么时候被放出来。
    */
    resetsAt: now + resetsInSeconds * 1000
  }
}

/**
 * 从响应头里解出额度快照。**认不出返回 `null`。**
 *
 * `null` 和「两个窗口都是 0%」是两件完全不同的事,调用方不得把它们合并
 * (见 `QuotaBar` 与界面文案 `providerAccount.quota.empty`)。
 */
export function parseCodexQuota(headers: HeaderReader, now: number): ProviderQuotaSnapshot | null {
  const primary = windowFrom(headers, 'primary', now)
  const secondary = windowFrom(headers, 'secondary', now)
  if (primary === undefined && secondary === undefined) return null
  return {
    ...(primary === undefined ? {} : { primary }),
    ...(secondary === undefined ? {} : { secondary }),
    capturedAt: now
  }
}

/**
 * 把 `Response.headers` 包成取头函数。
 *
 * ★ 单独一个函数是为了让上面那些**完全不依赖 DOM/undici 的类型** ——
 * 测试里喂一个 `Record` 就能跑,不必造 `Headers`。
 */
export function headerReaderOf(headers: Headers): HeaderReader {
  return (name) => headers.get(name)
}

/**
 * 诊断用:这次响应带了哪些 `x-codex-*` 头。
 *
 * ★★ **这是 §11 第 1 条那次实测的入口。** 头名还没被真实响应验证过,而
 * 「解析不出来」和「上游根本没给」在日志里长得一模一样。把看到的头原样记一行,
 * 拿到真实形状之后就能把 `namesFor` 里那几个候选删干净。
 *
 * ★ 只记**名字和值**,不记整份响应头:`authorization`、`set-cookie` 都在里面。
 */
export function codexQuotaHeaderNames(headers: Headers): string[] {
  const seen: string[] = []
  headers.forEach((value, name) => {
    if (name.toLowerCase().startsWith('x-codex-')) seen.push(`${name}=${value}`)
  })
  return seen
}
