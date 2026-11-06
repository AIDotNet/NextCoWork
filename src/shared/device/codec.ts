import { decode, encode } from '@msgpack/msgpack'
import { DEVICE_COLLECTION_MAX_ITEMS, DEVICE_FRAME_MAX_BYTES, DEVICE_VALUE_MAX_DEPTH, DEVICE_VALUE_MAX_NODES } from './capabilities'
import { deviceFrameSchema, type DeviceFrame } from './contract'
import { DeviceProtocolError } from './errors'
import { assertDeviceValue, type DeviceValue } from './value'

function invalid(message: string): never {
  throw new DeviceProtocolError('protocol_error', message)
}

function validUtf8(bytes: Uint8Array, start: number, end: number): void {
  for (let i = start; i < end;) {
    const first = bytes[i++]!
    if (first < 0x80) continue
    const count = first >= 0xc2 && first <= 0xdf ? 1 : first >= 0xe0 && first <= 0xef ? 2 : first >= 0xf0 && first <= 0xf4 ? 3 : -1
    if (count < 0 || i + count > end) invalid('Invalid UTF-8')
    let code = first & (0x7f >> count)
    for (let j = 0; j < count; j++) {
      const next = bytes[i++]!
      if ((next & 0xc0) !== 0x80) invalid('Invalid UTF-8')
      code = (code << 6) | (next & 0x3f)
    }
    if (code < [0, 0x80, 0x800, 0x10000][count]! || code > 0x10ffff || (code >= 0xd800 && code <= 0xdfff)) invalid('Invalid UTF-8')
  }
}

/** Decoder 没有深度上限；先按帧结构计数，避免小报文先分配出极深对象图。 */
function inspectFrame(bytes: Uint8Array): void {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
  const stack: { left: number; map: boolean; keys: Set<string> }[] = [{ left: 1, map: false, keys: new Set() }]
  let offset = 0
  let nodes = 0
  const take = (length: number): number => {
    if (offset + length > bytes.length) invalid('Truncated MessagePack frame')
    const start = offset
    offset += length
    return start
  }
  const size = (width: number): number => {
    const at = take(width)
    return width === 1 ? view.getUint8(at) : width === 2 ? view.getUint16(at) : view.getUint32(at)
  }
  while (stack.length > 0) {
    const parent = stack[stack.length - 1]!
    if (parent.left === 0) { stack.pop(); continue }
    if (++nodes > DEVICE_VALUE_MAX_NODES || stack.length > DEVICE_VALUE_MAX_DEPTH + 1) {
      throw new DeviceProtocolError('resource_limit', 'MessagePack structural limit exceeded')
    }
    const key = parent.map && parent.left % 2 === 0
    parent.left--
    const start = offset
    const marker = bytes[take(1)]!
    let length = 0
    let children = 0
    let map = false
    let string = false
    if (marker <= 0x7f || marker >= 0xe0 || marker === 0xc0 || marker === 0xc2 || marker === 0xc3) { /* scalar */ }
    else if ((marker & 0xe0) === 0xa0) { string = true; length = marker & 0x1f }
    else if ((marker & 0xf0) === 0x90) children = marker & 0x0f
    else if ((marker & 0xf0) === 0x80) { map = true; children = marker & 0x0f }
    else if (marker >= 0xc4 && marker <= 0xc6) length = size(1 << (marker - 0xc4))
    else if (marker >= 0xd9 && marker <= 0xdb) { string = true; length = size(1 << (marker - 0xd9)) }
    else if (marker === 0xdc || marker === 0xdd) children = size(marker === 0xdc ? 2 : 4)
    else if (marker === 0xde || marker === 0xdf) { map = true; children = size(marker === 0xde ? 2 : 4) }
    else if (marker === 0xca) length = 4
    else if (marker === 0xcb) length = 8
    else if (marker >= 0xcc && marker <= 0xcf) length = 1 << (marker - 0xcc)
    else if (marker >= 0xd0 && marker <= 0xd3) length = 1 << (marker - 0xd0)
    else invalid('MessagePack extensions and reserved markers are not supported')
    if (children > DEVICE_COLLECTION_MAX_ITEMS) throw new DeviceProtocolError('resource_limit', 'MessagePack collection limit exceeded')
    const payload = take(length)
    if (string) validUtf8(bytes, payload, offset)
    if (key) {
      if (!string) invalid('MessagePack map keys must be strings')
      const name = decode(bytes.subarray(start, offset)) as string
      if (name === '__proto__' || parent.keys.has(name)) invalid('Duplicate or unsafe MessagePack key')
      parent.keys.add(name)
    }
    if (children > 0) stack.push({ left: children * (map ? 2 : 1), map, keys: new Set() })
  }
  if (offset !== bytes.length) invalid('Extra data after MessagePack frame')
}

export function encodeDeviceValue(value: DeviceValue): Uint8Array {
  assertDeviceValue(value)
  // assertDeviceValue 从深度 0 开始，MessagePack Encoder 从 1 开始。
  const bytes = encode(value, { maxDepth: DEVICE_VALUE_MAX_DEPTH + 1, sortKeys: true })
  if (bytes.byteLength > DEVICE_FRAME_MAX_BYTES) throw new DeviceProtocolError('resource_limit', 'Device frame is too large')
  return bytes
}

export function encodeDeviceFrame(frame: DeviceFrame): Uint8Array {
  assertDeviceValue(frame)
  const parsed = deviceFrameSchema.safeParse(frame)
  if (!parsed.success) throw new DeviceProtocolError('invalid_request', 'Invalid device frame')
  return encodeDeviceValue(parsed.data)
}

export function decodeDeviceValue(bytes: Uint8Array): DeviceValue {
  if (bytes.byteLength === 0 || bytes.byteLength > DEVICE_FRAME_MAX_BYTES) {
    throw new DeviceProtocolError('resource_limit', 'Invalid device frame length')
  }
  try {
    inspectFrame(bytes)
    const value = decode(bytes, {
      maxStrLength: DEVICE_FRAME_MAX_BYTES, maxBinLength: DEVICE_FRAME_MAX_BYTES,
      maxArrayLength: DEVICE_COLLECTION_MAX_ITEMS, maxMapLength: DEVICE_COLLECTION_MAX_ITEMS,
      maxExtLength: 0, keyDecoder: null
    })
    assertDeviceValue(value)
    return value
  } catch (error) {
    if (error instanceof DeviceProtocolError) throw error
    return invalid('Malformed MessagePack frame')
  }
}

export function decodeDeviceFrame(bytes: Uint8Array): DeviceFrame {
  const parsed = deviceFrameSchema.safeParse(decodeDeviceValue(bytes))
  if (!parsed.success) invalid('Invalid device frame')
  return parsed.data
}
