/**
 * 插件工具 → `ToolRegistry` 的桥。
 *
 * ## 这一层为什么必须存在
 *
 * 插件注册的工具最终要和内置工具、MCP 工具**挤在同一张表里**下发给模型 ——
 * 模型没有「这是插件工具」这个概念,它只看见一个名字和一段描述。所以这里要做
 * 三件事,一件都不能省:
 *
 * 1. **加前缀**。`plugin__<publisher>_<name>__<tool>`,和 MCP 的 `mcp__server__tool`
 *    同一个形状。不加的话,两个插件各注册一个 `search`,后注册的会**静默顶掉**
 *    先注册的(注册表按 internalId 幂等替换)。
 * 2. **描述消毒**。描述直接进系统提示词,而它来自第三方 —— 和 MCP 工具同级别的
 *    不可信输入。消毒由 `ToolRegistry.register` 统一做,这里只负责别绕开它。
 * 3. **`source` 带 pluginId**。插件崩了/被禁用时要 `unregisterBySource` 成批下线,
 *    而且**下线必须先于销毁**(同 `mcp/manager.ts` 的次序)。
 */
import { toolFail, toolOk } from '../../shared/agent/tool'
import type { JsonSchema } from '../../shared/agent/tool'
import { sanitizeToolCard } from '../../shared/agent/tool-card'
import type { ToolRegistration } from '../kernel/tool/registry'

/** 插件在 `tools.register` 里报上来的那一份声明。 */
export interface PluginToolDeclaration {
  name: string
  description: string
  inputSchema: unknown
  readOnly: boolean
  destructive: boolean
  needsNetwork: boolean
  /** 交互式工具:会推带按钮的实时卡片并挂起等用户点 —— 宿主放宽超时,见 PLUGIN_TIMEOUT */
  interactive?: boolean
}

/** 工具名的形状 —— 和 `EXTERNAL_NAME_RE` 兼容,注册表还会再截断去重一次。 */
const TOOL_NAME_RE = /^[A-Za-z0-9_-]{1,48}$/

export function isValidPluginToolName(name: string): boolean {
  return TOOL_NAME_RE.test(name)
}

/**
 * 内部 id。
 *
 * ★ 用 `__` 分隔而不是 `.`:`.` 在 `EXTERNAL_NAME_RE`(`^[a-zA-Z0-9_-]{1,64}$`)
 * 里不合法,而上游对工具名的这条约束是硬的 —— 名字不合法会换来一个
 * 什么都没说清楚的 400,整个请求被拒,而不是只丢掉这一个工具。
 */
export function pluginToolId(pluginId: string, name: string): string {
  return `plugin__${pluginId.replaceAll('.', '_').replaceAll('-', '_')}__${name}`
}

/**
 * 一条声明 → 一个可注册的工具。
 *
 * ★ **不走 `defineTool`。** 那个包装器会把 zod schema 转成 JSON Schema 并在
 * 调用前 `safeParse` 一次 —— 而插件给的是**已经是 JSON Schema 的东西**。
 * 反向转换(JSON Schema → zod)意味着每一条没实现对的关键字都变成一次
 * 「模型传了合法参数却被工具拒掉」,而错误信息会指向我们的转换器。
 * 所以这里原样下发它的 schema,参数校验交给插件自己 —— 它本来就更清楚。
 *
 * `execute` 通过 `invoke` 回调打到插件里;这里不知道插件跑在哪,那是
 * `PluginRuntime` 的事。
 */
export function toolRegistrationFor(
  pluginId: string,
  declaration: PluginToolDeclaration,
  invoke: (
    name: string,
    input: unknown,
    callId: string,
    signal: AbortSignal,
    /** 运行中推进度/实时卡片的回调 —— 转发到内核 `ctx.emit`,见 manager 的 liveToolEmits */
    emit: (progress: { callId: string; message: string; fraction?: number }) => void,
    /** 文档 RPC 的作用域来源；不可由插件输入伪造。 */
    workspaceId?: string
  ) => Promise<unknown>,
  /** 该插件 `contributes.cardViews` 声明的 viewType —— frame 卡片只能指向其中之一。 */
  cardViewTypes: ReadonlySet<string> = new Set()
): ToolRegistration {
  return {
    internalId: pluginToolId(pluginId, declaration.name),
    description: declaration.description,
    inputSchema: normalizeSchema(declaration.inputSchema),
    readOnly: declaration.readOnly,
    destructive: declaration.destructive,
    needsNetwork: declaration.needsNetwork,
    source: { kind: 'plugin', pluginId },
    async execute(input, ctx) {
      try {
        const result = await invoke(declaration.name, input, ctx.callId, ctx.signal, ctx.emit, ctx.workspaceId)
        return normalizeToolResult(result, cardViewTypes)
      } catch (error) {
        /*
          ★ 中断要**原样抛**,不能伪装成工具失败(`kernel/tool/define.ts:74`
          的约定)。伪装的后果是:用户点了停止,模型却收到一条「工具失败了」
          的结果,于是它会重试 —— 而用户看着它在自己已经叫停之后继续动。
        */
        if (ctx.signal.aborted) throw error
        return toolFail(`plugin tool failed: ${(error as Error).message}`)
      }
    }
  }
}

/** 认不出的 schema 退化成一个空对象 —— 上游拒绝没有 `type` 的 schema。 */
function normalizeSchema(raw: unknown): JsonSchema {
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) return { type: 'object', properties: {} }
  const record = raw as JsonSchema
  return record.type === undefined ? { ...record, type: 'object' } : record
}

/**
 * 插件回来的结果 → 工具输出。
 *
 * 认不出的形状退化成「把它当文本」,而不是报错:一个返回了奇怪东西的插件
 * 工具,对模型来说应该是「这次调用没给出有用的信息」,而不是一次系统故障。
 *
 * ★ 可选的 `card` 是**只走 UI 轨**的:`sanitizeToolCard` 是不可信输入的收口
 * (白名单原语、独立字节预算、image/link scheme、frame viewType 必须已声明)。
 * 非法 card 直接丢弃,只保留文本 —— 卡片是锦上添花,不该拖垮工具结果本身。
 */
function normalizeToolResult(raw: unknown, cardViewTypes: ReadonlySet<string>): ReturnType<typeof toolOk> {
  if (typeof raw === 'string') return toolOk(raw)
  if (raw === null || raw === undefined) return toolOk('')
  const record = raw as { content?: unknown; isError?: unknown; card?: unknown }
  const card = sanitizeToolCard(record.card, cardViewTypes)
  const withCard = (result: ReturnType<typeof toolOk>): ReturnType<typeof toolOk> =>
    card === undefined ? result : { ...result, output: { ...result.output, card } }
  if (Array.isArray(record.content)) {
    const text = record.content
      .map((part) => (typeof part === 'string' ? part : typeof (part as { text?: unknown }).text === 'string' ? (part as { text: string }).text : ''))
      .filter((part) => part !== '')
      .join('\n')
    return withCard(record.isError === true ? toolFail(text) : toolOk(text))
  }
  // 有 card 但 content 不是数组时,别把整个对象 JSON.stringify 塞进文本(那会把
  // card 也塞进模型可见的 content)。有 card → 文本留空,让卡片说话;没有 → 老行为。
  if (card !== undefined) return withCard(toolOk(''))
  return toolOk(JSON.stringify(raw))
}
