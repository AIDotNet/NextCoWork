import { describe, expect, it } from 'vitest'
import { EVENT_CHANNELS, INVOKE_CHANNELS, SEND_CHANNELS } from '../contract'
import {
  EVENT_BOUNDARIES,
  INVOKE_BOUNDARIES,
  SEND_BOUNDARIES,
  type IpcBoundary
} from '../boundaries'

/**
 * 这一组测的是**两张表之间的对齐**,而不是某一处的行为:
 * `contract.ts` 的频道白名单是运行时的权威清单,`boundaries.ts` 是逐条写死的
 * 路由边界分类。两边一旦分叉(新增频道忘了分类、改名没跟上),后果都不是
 * 编译期能看见的 —— 所以这里逐表比 key,并把**安全相关的例外**钉死。
 *
 * ★ 分类表自己是 `satisfies Record<频道联合, IpcBoundary>`,少一条 / 多一条
 *   频道都会让 typecheck 报错。这里是那层编译期约束之外的第二道防线:
 *   它测的是**运行时的实际 key 集合**,连「编译期过了但对象被改坏」也拦得住。
 */

const LEGAL: IpcBoundary[] = ['controller-local', 'device', 'composed']

const sortedKeys = (o: object): string[] => Object.keys(o).sort()

/**
 * 指定的例外 —— 逐条写死,**不从 boundaries.ts 反向推**。
 * 反推的话,把某条频道的分类改错、测试会跟着一起错;写死才有意义。
 * 表里没有的频道一律是生成时的默认档 `device`(见 boundaries.ts 文件头)。
 */
const CONTROLLER_LOCAL_INVOKE = [
  'app:openExternal',
  'app:checkForUpdates',
  'app:updateCheck',
  'app:updateDownload',
  'app:updateInstall',
  'app:updateGetState',
  'app:copyText',
  'app:copyImage',
  'app:openSessionWindow',
  'browser:bind'
] as const

const COMPOSED_INVOKE = [
  'app:getBootstrap',
  'app:saveTextFile',
  'app:saveImageFile',
  'clientAuth:startLogin',
  'theme:importImage',
  'connection:pickFile',
  'workspace:pick',
  'workspace:revealFile',
  'workspace:listOpenTargets',
  'workspace:openWith',
  'workspace:copyPath',
  'workspace:saveFileAs',
  'browser:exportCookies',
  'browser:importCookies',
  'attachment:pick',
  'plugins:pickPackage',
  'plugins:activateEditor',
  'plugins:openWebApp',
  'plugins:setEditorDirty',
  'plugins:confirmClose',
  'skills:pickZip',
  'video:saveFile',
  'provider:export',
  'provider:import',
  'provider:startOAuth',
  'provider:addAccount',
  'provider:reauthAccount',
  'storage:openDataDirectory',
  'storage:export',
  'storage:importPreview',
  'storage:chooseBackupDirectory',
  'storage:restoreBackup',
  'storage:clearLocalData',
  'imports:chooseSource',
  'dataMigration:openDataDirectory'
] as const

const CONTROLLER_LOCAL_SEND = ['window:ready', 'window:control', 'app:quitConfirmed'] as const

const CONTROLLER_LOCAL_EVENT = [
  'window:maximized',
  'window:visibility',
  'app:updateChanged'
] as const

const COMPOSED_EVENT = [
  'theme:changed',
  'plugins:message',
  'plugins:openCustomEditor',
  'plugins:openTab',
  'plugins:interaction',
  'scheduled:focusRun'
] as const

/**
 * 钉几条**必须留在 device** 的高敏感频道。它们出现在这里不是因为分类可疑,
 * 而是因为把它们误改成 controller-local / composed 等于宣称「本地控制端
 * 就能直接读写库 / 拿凭据」—— 那正是这张表要防的那一类错误。
 */
const MUST_STAY_DEVICE_INVOKE = [
  'agent:run',
  'workspace:readFile',
  'workspace:writeFile',
  'sessions:get',
  'provider:setCredential',
  'provider:revealCredential',
  'mcp:setSecrets',
  'storage:importApply',
  'storage:clearHistory',
  'terminal:create'
] as const

describe('boundaries —— 与契约白名单严格同集', () => {
  it('invoke 表的 key 与 INVOKE_CHANNELS 完全相同', () => {
    expect(sortedKeys(INVOKE_BOUNDARIES)).toEqual(sortedKeys(INVOKE_CHANNELS))
  })

  it('send 表的 key 与 SEND_CHANNELS 完全相同', () => {
    expect(sortedKeys(SEND_BOUNDARIES)).toEqual(sortedKeys(SEND_CHANNELS))
  })

  it('event 表的 key 与 EVENT_CHANNELS 完全相同', () => {
    expect(sortedKeys(EVENT_BOUNDARIES)).toEqual(sortedKeys(EVENT_CHANNELS))
  })
})

describe('boundaries —— 每个值都是合法分类', () => {
  it.each([
    ['INVOKE_BOUNDARIES', INVOKE_BOUNDARIES],
    ['SEND_BOUNDARIES', SEND_BOUNDARIES],
    ['EVENT_BOUNDARIES', EVENT_BOUNDARIES]
  ])('%s 的每一项只可能是三档之一', (_name, table) => {
    for (const [channel, boundary] of Object.entries(table)) {
      expect(LEGAL, `${channel} 的分类 ${String(boundary)} 不合法`).toContain(boundary)
    }
  })

  it('三档都真的被用到了 —— 免得整张表退化成同一个值', () => {
    const used = new Set<string>([
      ...Object.values(INVOKE_BOUNDARIES),
      ...Object.values(SEND_BOUNDARIES),
      ...Object.values(EVENT_BOUNDARIES)
    ])
    expect([...used].sort()).toEqual(['composed', 'controller-local', 'device'])
  })
})

describe('boundaries —— 指定的例外逐条钉死', () => {
  it('invoke 的 controller-local 例外', () => {
    for (const channel of CONTROLLER_LOCAL_INVOKE) {
      expect(INVOKE_CHANNELS, `${channel} 不在契约白名单里`).toHaveProperty(channel)
      expect(INVOKE_BOUNDARIES[channel], channel).toBe('controller-local')
    }
  })

  it('invoke 的 composed 例外', () => {
    for (const channel of COMPOSED_INVOKE) {
      expect(INVOKE_CHANNELS, `${channel} 不在契约白名单里`).toHaveProperty(channel)
      expect(INVOKE_BOUNDARIES[channel], channel).toBe('composed')
    }
  })

  it('send 的 controller-local 例外,其余全是 device', () => {
    for (const channel of CONTROLLER_LOCAL_SEND) {
      expect(SEND_CHANNELS, `${channel} 不在契约白名单里`).toHaveProperty(channel)
      expect(SEND_BOUNDARIES[channel], channel).toBe('controller-local')
    }
    for (const [channel, boundary] of Object.entries(SEND_BOUNDARIES)) {
      if (!(CONTROLLER_LOCAL_SEND as readonly string[]).includes(channel)) {
        expect(boundary, channel).toBe('device')
      }
    }
  })

  it('event 的 controller-local 例外', () => {
    for (const channel of CONTROLLER_LOCAL_EVENT) {
      expect(EVENT_CHANNELS, `${channel} 不在契约白名单里`).toHaveProperty(channel)
      expect(EVENT_BOUNDARIES[channel], channel).toBe('controller-local')
    }
  })

  it('event 的 composed 例外', () => {
    for (const channel of COMPOSED_EVENT) {
      expect(EVENT_CHANNELS, `${channel} 不在契约白名单里`).toHaveProperty(channel)
      expect(EVENT_BOUNDARIES[channel], channel).toBe('composed')
    }
  })

  it('★ 高敏感频道不许被降级成 controller-local / composed', () => {
    for (const channel of MUST_STAY_DEVICE_INVOKE) {
      expect(INVOKE_CHANNELS, `${channel} 不在契约白名单里`).toHaveProperty(channel)
      expect(INVOKE_BOUNDARIES[channel], channel).toBe('device')
    }
  })

  it('例外表里没有重复项 —— 重复就是一条被写了两遍的分类', () => {
    for (const list of [CONTROLLER_LOCAL_INVOKE, COMPOSED_INVOKE, COMPOSED_EVENT]) {
      expect(new Set(list).size, list.join(',')).toBe(list.length)
    }
    // controller-local 与 composed 不可能同时命中同一条 invoke 频道。
    for (const channel of CONTROLLER_LOCAL_INVOKE) {
      expect(COMPOSED_INVOKE).not.toContain(channel)
    }
  })
})
