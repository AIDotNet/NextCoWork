/**
 * Google Veo —— 原生 `predictLongRunning`(不是 OpenAI 兼容层)。
 *
 * ★ 三处与"把它当 OpenAI 视频接口"不同的地方:
 *   - 鉴权头是 `x-goog-api-key`,不是 `Authorization: Bearer`;
 *   - 创建返回一个 **operation name**(长路径,Gemini 的 operation 标识),
 *     查询就按那个路径 GET;
 *   - 下载 URI **要带同一个 key** 才取得到 —— 匿名下载会 401。
 *
 * ★ 帧与视频按官方 REST 形状给:`instances[0].image.inlineData` /
 *   `instances[0].lastFrame.inlineData` / `instances[0].video.inlineData`。
 *   这几个字段名是 `GenerateVideosConfig` 的线形,写错会得到一句
 *   `Invalid JSON payload`,而它不会指出是哪个键。
 */
import { ProviderCredential } from '../../../../shared/domain/credential'
import type { VideoAdapter, VideoAdapterContext, VideoCreateResult, VideoRequest, VideoStatusResult, VideoRemoteAsset } from './contract'
import { assetOfRef, dig, httpFailure, readJson, recordOf, requireAsset, strOf } from './contract'

function baseUrl(ctx: VideoAdapterContext): string {
  const base = (ctx.provider.videoGeneration?.baseUrl ?? 'https://generativelanguage.googleapis.com').replace(/\/+$/u, '')
  const version = ctx.profile.apiVersion ?? 'v1beta'
  return base.endsWith(`/${version}`) ? base : `${base}/${version}`
}

const headers = (cred: ProviderCredential): Record<string, string> => {
  const key = cred.kind === 'api-key' ? cred.apiKey : cred.kind === 'oauth' ? cred.accessToken : cred.accessKeyId
  return { 'content-type': 'application/json', 'x-goog-api-key': key }
}

const inlineOf = (media: { mime: string; dataRef?: { kind: 'url'; url: string } | { kind: 'bytes'; bytes: Uint8Array } }): Record<string, unknown> | { url: string } | undefined => {
  const ref = media.dataRef
  if (ref === undefined) return undefined
  if (ref.kind === 'url') return { url: ref.url }
  return { inlineData: { mimeType: media.mime, data: Buffer.from(ref.bytes).toString('base64') } }
}

export const googleVeoAdapter: VideoAdapter = {
  id: 'google-veo',
  async create(ctx: VideoAdapterContext, request: VideoRequest): Promise<VideoCreateResult> {
    const instance: Record<string, unknown> = { prompt: request.prompt }
    const image = request.image
    if (image !== undefined) instance['image'] = inlineOf(image)
    if (request.lastFrame !== undefined) instance['lastFrame'] = inlineOf(request.lastFrame)
    /*
      ★ 延长走的是同一个端点、同一个 `video` 字段,而不是另一条路径 ——
      Google 把"续写"表达成"给一段视频作为输入"。
    */
    if (request.video !== undefined) instance['video'] = { url: request.video.url }

    const parameters: Record<string, unknown> = {}
    if (request.aspectRatio !== undefined) parameters['aspectRatio'] = request.aspectRatio
    if (request.resolution !== undefined) parameters['resolution'] = request.resolution
    if (request.action === 'extend') parameters['numberOfVideos'] = 1

    const res = await ctx.fetch(`${baseUrl(ctx)}/models/${encodeURIComponent(request.model)}:predictLongRunning`, {
      method: 'POST',
      headers: headers(ctx.credential),
      body: JSON.stringify({
        instances: [instance],
        ...(Object.keys(parameters).length === 0 ? {} : { parameters })
      }),
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    const name = strOf(dig(payload, 'name'))
    if (name === undefined) throw new Error('Veo: the create response carried no operation name')
    return { upstreamId: name, status: 'queued' }
  },

  async status(ctx: VideoAdapterContext, upstreamId: string): Promise<VideoStatusResult> {
    // ★ operation name 本身就是一条长路径,原样 GET(不拼 base 之外的东西)。
    const res = await ctx.fetch(`${baseUrl(ctx)}/${upstreamId}`, { headers: headers(ctx.credential), signal: ctx.signal })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const payload = await readJson(res)
    if (dig(payload, 'done') !== true) {
      const meta = recordOf(dig(payload, 'metadata'))
      return { status: 'running', ...(strOf(meta?.['state']) === undefined ? {} : { stage: String(meta?.['state']) }) }
    }
    if (dig(payload, 'error') !== undefined) {
      return { status: 'failed', error: strOf(dig(payload, 'error.message')) ?? 'Veo reported an error' }
    }
    const samples = dig(payload, 'response.generateVideoResponse.generatedSamples')
    const first = Array.isArray(samples) ? samples[0] : undefined
    const uri = strOf(dig(first, 'video.uri')) ?? strOf(dig(payload, 'response.generatedVideos.0.video.uri'))
    /*
      ★ 返回的 URI **要带 key** 才下得下来 —— 把鉴权头挂在这个 asset 上,
      下载那一跳会带上它(且只在那个主机)。**没有 URI 就是失败**,不能返回一个
      "成功但没有视频"的结果(那会让卡片永远停在"处理中")。
    */
    if (uri === undefined) {
      return { status: 'failed', error: 'Veo reported done but carried no video URI' }
    }
    const assets: VideoRemoteAsset[] = [{ ...assetOfRef(uri, 'video/mp4'), headers: headers(ctx.credential) }]
    requireAsset(assets, 'Veo')
    return { status: 'succeeded', assets, percent: 100 }
  }
}
