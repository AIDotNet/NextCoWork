/**
 * 供应商与凭据的远程管理,复用桌面同一套 `ipc/provider.ts` 的写逻辑(地址白名单、托管供应商保护、
 * `credentialRef` 永远由设备派生、删除时连密钥一起删)。
 *
 * 在桌面写逻辑之上多收紧三条:
 *  1. **密钥只写不读**:响应里只有 `hasKey / last4`;不提供 reveal。
 *  2. 不接受 `protocolOptions` / `videoGeneration`(它们决定凭据发往哪个地址),沿用设备上已有的值。
 *  3. 桌面函数抛的是带中文细节的普通 Error:这里一律译成稳定错误码 + 固定短句,不外带细节。
 */
import { z } from 'zod'
import { infoFor, removeProvider, setCredential, upsertProvider } from '../../../main/ipc/provider'
import { ensureSeeded, getHost } from '../../../main/runtime'
import { store } from '../../../main/state/store'
import { DeviceProtocolError } from '../../../shared/device/errors'
import {
  credentialMaskResult, DEVICE_METHODS, providersCredentialInfoParams, providersGetParams, providersRemoveParams,
  providersSetCredentialParams, providersUpsertParams
} from '../../../shared/device/methods'
import type { DeviceValue } from '../../../shared/device/value'
import type { DeviceRpcRouter } from '../rpc'

const anyValue = z.custom<DeviceValue>(() => true)

/** 桌面校验失败(空名、坏地址、未知协议…)是调用方可修正的错误;其余原样交给脱敏。 */
function translate(error: unknown): never {
  if (error instanceof DeviceProtocolError) throw error
  if (error instanceof Error && !(error.name === 'DatabaseBusyError')) {
    throw new DeviceProtocolError(error.message.includes('没有这个供应商') ? 'not_found' : 'invalid_request', 'Provider request was rejected')
  }
  throw error
}
async function guarded<T>(work: () => Promise<T> | T): Promise<T> {
  try { return await work() } catch (error) { return translate(error) }
}

function find(id: string) {
  ensureSeeded()
  const provider = store.listProviders().find((item) => item.id === id)
  if (provider === undefined) throw new DeviceProtocolError('not_found', 'Provider does not exist')
  return provider
}

async function mask(id: string): Promise<{ hasKey: boolean; last4: string | null }> {
  const provider = find(id)
  const info = infoFor(await getHost().secrets.get(provider.credentialRef))
  return { hasKey: info.hasKey, last4: info.last4 }
}

export function registerProviderMethods(router: DeviceRpcRouter): void {
  router.query(DEVICE_METHODS.providersGet, providersGetParams, anyValue, (_context, { id }) => {
    const provider = find(id)
    // 没有 credentialRef:那是设备内部的存储引用。
    return { id: provider.id, name: provider.name, protocol: provider.protocol, baseUrl: provider.baseUrl,
      priority: provider.priority, enabled: provider.enabled }
  })

  router.query(DEVICE_METHODS.providersCredentialInfo, providersCredentialInfoParams, credentialMaskResult,
    (_context, { id }) => guarded(() => mask(id)))

  router.execution(DEVICE_METHODS.providersUpsert, providersUpsertParams,
    z.strictObject({ id: z.string(), hasKey: z.boolean(), last4: z.string().nullable() }), (_execution, p) => guarded(async () => {
      const existing = store.listProviders().find((item) => item.id === p.id)
      const saved = upsertProvider({
        id: p.id, name: p.name, protocol: p.protocol, baseUrl: p.baseUrl, priority: p.priority ?? existing?.priority ?? 50,
        enabled: p.enabled, credentialRef: existing?.credentialRef ?? ''
      })
      if (p.apiKey !== undefined) await setCredential(saved.id, p.apiKey)
      return { id: saved.id, ...(await mask(saved.id)) }
    }))

  router.execution(DEVICE_METHODS.providersSetCredential, providersSetCredentialParams, credentialMaskResult,
    (_execution, p) => guarded(async () => {
      await setCredential(p.id, p.apiKey)
      return mask(p.id)
    }))

  router.execution(DEVICE_METHODS.providersRemove, providersRemoveParams, z.strictObject({ removed: z.boolean() }),
    (_execution, p) => guarded(() => {
      const existed = store.listProviders().some((item) => item.id === p.id)
      removeProvider(p.id)
      return { removed: existed }
    }))
}
