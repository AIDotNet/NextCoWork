import { z } from 'zod'
import { clientCapabilitiesSchema, serverCapabilitiesSchema, DEVICE_PROTOCOL_VERSION } from './capabilities'
import { deviceBindingSchema, deviceIdSchema } from './identity'
import { deviceErrorSchema } from './errors'
import { assertDeviceValue, type DeviceValue } from './value'

export const deviceValueSchema = z.custom<DeviceValue>((value) => {
  try { assertDeviceValue(value); return true } catch { return false }
})
const methodSchema = z.string().max(128).regex(/^[a-z][A-Za-z0-9]*(?:[.:][A-Za-z][A-Za-z0-9]*)+$/)
const cursorSchema = z.string().min(1).max(128)
const base = { v: z.literal(DEVICE_PROTOCOL_VERSION) }

export const deviceRequestSchema = z.strictObject({
  ...base,
  type: z.literal('request'),
  id: deviceIdSchema,
  binding: deviceBindingSchema,
  method: methodSchema,
  // 方法级前置条件放在 params，由该方法决定数字 CAS 或文件摘要的类型。
  params: deviceValueSchema,
  idempotencyKey: deviceIdSchema.optional()
})

export const deviceResponseSchema = z.discriminatedUnion('ok', [
  z.strictObject({ ...base, type: z.literal('response'), id: deviceIdSchema, binding: deviceBindingSchema,
    ok: z.literal(true), data: deviceValueSchema }),
  z.strictObject({ ...base, type: z.literal('response'), id: deviceIdSchema, binding: deviceBindingSchema,
    ok: z.literal(false), error: deviceErrorSchema })
])

export const deviceEventSchema = z.strictObject({
  ...base,
  type: z.literal('event'),
  binding: deviceBindingSchema,
  streamId: deviceIdSchema,
  cursor: cursorSchema,
  previousCursor: cursorSchema.nullable(),
  event: methodSchema,
  data: deviceValueSchema
})
export const deviceHelloSchema = z.strictObject({
  ...base, type: z.literal('hello'),
  versions: z.array(z.number().int().min(1).max(65_535)).min(1).max(16),
  clientSessionId: deviceIdSchema,
  bindingEpoch: z.number().int().min(0).max(Number.MAX_SAFE_INTEGER),
  capabilities: clientCapabilitiesSchema
})

/** 服务端回执：binding 是这次绑定的权威描述，控制端仍需与配对期望逐字段核对。 */
export const deviceWelcomeSchema = z.strictObject({
  ...base, type: z.literal('welcome'),
  protocol: z.literal(DEVICE_PROTOCOL_VERSION),
  binding: deviceBindingSchema,
  capabilities: serverCapabilitiesSchema
})

export const deviceHandshakeErrorSchema = z.strictObject({
  ...base, type: z.literal('handshake_error'),
  error: deviceErrorSchema
})

export const deviceFrameSchema = z.union([
  deviceRequestSchema, deviceResponseSchema, deviceEventSchema,
  deviceHelloSchema, deviceWelcomeSchema, deviceHandshakeErrorSchema
])
export type DeviceRequest = z.infer<typeof deviceRequestSchema>
export type DeviceResponse = z.infer<typeof deviceResponseSchema>
export type DeviceEvent = z.infer<typeof deviceEventSchema>
export type DeviceHello = z.infer<typeof deviceHelloSchema>
export type DeviceWelcome = z.infer<typeof deviceWelcomeSchema>
export type DeviceHandshakeError = z.infer<typeof deviceHandshakeErrorSchema>
export type DeviceFrame = z.infer<typeof deviceFrameSchema>

/** ResourceRef 不携带网络凭据或控制端本机路径；实际读取仍需认证与范围校验。 */
export const deviceResourceSchema = z.strictObject({
  deviceId: deviceIdSchema,
  dataEpoch: deviceIdSchema,
  scopeEpoch: deviceIdSchema,
  resourceId: deviceIdSchema,
  revision: deviceIdSchema,
  mime: z.string().min(1).max(256),
  size: z.number().int().min(0).max(Number.MAX_SAFE_INTEGER)
})
export type DeviceResource = z.infer<typeof deviceResourceSchema>
