/**
 * `ncw-plugin://` 的宿主页面与它的 CSP。
 *
 * ## 这条测试钉的是一个「两端对不上」的契约
 *
 * 宿主页面靠**两段内联脚本**启动:importmap 把裸模块名 `nextcowork` 映射到
 * `/__runtime.js`,紧随其后的 module 脚本调 `__bootstrap`。而 CSP 是
 * `script-src 'self'` —— 只放行**外部**脚本,内联的一律拦掉(importmap 同样
 * 受 `script-src` 管)。
 *
 * 于是插件永远握手不上,而症状全部指向别处:
 * - 主进程 `spawn()` 里的 `await ready` 永不 resolve → 点了没反应 / 启用后不自启动;
 * - 控制台只有一句「Refused to execute inline script」。
 *
 * 所以这里同时断言**两件事**,缺一条这个契约就又断了:
 * 1. CSP 里有 `'nonce-…'`(只靠 `'self'` 挡不住内联脚本被拒);
 * 2. 页面里的两段脚本带着**同一个** nonce(各生成各的等于没加)。
 */
import { runInNewContext } from 'node:vm'
import { describe, expect, it } from 'vitest'
import { handlePluginRequest } from '../protocol'

const ROOT = '/plugins/acme.demo'

function resolverFor(id: string): (pluginId: string) => { root: string; main: string } | undefined {
  return (pluginId) => (pluginId === id ? { root: ROOT, main: './dist/extension.js' } : undefined)
}

function nonceFromCsp(csp: string): string | null {
  return /'nonce-([^']+)'/.exec(csp)?.[1] ?? null
}

describe('文档会话垫片', () => {
  it('forwards each documents method and its per-call scope through the actual runtime bridge', async () => {
    const response = await handlePluginRequest(new Request('ncw-plugin://acme.demo/__runtime.js'), resolverFor('acme.demo'))
    expect(response.status).toBe(200)
    const requests: { method: string; params: unknown }[] = []
    // 需求：执行实际生成的垫片而非只匹配源码，避免方法表有了却没有可调用的导出。
    const runtime = runInNewContext(`${(await response.text()).replace(/^export \{[^\n]+\}\s*$/gm, '').replace(/^export /gm, '')}\n;documents`, {
      __ncwPluginBridge: {
        request: async (request: { id: number; method: string; params: unknown }) => {
          requests.push(request)
          return { id: request.id, ok: true, data: { received: request.params } }
        }
      }
    }) as Record<string, (params: unknown) => Promise<unknown>>
    const methods = { open: 'open', apply: 'apply', save: 'save', exportDocument: 'export', getState: 'getState', query: 'query', getOperation: 'getOperation', close: 'close' }
    for (const [name, rpc] of Object.entries(methods)) {
      const params = { sessionId: 'session', callId: `call-${name}` }
      expect(await runtime[name]?.(params)).toEqual({ received: params })
      expect(requests.at(-1)).toMatchObject({ method: `documents.${rpc}`, params })
    }
  })
})

describe('插件宿主页面的 CSP', () => {
  it('★ CSP 放行 nonce,页面里的两段内联脚本带的是同一个 nonce', async () => {
    const response = await handlePluginRequest(
      new Request('ncw-plugin://acme.demo/__host.html'),
      resolverFor('acme.demo')
    )
    expect(response.status).toBe(200)

    const csp = response.headers.get('Content-Security-Policy') ?? ''
    const nonce = nonceFromCsp(csp)
    expect(nonce).not.toBeNull()
    // `script-src` 里仍然不允许裸的内联 —— 放行的是「带这个 nonce 的那两段」
    expect(csp).toContain("script-src 'self' 'nonce-")
    expect(csp).not.toContain("'unsafe-inline' 'wasm")

    const html = await response.text()
    // importmap 与 bootstrap 各一次
    expect(html).toContain(`<script nonce="${String(nonce)}" type="importmap">`)
    expect(html).toContain(`<script nonce="${String(nonce)}" type="module">`)
    expect(html.match(/nonce=/g)).toHaveLength(2)
  })

  it('nonce 每次响应都不一样 —— 固定值等于换个写法的 unsafe-inline', async () => {
    const first = await handlePluginRequest(
      new Request('ncw-plugin://acme.demo/__host.html'),
      resolverFor('acme.demo')
    )
    const second = await handlePluginRequest(
      new Request('ncw-plugin://acme.demo/__host.html'),
      resolverFor('acme.demo')
    )
    const a = nonceFromCsp(first.headers.get('Content-Security-Policy') ?? '')
    const b = nonceFromCsp(second.headers.get('Content-Security-Policy') ?? '')
    expect(a).not.toBeNull()
    expect(a).not.toBe(b)
  })

  it('认不出的插件一律 403,不给页面也不给 nonce', async () => {
    const response = await handlePluginRequest(
      new Request('ncw-plugin://someone.else/__host.html'),
      resolverFor('acme.demo')
    )
    expect(response.status).toBe(403)
  })
})
