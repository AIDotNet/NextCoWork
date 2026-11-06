/**
 * 表格(.xlsx)编辑器视图 —— 跑在 `ncw-plugin://ncw.sheets` 的 iframe 里。
 *
 * ## 为了什么需求建的
 *
 * 用户要在 NextCoWork 里像 WPS 表格一样看、改 Excel 文件,并且与 Agent 改的是同一份活动模型
 * (计划 §1、§7.2「Sheets 行列头 / 公式栏 / 工作表标签」)。格子与网格线由引擎画(宿主的
 * `DocumentCanvas`);这里做表格特有的外壳:功能区、名称框 + 编辑栏、行号列标、工作表标签。
 *
 * ## 这一版故意不做的
 *
 * - 插入 / 删除 / 重命名工作表、拖动改列宽、冻结窗格、筛选排序、图表:需要引擎侧的命令,还没有;
 *   没有后端能力的按钮不画。
 * - 工作表可滚动范围跟随引擎报的文档尺寸(已用区域附近),还没有向引擎登记「客户端可见区域」来扩展它。
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Button, EmptyState, Segmented, Spinner } from 'nextcowork/ui'
import {
  DocumentCanvas, mount, openEngineDocument,
  type EngineDocument, type EngineDocumentError, type EngineDocumentOpened, type EngineDocumentState
} from 'nextcowork/view'
import type { CommandStates, Run } from '../../office-common/ribbon-parts'
import { COLUMN_HEADER_HEIGHT, ColumnHeaders, FormulaBar, ROW_HEADER_WIDTH, RowHeaders, SheetTabs, type HeaderList } from './grid-chrome'
import { text } from './messages'
import { Ribbon } from './ribbon'

const ZOOMS = ['0.75', '1', '1.25', '1.5'] as const
const isMac = /mac/i.test(navigator.platform)
/** 滚动停下多久之后重取行列头:边滚边取的话,每一帧一次查询 */
const HEADERS_DEBOUNCE_MS = 80

type Area = { x: number; y: number; width: number; height: number }

function Sheets(): React.ReactNode {
  const [doc, setDoc] = useState<EngineDocument | null>(null)
  const [opened, setOpened] = useState<EngineDocumentOpened | null>(null)
  const [state, setState] = useState<EngineDocumentState | null>(null)
  const [states, setStates] = useState<CommandStates>({})
  const [address, setAddress] = useState('')
  const [formula, setFormula] = useState('')
  const [openError, setOpenError] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [zoom, setZoom] = useState<(typeof ZOOMS)[number]>('1')
  const [part, setPart] = useState(0)
  const [parts, setParts] = useState<string[]>([])
  const [headers, setHeaders] = useState<{ rows: HeaderList; columns: HeaderList }>({ rows: [], columns: [] })
  const [scroll, setScroll] = useState({ left: 0, top: 0 })
  const area = useRef<Area | null>(null)
  const headersTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const canvas = useRef<{ focus: () => void } | null>(null)

  const onError = useCallback((error: EngineDocumentError) => {
    setProblem(error.code === 'engine_unavailable' || error.code === 'result_unknown' || error.code === 'session_closed' ? text.crashed : `${text.failed} (${error.code})`)
  }, [])

  /** 按当前可见区域重取行列头(防抖) */
  const refreshHeaders = useCallback((next: EngineDocument | null) => {
    clearTimeout(headersTimer.current)
    headersTimer.current = setTimeout(() => {
      const current = area.current
      if (next === null || current === null) return
      next.headers(current).then(setHeaders, () => undefined)
    }, HEADERS_DEBOUNCE_MS)
  }, [])

  useEffect(() => {
    // ★ 每个视图只打开一次:每次调用宿主都会重开一次会话视图;cleanup 必须 dispose
    const next = openEngineDocument()
    setDoc(next)
    next.ready.then(
      (result) => {
        setOpened(result)
        setState(result.state)
        next.list('parts').then(setParts, () => undefined)
      },
      (error: EngineDocumentError) => { setOpenError(`${text.openFailed} (${error.code})`) }
    )
    const stopChange = next.onChange((changed) => {
      setState(changed)
      // Agent 可能加了 / 删了工作表:修订号变了就重取标签
      next.list('parts').then(setParts, () => undefined)
    })
    const stopResult = next.onResult((result) => {
      const changed = result.states
      if (changed !== undefined) setStates((current) => ({ ...current, ...changed }))
      if (result.cellAddress !== undefined) setAddress(result.cellAddress)
      if (result.cellFormula !== undefined) setFormula(result.cellFormula)
      if (result.headersChanged === true || result.documentSizeChanged === true) refreshHeaders(next)
    })
    return () => {
      stopChange()
      stopResult()
      clearTimeout(headersTimer.current)
      next.dispose()
    }
  }, [refreshHeaders])

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
    // 先让引擎切到那张表(之后的点选与键入落在它上面),再让画布按新表取版面
    doc.input(generation, [{ type: 'part', part: index }]).then(() => {
      setPart(index)
      canvas.current?.focus()
    }, onError)
  }, [doc, part, generation, onError])

  const onShortcut = useCallback((event: KeyboardEvent) => {
    // 保存经宿主存回工作区;送进引擎的话它会存进自己的私有副本(见 DocumentCanvas 的说明)
    const primary = isMac ? event.metaKey : event.ctrlKey
    if (primary && !event.altKey && event.code === 'KeyS') {
      save()
      return true
    }
    return false
  }, [save])

  const onViewport = useCallback((viewport: { css: { left: number; top: number }; twips: Area }) => {
    area.current = viewport.twips
    setScroll({ left: viewport.css.left, top: viewport.css.top })
    refreshHeaders(doc)
  }, [doc, refreshHeaders])

  const available = useMemo(() => new Set<string>(opened?.capabilities.commands ?? []), [opened])
  const listFonts = useCallback(() => doc?.list('fonts') ?? Promise.resolve([]), [doc])
  const [activeColumn, activeRow] = useMemo(() => {
    const match = /^\$?([A-Z]+)\$?([0-9]+)/.exec(address)
    return [match?.[1] ?? '', match?.[2] ?? '']
  }, [address])

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
  const zoomValue = Number(zoom)

  return (
    <div className="flex h-full flex-col bg-canvas">
      <div className="flex shrink-0 items-center gap-2 border-b border-hairline px-3 py-1.5">
        <Button variant="accent" size="sm" disabled={!canSave || !state.dirty || saving} onClick={save}>{text.save}</Button>
        <span className="min-w-0 flex-1 truncate text-[12px] text-fg-muted">{opened.path} · {status}</span>
        {problem !== null && <span role="alert" className="truncate text-[12px] text-danger">{problem}</span>}
        <span className="text-[12px] text-fg-muted">{text.zoom}</span>
        <Segmented
          value={zoom}
          options={ZOOMS.map((value) => ({ value, label: `${Math.round(Number(value) * 100)}%` }))}
          onChange={(value) => { setZoom(value) }}
        />
      </div>
      {editable && available.size > 0 && <Ribbon available={available} states={states} run={run} listFonts={listFonts} />}
      {editable && available.has('cells.enter') && (
        <FormulaBar
          address={address}
          formula={formula}
          onGoto={(ref) => { run('cells.goto', { ref }) }}
          onEnter={(value) => { run('cells.enter', { text: value }) }}
        />
      )}
      <div className="flex shrink-0">
        <div className="shrink-0 border-b border-r border-hairline bg-surface" style={{ width: ROW_HEADER_WIDTH, height: COLUMN_HEADER_HEIGHT }} />
        <ColumnHeaders columns={headers.columns} zoom={zoomValue} scrollLeft={scroll.left} active={activeColumn} />
      </div>
      <div className="flex min-h-0 flex-1">
        <RowHeaders rows={headers.rows} zoom={zoomValue} scrollTop={scroll.top} active={activeRow} />
        <DocumentCanvas
          doc={doc}
          opened={opened}
          zoom={zoomValue}
          part={part}
          label={text.canvas}
          onError={onError}
          onShortcut={onShortcut}
          onPartChange={setPart}
          onViewport={onViewport}
          controller={canvas}
        />
      </div>
      {parts.length > 0 && <SheetTabs names={parts} current={part} onSelect={switchPart} />}
    </div>
  )
}

mount(<Sheets />)
