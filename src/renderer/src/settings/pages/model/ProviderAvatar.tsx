/**
 * 供应商列表里那个方块头像。
 *
 * 之前三处(目录卡片 / 启用列表 / 右侧面板抬头)各写了一遍同样的
 * `avatarInitial(p.name)` 方块 —— 里面是一个字母。44 个内置预设摆在
 * 「添加供应商」网格里,就是 44 个灰色字母,而 O 有两家、M 有三家、
 * 「智」有两家:**用户没法一眼认出哪张卡是哪家**,而这正是那个网格的全部作用。
 *
 * 所以这里换成 lobehub 的品牌字形(`ProviderIcon`),**认不出时退回原来那个
 * 首字母**,而不是退回 `ProviderIcon` 默认那颗 `Sparkles`:
 * 内置预设里有四家 lobehub 没收字形(RoutinAI / OhMyGPT / LocalAI /
 * llama.cpp),它们会**并排**出现在同一个网格里 —— 全变成同一颗星等于把
 * 四家显示成一模一样,而首字母至少还能区分。
 *
 * ★ 候选顺序是 `[name, id]`,**名字在前**:`gemini-openai` 这个 id 里含着
 * `openai`,先拿 id 匹配就会给 Gemini 挂上 OpenAI 的 logo。
 * `__tests__/preset-brands.test.ts` 用同样的顺序钉死了全部 44 家。
 */
import type { ReactNode } from 'react'
import { ProviderIcon } from '../../../components/brand/ProviderIcon'
import { cn } from '../../../lib/cn'
import { avatarInitial } from './enabled-models'

/** 两种尺寸对应两处既有几何:目录卡片 20px、列表与面板抬头 24px */
const BOX = {
  sm: { box: 'size-5 rounded-[6px] text-[10px]', icon: 13 },
  md: { box: 'size-6 rounded-[8px] text-[11px]', icon: 15 }
} as const

export function ProviderAvatar({
  name,
  id,
  size = 'md',
  className
}: {
  name: string
  /** 供应商 id。名字认不出时的第二个候选 —— 比如本地那几家名字花哨、id 反而干净 */
  id?: string
  size?: keyof typeof BOX
  className?: string
}): ReactNode {
  const { box, icon } = BOX[size]
  return (
    <span
      className={cn('flex shrink-0 items-center justify-center bg-surface-sunken', box, className)}
      aria-hidden
    >
      <ProviderIcon
        name={[name, id]}
        size={icon}
        // 字形给足对比度,兜底的字母仍旧是弱化的 —— 它是占位,不该和旁边的名字抢
        className="text-fg"
        fallback={<span className="text-fg-muted">{avatarInitial(name)}</span>}
      />
    </span>
  )
}
