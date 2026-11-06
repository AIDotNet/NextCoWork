/**
 * 腾讯混元生视频(vclm)—— TC3 签名 + Action 形状。
 *
 * ★★ **字段形状本轮没有核对到正文**,所以对应的 profile 是 `actions: []` ——
 *    这个文件存在,但**不会被调用**。签名(`sign.ts`)与鉴权骨架是真写好的、
 *    有官方向量测试的;缺的只是 `SubmitHunyuanToVideoJob` 的请求体字段名。
 *
 * 核对之后要做的只有两件事:
 *   1. 把 `video-profiles.ts` 里 `tencent-vclm-hunyuan` 的 `actions` 填上;
 *   2. 按官方字段名补齐下面 `modelInputFor` 的实现。
 *
 * ★ 把这段留成**可执行的代码而不是注释**:它记录了"还差什么",
 *   而注释里的 TODO 不会出现在类型检查里。
 */
import type { ProviderCredential, SignatureCredential } from '../../../../shared/domain/credential'
import type { VideoAdapter, VideoAdapterContext, VideoCreateResult, VideoRequest, VideoStatusResult } from './contract'
import { tencentTc3Sign } from './sign'
import { httpFailure, readJson, recordOf, strOf } from './contract'

const SERVICE = 'vclm'
const VERSION = '2024-05-23'
const HOST = 'vclm.tencentcloudapi.com'

const asSignature = (cred: ProviderCredential): SignatureCredential => {
  if (cred.kind !== 'signature' || cred.scheme !== 'tencent-tc3') {
    throw new Error('Tencent vclm requires a TC3 signature credential (SecretId + SecretKey).')
  }
  return cred
}

/** 官方 Node SDK 的 Action 名,已核对。请求体字段**未核对**。 */
const SUBMIT_ACTION = 'SubmitHunyuanToVideoJob'
const QUERY_ACTION = 'DescribeHunyuanToVideoJob'

function callTencent(ctx: VideoAdapterContext, action: string, payload: Record<string, unknown>): Promise<Response> {
  const region = ctx.region ?? ctx.provider.videoGeneration?.region ?? 'ap-guangzhou'
  const body = JSON.stringify(payload)
  const signed = tencentTc3Sign({
    credential: asSignature(ctx.credential),
    service: SERVICE,
    host: HOST,
    action,
    version: VERSION,
    region,
    payload: body,
    timestampSeconds: Math.floor(Date.now() / 1000)
  })
  return ctx.fetch(`https://${HOST}`, { method: 'POST', headers: signed.headers, body: signed.body, signal: ctx.signal })
}

export const tencentVclmAdapter: VideoAdapter = {
  id: 'tencent-vclm',
  async create(ctx: VideoAdapterContext, request: VideoRequest): Promise<VideoCreateResult> {
    const res = await callTencent(ctx, SUBMIT_ACTION, modelInputFor(request))
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const response = recordOf(recordOf(await readJson(res))?.['Response'])
    const error = recordOf(response?.['Error'])
    if (error !== undefined) throw new Error(strOf(error['Message']) ?? 'Tencent vclm rejected the task')
    const id = strOf(response?.['JobId'])
    if (id === undefined) throw new Error('Tencent vclm: the submit response carried no JobId')
    return { upstreamId: id, status: 'queued' }
  },

  async status(ctx: VideoAdapterContext, upstreamId: string): Promise<VideoStatusResult> {
    const res = await callTencent(ctx, QUERY_ACTION, { JobId: upstreamId })
    if (!res.ok) throw new Error(httpFailure(res.status, await res.text().catch(() => '')))
    const response = recordOf(recordOf(await readJson(res))?.['Response'])
    const error = recordOf(response?.['Error'])
    if (error !== undefined) return { status: 'failed', error: strOf(error['Message']) ?? 'Tencent vclm query failed' }
    const status = strOf(response?.['Status'])
    if (status === 'DONE' || status === 'SUCCESS') {
      const url = strOf(recordOf(response?.['ResultVideo'])?.['VideoUrl']) ?? strOf(response?.['VideoUrl'])
      if (url === undefined) return { status: 'failed', error: 'Tencent vclm reported success but carried no video URL' }
      return { status: 'succeeded', percent: 100, assets: [{ url, mime: 'video/mp4' }] }
    }
    if (status === 'FAIL') return { status: 'failed', error: 'Tencent vclm reported FAIL' }
    return { status: status === 'WAIT' ? 'queued' : 'running' }
  }
}

/**
 * ★★ **未核对** —— 字段名按 SDK 的 Request 类型形状猜的,而"猜"在这里**不可接受**:
 * 腾讯会回一个 `InvalidParameter`,却不会说哪个字段错了。所以本函数**直接抛错**,
 * 由上层把它翻成"这家还没接通",而不是发一个注定被拒的付费请求。
 */
function modelInputFor(_request: VideoRequest): Record<string, unknown> {
  throw new Error(
    'Tencent Hunyuan video: the request field shape has not been verified against the official API reference yet, ' +
      'so this connection cannot submit tasks. The TC3 signing path is implemented and tested.'
  )
}
