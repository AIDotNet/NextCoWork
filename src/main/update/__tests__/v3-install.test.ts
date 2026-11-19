import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import type { V3File, V3InstallState } from '../../../shared/domain/v3-upgrade'
import { V3Installer, macSwapScript, psq, resolveFeedUrl, runningBundleOf, shq, windowsInstallCommand, type InstallerDeps } from '../v3-install'

let root: string
beforeEach(() => { root = mkdtempSync(join(tmpdir(), 'ncw-v3-install-')) })
afterEach(() => { rmSync(root, { recursive: true, force: true }) })

const bytes = Buffer.from('pretend this is a 3.0 package')
const fileFor = (data: Buffer, over: Partial<V3File> = {}): V3File => ({
  fileName: 'NextCoWork-stable-Portable.zip', kind: 'portable', sha256: createHash('sha256').update(data).digest('hex'),
  size: data.length, url: '/api/client/updates/v3/stable/osx-arm64/NextCoWork-stable-Portable.zip', ...over
})
const bodyOf = (data: Buffer, chunk = 7): NonNullable<Awaited<ReturnType<InstallerDeps['fetch']>>['body']> => {
  let at = 0
  return { getReader: () => ({ read: async () => {
    if (at >= data.length) return { done: true }
    const value = new Uint8Array(data.subarray(at, at + chunk)); at += chunk
    return { done: false, value }
  } }) }
}

function rig(platform: string, data: Buffer = bytes, over: Partial<InstallerDeps> = {}) {
  const apps = join(root, 'Applications')
  mkdirSync(apps, { recursive: true })
  const states: V3InstallState[] = []
  const spawned: Array<{ command: string; args: string[] }> = []
  const urls: string[] = []
  const deps: InstallerDeps = {
    fetch: async (url) => { urls.push(url); return { ok: true, body: bodyOf(data) } },
    run: async () => {},
    spawnDetached: (command, args) => { spawned.push({ command, args }) },
    platform, execPath: join(apps, 'NextCoWork.app', 'Contents', 'MacOS', 'NextCoWork'), pid: 4242,
    home: join(root, 'home'), workDir: join(root, 'work'),
    onState: (s) => states.push(s), ...over
  }
  return { installer: new V3Installer(deps), states, spawned, urls, apps }
}

describe('helpers', () => {
  it('only trusts nextco.work over https', () => {
    expect(resolveFeedUrl('/api/client/updates/v3/stable/x.zip')).toBe('https://nextco.work/api/client/updates/v3/stable/x.zip')
    expect(resolveFeedUrl('https://nextco.work/a.zip')).toBe('https://nextco.work/a.zip')
    expect(resolveFeedUrl('https://evil.example/a.zip')).toBeNull()
    expect(resolveFeedUrl('http://nextco.work/a.zip')).toBeNull()
    expect(resolveFeedUrl('//evil.example/a.zip')).toBeNull()
  })

  it('finds the running .app bundle', () => {
    expect(runningBundleOf('/Applications/NextCoWork.app/Contents/MacOS/NextCoWork')).toBe('/Applications/NextCoWork.app')
    expect(runningBundleOf('/usr/local/bin/electron')).toBeNull()
  })

  it('quotes for sh and PowerShell', () => {
    expect(shq("a'b")).toBe(`'a'\\''b'`)
    expect(psq("C:\\it's\\Setup.exe")).toBe("'C:\\it''s\\Setup.exe'")
    expect(windowsInstallCommand(7, 'C:\\t\\Setup.exe')).toContain("Wait-Process -Id 7")
  })
})

describe('V3Installer.download', () => {
  it('downloads, verifies the checksum, unpacks and verifies the signature on macOS', async () => {
    const calls: string[] = []
    const { installer, states, urls } = rig('darwin', bytes, {
      run: async (command, args) => {
        calls.push(`${command.split('/').pop()} ${args[0]}`)
        if (command.endsWith('ditto')) {
          const app = join(args[args.length - 1]!, 'NextCoWork.app', 'Contents', 'MacOS')
          mkdirSync(app, { recursive: true }); writeFileSync(join(app, 'sq.version'), '3.0.0')
        }
      }
    })
    await installer.download(fileFor(bytes))
    expect(urls).toEqual(['https://nextco.work/api/client/updates/v3/stable/osx-arm64/NextCoWork-stable-Portable.zip'])
    expect(calls).toEqual(['ditto -x', 'codesign --verify'])
    expect(states.map((s) => s.phase)).toEqual(expect.arrayContaining(['downloading', 'verifying', 'ready']))
    expect(states.at(-1)).toEqual({ phase: 'ready' })
  })

  it('discards a download whose checksum does not match', async () => {
    const { installer, states } = rig('win32')
    await installer.download(fileFor(bytes, { kind: 'velopack-setup', sha256: 'a'.repeat(64) }))
    expect(states.at(-1)).toEqual({ phase: 'error', code: 'checksum' })
    expect(installer.prepareInstall()).toBe(false)
  })

  it('reports a short download as a network error', async () => {
    const { installer, states } = rig('win32', bytes.subarray(0, 5) as Buffer)
    await installer.download(fileFor(bytes, { kind: 'velopack-setup' }))
    expect(states.at(-1)).toEqual({ phase: 'error', code: 'network' })
  })

  it('refuses a foreign host and a kind this platform cannot install', async () => {
    const a = rig('win32')
    await a.installer.download(fileFor(bytes, { kind: 'velopack-setup', url: 'https://evil.example/Setup.exe' }))
    expect(a.states.at(-1)).toEqual({ phase: 'error', code: 'unsupported' })
    expect(a.urls).toEqual([])
    const b = rig('win32')
    await b.installer.download(fileFor(bytes))
    expect(b.states.at(-1)).toEqual({ phase: 'error', code: 'unsupported' })
  })

  it('does not download 90 MB it cannot install: macOS app folder not writable / not an .app', async () => {
    const a = rig('darwin', bytes, { execPath: '/usr/local/bin/electron' })
    await a.installer.download(fileFor(bytes))
    expect(a.states.at(-1)).toEqual({ phase: 'error', code: 'permission' })
    expect(a.urls).toEqual([])
  })

  it('maps a failed codesign check to a signature error and a missing sq.version to an extract error', async () => {
    const stage = (args: string[]): void => {
      const app = join(args[args.length - 1]!, 'NextCoWork.app', 'Contents', 'MacOS')
      mkdirSync(app, { recursive: true }); writeFileSync(join(app, 'sq.version'), '3.0.0')
    }
    const sig = rig('darwin', bytes, { run: async (c, a) => { if (c.endsWith('ditto')) stage(a); else throw new Error('bad') } })
    await sig.installer.download(fileFor(bytes))
    expect(sig.states.at(-1)).toEqual({ phase: 'error', code: 'signature' })
    const ext = rig('darwin', bytes, { run: async (c, a) => { if (c.endsWith('ditto')) mkdirSync(join(a[a.length - 1]!, 'NextCoWork.app'), { recursive: true }) } })
    await ext.installer.download(fileFor(bytes))
    expect(ext.states.at(-1)).toEqual({ phase: 'error', code: 'extract' })
  })

  it('a network failure can be retried', async () => {
    let first = true
    const { installer, states } = rig('win32', bytes, { fetch: async () => { if (first) { first = false; throw new Error('offline') } return { ok: true, body: bodyOf(bytes) } } })
    const file = fileFor(bytes, { kind: 'velopack-setup' })
    await installer.download(file)
    expect(states.at(-1)).toEqual({ phase: 'error', code: 'network' })
    await installer.download(file)
    expect(states.at(-1)).toEqual({ phase: 'ready' })
  })
})

describe('V3Installer.prepareInstall', () => {
  it('does nothing until a download is ready', () => {
    const { installer, spawned } = rig('win32')
    expect(installer.prepareInstall()).toBe(false)
    expect(spawned).toEqual([])
  })

  it('Windows: starts a detached PowerShell that waits for 2.x to exit, then runs Setup.exe', async () => {
    const { installer, spawned, states } = rig('win32')
    await installer.download(fileFor(bytes, { kind: 'velopack-setup', fileName: 'NextCoWork-stable-Setup.exe' }))
    expect(installer.prepareInstall()).toBe(true)
    expect(spawned).toHaveLength(1)
    expect(spawned[0]!.command).toBe('powershell.exe')
    const command = spawned[0]!.args.at(-1)!
    expect(command).toContain('Wait-Process -Id 4242')
    expect(command).toContain('NextCoWork-stable-Setup.exe')
    expect(states.at(-1)).toEqual({ phase: 'installing' })
    installer.installCancelled()
    expect(states.at(-1)).toEqual({ phase: 'ready' })
  })

  it('Linux: puts the AppImage in ~/Applications, executable, and launches it after 2.x exits', async () => {
    const { installer, spawned } = rig('linux')
    await installer.download(fileFor(bytes, { kind: 'appimage', fileName: 'NextCoWork-stable.AppImage' }))
    expect(installer.prepareInstall()).toBe(true)
    const target = join(root, 'home', 'Applications', 'NextCoWork-3.AppImage')
    expect(readFileSync(target)).toEqual(bytes)
    expect(spawned[0]!.args.slice(1)).toEqual(['4242', target])
  })
})

describe('macSwapScript (run for real with /bin/sh)', () => {
  const sandbox = (): { script: string; stage: string; target: string; backup: string } => {
    const dir = join(root, 'sw'); mkdirSync(dir, { recursive: true })
    const script = join(dir, 'swap.sh'); writeFileSync(script, macSwapScript(), { mode: 0o755 })
    const target = join(dir, 'NextCoWork.app'); mkdirSync(target); writeFileSync(join(target, 'v'), '2.9.0')
    const stage = join(dir, '.stage-app'); mkdirSync(stage); writeFileSync(join(stage, 'v'), '3.0.0')
    return { script, stage, target, backup: join(dir, 'bak', 'NextCoWork.app') }
  }

  it('moves 2.x to the backup and puts 3.0 in its place (open is stubbed out)', () => {
    const { script, stage, target, backup } = sandbox()
    const bin = join(root, 'bin'); mkdirSync(bin)
    writeFileSync(join(bin, 'open'), '#!/bin/sh\necho "$1" > "$OPEN_LOG"\n', { mode: 0o755 })
    const log = join(root, 'open.log')
    execFileSync('/bin/sh', [script, '99999999', stage, target, backup], { env: { ...process.env, PATH: `${bin}:${process.env.PATH}`, OPEN_LOG: log } })
    expect(readFileSync(join(target, 'v'), 'utf8')).toBe('3.0.0')
    expect(readFileSync(join(backup, 'v'), 'utf8')).toBe('2.9.0')
    expect(readFileSync(log, 'utf8').trim()).toBe(target)
  })

  it('leaves everything alone when the staged bundle is gone (e.g. a second, stale script)', () => {
    const { script, stage, target, backup } = sandbox()
    rmSync(stage, { recursive: true })
    expect(() => execFileSync('/bin/sh', [script, '99999999', stage, target, backup])).toThrow()
    expect(readFileSync(join(target, 'v'), 'utf8')).toBe('2.9.0')
    expect(existsSync(backup)).toBe(false)
  })
})
