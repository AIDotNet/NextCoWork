/**
 * 视频卡片的**纯逻辑**:这一刻摆什么、状态行说什么。
 *
 * ★ 抽成 `.ts` 是这个仓库的可测试性手段(AGENTS §9):它的状态来自两类事实 ——
 * 落盘的 `output`(工具回执)与**后台任务的最新状态**(可能晚于工具调用到达)——
 * 两者之间的优先级规则盯屏幕是复现不出来的。
 *
 * ★★ 优先级是**任务状态压过工具回执**:一次 `generate` 调用早就结束(回执写着
 * "已提交"),而任务可能过几分钟才完成。以回执为准的话,卡片会永远停在
 * "已提交",而用户要的是"好了没有"。
 */
import type { ToolOutput } from '../../../../shared/agent/message'
import type { VideoJobView } from '../../../../shared/domain/video-generation'
import { pick } from '../../../../shared/domain/tool-presenter'

export type VideoGenPhase =
  /** 还没有任务 id(参数在流、或这次调用就是查询/取消) */
  | 'pending'
  /** 已提交,后台在跑 */
  | 'running'
  /** 云端成功、文件已落盘 —— 可以播 */
  | 'ready'
  /** 云端成功、但**取回失败** —— 这条必须与"生成失败"分开(见下) */
  | 'not-stored'
  | 'failed'
  | 'canceled'
  | 'paused'

export interface VideoGenView {
  phase: VideoGenPhase
  jobId?: string
  model?: string
  /** 真实的百分比与阶段文案;供应商没给就不给 */
  percent?: number
  stage?: string
  /** 可播放的地址(只在 `ready` 时非空) */
  src?: string
  mime?: string
  /** 独立取消能不能点:这家不支持时**不画**那颗按钮,而是画一句说明 */
  cancellable: boolean
  cancelRequested: boolean
  /** 失败/未落盘的原因(领域值,不翻译) */
  reason?: string
}

/**
 * 从入参 + 工具回执 + 任务状态算这一帧。
 *
 * ★ 三处刻意的判断:
 *   1. `not-stored` 与 `failed` **分开**:前者是"已经生成、还没存下来",
 *      重试只下载;后者是"没生成出来"。合成一个的话用户会点重试,再付一次钱。
 *   2. `canceled` 只在云端**明确说 canceled** 时才出现 —— 取消请求已发出
 *      (`cancel === 'requested'`)仍算 `running`,因为上游可能照样跑完。
 *   3. `cancellable` 看 `job.cancel !== 'unsupported'` —— 不支持的家不画按钮,
 *      画一句"这家没有取消接口",而不是给一个点了没反应的按钮。
 */
export function videoGenView(
  input: unknown,
  output: ToolOutput | undefined,
  job: VideoJobView | undefined
): VideoGenView {
  if (job === undefined) {
    /*
      ★★ **落盘的成品优先于"没有任务视图"**:重开历史会话时任务视图可能已经
      取不到了(任务行随会话清理、或超过上游的结果保质期),而工具回执里的
      `output.videos` 是**已经下载下来的会话附件地址** —— 它必须仍然能播。
      少了这一支,用户重开对话会发现昨天生成的视频"不见了",而文件就在磁盘上。

      ★ 判据是"回执里有 ncw:// 视频"而不是"有 job_id":地址才是能播的东西。
    */
    const stored = output?.videos?.[0]
    if (stored !== undefined) {
      return {
        phase: 'ready',
        src: stored.url,
        mime: stored.mime,
        cancellable: false,
        cancelRequested: false
      }
    }
    /*
      ★ 其余两种情况:
      - 这次调用是 `status` / `cancel`(入参里有 job_id);
      - 任务还没落到 store(刚提交、广播还没到)。
      都显示为"提交中",而不是"什么都没有" —— 前一帧的加载态不该闪没。
    */
    const jobId = input !== undefined && typeof input === 'object' && input !== null && 'job_id' in input
      ? String((input as Record<string, unknown>)['job_id'] ?? '')
      : ''
    return {
      phase: 'pending',
      ...(jobId === '' ? {} : { jobId }),
      cancellable: false,
      cancelRequested: false
    }
  }

  const base = {
    jobId: job.id,
    model: job.model,
    ...(job.percent === undefined ? {} : { percent: job.percent }),
    ...(job.stage === undefined ? {} : { stage: job.stage }),
    cancelRequested: job.cancel === 'requested',
    cancellable: job.cancel !== 'unsupported' && job.cloud !== 'succeeded' && job.cloud !== 'failed' && job.cloud !== 'canceled'
  }

  if (job.cloud === 'succeeded') {
    if (job.retrieval === 'ready' && job.videos[0] !== undefined) {
      return { ...base, phase: 'ready', src: job.videos[0].url, mime: job.videos[0].mime, cancellable: false }
    }
    /*
      ★★ 这就是那条最容易被合并掉的分支:云端成功、本地没取回来。
      它既不是"在跑"也不是"失败" —— 说成后者会让用户重试生成。
    */
    return {
      ...base,
      phase: 'not-stored',
      cancellable: false,
      ...(job.error === undefined ? {} : { reason: job.error })
    }
  }
  if (job.cloud === 'failed') {
    return { ...base, phase: 'failed', cancellable: false, ...(job.error === undefined ? {} : { reason: job.error }) }
  }
  if (job.cloud === 'canceled') {
    return { ...base, phase: 'canceled', cancellable: false }
  }
  if (job.retrieval === 'paused') {
    return { ...base, phase: 'paused', cancellable: false, ...(job.error === undefined ? {} : { reason: job.error }) }
  }
  /*
    ★ 回执里的模型名只是**兜底** —— 任务视图里的那个才是权威(它记录的是创建时
    冻结的模型,而回执里那句是当时拼的字符串)。
  */
  if (base.model === undefined && output !== undefined) {
    const matched = /using ([^\s.]+)/u.exec(output.content)
    if (matched?.[1] !== undefined) return { ...base, phase: 'running', model: matched[1] }
  }
  return { ...base, phase: 'running' }
}

/** 这次调用的动作 —— 卡片标题与图标据此分流。 */
export function videoActionOf(input: unknown): 'generate' | 'image' | 'frames' | 'edit' | 'extend' | 'status' | 'cancel' {
  const action = pick(input, 'action')
  return action === 'image' || action === 'frames' || action === 'edit' || action === 'extend' || action === 'status' || action === 'cancel'
    ? action
    : 'generate'
}
