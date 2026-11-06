/**
 * 安装器对原生组件的核对 —— 走真实的 `installPluginDirectory`,不 mock。
 *
 * 钉住的是计划 §4.2 的安装期不变式:摘要不符 / 本机无构建 / 任一平台入口缺失时整次
 * 安装拒绝且保留旧版本;通过时入口被赋可执行位;运行期核对能发现安装后被替换的入口。
 */
import { createHash } from 'node:crypto'
import { mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { installPluginDirectory } from '../installer'
import { resolveVerifiedEntry } from '../native-installer'
import type { NativeArch, NativeComponent, NativePlatform } from '../../../shared/plugin/native-component'

let work: string
const sha = (value: string): string => createHash('sha256').update(value).digest('hex')
const here = `${process.platform}-${process.arch}`

beforeEach(() => { work = mkdtempSync(join(tmpdir(), 'ncw-native-install-')) })
afterEach(() => { rmSync(work, { recursive: true, force: true }) })

function writePackage(dir: string, options: { helper?: string; digest?: string; otherEntryPresent?: boolean; version?: string; platform?: string } = {}): NativeComponent {
  const helper = options.helper ?? 'helper-binary'
  const platform = (options.platform ?? process.platform) as NativePlatform
  const component: NativeComponent = {
    id: 'libreoffice',
    version: '26.8.0',
    protocol: 1,
    targets: [
      { platform, arch: process.arch as NativeArch, entry: `native/${here}/helper`, sha256: options.digest ?? sha(helper) },
      { platform: process.platform === 'win32' ? 'linux' : 'win32', arch: 'x64', entry: 'native/other/helper.exe', sha256: sha('other') }
    ],
    license: { spdx: 'MPL-2.0', notices: 'licenses/NOTICE.txt' }
  }
  const manifest = {
    publisher: 'ncw', name: 'office-runtime', displayName: 'Office Runtime', description: 'd',
    version: options.version ?? '1.0.0', engines: { nextcowork: '^0.3.0' }, main: './dist/extension.js',
    nativeComponents: [component]
  }
  mkdirSync(join(dir, 'dist'), { recursive: true })
  mkdirSync(join(dir, 'native', here), { recursive: true })
  mkdirSync(join(dir, 'licenses'), { recursive: true })
  writeFileSync(join(dir, 'package.json'), JSON.stringify(manifest))
  writeFileSync(join(dir, 'dist', 'extension.js'), 'export function activate() {}')
  writeFileSync(join(dir, 'native', here, 'helper'), helper)
  writeFileSync(join(dir, 'licenses', 'NOTICE.txt'), 'notices')
  if (options.otherEntryPresent !== false) {
    mkdirSync(join(dir, 'native', 'other'), { recursive: true })
    writeFileSync(join(dir, 'native', 'other', 'helper.exe'), 'other')
  }
  return component
}

describe('native component install checks', () => {
  it('installs when the local target digest matches and marks the entry executable', async () => {
    const source = join(work, 'src')
    writePackage(source)
    const { target } = await installPluginDirectory(source, join(work, 'plugins'))
    const entry = join(target, 'native', here, 'helper')
    if (process.platform !== 'win32') expect(statSync(entry).mode & 0o111).not.toBe(0)
  })

  it('rejects a digest mismatch and keeps the previously installed version', async () => {
    const plugins = join(work, 'plugins')
    const good = join(work, 'good')
    writePackage(good, { version: '1.0.0' })
    const { target } = await installPluginDirectory(good, plugins)
    const bad = join(work, 'bad')
    writePackage(bad, { version: '1.1.0', digest: sha('something else') })
    await expect(installPluginDirectory(bad, plugins)).rejects.toThrow(/digest does not match/)
    expect(JSON.parse(readFileSync(join(target, 'package.json'), 'utf8')).version).toBe('1.0.0')
  })

  it('rejects a package without a build for this machine', async () => {
    const source = join(work, 'src')
    writePackage(source, { platform: process.platform === 'darwin' ? 'linux' : 'darwin' })
    await expect(installPluginDirectory(source, join(work, 'plugins'))).rejects.toThrow(/has no build for/)
  })

  it('rejects a package missing another platform entry, so it cannot ship broken elsewhere', async () => {
    const source = join(work, 'src')
    writePackage(source, { otherEntryPresent: false })
    await expect(installPluginDirectory(source, join(work, 'plugins'))).rejects.toThrow(/missing its .* entry/)
  })

  it('detects an entry replaced after installation before it could be spawned', async () => {
    const source = join(work, 'src')
    const component = writePackage(source)
    const { target } = await installPluginDirectory(source, join(work, 'plugins'))
    const local = component.targets[0]
    if (local === undefined) throw new Error('fixture')
    await expect(resolveVerifiedEntry(target, component, local)).resolves.toContain('helper')
    writeFileSync(join(target, 'native', here, 'helper'), 'tampered')
    await expect(resolveVerifiedEntry(target, component, local)).rejects.toThrow(/digest does not match/)
  })
})
