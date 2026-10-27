/**
 * 需求：真实 TCP 验证外部 Agent 的鉴权、输入边界、SSE 恢复和会话并发；
 * 复用生产 adapter/启动器，模型正文用仓库已有演示上游，不调用收费服务。
 */
import { createServer } from 'node:net'
import { request as httpRequest } from 'node:http'
import type { WebContents } from 'electron'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import type { RunRequest } from '../../../shared/agent/run-request'
import { isToolResultOnly, type AgentMessage } from '../../../shared/agent/message'
import type { Session } from '../../../shared/domain/session'
import type { GatewayStatus } from '../../../shared/domain/provider'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import { startExternalRun, startRun, abortRun, shutdownRuns } from '../../ipc/agent'
import { interactions } from '../../kernel/interaction-gate'
import { nodeHost } from '../../kernel/host'
import { collect, runs, type RunHandle } from '../../kernel/run-registry'
import { DEMO_ALIAS, DEMO_ALIASES, DEMO_PROVIDER, withDemo } from '../../kernel/upstream/demo'
import { installHost, resetRuntimeForTest } from '../../runtime'
import { store } from '../../state/store'
import { AgentBackend } from '../agent-backend'
import { GatewayServer } from '../server'

const KEY = 'a'.repeat(64)
let server: GatewayServer
let base: string
let start: (request: RunRequest) => RunHandle
let backend: AgentBackend

async function request(path: string, body?: unknown, init: RequestInit = {}): Promise<Response> {
  return fetch(base + path, {
    ...(body === undefined ? {} : { method: 'POST', body: JSON.stringify(body) }),
    ...init,
    headers: { Authorization: `Bearer ${KEY}`, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }), ...init.headers },
    signal: init.signal ?? AbortSignal.timeout(10_000)
  })
}

async function createSession(): Promise<Session> {
  const response = await request('/v1/sessions', { workspaceId: 'w1' })
  expect(response.status).toBe(201)
  return await response.json() as Session
}

async function run(sessionId: string): Promise<string> {
  const response = await request('/v1/runs', { sessionId, text: 'hello', permissionMode: 'full' })
  expect(response.status).toBe(202)
  return (await response.json() as { runId: string }).runId
}

beforeEach(async () => {
  resetRuntimeForTest()
  store.putWorkspace({ id: 'w1', name: 'API workspace', rootPath: '', environment: { kind: 'local' }, settings: { ...DEFAULT_WORKSPACE_SETTINGS, defaultModel: DEMO_ALIAS }, createdAt: 1, lastOpenedAt: 1 })
  installHost(withDemo(nodeHost(), { chunkDelayMs: 0 }))
  store.putProvider(DEMO_PROVIDER)
  for (const alias of DEMO_ALIASES) store.putAlias(alias)
  start = (req) => startExternalRun(req)
  backend = new AgentBackend({
    start: (req) => start(req),
    abort: (runId) => abortRun({ runId, cascade: true }),
    status: (): GatewayStatus => ({ enabled: true, port: server?.port ?? null, host: '127.0.0.1', hasKey: true, failover: false, health: [] }),
    created: () => {}
  })
  server = new GatewayServer(backend, KEY, '127.0.0.1')
  await server.listen(0)
  base = `http://127.0.0.1:${server.port}`
})

afterEach(async () => {
  await server.close()
  shutdownRuns()
  interactions.clear()
  resetRuntimeForTest()
})

describe('Agent HTTP boundary', () => {
  it('requires Bearer auth on business routes and rejects foreign browser origins and rebinding hosts', async () => {
    expect((await request('/v1/workspaces', undefined, { headers: { Authorization: '' } })).status).toBe(401)
    expect((await request('/v1/runs', { sessionId: 's', text: 'x' }, { headers: { Authorization: 'Bearer wrong' } })).status).toBe(401)
    expect((await request('/v1/status', undefined, { headers: { Origin: 'https://evil.example' } })).status).toBe(403)
    // Node fetch 不允许覆写 Host，使用真实 HTTP 客户端验证重绑定边界。
    const hostStatus = await new Promise<number | undefined>((resolve, reject) => {
      const req = httpRequest(base + '/v1/status', { headers: { Authorization: `Bearer ${KEY}`, Host: 'evil.example' } }, (response) => {
        response.resume(); resolve(response.statusCode)
      })
      req.on('error', reject); req.end()
    })
    expect(hostStatus).toBe(403)
    const response = await request('/v1/workspaces')
    expect(response.status).toBe(200)
    expect((await response.json() as { data: Array<{ id: string }> }).data[0]?.id).toBe('w1')
  })

  it('serves offline Swagger resources and the authoritative contract without exposing the key', async () => {
    for (const path of ['/docs', '/docs/', '/docs/swagger-ui.css', '/docs/swagger-ui-bundle.js', '/docs/init.js', '/docs/openapi.json']) {
      const response = await fetch(base + path)
      expect(response.status).toBe(200)
      expect(response.headers.get('content-security-policy')).toContain("connect-src 'self'")
      expect(response.headers.get('content-security-policy')).toContain("frame-ancestors 'none'")
      expect(response.headers.get('x-content-type-options')).toBe('nosniff')
      expect(await response.text()).not.toContain(KEY)
    }
    const contract = await (await fetch(base + '/docs/openapi.json')).json()
    expect(contract.servers).toEqual([{ url: '/', description: expect.any(String) }])
    expect(contract.security).toEqual([{ BearerKey: [] }])
    expect(contract.paths['/v1/runs/{runId}/abort'].post.operationId).toBe('abortRun')
    const init = await (await fetch(base + '/docs/init.js')).text()
    expect(init).toContain('persistAuthorization: false')
    expect(init).toContain('validatorUrl: null')
    expect((await fetch(base + '/docs', { method: 'HEAD' })).status).toBe(200)
    expect((await fetch(base + '/docs/missing.js')).status).toBe(401)
    expect((await fetch(base + '/docs', { method: 'POST' })).status).toBe(401)
  })

  it('allows authenticated Swagger same-origin create, send, query and abort calls only', async () => {
    start = (req) => startExternalRun(req, () => {})
    const init = { headers: { Origin: base } }
    expect((await request('/v1/sessions', { workspaceId: 'w1' }, { headers: { Origin: base, Authorization: '' } })).status).toBe(401)
    for (const origin of ['null', 'https://evil.example', base + '.evil.example', base + '/']) {
      expect((await request('/v1/sessions', { workspaceId: 'w1' }, { headers: { Origin: origin } })).status).toBe(403)
    }
    expect(store.listSessions('w1')).toHaveLength(0)
    const session = await (await request('/v1/sessions', { workspaceId: 'w1' }, init)).json() as Session
    const accepted = await request('/v1/runs', { sessionId: session.id, text: 'hello' }, init)
    expect(accepted.status).toBe(202)
    const { runId } = await accepted.json() as { runId: string }
    expect((await request(`/v1/runs/${runId}`, undefined, init)).status).toBe(200)
    expect((await request(`/v1/sessions/${session.id}`, undefined, init)).status).toBe(200)
    expect((await request(`/v1/runs/${runId}/abort`, undefined, { ...init, method: 'POST' })).status).toBe(204)
    expect(runs.get(runId)?.signal.aborted).toBe(true)
  })

  it('rejects malformed, oversized, unknown and unsupported requests before side effects', async () => {
    const post = (body: string, type = 'application/json'): Promise<Response> => request('/v1/sessions', undefined, { method: 'POST', body, headers: { 'Content-Type': type } })
    expect((await post('{')).status).toBe(400)
    expect((await post('{}', 'text/plain')).status).toBe(415)
    expect((await post('x'.repeat(1024 * 1024 + 1))).status).toBe(413)
    expect((await request('/v1/sessions', { workspaceId: 'w1', parentSessionId: 'hidden' })).status).toBe(400)
    expect((await request('/v1/sessions', { workspaceId: 'missing' })).status).toBe(404)
    expect((await request('/v1/chat/completions', {})).status).toBe(404)
    expect(store.listSessions('w1')).toHaveLength(0)
    expect((await request('/v1/models')).status).toBe(200)
  })

  it('falls back to a real free port and only enables wildcard binding explicitly', async () => {
    const blocker = createServer()
    await new Promise<void>((resolve) => blocker.listen(0, '127.0.0.1', resolve))
    const address = blocker.address()
    if (address === null || typeof address === 'string') throw new Error('No blocker address')
    const remote = new GatewayServer(backend, KEY, '0.0.0.0')
    const fallback = new GatewayServer(backend, KEY, '127.0.0.1')
    try {
      await fallback.listen(address.port)
      expect(fallback.port).not.toBe(address.port)
      await remote.listen(0)
      expect(remote.host).toBe('0.0.0.0')
      const response = await fetch(`http://127.0.0.1:${remote.port}/v1/status`, { headers: { Authorization: `Bearer ${KEY}`, Host: 'lan.example' } })
      expect(response.status).toBe(200)
      expect((await fetch(`http://127.0.0.1:${remote.port}/v1/status`)).status).toBe(401)
    } finally {
      await remote.close()
      await fallback.close()
      await new Promise<void>((resolve) => blocker.close(() => resolve()))
    }
  })

  it('rejects API requests while the account configuration is switching', async () => {
    let available = false
    const changing = new GatewayServer(backend, KEY, '127.0.0.1', () => available)
    await changing.listen(0)
    const url = `http://127.0.0.1:${changing.port}/v1/workspaces`
    const headers = { Authorization: `Bearer ${KEY}` }
    try {
      expect((await fetch(url, { headers })).status).toBe(503)
      available = true
      expect((await fetch(url, { headers })).status).toBe(200)
    } finally { await changing.close() }
  })

  it('persists consecutive turns through the real Agent runtime and replays a completed run', async () => {
    const session = await createSession()
    const firstId = await run(session.id)
    const first = runs.get(firstId)!
    const events = await collect(first)
    expect(first.status).toBe('done')
    expect(events.some((event) => event.type === 'tool_end')).toBe(true)
    const stream = await request(`/v1/runs/${firstId}/events`)
    const replay = await stream.text()
    expect(stream.headers.get('content-type')).toContain('text/event-stream')
    expect(replay).toContain('event: snapshot')
    expect(replay).toContain('message_commit')
    expect(replay).toContain('"status":"done"')
    const second = runs.get(await run(session.id))!
    await collect(second)
    const response = await request(`/v1/sessions/${session.id}`)
    const detail = await response.json() as { messages: AgentMessage[] }
    expect(detail.messages.filter((message) => message.role === 'user' && !isToolResultOnly(message))).toHaveLength(2)
    expect(second.status).toBe('done')
  })

  it('rejects API and desktop collisions on the same session while allowing another session', async () => {
    start = (req) => startExternalRun(req, () => {})
    const a = await createSession()
    const b = await createSession()
    const first = await run(a.id)
    expect((await request('/v1/runs', { sessionId: a.id, text: 'collision' })).status).toBe(409)
    expect(await run(b.id)).not.toBe(first)
    const sender = { id: 901, once: () => {}, isDestroyed: () => false, send: () => {} } as unknown as WebContents
    const original = runs.get(first)!
    const req: RunRequest = { runId: 'desktop-conflict', sessionId: a.id, workspaceId: 'w1', depth: 0, input: [], mode: 'code', thinking: 'off', permissionMode: 'ask', webSearch: false, model: DEMO_ALIAS, skillIds: [] }
    expect(() => startRun(req, { id: 901, kind: 'main', sender }, () => {})).toThrow('Session already has an active run')
    expect(original.status).toBe('running')
    const invalid = await request('/v1/runs', { sessionId: a.id, text: 'x', parentRunId: first })
    expect(invalid.status).toBe(400)
  })

  it('streams live events, releases disconnected observers and resumes from Last-Event-ID', async () => {
    start = (req) => startExternalRun(req, () => {})
    const handle = runs.get(await run((await createSession()).id))!
    const initialListeners = handle.listenerCount
    const controller = new AbortController()
    const response = await request(`/v1/runs/${handle.runId}/events`, undefined, { signal: controller.signal })
    const reader = response.body!.getReader()
    expect(new TextDecoder().decode((await reader.read()).value)).toContain('event: snapshot')
    handle.emit({ type: 'stream', delta: { type: 'text_delta', index: 0, text: 'live text' } })
    expect(new TextDecoder().decode((await reader.read()).value)).toContain('live text')
    const cursor = handle.seq
    controller.abort()
    await new Promise((resolve) => setTimeout(resolve, 30))
    expect(handle.listenerCount).toBe(initialListeners)
    expect(handle.status).toBe('running')
    handle.finish('done')
    const resumed = await request(`/v1/runs/${handle.runId}/events`, undefined, { headers: { 'Last-Event-ID': String(cursor) } })
    const text = await resumed.text()
    expect(text).toContain('run_end')
    expect(text).not.toContain('live text')
    expect((await request(`/v1/runs/${handle.runId}/events?sinceSeq=-1`)).status).toBe(400)
  })

  it('answers real pending interactions and cascades abort through the existing gate', async () => {
    start = (req) => startExternalRun(req, () => {})
    const handle = runs.get(await run((await createSession()).id))!
    const pending = interactions.request(handle, { kind: 'ask_user', questions: [{ header: 'scope', question: 'Scope?', options: [], multiSelect: false, allowFreeform: true }] }, Date.now())
    const list = await request(`/v1/runs/${handle.runId}/interactions`)
    const interaction = (await list.json() as { data: Array<{ id: string }> }).data[0]!
    expect((await request(`/v1/interactions/${interaction.id}/response`, { kind: 'ask_user', answers: [] })).status).toBe(400)
    expect((await request(`/v1/interactions/${interaction.id}/response`, { kind: 'ask_user', answers: [['API']] })).status).toBe(204)
    expect(await pending).toMatchObject({ kind: 'ask_user', answers: [['API']] })
    expect((await request(`/v1/interactions/${interaction.id}/response`, { kind: 'ask_user', answers: [['twice']] })).status).toBe(409)
    const child = runs.create({ runId: 'child', sessionId: 'child-session', workspaceId: 'w1', parentRunId: handle.runId, depth: 1, input: [], model: DEMO_ALIAS, mode: 'code', thinking: 'off', permissionMode: 'ask', webSearch: false, skillIds: [] })
    expect((await request(`/v1/runs/${handle.runId}/abort`, {})).status).toBe(204)
    expect(handle.signal.aborted).toBe(true)
    expect(child.signal.aborted).toBe(true)
  })
})
