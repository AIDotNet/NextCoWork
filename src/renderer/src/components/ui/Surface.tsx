/**
 * 转录流里所有「块」共用的那一个盒子。
 *
 * ★★ **为什么要有这个文件。** 改造之前,聊天流里并排堆着七套各写各的卡片配方:
 *
 *     ThinkingBlock       rounded-card bg-surface-raised/60          ← 没有描边
 *     ToolCallCard        rounded-card border border-border …/60
 *     SubagentNode        rounded-card border border-border …/40     ← 底色又差一档
 *     SubagentReportRow   rounded-card border-dashed border-accent/35
 *     WorkspaceBlock      rounded-card border border-border …/40
 *     PlanPanel           rounded-card border border-border bg-surface-raised(不透明)
 *     CompactionDivider   rounded-card border border-hairline …/40
 *
 * 七套里有三种底色不透明度、三种描边、两种「有没有描边」。单看每一张都说得过去,
 * 可它们是**上下紧挨着出现的** —— 一屏里「深度思考」是平的、命令卡是描边的、
 * 子代理卡又浅一档,读起来就是「这个界面有点乱」,而没有任何一条单独的规则写错了。
 * 这正是那种不会有人当 bug 报、却谁都看得见的问题。
 *
 * 所以收敛成一处定义。**收敛这件事没变,变的是收敛到哪一套外观:**
 *
 * ★★ 需求(本次):转录里的过程块 —— 工具行、折叠组标题、深度思考、子代理卡、
 * 工作区区块 —— 全部改成**文本风格**:不画描边、不画底色、不画左侧竖条,
 * 层级只用**缩进 + 颜色 + 字号**表达。理由是这些块在一次长回复里会连着出现
 * 二三十个,每个都镶一圈边、铺一层底色时,整屏读起来是「一列控件」,
 * 而它们表达的其实是「模型这一路做了什么」——那是正文的注脚,不是卡片墙。
 *
 * 于是 `tone` / `rail` / `dashed` 三个参数一并删掉,而不是留着让它们什么也不做:
 * 留下按了没反应的开关,下一个人会以为自己传错了值(§5 不做防御式 UI)。
 * **失败态因此只剩红字 + 红图标**(这是明确选定的方案),不再有红底和红竖条 ——
 * 要恢复「左侧竖条」那档强调,得先想清楚它在无边框版式里靠什么立住,
 * 不要直接把 `border-l-2` 加回来:没有外框的时候,一条孤零零的竖线会被读成缩进线。
 *
 * 原先那两个轴(tone/rail)和它们的底色不透明度、`border-stroke` 而非
 * `border-border` 的选择,都是为「有边框的卡片」服务的 —— 无边框之后这些
 * 约束整体失效,所以连同常量一起删,而不是留着注释掉的死代码。
 */
import { AnimatePresence, motion } from 'motion/react'
import type { ReactNode } from 'react'
import { cn } from '../../lib/cn'
import { motionScale, useMotionLevel } from '../../theme/useMotionLevel'

/**
 * 缩进量 = 折叠箭头(13px) + 行内 gap(8px)。
 *
 * ★ 展开区和次级行都靠它和标题文字左对齐 —— 无边框之后,**这条缩进就是
 * 「这几行属于上面那一行」的唯一视觉证据**。各调用点自己写 `pl-5`、`pl-3`
 * 会让同一层级的块对不齐,而那种错位不会有人当 bug 报。
 */
export const SURFACE_INDENT = 'pl-[21px]'

export function Surface({
  className,
  children,
  ...rest
}: {
  className?: string
  children: ReactNode
  // React 19 起 `ref` 就是一个普通 prop,跟着 `...rest` 一起落到 div 上 ——
  // 所以这里要用 `WithRef` 而不是 `WithoutRef`,否则 WorkspaceBlock 那个
  // 滚动定位用的 ref 会在类型上被挡掉。
} & React.ComponentPropsWithRef<'div'>): ReactNode {
  return (
    <div
      {...rest}
      className={cn(
        // 文字颜色的过渡:工具从 running 变 error 时整行换色,
        // 硬切会在一屏十几行里显得「闪了一下」(这条是从有边框那版留下来的,
        // 当时过渡的是底色和描边,现在过渡的是文字色,理由同一条)
        'min-w-0 transition-colors duration-200',
        className
      )}
    >
      {children}
    </div>
  )
}

/**
 * 块**内部**的次级行(运行状态、错误摘要、汇报状态那几条)。
 *
 * ★ 原先靠一道 `border-t border-hairline` 把它和标题行分开;无边框之后
 * 改成缩进对齐到标题文字下方 —— 没有这条缩进,次级行会读成「下一个块」,
 * 而不是「上一行的补充」。
 */
export function SurfaceRow({
  className,
  children,
  ...rest
}: {
  className?: string
  children: ReactNode
} & React.ComponentPropsWithoutRef<'div'>): ReactNode {
  return (
    <div
      {...rest}
      className={cn(
        'flex items-center gap-2 py-0.5 text-[11.5px]',
        SURFACE_INDENT,
        className
      )}
    >
      {children}
    </div>
  )
}

/**
 * 块的展开区(工具详情、思考正文、汇报全文)。
 *
 * ★★ **这是这次「加动效」里唯一真正需要 Motion 的地方。**
 * 展开区的高度是内容决定的,事先不知道 —— CSS 没法从 0 过渡到一个未知值。
 * (`grid-template-rows: 0fr → 1fr` 那个技巧能做到,但它在内容里有
 * `overflow` 或绝对定位时会裁错,而这里装的是 Markdown 和 diff。)
 * Motion 的 `height: 'auto'` 会在每次动画前实测目标高度,这正是它存在的理由。
 *
 * ★ `AnimatePresence` 收着退场动画:不套它的话 `{open && …}` 一旦转 false,
 *   React 当场卸载,收起是硬切 —— 展开有动画、收起没有,比两边都没有更难受。
 *
 * ★ padding 挂在**内层**。挂外层的话 `height: 0` 仍然留着上下内边距,
 *   收起后会剩一条几像素的空带。
 *
 * ★ 原先有一个 `divider` 参数控制展开区顶上那道分隔线(以及随之而来的内边距)。
 *   改文本风格后不再画任何分隔线,那个参数没有剩下的语义,所以删掉;
 *   缩进统一用 `SURFACE_INDENT`,调用点只补自己那份上下留白。
 */
export function SurfaceReveal({
  open,
  className,
  children
}: {
  open: boolean
  className?: string
  children: ReactNode
}): ReactNode {
  const scale = motionScale(useMotionLevel())
  return (
    <AnimatePresence initial={false}>
      {open && (
        <motion.div
          key="reveal"
          initial={{ height: 0, opacity: 0 }}
          animate={{ height: 'auto', opacity: 1 }}
          exit={{ height: 0, opacity: 0 }}
          // 和 `--ease-panel` 同一条曲线:起步快、尾巴长。
          // 三处面板开合用的就是它,展开区跟着走,整套界面的手感才是一套。
          transition={{
            duration: 0.22 * scale,
            ease: [0.32, 0.72, 0, 1],
            opacity: { duration: 0.14 * scale }
          }}
          style={{ overflow: 'hidden' }}
        >
          <div
            className={cn('pt-1 pb-1', SURFACE_INDENT, className)}
          >
            {children}
          </div>
        </motion.div>
      )}
    </AnimatePresence>
  )
}
