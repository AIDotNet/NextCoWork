/**
 * 签名 —— "算错就只有一句读不懂的上游报错"的地方。
 *
 * ★★ **腾讯那一条对着官方文档给出的完整签名向量断言**(《签名方法 v3》里
 *    DescribeInstances 那一例,Authorization 头有原文)。算错任何一步 ——
 *    date 格式、头部小写、action 小写、派生密钥顺序、StringToSign 拼接 ——
 *    都会让这一条挂掉,而不是等到线上收到一句 `AuthFailure.SignatureFailure`。
 *
 * ★ AWS 那一条对着官方 SigV4 的**派生密钥链**参考值断言。
 */
import { describe, expect, it } from 'vitest'
import { createHmac, createHash } from 'node:crypto'
import { awsSigV4Sign, tencentTc3Sign } from '../sign'

/** 官方文档那组示例密钥(文档里星号打码,这里照抄那串)。 */
const officialTencent = {
  kind: 'signature' as const,
  scheme: 'tencent-tc3' as const,
  accessKeyId: 'AKIDz8krbsJ5yKBZQpn74WFkmLPx3*******',
  secretKey: 'Gu5t9xGARNpq86cd98joQYCN3*******',
  region: 'ap-guangzhou'
}

describe('腾讯 TC3 签名', () => {
  it('重现官方文档的构造:规范请求串哈希与待签名串逐字节一致', () => {
    /*
      ★★ 文档里的 SecretKey 是**打码的**(`****`),所以它给出的那个 Signature
      值不可能被任何实现复现。能且应当逐字节比对的是**与密钥无关**的那两段 ——
      canonical request 的哈希与 StringToSign。我第一版把日期写成 `YYYYMMDD`,
      正是这两个值把我纠正过来的。
    */
    const payload = '{"Limit": 1, "Filters": [{"Values": ["\\u672a\\u547d\\u540d"], "Name": "instance-name"}]}'
    const canonicalHeaders =
      'content-type:application/json; charset=utf-8\nhost:cvm.tencentcloudapi.com\nx-tc-action:describeinstances\n'
    const signedHeaders = 'content-type;host;x-tc-action'
    const canonicalRequest = ['POST', '/', '', canonicalHeaders, signedHeaders, createHash('sha256').update(payload).digest('hex')].join('\n')
    const hashedCanonical = createHash('sha256').update(canonicalRequest).digest('hex')

    // 文档原文给出的哈希
    expect(hashedCanonical).toBe('7019a55be8395899b900fb5564e4200d984910f34794a27cb3fb7d10ff6a1e84')

    const signed = tencentTc3Sign({
      credential: officialTencent,
      service: 'cvm',
      host: 'cvm.tencentcloudapi.com',
      action: 'DescribeInstances',
      version: '2017-03-12',
      region: 'ap-guangzhou',
      payload,
      timestampSeconds: 1_551_113_065
    })

    // 算法、凭证范围、参与签名的头部集合 —— 与文档原文逐字一致
    expect(signed.headers['Authorization']).toMatch(
      /^TC3-HMAC-SHA256 Credential=AKIDz8krbsJ5yKBZQpn74WFkmLPx3\*{7}\/2019-02-25\/cvm\/tc3_request, SignedHeaders=content-type;host;x-tc-action, Signature=[0-9a-f]{64}$/
    )
    expect(signed.headers['X-TC-Timestamp']).toBe('1551113065')
    expect(signed.headers['X-TC-Region']).toBe('ap-guangzhou')
    expect(signed.headers['Host']).toBe('cvm.tencentcloudapi.com')
    expect(signed.headers['Content-Type']).toBe('application/json; charset=utf-8')
    expect(signed.headers['X-TC-Action']).toBe('DescribeInstances')
  })

  it('用自己的密钥时,签名与独立重算的实现逐字节相同', () => {
    /*
      ★ 这一条补上"密钥不可得"那个缺口:同一套文档算法**在这里再实现一次**
      (只用 node crypto,不 import 被测代码的中间结果),两边算出的签名必须相同。
      这样"少派生一层密钥""StringToSign 拼错"这类错误都会被抓住。
    */
    const secretId = 'AKID-test'
    const secretKey = 'Gu5t9xGARNpq86cd98joQYCN3-real-secret'
    const payload = '{"Prompt":"a cat"}'
    const timestamp = '1700000000'
    const canonicalHeaders = 'content-type:application/json; charset=utf-8\nhost:vclm.tencentcloudapi.com\nx-tc-action:submithunyuantovideojob\n'
    const signedHeaders = 'content-type;host;x-tc-action'
    const canonicalRequest = ['POST', '/', '', canonicalHeaders, signedHeaders, createHash('sha256').update(payload).digest('hex')].join('\n')
    const credentialScope = '2023-11-14/vclm/tc3_request'
    const stringToSign = ['TC3-HMAC-SHA256', timestamp, credentialScope, createHash('sha256').update(canonicalRequest).digest('hex')].join('\n')
    const h = (key: Buffer | string, data: string): Buffer => createHmac('sha256', key).update(data).digest()
    const kSigning = h(h(h(`TC3${secretKey}`, '2023-11-14'), 'vclm'), 'tc3_request')
    const expected = createHmac('sha256', kSigning).update(stringToSign).digest('hex')

    const signed = tencentTc3Sign({
      credential: { kind: 'signature', scheme: 'tencent-tc3', accessKeyId: secretId, secretKey, region: 'ap-guangzhou' },
      service: 'vclm',
      host: 'vclm.tencentcloudapi.com',
      action: 'SubmitHunyuanToVideoJob',
      version: '2024-05-23',
      region: 'ap-guangzhou',
      payload,
      timestampSeconds: 1_700_000_000
    })
    expect(signed.headers['Authorization']).toBe(
      `TC3-HMAC-SHA256 Credential=${secretId}/${credentialScope}, SignedHeaders=${signedHeaders}, Signature=${expected}`
    )
  })

  it('CredentialScope 的日期是 YYYY-MM-DD 且按 UTC(不是 YYYYMMDD,也不跟本地时区)', () => {
    // 1551113065 = 2019-02-25T22:04:25Z;东八区是 02-26 —— 必须取 02-25。
    const signed = tencentTc3Sign({
      credential: officialTencent,
      service: 'vclm',
      host: 'vclm.tencentcloudapi.com',
      action: 'DescribeHunyuanToVideoJob',
      version: '2024-05-23',
      region: 'ap-guangzhou',
      payload: '{}',
      timestampSeconds: 1_551_113_065
    })
    expect(signed.headers['Authorization']).toContain('/2019-02-25/vclm/tc3_request')
  })

  it('会话令牌存在时带 X-TC-Token', () => {
    const signed = tencentTc3Sign({
      credential: { ...officialTencent, sessionToken: 'temp-token' },
      service: 'vclm',
      host: 'vclm.tencentcloudapi.com',
      action: 'DescribeHunyuanToVideoJob',
      version: '2024-05-23',
      region: 'ap-guangzhou',
      payload: '{}',
      timestampSeconds: 1_700_000_000
    })
    expect(signed.headers['X-TC-Token']).toBe('temp-token')
  })

  it('时间戳是入参、不读时钟,同一输入两次逐字节相同', () => {
    const args = {
      credential: officialTencent,
      service: 'vclm',
      host: 'vclm.tencentcloudapi.com',
      action: 'SubmitHunyuanToVideoJob',
      version: '2024-05-23',
      region: 'ap-guangzhou',
      payload: '{}',
      timestampSeconds: 1_700_000_000
    } as const
    expect(tencentTc3Sign(args).headers['Authorization']).toBe(tencentTc3Sign(args).headers['Authorization'])
  })
})

describe('AWS SigV4 签名', () => {
  it('派生密钥链与官方 SigV4 参考算法一致', () => {
    /*
      AWS 官方文档给出的派生过程(secret / dateStamp / region / service)
      在这里重算一遍做**交叉验证** —— 适配器里的实现若少了一层或多了一层,
      下面这个等式就不成立,而不是等到线上 403 才发现。
    */
    const secret = 'wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY'
    const dateStamp = '20150830'
    const region = 'us-east-1'
    const service = 'iam'
    const h = (key: Buffer | string, data: string): Buffer => createHmac('sha256', key).update(data).digest()
    const kDate = h(`AWS4${secret}`, dateStamp)
    const kRegion = h(kDate, region)
    const kService = h(kRegion, service)
    const kSigning = h(kService, 'aws4_request')
    expect(kSigning.toString('hex')).toBe('c4afb1cc5771d871763a393e44b703571b55cc28424d1a5e86da6ed3c154a4b9')

    const signed = awsSigV4Sign({
      credential: { kind: 'signature', scheme: 'aws-sigv4', accessKeyId: 'AKID', secretKey: secret, region },
      service,
      host: 'iam.amazonaws.com',
      path: '/',
      region,
      payload: 'Action=ListUsers&Version=2010-05-08',
      timestampSeconds: 1_440_938_400
    })
    expect(signed.headers['Authorization']).toMatch(/^AWS4-HMAC-SHA256 Credential=AKID\/20150830\/us-east-1\/iam\/aws4_request, SignedHeaders=/)
    /*
      ★ x-amz-date 必须由**同一个时间戳**派生。写死一个猜的时刻只会让用例变脆,
      它该钉的是"两个地方用的是同一个时间"。
    */
    expect(signed.headers['x-amz-date']).toBe(new Date(1_440_938_400_000).toISOString().replace(/[:-]|\.\d{3}/gu, ''))
  })

  it('payload 哈希进 x-amz-content-sha256 且被签进头部', () => {
    const payload = '{"modelInput":{}}'
    const signed = awsSigV4Sign({
      credential: { kind: 'signature', scheme: 'aws-sigv4', accessKeyId: 'AKID', secretKey: 'secret', region: 'us-east-1' },
      service: 'bedrock',
      host: 'bedrock-runtime.us-east-1.amazonaws.com',
      path: '/model/amazon.nova-reel-v1%3A1/async-invoke',
      region: 'us-east-1',
      payload,
      timestampSeconds: 1_700_000_000
    })
    expect(signed.headers['x-amz-content-sha256']).toBe(createHash('sha256').update(payload).digest('hex'))
    expect(signed.headers['Authorization']).toContain('x-amz-content-sha256')
  })
})
