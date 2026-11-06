/**
 * 静态代码正文 —— 高亮后的 `<pre><code>`,不挂编辑器。
 *
 * (原 `components/markdown/CodeSource.tsx`。类名从 `markdown-code-source` 改成
 *  `code-source`,样式随之挪到同目录的 `code.css`:它的使用者里 markdown 只占一个。)
 */
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { useI18n } from '../../i18n'
import type { CodeSpan } from './highlight'
import './code.css'

export interface Highlight { code: string; language: string; spans: CodeSpan[] }

/**
 * 高亮是异步的,而流式追加是**前缀稳定**的:旧 spans 对公共前缀依然成立。
 * 所以缓存命中条件是「前缀」而不是「全等」—— 全等在流式下几乎永不命中,
 * 每个 token 都会让整块回退成无色纯文本并被绘制一帧,这正是代码块一闪一闪的成因。
 */
export function reusableSpans(highlight: Highlight | null, code: string, language: string): CodeSpan[] {
  if (!highlight || highlight.language !== language || !code.startsWith(highlight.code)) return []
  if (highlight.code === code) return highlight.spans
  // 严格前缀:最后一行可能正打到一半(半截字符串、未闭合注释),拿旧结果染色会染错。
  // 只采信收尾于旧内容最后一个换行符之前的 span,剩下的下一轮 parse 自然补上。
  const safeEnd = highlight.code.lastIndexOf('\n') + 1
  const cut = highlight.spans.findIndex((span) => span.to > safeEnd)
  return cut < 0 ? highlight.spans : highlight.spans.slice(0, cut)
}

/** Static code DOM shared by ordinary fences and rich-preview source fallbacks. */
export function CodeSource({ code, language, wrap = false }: { code: string; language: string; wrap?: boolean }): ReactNode {
  const { t } = useI18n()
  const [highlight, setHighlight] = useState<Highlight | null>(null)
  const busy = useRef(false)
  useEffect(() => {
    // 一次只跑一个高亮。流式追加时每个 token 都发起会把整块重新 parse 一遍,合起来是
    // O(n²);在飞的那次落地后 setHighlight 会让本 effect 重跑,自然追上最新的 code。
    if (busy.current || (highlight?.code === code && highlight.language === language)) return
    // Grammar/code failures fall back to escaped plain text without affecting the reply.
    if (code.length > 100_000 || !language) return
    busy.current = true
    void import('./highlight').then((module) => module.highlightCode(code, language))
      .then((spans) => { busy.current = false; setHighlight({ code, language, spans }) })
      .catch(() => { busy.current = false })
  }, [code, language, highlight])

  const children: ReactNode[] = []
  let cursor = 0
  for (const span of reusableSpans(highlight, code, language)) {
    if (span.from > cursor) children.push(code.slice(cursor, span.from))
    children.push(<span key={span.from} className={span.className}>{code.slice(span.from, span.to)}</span>)
    cursor = span.to
  }
  children.push(code.slice(cursor))
  return <pre className="code-source scroll-thin" data-wrap={wrap || undefined} tabIndex={0}
    aria-label={t('markdown.codeRegion', { language: language || t('markdown.code') })}><code>{children}</code></pre>
}
