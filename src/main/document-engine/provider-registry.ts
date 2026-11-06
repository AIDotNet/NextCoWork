/**
 * 文档引擎 provider 登记表。
 *
 * 需求：办公插件被禁用、卸载或升级时，旧的原生 helper 不能继续作为可用引擎；
 * 同时首次打开文档前不应启动任何 helper。这里拥有“已验证清单 → provider”的惰性缓存，
 * 故意不负责文档会话和排版语义——那两件事分别由 `manager.ts` 与引擎插件负责。
 * 不满足会怎样：若把未验证的入口提前登记，用户会在第一次打开文档时才看到平台或摘要错误；
 * 若升级后复用旧 provider，源码已更新但界面仍会使用旧 helper，且全程可能没有明显报错。
 */
import { join } from 'node:path'
import type { PluginManifest } from '../../shared/plugin/manifest'
import { selectNativeTarget, type NativeComponent, type NativeComponentTarget } from '../../shared/plugin/native-component'
import { DocumentEngineError, type DocumentFormat } from '../../shared/document-engine/protocol'
import { NativeDocumentEngineProvider } from './native-host'
import type { DocumentEngineProvider } from './manager'
import { resolveVerifiedEntry } from '../plugin/native-installer'

export interface EngineHostPlugin {
  pluginId: string
  root: string
  manifest: PluginManifest
}

export interface NativeProviderOptions {
  id: string
  formats: readonly DocumentFormat[]
  resolveEntry: () => Promise<string>
  workRoot: string
  args?: string[]
}

interface ProviderRegistryOptions {
  sessions: {
    registerProvider: (provider: DocumentEngineProvider) => () => void
    closeAll: (providerId?: string, options?: { onlyClean?: boolean }) => Promise<void>
  }
  workRoot: string
  lookupPlugin: (pluginId: string) => EngineHostPlugin | null
  platform: string
  arch: string
  createProvider?: (options: NativeProviderOptions) => DocumentEngineProvider
}

interface RegisteredProvider {
  provider: DocumentEngineProvider
  unregister: () => void
  fingerprint: string
  pluginId: string
}

export function splitProviderId(value: string): { pluginId: string; engineId: string } | null {
  const first = value.indexOf('/')
  if (first <= 0 || first !== value.lastIndexOf('/') || first === value.length - 1) return null
  const pluginId = value.slice(0, first)
  const engineId = value.slice(first + 1)
  return pluginId === '' || engineId === '' ? null : { pluginId, engineId }
}

/**
 * provider 的缓存与会话关闭是两张表：provider 被升级替换后，旧 id 仍需交给
 * `retire()` 收掉它的 clean session。只看当前缓存会漏掉这类会话，表现为插件禁用后
 * 原生 helper 还在后台占着文件和内存。
 */
export class DocumentEngineProviderRegistry {
  private readonly registered = new Map<string, RegisteredProvider>()
  private readonly knownProviderIds = new Map<string, Set<string>>()
  private readonly createProvider: (options: NativeProviderOptions) => DocumentEngineProvider

  constructor(private readonly options: ProviderRegistryOptions) {
    this.createProvider = options.createProvider ?? ((providerOptions) => new NativeDocumentEngineProvider({
      id: providerOptions.id,
      formats: providerOptions.formats,
      resolveEntry: providerOptions.resolveEntry,
      workRoot: providerOptions.workRoot,
      ...(providerOptions.args === undefined ? {} : { args: providerOptions.args })
    }))
  }

  ensure(providerId: string): DocumentEngineProvider | null {
    const parts = splitProviderId(providerId)
    if (parts === null) return null

    const plugin = this.options.lookupPlugin(parts.pluginId)
    const contribution = plugin?.manifest.contributes.documentEngines?.find((engine) => engine.id === parts.engineId)
    const component = plugin?.manifest.nativeComponents?.find((candidate) => candidate.id === contribution?.component)
    const target = component === undefined ? null : selectNativeTarget(component, this.options.platform, this.options.arch)
    if (plugin === null || contribution === undefined || component === undefined || target === null) {
      this.drop(providerId)
      return null
    }

    const fingerprint = providerFingerprint(plugin, contribution.formats, component, target)
    const current = this.registered.get(providerId)
    if (current !== undefined && current.fingerprint === fingerprint) return current.provider
    if (current !== undefined) this.drop(providerId)

    const providerOptions: NativeProviderOptions = {
      id: providerId,
      formats: contribution.formats,
      workRoot: this.options.workRoot,
      args: [`--cache-dir=${join(this.options.workRoot, 'cache')}`],
      resolveEntry: async () => {
        // 需求：ensure 到实际 spawn 之间可能撤销权限/升级，缓存的 provider 不能继续起旧入口。
        const latest = this.options.lookupPlugin(parts.pluginId)
        const engine = latest?.manifest.contributes.documentEngines?.find((item) => item.id === parts.engineId)
        const native = latest?.manifest.nativeComponents?.find((item) => item.id === engine?.component)
        const build = native === undefined ? null : selectNativeTarget(native, this.options.platform, this.options.arch)
        if (latest === null || engine === undefined || native === undefined || build === null || providerFingerprint(latest, engine.formats, native, build) !== fingerprint) {
          throw new DocumentEngineError('engine_unavailable', 'document engine was disabled or changed before starting')
        }
        return resolveVerifiedEntry(latest.root, native, build)
      }
    }
    const provider = this.createProvider(providerOptions)
    const unregister = this.options.sessions.registerProvider(provider)
    this.registered.set(providerId, { provider, unregister, fingerprint, pluginId: parts.pluginId })
    let ids = this.knownProviderIds.get(parts.pluginId)
    if (ids === undefined) {
      ids = new Set<string>()
      this.knownProviderIds.set(parts.pluginId, ids)
    }
    ids.add(providerId)
    return provider
  }

  /**
   * 插件禁用 / 卸载时只关闭干净会话：脏会话还要由上层先保存或明确丢弃。
   * provider 曾经被升级替换也要保留在 knownProviderIds 中，否则旧会话没有收尾路径。
   */
  async retire(pluginId: string): Promise<void> {
    const ids = this.knownProviderIds.get(pluginId)
    if (ids === undefined) return
    for (const providerId of ids) {
      this.drop(providerId)
      await this.options.sessions.closeAll(providerId, { onlyClean: true })
    }
    this.knownProviderIds.delete(pluginId)
  }

  private drop(providerId: string): void {
    const current = this.registered.get(providerId)
    if (current === undefined) return
    current.unregister()
    this.registered.delete(providerId)
  }
}

function providerFingerprint(
  plugin: EngineHostPlugin,
  formats: readonly DocumentFormat[],
  component: NativeComponent,
  target: NativeComponentTarget
): string {
  return JSON.stringify({
    pluginRoot: plugin.root,
    pluginVersion: plugin.manifest.version,
    componentVersion: component.version,
    componentProtocol: component.protocol,
    target: {
      platform: target.platform,
      arch: target.arch,
      entry: target.entry,
      sha256: target.sha256,
      payload: target.payload
    },
    formats
  })
}
