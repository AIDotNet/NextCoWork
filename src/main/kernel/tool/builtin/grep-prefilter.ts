/**
 * `Grep` 的**整文件预筛** —— 从模型给的正则里抽出一段「任何命中都必须包含」的字面量,
 * 先拿它在整篇文本上查一次,查不到的文件整个跳过,不再逐行跑正则。
 *
 * 需求:绝大多数搜索是在找一个标识符(`GiftBalance`、`useSessionStore`),而一个仓库里
 * 99% 的文件根本不含它。原先每个文件都要 split 成行、逐行截断、逐行 `RegExp.test`,
 * 实测在两万文件的仓库上光这部分就占了 1.5 秒。整篇 `includes` 是线性的,快一个数量级。
 *
 * ★ 不变式:**只许误报,不许漏报**。抽出来的字面量必须是「所有匹配的必经之路」——
 * 抽错一个字符,文件就被静默跳过,模型拿到的是「仓库里没有」,而它就在那儿。
 * 所以这里一律取保守:看不懂的语法(分组、交替、字符类、`\w` 这类转义、断言)
 * 全部当成「断开」,宁可抽不出字面量(退回逐行扫描,只是慢),也不猜。
 *
 * ★ 为什么不直接在整篇文本上跑原正则来预筛:逐行扫描前会把每行截到 2000 字符,
 * 那是给「良性但慢」的模式(`.*foo.*bar` 撞上一行 5MB 的压缩产物)封顶的。
 * 整篇跑原正则等于拆掉这道闸。纯字面量正则/`includes` 是线性的,没有这个问题。
 *
 * 故意不做:交替分支各自抽字面量再 OR 起来、分组内部下钻。收益有限,
 * 而每多一种语法就多一处可能漏报的地方。
 */

/** 抽出来的字面量短于这个就不用 —— 一两个字符几乎每个文件都有,预筛只是白查一遍 */
const MIN_LITERAL_CHARS = 3

/** 从 `[` 开始跳过一个字符类,返回 `]` 的下标(没闭合就返回末尾)。和 `redos.ts` 同一套规则。 */
function skipCharClass(p: string, open: number): number {
  let i = open + 1
  if (p[i] === '^') i++
  if (p[i] === ']') i++
  for (; i < p.length; i++) {
    if (p[i] === '\\') {
      i++
      continue
    }
    if (p[i] === ']') return i
  }
  return p.length
}

/** 从 `(` 开始跳过一个分组(含嵌套、转义、字符类),返回配对 `)` 的下标。 */
function skipGroup(p: string, open: number): number {
  let depth = 0
  for (let i = open; i < p.length; i++) {
    const ch = p[i]
    if (ch === '\\') {
      i++
      continue
    }
    if (ch === '[') {
      i = skipCharClass(p, i)
      continue
    }
    if (ch === '(') depth++
    else if (ch === ')' && --depth === 0) return i
  }
  return p.length
}

/**
 * `p[i]` 处的量词。`len` = 0 表示不是量词;`optional` 表示它允许零次(`?` `*` `{0,…}`)。
 *
 * ★ 非 unicode 模式下,不成形的 `{`(`a{`、`{foo}`)是**字面量**,不是量词 ——
 * 只认 `{n}` / `{n,}` / `{n,m}` 这三种写法,和 V8 的判定一致。
 */
function quantifierAt(p: string, i: number): { len: number; optional: boolean } {
  const ch = p[i]
  let len: number
  let optional: boolean
  if (ch === '*' || ch === '?') {
    len = 1
    optional = true
  } else if (ch === '+') {
    len = 1
    optional = false
  } else if (ch === '{') {
    const m = /^\{(\d+)(?:,\d*)?\}/.exec(p.slice(i))
    if (m === null) return { len: 0, optional: false }
    len = m[0].length
    optional = Number(m[1]) === 0
  } else {
    return { len: 0, optional: false }
  }
  // 惰性修饰 `*?` / `+?` 只改匹配偏好,不改「必须出现」与否
  if (p[i + len] === '?') len++
  return { len, optional }
}

/**
 * 抽出正则里一段**必然出现**的字面量;抽不出就返回 `null`(调用方退回逐行扫描)。
 *
 * 做法:只看顶层的连续字面量字符,遇到任何非字面量的东西就断开,取最长的一段。
 * 顶层出现交替 `|` 时直接放弃 —— `foo|bar` 里没有哪一段是必经的。
 */
export function requiredLiteral(pattern: string): string | null {
  let best = ''
  let run = ''
  /** 上一个 atom 是不是刚被追加进 `run` 的字面量字符(量词要据此决定删不删它) */
  let lastWasLiteral = false

  const flush = (): void => {
    if (run.length > best.length) best = run
    run = ''
    lastWasLiteral = false
  }
  const pushLiteral = (ch: string): void => {
    run += ch
    lastWasLiteral = true
  }

  for (let i = 0; i < pattern.length; i++) {
    const ch = pattern[i] as string

    const q = quantifierAt(pattern, i)
    if (q.len > 0) {
      // 可选的量词作用在上一个字面量上 → 那个字符不是必经的,从 run 里拿掉
      if (lastWasLiteral && q.optional) run = run.slice(0, -1)
      // 不管哪种量词,它后面的字符都不再和前面**相邻**(`ab+c` 不保证出现 `abc`)
      flush()
      i += q.len - 1
      continue
    }

    if (ch === '|') return null

    if (ch === '\\') {
      const next = pattern[i + 1]
      i++
      // 非字母数字的转义(`\.` `\(` `\/`)在非 unicode 模式下就是那个字符本身;
      // `\w` `\d` `\b` `\n` `\1` `\u…` 之类一律当成断开 —— 保守,不去逐个翻译
      if (next !== undefined && !/[A-Za-z0-9]/.test(next)) pushLiteral(next)
      else flush()
      continue
    }

    if (ch === '[') {
      flush()
      i = skipCharClass(pattern, i)
      continue
    }

    if (ch === '(') {
      // 分组里可能有交替、可能整组可选 —— 不下钻,当成一个断点
      flush()
      i = skipGroup(pattern, i)
      continue
    }

    if (ch === '.' || ch === '^' || ch === '$') {
      flush()
      continue
    }

    pushLiteral(ch)
  }
  flush()

  return best.length >= MIN_LITERAL_CHARS ? best : null
}

/**
 * 把字面量变成一个「这篇文本可能命中吗」的判定。
 *
 * 区分大小写时用 `includes`;`-i` 时用**同样带 `i` 标志**的纯字面量正则 ——
 * 这样大小写折叠规则和主正则逐字节一致,不会因为 `toLowerCase` 与 RegExp 的
 * 折叠表不同(如 `İ`、开尔文符号)而漏报。纯字面量正则是线性的,见文件头。
 */
export function literalPrefilter(pattern: string, ignoreCase: boolean): ((text: string) => boolean) | null {
  const literal = requiredLiteral(pattern)
  if (literal === null) return null
  if (!ignoreCase) return (text) => text.includes(literal)
  const re = new RegExp(literal.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&'), 'i')
  return (text) => re.test(text)
}
