/**
 * MCP 服务器的远程管理,复用桌面同一套 `ipc/mcp.ts`(校验、先断后连、密钥整张 map 写入、删除连密钥一起删)。
 *
 * - 配置里只有键名;密钥值只写不读(`mcp.setSecrets`),`secretsInfo` 只回已存的键名。
 * - 桌面函数抛带中文细节的普通 Error(含用户输入的地址/命令):这里统一译成稳定错误码 + 固定短句。
 * - ★ stdio 会在设备上启动 `command` —— 与终端方法同一等级的授权,见 shared/device/methods.ts 的说明。
 */
import { z } from 'zod'
import {
  getMcpSecretsInfo, listMcpServers, removeMcpServer, setMcpSecrets, testMcpConnection, upsertMcpServer
} from '../../../main/ipc/mcp'
import { DeviceProtocolError } from '../../../shared/device/errors'
import {
  DEVICE_METHODS, mcpIdParams, mcpSecretsResult, mcpSetSecretsParams, mcpUpsertParams
} from '../../../shared/device/methods'
import type { McpServerConfig } from '../../../shared/domain/mcp'
import type { DeviceValue } from '../../../shared/device/value'
import { store } from '../../../main/state/store'
import type { DeviceRpcRouter } from '../rpc'

const anyValue = z.custom<DeviceValue>(() => true)

/** 状态里的 `error` 可能是连接失败的原文(含路径/命令输出),只保留「失败了」这一事实。 */
function publicStatus(status: unknown): DeviceValue {
  const copy = JSON.parse(JSON.stringify(status)) as { error?: unknown }
  if (copy.error !== undefined) copy.error = 'Connection failed'
  return copy as DeviceValue
}

async function guarded<T>(work: () => Promise<T> | T): Promise<T> {
  try {
    return await work()
  } catch (error) {
    if (error instanceof DeviceProtocolError) throw error
    if (error instanceof Error && error.name !== 'DatabaseBusyError') {
      throw new DeviceProtocolError(error.message.includes('没有 ID 为') ? 'not_found' : 'invalid_request', 'MCP request was rejected')
    }
    throw error
  }
}

function requireServer(id: string): void {
  if (store.getMcpServer(id) === undefined) throw new DeviceProtocolError('not_found', 'MCP server does not exist')
}

export function registerMcpMethods(router: DeviceRpcRouter): void {
  router.query(DEVICE_METHODS.mcpList, z.null(), anyValue, () => (listMcpServers() as unknown[]).map((status) => publicStatus(status)))

  router.query(DEVICE_METHODS.mcpSecretsInfo, mcpIdParams, mcpSecretsResult, (_context, { id }) => guarded(async () => {
    requireServer(id)
    return getMcpSecretsInfo(id)
  }))

  router.execution(DEVICE_METHODS.mcpUpsert, mcpUpsertParams, anyValue,
    (_execution, { config }) => guarded(async () => publicStatus(await upsertMcpServer(config as McpServerConfig))))

  router.execution(DEVICE_METHODS.mcpSetSecrets, mcpSetSecretsParams, mcpSecretsResult, (_execution, p) => guarded(async () => {
    requireServer(p.id)
    return setMcpSecrets(p.id, p.values)
  }))

  router.execution(DEVICE_METHODS.mcpTest, mcpIdParams, anyValue, (_execution, { id }) => guarded(async () => {
    requireServer(id)
    return publicStatus(await testMcpConnection(id))
  }))

  router.execution(DEVICE_METHODS.mcpRemove, mcpIdParams, z.strictObject({ removed: z.boolean() }), (_execution, { id }) => guarded(async () => {
    const existed = store.getMcpServer(id) !== undefined
    await removeMcpServer(id)
    return { removed: existed }
  }))
}
