#!/usr/bin/env node
/**
 * 构建**自部署中转**(`src/relay/bin.ts`)→ `out/relay/bin.mjs`。
 *
 * 与 `build-agent.mjs` 同一条工具链(vite SSR,依赖保持外部)。产物自检:
 *  1. 不得引用 `electron`;
 *  2. 不得引用 `node:sqlite` 或设备数据库 —— 中转**不持久化任何业务数据**,看到这些说明
 *     设备服务的代码被误打进了中转;
 *  3. 入口必须存在且非空。
 *
 * 用法:`npm run build:relay`。
 */
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { build } from 'vite'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const outDir = join(root, 'out', 'relay')

/** 中转产物里不得出现的模块说明符。 */
export const FORBIDDEN_IMPORTS = ['electron', 'node:sqlite']
/** 设备服务才有的标记字符串。 */
export const FORBIDDEN_MARKERS = ['nextcowork.db', 'device-identity.json']

/** 扫描打包产物,返回违规描述(空数组 = 通过)。 */
export function inspectRelayBundle(code) {
  const problems = []
  for (const name of FORBIDDEN_IMPORTS) {
    const pattern = new RegExp(`(?:from\\s*|import\\s*\\(\\s*|require\\s*\\(\\s*)["']${name}(?:/[^"']*)?["']`)
    if (pattern.test(code)) problems.push(`bundle imports "${name}"`)
  }
  for (const marker of FORBIDDEN_MARKERS) {
    if (code.includes(marker)) problems.push(`bundle contains device-service code ("${marker}")`)
  }
  return problems
}

async function main() {
  rmSync(outDir, { recursive: true, force: true })
  mkdirSync(outDir, { recursive: true })
  await build({
    root, configFile: false, logLevel: 'warn',
    build: {
      ssr: join(root, 'src/relay/bin.ts'), outDir, emptyOutDir: true, target: 'node24', minify: false, sourcemap: true,
      rollupOptions: { output: { format: 'es', entryFileNames: 'bin.mjs', inlineDynamicImports: true } }
    },
    ssr: { target: 'node', noExternal: [] },
    resolve: { conditions: ['node'] }
  })
  const entry = join(outDir, 'bin.mjs')
  if (!existsSync(entry) || statSync(entry).size === 0) throw new Error('relay bundle was not produced')
  const problems = inspectRelayBundle(readFileSync(entry, 'utf8'))
  if (problems.length > 0) throw new Error(`relay bundle failed its checks:\n- ${problems.join('\n- ')}`)
  const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'))
  writeFileSync(join(outDir, 'RUNTIME.json'), JSON.stringify({
    entry: 'bin.mjs', node: '>=20', version: pkg.version, dependencies: ['ws'],
    note: 'Only the "ws" package is needed at runtime; install it next to bin.mjs.', files: readdirSync(outDir).sort()
  }, null, 2))
  console.log(`relay bundle ready: ${entry}`)
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => { console.error(error instanceof Error ? error.message : error); process.exit(1) })
}
