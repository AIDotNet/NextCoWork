/**
 * 工具结果的**自定义卡片** —— 只走 UI 轨,永不下发给模型(方案 §4.1 的双轨)。
 *
 * ## 为什么它挂在 `ToolOutput` 上而不是 `ToolResult` 顶层
 *
 * `ContentPart.tool_result.output` 与 `transcript` 的 `ToolCallState.output` 都是 `ToolOutput`。
 * 挂在这里,持久化(`repo.ts` 的 `JSON.stringify(parts)`)、重开对话的恢复
 * (`transcript.ts` 的 `writeToolPart` 直接用 `part.output`)、UI 访问(`call.output.card`)
 * **一条新管道都不用加**。三个上游编码器(anthropic / openai-*)都只读 `output.content`,
 * 于是 card 天然被 strip,不会进系统提示词。
 *
 * ## 两种卡片
 *
 * - `declarative`:一组**白名单原语**,由**可信渲染器**解释(插件代码不进渲染路径)。
 *   覆盖 80% 场景:任务卡、查询结果、状态。安全、轻量。
 * - `frame`:插件用自己的 `ncw-plugin://` 页面渲染任意 UI(`viewType` 必须在
 *   `contributes.cardViews` 里声明)。表现力换来「每张富卡一个 iframe」的成本,
 *   所以只在展开态懒挂载。
 *
 * ## 不可信输入
 *
 * card 来自第三方插件的 RPC 返回值。`sanitizeToolCard` 是唯一的收口:白名单原语、
 * 独立字节预算(与 `MAX_TOOL_OUTPUT_CHARS` 分开算)、image 只认 `data:` / `ncw://`、
 * link 只认 https、frame 的 viewType 必须在该插件声明内。**未知的块降级、越界的整体丢弃**,
 * 而不是让一个畸形 card 把卡片渲染打崩。
 */

import type { WidgetAction, WidgetFormat } from '../domain/widget'

/** 语义色调 —— 渲染层映射到主题 token,插件不能直接给颜色。 */
export type CardTone = 'neutral' | 'info' | 'ok' | 'warn' | 'danger'

export type CardBlock =
  | { type: 'keyValue'; rows: Array<{ label: string; value: string; tone?: CardTone }> }
  | { type: 'table'; columns: string[]; rows: string[][] }
  | { type: 'status'; label: string; tone?: CardTone }
  | { type: 'text'; value: string }
  | { type: 'code'; value: string; language?: string }
  | { type: 'image'; dataRef: string; alt?: string }
  | { type: 'progress'; fraction: number; label?: string }
  | { type: 'link'; href: string; label?: string }
  /**
   * 一段 markdown,由**宿主的**受信渲染器解释(`components/markdown/**`)。
   * 链接照 `link` 块的规矩路由到 openExternal,不允许裸 HTML。
   *
   * 需求:`text` 只能给一整段没有结构的字,而插件最常要表达的恰恰是
   * 「几行要点 + 一个链接」。没有它,作者只能把 markdown 塞进 `text`,
   * 然后在界面上看到一堆没被解释的 `**` 和 `-`。
   */
  | { type: 'markdown'; value: string }
  /** 一列要点。与 `table` 的区别是它**不承诺列对齐** —— 一行一件事,可带色调。 */
  | { type: 'list'; items: Array<{ label: string; tone?: CardTone; hint?: string }> }
  /**
   * 一个数值格:大字 + 说明 + 可选变化量。仪表类卡片用它,而不是把数字硬塞进
   * `keyValue` —— 那一栏的字号是给「标签:值」准备的,塞进去读不出重点。
   */
  | { type: 'metric'; label: string; value: string; delta?: string; tone?: CardTone }
  /** 分隔线。纯视觉,没有内容。 */
  | { type: 'divider' }
  /**
   * 交互按钮(第 2 层)。点了走反向通道 `plugins:cardAction` 回到**仍在运行**的工具。
   * 工具已结束(liveToolEmits 已撤)时点它无效果 —— 安全,不会打到别处。
   *
   * `confirm` = 点下去先要一次确认(危险动作用)。文案是 l10n key,不是句子。
   */
  | { type: 'button'; actionId: string; label: string; tone?: CardTone; confirm?: { titleKey: string } }

export type ToolCard =
  | { kind: 'declarative'; blocks: CardBlock[] }
  | { kind: 'frame'; viewType: string; data: unknown }
  /**
   * 内置 `visualize_show_widget` 画的 HTML/SVG —— 卡片自带代码,由宿主自己的
   * 沙箱 iframe 渲染(`renderer/views/chat/WidgetFrame.tsx`,经 `ncw-widget://` 出)。
   *
   * ★ **为什么不是一张 `frame`。** `frame` 的 `viewType` 要经插件清单
   * (`frameCardTarget`)反查出 `pluginId` 与包内路径,内置工具没有 pluginId,
   * 那条路根本走不通;而且 frame 的数据是"单向推入一份只读快照",widget 要的是
   * **边生成边推进的半截 HTML** + 流结束才执行脚本 —— 两种生命周期。
   *
   * ★ **代码随卡片走。** 卡片自带 `code`,于是重开对话时不必再向主进程要回
   * 任何东西(对比 frame:每次展开都要重新加载插件的那个页面)。
   *
   * ★ **插件伪造不出这一种。** `sanitizeToolCard` 认不出的 `kind` 一律返回
   * `undefined`(见那里),而它是插件侧唯一的收口 —— 所以这条通道**只有内置
   * 工具能走**。这条不变式就是"任意 HTML 不进可信渲染进程"的全部保证,
   * 改动 `sanitizeToolCard` 前先读一遍这里。
   *
   * ★ 它**不受 `MAX_CARD_BYTES` 管**:那笔预算是给插件卡的,而这里的一份
   * widget 上限是 `visualize.ts` 里的 128K。要给插件放开这条路,得先给
   * 消毒器写一条自己的策略(白名单域名之类),而不是把 `kind` 加进白名单了事。
   */
  | {
      kind: 'widget'
      title: string
      /** 模型写的源码(html / svg / tsx 原文)。复制源码、`visualize_edit_widget` 都读它 */
      code: string
      /** 缺省 = 按 `code` 开头推断 html/svg —— 旧转录里的卡片没有这个字段 */
      format?: WidgetFormat
      /** `tsx` 才有:主进程用 sucrase 编好的 CJS。iframe 只执行它,不在页面里编译 */
      compiled?: string
      /** 交互模式:工具挂起等 widget 里 `submit()`,提交值作为工具结果回给模型 */
      interactive?: boolean
      /** 交互模式下已提交的值(JSON 文本)。有它 = 这张卡已经交过卷 */
      submitted?: string
      /** 预先声明、widget 可按 id 触发的动作 */
      actions?: WidgetAction[]
    }
  /**
   * `visualize_update_widget` 推给某张 widget 的一份数据(按 `title` 认领)。
   * 同 `widget`:插件伪造不出这一种(`sanitizeToolCard` 不认)。
   */
  | { kind: 'widget_data'; title: string; data: unknown }

// ─────────────────────────── 校验上限 ───────────────────────────

/** card 的独立字节预算 —— 与 content 那笔账分开。防插件借 card 绕过输出配额撑爆持久化。 */
export const MAX_CARD_BYTES = 16 * 1024
/** 单个字符串字段的字符上限(label / value / cell / text / code)。 */
const MAX_FIELD_CHARS = 4 * 1024
/** declarative 最多几块;table 最多几行几列。都是防「一张卡塞下整个数据集」。 */
const MAX_BLOCKS = 32
const MAX_TABLE_ROWS = 100
const MAX_TABLE_COLS = 12

const TONES: ReadonlySet<string> = new Set<CardTone>(['neutral', 'info', 'ok', 'warn', 'danger'])

function str(v: unknown, max = MAX_FIELD_CHARS): string | undefined {
  return typeof v === 'string' ? v.slice(0, max) : undefined
}

function tone(v: unknown): CardTone | undefined {
  return typeof v === 'string' && TONES.has(v) ? (v as CardTone) : undefined
}

/**
 * 一个原语块的消毒。认不出 / 关键字段缺失 → `null`(调用方丢弃这一块,不影响别的)。
 *
 * ★ 未知 `type` 返回 `null` 而不是抛错 —— 前向兼容:未来插件用新块打到旧宿主时,
 * 那一块优雅消失,card 其余部分照常渲染。
 */
function sanitizeBlock(raw: unknown): CardBlock | null {
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) return null
  const b = raw as Record<string, unknown>
  switch (b.type) {
    case 'keyValue': {
      const rows = Array.isArray(b.rows) ? b.rows : []
      const out: Array<{ label: string; value: string; tone?: CardTone }> = []
      for (const row of rows.slice(0, MAX_TABLE_ROWS)) {
        if (row === null || typeof row !== 'object') continue
        const r = row as Record<string, unknown>
        const label = str(r.label)
        const value = str(r.value)
        if (label === undefined || value === undefined) continue
        const t = tone(r.tone)
        out.push({ label, value, ...(t === undefined ? {} : { tone: t }) })
      }
      return out.length === 0 ? null : { type: 'keyValue', rows: out }
    }
    case 'table': {
      const columns = (Array.isArray(b.columns) ? b.columns : [])
        .slice(0, MAX_TABLE_COLS)
        .map((c) => str(c) ?? '')
      if (columns.length === 0) return null
      const rows = (Array.isArray(b.rows) ? b.rows : [])
        .slice(0, MAX_TABLE_ROWS)
        .map((row) => (Array.isArray(row) ? row : []).slice(0, columns.length).map((c) => str(c) ?? ''))
      return { type: 'table', columns, rows }
    }
    case 'status': {
      const label = str(b.label)
      if (label === undefined) return null
      const t = tone(b.tone)
      return { type: 'status', label, ...(t === undefined ? {} : { tone: t }) }
    }
    case 'text': {
      const value = str(b.value)
      return value === undefined ? null : { type: 'text', value }
    }
    case 'code': {
      const value = str(b.value)
      if (value === undefined) return null
      const language = str(b.language, 32)
      return { type: 'code', value, ...(language === undefined ? {} : { language }) }
    }
    case 'image': {
      // ★ 只认受管附件(ncw://)与内联 data:。别的 scheme(http/file/javascript)一律丢。
      const dataRef = str(b.dataRef, MAX_CARD_BYTES)
      if (dataRef === undefined || !(dataRef.startsWith('ncw://') || dataRef.startsWith('data:'))) return null
      const alt = str(b.alt, 256)
      return { type: 'image', dataRef, ...(alt === undefined ? {} : { alt }) }
    }
    case 'progress': {
      if (typeof b.fraction !== 'number' || !Number.isFinite(b.fraction)) return null
      const fraction = Math.max(0, Math.min(1, b.fraction))
      const label = str(b.label, 256)
      return { type: 'progress', fraction, ...(label === undefined ? {} : { label }) }
    }
    case 'link': {
      // ★ 只认 https。渲染层还会把它路由到 openExternal,而不是渲染裸 <a href>。
      const href = str(b.href, 2048)
      if (href === undefined || !href.startsWith('https://')) return null
      const label = str(b.label, 256)
      return { type: 'link', href, ...(label === undefined ? {} : { label }) }
    }
    case 'button': {
      const actionId = str(b.actionId, 128)
      const label = str(b.label, 256)
      if (actionId === undefined || actionId === '' || label === undefined) return null
      const t = tone(b.tone)
      /*
        ★ `confirm` 只认 **key 的形状**(不含空格、不太长),不认句子:
        它会被渲染成一个模态上的问句,而主进程侧一律不产出用户可见的裸文本。
        查不到的 key 显示成 key 本身 —— 看得见,才改得掉。
      */
      const confirmRaw = b.confirm
      const confirmKey = confirmRaw !== null && typeof confirmRaw === 'object' && !Array.isArray(confirmRaw)
        ? str((confirmRaw as Record<string, unknown>).titleKey, 128)
        : undefined
      const confirm = confirmKey !== undefined && confirmKey !== '' && !/\s/.test(confirmKey)
        ? { titleKey: confirmKey }
        : undefined
      return {
        type: 'button',
        actionId,
        label,
        ...(t === undefined ? {} : { tone: t }),
        ...(confirm === undefined ? {} : { confirm })
      }
    }
    case 'markdown': {
      const value = str(b.value)
      return value === undefined ? null : { type: 'markdown', value }
    }
    case 'list': {
      const items = Array.isArray(b.items) ? b.items : []
      const out: Array<{ label: string; tone?: CardTone; hint?: string }> = []
      for (const item of items.slice(0, MAX_TABLE_ROWS)) {
        if (item === null || typeof item !== 'object') continue
        const i = item as Record<string, unknown>
        const label = str(i.label)
        if (label === undefined) continue
        const t = tone(i.tone)
        const hint = str(i.hint, 256)
        out.push({ label, ...(t === undefined ? {} : { tone: t }), ...(hint === undefined ? {} : { hint }) })
      }
      return out.length === 0 ? null : { type: 'list', items: out }
    }
    case 'metric': {
      const label = str(b.label, 256)
      const value = str(b.value, 256)
      if (label === undefined || value === undefined) return null
      const delta = str(b.delta, 64)
      const t = tone(b.tone)
      return {
        type: 'metric',
        label,
        value,
        ...(delta === undefined ? {} : { delta }),
        ...(t === undefined ? {} : { tone: t })
      }
    }
    case 'divider':
      return { type: 'divider' }
    default:
      return null
  }
}

/**
 * 插件返回的 card → 一个可信的 `ToolCard`,或 `undefined`(不合法 / 超预算 → 无卡片,退纯文本)。
 *
 * @param allowedFrameViewTypes 该插件 `contributes.cardViews` 声明的 viewType 集合;
 *   frame 卡片的 viewType 必须在其中,否则丢弃(防止指向未声明或别的插件的页面)。
 */
export function sanitizeToolCard(raw: unknown, allowedFrameViewTypes: ReadonlySet<string>): ToolCard | undefined {
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) return undefined
  const c = raw as Record<string, unknown>
  let card: ToolCard | undefined
  if (c.kind === 'declarative') {
    const blocks = (Array.isArray(c.blocks) ? c.blocks : [])
      .slice(0, MAX_BLOCKS)
      .map(sanitizeBlock)
      .filter((b): b is CardBlock => b !== null)
    if (blocks.length === 0) return undefined
    card = { kind: 'declarative', blocks }
  } else if (c.kind === 'frame') {
    const viewType = str(c.viewType, 128)
    if (viewType === undefined || !allowedFrameViewTypes.has(viewType)) return undefined
    card = { kind: 'frame', viewType, data: c.data ?? null }
  } else {
    return undefined
  }
  // 独立字节预算:序列化后超了就整体丢弃(退纯文本比截半张卡诚实)。
  try {
    if (JSON.stringify(card).length > MAX_CARD_BYTES) return undefined
  } catch {
    return undefined // 含循环引用等不可序列化数据(尤其 frame.data)
  }
  return card
}
