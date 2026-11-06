/**
 * MiniMax —— **两条版本线**,靠 profile 的 `apiVersion` 区分。
 *
 * ★★ 这不是"新老两种写法"那么简单:H3/H3-Max 的 V2 接口成功后**直接给
 * `task.content.url`**;而 Hailuo 的 V1 接口成功后只给一个 **file_id**,
 *   还要再走一次 **下载接口** 才拿得到真正的视频地址。合并成一条的话,
 *   V1 的成品永远取不到(状态显示成功、就是没有文件),而 V2 会多发一次请求。
 *
 * ★ V1 的鉴权头历史上是 `Bearer <key>`;V2 也是 Bearer(账号体系内)。
 */
import type { ProviderCredential } from '../../../../shared/domain/credential'
import type { VideoAdapter, VideoAdapterContext, VideoCreateResult, VideoRequest, VideoStatusResult } from './contract'
import { dig, httpFailure, mediaForBody, readJson, strOf } from './contract'

const auth = (cred: ProviderCredential): string =>
  `Bearer ${cred.kind === 'api-key' ? cred.apiKey : cred.kind === 'oauth' ? cred.accessToken : cred.accessKeyId}`

const baseOf = (ctx: VideoAdapterContext): string => ctx.provider.videoGeneration?.baseUrl ?? 'https://api.minimax.io'

/** 首帧/尾帧在 V2 里是 content 数组里的 role。 */
function v2Content(request: VideoRequest): unknown[] {
  const content: unknown[] = [{ type: 'text', text: request.prompt }]
  // ★ V2 的 image_url.url 明确支持 `data:image/<fmt>;base64,…`。
  if (request.image !== undefined) {
    content.push({ type: 'image_url', image_url: { url: mediaForBody(request.image, { inlineDataUrls: true, where: 'MiniMax' }) }, role: 'first_frame' })
  }
  if (request.lastFrame !== undefined) {
    content.push({ type: 'image_url', image_url: { url: mediaForBody(request.lastFrame, { inlineDataUrls: true, where: 'MiniMax' }) }, role: 'last_frame' })
  }
  if (request.video !== undefined) {
    content.push({ type: 'video_url', video_url: { url: request.video.url }, role: 'reference_video' })
  }
  return content
}

export const minimaxVideoAdapter: VideoAdapter = {
  id: 'minimax-video',
  async create(ctx: VideoAdapterContext, request: VideoRequest): Promise<VideoCreateResult> {
    const base = baseOf(ctx)
    const v2 = ctx.profile.apiVersion === 'v2'
    /*
      ★ V2 的 `resolution` 只认 `480P/768P/2K`,V1 只认 `720P/768P/1080P` ——
      两套取值不通用,所以各写各的 body,不抽"一个函数套两种"。
    */
    if (v2) {
      const body: Record<string, unknown> = {
        model: request.model,
        content: v2Content(request),
        resolution: request.resolution ?? '768P',
        duration: Number(request.duration ?? 5)
      }
      if (request.aspectRatio !== undefined) body['ratio'] = request.aspectRatio
      const res = await ctx.fetch(`${base}/v2/video_generation`, {
        method: 'POST',
        headers: { 'content-type': 'application/json', authorization: auth(ctx.credential) },
        body: JSON.stringify(body),
        signal: ctx.signal
      })
      if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
      const payload = await readJson(res)
      const id = strOf(dig(payload, 'task_id'))
      if (id === undefined) throw new Error('MiniMax: the create response carried no task_id')
      return { upstreamId: id, status: 'queued' }
    }

    const body: Record<string, unknown> = { model: request.model, prompt: request.prompt }
    if (request.duration !== undefined) body['duration'] = Number(request.duration)
    if (request.resolution !== undefined) body['resolution'] = request.resolution
    // ★ V1 的在线图片走公网 URL(它的内联形态在文档里没有;拿不到就明确报错)。
    if (request.image !== undefined) body['first_frame_image'] = mediaForBody(request.image, { inlineDataUrls: true, where: 'MiniMax v1' })
    if (request.lastFrame !== undefined) body['last_frame_image'] = mediaForBody(request.lastFrame, { inlineDataUrls: true, where: 'MiniMax v1' })
    const res = await ctx.fetch(`${base}/v1/video_generation`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', authorization: auth(ctx.credential) },
      body: JSON.stringify(body),
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const id = strOf(dig(payload, 'task_id'))
    if (id === undefined) throw new Error('MiniMax: the create response carried no task_id')
    return { upstreamId: id, status: 'queued' }
  },

  async status(ctx: VideoAdapterContext, upstreamId: string): Promise<VideoStatusResult> {
    const base = baseOf(ctx)
    const v2 = ctx.profile.apiVersion === 'v2'
    const path = v2 ? `/v2/video_generation/${encodeURIComponent(upstreamId)}` : `/v1/query/video_generation?task_id=${encodeURIComponent(upstreamId)}`
    const res = await ctx.fetch(`${base}${path}`, { headers: { authorization: auth(ctx.credential) }, signal: ctx.signal })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)

    if (v2) {
      const status = strOf(dig(payload, 'task.status'))
      if (status === 'succeeded') {
        const url = strOf(dig(payload, 'task.content.url'))
        if (url === undefined) return { status: 'failed', error: 'MiniMax reported succeeded but carried no content.url' }
        return { status: 'succeeded', percent: 100, assets: [{ url, mime: 'video/mp4' }] }
      }
      if (status === 'failed') return { status: 'failed', error: strOf(dig(payload, 'task.error.message')) ?? 'MiniMax video generation failed' }
      if (status === 'cancelled' || status === 'canceled') return { status: 'canceled' }
      return { status: status === 'queued' ? 'queued' : 'running' }
    }

    const status = strOf(dig(payload, 'status')) ?? strOf(dig(payload, 'task.status'))
    if (status === 'Success') {
      /*
        ★★ V1 的关键一步:响应里那个 `file_id` **不是**可播放地址,
        要走 `/v1/files/retrieve` 换成 `file.download_url`。少了这一步,
        状态是成功、卡片上却永远没有视频。
      */
      const fileId = strOf(dig(payload, 'file_id'))
      if (fileId === undefined) return { status: 'failed', error: 'MiniMax returned Success but no file_id' }
      const fileRes = await ctx.fetch(`${base}/v1/files/retrieve?file_id=${encodeURIComponent(fileId)}`, {
        headers: { authorization: auth(ctx.credential) },
        signal: ctx.signal
      })
      if (!fileRes.ok) throw new Error(httpFailure(fileRes.status, await fileRes.text().catch(() => '')))
      const file = await readJson(fileRes)
      const url = strOf(dig(file, 'file.download_url'))
      if (url === undefined) return { status: 'failed', error: 'MiniMax file metadata carried no download_url' }
      return { status: 'succeeded', percent: 100, assets: [{ url, mime: 'video/mp4' }] }
    }
    if (status === 'Fail') return { status: 'failed', error: strOf(dig(payload, 'base_resp.status_msg')) ?? 'MiniMax video generation failed' }
    return { status: 'running' }
  }
}
