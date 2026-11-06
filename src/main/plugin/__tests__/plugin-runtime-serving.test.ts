/**
 * 插件视图运行时的**下发**(`/__react.js` / `/__ui.js` / `/__ui.css` / `/__view.js`)。
 *
 * ## 这条路存在的理由
 *
 * 插件视图本来就能用 React —— 自己打包进去即可(CSP 放行 `'self'` 的外部脚本)。
 * 代价要装了好几个插件之后才显形:每个视图一份 React;控件各自照着宿主重画一遍,
 * 宿主改版后所有插件同时变歪且无人收到通知;插件锁死在它打包那天的 React 上。
 *
 * 所以宿主把这几样**下发**。下面钉的是这条路上会静默坏掉的几处。
 */
import { existsSync, promises as fs } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

/*
  ★ `protocol.ts` 用 `net.fetch` 读盘(Electron 的 net 会处理 file:// 且不走 Node 的
  fs 权限)。测试环境里 `electron` 解析到的是那个导出可执行文件路径的 stub,
  `net` 是 undefined —— 所以这里给一个按 file:// 读盘的替身。

  ★ 只替 `net`:`protocol` / `session` 这两个在被测路径上一次都不会用到,
  替了反而会掩盖「有人往这条路上加了副作用」。
*/
vi.mock('electron', () => ({
  net: {
    fetch: async (url: string): Promise<Response> => {
      const path = new URL(url).pathname
      try {
        return new Response(await fs.readFile(decodeURIComponent(path)), { status: 200 })
      } catch {
        return new Response('not found', { status: 404 })
      }
    }
  },
  protocol: {},
  session: {}
}))

const { handlePluginRequest, setPluginRuntimeDir } = await import('../protocol')
const { pluginRuntimeDir, PLUGIN_RUNTIME_DIR } = await import('../host-window')

let pkgRoot = ''
let runtimeRoot = ''

const resolver = (id: string): { root: string; main: string } | undefined =>
  id === 'acme.demo' ? { root: pkgRoot, main: './dist/extension.js' } : undefined

beforeEach(async () => {
  pkgRoot = await fs.mkdtemp(join(tmpdir(), 'ncw-pkg-'))
  runtimeRoot = await fs.mkdtemp(join(tmpdir(), 'ncw-runtime-'))
  await fs.writeFile(join(runtimeRoot, 'ui.js'), 'export const Button = 1')
  await fs.writeFile(join(runtimeRoot, 'ui.css'), '.bg-canvas{color:red}')
  await fs.writeFile(join(runtimeRoot, 'react.js'), 'export default 1')
  setPluginRuntimeDir(runtimeRoot)
})

afterEach(async () => {
  setPluginRuntimeDir('')
  await fs.rm(pkgRoot, { recursive: true, force: true })
  await fs.rm(runtimeRoot, { recursive: true, force: true })
})

async function get(path: string): Promise<Response> {
  return handlePluginRequest(new Request(`ncw-plugin://acme.demo${path}`), resolver)
}

describe('运行时产物的下发', () => {
  it('★ /__ui.js 与 /__ui.css 下发,且 MIME 正确', async () => {
    const js = await get('/__ui.js')
    expect(js.status).toBe(200)
    expect(js.headers.get('Content-Type')).toContain('text/javascript')

    const css = await get('/__ui.css')
    expect(css.status).toBe(200)
    // ★ CSS 的 MIME 给错的话浏览器直接拒绝应用样式表,而且只在控制台留一行
    expect(css.headers.get('Content-Type')).toContain('text/css')
  })

  it('★★ 包内同名文件盖不住宿主的运行时 —— 那会是一条能装下任意代码的后门', async () => {
    /*
      import map 把 `nextcowork/ui` 指向 `/__ui.js`。如果这个路径会落到**包内**
      的同名文件上,那么任何插件只要在包里放一个 `__ui.js`,就替换掉了所有视图
      的控件实现(包括别的插件的视图),而 import map 看上去毫无异常。
    */
    await fs.writeFile(join(pkgRoot, '__ui.js'), 'export const Button = "劫持"')
    const response = await get('/__ui.js')
    expect(response.status).toBe(200)
    expect(await response.text()).toBe('export const Button = 1')
  })

  it('产物目录没注入时回 404,而不是去读一个拼错的路径', async () => {
    setPluginRuntimeDir('')
    expect((await get('/__ui.js')).status).toBe(404)
  })

  it('认不出的插件连运行时都拿不到(403 在更前面)', async () => {
    const response = await handlePluginRequest(new Request('ncw-plugin://someone.else/__ui.js'), resolver)
    expect(response.status).toBe(403)
  })

  it('运行时里没有的文件名回 404 —— 表里只有那几个,不是一个任意读目录', async () => {
    // `/__secrets.js` 不在 RUNTIME_FILES 表里,于是走包内解析,包里也没有 → 404
    expect((await get('/__secrets.js')).status).toBe(404)
  })
})

describe('产物目录的位置', () => {
  it('★ 与 preload 同一套取法 —— 分叉的话打包后必然有一个 404,而开发时都正常', () => {
    expect(pluginRuntimeDir({ packaged: true, resourcesPath: '/A/Resources', appPath: '/ignored' }))
      .toBe(join('/A/Resources', PLUGIN_RUNTIME_DIR))
    expect(pluginRuntimeDir({ packaged: false, resourcesPath: '/ignored', appPath: '/repo' }))
      .toBe(join('/repo', 'resources', PLUGIN_RUNTIME_DIR))
  })
})

/**
 * 真产物的形状。
 *
 * ★ `skipIf`:产物不进版本库(`npm run dev` / `build` 前才生成),而 CI 跑的是
 * typecheck + lint + test —— 不跑构建。这里硬要求产物存在的话,CI 会在一个
 * **与它无关**的环节上红,而真正的问题(产物坏了)只有开发机看得见。
 */
const RUNTIME_BUILD = join(process.cwd(), 'resources', 'plugin-runtime')
const built = existsSync(join(RUNTIME_BUILD, 'ui.js'))

describe.skipIf(!built)('已构建的运行时产物', () => {
  /** 产物里所有的裸模块 import(相对路径的不算)。 */
  async function bareImports(file: string): Promise<string[]> {
    const source = await fs.readFile(join(RUNTIME_BUILD, file), 'utf8')
    const out = new Set<string>()
    for (const match of source.matchAll(/(?:^|[;\s])(?:import|export)[^;]*?from\s*["']([^"'.][^"']*)["']/g)) {
      if (match[1] !== undefined) out.add(match[1])
    }
    return [...out]
  }

  it('★★ ui.js / view.js 里的每一个裸 import 都在 import map 里', async () => {
    /*
      这条挡的是一类只在运行期显形的回归:有人给 `components/ui/**` 加了一个
      新依赖(比如换了个图标库),构建把它标成 external 之外的东西打进去还好,
      **万一留成了裸 import**,浏览器解析不出来 —— 整个视图白屏,控制台只有一句
      "Failed to resolve module specifier"。而构建本身是绿的。
    */
    const allowed = new Set(['react', 'react-dom', 'react-dom/client', 'react/jsx-runtime', 'react/jsx-dev-runtime'])
    for (const file of ['ui.js', 'view.js']) {
      for (const specifier of await bareImports(file)) {
        expect(allowed, `${file} imports ${specifier}`).toContain(specifier)
      }
    }
  })

  it('★ react 的实现只有一份 —— 两份的症状是 Invalid hook call', async () => {
    // 门面文件只能转发,不许自己带 React 实现
    const facade = await fs.readFile(join(RUNTIME_BUILD, 'react.js'), 'utf8')
    // ★ 带 `__` 前缀:门面文件在 `/__react.js` 下发,相对引用必须落到协议层认得的 `/__react-core.js`
    expect(facade).toContain("from './__react-core.js'")
    expect(facade.length).toBeLessThan(4000)
    // 核里不能残留运行期 require:浏览器 ESM 里没有 require,一 import 就炸
    const core = await fs.readFile(join(RUNTIME_BUILD, 'react-core.js'), 'utf8')
    expect(core).not.toMatch(/\brequire\("react"\)/)
  })

  it('★ ui.css 里有 token 与工具类 —— 少了就是「有布局没颜色」', async () => {
    const css = await fs.readFile(join(RUNTIME_BUILD, 'ui.css'), 'utf8')
    expect(css).toContain('--color-canvas')
    expect(css).toContain('.bg-accent')
  })

  it('★ 没把宿主的全量 i18n 表打进去 —— Dialog 只为一个 aria-label 用了一次 t()', async () => {
    const ui = await fs.readFile(join(RUNTIME_BUILD, 'ui.js'), 'utf8')
    // 全量表里才有的词。出现了说明 i18n 替身没生效,包会凭空大 ~200KB
    expect(ui).not.toContain('会话列表')
  })
})

describe('注入进视图 HTML 的那份 import map', () => {
  it('★ react 与 react-dom/client 指向同一个核 —— 两份 React 的症状是 Invalid hook call', async () => {
    await fs.mkdir(join(pkgRoot, 'dist'), { recursive: true })
    await fs.writeFile(join(pkgRoot, 'dist/view.html'), '<html><head></head><body></body></html>')
    const html = await (await get('/dist/view.html')).text()

    const map = JSON.parse(/<script nonce="[^"]+" type="importmap">(.*?)<\/script>/.exec(html)?.[1] ?? '{}') as {
      imports: Record<string, string>
    }
    expect(map.imports['react']).toBe('/__react.js')
    expect(map.imports['react-dom/client']).toBe('/__react-dom-client.js')
    expect(map.imports['nextcowork/ui']).toBe('/__ui.js')
    expect(map.imports['nextcowork/view']).toBe('/__view.js')
    // 样式表也注入:插件不写任何引用就该拿到正确外观
    expect(html).toContain('<link rel="stylesheet" href="/__ui.css">')
  })

  it('pathToFileURL 对中文/空格路径也要成立 —— 插件目录名来自用户', async () => {
    // 这一条其实在验读盘那一步:URL 编码没处理好的话,带空格的路径会 404
    const spaced = join(runtimeRoot, 'ui.js')
    expect(pathToFileURL(spaced).toString().startsWith('file://')).toBe(true)
    expect((await get('/__ui.js')).status).toBe(200)
  })
})
