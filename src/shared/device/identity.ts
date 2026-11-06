import { z } from 'zod'

export const deviceIdSchema = z.string().min(1).max(128).regex(/^[A-Za-z0-9][A-Za-z0-9_.:-]*$/)
export const scopeIdSchema = z.string().min(1).max(256).refine((value) => {
  for (const character of value) {
    const code = character.codePointAt(0)!
    if (code < 0x20 || code === 0x7f) return false
  }
  return true
}, 'Scope must not contain control characters')

/** 身份由安全传输认证；此处只验证报文形状，不能据此授予权限。 */
export const deviceBindingSchema = z.strictObject({
  deviceId: deviceIdSchema,
  serverEpoch: deviceIdSchema,
  dataEpoch: deviceIdSchema,
  controllerId: deviceIdSchema,
  clientSessionId: deviceIdSchema,
  configScope: scopeIdSchema,
  scopeEpoch: deviceIdSchema,
  bindingEpoch: z.number().int().min(0).max(Number.MAX_SAFE_INTEGER)
})
export type DeviceBinding = z.infer<typeof deviceBindingSchema>

export function sameDeviceBinding(a: DeviceBinding, b: DeviceBinding): boolean {
  return a.deviceId === b.deviceId && a.serverEpoch === b.serverEpoch
    && a.dataEpoch === b.dataEpoch && a.controllerId === b.controllerId
    && a.clientSessionId === b.clientSessionId && a.configScope === b.configScope
    && a.scopeEpoch === b.scopeEpoch && a.bindingEpoch === b.bindingEpoch
}
