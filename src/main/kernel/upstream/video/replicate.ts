/**
 * Replicate —— `/v1/predictions` + `/v1/predictions/{id}` + `.../cancel`。
 *
 * ★ 输出形状**由模型决定**:可能是 string、array,也可能是一个带 `video` 键的
 *   对象。所以这里用一个统一的"从任意形状里捞出 http(s) 地址"的读取器,
 *   而不是猜某个具体字段名。
 * ★ 输入由模型的 openapi_schema 决定 —— 这里给通用字段,具体模型要什么由用户
 *   在视频页那条绑定上补。
 * ★ 结果默认**一小时后清理**,所以拿到就下载。
 */
import type { ProviderCredential } from '../../../../shared/domain/credential'
import type { VideoAdapter, VideoAdapterContext, VideoCreateResult, VideoRequest, VideoStatusResult } from './contract'
import { dig, httpFailure, mediaForBody, readJson, recordOf, strOf } from './contract'

const headers = (cred: ProviderCredential): Record<string, string> => ({
  'content-type': 'application/json',
  authorization: `Bearer ${cred.kind === 'api-key' ? cred.apiKey : cred.kind === 'oauth' ? cred.accessToken : cred.accessKeyId}`
})

/** 从任意输出形状里捞出可下载的地址。 */
function outputUrls(output: unknown): string[] {
  if (typeof output === 'string') return output.startsWith('http') ? [output] : []
  if (Array.isArray(output)) return output.flatMap((item) => outputUrls(item))
  const obj = recordOf(output)
  if (obj === undefined) return []
  const found: string[] = []
  for (const value of Object.values(obj)) {
    if (typeof value === 'string' && value.startsWith('http')) found.push(value)
    else if (Array.isArray(value) || recordOf(value) !== undefined) found.push(...outputUrls(value))
  }
  return found
}

export const replicateAdapter: VideoAdapter = {
  id: 'replicate-predictions',
  async create(ctx: VideoAdapterContext, request: VideoRequest): Promise<VideoCreateResult> {
    const base = ctx.provider.videoGeneration?.baseUrl ?? 'https://api.replicate.com/v1'
    const input: Record<string, unknown> = { prompt: request.prompt }
    // ★ Replicate 明确接受 data URL(小文件)与 http URL —— 两种都行。
    if (request.image !== undefined) input['image'] = mediaForBody(request.image, { inlineDataUrls: true, where: 'Replicate' })
    if (request.video !== undefined) input['video'] = request.video.url
    if (request.duration !== undefined) input['duration'] = Number(request.duration)
    if (request.aspectRatio !== undefined) input['aspect_ratio'] = request.aspectRatio
    if (request.resolution !== undefined) input['resolution'] = request.resolution
    if (request.seed !== undefined) input['seed'] = request.seed
    /*
      ★ 官方模型(`owner/name`)与"版本锁定"模型走**两条**创建路径。
      这里按常见形态走前者(官方模型);需要钉版本时由用户在绑定里给
      endpointId 形如 `owner/name:version`,那时再走 `{version, input}`。
    */
    const pinned = request.model.includes(':')
    const url = pinned ? `${base}/predictions` : `${base}/models/${request.model}/predictions`
    const body = pinned ? { version: request.model.split(':')[1], input } : { input }
    const res = await ctx.fetch(url, {
      method: 'POST',
      headers: headers(ctx.credential),
      body: JSON.stringify(body),
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const id = strOf(dig(payload, 'id'))
    if (id === undefined) throw new Error('Replicate: the create response carried no prediction id')
    const status = strOf(dig(payload, 'status'))
    return { upstreamId: id, status: status === 'succeeded' ? 'succeeded' : status === 'failed' ? 'failed' : 'queued' }
  },

  async status(ctx: VideoAdapterContext, upstreamId: string): Promise<VideoStatusResult> {
    const base = ctx.provider.videoGeneration?.baseUrl ?? 'https://api.replicate.com/v1'
    const res = await ctx.fetch(`${base}/predictions/${encodeURIComponent(upstreamId)}`, { headers: headers(ctx.credential), signal: ctx.signal })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const status = strOf(dig(payload, 'status'))
    if (status === 'succeeded') {
      const urls = outputUrls(dig(payload, 'output'))
      if (urls.length === 0) return { status: 'failed', error: 'Replicate succeeded but carried no output URL' }
      return { status: 'succeeded', percent: 100, assets: urls.map((url) => ({ url })) }
    }
    if (status === 'failed') return { status: 'failed', error: strOf(dig(payload, 'error')) ?? 'Replicate prediction failed' }
    if (status === 'canceled') return { status: 'canceled' }
    return { status: status === 'starting' ? 'queued' : 'running' }
  },

  async cancel(ctx: VideoAdapterContext, upstreamId: string): Promise<void> {
    const base = ctx.provider.videoGeneration?.baseUrl ?? 'https://api.replicate.com/v1'
    const res = await ctx.fetch(`${base}/predictions/${encodeURIComponent(upstreamId)}/cancel`, {
      method: 'POST',
      headers: headers(ctx.credential),
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
  }
}
