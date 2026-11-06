import assert from 'node:assert/strict'
import { test } from 'node:test'
import { FORBIDDEN_IMPORTS, REQUIRED_BUILTINS, externalSpecifiers, inspectBundle, packageName } from './build-agent.mjs'

const good = 'import { DatabaseSync } from "node:sqlite";\nimport WebSocket from "ws";\nexport {};'

test('accepts a bundle that keeps node:sqlite and has no electron', () => {
  assert.deepEqual(inspectBundle(good), [])
})

test('rejects every spelling of an electron import', () => {
  for (const code of [
    `${good}\nimport { app } from "electron";`,
    `${good}\nimport{app}from'electron'`,
    `${good}\nconst e = require("electron");`,
    `${good}\nawait import("electron/main");`
  ]) {
    assert.deepEqual(inspectBundle(code), ['bundle imports "electron"'], code)
  }
})

test('does not confuse similarly named packages or plain text with an electron import', () => {
  assert.deepEqual(inspectBundle(`${good}\nimport x from "electron-store-like";\n// electron is mentioned in a comment`), [])
})

test('fails when the sqlite builtin import disappears', () => {
  assert.deepEqual(inspectBundle('import WebSocket from "ws";'), ['bundle lost the "node:sqlite" builtin import'])
})

test('the check lists stay in sync with what the service actually requires', () => {
  assert.deepEqual(FORBIDDEN_IMPORTS, ['electron'])
  assert.deepEqual(REQUIRED_BUILTINS, ['node:sqlite'])
})

/**
 * ★ 这几条守的是「别再去扫产物正文」。rollup 的模块图里带子路径
 * (`@modelcontextprotocol/sdk/client/index.js`),要归一到包名;而正文里混着给模型看的
 * TSX 示例(`visualize-guidelines/components.ts` 的模板字符串里写着
 * `import { TrendingUp } from 'lucide-react'`),按正文判依赖会把它当成真 import,
 * 把打包卡死在一个根本没被引用的包上。
 */
test('reads external package names from the chunk module graph, normalized', () => {
  const imports = ['node:sqlite', './chunk.js', 'ws', 'zod', 'sucrase', '@modelcontextprotocol/sdk/client/index.js', 'ssh2-streams']
  assert.deepEqual(externalSpecifiers(imports), ['@modelcontextprotocol/sdk', 'ssh2-streams', 'sucrase', 'ws', 'zod'])
})

test('normalizes scoped and subpath specifiers to the installable package name', () => {
  assert.equal(packageName('@scope/pkg/sub/x.js'), '@scope/pkg')
  assert.equal(packageName('zod/v4'), 'zod')
  assert.equal(packageName('ws'), 'ws')
})

test('an empty import list yields no externals instead of throwing', () => {
  assert.deepEqual(externalSpecifiers([]), [])
  assert.deepEqual(externalSpecifiers([undefined, 42, '']), [])
})
