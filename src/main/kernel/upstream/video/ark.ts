/**
 * 火山方舟 —— `/api/v3/contents/generations/tasks` + `/tasks/{id}`。
 *
 * ★ 一处与绝大多数家不同的设计:输入是**一个 content 数组**,而不是若干具名参数。
 *   文生、首尾帧、参考视频全都是"往数组里放不同 type/role 的元素"。
 *   ★`role` 就是首帧/尾帧的表达(`first_frame` / `last_frame`),而三个场景
 *   (首帧 / 首尾帧 / 全模态参考)**互斥**,不能混着放。
 * ★ Seedance 2.5 有 `omni_reference_task_type` 可以在提交时就限定子任务类型
 *   (`reference`/`edit`/`extend`),让**提交时**就校验而不是异步报错 ——
 *   但那是 2.5 才有的参数,旧版传了会报"参数不合法"。这里只对 2.5 传。
 * ★ 视频编辑:ratio 必须 adaptive、duration 必须 -1(保持原片时长)。
 */
import type { ProviderCredential } from '../../../../shared/domain/credential'
import type { VideoAdapter, VideoAdapterContext, VideoCreateResult, VideoRemoteMedia, VideoRequest, VideoStatusResult } from './contract'
import { assetOfRef, dig, httpFailure, mediaForBody, readJson, strOf } from './contract'

const headers = (cred: ProviderCredential): Record<string, string> => ({
  'content-type': 'application/json',
  authorization: `Bearer ${cred.kind === 'api-key' ? cred.apiKey : cred.kind === 'oauth' ? cred.accessToken : cred.accessKeyId}`
})

/*
  ★ 方舟的 `content[].image_url.url` 官方明确支持 **base64 data URL**
  ("将本地文件转换为 Base64 编码字符串后提交")。所以会话图片走内联 —— 这正是
  我们唯一能做的那条路(它们没有公网地址)。
*/
const urlOf = (m: VideoRemoteMedia): string | undefined =>
  mediaForBody(m, { inlineDataUrls: true, where: 'Ark' })

const is25 = (model: string): boolean => /seedance-2-5|2\.5/u.test(model)

function contentOf(request: VideoRequest): unknown[] {
  const content: unknown[] = [{ type: 'text', text: request.prompt }]
  if (request.image !== undefined) {
    const url = urlOf(request.image)
    if (url !== undefined) content.push({ type: 'image_url', image_url: { url }, role: 'first_frame' })
  }
  if (request.lastFrame !== undefined) {
    const url = urlOf(request.lastFrame)
    if (url !== undefined) content.push({ type: 'image_url', image_url: { url }, role: 'last_frame' })
  }
  if (request.video !== undefined) {
    content.push({ type: 'video_url', video_url: { url: request.video.url }, role: 'reference_video' })
  }
  return content
}

export const arkVideoAdapter: VideoAdapter = {
  id: 'ark-video',
  async create(ctx: VideoAdapterContext, request: VideoRequest): Promise<VideoCreateResult> {
    const base = ctx.provider.videoGeneration?.baseUrl ?? 'https://ark.cn-beijing.volces.com/api/v3'
    const body: Record<string, unknown> = { model: request.model, content: contentOf(request) }
    /*
      ★ 编辑/延长:ratio 与 duration 由协议钉死,不能透传 --
      透了会被上游拒不合法,而用户看到的是一句读不出原因的 400。
    */
    const editing = request.action === 'edit' || request.action === 'extend'
    if (editing) {
      body['ratio'] = 'adaptive'
      body['duration'] = -1
      if (is25(request.model)) body['omni_reference_task_type'] = request.action === 'edit' ? 'edit' : 'extend'
    } else {
      if (request.resolution !== undefined) body['resolution'] = request.resolution
      if (request.aspectRatio !== undefined) body['ratio'] = request.aspectRatio
      if (request.duration !== undefined) body['duration'] = Number(request.duration)
      if (request.seed !== undefined) body['seed'] = request.seed
    }
    const res = await ctx.fetch(`${base}/contents/generations/tasks`, {
      method: 'POST',
      headers: headers(ctx.credential),
      body: JSON.stringify(body),
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const id = strOf(dig(payload, 'id')) ?? strOf(dig(payload, 'task_id'))
    if (id === undefined) throw new Error('Ark: the create response carried no task id')
    return { upstreamId: id, status: 'queued' }
  },

  async status(ctx: VideoAdapterContext, upstreamId: string): Promise<VideoStatusResult> {
    const base = ctx.provider.videoGeneration?.baseUrl ?? 'https://ark.cn-beijing.volces.com/api/v3'
    const res = await ctx.fetch(`${base}/contents/generations/tasks/${encodeURIComponent(upstreamId)}`, { headers: headers(ctx.credential), signal: ctx.signal })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const status = strOf(dig(payload, 'status'))
    if (status === 'succeeded') {
      const url = strOf(dig(payload, 'content.video_url'))
      if (url === undefined) return { status: 'failed', error: 'Ark reported succeeded but carried no video_url' }
      return { status: 'succeeded', percent: 100, assets: [assetOfRef(url, 'video/mp4')] }
    }
    if (status === 'failed') {
      return { status: 'failed', error: strOf(dig(payload, 'error.message')) ?? strOf(dig(payload, 'error.code')) ?? 'Ark video generation failed' }
    }
    if (status === 'cancelled' || status === 'canceled') return { status: 'canceled' }
    return { status: status === 'queued' ? 'queued' : 'running' }
  },

  async cancel(ctx: VideoAdapterContext, upstreamId: string): Promise<void> {
    const base = ctx.provider.videoGeneration?.baseUrl ?? 'https://ark.cn-beijing.volces.com/api/v3'
    // ★ 方舟的取消是 DELETE 那条任务(见"取消或删除视频生成任务")。
    const res = await ctx.fetch(`${base}/contents/generations/tasks/${encodeURIComponent(upstreamId)}`, {
      method: 'DELETE',
      headers: headers(ctx.credential),
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
  }
}
