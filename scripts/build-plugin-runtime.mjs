#!/usr/bin/env node
/**
 * 构建**插件视图运行时** —— 由 `ncw-plugin://` 协议经 import map 下发给插件视图。
 *
 * | 产物 | 模块名 | 内容 |
 * |---|---|---|
 * | `react.js` | `react`, `react/jsx-runtime` | React 本体(CJS → ESM) |
 * | `react-dom.js` | `react-dom`, `react-dom/client` | 同上,react 标 external |
 * | `ui.js` | `nextcowork/ui` | 宿主 `components/ui/**` 原样打包 |
 * | `ui.css` | (由 `<link>` 注入) | theme.css + 上面那批组件用到的 Tailwind 工具类 |
 * | `view.js` | `nextcowork/view` | 视图侧运行时(主题 / 文档通道 / mount) |
 *
 * ## 为什么必须是**宿主**下发,而不是插件自己打
 *
 * 插件自己打也能跑(CSP 放行 `'self'` 的外部脚本),`acme.markdown-studio`
 * 就是这么做的。代价有三条,而且都只在装了好几个插件之后才显形:
 *
 * 1. 每个视图一份 React —— N 个 iframe = N 份运行时;
 * 2. 控件要各自重画一遍,宿主一改尺寸/配色,所有插件同时变歪,无人收到通知;
 * 3. 插件锁死在它打包那天的 React 版本上。
 *
 * ## 为什么用 vite 而不是直接 esbuild
 *
 * `ui.css` 要跑 Tailwind v4。仓库已经有 `@tailwindcss/vite`,用它等于和宿主
 * 自己的样式走**同一条工具链**;换 esbuild 就得再引一个 Tailwind 的 CLI 或
 * 直接调它的内部 API —— 两者都会在 Tailwind 升级时率先坏掉,而且坏的形式是
 * 「样式少生成了几个类」这种没人看得出来的。
 *
 * ## 产物不进版本库
 *
 * `resources/plugin-runtime/` 在 .gitignore 里。这个脚本在 `dev` 和 `build`
 * 前各跑一次,并且**按 mtime 跳过**没必要的重建 —— 不跳的话每次 `npm run dev`
 * 都要多等几秒,而它一年也变不了几次。
 */
import { createHash } from 'node:crypto'
import { createRequire } from 'node:module'
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs'
import { dirname, join, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { build } from 'vite'
import tailwind from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import * as esbuild from 'esbuild'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const src = join(root, 'src/renderer/src')
const out = join(root, 'resources/plugin-runtime')
const stampFile = join(out, '.stamp')

/*
  重建判据:所有输入文件的 (路径, mtime) 摘要。

  ★ 用 mtime 而不是内容哈希:内容哈希要读几百个文件,比这次构建本身还慢的
  情况是存在的。mtime 的代价是「touch 一下就重建」—— 一次多余的构建,
  而漏建的代价是「改了组件但插件视图没变」,那会浪费掉一整轮排查。
*/
function fingerprint() {
  const files = []
  const walk = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.name === 'node_modules' || entry.name.startsWith('.')) continue
      const full = join(dir, entry.name)
      if (entry.isDirectory()) walk(full)
      else if (/\.(tsx?|css)$/.test(entry.name)) files.push(full)
    }
  }
  /*
    ★ `shared/document-engine` 也要算进来:`plugin-ui/view.tsx` 从那里导出文档会话客户端用的
    报文定义与画布换算。漏掉它的话,改了换算函数而运行时不重建 —— 插件视图用的还是旧实现,
    宿主与视图的坐标换算从此对不上,且零报错。
  */
  /*
    ★ `plugins/` 也算:Tailwind v4 从仓库根自动扫描 class,内置办公插件(ncw.writer / sheets / slides)
    用到的工具类是从它们的源码里生成进 ui.css 的。不算进来的话,插件新写一个 class 而运行时不重建,
    那个 class 就不存在 —— 表现为「布局对、某一处没样式」,且零报错。
  */
  for (const dir of [join(src, 'components/ui'), join(src, 'components/arc'), join(src, 'plugin-ui'), join(src, 'widget-kit'), join(src, 'lib'), join(src, 'theme'), join(src, 'styles'), join(root, 'src/shared/document-engine'), join(root, 'plugins')]) {
    if (existsSync(dir)) walk(dir)
  }
  files.push(fileURLToPath(import.meta.url))
  const hash = createHash('sha256')
  for (const file of files.sort()) hash.update(`${relative(root, file)}:${String(statSync(file).mtimeMs)}\n`)
  // 依赖版本也算进去:升级 React 之后必须重建,而那不会改动任何源文件
  const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'))
  hash.update(JSON.stringify([pkg.dependencies, pkg.devDependencies]))
  return hash.digest('hex')
}

const stamp = fingerprint()
if (!process.argv.includes('--force') && existsSync(stampFile) && readFileSync(stampFile, 'utf8') === stamp) {
  console.log('[plugin-runtime] 已是最新,跳过')
  process.exit(0)
}

mkdirSync(out, { recursive: true })

/*
  ★ `renderer/src/i18n` 在这个包里被换成一个替身。

  `components/ui/Dialog.tsx` 为了关闭按钮的 aria-label 用了一次 `t()`,
  把真的 i18n 打进来会连带拖进三千多行全量文案表 —— 一个只为了一个标签的
  ~200KB,而且其中每一条都和插件无关。替身见 `plugin-ui/i18n-stub.ts`。

  用 `resolveId` 而不是 `resolve.alias`:Dialog 里写的是**相对路径**
  (`'../../i18n'`),alias 匹配的是原始 specifier 字符串,对相对路径无能为力。
*/
const i18nStub = {
  name: 'ncw-plugin-ui-i18n-stub',
  enforce: 'pre',
  resolveId(source, importer) {
    if (importer === undefined || !source.includes('i18n')) return null
    const target = resolve(dirname(importer), source)
    if (target === join(src, 'i18n') || target === join(src, 'i18n/index')) {
      return join(src, 'plugin-ui/i18n-stub.ts')
    }
    return null
  }
}

/*
  ★ widget 套件(`widget-kit/index.tsx`)按**相对路径**转发 `plugin-ui/ui` —— 这样它在宿主的
  类型检查里是一个普通模块。打包时要把那条相对 import 换回裸名 `nextcowork/ui` 并标 external:
  否则 widget.js 里会再装一份控件实现(连同第二份 Radix 状态),和 ui.css 对不上的那种。
*/
const uiExternal = {
  name: 'ncw-widget-ui-external',
  enforce: 'pre',
  resolveId(source, importer) {
    if (importer === undefined || !importer.startsWith(join(src, 'widget-kit'))) return null
    const target = resolve(dirname(importer), source)
    if (target === join(src, 'plugin-ui/ui') || target === join(src, 'plugin-ui/ui.ts')) {
      return { id: 'nextcowork/ui', external: true }
    }
    return null
  }
}

/** 一次 vite lib 构建。`external` 里的东西由 import map 在运行期给。 */
async function bundle(entry, fileName, external) {
  await build({
    root,
    configFile: false,
    logLevel: 'warn',
    plugins: [react(), tailwind(), i18nStub, uiExternal],
    /*
      ★ Vite 的 lib 模式**不替换** `process.env.NODE_ENV`(它假设产物会再被使用方的打包器处理),
      而这两份产物是直接下发给浏览器的 ESM:`ui.js` 里 framer-motion 的那一句
      `process.env.NODE_ENV !== "production"` 一执行就是 `process is not defined`,
      用 `nextcowork/ui` 的插件视图整页白屏(Electron 实测)。与上面 React 核的 define 同一个理由。
    */
    define: { 'process.env.NODE_ENV': '"production"' },
    build: {
      outDir: out,
      emptyOutDir: false,
      cssCodeSplit: false,
      // ★ 不压缩变量名以外的东西:插件作者调试视图时看到的是这份代码。
      minify: 'esbuild',
      lib: { entry: join(src, entry), formats: ['es'], fileName: () => fileName },
      rollupOptions: {
        external,
        output: { assetFileNames: 'ui.css' }
      }
    }
  })
}

// ── 1. React ──
/*
  React 19 的 npm 包**只有 CJS**(`react/package.json` 的 exports 全指向 .js)。
  这件事决定了下面这套看着绕的做法:

  直觉做法是 `react-dom` 打一个包、把 `react` 标成 external。esbuild 会把 CJS
  里的 `require("react")` 转成**运行期** `__require("react")` —— 而浏览器 ESM
  里没有 require,视图一 import 就炸,且报错指向一堆压缩过的变量名。

  所以:react 和 react-dom 打进**同一个核**(保证全局只有一份 React 实例,
  两份的症状是 "Invalid hook call",排查起来能耗掉一天),再按 import map 要
  暴露的四个模块名各生成一个**门面**。

  ★ 门面的命名导出必须是**静态**的(ESM 规定),所以这里在构建期用 Node 把
  各自的 key 列出来生成代码,而不是运行期转发。React 升级后重新生成即可,
  不用手维护一张 API 清单。
*/
const REACT_MODULES = [
  { ns: 'react', spec: 'react', file: 'react.js' },
  { ns: 'reactDom', spec: 'react-dom', file: 'react-dom.js' },
  { ns: 'reactDomClient', spec: 'react-dom/client', file: 'react-dom-client.js' },
  { ns: 'jsxRuntime', spec: 'react/jsx-runtime', file: 'jsx-runtime.js' },
  { ns: 'jsxDevRuntime', spec: 'react/jsx-dev-runtime', file: 'jsx-dev-runtime.js' }
]

await esbuild.build({
  stdin: {
    // `import x from '<cjs>'` 经 esbuild 的 interop 拿到的就是 module.exports 本身
    contents: REACT_MODULES.map(({ ns, spec }) => `import ${ns} from '${spec}'`).join('\n')
      + `\nexport { ${REACT_MODULES.map((m) => m.ns).join(', ')} }\n`,
    resolveDir: root,
    loader: 'js'
  },
  outfile: join(out, 'react-core.js'),
  bundle: true,
  format: 'esm',
  platform: 'browser',
  target: 'es2022',
  /*
    ★ `process.env.NODE_ENV` 必须在构建期替换。React 的 CJS 入口靠它选
    development / production 分支,而 iframe 里**没有 `process`** ——
    留着的话首次 import 就是 `process is not defined`,整个视图白屏。
  */
  define: { 'process.env.NODE_ENV': '"production"' },
  minify: true,
  logLevel: 'warning'
})

const requireCjs = createRequire(import.meta.url)
/** 能做 ESM 命名导出的标识符。`default` 单独处理,保留字直接跳过。 */
const RESERVED = new Set(['default', 'do', 'if', 'in', 'for', 'new', 'var', 'let', 'class', 'const', 'import', 'export'])
for (const { ns, spec, file } of REACT_MODULES) {
  const keys = Object.keys(requireCjs(spec)).filter((key) => /^[A-Za-z_$][\w$]*$/.test(key) && !RESERVED.has(key))
  const lines = [
    /*
      ★ 引的是 `./__react-core.js`,不是产物文件名 `./react-core.js`:门面文件经
      `ncw-plugin://<id>/__react.js` 下发,相对路径解析到同一目录下,而协议层只按
      `RUNTIME_FILES` 那张表(`__` 前缀)提供运行时文件。写成 `./react-core.js` 的症状是
      React 写的插件视图一片空白,控制台只有一条 react-core.js 的 404(Electron 实测)。
    */
    `import { ${ns} as __ns } from './__react-core.js'`,
    ...keys.map((key) => `export const ${key} = __ns.${key}`),
    // `import React from 'react'` 拿到的就是 module.exports —— 和 CJS 下一致
    'export default __ns',
    ''
  ]
  writeFileSync(join(out, file), lines.join('\n'))
}


// ── 2. UI 套件(带 ui.css)与视图运行时 ──
const runtimeExternals = ['react', 'react-dom', 'react-dom/client', 'react/jsx-runtime']
await bundle('plugin-ui/ui.ts', 'ui.js', runtimeExternals)
await bundle('plugin-ui/view.tsx', 'view.js', runtimeExternals)

// ── 3. TSX widget 的模块(`ncw-widget://shell/` 下发,见 net/widget-protocol.ts) ──
const widgetExternals = [...runtimeExternals, 'nextcowork/ui']
await bundle('widget-kit/index.tsx', 'widget.js', widgetExternals)
await bundle('widget-kit/charts.tsx', 'charts.js', widgetExternals)
await bundle('widget-kit/lucide.ts', 'lucide.js', widgetExternals)

writeFileSync(stampFile, stamp)

for (const file of ['react-core.js', 'react.js', 'react-dom.js', 'react-dom-client.js', 'jsx-runtime.js', 'jsx-dev-runtime.js', 'ui.js', 'ui.css', 'view.js', 'widget.js', 'charts.js', 'lucide.js']) {
  const path = join(out, file)
  if (!existsSync(path)) {
    // ★ 缺产物直接失败。让它悄悄通过的话,协议层会对每个视图 404,
    //   而插件作者看到的是「import 'nextcowork/ui' 失败」,和自己的代码无关。
    console.error(`[plugin-runtime] 缺少产物:${file}`)
    process.exit(1)
  }
  console.log(`[plugin-runtime] ${file}  ${String(Math.round(statSync(path).size / 1024))} KB`)
}
