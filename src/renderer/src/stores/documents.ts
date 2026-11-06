import { create } from 'zustand'
import type { WorkspaceFile, WorkspaceFileMutationRequest } from '../../../shared/domain/workspace-file'
import type { TranslationKey } from '../i18n'
import { announceWorkspaceFileChanged, readWorkspaceFile, workspaceFileErrorKey, writeWorkspaceFile } from '../services/workspace-files'
import { pendingDocumentSave, pendingDocumentSaveAtPath, trackDocumentSave } from './document-saves'

export interface DocumentDraft {
  workspaceId: string
  path: string
  /**
   * 这份**文档身份**的稳定 id。
   *
   * ★ 它就是保存回执的认领依据。`path` 不是身份:同一个路径上的文件可能已经
   * 被删掉又重新建出来,而改名/移动又会让同一份文档换一个 key。按 path 认领的
   * 话,「保存途中文件被改名」会让回执落到不存在的 key 上(状态永远卡在 saving),
   * 「删除后重开同 path」会让旧回执清掉新文档的 saving、灌进一个过期的 revision ——
   * 而这两件事都不报错。
   */
  requestId?: string
  file?: WorkspaceFile
  draft: string
  base: string
  bom: string
  lineEnding: string
  lineEndings: string[]
  loading: boolean
  saving: boolean
  error?: TranslationKey
  mode: 'preview' | 'source'
}

interface Confirmation {
  keys: string[]
  resolve: (proceed: boolean) => void
}

interface DocumentsState {
  entries: Record<string, DocumentDraft>
  confirmation: Confirmation | null
  load: (workspaceId: string, path: string, force?: boolean) => Promise<void>
  edit: (workspaceId: string, path: string, content: string) => void
  setMode: (workspaceId: string, path: string, mode: DocumentDraft['mode']) => void
  save: (workspaceId: string, path: string) => Promise<boolean>
  /** 保存这块草稿的**最后一次在途请求** —— 破坏性操作闸门等它落定(按路径,含已删条目)。 */
  activeSave: (workspaceId: string, path: string) => Promise<boolean> | undefined
  discard: (keys: readonly string[]) => void
  release: (workspaceId: string, path?: string) => void
  applyMutation: (req: WorkspaceFileMutationRequest) => void
}

/**
 * 一次「保存 / 读盘」请求的身份。**在 entry 创建时铸、跟着 entry 一起搬家。**
 *
 * 不能拿 path 当身份:同 path 关闭重开、或改名/移动让 path 变了,旧请求的回执
 * 会落到一个不该属于它的 entry 上(见 `requestId` 的注释)。
 */
function newRequestId(): string {
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`
}

export const documentKey = (workspaceId: string, path: string): string => JSON.stringify([workspaceId, path])
/** 可渲染预览的文本格式；决定预览/源码开关的显示与默认模式。 */
export const previewFormat = (path: string): 'markdown' | 'html' | null =>
  /\.(?:md|markdown|mdown|mdx)$/i.test(path) ? 'markdown' : /\.html?$/i.test(path) ? 'html' : null
export const isDocumentDirty = (entry: DocumentDraft): boolean => entry.draft !== entry.base
export const isWithinPath = (path: string, parent?: string): boolean => parent === undefined || path === parent || path.startsWith(`${parent}/`)

export function editableText(content: string): { text: string; bom: string; lineEnding: string; lineEndings: string[] } {
  const bom = content.startsWith('\uFEFF') ? '\uFEFF' : ''
  const lineEndings = content.match(/\r\n|\r|\n/g) ?? []
  return { text: content.slice(bom.length).replace(/\r\n?/g, '\n'), bom, lineEnding: lineEndings[0] ?? '\n', lineEndings }
}

export function serializeDraft(entry: DocumentDraft): string {
  if (entry.file?.kind === 'text' && !isDocumentDirty(entry)) return entry.file.content
  let line = 0
  return entry.bom + entry.draft.replace(/\n/g, () => entry.lineEndings[line++] ?? entry.lineEnding)
}

/** Preserve delimiters outside the changed span, including mixed CRLF/LF files. */
function editedLineEndings(entry: DocumentDraft, next: string): string[] {
  let start = 0
  while (start < entry.draft.length && start < next.length && entry.draft[start] === next[start]) start++
  let oldEnd = entry.draft.length
  let newEnd = next.length
  while (oldEnd > start && newEnd > start && entry.draft[oldEnd - 1] === next[newEnd - 1]) { oldEnd--; newEnd-- }
  const count = (value: string): number => value.match(/\n/g)?.length ?? 0
  const before = count(entry.draft.slice(0, start))
  const removed = count(entry.draft.slice(start, oldEnd))
  const inserted = count(next.slice(start, newEnd))
  return [
    ...entry.lineEndings.slice(0, before),
    ...Array.from({ length: inserted }, (_, i) => i < removed ? entry.lineEndings[before + i] ?? entry.lineEnding : entry.lineEnding),
    ...entry.lineEndings.slice(before + removed)
  ]
}

/**
 * 通知别处（比如聊天里的计划卡片）重读这个刚保存的文件。
 *
 * ★ 走传输层那一个派发点,不再自己 dispatch:两个模块各自 `new CustomEvent`
 * 迟早会分叉,而分叉的表现是「只有某一条路径上的监听方收不到通知」。
 * 传输层记录通知异常但不影响保存结果 —— 派发失败和写盘失败是两件事，
 * 已经写进盘里的东西不能被一个监听方抛的错报成失败。
 */
function notifyWorkspaceFileChanged(workspaceId: string, path: string): void {
  announceWorkspaceFileChanged({ workspaceId, path, operation: 'save' })
}

/**
 * 这个路径上**还在飞**的那次保存 —— 破坏性操作闸门用它。
 *
 * ★ 判据是 `(工作区, 路径)` 的索引,**不是遍历 entries**:条目可能在闸门被问到
 * 之前就已经被 `applyMutation` 删掉了(删除 / 改名的调用点正是这里),而那次
 * 写盘还在飞。靠 entries 找的话,「删除时等保存落定」会在条目删掉后变成空等 ——
 * 而这时恰恰最需要等。
 */
function findSaveAt(state: DocumentsState, workspaceId: string, path: string): Promise<boolean> | undefined {
  // 先看按路径登记的索引(即使条目已删)
  const byPath = pendingDocumentSaveAtPath(workspaceId, path)
  if (byPath !== undefined) return byPath
  // 再兜一层:改名搬过家的条目,旧路径的请求仍在飞、记录还在旧路径上 —— 条目
  // 自己带着新的 file.path,拿它反查同一次请求。
  for (const entry of Object.values(state.entries)) {
    if (entry.workspaceId !== workspaceId || !entry.saving) continue
    if (entry.path === path || entry.file?.path === path) {
      const tracked = pendingDocumentSave(entry.requestId ?? '')
      if (tracked !== undefined) return tracked
    }
  }
  return undefined
}

export const useDocumentsStore = create<DocumentsState>((set, get) => {
  const patch = (key: string, value: Partial<DocumentDraft>): void => {
    const entry = get().entries[key]
    if (entry) set({ entries: { ...get().entries, [key]: { ...entry, ...value } } })
  }

  /**
   * 回执落到**当前**这个 key 上的那份文档 —— 认领靠身份,不是路径。
   *
   * 拿 `requestId` 比对:改名/移动把它带着搬家,关闭重开则铸新的。于是
   * 「保存途中被改名」的回执能找到搬过去的 entry,而「删除后重开同 path」
   * 的旧回执发现身份对不上,什么都不动。
   */
  const settle = (key: string, requestId: string, value: Partial<DocumentDraft>): void => {
    const entry = get().entries[key]
    if (entry === undefined || entry.requestId !== requestId) return
    set({ entries: { ...get().entries, [key]: { ...entry, ...value } } })
  }

  /** 同 `settle`,但键由一个箭头函数**当场**从最新状态里取 —— 兼容改名搬家的那一刻。 */
  const settleByIdentity = (requestId: string, value: Partial<DocumentDraft> | ((entry: DocumentDraft) => Partial<DocumentDraft>)): void => {
    for (const [key, entry] of Object.entries(get().entries)) {
      if (entry.requestId !== requestId) continue
      set({ entries: { ...get().entries, [key]: { ...entry, ...(typeof value === 'function' ? value(entry) : value) } } })
      return
    }
  }

  return {
    entries: {},
    confirmation: null,
    async load(workspaceId, path, force = false) {
      const key = documentKey(workspaceId, path)
      const previous = get().entries[key]
      if (previous && (!force || previous.loading || previous.saving || isDocumentDirty(previous))) return
      // 同一路径上重来一次读盘 = 一次**全新**的请求:铸新身份,旧请求的回执
      // (如果有)落到这里会因为对不上而被丢弃。
      const requestId = newRequestId()
      const pending: DocumentDraft = { workspaceId, path, requestId, draft: '', base: '', bom: '', lineEnding: '\n', lineEndings: [], loading: true, saving: false, mode: previous?.mode ?? (previewFormat(path) ? 'preview' : 'source') }
      set({ entries: { ...get().entries, [key]: pending } })
      try {
        const file = await readWorkspaceFile(workspaceId, path)
        const parsed = editableText(file.kind === 'text' ? file.content : '')
        settle(key, requestId, { file, draft: parsed.text, base: parsed.text, bom: parsed.bom, lineEnding: parsed.lineEnding, lineEndings: parsed.lineEndings, loading: false })
      } catch (error) {
        settle(key, requestId, { loading: false, error: workspaceFileErrorKey(error) })
      }
    },
    edit(workspaceId, path, content) {
      const key = documentKey(workspaceId, path)
      const entry = get().entries[key]
      if (entry) patch(key, { draft: content, lineEndings: editedLineEndings(entry, content) })
    },
    setMode(workspaceId, path, mode) {
      patch(documentKey(workspaceId, path), { mode })
    },
    save(workspaceId, path) {
      const key = documentKey(workspaceId, path)
      const entry = get().entries[key]
      if (!entry) return Promise.resolve(false)
      const identity = entry.requestId ?? key
      const inFlight = pendingDocumentSave(identity)
      if (inFlight !== undefined) return inFlight
      if (entry.file?.kind !== 'text' || entry.loading) return Promise.resolve(false)
      if (!isDocumentDirty(entry)) return Promise.resolve(true)
      const revision = entry.file.revision
      const savedDraft = entry.draft
      patch(key, { saving: true, error: undefined })
      const promise = (async (): Promise<boolean> => {
        try {
          const file = await writeWorkspaceFile({ workspaceId, path, content: serializeDraft(entry), revision })
          /*
            ★ 回执按 `requestId` 认领,不按路径。
            - 文件名变了 → entry 搬到了新 key,这里跟过去;
            - 文件被删并重开同一路径 → 那份文档是新身份,这里一个字都不动
              (否则会把它的 saving 清掉、塞进一个过期的 revision);
            - 保存途中又敲了字 → `draft` 原样留着,只把它标成新的 base。
          */
          settleByIdentity(identity, (doc) => ({
            // 写下去的是请求发出那一刻的草案;文件随后被改名也只是换了路径,
            // 内容不变 —— 所以新 base 一律是 `savedDraft`,期间新敲的字继续 dirty。
            file: { ...file, path: doc.path },
            base: savedDraft,
            saving: false,
            error: undefined
          }))
          notifyWorkspaceFileChanged(workspaceId, path)
          return true
        } catch (error) {
          settleByIdentity(identity, { saving: false, error: workspaceFileErrorKey(error) })
          return false
        }
      })()
      trackDocumentSave(identity, { workspaceId, path }, promise)
      return promise
    },
    activeSave(workspaceId, path) {
      return findSaveAt(get(), workspaceId, path)
    },
    discard(keys) {
      const entries = { ...get().entries }
      for (const key of keys) {
        const entry = entries[key]
        if (entry && !entry.saving) entries[key] = { ...entry, draft: entry.base, lineEndings: editableText(entry.file?.kind === 'text' ? entry.file.content : '').lineEndings, error: undefined }
      }
      set({ entries })
    },
    release(workspaceId, path) {
      const entries = { ...get().entries }
      for (const [key, entry] of Object.entries(entries)) {
        if (entry.workspaceId === workspaceId && isWithinPath(entry.path, path) && !entry.saving) delete entries[key]
      }
      set({ entries })
    },
    applyMutation(req) {
      if (!['rename', 'move', 'delete'].includes(req.operation)) return
      const entries = { ...get().entries }
      for (const [key, entry] of Object.entries(entries)) {
        if (entry.workspaceId !== req.workspaceId || !isWithinPath(entry.path, req.path)) continue
        delete entries[key]
        if (req.operation !== 'delete' && req.destination && !entry.loading) {
          const path = req.destination + entry.path.slice(req.path.length)
          entries[documentKey(req.workspaceId, path)] = { ...entry, path, file: entry.file ? { ...entry.file, path } : undefined }
        }
      }
      set({ entries })
    }
  }
})

/**
 * Drafts survive tab/workspace switches. Only destructive navigation asks.
 *
 * ★ **它问的不再只有 `documents` store。** 插件接管的自定义编辑器把改动放在
 * 自己那边(宿主连那份文档长什么样都不知道),而它们同样会被「关 Tab」
 * 这个动作丢掉。所以这里泛化成「问所有脏文档源」:
 *
 * 1. 内置草稿 —— 老路,弹挽留对话框让用户选;
 * 2. 插件编辑器 —— 让插件**自己存**,存不下来才拦住关闭。
 *
 * 两者的处理方式不同是有理由的:内置草稿宿主能显示、能让用户选择丢弃,
 * 插件文档宿主显示不了 —— 给一个「丢弃吗」的对话框而不给预览,
 * 等于让用户在看不见内容的情况下决定要不要丢掉它。
 */
export async function confirmDocumentChanges(workspaceId?: string, path?: string): Promise<boolean> {
  const matches = (entry: DocumentDraft): boolean => (workspaceId === undefined || entry.workspaceId === workspaceId) && isWithinPath(entry.path, path)
  // ★ 等的是**身份**上挂着的那次保存,不是路径。保存途中文件被改名/移动之后,
  //   请求记的还是旧 path —— 按路径找会漏掉它,于是「等保存落定」变成了空等。
  await Promise.all(Object.values(useDocumentsStore.getState().entries)
    .filter((entry) => matches(entry) && entry.saving)
    .map((entry) => pendingDocumentSave(entry.requestId ?? '') ?? Promise.resolve(true)))
  const keys = Object.entries(useDocumentsStore.getState().entries).filter(([, entry]) => matches(entry) && isDocumentDirty(entry)).map(([key]) => key)
  if (keys.length > 0) {
    if (useDocumentsStore.getState().confirmation) return false
    const proceed = await new Promise<boolean>((resolve) => useDocumentsStore.setState({ confirmation: { keys, resolve } }))
    if (!proceed) return false
  }
  return confirmPluginDocuments(path)
}

/**
 * 插件编辑器那一半。
 *
 * ★ 失败一律当成「可以关」:插件系统没起来、IPC 断了、这一版还没接上 ——
 * 这些都不该让用户关不掉一个 Tab。**能丢数据的那个方向是主进程说了算的**
 * (它拿得到真实的脏状态),而这里拿不到答案时,拦住用户的代价更高。
 */
async function confirmPluginDocuments(path?: string): Promise<boolean> {
  try {
    const { invoke } = await import('../services/ipc')
    const result = await invoke('plugins:confirmClose', path === undefined ? {} : { path })
    return result.safe
  } catch {
    return true
  }
}
