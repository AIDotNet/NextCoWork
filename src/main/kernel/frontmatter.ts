/**
 * YAML 前置块解析 —— 和 `text.ts` 平级的「单一答案」。
 *
 * Skill 加载器(`skill/load.ts`)和子代理加载器(`agent/load.ts`)**两处**都要读
 * frontmatter。而「两个来源必须给出同一个答案」正是 `text.ts` 存在的理由
 * (见它的文件头):两边对「`tools: Read, Grep` 算不算一个列表」的理解一旦分叉,
 * 就会出现「Skill 里写逗号能认、agent 里写逗号认成一个长字符串」这种
 * 不报错、只是行为不一致的洞。
 *
 * ## 为什么手写,不引 js-yaml / yaml
 *
 * 要处理的只是**一张扁平 map**,值是标量或列表 —— 没有嵌套、没有锚点、
 * 没有多文档。而 SKILL.md 来自 zip / git,是**不可信输入**:一个完整 YAML
 * 解析器的历史攻击面(alias 炸弹、merge key `<<`、类型标签 `!!python/object`)
 * 是白送的风险。这个解析器**只能产出 `string | string[]`**,所以最坏的结果
 * 是一个错误的字符串,而不是一次代码执行或一次内存耗尽。
 *
 * ## 支持的子集(这就是契约,别扩)
 *
 * - 首行 `---`(容忍 BOM 和 CRLF),以单独一行 `---` 或 `...` 收尾
 * - 键 `^[A-Za-z_][A-Za-z0-9_-]*$`,不匹配的行**跳过不报错**
 * - 值:裸标量、`'...'` / `"..."`、`[a, b]` 流式序列、`- x` 块式序列
 * - 块标量 `|`(字面)/ `>`(折叠),含截断指示 `-` / `+` 与缩进指示 `1`-`9`
 * - **一层**嵌套 map,扁平成 `parent.child` 一个键(`metadata:` 下的 author / tags)
 * - `true` / `false` **保持字符串**,由 `fmBool` 统一 coerce
 *
 * ## 明确不支持,遇到就跳过并记入 `skipped`
 *
 * 两层以上的嵌套、锚点别名(`&a` / `*a`)、多文档(`---` 再开一段)。
 * ★ **行内 `#` 注释不剥离** —— Claude Code 自己的 description 里就带 `#`
 * (「用 `#` 开头的行是注释」这类说明),剥了会把语义从中间截断。
 *
 * ## 为什么块标量要认
 *
 * `description: |` 是官方 Skill 写多行说明最常见的形状。跳过它的代价不止「少一个
 * 键」:`installSkillZip` 把 `skipped` 非空当**硬失败**(它不装一个自己读不全的包),
 * 于是整包装不上,而用户只看到一句「SKILL.md frontmatter 无法识别」—— 从这句话
 * 完全看不出问题出在 description 的写法上。
 *
 * ★ 产出仍然只有 `string | string[]` —— 块标量只是「一种换行写法」,一层嵌套只是
 *   「一种键名写法」,都不引入新类型。上面那条「最坏结果是一个错误的字符串」的
 *   保证原样成立,`metadata:` 这种纯元信息也不再连坐掉整个安装。
 * ★ 写回去是双引号里的 `\n`,不是 `|`:往返后**内容**逐字相等,只有形状变了。
 *   要真写回 `|`,得把截断与缩进指示一起往返,否则结尾换行会在存一次之后变样。
 *
 * ## `parseFrontmatter` 永不 throw
 *
 * 没有前置块就返回 `{ data: {}, body: src, skipped: [] }`。★ 一个坏掉的
 * SKILL.md 不该让整次目录扫描失败 —— 「这条有效吗」是**加载器**的判断
 * (缺 `name` / `description` 即无效),不是解析器的。解析器只负责
 * 「尽力读出能读的,剩下的照实记下来」。
 */
import { stripControlChars } from './text'

/** 前置块本身的字符上限。超出的部分不解析,但正文位置仍然找得对。 */
const FM_BLOCK_MAX = 8 * 1024
/** 找结束标记时最多往下看多少字符。防一个没有结束标记的大文件把整个文件当成前置块扫一遍。 */
const FM_SCAN_MAX = 64 * 1024
/** 键的数量上限 */
const FM_KEYS_MAX = 64
/** 单个值的字符上限。description 会直接进系统提示词,长度必须有个头。 */
const FM_VALUE_MAX = 4 * 1024
/** 列表的项数上限 */
const FM_LIST_MAX = 64

const KEY_RE = /^[A-Za-z_][A-Za-z0-9_-]*$/

/**
 * 块标量的头:`|` 或 `>`,后面可跟截断指示(`-` / `+`)和缩进指示(`1`-`9`),
 * 两种顺序(`|2-` 与 `|-2`)都合法。
 *
 * ★ `needsQuote` 直接复用这个常量,不是抄一个形状近似的正则。两边一旦分叉,
 *   就会有一个「裸值恰好长得像块标量头」的字符串被原样写出去、再读回来时
 *   把后面几行吃掉 —— 往返契约就这么破了。
 */
const BLOCK_RE = /^([|>])(?:([-+])([1-9])?|([1-9])([-+])?)?$/

/**
 * ★ 这三个键**必须显式拒绝**,不能只靠 `Object.create(null)`。
 *
 * `Object.create(null)` 让产出的对象自己没有原型,但 `data` 会被下游拷进
 * 普通对象、展开进字面量、`JSON.parse(JSON.stringify(...))` 转一圈 ——
 * 只要中途落到一个**有原型**的对象上,`obj['__proto__'] = v` 就真的污染了。
 * 一份从 git 装来的 SKILL.md 里写一行 `__proto__: polluted` 是零成本的。
 */
const FORBIDDEN_KEYS = new Set(['__proto__', 'constructor', 'prototype'])

export interface Frontmatter {
  readonly data: Readonly<Record<string, string | string[]>>
  /** 前置块之后的全部内容。没有前置块时就是原文。 */
  readonly body: string
  /** 不支持的语法、被截断的值 —— 由调用方决定是 warn 还是忽略 */
  readonly skipped: readonly string[]
}

const EMPTY: Frontmatter = {
  data: Object.freeze(Object.create(null) as Record<string, string | string[]>),
  body: '',
  skipped: Object.freeze([])
}

/** 去掉引号,并处理引号内那一层最基本的转义 */
function unquote(raw: string): string {
  if (raw.length >= 2 && raw.startsWith("'") && raw.endsWith("'")) {
    // YAML 单引号里,两个连续单引号表示一个字面单引号
    return raw.slice(1, -1).replace(/''/g, "'")
  }
  if (raw.length >= 2 && raw.startsWith('"') && raw.endsWith('"')) {
    /*
      ★ **单次扫描,不能用链式 `replace`**。曾经这里是
      `.replace(/\\"/g,'"').replace(/\\n/g,'\n').replace(/\\t/g,'\t').replace(/\\\\/g,'\\')`,
      而那是错的:一个字面反斜杠按规矩要写成 `\\`,于是 `a\\nb`(想表达 `a` + 反斜杠 + `n`)
      里的第二个反斜杠会先被 `/\\n/` 抓去当成换行转义,读出来变成 `a` + 反斜杠 + 换行。

      只读的时候这只是个冷门的边缘情况;有了 `serializeFrontmatter` 之后它变成
      **写进去再读出来就损坏** —— 往返契约要求这里必须真的可逆。
    */
    const inner = raw.slice(1, -1)
    let out = ''
    for (let i = 0; i < inner.length; i++) {
      if (inner[i] !== '\\') {
        out += inner[i]
        continue
      }
      const next = inner[++i]
      // 末尾的孤立反斜杠:原样留着,不吞
      if (next === undefined) return `${out}\\`
      if (next === 'n') out += '\n'
      else if (next === 't') out += '\t'
      else if (next === '"' || next === '\\') out += next
      // 认不出的转义序列原样保留 —— 猜错比留着更难查
      else out += `\\${next}`
    }
    return out
  }
  return raw
}

/** `[a, b, "c d"]` → `['a', 'b', 'c d']`。★ 不支持项里带逗号的引号串,那需要真解析器。 */
function parseFlowSeq(raw: string): string[] {
  return raw
    .slice(1, -1)
    .split(',')
    .map((p) => unquote(p.trim()))
    .filter((p) => p !== '')
}

export function parseFrontmatter(src: string): Frontmatter {
  // BOM 与 CRLF:两者都会让「首行是不是恰好 ---」判断失败,而失败的表现是
  // 整个前置块被当成正文 —— Skill 变成「没有 name」于是静默消失。
  const text = src.replace(/^\uFEFF/, '').replace(/\r\n?/g, '\n')
  const lines = text.split('\n')

  if ((lines[0] ?? '').trim() !== '---') return { ...EMPTY, body: src }

  const skipped: string[] = []

  // ── 先找结束标记,确定正文从哪儿开始 ──
  let end = -1
  let scanned = 0
  for (let i = 1; i < lines.length; i++) {
    const line = lines[i] ?? ''
    scanned += line.length + 1
    if (scanned > FM_SCAN_MAX) break
    const t = line.trim()
    if (t === '---' || t === '...') {
      end = i
      break
    }
  }
  // 没有结束标记 = 这根本不是一个前置块。★ 绝不能把整个文件当成前置块吞掉:
  // 那样正文就没了,而模型收到的是一份空 Skill。
  if (end === -1) return { ...EMPTY, body: src, skipped: ['前置块没有结束标记(---),整块按正文处理'] }

  const body = lines.slice(end + 1).join('\n')

  // ── 解析块内的键值 ──
  const acc = new Map<string, string | string[]>()
  /** 上一个「值为空」的键 —— 块式序列(`- x`)要挂到它上面 */
  let pendingListKey: string | null = null
  /** 上一个「值为空」的**顶层**键 —— 缩进的 `k: v` 作为它的子键挂上去 */
  let parentKey: string | null = null
  /** 子层的缩进宽度,由第一个子键定下来。0 = 还没见过子键 */
  let childIndent = 0
  let used = 0
  let overflowed = false

  for (let i = 1; i < end; i++) {
    const line = lines[i] ?? ''
    used += line.length + 1
    if (used > FM_BLOCK_MAX) {
      overflowed = true
      break
    }

    const indented = /^\s/.test(line)
    const t = line.trim()

    if (t === '') continue

    // 整行注释:静默跳过。这是标准 YAML,跳过它不算「丢了东西」。
    if (t.startsWith('#')) continue

    // ── 块式序列的一项 ──
    if (t.startsWith('- ') || t === '-') {
      if (pendingListKey === null) {
        skipped.push(`第 ${String(i + 1)} 行:顶层的列表项没有对应的键,已忽略`)
        continue
      }
      const item = unquote(t.slice(1).trim())
      if (item === '') continue
      const cur = acc.get(pendingListKey)
      const list = Array.isArray(cur) ? cur : []
      if (list.length >= FM_LIST_MAX) {
        skipped.push(`"${pendingListKey}" 的列表超过 ${String(FM_LIST_MAX)} 项,多余的已丢弃`)
        continue
      }
      list.push(clampValue(item, pendingListKey, skipped))
      acc.set(pendingListKey, list)
      continue
    }

    /*
      ★ 有缩进而又不是列表项 —— 一层嵌套 map,扁平成 `parent.child` 收下。

      只收**一层**,且值仍然只有 `string | string[]`:这样「最坏结果是一个错误的
      字符串」的保证没有松动,而现实里的 SKILL.md 也只用到这一层(`metadata:` 下
      挂 author / version / tags)。再深一层就跳过 —— 那种形状真需要时,该换的是
      数据模型,不是在这里多猜一层。
    */
    const indent = indented ? line.length - line.trimStart().length : 0
    if (indented) {
      if (parentKey === null) {
        skipped.push(`第 ${String(i + 1)} 行:缩进的行没有对应的父键,已忽略`)
        pendingListKey = null
        continue
      }
      if (childIndent === 0) childIndent = indent
      if (indent !== childIndent) {
        skipped.push(`第 ${String(i + 1)} 行:只支持一层嵌套,更深的层级已忽略`)
        pendingListKey = null
        continue
      }
    }

    const colon = t.indexOf(':')
    if (colon <= 0) {
      skipped.push(`第 ${String(i + 1)} 行:不是 "键: 值" 的形状,已忽略`)
      pendingListKey = null
      continue
    }

    const bare = t.slice(0, colon).trim()
    const rawValue = t.slice(colon + 1).trim()
    pendingListKey = null
    if (!indented) {
      parentKey = null
      childIndent = 0
    }

    if (!KEY_RE.test(bare)) {
      skipped.push(`第 ${String(i + 1)} 行:键名 "${bare}" 不合法,已忽略`)
      continue
    }
    if (FORBIDDEN_KEYS.has(bare)) {
      // ★ 记下来而不是静默忽略:出现这个键几乎不可能是手滑
      skipped.push(`键 "${bare}" 出于安全原因被拒绝`)
      continue
    }
    const key = indented && parentKey !== null ? `${parentKey}.${bare}` : bare
    if (!acc.has(key) && acc.size >= FM_KEYS_MAX) {
      skipped.push(`键的数量超过 ${String(FM_KEYS_MAX)} 个,"${key}" 及其后已丢弃`)
      continue
    }
    // 父键先前按「空值」记了个占位;既然它下面挂着子键,它就是一张 map,不是空串。
    if (indented && parentKey !== null && acc.get(parentKey) === '') acc.delete(parentKey)

    // 锚点 / 别名:不支持(值已经没了,只能记一笔)
    if (rawValue.startsWith('&') || rawValue.startsWith('*')) {
      skipped.push(`"${key}":不支持锚点或别名,已忽略`)
      continue
    }
    const block = BLOCK_RE.exec(rawValue)
    if (block !== null) {
      const folded = block[1] === '>'
      const chomp = block[2] ?? block[5] ?? ''
      // 缩进指示是**相对父节点**的列数,所以要叠上键本身的缩进:顶层的 `|2` 是
      // 第 2 列,`metadata:` 下某个子键的 `|2` 是第 4 列。0 = 没给,看第一行。
      const digits = Number(block[3] ?? block[4] ?? 0)
      let blockIndent = digits === 0 ? 0 : indent + digits
      const collected: string[] = []
      let j = i + 1
      for (; j < end; j++) {
        const next = lines[j] ?? ''
        const blank = next.trim() === ''
        if (!blank) {
          const width = next.length - next.replace(/^[ \t]+/, '').length
          // 缩进退回到键这一层(或更浅)= 块到此为止。★ 这一行不能计进 `used`,
          // 也不能吃掉 —— 它归外层循环,下一轮还要当键值解析。
          if (width <= indent) break
          if (blockIndent !== 0 && width < blockIndent) break
          if (blockIndent === 0) blockIndent = width
        }
        used += next.length + 1
        if (used > FM_BLOCK_MAX) {
          overflowed = true
          break
        }
        collected.push(blank ? '' : next.slice(blockIndent))
      }
      i = j - 1
      const text = folded ? foldLines(collected) : collected.join('\n')
      acc.set(key, clampValue(chompBlock(text, chomp), key, skipped))
      if (overflowed) break
      continue
    }

    if (rawValue === '') {
      // 可能是块式序列的头、一张嵌套 map 的头,也可能就是一个空值。先记成空串,
      // `- x` 来了转成列表,缩进的 `k: v` 来了就把这个占位删掉。
      acc.set(key, '')
      pendingListKey = key
      if (!indented) parentKey = key
      continue
    }
    if (rawValue.startsWith('[') && rawValue.endsWith(']')) {
      const items = parseFlowSeq(rawValue).slice(0, FM_LIST_MAX)
      acc.set(
        key,
        items.map((v) => clampValue(v, key, skipped))
      )
      continue
    }
    acc.set(key, clampValue(unquote(rawValue), key, skipped))
  }

  if (overflowed) {
    skipped.push(`前置块超过 ${String(FM_BLOCK_MAX / 1024)}KB,超出部分未解析`)
  }

  // ★ 收尾落到无原型对象上。中间用 Map 累积,`acc.set('__proto__', v)`
  // 对 Map 来说只是一个普通的键,不会污染任何东西。
  const data = Object.create(null) as Record<string, string | string[]>
  for (const [k, v] of acc) data[k] = v

  return { data: Object.freeze(data), body, skipped: Object.freeze(skipped) }
}

/**
 * 折叠式(`>`)的换行规则,三条缺一不可:相邻非空行之间的换行折成一个空格;
 * n 个空行变成 n 个换行;**更深缩进的行前后保持字面换行**。最后这条是段落里
 * 夹一段代码/列表时唯一能让它不被揉成一行的办法。
 */
function foldLines(collected: readonly string[]): string {
  let out = ''
  let started = false
  let breaks = 0
  let previousDeeper = false
  for (const line of collected) {
    if (line.trim() === '') {
      breaks++
      continue
    }
    const deeper = /^[ \t]/.test(line)
    if (!started) {
      out = line
      started = true
    } else if (breaks > 0) {
      out += '\n'.repeat(breaks) + line
    } else {
      out += (deeper || previousDeeper ? '\n' : ' ') + line
    }
    breaks = 0
    previousDeeper = deeper
  }
  return started ? out + '\n'.repeat(breaks) : ''
}

/** 截断指示:`-` 去掉全部结尾换行,`+` 原样留着,缺省(clip)留一个。 */
function chompBlock(text: string, chomp: string): string {
  if (text.trim() === '') return ''
  const content = `${text}\n`
  if (chomp === '+') return content
  if (chomp === '-') return content.replace(/\n+$/, '')
  return content.replace(/\n+$/, '\n')
}

/** 削控制字符 + 限长。★ 值会直接进系统提示词,这两步都不能省。 */
function clampValue(v: string, key: string, skipped: string[]): string {
  const clean = stripControlChars(v)
  if (clean.length <= FM_VALUE_MAX) return clean
  skipped.push(`"${key}" 的值超过 ${String(FM_VALUE_MAX / 1024)}KB,已截断`)
  return clean.slice(0, FM_VALUE_MAX)
}

/** 取一个字符串值。写成列表的话返回 `undefined` —— 拼起来是猜,猜错比缺失更难查。 */
export function fmString(fm: Frontmatter, key: string): string | undefined {
  const v = fm.data[key]
  if (typeof v !== 'string') return undefined
  const t = v.trim()
  return t === '' ? undefined : t
}

/**
 * 取一个列表值。
 *
 * ★ 「逗号分隔的字符串」与「YAML 数组」在**这一个函数里**合流,不在两个加载器
 * 里各写一遍。用户会把 Claude Code 的 agent 文件原样粘过来,里面写的是
 * `tools: Read, Grep, Glob`(一个裸标量);而 Skill 的 `allowed-tools`
 * 更常写成 `[Read, Grep]`。两种都得认,且必须认成同一个东西。
 */
export function fmList(fm: Frontmatter, key: string): string[] | undefined {
  const v = fm.data[key]
  if (v === undefined) return undefined
  if (Array.isArray(v)) {
    const out = v.map((s) => s.trim()).filter((s) => s !== '')
    return out.length === 0 ? undefined : out
  }
  const out = v
    .split(',')
    .map((s) => s.trim())
    .filter((s) => s !== '')
  return out.length === 0 ? undefined : out
}

/** `true` / `false` 在解析时保持字符串,coerce 只在这里发生。认不出返回 `undefined`。 */
export function fmBool(fm: Frontmatter, key: string): boolean | undefined {
  const v = fmString(fm, key)?.toLowerCase()
  if (v === undefined) return undefined
  if (v === 'true' || v === 'yes' || v === 'on' || v === '1') return true
  if (v === 'false' || v === 'no' || v === 'off' || v === '0') return false
  return undefined
}

export const FM_LIMITS = {
  FM_BLOCK_MAX,
  FM_SCAN_MAX,
  FM_KEYS_MAX,
  FM_VALUE_MAX,
  FM_LIST_MAX
} as const

// ─────────────────────────────────────────────────────────────
// 序列化 —— `parseFrontmatter` 的逆函数
// ─────────────────────────────────────────────────────────────

/**
 * 需要加引号才能原样读回来的裸标量。★ 每一条都对应 `parseFrontmatter` 里一段
 * 真实的分支,不是照着 YAML 规范抄的:
 *
 * - `''`          → 空值那条分支会把它变成「块式序列的头」(`pendingListKey`)
 * - 首尾有空白    → 解析时 `t.slice(colon + 1).trim()` 会削掉
 * - `[` … `]`     → 被当成流式序列
 * - `&` / `*` 开头 → 被当成锚点 / 别名并跳过
 * - `|` / `>` 形状 → 被当成块标量的头,会把后面的缩进行一起吃进来
 * - `- ` 开头      → 在块式列表的上下文里会被读成列表项
 * - 含换行或 tab   → 一行放不下,只能靠双引号里的 `\n` / `\t`
 * - `#` 开头       → 整行会被当成注释
 *
 * ★ 值里含 `: ` **不需要**加引号:解析取的是第一个冒号,`k: foo: bar` 读回来
 *   就是 `foo: bar`。这一条是实测出来的,别凭 YAML 直觉再加回去。
 * ★ `true` / `123` 这类**也不需要**:这个解析器只产出字符串,coerce 全在
 *   `fmBool` 里,不存在「读回来变成布尔」的可能。
 */
function needsQuote(v: string): boolean {
  if (v === '') return true
  if (v !== v.trim()) return true
  if (/[\n\t]/.test(v)) return true
  if (v.startsWith('[') && v.endsWith(']')) return true
  if (v.startsWith('&') || v.startsWith('*') || v.startsWith('#')) return true
  if (BLOCK_RE.test(v)) return true
  if (v.startsWith('- ') || v === '-') return true
  return false
}

/** 双引号里那一层转义 —— 和 `unquote` 的双引号分支严格互为逆运算。 */
function quote(v: string): string {
  const escaped = v
    .replace(/\\/g, '\\\\')
    .replace(/"/g, '\\"')
    .replace(/\n/g, '\\n')
    .replace(/\t/g, '\\t')
  return `"${escaped}"`
}

function scalar(v: string): string {
  return needsQuote(v) ? quote(v) : v
}

/** 写一个键值对。`pad` 是缩进 —— 嵌套子键和它的列表项都要跟着缩。 */
function emitEntry(name: string, value: string | string[] | undefined, pad: string, lines: string[]): void {
  if (typeof value === 'string') {
    lines.push(`${pad}${name}: ${scalar(value)}`)
    return
  }
  if (!Array.isArray(value)) return
  const items = value.slice(0, FM_LIST_MAX)
  if (items.length === 0) {
    // `[]` 能原样读回一个空数组（`parseFlowSeq` 把空串过滤掉了）。
    lines.push(`${pad}${name}: []`)
    return
  }
  // 流式序列按逗号切,所以只要有一项含逗号（或方括号、或首尾空白），整列退块式。
  const flowSafe = items.every((s) => !/[,[\]]/.test(s) && s === s.trim() && s !== '' && !/[\n\t]/.test(s))
  if (flowSafe) {
    lines.push(`${pad}${name}: [${items.join(', ')}]`)
    return
  }
  lines.push(`${pad}${name}:`)
  for (const item of items) lines.push(`${pad}  - ${scalar(item)}`)
}

export interface SerializeOptions {
  /** 优先输出的键序;其余键按字母序跟在后面。 */
  order?: readonly string[]
}

/**
 * 把一张扁平 map 写回 YAML 前置块 —— `parseFrontmatter` 的**逆函数**。
 *
 * ★ 契约:`parseFrontmatter(serializeFrontmatter(data, body)).data` 与 `data` 逐键相等。
 *   表单保存一次就丢一个字段,是那种当场看不出、三天后才发现的损坏。
 *
 * ★ **只输出这个解析器读得回来的子集**,照着它的限制写,而不是照着 YAML 规范写:
 *   不产出块标量(多行值走双引号里的 `\n`)、含逗号的列表退回块式
 *   (`parseFlowSeq` 是 `.split(',')`)。
 *
 * ★ 带一个点的键(`metadata.author`)写回成一层嵌套 map,和解析时的扁平化互逆。
 *   **不写回去就是真的丢**:编辑器读一次写一次,`metadata:` 整块会在用户只改了
 *   description 的那一次保存里消失。同名的标量与 map 无法同时表示,此时 map 赢
 *   (`metadata` 与 `metadata.author` 同在 → 只写后者)。
 *
 * ★ **正文紧贴着结束标记写,不额外空一行**。解析时 body 取的是结束标记的下一行起
 *   到结尾,所以 `---\n\nbody` 读回来的 body 自带一个前导换行 —— 如果这里再补一个
 *   空行,而 body 又是上一次 parse 的产物,那么每存一次就多一个空行,会一直累积。
 *
 * 键序稳定是刻意的:每存一次就换一次顺序,git diff 会变成一片噪音。
 */
export function serializeFrontmatter(
  data: Readonly<Record<string, string | string[] | undefined>>,
  body: string,
  options?: SerializeOptions
): string {
  const order = options?.order ?? []
  const named = Object.keys(data).filter(
    (k) =>
      data[k] !== undefined &&
      k.split('.').every((part) => KEY_RE.test(part) && !FORBIDDEN_KEYS.has(part)) &&
      k.split('.').length <= 2
  )
  // 同名的标量与 map 不能同时写出去:`metadata: x` 后面再跟一个 `metadata:` 块,
  // 读回来只会剩一个。map 赢 —— 标量那半是占位,子键才是内容。
  const parents = new Set(named.filter((k) => k.includes('.')).map((k) => k.slice(0, k.indexOf('.'))))
  const keys = named
    .filter((k) => k.includes('.') || !parents.has(k))
    .sort((a, b) => {
      const ia = order.indexOf(a)
      const ib = order.indexOf(b)
      if (ia !== -1 && ib !== -1) return ia - ib
      if (ia !== -1) return -1
      if (ib !== -1) return 1
      return a.localeCompare(b)
    })

  const lines: string[] = []
  const written = new Set<string>()
  let count = 0
  for (const key of keys) {
    if (count >= FM_KEYS_MAX) break
    if (!key.includes('.')) {
      emitEntry(key, data[key], '', lines)
      count++
      continue
    }
    // 子键在排序后天然连成一片(都带同一个 `parent.` 前缀),整组在第一个子键
    // 的位置一次写完 —— 否则父键会被写成两个 `metadata:` 块。
    const parent = key.slice(0, key.indexOf('.'))
    if (written.has(parent)) continue
    written.add(parent)
    lines.push(`${parent}:`)
    for (const child of keys) {
      if (count >= FM_KEYS_MAX) break
      if (!child.startsWith(`${parent}.`)) continue
      emitEntry(child.slice(parent.length + 1), data[child], '  ', lines)
      count++
    }
  }

  const normalizedBody = body.replace(/\r\n?/g, '\n')
  // 没有任何键时不写前置块 —— 命令文件本来就允许没有 frontmatter。
  if (lines.length === 0) return normalizedBody
  return `---\n${lines.join('\n')}\n---\n${normalizedBody}`
}
