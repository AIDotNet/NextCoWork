/**
 * 视频档案表 —— 「供应商 × 型号 × API 版本」的可执行请求形状。
 *
 * ★★ **这张表的每一条都要有官方来源,而且 `actions` 只能是"我们真的写了"的那些。**
 *
 * 目录(`model-catalog-inventory`)说的是"世界上有哪些型号";这里说的是
 * "我们的适配器认得哪几条请求"。两者合起来才决定设置页里哪个条目可选。
 * 把没写适配器的型号标成可调用,用户点下去只会拿到一句上游 400/404 ——
 * 而那是我们这边缺东西,不是他的配置有问题。
 *
 * ★ 一家多版本**必须各写一条**。MiniMax H3 是 `/v2/video_generation`,
 * Hailuo 是 v1 且成功后还要拿 file_id 换下载地址;Luma Ray 3.2 在新 host,
 * Ray 2/Flash 在旧 Dream Machine 上。合成一条的话,升级版参数会被发去旧端点。
 *
 * 来源与核对日期写在每条 `source` 上;核对日期是**调查当天**,不是"现在" ——
 * 上游会漂,漂了就该改这一行并更新日期,而不是让代码里的一句话慢慢变成假话。
 */

import type { VideoCapabilities, VideoProfile } from './video-generation'

/** 核对日期。与目录的 `MODEL_CATALOG_FETCHED_AT` 分开:接口比型号烂得快。 */
export const VIDEO_PROFILES_VERIFIED_AT = '2026-10-05'

const none: VideoCapabilities = {
  textToVideo: false,
  imageToVideo: false,
  firstLastFrame: false,
  editVideo: false,
  extendVideo: false
}

/** 十秒以内的整数秒,给"只接受离散秒数"的那些家复用。 */
const seconds = (...values: number[]): readonly number[] => values

export const VIDEO_PROFILES: readonly VideoProfile[] = [
  // ── Google Veo(原生 generateContent / predictLongRunning)──
  {
    id: 'google-veo-3.1',
    label: 'Google Veo 3.1',
    adapter: 'google-veo',
    apiVersion: 'v1beta',
    actions: ['generate', 'image', 'frames', 'extend'],
    capabilities: {
      textToVideo: true,
      imageToVideo: true,
      firstLastFrame: true,
      editVideo: false,
      extendVideo: true,
      nativeAudio: true,
      // 延长只吃 Veo 自己生成的视频,且有时效(官方:2 天内)。
      videoInputLimitedToOwnOutput: true,
      videoInputMaxAgeMs: 2 * 24 * 60 * 60 * 1000
    },
    inputs: {
      generate: ['prompt'],
      image: ['prompt', 'imageBytes'],
      frames: ['prompt', 'imageBytes'],
      extend: ['prompt', 'videoBytes']
    },
    parameters: {
      generate: {
        aspectRatios: ['16:9', '9:16'],
        defaultAspectRatio: '16:9',
        resolutions: ['720p', '1080p', '4k'],
        defaultResolution: '720p'
      },
      // 延长固定 7 秒、最多 20 次,而且只出 720p —— 不给用户假的选择。
      extend: { resolutions: ['720p'], defaultResolution: '720p' }
    },
    videoLimits: { maxDurationSeconds: 141, mimes: ['video/mp4'] },
    cancel: false,
    source: { url: 'https://ai.google.dev/gemini-api/docs/veo', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },
  {
    id: 'google-veo-3.1-fast',
    label: 'Google Veo 3.1 Fast',
    adapter: 'google-veo',
    apiVersion: 'v1beta',
    actions: ['generate', 'image', 'frames', 'extend'],
    capabilities: {
      textToVideo: true,
      imageToVideo: true,
      firstLastFrame: true,
      editVideo: false,
      extendVideo: true,
      nativeAudio: true,
      videoInputLimitedToOwnOutput: true,
      videoInputMaxAgeMs: 2 * 24 * 60 * 60 * 1000
    },
    inputs: {
      generate: ['prompt'],
      image: ['prompt', 'imageBytes'],
      frames: ['prompt', 'imageBytes'],
      extend: ['prompt', 'videoBytes']
    },
    parameters: {
      generate: { aspectRatios: ['16:9', '9:16'], defaultAspectRatio: '16:9', resolutions: ['720p', '1080p'], defaultResolution: '720p' },
      extend: { resolutions: ['720p'], defaultResolution: '720p' }
    },
    videoLimits: { maxDurationSeconds: 141, mimes: ['video/mp4'] },
    cancel: false,
    source: { url: 'https://ai.google.dev/gemini-api/docs/veo', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },
  {
    id: 'google-veo-3.1-lite',
    label: 'Google Veo 3.1 Lite',
    adapter: 'google-veo',
    apiVersion: 'v1beta',
    // ★ Lite **没有延长**(官方明确排除)。
    actions: ['generate', 'image'],
    capabilities: {
      textToVideo: true,
      imageToVideo: true,
      firstLastFrame: false,
      editVideo: false,
      extendVideo: false,
      nativeAudio: true
    },
    inputs: { generate: ['prompt'], image: ['prompt', 'imageBytes'] },
    parameters: {
      generate: { aspectRatios: ['16:9', '9:16'], defaultAspectRatio: '16:9', resolutions: ['720p', '1080p'], defaultResolution: '720p' }
    },
    cancel: false,
    source: { url: 'https://ai.google.dev/gemini-api/docs/veo', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },
  // ★ Gemini Omni Flash 是**另一条**原生 API(Interactions),不是 generateContent。
  //   单独一条 profile:合成一条的话,它会被发去 predictLongRunning 那个路径。
  {
    id: 'google-omni-flash-video',
    label: 'Gemini Omni Flash(视频)',
    adapter: 'google-omni',
    apiVersion: 'interactions',
    // ★★ 空 actions:**本次没核到它的请求/响应形状** —— 文档只说到"用 Interactions API、
    //    支持多轮编辑"。填一个猜出来的 body 会让用户以为能调,然后在某个字段上撞 400。
    //    条目在设置页可见(标"接口待核对"),但**不可选**,一个付费请求都不会发。
    actions: [],
    capabilities: {
      textToVideo: true,
      imageToVideo: true,
      firstLastFrame: false,
      editVideo: true,
      extendVideo: false,
      nativeAudio: true
    },
    inputs: { generate: ['prompt'], image: ['prompt', 'imageBytes'], edit: ['prompt', 'videoBytes'] },
    cancel: false,
    source: { url: 'https://ai.google.dev/gemini-api/docs/video', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    // ★ 这条的精确请求/响应形状**本次没有核到**——文档只说到"用 Interactions API、
    //   支持多轮编辑"。标 unverified:设置页会显示"接口待核对",而不是画一个
    //   point-and-pray 的选项。
    verification: 'unverified'
  },

  // ── xAI Grok Imagine ──
  {
    id: 'xai-video-1.5',
    label: 'xAI Grok Imagine 1.5',
    adapter: 'xai-video',
    apiVersion: 'v1',
    actions: ['generate', 'image', 'frames', 'edit', 'extend'],
    capabilities: {
      textToVideo: true,
      imageToVideo: true,
      firstLastFrame: true,
      editVideo: true,
      extendVideo: true,
      nativeAudio: true
    },
    inputs: {
      generate: ['prompt'],
      image: ['prompt', 'imageUrl'],
      frames: ['imageUrl'],
      edit: ['prompt', 'videoUrl'],
      extend: ['prompt', 'videoUrl']
    },
    parameters: {
      generate: {
        durationRange: { min: 1, max: 15 },
        defaultDuration: 8,
        aspectRatios: ['1:1', '16:9', '9:16', '4:3', '3:4', '3:2', '2:3'],
        defaultAspectRatio: '16:9',
        resolutions: ['480p', '720p', '1080p'],
        defaultResolution: '720p',
        audio: true
      },
      image: { durationRange: { min: 1, max: 15 }, defaultDuration: 8, resolutions: ['480p', '720p', '1080p'], defaultResolution: '720p', audio: true },
      frames: { durationRange: { min: 1, max: 15 }, defaultDuration: 8, audio: true },
      // 编辑不接受自定义时长/比例/分辨率 —— 输出跟输入走,官方明确写了。
      edit: {},
      extend: { durationRange: { min: 2, max: 10 }, defaultDuration: 6 }
    },
    videoLimits: { minDurationSeconds: 2, maxDurationSeconds: 15, mimes: ['video/mp4'] },
    cancel: false,
    source: { url: 'https://docs.x.ai/developers/model-capabilities/video/generation', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },
  {
    id: 'xai-video-classic',
    label: 'xAI Grok Imagine(经典型)',
    adapter: 'xai-video',
    apiVersion: 'v1',
    // ★ 经典型**拒绝** last_frame / keyframes(官方原话),所以没有首尾帧。
    actions: ['generate', 'image', 'edit', 'extend'],
    capabilities: {
      textToVideo: true,
      imageToVideo: true,
      firstLastFrame: false,
      editVideo: true,
      extendVideo: true,
      nativeAudio: true
    },
    inputs: {
      generate: ['prompt'],
      image: ['prompt', 'imageUrl'],
      edit: ['prompt', 'videoUrl'],
      extend: ['prompt', 'videoUrl']
    },
    parameters: {
      generate: { durationRange: { min: 1, max: 15 }, defaultDuration: 8, aspectRatios: ['16:9', '9:16'], defaultAspectRatio: '16:9', resolutions: ['480p', '720p'], defaultResolution: '480p', audio: true },
      image: { durationRange: { min: 1, max: 15 }, defaultDuration: 8, audio: true },
      edit: {},
      extend: { durationRange: { min: 2, max: 10 }, defaultDuration: 6 }
    },
    videoLimits: { minDurationSeconds: 2, maxDurationSeconds: 15, mimes: ['video/mp4'] },
    cancel: false,
    source: { url: 'https://docs.x.ai/developers/model-capabilities/video/generation', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },

  // ── 火山方舟 / Seedance ──
  {
    id: 'ark-seedance-2-5',
    label: 'Seedance 2.5(方舟)',
    adapter: 'ark-video',
    apiVersion: 'v3',
    actions: ['generate', 'image', 'frames', 'edit', 'extend'],
    capabilities: {
      textToVideo: true,
      imageToVideo: true,
      firstLastFrame: true,
      editVideo: true,
      extendVideo: true,
      nativeAudio: true
    },
    inputs: {
      generate: ['prompt'],
      image: ['prompt', 'imageUrl'],
      frames: ['prompt', 'imageUrl'],
      edit: ['prompt', 'videoUrl'],
      extend: ['prompt', 'videoUrl']
    },
    parameters: {
      generate: {
        durationRange: { min: 4, max: 30 },
        defaultDuration: 5,
        aspectRatios: ['16:9', '4:3', '1:1', '3:4', '9:16', '21:9'],
        defaultAspectRatio: '16:9',
        resolutions: ['480p', '720p', '1080p'],
        defaultResolution: '720p',
        seed: true,
        audio: true
      },
      image: { durationRange: { min: 4, max: 30 }, defaultDuration: 5, resolutions: ['480p', '720p', '1080p'], defaultResolution: '720p', seed: true },
      frames: { durationRange: { min: 4, max: 30 }, defaultDuration: 5, resolutions: ['480p', '720p', '1080p'], defaultResolution: '720p', seed: true },
      // 编辑:ratio 只能 adaptive、duration 只能 -1(保持原片长)。
      edit: {},
      extend: {}
    },
    imageLimits: { maxBytes: 30 * 1024 * 1024, minWidth: 300, maxWidth: 6000 },
    videoLimits: { maxBytes: 200 * 1024 * 1024, minDurationSeconds: 4, maxDurationSeconds: 30, mimes: ['video/mp4', 'video/quicktime'] },
    cancel: true,
    source: { url: 'https://docs.volcengine.com/docs/ark/create-video-generation-task-api', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },
  {
    id: 'ark-seedance-2-0',
    label: 'Seedance 2.0(方舟)',
    adapter: 'ark-video',
    apiVersion: 'v3',
    actions: ['generate', 'image', 'frames', 'edit', 'extend'],
    capabilities: {
      textToVideo: true,
      imageToVideo: true,
      firstLastFrame: true,
      editVideo: true,
      extendVideo: true,
      nativeAudio: true
    },
    inputs: {
      generate: ['prompt'],
      image: ['prompt', 'imageUrl'],
      frames: ['prompt', 'imageUrl'],
      edit: ['prompt', 'videoUrl'],
      extend: ['prompt', 'videoUrl']
    },
    parameters: {
      generate: { durationRange: { min: 4, max: 15 }, defaultDuration: 5, aspectRatios: ['16:9', '4:3', '1:1', '3:4', '9:16', '21:9'], defaultAspectRatio: '16:9', resolutions: ['480p', '720p', '1080p', '4k'], defaultResolution: '720p', seed: true, audio: true },
      image: { durationRange: { min: 4, max: 15 }, defaultDuration: 5, resolutions: ['480p', '720p', '1080p', '4k'], defaultResolution: '720p', seed: true },
      frames: { durationRange: { min: 4, max: 15 }, defaultDuration: 5, seed: true },
      edit: {},
      extend: {}
    },
    imageLimits: { maxBytes: 30 * 1024 * 1024 },
    videoLimits: { maxBytes: 200 * 1024 * 1024, minDurationSeconds: 2, maxDurationSeconds: 15 },
    cancel: true,
    source: { url: 'https://docs.volcengine.com/docs/ark/create-video-generation-task-api', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },
  {
    id: 'ark-seedance-1-0-pro',
    label: 'Seedance 1.0 Pro(方舟)',
    adapter: 'ark-video',
    apiVersion: 'v3',
    // ★ 1.0 没有全模态参考/编辑/延长,只有文生 + 图生 + 首尾帧。
    actions: ['generate', 'image', 'frames'],
    capabilities: { ...none, textToVideo: true, imageToVideo: true, firstLastFrame: true },
    inputs: { generate: ['prompt'], image: ['prompt', 'imageUrl'], frames: ['prompt', 'imageUrl'] },
    parameters: {
      generate: { durationRange: { min: 2, max: 12 }, defaultDuration: 5, aspectRatios: ['16:9', '4:3', '1:1', '3:4', '9:16', '21:9'], defaultAspectRatio: '16:9', resolutions: ['480p', '720p', '1080p'], defaultResolution: '1080p', seed: true },
      image: { durationRange: { min: 2, max: 12 }, defaultDuration: 5, seed: true },
      frames: { durationRange: { min: 2, max: 12 }, defaultDuration: 5, seed: true }
    },
    imageLimits: { maxBytes: 30 * 1024 * 1024 },
    cancel: true,
    source: { url: 'https://docs.volcengine.com/docs/ark/create-video-generation-task-api', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },

  // ── 阿里百炼 Wan(原生异步任务)──
  {
    id: 'dashscope-wan-2-7-t2v',
    label: 'Wan 2.7 文生视频',
    adapter: 'dashscope-video',
    apiVersion: 'v1',
    // ★ 这是**文生**专属端点,不接受图片 —— 图生是另一个模型 id。
    actions: ['generate'],
    capabilities: { ...none, textToVideo: true, nativeAudio: true },
    inputs: { generate: ['prompt'] },
    parameters: {
      generate: {
        durations: seconds(2, 3, 5, 8, 10, 15),
        defaultDuration: 5,
        aspectRatios: ['16:9', '9:16', '1:1', '4:3', '3:4'],
        defaultAspectRatio: '16:9',
        resolutions: ['720P', '1080P'],
        defaultResolution: '1080P',
        seed: true,
        audio: true
      }
    },
    cancel: true,
    resultTtlMs: 24 * 60 * 60 * 1000,
    source: { url: 'https://help.aliyun.com/zh/model-studio/text-to-video-api-reference', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },
  {
    id: 'dashscope-wan-2-7-i2v',
    label: 'Wan 2.7 图生视频',
    adapter: 'dashscope-video',
    apiVersion: 'v1',
    actions: ['image'],
    capabilities: { ...none, imageToVideo: true, nativeAudio: true },
    inputs: { image: ['prompt', 'imageUrl'] },
    parameters: {
      image: { durations: seconds(2, 3, 5, 8, 10, 15), defaultDuration: 5, resolutions: ['720P', '1080P'], defaultResolution: '1080P', seed: true }
    },
    cancel: true,
    resultTtlMs: 24 * 60 * 60 * 1000,
    source: { url: 'https://help.aliyun.com/zh/model-studio/text-to-video-api-reference', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },

  // ── 可灵(Bearer,当前官方 quick start)──
  {
    id: 'kling-video-current',
    label: '可灵视频生成',
    adapter: 'kling-video',
    apiVersion: 'current',
    // ★★ 空 actions:本次只核到 **Bearer Key + host + 目录型号**,逐型号的
    //    请求路径与参数没有核到正文。可见、不可选。
    actions: [],
    capabilities: { ...none, textToVideo: true, imageToVideo: true, firstLastFrame: true, nativeAudio: true },
    inputs: { generate: ['prompt'], image: ['prompt', 'imageUrl'], frames: ['prompt', 'imageUrl'] },
    cancel: false,
    // ★ 本次只核到 Bearer + host + 目录型号;**逐型号的请求路径与参数没有核到正文**。
    //   标 unverified 让界面说"待核对",而不是让用户点下去撞 404。
    source: { url: 'https://kling.ai/document-api/guides/get-started/quick-start', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'unverified'
  },

  // ── MiniMax ──
  {
    id: 'minimax-h3-v2',
    label: 'MiniMax H3(V2 任务)',
    adapter: 'minimax-video',
    apiVersion: 'v2',
    actions: ['generate', 'image', 'frames'],
    capabilities: { ...none, textToVideo: true, imageToVideo: true, firstLastFrame: true, nativeAudio: true },
    inputs: { generate: ['prompt'], image: ['prompt', 'imageUrl'], frames: ['prompt', 'imageUrl'] },
    parameters: {
      generate: {
        durationRange: { min: 4, max: 15 },
        defaultDuration: 5,
        aspectRatios: ['21:9', '16:9', '4:3', '1:1', '3:4', '9:16'],
        defaultAspectRatio: '16:9',
        resolutions: ['768P', '2K'],
        defaultResolution: '768P',
        audio: true
      },
      image: { durationRange: { min: 4, max: 15 }, defaultDuration: 5, resolutions: ['768P', '2K'], defaultResolution: '768P' },
      frames: { durationRange: { min: 4, max: 15 }, defaultDuration: 5, resolutions: ['768P', '2K'], defaultResolution: '768P' }
    },
    cancel: true,
    resultTtlMs: 7 * 24 * 60 * 60 * 1000,
    source: { url: 'https://platform.minimax.io/docs/api-reference/video-generation-v2-create', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },
  {
    id: 'minimax-h3-max-v2',
    label: 'MiniMax H3 Max(V2 任务)',
    adapter: 'minimax-video',
    apiVersion: 'v2',
    actions: ['generate', 'image', 'frames'],
    capabilities: { ...none, textToVideo: true, imageToVideo: true, firstLastFrame: true, nativeAudio: true },
    inputs: { generate: ['prompt'], image: ['prompt', 'imageUrl'], frames: ['prompt', 'imageUrl'] },
    parameters: {
      generate: { durationRange: { min: 5, max: 15 }, defaultDuration: 5, aspectRatios: ['21:9', '16:9', '4:3', '1:1', '3:4', '9:16'], defaultAspectRatio: '16:9', resolutions: ['480P', '768P'], defaultResolution: '768P', audio: true },
      image: { durationRange: { min: 5, max: 15 }, defaultDuration: 5, resolutions: ['480P', '768P'], defaultResolution: '768P' },
      frames: { durationRange: { min: 5, max: 15 }, defaultDuration: 5, resolutions: ['480P', '768P'], defaultResolution: '768P' }
    },
    cancel: true,
    resultTtlMs: 7 * 24 * 60 * 60 * 1000,
    source: { url: 'https://platform.minimax.io/docs/api-reference/video-generation-v2-create', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },
  {
    id: 'minimax-hailuo-v1',
    label: 'MiniMax Hailuo(V1 任务)',
    adapter: 'minimax-video',
    apiVersion: 'v1',
    // ★ 旧版成功后要按 file_id 再取一次下载地址 —— 适配器里那条分支就是它。
    actions: ['generate', 'image', 'frames'],
    capabilities: { ...none, textToVideo: true, imageToVideo: true, firstLastFrame: true },
    inputs: { generate: ['prompt'], image: ['prompt', 'imageUrl'], frames: ['prompt', 'imageUrl'] },
    parameters: {
      generate: { durations: seconds(6, 10), defaultDuration: 6, resolutions: ['720P', '768P', '1080P'], defaultResolution: '768P' },
      image: { durations: seconds(6, 10), defaultDuration: 6, resolutions: ['720P', '768P', '1080P'], defaultResolution: '768P' },
      frames: { durations: seconds(6, 10), defaultDuration: 6, resolutions: ['720P', '768P', '1080P'], defaultResolution: '768P' }
    },
    cancel: false,
    source: { url: 'https://platform.minimax.io/docs/api-reference/video-generation-t2v', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },

  // ── 智谱 / CogVideoX 与托管的 Vidu ──
  {
    id: 'bigmodel-cogvideox-3',
    label: 'CogVideoX-3(智谱)',
    adapter: 'bigmodel-video',
    apiVersion: 'v4',
    actions: ['generate', 'image', 'frames'],
    capabilities: { ...none, textToVideo: true, imageToVideo: true, firstLastFrame: true, nativeAudio: true },
    inputs: { generate: ['prompt'], image: ['prompt', 'imageUrl'], frames: ['prompt', 'imageUrl'] },
    parameters: {
      generate: {
        durations: seconds(5, 10),
        defaultDuration: 5,
        resolutions: ['1280x720', '720x1280', '1024x1024', '1920x1080', '1080x1920', '2048x1080', '3840x2160'],
        defaultResolution: '1280x720',
        audio: true
      },
      image: { durations: seconds(5, 10), defaultDuration: 5 },
      frames: { durations: seconds(5, 10), defaultDuration: 5 }
    },
    imageLimits: { maxBytes: 5 * 1024 * 1024, mimes: ['image/png', 'image/jpeg'] },
    cancel: true,
    source: { url: 'https://docs.bigmodel.cn/api-reference/%E6%A8%A1%E5%9E%8B-api/%E8%A7%86%E9%A2%91%E7%94%9F%E6%88%90%E5%BC%82%E6%AD%A5.md', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },
  {
    id: 'bigmodel-vidu-q1',
    label: 'Vidu Q1(智谱托管)',
    adapter: 'bigmodel-video',
    apiVersion: 'v4',
    actions: ['generate', 'image', 'frames'],
    capabilities: { ...none, textToVideo: true, imageToVideo: true, firstLastFrame: true },
    inputs: { generate: ['prompt'], image: ['prompt', 'imageUrl'], frames: ['prompt', 'imageUrl'] },
    parameters: {
      generate: { durations: seconds(5), defaultDuration: 5, aspectRatios: ['16:9', '9:16', '1:1'], defaultAspectRatio: '16:9', resolutions: ['1920x1080'], defaultResolution: '1920x1080' },
      image: { durations: seconds(5), defaultDuration: 5, resolutions: ['1920x1080'], defaultResolution: '1920x1080' },
      frames: { durations: seconds(5), defaultDuration: 5, resolutions: ['1920x1080'], defaultResolution: '1920x1080' }
    },
    imageLimits: { maxBytes: 50 * 1024 * 1024 },
    cancel: true,
    source: { url: 'https://docs.bigmodel.cn/api-reference/%E6%A8%A1%E5%9E%8B-api/%E8%A7%86%E9%A2%91%E7%94%9F%E6%88%90%E5%BC%82%E6%AD%A5.md', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },

  // ── 硅基流动 ──
  {
    id: 'siliconflow-video',
    label: '硅基流动视频',
    adapter: 'siliconflow-video',
    actions: [],
    capabilities: { ...none },
    // ★★ 本次**没能定位到当前的官方视频接口文档**(旧路径、新版路径、llms 入口
    //   全 404)。所以 `actions` 是空的 —— 设置页会显示"接口待核对",
    //   条目可见但**不可选**。这不是遗漏,是"不猜"。
    //   定位到之后:填 actions + inputs + parameters,并把 verification 改成 documented。
    source: { url: 'https://docs.siliconflow.cn/', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'unverified'
  },

  // ── Runway ──
  {
    id: 'runway-gen4-5',
    label: 'Runway Gen-4.5',
    adapter: 'runway-video',
    apiVersion: '2024-11-06',
    actions: ['generate', 'image', 'edit'],
    capabilities: { ...none, textToVideo: true, imageToVideo: true, editVideo: true },
    inputs: { generate: ['prompt'], image: ['prompt', 'imageUrl'], edit: ['prompt', 'videoUrl'] },
    parameters: {
      generate: { durationRange: { min: 2, max: 10 }, defaultDuration: 5, aspectRatios: ['1280:720', '720:1280'], defaultAspectRatio: '1280:720' },
      image: { durationRange: { min: 2, max: 10 }, defaultDuration: 5 }
    },
    cancel: true,
    // ★ 输出 URL 是临时的,必须尽快下载 —— 官方原话。
    source: { url: 'https://docs.dev.runwayml.com/ai-context.md', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },
  {
    id: 'runway-gen4-turbo',
    label: 'Runway Gen-4 Turbo',
    adapter: 'runway-video',
    apiVersion: '2024-11-06',
    actions: ['generate', 'image', 'edit'],
    capabilities: { ...none, textToVideo: true, imageToVideo: true, editVideo: true },
    inputs: { generate: ['prompt'], image: ['prompt', 'imageUrl'], edit: ['prompt', 'videoUrl'] },
    parameters: {
      generate: { durationRange: { min: 2, max: 10 }, defaultDuration: 5, aspectRatios: ['1280:720', '720:1280'], defaultAspectRatio: '1280:720' },
      image: { durationRange: { min: 2, max: 10 }, defaultDuration: 5 }
    },
    cancel: true,
    source: { url: 'https://docs.dev.runwayml.com/ai-context.md', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },

  // ── Luma ──
  {
    id: 'luma-ray-3-2',
    label: 'Luma Ray 3.2(Agents API)',
    adapter: 'luma-video',
    apiVersion: 'agents-v1',
    actions: ['generate', 'image', 'frames', 'edit'],
    capabilities: { ...none, textToVideo: true, imageToVideo: true, firstLastFrame: true, editVideo: true },
    inputs: { generate: ['prompt'], image: ['prompt', 'imageUrl'], frames: ['prompt', 'imageUrl'], edit: ['prompt', 'videoUrl'] },
    parameters: {
      generate: { durations: ['5s', '9s'], defaultDuration: '5s', aspectRatios: ['16:9', '9:16', '1:1'], defaultAspectRatio: '16:9', resolutions: ['540p', '720p', '1080p', '4k'], defaultResolution: '720p' },
      image: { durations: ['5s', '9s'], defaultDuration: '5s', resolutions: ['540p', '720p', '1080p', '4k'], defaultResolution: '720p' },
      frames: { durations: ['5s', '9s'], defaultDuration: '5s' },
      edit: {}
    },
    cancel: false,
    source: { url: 'https://docs.agents.lumalabs.ai/', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },
  {
    id: 'luma-ray-2-legacy',
    label: 'Luma Ray 2 / Flash(旧 Dream Machine)',
    adapter: 'luma-legacy-video',
    apiVersion: 'dream-machine-v1',
    // ★ 旧版延长**只接受自己生成过的 video id**(keyframes 里 type: 'generation')。
    actions: ['generate', 'image', 'frames', 'extend'],
    capabilities: {
      textToVideo: true,
      imageToVideo: true,
      firstLastFrame: true,
      editVideo: false,
      extendVideo: true,
      videoInputLimitedToOwnOutput: true
    },
    inputs: {
      generate: ['prompt'],
      image: ['prompt', 'imageUrl'],
      frames: ['prompt', 'imageUrl'],
      extend: ['prompt', 'videoUrl']
    },
    parameters: {
      generate: { durations: ['5s', '9s'], defaultDuration: '5s', aspectRatios: ['16:9', '9:16', '1:1'], defaultAspectRatio: '16:9', resolutions: ['540p', '720p', '1080p', '4k'], defaultResolution: '720p' },
      image: { durations: ['5s', '9s'], defaultDuration: '5s' },
      frames: { durations: ['5s', '9s'], defaultDuration: '5s' },
      extend: {}
    },
    cancel: false,
    source: { url: 'https://docs.lumalabs.ai/docs/video-generation.md', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },

  // ── fal.ai(队列协议可以适配,模型逐条映射)──
  {
    id: 'fal-queue-endpoint',
    label: 'fal.ai(按 endpoint 调用)',
    adapter: 'fal-queue',
    apiVersion: 'queue',
    // ★ 队列协议本身是通用的,但**模型的输入 schema 是逐条的**。
    //   所以这条 profile 说的是"我们实现了队列协议",具体模型靠
    //   `alias.video.endpointId` 指过去;没有 endpointId 的条目不可调用。
    actions: ['generate', 'image', 'edit'],
    capabilities: { ...none, textToVideo: true, imageToVideo: true, editVideo: true },
    inputs: { generate: ['prompt'], image: ['prompt', 'imageUrl'], edit: ['prompt', 'videoUrl'] },
    cancel: true,
    source: { url: 'https://fal.ai/docs/documentation/model-apis/inference/queue.md', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },
  {
    id: 'replicate-predictions',
    label: 'Replicate(按模型调用)',
    adapter: 'replicate-predictions',
    apiVersion: 'v1',
    actions: ['generate', 'image', 'edit'],
    capabilities: { ...none, textToVideo: true, imageToVideo: true, editVideo: true },
    inputs: { generate: ['prompt'], image: ['prompt', 'imageUrl'], edit: ['prompt', 'videoUrl'] },
    cancel: true,
    // ★ 输入由模型的 openapi_schema 决定,输出可能是 string/array/object ——
    //   适配器按 schema 映射,不猜字段名。
    source: { url: 'https://replicate.com/docs/reference/http', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  },

  // ── 腾讯混元原生(TC3 Action API)──
  {
    id: 'tencent-vclm-hunyuan',
    label: '腾讯混元生视频(vclm)',
    adapter: 'tencent-vclm',
    apiVersion: '2024-05-23',
    // ★★ 空 actions:官方 SDK 核到了 Action 名与 endpoint/version(`vclm.tencentcloudapi.com`,
    //    2024-05-23,`SubmitHunyuanToVideoJob` / `DescribeHunyuanToVideoJob`),**但请求字段
    //    形状没有核到正文**。签名与鉴权那条路已经写好(`sign.ts`,有官方向量测试),
    //    等字段核对完就是填这一行 actions。
    actions: [],
    capabilities: { ...none, textToVideo: true, imageToVideo: true },
    inputs: { generate: ['prompt'], image: ['prompt', 'imageUrl'] },
    cancel: false,
    source: { url: 'https://raw.githubusercontent.com/TencentCloud/tencentcloud-sdk-nodejs/master/src/services/vclm/v20240523/vclm_client.ts', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    // ★ SDK 只核到 Action 名与 endpoint/version;**字段形状**没核到正文。
    verification: 'unverified'
  },

  // ── AWS Nova Reel(Bedrock 异步 + S3)──
  {
    id: 'aws-nova-reel-1-1',
    label: 'Amazon Nova Reel 1.1',
    adapter: 'aws-bedrock-video',
    apiVersion: '1.1',
    actions: ['generate', 'image'],
    capabilities: { ...none, textToVideo: true, imageToVideo: true },
    inputs: { generate: ['prompt'], image: ['prompt', 'imageUrl'] },
    parameters: {
      // 官方:6 秒一档,最多 2 分钟;720p。
      generate: { durations: seconds(6, 12), defaultDuration: 6, resolutions: ['1280x720'], defaultResolution: '1280x720' }
    },
    videoLimits: { maxDurationSeconds: 120 },
    cancel: false,
    source: { url: 'https://docs.aws.amazon.com/nova/latest/userguide/video-generation.html', verifiedAt: VIDEO_PROFILES_VERIFIED_AT },
    verification: 'documented'
  }
]

const BY_ID = new Map(VIDEO_PROFILES.map((profile) => [profile.id, profile]))

export function videoProfile(id: string): VideoProfile | undefined {
  return BY_ID.get(id)
}

/** 这条 profile 现在**真的能调**吗 —— 至少有一个实现并核对过的动作。 */
export function isCallableProfile(profile: VideoProfile | undefined): profile is VideoProfile {
  return profile !== undefined && profile.actions.length > 0
}

/** 这条 profile 核对过吗。未核对的在设置页显示"待核对",但仍可见。 */
export function isVerifiedProfile(profile: VideoProfile | undefined): boolean {
  return profile?.verification === 'documented'
}

/** 给模型看的紧凑能力摘要 —— 只列真的能做的,免得它去猜时长/分辨率。 */
export function videoProfileSummary(profile: VideoProfile): string {
  const parts: string[] = []
  for (const action of profile.actions) {
    const spec = profile.parameters?.[action]
    const bits: string[] = []
    if (spec?.durations !== undefined && spec.durations.length > 0) bits.push(`seconds ${spec.durations.join('/')}`)
    else if (spec?.durationRange !== undefined) bits.push(`seconds ${String(spec.durationRange.min)}-${String(spec.durationRange.max)}`)
    if (spec?.resolutions !== undefined && spec.resolutions.length > 0) bits.push(spec.resolutions.join('/'))
    if (spec?.aspectRatios !== undefined && spec.aspectRatios.length > 0) bits.push(spec.aspectRatios.join('/'))
    parts.push(bits.length === 0 ? action : `${action} (${bits.join(', ')})`)
  }
  return parts.join('; ')
}
