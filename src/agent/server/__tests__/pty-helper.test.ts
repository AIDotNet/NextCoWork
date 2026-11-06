import { execFileSync } from 'node:child_process'
import { chmodSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { ensurePtyHelperExecutable } from '../pty-helper'

let root = ''
beforeEach(() => { root = mkdtempSync(join(tmpdir(), 'ncw-pty-helper-')) })
afterEach(() => { rmSync(root, { recursive: true, force: true }) })

const mode = (path: string): number => statSync(path).mode & 0o777
const helper = (...dir: readonly string[]): string => join(root, ...dir, 'spawn-helper')

/** 造一个假的 node-pty:真包与它只差这些文件,`pty.node` 与 `spawn-helper` 同目录才是重点。 */
function fakeNodePty(dirs: readonly string[], helperMode = 0o644): (specifier: string) => string {
  for (const dir of dirs) {
    mkdirSync(join(root, dir), { recursive: true })
    writeFileSync(join(root, dir, 'pty.node'), 'native')
    writeFileSync(join(root, dir, 'spawn-helper'), 'helper')
    chmodSync(join(root, dir, 'spawn-helper'), helperMode)
  }
  writeFileSync(join(root, 'package.json'), '{"name":"node-pty","version":"1.1.0"}')
  return () => join(root, 'package.json')
}

/*
  ★ 这一整个 describe 在 Windows 上跳过:`chmod` 在 Win32 上只翻转只读位,`stat` 回来的 mode
  恒是 `644 | 666 & umask`(CI 实测 0o666),1/4/8 三个位怎么设都读不回来。这些用例要断的正是
  「执行位有没有被设上 / 有没有被动过」,而 Win32 根本表达不了这件事 —— 不是断言写错了平台,
  是这个平台没有可断言的对象。净化逻辑本身在 win32 上第一行就 return,已由下面那条用例覆盖。
*/
describe.skipIf(process.platform === 'win32')('node-pty helper 的执行位', () => {
  it('gives a prebuilt spawn-helper its exec bit and keeps its contents', () => {
    const resolve = fakeNodePty(['prebuilds/darwin-arm64'])
    expect(ensurePtyHelperExecutable({ platform: 'darwin', arch: 'arm64', resolve })).toBeNull()
    expect(mode(helper('prebuilds', 'darwin-arm64'))).toBe(0o755)
  })

  it('leaves an already executable helper exactly as it is', () => {
    const resolve = fakeNodePty(['prebuilds/darwin-arm64'], 0o700)
    expect(ensurePtyHelperExecutable({ platform: 'darwin', arch: 'arm64', resolve })).toBeNull()
    expect(mode(helper('prebuilds', 'darwin-arm64'))).toBe(0o700)
  })

  it('fixes the helper in the directory node-pty actually loads the native module from', () => {
    // 只有 build/Debug 与 prebuilds 时,node-pty 载入 build/Debug —— 补 prebuilds 等于没补。
    const resolve = fakeNodePty(['prebuilds/darwin-arm64', 'build/Debug'])
    expect(ensurePtyHelperExecutable({ platform: 'darwin', arch: 'arm64', resolve })).toBeNull()
    expect(mode(helper('build', 'Debug'))).toBe(0o755)
    expect(mode(helper('prebuilds', 'darwin-arm64'))).toBe(0o644)
  })

  it('does nothing when node-pty is not installed or when the platform does not use exec bits', () => {
    const absent = (): string => { throw new Error('Cannot find module node-pty') }
    expect(ensurePtyHelperExecutable({ platform: 'darwin', arch: 'arm64', resolve: absent })).toBeNull()
  })

  // chflags uchg 是 macOS 上唯一不需要 root 就能让 chmod 失败的办法;Linux 上要 root 才能 chattr +i。
  it.skipIf(process.platform !== 'darwin')('reports why the exec bit could not be set instead of throwing', () => {
    const resolve = fakeNodePty(['prebuilds/darwin-arm64'])
    execFileSync('chflags', ['uchg', helper('prebuilds', 'darwin-arm64')])
    try {
      expect(ensurePtyHelperExecutable({ platform: 'darwin', arch: 'arm64', resolve })).toContain('could not be made executable')
    } finally {
      execFileSync('chflags', ['nouchg', helper('prebuilds', 'darwin-arm64')])
    }
  })
})

/*
  win32 分支在任何平台上都跑得起来(它只看 `platform` 参数),所以在 Windows 上也留着 ——
  这里要断的是「这个平台没有执行位,一个字节都不许动」。
*/
const windowsHelper = (): { resolve: (specifier: string) => string; file: string } => {
  const root = mkdtempSync(join(tmpdir(), 'ncw-pty-helper-win-'))
  mkdirSync(join(root, 'prebuilds', 'win32-x64'), { recursive: true })
  writeFileSync(join(root, 'prebuilds', 'win32-x64', 'pty.node'), 'native')
  const file = join(root, 'prebuilds', 'win32-x64', 'spawn-helper')
  writeFileSync(file, 'helper')
  writeFileSync(join(root, 'package.json'), '{"name":"node-pty","version":"1.1.0"}')
  return { resolve: () => join(root, 'package.json'), file }
}

describe('node-pty helper 在 win32 上不动文件', () => {
  it('returns null and leaves the file untouched', () => {
    const { resolve, file } = windowsHelper()
    try {
      const before = statSync(file).mode
      expect(ensurePtyHelperExecutable({ platform: 'win32', arch: 'x64', resolve })).toBeNull()
      expect(statSync(file).mode).toBe(before)
      expect(readFileSync(file, 'utf8')).toBe('helper')
      // POSIX 上顺带确认没被执行位逻辑碰过;Windows 上 mode 根本不可信,只断上面两条。
      if (process.platform !== 'win32') expect(statSync(file).mode & 0o777).toBe(0o644)
    } finally {
      rmSync(dirname(file), { recursive: true, force: true })
    }
  })
})
