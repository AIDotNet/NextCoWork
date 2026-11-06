/**
 * 后台任务管理器 —— 三条不变式各写一组反例。
 *
 * ★★ 这批用例的价值在于**它们模拟的是真事故**:
 *   - "重启之后又发了一次创建请求"(重复付费);
 *   - "下载失败被报成生成失败"(用户重试 → 再付一次);
 *   - "换了账户之后拿新 Key 去查旧任务"(查到别人的任务 / 404)。
 * 这三件事在真实使用里都不罕见,而每一种都不会在界面上留下明显线索。
 *
 * ★ adapter 与 fetch 全部用假的 —— 被测的是**状态机**,不是 HTTP。
 */
import { describe, expect, it, vi } from 'vitest'
import { VideoManager, type VideoJobStore } from '../manager'
import type { VideoJob } from '../../../shared/domain/video-generation'
import type { UpstreamProvider } from '../../../shared/domain/provider'
import type { ProviderCredential } from '../../../shared/domain/credential'

/* ── 一个最小的 provider × alias × profile 装配 ── */

const provider: UpstreamProvider = {
  id: 'video-google',
  name: 'Google',
  protocol: 'openai-chat',
  baseUrl: 'https://generativelanguage.googleapis.com',
  credentialRef: 'provider:video-google',
  priority: 50,
  enabled: true,
  videoGeneration: { adapter: 'google-veo', baseUrl: 'https://generativelanguage.googleapis.com' }
}

const binding = {
  alias: 'veo-3.1-generate-preview',
  providerId: 'video-google',
  upstreamModel: 'veo-3.1-generate-preview',
  capabilities: { tools: false, vision: false, thinking: false, caching: false, videoOutput: true },
  contextWindow: 0,
  maxOutputTokens: 0,
  modality: 'video' as const,
  video: { profileId: 'google-veo-3.1' }
}

function jobStore(): VideoJobStore & { rows: Map<string, VideoJob> } {
  const rows = new Map<string, VideoJob>()
  return {
    rows,
    get: (id) => rows.get(id),
    put: (job) => { rows.set(job.id, job) },
    remove: (id) => { rows.delete(id) },
    listRecoverable: () => [...rows.values()],
    listForSession: (sessionId) => [...rows.values()].filter((job) => job.sessionId === sessionId)
  }
}

const cred: ProviderCredential = { kind: 'api-key', apiKey: 'sk-1' }

/** 一个可控的"未来任务":调用方决定每次查询回什么。 */
function harness(options: {
  statuses: Array<{ status: 'running' | 'succeeded' | 'failed'; assets?: { url: string }[] }>
  retrieve?: (asset: { url: string }) => Promise<{ url: string; mime: string; size: number }>
  fetch?: typeof fetch
  provider?: UpstreamProvider
  binding?: typeof binding
  credential?: () => Promise<ProviderCredential | null>
}) {
  const activeProvider = options.provider ?? provider
  const activeBinding = options.binding ?? binding
  const store = jobStore()
  let clock = 1_000_000
  const timers: Array<{ fn: () => void; cancelled: boolean }> = []
  let index = 0
  /*
    ★ 类型标成 `ReturnType<typeof vi.fn>`:下面几处要用 `fetchMock.mock.calls`,
    而一个裸函数类型上没有 `mock`。
  */
  const fetchMock: ReturnType<typeof vi.fn> = options.fetch === undefined ? vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input)
    if (url.includes(':predictLongRunning')) {
      return new Response(JSON.stringify({ name: 'models/veo/operations/1' }), { status: 200 })
    }
    const step = options.statuses[Math.min(index, options.statuses.length - 1)]!
    index += 1
    if (step.status === 'succeeded') {
      return new Response(JSON.stringify({
        done: true,
        response: { generateVideoResponse: { generatedSamples: step.assets?.map((a) => ({ video: { uri: a.url } })) ?? [] } }
      }), { status: 200 })
    }
    if (step.status === 'failed') {
      return new Response(JSON.stringify({ done: true, error: { message: 'blocked by safety filter' } }), { status: 200 })
    }
    return new Response(JSON.stringify({ done: false }), { status: 200 })
  }) : (options.fetch as unknown as ReturnType<typeof vi.fn>)

  const manager = new VideoManager({
    store,
    providers: () => [activeProvider],
    aliases: () => [activeBinding],
    preferredModel: () => ({ alias: activeBinding.alias, providerId: activeProvider.id }),
    enabled: () => true,
    credential: options.credential ?? (async () => cred),
    fetch: fetchMock as unknown as typeof fetch,
    now: () => (clock += 1_000),
    schedule: (ms, fn) => {
      void ms
      const timer = { fn, cancelled: false }
      timers.push(timer)
      return { cancel: () => { timer.cancelled = true } }
    },
    /*
      ★ 注意签名是 `(job, asset)` —— 真正的实现要按 job 的会话来落盘,
      所以它拿得到任务本身。这里转发**第二个**参数给用例的 retrieve。
    */
    retrieve: async (_job, asset) => options.retrieve === undefined
      ? { url: `ncw://attachments/sessions/s1/${asset.url.split('/').pop() ?? 'x.mp4'}`, mime: 'video/mp4', size: 1024 }
      : options.retrieve(asset)
  })

  return { manager, store, fetchMock, clock: () => clock, timers, runTimers: () => { for (const t of timers.splice(0)) if (!t.cancelled) t.fn() } }
}

const draft = {
  sessionId: 's1',
  workspaceId: 'w1',
  configProfile: 'local',
  action: 'generate' as const,
  prompt: 'a cat surfing'
}

describe('提交', () => {
  it('创建成功之后把上游 id 记下来,并把任务排进轮询', async () => {
    const h = harness({ statuses: [{ status: 'running' }] })
    const result = await h.manager.submit(draft)
    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.job.cloud).toBe('queued')
      expect(result.job.upstreamId).toBe('models/veo/operations/1')
    }
    expect(h.timers.length).toBeGreaterThan(0)
  })

  it('没有选中模型时拒绝,且不发任何请求', async () => {
    const store = jobStore()
    const fetchMock = vi.fn()
    const manager = new VideoManager({
      store, providers: () => [provider], aliases: () => [binding],
      preferredModel: () => null, enabled: () => true,
      credential: async () => cred, fetch: fetchMock as unknown as typeof fetch,
      now: () => 1, schedule: () => ({ cancel: () => {} }),
      retrieve: async () => ({ url: 'ncw://x', mime: 'video/mp4', size: 1 })
    })
    const result = await manager.submit(draft)
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toContain('No video model is selected')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('开关关掉时拒绝**新建**(已提交的任务不受影响,见下一条)', async () => {
    const store = jobStore()
    const fetchMock = vi.fn()
    const manager = new VideoManager({
      store, providers: () => [provider], aliases: () => [binding],
      preferredModel: () => ({ alias: binding.alias, providerId: provider.id }),
      enabled: () => false,
      credential: async () => cred, fetch: fetchMock as unknown as typeof fetch,
      now: () => 1, schedule: () => ({ cancel: () => {} }),
      retrieve: async () => ({ url: 'ncw://x', mime: 'video/mp4', size: 1 })
    })
    const result = await manager.submit(draft)
    expect(result.ok).toBe(false)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('自定义供应商与自由模型 ID 经创建、查询、取回全程使用自定义路由', async () => {
    const custom: UpstreamProvider = {
      ...provider, id: 'video-custom-relay', name: 'Relay', credentialRef: 'provider:video-custom-relay',
      baseUrl: 'https://relay.example/chat/v1',
      videoGeneration: { adapter: 'google-veo', baseUrl: 'https://relay.example/gemini' }
    }
    const h = harness({
      provider: custom,
      binding: { ...binding, providerId: custom.id, alias: 'my-video', upstreamModel: 'my-private-veo' },
      statuses: [{ status: 'succeeded', assets: [{ url: 'https://relay.example/generated.mp4' }] }]
    })
    const submitted = await h.manager.submit(draft)
    expect(submitted.ok).toBe(true)
    if (!submitted.ok) return
    expect(submitted.providerName).toBe('Relay')
    expect(submitted.job).toMatchObject({ providerId: custom.id, model: 'my-private-veo', profileId: 'google-veo-3.1' })
    await h.manager.pollOnce(submitted.job.id)
    const calls = h.fetchMock.mock.calls as [string, RequestInit][]
    expect(calls[0]?.[0]).toBe('https://relay.example/gemini/v1beta/models/my-private-veo:predictLongRunning')
    expect(calls[1]?.[0]).toBe('https://relay.example/gemini/v1beta/models/veo/operations/1')
    expect(calls.every(([url]) => url.startsWith('https://relay.example/gemini/'))).toBe(true)
    expect(h.store.get(submitted.job.id)?.retrieval).toBe('ready')
  })

  it('接口与模型档案不匹配时在付费请求之前拒绝', async () => {
    const h = harness({
      provider: { ...provider, videoGeneration: { adapter: 'xai-video', baseUrl: 'https://relay.example/v1' } },
      statuses: [{ status: 'running' }]
    })
    const result = await h.manager.submit(draft)
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toContain('does not match')
    expect(h.fetchMock).not.toHaveBeenCalled()
    expect(h.store.rows.size).toBe(0)
  })

  it('参数不合法时本地就拒(不发付费请求)', async () => {
    const h = harness({ statuses: [{ status: 'running' }] })
    // Veo 的生成不接受不存在的比例
    const result = await h.manager.submit({ ...draft, aspectRatio: '7:3' })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toContain('aspect ratio')
    expect(h.fetchMock).not.toHaveBeenCalled()
  })
})

describe('自定义连接的任务快照', () => {
  it('提交后编辑地址、接口和嵌套输出配置不改变旧任务的查询路由', async () => {
    const connection = { adapter: 'google-veo' as const, baseUrl: 'https://relay.example/original', region: 'original-region', s3: { bucket: 'original-bucket', prefix: 'videos' } }
    const custom: UpstreamProvider = { ...provider, videoGeneration: connection }
    const h = harness({ provider: custom, statuses: [{ status: 'running' }] })
    const submitted = await h.manager.submit(draft)
    expect(submitted.ok).toBe(true)
    if (!submitted.ok) return
    expect(submitted.job.connection).toEqual(connection)
    expect(submitted.job.connection).not.toBe(connection)
    expect(submitted.job.connection?.s3).not.toBe(connection.s3)
    connection.s3.bucket = 'changed-bucket'
    custom.videoGeneration = { adapter: 'xai-video', baseUrl: 'https://other.example/new', region: 'new-region', s3: { bucket: 'new-bucket', prefix: 'other' } }
    await h.manager.pollOnce(submitted.job.id)
    expect(String(h.fetchMock.mock.calls[1]?.[0])).toBe('https://relay.example/original/v1beta/models/veo/operations/1')
    expect(h.store.get(submitted.job.id)?.connection?.s3?.bucket).toBe('original-bucket')
  })

  it('取消在途任务也使用提交时的连接，而不是编辑后的地址', async () => {
    const custom: UpstreamProvider = {
      ...provider,
      videoGeneration: { adapter: 'ark-video', baseUrl: 'https://relay.example/ark/api/v3' }
    }
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) =>
      new Response(JSON.stringify(init?.method === 'POST' ? { id: 'task-1' } : {}), { status: 200 })
    )
    const h = harness({ provider: custom, binding: { ...binding, video: { profileId: 'ark-seedance-2-5' } }, fetch: fetchMock as typeof fetch, statuses: [] })
    const submitted = await h.manager.submit(draft)
    expect(submitted.ok).toBe(true)
    if (!submitted.ok) return
    custom.videoGeneration = { adapter: 'xai-video', baseUrl: 'https://other.example/v1' }
    expect(await h.manager.cancel(submitted.job.id)).toEqual({ ok: true, state: 'requested' })
    expect(fetchMock.mock.calls[1]?.[0]).toBe('https://relay.example/ark/api/v3/contents/generations/tasks/task-1')
    expect(fetchMock.mock.calls[1]?.[1]?.method).toBe('DELETE')
  })

  it('更换密钥后不能拿新的凭据取消旧任务', async () => {
    let current: ProviderCredential = cred
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) =>
      new Response(JSON.stringify(init?.method === 'POST' ? { id: 'task-1' } : {}), { status: 200 })
    )
    const h = harness({
      provider: { ...provider, videoGeneration: { adapter: 'ark-video', baseUrl: 'https://relay.example/ark/api/v3' } },
      binding: { ...binding, video: { profileId: 'ark-seedance-2-5' } },
      credential: async () => current, fetch: fetchMock as typeof fetch, statuses: []
    })
    const submitted = await h.manager.submit(draft)
    expect(submitted.ok).toBe(true)
    if (!submitted.ok) return
    current = { kind: 'api-key', apiKey: 'sk-another-account' }
    const canceled = await h.manager.cancel(submitted.job.id)
    expect(canceled.ok).toBe(false)
    if (!canceled.ok) expect(canceled.reason).toContain('credential for this job changed')
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('旧任务未记录连接快照时仍兼容原配置', async () => {
    const h = harness({ statuses: [{ status: 'running' }] })
    const submitted = await h.manager.submit(draft)
    expect(submitted.ok).toBe(true)
    if (!submitted.ok) return
    h.store.put({ ...submitted.job, connection: undefined })
    await h.manager.pollOnce(submitted.job.id)
    expect(String(h.fetchMock.mock.calls[1]?.[0])).toBe('https://generativelanguage.googleapis.com/v1beta/models/veo/operations/1')
  })

  it('连接快照经过持久化和重启后仍用于查询，且不重新创建任务', async () => {
    const custom: UpstreamProvider = { ...provider, videoGeneration: { adapter: 'google-veo', baseUrl: 'https://relay.example/original' } }
    const h = harness({ provider: custom, statuses: [{ status: 'running' }] })
    const submitted = await h.manager.submit(draft)
    expect(submitted.ok).toBe(true)
    if (!submitted.ok) return
    h.manager.shutdown()
    const restoredStore = jobStore()
    restoredStore.put(JSON.parse(JSON.stringify(submitted.job)) as VideoJob)
    custom.videoGeneration = { adapter: 'xai-video', baseUrl: 'https://other.example/new' }
    const restarted = new VideoManager({
      store: restoredStore, providers: () => [custom], aliases: () => [binding],
      preferredModel: () => null, enabled: () => true, credential: async () => cred,
      fetch: h.fetchMock as unknown as typeof fetch, now: h.clock,
      schedule: () => ({ cancel: () => {} }),
      retrieve: async () => ({ url: 'ncw://attachments/sessions/s1/v.mp4', mime: 'video/mp4', size: 9 })
    })
    restarted.resume('local')
    await restarted.pollOnce(submitted.job.id)
    expect(String(h.fetchMock.mock.calls[1]?.[0])).toBe('https://relay.example/original/v1beta/models/veo/operations/1')
    expect(h.fetchMock.mock.calls.filter(([url]) => String(url).includes(':predictLongRunning'))).toHaveLength(1)
  })
})

describe('恢复(重启)', () => {
  it('★★ 恢复只查询,不重新提交 —— POST 计数一次都不增加', async () => {
    const h = harness({ statuses: [{ status: 'running' }, { status: 'succeeded', assets: [{ url: 'https://x/v.mp4' }] }] })
    await h.manager.submit(draft)
    h.manager.shutdown()

    const postsBefore = h.fetchMock.mock.calls.filter(([input]) => String(input).includes(':predictLongRunning')).length
    expect(postsBefore).toBe(1)

    // 模拟"重启":同一个 store,新建一个 manager
    const restarted = new VideoManager({
      store: h.store, providers: () => [provider], aliases: () => [binding],
      preferredModel: () => ({ alias: binding.alias, providerId: provider.id }),
      enabled: () => true, credential: async () => cred,
      fetch: h.fetchMock as unknown as typeof fetch,
      now: h.clock,
      schedule: (ms, fn) => { void ms; return { cancel: () => { void fn } } },
      retrieve: async () => ({ url: 'ncw://attachments/sessions/s1/v.mp4', mime: 'video/mp4', size: 9 })
    })
    const resumed = restarted.resume('local')
    expect(resumed).toBe(1)

    const postsAfter = h.fetchMock.mock.calls.filter(([input]) => String(input).includes(':predictLongRunning')).length
    // ★ 这一条就是整个恢复设计的判据
    expect(postsAfter).toBe(1)
  })

  it('★ 提交应答没拿到(unknown)时不自动推进 —— 绝不重复提交', async () => {
    const store = jobStore()
    store.put({
      id: 'vjob_x', configProfile: 'local', workspaceId: 'w1', sessionId: 's1',
      cloud: 'unknown', retrieval: 'waiting', cancel: 'none',
      providerId: provider.id, profileId: 'google-veo-3.1', model: binding.upstreamModel,
      assets: [], createdAt: 1, updatedAt: 1, revision: 1
    })
    const fetchMock = vi.fn()
    const manager = new VideoManager({
      store, providers: () => [provider], aliases: () => [binding],
      preferredModel: () => ({ alias: binding.alias, providerId: provider.id }),
      enabled: () => true, credential: async () => cred,
      fetch: fetchMock as unknown as typeof fetch, now: () => 1,
      schedule: () => ({ cancel: () => {} }),
      retrieve: async () => ({ url: 'ncw://x', mime: 'video/mp4', size: 1 })
    })
    manager.resume('local')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('★ 凭据换了(账户/Key 变了)的任务不续查,转 paused', async () => {
    const h = harness({ statuses: [{ status: 'running' }] })
    const submitted = await h.manager.submit(draft)
    expect(submitted.ok).toBe(true)
    h.manager.shutdown()

    const otherCred: ProviderCredential = { kind: 'api-key', apiKey: 'sk-OTHER' }
    const fetchMock = vi.fn()
    const manager = new VideoManager({
      store: h.store, providers: () => [provider], aliases: () => [binding],
      preferredModel: () => ({ alias: binding.alias, providerId: provider.id }),
      enabled: () => true, credential: async () => otherCred,
      fetch: fetchMock as unknown as typeof fetch, now: h.clock,
      schedule: () => ({ cancel: () => {} }),
      retrieve: async () => ({ url: 'ncw://x', mime: 'video/mp4', size: 1 })
    })
    manager.resume('local')
    // ★ 不能拿新 Key 去问旧任务
    expect(fetchMock).not.toHaveBeenCalled()
  })
})

describe('取回', () => {
  it('★★ 云端成功但下载失败 → retrieval=retryable_error,cloud 仍为 succeeded', async () => {
    const h = harness({
      statuses: [{ status: 'succeeded', assets: [{ url: 'https://x/v.mp4' }] }],
      retrieve: async () => { throw new Error('disk full') }
    })
    const result = await h.manager.submit(draft)
    expect(result.ok).toBe(true)
    await h.manager.pollOnce(result.ok ? result.job.id : '')

    const row = [...h.store.rows.values()][0]!
    // ★★ 两条轨道分开,用户看到的才会是"已生成、没取回来",而不是"生成失败"
    expect(row.cloud).toBe('succeeded')
    expect(row.retrieval).toBe('retryable_error')
    expect(row.error).toContain('disk full')
  })

  it('重试取回只查询/下载,不再提交', async () => {
    let attempts = 0
    const h = harness({
      statuses: [{ status: 'succeeded', assets: [{ url: 'https://x/v.mp4' }] }],
      retrieve: async (asset) => {
        attempts += 1
        if (attempts === 1) throw new Error('network')
        return { url: `ncw://attachments/sessions/s1/${asset.url.split('/').pop()!}`, mime: 'video/mp4', size: 5 }
      }
    })
    const submitted = await h.manager.submit(draft)
    const id = submitted.ok ? submitted.job.id : ''
    await h.manager.pollOnce(id)
    expect([...h.store.rows.values()][0]!.retrieval).toBe('retryable_error')

    const postsBefore = h.fetchMock.mock.calls.filter(([input]) => String(input).includes(':predictLongRunning')).length
    await h.manager.retryRetrieval(id)
    await h.manager.pollOnce(id)
    const row = [...h.store.rows.values()][0]!
    expect(row.retrieval).toBe('ready')
    expect(row.assets[0]?.url).toContain('ncw://attachments/sessions/s1/')
    const postsAfter = h.fetchMock.mock.calls.filter(([input]) => String(input).includes(':predictLongRunning')).length
    expect(postsAfter).toBe(postsBefore)
  })
})

describe('取消', () => {
  it('不支持取消的家如实说不支持,并提示可能仍在计费;任务继续收取', async () => {
    const h = harness({ statuses: [{ status: 'running' }] })
    const submitted = await h.manager.submit(draft)
    const id = submitted.ok ? submitted.job.id : ''
    // Veo 适配器没有 cancel
    const result = await h.manager.cancel(id)
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toContain('does not offer a cancel operation')
    const row = [...h.store.rows.values()][0]!
    expect(row.cancel).toBe('unsupported')
    // ★ 云端任务仍在跑 —— 不能因为我们点了取消就把它标成取消
    expect(row.cloud).not.toBe('canceled')
  })

  it('已经完成的任务不谎报取消', async () => {
    const h = harness({ statuses: [{ status: 'succeeded', assets: [{ url: 'https://x/v.mp4' }] }] })
    const submitted = await h.manager.submit(draft)
    const id = submitted.ok ? submitted.job.id : ''
    await h.manager.pollOnce(id)
    const result = await h.manager.cancel(id)
    expect(result).toEqual({ ok: true, state: 'already-done' })
  })
})
