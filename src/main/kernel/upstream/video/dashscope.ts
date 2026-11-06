/**
 * 阿里百炼 —— **原生异步任务**,不是 compatible-mode 的聊天端点。
 *
 * ★★ 三处必须记住的协议差别(漏一个都是一个读不懂的 404):
 *   1. 创建路径是 `/api/v1/services/aigc/video-generation/video-synthesis`,
 *      而聊天那条是 `/compatible-mode/v1`;
 *   2. 创建**必须**带 `X-DashScope-Async: enable`,缺了会报
 *      "current user api does not support synchronous calls";
 *   3. 查询是 `/api/v1/tasks/{task_id}`,**不是** `/video-generation/video-synthesis/{id}`。
 * ★ Wan 2.7 用 `resolution` + `ratio` 两个字段表达输出规格(旧版是 `size`),
 *   而模型/Key/地域必须同属一地 —— 后者由 preset 的 region 表达。
 */
import type { ProviderCredential } from '../../../../shared/domain/credential'
import type { VideoAdapter, VideoAdapterContext, VideoCreateResult, VideoRequest, VideoStatusResult } from './contract'
import { assetOfRef, dig, httpFailure, mediaForBody, readJson, strOf } from './contract'

const auth = (cred: ProviderCredential): string =>
  `Bearer ${cred.kind === 'api-key' ? cred.apiKey : cred.kind === 'oauth' ? cred.accessToken : cred.accessKeyId}`

export const dashscopeVideoAdapter: VideoAdapter = {
  id: 'dashscope-video',
  async create(ctx: VideoAdapterContext, request: VideoRequest): Promise<VideoCreateResult> {
    const base = ctx.provider.videoGeneration?.baseUrl ?? 'https://dashscope.aliyuncs.com'
    const input: Record<string, unknown> = { prompt: request.prompt }
    /*
      ★ 百炼的图片输入叫 `img_url`(不是 image_url),而且**接受 base64 data URL**。
      会话图片没有公网地址,内联是唯一可行的那条(见 contract.mediaForBody)。
    */
    if (request.image !== undefined) {
      input['img_url'] = mediaForBody(request.image, { inlineDataUrls: true, where: 'DashScope' })
    }
    const parameters: Record<string, unknown> = {}
    if (request.resolution !== undefined) parameters['resolution'] = request.resolution
    if (request.aspectRatio !== undefined) parameters['ratio'] = request.aspectRatio
    if (request.duration !== undefined) parameters['duration'] = Number(request.duration)
    if (request.seed !== undefined) parameters['seed'] = request.seed

    const res = await ctx.fetch(`${base}/api/v1/services/aigc/video-generation/video-synthesis`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        authorization: auth(ctx.credential),
        'X-DashScope-Async': 'enable'
      },
      body: JSON.stringify({ model: request.model, input, ...(Object.keys(parameters).length === 0 ? {} : { parameters }) }),
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const id = strOf(dig(payload, 'output.task_id'))
    if (id === undefined) throw new Error('DashScope: the create response carried no task_id')
    return { upstreamId: id, status: 'queued' }
  },

  async status(ctx: VideoAdapterContext, upstreamId: string): Promise<VideoStatusResult> {
    const base = ctx.provider.videoGeneration?.baseUrl ?? 'https://dashscope.aliyuncs.com'
    const res = await ctx.fetch(`${base}/api/v1/tasks/${encodeURIComponent(upstreamId)}`, {
      headers: { authorization: auth(ctx.credential) },
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const status = strOf(dig(payload, 'output.task_status'))
    if (status === 'SUCCEEDED') {
      const url = strOf(dig(payload, 'output.video_url'))
      if (url === undefined) return { status: 'failed', error: 'DashScope reported SUCCEEDED but carried no video_url' }
      return { status: 'succeeded', percent: 100, assets: [assetOfRef(url, 'video/mp4')] }
    }
    if (status === 'FAILED') {
      return { status: 'failed', error: strOf(dig(payload, 'output.message')) ?? strOf(dig(payload, 'output.code')) ?? 'DashScope video generation failed' }
    }
    if (status === 'CANCELED') return { status: 'canceled' }
    if (status === 'UNKNOWN') return { status: 'failed', error: 'the DashScope task id is unknown (it may have expired after 24 hours)' }
    return { status: status === 'PENDING' ? 'queued' : 'running' }
  },

  async cancel(ctx: VideoAdapterContext, upstreamId: string): Promise<void> {
    // ★ 百炼的取消是 POST /api/v1/tasks/{id}/cancel(见"管理异步任务")。
    const base = ctx.provider.videoGeneration?.baseUrl ?? 'https://dashscope.aliyuncs.com'
    const res = await ctx.fetch(`${base}/api/v1/tasks/${encodeURIComponent(upstreamId)}/cancel`, {
      method: 'POST',
      headers: { authorization: auth(ctx.credential) },
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
  }
}
