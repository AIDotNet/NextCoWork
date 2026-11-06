import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { closeDatabase, openDatabase, stmt } from '../../../main/db'
import { runTakeoverCli, TakeoverUsageError } from '../takeover-cli'

let dir = ''
const lines: string[] = []
const io = { out: (line: string) => { lines.push(line) }, err: (line: string) => { lines.push(line) } }
const dead = (): boolean => false

beforeEach(() => {
  closeDatabase()
  dir = mkdtempSync(join(tmpdir(), 'ncw-takeover-cli-'))
  openDatabase(dir)
  stmt('INSERT INTO kv (key, json) VALUES (?, ?)').run('k', '{}')
  closeDatabase()
  lines.length = 0
})
afterEach(() => { closeDatabase(); rmSync(dir, { recursive: true, force: true }) })

describe('takeover command line', () => {
  it('rejects bad usage', () => {
    for (const bad of [[], ['launch'], ['begin'], ['begin', '--data-dir'], ['begin', '--data-dir', dir, '--bogus'], ['begin', '--data-dir', dir, '--acknowledge-data-loss']]) {
      expect(() => runTakeoverCli(bad, io)).toThrow(TakeoverUsageError)
    }
  })

  it('walks begin → commit → rollback with the explicit acknowledgement, printing only phases and locations', () => {
    runTakeoverCli(['status', '--data-dir', dir], io)
    expect(lines.at(-1)).toBe('no takeover in progress')
    runTakeoverCli(['begin', '--data-dir', dir], io, { isAlive: dead })
    expect(lines.join('\n')).toMatch(/takeover [0-9a-f-]+: verified/)
    runTakeoverCli(['commit', '--data-dir', dir], io, { isAlive: dead })
    expect(lines.at(-2)).toMatch(/: committed$/)
    expect(() => runTakeoverCli(['rollback', '--data-dir', dir], io, { isAlive: dead })).toThrowError(expect.objectContaining({ code: 'forbidden' }))
    runTakeoverCli(['rollback', '--data-dir', dir, '--acknowledge-data-loss'], io, { isAlive: dead })
    expect(lines.join('\n')).toMatch(/: rolled-back/)
    expect(lines.join('\n')).toContain('data written after the takeover was kept at:')
  })
})
