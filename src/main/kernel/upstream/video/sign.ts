/**
 * 签名鉴权 —— 腾讯云 TC3 与 AWS SigV4。
 *
 * ★★ **手算签名,不引 SDK。** 两个理由:
 *   1. `@aws-sdk/*` 与 `tencentcloud-sdk-nodejs` 加起来是几十兆的依赖,而这里
 *      要签的只有**两个 Action 和一条 GET**;
 *   2. 引入 SDK 就等于把"密钥怎么进 HTTP 头"这件事交给第三方版本的实现,
 *      而我们**需要**它可测:签名是唯一一处"算错就是一个读不懂的 401"的地方,
 *      所以它必须能在 vitest 里拿官方向量钉住。
 *
 * ★ 用的是 Node 内置 `crypto`(内核的既有依赖面),`nodeHost()` 下跑得起来 ——
 *   签名是纯计算,不需要 electron。
 */
import { createHash, createHmac } from 'node:crypto'
import type { SignatureCredential } from '../../../../shared/domain/credential'

const sha256Hex = (data: string | Uint8Array): string =>
  createHash('sha256').update(data).digest('hex')

const hmac = (key: string | Uint8Array, data: string): Buffer =>
  createHmac('sha256', key).update(data).digest()

const hmacHex = (key: string | Uint8Array, data: string): string =>
  createHmac('sha256', key).update(data).digest('hex')

// ─────────────────────────────────────────────────────────────
// 腾讯云 TC3-HMAC-SHA256
// ─────────────────────────────────────────────────────────────

export interface TencentSignedRequest {
  headers: Record<string, string>
  body: string
}

/**
 * TC3 签名。
 *
 * ★ 时间戳**由调用方给**(`timestampSeconds`),不在函数里读时钟 —— 签名算法
 * 需要可复现,而单测必须能钉住一个固定时刻。生产调用点传 `Math.floor(now/1000)`。
 *
 * ★ `host` 与 `service` 分开传:AWS 那侧只有 host,腾讯这里
 * `vclm.tencentcloudapi.com` 的 service 段是 **`vclm`**,不能从 host 猜
 * (猜错换来一句 `AuthFailure.SignatureFailure`,而那个报错不会告诉你是 service 写错了)。
 */
export function tencentTc3Sign(input: {
  credential: SignatureCredential
  service: string
  host: string
  /** 只含 path,如 `/` */
  action: string
  version: string
  region: string
  payload: string
  timestampSeconds: number
}): TencentSignedRequest {
  const { credential, service, host, action, version, region, payload, timestampSeconds } = input
  const contentType = 'application/json; charset=utf-8'

  const date = new Date(timestampSeconds * 1000)
  /*
    ★★ **CredentialScope 里的日期是 `YYYY-MM-DD`,不是 `YYYYMMDD`。** 这是腾讯与
    AWS 最容易混淆的一处(AWS 是 `YYYYMMDD`),而写错的症状是一个
    `AuthFailure.SignatureFailure` —— 它不会说是日期格式错了。
    ★ 而且必须是 **UTC** 日期:用本地时区算,东八区在 00:00–08:00 之间会算成前一天,
    表现是"白天好好的、凌晨必然失败"(官方文档专门警告过这一条)。
  */
  const dateStamp = date.toISOString().slice(0, 10)
  const timestamp = String(timestampSeconds)

  // 1) CanonicalRequest。腾讯的头部集合是固定的这几个,按字典序。
  const canonicalHeaders =
    `content-type:${contentType}\n` +
    `host:${host}\n` +
    `x-tc-action:${action.toLowerCase()}\n`
  const signedHeaders = 'content-type;host;x-tc-action'
  const hashedPayload = sha256Hex(payload)
  const canonicalRequest = ['POST', '/', '', canonicalHeaders, signedHeaders, hashedPayload].join('\n')

  // 2) StringToSign
  const algorithm = 'TC3-HMAC-SHA256'
  const credentialScope = `${dateStamp}/${service}/tc3_request`
  const stringToSign = [algorithm, timestamp, credentialScope, sha256Hex(canonicalRequest)].join('\n')

  // 3) Signature(逐层派生密钥)
  const secretDate = hmac(`TC3${credential.secretKey}`, dateStamp)
  const secretService = hmac(secretDate, service)
  const secretSigning = hmac(secretService, 'tc3_request')
  const signature = hmacHex(secretSigning, stringToSign)

  return {
    body: payload,
    headers: {
      Authorization: `${algorithm} Credential=${credential.accessKeyId}/${credentialScope}, SignedHeaders=${signedHeaders}, Signature=${signature}`,
      'Content-Type': contentType,
      Host: host,
      'X-TC-Action': action,
      'X-TC-Version': version,
      'X-TC-Timestamp': timestamp,
      'X-TC-Region': region,
      ...(credential.sessionToken === undefined ? {} : { 'X-TC-Token': credential.sessionToken })
    }
  }
}

// ─────────────────────────────────────────────────────────────
// AWS SigV4
// ─────────────────────────────────────────────────────────────

export interface AwsSignedRequest {
  headers: Record<string, string>
  body: string
}

/**
 * AWS SigV4 签名(服务固定 `bedrock`,`POST` + JSON)。
 *
 * ★ 与腾讯那条的三处不同,都是协议决定的:
 *   - 头部集合含 `x-amz-content-sha256` 与 `x-amz-date`;
 *   - 末层密钥是 `kSigning = HMAC("AWS4"+secret, date/service/aws4_request)` 的**链接**;
 *   - 有会话令牌时还要签进头部(`x-amz-security-token`)。
 */
export function awsSigV4Sign(input: {
  credential: SignatureCredential
  service: string
  host: string
  /** 规范 URI。Bedrock 的 `/model/...` 这条里冒号要保留编码 */
  path: string
  region: string
  payload: string
  timestampSeconds: number
}): AwsSignedRequest {
  const { credential, service, host, path, region, payload, timestampSeconds } = input
  const contentType = 'application/json'

  const amzDate = new Date(timestampSeconds * 1000).toISOString().replace(/[:-]|\.\d{3}/gu, '')
  const dateStamp = amzDate.slice(0, 8)
  const hashedPayload = sha256Hex(payload)

  /*
    ★ `x-amz-content-sha256` 是 SDK 最容易漏、也最容易"本地能跑线上 403"的一个:
    Bedrock 强制校验它。头部按字典序,拼串时不能漏任何一个被签进去的头。
  */
  const canonicalHeaders =
    `content-type:${contentType}\n` +
    `host:${host}\n` +
    `x-amz-content-sha256:${hashedPayload}\n` +
    `x-amz-date:${amzDate}\n` +
    (credential.sessionToken === undefined ? '' : `x-amz-security-token:${credential.sessionToken}\n`)
  const signedHeaders =
    credential.sessionToken === undefined
      ? 'content-type;host;x-amz-content-sha256;x-amz-date'
      : 'content-type;host;x-amz-content-sha256;x-amz-date;x-amz-security-token'

  const canonicalRequest = ['POST', path, '', canonicalHeaders, signedHeaders, hashedPayload].join('\n')
  const credentialScope = `${dateStamp}/${region}/${service}/aws4_request`
  const stringToSign = ['AWS4-HMAC-SHA256', amzDate, credentialScope, sha256Hex(canonicalRequest)].join('\n')

  const kDate = hmac(`AWS4${credential.secretKey}`, dateStamp)
  const kRegion = hmac(kDate, region)
  const kService = hmac(kRegion, service)
  const kSigning = hmac(kService, 'aws4_request')
  const signature = hmacHex(kSigning, stringToSign)

  return {
    body: payload,
    headers: {
      Authorization: `AWS4-HMAC-SHA256 Credential=${credential.accessKeyId}/${credentialScope}, SignedHeaders=${signedHeaders}, Signature=${signature}`,
      'Content-Type': contentType,
      Host: host,
      'x-amz-content-sha256': hashedPayload,
      'x-amz-date': amzDate,
      ...(credential.sessionToken === undefined ? {} : { 'x-amz-security-token': credential.sessionToken })
    }
  }
}
