/**
 * xAI Grok Imagine —— `/v1/videos/generations|edits|extensions` + `request_id` 轮询。
 *
 * ★ 三个动作是**三条路径**,不是一个带 mode 的端点 —— 写成一个的话,
 * 编辑会被发去生成端点,而它返回的是一个**新生成**的视频(用户以为在改,
 * 我们其实在画一张新的)。
 * ★ 编辑不接受 duration / aspect_ratio / resolution:官方明确写"输出跟输入走"。
 * 传了会被忽略 —— 所以 `validate` 那一侧干脆不给这几个参数留位置。
 * ★ 结果是一个**临时 URL**,拿到就下载。
 */
import type { ProviderCredential } from '../../../../shared/domain/credential'
import type { VideoAdapter, VideoAdapterContext, VideoCreateResult, VideoRequest, VideoStatusResult } from './contract'
import { assetOfRef, dig, httpFailure, mediaForBody, readJson, strOf } from './contract'

const headers = (cred: ProviderCredential): Record<string, string> => ({
  'content-type': 'application/json',
  authorization: `Bearer ${cred.kind === 'api-key' ? cred.apiKey : cred.kind === 'oauth' ? cred.accessToken : cred.accessKeyId}`
})

const pathOf = (action: string): string =>
  action === 'edit' ? '/videos/edits' : action === 'extend' ? '/videos/extensions' : '/videos/generations'

export const xaiVideoAdapter: VideoAdapter = {
  id: 'xai-video',
  async create(ctx: VideoAdapterContext, request: VideoRequest): Promise<VideoCreateResult> {
    const body: Record<string, unknown> = { model: request.model, prompt: request.prompt }
    if (request.duration !== undefined) body['duration'] = Number(request.duration)
    if (request.aspectRatio !== undefined) body['aspect_ratio'] = request.aspectRatio
    if (request.resolution !== undefined) body['resolution'] = request.resolution
    if (request.audio !== undefined) body['generate_audio'] = request.audio
    /*
      ★ xAI 的图片输入是一个 `{ url }` —— **只收公网地址**。会话图片没有公网地址,
      所以在它上面用图生视频会明确失败并说清原因,而不是发一个它读不懂的 data URL。
    */
    if (request.image !== undefined) {
      body['image'] = { url: mediaForBody(request.image, { inlineDataUrls: false, where: 'xAI' }) }
    }
    if (request.lastFrame !== undefined) {
      body['last_frame'] = { url: mediaForBody(request.lastFrame, { inlineDataUrls: false, where: 'xAI' }) }
    }
    if (request.video !== undefined) body['video'] = { url: request.video.url }

    const res = await ctx.fetch(`${ctx.provider.videoGeneration?.baseUrl ?? ctx.provider.baseUrl}${pathOf(request.action)}`, {
      method: 'POST',
      headers: headers(ctx.credential),
      body: JSON.stringify(body),
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const id = strOf(dig(payload, 'request_id')) ?? strOf(dig(payload, 'id'))
    if (id === undefined) throw new Error('xAI: the create response carried no request_id')
    return { upstreamId: id, status: 'queued' }
  },

  async status(ctx: VideoAdapterContext, upstreamId: string): Promise<VideoStatusResult> {
    const base = ctx.provider.videoGeneration?.baseUrl ?? ctx.provider.baseUrl
    const res = await ctx.fetch(`${base}/videos/${encodeURIComponent(upstreamId)}`, { headers: headers(ctx.credential), signal: ctx.signal })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const status = strOf(dig(payload, 'status'))
    if (status === 'done') {
      const url = strOf(dig(payload, 'video.url'))
      if (url === undefined) return { status: 'failed', error: 'xAI reported done but carried no video URL' }
      const duration = dig(payload, 'video.duration')
      return { status: 'succeeded', percent: 100, assets: [assetOfRef(url, 'video/mp4')], ...(typeof duration === 'number' ? { stage: `${String(duration)}s` } : {}) }
    }
    if (status === 'failed') {
      return { status: 'failed', error: strOf(dig(payload, 'error.message')) ?? strOf(dig(payload, 'error.code')) ?? 'xAI video generation failed' }
    }
    if (status === 'expired') return { status: 'failed', error: 'the xAI video request expired before it completed' }
    return { status: 'running' }
  }
}
