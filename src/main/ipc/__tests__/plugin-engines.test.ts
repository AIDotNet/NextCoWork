/**
 * 原生 helper 的启动门(`ipc/plugin-engines.ts`)。
 *
 * 需求:`documents.open` 只需要 `workspace.read` —— 打开一份文档就能把**引擎插件**包里的
 * 原生可执行文件跑起来,而那份清单里的授权此前一个都没被问过。这几条断言钉住的就是
 * 「什么情况下这个进程必须起不来」,以及「什么情况下它仍然该起来」(判据过严会把
 * 正常办公插件的文档功能整个关掉,而症状只有「引擎不可用」一句)。
 */
import { describe, expect, it } from 'vitest'
import type { PluginManifest } from '../../../shared/plugin/manifest'
import type { PluginPermissionState } from '../../../shared/plugin/permission'
import { engineHelperCommand, engineContributionRefusal, helperSpawnRefusal, type EngineSpawnSubject } from '../plugin-engines'

function subject(options: {
  permissions?: PluginPermissionState
  allowedCommands?: string[]
  enabled?: boolean
  status?: EngineSpawnSubject['status']
  entry?: string
  engineId?: string
} = {}): EngineSpawnSubject {
  const manifest = {
    id: 'ncw.office-runtime',
    permissions: options.permissions?.required ?? ['process'],
    optionalPermissions: options.permissions?.optional ?? [],
    allowedCommands: options.allowedCommands ?? ['ncw-office-helper'],
    nativeComponents: [{
      id: 'helper',
      targets: [{ platform: 'darwin', arch: 'arm64', entry: options.entry ?? 'native/darwin-arm64/ncw-office-helper', sha256: 'a'.repeat(64) }],
      license: { spdx: 'MPL-2.0', notices: 'native/NOTICE' }
    }],
    contributes: { documentEngines: [{ id: options.engineId ?? 'office', component: 'helper', formats: ['docx'] }] }
  } as unknown as PluginManifest
  return {
    manifest,
    status: options.status ?? 'idle',
    enabled: options.enabled ?? true,
    permissions: options.permissions ?? { required: ['process'], optional: [], granted: ['process'] }
  }
}

describe('引擎插件的 helper 启动门', () => {
  it('入口名在白名单里、process 已授权时放行', () => {
    const plugin = subject()
    expect(engineHelperCommand(plugin.manifest, 'office', 'darwin', 'arm64')).toBe('ncw-office-helper')
    expect(engineContributionRefusal(plugin, 'office', 'darwin', 'arm64')).toBeNull()
    expect(helperSpawnRefusal(plugin, '/plugins/ncw.office-runtime/native/darwin-arm64/ncw-office-helper')).toBeNull()
  })

  it('★ 只声明 workspace.read 的引擎包起不来 —— 打不开文档就不再等于可以执行原生代码', () => {
    const plugin = subject({ permissions: { required: ['workspace.read'], optional: [], granted: ['workspace.read'] } })
    expect(engineContributionRefusal(plugin, 'office', 'darwin', 'arm64')).toMatch(/process/)
    expect(helperSpawnRefusal(plugin, 'native/darwin-arm64/ncw-office-helper')).toMatch(/process/)
  })

  it('rejects a stale grant when the current manifest no longer declares process', () => {
    const plugin = subject()
    plugin.manifest.permissions = []
    expect(helperSpawnRefusal(plugin, 'ncw-office-helper')).toMatch(/process/)
  })

  it('声明了 process 但入口不在命令白名单里时拒绝,理由来自 narrowCommand', () => {
    const plugin = subject({ allowedCommands: ['soffice'] })
    expect(engineContributionRefusal(plugin, 'office', 'darwin', 'arm64')).toMatch(/not in the manifest command allow-list/)
  })

  it('空白名单一律拒绝(声明了 process 也不例外)', () => {
    expect(engineContributionRefusal(subject({ allowedCommands: [] }), 'office', 'darwin', 'arm64')).toMatch(/allow-list/)
  })

  it('撤掉 process 之后仍然拒绝 —— provider 缓存里那一份要能当场失效', () => {
    // 撤的是一个落在 optionalPermissions 里的能力:插件照常 enabled,缓存不会被清,
    // 所以「真实 spawn 前现查」是唯一挡住它的地方(见模块头注释第 2 条)。
    const plugin = subject({ permissions: { required: [], optional: ['process'], granted: [] } })
    expect(plugin.enabled).toBe(true)
    expect(helperSpawnRefusal(plugin, 'ncw-office-helper')).toMatch(/process/)
  })

  it('停用 / 待批准 / 装载失败的引擎插件一律拒绝', () => {
    expect(helperSpawnRefusal(subject({ enabled: false }), 'ncw-office-helper')).toMatch(/disabled/)
    expect(helperSpawnRefusal(subject({ status: 'pending-approval' }), 'ncw-office-helper')).toMatch(/approve/)
    expect(helperSpawnRefusal(subject({ status: 'error' }), 'ncw-office-helper')).toMatch(/did not load/)
  })

  it('Windows 入口的 .exe 后缀按 narrowCommand 的归一对上裸名', () => {
    const plugin = subject({ entry: 'native/win32-x64/ncw-office-helper.exe' })
    expect(helperSpawnRefusal(plugin, 'native/win32-x64/ncw-office-helper.exe')).toBeNull()
  })

  it('本机没有构建时不在这里拒 —— 平台判定归登记表', () => {
    const plugin = subject()
    expect(engineContributionRefusal(plugin, 'office', 'win32', 'x64')).toBeNull()
    expect(engineHelperCommand(plugin.manifest, 'office', 'win32', 'x64')).toBeNull()
    // 引擎 id 不存在同理:交给登记表返回 null(engine_unavailable)
    expect(engineContributionRefusal(plugin, 'nope', 'darwin', 'arm64')).toBeNull()
  })
})
