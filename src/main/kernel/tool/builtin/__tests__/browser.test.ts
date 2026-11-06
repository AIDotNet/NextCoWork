import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../../../shared/domain/workspace'
import { browserManager, setBrowserChangeListener } from '../../../../browser/manager'
import {
  installBrowserAutomationBridge,
  type BrowserAutomationBridge,
  type BrowserPageSnapshot
} from '../../../../browser/runtime'
import { store } from '../../../../state/store'
import { nodeHost } from '../../../host'
import type { ToolContext } from '../../registry'
import { browserClickTool, browserOpenTool, browserScreenshotTool, browserSnapshotTool } from '../browser'

const opened: string[] = []
let snapshot: BrowserPageSnapshot
let bridge: BrowserAutomationBridge

beforeEach(() => {
  vi.spyOn(store, 'getWorkspace').mockReturnValue({
    id: 'workspace-a',
    name: 'local',
    rootPath: '/tmp/workspace-a',
    environment: { kind: 'local' },
    settings: DEFAULT_WORKSPACE_SETTINGS,
    createdAt: 1,
    lastOpenedAt: 1
  })
  snapshot = {
    tree: '- heading "Example" [ref=e1]\n- button "Continue" [ref=e2]',
    title: 'Example',
    url: 'https://example.com/',
    snapshotId: 'snapshot-1',
    viewport: { width: 1280, height: 720 },
    truncated: false
  }
  bridge = {
    bindIab: vi.fn(async () => ({ width: 1280, height: 720 })),
    openHeadless: vi.fn(async () => ({ width: 1280, height: 720 })),
    waitFor: vi.fn(async () => undefined),
    info: vi.fn(() => ({ bound: true, backend: 'iab' as const, viewport: { width: 1280, height: 720 } })),
    navigate: vi.fn(async () => undefined),
    snapshot: vi.fn(async () => snapshot),
    click: vi.fn(async () => undefined),
    type: vi.fn(async () => undefined),
    press: vi.fn(async () => undefined),
    select: vi.fn(async () => undefined),
    scroll: vi.fn(async () => undefined),
    cuaClick: vi.fn(async () => undefined),
    cuaDrag: vi.fn(async () => undefined),
    screenshot: vi.fn(async () => ({
      data: new Uint8Array([1, 2, 3]),
      mimeType: 'image/png' as const,
      width: 1280,
      height: 720,
      viewport: { width: 1280, height: 720 }
    })),
    release: vi.fn(async () => undefined),
    clearProfile: vi.fn(async () => undefined),
    reconcile: vi.fn(),
    setCuaListener: vi.fn(),
    shutdown: vi.fn(async () => undefined)
  }
  installBrowserAutomationBridge(bridge)
})

afterEach(() => {
  installBrowserAutomationBridge(null)
  for (const id of opened.splice(0)) {
    if (browserManager.get(id) !== undefined) browserManager.close(id)
  }
  vi.restoreAllMocks()
})

function openAgentTab(runId = 'run-a') {
  const tab = browserManager.open({
    workspaceId: 'workspace-a',
    source: 'agent',
    ownerRunId: runId,
    profileId: 'default',
    url: 'https://example.com/'
  })
  opened.push(tab.id)
  return tab
}

function ctx(runId = 'run-a'): ToolContext {
  return {
    workspaceId: 'workspace-a',
    workspaceRoot: '/tmp/workspace-a',
    signal: new AbortController().signal,
    permissionMode: 'full',
    depth: 0,
    callId: 'call-1',
    runId,
    host: nodeHost(),
    emit: () => undefined
  }
}

describe('live browser tools', () => {
  it('从已绑定页面返回带 ref 的真实快照', async () => {
    const tab = openAgentTab()

    const result = await browserSnapshotTool.execute({ tabId: tab.id }, ctx())

    expect(result.isError).toBe(false)
    expect(result.output.content).toContain('Page: Example')
    expect(result.output.content).toContain('[ref=e2]')
    expect(bridge.snapshot).toHaveBeenCalledWith(tab.id)
  })

  it('把 ref 点击委托给同一页面并拒绝另一个 run', async () => {
    const tab = openAgentTab()

    const accepted = await browserClickTool.execute({ tabId: tab.id, ref: 'e2' }, ctx())
    const rejected = await browserClickTool.execute({ tabId: tab.id, ref: 'e2' }, ctx('run-b'))

    expect(accepted.isError).toBe(false)
    expect(bridge.click).toHaveBeenCalledWith(tab.id, 'e2', {})
    expect(rejected.isError).toBe(true)
    expect(rejected.output.content).toContain('opened or claimed')
  })

  it('把截图作为模型可见的图片结果返回', async () => {
    const tab = openAgentTab()

    const result = await browserScreenshotTool.execute({ tabId: tab.id }, ctx())

    expect(result.isError).toBe(false)
    expect(result.output.images).toEqual([{
      mime: 'image/png',
      dataRef: 'data:image/png;base64,AQID'
    }])
  })

  /*
    需求:截图不再以整段 base64 留在转录里 —— 每张最多 8MiB,过 IPC、落库、常驻渲染层内存。
    它落成本会话附件,转录里只存 `ncw://` 地址;发往上游时再换回 data URL,模型看到的字节不变。
  */
  it('★ 有会话图片仓时:截图落成附件,output.images 只存 ncw:// 地址,回执正文不变', async () => {
    const tab = openAgentTab()
    const save = vi.fn(async () => ({ mime: 'image/png' as const, dataRef: 'ncw://attachments/sessions/s/shot.png' }))
    const inline = await browserScreenshotTool.execute({ tabId: tab.id }, ctx())

    const result = await browserScreenshotTool.execute({ tabId: tab.id }, { ...ctx(), sessionImages: { save, read: vi.fn() } })

    expect(save).toHaveBeenCalledWith({ mime: 'image/png', dataRef: 'data:image/png;base64,AQID' })
    expect(result.output.images).toEqual([{ mime: 'image/png', dataRef: 'ncw://attachments/sessions/s/shot.png' }])
    expect(result.output.content).toBe(inline.output.content)
  })

  it('图片仓存不下来:退回内联 data URL,截图本身不失败', async () => {
    const tab = openAgentTab()
    const save = vi.fn(async () => { throw new Error('disk full') })

    const result = await browserScreenshotTool.execute({ tabId: tab.id }, { ...ctx(), sessionImages: { save, read: vi.fn() } })

    expect(result.isError).toBe(false)
    expect(result.output.images).toEqual([{ mime: 'image/png', dataRef: 'data:image/png;base64,AQID' }])
  })

  /*
    需求：浏览器工具要能打开 file:// 本地页面（2026-09-22，见 ssrf.ts 的
    allowFileUrls），并且 Agent 一打开浏览器右侧工作台就必须展开
    （openRightPanel → 变更事件上的 rightPanelOpen 标记，链条见
    __tests__/manager.test.ts 同名用例）。这条端到端钉住 tool → manager 这一段：
    任何一层把 file:// 拒掉、或者把 openRightPanel 丢掉，这里都会红。
  */
  it('用 file:// 打开 iab 页面成功，并请求右侧工作台展开', async () => {
    const changes: Array<{ rightPanelOpen?: boolean }> = []
    setBrowserChangeListener((change) => changes.push(change))

    const result = await browserOpenTool.execute({ url: 'file:///tmp/report.html' }, ctx())

    expect(result.isError).toBe(false)
    opened.push(
      ...browserManager
        .list('workspace-a')
        .filter((tab) => tab.url === 'file:///tmp/report.html')
        .map((tab) => tab.id)
    )
    expect(result.output.content).toContain('file:///tmp/report.html')
    expect(bridge.waitFor).toHaveBeenCalled()
    expect(changes.length).toBeGreaterThan(0)
    expect(changes[changes.length - 1]?.rightPanelOpen).toBe(true)
  })
})
