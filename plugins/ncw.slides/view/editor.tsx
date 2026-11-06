/**
 * 演示(.pptx)编辑器视图 —— 跑在 `ncw-plugin://ncw.slides` 的 iframe 里。
 *
 * ## 为了什么需求建的
 *
 * 用户要在 NextCoWork 里像 WPS 演示一样看、改 PowerPoint 文件,并且与 Agent 改的是同一份活动模型
 * (计划 §1、§7.2「Slides 缩略图 / 放映」)。幻灯片本身由引擎画(宿主的 `DocumentCanvas`,居中放在
 * 留白里);这里做演示特有的外壳:功能区、左侧缩略图栏、状态栏、放映。
 *
 * ## 这一版故意不做的
 *
 * - 备注、母版编辑、动画 / 切换效果、插入图片与媒体:需要引擎侧的命令,还没有;没有后端能力的按钮不画。
 * - 缩略图拖动排序:用上移 / 下移按钮代替。
 * - 放映不是真全屏、没有动画(见 `slideshow.tsx` 的文件头)。
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Button, EmptyState, Segmented, Spinner } from 'nextcowork/ui'
import {
  DocumentCanvas, mount, openEngineDocument, twipsToCssPx,
  type EngineDocument, type EngineDocumentError, type EngineDocumentOpened, type EngineDocumentState
} from 'nextcowork/view'
import { Presentation } from 'lucide-react'
import type { CommandStates, Run } from '../../office-common/ribbon-parts'
import { text } from './messages'
import { Ribbon } from './ribbon'
import { Slideshow } from './slideshow'
import { Thumbnails, type SlideSize } from './thumbnails'

const ZOOMS = ['fit', '0.5', '0.75', '1'] as const
type Zoom = (typeof ZOOMS)[number]
const isMac = /mac/i.test(navigator.platform)
/** 「适应窗口」时幻灯片四周的留白(CSS px,含画布的 p-6) */
const FIT_MARGIN = 64

function sizeOf(layout: unknown): SlideSize | null {
  if (layout === null || typeof layout !== 'object') return null
  const { width, height } = layout as Record<string, unknown>
  return typeof width === 'number' && typeof height === 'number' && width > 0 && height > 0 ? { width, height } : null
}

function Slides(): React.ReactNode {
  const [doc, setDoc] = useState<EngineDocument | null>(null)
  const [opened, setOpened] = useState<EngineDocumentOpened | null>(null)
  const [state, setState] = useState<EngineDocumentState | null>(null)
  const [states, setStates] = useState<CommandStates>({})
  const [openError, setOpenError] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [zoom, setZoom] = useState<Zoom>('fit')
  const [part, setPart] = useState(0)
  const [count, setCount] = useState(0)
  const [size, setSize] = useState<SlideSize>({ width: 0, height: 0 })
  const [area, setArea] = useState({ width: 0, height: 0 })
  const [showing, setShowing] = useState<number | null>(null)
  const stageObserver = useRef<ResizeObserver | null>(null)
  const canvas = useRef<{ focus: () => void } | null>(null)

  const onError = useCallback((error: EngineDocumentError) => {
    setProblem(error.code === 'engine_unavailable' || error.code === 'result_unknown' || error.code === 'session_closed' ? text.crashed : `${text.failed} (${error.code})`)
  }, [])

  /**
   * 重取幻灯片数与尺寸(增删了幻灯片、文档改了)。
   * ★ 量的是**当前**这张(`layout()` 不带 part):演示里每张一样大,而带别的 part 的版面查询会让引擎
   *   把用户视图切过去再切回来 —— 用户正在文本框里打字的话,编辑状态就此结束,下一个键落空
   *   (Electron 探针实测:插入文本框后键入的字没进文件)。onChange 在用户自己每次改动后都会来。
   */
  const refreshParts = useCallback((next: EngineDocument) => {
    next.list('parts').then((names) => { setCount(names.length) }, () => undefined)
    next.layout().then((result) => {
      const measured = sizeOf(result.layout)
      if (measured !== null) setSize((current) => current.width === measured.width && current.height === measured.height ? current : measured)
    }, () => undefined)
  }, [])

  useEffect(() => {
    // ★ 每个视图只打开一次:每次调用宿主都会重开一次会话视图;cleanup 必须 dispose
    const next = openEngineDocument()
    setDoc(next)
    next.ready.then(
      (result) => {
        setOpened(result)
        setState(result.state)
        refreshParts(next)
      },
      (error: EngineDocumentError) => { setOpenError(`${text.openFailed} (${error.code})`) }
    )
    const stopChange = next.onChange((changed) => {
      setState(changed)
      // Agent 可能加了 / 删了幻灯片
      refreshParts(next)
    })
    const stopResult = next.onResult((result) => {
      const changed = result.states
      if (changed !== undefined) setStates((current) => ({ ...current, ...changed }))
      if (result.parts !== undefined) {
        setCount(result.parts)
        refreshParts(next)
      }
    })
    return () => {
      stopChange()
      stopResult()
      next.dispose()
    }
  }, [refreshParts])

  // 「适应窗口」要量画布那一块的大小。回调 ref:那块只在打开完成后才挂上
  const stage = useCallback((element: HTMLDivElement | null) => {
    stageObserver.current?.disconnect()
    stageObserver.current = null
    if (element === null) return
    const observer = new ResizeObserver(() => { setArea({ width: element.clientWidth, height: element.clientHeight }) })
    observer.observe(element)
    stageObserver.current = observer
  }, [])

  const canSave = opened?.capabilities.canSave === true
  const save = useCallback(() => {
    if (doc === null || !canSave || saving) return
    setSaving(true)
    doc.save().then((next) => { setState(next); setProblem(null) }, onError).finally(() => { setSaving(false) })
  }, [doc, canSave, saving, onError])

  const generation = state?.generation ?? 0
  const run: Run = useCallback((command, args) => {
    if (doc === null) return
    doc.command(generation, command, args).then(() => { setProblem(null) }, onError).finally(() => { canvas.current?.focus() })
  }, [doc, generation, onError])

  const switchPart = useCallback((index: number) => {
    if (doc === null || index === part) return
    // 先让引擎切到那张幻灯片(之后的点选与键入落在它上面),再让画布按新页取版面
    doc.input(generation, [{ type: 'part', part: index }]).then(() => { setPart(index) }, onError)
  }, [doc, part, generation, onError])

  const present = useCallback((from: number) => {
    if (count > 0) setShowing(Math.max(0, Math.min(count - 1, from)))
  }, [count])

  const onShortcut = useCallback((event: KeyboardEvent) => {
    // 保存经宿主存回工作区;送进引擎的话它会存进自己的私有副本(见 DocumentCanvas 的说明)
    const primary = isMac ? event.metaKey : event.ctrlKey
    if (primary && !event.altKey && event.code === 'KeyS') {
      save()
      return true
    }
    // F5 从头放映、Shift+F5 从当前页(与 WPS / PowerPoint 一致)
    if (event.key === 'F5' && !primary && !event.altKey) {
      present(event.shiftKey ? part : 0)
      return true
    }
    return false
  }, [save, present, part])

  const available = useMemo(() => new Set<string>(opened?.capabilities.commands ?? []), [opened])
  const listFonts = useCallback(() => doc?.list('fonts') ?? Promise.resolve([]), [doc])

  const zoomValue = useMemo(() => {
    if (zoom !== 'fit') return Number(zoom)
    const slideW = twipsToCssPx(size.width, 1)
    const slideH = twipsToCssPx(size.height, 1)
    if (slideW <= 0 || slideH <= 0 || area.width <= 0 || area.height <= 0) return 1
    const fit = Math.min((area.width - FIT_MARGIN) / slideW, (area.height - FIT_MARGIN) / slideH)
    // 取两位:窗口每挪一像素就换一次缩放的话,tile 缓存每一帧都清空
    return Math.max(0.1, Math.min(4, Math.floor(fit * 100) / 100))
  }, [zoom, size, area])

  if (openError !== null) return <EmptyState title={text.openFailed} hint={openError} />
  if (doc === null || opened === null || state === null) {
    return (
      <div className="flex h-full items-center justify-center gap-2 text-fg-muted">
        <Spinner size={16} />
        <span className="text-[12px]">{text.opening}</span>
      </div>
    )
  }

  const editable = opened.capabilities.interaction !== undefined
  const status = saving ? text.saving : !editable ? text.readOnly : state.dirty ? text.unsaved : text.saved

  return (
    <div className="flex h-full flex-col bg-canvas">
      <div className="flex shrink-0 items-center gap-2 border-b border-hairline px-3 py-1.5">
        <Button variant="accent" size="sm" disabled={!canSave || !state.dirty || saving} onClick={save}>{text.save}</Button>
        <span className="min-w-0 flex-1 truncate text-[12px] text-fg-muted">{opened.path} · {status}</span>
        {problem !== null && <span role="alert" className="truncate text-[12px] text-danger">{problem}</span>}
        <Button size="sm" icon={<Presentation size={14} />} disabled={count === 0 || size.width === 0} onClick={() => { present(part) }}>{text.present}</Button>
        <span className="text-[12px] text-fg-muted">{text.zoom}</span>
        <Segmented
          value={zoom}
          options={ZOOMS.map((value) => ({ value, label: value === 'fit' ? text.fit : `${Math.round(Number(value) * 100)}%` }))}
          onChange={(value) => { setZoom(value) }}
        />
      </div>
      {editable && available.size > 0 && <Ribbon available={available} states={states} run={run} listFonts={listFonts} />}
      <div className="flex min-h-0 flex-1">
        {count > 0 && size.width > 0 && (
          <Thumbnails
            doc={doc}
            count={count}
            current={part}
            size={size}
            available={editable ? available : new Set()}
            states={states}
            run={run}
            onSelect={switchPart}
          />
        )}
        <div ref={stage} className="flex min-h-0 min-w-0 flex-1">
          <DocumentCanvas
            doc={doc}
            opened={opened}
            zoom={zoomValue}
            part={part}
            centered
            label={text.canvas}
            onError={onError}
            onShortcut={onShortcut}
            onPartChange={setPart}
            controller={canvas}
          />
        </div>
      </div>
      <div className="flex shrink-0 items-center border-t border-hairline px-3 py-1 text-[11px] text-fg-muted">
        {count > 0 ? text.slideOf(Math.min(part, count - 1) + 1, count) : ''}
      </div>
      {showing !== null && (
        <Slideshow
          doc={doc}
          count={count}
          start={showing}
          size={size}
          onExit={(index) => {
            setShowing(null)
            // 停在放映到的那一张
            if (index !== part) switchPart(index)
            canvas.current?.focus()
          }}
        />
      )}
    </div>
  )
}

mount(<Slides />)
