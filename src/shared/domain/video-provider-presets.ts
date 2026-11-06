/**
 * 视频连接预设 —— 「这家怎么出网」的出厂数据。
 *
 * ★★ **和聊天预设(`presets.ts`)分开,而且是独立的 provider id。**
 *
 * 聊天预设描述的是 `{baseUrl}` 上的一条**对话**协议;视频接口既不是
 * openai-chat 也不是 anthropic,而且地址常常是另一个 host(Google 的视频在
 * `generativelanguage.googleapis.com`,百炼的原生视频在 `/api/v1/…` 而不是
 * `/compatible-mode/v1`)。挂在同一条 provider 上就要往 `baseUrl` 里塞第二个地址,
 * 而 `baseUrl` 只有一个 —— 谁被覆盖,谁的请求就 404。
 *
 * 所以视频连接是**另一条 provider 记录**,id 带 `video-` 前缀,永不与聊天那条撞名。
 * 用户在视频页加的是它;聊天页看不到它(那边的左列按 `isChatModelAlias` 过滤)。
 */

import type { VideoAdapterId } from './video-generation'

/**
 * 这个连接要哪种凭据。
 *
 * ★ `api-key` 走现有的 `provider:setCredential`(裸 Bearer);
 *   后两种是**签名凭据**,要 ID+Secret(以及 AWS 的 region/session token),
 *   存进同一个加密槽但是另一个 JSON 形状 —— 见 `credential.ts`。
 */
export type VideoCredentialKind = 'api-key' | 'tencent-signature' | 'aws-signature'

export interface VideoProviderModel {
  /** 目录里的规范型号 id */
  model: string
  /** 它用哪条 profile */
  profileId: string
  /**
   * 商城类(fal / Replicate)的上游 endpoint / owner-model。
   * 缺席 = 用 `model`。
   */
  endpointId?: string
}

export interface VideoProviderPreset {
  /** provider id。**必须带 `video-` 前缀**,见文件头 */
  id: string
  name: string
  adapter: VideoAdapterId
  baseUrl: string
  credential: VideoCredentialKind
  /** 需要选地域的那些(阿里百炼:模型/Key/地域必须同属一地) */
  region?: string
  /** AWS 这类以对象存储为输出的 */
  s3?: { bucketHint: string; regionHint: string }
  docsUrl: string
  /** 这家有哪些型号、各用哪条 profile */
  models: readonly VideoProviderModel[]
  /** 该预设特有的坑,直接显示在表单下方 */
  notes?: string
  verification: 'documented' | 'unverified'
}

/** Google 的原生视频 host 与 OpenAI 兼容层不同 —— 这就是分开一条连接的理由。 */
const GOOGLE_VIDEO_BASE = 'https://generativelanguage.googleapis.com'
const DASHSCOPE_VIDEO_BASE = 'https://dashscope.aliyuncs.com'

export const VIDEO_PROVIDER_PRESETS: readonly VideoProviderPreset[] = [
  {
    id: 'video-google',
    name: 'Google Veo / Gemini(原生)',
    adapter: 'google-veo',
    baseUrl: GOOGLE_VIDEO_BASE,
    credential: 'api-key',
    docsUrl: 'https://ai.google.dev/gemini-api/docs/veo',
    verification: 'documented',
    notes: '用 Google AI Studio 的 API Key。这条走原生 generateContent,不是 OpenAI 兼容层。',
    models: [
      { model: 'veo-3.1-generate-preview', profileId: 'google-veo-3.1' },
      { model: 'veo-3.1-fast-generate-preview', profileId: 'google-veo-3.1-fast' },
      { model: 'veo-3.1-lite-generate-preview', profileId: 'google-veo-3.1-lite' },
      { model: 'gemini-omni-flash', profileId: 'google-omni-flash-video' }
    ]
  },
  {
    id: 'video-xai',
    name: 'xAI Grok 视频',
    adapter: 'xai-video',
    baseUrl: 'https://api.x.ai/v1',
    credential: 'api-key',
    docsUrl: 'https://docs.x.ai/developers/model-capabilities/video/generation',
    verification: 'documented',
    notes: '视频结果是一个限时 URL,拿到就会立刻下载进本会话。',
    models: [
      { model: 'grok-imagine-video-1.5', profileId: 'xai-video-1.5' },
      { model: 'grok-imagine-video', profileId: 'xai-video-classic' }
    ]
  },
  {
    id: 'video-ark',
    name: '火山方舟 · Seedance',
    adapter: 'ark-video',
    baseUrl: 'https://ark.cn-beijing.volces.com/api/v3',
    credential: 'api-key',
    docsUrl: 'https://docs.volcengine.com/docs/ark/create-video-generation-task-api',
    verification: 'documented',
    notes: 'Seedance 2.0 系列要先满足开通条件(余额或资源包)才调得动。',
    models: [
      { model: 'doubao-seedance-2-5-260628', profileId: 'ark-seedance-2-5' },
      { model: 'doubao-seedance-2-0-260128', profileId: 'ark-seedance-2-0' },
      { model: 'doubao-seedance-2-0-fast-260128', profileId: 'ark-seedance-2-0' },
      { model: 'doubao-seedance-2-0-mini-260615', profileId: 'ark-seedance-2-0' },
      { model: 'doubao-seedance-1-0-pro-250528', profileId: 'ark-seedance-1-0-pro' },
      { model: 'doubao-seedance-1-0-pro-fast-251015', profileId: 'ark-seedance-1-0-pro' }
    ]
  },
  {
    id: 'video-dashscope',
    name: '阿里百炼 · Wan(原生任务)',
    adapter: 'dashscope-video',
    baseUrl: DASHSCOPE_VIDEO_BASE,
    credential: 'api-key',
    region: 'cn-beijing',
    docsUrl: 'https://help.aliyun.com/zh/model-studio/text-to-video-api-reference',
    verification: 'documented',
    notes: '模型、Key、地域必须同属一地;这条用的是原生视频任务接口,不是 compatible-mode。',
    models: [
      { model: 'wan2.7-t2v', profileId: 'dashscope-wan-2-7-t2v' },
      { model: 'wan2.7-i2v', profileId: 'dashscope-wan-2-7-i2v' }
    ]
  },
  {
    id: 'video-kling',
    name: '可灵 Kling AI',
    adapter: 'kling-video',
    baseUrl: 'https://api-singapore.klingai.com',
    credential: 'api-key',
    docsUrl: 'https://kling.ai/document-api/guides/get-started/quick-start',
    verification: 'unverified',
    // ★ 本次只核到 Bearer + host + 目录型号;逐型号的请求路径没有核到正文。
    //   界面上会显示"接口待核对"。
    notes: '鉴权是 Bearer API Key(不是旧的 AK/SK 签名)。逐型号的请求接口待核对。',
    models: [
      { model: 'kling-video-3.0', profileId: 'kling-video-current' },
      { model: 'kling-video-3.0-omni', profileId: 'kling-video-current' },
      { model: 'kling-video-3.0-turbo', profileId: 'kling-video-current' }
    ]
  },
  {
    id: 'video-minimax',
    name: 'MiniMax 视频',
    adapter: 'minimax-video',
    baseUrl: 'https://api.minimax.io',
    credential: 'api-key',
    docsUrl: 'https://platform.minimax.io/docs/api-reference/video-generation-v2-create',
    verification: 'documented',
    notes: 'H3 / H3 Max 走 V2 任务接口;Hailuo 系列是 V1,成功后还要再取一次下载地址。',
    models: [
      { model: 'MiniMax-H3', profileId: 'minimax-h3-v2' },
      { model: 'MiniMax-H3-Max', profileId: 'minimax-h3-max-v2' },
      { model: 'MiniMax-Hailuo-2.3', profileId: 'minimax-hailuo-v1' },
      { model: 'MiniMax-Hailuo-02', profileId: 'minimax-hailuo-v1' },
      { model: 'T2V-01', profileId: 'minimax-hailuo-v1' },
      { model: 'T2V-01-Director', profileId: 'minimax-hailuo-v1' }
    ]
  },
  {
    id: 'video-bigmodel',
    name: '智谱 BigModel 视频',
    adapter: 'bigmodel-video',
    baseUrl: 'https://open.bigmodel.cn/api',
    credential: 'api-key',
    docsUrl: 'https://docs.bigmodel.cn/cn/guide/models/video-generation/cogvideox-3',
    verification: 'documented',
    notes: '视频走按量 API,Coding Plan 的额度不包含它。',
    models: [
      { model: 'cogvideox-3', profileId: 'bigmodel-cogvideox-3' },
      { model: 'cogvideox-2', profileId: 'bigmodel-cogvideox-3' },
      { model: 'cogvideox-flash', profileId: 'bigmodel-cogvideox-3' },
      { model: 'viduq1-text', profileId: 'bigmodel-vidu-q1' },
      { model: 'viduq1-image', profileId: 'bigmodel-vidu-q1' },
      { model: 'viduq1-start-end', profileId: 'bigmodel-vidu-q1' }
    ]
  },
  {
    id: 'video-siliconflow',
    name: '硅基流动视频',
    adapter: 'siliconflow-video',
    baseUrl: 'https://api.siliconflow.cn/v1',
    credential: 'api-key',
    docsUrl: 'https://docs.siliconflow.cn/',
    verification: 'unverified',
    // ★ 本次没定位到当前的官方视频接口文档,所以型号全部不可选。
    //   界面上显示"接口待核对",条目可见 —— 这是"不猜",不是遗漏。
    notes: '官方视频接口文档本次未定位到,条目暂不可选。',
    models: []
  },
  {
    id: 'video-runway',
    name: 'Runway',
    adapter: 'runway-video',
    baseUrl: 'https://api.dev.runwayml.com',
    credential: 'api-key',
    docsUrl: 'https://docs.dev.runwayml.com/api.md',
    verification: 'documented',
    notes: '请求体按型号区分,不能把上一个型号的 ratio/duration 拿来复用。',
    models: [
      { model: 'gen4.5', profileId: 'runway-gen4-5' },
      { model: 'gen4_turbo', profileId: 'runway-gen4-turbo' }
    ]
  },
  {
    id: 'video-luma',
    name: 'Luma(Agents API)',
    adapter: 'luma-video',
    baseUrl: 'https://agents.lumalabs.ai/v1',
    credential: 'api-key',
    docsUrl: 'https://docs.agents.lumalabs.ai/',
    verification: 'documented',
    models: [
      { model: 'ray-3.2', profileId: 'luma-ray-3-2' }
    ]
  },
  {
    id: 'video-luma-legacy',
    name: 'Luma Dream Machine(旧版)',
    adapter: 'luma-legacy-video',
    baseUrl: 'https://api.lumalabs.ai/dream-machine/v1',
    credential: 'api-key',
    docsUrl: 'https://docs.lumalabs.ai/docs/video-generation',
    verification: 'documented',
    notes: '旧版延长只接受它自己生成过的视频;图片必须用公网 CDN 地址(官方明确说不支持直接上传)。',
    models: [
      { model: 'ray-2', profileId: 'luma-ray-2-legacy' },
      { model: 'ray-flash-2', profileId: 'luma-ray-2-legacy' }
    ]
  },
  {
    id: 'video-fal',
    name: 'fal.ai',
    adapter: 'fal-queue',
    baseUrl: 'https://queue.fal.run',
    credential: 'api-key',
    docsUrl: 'https://fal.ai/docs/documentation/model-apis/inference/queue.md',
    verification: 'documented',
    notes: '鉴权头是 `Authorization: Key …`(不是 Bearer)。每个模型要单独填 endpoint。',
    models: [
      { model: 'fal-ai/veo3', profileId: 'fal-queue-endpoint', endpointId: 'fal-ai/veo3' },
      { model: 'fal-ai/kling-video/v2/master/text-to-video', profileId: 'fal-queue-endpoint', endpointId: 'fal-ai/kling-video/v2/master/text-to-video' },
      { model: 'fal-ai/minimax/hailuo-02/standard/text-to-video', profileId: 'fal-queue-endpoint', endpointId: 'fal-ai/minimax/hailuo-02/standard/text-to-video' }
    ]
  },
  {
    id: 'video-replicate',
    name: 'Replicate',
    adapter: 'replicate-predictions',
    baseUrl: 'https://api.replicate.com/v1',
    credential: 'api-key',
    docsUrl: 'https://replicate.com/docs/reference/http',
    verification: 'documented',
    notes: 'API 的输入输出默认一小时后清理 —— 所以成品会立刻下载进本会话。',
    models: [
      { model: 'minimax/video-01', profileId: 'replicate-predictions', endpointId: 'minimax/video-01' },
      { model: 'wan-video/wan-2.2-t2v-fast', profileId: 'replicate-predictions', endpointId: 'wan-video/wan-2.2-t2v-fast' }
    ]
  },
  {
    id: 'video-tencent',
    name: '腾讯混元生视频(vclm)',
    adapter: 'tencent-vclm',
    baseUrl: 'https://vclm.tencentcloudapi.com',
    credential: 'tencent-signature',
    region: 'ap-guangzhou',
    docsUrl: 'https://cloud.tencent.com/document/product/1749',
    verification: 'unverified',
    // ★ 只核到 Action 与 endpoint/version;字段形状没核到正文。
    notes: '这是 TC3 签名接口,需要 SecretId + SecretKey(不是一把 Bearer Key)。字段形状待核对。',
    models: [
      { model: 'hunyuan-video', profileId: 'tencent-vclm-hunyuan' }
    ]
  },
  {
    id: 'video-aws',
    name: 'Amazon Nova Reel(Bedrock)',
    adapter: 'aws-bedrock-video',
    baseUrl: 'https://bedrock-runtime.us-east-1.amazonaws.com',
    credential: 'aws-signature',
    region: 'us-east-1',
    s3: { bucketHint: 'nova-video-output', regionHint: 'us-east-1' },
    docsUrl: 'https://docs.aws.amazon.com/nova/latest/userguide/video-generation.html',
    verification: 'documented',
    notes: '需要 AWS 访问密钥 + region + 一个输出 S3 桶。当前只有美国东部(弗吉尼亚北部)可用。',
    models: [
      { model: 'amazon.nova-reel-v1:1', profileId: 'aws-nova-reel-1-1' }
    ]
  }
]

const BY_ID = new Map(VIDEO_PROVIDER_PRESETS.map((preset) => [preset.id, preset]))

export function videoProviderPreset(id: string): VideoProviderPreset | undefined {
  return BY_ID.get(id)
}

/** 这条预设里某个目录型号的绑定。找不到 = 这家不提供这个型号。 */
export function videoModelBinding(presetId: string, model: string): VideoProviderModel | undefined {
  return videoProviderPreset(presetId)?.models.find((entry) => entry.model === model)
}
