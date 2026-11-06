/**
 * 原生组件 + 文档引擎的清单规则。
 *
 * 这一族断言钉的是计划 §4.1 / §7 的装载期不变式:原生入口只能在 `native/` 下、
 * 引用必须闭合、旧清单解析结果一个字段都不变。任何一条松动的症状都是
 * 「插件装上了,第一次打开 .docx 才失败」。
 */
import { describe, expect, it } from 'vitest'
import { parsePluginManifest } from '../manifest'
import { isNativePackagePath, selectNativeTarget } from '../native-component'

const SHA = 'a'.repeat(64)

const BASE = {
  publisher: 'ncw',
  name: 'office-runtime',
  displayName: 'Office Runtime',
  description: 'demo',
  version: '1.0.0',
  engines: { nextcowork: '^0.3.0' },
  main: './dist/extension.js'
}

const COMPONENT = {
  id: 'libreoffice',
  version: '26.8.0',
  protocol: 1,
  targets: [
    { platform: 'darwin', arch: 'arm64', entry: 'native/darwin-arm64/ncw-office-helper', sha256: SHA },
    { platform: 'linux', arch: 'x64', entry: 'native/linux-x64/ncw-office-helper', sha256: SHA }
  ],
  license: { spdx: 'MPL-2.0 AND LGPL-3.0-or-later', notices: 'licenses/NOTICE.txt', source: 'https://example.com/src' }
}

function parse(extra: Record<string, unknown>): ReturnType<typeof parsePluginManifest> {
  return parsePluginManifest({ ...BASE, ...extra })
}

function errorFields(result: ReturnType<typeof parsePluginManifest>): string[] {
  return result.ok ? [] : result.errors.map((error) => error.field)
}

describe('nativeComponents', () => {
  it('accepts a component whose entries live under native/ and exposes it on the manifest', () => {
    const result = parse({ nativeComponents: [COMPONENT] })
    expect(result.ok, JSON.stringify(errorFields(result))).toBe(true)
    if (!result.ok) return
    expect(result.manifest.nativeComponents?.[0]?.targets).toHaveLength(2)
  })

  it('leaves manifests without native components byte-for-byte unchanged (no new keys)', () => {
    const result = parse({})
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect('nativeComponents' in result.manifest).toBe(false)
    expect('documentEngines' in result.manifest.contributes).toBe(false)
  })

  it('rejects an entry outside native/ so the view protocol can never serve engine binaries', () => {
    const bad = { ...COMPONENT, targets: [{ ...COMPONENT.targets[0], entry: 'dist/helper' }] }
    expect(errorFields(parse({ nativeComponents: [bad] }))).toContain('nativeComponents.libreoffice.targets')
  })

  it('rejects traversal, missing digests, unknown platforms and duplicate targets', () => {
    const cases = [
      { ...COMPONENT.targets[0], entry: 'native/../dist/x' },
      { ...COMPONENT.targets[0], sha256: 'abc' },
      { ...COMPONENT.targets[0], platform: 'freebsd' }
    ]
    for (const target of cases) {
      expect(parse({ nativeComponents: [{ ...COMPONENT, targets: [target] }] }).ok).toBe(false)
    }
    expect(parse({ nativeComponents: [{ ...COMPONENT, targets: [COMPONENT.targets[0], COMPONENT.targets[0]] }] }).ok).toBe(false)
  })

  it('refuses a helper protocol newer than the host instead of failing at first open', () => {
    expect(errorFields(parse({ nativeComponents: [{ ...COMPONENT, protocol: 99 }] }))).toContain('nativeComponents.libreoffice.protocol')
  })

  it('requires license notices inside the package', () => {
    expect(parse({ nativeComponents: [{ ...COMPONENT, license: { spdx: 'MPL-2.0', notices: '/etc/passwd' } }] }).ok).toBe(false)
    expect(parse({ nativeComponents: [{ ...COMPONENT, license: { spdx: 'MPL-2.0', notices: 'n.txt', source: 'http://x' } }] }).ok).toBe(false)
  })

  it('forbids native components in zero-code webapp plugins', () => {
    const result = parsePluginManifest({
      ...BASE,
      main: undefined,
      kind: 'webapp',
      nativeComponents: [COMPONENT],
      contributes: { webApps: [{ id: 'home', title: '%app.home%', url: 'https://example.com/' }] }
    })
    expect(errorFields(result)).toContain('nativeComponents')
  })

  it('selects only the exact platform/arch target and never falls back to another arch', () => {
    const result = parse({ nativeComponents: [COMPONENT] })
    if (!result.ok) throw new Error('fixture must parse')
    const component = result.manifest.nativeComponents?.[0]
    if (component === undefined) throw new Error('missing component')
    expect(selectNativeTarget(component, 'darwin', 'arm64')?.entry).toBe('native/darwin-arm64/ncw-office-helper')
    expect(selectNativeTarget(component, 'darwin', 'x64')).toBeNull()
    expect(selectNativeTarget(component, 'win32', 'x64')).toBeNull()
  })

  it('classifies native package paths case-insensitively', () => {
    expect(isNativePackagePath('native/x.dylib')).toBe(true)
    expect(isNativePackagePath('Native/x.dylib')).toBe(true)
    expect(isNativePackagePath('./native')).toBe(true)
    expect(isNativePackagePath('dist/native.js')).toBe(false)
  })
})

describe('documentEngines + customEditors binding', () => {
  const VIEWS = [
    { id: 'word', title: '%v.word%', path: 'dist/view/word.html' },
    { id: 'sheet', title: '%v.sheet%', path: 'dist/view/sheet.html' }
  ]

  it('accepts an engine backed by its own native component and an editor bound to it', () => {
    const result = parse({
      nativeComponents: [COMPONENT],
      contributes: {
        documentEngines: [{ id: 'office', component: 'libreoffice', formats: ['docx', 'docm', 'docx'] }],
        customEditors: [{ viewType: 'word', displayName: '%e.word%', selector: [{ filenamePattern: '*.docx' }], viewId: 'word', documentEngine: 'office' }],
        views: VIEWS
      }
    })
    expect(result.ok, JSON.stringify(errorFields(result))).toBe(true)
    if (!result.ok) return
    expect(result.manifest.contributes.documentEngines?.[0]?.formats).toEqual(['docx', 'docm'])
    expect(result.manifest.contributes.customEditors[0]).toMatchObject({ viewId: 'word', documentEngine: 'office' })
  })

  it('rejects formats outside the launch whitelist (legacy .doc is not promised)', () => {
    const result = parse({
      nativeComponents: [COMPONENT],
      contributes: { documentEngines: [{ id: 'office', component: 'libreoffice', formats: ['doc'] }] }
    })
    expect(errorFields(result)).toContain('contributes.documentEngines.office.formats')
  })

  it('rejects an engine whose component is not declared by the same plugin', () => {
    const result = parse({ contributes: { documentEngines: [{ id: 'office', component: 'libreoffice', formats: ['docx'] }] } })
    expect(errorFields(result)).toContain('contributes.documentEngines.office.component')
  })

  it('rejects a viewId that points at no view, instead of silently falling back to views[0]', () => {
    const result = parse({
      contributes: {
        customEditors: [{ viewType: 'sheet', displayName: '%e.sheet%', selector: [{ filenamePattern: '*.xlsx' }], viewId: 'missing' }],
        views: VIEWS
      }
    })
    expect(errorFields(result)).toContain('contributes.customEditors.sheet.viewId')
  })

  it('requires cross-plugin engine references to be declared dependencies', () => {
    const editor = { viewType: 'word', displayName: '%e.word%', selector: [{ filenamePattern: '*.docx' }], documentEngine: 'ncw.office-runtime/office' }
    const without = parse({ name: 'writer', contributes: { customEditors: [editor], views: VIEWS } })
    expect(errorFields(without)).toContain('contributes.customEditors.word.documentEngine')
    const withDep = parse({ name: 'writer', dependencies: { 'ncw.office-runtime': '^1.0.0' }, contributes: { customEditors: [editor], views: VIEWS } })
    expect(withDep.ok, JSON.stringify(errorFields(withDep))).toBe(true)
  })

  it('rejects a local engine reference with no matching engine', () => {
    const result = parse({
      contributes: {
        customEditors: [{ viewType: 'word', displayName: '%e.word%', selector: [{ filenamePattern: '*.docx' }], documentEngine: 'office' }],
        views: VIEWS
      }
    })
    expect(errorFields(result)).toContain('contributes.customEditors.word.documentEngine')
  })
})
