import { mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { parseRelayArgs, RelayUsageError, runRelayCli } from '../main'

let dir = ''
const lines: string[] = []
const io = { out: (line: string) => { lines.push(line) }, err: (line: string) => { lines.push(line) } }
beforeEach(() => { dir = mkdtempSync(join(tmpdir(), 'ncw-relay-cli-')); lines.length = 0 })
afterEach(() => { rmSync(dir, { recursive: true, force: true }) })

describe('relay command line', () => {
  it('parses strictly', () => {
    expect(parseRelayArgs(['serve', '--registrations', 'r.json', '--behind-tls']).bare.has('--behind-tls')).toBe(true)
    for (const bad of [[], ['launch'], ['serve'], ['serve', '--registrations'], ['register', '--registrations', 'r.json'],
      ['serve', '--registrations', 'r.json', '--bogus']]) {
      expect(() => parseRelayArgs(bad)).toThrow(RelayUsageError)
    }
  })

  it('registers a device, printing the credential once and storing only its digest with 0600 permissions', async () => {
    const file = join(dir, 'registrations.json')
    await runRelayCli(['register', '--registrations', file, '--device', 'dev-a'], io)
    const token = lines[0]!
    expect(token.length).toBeGreaterThanOrEqual(43)
    const stored = readFileSync(file, 'utf8')
    expect(stored).not.toContain(token)
    expect(Object.keys(JSON.parse(stored))).toEqual(['dev-a'])
    if (process.platform !== 'win32') expect(statSync(file).mode & 0o077).toBe(0)
    await runRelayCli(['revoke', '--registrations', file, '--device', 'dev-a'], io)
    expect(lines.at(-1)).toBe('revoked')
    await runRelayCli(['revoke', '--registrations', file, '--device', 'dev-a'], io)
    expect(lines.at(-1)).toBe('not registered')
  })

  it('refuses plaintext serving unless TLS termination is declared, and rejects broken registrations files', async () => {
    const file = join(dir, 'registrations.json')
    await expect(runRelayCli(['serve', '--registrations', file], io)).rejects.toThrow(/plaintext/)
    writeFileSync(file, '{ not json')
    await expect(runRelayCli(['serve', '--registrations', file, '--behind-tls'], io)).rejects.toThrow(RelayUsageError)
    writeFileSync(file, JSON.stringify({ 'dev-a': 'not-a-hash' }))
    await expect(runRelayCli(['serve', '--registrations', file, '--behind-tls'], io)).rejects.toThrow(RelayUsageError)
  })

  it('serves behind TLS and picks up new registrations and revocations on reload without a restart', async () => {
    const file = join(dir, 'registrations.json')
    const relay = (await runRelayCli(['serve', '--registrations', file, '--behind-tls', '--port', '0'], io))!
    try {
      // behind-tls 下对外是 ws:没有 TLS 的测试里用原始 ws 客户端验证认证行为。
      const { default: WebSocket } = await import('ws')
      const attempt = (deviceId: string, token: string) => new Promise<number>((resolve) => {
        const ws = new WebSocket(`ws://127.0.0.1:${relay.address().port}/v1/device`, 'ncw-relay-v1',
          { headers: { 'x-ncw-device-id': deviceId, authorization: `Bearer ${token}` } })
        ws.on('open', () => { ws.terminate(); resolve(101) })
        ws.on('unexpected-response', (_request, response) => { response.resume(); resolve(response.statusCode ?? 0) })
        ws.on('error', () => {})
      })
      expect(await attempt('dev-b', 'x'.repeat(43))).toBe(401)
      await runRelayCli(['register', '--registrations', file, '--device', 'dev-b'], io)
      const token = lines.at(-1)!
      expect(await attempt('dev-b', token)).toBe(401) // 还没重新加载
      relay.reload()
      expect(await attempt('dev-b', token)).toBe(101)
      await runRelayCli(['revoke', '--registrations', file, '--device', 'dev-b'], io)
      relay.reload()
      expect(await attempt('dev-b', token)).toBe(401)
    } finally { await relay.close() }
  })
})
