/**
 * 灾难性回溯的**静态筛查** —— `Grep` 编译模型给的正则之前的最后一道闸。
 *
 * ## 为什么光靠截断行长救不了
 *
 * 原本的设计只有三道缓解:行截到 2000 字符、每 N 个文件查中断、整次搜索一个墙钟预算。
 * 前两道都拦不住 `(a+)+$` 这一类:
 *
 * - `^(a+)+$` 撞上 2000 个 `a` 后面跟一个 `b`,回溯的分支数是 **2^1999**。
 *   把行从 5000 截到 2000 只是把「宇宙热寂」缩短成「宇宙热寂」。
 * - `RegExp.prototype.test` 是**原子的**:V8 跑进去就出不来,中间没有任何
 *   可以插进去查 `signal` 或查时钟的地方。单线程,连 abort 事件都发不出去,
 *   用户只能强杀整个应用。
 *
 * 所以唯一的正解是**根本不在主线程跑那个正则** —— 这一步现在由 `grep-regex.ts`
 * 的 worker 隔离承担:编译与匹配都在子线程里,真实墙钟到期就把 worker terminate。
 * 本文件这一道闸因此从「唯一承重」退化成一道**便宜的快速拒绝**:它兜住最常见的
 * 危险写法并给模型一句「怎么改」,漏网的写法由 worker 的 deadline 兜底,
 * **不会再挂死整个应用**。
 *
 * ## 判据
 *
 * 只认一个家族,也是实际会出事的那个家族:**一个被无界量词修饰的分组,
 * 它的内部还有另一个无界量词**(`(a+)+`、`(\s*\w+)*`、`(\d+\.)+`、`(a{2,})+`)。
 * 这种嵌套让同一段输入有指数级多种切分方式,匹配失败时全部要试一遍。
 *
 * **刻意不管交替**:`(a|aa)+` 这类写法同样能指数级回溯,但它不在本文件判据的
 * 范围内 —— 静态判不出「交替分支能否匹配同一串」。这类写法交给 worker 隔离:
 * 到期即 terminate,代价是几秒的一次「部分结果 + 超时说明」,而不是主进程无响应。
 * 有 worker 隔离之前,这里的漏报代价才是「整个应用无响应」;现在不是了。
 *
 * ★ 误报的代价是**可见且可操作的**:模型收到一条说明怎么改写的错误,重试一次就行。
 * 所以这道闸宁可紧一点。
 */

/** 从 `[` 开始跳过一个字符类,返回 `]` 的下标(没闭合就返回末尾)。 */
function skipCharClass(p: string, open: number): number {
  // `[]a]` 里第一个 `]` 是字面量,`[^]a]` 同理
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

/**
 * `p[i]` 处是不是一个**无界**量词。返回它的字符长度,不是就返回 0。
 *
 * `{2,}` 无界;`{2}` / `{2,5}` 有界 —— 后两者的工作量有上限,不算风险。
 */
function unboundedQuantifierAt(p: string, i: number): number {
  const ch = p[i]
  if (ch === '*' || ch === '+') return 1
  if (ch !== '{') return 0
  const close = p.indexOf('}', i)
  if (close === -1) return 0
  return /^\d+,\s*$/.test(p.slice(i + 1, close)) ? close - i + 1 : 0
}

/** 这一段里有没有无界量词(跳过转义和字符类)。 */
function containsUnbounded(body: string): boolean {
  for (let i = 0; i < body.length; i++) {
    const ch = body[i]
    if (ch === '\\') {
      i++
      continue
    }
    if (ch === '[') {
      i = skipCharClass(body, i)
      continue
    }
    if (unboundedQuantifierAt(body, i) > 0) return true
  }
  return false
}

/**
 * 有风险就返回一段**给模型看的**说明,没有就返回 `null`。
 *
 * 说明里必须包含「改成什么」—— 只说「不行」的话,模型会原样重试,
 * 或者换一个同样危险的写法。
 */
export function redosRisk(pattern: string): string | null {
  const stack: number[] = []

  for (let i = 0; i < pattern.length; i++) {
    const ch = pattern[i]
    if (ch === '\\') {
      i++
      continue
    }
    if (ch === '[') {
      i = skipCharClass(pattern, i)
      continue
    }
    if (ch === '(') {
      stack.push(i)
      continue
    }
    if (ch !== ')') continue

    const start = stack.pop()
    if (start === undefined) continue
    if (unboundedQuantifierAt(pattern, i + 1) === 0) continue

    const body = pattern.slice(start + 1, i)
    if (!containsUnbounded(body)) continue

    return (
      `This pattern risks catastrophic backtracking and was refused (it could freeze the whole app for minutes). ` +
      `The problem is the group "(${body})": it contains an unbounded quantifier (*, +, or {n,}) and is itself ` +
      `wrapped in another unbounded quantifier, so a failed match has to try exponentially many splits. ` +
      `Fix it by dropping one of the two quantifiers (write (a+)+ as just a+), by bounding the inner one ` +
      `(e.g. {1,20}), or by adding a more specific literal to narrow the search.`
    )
  }

  return null
}
