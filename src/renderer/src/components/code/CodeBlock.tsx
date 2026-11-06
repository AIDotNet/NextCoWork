/**
 * 代码卡 —— 全仓库**唯一**一张画代码的卡。
 *
 * 需求:原先有两张。`components/markdown/CodeBlock.tsx` 画 markdown 围栏(常驻
 * 工具栏、预览/源码切换、折行开关),`views/chat/CodeBlock.tsx` 画工具展开详情里的
 * 代码(行号栏、悬停复制、渲染前按行截断)。两张卡各自维护复制状态、各自决定
 * 高亮怎么挂,于是「复制反馈两秒后复位」这种细节修一处漏一处。
 * 这里合成一张:**正文永远是 `CodeSource`**,差异收敛成 `variant` 加几个插槽。
 *
 * 三条硬约束(从两张卡的文件头合并而来,都不是装饰):
 *   1. **高亮复用编辑器的语法**(`./CodeSource` → `./highlight`,底下是
 *      `@codemirror/language-data`)。不引第二个高亮库:两套语法集会慢慢分叉,
 *      同一个文件在编辑器里和在聊天里染成两个样子。
 *   2. `card` 档**不折行**,横向滚动。折行会把缩进层级拆掉,而那是读代码的骨架。
 *      `fence` 档由工具栏上的开关决定,默认同样不折行。
 *   3. 行数在**渲染前**截断(`maxLines`):一次 `Read` 能有两千行,全塞进 DOM 再用
 *      高度盖住,滚动转录时依然要为它们布局。围栏不传 `maxLines`,它靠 CSS 限高。
 *
 * ★ 根节点那个 `code-scope` **不是装饰**:`code.css` 里所有 `.tok-*` 配色都挂在
 * 这个作用域下面。去掉它,高亮 span 仍然会被渲染出来,但一个颜色都不会生效 ——
 * 表现为「高亮好像没跑」,而且没有报错。
 */
import { Check, Code2, Copy, WrapText } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import { useI18n } from '../../i18n'
import { cn } from '../../lib/cn'
import { useMarkdownEnvironment } from '../markdown/MarkdownProvider'
import { CodeSource } from './CodeSource'
import './code.css'

/**
 * 行号栏的字体必须和代码**逐像素一致**,否则两列会越往下错得越开。
 * 这两个值抄自 `code.css` 里 `.code-source` 的 `font: 12px/1.75`;改那边就要同步
 * 改这里(没有共享变量可依赖,因为那条规则是简写 `font`,Tailwind 取不到)。
 */
const GUTTER_CLASS =
  'select-none px-2 py-[13px] text-right font-mono text-[12px] leading-[1.75] tabular-nums text-fg-faint'

export interface CodeBlockProps {
  code: string
  /** 高亮语言(通常是扩展名)。空串 = 不高亮,正文照常显示 */
  language?: string
  /**
   * `fence` = markdown 围栏:常驻工具栏,正文可被 `children` 换成预览。
   * `card` = 详情卡:无工具栏,悬停才出现复制按钮,可带行号栏。
   */
  variant?: 'fence' | 'card'
  /** 给出就画行号栏,从这一行开始数(仅 `card`) */
  startLine?: number | undefined
  /** 渲染前按行截断;不给就不截断(围栏靠 CSS 限高) */
  maxLines?: number
  /** 卡壳的类名。`card` 的外框由调用方给 —— 展开区那张卡的形状是 chat 的决定 */
  className?: string
  /** 工具栏里语言名右边的额外控件(围栏的「预览 / 源码」切换) */
  actions?: ReactNode
  /** 工具栏下方的一行状态(围栏的流式渲染提示) */
  status?: ReactNode
  /** 给了就用它替换代码正文(围栏的预览渲染器) */
  children?: ReactNode
  /** 复制实现。缺省走宿主注入的 `onCopyCode`,再缺省才是浏览器剪贴板 */
  onCopy?: (code: string) => void | Promise<void>
  /** 围栏是否还在流(只影响 data 属性,按钮的禁用由 `actions` 自己判) */
  streaming?: boolean
}

export function CodeBlock({
  code,
  language = '',
  variant = 'card',
  startLine,
  maxLines,
  className,
  actions,
  status,
  children,
  onCopy,
  streaming
}: CodeBlockProps): ReactNode {
  const { t } = useI18n()
  const { onCopyCode } = useMarkdownEnvironment()
  const [copy, setCopy] = useState<'idle' | 'copied' | 'failed'>('idle')
  const [wrap, setWrap] = useState(false)

  useEffect(() => { setCopy('idle') }, [code])
  useEffect(() => {
    if (copy === 'idle') return
    const timer = setTimeout(() => setCopy('idle'), 2200)
    return () => clearTimeout(timer)
  }, [copy])

  const lines = code.split('\n')
  const shown = maxLines === undefined || lines.length <= maxLines ? lines : lines.slice(0, maxLines)
  const omitted = lines.length - shown.length
  const clipped = omitted === 0 ? code : shown.join('\n')

  /*
    需求:复制的落点由宿主决定 —— 桌面端要走主进程剪贴板(`onCopyCode`),而
    markdown 这套组件也会在拿不到宿主能力的地方渲染,那时浏览器剪贴板是唯一选择。
    这里只负责「复制哪一段」:截断之后显示的是哪些行,复制出去的就是哪些行。
  */
  const runCopy = (): void => {
    const write = onCopy ?? onCopyCode ?? ((text: string): Promise<void> => navigator.clipboard.writeText(text))
    void Promise.resolve()
      .then(() => write(clipped))
      .then(() => setCopy('copied'))
      .catch(() => setCopy('failed'))
  }

  const body = children ?? <CodeSource code={clipped} language={language} wrap={variant === 'fence' && wrap} />

  if (variant === 'fence') {
    return (
      <div
        className={cn('code-scope code-card', className)}
        data-language={language || undefined}
        data-streaming={streaming === true ? true : undefined}
      >
        <div className="code-card-toolbar">
          {/* 需求：代码块标题要先被识别成文件/代码产物，再呈现语言和操作。 */}
          <Code2 size={14} aria-hidden="true" className="shrink-0 text-fg-faint" />
          <span className="code-card-language">{language || t('markdown.code')}</span>
          {actions}
          <div className="code-card-actions">
            {children === undefined && (
              <button type="button" title={t(wrap ? 'markdown.unwrap' : 'markdown.wrap')}
                aria-label={t(wrap ? 'markdown.unwrap' : 'markdown.wrap')} aria-pressed={wrap}
                onClick={() => setWrap((v) => !v)}>
                <WrapText size={14} aria-hidden="true" />
              </button>
            )}
            <button type="button" className="markdown-copy"
              title={t(copy === 'failed' ? 'markdown.copyFailed' : 'markdown.copy')} onClick={runCopy}>
              {copy === 'copied' ? <Check size={13} aria-hidden="true" /> : <Copy size={13} aria-hidden="true" />}
              <span aria-live="polite">
                {t(copy === 'copied' ? 'markdown.copied' : copy === 'failed' ? 'markdown.copyFailed' : 'markdown.copy')}
              </span>
            </button>
          </div>
        </div>
        {status}
        {body}
      </div>
    )
  }

  if (code === '' && children === undefined) return null

  return (
    <div data-testid="code-block" className={cn('code-scope group/code relative', className)}>
      {/*
        复制按钮悬停才出现:代码卡在一轮回复里可能有十几张,十几个常驻按钮
        会把视线从代码上抢走 —— 而复制是偶尔才做的事。
      */}
      <button
        type="button"
        data-testid="code-copy"
        title={t('markdown.copy')}
        aria-label={t('markdown.copy')}
        onClick={runCopy}
        className="absolute top-1.5 right-1.5 z-10 inline-flex h-6 w-6 cursor-pointer items-center justify-center rounded-[6px] bg-surface-field/90 text-fg-faint opacity-0 transition-opacity hover:text-fg group-hover/code:opacity-100 motion-reduce:transition-none"
      >
        {copy === 'copied' ? <Check size={12} /> : <Copy size={12} />}
      </button>

      <div className="scroll-thin flex max-h-[min(50vh,420px)] overflow-auto">
        {startLine !== undefined && (
          <div aria-hidden className={GUTTER_CLASS}>
            {shown.map((_, index) => (
              <div key={startLine + index}>{startLine + index}</div>
            ))}
          </div>
        )}
        <div className="min-w-0 flex-1">{body}</div>
      </div>

      {omitted > 0 && (
        <div className="px-3 pb-1.5 text-[12px] text-fg-faint">
          {t('chat.tool.linesOmitted', { count: omitted }).trim()}
        </div>
      )}
    </div>
  )
}
