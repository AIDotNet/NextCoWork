/**
 * 输入框本体 —— 一个把草稿画成「文字 + tag」的 contentEditable。
 *
 * ★ **React 不接管这棵子树。** 受控组件那套(每次 `value` 变就重渲染孩子)会在
 * 每一个按键上把光标打回原点;而 `key` 再稳,文本节点的内容一变,React 就得改
 * 那个节点,光标就没了。所以孩子全部命令式地画(见 `rich-draft.ts`),
 * React 只负责这个容器和它的事件。
 *
 * ★ **重画是有代价的动作**,只在两种时候做:
 *   1. `value` 从外面变了(切会话、发送后清空)——DOM 里的不是它了;
 *   2. DOM 脏了(`[a](b)` 刚打完最后一个括号,该长出 tag 了)。
 *   光敲普通字符两条都不满足,于是一次都不重画,光标行为完全是原生的。
 *
 * ★ **组词期间绝不重画。** 输入法的候选串挂在浏览器自己维护的那个文本节点上,
 * 底下一换,组词当场断掉 —— 打一半的中文会碎成拼音。
 */
import { useEffect, useLayoutEffect, useRef, type ReactNode, type RefObject } from 'react'
import { parseMentions } from '../../../../shared/domain/file-mention'
import { cn } from '../../lib/cn'
import { caretOf, domDirty, placeCaret, readDraft, renderDraft, selectionOf, type DraftSelection } from './rich-draft'

export interface MentionInputHandle {
  focus: () => void
  /** 整段换掉草稿并把光标放到 `caret`。插入 `@` 引用走这条。 */
  replace: (text: string, caret: number) => void
  /** 菜单移走焦点后，仍能取回用户最后的输入选区。 */
  selection: () => DraftSelection | null
  /**
   * 在草稿**末尾**接上一段灰字(Tab 接受)。
   *
   * ★ 纯文本走 `execCommand('insertText')`:它是浏览器自己的一次编辑,进原生撤销栈,
   *   ⌘Z 一下就回到接受之前。走 `replace` 的话整棵重画,撤销栈从此对不上 —— 那是
   *   从 `@` 弹层选文件时的现状,对 chip 无可避免,对一段纯文字没有理由这样。
   */
  acceptGhost: (text: string) => void
}

export function MentionInput({
  value,
  onChange,
  onCaret,
  onKeyDown,
  onPaste,
  onBlur,
  onComposing,
  onFocus,
  placeholder,
  ghost,
  metrics,
  handle,
  skillDescriptions,
  ...aria
}: {
  value: string
  /** 文本变了。`caret` 在组词途中为 null(那一刻的位置没有意义) */
  onChange: (text: string, caret: number | null) => void
  /**
   * 只是光标动了(点击、方向键)。
   *
   * ★ 文本一并带出去,不让调用方去读它自己那份 `value` —— `selectionchange`
   * 可能抢在 React commit 之前发,那一刻调用方手里的还是上一帧的草稿,
   * 拿它配这一帧的光标算 `@查询`,算出来的是一段谁也没打过的字。
   */
  onCaret: (text: string, caret: number | null) => void
  onKeyDown: (e: React.KeyboardEvent<HTMLDivElement>) => void
  onPaste: (e: React.ClipboardEvent<HTMLDivElement>) => void
  onBlur: () => void
  onComposing: (composing: boolean) => void
  onFocus?: () => void
  /** 空草稿时画在这里;Composer 用它显示「建议下一步」 */
  placeholder: ReactNode
  /**
   * 非空草稿时接在文字后面的灰字(已经是显示用的文字)。
   * ★ 画成根节点上的 `data-ghost` + CSS `::after`(见 theme.css):伪元素不是 DOM 节点,
   *   `readDraft` / `domDirty` / 光标 / 撤销栈全都看不见它,组词也不会被它打断。
   */
  ghost?: string
  /** 内边距 / 字号 / 行高。与占位符共用,两者必须逐像素对齐 */
  metrics: string
  handle: RefObject<MentionInputHandle | null>
  skillDescriptions?: Readonly<Record<string, string>>
  'aria-expanded': boolean
  'aria-controls': string | undefined
  'aria-activedescendant': string | undefined
}): ReactNode {
  const root = useRef<HTMLDivElement>(null)
  /** DOM 里此刻画的是哪段文本。★ 判断「要不要重画」的依据,不能用 `value`(它慢一拍) */
  const shown = useRef<string | null>(null)
  const composing = useRef(false)
  const lastSelection = useRef<DraftSelection | null>(null)
  /** 事件回调放进 ref:否则 `selectionchange` 的订阅每渲染一次就要重挂一次 */
  const onCaretRef = useRef(onCaret)
  onCaretRef.current = onCaret

  function apply(text: string, caret: number): void {
    const el = root.current
    if (el === null) return
    renderDraft(el, text)
    shown.current = text
    el.focus()
    placeCaret(el, caret)
    lastSelection.current = { start: caret, end: caret }
    onChange(text, caret)
  }

  function acceptGhost(text: string): void {
    const el = root.current
    if (el === null || text === '') return
    const cur = shown.current ?? value
    const chips = (t: string): number => parseMentions(t).filter((seg) => seg.kind !== 'text').length
    // 会长出新 chip、带换行、或草稿以换行结尾(末尾那个 `<br>`)时,原生插入之后必然要重画,
    // 撤销栈反正保不住 —— 直接走 apply,至少 DOM 和光标是一次摆对的
    const native = typeof document.execCommand === 'function'
      && !text.includes('\n') && !cur.endsWith('\n') && chips(cur + text) === chips(cur)
    if (native) {
      el.focus()
      // ★ 空草稿里只有一个撑行高的 `<br>`:不先换成空文本节点的话,插入后 `domDirty`
      //   判它是多余的 `<br>` → 重画 → 撤销栈丢失,而空草稿正是「建议下一步」唯一出现的地方
      if (cur === '') el.replaceChildren(document.createTextNode(''))
      placeCaret(el, cur.length)
      try {
        if (document.execCommand('insertText', false, text)) return
      } catch {
        // 落到下面的 apply
      }
    }
    apply(cur + text, cur.length + text.length)
    el.scrollTop = el.scrollHeight
  }

  handle.current = {
    focus: () => root.current?.focus(),
    replace: apply,
    selection: () => (root.current === null ? null : selectionOf(root.current)) ?? lastSelection.current,
    acceptGhost
  }

  // ★ `writingsuggestions` 不在 @types/react 里;它关掉系统 / Chromium 自带的输入预测,
  //   否则两层灰字会叠在一起(Chrome 124+、Safari 18+)
  useEffect(() => {
    root.current?.setAttribute('writingsuggestions', 'false')
  }, [])

  /*
    外面把 `value` 换了(发送后清空、切会话、切换到另一个草稿)。
    用 `useLayoutEffect`:光标要在浏览器绘制之前就位,否则会在旧位置上闪一帧。
  */
  useLayoutEffect(() => {
    const el = root.current
    if (el === null || composing.current) return
    if (shown.current === value && !domDirty(el, value)) return
    const focused = document.activeElement === el
    const caret = focused ? caretOf(el) : null
    renderDraft(el, value)
    shown.current = value
    if (focused) placeCaret(el, Math.min(caret ?? value.length, value.length))
  }, [value])

  useEffect(() => {
    for (const chip of root.current?.querySelectorAll<HTMLElement>('[data-skill]') ?? []) {
      chip.title = skillDescriptions?.[chip.dataset.name ?? ''] ?? chip.dataset.name ?? ''
    }
  }, [value, skillDescriptions])

  /*
    ★ 光标移动只能靠 document 上的 `selectionchange` —— contentEditable 不发
    `onSelect`。少了它,用户用鼠标点回一个写了一半的 `@comp` 中间时,列表不会回来。
  */
  useEffect(() => {
    function onSelectionChange(): void {
      const el = root.current
      if (el === null || composing.current) return
      if (document.activeElement !== el) return
      lastSelection.current = selectionOf(el)
      onCaretRef.current(readDraft(el), caretOf(el))
    }
    document.addEventListener('selectionchange', onSelectionChange)
    return () => {
      document.removeEventListener('selectionchange', onSelectionChange)
    }
  }, [])

  function sync(): void {
    const el = root.current
    if (el === null) return
    const text = readDraft(el)
    if (!composing.current) lastSelection.current = selectionOf(el)
    shown.current = text
    if (!composing.current && domDirty(el, text)) {
      const caret = caretOf(el)
      renderDraft(el, text)
      if (caret !== null) placeCaret(el, Math.min(caret, text.length))
    }
    onChange(text, composing.current ? null : caretOf(el))
  }

  /** 在光标处插入一段纯文本(换行、粘贴、拖入都走它) */
  function insert(t: string): void {
    const el = root.current
    if (el === null) return
    const cur = shown.current ?? value
    const sel = selectionOf(el) ?? { start: cur.length, end: cur.length }
    apply(cur.slice(0, sel.start) + t + cur.slice(sel.end), sel.start + t.length)
  }

  return (
    <div className="relative">
      {value === '' && (
        <div
          aria-hidden
          className={cn(metrics, 'pointer-events-none absolute inset-0 truncate text-fg-faint')}
        >
          {placeholder}
        </div>
      )}
      <div
        ref={root}
        data-testid="composer-input"
        data-composer-input=""
        data-ghost={value !== '' && ghost !== undefined && ghost !== '' ? ghost : undefined}
        contentEditable
        suppressContentEditableWarning
        role="combobox"
        aria-multiline="true"
        aria-expanded={aria['aria-expanded']}
        aria-controls={aria['aria-controls']}
        aria-activedescendant={aria['aria-activedescendant']}
        spellCheck={false}
        className={cn(
          metrics,
          'scroll-thin selectable max-h-[280px] w-full cursor-text overflow-y-auto break-words whitespace-pre-wrap text-fg focus:outline-none'
        )}
        onInput={sync}
        onFocus={() => onFocus?.()}
        onBlur={() => {
          const el = root.current
          if (el !== null) lastSelection.current = selectionOf(el) ?? lastSelection.current
          onBlur()
        }}
        onCopy={(e) => {
          const el = root.current
          const selected = el === null ? null : selectionOf(el)
          if (selected === null || selected.start === selected.end) return
          e.preventDefault()
          e.clipboardData.setData('text/plain', readDraft(el!).slice(selected.start, selected.end))
        }}
        onCut={(e) => {
          const el = root.current
          const selected = el === null ? null : selectionOf(el)
          if (el === null || selected === null || selected.start === selected.end) return
          const text = readDraft(el)
          e.preventDefault()
          e.clipboardData.setData('text/plain', text.slice(selected.start, selected.end))
          apply(text.slice(0, selected.start) + text.slice(selected.end), selected.start)
        }}
        onCompositionStart={() => {
          composing.current = true
          onComposing(true)
        }}
        onCompositionEnd={() => {
          composing.current = false
          onComposing(false)
          // ★ Chromium 的最后一个 `input` 在 `compositionend` **之前**发,
          //   那一发被上面的 composing 挡掉了,所以这里必须再同步一次。
          sync()
        }}
        onPaste={(e) => {
          onPaste(e)
          const t = e.clipboardData.getData('text/plain')
          // ★ 一律拦下原生粘贴:contentEditable 的默认行为会把整段 HTML 塞进来,
          //   连带样式、链接、图片 —— 那些最后都会变成模型看见的内容。
          e.preventDefault()
          if (t !== '') insert(t)
        }}
        onDrop={(e) => {
          if (e.dataTransfer.files.length > 0) return // 附件那条路,交给外层容器
          e.preventDefault()
          const t = e.dataTransfer.getData('text/plain')
          if (t === '') return
          // 落点优先于原光标:拖过来的东西该掉在鼠标指的地方
          const r = document.caretRangeFromPoint(e.clientX, e.clientY)
          if (r !== null) {
            const sel = window.getSelection()
            sel?.removeAllRanges()
            sel?.addRange(r)
          }
          insert(t)
        }}
        onKeyDown={(e) => {
          onKeyDown(e)
          if (e.defaultPrevented || e.nativeEvent.isComposing) return
          if (e.key === 'Enter') {
            /*
              走到这儿只可能是 Shift+Enter(普通 Enter 被外面拿去发送了)。
              ★ 交给浏览器会插出 `<div>`/`<br>` 的层级结构,和「草稿是一段纯文本」
              的前提冲突 —— 而且插出来的层级下一次重画就没了,撤销栈跟着错位。
            */
            e.preventDefault()
            insert('\n')
          }
        }}
      />
    </div>
  )
}
