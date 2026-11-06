/**
 * Coding Plan 额度归属的判定。
 *
 * ★ 这几个函数被主进程(IPC 拒绝与否)和渲染层(画不画刷新按钮/额度区块)
 * 同时依赖 —— 两边判据分叉的症状是「按钮画出来了,点了却报不支持」。
 * 测试钉住预设 id / issuer 与 family 的对应关系:预设表改了名字,
 * 这里先红,而不是用户先发现。
 */
import { describe, expect, it } from 'vitest'
import { codingPlanFamilyFor, codingPlanFamilyForIssuer, codingPlanQuotaUrl } from '../domain/coding-plan'

describe('codingPlanFamilyFor', () => {
  it('两张订阅制预设分别归 bigmodel / zai', () => {
    expect(codingPlanFamilyFor('zhipu-coding')).toBe('bigmodel')
    expect(codingPlanFamilyFor('zai-coding')).toBe('zai')
  })

  it('按量预设与未知 id 返回 null —— 界面据此不画刷新按钮,而不是点了等 401', () => {
    expect(codingPlanFamilyFor('zhipu')).toBeNull()
    expect(codingPlanFamilyFor('zai')).toBeNull()
    expect(codingPlanFamilyFor('custom-not-a-preset')).toBeNull()
  })
})

describe('codingPlanFamilyForIssuer', () => {
  it('穷尽表:两张 zcode issuer 有归属,其余恒 null', () => {
    expect(codingPlanFamilyForIssuer('zcode-bigmodel')).toBe('bigmodel')
    expect(codingPlanFamilyForIssuer('zcode-zai')).toBe('zai')
    expect(codingPlanFamilyForIssuer('chatgpt')).toBeNull()
    expect(codingPlanFamilyForIssuer('kimi-code')).toBeNull()
  })
})

describe('codingPlanQuotaUrl', () => {
  it('额度在各自主域上,不在 AI 端点域(open.bigmodel.cn)上', () => {
    expect(codingPlanQuotaUrl('bigmodel')).toBe('https://bigmodel.cn/api/monitor/usage/quota/limit')
    expect(codingPlanQuotaUrl('zai')).toBe('https://api.z.ai/api/monitor/usage/quota/limit')
  })
})
