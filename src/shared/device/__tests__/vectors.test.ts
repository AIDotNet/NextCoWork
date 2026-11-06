import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import { z } from 'zod'
import { decodeDeviceFrame, encodeDeviceFrame } from '../codec'
import { deviceFrameSchema } from '../contract'

const fixtureSchema = z.strictObject({
  protocol: z.literal(1), encoding: z.literal('MessagePack'),
  vectors: z.array(z.strictObject({ name: z.string(), frame: deviceFrameSchema, hex: z.string().regex(/^(?:[0-9a-f]{2})+$/) })).min(6)
})
const fixtures = fixtureSchema.parse(JSON.parse(readFileSync(resolve('test/device-protocol-vectors/v1.json'), 'utf8')))

describe('cross-language device protocol vectors', () => {
  for (const vector of fixtures.vectors) {
    it(vector.name, () => {
      expect(Buffer.from(encodeDeviceFrame(vector.frame)).toString('hex')).toBe(vector.hex)
      expect(decodeDeviceFrame(new Uint8Array(Buffer.from(vector.hex, 'hex')))).toEqual(vector.frame)
    })
  }
})
