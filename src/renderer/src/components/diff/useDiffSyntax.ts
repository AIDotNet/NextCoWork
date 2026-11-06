/**
 * 给一份 diff 算语法高亮。
 *
 * 需求:diff 里读代码同样要有「关键字 / 字符串 / 注释各一个颜色」。但 `views/git`
 * 那套 diff 是**选中文件时同步渲染**的,当年写下「故意不做语法高亮」这条结论
 * (原 `views/git/diff-model.ts` 文件头)正是因为同步跑一遍语法解析能把面板卡住
 * 半秒。现在的做法不是放弃高亮,而是把它整个挪出首帧:
 *
 *   1. 语法集(`@codemirror/language-data`)走**动态 import**,首帧永远是
 *      「有 diff、没有颜色」,不是白屏也不是等待;
 *   2. 超出 `MAX_*` 的 diff 直接不高亮 —— 拿不到颜色远好过卡住一次交互;
 *   3. 一次只跑一个;在飞的那次落地后 `setState` 会让本 effect 重跑,自然追上最新的
 *      那一份(和 `CodeSource` 同一套写法,那里写了为什么不是「每次都发一个」)。
 *
 * ★ 缓存命中用的是 `reusableSpans` 的**前缀**规则,不是全等。工具卡里的 diff 在
 *   `new_string` 流式追加时每一帧都会重算,全等永远不命中 —— 表现为一边流一边
 *   「有色 → 无色 → 有色」地闪,而这恰好发生在用户正盯着看的时刻。
 *
 * 返回值按行对齐入参 `lines`:第 i 项就是第 i 行的**行内** token,没有就是空数组。
 */
import { useEffect, useMemo, useRef, useState } from 'react'
import { reusableSpans, type Highlight } from '../code/CodeSource'
import type { CodeSpan } from '../code/highlight'
import type { DiffLine } from './model'
import { diffSides, tokensByLine } from './syntax'

/** 与 `highlightCode` 内部同一档:再大它本来也会放弃,这里提前省掉一次 import。 */
const MAX_CHARS = 100_000
/** 行数另算一道闸:几千行 diff 的 token 数量会把合并那一步也拖成可感知的卡顿。 */
const MAX_LINES = 4_000

const NO_TOKENS: CodeSpan[] = []

export function useDiffSyntax(lines: DiffLine[], language: string): CodeSpan[][] | null {
  const sides = useMemo(() => diffSides(lines), [lines])
  const [done, setDone] = useState<{ old: Highlight; new: Highlight } | null>(null)
  const busy = useRef(false)

  useEffect(() => {
    if (language === '') return
    if (sides.oldText.length + sides.newText.length > MAX_CHARS) return
    if (lines.length > MAX_LINES) return
    if (busy.current) return
    if (done !== null && done.old.language === language
      && done.old.code === sides.oldText && done.new.code === sides.newText) return
    busy.current = true
    void import('../code/highlight')
      .then(async (module) => ({
        old: { code: sides.oldText, language, spans: await module.highlightCode(sides.oldText, language) },
        new: { code: sides.newText, language, spans: await module.highlightCode(sides.newText, language) }
      }))
      .then((next) => { busy.current = false; setDone(next) })
      .catch(() => { busy.current = false })
  }, [sides, language, lines.length, done])

  return useMemo(() => {
    if (done === null) return null
    const oldTokens = tokensByLine(sides.oldText, reusableSpans(done.old, sides.oldText, language))
    const newTokens = tokensByLine(sides.newText, reusableSpans(done.new, sides.newText, language))
    return sides.index.map((at) =>
      at.side === null ? NO_TOKENS : (at.side === 'old' ? oldTokens : newTokens)[at.line] ?? NO_TOKENS
    )
  }, [done, sides, language])
}
