import { encode } from '@msgpack/msgpack'
import { describe, expect, it } from 'vitest'
import { decodeDeviceFrame, decodeDeviceValue, encodeDeviceFrame, encodeDeviceValue } from '../codec'
import { DEVICE_FRAME_MAX_BYTES, DEVICE_VALUE_MAX_DEPTH, negotiateDeviceProtocol } from '../capabilities'
import { assertDeviceValue, utf8Length, type DeviceValue } from '../value'
import type { DeviceRequest } from '../contract'

const request: DeviceRequest = {
  v: 1, type: 'request', id: 'request-1', method: 'agent.run',
  binding: { deviceId: 'device-a', controllerId: 'controller-1', clientSessionId: 'client-1',
    serverEpoch: 'server-1', dataEpoch: 'data-1', configScope: 'local', scopeEpoch: 'scope-1', bindingEpoch: 0 },
  idempotencyKey: 'command-1', params: { text: '中文', clear: null, bytes: new Uint8Array([0, 255]) }
}

describe('device MessagePack contract', () => {
  it('round trips text, explicit null and binary without Node-only values', () => {
    expect(decodeDeviceFrame(encodeDeviceFrame(request))).toEqual(request)
    expect(utf8Length('a中\u{1f600}')).toBe(8)
  })

  it('is deterministic for command parameter maps', () => {
    expect(encodeDeviceFrame({ ...request, params: { z: 2, a: 1 } }))
      .toEqual(encodeDeviceFrame({ ...request, params: { a: 1, z: 2 } }))
  })

  it.each([undefined, NaN, Infinity, Number.MAX_SAFE_INTEGER + 1, new Date(), new Map(), () => {}, '\ud800'])('rejects unsupported wire values (%s)', (value) => {
    expect(() => assertDeviceValue(value)).toThrow()
  })

  it('does not call getters or silently omit an undefined property', () => {
    let invoked = false
    const value = { get secret() { invoked = true; return 'x' } }
    expect(() => assertDeviceValue(value)).toThrow()
    expect(invoked).toBe(false)
    expect(() => encodeDeviceFrame({ ...request, params: { absent: undefined } } as unknown as DeviceRequest)).toThrow()
  })

  it('rejects cycles, sparse arrays and oversized payloads before encoding', () => {
    const cycle: unknown[] = []; cycle.push(cycle)
    expect(() => assertDeviceValue(cycle)).toThrow()
    expect(() => assertDeviceValue(new Array(4))).toThrow()
    expect(() => encodeDeviceFrame({ ...request, params: '中'.repeat(DEVICE_FRAME_MAX_BYTES / 2) })).toThrow()
  })

  it('checks framing, depth, duplicates and UTF-8 before allocating the decoded tree', () => {
    const frame = encodeDeviceFrame(request)
    expect(() => decodeDeviceFrame(frame.subarray(0, frame.length - 1))).toThrow()
    expect(() => decodeDeviceFrame(new Uint8Array([...frame, 0]))).toThrow()
    expect(() => decodeDeviceFrame(new Uint8Array(DEVICE_FRAME_MAX_BYTES + 1))).toThrow()
    expect(() => decodeDeviceFrame(new Uint8Array([...new Array(100).fill(0x91), 0xc0]))).toThrow()
    expect(() => decodeDeviceFrame(new Uint8Array([0x82, 0xa1, 0x61, 1, 0xa1, 0x61, 2]))).toThrow(/Duplicate/)
    expect(() => decodeDeviceFrame(new Uint8Array([0xa1, 0xff]))).toThrow(/UTF-8/)
    expect(() => decodeDeviceFrame(new Uint8Array([0xa3, 0xed, 0xa0, 0x80]))).toThrow(/UTF-8/)
    expect(() => decodeDeviceFrame(encode(new Date()))).toThrow(/extensions/)
  })

  it('rejects unknown versions, unsafe keys, non-string map keys and extra envelope fields', () => {
    expect(() => decodeDeviceFrame(encode({ ...request, v: 2 }))).toThrow()
    expect(() => decodeDeviceFrame(encode({ ...request, role: 'admin' }))).toThrow()
    expect(() => decodeDeviceFrame(encode(JSON.parse('{"__proto__":1}')))).toThrow()
    expect(() => decodeDeviceFrame(new Uint8Array([0x81, 1, 2]))).toThrow(/keys/)
  })

  it('uses the same depth boundary for validation, encoding and decoding', () => {
    let value: DeviceValue = null
    for (let depth = 0; depth <= DEVICE_VALUE_MAX_DEPTH; depth++) {
      expect(() => assertDeviceValue(value)).not.toThrow()
      expect(decodeDeviceValue(encodeDeviceValue(value))).toEqual(value)
      value = { child: value }
    }
    expect(() => encodeDeviceValue(value)).toThrow()
    expect(() => decodeDeviceValue(encode(value))).toThrow()
    expect(utf8Length('2'.repeat(80))).toBe(80)
    const array = [1]
    Object.defineProperty(array, '4294967295', { value: 1, enumerable: true })
    expect(() => assertDeviceValue(array)).toThrow()
  })

  it('negotiates only explicitly shared positive protocol versions', () => {
    expect(negotiateDeviceProtocol([1, 2], [1])).toBe(1)
    expect(negotiateDeviceProtocol([2], [1])).toBeNull()
    expect(negotiateDeviceProtocol([NaN, -1, 0], [NaN, -1, 0])).toBeNull()
  })
})
