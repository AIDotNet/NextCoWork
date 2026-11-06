/**
 * 邀请海报生成器的单测。
 *
 * ★ 这里守的核心事实只有一条：**二维码里编的必须是那个用户的邀请链接**。
 * 编错了的症状是「朋友确实扫码注册了，但双方都没有奖励」，而且全程零报错 ——
 * 只有事后对账才发现，所以必须在这一层钉死。
 */
import { describe, expect, it } from 'vitest'
import {
  buildInvitePosterSvg,
  invitePosterFileName,
  POSTER_HEIGHT,
  POSTER_WIDTH,
  qrPathData,
  type InvitePosterCopy
} from '../invite-poster'

const copy: InvitePosterCopy = {
  eyebrow: '好东西，和朋友分享',
  titleLine1: '邀请好友，',
  titleLine2: '一起获得更多。',
  subtitle: '双方都能拿到免费额度',
  bullets: ['内置多模型', '一屏搞定', '跨设备同步'],
  scanTitle: '扫码注册',
  scanHint: '扫码即自动绑定邀请关系',
  codeLabel: '邀请码',
  tagline: '编码型 Agent 桌面端'
}

const input = {
  code: 'a1b2c3d4e5f6',
  inviteUrl: 'https://nextco.work/login?ref=a1b2c3d4e5f6',
  copy
}

describe('qrPathData', () => {
  it('encodes the exact text it was given, including the ref query', () => {
    // 同一段文字两次编码必须一致；不同链接必须给出不同的码
    const first = qrPathData(input.inviteUrl)
    const again = qrPathData(input.inviteUrl)
    const other = qrPathData('https://nextco.work/login?ref=ffffffffffff')
    expect(first.path).toBe(again.path)
    expect(first.path).not.toBe(other.path)
    expect(first.moduleCount).toBeGreaterThanOrEqual(21)
  })

  it('keeps every module inside the reserved square', () => {
    const rect = { x: 100, y: 200, size: 300 } as const
    const { path } = qrPathData(input.inviteUrl, rect)
    const xs = [...path.matchAll(/M([\d.]+) ([\d.]+)/g)].map((m) => [Number(m[1]), Number(m[2])] as const)
    expect(xs.length).toBeGreaterThan(50)
    for (const [x, y] of xs) {
      expect(x).toBeGreaterThanOrEqual(rect.x)
      expect(y).toBeGreaterThanOrEqual(rect.y)
      expect(x).toBeLessThanOrEqual(rect.x + rect.size)
      expect(y).toBeLessThanOrEqual(rect.y + rect.size)
    }
  })
})

describe('buildInvitePosterSvg', () => {
  it('produces a 16:9 svg carrying this user code and copy', () => {
    const svg = buildInvitePosterSvg(input)
    expect(svg.startsWith('<svg')).toBe(true)
    expect(svg).toContain(`width="${POSTER_WIDTH}" height="${POSTER_HEIGHT}"`)
    expect(svg).toContain(input.code)
    expect(svg).toContain(copy.titleLine2)
    expect(svg).toContain(copy.bullets[2]!)
  })

  it('never writes copy of its own — swapping the copy swaps every visible line', () => {
    // 生成器里一旦写死中文，切英文时海报还是中文，而界面是英文的
    const english = buildInvitePosterSvg({
      ...input,
      copy: { ...copy, titleLine1: 'Invite a friend,', titleLine2: 'gain more together.', eyebrow: 'Share something good', subtitle: 'Both of you get free credit', bullets: ['a', 'b', 'c'], scanTitle: 'Scan to join', scanHint: 'Scanning links the invite', codeLabel: 'Invite code', tagline: 'The coding agent desktop' }
    })
    expect(english).toContain('gain more together.')
    expect(english).not.toContain('一起获得更多。')
  })

  it('escapes xml so a code or copy with & cannot break the file', () => {
    const svg = buildInvitePosterSvg({ ...input, code: 'a&b<c', copy: { ...copy, subtitle: '5 < 6 & 7' } })
    expect(svg).toContain('a&amp;b&lt;c')
    expect(svg).not.toContain('a&b<c')
  })

  it('drops extra bullets instead of overflowing the layout', () => {
    const svg = buildInvitePosterSvg({ ...input, copy: { ...copy, bullets: ['一', '二', '三', '四'] } })
    expect(svg).toContain('>三<')
    expect(svg).not.toContain('>四<')
  })
})

describe('invitePosterFileName', () => {
  it('keeps the code in the name and strips anything a path cannot hold', () => {
    expect(invitePosterFileName('a1b2c3')).toBe('nextcowork-invite-a1b2c3.png')
    expect(invitePosterFileName('../etc/passwd')).toBe('nextcowork-invite-etcpasswd.png')
    expect(invitePosterFileName('')).toBe('nextcowork-invite.png')
  })
})
