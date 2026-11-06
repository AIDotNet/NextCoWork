/**
 * diff 的**唯一**渲染组件 —— 工具卡里的 Edit 片段、改动审查整页、Git 面板右栏
 * 画的都是它。
 *
 * 需求:在这之前有两份实现(`views/chat/DiffView.tsx` 与 `GitFeature.tsx` 里的
 * `DiffView`),各有一张行样式表、一套行号栏和一条换行策略。结果是同一份改动在
 * 两个界面里底色深浅不同、行号栏宽度不同、`@@` 标题一个有一个没有,而任何一次
 * 视觉修补都只落在其中一边。这里把「一行 diff 长什么样」定死一次:
 * 谁要画 diff,先把数据转成 `DiffLine[]`(见 `./model.ts`)。
 *
 * ★ **正文一律用常规前景色,增删只靠底色区分。** 一开始把整行文字也染成
 *   accent/danger,结果绿字压绿底、红字压红底 —— 代码本身反而读不动了。
 *   词级高亮只会出现在「同一行里只改了一部分」的行上(见 compute.ts 的饱和护栏)。
 *
 * ★ **符号列不能 `aria-hidden`**:`+` / `−` 是「这一行是增是删」唯一的非颜色线索,
 *   屏幕阅读器和色觉障碍用户都只有它。而行号列相反,必须 `select-none` +
 *   `aria-hidden` —— 否则复制一段 diff 粘到别处时,每行前面都挂着两个数字。
 *
 * 语法高亮是**异步**补上的(`./useDiffSyntax`),拿不到就是「有 diff 没有颜色」,
 * 首帧永远不等它。
 */
import type { ReactNode } from 'react'
import { cn } from '../../lib/cn'
import type { CodeSpan } from '../code/highlight'
import '../code/code.css'
import { lineText, type DiffLine, type DiffLineKind } from './model'
import { styleSpans } from './syntax'
import { useDiffSyntax } from './useDiffSyntax'

/**
 * 一行 diff 的外观。整张表在这里,不在任何一个视图里 —— 加一种 kind 就是加一行,
 * 而不是在两个组件里各加一个分支。
 */
const KIND_STYLE: Record<DiffLineKind, { row: string; sign: string }> = {
  hunk: { row: 'border-y border-border bg-tint text-accent-soft', sign: '' },
  meta: { row: 'text-fg-faint', sign: '' },
  add: { row: 'bg-accent/10', sign: '+' },
  del: { row: 'bg-danger/10', sign: '-' },
  context: { row: '', sign: ' ' }
}

/** 行号栏宽度:五位行号(万行文件)在 12px 等宽下刚好放得下,再宽就开始抢正文。 */
const LINE_NUMBER_CLASS =
  'w-12 shrink-0 select-none border-r border-border px-1.5 text-right tabular-nums text-fg-faint'

const NO_TOKENS: CodeSpan[] = []

export function DiffLines({
  lines,
  language = '',
  lineNumbers = false,
  wrap = true,
  className
}: {
  lines: DiffLine[]
  /** 语法高亮的语言(通常是扩展名,见 `components/code/language.ts`)。空串 = 不高亮 */
  language?: string
  /** 画不画旧/新两侧行号栏。工具卡里的片段没有行号可言 */
  lineNumbers?: boolean
  /** 工具卡和 Git 右栏折行(守住窄卡宽度);整页审查不折行,保留代码列结构 */
  wrap?: boolean
  className?: string
}): ReactNode {
  const tokens = useDiffSyntax(lines, language)
  return (
    <div className={cn('code-scope selectable font-mono', className)}>
      {lines.map((line, index) => (
        <DiffLineRow
          key={index}
          line={line}
          tokens={tokens?.[index] ?? NO_TOKENS}
          lineNumbers={lineNumbers}
          wrap={wrap}
        />
      ))}
    </div>
  )
}

function DiffLineRow({
  line,
  tokens,
  lineNumbers,
  wrap
}: {
  line: DiffLine
  tokens: CodeSpan[]
  lineNumbers: boolean
  wrap: boolean
}): ReactNode {
  const style = KIND_STYLE[line.kind]

  /*
    需求:hunk 标题和文件头不是代码行 —— 它们没有行号、没有 +/−,也不该被语法
    高亮碰。给它们套上空的行号栏只会在正文左边留两条永远空着的竖线。
    `sticky left-0`:横向滚动时 `@@ …` 必须跟着走,它是「现在看的是第几行」的锚。
  */
  if (line.kind === 'hunk' || line.kind === 'meta') {
    return (
      <div className={cn('flex', style.row)}>
        <span className="sticky left-0 select-none px-3 py-0.5">{lineText(line)}</span>
      </div>
    )
  }

  return (
    <div className={cn('flex', style.row, lineNumbers ? 'min-w-full' : 'px-2.5')}>
      {lineNumbers && (
        <>
          <span aria-hidden className={LINE_NUMBER_CLASS}>{line.oldLine ?? ''}</span>
          <span aria-hidden className={LINE_NUMBER_CLASS}>{line.newLine ?? ''}</span>
        </>
      )}
      {/* select-none:复制 diff 时不把 +/- 前缀也带上 */}
      <span
        className={cn(
          'w-5 shrink-0 select-none text-center',
          line.kind === 'add' ? 'text-accent' : line.kind === 'del' ? 'text-danger' : 'text-fg-faint'
        )}
      >
        {style.sign}
      </span>
      <span
        className={cn(
          'flex-1 text-fg-muted',
          wrap ? 'min-w-0 break-all whitespace-pre-wrap' : 'min-w-max whitespace-pre pr-4'
        )}
      >
        {styleSpans(line.spans, tokens).map((span, index) => (
          <span
            key={index}
            className={cn(
              span.className,
              // 词级高亮盖在语法色之上:改了哪几个词比它是不是关键字更要紧
              span.hi && 'rounded-[2px]',
              span.hi && (line.kind === 'add' ? 'bg-accent/25 text-accent' : 'bg-danger/20 text-danger')
            )}
          >
            {span.text}
          </span>
        ))}
      </span>
    </div>
  )
}
