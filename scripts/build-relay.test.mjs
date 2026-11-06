import assert from 'node:assert/strict'
import { test } from 'node:test'
import { FORBIDDEN_IMPORTS, FORBIDDEN_MARKERS, inspectRelayBundle } from './build-relay.mjs'

const good = 'import WebSocket from "ws";\nimport { createServer } from "node:https";\nexport {};'

test('accepts a relay bundle that only uses ws and node builtins', () => {
  assert.deepEqual(inspectRelayBundle(good), [])
})

test('rejects electron and sqlite imports', () => {
  assert.deepEqual(inspectRelayBundle(`${good}\nimport { app } from "electron";`), ['bundle imports "electron"'])
  assert.deepEqual(inspectRelayBundle(`${good}\nimport { DatabaseSync } from "node:sqlite";`), ['bundle imports "node:sqlite"'])
  assert.deepEqual(inspectRelayBundle(`${good}\nconst s = require('node:sqlite')`), ['bundle imports "node:sqlite"'])
})

test('rejects device-service code that leaked into the relay', () => {
  assert.deepEqual(inspectRelayBundle(`${good}\nconst file = "nextcowork.db";`), ['bundle contains device-service code ("nextcowork.db")'])
  assert.deepEqual(inspectRelayBundle(`${good}\nconst f = "device-identity.json";`), ['bundle contains device-service code ("device-identity.json")'])
})

test('check lists stay in sync', () => {
  assert.deepEqual(FORBIDDEN_IMPORTS, ['electron', 'node:sqlite'])
  assert.deepEqual(FORBIDDEN_MARKERS, ['nextcowork.db', 'device-identity.json'])
})
