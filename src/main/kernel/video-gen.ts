/*
 * `generate_video` 的宿主通道 —— 工具与「后台任务管理器 + 附件存储」之间的那条缝。
 *
 * ★★ **和生图桥(`image-gen.ts`)最根本的区别:这里没有"同步拿到结果"这回事。**
 * 所以桥的动词不是 `generate(prompt) → images`,而是:
 *   - `unavailableReason()`:能不能**新建**(同步,给 `isEnabled`);
 *   - `submit(...)` → 一个**任务**;
 *   - `status(jobId)` / `cancel(jobId)`:对**已有任务**的操作。
 *
 * 后三个**不受"开关"和"当前选中的模型"约束** —— 那是关键:一个任务可能已经
 * 在云端跑了五分钟、已经计过费,用户关掉开关或换了个模型,都不该让他
 * **看不到**那条任务的结果。要停就单独取消(见 `settings.ts` 的
 * `videoGenerationEnabled` 那段)。
 *
 * ★ 形状与 `imageGen` / `shells` / `scheduling` 同一条规矩:内核只认这个窄接口,
 * 「任务存哪、密钥怎么读、URL 怎么下」全部由注入给出,内核零 electron、可单测。
 */
import type { VideoJobView, VideoAction } from '../../shared/domain/video-generation'

export interface VideoSubmitToolInput {
  action: VideoAction
  prompt: string
  image?: { mime: string; dataRef: { kind: 'url'; url: string } | { kind: 'bytes'; bytes: Uint8Array } }
  lastFrame?: { mime: string; dataRef: { kind: 'url'; url: string } | { kind: 'bytes'; bytes: Uint8Array } }
  /** 编辑/延长的源视频。**只可能是公网 http(s) URL**(见 video-generation.ts) */
  video?: { url: string }
  duration?: number | string
  aspectRatio?: string
  resolution?: string
  seed?: number
}

export interface VideoSubmitOutcome {
  ok: true
  job: VideoJobView
  /** 实际用的模型与供应商名 —— 回执里要写清"是谁生成的" */
  providerName: string
}

export interface VideoGenBridge {
  /**
   * 这一刻能不能**新建**。`null` = 可以;否则是给模型看的完整原因。
   *
   * ★ 同步、不碰凭证:与生图桥同一条理由 —— `isEnabled` 是每轮工具快照里的
   * 同步谓词,读密钥是异步的。没配 key 时工具照常下发,调用时得到一句
   * 可行动的失败(那比"工具不见了"好查得多)。
   */
  unavailableReason(): string | null
  /** 本会话的任务(降序)。有历史任务时 `status`/`cancel` 仍然可用。 */
  list(): readonly VideoJobView[]
  submit(input: VideoSubmitToolInput): Promise<VideoSubmitOutcome | { ok: false; reason: string }>
  /**
   * 独立取消。**只看任务,不看当前选中的模型和开关** —— 见文件头。
   * `requested` 表示取消请求已被上游接受(**不代表已取消**,由轮询收尾)。
   */
  cancel(jobId: string): Promise<{ ok: true; state: 'requested' | 'already-done' } | { ok: false; reason: string }>
  /** 某个任务的当前视图。找不到答 undefined(渲染层/工具都要能区分"没有这条")。 */
  status(jobId: string): VideoJobView | undefined
  /** 取回失败之后的重试 —— **只查询/只下载,绝不重新生成** */
  retryRetrieval(jobId: string): Promise<{ ok: boolean; reason?: string }>
}

/** 提交回执的开头 —— 模型据此知道"还没好",卡片据此认出这是提交而非成品。 */
export const VIDEO_SUBMITTED_PREFIX = 'Video generation submitted'
