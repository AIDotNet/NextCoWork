import { DEVICE_COLLECTION_MAX_ITEMS, DEVICE_FRAME_MAX_BYTES, DEVICE_VALUE_MAX_DEPTH, DEVICE_VALUE_MAX_NODES } from './capabilities'
import { DeviceProtocolError } from './errors'

export type DeviceValue = null | boolean | number | string | Uint8Array | DeviceValue[] | { [key: string]: DeviceValue }

export function utf8Length(text: string): number {
  let bytes = 0
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i)
    if (c < 0x80) bytes++
    else if (c < 0x800) bytes += 2
    else if (c >= 0xd800 && c <= 0xdbff) {
      const next = text.charCodeAt(++i)
      if (!(next >= 0xdc00 && next <= 0xdfff)) throw new DeviceProtocolError('invalid_request', 'Unpaired UTF-16 surrogate')
      bytes += 4
    } else {
      if (c >= 0xdc00 && c <= 0xdfff) throw new DeviceProtocolError('invalid_request', 'Unpaired UTF-16 surrogate')
      bytes += 3
    }
  }
  return bytes
}

/** 编码之前限制对象图；拒绝 getter、类实例、循环和 undefined，不能静默把清空变成省略。 */
export function assertDeviceValue(value: unknown, maxBytes = DEVICE_FRAME_MAX_BYTES): asserts value is DeviceValue {
  let nodes = 0
  let bytes = 0
  const ancestors = new Set<object>()
  const charge = (size: number): void => {
    bytes += size
    if (bytes > maxBytes) throw new DeviceProtocolError('resource_limit', 'Device value exceeds byte budget')
  }
  const visit = (item: unknown, depth: number): void => {
    if (++nodes > DEVICE_VALUE_MAX_NODES || depth > DEVICE_VALUE_MAX_DEPTH) {
      throw new DeviceProtocolError('resource_limit', 'Device value exceeds structural budget')
    }
    if (item === null || typeof item === 'boolean') { charge(1); return }
    if (typeof item === 'number') {
      if (!Number.isFinite(item) || (Number.isInteger(item) && !Number.isSafeInteger(item))) {
        throw new DeviceProtocolError('invalid_request', 'Invalid wire number')
      }
      charge(9); return
    }
    if (typeof item === 'string') { charge(5 + utf8Length(item)); return }
    if (item instanceof Uint8Array) { charge(5 + item.byteLength); return }
    if (typeof item !== 'object' || item === undefined) throw new DeviceProtocolError('invalid_request', 'Unsupported wire value')
    if (ancestors.has(item)) throw new DeviceProtocolError('invalid_request', 'Cyclic wire value')
    const array = Array.isArray(item)
    const prototype: unknown = Object.getPrototypeOf(item)
    if (!array && prototype !== Object.prototype && prototype !== null) {
      throw new DeviceProtocolError('invalid_request', 'Wire objects must be plain records')
    }
    const keys = Reflect.ownKeys(item)
    if (keys.some((key) => typeof key !== 'string')) throw new DeviceProtocolError('invalid_request', 'Symbol wire key')
    const entries = keys.filter((key) => !(array && key === 'length')) as string[]
    if (entries.length > DEVICE_COLLECTION_MAX_ITEMS || (array && item.length !== entries.length)) {
      throw new DeviceProtocolError('resource_limit', 'Invalid or oversized wire collection')
    }
    ancestors.add(item)
    charge(5)
    for (const key of entries) {
      if (array && (!/^(0|[1-9]\d*)$/.test(key) || Number(key) >= item.length)) {
        throw new DeviceProtocolError('invalid_request', 'Invalid wire array property')
      }
      if (key === '__proto__') throw new DeviceProtocolError('invalid_request', 'Unsafe wire key')
      const descriptor = Object.getOwnPropertyDescriptor(item, key)!
      if (!descriptor.enumerable || !('value' in descriptor)) throw new DeviceProtocolError('invalid_request', 'Unsupported wire property')
      if (!array) {
        // wire 预检也会把 map key 算作节点，两侧必须是同一预算。
        if (++nodes > DEVICE_VALUE_MAX_NODES) throw new DeviceProtocolError('resource_limit', 'Device value exceeds structural budget')
        charge(5 + utf8Length(key))
      }
      visit(descriptor.value, depth + 1)
    }
    ancestors.delete(item)
  }
  visit(value, 0)
}
