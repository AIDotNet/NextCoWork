/**
 * 视频卡片的纯逻辑 —— 三处**只有事件顺序才暴露**的判断。
 *
 * ★★ 这批用例盯的是"两条轨道"(云端生成 / 本地取回)与"两个来源"(实时任务状态 /
 * 落盘的会话附件)之间的优先级。它们错了的症状都很隐蔽,而且各不相同:
 *   - 把"已生成、未取回"当失败 → 用户点重试,**再付一次钱**;
 *   - 取消请求已发出就显示"已取消" → 上游其实还在跑、还在计费;
 *   - 没有任务视图时不看落盘成品 → 重开对话,昨天的视频"不见了"(文件还在磁盘上)。
 */
import { describe, expect, it } from 'vitest'
import type { ToolOutput } from '../../../../../shared/agent/message'
import type { VideoJobView } from '../../../../../shared/domain/video-generation'
import { videoGenView } from '../video-gen-view'

const job = (patch: Partial<VideoJobView>): VideoJobView => ({
  id: 'vjob_1',
  sessionId: 's1',
  cloud: 'running',
  retrieval: 'waiting',
  cancel: 'none',
  providerId: 'video-google',
  model: 'veo-3.1-generate-preview',
  videos: [],
  createdAt: 0,
  updatedAt: 0,
  revision: 1,
  ...patch
})

describe('两条轨道的展示', () => {
  it('云端成功 + 已落盘 → ready,带可播地址', () => {
    const view = videoGenView({ action: 'generate', prompt: 'x' }, undefined, job({
      cloud: 'succeeded',
      retrieval: 'ready',
      videos: [{ url: 'ncw://attachments/sessions/s1/a.mp4', mime: 'video/mp4', size: 100 }]
    }))
    expect(view.phase).toBe('ready')
    expect(view.src).toBe('ncw://attachments/sessions/s1/a.mp4')
  })

  it('★★ 云端成功但取回失败 → not-stored(不是 failed)', () => {
    const view = videoGenView({ action: 'generate', prompt: 'x' }, undefined, job({
      cloud: 'succeeded',
      retrieval: 'retryable_error',
      error: 'disk full'
    }))
    // ★ 说成 failed 会让用户重试 → 再付一次钱;这一条就是那句不变式的界面侧
    expect(view.phase).toBe('not-stored')
    expect(view.reason).toBe('disk full')
    // 已经生成好了,不该再给"取消"
    expect(view.cancellable).toBe(false)
  })

  it('云端还在跑 → running,且**取消请求已发出**仍算 running(上游可能跑完)', () => {
    const view = videoGenView({ action: 'generate', prompt: 'x' }, undefined, job({ cancel: 'requested' }))
    expect(view.phase).toBe('running')
    expect(view.cancelRequested).toBe(true)
  })

  it('★ 这家没有取消接口 → 不画取消按钮(cancellable=false)', () => {
    const view = videoGenView({ action: 'generate', prompt: 'x' }, undefined, job({ cancel: 'unsupported' }))
    expect(view.cancellable).toBe(false)
  })
})

describe('两个来源的优先级', () => {
  it('★★ 没有任务视图、但回执里有落盘视频 → 照样 ready(重开历史会话)', () => {
    const output: ToolOutput = { content: 'Generated 1 video', videos: [{ url: 'ncw://attachments/sessions/s1/old.mp4', mime: 'video/mp4', size: 9 }] }
    const view = videoGenView({ action: 'status', job_id: 'vjob_x' }, output, undefined)
    expect(view.phase).toBe('ready')
    expect(view.src).toBe('ncw://attachments/sessions/s1/old.mp4')
  })

  it('没有任务视图也没有成品 → pending(加载态不该闪没)', () => {
    const view = videoGenView({ action: 'generate', prompt: 'x' }, undefined, undefined)
    expect(view.phase).toBe('pending')
  })

  it('查询/取消调用带上 job_id 时把 id 带出来(卡片据此订阅)', () => {
    const view = videoGenView({ action: 'status', job_id: 'vjob_9' }, undefined, undefined)
    expect(view.jobId).toBe('vjob_9')
  })

  it('暂停(凭据换了)→ paused,不给取消按钮', () => {
    const view = videoGenView({ action: 'generate', prompt: 'x' }, undefined, job({ retrieval: 'paused', error: 'credential changed' }))
    expect(view.phase).toBe('paused')
    expect(view.cancellable).toBe(false)
  })
})
