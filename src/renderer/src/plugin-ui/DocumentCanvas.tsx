/**
 * 办公文档画布 —— `nextcowork/view` 的 `DocumentCanvas`,宿主下发给所有绑定了文档引擎的编辑器。
 *
 * ## 为了什么需求建的
 *
 * Word / Excel / PPT 编辑器的中间那块都是同一件事(计划 §7.2):按块向引擎要像素、只重画
 * 失效的块、画光标与选区、把键鼠与输入法交给引擎、Agent 改了文档时重画。这件事做错的
 * 表现(点击落点偏一个字、输入法候选窗飞到屏幕角落、打字慢一拍才出现、某个缩放下 tile 之间
 * 有缝)都只在特定机器、特定缩放下出现,每个插件各写一份等于每个插件各踩一遍。所以画布
 * 由宿主提供,插件只做 Ribbon、侧栏这些产品界面。(计划原写 `packages/office-plugin-common`;
 * 改放宿主运行时,是因为测试只收 `src/**`,而且下发的运行时天然只有一份实现。)
 *
 * ## 分工
 *
 * - 账目(哪块缺、哪块旧、超预算丢谁):`shared/document-engine/tile-cache.ts`
 * - 换算(twips ↔ px、tile 网格、键码):`shared/document-engine/viewport.ts` / `keys.ts`
 * - 输入的合批与键位:同目录 `canvas-input.ts`
 * - 这个组件只做 DOM 胶水。
 *
 * ## 故意不做的
 *
 * - 不画 Excel 行列头、PPT 缩略图:那是各产品的界面,由插件在画布旁边自己画。
 * - 只读文档(引擎没声明 `interaction`)不画光标、不接输入 —— 画出来就是一个会失败的承诺。
 */
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import type { DocumentInputEvent, DocumentRect } from '../../../shared/document-engine/interaction'
import { TileCache, type TileConfig } from '../../../shared/document-engine/tile-cache'
import { cssPxToTwips, grownSheetReach, tileRequest, tilesInView, twipsRectToCss, twipsToCssPx, type CssRect, type TileIndex } from '../../../shared/document-engine/viewport'
import { cn } from '../lib/cn'
import { InputQueue, LatePulls, PULL_AFTER_INPUT_MS, PULL_AFTER_OPEN_MS, keyEvent, keyPlatformOf, lokButtonOf, lokButtons, lokMouseModifier, needsLatePull } from './canvas-input'
import { EngineDocumentError, type EngineDocument, type EngineDocumentOpened } from './engine-document'
import type { EngineInputResult } from '../../../shared/document-engine/view-frame'

/** 每块 tile 的设备像素边长。256² RGBA = 256 KiB,滚动时一屏十几块 */
const TILE_PX = 256
/** 同时在途的渲染请求。引擎单线程,多发只是在会话队列里排队,反而让可见块等离屏块 */
const MAX_IN_FLIGHT = 2
/** 像素缓存预算。超了按最久没用淘汰,可见块不淘汰(见 tile-cache) */
const CACHE_BYTES = 96 * 1024 * 1024
/** 滚动停下多久之后向引擎登记可见区域(翻页键按它翻)。边滚边登记是每帧一次请求 */
const VISIBLE_AREA_DEBOUNCE_MS = 150

interface Layout {
  width: number
  height: number
  /** 'text' 的文档没有 part,渲染请求不能带 part(引擎会拒) */
  documentType: string
}

function parseLayout(raw: unknown): Layout | null {
  if (raw === null || typeof raw !== 'object') return null
  const record = raw as Record<string, unknown>
  if (typeof record.width !== 'number' || typeof record.height !== 'number') return null
  return { width: record.width, height: record.height, documentType: typeof record.documentType === 'string' ? record.documentType : '' }
}

/** 一块 tile:拿到像素就画进自己的 canvas。★ 用 layout effect,否则新像素会晚一帧出现、滚动时闪 */
function TileView({ data, box }: { data: ImageData; box: CssRect }): ReactNode {
  const ref = useRef<HTMLCanvasElement>(null)
  useLayoutEffect(() => {
    ref.current?.getContext('2d')?.putImageData(data, 0, 0)
  }, [data])
  return (
    <canvas
      ref={ref}
      width={data.width}
      height={data.height}
      className="pointer-events-none absolute"
      style={{ left: box.left, top: box.top, width: box.width, height: box.height }}
    />
  )
}

export function DocumentCanvas({
  doc,
  opened,
  zoom = 1,
  part,
  label,
  className,
  onError,
  onPartChange,
  onShortcut,
  controller,
  onViewport,
  centered = false
}: {
  /** `openEngineDocument()` 的返回值;画布不负责打开与 dispose */
  doc: EngineDocument
  /** `doc.ready` 的结果:能力决定画不画光标、接不接输入 */
  opened: EngineDocumentOpened
  /** 1 = 100% */
  zoom?: number
  /** 工作表 / 幻灯片。Writer 省略 */
  part?: number
  /** 无障碍名字,插件自己翻译好 */
  label: string
  className?: string
  /** 画布自己处理不了的失败(引擎崩溃、结果未知),交给插件决定怎么提示 */
  onError?: (error: EngineDocumentError) => void
  /** 用户操作让引擎切换了当前工作表 / 幻灯片(例如 Ctrl+PageDown) */
  onPartChange?: (part: number) => void
  /**
   * 按键交给引擎之前先问插件。返回 true = 插件处理了,不送引擎。
   * ★ 保存(Ctrl/Cmd+S)必须在这里截走:送进引擎的话 LibreOffice 会把模型存进它自己的
   *   私有工作副本 —— 用户以为存了,工作区里的文件纹丝不动,会话也仍然是脏的。
   */
  onShortcut?: (event: KeyboardEvent) => boolean
  /**
   * 插件拿到的控制把手。现在只有 `focus()`:点完功能区按钮后把焦点还给画布,
   * 否则用户接着打的字进了按钮(或者哪儿也没进)。
   */
  controller?: { current: { focus: () => void } | null }
  /**
   * 可见区域变了(滚动、尺寸、缩放、版面)。`css` 是滚动后的 CSS px,`twips` 是同一块文档区域。
   * 表格插件据此取行列头、并把行号列标跟着滚动对齐。
   */
  onViewport?: (viewport: { css: CssRect; twips: { x: number; y: number; width: number; height: number } }) => void
  /**
   * 把文档居中放在留白里(演示的幻灯片)。缺省贴左上角(Writer 的页面自带页边距,表格从 A1 开始)。
   * ★ 居中之后文档不再从滚动原点开始:可见块与 `onViewport` 都要扣掉文档的偏移,见 measure。
   */
  centered?: boolean
}): ReactNode {
  const scroller = useRef<HTMLDivElement>(null)
  const content = useRef<HTMLDivElement>(null)
  const input = useRef<HTMLTextAreaElement>(null)
  const cache = useRef(new TileCache<ImageData>(CACHE_BYTES))
  const inFlight = useRef(0)
  const generation = useRef(opened.state.generation)
  const [layout, setLayout] = useState<Layout | null>(null)
  /** 表格:已滚到的最远处再留一屏(见 grownSheetReach)。其余文档类型不用 */
  const [reach, setReach] = useState({ width: 0, height: 0 })
  const [visible, setVisible] = useState<TileIndex[]>([])
  const [, setFrame] = useState(0)
  const [cursor, setCursor] = useState<DocumentRect | null>(null)
  const [cursorShown, setCursorShown] = useState(true)
  const [selection, setSelection] = useState<DocumentRect[]>([])
  const [cellCursor, setCellCursor] = useState<DocumentRect | null>(null)
  const [focused, setFocused] = useState(false)
  const interaction = opened.capabilities.interaction
  const platform = useMemo(() => keyPlatformOf(typeof navigator === 'undefined' ? '' : navigator.platform), [])
  const partOf = part ?? 0

  const report = useCallback((error: unknown) => {
    if (error instanceof EngineDocumentError) onError?.(error)
  }, [onError])

  const relayout = useCallback(() => {
    doc.layout(part).then((result) => {
      generation.current = result.generation
      setLayout(parseLayout(result.layout))
    }, report)
  }, [doc, part, report])

  useEffect(() => { relayout() }, [relayout])
  // 换了工作表:那张表从头算余量
  useEffect(() => { setReach({ width: 0, height: 0 }) }, [partOf])

  /** 可滚动、可渲染的范围(twips):表格 = 引擎尺寸与余量取大;其余 = 引擎尺寸 */
  const extent = useMemo(() => layout === null ? null
    : layout.documentType === 'spreadsheet'
      ? { width: Math.max(layout.width, reach.width), height: Math.max(layout.height, reach.height) }
      : { width: layout.width, height: layout.height }, [layout, reach])

  const configOf = useCallback((): TileConfig => ({
    grid: { zoom, dpr: globalThis.devicePixelRatio > 0 ? globalThis.devicePixelRatio : 1, tilePx: TILE_PX },
    generation: generation.current,
    part: partOf
  }), [zoom, partOf])

  /** 最近一次的可见区域(twips),防抖后登记给引擎(见 sendVisibleAreaRef 的赋值处) */
  const visibleArea = useRef<{ x: number; y: number; width: number; height: number } | null>(null)
  const visibleAreaTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const sendVisibleAreaRef = useRef<() => void>(() => undefined)
  useEffect(() => () => { clearTimeout(visibleAreaTimer.current) }, [])

  /** 按当前滚动位置算可见块。★ 每次滚动 / 尺寸变化 / 配置变化都要算,少一次就是边缘露白 */
  const measure = useCallback(() => {
    const box = scroller.current
    if (box === null || layout === null || extent === null) return
    // 文档相对滚动原点的偏移(居中时 > 0;content 的 offsetParent 就是这个 relative 的滚动框)
    const offsetX = content.current?.offsetLeft ?? 0
    const offsetY = content.current?.offsetTop ?? 0
    const view = { left: Math.max(0, box.scrollLeft - offsetX), top: Math.max(0, box.scrollTop - offsetY), width: box.clientWidth, height: box.clientHeight }
    setVisible(tilesInView(view, configOf().grid, extent))
    const x = cssPxToTwips(view.left, zoom)
    const y = cssPxToTwips(view.top, zoom)
    const twips = { x, y, width: Math.max(1, cssPxToTwips(view.left + view.width, zoom) - x), height: Math.max(1, cssPxToTwips(view.top + view.height, zoom) - y) }
    if (layout.documentType === 'spreadsheet') setReach((current) => grownSheetReach(current, twips))
    visibleArea.current = twips
    clearTimeout(visibleAreaTimer.current)
    visibleAreaTimer.current = setTimeout(() => { sendVisibleAreaRef.current() }, VISIBLE_AREA_DEBOUNCE_MS)
    viewportRef.current?.({ css: view, twips })
  }, [layout, extent, configOf, zoom])
  const viewportRef = useRef(onViewport)
  viewportRef.current = onViewport

  /** 向引擎要缺失 / 过期的可见块 */
  const pump = useCallback(() => {
    if (layout === null) return
    const config = configOf()
    if (cache.current.configure(config)) setFrame((n) => n + 1)
    const planned = cache.current.plan(visible, MAX_IN_FLIGHT - inFlight.current)
    for (const { tile, ticket } of planned) {
      inFlight.current += 1
      const request = tileRequest(tile, config.grid)
      doc.render(layout.documentType === 'text' ? request : { ...request, part: config.part }).then(
        (result) => {
          const image = new ImageData(result.pixels, result.width, result.height)
          if (cache.current.put(tile, ticket, config, image, result.pixels.byteLength, visible)) setFrame((n) => n + 1)
        },
        (error: unknown) => {
          cache.current.fail(tile, config)
          // 引擎重启过:坐标全部作废,重取版面(会重建网格);其余失败交给插件
          if (error instanceof EngineDocumentError && error.code === 'stale_generation') relayout()
          else report(error)
        }
      ).finally(() => {
        inFlight.current -= 1
        pumpRef.current()
      })
    }
  }, [doc, layout, visible, configOf, relayout, report])
  // 回执里要调「最新的」pump(那时 visible 可能已经变了),经 ref 取,不闭包旧的
  const pumpRef = useRef(pump)
  pumpRef.current = pump
  useEffect(() => { pump() }, [pump])

  useLayoutEffect(() => {
    measure()
    const box = scroller.current
    if (box === null) return
    let frame = 0
    const onScroll = (): void => {
      cancelAnimationFrame(frame)
      frame = requestAnimationFrame(measure)
    }
    box.addEventListener('scroll', onScroll, { passive: true })
    const observer = new ResizeObserver(onScroll)
    observer.observe(box)
    return () => {
      cancelAnimationFrame(frame)
      box.removeEventListener('scroll', onScroll)
      observer.disconnect()
    }
  }, [measure])

  /** 一批输入(或拉取)的回执:失效 → 重画;光标 / 选区 → 覆盖层 */
  const applyResult = useCallback((result: EngineInputResult) => {
    if (result.generation !== generation.current) {
      generation.current = result.generation
      relayout()
      return
    }
    if (extent !== null) cache.current.invalidate(result.invalidations, extent)
    if (result.cursor !== undefined) setCursor(result.cursor)
    if (result.cursorVisible !== undefined) setCursorShown(result.cursorVisible)
    if (result.selection !== undefined) setSelection(result.selection)
    if (result.cellCursor !== undefined) setCellCursor(result.cellCursor)
    if (result.documentSizeChanged === true) relayout()
    if (result.part !== undefined && result.part !== partOf) onPartChange?.(result.part)
    setFrame((n) => n + 1)
    pumpRef.current()
  }, [extent, partOf, relayout, onPartChange])

  /*
    ★ 回执一律经 `doc.onResult` 应用,不走队列自己的回调:功能区命令(插件直接调 `doc.command`)
      的回执也要让画布重画、更新光标;两条路各应用一次的话,同一个失效区会画两遍。
  */
  const queue = useMemo(() => new InputQueue<EngineInputResult>(
    (events) => doc.input(generation.current, events),
    () => undefined,
    (error) => {
      if (error instanceof EngineDocumentError && error.code === 'stale_generation') relayoutRef.current()
      else report(error)
    }
  ), [doc, report])
  const applyRef = useRef(applyResult)
  applyRef.current = applyResult
  useEffect(() => doc.onResult((result) => { applyRef.current(result) }), [doc])
  const relayoutRef = useRef(relayout)
  relayoutRef.current = relayout
  useEffect(() => () => { queue.close() }, [queue])

  /*
    向引擎登记可见区域(翻页键按真实可见高度翻;实测不登记时 PageDown 一次跳 76、130、260 行)。
    只在变了时发;直接进队列而不经 push:它不是用户输入,不该排迟到拉取。
  */
  const sentArea = useRef('')
  sendVisibleAreaRef.current = () => {
    const area = visibleArea.current
    // 旧版引擎不认识这种事件,会连同同一批的按键整批拒掉(见 DocumentInteraction.visibleArea)
    if (area === null || interaction?.visibleArea !== true) return
    const key = `${generation.current}:${area.x},${area.y},${area.width},${area.height}`
    if (key === sentArea.current) return
    sentArea.current = key
    queue.push([{ type: 'viewport', ...area }])
  }

  /*
    补拉迟到事件(时刻与理由见 canvas-input 的 PULL_AFTER_INPUT_MS)。
    ★ 不补的话:改了之后画布一直画着改之前的字形(Electron 实测:功能区「加粗」的回执里失效区是空的);
      移动光标后功能区按钮停在上一个位置的状态。
  */
  const latePulls = useMemo(() => new LatePulls(() => { queue.push([]) }), [queue])
  useEffect(() => doc.onResult((result) => {
    if (needsLatePull(result)) latePulls.schedule(PULL_AFTER_INPUT_MS)
  }), [doc, latePulls])
  useEffect(() => () => { latePulls.cancel() }, [latePulls])

  /*
    版面第一次到手时拉一次:光标位置与功能区的当前状态(字体、加粗……)要在用户动手之前就有 ——
    helper 对空批次会补发全量状态。★ 第一批状态在打开后一两秒才到(引擎空闲时发),所以再按
    PULL_AFTER_OPEN_MS 补拉;不补的话字体框、样式框要等用户第一次动手才有值。
  */
  const pulled = useRef(false)
  useEffect(() => {
    if (layout === null || pulled.current) return
    pulled.current = true
    queue.push([])
    latePulls.schedule(PULL_AFTER_OPEN_MS)
  }, [layout, queue, latePulls])

  useEffect(() => {
    if (controller === undefined) return
    controller.current = { focus: () => { input.current?.focus({ preventScroll: true }) } }
    return () => { controller.current = null }
  }, [controller])

  // Agent 改了 / 存了:拉一次迟到事件,拿到要重画的区域(不重复记修订号,见 helper 的 input)
  useEffect(() => doc.onChange((state) => {
    if (state.generation !== generation.current) relayoutRef.current()
    else queue.push([])
  }), [doc, queue])

  const push = useCallback((events: DocumentInputEvent[]) => {
    if (events.length === 0) return
    queue.push(events)
    // 按键没动光标也没改模型时(比如方向键撞到文档边),回执不会排补拉;这里先排上
    latePulls.schedule(PULL_AFTER_INPUT_MS)
  }, [queue, latePulls])

  const pointAt = (event: { clientX: number; clientY: number }): { x: number; y: number } => {
    const rect = content.current?.getBoundingClientRect()
    return { x: cssPxToTwips(event.clientX - (rect?.left ?? 0), zoom), y: cssPxToTwips(event.clientY - (rect?.top ?? 0), zoom) }
  }

  const editable = interaction?.keyboard === true || interaction?.mouse === true
  const cursorBox = cursor === null ? null : twipsRectToCss(cursor, zoom)

  return (
    <div ref={scroller} className={cn('relative min-h-0 flex-1 overflow-auto bg-surface-sunken', centered && 'flex p-6', className)}>
      <div
        ref={content}
        role="document"
        aria-label={label}
        // m-auto 而不是 justify-center:文档比窗口大时,justify-center 会把左边一截推到滚动原点之外、滚不回来
        className={cn('relative', centered && 'm-auto shrink-0 shadow-sm shadow-black/20')}
        style={extent === null ? undefined : { width: twipsToCssPx(extent.width, zoom), height: twipsToCssPx(extent.height, zoom) }}
        onPointerDown={(event) => {
          if (interaction?.mouse !== true) return
          // ★ 焦点给隐藏输入框:键盘与输入法事件只从它来。preventDefault 防止点击把焦点抢回 div
          event.preventDefault()
          input.current?.focus({ preventScroll: true })
          event.currentTarget.setPointerCapture(event.pointerId)
          push([{ type: 'mouse', action: 'down', ...pointAt(event), count: Math.min(3, Math.max(1, event.detail)), buttons: lokButtonOf(event.button), modifier: lokMouseModifier(event, platform) }])
        }}
        onPointerMove={(event) => {
          if (interaction?.mouse !== true || event.buttons === 0) return
          push([{ type: 'mouse', action: 'move', ...pointAt(event), count: 1, buttons: lokButtons(event.buttons), modifier: lokMouseModifier(event, platform) }])
        }}
        onPointerUp={(event) => {
          if (interaction?.mouse !== true) return
          push([{ type: 'mouse', action: 'up', ...pointAt(event), count: Math.min(3, Math.max(1, event.detail)), buttons: lokButtonOf(event.button), modifier: lokMouseModifier(event, platform) }])
        }}
      >
        {layout !== null && visible.map((tile) => {
          const cached = cache.current.get(tile)
          if (cached === undefined) return null
          const request = tileRequest(tile, configOf().grid)
          return <TileView key={`${tile.col}:${tile.row}`} data={cached.data} box={twipsRectToCss({ x: request.x, y: request.y, width: request.tileWidth, height: request.tileHeight }, zoom)} />
        })}
        {selection.map((rect, index) => {
          const box = twipsRectToCss(rect, zoom)
          // 选区矩形没有稳定身份,引擎每次整组重报;序号作 key 是对的
          return <div key={index} className="pointer-events-none absolute bg-accent/25" style={box} />
        })}
        {cellCursor !== null && <div className="pointer-events-none absolute border-2 border-accent" style={twipsRectToCss(cellCursor, zoom)} />}
        {editable && focused && cursorShown && cursorBox !== null && (
          <div className="pointer-events-none absolute bg-page-ink" style={{ left: cursorBox.left, top: cursorBox.top, width: Math.max(1, cursorBox.width), height: cursorBox.height }} />
        )}
        {editable && (
          <textarea
            ref={input}
            aria-label={label}
            aria-multiline="true"
            autoCapitalize="off"
            autoComplete="off"
            spellCheck={false}
            // ★ 跟着光标走:输入法候选窗按这个输入框的位置弹出,放在左上角就飞到屏幕角落
            className="pointer-events-none absolute h-4 w-px resize-none overflow-hidden border-0 p-0 opacity-0"
            style={{ left: cursorBox?.left ?? 0, top: cursorBox?.top ?? 0 }}
            onFocus={() => { setFocused(true) }}
            onBlur={() => { setFocused(false) }}
            onKeyDown={(event) => {
              if (event.nativeEvent.isComposing) return
              if (onShortcut?.(event.nativeEvent) === true) {
                event.preventDefault()
                return
              }
              const events = keyEvent(event, 'press', platform)
              if (events.length === 0) return
              event.preventDefault()
              push(events)
            }}
            onKeyUp={(event) => {
              if (event.nativeEvent.isComposing) return
              push(keyEvent(event, 'release', platform))
            }}
            onCompositionUpdate={(event) => { push([{ type: 'text', action: 'compose', text: event.data }]) }}
            onCompositionEnd={(event) => {
              // 提交空串 = 用户把拼音删光了:取消组字,而不是提交一个空字符
              push([event.data === '' ? { type: 'text', action: 'compose', text: '' } : { type: 'text', action: 'commit', text: event.data }])
              event.currentTarget.value = ''
            }}
            onInput={(event) => {
              // 文字全部经按键 / 组字事件送给引擎;输入框自己不留内容,否则下一次组字带着旧字
              if (!(event.nativeEvent as InputEvent).isComposing) event.currentTarget.value = ''
            }}
          />
        )}
      </div>
    </div>
  )
}
