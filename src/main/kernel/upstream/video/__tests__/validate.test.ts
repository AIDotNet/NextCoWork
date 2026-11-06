/**
 * 参数校验 —— 这一层是"一个付费请求都不发"的闸门,所以每条规则都要有反例。
 *
 * ★ 每个用例都盯一件事:**非法输入必须被本地拒掉**。上游对非法参数的反应分三种
 * (静默忽略 / 自动升档 / 异步报错),三种都会让用户为一个不是他要的东西付钱。
 */
import { describe, expect, it } from 'vitest'
import { validateVideoRequest } from '../validate'
import { videoProfile } from '../../../../../shared/domain/video-profiles'

const ark25 = videoProfile('ark-seedance-2-5')!
const xai15 = videoProfile('xai-video-1.5')!
const veo = videoProfile('google-veo-3.1')!
const luma = videoProfile('luma-ray-3-2')!
const unsupported = videoProfile('google-omni-flash-video')!

describe('动作可用性', () => {
  it('没核对过请求形状的 profile 任何动作都不放行', () => {
    const result = validateVideoRequest(unsupported, { action: 'generate', prompt: 'x' })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toContain('does not support')
  })

  it('Veo 3.1 Lite 不支持延长 —— 与 3.1 不是同一条规则', () => {
    const lite = videoProfile('google-veo-3.1-lite')!
    expect(validateVideoRequest(lite, { action: 'extend', prompt: 'x', video: true }).ok).toBe(false)
    expect(validateVideoRequest(veo, { action: 'extend', prompt: 'x', video: true }).ok).toBe(true)
  })
})

describe('时长', () => {
  it('离散集合之外的秒数被拒(不悄悄改成最近的合法值)', () => {
    const result = validateVideoRequest(veo, { action: 'generate', prompt: 'x', duration: 5 })
    // Veo 的生成不接受 duration 参数 —— 它固定 8 秒一档
    expect(result.ok).toBe(false)
  })

  it('区间之外被拒', () => {
    const result = validateVideoRequest(ark25, { action: 'generate', prompt: 'x', duration: 31 })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toContain('4 to 30')
  })

  it('缺省用档案里的显式默认', () => {
    const result = validateVideoRequest(ark25, { action: 'generate', prompt: 'x' })
    expect(result.ok).toBe(true)
    if (result.ok) expect(result.value.duration).toBe(5)
  })

  it('Luma 的字符串时长按原值发出去(5 与 5s 都能对上)', () => {
    const asNumber = validateVideoRequest(luma, { action: 'generate', prompt: 'x', duration: 5 })
    const asString = validateVideoRequest(luma, { action: 'generate', prompt: 'x', duration: '5s' })
    expect(asNumber.ok && asNumber.value.duration).toBe('5s')
    expect(asString.ok && asString.value.duration).toBe('5s')
  })

  it('编辑不接受自定义时长时明确拒绝', () => {
    const result = validateVideoRequest(xai15, { action: 'edit', prompt: 'x', video: true, duration: 10 })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toContain('does not accept a duration')
  })
})

describe('比例与分辨率', () => {
  it('不在白名单里的比例被拒并列出可选值', () => {
    const result = validateVideoRequest(ark25, { action: 'generate', prompt: 'x', aspectRatio: '7:3' })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toContain('16:9')
  })

  it('不看档案的分辨率被拒', () => {
    const result = validateVideoRequest(ark25, { action: 'generate', prompt: 'x', resolution: '720P' })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toContain('480p')
  })
})

describe('素材与动作的配对', () => {
  it('首尾帧只给一帧时被拒', () => {
    expect(validateVideoRequest(veo, { action: 'frames', prompt: 'x', image: true }).ok).toBe(false)
    expect(validateVideoRequest(veo, { action: 'frames', prompt: 'x', image: true, lastFrame: true }).ok).toBe(true)
  })

  it('图生视频没给图时被拒', () => {
    const result = validateVideoRequest(veo, { action: 'image', prompt: 'x' })
    expect(result.ok).toBe(false)
  })

  it('编辑/延长没给视频时被拒(且提示要公网 URL)', () => {
    const edit = validateVideoRequest(xai15, { action: 'edit', prompt: 'x' })
    expect(edit.ok).toBe(false)
    if (!edit.ok) expect(edit.reason).toContain('public video URL')
  })

  it('空的 prompt 被拒', () => {
    expect(validateVideoRequest(ark25, { action: 'generate', prompt: '   ' }).ok).toBe(false)
  })

  it('seed 只在声明支持时可用', () => {
    expect(validateVideoRequest(ark25, { action: 'generate', prompt: 'x', seed: 7 }).ok).toBe(true)
    expect(validateVideoRequest(xai15, { action: 'generate', prompt: 'x', seed: 7 }).ok).toBe(false)
  })
})
