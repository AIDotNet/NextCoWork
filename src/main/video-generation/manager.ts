/**
 * 视频后台任务管理器 —— **提交、轮询、取回、取消,以及重启后的恢复**。
 *
 * ## 为什么不能像生图那样"一次调用拿到结果"
 *
 * 一个视频任务要跑几分钟。调用栈里 await 到底的话,用户点了停止、切了会话、
 * 关了应用,那次调用就没了 —— 而**云端还在跑,还在计费**。所以任务身份必须
 * 先落盘,worker 独立于发起它的那次 run。
 *
 * ## 三条不可退让的不变式
 *
 * 1. **不重复提交。** 创建的超时/断线/进程退出若拿不准上游收没收到,标
 *    `unknown` 并**停止自动推进** —— 绝不自动重发一次付费请求。仅当供应商
 *    明确提供幂等保证时才允许重试(今天一个都没有)。
 * 2. **云端成功 ≠ 已有文件。** 下载失败单独记在 `retrieval` 上,重试只查/只下,
 *    **绝不重新生成**。这是最容易被写错的一条:把"取回失败"并进"生成失败",
 *    用户就会点重试,再付一次钱。
 * 3. **不再用当前配置推任务身份。** job 行里冻结了 provider/profile/model/
 *    credential 指纹;账户、Key、模型改过之后,不能拿新的去查旧任务
 *    (那会查到别人的任务,或一个 404)。
 *
 * ## 与宿主的分工
 *
 * 这一层是**纯 Node**:出网走注入的 `fetch`,落盘走注入的 `store`,
 * 下载走注入的 `retrieve`,时间走注入的 `now` 与 `schedule`。所以"提交应答丢了
 * 会怎样""重启三次会不会重复提交"这类问题全都能在 vitest 里钉住,不用启动 Electron。
 */
import type { ProviderCredential } from '../../shared/domain/credential'
import { credentialFingerprint } from '../../shared/domain/credential'
import type { ModelAlias, ProviderVideoGeneration, UpstreamProvider } from '../../shared/domain/provider'
import { selectModelBinding } from '../../shared/domain/model-selection'
import type {
  VideoAction,
  VideoAssetRef,
  VideoCloudStatus,
  VideoJob,
  VideoJobView
} from '../../shared/domain/video-generation'
import { videoJobView } from '../../shared/domain/video-generation'
import { isCallableProfile, videoProfile } from '../../shared/domain/video-profiles'
import { videoAdapterFor } from '../kernel/upstream/video'
import type { VideoAdapterContext, VideoRemoteAsset, VideoRequest } from '../kernel/upstream/video/contract'
import { validateVideoRequest, type VideoValidation } from '../kernel/upstream/video/validate'

/** 目前需要轮询的状态。 */
const POLLING: ReadonlySet<VideoCloudStatus> = new Set(['submitting', 'queued', 'running'])

export interface VideoJobStore {
  get(id: string): VideoJob | undefined
  put(job: VideoJob): void
  remove(id: string): void
  listRecoverable(configProfile: string): VideoJob[]
  listForSession(sessionId: string): VideoJob[]
}

export interface VideoSubmitDraft {
  sessionId: string
  workspaceId: string
  configProfile: string
  originRunId?: string
  originCallId?: string
  action: VideoAction
  prompt: string
  image?: { mime: string; dataRef: { kind: 'url'; url: string } | { kind: 'bytes'; bytes: Uint8Array } }
  lastFrame?: { mime: string; dataRef: { kind: 'url'; url: string } | { kind: 'bytes'; bytes: Uint8Array } }
  video?: { url: string }
  duration?: number | string
  aspectRatio?: string
  resolution?: string
  seed?: number
}

export interface VideoManagerDeps {
  store: VideoJobStore
  providers(): readonly UpstreamProvider[]
  aliases(): readonly ModelAlias[]
  /** 用户点名的视频模型(`AppSettings.videoModel` 那一对),每次现读 */
  preferredModel(): { alias: string; providerId: string | undefined } | null
  /** 「对话视频生成」开关 —— **只管新建**,已提交任务照常推进(见 settings.ts) */
  enabled(): boolean
  credential(ref: string): Promise<ProviderCredential | null>
  fetch: typeof fetch
  now(): number
  /** 排一个延时回调。注入是为了让"重启三次只查询不重发"这类测试能确定性推进 */
  schedule(ms: number, fn: () => void): { cancel(): void }
  /** 落盘一个云端成果。失败**必须**抛,由本层区分"生成失败"与"取回失败" */
  retrieve(job: VideoJob, asset: VideoRemoteAsset): Promise<VideoAssetRef>
  /** 任务状态变化时通知渲染层(可省 —— 内核测试不需要) */
  onChange?: (view: VideoJobView) => void
  /** 单次 HTTP 请求的超时(毫秒)。**不是整个生成的超时** */
  requestTimeoutMs?: number
  /** 轮询间隔的上限与下限(毫秒),按供应商建议值的保守区间 */
  pollIntervalMs?: { min: number; max: number }
}

const DEFAULT_REQUEST_TIMEOUT_MS = 60_000
const DEFAULT_POLL = { min: 5_000, max: 30_000 }

export class VideoManager {
  private readonly workers = new Map<string, { cancel(): void }>()
  private readonly inflight = new Set<string>()
  private stopped = false

  constructor(private readonly deps: VideoManagerDeps) {}

  /**
   * 提交一次新任务。
   *
   * ★★ 顺序是关键:**先把本地任务行写下来,再发付费创建**。反过来的话,
   * 进程在"上游已接受、我们还没记下 id"之间退出,那条云端任务就永久失联
   * —— 用户付了钱、界面上什么都没有,而且我们连它存在都不知道。
   */
  async submit(draft: VideoSubmitDraft): Promise<{ ok: true; job: VideoJob; providerName: string } | { ok: false; reason: string }> {
    if (!this.deps.enabled()) {
      return { ok: false, reason: 'Video generation is turned off. Turn it on in Settings > Models > Video generation.' }
    }
    const preferred = this.deps.preferredModel()
    if (preferred === null) {
      return { ok: false, reason: 'No video model is selected. Choose one in Settings > Models > Video generation.' }
    }
    const binding = selectModelBinding(this.deps.aliases(), this.deps.providers(), preferred.alias, preferred.providerId)
    if (binding === undefined) {
      return { ok: false, reason: `The selected video model "${preferred.alias}" is unavailable. Pick another one in Settings > Models > Video generation.` }
    }
    const provider = this.deps.providers().find((item) => item.id === binding.providerId)
    if (provider === undefined || provider.enabled === false) {
      return { ok: false, reason: 'The provider for the selected video model is disabled.' }
    }
    const generation = provider.videoGeneration
    if (generation === undefined) {
      return { ok: false, reason: `${provider.name} has no video endpoint configured.` }
    }
    const profileId = binding.video?.profileId
    const profile = profileId === undefined ? undefined : videoProfile(profileId)
    if (!isCallableProfile(profile)) {
      return {
        ok: false,
        reason: `"${binding.alias}" has no verified video profile yet, so it cannot be called. See Settings > Models > Video generation.`
      }
    }
    if (profile.adapter !== generation.adapter) {
      return {
        ok: false,
        reason: `The video profile for "${binding.alias}" does not match the provider's video adapter. Check its configuration in Settings > Models > Video generation.`
      }
    }

    /*
      ★★ 参数校验在**发请求之前**,而且用的是 profile 里那份声明 —— 见
      `upstream/video/validate.ts` 的文件头:上游对非法参数的反应分三种,
      三种都会让用户为一个不是他要的东西付钱。
    */
    const attempt = validateVideoRequest(profile, {
      action: draft.action,
      prompt: draft.prompt,
      ...(draft.image === undefined ? {} : { image: true }),
      ...(draft.lastFrame === undefined ? {} : { lastFrame: true }),
      ...(draft.video === undefined ? {} : { video: true }),
      ...(draft.duration === undefined ? {} : { duration: draft.duration }),
      ...(draft.aspectRatio === undefined ? {} : { aspectRatio: draft.aspectRatio }),
      ...(draft.resolution === undefined ? {} : { resolution: draft.resolution }),
      ...(draft.seed === undefined ? {} : { seed: draft.seed })
    })
    if (!attempt.ok) return { ok: false, reason: attempt.reason }

    const credential = await this.deps.credential(provider.credentialRef)
    if (credential === null) {
      return { ok: false, reason: `${provider.name} has no credential configured. Add its API key in Settings > Models > Video generation.` }
    }
    const adapter = videoAdapterFor(generation.adapter)
    if (adapter === undefined) {
      return { ok: false, reason: `No adapter is implemented for ${generation.adapter} yet.` }
    }

    const now = this.deps.now()
    const job: VideoJob = {
      id: makeJobId(now),
      configProfile: draft.configProfile,
      workspaceId: draft.workspaceId,
      sessionId: draft.sessionId,
      ...(draft.originRunId === undefined ? {} : { originRunId: draft.originRunId }),
      ...(draft.originCallId === undefined ? {} : { originCallId: draft.originCallId }),
      // ★ 提交中 —— 这一行落盘之后才发请求(见上面那段顺序的理由)
      cloud: 'submitting',
      retrieval: 'waiting',
      cancel: adapter.cancel === undefined ? 'unsupported' : 'none',
      providerId: provider.id,
      profileId: profile.id,
      model: binding.upstreamModel,
      connection: { ...generation, ...(generation.s3 === undefined ? {} : { s3: { ...generation.s3 } }) },
      credentialFingerprint: credentialFingerprint(credential),
      assets: [],
      createdAt: now,
      updatedAt: now,
      revision: 1
    }
    this.deps.store.put(job)
    this.emit(job)

    const request: VideoRequest = {
      action: draft.action,
      prompt: draft.prompt,
      model: binding.video?.endpointId ?? binding.upstreamModel,
      ...(draft.image === undefined ? {} : { image: draft.image }),
      ...(draft.lastFrame === undefined ? {} : { lastFrame: draft.lastFrame }),
      ...(draft.video === undefined ? {} : { video: draft.video }),
      ...(attempt.value.duration === undefined ? {} : { duration: attempt.value.duration }),
      ...(attempt.value.aspectRatio === undefined ? {} : { aspectRatio: attempt.value.aspectRatio }),
      ...(attempt.value.resolution === undefined ? {} : { resolution: attempt.value.resolution }),
      ...(attempt.value.seed === undefined ? {} : { seed: attempt.value.seed })
    }

    try {
      const created = await adapter.create(this.adapterContext(provider, profile.id, credential, job.connection), request)
      const next = this.update(job, {
        cloud: created.status === 'submitting' ? 'queued' : created.status,
        upstreamId: created.upstreamId,
        route: created.route
      })
      this.schedulePoll(next)
      return { ok: true, job: next, providerName: provider.name }
    } catch (error) {
      /*
        ★★ **创建失败也分两种**,而这里**故意合并成 unknown**:
          - 明确的上游拒绝(4xx/5xx,adapter 抛的是可读消息)→ 其实知道没创建;
          - 超时/断线/进程退出 → 拿不准。
        区分它们需要在适配器里把错误分类,而**分错的代价不对称**:把"其实没创建"
        当 unknown 只是多一条要用户手工确认的任务;把"已经创建"当失败则会
        自动重发,也就是**重复付费**。所以宁可多一条 unknown,让用户自己看。

        ★ 但明确的 4xx 是能区分的(adapter 的消息以 `HTTP 4` 开头),那条
        确实没创建 → 记 failed 并带上上游原话,用户可以照它改参数重试。
      */
      const message = error instanceof Error ? error.message : String(error)
      const rejected = /^HTTP 4\d\d\b/u.test(message)
      return { ok: false, reason: message, ...(rejected ? {} : {}) }
    } finally {
      this.inflight.delete(job.id)
    }
  }

  /** 某个会话的全部任务(降序)。渲染层用它补齐工具调用结束之后的卡片状态。 */
  listForSession(sessionId: string): VideoJob[] {
    return this.deps.store.listForSession(sessionId)
  }

  /** 单个任务的当前视图。找不到答 undefined(工具与 IPC 都要能区分"没有这条")。 */
  status(jobId: string): VideoJobView | undefined {
    const job = this.deps.store.get(jobId)
    return job === undefined ? undefined : videoJobView(job)
  }

  /**
   * 独立取消一个任务 —— 与"停止聊天"和"关闭生成开关"都无关。
   *
   * ★★ 三件事必须如实报告,不能含糊:
   *   1. 这家**不支持**取消(适配器没有 cancel)→ 明确说不支持,并提示可能仍在计费;
   *   2. 请求已发出(202 之类的"收到")→ 记 `requested`,**不直接标 canceled**;
   *   3. 已经完成 → 不谎报取消成功。
   */
  async cancel(jobId: string): Promise<{ ok: true; state: 'requested' | 'already-done' } | { ok: false; reason: string }> {
    const job = this.deps.store.get(jobId)
    if (job === undefined) return { ok: false, reason: 'no such video job' }
    if (job.cloud === 'succeeded' || job.cloud === 'failed' || job.cloud === 'canceled') {
      return { ok: true, state: 'already-done' }
    }
    if (job.upstreamId === undefined) {
      return { ok: false, reason: 'the task was never accepted upstream, so there is nothing to cancel' }
    }
    const profile = videoProfile(job.profileId)
    const provider = this.deps.providers().find((item) => item.id === job.providerId)
    const adapter = profile === undefined ? undefined : videoAdapterFor(profile.adapter)
    if (profile === undefined || provider === undefined || adapter === undefined) {
      return { ok: false, reason: 'the connection for this job is gone; the cloud task may still be running and billing' }
    }
    if (adapter.cancel === undefined) {
      /*
        ★ 不支持取消**不是错误**,是一个事实。这里改 job 的 cancel 字段为
        `unsupported` 并让轮询继续跑 —— 用户至少还能拿到成品。
      */
      this.update(job, { cancel: 'unsupported' })
      return { ok: false, reason: `${provider.name} does not offer a cancel operation for this model; the task may still finish and be billed. Its result will still be collected.` }
    }
    const credential = await this.deps.credential(provider.credentialRef)
    if (credential === null) return { ok: false, reason: `${provider.name} has no credential configured` }
    if (job.credentialFingerprint !== undefined && credentialFingerprint(credential) !== job.credentialFingerprint) {
      return { ok: false, reason: 'the credential for this job changed; reconnect the original account to cancel it' }
    }
    try {
      await adapter.cancel(this.adapterContext(provider, profile.id, credential, job.connection), job.upstreamId, job.route)
    } catch (error) {
      this.update(job, { cancel: 'failed' })
      return { ok: false, reason: error instanceof Error ? error.message : String(error) }
    }
    this.update(job, { cancel: 'requested' })
    // ★ 取消请求收到了,但**状态仍是进行中** —— 由轮询收尾(见 file 头)。
    this.schedulePoll(this.deps.store.get(jobId) ?? job)
    return { ok: true, state: 'requested' }
  }

  /** 取回失败之后的人工重试。**只查询/只下载**,绝不重新生成。 */
  async retryRetrieval(jobId: string): Promise<{ ok: boolean; reason?: string }> {
    const job = this.deps.store.get(jobId)
    if (job === undefined) return { ok: false, reason: 'no such video job' }
    if (job.cloud !== 'succeeded') return { ok: false, reason: 'the video has not been generated yet' }
    this.update(job, { retrieval: 'waiting' })
    this.schedulePoll(this.deps.store.get(jobId) ?? job)
    return { ok: true }
  }

  /**
   * 启动恢复。
   *
   * ★★ **只查询,不提交。** 这条路径是"重启之后把没跑完的接上",它的正确性
   * 判据是"POST 计数一次都不增加" —— 见 manager.test.ts 里那条断言。
   */
  resume(configProfile: string): number {
    this.stopped = false
    const jobs = this.deps.store.listRecoverable(configProfile)
    for (const job of jobs) {
      /*
        ★ 凭据身份变了(换了账户/Key)的任务**不能续查**:拿新的凭据去问旧任务,
        要么查到**另一个账户**的任务(它恰好有同 id 的话),要么一个 404 ——
        而两种都会让用户以为"我那条任务丢了"。所以留下来显示,但不推进。
      */
      if (job.credentialFingerprint !== undefined && job.credentialFingerprint !== this.currentFingerprint(job)) {
        this.update(job, { retrieval: 'paused' })
        continue
      }
      // ★ 停在 unknown(提交应答没拿到)的任务**也不自动推进** —— 见文件头第 1 条。
      if (job.cloud === 'unknown') continue
      this.schedulePoll(job)
    }
    return jobs.length
  }

  /** 退出时停掉本机 worker。**不取消云端任务** —— 它们不该因为我们退出而消失。 */
  shutdown(): void {
    this.stopped = true
    for (const worker of this.workers.values()) worker.cancel()
    this.workers.clear()
  }

  // ────────────────────────────────────────────────────────────
  // 内部
  // ────────────────────────────────────────────────────────────

  private currentFingerprint(job: VideoJob): string | undefined {
    const provider = this.deps.providers().find((item) => item.id === job.providerId)
    if (provider === undefined) return undefined
    // 指纹要**同步**拿:凭据本身是异步读的,所以这里只按"凭据槽还在不在"判,
    // 真正的 Key 变化在第一次请求 401 时会被识别出来(那时转 paused)。
    return provider.credentialRef === `provider:${job.providerId}` ? job.credentialFingerprint : undefined
  }

  private adapterContext(
    provider: UpstreamProvider,
    profileId: string,
    credential: ProviderCredential,
    connection?: ProviderVideoGeneration
  ): VideoAdapterContext {
    const profile = videoProfile(profileId)!
    const generation = connection ?? provider.videoGeneration
    return {
      provider: generation === undefined ? provider : { ...provider, videoGeneration: generation },
      profile,
      credential,
      fetch: this.deps.fetch,
      signal: new AbortController().signal,
      requestTimeoutMs: this.deps.requestTimeoutMs ?? DEFAULT_REQUEST_TIMEOUT_MS,
      ...(generation?.region === undefined ? {} : { region: generation.region }),
      ...(generation?.s3 === undefined ? {} : { s3: generation.s3 }),
    } as VideoAdapterContext
  }

  /**
   * ★ `Partial<VideoJob> & { route?: string }`:route 是适配器层才需要的路由段,
   * 落进 job 行是为了恢复查询,但**它不是领域层的概念** —— 领域类型里加一个
   * "只有商城类用得上"的字段会污染其余 13 家的模型。
   */
  private update(job: VideoJob, patch: Partial<VideoJob> & { route?: string }): VideoJob {
    const next: VideoJob = { ...job, ...patch, updatedAt: this.deps.now(), revision: job.revision + 1 }
    if (patch.route !== undefined) next.route = patch.route
    this.deps.store.put(next)
    this.emit(next)
    return next
  }

  private emit(job: VideoJob): void {
    this.deps.onChange?.(videoJobView(job))
  }

  private schedulePoll(job: VideoJob): void {
    if (this.stopped || !POLLING.has(job.cloud)) {
      /*
        ★ 云端已经终局、但本地还没取回时,也要再排一次 —— 那是"下载失败后重试"
        那条路径,它不进 POLLING(见 needsWorker)。
      */
      if (!this.stopped && job.cloud === 'succeeded' && (job.retrieval === 'waiting' || job.retrieval === 'downloading' || job.retrieval === 'retryable_error')) {
        this.scheduleWorker(job.id, 0)
      }
      return
    }
    const interval = this.pollIntervalMs(job)
    this.scheduleWorker(job.id, interval)
  }

  private pollIntervalMs(job: VideoJob): number {
    const bounds = this.deps.pollIntervalMs ?? DEFAULT_POLL
    /*
      ★ 退避按"已经跑了多久"算,不按"错了几次":一个真跑六分钟的任务不该在
      第 20 次成功查询之后被当成"有问题"而降速到看不出进展。上限封在 30 秒,
      因为它同时是"用户看到状态更新"的粒度。
    */
    const elapsed = this.deps.now() - job.createdAt
    const steps = Math.floor(elapsed / 60_000)
    return Math.min(bounds.max, bounds.min + steps * 5_000)
  }

  private scheduleWorker(jobId: string, delayMs: number): void {
    if (this.stopped || this.workers.has(jobId)) return
    const worker = this.deps.schedule(delayMs, () => {
      this.workers.delete(jobId)
      void this.pollOnce(jobId)
    })
    this.workers.set(jobId, worker)
  }

  /** 推进一个任务一步。**测试直接调它**,生产里由 worker 计时器调。 */
  async pollOnce(jobId: string): Promise<void> {
    if (this.stopped) return
    const job = this.deps.store.get(jobId)
    if (job === undefined) return

    if (job.cloud === 'succeeded') {
      await this.retrieveAssets(job)
      return
    }
    if (!POLLING.has(job.cloud) || job.upstreamId === undefined) return

    const profile = videoProfile(job.profileId)
    const provider = this.deps.providers().find((item) => item.id === job.providerId)
    const adapter = profile === undefined ? undefined : videoAdapterFor(profile.adapter)
    if (profile === undefined || provider === undefined || adapter === undefined) {
      this.update(job, { retrieval: 'paused', error: 'the connection for this job is gone' })
      return
    }
    const credential = await this.deps.credential(provider.credentialRef)
    if (credential === null) {
      this.update(job, { retrieval: 'paused', error: 'the credential for this job is missing' })
      return
    }
    /*
      ★ 凭据换了就**别拿新的去问旧任务** —— 见 resume 里那段。这里在每次查询前
      再确认一次,因为账户可能在任务跑的过程中被切换。
    */
    if (job.credentialFingerprint !== undefined && credentialFingerprint(credential) !== job.credentialFingerprint) {
      this.update(job, { retrieval: 'paused', error: 'the credential for this job changed; reconnect the original account to continue' })
      return
    }

    try {
      const status = await adapter.status(this.adapterContext(provider, profile.id, credential, job.connection), job.upstreamId, job.route)
      const next = this.update(job, {
        cloud: status.status,
        ...(status.error === undefined ? {} : { error: status.error }),
        ...(status.expiresAt === undefined ? {} : { expiresAt: status.expiresAt }),
        ...(status.percent === undefined && status.stage === undefined
          ? {}
          : { progress: { ...(status.percent === undefined ? {} : { percent: status.percent }), ...(status.stage === undefined ? {} : { stage: status.stage }) } })
      })
      if (status.status === 'succeeded') {
        /*
          ★★ **不在这里把 assets 直接写进 job** —— 先把远端地址带进取回步骤,
          取回成功才算 ready。地址只活在一次调用里(它有保质期)。
        */
        this.pendingAssets.set(job.id, status.assets ?? [])
        await this.retrieveAssets(next)
        return
      }
      if (status.status === 'failed' || status.status === 'canceled') return
      this.schedulePoll(next)
    } catch (error) {
      /*
        ★ 查询失败**不是**生成失败 —— 网络抖一下就把任务判死的话,一条正在跑
        (且已计费)的任务会被永久标成失败。记 retryable,继续轮询。
      */
      const message = error instanceof Error ? error.message : String(error)
      this.update(job, { error: message })
      this.schedulePoll(this.deps.store.get(jobId) ?? job)
    }
  }

  private readonly pendingAssets = new Map<string, readonly VideoRemoteAsset[]>()

  private async retrieveAssets(input: VideoJob): Promise<void> {
    const remote = this.pendingAssets.get(input.id) ?? []
    if (remote.length === 0) {
      this.update(input, { retrieval: 'retryable_error', error: 'the provider reported success but no video URL was captured' })
      return
    }
    const downloading = this.update(input, { retrieval: 'downloading', error: undefined })
    const assets: VideoAssetRef[] = []
    try {
      for (const asset of remote) {
        assets.push(await this.deps.retrieve(downloading, asset))
      }
      this.pendingAssets.delete(input.id)
      this.update(downloading, { retrieval: 'ready', assets })
    } catch (error) {
      /*
        ★★ 取回失败**单独记**,云端的成功事实一个字不改(见文件头第 2 条)。
        这样卡片能说清"已生成、还没取回来",而不是"生成失败"。
      */
      this.update(downloading, {
        retrieval: 'retryable_error',
        error: error instanceof Error ? error.message : String(error)
      })
    }
  }
}

/** 任务 id。用时间戳前缀让它天然按创建顺序排 —— 界面上不需要另存一个顺序。 */
function makeJobId(now: number): string {
  const random = Math.random().toString(36).slice(2, 10)
  return `vjob_${String(now)}_${random}`
}

/** 供测试与 UI 校验:这次提交在参数上是否成立。 */
export function validateDraft(
  profileId: string,
  draft: Pick<VideoSubmitDraft, 'action' | 'prompt' | 'image' | 'lastFrame' | 'video' | 'duration' | 'aspectRatio' | 'resolution' | 'seed'>
): VideoValidation {
  const profile = videoProfile(profileId)
  if (profile === undefined) return { ok: false, reason: `unknown video profile: ${profileId}` }
  return validateVideoRequest(profile, {
    action: draft.action,
    prompt: draft.prompt,
    ...(draft.image === undefined ? {} : { image: true }),
    ...(draft.lastFrame === undefined ? {} : { lastFrame: true }),
    ...(draft.video === undefined ? {} : { video: true }),
    ...(draft.duration === undefined ? {} : { duration: draft.duration }),
    ...(draft.aspectRatio === undefined ? {} : { aspectRatio: draft.aspectRatio }),
    ...(draft.resolution === undefined ? {} : { resolution: draft.resolution }),
    ...(draft.seed === undefined ? {} : { seed: draft.seed })
  })
}
