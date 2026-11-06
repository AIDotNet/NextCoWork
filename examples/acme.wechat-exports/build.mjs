#!/usr/bin/env node
/**
 * 需求：插件宿主只接收单文件 ESM；把解压依赖打入 extension.js，nextcowork 保持 external。
 * 打包只携带可安装文件，不携带开发依赖，也不需要系统上的 Swift/微信进程。
 */
import { cpSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { spawnSync } from 'node:child_process'
import * as esbuild from 'esbuild'

const here = dirname(fileURLToPath(import.meta.url))
const pkg = JSON.parse(readFileSync(join(here, 'package.json'), 'utf8'))
const id = `${pkg.publisher}.${pkg.name}`
const dist = join(here, 'dist')
mkdirSync(dist, { recursive: true })
await esbuild.build({
  entryPoints: [join(here, 'src/extension.ts')],
  outfile: join(dist, 'extension.js'),
  bundle: true,
  format: 'esm',
  platform: 'browser',
  target: 'es2022',
  external: ['nextcowork'],
  minify: true
})
if (process.argv.includes('--package')) {
  const staging = join(here, '.ncw-package')
  rmSync(staging, { recursive: true, force: true })
  mkdirSync(join(staging, id), { recursive: true })
  // ★ assets 必须随包：清单的 icon 指向它，安装器会核对该文件真的在包里。
  for (const entry of ['dist', 'l10n', 'assets']) cpSync(join(here, entry), join(staging, id, entry), { recursive: true })
  const { devDependencies: _dev, scripts: _scripts, ...shipped } = pkg
  writeFileSync(join(staging, id, 'package.json'), JSON.stringify(shipped, null, 2))
  const zip = join(here, `${id}-${pkg.version}.zip`)
  rmSync(zip, { force: true })
  const result = spawnSync('zip', ['-qr', zip, id], { cwd: staging, stdio: 'inherit' })
  rmSync(staging, { recursive: true, force: true })
  if (result.status !== 0) process.exit(result.status ?? 1)
  console.log(zip)
}
