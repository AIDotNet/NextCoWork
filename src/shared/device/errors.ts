import { z } from 'zod'

export const DEVICE_ERROR_CODES = [
  'invalid_request', 'protocol_error', 'version_mismatch', 'unauthorized', 'forbidden',
  'stale_binding', 'conflict', 'busy', 'not_found', 'disconnected', 'timeout',
  'result_unknown', 'resource_limit', 'resync_required', 'not_configured', 'internal'
] as const

export const deviceErrorSchema = z.strictObject({
  code: z.enum(DEVICE_ERROR_CODES),
  message: z.string().max(2048),
  retryable: z.boolean()
})
export type DeviceError = z.infer<typeof deviceErrorSchema>
export type DeviceErrorCode = DeviceError['code']

/** 无平台依赖；网络对端不能通过堆栈或任意异常对象暴露内部状态。 */
export class DeviceProtocolError extends Error {
  constructor(readonly code: DeviceErrorCode, message: string) {
    super(message)
    this.name = 'DeviceProtocolError'
  }
}
