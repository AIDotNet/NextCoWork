/**
 * 需求：Agent HTTP v1 的资源语义集中在一张路由表；不把 HTTP 字段或响应对象
 * 带进运行时。会话、工具审批和取消均委托给应用同一份事实源。
 */
import type { IncomingMessage, ServerResponse } from 'node:http'
import type { GatewayBackend } from './backend'
import { HttpError, identifier, interactionInput, parse, runInput, sessionInput } from './validation'
import { streamRun } from './stream'

type Context = { backend: GatewayBackend; request: IncomingMessage; response: ServerResponse; url: URL; body: unknown; ids: string[] }
type Route = { method: string; path: RegExp; handle(context: Context): unknown }

/** 需求：保持 JSON 错误和成功响应同一种编码，关闭缓存以免复用旧会话状态。 */
export function json(response: ServerResponse, status: number, data?: unknown): void {
  response.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' })
  response.end(data === undefined ? undefined : JSON.stringify(data))
}

function id(context: Context, index = 0): string {
  return parse(identifier, context.ids[index])
}

function since(context: Context): number {
  const raw = context.url.searchParams.get('sinceSeq') ?? context.request.headers['last-event-id'] ?? '0'
  if (typeof raw !== 'string' || !/^\d+$/.test(raw) || !Number.isSafeInteger(Number(raw))) {
    throw new HttpError(400, 'invalid_request', 'sinceSeq must be a non-negative safe integer')
  }
  return Number(raw)
}

const routes: Route[] = [
  { method: 'GET', path: /^\/v1\/status$/, handle: ({ backend }) => backend.status() },
  { method: 'GET', path: /^\/v1\/workspaces$/, handle: ({ backend }) => ({ data: backend.workspaces() }) },
  { method: 'GET', path: /^\/v1\/models$/, handle: ({ backend }) => ({ data: backend.models() }) },
  { method: 'GET', path: /^\/v1\/sessions$/, handle: (ctx) => ({ data: ctx.backend.sessions(parse(identifier, ctx.url.searchParams.get('workspaceId'))) }) },
  { method: 'POST', path: /^\/v1\/sessions$/, handle: (ctx) => {
    json(ctx.response, 201, ctx.backend.createSession(parse(sessionInput, ctx.body)))
  } },
  { method: 'GET', path: /^\/v1\/sessions\/([^/]+)$/, handle: (ctx) => ctx.backend.session(id(ctx)) },
  { method: 'POST', path: /^\/v1\/runs$/, handle: (ctx) => {
    const handle = ctx.backend.start(parse(runInput, ctx.body))
    json(ctx.response, 202, { runId: handle.runId, sessionId: handle.sessionId, eventsUrl: `/v1/runs/${encodeURIComponent(handle.runId)}/events` })
  } },
  { method: 'GET', path: /^\/v1\/runs\/([^/]+)$/, handle: (ctx) => ctx.backend.run(id(ctx)).snapshot(since(ctx)) },
  { method: 'GET', path: /^\/v1\/runs\/([^/]+)\/events$/, handle: (ctx) => {
    streamRun(ctx.response, ctx.backend.run(id(ctx)), since(ctx))
  } },
  { method: 'POST', path: /^\/v1\/runs\/([^/]+)\/abort$/, handle: (ctx) => {
    ctx.backend.abort(id(ctx)); json(ctx.response, 204)
  } },
  { method: 'GET', path: /^\/v1\/runs\/([^/]+)\/interactions$/, handle: (ctx) => ({ data: ctx.backend.interactions(id(ctx)) }) },
  { method: 'POST', path: /^\/v1\/interactions\/([^/]+)\/response$/, handle: (ctx) => {
    ctx.backend.respond({ ...parse(interactionInput, ctx.body), id: id(ctx) }); json(ctx.response, 204)
  } }
]

/** 需求：未登记端点不能落入任意 IPC 转发；外部 API 只拥有表内明确开放的能力。 */
export function dispatch(context: Omit<Context, 'ids'>): void {
  for (const route of routes) {
    const match = context.url.pathname.match(route.path)
    if (match === null || route.method !== context.request.method) continue
    let ids: string[]
    try { ids = match.slice(1).map((value) => decodeURIComponent(value)) }
    catch { throw new HttpError(400, 'invalid_request', 'Invalid URL encoding') }
    const result = route.handle({ ...context, ids })
    if (!context.response.headersSent) json(context.response, 200, result)
    return
  }
  throw new HttpError(404, 'not_found', 'Endpoint not found')
}
