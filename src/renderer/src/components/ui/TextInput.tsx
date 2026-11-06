import { AnimatePresence, animate, motion, useReducedMotion } from 'motion/react'
import { useEffect, useId, useRef } from 'react'
import { cn } from '../../lib/cn'

/**
 * 纯受控文本框。**故意不含草稿逻辑** —— 两类调用点要的东西正好相反:
 *
 *   设置浮层的搜索框:逐键过滤,受控就是它想要的;
 *   代理地址 / 端口:  逐键写入等于每敲一个字符一次 IPC + 一次全窗口广播,
 *                      而且中间态(空串、只打了 `http://`)不该被存进设置。
 *
 * 所以草稿态在**外面**做(`settings/Row.tsx` 的 `TextFieldRow`、`NumberInput`),
 * 这一层只负责长相。把草稿塞进这里的话,搜索框就得为了绕开它而不用它。
 *
 * `bg-surface-field` 而不是 `bg-tint-hover`:深色下两者同值,浅色下不是 ——
 * 理由见 theme.css 里那个 token 的注释。
 * `.selectable` 是必须的:全局 `user-select: none`,输入框要自己 opt-in。
 *
 * 外观与动效借自 beUI 的 Input(https://beui.dev/components/motion/input,MIT):
 * 聚焦时一圈柔和的 ring、出错时整框左右抖一下并描红、可选的成功对勾(描线出现)、
 * 错误文案带模糊浮现。★ 只借外观,**没有换成它的实现** —— 它的 `onChange` 签名、
 * 回车/Esc/输入法组词处理都和这里的调用点不兼容(见上面 Enter 那段与
 * `text-input-ime.test.ts`),所以行为层原样保留。
 * ★ 没有传 `error` 文案时返回的就是单个输入框(和以前同一棵 DOM),
 * 调用点传进来的 `className`(宽度/外边距)因此不受影响。
 */
export function TextInput({
  value,
  onChange,
  onCommit,
  onRevert,
  onFocus,
  placeholder,
  icon,
  invalid = false,
  error,
  success = false,
  disabled = false,
  ariaLabel,
  size = 'md',
  inputMode,
  type = 'text',
  className,
  inputRef
}: {
  value: string
  onChange: (v: string) => void
  /** Enter 或失焦。搜索框不传 —— 它没有「提交」这个概念 */
  onCommit?: () => void
  /** Escape。不传时 Escape 交给上层(设置浮层用它关闭) */
  onRevert?: () => void
  /** 走 `useDraft` 的调用点要用它标记「用户正在打字,别回灌」 */
  onFocus?: () => void
  placeholder?: string
  /** 左侧那颗图标(搜索框的放大镜) */
  icon?: React.ReactNode
  /** 端口越界这类:描红环,但**不阻止继续输入** */
  invalid?: boolean
  /** 带一句错误文案:等价于 `invalid`,并在输入框下方浮现这句话。 */
  error?: string
  /** 校验通过:右侧画一个描线对勾。 */
  success?: boolean
  disabled?: boolean
  ariaLabel: string
  size?: 'sm' | 'md'
  inputMode?: 'text' | 'numeric' | 'url'
  type?: 'text' | 'password'
  className?: string
  inputRef?: React.Ref<HTMLInputElement>
}): React.ReactNode {
  const reduce = useReducedMotion() ?? false
  const fieldRef = useRef<HTMLDivElement>(null)
  const hasError = invalid || (error !== undefined && error !== '')
  const errorId = useId()

  // 出错的那一刻抖一下(beUI Input 的做法);减少动效时不抖
  useEffect(() => {
    if (fieldRef.current === null || reduce || !hasError) return
    animate(fieldRef.current, { x: [0, -5, 5, -3, 3, -1, 0] }, { duration: 0.4 })
  }, [hasError, reduce])

  const field = (
    <div
      ref={fieldRef}
      data-state={hasError ? 'error' : success ? 'success' : 'idle'}
      className={cn(
        'apple-field app-no-drag flex items-center gap-2',
        'bg-surface-field',
        size === 'sm' ? 'h-7 px-2' : 'h-8 px-2.5',
        hasError
          ? 'border-danger ring-2 ring-danger/20'
          : 'border-border focus-within:border-fg-faint focus-within:ring-2 focus-within:ring-fg-faint/15',
        disabled && 'opacity-40',
        className
      )}
    >
      {icon !== undefined && <span className="shrink-0 text-fg-faint">{icon}</span>}
      <input
        ref={inputRef}
        type={type}
        value={value}
        disabled={disabled}
        aria-label={ariaLabel}
        aria-invalid={hasError || undefined}
        aria-describedby={error !== undefined && error !== '' ? errorId : undefined}
        placeholder={placeholder}
        inputMode={inputMode}
        onChange={(e) => onChange(e.target.value)}
        onFocus={onFocus}
        onBlur={onCommit}
        onKeyDown={(e) => {
          if (e.key === 'Enter') {
            /*
              ★ 组词期间的回车是「确认候选词」,不是提交 —— 更不能 `blur()`。
              少这一句的症状不是「提交早了」那么轻:回车一到就 `preventDefault`
              再抽走焦点,输入法那次上屏当场被打断,而它自己的组词串还在,
              于是同一段中文被写进框里**两遍**(中文用户打一个词看到「测试测试」)。
              判据与 `shell/TabRenameInput.tsx:86` 一字不差,别在这里改写法。
            */
            if (e.nativeEvent.isComposing) return
            e.preventDefault()
            onCommit?.()
            e.currentTarget.blur()
          } else if (e.key === 'Escape' && onRevert !== undefined) {
            // ★ 用 preventDefault 宣告「这次 Escape 我消费了」,**不能用
            // stopPropagation**:React 18 把监听器挂在根容器上,合成事件的
            // stopPropagation 只影响 React 树内部,原生事件照样一路冒到
            // document —— 而设置浮层的关闭正是挂在 document 上的。
            // 于是在端口框里按 Escape 会「还原草稿」+「整个面板消失」。
            // 约定:document 级的 Escape 消费者一律先查 `e.defaultPrevented`。
            e.preventDefault()
            onRevert()
          }
        }}
        className={cn(
          'selectable min-w-0 flex-1 bg-transparent text-[13px] text-fg outline-none',
          'placeholder:text-fg-faint'
        )}
      />
      {success && !hasError && (
        <motion.svg viewBox="0 0 24 24" fill="none" aria-hidden className="size-4 shrink-0 text-accent">
          <motion.path
            d="M5 12.5l4.5 4.5L19 7.5"
            stroke="currentColor"
            strokeWidth={2.5}
            strokeLinecap="round"
            strokeLinejoin="round"
            initial={reduce ? { pathLength: 1 } : { pathLength: 0 }}
            animate={{ pathLength: 1 }}
            transition={{ duration: 0.3, ease: 'easeOut' }}
          />
        </motion.svg>
      )}
    </div>
  )

  if (error === undefined) return field
  return (
    <div className="flex flex-col gap-1">
      {field}
      <AnimatePresence initial={false}>
        {error !== '' && (
          <motion.p
            id={errorId}
            role="alert"
            initial={reduce ? { opacity: 0 } : { opacity: 0, y: -4, filter: 'blur(4px)' }}
            animate={{ opacity: 1, y: 0, filter: 'blur(0px)' }}
            exit={reduce ? { opacity: 0 } : { opacity: 0, y: -4, filter: 'blur(4px)' }}
            transition={{ duration: 0.2 }}
            className="px-1 text-[11.5px] text-danger"
          >
            {error}
          </motion.p>
        )}
      </AnimatePresence>
    </div>
  )
}
