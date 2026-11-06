/**
 * Runway —— `/v1/text_to_video|image_to_video|video_to_video` + `/v1/tasks/{id}`。
 *
 * ★★ 两条**全局**约定,漏掉任一条都是"请求看起来对、上游不认":
 *   1. 每个请求都要带 `X-Runway-Version: 2024-11-06`(少了直接失败);
 *   2. 创建只回一个 task id,**结果要轮询** `/v1/tasks/{id}`,只有 SUCCEEDED 才带 output。
 * ★ `THROTTLED` **不是错误** —— 它是"排在限流后面,自己会继续"。把它当失败会让
 *   一次正常的排队变成"生成失败",而用户重试就又多花一次钱。
 */
import type { ProviderCredential } from '../../../../shared/domain/credential'
import type { VideoAdapter, VideoAdapterContext, VideoCreateResult, VideoRequest, VideoStatusResult } from './contract'
import { arrayOf, dig, httpFailure, mediaForBody, readJson, strOf } from './contract'

const VERSION = '2024-11-06'

const headers = (cred: ProviderCredential): Record<string, string> => ({
  'content-type': 'application/json',
  authorization: `Bearer ${cred.kind === 'api-key' ? cred.apiKey : cred.kind === 'oauth' ? cred.accessToken : cred.accessKeyId}`,
  'X-Runway-Version': VERSION
})

const pathOf = (action: string): string =>
  action === 'image' ? '/v1/image_to_video' : action === 'edit' ? '/v1/video_to_video' : '/v1/text_to_video'

export const runwayVideoAdapter: VideoAdapter = {
  id: 'runway-video',
  async create(ctx: VideoAdapterContext, request: VideoRequest): Promise<VideoCreateResult> {
    const base = ctx.provider.videoGeneration?.baseUrl ?? 'https://api.dev.runwayml.com'
    const body: Record<string, unknown> = { model: request.model, promptText: request.prompt }
    if (request.duration !== undefined) body['duration'] = Number(request.duration)
    if (request.aspectRatio !== undefined) body['ratio'] = request.aspectRatio
    // ★ Runway 的 promptImage 只收公网地址(内联走 /v1/uploads,那是另一个功能)。
    if (request.image !== undefined) {
      body['promptImage'] = mediaForBody(request.image, { inlineDataUrls: false, where: 'Runway' })
    }
    const video = request.video?.url
    if (video !== undefined) body['videoUri'] = video
    const res = await ctx.fetch(`${base}${pathOf(request.action)}`, {
      method: 'POST',
      headers: headers(ctx.credential),
      body: JSON.stringify(body),
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const id = strOf(dig(payload, 'id'))
    if (id === undefined) throw new Error('Runway: the create response carried no task id')
    return { upstreamId: id, status: 'queued' }
  },

  async status(ctx: VideoAdapterContext, upstreamId: string): Promise<VideoStatusResult> {
    const base = ctx.provider.videoGeneration?.baseUrl ?? 'https://api.dev.runwayml.com'
    const res = await ctx.fetch(`${base}/v1/tasks/${encodeURIComponent(upstreamId)}`, { headers: headers(ctx.credential), signal: ctx.signal })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const status = strOf(dig(payload, 'status'))
    if (status === 'SUCCEEDED') {
      const output = arrayOf(dig(payload, 'output'))
      const url = strOf(output[0])
      if (url === undefined) return { status: 'failed', error: 'Runway reported SUCCEEDED but carried no output URL' }
      return { status: 'succeeded', percent: 100, assets: [{ url, mime: 'video/mp4' }] }
    }
    if (status === 'FAILED') {
      const failure = strOf(dig(payload, 'failure')) ?? strOf(dig(payload, 'failureCode')) ?? 'Runway task failed'
      return { status: 'failed', error: failure }
    }
    if (status === 'CANCELLED') return { status: 'canceled' }
    // ★ THROTTLED 是"排队中",不是失败 —— 见文件头。
    return { status: status === 'PENDING' || status === 'THROTTLED' ? 'queued' : 'running' }
  },

  async cancel(ctx: VideoAdapterContext, upstreamId: string): Promise<void> {
    const base = ctx.provider.videoGeneration?.baseUrl ?? 'https://api.dev.runwayml.com'
    // ★ 取消与删除是同一条 DELETE —— 任务还能取消就取消,已完成就删掉记录。
    const res = await ctx.fetch(`${base}/v1/tasks/${encodeURIComponent(upstreamId)}`, {
      method: 'DELETE',
      headers: headers(ctx.credential),
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
  }
}
