/**
 * 输入框灰字的接线:四个来源 → 一条灰字 → 按键。设计见 `docs/composer-autocomplete.md`。
 *
 *   L2 建议下一步  —— `suggestTrigger` 变了(ChatView 看着一轮成功跑完)时请求一次
 *   L1 历史指令    —— 每次按键现算,零请求
 *   热文件         —— 每次按键现算,零请求;Tab 后变成文件 chip
 *   L3 打字续写    —— 停顿 300ms 后请求(默认关)
 *
 * ★ 状态放在 ref 里、由 `dispatch` 同步推进,而不是 `useReducer`:按键处理(Tab 接受)
 *   要在**同一个事件里**读到最新的灰字,等一次渲染再读就是上一帧的。
 * ★ 一切请求失败都当「没有建议」,不弹任何错误。
 */
import { useEffect, useMemo, useReducer, useRef, type KeyboardEvent, type RefObject } from 'react'
import type { FileSuggestion } from '../../../../shared/domain/file-tree'
import type { GitOverview } from '../../../../shared/domain/git'
import { insertMention } from '../../../../shared/domain/file-mention'
import type { Workspace } from '../../../../shared/domain/workspace'
import { ulid } from '../../../../shared/util/id'
import { cancelComposerRequest, completeInline, suggestNext } from '../../services/composer'
import { getGitOverview } from '../../services/git'
import { isRemoteDeviceSelected, useComposerPrefs } from '../../stores/composer-prefs'
import {
  fileGhost,
  ghostDisplay,
  historyGhost,
  hotFilesFrom,
  inlineBackoff,
  inlineDebounceMs,
  inlineEligible,
  nextWordChunk,
  noteAccepted,
  remainderOf,
  resolveGhostKey,
  showAcceptHint,
  suggestionBackoff,
  type Ghost
} from './ghost'
import { INITIAL_GHOST_STATE, reduceGhost, visibleGhost, type GhostAction, type GhostEvent } from './ghost-state'
import type { MentionInputHandle } from './MentionInput'
import { loadPromptHistory, promptHistory, rememberPrompt } from './prompt-history'

const IS_MAC = typeof navigator !== 'undefined' && navigator.platform.toLowerCase().includes('mac')
/** git 概况的缓存:每轮结束才可能有新改动,30 秒内的重挂载/切 Tab 不必再跑一次 git status */
const GIT_TTL_MS = 30_000
const gitCache = new Map<string, { at: number; overview: GitOverview | null }>()

export interface ComposerGhostArgs {
  input: RefObject<MentionInputHandle | null>
  workspace: Workspace
  sessionId: string | null
  draft: string
  popupOpen: boolean
  attachments: number
  running: boolean
  /** 输入框此刻的生效模型(建议下一步默认跟随它,不换家) */
  model: string
  modelProviderId?: string
  suggestTrigger?: number
  mentionedFiles?: readonly FileSuggestion[]
  /** 斜杠命令名与 Skill 名,只给续写当上下文 */
  commands: readonly string[]
}

export interface ComposerGhostView {
  /** 灰字上画的字(mention 已画成 `@名`) */
  display: string
  /** 草稿是空的 —— 灰字画在占位符层,而不是接在文字后面 */
  empty: boolean
  showHint: boolean
  /** 给读屏的那句:同一条灰字只播报一次 */
  announce: string
}

export interface ComposerGhost {
  view: ComposerGhostView | null
  onInput: (text: string, caret: number | null) => void
  onCaret: (text: string, caret: number | null) => void
  onComposing: (value: boolean) => void
  onFocus: () => void
  onBlur: () => void
  onPaste: () => void
  /** 返回 true = 这一下归灰字,调用方别再处理 */
  handleKey: (e: KeyboardEvent<HTMLElement>) => boolean
  onSubmit: (raw: string) => void
}

/** 会产生字符或删字的按键 —— 粘贴保持期到此为止 */
function typesSomething(e: KeyboardEvent<HTMLElement>): boolean {
  if (e.metaKey || e.ctrlKey) return false
  return e.key.length === 1 || e.key === 'Backspace' || e.key === 'Delete'
}

export function useComposerGhost(args: ComposerGhostArgs): ComposerGhost {
  const prefs = useComposerPrefs()
  const state = useRef(INITIAL_GHOST_STATE)
  const [tick, bump] = useReducer((n: number) => n + 1, 0)
  const latest = useRef(args)
  latest.current = args
  const pendingSuggest = useRef<string | null>(null)
  const pendingInline = useRef<string | null>(null)
  const inlineTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  /** 按过 Esc 的那份草稿:续写在它变之前不再请求 */
  const inlineDismissedAt = useRef<string | null>(null)
  const hint = useRef(showAcceptHint())
  const announced = useRef<{ key: string; text: string }>({ key: '', text: '' })

  function note(events: readonly GhostEvent[]): void {
    for (const e of events) {
      if (e.source === 'suggest') suggestionBackoff.note('ignored')
      if (e.source === 'inline') inlineBackoff.note('ignored')
    }
  }

  function dispatch(action: GhostAction): void {
    const { state: next, events } = reduceGhost(state.current, action)
    note(events)
    if (next !== state.current) {
      state.current = next
      bump()
    }
  }

  function cancelPending(ref: { current: string | null }): void {
    const id = ref.current
    if (id === null) return
    ref.current = null
    void cancelComposerRequest(id).catch(() => undefined)
  }

  // ── L2:回合结束后建议下一步 ──
  // ★ 挂载那一刻的 trigger 不算:Composer 会在空态 → 内容态切换时重挂,而 ChatView 那份 trigger 还在
  const seenTrigger = useRef(args.suggestTrigger)
  useEffect(() => {
    const trigger = args.suggestTrigger
    if (trigger === undefined || trigger === seenTrigger.current) return
    seenTrigger.current = trigger
    const a = latest.current
    if (!prefs.promptSuggestions || isRemoteDeviceSelected() || a.sessionId === null || a.draft !== ''
      || a.attachments > 0 || a.model === '' || a.running) return
    if (!suggestionBackoff.shouldRequest()) return
    cancelPending(pendingSuggest)
    const requestId = ulid()
    pendingSuggest.current = requestId
    void suggestNext({
      requestId,
      workspaceId: a.workspace.id,
      sessionId: a.sessionId,
      triggerKey: trigger,
      model: a.model,
      ...(a.modelProviderId === undefined ? {} : { modelProviderId: a.modelProviderId })
    })
      .catch(() => ({ requestId, text: '' }))
      .then((r) => {
        if (pendingSuggest.current !== requestId) return
        pendingSuggest.current = null
        dispatch({ type: 'suggestion', trigger, text: r.text, draft: latest.current.draft })
      })
  }, [args.suggestTrigger, prefs.promptSuggestions])

  // 开跑了(用户发了、队列接上了):在途的请求和挂着的灰字都作废
  useEffect(() => {
    if (!args.running) return
    cancelPending(pendingSuggest)
    cancelPending(pendingInline)
    dispatch({ type: 'reset', reason: 'running' })
  }, [args.running])

  useEffect(() => () => {
    clearTimeout(inlineTimer.current)
    cancelPending(pendingSuggest)
    cancelPending(pendingInline)
  }, [])

  // ── L1 / 热文件:清单在挂载和每轮结束时刷新 ──
  const workspaceId = args.workspace.id
  useEffect(() => {
    if (!prefs.localCompletion) return
    let cancelled = false
    void loadPromptHistory(workspaceId).then(() => { if (!cancelled) bump() })
    return () => { cancelled = true }
  }, [workspaceId, prefs.localCompletion, args.suggestTrigger])

  const cachedGit = gitCache.get(workspaceId)
  const overview = cachedGit?.overview ?? null
  const seenGitTrigger = useRef<number | undefined>(undefined)
  useEffect(() => {
    if (!prefs.localCompletion || isRemoteDeviceSelected()) return
    const hit = gitCache.get(workspaceId)
    if (hit !== undefined && Date.now() - hit.at < GIT_TTL_MS && args.suggestTrigger === seenGitTrigger.current) return
    seenGitTrigger.current = args.suggestTrigger
    let cancelled = false
    void getGitOverview(workspaceId)
      .catch(() => null)
      .then((o) => {
        gitCache.set(workspaceId, { at: Date.now(), overview: o })
        if (!cancelled) bump()
      })
    return () => { cancelled = true }
  }, [workspaceId, prefs.localCompletion, args.suggestTrigger])

  const hotFiles = useMemo(
    () => hotFilesFrom(overview, args.workspace.rootPath, args.mentionedFiles ?? []),
    [overview, args.workspace.rootPath, args.mentionedFiles]
  )

  const s = state.current
  const history = prefs.localCompletion ? historyGhost(args.draft, promptHistory(workspaceId)) : null
  const file = prefs.localCompletion && history === null ? fileGhost(args.draft, hotFiles) : null
  const visible = visibleGhost(s, {
    draft: args.draft,
    popupOpen: args.popupOpen,
    attachments: args.attachments,
    local: { history, file }
  })

  // ── L3:停顿后续写 ──
  useEffect(() => {
    clearTimeout(inlineTimer.current)
    const cur = state.current
    const draft = args.draft
    if (inlineDismissedAt.current !== null && inlineDismissedAt.current !== draft) inlineDismissedAt.current = null
    // 跟打中:手上这条还对得上,不必再问
    if (cur.inline !== null && remainderOf(cur.inline, draft) !== null) return
    const localGhost = history !== null || file !== null
      || (cur.suggest !== null && remainderOf(cur.suggest, draft) !== null)
    const eligible = inlineEligible(draft, {
      enabled: prefs.inlineCompletion,
      composing: cur.composing,
      caretAtEnd: cur.caretAtEnd && cur.focused,
      popupOpen: args.popupOpen,
      hasLocalGhost: localGhost,
      dismissed: inlineDismissedAt.current === draft,
      pasteHold: cur.pasteHold,
      remote: isRemoteDeviceSelected()
    })
    if (!eligible) return
    inlineTimer.current = setTimeout(() => {
      const a = latest.current
      if (a.draft !== draft) return
      cancelPending(pendingInline)
      const requestId = ulid()
      pendingInline.current = requestId
      void completeInline({
        requestId,
        workspaceId: a.workspace.id,
        sessionId: a.sessionId,
        draft,
        context: {
          project: a.workspace.name,
          ...(overview !== null && overview.available && overview.branch !== '' ? { branch: overview.branch } : {}),
          files: hotFiles.slice(0, 20).map((f) => f.path),
          commands: a.commands.slice(0, 30)
        }
      })
        .catch(() => ({ requestId, text: '' }))
        .then((r) => {
          if (pendingInline.current !== requestId) return
          pendingInline.current = null
          dispatch({ type: 'inline', requestId, base: draft, text: r.text, draft: latest.current.draft })
        })
    }, inlineDebounceMs(inlineBackoff.streak()))
    return () => clearTimeout(inlineTimer.current)
    // `tick`:组词 / 焦点 / 光标这几样住在 ref 里,它们一变就得重新判一次
  }, [args.draft, tick, prefs.inlineCompletion, args.popupOpen])

  function accept(ghost: Ghost, remainder: string, wordOnly: boolean): void {
    const handle = latest.current.input.current
    if (handle === null) return
    const draft = latest.current.draft
    if (ghost.file !== undefined) {
      // 文件灰字整枚接受(chip 是原子的),走和 `@` 弹层选文件同一条路
      dispatch({ type: 'accept', ghost, partial: false })
      const r = insertMention(draft, { start: ghost.file.tokenStart, end: draft.length }, ghost.file)
      handle.replace(r.text, r.caret)
    } else {
      const chunk = wordOnly ? nextWordChunk(ghost.base + ghost.text, draft.length) : remainder
      const partial = chunk.length < remainder.length
      dispatch({ type: 'accept', ghost, partial })
      handle.acceptGhost(chunk)
    }
    noteAccepted()
    hint.current = showAcceptHint()
    if (ghost.source === 'suggest') suggestionBackoff.note('accepted')
    if (ghost.source === 'inline') inlineBackoff.note('accepted')
  }

  function handleKey(e: KeyboardEvent<HTMLElement>): boolean {
    if (typesSomething(e)) dispatch({ type: 'keydown' })
    if (visible === null) return false
    const action = resolveGhostKey({
      key: e.key,
      shiftKey: e.shiftKey,
      altKey: e.altKey,
      ctrlKey: e.ctrlKey,
      metaKey: e.metaKey,
      isComposing: e.nativeEvent.isComposing,
      keyCode: e.nativeEvent.keyCode
    }, { visible: true, mac: IS_MAC })
    if (action === null) return false
    e.preventDefault()
    if (action === 'dismiss') {
      if (visible.ghost.source === 'inline') inlineDismissedAt.current = latest.current.draft
      dispatch({ type: 'dismiss', ghost: visible.ghost })
      return true
    }
    accept(visible.ghost, visible.remainder, action === 'acceptWord')
    return true
  }

  let view: ComposerGhostView | null = null
  if (visible !== null) {
    if (announced.current.key !== visible.ghost.key) {
      announced.current = { key: visible.ghost.key, text: ghostDisplay(visible.remainder) }
    }
    view = {
      display: ghostDisplay(visible.remainder),
      empty: args.draft === '',
      showHint: hint.current,
      announce: announced.current.text
    }
  }

  return {
    view,
    onInput: (text, caret) => dispatch({ type: 'input', text, caret }),
    onCaret: (text, caret) => dispatch({ type: 'caret', text, caret }),
    onComposing: (value) => dispatch({ type: 'composing', value }),
    onFocus: () => dispatch({ type: 'focus', value: true }),
    onBlur: () => dispatch({ type: 'focus', value: false }),
    onPaste: () => dispatch({ type: 'paste' }),
    handleKey,
    onSubmit: (raw) => {
      rememberPrompt(latest.current.workspace.id, raw)
      clearTimeout(inlineTimer.current)
      cancelPending(pendingInline)
      dispatch({ type: 'reset', reason: 'submit' })
    }
  }
}
