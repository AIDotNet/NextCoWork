/*
 * `VideoGenBridge` 的主进程实现 —— `generate_video` 工具与后台 manager 之间的缝。
 *
 * ★★ 这个文件里最要紧的是**两种语义的分离**(见 `kernel/video-gen.ts` 的文件头):
 *
 * - `unavailableReason()` 回答的是"**能不能新建**" —— 它同步、不碰凭证,
 *   因为 `isEnabled` 是每轮工具快照里的同步谓词;
 * - `submit` / `status` / `cancel` 是对**任务**的操作,而**后者不看开关、
 *   不看当前选中的模型** —— 一个已经计过费的任务不该因为我们换了设置就查不到。
 *
 * 把这两件事混起来(比如把 `status` 也挂在 `available()` 后面)的症状是:
 * 用户关掉开关之后,那条已经跑了几分钟、已经计费的任务**从界面上消失**,
 * 而他永远不知道结果。
 *
 * ★ 形状与 `image-gen.ts` 同构:内核只认窄接口,「任务存哪、怎么装配」
 * 由 `runtime.ts` 注入。内核零 electron、可单测。
 */
import type { VideoJobView } from '../shared/domain/video-generation'
import { videoJobView } from '../shared/domain/video-generation'
import type { VideoGenBridge, VideoSubmitOutcome, VideoSubmitToolInput } from './kernel/video-gen'
import type { VideoManager } from './video-generation/manager'

export interface VideoGenBridgeDeps {
  sessionId: string
  /** 现取 manager(不闭包捕获):它可能在启动/切账户之后被重建 */
  manager: () => VideoManager | undefined
  /** 落库时记的配置作用域。不在这里判"能不能用",只记事实 */
  configProfile: () => string
  /** 任务的 workspace 归属。缺失 = 由任务表里那份说话 */
  workspaceId?: () => string
}

export function videoGenBridgeFor(deps: VideoGenBridgeDeps): VideoGenBridge {
  const manager = (): VideoManager | undefined => deps.manager()

  return {
    unavailableReason(): string | null {
      if (manager() === undefined) return 'Video generation is not available in this environment yet.'
      /*
        ★ 这里**刻意不查"有没有选中模型"** —— 那个判断在 manager 的 submit 里
        (它要读设置与别名表)。放在这儿的话 `isEnabled` 会因为一次"还没配"就把
        整个工具摘掉,连带 `status` / `cancel` 一起消失 —— 而那两条正是任务已经
        存在时唯一能用的东西。
      */
      return null
    },

    list(): readonly VideoJobView[] {
      const instance = manager()
      return instance === undefined ? [] : instance.listForSession(deps.sessionId).map(videoJobView)
    },

    async submit(input: VideoSubmitToolInput): Promise<VideoSubmitOutcome | { ok: false; reason: string }> {
      const instance = manager()
      if (instance === undefined) return { ok: false, reason: 'Video generation is not available in this environment.' }
      const result = await instance.submit({
        sessionId: deps.sessionId,
        workspaceId: deps.workspaceId?.() ?? '',
        configProfile: deps.configProfile(),
        action: input.action,
        prompt: input.prompt,
        ...(input.image === undefined ? {} : { image: input.image }),
        ...(input.lastFrame === undefined ? {} : { lastFrame: input.lastFrame }),
        ...(input.video === undefined ? {} : { video: input.video }),
        ...(input.duration === undefined ? {} : { duration: input.duration }),
        ...(input.aspectRatio === undefined ? {} : { aspectRatio: input.aspectRatio }),
        ...(input.resolution === undefined ? {} : { resolution: input.resolution }),
        ...(input.seed === undefined ? {} : { seed: input.seed })
      })
      if (!result.ok) return { ok: false, reason: result.reason }
      /*
        ★ `providerName` 是回执里那句"是谁生成的" —— 从 provider 表现取,
        因为 job 行里只存了 providerId(那是持久化的口径)。
      */
      return { ok: true, job: videoJobView(result.job), providerName: result.providerName }
    },

    async cancel(jobId: string) {
      const instance = manager()
      if (instance === undefined) return { ok: false, reason: 'Video generation is not available in this environment.' }
      return instance.cancel(jobId)
    },

    status(jobId: string): VideoJobView | undefined {
      return manager()?.status(jobId)
    },

    async retryRetrieval(jobId: string) {
      const instance = manager()
      if (instance === undefined) return { ok: false, reason: 'Video generation is not available in this environment.' }
      return instance.retryRetrieval(jobId)
    }
  }
}
