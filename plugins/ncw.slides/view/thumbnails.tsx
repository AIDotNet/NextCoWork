/**
 * 左侧缩略图栏:每张幻灯片一张小图,点击切页,上方是幻灯片管理(新建 / 复制 / 删除 / 上移 / 下移)。
 *
 * ## 怎么画
 *
 * 每张缩略图是一次整页渲染(`render` 带 `part`,区域是整张幻灯片,像素宽 = 显示宽 × DPR)。
 * 引擎单线程,所以**同一时刻只有一个请求在途**:先画当前页,再画可见的页(IntersectionObserver),
 * 看不见的页等滚到了再画。实测:用户正在文本框里打字时渲染别的幻灯片不会打断编辑。
 *
 * ## 什么时候重画
 *
 * 缩略图只标「过期」,旧图继续显示到新图到达(不闪白):
 * - 回执的失效矩形带 `part` → 那一页;不带 → 当前页;`invalidations.all` → 当前页;
 * - 回执带 `parts`(增删了幻灯片)或 `documentSizeChanged`(移动了顺序)→ 全部;
 * - Agent 改了文档(`onChange`)→ 全部。
 * 标完防抖 400ms 再画:连续打字时每个按键都重画一整页没有意义。
 */
import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import type { EngineDocument } from 'nextcowork/view'
import { ArrowDown, ArrowUp, Copy, Plus, Trash2 } from 'lucide-react'
import { CommandButton, type CommandStates, type Run } from '../../office-common/ribbon-parts'
import { text } from './messages'

/** 缩略图的 CSS 宽度 */
const THUMB_WIDTH = 160
/** 引擎单次渲染的像素上限(helper 的 kMaxRenderPixels) */
const MAX_RENDER_PX = 2048
const REDRAW_DEBOUNCE_MS = 400

export type SlideSize = { width: number; height: number }

interface Thumb { image: ImageData | null; stale: boolean }

function Thumbnail({ image, width, height }: { image: ImageData | null; width: number; height: number }): ReactNode {
  const canvas = useRef<HTMLCanvasElement>(null)
  useLayoutEffect(() => {
    const context = canvas.current?.getContext('2d')
    if (context === null || context === undefined || image === null) return
    canvas.current!.width = image.width
    canvas.current!.height = image.height
    context.putImageData(image, 0, 0)
  }, [image])
  return <canvas ref={canvas} className="block bg-white" style={{ width, height }} />
}

export function Thumbnails({ doc, count, current, size, available, states, run, onSelect }: {
  doc: EngineDocument
  /** 幻灯片数 */
  count: number
  current: number
  /** 幻灯片尺寸(twips) */
  size: SlideSize
  available: ReadonlySet<string>
  states: CommandStates
  run: Run
  onSelect: (index: number) => void
}): ReactNode {
  // ★ 账目放 ref(唯一真相),state 只是「该重渲染了」的信号:调度在 promise 回调里读账目,
  //   读 state 的话会读到还没提交的旧值,同一张图画两次或漏画
  const thumbs = useRef(new Map<number, Thumb>())
  const [, setVersion] = useState(0)
  const bump = useCallback(() => { setVersion((n) => n + 1) }, [])
  const visible = useRef(new Set<number>())
  const busy = useRef(false)
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const list = useRef<HTMLDivElement>(null)
  const items = useRef(new Map<number, HTMLElement>())
  const observer = useRef<IntersectionObserver | null>(null)
  // 回调里要读最新值,经 ref 取
  const latest = useRef({ current, count, size })
  latest.current = { current, count, size }

  const height = Math.max(1, Math.round(THUMB_WIDTH * size.height / Math.max(1, size.width)))

  /** 画下一张:当前页优先,其次可见页;没有缺的 / 过期的就停 */
  const pump = useCallback(() => {
    if (busy.current) return
    const known = thumbs.current
    const { current: now, count: total, size: slide } = latest.current
    const needs = (index: number): boolean => index < total && (known.get(index)?.stale ?? true)
    const next = needs(now) ? now : [...visible.current].sort((a, b) => a - b).find(needs)
    if (next === undefined || slide.width <= 0 || slide.height <= 0) return
    const dpr = globalThis.devicePixelRatio > 0 ? globalThis.devicePixelRatio : 1
    const width = Math.min(MAX_RENDER_PX, Math.round(THUMB_WIDTH * dpr))
    const pixelHeight = Math.min(MAX_RENDER_PX, Math.max(1, Math.round(width * slide.height / slide.width)))
    busy.current = true
    // 先落「已在画」:画的途中又被标过期的话,回来时 stale 仍是 true,会再画一次
    known.set(next, { image: known.get(next)?.image ?? null, stale: false })
    doc.render({ part: next, x: 0, y: 0, tileWidth: slide.width, tileHeight: slide.height, width, height: pixelHeight }).then(
      (tile) => {
        const image = new ImageData(tile.pixels, tile.width, tile.height)
        known.set(next, { image, stale: known.get(next)?.stale ?? false })
        bump()
      },
      // 画不出来(引擎重启、这张刚被删)就保持旧图;下一次标记会再试,不在这里重试成循环
      () => undefined
    ).finally(() => {
      busy.current = false
      pumpRef.current()
    })
  }, [doc, bump])
  const pumpRef = useRef(pump)
  pumpRef.current = pump

  const markStale = useCallback((which: 'all' | number[]) => {
    for (const [index, thumb] of thumbs.current) {
      if (which === 'all' || which.includes(index)) thumb.stale = true
    }
    clearTimeout(timer.current)
    timer.current = setTimeout(() => { pumpRef.current() }, REDRAW_DEBOUNCE_MS)
  }, [])

  useEffect(() => {
    const stopResult = doc.onResult((result) => {
      if (result.parts !== undefined || result.documentSizeChanged === true) {
        markStale('all')
        return
      }
      const now = latest.current.current
      const pages = new Set<number>()
      if (result.invalidations.all) pages.add(now)
      for (const rect of result.invalidations.rects) pages.add(rect.part ?? now)
      if (pages.size > 0) markStale([...pages])
    })
    const stopChange = doc.onChange(() => { markStale('all') })
    return () => {
      stopResult()
      stopChange()
      clearTimeout(timer.current)
    }
  }, [doc, markStale])

  // 数量、当前页、尺寸变了:删掉多出来的,补画缺的
  useEffect(() => {
    for (const index of [...thumbs.current.keys()]) {
      if (index >= count) thumbs.current.delete(index)
    }
    pumpRef.current()
  }, [count, current, size])

  useEffect(() => {
    const root = list.current
    if (root === null) return
    const watcher = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        const index = Number((entry.target as HTMLElement).dataset.index)
        if (entry.isIntersecting) visible.current.add(index)
        else visible.current.delete(index)
      }
      pumpRef.current()
    }, { root })
    observer.current = watcher
    for (const element of items.current.values()) watcher.observe(element)
    return () => {
      watcher.disconnect()
      observer.current = null
      visible.current.clear()
    }
  }, [])

  const itemRef = useCallback((index: number) => (element: HTMLElement | null) => {
    const previous = items.current.get(index)
    if (previous !== undefined && previous !== element) {
      observer.current?.unobserve(previous)
      visible.current.delete(index)
    }
    if (element === null) {
      items.current.delete(index)
      return
    }
    items.current.set(index, element)
    observer.current?.observe(element)
  }, [])

  useEffect(() => {
    items.current.get(current)?.scrollIntoView({ block: 'nearest' })
  }, [current])

  const button = (id: Parameters<Run>[0], label: string, icon: ReactNode): ReactNode =>
    <CommandButton id={id} label={label} icon={icon} available={available} states={states} run={run} />

  return (
    <div className="flex w-[208px] shrink-0 flex-col border-r border-hairline bg-surface">
      <div className="flex shrink-0 items-center gap-0.5 border-b border-hairline px-2 py-1">
        {button('slides.new', text.newSlide, <Plus size={16} />)}
        {button('slides.duplicate', text.duplicateSlide, <Copy size={16} />)}
        {button('slides.delete', text.deleteSlide, <Trash2 size={16} />)}
        {button('slides.moveUp', text.moveUp, <ArrowUp size={16} />)}
        {button('slides.moveDown', text.moveDown, <ArrowDown size={16} />)}
      </div>
      <div
        ref={list}
        role="listbox"
        aria-label={text.slides}
        aria-activedescendant={`ncw-slide-${current}`}
        tabIndex={0}
        className="min-h-0 flex-1 overflow-y-auto py-2 outline-none"
        onKeyDown={(event) => {
          if (event.key === 'ArrowUp' && current > 0) onSelect(current - 1)
          else if (event.key === 'ArrowDown' && current < count - 1) onSelect(current + 1)
          else if (event.key === 'Home' && count > 0) onSelect(0)
          else if (event.key === 'End' && count > 0) onSelect(count - 1)
          else return
          event.preventDefault()
        }}
      >
        {Array.from({ length: count }, (_, index) => (
          <div
            key={index}
            id={`ncw-slide-${index}`}
            ref={itemRef(index)}
            data-index={index}
            role="option"
            aria-selected={index === current}
            aria-label={text.slideN(index + 1)}
            className="flex cursor-default items-start gap-1.5 px-2 py-1.5"
            onClick={() => { onSelect(index) }}
          >
            <span className="w-5 shrink-0 pt-0.5 text-right text-[11px] tabular-nums text-fg-muted">{index + 1}</span>
            <div className={index === current ? 'rounded-sm ring-2 ring-accent' : 'rounded-sm ring-1 ring-hairline'}>
              <Thumbnail image={thumbs.current.get(index)?.image ?? null} width={THUMB_WIDTH} height={height} />
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}
