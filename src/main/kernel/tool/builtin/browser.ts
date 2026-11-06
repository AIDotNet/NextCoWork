/*
 * Agent browser tools operate a live workspace page, never a second HTTP fetch.
 *
 * ★ The latest browser_snapshot is the only source of semantic refs, and one mutating action
 * consumes it. Coordinate actions similarly require a fresh screenshot. Without those two
 * rules the model can click a target inferred from page state that no longer exists.
 *
 * Electron and Playwright stay behind `browser/runtime.ts`; importing this registry in plain
 * Node tests must not initialize either graphical runtime.
 */
import { z } from 'zod'
import type { ToolOutputImage } from '../../../../shared/agent/message'
import type { ToolResult } from '../../../../shared/agent/tool'
import { toolFail, toolOk } from '../../../../shared/agent/tool'
import type { BrowserTab } from '../../../../shared/domain/browser'
import { formatBrowserSnapshot } from '../../../browser/aria-snapshot'
import { browserManager } from '../../../browser/manager'
import { getBrowserAutomationBridge, type BrowserMouseButton } from '../../../browser/runtime'
import { defineTool } from '../define'
import type { ToolContext, ToolRegistration } from '../registry'
import { ssrfRisk } from './ssrf'

const OBSERVE_THEN_ACT =
  'Use a ref only from the latest browser_snapshot. Perform one state-changing action, then snapshot again. ' +
  'If a ref fails, do not retry it unchanged; take a fresh snapshot and rebuild the target.'

const BrowserTabId = z.string().min(1).describe('Browser tab id returned by browser_open, browser_tabs, or browser_claim')
const BrowserRef = z.string().regex(/^e[1-9]\d*$/u).describe('Element ref from the latest browser_snapshot, for example e12')
const MouseButton = z.enum(['left', 'right', 'middle'])
const Point = z.object({
  x: z.number().finite().min(0),
  y: z.number().finite().min(0)
})

function workspaceIdOf(ctx: Pick<ToolContext, 'workspaceId' | 'workspaceRoot'>): string {
  // Production runs always carry workspaceId; workspaceRoot keeps headless unit tests deterministic.
  return ctx.workspaceId ?? ctx.workspaceRoot
}

function actorOf(ctx: Pick<ToolContext, 'workspaceId' | 'workspaceRoot' | 'runId' | 'sessionId'>): {
  workspaceId: string
  runId: string
  sessionId?: string
} {
  return {
    workspaceId: workspaceIdOf(ctx),
    runId: ctx.runId,
    ...(ctx.sessionId === undefined ? {} : { sessionId: ctx.sessionId })
  }
}

function ownedTab(tabId: string, ctx: ToolContext): BrowserTab {
  const tab = browserManager.get(tabId)
  if (tab === undefined) throw new Error(`Browser tab does not exist: ${tabId}`)
  if (tab.workspaceId !== workspaceIdOf(ctx)) throw new Error('That browser tab belongs to another workspace.')
  const sameSession = ctx.sessionId !== undefined && tab.ownerSessionId === ctx.sessionId
  if (tab.ownerRunId !== ctx.runId && !sameSession) {
    throw new Error('You can only operate a browser tab opened or claimed by this Agent session.')
  }
  return tab
}

/*
  需求：工作区所有者要求浏览器工具能够打开本机/内网页面（2026-09-20，已确认过
  `ssrf.ts` 里 `SsrfRiskOptions` 记的那条风险后仍要求继续）。所以这里传
  `allowPrivateAddresses: true`,只保留协议白名单和
  `user:pass@` 凭证拦截 —— 这两条和「是不是内网」无关,不受这个开关影响。

  需求：同一位所有者 2026-09-22 追加 —— 浏览器还要能打开 `file://` 本地页面
  （本地生成的 HTML 报告进右侧工作台直接看），所以再传 `allowFileUrls: true`。
  风险记在 `ssrf.ts` 的 `SsrfRiskOptions.allowFileUrls` 上，这里不重复；
  `WebFetch` 不传，file:// 在那边仍指向 Read。
*/
async function publicUrl(raw: string): Promise<URL> {
  const url = new URL(raw)
  const risk = ssrfRisk(url, { allowPrivateAddresses: true, allowFileUrls: true })
  if (risk !== null) throw new Error(risk)
  return url
}

async function readyRuntime(tab: BrowserTab, ctx: ToolContext): Promise<ReturnType<typeof getBrowserAutomationBridge>> {
  const runtime = getBrowserAutomationBridge()
  await runtime.waitFor(tab.id, ctx.signal)
  return runtime
}

function messageOf(err: unknown): string {
  return err instanceof Error ? err.message : String(err)
}

/**
 * 截图 → 工具结果。
 *
 * ★ 图落成**本会话附件**(`ctx.sessionImages`,与生图同一条路),`output.images` 只存一个
 * `ncw://` 地址,不再把整张 PNG 的 base64 塞进转录。不这样的话,每一次截图(上限 8MiB,
 * base64 后约 10.7MB 字符)都要过一次结构化克隆推给渲染层、落进 SQLite、随会话常驻内存。
 * 发往上游时 `prepareRequestImages` 会把本会话的 `ncw://` 换回 data URL —— 模型看到的
 * 字节与原来完全一样,回执正文也一字不改。
 *
 * ★ 存不下来(没有装配仓、磁盘满、超限)就退回内联 data URL,截图本身不失败。
 */
async function screenshotResult(tabId: string, screenshot: {
  data: Uint8Array
  mimeType: 'image/png' | 'image/jpeg'
  width: number
  height: number
  viewport: { width: number; height: number }
}, ctx: ToolContext): Promise<ToolResult> {
  const inline: ToolOutputImage = { mime: screenshot.mimeType, dataRef: `data:${screenshot.mimeType};base64,${Buffer.from(screenshot.data).toString('base64')}` }
  let image = inline
  if (ctx.sessionImages !== undefined) {
    try {
      image = await ctx.sessionImages.save(inline)
    } catch {
      image = inline
    }
  }
  return toolOk(
    `Captured browser tab ${tabId}: image ${String(screenshot.width)}x${String(screenshot.height)} pixels, CSS viewport ${String(screenshot.viewport.width)}x${String(screenshot.viewport.height)}. Coordinate actions use CSS viewport pixels; scale image coordinates when these sizes differ.`,
    { images: [image] }
  )
}

const BrowserOpenInput = z.object({
  url: z.string().url().describe('A complete http, https, or file URL'),
  title: z.string().max(160).optional().describe('Optional label for the browser tab'),
  profileId: z.string().optional().describe('Optional browser Profile id; defaults to the built-in browser'),
  backend: z.enum(['iab', 'headless']).optional().describe('iab opens a visible workspace tab; headless runs managed Chromium without a visible tab')
})

const browserOpenTool: ToolRegistration = defineTool({
  internalId: 'browser_open',
  description:
    'Open a web page in an isolated browser page owned by this Agent run. The default iab backend is visible in the workspace; headless uses managed Chromium. ' +
    'http, https and file:// URLs are supported. After opening, call browser_snapshot before interacting.',
  schema: BrowserOpenInput,
  readOnly: true,
  destructive: false,
  needsNetwork: true,
  async run(input, ctx) {
    const url = await publicUrl(input.url)
    const backend = input.backend ?? 'iab'
    const tab = browserManager.open({
      workspaceId: workspaceIdOf(ctx),
      ownerRunId: ctx.runId,
      ...(ctx.sessionId === undefined ? {} : { ownerSessionId: ctx.sessionId }),
      source: 'agent',
      backend,
      url: url.href,
      title: input.title,
      profileId: input.profileId,
      openRightPanel: backend === 'iab'
    })
    const runtime = getBrowserAutomationBridge()
    try {
      if (backend === 'headless') {
        await runtime.openHeadless({
          workspaceId: tab.workspaceId,
          tabId: tab.id,
          profileId: tab.profileId,
          url: tab.url,
          signal: ctx.signal
        })
      } else {
        await runtime.waitFor(tab.id, ctx.signal)
      }
      return toolOk(`Opened ${backend} browser tab ${tab.id} at ${tab.url}. Use browser_snapshot with tabId "${tab.id}" before interacting.`)
    } catch (err) {
      try { browserManager.close(tab.id, actorOf(ctx)) } catch { /* The tab may already be gone. */ }
      return toolFail(`Unable to open browser page: ${messageOf(err)}`)
    }
  }
})

const BrowserNavigateInput = z.object({
  tabId: BrowserTabId,
  url: z.string().url().describe('A complete http or https URL')
})

const browserNavigateTool: ToolRegistration = defineTool({
  internalId: 'browser_navigate',
  description: 'Navigate a browser page owned by this run. The navigation invalidates all prior refs; call browser_snapshot next.',
  schema: BrowserNavigateInput,
  readOnly: true,
  destructive: false,
  needsNetwork: true,
  async run(input, ctx) {
    const tab = ownedTab(input.tabId, ctx)
    const url = await publicUrl(input.url)
    const runtime = await readyRuntime(tab, ctx)
    try {
      browserManager.update(tab.id, { status: 'loading' }, actorOf(ctx))
      await runtime.navigate(tab.id, url.href)
      const next = browserManager.get(tab.id)
      if (next === undefined) throw new Error('The browser tab closed during navigation.')
      return toolOk(`Browser tab ${next.id} navigated to ${next.url}. Take a fresh browser_snapshot before interacting.`)
    } catch (err) {
      try { browserManager.update(tab.id, { status: 'error' }, actorOf(ctx)) } catch { /* Closed concurrently. */ }
      return toolFail(`Unable to navigate browser page: ${messageOf(err)}`)
    }
  }
})

const BrowserSnapshotInput = z.object({ tabId: BrowserTabId })

const browserSnapshotTool: ToolRegistration = defineTool({
  internalId: 'browser_snapshot',
  description:
    'Observe the live browser page as a compact accessibility tree. Interactive nodes carry refs such as [ref=e12]. ' +
    'This snapshot is the only valid source of refs; navigation or one state-changing action invalidates them. Cross-origin iframe contents are not expanded.',
  schema: BrowserSnapshotInput,
  readOnly: true,
  destructive: false,
  needsNetwork: false,
  async run(input, ctx) {
    const tab = ownedTab(input.tabId, ctx)
    const runtime = await readyRuntime(tab, ctx)
    try {
      return toolOk(formatBrowserSnapshot(await runtime.snapshot(tab.id)))
    } catch (err) {
      return toolFail(`Unable to snapshot browser page: ${messageOf(err)}`)
    }
  }
})

const browserClickTool: ToolRegistration = defineTool({
  internalId: 'browser_click',
  description: `Click one element by ref. ${OBSERVE_THEN_ACT}`,
  schema: z.object({
    tabId: BrowserTabId,
    ref: BrowserRef,
    button: MouseButton.optional(),
    clickCount: z.number().int().min(1).max(3).optional()
  }),
  readOnly: false,
  destructive: false,
  needsNetwork: true,
  async run(input, ctx) {
    const tab = ownedTab(input.tabId, ctx)
    const runtime = await readyRuntime(tab, ctx)
    await runtime.click(tab.id, input.ref, {
      ...(input.button === undefined ? {} : { button: input.button }),
      ...(input.clickCount === undefined ? {} : { clickCount: input.clickCount })
    })
    return toolOk(`Clicked ${input.ref} in browser tab ${tab.id}. Take a fresh browser_snapshot to verify the result.`)
  }
})

const browserTypeTool: ToolRegistration = defineTool({
  internalId: 'browser_type',
  description: `Focus one editable element and replace its value with text. ${OBSERVE_THEN_ACT}`,
  schema: z.object({ tabId: BrowserTabId, ref: BrowserRef, text: z.string() }),
  readOnly: false,
  destructive: false,
  needsNetwork: true,
  async run(input, ctx) {
    const tab = ownedTab(input.tabId, ctx)
    const runtime = await readyRuntime(tab, ctx)
    await runtime.type(tab.id, input.ref, input.text)
    return toolOk(`Typed into ${input.ref} in browser tab ${tab.id}. Take a fresh browser_snapshot to verify the result.`)
  }
})

const browserPressTool: ToolRegistration = defineTool({
  internalId: 'browser_press',
  description: `Press one key or key chord, optionally after focusing a ref. Pass modifiers separately, for example ["Control", "Enter"]. ${OBSERVE_THEN_ACT}`,
  schema: z.object({
    tabId: BrowserTabId,
    keys: z.array(z.string().min(1)).min(1).max(5),
    ref: BrowserRef.optional()
  }),
  readOnly: false,
  destructive: false,
  needsNetwork: true,
  async run(input, ctx) {
    const tab = ownedTab(input.tabId, ctx)
    const runtime = await readyRuntime(tab, ctx)
    await runtime.press(tab.id, input.keys, input.ref)
    return toolOk(`Pressed ${input.keys.join('+')} in browser tab ${tab.id}. Take a fresh browser_snapshot to verify the result.`)
  }
})

const browserSelectTool: ToolRegistration = defineTool({
  internalId: 'browser_select',
  description: `Select one or more option values in a select element. ${OBSERVE_THEN_ACT}`,
  schema: z.object({
    tabId: BrowserTabId,
    ref: BrowserRef,
    values: z.array(z.string()).min(1).max(100)
  }),
  readOnly: false,
  destructive: false,
  needsNetwork: true,
  async run(input, ctx) {
    const tab = ownedTab(input.tabId, ctx)
    const runtime = await readyRuntime(tab, ctx)
    await runtime.select(tab.id, input.ref, input.values)
    return toolOk(`Selected option values in ${input.ref}. Take a fresh browser_snapshot to verify the result.`)
  }
})

const browserScrollTool: ToolRegistration = defineTool({
  internalId: 'browser_scroll',
  description: 'Scroll the page at an optional viewport coordinate. Observe the page before scrolling and snapshot again afterward.',
  schema: z.object({
    tabId: BrowserTabId,
    scrollX: z.number().finite().default(0),
    scrollY: z.number().finite(),
    x: z.number().finite().min(0).optional(),
    y: z.number().finite().min(0).optional()
  }),
  readOnly: true,
  destructive: false,
  needsNetwork: false,
  async run(input, ctx) {
    const tab = ownedTab(input.tabId, ctx)
    const runtime = await readyRuntime(tab, ctx)
    await runtime.scroll(tab.id, {
      scrollX: input.scrollX,
      scrollY: input.scrollY,
      ...(input.x === undefined ? {} : { x: input.x }),
      ...(input.y === undefined ? {} : { y: input.y })
    })
    return toolOk(`Scrolled browser tab ${tab.id}. Take a fresh browser_snapshot to inspect the new viewport.`)
  }
})

const browserScreenshotTool: ToolRegistration = defineTool({
  internalId: 'browser_screenshot',
  description: 'Capture the current viewport. Use this only for canvas/custom-drawn controls or visual verification; prefer browser_snapshot for normal DOM interaction.',
  schema: BrowserSnapshotInput,
  readOnly: true,
  destructive: false,
  needsNetwork: false,
  async run(input, ctx) {
    const tab = ownedTab(input.tabId, ctx)
    const runtime = await readyRuntime(tab, ctx)
    return screenshotResult(tab.id, await runtime.screenshot(tab.id), ctx)
  }
})

const browserCuaClickTool: ToolRegistration = defineTool({
  internalId: 'browser_cua_click',
  description: 'Click exact CSS viewport coordinates. You MUST take a fresh browser_screenshot first; use browser_click with a ref whenever possible.',
  schema: z.object({ tabId: BrowserTabId, ...Point.shape, button: MouseButton.optional() }),
  readOnly: false,
  destructive: false,
  needsNetwork: true,
  async run(input, ctx) {
    const tab = ownedTab(input.tabId, ctx)
    const runtime = await readyRuntime(tab, ctx)
    await runtime.cuaClick(tab.id, { x: input.x, y: input.y }, input.button as BrowserMouseButton | undefined)
    return toolOk(`Clicked (${String(input.x)}, ${String(input.y)}) in browser tab ${tab.id}. Observe again before another action.`)
  }
})

const browserCuaDragTool: ToolRegistration = defineTool({
  internalId: 'browser_cua_drag',
  description: 'Drag through every supplied CSS viewport point. You MUST take a fresh browser_screenshot first; points are preserved in order.',
  schema: z.object({
    tabId: BrowserTabId,
    path: z.array(Point).min(2).max(200),
    keys: z.array(z.enum(['Alt', 'Control', 'Meta', 'Shift'])).max(4).optional()
  }),
  readOnly: false,
  destructive: false,
  needsNetwork: true,
  async run(input, ctx) {
    const tab = ownedTab(input.tabId, ctx)
    const runtime = await readyRuntime(tab, ctx)
    await runtime.cuaDrag(tab.id, input.path, input.keys)
    return toolOk(`Dragged across ${String(input.path.length)} points in browser tab ${tab.id}. Observe again before another action.`)
  }
})

const BrowserTabsInput = z.object({})

const browserTabsTool: ToolRegistration = defineTool({
  internalId: 'browser_tabs',
  description: 'List browser tabs controlled by this Agent run, including backend, attachment state, URL, and viewport.',
  schema: BrowserTabsInput,
  readOnly: true,
  destructive: false,
  needsNetwork: false,
  async run(_input, ctx) {
    const runtime = getBrowserAutomationBridge()
    const tabs = browserManager.list(workspaceIdOf(ctx)).filter(
      (tab) => tab.ownerRunId === ctx.runId || (ctx.sessionId !== undefined && tab.ownerSessionId === ctx.sessionId)
    )
    if (tabs.length === 0) return toolOk('No controlled browser tabs are open in this workspace.')
    return toolOk(tabs.map((tab) => {
      const state = runtime.info(tab.id)
      const viewport = state?.viewport
      return [
        tab.id,
        tab.backend,
        state?.bound === true ? 'active' : 'detached',
        viewport === undefined ? '-' : `${String(viewport.width)}x${String(viewport.height)}`,
        tab.title,
        tab.url,
        tab.status
      ].join('\t')
    }).join('\n'))
  }
})

const browserUserTabsTool: ToolRegistration = defineTool({
  internalId: 'browser_user_tabs',
  description: 'List user-opened visible browser tabs that are unclaimed or already controlled by this run. Call browser_claim before interacting with one.',
  schema: BrowserTabsInput,
  readOnly: true,
  destructive: false,
  needsNetwork: false,
  async run(_input, ctx) {
    const tabs = browserManager.list(workspaceIdOf(ctx)).filter((tab) =>
      tab.source === 'user' && (
        tab.ownerRunId === undefined ||
        tab.ownerRunId === ctx.runId ||
        (ctx.sessionId !== undefined && tab.ownerSessionId === ctx.sessionId)
      )
    )
    if (tabs.length === 0) return toolOk('No claimable user browser tabs are open in this workspace.')
    return toolOk(tabs.map((tab) => [
      tab.id,
      tab.ownerRunId !== undefined ? 'claimed' : 'unclaimed',
      tab.title,
      tab.url
    ].join('\t')).join('\n'))
  }
})

const browserClaimTool: ToolRegistration = defineTool({
  internalId: 'browser_claim',
  description: 'Claim one user-opened browser tab for this Agent run. A tab already claimed by another run cannot be taken over.',
  schema: z.object({ tabId: BrowserTabId }),
  readOnly: false,
  destructive: false,
  needsNetwork: false,
  async run(input, ctx) {
    const tab = browserManager.claim(input.tabId, actorOf(ctx))
    const runtime = getBrowserAutomationBridge()
    await runtime.waitFor(tab.id, ctx.signal)
    return toolOk(`Claimed user browser tab ${tab.id}. Take browser_snapshot before interacting.`)
  }
})

const browserProfilesTool: ToolRegistration = defineTool({
  internalId: 'browser_profiles',
  description: 'List browser Profiles available to this workspace. Profiles are global configuration with isolated cookies and login state.',
  schema: BrowserTabsInput,
  readOnly: true,
  destructive: false,
  needsNetwork: false,
  async run() {
    const profiles = browserManager.listProfiles()
    return toolOk(profiles.map((profile) => {
      const domains = profile.domains.length === 0 ? '*' : profile.domains.join(', ')
      return `${profile.id}\t${profile.name}\t${profile.isDefault ? 'default' : 'custom'}\t${domains}`
    }).join('\n'))
  }
})

const browserCloseTool: ToolRegistration = defineTool({
  internalId: 'browser_close',
  description: 'Close one browser tab controlled by this Agent run.',
  schema: z.object({ tabId: BrowserTabId }),
  readOnly: true,
  destructive: false,
  needsNetwork: false,
  async run(input, ctx) {
    const tab = ownedTab(input.tabId, ctx)
    const runtime = getBrowserAutomationBridge()
    await runtime.release(tab.id)
    browserManager.close(tab.id, actorOf(ctx))
    return toolOk(`Closed browser tab ${tab.id}.`)
  }
})

export const browserTools: readonly ToolRegistration[] = [
  browserOpenTool,
  browserNavigateTool,
  browserSnapshotTool,
  browserClickTool,
  browserTypeTool,
  browserPressTool,
  browserSelectTool,
  browserScrollTool,
  browserScreenshotTool,
  browserCuaClickTool,
  browserCuaDragTool,
  browserTabsTool,
  browserUserTabsTool,
  browserClaimTool,
  browserProfilesTool,
  browserCloseTool
]

export {
  browserOpenTool,
  browserNavigateTool,
  browserSnapshotTool,
  browserClickTool,
  browserTypeTool,
  browserPressTool,
  browserSelectTool,
  browserScrollTool,
  browserScreenshotTool,
  browserCuaClickTool,
  browserCuaDragTool,
  browserTabsTool,
  browserUserTabsTool,
  browserClaimTool,
  browserProfilesTool,
  browserCloseTool
}
