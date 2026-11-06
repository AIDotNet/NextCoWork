import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { closeDatabase, db, openDatabase } from '../index'
import {
  completeEnrollment, createEnrollment, ENROLLMENT_MAX_OUTSTANDING, ENROLLMENT_TTL_MS,
  findControllerByFingerprint, isControllerRevoked, listControllers, revokeController
} from '../device-trust'

let directory = ''
const fp = (c: string): string => c.repeat(64)
const pair = (token: string, id = 'phone', fingerprint = fp('a'), now = 1_000) =>
  completeEnrollment({ token, controllerId: id, fingerprintSha256: fingerprint, label: 'My phone' }, now)

beforeEach(() => {
  closeDatabase()
  directory = mkdtempSync(join(tmpdir(), 'ncw-device-trust-'))
  openDatabase(directory, { durability: 'full' })
})
afterEach(() => { closeDatabase(); rmSync(directory, { recursive: true, force: true }) })

describe('device trust records', () => {
  it('pairs once with a 256-bit invite and stores only its digest', () => {
    const { token, expiresAt } = createEnrollment(1_000)
    expect(token.length).toBeGreaterThanOrEqual(43)
    expect(expiresAt).toBe(1_000 + ENROLLMENT_TTL_MS)
    const stored = JSON.stringify(db().prepare('SELECT * FROM device_enrollments').all())
    expect(stored).not.toContain(token)

    expect(pair(token)).toEqual({ controllerId: 'phone', label: 'My phone', pairedAt: 1_000, revokedAt: null })
    expect(findControllerByFingerprint(fp('a'))?.controllerId).toBe('phone')
    expect(() => pair(token, 'tablet', fp('b'))).toThrowError(expect.objectContaining({ code: 'unauthorized' }))
  })

  it('lets exactly one of two controllers win the same invite', () => {
    const { token } = createEnrollment(1_000)
    const outcomes = [['one', fp('1')], ['two', fp('2')]].map(([id, f]) => {
      try { pair(token, id, f); return 'ok' } catch (error) { return (error as { code: string }).code }
    })
    expect(outcomes.sort()).toEqual(['ok', 'unauthorized'])
    expect(listControllers()).toHaveLength(1)
  })

  it('answers unknown, expired and consumed invites identically', () => {
    const { token } = createEnrollment(1_000)
    const code = (run: () => unknown): string => { try { run() } catch (e) { return (e as { code: string }).code } return 'none' }
    expect(code(() => pair('x'.repeat(43)))).toBe('unauthorized')
    expect(code(() => pair(token, 'phone', fp('a'), 1_000 + ENROLLMENT_TTL_MS))).toBe('unauthorized')
    expect(listControllers()).toEqual([])
  })

  it('does not burn the invite when the controller identity is already taken', () => {
    const first = createEnrollment(1_000).token
    pair(first)
    const second = createEnrollment(1_000).token
    expect(() => pair(second, 'phone', fp('c'))).toThrowError(expect.objectContaining({ code: 'conflict' }))
    expect(pair(second, 'phone-2', fp('c')).controllerId).toBe('phone-2')
  })

  it('revokes pairing: unknown after revoke, key cannot be re-registered, revoke is idempotent', () => {
    pair(createEnrollment(1_000).token)
    expect(isControllerRevoked('phone')).toBe(false)
    expect(revokeController('phone', 2_000)).toBe(true)
    expect(revokeController('phone', 3_000)).toBe(false)
    expect(isControllerRevoked('phone')).toBe(true)
    expect(isControllerRevoked('never-paired')).toBe(true)
    expect(findControllerByFingerprint(fp('a'))).toBeNull()
    expect(listControllers()[0]?.revokedAt).toBe(2_000)
    expect(() => pair(createEnrollment(4_000).token, 'phone-new', fp('a'), 4_000))
      .toThrowError(expect.objectContaining({ code: 'conflict' }))
  })

  it('caps outstanding invites and purges expired ones', () => {
    for (let i = 0; i < ENROLLMENT_MAX_OUTSTANDING; i++) createEnrollment(1_000)
    expect(() => createEnrollment(1_000)).toThrowError(expect.objectContaining({ code: 'resource_limit' }))
    expect(() => createEnrollment(1_000 + ENROLLMENT_TTL_MS)).not.toThrow()
  })

  it('rejects malformed inputs before touching the database', () => {
    const { token } = createEnrollment(1_000)
    const bad = (patch: Partial<Parameters<typeof completeEnrollment>[0]>) => () =>
      completeEnrollment({ token, controllerId: 'phone', fingerprintSha256: fp('a'), label: 'x', ...patch }, 1_000)
    expect(bad({ fingerprintSha256: 'ABC' })).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
    expect(bad({ label: 'a\nb' })).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
    expect(bad({ controllerId: '' })).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
    expect(pair(token).controllerId).toBe('phone')
  })
})
