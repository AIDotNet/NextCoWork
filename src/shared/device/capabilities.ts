import { z } from 'zod'

export const DEVICE_PROTOCOL_VERSION = 1
export const DEVICE_FRAME_MAX_BYTES = 1024 * 1024
export const DEVICE_VALUE_MAX_DEPTH = 32
export const DEVICE_VALUE_MAX_NODES = 16_384
export const DEVICE_COLLECTION_MAX_ITEMS = 4096

const maxFrameBytesSchema = z.number().int().min(4096).max(DEVICE_FRAME_MAX_BYTES)
const featuresSchema = z.array(z.enum([
  'tasks', 'interactions', 'resources', 'range', 'terminal', 'browser-surface',
  'document-surface', 'plugin-views', 'push-notifications'
])).max(16)

export const clientCapabilitiesSchema = z.strictObject({
  kind: z.enum(['desktop', 'ios', 'android', 'test']),
  maxFrameBytes: maxFrameBytesSchema,
  features: featuresSchema
})
export type ClientCapabilities = z.infer<typeof clientCapabilitiesSchema>

/** 服务能力与控制端展示能力不同；服务器不伪装成 kind:'test' 的客户端。 */
export const serverCapabilitiesSchema = z.strictObject({
  maxFrameBytes: maxFrameBytesSchema,
  features: featuresSchema
})
export type ServerCapabilities = z.infer<typeof serverCapabilitiesSchema>

export function negotiateDeviceProtocol(client: readonly number[], server: readonly number[]): number | null {
  const supported = client.filter((version) => Number.isSafeInteger(version) && version > 0 && server.includes(version))
  return supported.length === 0 ? null : Math.max(...supported)
}
