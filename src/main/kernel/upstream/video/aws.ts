/**
 * AWS Nova Reel —— Bedrock 异步调用 + **S3 输出**。
 *
 * ★★ 与其余 13 家有三处结构性不同,这也是它必须单写一条适配器的原因:
 *   1. 鉴权是 **SigV4 重算**(`sign.ts`),不是 Bearer;
 *   2. 调用走 **Bedrock 的 Action 形状**(`/model/{modelId}/async-invoke`),
 *      要 `X-Amz-*` 那一套头;
 *   3. **成品落在 S3** —— 查询接口回的是 `s3://bucket/key` 这样的 URI,
 *      而下载它同样要 SigV4 签名。所以这里把 asset 表示成 `s3://…`,
 *      由主进程的下载步骤用 `awsS3Get` 取字节(见 `video-download.ts`)。
 *
 * ★ 只有 720p、6 秒一档(最多 2 分钟),而且**只有美国东部(弗吉尼亚北部)**
 *   可用 —— 后者由 preset 的 region 表达,选错 region 会得到一个
 *   `ValidationException`,而它不会说"这个型号不在这片区域"。
 */
import type { ProviderCredential, SignatureCredential } from '../../../../shared/domain/credential'
import type { VideoAdapter, VideoAdapterContext, VideoCreateResult, VideoRequest, VideoStatusResult } from './contract'
import { awsSigV4Sign } from './sign'
import { httpFailure, readJson, recordOf, strOf } from './contract'

const asSignature = (cred: ProviderCredential): SignatureCredential => {
  if (cred.kind !== 'signature') {
    throw new Error('AWS Bedrock requires an AWS signature credential (AccessKeyId + SecretAccessKey).')
  }
  return cred
}

const hostOf = (region: string): string => `bedrock-runtime.${region}.amazonaws.com`

/** Bedrock 的 `startAsyncInvoke` 请求体。字段名按官方线形。 */
function modelInputFor(request: VideoRequest): Record<string, unknown> {
  const duration = typeof request.duration === 'number' ? request.duration : Number(request.duration ?? 6)
  const config: Record<string, unknown> = { durationSeconds: Number.isFinite(duration) ? duration : 6, fps: 24 }
  if (request.seed !== undefined) config['seed'] = request.seed
  const input: Record<string, unknown> = {
    taskType: 'TEXT_VIDEO',
    videoGenerationConfig: config
  }
  if (request.image?.dataRef?.kind === 'url') {
    // 图生视频:`textToVideoParams.images` 只收 S3 或 base64 —— URL 要我们这边先转。
    // ★ 这一条本轮**没有核到官方的图片输入形状**,所以 profile 里给它标了
    //   "图片输入待核对";真走到这里会明确失败,而不是发一个猜的 body。
    throw new Error('Amazon Nova Reel image input is not wired yet (its exact input shape was not verified).')
  }
  input['textToVideoParams'] = { text: request.prompt }
  return input
}

export const awsBedrockVideoAdapter: VideoAdapter = {
  id: 'aws-bedrock-video',
  async create(ctx: VideoAdapterContext, request: VideoRequest): Promise<VideoCreateResult> {
    const region = ctx.region ?? ctx.provider.videoGeneration?.region ?? 'us-east-1'
    const s3 = ctx.s3
    if (s3 === undefined || s3.bucket === '') {
      throw new Error('Amazon Nova Reel needs an output S3 bucket configured on the connection.')
    }
    const prefix = (s3.prefix ?? 'ncw-video').replace(/^\/+|\/+$/gu, '')
    const payload = JSON.stringify({
      modelInput: modelInputFor(request),
      outputDataConfig: { s3OutputDataConfig: { s3Uri: `s3://${s3.bucket}/${prefix}/` } }
    })
    const host = hostOf(region)
    const path = `/model/${encodeURIComponent(request.model)}/async-invoke`
    const signed = awsSigV4Sign({
      credential: asSignature(ctx.credential),
      service: 'bedrock',
      host,
      path,
      region,
      payload,
      timestampSeconds: Math.floor(Date.now() / 1000)
    })
    const res = await ctx.fetch(`https://${host}${path}`, {
      method: 'POST',
      headers: signed.headers,
      body: signed.body,
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const body = await readJson(res)
    const arn = strOf(recordOf(body)?.['invocationArn'])
    if (arn === undefined) throw new Error('Bedrock: the async-invoke response carried no invocationArn')
    return { upstreamId: arn, status: 'queued' }
  },

  async status(ctx: VideoAdapterContext, upstreamId: string): Promise<VideoStatusResult> {
    const region = ctx.region ?? ctx.provider.videoGeneration?.region ?? 'us-east-1'
    const host = hostOf(region)
    const path = `/async-invoke/${Buffer.from(upstreamId).toString('base64url')}`
    const signed = awsSigV4Sign({
      credential: asSignature(ctx.credential),
      service: 'bedrock',
      host,
      path,
      region,
      payload: '',
      timestampSeconds: Math.floor(Date.now() / 1000)
    })
    const res = await ctx.fetch(`https://${host}${path}`, {
      method: 'POST',
      headers: signed.headers,
      body: signed.body,
      signal: ctx.signal
    })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const body = recordOf(await readJson(res))
    const status = strOf(body?.['status'])
    if (status === 'Completed') {
      // ★ 成品是 S3 前缀;下游下载步骤用 SigV4 GET 取(见 `video-download.ts`)。
      const uri = strOf(recordOf(recordOf(body?.['outputDataConfig'])?.['s3OutputDataConfig'])?.['s3Uri'])
      if (uri === undefined) return { status: 'failed', error: 'Bedrock reported Completed but carried no s3Uri' }
      return { status: 'succeeded', percent: 100, assets: [{ url: uri, mime: 'video/mp4', size: undefined }] }
    }
    if (status === 'Failed') return { status: 'failed', error: strOf(body?.['failureMessage']) ?? 'Bedrock async invoke failed' }
    return { status: status === 'InProgress' ? 'running' : 'queued' }
  }
}
