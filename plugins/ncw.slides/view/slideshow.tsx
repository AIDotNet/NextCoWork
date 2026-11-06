/**
 * 放映:盖住整个视图的黑底层,一次一张幻灯片,按窗口大小等比放到最大。
 *
 * ## 怎么画
 *
 * 整张幻灯片按设备像素渲染。引擎单次渲染不超过 2048px 一边,所以大窗口 / 高 DPR 下切成若干块,
 * 逐块要像素再拼进同一个 canvas。当前页画完后预载下一张,翻页时直接换上。
 *
 * ## 限制(有意的)
 *
 * - **不是真全屏**:视图 iframe 没有 `allow="fullscreen"` —— 这是有意不开的,插件能全屏的话就能画一个
 *   以假乱真的宿主界面或系统对话框。放映只铺满这个标签页。
 * - **静态渲染**:引擎给的是每页的静态像素,没有动画、切换效果、媒体播放。
 */
import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import type { EngineDocument } from 'nextcowork/view'
import type { SlideSize } from './thumbnails'
import { text } from './messages'

/** 引擎单次渲染的像素上限(helper 的 kMaxRenderPixels) */
const MAX_RENDER_PX = 2048

interface Frame { index: number; width: number; height: number; canvas: HTMLCanvasElement }

/** 把整张幻灯片按 pxW × pxH 设备像素渲染出来(必要时分块) */
async function renderSlide(doc: EngineDocument, index: number, size: SlideSize, pxW: number, pxH: number): Promise<HTMLCanvasElement> {
  const canvas = document.createElement('canvas')
  canvas.width = pxW
  canvas.height = pxH
  const context = canvas.getContext('2d')
  if (context === null) throw new Error('no 2d context')
  const cols = Math.ceil(pxW / MAX_RENDER_PX)
  const rows = Math.ceil(pxH / MAX_RENDER_PX)
  // 像素边界与 twips 边界按同一比例取整:相邻块共用边界,不留缝
  const px = (i: number, n: number, total: number): number => Math.round(i * total / n)
  const tw = (p: number, total: number, twips: number): number => Math.round(p * twips / total)
  for (let row = 0; row < rows; row += 1) {
    for (let col = 0; col < cols; col += 1) {
      const x0 = px(col, cols, pxW)
      const x1 = px(col + 1, cols, pxW)
      const y0 = px(row, rows, pxH)
      const y1 = px(row + 1, rows, pxH)
      const tx0 = tw(x0, pxW, size.width)
      const ty0 = tw(y0, pxH, size.height)
      const tile = await doc.render({
        part: index,
        x: tx0,
        y: ty0,
        tileWidth: tw(x1, pxW, size.width) - tx0,
        tileHeight: tw(y1, pxH, size.height) - ty0,
        width: x1 - x0,
        height: y1 - y0
      })
      context.putImageData(new ImageData(tile.pixels, tile.width, tile.height), x0, y0)
    }
  }
  return canvas
}

export function Slideshow({ doc, count, start, size, onExit }: {
  doc: EngineDocument
  count: number
  start: number
  /** 幻灯片尺寸(twips) */
  size: SlideSize
  /** 退出时停在哪一张(放映结束页退出时是最后一张) */
  onExit: (index: number) => void
}): ReactNode {
  const [index, setIndex] = useState(start)
  const [box, setBox] = useState({ width: 0, height: 0 })
  const [frame, setFrame] = useState<Frame | null>(null)
  const [revision, setRevision] = useState(0)
  const root = useRef<HTMLDivElement>(null)
  const target = useRef<HTMLCanvasElement>(null)
  const cache = useRef(new Map<string, Promise<HTMLCanvasElement>>())

  useLayoutEffect(() => {
    const element = root.current
    if (element === null) return
    element.focus()
    const observer = new ResizeObserver(() => { setBox({ width: element.clientWidth, height: element.clientHeight }) })
    observer.observe(element)
    return () => { observer.disconnect() }
  }, [])

  // Agent 在放映期间改了文档:丢掉缓存,当前页重画
  useEffect(() => doc.onChange(() => {
    cache.current.clear()
    setRevision((n) => n + 1)
  }), [doc])

  const scale = box.width > 0 && box.height > 0 && size.width > 0 && size.height > 0
    ? Math.min(box.width / size.width, box.height / size.height)
    : 0
  const cssW = Math.floor(size.width * scale)
  const cssH = Math.floor(size.height * scale)
  const dpr = globalThis.devicePixelRatio > 0 ? globalThis.devicePixelRatio : 1

  const load = useCallback((slide: number): Promise<HTMLCanvasElement> | null => {
    if (slide < 0 || slide >= count || cssW <= 0 || cssH <= 0) return null
    const pxW = Math.round(cssW * dpr)
    const pxH = Math.round(cssH * dpr)
    const key = `${slide}:${pxW}x${pxH}:${revision}`
    let pending = cache.current.get(key)
    if (pending === undefined) {
      pending = renderSlide(doc, slide, size, pxW, pxH)
      // 失败的不留在缓存里,翻回来时重试
      pending.catch(() => { cache.current.delete(key) })
      cache.current.set(key, pending)
      // 只留最近几张:4K 屏上一张就是几十 MiB
      while (cache.current.size > 3) cache.current.delete(cache.current.keys().next().value!)
    }
    return pending
  }, [doc, count, size, cssW, cssH, dpr, revision])

  useEffect(() => {
    let live = true
    const pending = load(index)
    if (pending === null) return
    pending.then((canvas) => {
      if (!live) return
      setFrame({ index, width: cssW, height: cssH, canvas })
      // 当前页到手再预载下一张:引擎单线程,两张一起要只会让当前页更慢
      void load(index + 1)?.catch(() => undefined)
    }, () => undefined)
    return () => { live = false }
  }, [index, load, cssW, cssH])

  useLayoutEffect(() => {
    const canvas = target.current
    if (canvas === null || frame === null) return
    canvas.width = frame.canvas.width
    canvas.height = frame.canvas.height
    canvas.getContext('2d')?.drawImage(frame.canvas, 0, 0)
  }, [frame])

  const ended = index >= count
  const go = (next: number): void => { setIndex(Math.max(0, Math.min(count, next))) }
  const exit = (): void => { onExit(Math.min(index, count - 1)) }

  return (
    <div
      ref={root}
      role="dialog"
      aria-modal="true"
      aria-label={text.slideshow}
      tabIndex={-1}
      className="fixed inset-0 z-50 flex cursor-default select-none items-center justify-center bg-black outline-none"
      onClick={() => { if (ended) exit(); else go(index + 1) }}
      onKeyDown={(event) => {
        switch (event.key) {
          case 'ArrowRight': case 'ArrowDown': case ' ': case 'PageDown': case 'Enter': go(index + 1); break
          case 'ArrowLeft': case 'ArrowUp': case 'PageUp': case 'Backspace': go(index - 1); break
          case 'Home': go(0); break
          case 'End': go(count - 1); break
          case 'Escape': exit(); break
          default: return
        }
        event.preventDefault()
        event.stopPropagation()
      }}
    >
      {ended ? (
        <p className="text-[14px] text-white/80">{text.slideshowEnd}</p>
      ) : (
        <canvas
          ref={target}
          aria-label={text.slideN(index + 1)}
          // 还没画出这一张时显示上一张(翻页不闪黑);尺寸按当前窗口
          style={{ width: frame?.width ?? cssW, height: frame?.height ?? cssH, visibility: frame === null ? 'hidden' : 'visible' }}
        />
      )}
      <span className="pointer-events-none absolute bottom-3 right-4 text-[11px] text-white/40">
        {ended ? '' : `${text.slideOf(index + 1, count)} · ${text.exitSlideshow}`}
      </span>
    </div>
  )
}
