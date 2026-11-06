/**
 * 智谱 BigModel —— `/api/paas/v4/videos/generations` + 异步结果查询。
 *
 * ★ 一条接口多种型号:CogVideoX-3 / CogVideoX-2 / Flash / 托管 Vidu,
 *   它们的**输入字段名与合法取值都不同**(CogVideoX 用 size/fps,而 Vidu 用
 *   size + aspect_ratio)。所以这里按 model 分流,而不是一个 body 打天下。
 * ★ 首尾帧:CogVideoX-3 用 `image_url` **传两张**(第一张首帧、第二张尾帧),
 *   而不是具名的 first/last 字段 —— 这是这一家特有的形状。
 * ★ 视频走按量 API,Coding Plan 的额度不覆盖。
 */
import type { ProviderCredential } from '../../../../shared/domain/credential'
import type { VideoAdapter, VideoAdapterContext, VideoCreateResult, VideoRequest, VideoStatusResult } from './contract'
import { assetOfRef, dig, httpFailure, mediaForBody, readJson, strOf } from './contract'

const auth = (cred: ProviderCredential): string =>
  `Bearer ${cred.kind === 'api-key' ? cred.apiKey : cred.kind === 'oauth' ? cred.accessToken : cred.accessKeyId}`

const baseOf = (ctx: VideoAdapterContext): string => ctx.provider.videoGeneration?.baseUrl ?? 'https://open.bigmodel.cn/api'

const isVidu = (model: string): boolean => /vidu/iu.test(model)

export const bigmodelVideoAdapter: VideoAdapter = {
  id: 'bigmodel-video',
  async create(ctx: VideoAdapterContext, request: VideoRequest): Promise<VideoCreateResult> {
    const base = baseOf(ctx)
    const body: Record<string, unknown> = { model: request.model, prompt: request.prompt }

    // 智谱明确支持 data:image/png;base64,…(CogVideoX 与 Vidu 都收)。
    const first = request.image === undefined ? undefined : mediaForBody(request.image, { inlineDataUrls: true, where: 'BigModel' })
    const last = request.lastFrame === undefined ? undefined : mediaForBody(request.lastFrame, { inlineDataUrls: true, where: 'BigModel' })
    if (first !== undefined && last !== undefined) {
      // CogVideoX-3 的首尾帧:两张一起放进 image_url 数组,顺序即首/尾。
      body['image_url'] = [first, last]
    } else if (first !== undefined) {
      body['image_url'] = first
    } else if (last !== undefined) {
      body['image_url'] = [last]
    }

    if (request.duration !== undefined) body['duration'] = Number(request.duration)
    if (request.resolution !== undefined) body['size'] = request.resolution
    if (request.aspectRatio !== undefined && isVidu(request.model)) body['aspect_ratio'] = request.aspectRatio
    if (request.audio !== undefined) body['with_audio'] = request.audio

    const res = await ctx.fetch(`${base}/paas/v4/videos/generations`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', authorization: auth(ctx.credential) },
      body: JSON.stringify(body),
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const id = strOf(dig(payload, 'id')) ?? strOf(dig(payload, 'request_id'))
    if (id === undefined) throw new Error('BigModel: the create response carried no task id')
    const status = strOf(dig(payload, 'task_status'))
    return { upstreamId: id, status: status === 'SUCCESS' ? 'succeeded' : 'queued' }
  },

  async status(ctx: VideoAdapterContext, upstreamId: string): Promise<VideoStatusResult> {
    const base = baseOf(ctx)
    const res = await ctx.fetch(`${base}/paas/v4/async-result/${encodeURIComponent(upstreamId)}`, {
      headers: { authorization: auth(ctx.credential) },
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const status = strOf(dig(payload, 'task_status'))
    if (status === 'SUCCESS') {
      const url = strOf(dig(payload, 'video_result.0.url')) ?? strOf(dig(payload, 'video_url'))
      if (url === undefined) return { status: 'failed', error: 'BigModel reported SUCCESS but carried no video URL' }
      return { status: 'succeeded', percent: 100, assets: [assetOfRef(url, 'video/mp4')] }
    }
    if (status === 'FAIL') return { status: 'failed', error: strOf(dig(payload, 'error.message')) ?? 'BigModel video generation failed' }
    return { status: status === 'PROCESSING' ? 'running' : 'queued' }
  }
}
