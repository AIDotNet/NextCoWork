/**
 * 代码对比视图 —— Git 面板右栏和「改动审查」整页画的都是它。
 *
 * 需求:之前这两处用的是 `DiffLines`(一行一个 `<div>`)。它在聊天工具卡里够用,
 * 但放进整页审查就露了底:正文要么折行(标识符被拦腰截断),要么横向滚动而
 * 行号栏跟着被推走;没有虚拟化,只能靠行数上限硬截断;不能并排看,也不能把
 * 大段没改的上下文折起来。这些恰好都是 CodeMirror 已经解决了的问题,所以这里
 * 直接用 `@codemirror/merge`:
 *
 *   - 行内(unified):一个只读编辑器显示新内容,删除的行以块挂件插在上方;
 *   - 并排(split):`MergeView` 左右两个编辑器,未改动的行上下对齐。
 *
 * 两种布局共用同一套行号 / 配色 / 折叠 / 跳转逻辑,也共用语法高亮
 * (`classHighlighter` 的 `tok-*` 类,配色在 `code.css`,和聊天里的代码块同一套)。
 *
 * ★ **行号栏不随横向滚动走**:CodeMirror 的 gutter 天生 sticky。这是换掉
 *   `DiffLines` 的头号理由,别为了样式把 `.cm-gutters` 改成非 sticky。
 *
 * ★ **并排模式改成「每边自己滚动 + 两边同步」**。`MergeView` 默认是外层容器
 *   整体纵向滚动、两个编辑器撑满全高 —— 那样横向滚动条在**文档最底部**,
 *   长文件里等于没有。所以在 `code-diff-viewer.css` 里把每个编辑器的
 *   `.cm-scroller` 改回定高可滚,再在这里把两边的滚动位置互相同步。
 *   两边内容高度由 MergeView 的 spacer 对齐,同步 scrollTop 就是逐行对齐。
 *
 * ★ **这个模块别从 `components/diff/index.ts` 桶里导出**:它静态引入了整个
 *   CodeMirror,进了桶,工具卡那条 `DiffLines` 的链路就会把它拖进聊天 chunk。
 */
import {
  MergeView,
  getChunks,
  getOriginalDoc,
  mergeViewSiblings,
  unifiedMergeView,
  type Chunk
} from '@codemirror/merge'
import { syntaxHighlighting } from '@codemirror/language'
import { classHighlighter } from '@lezer/highlight'
import {
  basicSetup,
  Compartment,
  EditorSelection,
  EditorState,
  EditorView,
  GutterMarker,
  gutter,
  keymap,
  lineNumbers,
  type Extension,
  type Text
} from '@uiw/react-codemirror'
import { ChevronDown, ChevronUp, WrapText } from 'lucide-react'
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { create } from 'zustand'
import { useI18n, type Translate } from '../../i18n'
import { editorPhrases } from '../../i18n/editor'
import { cn } from '../../lib/cn'
import { useAppearance } from '../../theme/useAppearance'
import { languageForPath } from '../../views/files/editor-language'
import '../code/code.css'
import { IconButton } from '../ui/IconButton'
import SegmentedControl from '../arc/segmented-control/segmented-control'
import './code-diff-viewer.css'

export type DiffLayout = 'unified' | 'split'

/**
 * 布局和折行是**用户偏好**,不是某一份 diff 的状态:切文件、在 Git 面板和
 * 改动审查之间来回,都应该保持上次的选择。放模块级 store,应用生命周期内有效。
 */
const useDiffViewerPrefs = create<{
  layout: DiffLayout
  wrap: boolean
  setLayout: (layout: DiffLayout) => void
  setWrap: (wrap: boolean) => void
}>((set) => ({
  layout: 'unified',
  wrap: false,
  setLayout: (layout) => set({ layout }),
  setWrap: (wrap) => set({ wrap })
}))

/** 改动之间至少隔这么多行没改的才折叠;折叠后在改动两侧各留 `margin` 行上下文。 */
const COLLAPSE = { margin: 3, minSize: 6 }

/** 跳到某处改动时,它上方留出的距离 —— 让上一行上下文也在视野里。 */
const JUMP_MARGIN = 48

type Mounted =
  | { layout: 'unified'; view: EditorView }
  | { layout: 'split'; merge: MergeView }

function editorsOf(mounted: Mounted): EditorView[] {
  return mounted.layout === 'unified' ? [mounted.view] : [mounted.merge.a, mounted.merge.b]
}

/** 负责导航的那个编辑器:并排时是右边(新内容)那一个,滚动会同步到左边。 */
function primaryOf(mounted: Mounted): EditorView {
  return mounted.layout === 'unified' ? mounted.view : mounted.merge.b
}

// ═══════════════════════════════════════════════════════════════
// 行内模式的「原文行号」栏
// ═══════════════════════════════════════════════════════════════

class NumberMarker extends GutterMarker {
  constructor(readonly text: string) {
    super()
  }
  override eq(other: GutterMarker): boolean {
    return other instanceof NumberMarker && other.text === this.text
  }
  override toDOM(): Node {
    return document.createTextNode(this.text)
  }
}

/** 删除块挂件旁边那一摞原文行号,一行一个,和挂件里的 `.cm-deletedLine` 逐行对齐。 */
class StackMarker extends GutterMarker {
  constructor(readonly first: number, readonly count: number) {
    super()
  }
  override eq(other: GutterMarker): boolean {
    return other instanceof StackMarker && other.first === this.first && other.count === this.count
  }
  override toDOM(): Node {
    const box = document.createElement('div')
    for (let i = 0; i < this.count; i++) {
      const row = box.appendChild(document.createElement('div'))
      row.textContent = String(this.first + i)
    }
    return box
  }
}

/** 一段改动在某一侧占了几行(那一侧为空就是 0)。 */
function chunkLines(doc: Text, from: number, to: number, end: number): number {
  if (from >= to) return 0
  return doc.lineAt(end).number - doc.lineAt(from).number + 1
}

/**
 * 行内模式下,CodeMirror 自带的行号栏只认得新文档 —— 删掉的行没有行号,
 * 没改的行也看不出它原来在第几行。这一栏补上原文行号,和新行号并列,
 * 和 `git diff` 的双栏行号是同一个读法。
 *
 * ★ 删除块挂件的判据是「位置宽度为 0 的块挂件,且恰好落在某段改动的 fromB」。
 *   不认 merge 包内部的 `DeletionWidget` 类(没导出);折叠挂件是有宽度的 replace,
 *   不会被误认。
 *
 * 已知限制:开着「自动换行」时,删除行若折成多行,这一摞行号会和它错开 ——
 * gutter 拿不到挂件内部每一行的实际高度。
 */
function originalLineNumbers(): Extension {
  const marker = (view: EditorView, from: number): GutterMarker | null => {
    const chunks = getChunks(view.state)?.chunks ?? []
    const original = getOriginalDoc(view.state)
    let delta = 0
    for (const chunk of chunks) {
      if (chunk.fromB > from) break
      // 落在改动内部(新增 / 改过的行):原文里没有这一行
      if (from < chunk.toB) return null
      delta = chunk.toA - chunk.toB
    }
    const pos = Math.min(original.length, Math.max(0, from + delta))
    return new NumberMarker(String(original.lineAt(pos).number))
  }
  return gutter({
    class: 'cm-originalLineNumbers',
    lineMarker: (view, line) => marker(view, line.from),
    widgetMarker: (view, _widget, block) => {
      if (block.from !== block.to) return null
      const chunk = getChunks(view.state)?.chunks.find(
        (item) => item.fromB === block.from && item.fromA < item.toA
      )
      if (chunk === undefined) return null
      const original = getOriginalDoc(view.state)
      const first = original.lineAt(chunk.fromA).number
      return new StackMarker(first, chunkLines(original, chunk.fromA, chunk.toA, chunk.endA))
    },
    initialSpacer: (view) => new NumberMarker(String(getOriginalDoc(view.state).lines))
  })
}

// ═══════════════════════════════════════════════════════════════
// 跳转与统计
// ═══════════════════════════════════════════════════════════════

/**
 * 跳到上 / 下一处改动。
 *
 * ★ 以**视口顶部**为基准,不是光标:看 diff 的人多半是用滚轮滚到这里的,光标
 *   还停在第一行 —— 按光标算的话(merge 包自带的 `goToNextChunk` 就是这样),
 *   滚到一半按「下一处」会跳回文件开头附近。跳过去之后改动被放在视口顶部往下
 *   `JUMP_MARGIN` 的位置,下一次按就从它之后找,连按是连续的。
 */
function jumpToChunk(view: EditorView, dir: 1 | -1): boolean {
  const info = getChunks(view.state)
  if (info === null || info.chunks.length === 0) return false
  const start = (chunk: Chunk): number => (info.side === 'a' ? chunk.fromA : chunk.fromB)
  const anchor = view.lineBlockAtHeight(view.scrollDOM.scrollTop + JUMP_MARGIN + 1).from
  const chunks = info.chunks
  const target =
    dir > 0
      ? (chunks.find((chunk) => start(chunk) > anchor) ?? chunks[0])
      : ([...chunks].reverse().find((chunk) => start(chunk) < anchor) ?? chunks[chunks.length - 1])
  if (target === undefined) return false
  const pos = Math.min(start(target), view.state.doc.length)
  /*
    新内容这一侧,删掉的行(行内模式的删除块 / 并排模式的对齐 spacer)挂在 `pos`
    **上方**,而 scrollIntoView 量的是 `pos` 那一行文字 —— 不补这段高度,
    删除块会正好被滚出视口顶部。补的量封顶半屏,删得再多也至少看得见改动的开头。
  */
  const hidden = info.side === 'a' ? 0 : chunkLines(sideA(view), target.fromA, target.toA, target.endA)
  const extra = Math.min(hidden * view.defaultLineHeight, view.scrollDOM.clientHeight / 2)
  view.dispatch({
    selection: EditorSelection.cursor(pos),
    effects: EditorView.scrollIntoView(pos, { y: 'start', yMargin: JUMP_MARGIN + extra }),
    userEvent: 'select.byChunk'
  })
  return true
}

/** 原文那一侧的文档:行内模式存在 merge 状态里,并排模式是左边编辑器自己的文档。 */
function sideA(view: EditorView): Text {
  return mergeViewSiblings(view)?.a.state.doc ?? getOriginalDoc(view.state)
}

interface DiffStats {
  added: number
  removed: number
  chunks: number
}

function statsOf(chunks: readonly Chunk[], a: Text, b: Text): DiffStats {
  let added = 0
  let removed = 0
  for (const chunk of chunks) {
    removed += chunkLines(a, chunk.fromA, chunk.toA, chunk.endA)
    added += chunkLines(b, chunk.fromB, chunk.toB, chunk.endB)
  }
  return { added, removed, chunks: chunks.length }
}

function readStats(mounted: Mounted): DiffStats {
  if (mounted.layout === 'split') {
    const { merge } = mounted
    return statsOf(merge.chunks, merge.a.state.doc, merge.b.state.doc)
  }
  const state = mounted.view.state
  return statsOf(getChunks(state)?.chunks ?? [], getOriginalDoc(state), state.doc)
}

// ═══════════════════════════════════════════════════════════════
// 并排模式的滚动同步
// ═══════════════════════════════════════════════════════════════

/**
 * 两个 scroller 互相跟随。
 *
 * ★ 回声抑制用「下一帧之前忽略对方的 scroll 事件」,而不是比较数值:两边内容
 *   宽度不同时,写过去的 scrollLeft 会被浏览器夹到对方的最大值,数值永远对不上,
 *   比数值的写法会在两边之间来回弹。scroll 事件在下一帧的渲染步骤里派发,
 *   早于同一帧的 rAF 回调,所以 rAF 里解除忽略刚好覆盖那一次回声。
 */
function syncScroll(a: HTMLElement, b: HTMLElement): () => void {
  let ignore: HTMLElement | null = null
  let frame = 0
  const follow = (from: HTMLElement, to: HTMLElement) => (): void => {
    if (ignore === from) return
    ignore = to
    to.scrollTop = from.scrollTop
    to.scrollLeft = from.scrollLeft
    cancelAnimationFrame(frame)
    frame = requestAnimationFrame(() => {
      ignore = null
    })
  }
  const onA = follow(a, b)
  const onB = follow(b, a)
  a.addEventListener('scroll', onA, { passive: true })
  b.addEventListener('scroll', onB, { passive: true })
  return () => {
    cancelAnimationFrame(frame)
    a.removeEventListener('scroll', onA)
    b.removeEventListener('scroll', onB)
  }
}

// ═══════════════════════════════════════════════════════════════
// 组件
// ═══════════════════════════════════════════════════════════════

export function CodeDiffViewer({
  original,
  modified,
  path,
  className
}: {
  /** 改动前的全文。新文件给空串 */
  original: string
  /** 改动后的全文。删除的文件给空串 */
  modified: string
  /** 仓库 / 工作区相对路径:推语法高亮的语言,也进读屏标签 */
  path: string
  className?: string
}): ReactNode {
  const { t } = useI18n()
  const appearance = useAppearance()
  const layout = useDiffViewerPrefs((state) => state.layout)
  const wrap = useDiffViewerPrefs((state) => state.wrap)
  const setLayout = useDiffViewerPrefs((state) => state.setLayout)
  const setWrap = useDiffViewerPrefs((state) => state.setWrap)

  const hostRef = useRef<HTMLDivElement>(null)
  const mountedRef = useRef<Mounted | null>(null)
  /** 同一个文件内容刷新(轮询)后重建视图时,接着上次的滚动位置,而不是跳回顶部 */
  const scrollMemo = useRef<{ key: string; top: number; left: number } | null>(null)
  const [stats, setStats] = useState<DiffStats | null>(null)
  /**
   * 这个路径的语法扩展;`extension: []` = 没有对应语法或加载失败。
   *
   * ★ **语法到位之前不建编辑器**,而不是先建、再热换进去:行内模式的删除块是
   *   挂件,DOM 在第一次画出来时就定了(merge 包按 chunk 缓存),之后再换语言
   *   也不会重新着色 —— 表现为删掉的行永远是一片无高亮的白字。语法集是动态
   *   import,第一次之后就在缓存里,等的这一下通常感觉不到。
   */
  const [loaded, setLoaded] = useState<{ path: string; extension: Extension } | null>(null)
  const language = loaded?.path === path ? loaded.extension : null

  /*
    Compartment 是「同一个编辑器里可以热换的那几块配置」。内容 / 布局 / 语言变了
    才重建编辑器;折行、主题、文案只换各自那一格,滚动位置和选区都不动。
  */
  const compartments = useMemo(
    () => ({ wrap: new Compartment(), theme: new Compartment(), phrases: new Compartment() }),
    []
  )
  // 建编辑器的那个 effect 不该因为折行 / 主题 / 文案而重跑;它们走 ref 读当前值
  const live = useRef({ wrap, appearance, t })
  live.current = { wrap, appearance, t }

  useEffect(() => {
    let active = true
    const description = languageForPath(path)
    if (description === null) {
      setLoaded({ path, extension: [] })
      return
    }
    description
      .load()
      .then((support) => {
        if (active) setLoaded({ path, extension: support })
      })
      .catch(() => {
        // 拿不到语法就是「有对比、没有颜色」,不值得打断用户
        if (active) setLoaded({ path, extension: [] })
      })
    return () => {
      active = false
    }
  }, [path])

  // 建 / 重建编辑器
  useEffect(() => {
    const host = hostRef.current
    if (host === null || language === null) return
    const current = live.current
    const shared = (label: string): Extension[] => [
      basicSetup({
        lineNumbers: false,
        foldGutter: false,
        highlightActiveLine: false,
        highlightActiveLineGutter: false,
        dropCursor: false,
        indentOnInput: false,
        bracketMatching: false,
        closeBrackets: false,
        autocompletion: false,
        rectangularSelection: false,
        crosshairCursor: false,
        history: false,
        historyKeymap: false,
        closeBracketsKeymap: false,
        completionKeymap: false,
        foldKeymap: false,
        lintKeymap: false
      }),
      lineNumbers(),
      EditorState.readOnly.of(true),
      language,
      syntaxHighlighting(classHighlighter),
      keymap.of([
        { key: 'F7', run: (view) => jumpToChunk(view, 1), shift: (view) => jumpToChunk(view, -1) }
      ]),
      EditorView.contentAttributes.of({ 'aria-label': label, spellcheck: 'false' }),
      compartments.wrap.of(current.wrap ? EditorView.lineWrapping : []),
      compartments.theme.of(EditorView.theme({}, { dark: current.appearance === 'dark' })),
      compartments.phrases.of(phrasesFor(current.t))
    ]

    let mounted: Mounted
    let cleanupSync = (): void => {}
    if (layout === 'split') {
      const merge = new MergeView({
        a: { doc: original, extensions: shared(current.t('diff.viewer.original', { path })) },
        b: { doc: modified, extensions: shared(current.t('diff.viewer.modified', { path })) },
        parent: host,
        gutter: true,
        highlightChanges: true,
        collapseUnchanged: COLLAPSE
      })
      cleanupSync = syncScroll(merge.a.scrollDOM, merge.b.scrollDOM)
      mounted = { layout: 'split', merge }
    } else {
      const view = new EditorView({
        parent: host,
        state: EditorState.create({
          doc: modified,
          extensions: [
            // 先于 `shared` 里的 lineNumbers:gutter 按注册顺序从左往右排,原文行号在左
            originalLineNumbers(),
            ...shared(current.t('diff.viewer.modified', { path })),
            unifiedMergeView({
              original,
              gutter: true,
              highlightChanges: true,
              mergeControls: false,
              syntaxHighlightDeletions: true,
              collapseUnchanged: COLLAPSE
            })
          ]
        })
      })
      mounted = { layout: 'unified', view }
    }
    mountedRef.current = mounted
    setStats(readStats(mounted))

    const primary = primaryOf(mounted)
    const memoKey = `${path}\u0000${layout}`
    const memo = scrollMemo.current
    if (memo !== null && memo.key === memoKey) {
      // 等 CodeMirror 量完第一次高度再滚,不然 scrollTop 会被夹在初始内容高度上
      requestAnimationFrame(() => {
        primary.scrollDOM.scrollTop = memo.top
        primary.scrollDOM.scrollLeft = memo.left
      })
    }

    return () => {
      scrollMemo.current = { key: memoKey, top: primary.scrollDOM.scrollTop, left: primary.scrollDOM.scrollLeft }
      cleanupSync()
      if (mounted.layout === 'split') mounted.merge.destroy()
      else mounted.view.destroy()
      mountedRef.current = null
      // 换文件时新编辑器要等语法到位才建,这段空档里别挂着上一个文件的统计
      setStats(null)
    }
  }, [original, modified, layout, path, language, compartments])

  // 以下几格热换,不重建编辑器
  useEffect(() => {
    const mounted = mountedRef.current
    if (mounted === null) return
    for (const view of editorsOf(mounted)) {
      view.dispatch({ effects: compartments.wrap.reconfigure(wrap ? EditorView.lineWrapping : []) })
    }
  }, [wrap, compartments])

  useEffect(() => {
    const mounted = mountedRef.current
    if (mounted === null) return
    for (const view of editorsOf(mounted)) {
      view.dispatch({
        effects: compartments.theme.reconfigure(EditorView.theme({}, { dark: appearance === 'dark' }))
      })
    }
  }, [appearance, compartments])

  useEffect(() => {
    const mounted = mountedRef.current
    if (mounted === null) return
    for (const view of editorsOf(mounted)) {
      view.dispatch({ effects: compartments.phrases.reconfigure(phrasesFor(t)) })
    }
  }, [t, compartments])

  const jump = (dir: 1 | -1): void => {
    const mounted = mountedRef.current
    if (mounted === null) return
    const view = primaryOf(mounted)
    jumpToChunk(view, dir)
    view.focus()
  }

  const noChunks = stats === null || stats.chunks === 0

  return (
    <div className={cn('code-diff-viewer code-scope flex min-h-0 min-w-0 flex-1 flex-col', className)}>
      <div
        role="toolbar"
        aria-label={t('diff.viewer.toolbar')}
        className="flex shrink-0 items-center gap-1.5 border-b border-hairline bg-canvas px-3 py-1.5 text-[11px]"
      >
        {stats !== null && (
          <span className="flex items-center gap-2 font-mono tabular-nums">
            <span className="text-accent" title={t('diff.viewer.added', { count: stats.added })}>
              +{stats.added}
            </span>
            <span className="text-danger" title={t('diff.viewer.removed', { count: stats.removed })}>
              −{stats.removed}
            </span>
          </span>
        )}
        <div className="ml-auto flex items-center gap-1">
          <IconButton label={t('diff.viewer.previous')} size={24} disabled={noChunks} onClick={() => jump(-1)}>
            <ChevronUp size={14} />
          </IconButton>
          <IconButton label={t('diff.viewer.next')} size={24} disabled={noChunks} onClick={() => jump(1)}>
            <ChevronDown size={14} />
          </IconButton>
          <IconButton
            label={t('diff.viewer.wrap')}
            size={24}
            active={wrap}
            pressed={wrap}
            onClick={() => setWrap(!wrap)}
          >
            <WrapText size={14} />
          </IconButton>
          <SegmentedControl
            className="ml-1"
            value={layout}
            label={t('diff.viewer.layout')}
            options={[
              { value: 'unified', label: t('diff.viewer.unified') },
              { value: 'split', label: t('diff.viewer.split') }
            ]}
            onValueChange={(value) => setLayout(value as DiffLayout)}
          />
        </div>
      </div>
      <div ref={hostRef} className="code-diff-viewer-host selectable relative min-h-0 flex-1" />
    </div>
  )
}

function phrasesFor(t: Translate): Extension {
  return EditorState.phrases.of({
    ...editorPhrases(t as (key: string) => string),
    '$ unchanged lines': t('diff.viewer.unchangedLines')
  })
}
