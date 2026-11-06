/**
 * `Workflow` 工具的画布 —— 节点 + 依赖连线,状态实时变。
 *
 * ## 尺寸怎么定(方案 A:内联自适应画布)
 *
 * - **宽**:撑满工具卡(对话列 760px,卡内约 720px)。不突破列宽 —— 一张图把对话列
 *   左右撑开,往上往下的文字就对不齐了。
 * - **高**:按布局算出来的图高 + 内边距,夹在 `INLINE_MIN_H`–`INLINE_MAX_H` 之间。
 *   两个节点的小图不会占出一大块空白,二十个节点的大图也不会把半屏对话顶出去。
 *   放不下的部分靠 `fitView` 缩小(下限 `INLINE_MIN_ZOOM`,再小字就读不清了),
 *   还放不下就拖拽平移,或者用右上角的按钮缩放 / 适应 / 全屏。
 * - **没有独立标题栏**:标题与进度浮在画布左上角,操作按钮浮在右上角 ——
 *   卡片就是画布本身,不再多占一条 36px 的横栏(对话里外面那行工具标题也去掉了,
 *   见 `parts.tsx`)。
 *
 * ## 滚轮交还给对话
 *
 * 内联画布**关掉滚轮缩放/平移**,`preventScrolling={false}`:它嵌在一条长对话中间,
 * 鼠标划过时把滚轮吃掉,用户就会「卡」在这张图上滚不下去。缩放留给触控板捏合和全屏。
 *
 * ★ 这个文件是懒加载的(`WorkflowDetail.tsx` 里 `lazy()`):`@xyflow/react` 只在
 *   对话里真出现工作流时才进来,没开实验开关的人一个字节都不多下。
 */
import '@xyflow/react/dist/base.css'
import {
  Background,
  BackgroundVariant,
  Handle,
  MarkerType,
  Panel,
  Position,
  ReactFlow,
  ReactFlowProvider,
  useReactFlow,
  type Edge,
  type Node,
  type NodeProps
} from '@xyflow/react'
import { CheckCircle2, Circle, Loader2, Maximize2, Minus, MinusCircle, Plus, Scan, XCircle } from 'lucide-react'
import { memo, useEffect, useMemo, useState, type ReactNode } from 'react'
import { formatDuration } from '../../../../shared/agent/duration'
import { formatTokenCount } from '../../../../shared/agent/tokens'
import {
  countWorkflow,
  layoutWorkflow,
  WORKFLOW_NODE_H,
  WORKFLOW_NODE_W,
  workflowTokens,
  type WorkflowNodeState,
  type WorkflowNodeStatus,
  type WorkflowState
} from '../../../../shared/agent/workflow'
import { Dialog } from '../../components/ui/Dialog'
import { useI18n } from '../../i18n'
import { cn } from '../../lib/cn'
import { DETAIL_CARD_CLASS } from './detail-card'

const INLINE_MIN_H = 180
const INLINE_MAX_H = 380
const INLINE_MIN_ZOOM = 0.55

const STATUS_ICON: Record<WorkflowNodeStatus, typeof Circle> = {
  pending: Circle,
  running: Loader2,
  done: CheckCircle2,
  error: XCircle,
  skipped: MinusCircle
}

const STATUS_TONE: Record<WorkflowNodeStatus, string> = {
  pending: 'text-fg-faint',
  running: 'text-accent',
  done: 'text-success',
  error: 'text-danger',
  skipped: 'text-fg-faint'
}

const STATUS_BORDER: Record<WorkflowNodeStatus, string> = {
  pending: 'border-stroke',
  running: 'border-accent shadow-[0_0_0_3px_color-mix(in_srgb,var(--color-accent)_18%,transparent)]',
  done: 'border-stroke',
  error: 'border-danger/60',
  skipped: 'border-stroke border-dashed opacity-60'
}

type CanvasNode = Node<{ state: WorkflowNodeState; selected: boolean }, 'step'>

function nodeDuration(n: WorkflowNodeState): string | undefined {
  return n.startedAt !== undefined && n.endedAt !== undefined ? formatDuration(n.endedAt - n.startedAt) : undefined
}

const StepNode = memo(function StepNode({ data }: NodeProps<CanvasNode>): ReactNode {
  const { t } = useI18n()
  const n = data.state
  const Icon = STATUS_ICON[n.status]
  const duration = nodeDuration(n)
  const items = n.items
  return (
    <div
      data-testid="workflow-node"
      data-map={n.mapOver === undefined ? undefined : 'true'}
      data-status={n.status}
      title={`${n.title} · ${t(`workflow.status.${n.status}`)}`}
      className={cn(
        'flex cursor-pointer items-center gap-2 rounded-[10px] border bg-canvas px-2.5 transition-[border-color,box-shadow] motion-reduce:transition-none',
        STATUS_BORDER[n.status],
        data.selected && 'ring-2 ring-accent/50',
        // 逐项节点画成一叠卡片:它背后是一组子代理,不是一个
        n.mapOver !== undefined && 'shadow-[3px_3px_0_-1px_var(--color-canvas),3px_3px_0_0_var(--color-stroke),6px_6px_0_-1px_var(--color-canvas),6px_6px_0_0_var(--color-stroke)]'
      )}
      style={{ width: WORKFLOW_NODE_W, height: WORKFLOW_NODE_H }}
    >
      {/* 连线端点:不可交互,只是给边一个落点 */}
      <Handle type="target" position={Position.Left} isConnectable={false} className="!h-1.5 !w-1.5 !min-w-0 !border-0 !bg-stroke" />
      <Icon
        size={15}
        className={cn('shrink-0', STATUS_TONE[n.status], n.status === 'running' && 'animate-spin motion-reduce:animate-none')}
        aria-hidden
      />
      <div className="min-w-0 flex-1">
        <div className="truncate text-[12.5px] font-medium leading-tight text-fg">{n.title}</div>
        <div className="mt-0.5 flex items-center gap-1.5 text-[11px] leading-tight text-fg-faint">
          <span className="truncate">{n.model === undefined ? n.agent : `${n.agent} · ${n.model}`}</span>
          {duration !== undefined && <span className="shrink-0 tabular-nums">· {duration}</span>}
        </div>
      </div>
      {items !== undefined && (
        <span
          data-testid="workflow-node-items"
          className={cn('shrink-0 rounded-full bg-tint-hover px-1.5 text-[10px] tabular-nums', items.failed > 0 ? 'text-danger' : 'text-fg-muted')}
          title={t('workflow.node.items', { done: items.done, total: items.total })}
        >
          {items.done}/{items.total}
        </span>
      )}
      {n.mapOver !== undefined && items === undefined && (
        <span className="shrink-0 rounded-full bg-tint-hover px-1.5 text-[10px] text-fg-muted">{t('workflow.node.each')}</span>
      )}
      {(n.attempts ?? 1) > 1 && (
        <span className="shrink-0 rounded-full bg-tint-hover px-1.5 text-[10px] tabular-nums text-fg-muted" title={t('workflow.node.attempts', { count: n.attempts ?? 1 })}>
          ×{n.attempts}
        </span>
      )}
      <Handle type="source" position={Position.Right} isConnectable={false} className="!h-1.5 !w-1.5 !min-w-0 !border-0 !bg-stroke" />
    </div>
  )
})

const NODE_TYPES = { step: StepNode }

function edgeTone(target: WorkflowNodeStatus, source: WorkflowNodeStatus): string {
  if (source === 'error' || target === 'skipped') return 'var(--color-fg-faint)'
  if (source === 'done') return 'var(--color-success)'
  return 'var(--color-border)'
}

function buildGraph(state: WorkflowState, selected: string | null): { nodes: CanvasNode[]; edges: Edge[] } {
  const { positions } = layoutWorkflow(state)
  const byId = new Map(state.nodes.map((n) => [n.id, n]))
  const nodes: CanvasNode[] = state.nodes.map((n) => ({
    id: n.id,
    type: 'step',
    position: positions[n.id] ?? { x: 0, y: 0 },
    data: { state: n, selected: n.id === selected },
    draggable: false,
    connectable: false,
    selectable: false
  }))
  const edges: Edge[] = state.nodes.flatMap((n) =>
    n.dependsOn
      .filter((d) => byId.has(d))
      .map((d) => {
        const source = byId.get(d)!
        const color = edgeTone(n.status, source.status)
        return {
          id: `${d}->${n.id}`,
          source: d,
          target: n.id,
          type: 'smoothstep',
          // 下游正在跑 = 数据正沿这条边流过去
          animated: n.status === 'running',
          // 虚线 = 软依赖:这条上游没成,下游照样会跑
          style: { stroke: color, strokeWidth: 1.5, ...(n.allowPartial === true ? { strokeDasharray: '5 4' } : {}) },
          markerEnd: { type: MarkerType.ArrowClosed, width: 14, height: 14, color }
        }
      })
  )
  return { nodes, edges }
}

/**
 * fit 时给四周留的边。★ 顶部要让出浮在画布上的那一排(标题胶囊 + 按钮组,约 28px 高
 * + 上边距),底部让出「点击节点查看产出」那行提示 —— 否则 fit 之后第一行节点
 * 正好压在标题底下。
 */
const FIT_PADDING = { top: '52px', bottom: '30px', left: '24px', right: '24px' } as const
const FIT_PAD_Y = 52 + 30

/**
 * 布局变了(新节点出现、容器宽度变化)就重新 fit。
 * ★ 只看「结构」不看状态:每次状态推送都 fit 的话,用户刚拖到一边看细节,
 *   下一条进度又把视图拽回原位。
 */
function FitOnLayout({ structureKey, minZoom }: { structureKey: string; minZoom: number }): null {
  const { fitView } = useReactFlow()
  useEffect(() => {
    void fitView({ padding: FIT_PADDING, maxZoom: 1, minZoom })
  }, [structureKey, fitView, minZoom])
  return null
}

const TOOL_BUTTON_CLASS =
  'inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-[6px] text-fg-muted transition-colors hover:bg-tint-hover hover:text-fg motion-reduce:transition-none'

/** 浮在画布左上角:标题 + 进度。★ 浮层而不是独立一条标题栏 —— 画布就是整张卡 */
function TitleChip({ state }: { state: WorkflowState }): ReactNode {
  const { t } = useI18n()
  const c = countWorkflow(state)
  const tokens = state.nodes.reduce((sum, n) => sum + workflowTokens(n.usage), 0)
  return (
    <div className="flex max-w-[60%] items-center gap-2 rounded-[8px] border border-stroke bg-canvas/85 px-2.5 py-1 text-[12px] backdrop-blur-sm">
      <span className="min-w-0 truncate font-medium text-fg" title={state.title}>{state.title}</span>
      <span className="shrink-0 tabular-nums text-fg-faint">{t('workflow.progress', { done: c.done, total: c.total })}</span>
      {c.running > 0 && <span className="shrink-0 text-accent">{t('workflow.running', { count: c.running })}</span>}
      {c.failed > 0 && <span className="shrink-0 text-danger">{t('workflow.failed', { count: c.failed })}</span>}
      {tokens > 0 && <span className="shrink-0 tabular-nums text-fg-faint">{t('workflow.tokens', { count: formatTokenCount(tokens) })}</span>}
    </div>
  )
}

/** 浮在画布右上角的按钮组。必须在 `<ReactFlow>` 里面 —— 缩放要拿这一张画布的实例 */
function Toolbar({ minZoom, onExpand }: { minZoom: number; onExpand?: () => void }): ReactNode {
  const { t } = useI18n()
  const { zoomIn, zoomOut, fitView } = useReactFlow()
  return (
    <div className="flex items-center gap-0.5 rounded-[8px] border border-stroke bg-canvas/85 p-0.5 backdrop-blur-sm">
      <button type="button" className={TOOL_BUTTON_CLASS} onClick={() => void zoomOut()} aria-label={t('workflow.zoomOut')} title={t('workflow.zoomOut')}>
        <Minus size={13} />
      </button>
      <button type="button" className={TOOL_BUTTON_CLASS} onClick={() => void zoomIn()} aria-label={t('workflow.zoomIn')} title={t('workflow.zoomIn')}>
        <Plus size={13} />
      </button>
      <button
        type="button"
        className={TOOL_BUTTON_CLASS}
        onClick={() => void fitView({ padding: FIT_PADDING, maxZoom: 1, minZoom, duration: 200 })}
        aria-label={t('workflow.fit')}
        title={t('workflow.fit')}
      >
        <Scan size={13} />
      </button>
      {onExpand !== undefined && (
        <button
          type="button"
          data-testid="workflow-expand"
          className={TOOL_BUTTON_CLASS}
          onClick={onExpand}
          aria-label={t('workflow.expand')}
          title={t('workflow.expand')}
        >
          <Maximize2 size={13} />
        </button>
      )}
    </div>
  )
}

function Graph({
  state,
  selected,
  onSelect,
  inline,
  onExpand
}: {
  state: WorkflowState
  selected: string | null
  onSelect: (id: string | null) => void
  inline: boolean
  onExpand?: () => void
}): ReactNode {
  const { t } = useI18n()
  const { nodes, edges } = useMemo(() => buildGraph(state, selected), [state, selected])
  const structureKey = state.nodes.map((n) => `${n.id}<${n.dependsOn.join(',')}`).join('|')
  const minZoom = inline ? INLINE_MIN_ZOOM : 0.25
  return (
    <ReactFlow
      nodes={nodes}
      edges={edges}
      nodeTypes={NODE_TYPES}
      fitView
      fitViewOptions={{ padding: FIT_PADDING, maxZoom: 1, minZoom }}
      minZoom={minZoom}
      maxZoom={1.5}
      nodesDraggable={false}
      nodesConnectable={false}
      elementsSelectable={false}
      zoomOnScroll={!inline}
      panOnScroll={false}
      zoomOnDoubleClick={false}
      preventScrolling={!inline}
      proOptions={{ hideAttribution: true }}
      onNodeClick={(_, node) => onSelect(node.id === selected ? null : node.id)}
      onPaneClick={() => onSelect(null)}
      colorMode="system"
      className="workflow-canvas"
    >
      <FitOnLayout structureKey={structureKey} minZoom={minZoom} />
      <Background variant={BackgroundVariant.Dots} gap={16} size={1} color="var(--color-stroke)" />
      <Panel position="top-left" className="!m-2.5 max-w-[calc(100%-140px)]">
        <TitleChip state={state} />
      </Panel>
      <Panel position="top-right" className="!m-2.5">
        <Toolbar minZoom={minZoom} {...(onExpand === undefined ? {} : { onExpand })} />
      </Panel>
      {selected === null && (
        <Panel position="bottom-right" className="!m-1.5 pointer-events-none text-[11px] text-fg-faint">
          {t('workflow.hint')}
        </Panel>
      )}
    </ReactFlow>
  )
}

function NodeInspector({ node }: { node: WorkflowNodeState }): ReactNode {
  const { t } = useI18n()
  const body = node.status === 'done' ? node.summary : (node.error ?? node.summary)
  const duration = nodeDuration(node)
  const tokens = workflowTokens(node.usage)
  return (
    <div data-testid="workflow-inspector" className="border-t border-stroke px-3 py-2.5">
      <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[12px]">
        <span className="font-medium text-fg">{node.title}</span>
        <span className={STATUS_TONE[node.status]}>{t(`workflow.status.${node.status}`)}</span>
        {duration !== undefined && <span className="tabular-nums text-fg-faint">{duration}</span>}
        {tokens > 0 && <span className="tabular-nums text-fg-faint">{t('workflow.tokens', { count: formatTokenCount(tokens) })}</span>}
        {(node.attempts ?? 1) > 1 && <span className="text-fg-faint">{t('workflow.node.attempts', { count: node.attempts ?? 1 })}</span>}
        {node.items !== undefined && (
          <span className="tabular-nums text-fg-faint">
            {t('workflow.node.items', { done: node.items.done, total: node.items.total })}
            {node.items.failed > 0 && <span className="text-danger"> · {t('workflow.node.itemsFailed', { count: node.items.failed })}</span>}
            {node.items.dropped !== undefined && <> · {t('workflow.node.itemsDropped', { count: node.items.dropped })}</>}
          </span>
        )}
        {node.mapOver !== undefined && (
          <span className="text-fg-faint">
            {t('workflow.node.mapOver')} · <span className="font-mono">{node.mapOver}</span>
          </span>
        )}
        {node.structured === true && <span className="font-mono text-fg-faint">JSON</span>}
        <span className="text-fg-faint">
          {t('workflow.node.agent')} · <span className="font-mono">{node.agent}</span>
        </span>
        {node.model !== undefined && (
          <span className="text-fg-faint">
            {t('workflow.node.model')} · <span className="font-mono">{node.model}</span>
          </span>
        )}
        {node.dependsOn.length > 0 && (
          <span className="text-fg-faint">
            {t('workflow.node.dependsOn')} · <span className="font-mono">{node.dependsOn.join(', ')}</span>
            {node.allowPartial === true && <> · {t('workflow.node.allowPartial')}</>}
          </span>
        )}
      </div>
      <p
        className={cn(
          'scroll-thin mt-1.5 max-h-40 overflow-auto whitespace-pre-wrap text-[12px] leading-relaxed',
          node.status === 'error' ? 'text-danger' : 'text-fg-muted'
        )}
      >
        {body === undefined || body === '' ? t('workflow.node.noOutput') : body}
      </p>
    </div>
  )
}

export default function WorkflowCanvas({ state }: { state: WorkflowState }): ReactNode {
  const { t } = useI18n()
  const [selected, setSelected] = useState<string | null>(null)
  const [expanded, setExpanded] = useState(false)
  const layout = useMemo(() => layoutWorkflow(state), [state])
  // 高 = 图高 + 上下给浮层让出的边,夹在上下限之间(理由见文件头)
  const height = Math.min(INLINE_MAX_H, Math.max(INLINE_MIN_H, layout.height + FIT_PAD_Y))
  const selectedNode = selected === null ? undefined : state.nodes.find((n) => n.id === selected)

  return (
    <div data-testid="workflow-canvas" className={cn(DETAIL_CARD_CLASS, 'w-full')}>
      <div className="relative w-full" style={{ height }}>
        <ReactFlowProvider>
          <Graph state={state} selected={selected} onSelect={setSelected} inline onExpand={() => setExpanded(true)} />
        </ReactFlowProvider>
      </div>
      {selectedNode !== undefined && <NodeInspector node={selectedNode} />}

      <Dialog title={t('workflow.dialogTitle')} open={expanded} onClose={() => setExpanded(false)} width={1200}>
        <div className={cn(DETAIL_CARD_CLASS, 'w-full')}>
          <div className="relative w-full" style={{ height: 'min(68vh, 720px)' }}>
            <ReactFlowProvider>
              <Graph state={state} selected={selected} onSelect={setSelected} inline={false} />
            </ReactFlowProvider>
          </div>
          {selectedNode !== undefined && <NodeInspector node={selectedNode} />}
        </div>
      </Dialog>
    </div>
  )
}
