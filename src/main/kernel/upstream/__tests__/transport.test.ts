/**
 * 传输装饰的边界测试。
 *
 * ★ 最重要的是「API Key 凭证 → 头是空对象、body 恒等」那两条 ——
 * 它们守的是**现有全部供应商零回归**。这一层是插在每一个请求的必经之路上的,
 * 装错了不是某一家坏,是全都坏。
 */
import { describe, expect, it } from 'vitest'
import type { OAuthCredential } from '../../../../shared/domain/credential'
import { CLIENT_PROVIDER_ID } from '../../../../shared/domain/presets'
import type { UpstreamProvider } from '../../../../shared/domain/provider'
import { isOpencodeGo, platformLoginAuth, sessionUuid, upstreamTransport } from '../transport'

const provider: UpstreamProvider = {
  id: 'p',
  name: 'P',
  protocol: 'openai-responses',
  baseUrl: 'https://chatgpt.com/backend-api/codex',
  credentialRef: 'provider:p',
  priority: 60,
  enabled: true
}

const oauth: OAuthCredential = {
  kind: 'oauth',
  issuer: 'chatgpt',
  accessToken: 'at-123',
  refreshToken: 'rt-456',
  expiresAt: 9_999_999_999_999,
  accountId: 'acct-789'
}

describe('upstreamTransport · API Key 零回归', () => {
  it('★ 头是空对象 —— 老供应商一个额外的头都不会多出来', () => {
    const t = upstreamTransport(provider, { kind: 'api-key', apiKey: 'sk-1' }, {})
    expect(t.headers).toEqual({})
  })

  it('★ body 是恒等,连对象引用都不换', () => {
    const t = upstreamTransport(provider, { kind: 'api-key', apiKey: 'sk-1' }, {})
    const body = { model: 'm', stream: true }
    expect(t.body(body)).toBe(body)
  })
})

/**
 * 平台那条上游存的是登录 JWT,而平台网关按头分流:`x-api-key` 查 API Key 表、
 * `Authorization: Bearer` 才验登录态。翻成 Anthropic 格式之后每次对话都是
 * 「API Key 无效或已停用」,而用户从没填过 key。
 */
describe('upstreamTransport · NextCoWork 平台登录态', () => {
  const platform: UpstreamProvider = { ...provider, id: CLIENT_PROVIDER_ID, protocol: 'anthropic' }

  it('登录 token 走 Authorization: Bearer', () => {
    const t = upstreamTransport(platform, { kind: 'api-key', apiKey: 'jwt-1' }, {})
    expect(t.headers.authorization).toBe('Bearer jwt-1')
  })

  it('★ x-api-key 是删掉而不是置空 —— 非空就会被判进 API Key 分支', () => {
    const t = upstreamTransport(platform, { kind: 'api-key', apiKey: 'jwt-1' }, {})
    expect(t.dropHeaders).toContain('x-api-key')
  })

  it('别的供应商不受影响', () => {
    expect(platformLoginAuth('p', 'sk-1')).toBeNull()
  })
})

describe('upstreamTransport · ChatGPT 订阅通道', () => {
  it('五个头齐全,UA 自报成 Codex CLI', () => {
    const t = upstreamTransport(provider, oauth, { sessionId: 's-1' })
    expect(t.headers['chatgpt-account-id']).toBe('acct-789')
    expect(t.headers['openai-beta']).toBe('responses=experimental')
    expect(t.headers['originator']).toBe('codex_cli_rs')
    expect(t.headers['session_id']).toBe(sessionUuid('s-1'))
    // 与 originator 同族;写在 transport 上才能压过 router 那行通用的 NextCoWork UA
    expect(t.headers['user-agent']).toMatch(/^codex_cli_rs\//u)
  })

  it('★ 用户 patch 把 store 改成 true,仍被按回 false', () => {
    // 从别处抄来的 requestAdapter patch 带个 store:true,会让这家整个不可用,
    // 而报错指向的是上游而不是那条 patch —— 所以硬约束压在最终线上边界
    const t = upstreamTransport(provider, oauth, {})
    expect(t.body({ model: 'm', store: true, stream: false })).toEqual({
      model: 'm',
      store: false,
      stream: true
    })
  })

  it('★ 摘掉 max_output_tokens 和 temperature —— 这条通道对它们直接 400', () => {
    // 实测:Unsupported parameter: max_output_tokens。encode 那边写进去对
    // api.openai.com 是对的,所以只能在这个凭证专属的边界上摘
    const t = upstreamTransport(provider, oauth, {})
    expect(t.body({ model: 'm', max_output_tokens: 8192, temperature: 0.7, input: [] })).toEqual({
      model: 'm',
      input: [],
      store: false,
      stream: true
    })
  })

  it('不动 body 里其他字段', () => {
    const t = upstreamTransport(provider, oauth, {})
    expect(t.body({ model: 'm', input: [{ role: 'user' }] })).toMatchObject({
      model: 'm',
      input: [{ role: 'user' }]
    })
  })
})

/**
 * OpenCode Go 要求每个对话带一个稳定的 `x-opencode-session`,缺了是一句
 * 「Request is missing x-opencode-session and cannot be routed efficiently」——
 * 措辞像性能建议,实则是硬拒绝。
 *
 * ★ 这一组里最重要的不是「命中」那几条,是**「不命中」那几条**:判宽了等于
 * 我们主动把会话标识发给一台不是 OpenCode 的机器。
 */
describe('upstreamTransport · OpenCode Go 会话头', () => {
  const key = { kind: 'api-key' as const, apiKey: 'sk-1' }
  const og = (over: Partial<UpstreamProvider> = {}): UpstreamProvider => ({
    ...provider,
    id: 'opencode-go',
    protocol: 'openai-chat',
    baseUrl: 'https://opencode.ai/zen/go/v1',
    ...over
  })

  it('预设 id 命中,值是 sessionId 折出来的那个 UUID', () => {
    expect(upstreamTransport(og(), key, { sessionId: 's-1' }).headers['x-opencode-session']).toBe(
      sessionUuid('s-1')
    )
  })

  it('★ 自己填 opencode.ai 地址的自定义供应商也命中 —— 只按 id 匹配会整个漏掉他', () => {
    const t = upstreamTransport(og({ id: 'custom-my-opencode' }), key, { sessionId: 's-1' })
    expect(t.headers['x-opencode-session']).toBe(sessionUuid('s-1'))
  })

  it('子域命中', () => {
    const p = og({ id: 'custom-x', baseUrl: 'https://gateway.opencode.ai/v1' })
    expect(upstreamTransport(p, key, { sessionId: 's-1' }).headers['x-opencode-session']).toBe(
      sessionUuid('s-1')
    )
  })

  /**
   * ★★ 这一条是整组的理由所在。`baseUrl.includes('opencode.ai')` 会让它通过,
   * 而通过的代价是把会话标识发给一台第三方主机 —— 且用户看不到任何异常,
   * 因为那台机器完全可以反代真上游、把回复原样送回来。
   */
  it.each([
    'https://opencode.ai.attacker.com/v1',
    'https://notopencode.ai/v1',
    'https://evil.example.com/?upstream=opencode.ai'
  ])('★★ 不命中 %s —— 子串匹配会把会话 id 发给第三方', (baseUrl) => {
    expect(upstreamTransport(og({ id: 'custom-x', baseUrl }), key, { sessionId: 's-1' }).headers)
      .toEqual({})
  })

  it('别家供应商一个头都不多', () => {
    expect(upstreamTransport(provider, key, { sessionId: 's-1' }).headers).toEqual({})
  })

  /**
   * ★ `new URL()` 对这些会抛。抛出去的话整条对话会崩在一个和网络、和凭证
   * 都无关的地方,而报错里一个字都不会提到「地址」。
   */
  it.each(['', '   ', 'not a url', '//opencode.ai/v1', 'opencode.ai/zen/go/v1', 'https://'])(
    '★ 畸形 baseUrl(%j)不抛异常,只是不命中',
    (baseUrl) => {
      const p = og({ id: 'custom-x', baseUrl })
      expect(() => upstreamTransport(p, key, { sessionId: 's-1' })).not.toThrow()
      expect(upstreamTransport(p, key, { sessionId: 's-1' }).headers).toEqual({})
    }
  )

  it('sessionId 缺失时仍然发头 —— 省略它等于退回那个 missing header 报错', () => {
    expect(upstreamTransport(og(), key, {}).headers['x-opencode-session']).toBe(
      sessionUuid(undefined)
    )
    expect(
      upstreamTransport(og(), key, { sessionId: '' }).headers['x-opencode-session']
    ).toBe(sessionUuid(undefined))
  })

  /**
   * ★★ 装饰是**叠加**不是替换。今天 OpenCode 只用 api-key,这两条钉的是
   * 「那家哪天上了 OAuth / 走到平台那条」时结构仍然对 —— 写成互斥分支不会报错,
   * 只会在那一天静默地把其中一件事改没。
   */
  it('★★ 与 OAuth 分支共存 —— 四个 OAuth 头和 body 改写都还在', () => {
    const p = og({ id: 'custom-x', protocol: 'openai-responses' })
    const t = upstreamTransport(p, oauth, { sessionId: 's-1' })
    expect(t.headers['chatgpt-account-id']).toBe('acct-789')
    expect(t.headers['openai-beta']).toBe('responses=experimental')
    expect(t.headers['originator']).toBe('codex_cli_rs')
    expect(t.headers['session_id']).toBe(sessionUuid('s-1'))
    expect(t.headers['x-opencode-session']).toBe(sessionUuid('s-1'))
    expect(t.body({ model: 'm', store: true, stream: false })).toEqual({
      model: 'm',
      store: false,
      stream: true
    })
  })

  it('★★ 与平台登录态分支共存 —— dropHeaders 不被装饰弄丢', () => {
    const p = og({ id: CLIENT_PROVIDER_ID, protocol: 'anthropic' })
    const t = upstreamTransport(p, { kind: 'api-key', apiKey: 'jwt-1' }, { sessionId: 's-1' })
    expect(t.headers.authorization).toBe('Bearer jwt-1')
    expect(t.headers['x-opencode-session']).toBe(sessionUuid('s-1'))
    expect(t.dropHeaders).toContain('x-api-key')
  })

  /**
   * ★★ `IDENTITY` 是模块级共享常量。往它身上就地写一个头,会让这个进程里
   * **此后每一个供应商**的每一个请求都带上它 —— 连同那个会话 id,发给别家上游。
   * 而零回归那两条断言会从此恒假,却没有任何一条用例会先跑到 OpenCode 那条路上去。
   */
  it('★★ 装饰不污染 IDENTITY —— 装过一次之后普通供应商的头仍是空对象', () => {
    upstreamTransport(og(), key, { sessionId: 's-1' })
    expect(upstreamTransport(provider, key, { sessionId: 's-1' }).headers).toEqual({})
    const body = { model: 'm', stream: true }
    expect(upstreamTransport(provider, key, {}).body(body)).toBe(body)
  })
})

describe('isOpencodeGo', () => {
  it('id 与主机名是「或」,任一命中即可', () => {
    const base = { ...provider, baseUrl: 'https://my-proxy.internal/v1' }
    expect(isOpencodeGo({ ...base, id: 'opencode-go' })).toBe(true)
    expect(isOpencodeGo({ ...base, id: 'custom-x' })).toBe(false)
    expect(
      isOpencodeGo({ ...base, id: 'custom-x', baseUrl: 'https://opencode.ai/zen/go/v1' })
    ).toBe(true)
  })

  it('主机名大小写不敏感', () => {
    const p = { ...provider, id: 'custom-x', baseUrl: 'https://OpenCode.AI/zen/go/v1' }
    expect(isOpencodeGo(p)).toBe(true)
  })
})

describe('sessionUuid', () => {
  it('★ 确定性 —— 同一个 sessionId 任何进程任何时刻算出同一个值', () => {
    expect(sessionUuid('01JABCDEF')).toBe(sessionUuid('01JABCDEF'))
  })

  it('不同 sessionId 不相等', () => {
    expect(sessionUuid('a')).not.toBe(sessionUuid('b'))
  })

  it('是合法的 v4 形状(version 4 + variant 10xx)', () => {
    expect(sessionUuid('01JABCDEF')).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u
    )
  })

  it('sessionId 缺失时用固定种子,而不是每次现摇一个', () => {
    expect(sessionUuid(undefined)).toBe(sessionUuid(''))
    expect(sessionUuid(undefined)).toMatch(/^[0-9a-f]{8}-/u)
  })
})
