/**
 * `McpManager` 的测试 —— **真起一台 MCP 服务器**。
 *
 * 用 SDK 自带的 `InMemoryTransport` 把一个真的 `McpServer` 和我们的 `Client`
 * 接在一起,协议层(initialize 握手、tools/list、tools/call、JSON-RPC 编解码)
 * 全都是真的,只有「字节怎么在两端之间流动」被换掉了。
 *
 * 为什么非这样不可:这一层的全部价值就是「工具真的进了 ToolRegistry、
 * 调用真的能往返、断开真的清干净」。拿一个假 client 去测,测到的是
 * 我们自己对 SDK 的**想象**,而这正是最容易错的那一半 ——
 * `callTool` 的返回形状、`isError` 在哪一层、annotations 缺省时是 undefined 还是 false。
 */
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'
import type { Transport } from '@modelcontextprotocol/sdk/shared/transport.js'
import { beforeEach, describe, expect, it } from 'vitest'
import { z } from 'zod'
import type { McpServerConfig } from '../../../shared/domain/mcp'
import { nodeHost } from '../../kernel/host'
import type { ToolContext } from '../../kernel/tool/registry'
import { ToolRegistry } from '../../kernel/tool/registry'
import { McpManager } from '../manager'
import { evaluate } from '../../kernel/permission-gate'

const host = nodeHost()

/** 一台带三个工具的服务器,三个工具各自钉一件事 */
function makeServer(): McpServer {
  const s = new McpServer({ name: '测试服务器', version: '1.0.0' })

  s.registerTool(
    'add',
    {
      description: '把两个数加起来',
      inputSchema: { a: z.number(), b: z.number() },
      annotations: { readOnlyHint: true, destructiveHint: false }
    },
    ({ a, b }) => ({ content: [{ type: 'text', text: String(a + b) }] })
  )

  // 没有 annotations —— 用来钉「未声明时朝最危险的方向兜底」
  s.registerTool('unknown_risk', { description: '没声明任何 hint' }, () => ({
    content: [{ type: 'text', text: 'ok' }]
  }))

  s.registerTool('always_fails', { description: '总是失败' }, () => ({
    content: [{ type: 'text', text: '这台服务器说这次不行' }],
    isError: true
  }))

  return s
}

/**
 * 把一台服务器接到 manager 上:一对 InMemoryTransport,服务器那半自己连好。
 *
 * ★ **每次调用现起一台新服务器**,不复用同一个实例。SDK 的 `Protocol` 是
 * 一对一的(第二次 `connect` 会抛 "Already connected to a transport"),
 * 而这恰好对应真实情形:每台 MCP 服务器都是一个独立进程 / 一条独立连接。
 * 共用一个实例的话,「连两台」这类用例测到的是我们测试脚手架的限制,不是产品行为。
 */
function linked(): (cfg: McpServerConfig) => Promise<Transport> {
  return async () => {
    const [clientSide, serverSide] = InMemoryTransport.createLinkedPair()
    await makeServer().connect(serverSide)
    return clientSide
  }
}

/**
 * ★ 两个构造器,不是一个带 `Partial<McpServerConfig>` 的。
 * `McpServerConfig` 是判别联合,把 `Partial<联合>` 展开进去会先把两支合成一个
 * 「transport 是三选一、command 和 url 都可能有」的宽类型,再赋不回联合本身 ——
 * 于是构造器要么加断言(把类型检查关掉),要么写成两个(让每一支自己成立)。
 */
const cfg = (over: { id?: string; enabled?: boolean } = {}): McpServerConfig => ({
  id: 'demo',
  name: '演示',
  enabled: true,
  transport: 'stdio',
  command: 'irrelevant',
  args: [],
  envNames: [],
  ...over
})

const httpCfg = (id: string): McpServerConfig => ({
  id,
  name: '远程演示',
  enabled: true,
  transport: 'streamable-http',
  url: 'https://example.com/mcp',
  headerNames: []
})

function ctx(signal = new AbortController().signal): ToolContext {
  return {
    workspaceRoot: '/tmp',
    signal,
    permissionMode: 'auto',
    depth: 0,
    callId: 'c1',
    runId: 'r1',
    host,
    emit: () => {}
  }
}

describe('McpManager · 连接与注册', () => {
  let tools: ToolRegistry
  let manager: McpManager

  beforeEach(() => {
    tools = new ToolRegistry()
    manager = new McpManager({
      tools,
      secrets: host.secrets,
      logger: host.logger,
      transportFactory: linked()
    })
  })

  it('连上之后工具以 mcp__<id>__<tool> 进注册表', async () => {
    const status = await manager.connect(cfg())

    expect(status.state).toBe('connected')
    expect(status.toolCount).toBe(3)
    expect(tools.byInternalId('mcp__demo__add')).toBeDefined()
    expect(tools.byInternalId('mcp__demo__always_fails')).toBeDefined()
  })

  /** ★ 未声明时朝最危险的方向兜底 —— 见 `bridge.ts` 文件头那张表 */
  it('服务器自报的 hints 不降低审批等级', async () => {
    await manager.connect(cfg())

    const declared = tools.byInternalId('mcp__demo__add')
    expect(declared?.readOnly).toBe(false)
    expect(declared?.destructive).toBe(true)
    expect(evaluate({ mode: 'ask', readOnly: declared!.readOnly, destructive: declared!.destructive, webSearch: true }))
      .toEqual({ kind: 'ask' })
    expect(evaluate({ mode: 'auto', readOnly: declared!.readOnly, destructive: declared!.destructive, webSearch: true }))
      .toEqual({ kind: 'ask' })

    const silent = tools.byInternalId('mcp__demo__unknown_risk')
    expect(silent?.readOnly).toBe(false)
    expect(silent?.destructive).toBe(true)
  })

  /**
   * ★ 联网标记看的是**我们的传输方式**,不是服务器的说法。
   * stdio 打到的是本地子进程 → false;远程传输定义上跨网络 → true。
   */
  it('needsNetwork 由传输方式决定', async () => {
    await manager.connect(cfg())
    expect(tools.byInternalId('mcp__demo__add')?.needsNetwork).toBe(false)

    const remote = new McpManager({
      tools,
      secrets: host.secrets,
      logger: host.logger,
      transportFactory: linked()
    })
    await remote.connect(httpCfg('far'))
    expect(tools.byInternalId('mcp__far__add')?.needsNetwork).toBe(true)
  })

  it('调用能往返,入参与返回都过得去', async () => {
    await manager.connect(cfg())
    const tool = tools.byInternalId('mcp__demo__add')
    const r = await tool?.execute({ a: 2, b: 40 }, ctx())
    expect(r?.isError).toBe(false)
    expect(r?.output.content).toBe('42')
  })

  /** 服务器说失败 → 一次**工具失败**(进转录、模型再试),不是异常 */
  it('isError 原样透传成工具失败', async () => {
    await manager.connect(cfg())
    const r = await tools.byInternalId('mcp__demo__always_fails')?.execute({}, ctx())
    expect(r?.isError).toBe(true)
    expect(r?.output.content).toContain('这台服务器说这次不行')
  })
})

describe('McpManager · 断开与重连', () => {
  let tools: ToolRegistry
  let manager: McpManager

  beforeEach(() => {
    tools = new ToolRegistry()
    manager = new McpManager({
      tools,
      secrets: host.secrets,
      logger: host.logger,
      transportFactory: linked()
    })
  })

  it('断开后这台服务器的工具从注册表里清干净', async () => {
    await manager.connect(cfg())
    expect(tools.size).toBe(3)

    await manager.disconnect('demo')
    expect(tools.size).toBe(0)
    expect(manager.statusOf(cfg()).state).toBe('disconnected')
  })

  /** 别家的工具不能被连坐 —— `unregisterBySource` 是按源删的 */
  it('断开一台不影响另一台', async () => {
    await manager.connect(cfg({ id: 'a' }))
    await manager.connect(cfg({ id: 'b' }))
    expect(tools.size).toBe(6)

    await manager.disconnect('a')
    expect(tools.size).toBe(3)
    expect(tools.byInternalId('mcp__b__add')).toBeDefined()
  })

  /**
   * ★ 重连是**替换**,不是报错,也不是翻倍。
   * 而且 externalName 由 internalId 记忆,所以重连后历史转录里的名字仍然对得上 ——
   * 这一条是 `naming.ts` 那句「映射在会话内必须稳定」的实测。
   */
  it('重连是替换,工具不翻倍且外部名不变', async () => {
    await manager.connect(cfg())
    const before = tools.byInternalId('mcp__demo__add')?.externalName

    await manager.connect(cfg())
    expect(tools.size).toBe(3)
    expect(tools.byInternalId('mcp__demo__add')?.externalName).toBe(before)
  })

  it('从没连过的 id 也能安全断开', async () => {
    await expect(manager.disconnect('never')).resolves.toBeUndefined()
  })

  it('shutdown 之后一个工具都不剩', async () => {
    await manager.connect(cfg({ id: 'a' }))
    await manager.connect(cfg({ id: 'b' }))
    await manager.shutdown()
    expect(tools.size).toBe(0)
  })
})

describe('McpManager · 失败路径', () => {
  const failing: McpManagerFactory = async () => {
    throw new Error('spawn npx ENOENT')
  }
  type McpManagerFactory = (cfg: McpServerConfig) => Promise<Transport>

  /** 连不上收敛成 `state:'error'`,**不抛** —— 抛的话启动时一台坏服务器能打断整个 initRuntime */
  it('连不上时返回 error 状态而不是抛异常', async () => {
    const tools = new ToolRegistry()
    const manager = new McpManager({
      tools,
      secrets: host.secrets,
      logger: { ...host.logger, warn: () => {} },
      transportFactory: failing
    })

    const status = await manager.connect(cfg())
    expect(status.state).toBe('error')
    // ENOENT 翻成一句能照着做的话,而不是把 Node 的原文丢给用户
    expect(status.error).toContain('PATH')
    expect(tools.size).toBe(0)
  })

  it('id 不合法时直接拒绝,不去连', async () => {
    const tools = new ToolRegistry()
    const manager = new McpManager({
      tools,
      secrets: host.secrets,
      logger: { ...host.logger, warn: () => {} },
      transportFactory: linked()
    })
    const status = await manager.connect(cfg({ id: '../etc/passwd' }))
    expect(status.state).toBe('error')
    expect(tools.size).toBe(0)
  })

  /**
   * ★ 设置页那个「测试连接」按钮双击是常态。两条连接同时起来的话,
   * 后一条注册完工具,前一条的收尾会把它们全下掉 —— 表现是「连上了,工具是 0 个」。
   */
  it('同一个 id 并发连接只跑一次', async () => {
    const tools = new ToolRegistry()
    let made = 0
    const manager = new McpManager({
      tools,
      secrets: host.secrets,
      logger: host.logger,
      transportFactory: async (c) => {
        made++
        return linked()(c)
      }
    })

    const [a, b] = await Promise.all([manager.connect(cfg()), manager.connect(cfg())])
    expect(made).toBe(1)
    expect(a.state).toBe('connected')
    expect(b.state).toBe('connected')
    expect(tools.size).toBe(3)
  })
})
