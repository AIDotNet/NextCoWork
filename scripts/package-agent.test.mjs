import assert from 'node:assert/strict'
import { test } from 'node:test'
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { buildPackageJson, packedFilename, readBundleExternals, runtimeDependencies } from './package-agent.mjs'

const deps = { ws: '8.22.0', zod: '^4.5.4', 'node-pty': '^1.1.0', '@msgpack/msgpack': '3.1.3', unused: '1.0.0' }

test('keeps only the dependencies the bundle really imports', () => {
  assert.deepEqual(runtimeDependencies(['ws', 'zod', 'node-pty', '@msgpack/msgpack'], deps), {
    '@msgpack/msgpack': '3.1.3', 'node-pty': '^1.1.0', ws: '8.22.0', zod: '^4.5.4'
  })
})

/**
 * ★ 依赖清单来自 rollup 的模块图(`BUNDLE_IMPORTS.json`),不扫产物正文 ——
 * 正文里的 TSX 示例代码会写出 `import … from 'lucide-react'` 这样的句子。
 * 内建与相对路径在这里再挡一道:清单是产物旁边的普通文件,可能被人手改。
 */
test('reads the external list the bundler wrote, and keeps only what can be installed', () => {
  const dir = mkdtempSync(join(tmpdir(), 'ncw-bundle-imports-'))
  try {
    const file = join(dir, 'BUNDLE_IMPORTS.json')
    writeFileSync(file, JSON.stringify({ external: ['node:sqlite', './chunk.js', 'ws', 'zod'] }))
    assert.deepEqual(readBundleExternals(file), ['ws', 'zod'])
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('a missing or malformed external list fails the package instead of silently dropping dependencies', () => {
  const dir = mkdtempSync(join(tmpdir(), 'ncw-bundle-imports-'))
  try {
    assert.throws(() => readBundleExternals(join(dir, 'nope.json')), /run `npm run build:agent` first/)
    const file = join(dir, 'BUNDLE_IMPORTS.json')
    writeFileSync(file, JSON.stringify({ external: 'ws' }))
    assert.throws(() => readBundleExternals(file), /no "external" list/)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('fails the package when the bundle imports something package.json does not declare', () => {
  const result = buildPackageJson({ version: '1.2.3', dependencies: deps }, ['ws', 'left-pad'])
  assert.deepEqual(result.missing, ['left-pad'])
})

test('a package declared only as an optional dependency is not "missing"', () => {
  const result = buildPackageJson({ version: '1.2.3', dependencies: deps, optionalDependencies: { 'cpu-features': '^0.0.10' } }, ['cpu-features'])
  assert.deepEqual(result.missing, [])
})

test('produces a manifest that pins the Node floor and the repository versions', () => {
  const { manifest, missing } = buildPackageJson({ version: '1.2.3', dependencies: deps }, ['ws'])
  assert.deepEqual(missing, [])
  assert.equal(manifest.name, '@aidotnet/nextcowork-device-service')
  assert.equal(manifest.version, '1.2.3')
  assert.equal(manifest.type, 'module')
  assert.equal(manifest.engines.node, '>=24')
  // 装完得有命令可跑:只有 main 时用户还得自己拼 node_modules 里的路径。
  assert.deepEqual(manifest.bin, { 'ncw-device-service': 'bin.mjs' })
  // 文档随包走:npm 页面上看到的就是 docs/device-service.md。
  assert.ok(manifest.files.includes('README.md'))
  // 依赖清单也随包走:装完之后仍能看出这份产物按什么清单装配的。
  assert.ok(manifest.files.includes('BUNDLE_IMPORTS.json'))
  assert.deepEqual(manifest.dependencies, { ws: '8.22.0' })
})

test('reads the tarball name from either `npm pack --json` shape', () => {
  const entry = { filename: 'aidotnet-nextcowork-device-service-1.2.3.tgz' }
  assert.equal(packedFilename(JSON.stringify([entry])), entry.filename) // npm ≤ 11
  assert.equal(packedFilename(JSON.stringify({ '@aidotnet/nextcowork-device-service': entry })), entry.filename) // npm ≥ 12
  assert.throws(() => packedFilename('{}'), /no filename/)
})
