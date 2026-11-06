/**
 * `generate_image` 的下发与回传。
 *
 * 两条主线:① 没配生图模型时**整体不下发**(`isEnabled`),下发了也调不动的
 * 承诺一个都不画;② 图必须真的回到 `output.images` —— 卡片、上下文回传
 * (`encode/*`)、`ToolDetail` 的预览读的都是这一个字段,丢了它就是
 * 「模型说画好了,界面什么都没有」。
 */
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it, vi } from 'vitest'
import type { AgentMessage } from '../../../../../shared/agent/message'
import type { ToolProgress } from '../../../../../shared/agent/tool'
import type { ImageGenBridge, ImageGenOptions } from '../../../image-gen'
import { nodeHost } from '../../../host'
import type { SessionImageStore } from '../../../session-images'
import type { ToolContext } from '../../registry'
import { generateImageTool } from '../image'

function bridge(over: Partial<ImageGenBridge> = {}): ImageGenBridge {
  return {
    available: () => true,
    generate: vi.fn(async () => ({
      images: [{ mime: 'image/png' as const, dataRef: 'data:image/png;base64,AAAA' }],
      model: 'gpt-image-2',
      providerName: 'OpenAI',
      requested: 1,
      failures: []
    })),
    edit: vi.fn(async () => ({
      images: [{ mime: 'image/png' as const, dataRef: 'data:image/png;base64,BBBB' }],
      model: 'gpt-image-2',
      providerName: 'OpenAI',
      requested: 1,
      failures: []
    })),
    ...over
  }
}

function ctx(overrides: Partial<ToolContext> = {}): ToolContext {
  return {
    workspaceRoot: '/tmp/does-not-matter',
    signal: new AbortController().signal,
    permissionMode: 'auto',
    depth: 0,
    callId: 'call_1',
    runId: 'run_1',
    host: nodeHost(),
    emit: () => {},
    ...overrides
  }
}

describe('generate_image · 元数据', () => {
  it('只读、非破坏、必须联网 —— 联网位是 permission-gate 第 1 行与开关的入参', () => {
    expect(generateImageTool.readOnly).toBe(true)
    expect(generateImageTool.destructive).toBe(false)
    expect(generateImageTool.needsNetwork).toBe(true)
  })
})

describe('generate_image · 下发', () => {
  it('没装桥(纯内核环境)时不下发', () => {
    expect(generateImageTool.isEnabled?.(ctx())).toBe(false)
  })

  it('桥说没有生图模型时不下发,有候选才下发', () => {
    expect(generateImageTool.isEnabled?.(ctx({ imageGen: bridge({ available: () => false }) }))).toBe(false)
    expect(generateImageTool.isEnabled?.(ctx({ imageGen: bridge() }))).toBe(true)
  })
})

describe('generate_image · 执行', () => {
  it('图回到 output.images,文案带上实际用的模型与供应商', async () => {
    const fake = bridge()
    const result = await generateImageTool.execute({ prompt: 'a lighthouse at dawn' }, ctx({ imageGen: fake }))
    expect(result.isError).toBe(false)
    expect(result.output.images).toEqual([{ mime: 'image/png', dataRef: 'data:image/png;base64,AAAA' }])
    expect(result.output.content).toContain('gpt-image-2')
    expect(result.output.content).toContain('OpenAI')
    expect(fake.generate).toHaveBeenCalledWith('a lighthouse at dawn', expect.any(AbortSignal), expect.objectContaining({ count: 1 }))
  })

  it('桥不在了(快照之后被摘)按工具失败上报,不能报成功', async () => {
    const result = await generateImageTool.execute({ prompt: 'x' }, ctx())
    expect(result.isError).toBe(true)
    expect(result.output.content).toMatch(/not available/u)
  })

  it('桥抛出的失败(没 key / 上游 4xx)原样进 tool_failed,消息带原因', async () => {
    const fake = bridge({ generate: vi.fn(async () => { throw new Error('OpenAI: no API key') }) })
    const result = await generateImageTool.execute({ prompt: 'x' }, ctx({ imageGen: fake }))
    expect(result.isError).toBe(true)
    expect(result.output.content).toContain('OpenAI: no API key')
  })

  it('空 prompt 被 schema 拦下 —— 模型只能带完整入参调用', async () => {
    const result = await generateImageTool.execute({ prompt: '' }, ctx({ imageGen: bridge() }))
    expect(result.isError).toBe(true)
    expect(result.output.content).toMatch(/Invalid arguments/u)
  })
})

describe('generate_image · 一次多张', () => {
  const img = (tag: string): { mime: 'image/png'; dataRef: string } => ({ mime: 'image/png', dataRef: `data:image/png;base64,${tag}` })

  it('n 透传给桥;每到手一张就 emit 一条带格子序号的进度,回执写实际张数', async () => {
    const emitted: ToolProgress[] = []
    const fake = bridge({
      generate: vi.fn(async (_prompt: string, _signal: AbortSignal, options?: ImageGenOptions) => {
        // 模拟并发完成顺序与格子序号无关:第 2 格先到
        options?.onImage?.(1, img('BBBB'))
        options?.onImage?.(0, img('AAAA'))
        options?.onImage?.(2, img('CCCC'))
        return { images: [img('AAAA'), img('BBBB'), img('CCCC')], model: 'gpt-image-2', providerName: 'OpenAI', requested: 3, failures: [] }
      })
    })
    const result = await generateImageTool.execute({ prompt: 'three cats', n: 3 }, ctx({ imageGen: fake, emit: (p) => emitted.push(p) }))
    expect(result.isError).toBe(false)
    expect(fake.generate).toHaveBeenCalledWith('three cats', expect.any(AbortSignal), expect.objectContaining({ count: 3 }))
    expect(emitted.map((p) => [p.image?.index, p.message])).toEqual([[1, '1/3'], [0, '2/3'], [2, '3/3']])
    expect(emitted.every((p) => p.callId === 'call_1')).toBe(true)
    expect(result.output.images).toHaveLength(3)
    expect(result.output.content).toMatch(/^Generated 3 images with gpt-image-2 via OpenAI\. The images are attached below\.$/u)
  })

  it('部分失败:保留已到手的图,回执写清「几张失败、为什么」—— 否则模型会宣布画齐了', async () => {
    const fake = bridge({
      generate: vi.fn(async () => ({
        images: [img('AAAA')], model: 'gpt-image-2', providerName: 'OpenAI', requested: 3,
        failures: ['HTTP 429 rate limited', 'HTTP 429 rate limited']
      }))
    })
    const result = await generateImageTool.execute({ prompt: 'x', n: 3 }, ctx({ imageGen: fake }))
    expect(result.isError).toBe(false)
    expect(result.output.images).toHaveLength(1)
    // 同因去重:一条限流报两遍没有信息量
    expect(result.output.content).toContain('2 of 3 requested images failed: HTTP 429 rate limited')
    expect(result.output.content).not.toContain('rate limited; HTTP 429')
  })

  it('n 超出上限被 schema 拦下,一个请求都不发', async () => {
    const fake = bridge()
    const result = await generateImageTool.execute({ prompt: 'x', n: 9 }, ctx({ imageGen: fake }))
    expect(result.isError).toBe(true)
    expect(fake.generate).not.toHaveBeenCalled()
  })
})

describe('generate_image · 改图', () => {
  /** 1×1 PNG */
  const PNG = Buffer.from(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
    'base64'
  )

  it('image:"latest" 取对话里**最近**的一张图交给桥,回执写 Edited', async () => {
    const fake = bridge()
    const messages: AgentMessage[] = [
      {
        id: 'm1', role: 'user', createdAt: 1, schemaVersion: 1,
        parts: [{ type: 'image', mime: 'image/png', dataRef: 'ncw://attachments/sessions/s1/older.png' }]
      },
      {
        id: 'm2', role: 'assistant', createdAt: 2, schemaVersion: 1,
        parts: [{
          type: 'tool_result', callId: 'c0', isError: false,
          output: { content: 'ok', images: [{ mime: 'image/png', dataRef: 'data:image/png;base64,AAAA' }] }
        }]
      }
    ]
    const result = await generateImageTool.execute(
      { prompt: 'make it noir', image: 'latest' },
      ctx({ imageGen: fake, messages })
    )
    expect(result.isError).toBe(false)
    expect(result.output.content).toMatch(/^Edited/u)
    // 最近的那张是 m2 里工具产出的图,不是 m1 的附件 —— 模型没法点名,只能由我们认最近的
    expect(fake.edit).toHaveBeenCalledWith(
      'make it noir',
      { mime: 'image/png', dataRef: 'data:image/png;base64,AAAA' },
      expect.any(AbortSignal),
      expect.objectContaining({ count: 1 })
    )
  })

  it('image:"latest" 但对话里一张图都没有 —— 当场失败并说清,一个请求都不发', async () => {
    const fake = bridge()
    const messages: AgentMessage[] = [
      { id: 'm1', role: 'user', createdAt: 1, schemaVersion: 1, parts: [{ type: 'text', text: 'hi' }] }
    ]
    const result = await generateImageTool.execute(
      { prompt: 'x', image: 'latest' },
      ctx({ imageGen: fake, messages })
    )
    expect(result.isError).toBe(true)
    expect(result.output.content).toMatch(/no image in this conversation/u)
    expect(fake.edit).not.toHaveBeenCalled()
  })

  it('image 给工作区路径 —— 读字节、按魔数认 mime 再交给桥', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'ncw-generate-image-'))
    const file = join(dir, 'photo.png')
    writeFileSync(file, PNG)
    try {
      const fake = bridge()
      const result = await generateImageTool.execute(
        { prompt: 'crop it', image: file },
        ctx({ imageGen: fake, workspaceRoot: dir })
      )
      expect(result.isError).toBe(false)
      expect(fake.edit).toHaveBeenCalledWith(
        'crop it',
        expect.objectContaining({ mime: 'image/png' }),
        expect.any(AbortSignal),
        expect.objectContaining({ count: 1 })
      )
      const source = vi.mocked(fake.edit).mock.calls[0]?.[1] as { dataRef: string }
      expect(source.dataRef.startsWith('data:image/png;base64,')).toBe(true)
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })

  it('路径指向不存在的文件 —— 失败信息指向文件,不进桥', async () => {
    const fake = bridge()
    const result = await generateImageTool.execute(
      { prompt: 'x', image: '/definitely/not/here.png' },
      ctx({ imageGen: fake })
    )
    expect(result.isError).toBe(true)
    expect(result.output.content).toMatch(/does not exist/u)
    expect(fake.edit).not.toHaveBeenCalled()
  })
})

describe('generate_image · 改图(URL 源)', () => {
  /** 1×1 PNG(与上面那组同款;作用域各自独立,别跨 describe 引用) */
  const PNG = Buffer.from(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
    'base64'
  )
  /** 用假 fetch 换掉 host.fetch —— URL 下载走 `downloadImage(ctx.host.fetch, …)` */
  function ctxWithFetch(fetchMock: ReturnType<typeof vi.fn>, fake: ImageGenBridge): ToolContext {
    return ctx({ imageGen: fake, host: { ...nodeHost(), fetch: fetchMock as unknown as typeof globalThis.fetch } })
  }

  it('http(s) URL 下载成功 → 以 data URL 交给桥走改图', async () => {
    const fetchMock = vi.fn(async (): Promise<Response> => new Response(new Uint8Array(PNG), { status: 200 }))
    const fake = bridge()
    const result = await generateImageTool.execute(
      { prompt: 'make it noir', image: 'https://cdn.example.com/pic.png' },
      ctxWithFetch(fetchMock, fake)
    )
    expect(result.isError).toBe(false)
    expect(result.output.content).toMatch(/^Edited/u)
    const source = vi.mocked(fake.edit).mock.calls[0]?.[1] as { mime: string; dataRef: string }
    expect(source.mime).toBe('image/png')
    expect(source.dataRef.startsWith('data:image/png;base64,')).toBe(true)
  })

  /*
    ★ 需求:URL 死了必须失败,**绝不回落成文生图** —— 模型传了 URL 就是要改这一张,
    默默画一张新图是答非所问,而且模型会向用户宣布「改好了」。
    下面三例都守 `generate` 与 `edit` 一个都不许被调用。
  */
  it('URL 404 —— 报错带原因,不生成新图', async () => {
    const fetchMock = vi.fn(async (): Promise<Response> => new Response(null, { status: 404 }))
    const fake = bridge()
    const result = await generateImageTool.execute(
      { prompt: 'x', image: 'https://cdn.example.com/gone.png' },
      ctxWithFetch(fetchMock, fake)
    )
    expect(result.isError).toBe(true)
    expect(result.output.content).toMatch(/HTTP 404/u)
    expect(fake.generate).not.toHaveBeenCalled()
    expect(fake.edit).not.toHaveBeenCalled()
  })

  it('URL 指向本机 —— 拒下,不生成新图', async () => {
    const fetchMock = vi.fn()
    const fake = bridge()
    const result = await generateImageTool.execute(
      { prompt: 'x', image: 'http://127.0.0.1:8080/steal.png' },
      ctxWithFetch(fetchMock, fake)
    )
    expect(result.isError).toBe(true)
    expect(result.output.content).toMatch(/Refused to download the image/u)
    expect(fetchMock).not.toHaveBeenCalled()
    expect(fake.edit).not.toHaveBeenCalled()
  })

  it('URL 下载回来不是图片 —— 报错,不把 HTML 当 png 送进桥', async () => {
    const fetchMock = vi.fn(async (): Promise<Response> => new Response(new Uint8Array([1, 2, 3]), { status: 200 }))
    const fake = bridge()
    const result = await generateImageTool.execute(
      { prompt: 'x', image: 'https://cdn.example.com/not-an-image' },
      ctxWithFetch(fetchMock, fake)
    )
    expect(result.isError).toBe(true)
    expect(result.output.content).toMatch(/not a recognized image/u)
    expect(fake.edit).not.toHaveBeenCalled()
  })
})

/*
  需求:生成图要能被点名(改图的 `image`、SaveImage 的 `url`)。地址必须写进回执正文 ——
  模型读不到 `output.images` 里的 dataRef。
*/
describe('generate_image · 会话图片地址', () => {
  function store(over: Partial<SessionImageStore> = {}): SessionImageStore {
    let n = 0
    return {
      save: vi.fn(async (image) => {
        n += 1
        return { mime: image.mime, dataRef: `ncw://attachments/sessions/s1/img${String(n)}.png` }
      }),
      read: vi.fn(async () => ({ mime: 'image/png' as const, bytes: new Uint8Array([1]) })),
      ...over
    }
  }

  it('每张图落成会话附件:output.images 存 ncw 地址,回执逐张列出', async () => {
    const images = store()
    const fake = bridge({
      generate: vi.fn(async () => ({
        images: [
          { mime: 'image/png' as const, dataRef: 'data:image/png;base64,AAAA' },
          { mime: 'image/png' as const, dataRef: 'data:image/png;base64,BBBB' }
        ],
        model: 'gpt-image-2', providerName: 'OpenAI', requested: 2, failures: []
      }))
    })
    const result = await generateImageTool.execute({ prompt: 'x', n: 2 }, ctx({ imageGen: fake, sessionImages: images }))
    expect(result.isError).toBe(false)
    expect(result.output.images).toEqual([
      { mime: 'image/png', dataRef: 'ncw://attachments/sessions/s1/img1.png' },
      { mime: 'image/png', dataRef: 'ncw://attachments/sessions/s1/img2.png' }
    ])
    expect(result.output.content).toContain('- ncw://attachments/sessions/s1/img1.png\n- ncw://attachments/sessions/s1/img2.png')
    expect(result.output.content).toContain('SaveImage')
  })

  it('存不下来的那张退回内联,调用仍成功,回执写明它没有地址和原因', async () => {
    const images = store({ save: vi.fn(async () => { throw new Error('disk full') }) })
    const result = await generateImageTool.execute({ prompt: 'x' }, ctx({ imageGen: bridge(), sessionImages: images }))
    expect(result.isError).toBe(false)
    expect(result.output.images).toEqual([{ mime: 'image/png', dataRef: 'data:image/png;base64,AAAA' }])
    expect(result.output.content).toContain('1 image(s) could not be stored and have no URL: disk full')
    expect(result.output.content).not.toContain('Image URLs')
  })

  it('image 传 ncw 地址:不读文件,原样交给桥的 edit 去做会话归属校验', async () => {
    const fake = bridge()
    const host = nodeHost()
    const read = vi.spyOn(host.fs, 'readFileBytes')
    const result = await generateImageTool.execute(
      { prompt: 'noir', image: 'ncw://attachments/sessions/s1/photo.jpg' },
      ctx({ imageGen: fake, host })
    )
    expect(result.isError).toBe(false)
    expect(fake.edit).toHaveBeenCalledWith(
      'noir',
      { mime: 'image/jpeg', dataRef: 'ncw://attachments/sessions/s1/photo.jpg' },
      expect.any(AbortSignal),
      expect.objectContaining({ count: 1 })
    )
    expect(read).not.toHaveBeenCalled()
  })
})
