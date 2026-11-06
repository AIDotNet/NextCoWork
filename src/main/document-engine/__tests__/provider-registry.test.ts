/**
 * 引擎 provider 登记表(`provider-registry.ts`):按需登记、插件不可用即撤、升级后重建、
 * 禁用时只收干净会话。provider 用替身 —— 这里测的是登记与撤销的时机,不是 helper。
 */
import { describe, expect, it } from 'vitest'
import type { PluginManifest } from '../../../shared/plugin/manifest'
import type { DocumentEngineProvider } from '../manager'
import { DocumentEngineProviderRegistry, splitProviderId, type EngineHostPlugin, type NativeProviderOptions } from '../provider-registry'

function hostPlugin(version = '1.0.0', sha = 'a'.repeat(64)): EngineHostPlugin {
  const manifest = {
    version,
    nativeComponents: [{
      id: 'helper',
      targets: [{ platform: 'darwin', arch: 'arm64', entry: 'native/darwin-arm64/ncw-office-helper', sha256: sha }],
      license: { spdx: 'MPL-2.0', notices: 'native/NOTICE' }
    }],
    contributes: { documentEngines: [{ id: 'office', component: 'helper', formats: ['docx', 'xlsx'] }] }
  } as unknown as PluginManifest
  return { pluginId: 'ncw.office-runtime', root: '/plugins/ncw.office-runtime', manifest }
}

function setup(initial: EngineHostPlugin | null = hostPlugin()): {
  registry: DocumentEngineProviderRegistry
  registered: string[]
  unregistered: string[]
  closed: { id: string | undefined; onlyClean: boolean | undefined }[]
  created: NativeProviderOptions[]
  setPlugin: (plugin: EngineHostPlugin | null) => void
} {
  let plugin = initial
  const registered: string[] = []
  const unregistered: string[] = []
  const closed: { id: string | undefined; onlyClean: boolean | undefined }[] = []
  const created: NativeProviderOptions[] = []
  const registry = new DocumentEngineProviderRegistry({
    sessions: {
      registerProvider: (provider) => { registered.push(provider.id); return () => { unregistered.push(provider.id) } },
      closeAll: async (id, options) => { closed.push({ id, onlyClean: options?.onlyClean }) }
    },
    workRoot: '/work',
    lookupPlugin: (id) => (id === 'ncw.office-runtime' ? plugin : null),
    platform: 'darwin',
    arch: 'arm64',
    createProvider: (options) => {
      created.push(options)
      return { id: options.id, formats: options.formats, open: async () => { throw new Error('unused') } } as DocumentEngineProvider
    }
  })
  return { registry, registered, unregistered, closed, created, setPlugin: (next) => { plugin = next } }
}

describe('DocumentEngineProviderRegistry', () => {
  it('registers lazily on first use and returns the same provider afterwards', () => {
    const { registry, registered, created } = setup()
    expect(registered).toEqual([])
    const first = registry.ensure('ncw.office-runtime/office')
    const second = registry.ensure('ncw.office-runtime/office')
    expect(first).not.toBeNull()
    expect(second).toBe(first)
    expect(registered).toEqual(['ncw.office-runtime/office'])
    expect(created[0]).toMatchObject({ formats: ['docx', 'xlsx'], workRoot: '/work' })
    // 共用缓存目录:helper 之间共享系统字体扫描结果,不必每开一份文档重扫
    expect(created[0]?.args?.[0]).toMatch(/^--cache-dir=.*cache$/)
  })

  it('returns null for unknown engines, malformed ids, or a platform without a build', () => {
    const { registry } = setup()
    expect(registry.ensure('ncw.office-runtime/nope')).toBeNull()
    expect(registry.ensure('no-slash')).toBeNull()
    expect(registry.ensure('a/b/c')).toBeNull()
    const other = new DocumentEngineProviderRegistry({
      sessions: { registerProvider: () => () => undefined, closeAll: async () => undefined },
      workRoot: '/work',
      lookupPlugin: () => hostPlugin(),
      platform: 'win32',
      arch: 'x64'
    })
    expect(other.ensure('ncw.office-runtime/office')).toBeNull()
  })

  it('unregisters a provider once its plugin is no longer enabled', () => {
    const { registry, unregistered, setPlugin } = setup()
    registry.ensure('ncw.office-runtime/office')
    setPlugin(null)
    expect(registry.ensure('ncw.office-runtime/office')).toBeNull()
    expect(unregistered).toEqual(['ncw.office-runtime/office'])
  })

  it('rebuilds the provider after an upgrade changes the verified build', () => {
    const { registry, registered, unregistered, setPlugin } = setup()
    const before = registry.ensure('ncw.office-runtime/office')
    setPlugin(hostPlugin('1.1.0', 'b'.repeat(64)))
    const after = registry.ensure('ncw.office-runtime/office')
    expect(after).not.toBe(before)
    expect(registered).toHaveLength(2)
    expect(unregistered).toEqual(['ncw.office-runtime/office'])
  })

  it('retires only clean sessions, including engines already dropped by ensure', async () => {
    const { registry, closed, setPlugin } = setup()
    registry.ensure('ncw.office-runtime/office')
    setPlugin(null)
    registry.ensure('ncw.office-runtime/office')
    await registry.retire('ncw.office-runtime')
    expect(closed).toEqual([{ id: 'ncw.office-runtime/office', onlyClean: true }])
  })

  it('refuses a cached entry after disable or after its installation root changes', async () => {
    const { registry, created, setPlugin } = setup()
    const before = registry.ensure('ncw.office-runtime/office')
    const resolveEntry = created[0]?.resolveEntry
    expect(resolveEntry).toBeDefined()
    setPlugin(null)
    await expect(resolveEntry?.()).rejects.toMatchObject({ code: 'engine_unavailable' })
    setPlugin({ ...hostPlugin(), root: '/replacement/ncw.office-runtime' })
    await expect(resolveEntry?.()).rejects.toMatchObject({ code: 'engine_unavailable' })
    expect(registry.ensure('ncw.office-runtime/office')).not.toBe(before)
  })

  it('splits provider ids into plugin and engine parts', () => {
    expect(splitProviderId('ncw.office-runtime/office')).toEqual({ pluginId: 'ncw.office-runtime', engineId: 'office' })
    expect(splitProviderId('/office')).toBeNull()
    expect(splitProviderId('ncw.office-runtime/')).toBeNull()
  })
})
