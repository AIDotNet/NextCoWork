/**
 * 适配器的请求形状 —— 14 家的 URL / 头 / body 逐条钉住。
 *
 * ★ 这批用例是"没有真实密钥时唯一能验证它们的方式"。每条都同时断言
 * **发送了什么** 与 **解析了什么**,因为这两件事的错法不同:
 * 前者是 400(能看见),后者是"状态成功、卡片空着"(看不见)。
 */
import { describe, expect, it, vi } from 'vitest'
import type { ProviderCredential } from '../../../../../shared/domain/credential'
import type { UpstreamProvider } from '../../../../../shared/domain/provider'
import { isCallableProfile, videoProfile } from '../../../../../shared/domain/video-profiles'
import { VIDEO_PROVIDER_PRESETS } from '../../../../../shared/domain/video-provider-presets'
import { videoAdapterFor } from '../index'
import type { VideoAdapterContext } from '../contract'
import { googleVeoAdapter } from '../google'
import { xaiVideoAdapter } from '../xai'
import { arkVideoAdapter } from '../ark'
import { dashscopeVideoAdapter } from '../dashscope'
import { minimaxVideoAdapter } from '../minimax'
import { bigmodelVideoAdapter } from '../bigmodel'
import { runwayVideoAdapter } from '../runway'
import { lumaAgentsAdapter, lumaLegacyAdapter } from '../luma'
import { falQueueAdapter } from '../fal'
import { replicateAdapter } from '../replicate'

const apiKey: ProviderCredential = { kind: 'api-key', apiKey: 'sk-test' }

function ctxFor(id: string, profileId: string, baseUrl: string, fetchImpl: typeof fetch): VideoAdapterContext {
  const provider: UpstreamProvider = {
    id: `video-${id}`,
    name: id,
    protocol: 'openai-chat',
    baseUrl,
    credentialRef: `provider:video-${id}`,
    priority: 50,
    enabled: true,
    videoGeneration: { adapter: videoProfile(profileId)!.adapter, baseUrl }
  }
  return {
    provider,
    profile: videoProfile(profileId)!,
    credential: apiKey,
    fetch: fetchImpl,
    signal: new AbortController().signal,
    requestTimeoutMs: 30_000
  }
}

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })

describe('Google Veo', () => {
  it('用 x-goog-api-key 打 predictLongRunning,并把 operation name 当任务 id', async () => {
    const fetchMock = vi.fn(async () => json({ name: 'models/veo/operations/abc' }))
    const ctx = ctxFor('google', 'google-veo-3.1', 'https://generativelanguage.googleapis.com', fetchMock as unknown as typeof fetch)
    const created = await googleVeoAdapter.create(ctx, { action: 'generate', prompt: 'a cat', model: 'veo-3.1-generate-preview', aspectRatio: '16:9' })
    expect(created.upstreamId).toBe('models/veo/operations/abc')

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toContain(':predictLongRunning')
    const headers = init.headers as Record<string, string>
    expect(headers['x-goog-api-key']).toBe('sk-test')
    // ★ 不是 Bearer —— Google 原生不认那个头
    expect(headers['authorization']).toBeUndefined()
    expect(JSON.parse(String(init.body))).toMatchObject({ instances: [{ prompt: 'a cat' }], parameters: { aspectRatio: '16:9' } })
  })

  it.each([
    ['https://relay.example/gemini', 'https://relay.example/gemini/v1beta'],
    ['https://relay.example/gemini/', 'https://relay.example/gemini/v1beta'],
    ['https://relay.example/gemini/v1beta/', 'https://relay.example/gemini/v1beta']
  ])('自定义地址 %s 同时用于创建和查询，不重复追加版本段', async (base, expected) => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) =>
      json(init?.method === 'POST' ? { name: 'models/my-veo/operations/job-1' } : { done: false })
    )
    const ctx = ctxFor('custom-google', 'google-veo-3.1', base, fetchMock as typeof fetch)
    await googleVeoAdapter.create(ctx, { action: 'generate', prompt: 'a cat', model: 'my-veo' })
    await googleVeoAdapter.status(ctx, 'models/my-veo/operations/job-1')
    expect(fetchMock.mock.calls.map(([url]) => String(url))).toEqual([
      `${expected}/models/my-veo:predictLongRunning`,
      `${expected}/models/my-veo/operations/job-1`
    ])
  })

  it('done 但没有 URI 时算失败,不算"成功但空"', async () => {
    const fetchMock = vi.fn(async () => json({ done: true, response: {} }))
    const ctx = ctxFor('google', 'google-veo-3.1', 'https://generativelanguage.googleapis.com', fetchMock as unknown as typeof fetch)
    const status = await googleVeoAdapter.status(ctx, 'models/veo/operations/abc')
    expect(status.status).toBe('failed')
  })

  it('下载 URI 带鉴权头(Google 的 URI 要 key 才取得下来)', async () => {
    const fetchMock = vi.fn(async () => json({ done: true, response: { generateVideoResponse: { generatedSamples: [{ video: { uri: 'https://generativelanguage.googleapis.com/v1/files/x:download' } }] } } }))
    const ctx = ctxFor('google', 'google-veo-3.1', 'https://generativelanguage.googleapis.com', fetchMock as unknown as typeof fetch)
    const status = await googleVeoAdapter.status(ctx, 'op')
    expect(status.status).toBe('succeeded')
    expect(status.assets?.[0]?.headers?.['x-goog-api-key']).toBe('sk-test')
  })
})

describe('API Key 视频接口 · 自定义服务地址', () => {
  const presets = VIDEO_PROVIDER_PRESETS.filter((preset) => preset.credential === 'api-key' && preset.models.some((entry) => {
    const profile = videoProfile(entry.profileId)
    return isCallableProfile(profile) && profile.adapter === preset.adapter
  }))
  const creates: Record<string, unknown> = {
    'google-veo': { name: 'models/relay/operations/job-1' },
    'xai-video': { request_id: 'job-1' },
    'ark-video': { id: 'job-1' },
    'dashscope-video': { output: { task_id: 'job-1', task_status: 'PENDING' } },
    'minimax-video': { task_id: 'job-1' },
    'bigmodel-video': { id: 'job-1', task_status: 'PROCESSING' },
    'runway-video': { id: 'job-1' },
    'luma-video': { id: 'job-1' },
    'luma-legacy-video': { id: 'job-1' },
    'fal-queue': { request_id: 'job-1' },
    'replicate-predictions': { id: 'job-1', status: 'starting' }
  }

  it.each(presets)('$name 创建与查询不回退到官方地址', async (preset) => {
    const entry = preset.models.find((item) => {
      const profile = videoProfile(item.profileId)
      return isCallableProfile(profile) && profile.adapter === preset.adapter
    })!
    const base = 'https://relay.example/proxy'
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => json(init?.method === 'POST'
      ? creates[preset.adapter]
      : { done: false, status: 'running', task_status: 'PROCESSING', task: { status: 'running' }, output: { task_status: 'RUNNING' } }
    ))
    const ctx = ctxFor('custom', entry.profileId, base, fetchMock as typeof fetch)
    const adapter = videoAdapterFor(preset.adapter)!
    const created = await adapter.create(ctx, { action: 'generate', prompt: 'a cat', model: entry.endpointId ?? entry.model })
    await adapter.status(ctx, created.upstreamId, created.route)
    expect(fetchMock.mock.calls.length).toBeGreaterThanOrEqual(2)
    expect(fetchMock.mock.calls.every(([url]) => String(url).startsWith(`${base}/`))).toBe(true)
  })
})

describe('xAI', () => {
  it('三个动作打三条路径,编辑不接受时长透传', async () => {
    const fetchMock = vi.fn(async () => json({ request_id: 'r1' }))
    const ctx = ctxFor('xai', 'xai-video-1.5', 'https://api.x.ai/v1', fetchMock as unknown as typeof fetch)
    await xaiVideoAdapter.create(ctx, { action: 'edit', prompt: 'teal', model: 'grok-imagine-video-1.5', video: { url: 'https://cdn.example/v.mp4' } })
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe('https://api.x.ai/v1/videos/edits')
    expect(JSON.parse(String(init.body))).not.toHaveProperty('duration')
    expect((init.headers as Record<string, string>)['authorization']).toBe('Bearer sk-test')
  })

  it('done 时从 video.url 取成品', async () => {
    const fetchMock = vi.fn(async () => json({ status: 'done', video: { url: 'https://vidgen.x.ai/x.mp4', duration: 8 } }))
    const ctx = ctxFor('xai', 'xai-video-1.5', 'https://api.x.ai/v1', fetchMock as unknown as typeof fetch)
    const status = await xaiVideoAdapter.status(ctx, 'r1')
    expect(status).toMatchObject({ status: 'succeeded', assets: [{ url: 'https://vidgen.x.ai/x.mp4' }] })
  })
})

describe('火山方舟', () => {
  it('content 数组用 role 表达首尾帧', async () => {
    const fetchMock = vi.fn(async () => json({ id: 't1' }))
    const ctx = ctxFor('ark', 'ark-seedance-2-5', 'https://ark.cn-beijing.volces.com/api/v3', fetchMock as unknown as typeof fetch)
    await arkVideoAdapter.create(ctx, {
      action: 'frames', prompt: 'move', model: 'doubao-seedance-2-5-260628',
      image: { mime: 'image/png', dataRef: { kind: 'url', url: 'https://a/first.png' } },
      lastFrame: { mime: 'image/png', dataRef: { kind: 'url', url: 'https://a/last.png' } }
    })
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toContain('/contents/generations/tasks')
    expect(JSON.parse(String(init.body))).toMatchObject({
      content: [
        { type: 'text', text: 'move' },
        { type: 'image_url', role: 'first_frame' },
        { type: 'image_url', role: 'last_frame' }
      ]
    })
  })

  it('编辑任务把 ratio 钉成 adaptive、duration 钉成 -1', async () => {
    const fetchMock = vi.fn(async () => json({ id: 't2' }))
    const ctx = ctxFor('ark', 'ark-seedance-2-5', 'https://ark.cn-beijing.volces.com/api/v3', fetchMock as unknown as typeof fetch)
    await arkVideoAdapter.create(ctx, { action: 'edit', prompt: 'warmer', model: 'doubao-seedance-2-5-260628', video: { url: 'https://a/v.mp4' }, duration: 9 })
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(JSON.parse(String(init.body))).toMatchObject({ ratio: 'adaptive', duration: -1, omni_reference_task_type: 'edit' })
  })

  it('取消是 DELETE 那条任务', async () => {
    const fetchMock = vi.fn(async () => new Response('{}', { status: 200 }))
    const ctx = ctxFor('ark', 'ark-seedance-2-5', 'https://ark.cn-beijing.volces.com/api/v3', fetchMock as unknown as typeof fetch)
    await arkVideoAdapter.cancel!(ctx, 't3')
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toContain('/tasks/t3')
    expect(init.method).toBe('DELETE')
  })
})

describe('百炼', () => {
  it('创建必须带 X-DashScope-Async,查询走 /api/v1/tasks/{id}', async () => {
    const fetchMock = vi.fn(async (...args: unknown[]) => {
      const url = String(args[0])
      return url.includes('/tasks/') ? json({ output: { task_status: 'SUCCEEDED', video_url: 'https://oss/x.mp4' } })
        : json({ output: { task_id: 'd1', task_status: 'PENDING' } })
    })
    const ctx = ctxFor('dashscope', 'dashscope-wan-2-7-t2v', 'https://dashscope.aliyuncs.com', fetchMock as unknown as typeof fetch)
    const created = await dashscopeVideoAdapter.create(ctx, { action: 'generate', prompt: 'x', model: 'wan2.7-t2v', resolution: '720P' })
    expect(created.upstreamId).toBe('d1')
    const [createUrl, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect((init.headers as Record<string, string>)['X-DashScope-Async']).toBe('enable')
    expect(createUrl).toContain('/api/v1/services/aigc/video-generation/video-synthesis')

    const status = await dashscopeVideoAdapter.status(ctx, 'd1')
    expect(status.assets?.[0]?.url).toBe('https://oss/x.mp4')
    expect(String((fetchMock.mock.calls[1] as unknown as [string] | undefined)?.[0])).toContain('/api/v1/tasks/d1')
  })
})

describe('MiniMax', () => {
  it('V2 直接给 content.url', async () => {
    const fetchMock = vi.fn(async () => json({ task: { status: 'succeeded', content: { url: 'https://cdn/x.mp4' } } }))
    const ctx = ctxFor('minimax', 'minimax-h3-v2', 'https://api.minimax.io', fetchMock as unknown as typeof fetch)
    const status = await minimaxVideoAdapter.status(ctx, '1')
    expect(status.assets?.[0]?.url).toBe('https://cdn/x.mp4')
    expect(String((fetchMock.mock.calls[0] as unknown as [string])[0])).toContain('/v2/video_generation/1')
  })

  it('V1 成功后还要按 file_id 换一次下载地址', async () => {
    const fetchMock = vi.fn(async (...args: unknown[]) => {
      const url = String(args[0])
      return url.includes('/files/retrieve')
        ? json({ file: { download_url: 'https://cdn/v1.mp4' } })
        : json({ status: 'Success', file_id: 'f9' })
    })
    const ctx = ctxFor('minimax', 'minimax-hailuo-v1', 'https://api.minimax.io', fetchMock as unknown as typeof fetch)
    const status = await minimaxVideoAdapter.status(ctx, 't9')
    expect(status.assets?.[0]?.url).toBe('https://cdn/v1.mp4')
    // ★ 少了第二跳的话,状态是成功、地址是空的 —— 这条用例专门盯它
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })
})

describe('智谱', () => {
  it('首尾帧用两张的 image_url 数组(CogVideoX-3 的形状)', async () => {
    const fetchMock = vi.fn(async () => json({ id: 'z1', task_status: 'PROCESSING' }))
    const ctx = ctxFor('bigmodel', 'bigmodel-cogvideox-3', 'https://open.bigmodel.cn/api', fetchMock as unknown as typeof fetch)
    await bigmodelVideoAdapter.create(ctx, {
      action: 'frames', prompt: 'x', model: 'cogvideox-3',
      image: { mime: 'image/png', dataRef: { kind: 'url', url: 'https://a/f.png' } },
      lastFrame: { mime: 'image/png', dataRef: { kind: 'url', url: 'https://a/l.png' } }
    })
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(JSON.parse(String(init.body))).toMatchObject({ image_url: ['https://a/f.png', 'https://a/l.png'] })
  })
})

describe('Runway', () => {
  it('每个请求都带版本头,THROTTLED 当排队而不是失败', async () => {
    const fetchMock = vi.fn(async () => json({ id: 't1' }))
    const ctx = ctxFor('runway', 'runway-gen4-5', 'https://api.dev.runwayml.com', fetchMock as unknown as typeof fetch)
    await runwayVideoAdapter.create(ctx, { action: 'generate', prompt: 'x', model: 'gen4.5', duration: 5 })
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect((init.headers as Record<string, string>)['X-Runway-Version']).toBe('2024-11-06')

    const throttled = vi.fn(async () => json({ status: 'THROTTLED' }))
    const ctx2 = ctxFor('runway', 'runway-gen4-5', 'https://api.dev.runwayml.com', throttled as unknown as typeof fetch)
    expect((await runwayVideoAdapter.status(ctx2, 't1')).status).toBe('queued')
  })
})

describe('Luma', () => {
  it('Agents API 的帧在 video.start_frame / end_frame 里', async () => {
    const fetchMock = vi.fn(async () => json({ id: 'g1' }))
    const ctx = ctxFor('luma', 'luma-ray-3-2', 'https://agents.lumalabs.ai/v1', fetchMock as unknown as typeof fetch)
    await lumaAgentsAdapter.create(ctx, {
      action: 'frames', prompt: 'x', model: 'ray-3.2',
      image: { mime: 'image/png', dataRef: { kind: 'url', url: 'https://a/f.png' } },
      lastFrame: { mime: 'image/png', dataRef: { kind: 'url', url: 'https://a/l.png' } },
      duration: '5s'
    })
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(JSON.parse(String(init.body))).toMatchObject({
      video: { start_frame: { url: 'https://a/f.png' }, end_frame: { url: 'https://a/l.png' }, duration: '5s' }
    })
  })

  it('旧 Dream Machine 的延长用 type: generation(只认自己生成过的 id)', async () => {
    const fetchMock = vi.fn(async () => json({ id: 'g2' }))
    const ctx = ctxFor('luma-legacy', 'luma-ray-2-legacy', 'https://api.lumalabs.ai/dream-machine/v1', fetchMock as unknown as typeof fetch)
    await lumaLegacyAdapter.create(ctx, { action: 'extend', prompt: 'more', model: 'ray-2', video: { url: 'gen-abc' } })
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(JSON.parse(String(init.body))).toMatchObject({ keyframes: { frame0: { type: 'generation', id: 'gen-abc' } } })
  })
})

describe('fal', () => {
  it('鉴权是 Key 而不是 Bearer,并记下 endpoint 作为 route', async () => {
    const fetchMock = vi.fn(async () => json({ request_id: 'rq1', status_url: 'https://queue.fal.run/x/requests/rq1/status' }))
    const ctx = ctxFor('fal', 'fal-queue-endpoint', 'https://queue.fal.run', fetchMock as unknown as typeof fetch)
    const created = await falQueueAdapter.create(ctx, { action: 'generate', prompt: 'x', model: 'fal-ai/veo3' })
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe('https://queue.fal.run/fal-ai/veo3')
    expect((init.headers as Record<string, string>)['authorization']).toBe('Key sk-test')
    // ★ route 必须存下来 —— 恢复查询靠它,不能拿别名现推
    expect(created.route).toBe('fal-ai/veo3')
  })

  it('查询用 route 里的 endpoint,而不是当前 profile', async () => {
    const fetchMock = vi.fn(async (...args: unknown[]) => {
      const url = String(args[0])
      return url.includes('/status') ? json({ status: 'IN_QUEUE', queue_position: 3 }) : json({ video: { url: 'https://v3.fal.media/x.mp4' } })
    })
    const ctx = ctxFor('fal', 'fal-queue-endpoint', 'https://queue.fal.run', fetchMock as unknown as typeof fetch)
    const status = await falQueueAdapter.status(ctx, 'rq1', 'fal-ai/kling-video/v2/master/text-to-video')
    expect(String((fetchMock.mock.calls[0] as unknown as [string])[0])).toBe('https://queue.fal.run/fal-ai/kling-video/v2/master/text-to-video/requests/rq1/status')
    expect(status).toMatchObject({ status: 'queued', stage: 'queue 3' })
  })

  it('COMPLETED 带 error 时算失败(不看状态名就宣布成功)', async () => {
    const fetchMock = vi.fn(async () => json({ status: 'COMPLETED', error: 'model overloaded', error_type: 'runner' }))
    const ctx = ctxFor('fal', 'fal-queue-endpoint', 'https://queue.fal.run', fetchMock as unknown as typeof fetch)
    expect((await falQueueAdapter.status(ctx, 'rq1', 'fal-ai/veo3')).status).toBe('failed')
  })

  it('取消是 PUT,202 才算接受', async () => {
    const ok = vi.fn(async () => json({ status: 'CANCELLATION_REQUESTED' }, 202))
    const ctx = ctxFor('fal', 'fal-queue-endpoint', 'https://queue.fal.run', ok as unknown as typeof fetch)
    await falQueueAdapter.cancel!(ctx, 'rq1', 'fal-ai/veo3')
    const [, init] = ok.mock.calls[0] as unknown as [string, RequestInit]
    expect(init.method).toBe('PUT')

    const completed = vi.fn(async () => json({ status: 'ALREADY_COMPLETED' }, 400))
    const ctx2 = ctxFor('fal', 'fal-queue-endpoint', 'https://queue.fal.run', completed as unknown as typeof fetch)
    await expect(falQueueAdapter.cancel!(ctx2, 'rq1', 'fal-ai/veo3')).rejects.toThrow(/already completed/)
  })
})

describe('Replicate', () => {
  it('从任意输出形状里捞出地址(string / array / object 三种)', async () => {
    const responses = [
      json({ status: 'succeeded', output: 'https://replicate.delivery/x.mp4' }),
      json({ status: 'succeeded', output: ['https://replicate.delivery/a.mp4'] }),
      json({ status: 'succeeded', output: { video: 'https://replicate.delivery/b.mp4' } })
    ]
    for (const response of responses) {
      const fetchMock = vi.fn(async () => response.clone())
      const ctx = ctxFor('replicate', 'replicate-predictions', 'https://api.replicate.com/v1', fetchMock as unknown as typeof fetch)
      const status = await replicateAdapter.status(ctx, 'p1')
      expect(status.status).toBe('succeeded')
      expect(status.assets?.[0]?.url).toContain('.mp4')
    }
  })
})

/**
 * 内联图片 vs 只收公网 URL —— 这条分叉的错法很隐蔽:会话图片**没有**公网地址,
 * 而各家对"能不能收字节"的规定不一样。
 *
 * ★ 错的方向只有两种,而两种都不可接受:
 *   - 该内联的发了 `ncw://`(上游不认识我们的私有协议)→ 一句读不懂的 400;
 *   - 只收 URL 的发了 data URL → 同样一句 400,但要等几分钟才从任务里报出来。
 */
describe('图片输入:内联 vs 只收 URL', () => {
  it('方舟收 base64(会话图片走这条)', async () => {
    const fetchMock = vi.fn(async () => json({ id: 't1' }))
    const ctx = ctxFor('ark', 'ark-seedance-2-5', 'https://ark.cn-beijing.volces.com/api/v3', fetchMock as unknown as typeof fetch)
    await arkVideoAdapter.create(ctx, {
      action: 'image', prompt: 'move', model: 'doubao-seedance-2-5-260628',
      image: { mime: 'image/png', dataRef: { kind: 'bytes', bytes: new Uint8Array([1, 2, 3]) } }
    })
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    const body = JSON.parse(String(init.body)) as { content: Array<{ image_url?: { url: string } }> }
    expect(body.content.some((item) => item.image_url?.url.startsWith('data:image/png;base64,'))).toBe(true)
  })

  it('★ xAI 只收公网 URL —— 给字节时**明确失败**,而不是发一个坏 body', async () => {
    const fetchMock = vi.fn(async () => json({ request_id: 'r1' }))
    const ctx = ctxFor('xai', 'xai-video-1.5', 'https://api.x.ai/v1', fetchMock as unknown as typeof fetch)
    await expect(xaiVideoAdapter.create(ctx, {
      action: 'image', prompt: 'move', model: 'grok-imagine-video-1.5',
      image: { mime: 'image/png', dataRef: { kind: 'bytes', bytes: new Uint8Array([1]) } }
    })).rejects.toThrow(/public http\(s\) URL/)
    // ★ 一个字都没发出去 —— 错误的请求不该先付费再说
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('★ Luma 旧版同样只收 CDN 地址(官方原话:这是目前唯一的传图方式)', async () => {
    const fetchMock = vi.fn(async () => json({ id: 'g1' }))
    const ctx = ctxFor('luma-legacy', 'luma-ray-2-legacy', 'https://api.lumalabs.ai/dream-machine/v1', fetchMock as unknown as typeof fetch)
    await expect(lumaLegacyAdapter.create(ctx, {
      action: 'image', prompt: 'x', model: 'ray-2',
      image: { mime: 'image/png', dataRef: { kind: 'bytes', bytes: new Uint8Array([1]) } }
    })).rejects.toThrow(/public http\(s\) URL/)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('公网 URL 的图片原样透传(不下载、不转码)', async () => {
    const fetchMock = vi.fn(async () => json({ request_id: 'r2' }))
    const ctx = ctxFor('xai', 'xai-video-1.5', 'https://api.x.ai/v1', fetchMock as unknown as typeof fetch)
    await xaiVideoAdapter.create(ctx, {
      action: 'image', prompt: 'x', model: 'grok-imagine-video-1.5',
      image: { mime: 'image/png', dataRef: { kind: 'url', url: 'https://cdn.example/a.png' } }
    })
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(JSON.parse(String(init.body))).toMatchObject({ image: { url: 'https://cdn.example/a.png' } })
  })
})
