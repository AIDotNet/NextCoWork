import { describe, expect, it } from 'vitest'
import { parseV3Latest, v3RidOf } from '../domain/v3-upgrade'

const latest = (items: unknown[]): unknown => ({ code: 200, message: 'success', data: { channel: 'stable', items } })
const item = (over: Record<string, unknown> = {}): Record<string, unknown> => ({
  rid: 'osx-arm64', version: '3.0.0', publishedAt: '2026-11-20T00:00:00Z', notes: '## 3.0', mandatory: false,
  downloads: [{ fileName: 'NextCoWork-stable-Setup.pkg', kind: 'installer-pkg', sha256: 'a'.repeat(64), size: 1, url: '/api/client/updates/v3/stable/osx-arm64/NextCoWork-stable-Setup.pkg' }],
  ...over
})

describe('v3RidOf', () => {
  it('maps the four 2.x targets and the two arm64 Windows / Linux ones to 3.x RIDs', () => {
    expect(v3RidOf('darwin', 'arm64')).toBe('osx-arm64')
    expect(v3RidOf('darwin', 'x64')).toBe('osx-x64')
    expect(v3RidOf('win32', 'x64')).toBe('win-x64')
    expect(v3RidOf('linux', 'x64')).toBe('linux-x64')
    expect(v3RidOf('linux', 'arm64')).toBe('linux-arm64')
  })

  it('returns null for platforms 3.x does not ship', () => {
    expect(v3RidOf('freebsd', 'x64')).toBeNull()
    expect(v3RidOf('win32', 'ia32')).toBeNull()
  })
})

describe('parseV3Latest', () => {
  it('picks the entry for this RID', () => {
    const body = latest([item({ rid: 'win-x64', version: '3.0.1' }), item()])
    expect(parseV3Latest(body, 'osx-arm64')).toEqual({ version: '3.0.0', publishedAt: '2026-11-20T00:00:00Z', notes: '## 3.0' })
    expect(parseV3Latest(body, 'win-x64')?.version).toBe('3.0.1')
  })

  it('is null when this platform has no entry or no installer to download', () => {
    expect(parseV3Latest(latest([item({ rid: 'win-x64' })]), 'osx-arm64')).toBeNull()
    expect(parseV3Latest(latest([item({ downloads: [] })]), 'osx-arm64')).toBeNull()
    expect(parseV3Latest(latest([]), 'osx-arm64')).toBeNull()
  })

  it('never offers a 2.x version as the 3.0 upgrade', () => {
    expect(parseV3Latest(latest([item({ version: '2.9.1' })]), 'osx-arm64')).toBeNull()
    expect(parseV3Latest(latest([item({ version: 'not-a-version' })]), 'osx-arm64')).toBeNull()
    expect(parseV3Latest(latest([item({ version: '3.0.0-beta.2' })]), 'osx-arm64')?.version).toBe('3.0.0-beta.2')
  })

  it('omits empty notes and a missing date instead of inventing them', () => {
    expect(parseV3Latest(latest([item({ notes: '  ', publishedAt: undefined })]), 'osx-arm64')).toEqual({ version: '3.0.0' })
  })

  it('treats any unexpected shape as "no offer" without throwing', () => {
    for (const body of [null, 'x', 42, {}, { data: null }, { data: { items: 'x' } }, latest([null, 'x', { rid: 'osx-arm64' }])]) {
      expect(parseV3Latest(body, 'osx-arm64')).toBeNull()
    }
  })
})
