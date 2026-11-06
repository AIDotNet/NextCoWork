import { Check } from 'lucide-react'
import { Checkbox as CheckboxPrimitive, RadioGroup as RadioGroupPrimitive } from 'radix-ui'
import type { ReactNode } from 'react'
import { cn } from '../../lib/cn'

export interface ChoiceOption {
  value: string
  label: string
  /** 选项下面那行小字。给了才占第二行,没给就是单行。 */
  description?: string
}

/**
 * 「一组可选项」的两种形态:单选(`RadioCards`)与多选(`CheckboxCards`),
 * 以及它们和交互卡里的「动作行」共用的那副行壳子(`OptionRow` / `RowLead`)。
 *
 * ★ **所有行共用同一副壳子,只有指示器不同** —— 分成几个文件写的话,某一天
 * 改选中态的配色就会改漏一个,而这些行在同一张卡里上下相邻,漏掉的那个一眼
 * 就能看出来。差异被收在 `RowLead` 一个组件里,壳子只有一份。
 *
 * 无障碍与键盘导航交给 Radix:单选组的方向键漫游、`aria-checked`、
 * 空格切换都在原语里,自己用 `<div onClick>` 拼是拼不出来的。
 */

/** 行的外框。**它不是按钮** —— 可点区是里面那层,理由见 `OptionRow`。 */
const ROW_FRAME = 'app-no-drag rounded-[10px] border transition-[background-color,border-color] duration-150 motion-reduce:transition-none'

/**
 * 需求：审批选项要像一列紧凑的 agent 决策项，而不是一摞彼此争抢注意力的表单卡。
 * 选中态仍保留整行槽色，确保键盘高亮与真实选择都能一眼看见。
 */
const ROW_ON = 'border-accent/50 bg-accent/10'
const ROW_OFF = 'border-transparent bg-transparent hover:border-hairline hover:bg-tint-hover/55'

/** 可点区的类名。外框不可点,所以这层必须自己撑满、自己接焦点环。 */
export const ROW_HIT = [
  'group flex min-w-0 flex-1 cursor-pointer items-start gap-2.5 rounded-[8px] px-2.5 py-1.5',
  'text-left outline-none focus-visible:ring-2 focus-visible:ring-accent/25 disabled:cursor-default'
].join(' ')

/**
 * 一行的壳子:外框 + 可点区(`children`)+ 右侧槽(`trailing`)+ 展开区(`expanded`)。
 *
 * ★ **展开区在可点区外面。** 套进 `<button>` 里的 `textarea` 点不动(点击会被
 * 按钮吃掉),嵌套交互元素本身也是非法结构 —— 所以外框只能是一个 `div`,
 * 选中态的配色靠算好的 `checked` 传进来,而不是 `data-[state=checked]`。
 */
export function OptionRow({
  checked,
  disabled = false,
  trailing,
  expanded,
  children
}: {
  checked: boolean
  disabled?: boolean
  /** 排在行尾的东西(`→`、「跳过」)。 */
  trailing?: ReactNode
  /** 选中/展开后就地长出来的东西(输入框)。 */
  expanded?: ReactNode
  children: ReactNode
}): ReactNode {
  return (
    <div className={cn(ROW_FRAME, checked ? ROW_ON : ROW_OFF, disabled && 'opacity-40')}>
      <div className="flex items-stretch">
        {children}
        {trailing !== undefined && trailing !== null && (
          <span className="flex shrink-0 items-center pr-2">{trailing}</span>
        )}
      </div>
      {expanded !== undefined && expanded !== null && <div className="px-2.5 pb-2">{expanded}</div>}
    </div>
  )
}

/**
 * leading 槽。
 *
 * ★ **数字徽章只在没选中时露面** —— 它是快捷键提示,而选中态更需要一眼扫得出
 * 的指示器;两者抢同一个位置,选中的那个赢。超过 9 的行键盘选不中,给一个没有
 * 数字的占位,而不是一个按不出来的「10」。
 */
export function RowLead({
  index,
  checked,
  kind
}: {
  /** 0 基。渲染出来是 1 基 —— 键盘上按的是 1 不是 0。 */
  index: number
  checked: boolean
  /** `plain` 永远显示徽章:动作行没有「选中」这回事,徽章是它唯一的 leading。 */
  kind: 'radio' | 'check' | 'plain'
}): ReactNode {
  const box = 'mt-[2px] flex h-[16px] w-[16px] shrink-0 items-center justify-center text-[10px]'
  if (kind !== 'plain' && checked) {
    return kind === 'radio' ? (
      <span className={cn(box, 'rounded-pill bg-fg')}>
        <span className="h-[6px] w-[6px] rounded-pill bg-canvas" />
      </span>
    ) : (
      <span className={cn(box, 'rounded-[4px] bg-fg')}>
        <Check aria-hidden size={10} strokeWidth={3} className="text-canvas" />
      </span>
    )
  }
  return (
    <span
      aria-hidden="true"
      className={cn(
        box,
        'tabular-nums text-fg-faint',
        kind === 'radio' ? 'rounded-pill border border-border' : 'rounded-[4px] border border-border',
        kind === 'plain' && 'border-none bg-tint'
      )}
    >
      {index < 9 ? index + 1 : ''}
    </span>
  )
}

function OptionBody({ option, icon }: { option: ChoiceOption; icon?: ReactNode }): ReactNode {
  return (
    <span className="min-w-0 flex-1">
      <span className="flex items-center gap-1.5 text-[13px] leading-[1.5] text-fg">
        {icon}
        {option.label}
      </span>
      {option.description !== undefined && option.description !== '' && (
        <span className="mt-0.5 block text-[12px] leading-[1.5] text-fg-muted">{option.description}</span>
      )}
    </span>
  )
}

export function RadioCards({
  value,
  options,
  onValueChange,
  ariaLabel,
  disabled = false,
  renderExpanded,
  className
}: {
  value: string
  options: readonly ChoiceOption[]
  onValueChange: (value: string) => void
  ariaLabel: string
  disabled?: boolean
  /** 这一项选中后要就地长出来的东西(如「其它」的输入框)。返回 null 就是不长。 */
  renderExpanded?: (option: ChoiceOption) => ReactNode
  className?: string
}): ReactNode {
  return (
    <RadioGroupPrimitive.Root
      value={value}
      onValueChange={onValueChange}
      disabled={disabled}
      aria-label={ariaLabel}
      className={cn('flex flex-col gap-1.5', className)}
    >
      {options.map((option, index) => {
        const checked = value === option.value
        return (
          <OptionRow
            key={option.value}
            checked={checked}
            disabled={disabled}
            expanded={checked ? renderExpanded?.(option) : undefined}
          >
            <RadioGroupPrimitive.Item value={option.value} data-row-value={option.value} className={ROW_HIT}>
              <RowLead index={index} checked={checked} kind="radio" />
              <OptionBody option={option} />
            </RadioGroupPrimitive.Item>
          </OptionRow>
        )
      })}
    </RadioGroupPrimitive.Root>
  )
}

export function CheckboxCards({
  values,
  options,
  onToggle,
  ariaLabel,
  disabled = false,
  renderExpanded,
  className
}: {
  values: readonly string[]
  options: readonly ChoiceOption[]
  /** 只报「哪一项被点了」,合进已选集合是调用方的事 —— 那里才知道要不要保序 */
  onToggle: (value: string) => void
  ariaLabel: string
  disabled?: boolean
  renderExpanded?: (option: ChoiceOption) => ReactNode
  className?: string
}): ReactNode {
  return (
    // Radix 没有 CheckboxGroup —— 分组语义靠这层 role 补,少了它读屏只会念出
    // 一串互不相干的复选框,听不出它们属于同一道题。
    <div role="group" aria-label={ariaLabel} className={cn('flex flex-col gap-1.5', className)}>
      {options.map((option, index) => {
        const checked = values.includes(option.value)
        return (
          <OptionRow
            key={option.value}
            checked={checked}
            disabled={disabled}
            expanded={checked ? renderExpanded?.(option) : undefined}
          >
            <CheckboxPrimitive.Root
              checked={checked}
              disabled={disabled}
              onCheckedChange={() => onToggle(option.value)}
              data-row-value={option.value}
              className={ROW_HIT}
            >
              <RowLead index={index} checked={checked} kind="check" />
              <OptionBody option={option} />
            </CheckboxPrimitive.Root>
          </OptionRow>
        )
      })}
    </div>
  )
}
