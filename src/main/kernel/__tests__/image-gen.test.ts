/**
 * 对话内生图桥(`imageGenBridgeFor`)的单模型点名、回包解析与 URL 下载。
 *
 * 这一组守的坏全都**不报错**:设置里点名的模型没生效(悄悄按 priority 挑了
 * 别家)、回包里明明有图却没接住、跳转把图下载到本机 —— 表现都是「模型说画好了,
 * 用户什么也没看见」或「改了设置行为没变」,全程零报错。
 */
import { describe, expect, it, vi } from 'vitest'
import type { ProviderCredential } from '../../../shared/domain/credential'
import type { ModelAlias, UpstreamProvider } from '../../../shared/domain/provider'
import { downloadImage, imageGenBridgeFor, type ImageGenDeps } from '../image-gen'

/** 1×1 PNG */
const PNG_B64 = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=='

function provider(over: Partial<UpstreamProvider> = {}): UpstreamProvider {
  return {
    id: 'p1',
    name: 'OpenAI',
    protocol: 'openai-chat',
    baseUrl: 'https://api.openai.com/v1',
    credentialRef: 'provider:p1',
    priority: 60,
    enabled: true,
    ...over
  }
}

function alias(over: Partial<ModelAlias> = {}): ModelAlias {
  return {
    alias: 'gpt-image-2',
    providerId: 'p1',
    upstreamModel: 'gpt-image-2',
    priority: 0,
    capabilities: { tools: false, vision: false, thinking: false, caching: false, imageOutput: true },
    contextWindow: 200_000,
    maxOutputTokens: 8192,
    ...over
  }
}

const KEY: ProviderCredential = { kind: 'api-key', apiKey: 'sk-test' }

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
}

function bytesResponse(bytes: Buffer): Response {
  return new Response(new Uint8Array(bytes), { status: 200 })
}

function deps(over: Partial<ImageGenDeps> = {}): ImageGenDeps {
  return {
    providers: () => [provider()],
    aliases: () => [alias()],
    // 默认点名 fixture 里那条绑定;选路用例按需覆盖(见 `preferredModel` 的注释)
    preferredModel: () => ({ alias: 'gpt-image-2', providerId: 'p1' }),
    // 默认开着;开关用例按需覆盖(见 `enabled` 的注释)
    enabled: () => true,
    credential: async () => KEY,
    fetch: vi.fn(async () => json({ data: [{ b64_json: PNG_B64 }] })),
    // 默认透传:真解析(ncw:// 归属、围栏)住 upstream/images.ts,有它自己的测试
    resolveImage: async (source) => source,
    ...over
  }
}

describe('生图桥 · 「对话生图」开关', () => {
  /*
    需求:生图有自己的开关(AppSettings.imageGenerationEnabled),不再挂在「联网搜索」上。
    关掉 = 工具不下发;下发之后才关掉的窗口里,调用也必须一个请求都不发。
  */
  it('开关关掉时 available 为 false,即使模型选得好好的', () => {
    expect(imageGenBridgeFor(deps({ enabled: () => false })).available()).toBe(false)
    expect(imageGenBridgeFor(deps()).available()).toBe(true)
  })

  it('开关关掉时生成/改图当场拒,错误说的是开关而不是「去选模型」,且一个请求都不发', async () => {
    const fetchMock = vi.fn()
    const resolveImage = vi.fn(async (source: { mime: string; dataRef: string }) => source)
    const bridge = imageGenBridgeFor(deps({ enabled: () => false, fetch: fetchMock, resolveImage }))
    await expect(bridge.generate('a cat', new AbortController().signal))
      .rejects.toThrow(/Image generation is turned off/u)
    await expect(bridge.edit('x', { mime: 'image/png', dataRef: 'data:image/png;base64,AAAA' }, new AbortController().signal))
      .rejects.toThrow(/Image generation is turned off/u)
    expect(fetchMock).not.toHaveBeenCalled()
    // 连源图都不解析 —— 开关先于一切
    expect(resolveImage).not.toHaveBeenCalled()
  })
})

describe('生图桥 · 点名的模型', () => {
  it('没选模型(设置空着)时 available 为 false,generate 给一句指向设置页的英文指引', async () => {
    const bridge = imageGenBridgeFor(deps({ preferredModel: () => null }))
    expect(bridge.available()).toBe(false)
    await expect(bridge.generate('a cat', new AbortController().signal))
      .rejects.toThrow(/No image model is selected/u)
  })

  it('点名的绑定解析不到(被删/被停用)同样不下发 —— 用户动作是同一个:去图片页重选', async () => {
    const gone = imageGenBridgeFor(deps({ preferredModel: () => ({ alias: 'ghost', providerId: 'p1' }) }))
    expect(gone.available()).toBe(false)
    const off = imageGenBridgeFor(deps({ aliases: () => [alias({ enabled: false })] }))
    expect(off.available()).toBe(false)
    await expect(off.generate('x', new AbortController().signal))
      .rejects.toThrow(/No image model is selected/u)
  })

  it('点名的不是图片模型时不算数 —— 工具不下发,不发一个注定 404 的请求', () => {
    const textOnly = alias({ modality: 'text', capabilities: { tools: true, vision: false, thinking: false, caching: false } })
    expect(imageGenBridgeFor(deps({ aliases: () => [textOnly] })).available()).toBe(false)
  })

  it('★ 点名生效:只发点名那一家,失败**不**换别家 —— priority 更靠前的另一家一个请求都不发', async () => {
    const named = provider({ id: 'a', name: 'Alpha', baseUrl: 'https://a.example/v1', priority: 50 })
    const ahead = provider({ id: 'b', name: 'Beta', baseUrl: 'https://b.example/v1', priority: 10 })
    const fetchMock = vi.fn(async (_input: RequestInfo | URL): Promise<Response> => json({ error: 'bad key' }, 401))
    const bridge = imageGenBridgeFor(deps({
      providers: () => [ahead, named],
      aliases: () => [
        alias({ providerId: 'a', alias: 'a-model', upstreamModel: 'a-model' }),
        alias({ providerId: 'b', alias: 'b-model', upstreamModel: 'b-model' })
      ],
      preferredModel: () => ({ alias: 'a-model', providerId: 'a' }),
      fetch: fetchMock
    }))
    await expect(bridge.generate('x', new AbortController().signal))
      .rejects.toThrow(/Alpha: HTTP 401/u)
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(String(fetchMock.mock.calls[0]?.[0])).toBe('https://a.example/v1/images/generations')
  })

  it('没配密钥时记 no API key,而不是当成一次网络故障', async () => {
    const bridge = imageGenBridgeFor(deps({ credential: async () => null }))
    await expect(bridge.generate('x', new AbortController().signal))
      .rejects.toThrow(/no API key/u)
  })
})

describe('生图桥 · 回包解析', () => {
  it('b64_json 分支:解码 + 按魔数认 mime + 带 Bearer 头', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      expect((init?.headers as Record<string, string>).authorization).toBe('Bearer sk-test')
      return json({ data: [{ b64_json: PNG_B64 }] })
    })
    const bridge = imageGenBridgeFor(deps({ fetch: fetchMock }))
    const result = await bridge.generate('a cat', new AbortController().signal)
    expect(result.images).toHaveLength(1)
    expect(result.images[0]?.mime).toBe('image/png')
    expect(result.images[0]?.dataRef.startsWith('data:image/png;base64,')).toBe(true)
  })

  it('url 分支:下载并内联成 data URL', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL): Promise<Response> => {
      const url = String(input)
      if (url.includes('/images/generations')) return json({ data: [{ url: 'https://cdn.example.com/gen.png' }] })
      return bytesResponse(Buffer.from(PNG_B64, 'base64'))
    })
    const bridge = imageGenBridgeFor(deps({ fetch: fetchMock }))
    const result = await bridge.generate('a cat', new AbortController().signal)
    expect(String(fetchMock.mock.calls[1]?.[0])).toBe('https://cdn.example.com/gen.png')
    expect(result.images[0]?.mime).toBe('image/png')
  })

  it('上游回的图地址指向本机时拒下,并作为这一次的失败原因', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL): Promise<Response> => {
      const url = String(input)
      if (url.includes('/images/generations')) return json({ data: [{ url: 'http://127.0.0.1:8080/steal' }] })
      return bytesResponse(Buffer.from(PNG_B64, 'base64'))
    })
    const bridge = imageGenBridgeFor(deps({ fetch: fetchMock }))
    await expect(bridge.generate('x', new AbortController().signal))
      .rejects.toThrow(/Refused to download the image/u)
  })

  it('200 但回包里没有图 —— 记 response carried no image,不谎报成功', async () => {
    const bridge = imageGenBridgeFor(deps({ fetch: vi.fn(async () => json({ data: [] })) }))
    await expect(bridge.generate('x', new AbortController().signal))
      .rejects.toThrow(/response carried no image/u)
  })

  it('200 但字节不是图片 —— 失败,不再假定 png 报成功', async () => {
    const bridge = imageGenBridgeFor(deps({
      fetch: vi.fn(async () => json({ data: [{ b64_json: Buffer.from('<html>oops</html>').toString('base64') }] }))
    }))
    await expect(bridge.generate('x', new AbortController().signal))
      .rejects.toThrow(/not a recognized image/u)
  })
})

describe('生图桥 · 一次多张', () => {
  /*
    需求:多张 = 逐张并发发 n=1 的请求(不透传 n —— dall-e-3 等会拒 n>1),
    每张到手立刻回调;挂了几张时保留已到手的,全挂才整体失败。
  */
  it('count=3 发 3 个 n=1 请求,每张到手回调一次(带格子序号),结果按格子序号排列', async () => {
    const bodies: unknown[] = []
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      bodies.push(JSON.parse(String(init?.body)))
      return json({ data: [{ b64_json: PNG_B64 }] })
    })
    const seen: number[] = []
    const bridge = imageGenBridgeFor(deps({ fetch: fetchMock }))
    const result = await bridge.generate('cats', new AbortController().signal, { count: 3, onImage: (index) => seen.push(index) })
    expect(fetchMock).toHaveBeenCalledTimes(3)
    expect(bodies.every((b) => (b as { n?: unknown }).n === 1)).toBe(true)
    expect([...seen].sort()).toEqual([0, 1, 2])
    expect(result.images).toHaveLength(3)
    expect(result.requested).toBe(3)
    expect(result.failures).toEqual([])
  })

  it('count 超上限被钳到 MAX_IMAGE_COUNT —— 桥不信任调用方', async () => {
    const fetchMock = vi.fn(async () => json({ data: [{ b64_json: PNG_B64 }] }))
    const bridge = imageGenBridgeFor(deps({ fetch: fetchMock }))
    const result = await bridge.generate('x', new AbortController().signal, { count: 99 })
    expect(fetchMock).toHaveBeenCalledTimes(4)
    expect(result.requested).toBe(4)
  })

  it('部分失败:保留成功的那几张,失败原因进 failures,不整体抛错', async () => {
    let call = 0
    const fetchMock = vi.fn(async (): Promise<Response> => {
      call += 1
      return call === 2 ? json({ error: 'rate limited' }, 429) : json({ data: [{ b64_json: PNG_B64 }] })
    })
    const seen: number[] = []
    const bridge = imageGenBridgeFor(deps({ fetch: fetchMock }))
    const result = await bridge.generate('x', new AbortController().signal, { count: 3, onImage: (index) => seen.push(index) })
    expect(result.images).toHaveLength(2)
    expect(result.failures).toHaveLength(1)
    expect(result.failures[0]).toMatch(/HTTP 429/u)
    // 失败那一格不回调 —— 卡片上它保持占位,直到 tool_end 以最终结果为准
    expect(seen).toHaveLength(2)
  })

  it('全部失败才整体抛错,带供应商名 —— 和单张时同一句', async () => {
    const bridge = imageGenBridgeFor(deps({ fetch: vi.fn(async () => json({ error: 'bad key' }, 401)) }))
    await expect(bridge.generate('x', new AbortController().signal, { count: 2 }))
      .rejects.toThrow(/OpenAI: HTTP 401/u)
  })

  it('用户中断原样上抛,不被收成「某一格失败」', async () => {
    const controller = new AbortController()
    const fetchMock = vi.fn(async (): Promise<Response> => {
      controller.abort()
      throw new DOMException('The operation was aborted.', 'AbortError')
    })
    const bridge = imageGenBridgeFor(deps({ fetch: fetchMock }))
    await expect(bridge.generate('x', controller.signal, { count: 2 })).rejects.toThrow(/abort/iu)
  })
})

describe('downloadImage · URL 下载(工具入参与上游回包共用)', () => {
  it('重定向逐跳跟:跨域公网跳转允许,每跳重新过 ssrfRisk', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL): Promise<Response> => {
      const url = String(input)
      if (url.includes('/redirect')) {
        return new Response(null, { status: 302, headers: { location: 'https://cdn2.example.com/final.png' } })
      }
      return bytesResponse(Buffer.from(PNG_B64, 'base64'))
    })
    const image = await downloadImage(fetchMock as typeof fetch, 'https://cdn.example.com/redirect', new AbortController().signal)
    expect(String(fetchMock.mock.calls[1]?.[0])).toBe('https://cdn2.example.com/final.png')
    expect(image.mime).toBe('image/png')
  })

  it('跳转目标指向本机时拒下 —— redirect:manual 逐跳校验就是防这一手', async () => {
    const fetchMock = vi.fn(async (): Promise<Response> =>
      new Response(null, { status: 302, headers: { location: 'http://169.254.169.254/latest/meta-data/' } })
    )
    await expect(downloadImage(fetchMock as typeof fetch, 'https://cdn.example.com/redirect', new AbortController().signal))
      .rejects.toThrow(/Refused to download the image/u)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('循环重定向到上限就停,不挂到超时', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL): Promise<Response> =>
      new Response(null, { status: 302, headers: { location: String(input) } })
    )
    await expect(downloadImage(fetchMock as typeof fetch, 'https://cdn.example.com/loop', new AbortController().signal))
      .rejects.toThrow(/too many redirects/u)
  })

  it('超过 32MB 的图拒下(content-length 预检)', async () => {
    const fetchMock = vi.fn(async (): Promise<Response> =>
      new Response(new Uint8Array(0), { status: 200, headers: { 'content-length': String(64 * 1024 * 1024) } })
    )
    await expect(downloadImage(fetchMock as typeof fetch, 'https://cdn.example.com/huge.png', new AbortController().signal))
      .rejects.toThrow(/exceeds the 32 MB limit/u)
  })

  it('下载回来的字节不是图片时说清,而不是把 HTML 包成 png', async () => {
    const fetchMock = vi.fn(async (): Promise<Response> => bytesResponse(Buffer.from('<html>404-ish</html>')))
    await expect(downloadImage(fetchMock as typeof fetch, 'https://cdn.example.com/gone.png', new AbortController().signal))
      .rejects.toThrow(/not a recognized image/u)
  })

  it('非法 URL 当场拒,不发请求', async () => {
    const fetchMock = vi.fn()
    await expect(downloadImage(fetchMock as typeof fetch, 'not a url', new AbortController().signal))
      .rejects.toThrow(/Invalid image URL/u)
    expect(fetchMock).not.toHaveBeenCalled()
  })
})

describe('生图桥 · 改图', () => {
  const SOURCE = { mime: 'image/png', dataRef: `data:image/png;base64,${PNG_B64}` }

  it('源图在解析模型之前解析一次,解析失败一个请求都不发', async () => {
    const fetchMock = vi.fn()
    const bridge = imageGenBridgeFor(deps({
      fetch: fetchMock,
      resolveImage: async () => { throw new Error('Image attachment does not belong to this session') }
    }))
    await expect(bridge.edit('make it noir', SOURCE, new AbortController().signal))
      .rejects.toThrow(/does not belong to this session/u)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('multipart 形状被拒(415)时按序换形状,最后试 JSON —— xAI 的 edits 只收 JSON', async () => {
    const seen: string[] = []
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const body = init?.body
      if (body instanceof FormData) {
        seen.push(`form:${body.has('image[]') ? 'image[]' : 'image'}`)
        return json({ error: 'unsupported media type' }, 415)
      }
      seen.push('json')
      return json({ data: [{ b64_json: PNG_B64 }] })
    })
    const bridge = imageGenBridgeFor(deps({ fetch: fetchMock }))
    const result = await bridge.edit('watercolor', SOURCE, new AbortController().signal)
    expect(seen).toEqual(['form:image[]', 'form:image', 'json'])
    expect(result.images[0]?.mime).toBe('image/png')
  })

  it('供应商级故障(401)不换形状重试 —— 记一次原因就结束', async () => {
    const fetchMock = vi.fn(async () => json({ error: 'bad key' }, 401))
    const bridge = imageGenBridgeFor(deps({ fetch: fetchMock }))
    await expect(bridge.edit('x', SOURCE, new AbortController().signal))
      .rejects.toThrow(/HTTP 401/u)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('JSON 形状的请求体带 image.url,源图 data URL 原样送进去', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const body = init?.body
      if (typeof body === 'string') {
        const parsed = JSON.parse(body) as { image?: { url?: string } }
        expect(parsed.image?.url).toBe(SOURCE.dataRef)
        return json({ data: [{ b64_json: PNG_B64 }] })
      }
      return json({ error: 'only json' }, 415)
    })
    const bridge = imageGenBridgeFor(deps({ fetch: fetchMock }))
    await bridge.edit('x', SOURCE, new AbortController().signal)
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })
})
