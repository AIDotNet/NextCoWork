/**
 * 转录里每一条「过程行」的排版原语 —— 工具行、思考行、折叠组标题、子代理行共用。
 *
 * ★★ 需求:参考实现里这些行读起来是**一份活动日志**,不是一叠控件:
 * 等高、左端对齐、一行几段(做了什么 / 对谁做的 / 在哪儿 / 改了多少),
 * 亮度依次递减;展开箭头平时不画,鼠标移上去才在**行尾**出现。
 * 这几件事各自都不难,但它们必须在所有行上**一模一样**才成立 ——
 * 一屏里出现两种行高或两处箭头位置,整列立刻散掉。所以常量收在这里,
 * 不在各自组件里手写(那正是上一版长出七套卡片配方的起点,见 `ui/Surface.tsx`)。
 *
 * ★ 这里**不做折叠状态、不做数据获取**:行只负责长相。谁能展开、展开什么,
 * 由调用方决定 —— 这样同一套排版能同时服务「已提交的转录」和「还在流的块」。
 */
import { ChevronRight } from 'lucide-react'
import type { ReactNode } from 'react'
import { dirOf, type ToolLine, type ToolLineStats } from '../../../../shared/domain/tool-presenter'
import { cn } from '../../lib/cn'
import { useI18n } from '../../i18n'
import { useWorkspaceFile } from './workspace-file'

/**
 * 一条过程行的外壳类名。
 *
 * ★ `min-h-[26px]` 而不是 `py-1.5`:行高要在「有图标」「只有文字」「带右端状态」
 * 三种内容下都相同,否则连续几十行会像被随机撑开过。
 * ★ `group/row` 是具名 group —— 行内可能还套着别的 group(子代理的可点区域),
 * 匿名 group 会让行尾箭头跟着最近的那个 group 亮,表现为「鼠标在别处也冒箭头」。
 */
export const ROW_CLASS =
  'group/row flex min-h-[26px] w-full min-w-0 items-center gap-2 py-px text-left text-[13.5px] text-fg-muted transition-colors hover:text-fg'

/**
 * 行尾的展开箭头。
 *
 * ★ **平时不画**(占位保留,不跳动):一列几十行里每行都挂一个箭头时,
 * 箭头本身变成了最显眼的图形,而它表达的信息量是零 —— 每行都能展开。
 * 展开中的行例外:它必须一直画,否则用户找不到收起的地方。
 */
export function RowChevron({ open }: { open: boolean }): ReactNode {
  return (
    <ChevronRight
      size={12}
      aria-hidden
      className={cn(
        'ml-0.5 shrink-0 text-fg-faint transition-[transform,opacity] duration-150 motion-reduce:transition-none',
        open ? 'rotate-90 opacity-100' : 'opacity-0 group-hover/row:opacity-60'
      )}
    />
  )
}

/**
 * 行右端的 `+7 −1`。
 *
 * ★ 0 的那一侧**不画**:一次纯新增显示成「+7 −0」时,那个 −0 会被当成
 * 「删了点什么」去找。两侧都是 0 就整格不画(Edit 把一行改成同样的一行)。
 * ★ 用 `−`(U+2212)而不是 ASCII 的 `-`:等宽字体里前者与 `+` 同宽,
 * 相邻行的数字因此能对齐成一列。
 */
function RowStats({ stats }: { stats: ToolLineStats }): ReactNode {
  if (stats.additions === 0 && stats.deletions === 0) return null
  return (
    <span
      data-testid="row-stats"
      className="flex shrink-0 items-center gap-1 font-mono text-[11.5px] tabular-nums"
    >
      {stats.additions > 0 && <span className="text-accent">+{stats.additions}</span>}
      {stats.deletions > 0 && <span className="text-danger">−{stats.deletions}</span>}
    </span>
  )
}

/**
 * 一条**可展开的工具行**的完整结构。
 *
 * 版式(亮度依次递减,这个顺序是固定的,别按工具临时调):
 *
 *     [图标] [标签 muted] [目标 fg] [目录 faint] … [+N −M] [摘要/状态] [箭头]
 *
 * ★ 文件类型**只由最左边那枚图标表达**(调用方按扩展名选图标,见 `ToolIcon`
 * 的 `path`)。这里曾经在标签和文件名之间再插一枚写着「TSX」的彩色方块,
 * 结果是同一件事在一行里说了两遍,而且它正好卡在视线扫「做了什么 → 对谁做的」
 * 的路径中间,把那一跳截断了。图标在行首、本来就要占位,是零成本的位置。
 *
 * 用户扫的是**目标**那一格(文件名、命令);标签只是让它有主语,写亮的话
 * 一列行就全是「读取 读取 编辑」在跳。失败时**只染标签**:目标是文件名,
 * 把它染红会让人以为是这个文件本身有问题。
 *
 * ★★ 为什么不是「一整个 `<button>`」:行里有两个不同的动作 —— 展开详情、
 * 打开这个文件。`<button>` 里不能再套 `<button>`(HTML 明令禁止),所以行本身是
 * 一个 `div`,里面摆成:
 *
 *     [展开按钮:图标+标签(+不可点的目标)] [文件名按钮] [展开按钮2:目录+右端信息] [附加动作]
 *
 * ★★ **目标不可点时必须待在展开按钮里面**。它曾经是一个裸 `<span>`,于是
 * 一条命令行(目标是命令原文,没有文件可开)最宽的那一段**点上去毫无反应** ——
 * 用户看到的是「点这行有时能展开、有时不能」,而那偏偏是行里最大的一块。
 * 只有真的能打开文件时,目标才独立成第二个按钮。
 *
 * ★ 第二个展开按钮 `tabIndex={-1} aria-hidden`:它只为「点行里的空白处也能展开」
 * 这个鼠标习惯而存在。键盘与读屏走第一个按钮 —— 两个都可聚焦的话,Tab 过一行要
 * 按两下,而它们做的是同一件事。
 */
export function ToolRow({
  icon,
  line,
  danger = false,
  stats,
  open,
  onToggle,
  onOpenPath,
  trailing,
  actions
}: {
  icon: ReactNode
  line: ToolLine
  danger?: boolean
  stats?: ToolLineStats | undefined
  open: boolean
  onToggle: () => void
  /**
   * 点文件名打开它。**不给就画成纯文本** —— 一枚点了没反应的链接比没有链接
   * 难解释得多(§5 不做防御式 UI)。只读面板拿不到工作区入口,走的就是这条。
   */
  onOpenPath?: ((path: string) => void) | undefined
  /** 右端的摘要 / 运行状态那几格 */
  trailing?: ReactNode
  /** 行尾按钮(停止运行中的命令)—— 它不属于任何一个展开按钮,所以是行的直接子节点 */
  actions?: ReactNode
}): ReactNode {
  const { t } = useI18n()
  const { root } = useWorkspaceFile()
  const target = line.target ?? ''
  const path = line.path
  /*
    ★ 目录在**这里**算,不在 presenter 里算:相对路径需要工作区根,而 presenter 是
    纯 shared 模块、不许碰 store(见 `tool-presenter.ts` 文件头与 `dirOf`)。
    没有 path 的行(命令、查询)用 presenter 给的 context 原样显示。
  */
  const context = path === undefined ? (line.context ?? '') : dirOf(path, root)
  const openable = path !== undefined && onOpenPath !== undefined
  const targetClass = cn(
    'min-w-0 truncate text-fg',
    line.mono === true && 'font-mono text-[12.5px]'
  )

  return (
    <div className={ROW_CLASS}>
      <button
        type="button"
        aria-expanded={open}
        onClick={onToggle}
        className={cn(
          'flex min-w-0 cursor-pointer items-center gap-2 text-left',
          // 不可点的目标住在这个按钮里,所以它要能收缩;可点时按钮只包图标和标签
          openable ? 'shrink-0' : 'shrink'
        )}
      >
        {icon}
        <span data-testid="row-label" className={cn('shrink-0', danger && 'text-danger')}>
          {line.label}
        </span>
        {!openable && target !== '' && (
          <span data-testid="row-target" className={targetClass}>{target}</span>
        )}
      </button>

      {openable && path !== undefined && target !== '' && (
        <button
          type="button"
          data-testid="row-target"
          title={t('chat.tool.openFile', { path })}
          onClick={() => onOpenPath?.(path)}
          className={cn(
            targetClass,
            'shrink-0 cursor-pointer hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/50'
          )}
        >
          {target}
        </button>
      )}

      <button
        type="button"
        tabIndex={-1}
        aria-hidden
        onClick={onToggle}
        className="flex min-w-0 flex-1 cursor-pointer items-center gap-2 text-left"
      >
        {context === ''
          ? <span className="min-w-0 flex-1" />
          : (
            <span
              data-testid="row-context"
              className="min-w-0 flex-1 truncate font-mono text-[12px] text-fg-faint"
            >
              {context}
            </span>
          )}
        {stats !== undefined && <RowStats stats={stats} />}
        {trailing}
        <RowChevron open={open} />
      </button>
      {actions}
    </div>
  )
}
