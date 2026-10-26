import { createElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { I18nProvider, type Locale } from '../../../i18n'
import { AgentMarkdown, type AgentMarkdownProps } from '../AgentMarkdown'
import { MarkdownProvider, type MarkdownEnvironment } from '../MarkdownProvider'
import { resolveMarkdownTarget } from '../links'
import { highlightCode } from '../../code/highlight'
import { reusableSpans } from '../../code/CodeSource'
import { canRenderMermaid } from '../mermaid-policy'

function render(content: string, props: Partial<AgentMarkdownProps> = {}, environment: MarkdownEnvironment = {}, locale: Locale = 'en-US'): string {
  return renderToStaticMarkup(createElement(I18nProvider, { initialLocale: locale, children:
    createElement(MarkdownProvider, { value: environment, children: createElement(AgentMarkdown, { content, ...props }) }) }))
}

describe('Agent Markdown', () => {
  it('renders CommonMark and GFM without losing nested content, alignment, or escaped text', () => {
    const html = render([
      '# Agent report', '', '## Summary', '', '**Ready**, *carefully*, ~~obsolete~~, `raw <text>`.', '',
      '> A quote', '> ', '> - Nested item', '',
      '3. Third', '   - Nested bullet', '4. Fourth', '',
      '- [x] Done', '- [ ] Todo', '',
      '| Item | Count |', '| :--- | ---: |', '| alpha | 42 |', '',
      'https://example.com', '', '[Reference][docs]', '', '[docs]: https://example.com/docs', '',
      'A hard break  ', 'next line', '', '---', '', '```unknown-language', '<script>raw text</script>', '```', '',
      '    indented <code>'
    ].join('\n'))
    expect(html).toContain('<strong>Ready</strong>')
    expect(html).toContain('<em>carefully</em>')
    expect(html).toContain('<del>obsolete</del>')
    expect(html).toContain('<code>raw &lt;text&gt;</code>')
    expect(html).toContain('<blockquote>')
    expect(html).toContain('<ol start="3">')
    expect(html).toContain('disabled="" checked=""')
    expect(html).toContain('<td style="text-align:right">42</td>')
    expect(html).toContain('href="https://example.com/docs"')
    expect(html).toContain('<br/>')
    expect(html).toContain('<hr/>')
    expect(html).toContain('&lt;script&gt;raw text&lt;/script&gt;')
    expect(html).toContain('indented &lt;code&gt;')
    expect(html).not.toContain('<script>')
  })

  // 需求：逐字符穷举不能因全套测试的 worker 争用误报超时；扩大预算但不减少覆盖的前缀。
  it('renders every incremental prefix, including unclosed fences and Markdown delimiters', () => {
    const source = '## Live\n\n**bold** and [link](https://example.com)\n\n```ts\nconst answer = 42\n```\n\n| A | B |\n| - | - |\n| 1 | 2 |'
    for (let length = 1; length <= source.length; length++) {
      expect(() => render(source.slice(0, length), { streaming: true })).not.toThrow()
    }
    expect(render('```ts\nconst answer = 42', { streaming: true })).toContain('data-language="ts" data-streaming="true"')
    expect(render('```ts\nconst answer = 42\n```', { streaming: true })).not.toContain('data-language="ts" data-streaming="true"')
    expect(render('```\nconst answer = 42', { streaming: false })).toContain('<code>const answer = 42</code>')
  }, 15_000)

  it('renders streaming through Streamdown and committed content through react-markdown', () => {
    // 双引擎是刻意的:Streamdown 逐块独立解析,跨块引用(脚注、[text][ref])解析不出来,
    // 所以只在流式时用它。接错引擎不会报错,只会悄悄退化,这里把归属钉死。
    expect(render('# Live', { streaming: true })).toContain('streamdown-animated')
    expect(render('# Live')).not.toContain('streamdown-animated')
    // remend 会把打到一半的链接改写成 `streamdown:incomplete-link`,
    // 那是自愈占位符而不是被拦截的链接,不能显示成「此链接无法打开」。
    const partial = render('See [docs](https://exa', { streaming: true })
    expect(partial).not.toContain('markdown-blocked-link')
    expect(partial).not.toContain('streamdown:incomplete-link')
  })

  it('waits for closed diagram fences, including nested and longer fences', () => {
    const environment: MarkdownEnvironment = { codeRenderers: { mermaid: ({ code }): ReactNode => createElement('output', { 'data-diagram': true }, code) } }
    for (const source of ['```mermaid\nflowchart LR\nA --> B', '> ```mermaid\n> A --> B', '````mermaid\nA --> B\n```']) {
      const html = render(source, { streaming: true }, environment)
      expect(html).not.toContain('data-diagram')
      expect(html).toContain('Receiving diagram source')
    }
    for (const source of ['```mermaid\nA --> B\n```', '> ```mermaid\n> A --> B\n> ```', '````mermaid\nA --> B\n````\n\nStill streaming']) {
      expect(render(source, { streaming: true }, environment)).toContain('data-diagram="true"')
    }
  })

  it('renders inline/block math locally and refuses trusted HTML/URL commands', () => {
    const html = render('Inline $E = mc^2$.\n\n$$\n\\frac{1}{2} + \\sqrt{x}\n$$\n\n$\\href{javascript:alert(1)}{bad}$')
    expect(html).toContain('class="katex"')
    expect(html).toContain('class="katex-display"')
    expect(html).toContain('<math')
    expect(html).not.toContain('href="javascript:')
    expect(html).not.toContain('<script')
  })

  it('renders LaTeX-style \\(…\\) and \\[…\\] delimiters but leaves code untouched', () => {
    const html = render('Inline \\(3 \\times 4\\).\n\n\\[\n\\frac{1}{2}\n\\]\n\n`\\(x\\)`')
    expect(html).toContain('class="katex"')
    expect(html).toContain('class="katex-display"')
    expect(html).toContain('\\(x\\)')
  })

  it('keeps heading and footnote IDs unique across messages and localizes footnotes', () => {
    const content = '# Summary\n\n# Summary\n\n[Jump](#summary)\n\nNote[^1] again[^1].\n\n[^1]: Supporting evidence.'
    const html = renderToStaticMarkup(createElement(I18nProvider, { initialLocale: 'zh-CN', children:
      createElement('section', null, createElement(AgentMarkdown, { content }), createElement(AgentMarkdown, { content })) }))
    const ids = [...html.matchAll(/\sid="([^"]+)"/g)].map((match) => match[1])
    expect(new Set(ids).size).toBe(ids.length)
    for (const match of html.matchAll(/href="#([^"]+)"/g)) expect(ids).toContain(match[1])
    for (const match of html.matchAll(/aria-describedby="([^"]+)"/g)) expect(ids).toContain(match[1])
    expect(html).toContain('注释')
    expect(html).toContain('返回引用 1')
    expect(html).not.toContain('Back to reference')
  })

  it('blocks raw HTML, unsafe URLs, path traversal, and unsolicited external images', () => {
    const html = render([
      '<script>alert(1)</script>', '', '<iframe src="https://evil.example"></iframe>', '',
      '[bad](javascript:alert%281%29)', '[encoded](javascript%3Aalert%281%29)', '[escape](../../private.txt)', '',
      '![remote](https://evil.example/pixel.png)', '![bad](data:image/svg+xml;base64,PHN2Zz4=)',
    ].join('\n'), {}, { resolveLink: (url) => resolveMarkdownTarget('docs/readme.md', url), onOpenFile: () => {} })
    expect(html).not.toContain('<script')
    expect(html).not.toContain('<iframe')
    expect(html).not.toContain('<img')
    expect(html).not.toContain('<a ')
    expect(html).not.toContain('javascript')
    expect(html).toContain('Load image')
    expect(html).toContain('markdown-blocked-link')
  })

  it('has reusable compact presentation and localized controls even for unlabeled fences', () => {
    const source = '```\n你好 <world>\n```'
    const html = render(source, { variant: 'compact' }, {}, 'zh-CN')
    expect(html).toContain('data-variant="compact"')
    expect(html).toContain('复制代码')
    expect(html).toContain('自动换行')
    expect(html).toContain('代码块：代码')
    expect(html).not.toContain('Copy code')
    expect(render(source, {}, {}, 'en-US')).toContain('Copy code')
  })

  it('localizes malformed formula hints and prevents heading/footnote ID collisions', () => {
    const html = render('# fn-note\n\n# footnote-label\n\nText[^note]\n\n[^note]: A footnote\n\n$\\frac{$', {}, {}, 'zh-CN')
    const ids = [...html.matchAll(/\sid="([^"]+)"/g)].map((match) => match[1])
    expect(new Set(ids).size).toBe(ids.length)
    expect(html).toContain('公式暂时无法渲染，已保留源码。')
    expect(html).not.toContain('title="ParseError')
  })

  it('highlights known language aliases and keeps unknown/oversized code selectable as text', async () => {
    const code = 'const result: number = 42'
    const spans = await highlightCode(code, 'ts')
    expect(spans.some((span) => span.className.includes('tok-keyword') && code.slice(span.from, span.to) === 'const')).toBe(true)
    expect(await highlightCode(code, 'not-a-language')).toEqual([])
    expect(await highlightCode('x'.repeat(100_001), 'ts')).toEqual([])
  })

  it('keeps highlight colors across the stable prefix while code streams in', () => {
    // 用户报的「代码块一闪一闪」就出在这里:高亮是异步的,以前用全等做缓存键,
    // 流式下每个 token 都命中不了,整块回退成无色纯文本并被绘制一帧。
    const cached = { code: 'const a\nconst b', language: 'ts', spans: [
      { from: 0, to: 5, className: 'tok-keyword' }, { from: 8, to: 13, className: 'tok-keyword' }] }
    expect(reusableSpans(cached, cached.code, 'ts')).toHaveLength(2)
    // 又追进来一个 token:仍是前缀,首行的颜色必须留住,不能整块回退。
    // 但最后一行还没打完,收尾越过末尾换行符的 span 一律不采信,免得染错色。
    expect(reusableSpans(cached, 'const a\nconst bb', 'ts')).toEqual([cached.spans[0]])
    expect(reusableSpans(cached, 'const a\nconst b', 'js')).toEqual([])
    expect(reusableSpans(cached, 'const c', 'ts')).toEqual([])
    expect(reusableSpans(null, 'const a', 'ts')).toEqual([])
  })

  it('keeps Mermaid resource nodes and configuration from bypassing image consent', () => {
    expect(canRenderMermaid('flowchart LR\n A[Agent] --> B[Tools]')).toBe(true)
    expect(canRenderMermaid('sequenceDiagram\n Alice->>Bob: Hello<br/>again')).toBe(true)
    for (const source of [
      'flowchart LR\n A@{ img: "https://example.com/pixel" }',
      'flowchart LR\n A@{ "\\u0069mg": "https://example.com/pixel" }',
      '%%{init: {"securityLevel":"loose"}}%%\nflowchart LR\n A --> B',
      '---\nconfig:\n  themeCSS: "@import url(https://example.com)"\n---\nflowchart LR',
      'flowchart LR\n classDef remote fill:u\\72l(https://example.com)',
      'flowchart LR\n A[<img src="https://example.com">]',
    ]) expect(canRenderMermaid(source), source).toBe(false)
  })
})
