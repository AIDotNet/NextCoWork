/**
 * 声明式工具卡片的渲染器 —— 把 `ToolCard` 的白名单原语摆成版式。
 *
 * ★ 跑在**可信主渲染进程**里(不是插件 iframe),所以每一条都只接受已消毒的数据:
 * `link` 只走 `openExternal`(绝不渲染裸 `<a href>`,否则 `javascript:`/`file:`/顶层导航
 * 会命中宿主窗口);`image` 的 dataRef 已被 `sanitizeToolCard` 限定为 `data:`/`ncw://`。
 *
 * ★ `frame` 卡片不由这里画 —— 它交给 `PluginCardFrame`(插件自己的 iframe),
 * pluginId/path 经 externalName 反查(见 `frameCardTarget`)。
 *
 * ★ `widget` 卡片**同理不由这里画**(内置可视化,交给 `WidgetFrame` 的沙箱 iframe)。
 * 两者都不是"声明式原语",所以这里对它们各自只做一次转发;`switch` 里那些
 * 分支才是这个文件的主体。少了这层转发的话,一张 widget 卡片会掉进下面的
 * `card.blocks` —— 而那个字段在 widget 卡片上根本不存在。
 */
import { useCallback, useMemo, useState, type ReactNode } from 'react'
import type { CardBlock, CardTone, ToolCard } from '../../../../shared/agent/tool-card'
import { openExternal } from '../../services/app'
import { invoke } from '../../services/ipc'
import { cn } from '../../lib/cn'
import { useI18n, type TranslationKey } from '../../i18n'
import { ProgressBar } from '../../components/ui/ProgressBar'
import { Button } from '../../components/arc/button/button'
import { Dialog } from '../../components/ui/Dialog'
import { AgentMarkdown } from '../../components/markdown'
import { PluginCardFrame } from '../../shell/PluginCardFrame'
import { frameCardTarget, pluginIdForTool, usePluginsStore } from '../../stores/plugins'
import { WidgetFrame } from './WidgetFrame'
import { inferWidgetFormat } from '../../../../shared/domain/widget'

/** 卡片按钮动作的回传句柄。工具已结束 / 反查不到插件时为 undefined —— 按钮渲染成禁用。 */
type CardActionSink = ((actionId: string, value?: unknown) => void) | undefined

/** 危险按钮点下去时先问一句。`titleKey` 是插件的 l10n key,由外层 `t()`。 */
type ConfirmSink = (titleKey: string, proceed: () => void) => void

const TONE_TEXT: Record<CardTone, string> = {
  neutral: 'text-fg-faint',
  info: 'text-accent',
  ok: 'text-accent',
  warn: 'text-warning',
  danger: 'text-danger'
}

function StatusBadge({ label, tone }: { label: string; tone?: CardTone }): ReactNode {
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1 rounded-pill bg-tint px-2 py-0.5 text-[11px]',
        TONE_TEXT[tone ?? 'neutral']
      )}
    >
      <span className="h-1.5 w-1.5 rounded-full bg-current" />
      {label}
    </span>
  )
}

function Block({ block, onAction, onConfirm }: { block: CardBlock; onAction: CardActionSink; onConfirm: ConfirmSink }): ReactNode {
  switch (block.type) {
    case 'keyValue':
      return (
        <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-[12.5px]">
          {block.rows.map((row, i) => (
            <div key={i} className="contents">
              <dt className="text-fg-faint">{row.label}</dt>
              <dd className={cn('min-w-0 break-words', TONE_TEXT[row.tone ?? 'neutral'], row.tone === undefined && 'text-fg')}>
                {row.value}
              </dd>
            </div>
          ))}
        </dl>
      )
    case 'table':
      return (
        <div className="overflow-x-auto">
          <table className="w-full border-collapse text-left text-[12px]">
            <thead>
              <tr>
                {block.columns.map((col, i) => (
                  <th key={i} className="border-b border-line/60 px-2 py-1 font-medium text-fg-faint">{col}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {block.rows.map((row, r) => (
                <tr key={r}>
                  {block.columns.map((_col, c) => (
                    <td key={c} className="border-b border-line/30 px-2 py-1 text-fg">{row[c] ?? ''}</td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )
    case 'status':
      return <StatusBadge label={block.label} tone={block.tone} />
    case 'text':
      return <p className="selectable whitespace-pre-wrap text-[12.5px] leading-relaxed text-fg">{block.value}</p>
    case 'code':
      return (
        <pre className="selectable overflow-x-auto rounded-md bg-tint px-3 py-2 font-mono text-[11.5px] leading-relaxed text-fg">
          {block.value}
        </pre>
      )
    case 'image':
      // dataRef 已被限定为 data:/ncw://,两者都能安全直显;不走 MessageImage 是因为
      // 卡片要的是内联缩略,不需要 lightbox,且这里 data: 也要能画。
      return (
        <img
          src={block.dataRef}
          alt={block.alt ?? ''}
          className="max-h-64 max-w-full rounded-md border border-line/50 object-contain"
        />
      )
    case 'progress':
      return <ProgressBar value={block.fraction} label={block.label ?? ''} />
    case 'link':
      return (
        <button
          type="button"
          onClick={() => void openExternal(block.href)}
          className="inline-flex max-w-full items-center gap-1 truncate text-[12.5px] text-accent hover:underline"
          title={block.href}
        >
          {block.label ?? block.href}
        </button>
      )
    case 'button':
      // 工具已结束(onAction 为空)→ 渲染禁用态:点了也送不到,别给可点的假象。
      return (
        <button
          type="button"
          disabled={onAction === undefined}
          onClick={() => {
            /*
              ★ 危险动作先问一句。问句是**插件给的 l10n key**,由宿主 `t()` 出来 ——
              而且走宿主自己的 `Dialog`,不是 `window.confirm`:后者不跟随主题、
              不跟随语言,而且会把整个渲染进程同步卡住。
            */
            if (block.confirm !== undefined) {
              onConfirm(block.confirm.titleKey, () => onAction?.(block.actionId))
              return
            }
            onAction?.(block.actionId)
          }}
          className={cn(
            'inline-flex items-center gap-1 rounded-md border border-line/60 px-2.5 py-1 text-[12px] transition-colors',
            onAction === undefined
              ? 'cursor-not-allowed text-fg-faint opacity-60'
              : cn('hover:bg-tint-hover/60', TONE_TEXT[block.tone ?? 'neutral'], block.tone === undefined && 'text-fg')
          )}
        >
          {block.label}
        </button>
      )
    case 'markdown':
      /*
        ★ 走**宿主的**受信 markdown 渲染器,不自己拼 HTML:它已经处理好了
        「链接路由到 openExternal、不执行裸 HTML」那两件事
        (`WorkspaceMarkdownProvider`)。自己拼一份等于把那两条重新赌一次。
      */
      return <AgentMarkdown content={block.value} variant="compact" className="selectable text-[12.5px] leading-relaxed text-fg" />
    case 'list':
      return (
        <ul className="flex flex-col gap-1 text-[12.5px]">
          {block.items.map((item, i) => (
            <li key={i} className="flex items-baseline gap-2">
              <span className={cn('h-1 w-1 shrink-0 translate-y-[-2px] rounded-full bg-current', TONE_TEXT[item.tone ?? 'neutral'])} />
              <span className={cn('min-w-0 break-words', TONE_TEXT[item.tone ?? 'neutral'], item.tone === undefined && 'text-fg')}>
                {item.label}
              </span>
              {item.hint !== undefined && <span className="shrink-0 text-[11px] text-fg-faint">{item.hint}</span>}
            </li>
          ))}
        </ul>
      )
    case 'metric':
      return (
        <div className="flex items-baseline gap-2">
          <span className={cn('text-[18px] font-medium tabular-nums', TONE_TEXT[block.tone ?? 'neutral'], block.tone === undefined && 'text-fg')}>
            {block.value}
          </span>
          <span className="text-[12px] text-fg-faint">{block.label}</span>
          {block.delta !== undefined && <span className="text-[11px] text-fg-faint tabular-nums">{block.delta}</span>}
        </div>
      )
    case 'divider':
      return <hr className="border-0 border-t border-line/40" />
    default:
      // 未知块(旧宿主遇到新原语):静默跳过,别把整张卡打崩。
      return null
  }
}

/** frame 卡片:反查挂载目标 → PluginCardFrame;查不到(插件已卸载/禁用)→ 温和提示。 */
function FrameCard({
  viewType,
  data,
  toolName,
  callId
}: {
  viewType: string
  data: unknown
  toolName: string | undefined
  callId: string | undefined
}): ReactNode {
  const { t } = useI18n()
  const catalog = usePluginsStore((s) => s.catalog)
  const target = useMemo(
    () => (toolName === undefined ? undefined : frameCardTarget(catalog, toolName, viewType)),
    [catalog, toolName, viewType]
  )
  if (target === undefined) {
    return <p className="text-[12px] text-fg-faint">{t('chat.tool.card.unavailable')}</p>
  }
  return (
    <PluginCardFrame
      pluginId={target.pluginId}
      path={target.path}
      viewType={viewType}
      callId={callId}
      data={data}
      label={viewType}
    />
  )
}

export function CardRenderer({
  card,
  toolName,
  callId
}: {
  card: ToolCard
  /** 工具的 externalName —— frame 卡片反查 pluginId 用 */
  toolName?: string
  callId?: string
}): ReactNode {
  const catalog = usePluginsStore((s) => s.catalog)
  // 按钮动作的回传句柄:反查得到 pluginId、且有 callId 才可点。工具结束后 catalog 里
  // 仍能查到 pluginId,但主进程侧 `deliverCardAction` 的 liveToolEmits 门会把迟到的
  // 点击挡掉 —— 这里给不给 sink 只影响「看起来能不能点」,真正的安全在主进程。
  const onAction = useMemo<CardActionSink>(() => {
    if (toolName === undefined || callId === undefined) return undefined
    const pluginId = pluginIdForTool(catalog, toolName)
    if (pluginId === undefined) return undefined
    return (actionId, value) => {
      void invoke('plugins:cardAction', { pluginId, callId, actionId, value })
    }
  }, [catalog, toolName, callId])

  const { t } = useI18n()
  /*
    等确认的那个动作。`pluginId` 一起存下来:确认框上的问句是**插件的** l10n key,
    拼前缀要它 —— 而等用户点确认时,这张卡可能已经因为别的原因重渲过了。
  */
  const [pending, setPending] = useState<{ titleKey: string; pluginId: string; proceed: () => void } | null>(null)
  const askConfirm = useCallback<ConfirmSink>(
    (titleKey, proceed) => {
      const pluginId = toolName === undefined ? undefined : pluginIdForTool(catalog, toolName)
      // 反查不到插件(已卸载)就不问了:那个动作本来也送不到
      if (pluginId === undefined) return
      setPending({ titleKey, pluginId, proceed })
    },
    [catalog, toolName]
  )

  if (card.kind === 'frame') {
    return <FrameCard viewType={card.viewType} data={card.data} toolName={toolName} callId={callId} />
  }
  if (card.kind === 'widget') {
    /*
      `final` 恒为 true:能走到这里的卡片要么是刚跑完的工具结果、要么是从转录里
      恢复出来的成品,两种情况下 `code` 都是完整的,脚本该执行了。
      生成期那一段走的是 `WidgetDetail`(shape 分支),那里的 `final` 是 false ——
      见 `WidgetDetail.tsx` 文件头那张分工表。
    */
    const format = card.format ?? inferWidgetFormat(card.code)
    return (
      <WidgetFrame
        code={format === 'tsx' ? card.compiled ?? '' : card.code}
        final
        title={card.title}
        format={format}
        source={card.code}
        {...(callId === undefined ? {} : { callId })}
        {...(card.interactive === true ? { interactive: true } : {})}
        {...(card.submitted === undefined ? {} : { submitted: true })}
        {...(card.actions === undefined ? {} : { actions: card.actions })}
      />
    )
  }
  /*
    `visualize_update_widget` 推的数据不单独画:它由同名的那张 widget 自己读
    (`WidgetFrame` 的 `data`)。这里画出来只会是一块与图重复的 JSON。
  */
  if (card.kind === 'widget_data') return null
  return (
    <div className="flex flex-col gap-2">
      {card.blocks.map((block, i) => (
        <Block key={i} block={block} onAction={onAction} onConfirm={askConfirm} />
      ))}
      {/*
        危险按钮的那一问。放在卡片这一层而不是每个按钮里:同一张卡上可能有好几个
        危险动作,而同时弹两个确认框用户分不清哪个是哪个。
      */}
      <Dialog
        title={pending === null ? '' : t(`plugin.${pending.pluginId}.${pending.titleKey.replace(/^%|%$/g, '')}` as TranslationKey)}
        open={pending !== null}
        onClose={() => setPending(null)}
        footer={
          <>
            <Button type="button" variant="secondary" onClick={() => setPending(null)}>{t('pluginAsk.cancel')}</Button>
            <Button
              type="button"
              variant="danger"
              onClick={() => {
                pending?.proceed()
                setPending(null)
              }}
            >
              {t('pluginAsk.confirm')}
            </Button>
          </>
        }
      >
        {/* 正文留空:问句本身已经在标题上,再重复一遍只会让这个框变高。 */}
        <span />
      </Dialog>
    </div>
  )
}
