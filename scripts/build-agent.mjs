#!/usr/bin/env node
/**
 * 构建**独立设备服务**(`src/agent/server/bin.ts`)→ `out/agent/bin.mjs`。
 *
 * 为什么是 vite 的 SSR 构建:仓库已有 vite(见 `build-plugin-runtime.mjs`),SSR 模式默认把
 * `node_modules` 依赖保持为外部 import —— 产物只含我们自己的代码,依赖由随包的
 * `package.json`/锁文件安装。原生模块(ssh2 / cpu-features 的 .node 等)因此不会被打进包里。
 *
 * 产物自检(失败即非零退出,阻断发布):
 *  1. 不得引用 `electron` —— 服务必须能在没有 Electron 的机器上启动;
 *  2. 必须保留对 `node:sqlite` 的内建引用 —— 需要 Node ≥ 24;
 *  3. 入口必须存在且非空。
 *
 * 用法:`npm run build:agent`。产物不进版本库(`out/` 已在 .gitignore)。
 */
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { build } from 'vite'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const outDir = join(root, 'out', 'agent')

/** 产物里不得出现的模块说明符。 */
export const FORBIDDEN_IMPORTS = ['electron']
/** 产物里必须仍然是内建引用的模块。 */
export const REQUIRED_BUILTINS = ['node:sqlite']

/** 扫描打包产物,返回违规描述(空数组 = 通过)。导出以便单元测试。 */
export function inspectBundle(code) {
  const problems = []
  for (const name of FORBIDDEN_IMPORTS) {
    const pattern = new RegExp(`(?:from\\s*|import\\s*\\(\\s*|require\\s*\\(\\s*)["']${name}(?:/[^"']*)?["']`)
    if (pattern.test(code)) problems.push(`bundle imports "${name}"`)
  }
  for (const name of REQUIRED_BUILTINS) {
    if (!code.includes(`"${name}"`) && !code.includes(`'${name}'`)) problems.push(`bundle lost the "${name}" builtin import`)
  }
  return problems
}

/** npm 包名:`@scope/name/sub.js` → `@scope/name`,`zod/x` → `zod`。 */
export function packageName(spec) {
  return spec.startsWith('@') ? spec.split('/').slice(0, 2).join('/') : spec.split('/')[0]
}

/**
 * 产物里保留下来的**外部包名** —— 取自 rollup 的**模块图**(chunk 的 `imports` / `dynamicImports`),
 * **不扫产物正文**:正文里现在混着给模型看的 TSX 示例(`visualize-guidelines/components.ts`
 * 那段模板字符串里就写着 `import { TrendingUp } from 'lucide-react'`),按正则扫正文会把文档
 * 当代码读、把一个根本没被引用的包报成「产物引用了它」,打包直接红。
 *
 * 只收**要安装的包**:内建(`node:` 前缀)与相对 / 绝对路径都丢掉 —— 清单的用途是「部署方要装
 * 哪些依赖」。`package-agent.mjs` 那边仍然再挡一道(清单是产物旁边的文件,可能被人手改)。
 * 导出以便单元测试。
 */
export function externalSpecifiers(chunkImports) {
  const found = new Set()
  for (const spec of chunkImports) {
    if (typeof spec !== 'string' || spec === '' || spec.startsWith('node:') || spec.startsWith('.') || spec.startsWith('/')) continue
    found.add(packageName(spec))
  }
  return [...found].sort()
}

async function main() {
  rmSync(outDir, { recursive: true, force: true })
  mkdirSync(outDir, { recursive: true })
  const result = await build({
    root,
    configFile: false,
    logLevel: 'warn',
    build: {
      ssr: join(root, 'src/agent/server/bin.ts'),
      outDir,
      emptyOutDir: true,
      target: 'node24',
      minify: false,
      sourcemap: true,
      rollupOptions: {
        output: {
          format: 'es', entryFileNames: 'bin.mjs', inlineDynamicImports: true,
          /*
            产物要能当命令直接跑:`npm i -g` 给 bin 建的是指向本文件的软链(POSIX),
            内核按 shebang 决定怎么执行它 —— 没有 shebang 就会把它当 shell 脚本读,
            报一行 `syntax error near unexpected token`。所以 shebang 属于产物本身。
          */
          banner: '#!/usr/bin/env node'
        }
      }
    },
    ssr: { target: 'node', noExternal: [] },
    resolve: { conditions: ['node'] }
  })

  const entry = join(outDir, 'bin.mjs')
  if (!existsSync(entry) || statSync(entry).size === 0) throw new Error('agent bundle was not produced')
  const code = readFileSync(entry, 'utf8')
  const problems = inspectBundle(code)
  if (problems.length > 0) throw new Error(`agent bundle failed its checks:\n- ${problems.join('\n- ')}`)

  /*
    `build()` 在 multi 模式下给数组、单次给带 `output` 的对象;`lib` 模式还可能把整个结果包在
    `{ 0: … }` 里。三种都认,认不出来就当**失败**:静默写一份空清单的话,打出来的包会缺掉全部运行时依赖。
  */
  const rolled = Array.isArray(result) || result?.output !== undefined ? result : result?.[0]
  const outputs = Array.isArray(rolled) ? rolled[0]?.output : rolled?.output
  const chunk = Array.isArray(outputs) ? outputs[0] : outputs
  if (chunk?.imports === undefined) throw new Error('rollup reported no chunk imports; cannot determine the bundle external packages')
  writeFileSync(
    join(outDir, 'BUNDLE_IMPORTS.json'),
    `${JSON.stringify({ external: externalSpecifiers([...chunk.imports, ...(chunk.dynamicImports ?? [])]) }, null, 2)}\n`
  )

  // 产物旁写一份最小运行说明,让部署方一眼看到 Node 版本与外部依赖来源。
  const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'))
  writeFileSync(join(outDir, 'RUNTIME.json'), JSON.stringify({
    entry: 'bin.mjs', node: '>=24', version: pkg.version,
    note: 'Dependencies are external; install them with the repository package.json before running.',
    files: readdirSync(outDir).sort()
  }, null, 2))
  console.log(`agent bundle ready: ${entry}`)
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => { console.error(error instanceof Error ? error.message : error); process.exit(1) })
}
