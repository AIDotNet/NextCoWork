/**
 * `ncw-widget://` 协议的响应形状。
 *
 * 这里钉的是几处**只在运行时才现形、而且症状都指向别处**的东西:
 *
 * - 外壳页面的 CSP 少了任何一条(`nonce` / `frame-ancestors` / CDN 白名单),
 *   表现都是"图出来了但不动"或"一片空白";
 * - **主窗口的 `frame-src` 必须点名这个 scheme** —— 这是本仓库已经踩过两次的坑
 *   (`src/renderer/index.html` 里那段注释写的就是它)。所以下面有一条用例
 *   直接去读那两处 CSP 的原文,漏一处就红。
 *
 * 测试环境里 `electron` 解析到的是那个"导出可执行文件路径"的 stub,
 * `protocol` 是 undefined —— 被测的是纯函数 `handleWidgetRequest`,不碰它。
 */
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { handleWidgetRequest } from '../widget-protocol'
import { setPluginRuntimeDir } from '../../plugin/protocol'

function request(path: string, host = 'shell'): Request {
  return new Request(`ncw-widget://${host}${path}`)
}

afterEach(() => {
  vi.restoreAllMocks()
})

describe('handleWidgetRequest', () => {
  it('外壳页面:带 nonce 的 CSP + 首页内容', async () => {
    const response = await handleWidgetRequest(request('/index.html'))
    expect(response.status).toBe(200)
    expect(response.headers.get('Content-Type')).toContain('text/html')

    const csp = response.headers.get('Content-Security-Policy') ?? ''
    const nonce = /'nonce-([^']+)'/.exec(csp)?.[1]
    expect(nonce).toBeTruthy()
    // 内联样式与 CDN 脚本都在;而 `'unsafe-inline'` 不许出现在 script-src 里
    expect(csp).toContain("script-src 'self' 'nonce-")
    expect(csp).toContain('https://cdn.jsdelivr.net')
    expect(/script-src[^;]*unsafe-inline/.test(csp)).toBe(false)
    // 只允许主窗口嵌它
    expect(csp).toContain('frame-ancestors')

    const html = await response.text()
    // nonce 必须**同时**出现在头与页面里,而且是同一个值 —— 分两次生成的话 CSP 依然拦得住
    expect(html).toContain(`content="${nonce ?? ''}"`)
    expect(html).toContain('src="/runtime.js"')
    // 基础样式表(规范让模型用的 class 由它提供)
    expect(html).toContain('svg .box')
    // 外壳自己不带内联脚本:逻辑全在 runtime.js 里
    expect(html).not.toContain('<script>')
  })

  it('每次响应换一个 nonce —— 固定值等于换个写法的 unsafe-inline', async () => {
    const first = await handleWidgetRequest(request('/index.html'))
    const second = await handleWidgetRequest(request('/index.html'))
    const nonceOf = (r: Response): string => /'nonce-([^']+)'/.exec(r.headers.get('Content-Security-Policy') ?? '')?.[1] ?? ''
    expect(nonceOf(first)).not.toBe(nonceOf(second))
  })

  it('运行时脚本按 JS 下发', async () => {
    const response = await handleWidgetRequest(request('/runtime.js'))
    expect(response.status).toBe(200)
    expect(response.headers.get('Content-Type')).toContain('javascript')
    expect(response.headers.get('Cache-Control')).toBe('no-store')
    expect((await response.text()).length).toBeGreaterThan(0)
  })

  it('别的 host 一律 404 —— 这里没有"URL → 文件"的映射', async () => {
    expect((await handleWidgetRequest(request('/index.html', 'somebody.else'))).status).toBe(404)
  })

  /**
   * ★ 取不到别的 js 要**出声**:那说明外壳产物不再自包含(多了一个按值 import,
   * rollup 就把它提到带哈希的共享 chunk 里了),而那时界面只是空白。
   */
  it('请求未知脚本时打日志说明成因', async () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {})
    const response = await handleWidgetRequest(request('/widget-abc123.js'))
    expect(response.status).toBe(404)
    expect(spy).toHaveBeenCalledTimes(1)
    expect(String(spy.mock.calls[0]?.[0])).toContain('自包含')
  })

  /**
   * TSX 模式:外壳页面带一份 import map,运行时文件从插件运行时目录里读,路径是写死的白名单。
   * ★ 门面文件里写死了 `./__react-core.js`,名字必须与插件协议那张表一致。
   */
  it('TSX 运行时:import map 点名 widget 模块,文件按白名单从运行时目录下发', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'ncw-widget-rt-'))
    writeFileSync(join(dir, 'widget.js'), 'export const ok = 1')
    writeFileSync(join(dir, 'ui.css'), ':root{}')
    setPluginRuntimeDir(dir)
    try {
      const html = await (await handleWidgetRequest(request('/index.html'))).text()
      expect(html).toContain('type="importmap"')
      expect(html).toContain('"nextcowork/widget":"/__widget.js"')
      expect(html).toContain('"react":"/__react.js"')

      const script = await handleWidgetRequest(request('/__widget.js'))
      expect(script.status).toBe(200)
      expect(script.headers.get('Content-Type')).toContain('javascript')
      expect(await script.text()).toBe('export const ok = 1')

      const css = await handleWidgetRequest(request('/__ui.css'))
      expect(css.headers.get('Content-Type')).toContain('text/css')

      // 不在表里的 `__` 文件、目录穿越都不认
      expect((await handleWidgetRequest(request('/__view.js'))).status).toBe(404)
      expect((await handleWidgetRequest(request('/__../widget.js'))).status).toBe(404)
    } finally {
      setPluginRuntimeDir('')
      rmSync(dir, { recursive: true, force: true })
    }
  })

  it('未知路径 404,且不打日志', async () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {})
    expect((await handleWidgetRequest(request('/secret.txt'))).status).toBe(404)
    expect(spy).not.toHaveBeenCalled()
  })
})

/**
 * ★ 两处 CSP 必须同时点名 `ncw-widget:`。
 *
 * 这条用例读的是**文件原文**而不是模拟一个 iframe:那个坑的失败形态就是
 * "开发者以为写了",而它只在被拦下的那一刻留一行 CSP 警告。生产版与 dev 版
 * 分居两个文件(而且 dev 那份是**整段替换**),少一个的后果是"开发时正常、
 * 打包后空白"。
 */
describe('主窗口 CSP 的 frame-src', () => {
  const repoRoot = join(process.cwd())

  it('生产版(src/renderer/index.html)点名了 ncw-widget:', () => {
    const html = readFileSync(join(repoRoot, 'src/renderer/index.html'), 'utf8')
    // 取那一条 CSP 的全文再找 —— 直接全文正则会被注释里提到的 "frame-src" 命中
    const policy = /http-equiv="Content-Security-Policy"[^>]*content="([^"]*)"/.exec(html)?.[1] ?? ''
    const frameSrc = /frame-src([^;]*)/.exec(policy)?.[1] ?? ''
    expect(frameSrc).toContain('ncw-widget:')
    expect(frameSrc).toContain('ncw-plugin:')
  })

  it('dev 版(electron.vite.config.ts 的 DEV_CSP)也点名了', () => {
    const config = readFileSync(join(repoRoot, 'electron.vite.config.ts'), 'utf8')
    // 认的是那一行字符串常量(`'frame-src …',`),不是注释里提到的名字
    const frameSrc = /['"]frame-src([^'"]*)['"]/.exec(config)?.[1] ?? ''
    expect(frameSrc).toContain('ncw-widget:')
    expect(frameSrc).toContain('ncw-plugin:')
  })
})
