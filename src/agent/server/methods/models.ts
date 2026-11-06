/**
 * 模型目录与别名管理。
 *
 * - `models.list`(只读):白名单视图。供应商只给 id / 名称 / 协议 / 是否启用 / 优先级;**不给 `baseUrl`**
 *   (可能含内网地址或嵌入凭据的 query)也**不给 `credentialRef`**。供应商与凭据的增删改在 `providers.*`。
 * - `models.fetch`:设备用**自己存着的密钥**向供应商拉取可用模型列表。密钥不经网络;失败原因里
 *   带有上游地址与响应,这里一律收成固定短句。
 * - `models.setAliases` / `models.remove`:复用桌面同一套写逻辑(整表替换、保留用户覆盖值、
 *   写后修复指向已消失别名的默认设置)。
 */
import { z } from 'zod'
import { fetchModels, removeModel, setAliases } from '../../../main/ipc/provider'
import { DeviceProtocolError } from '../../../shared/device/errors'
import { DEVICE_METHODS, modelsFetchParams, modelsRemoveParams, modelsSetAliasesParams } from '../../../shared/device/methods'
import type { DeviceValue } from '../../../shared/device/value'
import { store } from '../../../main/state/store'
import type { DeviceRpcRouter } from '../rpc'

const anyValue = z.custom<DeviceValue>(() => true)
const plain = (value: unknown): DeviceValue => JSON.parse(JSON.stringify(value ?? null)) as DeviceValue

const view = (alias: ReturnType<typeof store.listAliases>[number]) => ({
  alias: alias.alias, providerId: alias.providerId, displayName: alias.displayName ?? null,
  contextWindow: alias.contextWindow, maxOutputTokens: alias.maxOutputTokens, modality: alias.modality ?? null,
  enabled: alias.enabled !== false, reasoningEfforts: alias.reasoningEfforts ? [...alias.reasoningEfforts] : null
})

/** 桌面函数抛带中文细节(含上游 URL、响应片段)的普通 Error:统一成稳定错误码 + 固定短句。 */
async function guarded<T>(work: () => Promise<T> | T): Promise<T> {
  try { return await work() } catch (error) {
    if (error instanceof DeviceProtocolError) throw error
    if (error instanceof Error && error.name !== 'DatabaseBusyError') {
      throw new DeviceProtocolError(error.message.includes('没有这个供应商') ? 'not_found' : 'invalid_request', 'Model request was rejected')
    }
    throw error
  }
}

function requireProvider(providerId: string): void {
  if (!store.listProviders().some((provider) => provider.id === providerId)) throw new DeviceProtocolError('not_found', 'Provider does not exist')
}

export function registerModelMethods(router: DeviceRpcRouter): void {
  router.query(DEVICE_METHODS.modelsList, z.null(), anyValue, () => {
    const providers = store.listProviders().map((provider) => ({
      id: provider.id, name: provider.name, protocol: provider.protocol, enabled: provider.enabled, priority: provider.priority
    }))
    return plain({ providers, models: store.listAliases().map(view) })
  })

  router.execution(DEVICE_METHODS.modelsFetch, modelsFetchParams, anyValue, (_execution, { providerId }) => guarded(async () => {
    requireProvider(providerId)
    try {
      return plain(await fetchModels(providerId))
    } catch {
      // 上游不可达/401/解析不出:原因里有 URL 与响应,不出设备。
      throw new DeviceProtocolError('disconnected', 'Could not fetch the model list from the provider')
    }
  }))

  router.execution(DEVICE_METHODS.modelsSetAliases, modelsSetAliasesParams, anyValue, (_execution, p) => guarded(() => {
    requireProvider(p.providerId)
    return plain(setAliases(p.providerId, p.models).map(view))
  }))

  router.execution(DEVICE_METHODS.modelsRemove, modelsRemoveParams, z.strictObject({ removed: z.boolean() }), (_execution, p) => guarded(() => {
    requireProvider(p.providerId)
    const existed = store.listAliases().some((alias) => alias.providerId === p.providerId && alias.alias === p.alias)
    removeModel(p.providerId, p.alias)
    return { removed: existed }
  }))
}
