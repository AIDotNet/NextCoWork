/**
 * MCP tool → `ToolRegistration`。**本目录唯一的不可信输入收口**。
 *
 * 一台 MCP 服务器交回来的 `name` / `description` / `inputSchema` / `annotations`
 * 全部是别人写的字符串,而它们会进入系统提示词 —— 这正是工具投毒的入口(方案 §4.4)。
 * 所以这个文件的每一处判断都遵循同一条方向:
 *
 * **凡是「服务器说了算」会削弱一道防线的地方,一律不采信服务器的说法。**
 *
 * 具体三处,每一处都朝安全那边兜底:
 *
 * | 字段 | 服务器没说时 | 为什么是这个方向 |
 * |---|---|---|
 * | `readOnlyHint`    | 始终 `readOnly = false`    | 服务器不能靠自报只读绕过用户的审批档位 |
 * | `destructiveHint` | 始终 `destructive = true`  | 风险裁决来自用户规则，不来自第三方 hint |
 * | `needsNetwork`    | **完全不读服务器**    | 由传输方式推出来,见下 |
 *
 * ★ `needsNetwork` 刻意不看 annotations。让服务器自报「我不联网」的话,
 * 用户那颗联网开关就被第三方描述关掉了 —— 判定权必须留在我们这边。
 * (注:输入框「联网搜索」开关现在只管网页搜索与抓取,见 `permission-gate.ts` 的
 * `NETWORK_SWITCH_TOOLS` —— MCP 工具不再受它控制,这个字段目前只作事实描述;
 * 推导规则仍按下面这样保留,将来按出网加管控时它必须是可信的。)
 * 用的是**我们库里存的传输方式**:
 *
 * - `sse` / `streamable-http` → `true`。这一次调用本身就跨网络到远端,定义上成立。
 * - `stdio` → `false`。调用打到的是一个本地子进程。它自己会不会出网我们不可能知道,
 *   而把所有 stdio 服务器的工具都算成联网,那颗药丸就变成了「关掉 MCP」——
 *   和它标签上写的不是一件事。
 */
import type { Client } from '@modelcontextprotocol/sdk/client/index.js'
import type { JsonSchema, ToolResult } from '../../shared/agent/tool'
import { toolFail, toolOk } from '../../shared/agent/tool'
import type { McpServerConfig } from '../../shared/domain/mcp'
import { mcpInternalId } from '../../shared/domain/mcp'
import type { ToolContext, ToolRegistration } from '../kernel/tool/registry'
import { isAbortError, withDeadline } from '../kernel/abort'

/**
 * 一次 MCP 工具调用的墙钟上限。
 *
 * 比 `manager.ts` 的连接超时(30s)宽 —— 工具本身就可能慢(跑一遍测试、建一个 PR),
 * 拿连接握手的标准去卡执行,会把正常的慢工具误杀成失败。60s 是「慢」与「死」之间那条线。
 */
const CALL_TOOL_TIMEOUT_MS = 60_000

/** `client.listTools()` 里一个元素的形状,只取我们真的会读的字段。 */
export interface McpToolDescriptor {
  name: string
  description?: string
  inputSchema?: unknown
  annotations?: {
    readOnlyHint?: boolean
    destructiveHint?: boolean
  }
}

/**
 * 工具名的白名单。
 *
 * ★ 这一关**必须在拼 internalId 之前**过。`naming.ts` 的 `sanitizeToolName`
 * 会把非法字符洗掉再算 externalName,所以一个叫 `../../etc/passwd` 的工具
 * 光看模型那侧是安全的 —— 但它的 `internalId` 会带着那串字符,
 * 而 internalId 会进转录、进日志、进 `allowedTools` 的匹配。
 * 在入口拒掉比在三个下游各自防一遍便宜。
 */
const TOOL_NAME_RE = /^[a-zA-Z0-9_.-]{1,128}$/

/** 一次注册失败的原因。列表页要照实显示「这台服务器有 2 个工具没接上」。 */
export interface RejectedTool {
  name: string
  reason: string
}

/**
 * MCP 的 content 块拼成一段文本。
 *
 * `ToolOutput.content` 只有文本一个字段(`shared/agent/message.ts`),
 * 所以图片/音频/资源块**照实说明自己是什么,而不是被静默丢掉**。
 * 丢掉的症状是模型看到一个空结果,然后换个参数把同一个调用重试三次;
 * 说明清楚的话它会知道「这个工具返回的是图,我读不了」并告诉用户。
 */
export function flattenContent(content: unknown): string {
  if (!Array.isArray(content)) return ''
  const out: string[] = []
  for (const block of content) {
    if (typeof block !== 'object' || block === null) continue
    const b = block as Record<string, unknown>
    switch (b['type']) {
      case 'text':
        if (typeof b['text'] === 'string') out.push(b['text'])
        break
      case 'image':
      case 'audio':
        out.push(
          `[这个工具返回了一段${b['type'] === 'image' ? '图片' : '音频'}` +
            `(${typeof b['mimeType'] === 'string' ? b['mimeType'] : '类型未知'})。` +
            `这里只能承载文本,所以它没有被转达给你 —— 需要的话请让用户直接查看。]`
        )
        break
      case 'resource': {
        const res = b['resource']
        if (typeof res === 'object' && res !== null) {
          const r = res as Record<string, unknown>
          const uri = typeof r['uri'] === 'string' ? r['uri'] : '(无 uri)'
          if (typeof r['text'] === 'string') out.push(`[资源 ${uri}]\n${r['text']}`)
          else out.push(`[资源 ${uri} 是二进制内容,没有文本形式,未转达。]`)
        }
        break
      }
      default:
        // 未知块类型:说一声。SDK 会随协议演进加新块,静默忽略等于内容凭空少一段
        out.push(`[收到一个本版本还不认识的内容块(type=${String(b['type'])}),已跳过。]`)
    }
  }
  return out.join('\n\n')
}

/** `inputSchema` 必须是个对象 schema —— 上游只接受这一种形状 */
function asObjectSchema(raw: unknown): JsonSchema | null {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return null
  const s = raw as Record<string, unknown>
  if (s['type'] !== 'object') return null
  return s as JsonSchema
}

/** 这台服务器的工具算不算联网 —— 看**我们的**配置,不看服务器的说法。见文件头 */
export function transportNeedsNetwork(cfg: McpServerConfig): boolean {
  return cfg.transport !== 'stdio'
}

/**
 * 描述前缀带上服务器名。
 *
 * 模型同时看着七台服务器的四十个工具,`create_issue` 属于哪台是**它必须知道**的事 ——
 * 不然它会拿内网 GitLab 那台的凭据去建公网 GitHub 上的 issue。
 * 消毒不在这里做:`ToolRegistry.register` 是唯一的消毒点(那样才只有一处会漏)。
 */
function describe(cfg: McpServerConfig, tool: McpToolDescriptor): string {
  const head = cfg.description === undefined || cfg.description === '' ? cfg.name : `${cfg.name} · ${cfg.description}`
  const body = tool.description ?? '(这台服务器没有为这个工具提供描述)'
  return `[MCP:${head}]\n${body}`
}

/**
 * 一台服务器的一个工具 → 一条注册。返回 `null` 表示**拒绝注册**,
 * 原因由调用方收集后显示在设置页上 —— 静默跳过的话,用户看到的是
 * 「连上了,但工具数比服务器文档里少」,而没有任何地方说得出为什么。
 */
export function toRegistration(
  cfg: McpServerConfig,
  tool: McpToolDescriptor,
  client: Pick<Client, 'callTool'>
): ToolRegistration | RejectedTool {
  if (!TOOL_NAME_RE.test(tool.name)) {
    return { name: tool.name, reason: '工具名含不允许的字符(只接受字母、数字、下划线、点、连字符)' }
  }
  const schema = asObjectSchema(tool.inputSchema)
  if (schema === null) {
    return { name: tool.name, reason: 'inputSchema 不是一个 type:"object" 的 JSON Schema' }
  }

  const serverId = cfg.id
  return {
    internalId: mcpInternalId(serverId, tool.name),
    description: describe(cfg, tool),
    inputSchema: schema,
    // 第三方 hint 不是用户授权；需要免审批的工具应由用户的 allow 规则明确放行。
    readOnly: false,
    destructive: true,
    needsNetwork: transportNeedsNetwork(cfg),
    source: { kind: 'mcp', serverId },

    async execute(input: unknown, ctx: ToolContext): Promise<ToolResult> {
      /*
        ★ 入参**原样转发,不在这里校验**。
        schema 是服务器给的,校验它的人也该是服务器 —— 我们照着它的 schema 再实现
        一遍校验,只会在两边理解不一致时产生「模型按 schema 传了参,却被我们拒了」。
        `defineTool` 那层的 zod 校验是给**我们自己写的**工具用的,这里走不到。
      */
      let res: Awaited<ReturnType<Client['callTool']>>
      try {
        /*
          ★★ 墙钟上限 —— 一台不回包的服务器,以前能把整个 run **永久**挂住。

          `manager.ts` 给 `connect`(30s)和 `listTools`(15s)都上了闸,唯独真正
          执行的这一次漏了:握手成功、工具列出来了、然后某次调用一去不回,
          而这里只有 `ctx.signal`,没有任何超时 —— 除非用户自己按停止,否则
          这个 await 永远不结算。子代理尤其致命:它的卡片上只剩一个不动的「运行中」。

          比 connect 的 30s 宽,是因为工具本来就可能慢(跑测试、建 PR);
          但「慢」和「死」必须有一条线,60s 是那条线。
        */
        res = await withDeadline(
          /*
            ★ signal 一定要传下去(方案 §4.3)。不传的话用户点了停止,
            界面停了,而远端那次调用还在跑 —— 一个正在建 PR 的工具会把 PR 建完。
            超时同理:`withDeadline` 给的这个 signal 会在到点时 abort,
            所以远端那次调用也会被真的取消,而不是我们单方面走开。
          */
          (signal) =>
            client.callTool(
              { name: tool.name, arguments: (input ?? {}) as Record<string, unknown> },
              undefined,
              { signal }
            ),
          CALL_TOOL_TIMEOUT_MS,
          `MCP 工具 ${tool.name}`,
          ctx.signal
        )
      } catch (err) {
        /*
          ★ 中断照旧往上抛 —— 用户按停止不是一次「工具失败」,
          `agent-session.ts` 那边要靠它把整个 run 收成 `aborted`。
          超时(以及服务器自己抛的错)则收成 `toolFail`:进转录、模型看得见、
          能换条路,run 继续。这正是「统一硬超时 + 报工具失败」那条决定。
        */
        if (isAbortError(err)) throw err
        return toolFail(err instanceof Error ? err.message : String(err))
      }

      const text = flattenContent(res.content)
      /*
        `isError` 原样透传:服务器说这次失败了,那就是一次**工具失败**
        (进转录、模型换个方式再试),不是一次崩溃。方案 §4.11。
      */
      if (res.isError === true) {
        return toolFail(text === '' ? `MCP 工具 ${tool.name} 报告失败,但没有给出说明。` : text)
      }
      if (text === '') {
        return toolOk(`MCP 工具 ${tool.name} 执行成功,没有返回任何内容。`)
      }
      // 截断在 `agent-session.ts` 统一做(所有工具一视同仁),这里不重复一遍
      return toolOk(text)
    }
  }
}

/** 判别:`toRegistration` 返回的是注册还是拒绝 */
export function isRejected(x: ToolRegistration | RejectedTool): x is RejectedTool {
  return !('execute' in x)
}
