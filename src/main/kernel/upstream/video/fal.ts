/**
 * fal.ai —— 队列协议(`queue.fal.run/{endpoint}`)。
 *
 * ★★ **鉴权头是 `Authorization: Key <key>`,不是 Bearer。** 照抄生图那边的
 *    `Bearer` 写法会得到一个 401,而它不会说"格式错了"。
 *
 * ★★ **任务身份是「endpoint + request_id」一对。** 队列的查询/取消地址是
 *    `{endpoint}/requests/{id}/status|cancel`,而 endpoint 段只在**提交时用的
 *    那个地址**里。所以创建时把它存进 `route`(`VideoCreateResult.route`),
 *    查询/取消一律用那个值 —— 绝不拿当前模型别名现推:用户改过绑定之后,
 *    别名指向的是**另一条队列**,那时查到的是另一个模型的状态,或者一个 404。
 *
 * ★ 取消是 **PUT**;202 只代表"收到取消请求",**不代表已取消**(官方明确:
 *   已在处理中的可能仍会完成)—— 所以取消成功后状态仍是 running,由轮询收尾。
 * ★ COMPLETED **仍可能带 error**(官方原话),所以不能只看状态名。
 */
import type { ProviderCredential } from '../../../../shared/domain/credential'
import type { VideoAdapter, VideoAdapterContext, VideoCreateResult, VideoRequest, VideoStatusResult } from './contract'
import { dig, httpFailure, mediaForBody, readJson, strOf } from './contract'

const headers = (cred: ProviderCredential): Record<string, string> => ({
  'content-type': 'application/json',
  authorization: `Key ${cred.kind === 'api-key' ? cred.apiKey : cred.kind === 'oauth' ? cred.accessToken : cred.accessKeyId}`
})

const baseOf = (ctx: VideoAdapterContext): string => ctx.provider.videoGeneration?.baseUrl ?? 'https://queue.fal.run'

/** 从 `{endpoint}/requests/{id}` 形态的 route 里取 endpoint 段。 */
export function falEndpointOf(route: string | undefined, fallback: string): string {
  if (route === undefined || route === '') return fallback
  const at = route.indexOf('/requests/')
  return at < 0 ? route : route.slice(0, at)
}

/** fal 的视频结果形状逐模型不同,统一从任意形状里捞出 http(s) 地址。 */
function videoUrls(output: unknown): string[] {
  if (typeof output === 'string') return output.startsWith('http') ? [output] : []
  if (Array.isArray(output)) return output.flatMap((item) => videoUrls(item))
  if (typeof output !== 'object' || output === null) return []
  const obj = output as Record<string, unknown>
  const found: string[] = []
  for (const value of Object.values(obj)) {
    if (typeof value === 'string' && value.startsWith('http')) found.push(value)
    else if (typeof value === 'object' && value !== null) found.push(...videoUrls(value))
  }
  return found
}

export const falQueueAdapter: VideoAdapter = {
  id: 'fal-queue',
  async create(ctx: VideoAdapterContext, request: VideoRequest): Promise<VideoCreateResult> {
    const endpoint = request.model
    const input: Record<string, unknown> = { prompt: request.prompt }
    // ★ fal 多数 endpoint 收公网 URL;它在协议上也支持 fal CDN 地址(不属本轮)。
    if (request.image !== undefined) input['image_url'] = mediaForBody(request.image, { inlineDataUrls: false, where: 'fal' })
    if (request.video !== undefined) input['video_url'] = request.video.url
    if (request.duration !== undefined) input['duration'] = Number(request.duration)
    if (request.aspectRatio !== undefined) input['aspect_ratio'] = request.aspectRatio
    if (request.resolution !== undefined) input['resolution'] = request.resolution
    if (request.seed !== undefined) input['seed'] = request.seed

    const res = await ctx.fetch(`${baseOf(ctx)}/${endpoint}`, {
      method: 'POST',
      headers: headers(ctx.credential),
      body: JSON.stringify(input),
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const id = strOf(dig(payload, 'request_id'))
    if (id === undefined) throw new Error('fal: the submit response carried no request_id')
    // ★ 存下"这条队列"的 path —— 恢复查询全靠它,理由见文件头。
    return { upstreamId: id, status: 'queued', route: endpoint }
  },

  async status(ctx: VideoAdapterContext, upstreamId: string, route?: string): Promise<VideoStatusResult> {
    const endpoint = falEndpointOf(route, ctx.profile.id)
    const res = await ctx.fetch(`${baseOf(ctx)}/${endpoint}/requests/${encodeURIComponent(upstreamId)}/status`, {
      headers: headers(ctx.credential),
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const status = strOf(dig(payload, 'status'))
    if (status === 'COMPLETED') {
      /*
        ★★ COMPLETED 仍可能是失败 —— 官方明确:失败时同一个状态名会带
        `error` / `error_type`。先看 error,再去看结果。
      */
      const error = strOf(dig(payload, 'error'))
      if (error !== undefined) return { status: 'failed', error }
      const result = await ctx.fetch(`${baseOf(ctx)}/${endpoint}/requests/${encodeURIComponent(upstreamId)}`, {
        headers: headers(ctx.credential),
        signal: ctx.signal
      })
      if (!result.ok) throw new Error(httpFailure(result.status, await result.text().catch(() => '')))
      const output = await readJson(result)
      const urls = videoUrls(output)
      if (urls.length === 0) return { status: 'failed', error: 'fal reported COMPLETED but carried no video URL' }
      return { status: 'succeeded', percent: 100, assets: urls.map((url) => ({ url })) }
    }
    // IN_QUEUE 带一个 queue_position —— 那是真实的排队信息,直接当阶段文案。
    if (status === 'IN_QUEUE') {
      const position = dig(payload, 'queue_position')
      return { status: 'queued', ...(typeof position === 'number' ? { stage: `queue ${String(position)}` } : {}) }
    }
    return { status: 'running' }
  },

  async cancel(ctx: VideoAdapterContext, upstreamId: string, route?: string): Promise<void> {
    const endpoint = falEndpointOf(route, ctx.profile.id)
    const res = await ctx.fetch(`${baseOf(ctx)}/${endpoint}/requests/${encodeURIComponent(upstreamId)}/cancel`, {
      // ★ PUT,不是 POST —— 见文件头。
      method: 'PUT',
      headers: headers(ctx.credential),
      signal: ctx.signal
    })
    /*
      ★ 202 = 收到取消请求;400 `ALREADY_COMPLETED` 与 404 都不该被当成功 ——
      照官方那张表分流,让上层能如实告诉用户"它已经跑完了 / 已经不存在了"。
    */
    if (res.status === 202) return
    const body = await res.text().catch(() => '')
    if (res.status === 400 && body.includes('ALREADY_COMPLETED')) {
      throw new Error('fal: the request already completed before the cancel arrived')
    }
    if (res.status === 404) throw new Error('fal: no such request (it may already be gone)')
    throw new Error(httpFailure(res.status, body))
  }
}
