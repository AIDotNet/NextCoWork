/**
 * 状态行压力条的分母切换 —— 钉住的是「开了 1M 之后那句催压缩的话会不会自己消失」。
 *
 * 这组用例存在的理由是一个具体的现场:圆环写着 21%(分母已是 1M),同一行左边
 * 却挂着「接近上限,可 /compact」和「已无可折叠的历史,请开启摘要压缩或另起会话」
 * (分母还是上一轮的 272K)。分母两边必须同源,否则界面在催用户做一件他刚做完的事。
 */
import { describe, expect, it } from 'vitest'
import { DEFAULT_MAX_OUTPUT_TOKENS } from '../../../../../shared/agent/run-request'
import { contextPressure } from '../context-pressure'

/** 272K 下判过该压缩的一轮:用掉 230K,窗口 272K。 */
const tight = { used: 230_000, window: 272_000, shouldCompact: true }
const maxOutputTokens = DEFAULT_MAX_OUTPUT_TOKENS

describe('contextPressure', () => {
  it('没有上一轮占用时什么都不画 —— 那不是 0%', () => {
    expect(contextPressure(undefined)).toBeUndefined()
    expect(contextPressure(undefined, { window: 1_050_000, maxOutputTokens })).toBeUndefined()
  })

  it('拿不到本地窗口时逐字采信主进程的结论', () => {
    const pressure = contextPressure(tight)
    expect(pressure).toEqual({ ratio: 230_000 / 272_000, nearLimit: true, rescaled: false })
  })

  it('打开最大上下文后按新窗口重算,那句催压缩的话当帧消失', () => {
    const pressure = contextPressure(tight, { window: 1_050_000, maxOutputTokens })
    expect(pressure?.nearLimit).toBe(false)
    expect(pressure?.rescaled).toBe(true)
    expect(pressure?.ratio).toBeCloseTo(230_000 / 1_050_000, 6)
  })

  it('窗口没变时不撤销主进程的判断 —— 那一次读的是校准后的数,比这里准', () => {
    const pressure = contextPressure(tight, { window: 272_000, maxOutputTokens })
    expect(pressure?.nearLimit).toBe(true)
    expect(pressure?.rescaled).toBe(false)
  })

  /*
    ★ 两条用例的占用都按新阈值重标过:阈值 = 窗口 − min(maxOut, 20K) − 13K。
    272K 窗口下是 239K(旧公式是 272K×0.8 − 预留),1.05M 窗口下是 1.017M。
    要的是「刚好越过 / 刚好没越过」,不是某个写死的字面量。
  */
  it('关掉最大上下文把窗口收回来时,反过来能重新判出接近上限', () => {
    const roomy = { used: 250_000, window: 1_050_000, shouldCompact: false }
    expect(contextPressure(roomy, { window: 272_000, maxOutputTokens })?.nearLimit).toBe(true)
    expect(contextPressure(roomy)?.nearLimit).toBe(false)
  })

  it('放开窗口但占用真的还是太大时,警告留着', () => {
    const huge = { used: 1_030_000, window: 272_000, shouldCompact: true }
    expect(contextPressure(huge, { window: 1_050_000, maxOutputTokens })?.nearLimit).toBe(true)
  })

  it('比例夹在 0..1,越界的历史读数不会把条子画出格', () => {
    const over = { used: 400_000, window: 272_000, shouldCompact: true }
    expect(contextPressure(over)?.ratio).toBe(1)
  })
})
