import { createContext, memo, useContext, useId, useMemo, useRef, useState, type ReactNode, type RefObject } from 'react'
import ReactMarkdown, { type Components, type ExtraProps } from 'react-markdown'
import { Streamdown } from '@lobehub/streamdown'
import remarkGfm from 'remark-gfm'
import remarkMath from 'remark-math'
import rehypeKatex from 'rehype-katex'
import { useI18n, type TranslationKey } from '../../i18n'
import { cn } from '../../lib/cn'
import { CodeBlock } from './CodeBlock'
import { MarkdownImage } from './MarkdownImage'
import { useMarkdownEnvironment } from './MarkdownProvider'
import { STREAMING_FENCE, rehypeMarkdownIds, rehypeStreamingFence, textOf } from './ast'
import { resolveMarkdownTarget } from './links'
import { normalizeMathDelimiters } from './math-delimiters'
import 'katex/dist/katex.min.css'
import './markdown.css'

export interface AgentMarkdownProps {
  content: string
  streaming?: boolean
  variant?: 'response' | 'compact' | 'document'
  className?: string
}

interface RenderState {
  prefix: string
  container: RefObject<HTMLDivElement | null>
}
const RenderContext = createContext<RenderState | null>(null)
const remarkPlugins = [remarkGfm, remarkMath]
type RehypePlugins = NonNullable<Parameters<typeof ReactMarkdown>[0]['rehypePlugins']>

/*
 * ★ 必须是模块级常量,不能写成内联箭头。Streamdown 靠 `isDeepEqual` 判断 markdown
 * options 变没变,而它对函数只比较引用 —— 内联箭头每帧都是新引用,options 每帧失效,
 * 于是**所有块每帧重渲染,块缓存完全不生效**。没有报错也没有警告,
 * 症状只是「接了 Streamdown 但一点没变快」。
 */
const keepUrl = (url: string): string => url

/**
 * remend(Streamdown 的自愈层)会把打到一半的 `[text](url` 改写成这个哨兵值。
 * 它既不是真链接也不是被拦截的链接 —— 按纯文本显示,等 URL 打完自然变成链接。
 */
const INCOMPLETE_LINK = 'streamdown:incomplete-link'

/**
 * One renderer for committed replies, token streams, reasoning, plans and documents.
 *
 * 流式走 Streamdown(按顶层块缓存,已完成的块不再重解析),静态走 ReactMarkdown。
 * 之所以不全量替换:Streamdown 逐块独立解析,跨块引用永久失效 —— GFM 脚注和
 * `[text][ref]` 这类引用式链接会解析不出来,而文档预览恰恰依赖它们。
 */
export const AgentMarkdown = memo(function AgentMarkdown({ content, streaming = false, variant = 'response', className }: AgentMarkdownProps): ReactNode {
  const { t } = useI18n()
  const prefix = `markdown-${useId().replace(/[^a-zA-Z0-9-]/g, '')}-`
  const container = useRef<HTMLDivElement>(null)
  const source = useMemo(() => normalizeMathDelimiters(content), [content])
  const state = useMemo(() => ({ prefix, container }), [prefix])
  // `scoped` 与 `streaming` 同值,因为流式路径就是 Streamdown 路径(见上方注释)。
  const rehypePlugins = useMemo(() => [
    [rehypeKatex, { trust: false, strict: 'ignore', maxExpand: 1000, maxSize: 20 }],
    [rehypeMarkdownIds, { prefix, mathErrorLabel: t('markdown.mathFailed') }],
    [rehypeStreamingFence, { streaming, scoped: streaming }],
  ] as RehypePlugins, [prefix, t, streaming])
  const remarkRehypeOptions = useMemo(() => ({
    clobberPrefix: prefix,
    footnoteLabel: t('markdown.footnotes'),
    footnoteBackLabel: (reference: number, index: number) => t('markdown.footnoteBack', { reference: `${reference + 1}${index > 1 ? `-${index}` : ''}` }),
  }), [prefix, t])
  // 两条路径共用同一份 options,且引用必须逐帧稳定 —— 见 `keepUrl` 上的说明。
  const options = useMemo(() => ({
    components, remarkPlugins, rehypePlugins, remarkRehypeOptions, skipHtml: true, urlTransform: keepUrl,
  }), [rehypePlugins, remarkRehypeOptions])

  return <div ref={container} className={cn('agent-markdown selectable', className)} data-variant={variant}
    data-streaming={streaming || undefined} aria-busy={streaming}>
    <RenderContext.Provider value={state}>
      {/* 两条路径都套这一层,提交瞬间的 DOM 结构才一致,不会整棵子树重建。 */}
      <div className="agent-markdown-body">
        {streaming
          ? <Streamdown {...options} content={source} smoothing="realtime" latexGuard />
          : <ReactMarkdown {...options}>{source}</ReactMarkdown>}
      </div>
    </RenderContext.Provider>
  </div>
})

function MarkdownPre({ node }: ExtraProps): ReactNode {
  const code = node?.children.find((child) => child.type === 'element' && child.tagName === 'code')
  if (!code || code.type !== 'element') return <pre>{textOf(node)}</pre>
  const classes = Array.isArray(code.properties.className) ? code.properties.className.join(' ') : ''
  const language = /(?:^|\s)language-(\S+)/.exec(classes)?.[1]?.toLowerCase() ?? ''
  const meta = typeof code.data?.meta === 'string' ? code.data.meta : undefined
  // 围栏闭没闭在 rehype 阶段就判好了 —— 那里才拿得到与 position.offset 同坐标系的源串。
  return <CodeBlock code={textOf(code).replace(/\n$/, '')} language={language} meta={meta}
    streaming={node?.properties[STREAMING_FENCE] === true} />
}

function MarkdownLink({ href, title, children, node: _node, ...props }: React.ComponentProps<'a'> & ExtraProps): ReactNode {
  const { t } = useI18n()
  const state = useContext(RenderContext)!
  const { resolveLink, onOpenFile, checkFile, onOpenExternal } = useMarkdownEnvironment()
  // 打不开的原因。外部链接的失败由本组件自己写（`markdown.linkFailed`），
  // 文件引用则由宿主给一个 key —— 它才知道是「不存在」还是「没权限」。
  const [failure, setFailure] = useState<TranslationKey | null>(null)
  if (href === INCOMPLETE_LINK) return <>{children}</>
  const target = resolveLink ? resolveLink(href ?? '') : resolveMarkdownTarget('', href ?? '')
  if (target.kind === 'blocked' || target.kind === 'file' && !onOpenFile) {
    return <span className="markdown-blocked-link" title={t('markdown.linkBlocked')}>{children}</span>
  }
  const destination = target.kind === 'external' ? target.url : target.kind === 'anchor'
    ? `#${target.fragment.startsWith(state.prefix) ? target.fragment : `${state.prefix}${target.fragment}`}` : '#'
  return <>
    <a {...props} href={destination} title={title} rel="noreferrer noopener" target={target.kind === 'external' ? '_blank' : undefined}
      onClick={(event) => {
        setFailure(null)
        if (target.kind === 'external') {
          if (onOpenExternal) {
            event.preventDefault()
            void Promise.resolve().then(() => onOpenExternal(target.url)).catch(() => setFailure('markdown.linkFailed'))
          }
          return
        }
        event.preventDefault()
        if (target.kind === 'file') {
          /*
            需求：文件引用要先确认打得开、再开 —— 链接指向的文件被删掉/改名之后，
            直接开会给右侧工作台留一个只显示「文件不存在」的 Tab，而用户还得自己去关掉它。
            预检失败时不开 Tab，把宿主给的原因画在链接旁边。
          */
          void (async () => {
            const blocked = checkFile === undefined ? null : await checkFile(target.path)
            if (blocked !== null) {
              setFailure(blocked)
              return
            }
            await onOpenFile?.(target.path, target.fragment)
          })().catch(() => setFailure('markdown.linkFailed'))
        } else {
          const fragment = target.fragment
          const element = !fragment ? state.container.current : Array.from(state.container.current?.querySelectorAll<HTMLElement>('[id]') ?? [])
            .find((element) => element.id === `${state.prefix}${fragment}` || element.id === fragment)
          element?.scrollIntoView({ block: 'start' })
        }
      }}>{children}</a>
    {failure !== null && <span className="markdown-link-error" role="alert">{t(failure)}</span>}
  </>
}

function MarkdownTable({ children }: { children?: ReactNode }): ReactNode {
  const { t } = useI18n()
  return <div className="markdown-table-scroll scroll-thin" role="region" tabIndex={0} aria-label={t('markdown.tableRegion')}><table>{children}</table></div>
}

// Stable component identities preserve code controls and image state as tokens arrive.
const components: Components = {
  pre: MarkdownPre,
  a: MarkdownLink,
  img: ({ src, alt, title }) => <MarkdownImage key={typeof src === 'string' ? src : ''} src={typeof src === 'string' ? src : ''} alt={alt ?? ''} title={title} />,
  table: MarkdownTable,
}
