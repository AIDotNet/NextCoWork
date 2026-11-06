/**
 * GLM Coding Plan 额度响应的解析。
 *
 * ★ 字段语义来自 2026-09-29 对 ZCode.app 的逆向 + 真实账号实测(见源文件头),
 * 这里测的是**策略**:信封怎么判成功、窗口怎么挑、CREDIT_LIMIT 认不认、
 * 「没买套餐」和「读不懂」是不是两个结局。真实响应的原始样例贴在源文件头,
 * 拿到新的实测形状时先改那里再改这里。
 */
import { describe, expect, it } from 'vitest'
import { codingPlanQuotaErrorMessage, codingPlanQuotaRequest, parseCodingPlanQuota } from '../coding-plan-quota'

const NOW = 1_700_000_000_000

/** ZCode 渲染层挑选窗口用的那组 (type, unit, number) 原样照抄,别「顺手纠正」成看起来更合理的编号 */
const fiveHour = {
  type: 'TOKENS_LIMIT',
  unit: 3,
  number: 5,
  remaining: 8800,
  percentage: 12,
  nextResetTime: NOW + 3_600_000
}
const weekly = {
  type: 'TOKENS_LIMIT',
  unit: 6,
  number: 100,
  remaining: 2,
  percentage: 98,
  nextResetTime: NOW + 5 * 86_400_000
}
/** BigModel 真实账号实测返回的就是这个 type(积分制),ZCode 的 Bft 把它和 TOKENS_LIMIT 判成同类 */
const creditFiveHour = { ...fiveHour, type: 'CREDIT_LIMIT' }
/** ZCode 还有这条「工具月额度」,我们的快照里没有它的槽位 */
const monthlyTool = {
  type: 'TIME_LIMIT',
  unit: 5,
  number: 1,
  remaining: 97,
  percentage: 3,
  nextResetTime: NOW + 10 * 86_400_000
}

const envelope = (data: unknown): unknown => ({ success: true, code: 200, msg: '', data })

describe('parseCodingPlanQuota', () => {
  it('CREDIT_LIMIT(BigModel 积分制实测 type)照常解出 5 小时窗口', () => {
    const parsed = parseCodingPlanQuota(envelope({ limits: [creditFiveHour] }), NOW)
    expect(parsed).toEqual({
      kind: 'ok',
      snapshot: {
        primary: { usedPercent: 12, windowMinutes: 300, resetsAt: NOW + 3_600_000 },
        capturedAt: NOW
      }
    })
  })

  it('两个 token 型窗口(TOKENS_LIMIT + CREDIT_LIMIT)一起解出 primary 与 secondary', () => {
    const parsed = parseCodingPlanQuota(envelope({ limits: [creditFiveHour, weekly, monthlyTool] }), NOW)
    expect(parsed.kind).toBe('ok')
    if (parsed.kind !== 'ok') return
    expect(parsed.snapshot).toEqual({
      primary: { usedPercent: 12, windowMinutes: 300, resetsAt: NOW + 3_600_000 },
      secondary: { usedPercent: 98, windowMinutes: 10_080, resetsAt: NOW + 5 * 86_400_000 },
      capturedAt: NOW
    })
  })

  it('TIME_LIMIT(工具月额度)不进快照 —— 没有对应的窗口槽位,不硬塞', () => {
    expect(parseCodingPlanQuota(envelope({ limits: [monthlyTool] }), NOW).kind).toBe('no-quota')
  })

  it('code 为 0 / 200 / 缺席都算成功 —— 两家监控 API 混用这些值', () => {
    expect(parseCodingPlanQuota({ success: true, code: 0, data: { limits: [fiveHour] } }, NOW).kind).toBe('ok')
    expect(parseCodingPlanQuota({ success: true, code: 200, data: { limits: [fiveHour] } }, NOW).kind).toBe('ok')
    expect(parseCodingPlanQuota({ success: true, data: { limits: [fiveHour] } }, NOW).kind).toBe('ok')
  })

  it('★ success:false(401 信封)是 unrecognized,不是「没套餐」—— 401 该弹错,不该显示空态', () => {
    const parsed = parseCodingPlanQuota(
      { code: 1001, msg: 'Header中未收到Authorization参数', success: false },
      NOW
    )
    expect(parsed.kind).toBe('unrecognized')
  })

  it('percentage 缺席时用 remaining/number 反推已用百分比(number 就是那条的总量字段)', () => {
    const noPercentage = { ...fiveHour, percentage: undefined, remaining: 1 }
    const parsed = parseCodingPlanQuota(envelope({ limits: [noPercentage] }), NOW)
    expect(parsed.kind !== 'ok' ? null : parsed.snapshot.primary).toEqual({
      usedPercent: 80,
      windowMinutes: 300,
      resetsAt: NOW + 3_600_000
    })
  })

  it('缺 nextResetTime 的窗口整条不要;limits 在场却无可用时是 no-quota 且带诊断 detail', () => {
    const parsed = parseCodingPlanQuota(
      envelope({ limits: [{ type: 'CREDIT_LIMIT', unit: 3, number: 5, percentage: 10 }] }),
      NOW
    )
    expect(parsed.kind).toBe('no-quota')
    if (parsed.kind === 'no-quota') expect(parsed.detail).toContain('type=CREDIT_LIMIT')
  })

  it('★ limits 为空(data 合法)= no-quota,不是 unrecognized —— 未开通套餐是正常态', () => {
    expect(parseCodingPlanQuota(envelope({ limits: [] }), NOW).kind).toBe('no-quota')
    expect(parseCodingPlanQuota(envelope({ level: 'glm-coding-pro' }), NOW).kind).toBe('no-quota')
  })

  it('越界百分比夹紧到 0–100', () => {
    const entry = { ...fiveHour, percentage: 120 }
    const parsed = parseCodingPlanQuota(envelope({ limits: [entry] }), NOW)
    expect(parsed.kind === 'ok' ? parsed.snapshot.primary?.usedPercent : null).toBe(100)
  })

  it('响应不是对象 / 非 JSON 一律 unrecognized,绝不抛', () => {
    expect(parseCodingPlanQuota(null, NOW).kind).toBe('unrecognized')
    expect(parseCodingPlanQuota('nope', NOW).kind).toBe('unrecognized')
  })
})

describe('codingPlanQuotaRequest', () => {
  it('bigmodel 打主域,zai 打 api.z.ai;key 裸放 Authorization,不加 Bearer', () => {
    expect(codingPlanQuotaRequest('bigmodel', 'abc.def')).toEqual({
      url: 'https://bigmodel.cn/api/monitor/usage/quota/limit',
      headers: { authorization: 'abc.def' }
    })
    expect(codingPlanQuotaRequest('zai', 'k').url).toBe('https://api.z.ai/api/monitor/usage/quota/limit')
  })
})

describe('codingPlanQuotaErrorMessage', () => {
  it('401 译成人话,其余状态带上游原话片段', () => {
    expect(codingPlanQuotaErrorMessage(401, '')).toContain('订阅密钥')
    expect(codingPlanQuotaErrorMessage(500, '{"code":500,"msg":"oops"}')).toContain('oops')
    expect(codingPlanQuotaErrorMessage(503, '')).toContain('503')
  })
})
