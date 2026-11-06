/**
 * 视频请求的参数校验 —— **一个动作在一条 profile 上能不能这么发**。
 *
 * ★★ 这是整个视频功能里最容易被写漏、也最贵的一处判断。上游对非法参数的反应
 * 分三种,而三种都不可接受:
 *   1. 有的家**静默忽略**(把 3 秒的请求按 5 秒计费);
 *   2. 有的家**自动升档**(把 480p 请求按 1080p 出);
 *   3. 有的家回一个 400,但**要等任务创建之后**才异步报出来 —— 于是我们已经
 *      把任务提交出去了、也计了费的一半流程。
 *
 * 所以这里做的是**本地、同步、失败即拒**:参数组合不合法就当场返回原因,
 * 一个付费请求都不发。这与 `video-gen.ts` 里"默认值用 profile 的显式默认"配套 ——
 * 缺省可以替用户挑(那是我们明写在档案里的),但**显式给了非法值就必须拒**。
 *
 * ★ 纯函数、无 IO:14 家的参数约束因此可以在 vitest 里逐条钉住。
 */

import type { VideoAction, VideoProfile } from '../../../../shared/domain/video-generation'

export interface VideoRequestDraft {
  action: VideoAction
  prompt: string
  image?: boolean
  lastFrame?: boolean
  video?: boolean
  duration?: number | string
  aspectRatio?: string
  resolution?: string
  seed?: number
}

export interface ValidatedVideoRequest {
  duration?: number | string
  aspectRatio?: string
  resolution?: string
  seed?: number
}

export type VideoValidation =
  | { ok: true; value: ValidatedVideoRequest }
  | { ok: false; reason: string }

const formatList = (values: readonly (number | string)[]): string => values.map((v) => String(v)).join(', ')

/**
 * 这一条 profile 允许这个动作吗。
 *
 * ★ 错误信息里**带上支持的动作清单** —— 模型看到"不支持"之后要能自己改口,
 * 而不是换一个参数再试。这正是"不做防御式 UI"里"失败也要可行动"的那一半。
 */
export function unsupportedActionReason(profile: VideoProfile, action: VideoAction): string | undefined {
  if (profile.actions.includes(action)) return undefined
  const supported = profile.actions.length === 0 ? 'none yet' : profile.actions.join(', ')
  return `${profile.label} does not support the "${action}" action on this connection (supported: ${supported}).`
}

export function validateVideoRequest(
  profile: VideoProfile,
  draft: VideoRequestDraft
): VideoValidation {
  const unsupported = unsupportedActionReason(profile, draft.action)
  if (unsupported !== undefined) return { ok: false, reason: unsupported }

  if (draft.prompt.trim() === '') {
    return { ok: false, reason: 'A prompt is required.' }
  }

  const spec = profile.parameters?.[draft.action]
  const out: ValidatedVideoRequest = {}

  // ── 时长 ──
  if (draft.duration !== undefined) {
    if (spec?.durations === undefined && spec?.durationRange === undefined) {
      return {
        ok: false,
        reason: `${profile.label} does not accept a duration for "${draft.action}" (it follows the source media).`
      }
    }
    if (spec.durations !== undefined) {
      /*
        ★ 比较用**同一把尺子**:两边都能量成秒就按秒比(`5` 与 `'5s'` 等价 ——
        模型很可能把 Luma 的 `'5s'` 写成 5),量不成才退回原样字符串比较。
        ★ 但**发出去的仍是我们档案里写的那个原值**:Luma 只认 `'5s'`,
        把它规范化成 `'5'` 会换来一句读不懂的 400。
      */
      const wanted = draft.duration
      const hit = spec.durations.find((value) => sameDuration(value, wanted))
      if (hit === undefined) {
        return {
          ok: false,
          reason: `${profile.label} accepts only these durations for "${draft.action}": ${formatList(spec.durations)}.`
        }
      }
      out.duration = hit
    } else if (spec.durationRange !== undefined) {
      const seconds = numericDuration(draft.duration)
      if (seconds === undefined || seconds < spec.durationRange.min || seconds > spec.durationRange.max) {
        return {
          ok: false,
          reason: `${profile.label} accepts durations from ${String(spec.durationRange.min)} to ${String(spec.durationRange.max)} seconds for "${draft.action}".`
        }
      }
      out.duration = seconds
    }
  } else if (spec?.defaultDuration !== undefined) {
    // 缺省**可以**用档案里的显式默认 —— 那是我们替用户挑的、写下来过的值。
    out.duration = spec.defaultDuration
  }

  // ── 比例 ──
  if (draft.aspectRatio !== undefined) {
    if (spec?.aspectRatios === undefined) {
      return { ok: false, reason: `${profile.label} does not accept an aspect ratio for "${draft.action}".` }
    }
    if (!spec.aspectRatios.includes(draft.aspectRatio)) {
      return {
        ok: false,
        reason: `${profile.label} accepts only these aspect ratios: ${spec.aspectRatios.join(', ')}.`
      }
    }
    out.aspectRatio = draft.aspectRatio
  } else if (spec?.defaultAspectRatio !== undefined) {
    out.aspectRatio = spec.defaultAspectRatio
  }

  // ── 分辨率 ──
  if (draft.resolution !== undefined) {
    if (spec?.resolutions === undefined) {
      return { ok: false, reason: `${profile.label} does not accept a resolution for "${draft.action}".` }
    }
    if (!spec.resolutions.includes(draft.resolution)) {
      return {
        ok: false,
        reason: `${profile.label} accepts only these resolutions: ${spec.resolutions.join(', ')}.`
      }
    }
    out.resolution = draft.resolution
  } else if (spec?.defaultResolution !== undefined) {
    out.resolution = spec.defaultResolution
  }

  // ── 种子 ──
  if (draft.seed !== undefined) {
    if (spec?.seed !== true) {
      return { ok: false, reason: `${profile.label} does not accept a seed for "${draft.action}".` }
    }
    if (!Number.isInteger(draft.seed) || draft.seed < 0) {
      return { ok: false, reason: 'seed must be a non-negative integer.' }
    }
    out.seed = draft.seed
  }

  // ── 素材与该动作是否配对 ──
  /*
    ★★ 这一段是"两个帧齐全才进首尾帧"的**兜底**:工具 schema 允许只给首帧或只给尾帧,
    而它们的落点不同(只给首帧 = `image`;只给尾帧 = 该家支持就在 `frames` 里单独用,
    不支持就必须拒)。这里只判"这个动作需要的媒介有没有给全",不做动作之间的挑选 ——
    挑选在工具层,因为它依赖模型的意图(prompt 里说了什么)。

    只给尾帧、而这家又只在 frames 动作里认它时,`image`/`lastFrame` 的组合由调用方
    决定传哪个 action;这里只保证"声明用了 frames 就必须有帧"。
  */
  if (draft.action === 'frames' && !(draft.image === true && draft.lastFrame === true)) {
    return { ok: false, reason: 'First-and-last-frame generation needs both a first frame and a last frame.' }
  }
  if (draft.action === 'image' && draft.image !== true) {
    return { ok: false, reason: 'Image-to-video needs an input image.' }
  }
  if ((draft.action === 'edit' || draft.action === 'extend') && draft.video !== true) {
    return {
      ok: false,
      reason: `${draft.action === 'extend' ? 'Extending' : 'Editing'} a video needs a public video URL (video_url).`
    }
  }

  return { ok: true, value: out }
}

/** 把 `'5s'` / `5` 都规范成可比较的字符串。 */
function normalizeDuration(value: number | string): string {
  return String(value).trim().toLowerCase()
}

/**
 * 两个时长是同一个吗。
 *
 * ★ 判据**优先按秒数比**:这样 `5` 与 `'5s'` 等价(模型常常把 Luma 的 `'5s'`
 *   写成数字),而量不成秒的两边退回原样字符串比较 —— 于是写错的单位
 *   (`'5 min'`)不会被当成 5 秒收下。
 */
function sameDuration(a: number | string, b: number | string): boolean {
  const na = numericDuration(a)
  const nb = numericDuration(b)
  if (na !== undefined && nb !== undefined) return na === nb
  return normalizeDuration(a) === normalizeDuration(b)
}

/** 时长 → 秒数。`'5s'` 与 `5` 都答 5;认不出答 undefined。 */
export function numericDuration(value: number | string): number | undefined {
  if (typeof value === 'number') return Number.isFinite(value) ? value : undefined
  const n = Number(normalizeDuration(value).replace(/(?:s|sec|secs|seconds?)$/u, ''))
  return Number.isFinite(n) ? n : undefined
}
