#!/usr/bin/env node
/**
 * 构建表格编辑器插件。与 `examples/acme.note-editor/build.mjs` 同一套做法:**没有任何 npm 依赖**,
 * esbuild 取仓库根的那份。
 *
 * ★ 视图的 react 系与 `nextcowork/ui`、`nextcowork/view` **必须**标 external:
 *   自带 `nextcowork/view` 的话,它和宿主之间的 `ncw:engine:*` 通道没人接,画布永远停在「正在打开」。
 */
import { cpSync, mkdirSync, rmSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createRequire } from 'node:module'

const here = dirname(fileURLToPath(import.meta.url))
const dist = join(here, 'dist')
// 仓库根的 esbuild —— 示例不该为了构建自己而引一份依赖
const esbuild = createRequire(join(here, '../../package.json'))('esbuild')

const VIEW_EXTERNALS = [
  'react',
  'react-dom',
  'react-dom/client',
  'react/jsx-runtime',
  'react/jsx-dev-runtime',
  'nextcowork/ui',
  'nextcowork/view'
]

rmSync(dist, { recursive: true, force: true })
mkdirSync(join(dist, 'view'), { recursive: true })

await esbuild.build({
  entryPoints: [join(here, 'src/extension.ts')],
  outfile: join(dist, 'extension.js'),
  bundle: true,
  format: 'esm',
  platform: 'browser',
  target: 'es2022',
  external: ['nextcowork'],
  minify: true,
  logLevel: 'info'
})

await esbuild.build({
  entryPoints: [join(here, 'view/editor.tsx')],
  outfile: join(dist, 'view/editor.js'),
  bundle: true,
  format: 'esm',
  platform: 'browser',
  target: 'es2022',
  jsx: 'automatic',
  external: VIEW_EXTERNALS,
  minify: true,
  logLevel: 'info'
})

// HTML 原样拷过去 —— 它不需要构建,注入在下发时发生
cpSync(join(here, 'view/editor.html'), join(dist, 'view/editor.html'))

console.log('✓ dist/')
