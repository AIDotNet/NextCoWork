/**
 * Luma —— **两条 API 各一条适配器**(Ray 3.2 在 Agents API,Ray 2/Flash 在旧 Dream Machine)。
 *
 * ★★ 为什么不能合成一条:两个 host 不同、请求形状不同,而且**旧版的延长只接受
 *    它自己生成过的 generation id**(`keyframes.frame0.type === 'generation'`),
 *    新版的 video_edit 接受一个 host 上的 URL。混在一起的结果是把新参数发去旧端点。
 * ★ 旧版**不接受直接上传图片**(官方原话:要自己传到 CDN 再给 url)。
 */
import type { ProviderCredential } from '../../../../shared/domain/credential'
import type { VideoAdapter, VideoAdapterContext, VideoCreateResult, VideoRequest, VideoStatusResult } from './contract'
import { arrayOf, dig, httpFailure, mediaForBody, readJson, recordOf, strOf } from './contract'

const auth = (cred: ProviderCredential): string =>
  `Bearer ${cred.kind === 'api-key' ? cred.apiKey : cred.kind === 'oauth' ? cred.accessToken : cred.accessKeyId}`

/** ── 当前 Agents API(Ray 3.2)── */
export const lumaAgentsAdapter: VideoAdapter = {
  id: 'luma-video',
  async create(ctx: VideoAdapterContext, request: VideoRequest): Promise<VideoCreateResult> {
    const base = ctx.provider.videoGeneration?.baseUrl ?? 'https://agents.lumalabs.ai/v1'
    const body: Record<string, unknown> = {
      model: request.model,
      type: request.action === 'edit' ? 'video_edit' : 'video',
      prompt: request.prompt
    }
    if (request.action === 'edit' && request.video !== undefined) body['source'] = { url: request.video.url }
    const videoOptions: Record<string, unknown> = {}
    if (request.resolution !== undefined) videoOptions['resolution'] = request.resolution
    if (request.duration !== undefined) videoOptions['duration'] = typeof request.duration === 'number' ? `${String(request.duration)}s` : request.duration
    if (Object.keys(videoOptions).length > 0) body['video'] = videoOptions
    if (request.aspectRatio !== undefined) body['aspect_ratio'] = request.aspectRatio
    /*
      ★ Agents API 的帧是 `video.start_frame` / `video.end_frame`,
      而不是顶层字段 —— 放错层级不会被拒,只是被忽略(出一段无关的视频)。
    */
    /*
      ★ Luma 官方明确:「你应该自己上传图片、用你自己的 CDN 地址 —— 这是目前
      唯一的传图方式」。所以会话图片在这里也明确失败,而不是发一个它不认的 data URL。
    */
    const first = request.image === undefined ? undefined : mediaForBody(request.image, { inlineDataUrls: false, where: 'Luma' })
    const last = request.lastFrame === undefined ? undefined : mediaForBody(request.lastFrame, { inlineDataUrls: false, where: 'Luma' })
    if (first !== undefined || last !== undefined) {
      const video = recordOf(body['video']) ?? {}
      if (first !== undefined) video['start_frame'] = { url: first }
      if (last !== undefined) video['end_frame'] = { url: last }
      body['video'] = video
    }
    const res = await ctx.fetch(`${base}/generations`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', authorization: auth(ctx.credential) },
      body: JSON.stringify(body),
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const id = strOf(dig(payload, 'id'))
    if (id === undefined) throw new Error('Luma: the create response carried no generation id')
    return { upstreamId: id, status: 'queued' }
  },

  async status(ctx: VideoAdapterContext, upstreamId: string): Promise<VideoStatusResult> {
    const base = ctx.provider.videoGeneration?.baseUrl ?? 'https://agents.lumalabs.ai/v1'
    const res = await ctx.fetch(`${base}/generations/${encodeURIComponent(upstreamId)}`, { headers: { authorization: auth(ctx.credential) }, signal: ctx.signal })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const state = strOf(dig(payload, 'state'))
    if (state === 'completed') {
      const assets: { url: string; mime?: string }[] = []
      for (const out of arrayOf(dig(payload, 'output'))) {
        const url = strOf(dig(out, 'url')) ?? strOf(out)
        if (url !== undefined) assets.push({ url, mime: 'video/mp4' })
      }
      if (assets.length === 0) return { status: 'failed', error: 'Luma reported completed but carried no output URL' }
      return { status: 'succeeded', percent: 100, assets }
    }
    if (state === 'failed') return { status: 'failed', error: strOf(dig(payload, 'failure_reason')) ?? 'Luma generation failed' }
    return { status: 'running' }
  }
}

/** ── 旧 Dream Machine API(Ray 2 / Flash)── */
export const lumaLegacyAdapter: VideoAdapter = {
  id: 'luma-legacy-video',
  async create(ctx: VideoAdapterContext, request: VideoRequest): Promise<VideoCreateResult> {
    const base = ctx.provider.videoGeneration?.baseUrl ?? 'https://api.lumalabs.ai/dream-machine/v1'
    const body: Record<string, unknown> = { model: request.model, prompt: request.prompt }
    if (request.aspectRatio !== undefined) body['aspect_ratio'] = request.aspectRatio
    if (request.resolution !== undefined) body['resolution'] = request.resolution
    if (request.duration !== undefined) body['duration'] = typeof request.duration === 'number' ? `${String(request.duration)}s` : request.duration
    const keyframes: Record<string, unknown> = {}
    // ★ 同上:旧版同样只收公网 CDN 地址。
    const first = request.image === undefined ? undefined : mediaForBody(request.image, { inlineDataUrls: false, where: 'Luma' })
    const last = request.lastFrame === undefined ? undefined : mediaForBody(request.lastFrame, { inlineDataUrls: false, where: 'Luma' })
    if (first !== undefined) keyframes['frame0'] = { type: 'image', url: first }
    if (last !== undefined) keyframes['frame1'] = { type: 'image', url: last }
    /*
      ★★ 旧版延长的输入是 `{ type: 'generation', id }` —— 它只认**自己生成过的
      generation id**,不认任意 URL。所以这里把"源视频 URL"当 generation id 传,
      并要求调用方(工具层)已经确认那是本会话某次 Luma 生成的 id。
      传一个普通 URL 的话上游会拒,而那句报错不会说清原因。
    */
    if (request.video !== undefined) keyframes['frame0'] = { type: 'generation', id: request.video.url }
    if (Object.keys(keyframes).length > 0) body['keyframes'] = keyframes

    const res = await ctx.fetch(`${base}/generations`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', authorization: auth(ctx.credential) },
      body: JSON.stringify(body),
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const id = strOf(dig(payload, 'id'))
    if (id === undefined) throw new Error('Luma: the create response carried no generation id')
    return { upstreamId: id, status: 'queued' }
  },

  async status(ctx: VideoAdapterContext, upstreamId: string): Promise<VideoStatusResult> {
    const base = ctx.provider.videoGeneration?.baseUrl ?? 'https://api.lumalabs.ai/dream-machine/v1'
    const res = await ctx.fetch(`${base}/generations/${encodeURIComponent(upstreamId)}`, { headers: { authorization: auth(ctx.credential) }, signal: ctx.signal })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const state = strOf(dig(payload, 'state'))
    if (state === 'completed') {
      const url = strOf(dig(payload, 'assets.video'))
      if (url === undefined) return { status: 'failed', error: 'Luma reported completed but carried no assets.video' }
      return { status: 'succeeded', percent: 100, assets: [{ url, mime: 'video/mp4' }] }
    }
    if (state === 'failed') return { status: 'failed', error: strOf(dig(payload, 'failure_reason')) ?? 'Luma generation failed' }
    return { status: 'running' }
  }
}
