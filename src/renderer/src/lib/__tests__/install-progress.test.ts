import { describe, expect, it } from 'vitest'
import type { Translate } from '../../i18n'
import { installLabel, installRatio, type InstallProgress } from '../install-progress'

/** 把 key 和参数原样吐出来 —— 这里要测的是「选了哪条文案」,不是文案本身 */
const t: Translate = (key, params = {}) =>
  Object.keys(params).length === 0
    ? key
    : `${key}:${Object.values(params).map(String).join(',')}`

function progress(patch: Partial<InstallProgress>): InstallProgress {
  return { phase: 'downloading', startedAt: 0, ...patch }
}

describe('installRatio', () => {
  it('报得出百分比时给比例', () => {
    expect(installRatio(progress({ received: 512, total: 2048 }))).toBe(0.25)
  })

  // 服务端不回 Content-Length 时 total 就是 undefined。这时候必须是 null 不是 0 ——
  // 0 会画出一条空槽,而那是一句谎话:东西其实正在下。
  it('拿不到 total 时给 null,而不是 0', () => {
    expect(installRatio(progress({ received: 512 }))).toBeNull()
    expect(installRatio(progress({ received: 512, total: 0 }))).toBeNull()
  })

  it('授权与解压两段没有百分比', () => {
    expect(installRatio(progress({ phase: 'preparing' }))).toBeNull()
    expect(installRatio(progress({ phase: 'installing' }))).toBeNull()
  })

  // 主进程按 chunk 累加,末尾那一帧可能比 Content-Length 多出几个字节。
  // 不封顶的话进度条会画到 103%。
  it('超出 total 也不越过 1', () => {
    expect(installRatio(progress({ received: 2100, total: 2048 }))).toBe(1)
  })
})

describe('installLabel', () => {
  it('每一段各报各的文案', () => {
    expect(installLabel(progress({ phase: 'preparing' }), t)).toBe('plugins.preparing')
    expect(installLabel(progress({ phase: 'installing' }), t)).toBe('plugins.installing')
    expect(installLabel(progress({ received: 512 }), t)).toBe('plugins.downloading')
  })

  it('有比例时报整数百分比', () => {
    expect(installLabel(progress({ received: 640, total: 2048 }), t)).toBe('plugins.downloadingPercent:31')
  })

  /*
    Skill 那边的文案是同样的中文、另一套 key。测它是因为「共用这段逻辑」和
    「共用这几句文案」是两件事:插件的措辞要改的时候,Skill 这一侧不该跟着变。
  */
  it('换个命名空间就报另一套 key', () => {
    expect(installLabel(progress({ phase: 'preparing' }), t, 'skills')).toBe('skills.preparing')
    expect(installLabel(progress({ phase: 'installing' }), t, 'skills')).toBe('skills.installing')
    expect(installLabel(progress({ received: 512 }), t, 'skills')).toBe('skills.downloading')
    expect(installLabel(progress({ received: 640, total: 2048 }), t, 'skills')).toBe('skills.downloadingPercent:31')
  })
})
