import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { DiagnoseUsageError, realProbes, runChecks, runDiagnoseCli, type Probes } from '../diagnose-cli'

let root = ''
beforeEach(() => { root = mkdtempSync(join(tmpdir(), 'ncw-diagnose-')) })
afterEach(() => { rmSync(root, { recursive: true, force: true }) })

const healthy: Probes = {
  nodeVersion: () => '24.16.0', sqlite: async () => 'sqlite 3.50', pty: async () => 'pty ok', git: async () => 'git version 2.50', dataDir: async () => 'writable'
}
const capture = () => { const lines: string[] = []; return { lines, io: { out: (line: string) => { lines.push(line) }, err: (line: string) => { lines.push(line) } } } }

describe('diagnose CLI', () => {
  it('really probes this machine: sqlite, a pseudo-terminal, git and a writable data directory', async () => {
    const results = await runChecks(join(root, 'data'))
    const byId = Object.fromEntries(results.map((result) => [result.id, result]))
    expect(byId.node).toMatchObject({ status: 'ok', level: 'required' })
    expect(byId.sqlite).toMatchObject({ status: 'ok', detail: expect.stringMatching(/^sqlite \d/) })
    expect(byId.pty).toMatchObject({ status: 'ok', detail: expect.stringContaining('node-pty') })
    expect(byId.git?.status).toBe('ok')
    expect(byId['data-dir']).toMatchObject({ status: 'ok', level: 'required' })
    expect(realProbes.nodeVersion()).toBe(process.versions.node)
  }, 30_000)

  it('reports components that are not shipped with the service as absent, never as installed', async () => {
    const results = await runChecks(undefined, healthy)
    expect(results.filter((result) => result.status === 'absent').map((result) => result.id)).toEqual(['browser', 'office'])
    expect(results.some((result) => result.id === 'data-dir')).toBe(false)
  })

  it('exits 0 when the required checks pass and prints a readable summary', async () => {
    const { lines, io } = capture()
    expect(await runDiagnoseCli(['--data-dir', root], io, healthy)).toBe(0)
    expect(lines.join('\n')).toContain('can run on this machine')
    expect(lines.some((line) => line.startsWith('absent') && line.includes('browser'))).toBe(true)
  })

  it('exits 1 for a failed required check (old Node, broken sqlite, unwritable data dir) but not for an optional one', async () => {
    for (const broken of [
      { ...healthy, nodeVersion: () => '22.1.0' },
      { ...healthy, sqlite: async () => { throw new Error('no node:sqlite') } },
      { ...healthy, dataDir: async () => { throw new Error('read-only file system') } }
    ] satisfies Probes[]) {
      const { lines, io } = capture()
      expect(await runDiagnoseCli(['--data-dir', root], io, broken)).toBe(1)
      expect(lines.join('\n')).toContain('cannot run')
    }
    const optional = capture()
    expect(await runDiagnoseCli([], optional.io, { ...healthy, pty: async () => { throw new Error('no pty') }, git: async () => { throw new Error('no git') } })).toBe(0)
    expect(optional.lines.join('\n')).toContain('(optional) no pty')
  })

  it('prints machine-readable JSON and rejects bad options', async () => {
    const { lines, io } = capture()
    expect(await runDiagnoseCli(['--json'], io, healthy)).toBe(0)
    const parsed = JSON.parse(lines.join('\n')) as { ok: boolean; checks: Array<{ id: string; status: string }> }
    expect(parsed.ok).toBe(true)
    expect(parsed.checks.map((check) => check.id)).toEqual(['node', 'sqlite', 'pty', 'git', 'browser', 'office'])
    await expect(runDiagnoseCli(['--bogus'], io, healthy)).rejects.toBeInstanceOf(DiagnoseUsageError)
    await expect(runDiagnoseCli(['--data-dir'], io, healthy)).rejects.toBeInstanceOf(DiagnoseUsageError)
    await expect(runDiagnoseCli(['--data-dir', '--json'], io, healthy)).rejects.toBeInstanceOf(DiagnoseUsageError)
  })
})
