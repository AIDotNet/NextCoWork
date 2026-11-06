/**
 * 文字(.docx)编辑器视图 —— 跑在 `ncw-plugin://ncw.writer` 的 iframe 里。
 *
 * ## 为了什么需求建的
 *
 * 用户要在 NextCoWork 里像 WPS 一样直接看、改 Word 文档,并且与 Agent 改的是同一份活动模型
 * (计划 §1、§7)。画布由宿主的 `DocumentCanvas` 提供;这里做产品界面:标题栏(保存、状态、
 * 缩放)与功能区(`ribbon.tsx`)。
 *
 * ## 这一版故意不做的
 *
 * - 修订 / 批注、目录、页眉页脚、分节:需要引擎侧的命令与查询,还没有;没有后端能力的按钮不画
 *   (AGENTS §5「不做防御式 UI」)。
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Button, EmptyState, Segmented, Spinner } from 'nextcowork/ui'
import {
  DocumentCanvas, mount, openEngineDocument,
  type EngineCommandId, type EngineDocument, type EngineDocumentError, type EngineDocumentOpened, type EngineDocumentState
} from 'nextcowork/view'
import { text } from './messages'
import { Ribbon } from './ribbon'

const ZOOMS = ['0.75', '1', '1.25', '1.5'] as const
const isMac = /mac/i.test(navigator.platform)

function Writer(): React.ReactNode {
  const [doc, setDoc] = useState<EngineDocument | null>(null)
  const [opened, setOpened] = useState<EngineDocumentOpened | null>(null)
  const [state, setState] = useState<EngineDocumentState | null>(null)
  const [states, setStates] = useState<Partial<Record<EngineCommandId, string>>>({})
  const [openError, setOpenError] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [zoom, setZoom] = useState<(typeof ZOOMS)[number]>('1')
  const canvas = useRef<{ focus: () => void } | null>(null)

  useEffect(() => {
    // ★ 每个视图只打开一次:每次调用宿主都会重开一次会话视图;cleanup 必须 dispose
    const next = openEngineDocument()
    setDoc(next)
    next.ready.then(
      (result) => { setOpened(result); setState(result.state) },
      (error: EngineDocumentError) => { setOpenError(`${text.openFailed} (${error.code})`) }
    )
    const stopChange = next.onChange(setState)
    // 功能区的按下状态:任何一次输入 / 命令的回执里变过的那几条,合并进来
    const stopResult = next.onResult((result) => {
      const changed = result.states
      if (changed !== undefined) setStates((current) => ({ ...current, ...changed }))
    })
    return () => {
      stopChange()
      stopResult()
      next.dispose()
    }
  }, [])

  const onError = useCallback((error: EngineDocumentError) => {
    setProblem(error.code === 'engine_unavailable' || error.code === 'result_unknown' || error.code === 'session_closed' ? text.crashed : `${text.failed} (${error.code})`)
  }, [])

  const canSave = opened?.capabilities.canSave === true
  const save = useCallback(() => {
    if (doc === null || !canSave || saving) return
    setSaving(true)
    doc.save().then(
      (next) => { setState(next); setProblem(null) },
      onError
    ).finally(() => { setSaving(false) })
  }, [doc, canSave, saving, onError])

  const generation = state?.generation ?? 0
  const run = useCallback((command: EngineCommandId, args?: Record<string, string | number>) => {
    if (doc === null) return
    doc.command(generation, command, args).then(() => { setProblem(null) }, onError).finally(() => {
      // 点完按钮把焦点还给画布,接着打字
      canvas.current?.focus()
    })
  }, [doc, generation, onError])

  const onShortcut = useCallback((event: KeyboardEvent) => {
    // 保存由插件经宿主存回工作区;送进引擎的话它会存进自己的私有副本(见 DocumentCanvas 的说明)
    const primary = isMac ? event.metaKey : event.ctrlKey
    if (primary && !event.altKey && event.code === 'KeyS') {
      save()
      return true
    }
    return false
  }, [save])

  const available = useMemo(() => new Set<string>(opened?.capabilities.commands ?? []), [opened])
  const listFonts = useCallback(() => doc?.list('fonts') ?? Promise.resolve([]), [doc])
  const listStyles = useCallback(() => doc?.list('styles') ?? Promise.resolve([]), [doc])

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
        <span className="text-[12px] text-fg-muted">{text.zoom}</span>
        <Segmented
          value={zoom}
          options={ZOOMS.map((value) => ({ value, label: `${Math.round(Number(value) * 100)}%` }))}
          onChange={(value) => { setZoom(value) }}
        />
      </div>
      {editable && available.size > 0 && (
        <Ribbon available={available} states={states} run={run} listFonts={listFonts} listStyles={listStyles} />
      )}
      <DocumentCanvas
        doc={doc}
        opened={opened}
        zoom={Number(zoom)}
        label={text.canvas}
        onError={onError}
        onShortcut={onShortcut}
        controller={canvas}
      />
    </div>
  )
}

mount(<Writer />)
