/**
 * 工具行的图标 —— 形态类 + 状态的二维投影。
 *
 * ★ **为什么把图标单独拆一个文件**:图标要同时出现在三处 —— 工具行、
 * 折叠组标题(一排小图标)、工作区区块标题(同样一排)。三处各写一份 map,
 * 迟早会出现「列表里是扳手、摘要里是文件」的错位,而那种错位没人会当 bug 报,
 * 只会觉得这个界面「有点乱」。
 *
 * ★ **运行中不用 spinner,用呼吸环**。一次 run 里可能同时有三五个工具在跑,
 * 多个 spinner 各转各的,转速还不同步 —— 视觉噪音远大于信息量。
 * 一圈静静呼吸的 ring 表达的是同一件事,却不抢注意力。
 */
import {
  Brain,
  Camera,
  FilePen,
  FileText,
  Globe,
  ImageIcon,
  ListTree,
  MessageCircleQuestion,
  Plug,
  Search,
  Sparkles,
  Terminal,
  Video,
  Workflow,
  type LucideIcon
} from 'lucide-react'
import type { ReactNode } from 'react'
import type { ToolShape } from '../../../../shared/domain/tool-presenter'
import { base } from '../../../../shared/domain/tool-presenter'
import { cn } from '../../lib/cn'
import { iconFor } from '../../lib/file-icon'

/**
 * 四态与 ToolCallState 一致。已提交的 tool_call 在执行前是 pending；
 * 流式参数尚未提交、tools[callId] 还不存在时，界面也使用 pending。
 */
export type ToolViewStatus = 'pending' | 'running' | 'ok' | 'error'

export const SHAPE_ICON: Record<ToolShape, LucideIcon> = {
  reasoning: Brain,
  read: FileText,
  mutate: FilePen,
  search: Search,
  command: Terminal,
  network: Globe,
  orchestration: ListTree,
  interaction: MessageCircleQuestion,
  // 生成式可视化。选 Sparkles 而不是图表/图形类图标:这一档画的东西横跨
  // SVG 图、图表、仪表盘、表单,任何一个具体的"图"都会在别的用例上误导。
  widget: Sparkles,
  // 生图:产物就是一张图,图标直说,不和 widget 的 Sparkles 混用(那一档是"画代码")
  image: ImageIcon,
  // 生成视频:产物是一段视频,用摄像机图标直说(与 image 的相纸、screenshot 的相机区分开)
  video: Video,
  // 页面截图:同属「产物是一张图」,但出处是浏览器 —— 相机把「拍下来的」这件事说清,
  // 和生图的相纸图标区分开(一个是画的,一个是拍的)
  screenshot: Camera,
  // 工作流:产物是一张节点图,lucide 的 Workflow 图标就是两个连着的方块
  workflow: Workflow,
  external: Plug
}

/** 状态 → 前景色。成功态刻意用 faint:成功是默认结果,不该抢眼。 */
export const STATUS_COLOR: Record<ToolViewStatus, string> = {
  pending: 'text-fg-faint',
  running: 'text-accent',
  ok: 'text-fg-faint',
  error: 'text-danger'
}

export function ToolIcon({
  shape,
  status,
  size = 13,
  path
}: {
  shape: ToolShape
  status: ToolViewStatus
  size?: number
  /**
   * 这次调用的目标文件。给了就**按扩展名换图标**(`.tsx` 是蓝色的 TS 图标、
   * 锁文件是黄锁),没给就用形态图标。
   *
   * ★ 需求:文件类型要在行首一眼看出来,而不是在行中间挂一枚写着「TSX」的方块。
   * ★ 复用 `lib/file-icon`,不在这里再写一张扩展名表 —— 同一个 `.ts` 在文件树、
   * Git 面板、转录里必须长得一样;两张表迟早分叉成「树认得 .mjs、转录不认得」
   * (`views/git/GitFeature.tsx:17` 记过同一条理由)。
   */
  path?: string
}): ReactNode {
  const file = path === undefined || path === '' ? undefined : iconFor(base(path), 'file')
  const Icon = file?.Icon ?? SHAPE_ICON[shape]
  /*
    ★ 运行中 / 失败时**丢掉文件自带的颜色**:那两个状态是这一行此刻唯一重要的事,
    而 `.ts` 的蓝盖在上面会让一条失败的行看起来一切正常(失败只靠颜色表达,
    没有红底也没有边框)。等待态保留文件色,靠外层的 opacity 压住。
  */
  const fileClass = file !== undefined && (status === 'ok' || status === 'pending')
    ? file.className
    : undefined
  return (
    <span
      className={cn(
        'relative flex shrink-0 items-center justify-center',
        status === 'running' && 'text-accent',
        status === 'error' && 'text-danger',
        status === 'ok' && 'text-accent-soft',
        // 等待态压透明度而不是换颜色:它和「成功」都不该抢眼,
        // 但要能一眼分辨 —— 透明度差比色相差更安静
        status === 'pending' && 'text-fg-faint opacity-40'
      )}
      style={{ width: size + 4, height: size + 4 }}
    >
      {status === 'running' && (
        <span
          aria-hidden
          className="absolute inset-0 animate-pulse rounded-full ring-[1.5px] ring-accent/40"
        />
      )}
      <Icon size={size} {...(fileClass === undefined ? {} : { className: fileClass })} />
    </span>
  )
}

/** 一排形态小图标 —— 折叠组与工作区区块的标题行用。 */
export function ShapeStrip({
  shapes,
  size = 11
}: {
  shapes: readonly ToolShape[]
  size?: number
}): ReactNode {
  if (shapes.length === 0) return null
  return (
    <span className="flex shrink-0 items-center gap-1 text-fg-faint">
      {shapes.map((s) => {
        const Icon = SHAPE_ICON[s]
        return <Icon key={s} size={size} />
      })}
    </span>
  )
}
