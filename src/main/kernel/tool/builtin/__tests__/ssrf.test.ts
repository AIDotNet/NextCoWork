import { describe, expect, it } from 'vitest'
import { isPrivateAddress, resolvedAddressRisk, ssrfRisk } from '../ssrf'

/**
 * SSRF 筛查的边界表。纯函数,零 IO,所以能铺满。
 *
 * ★ 这一组里最重要的不是「127.0.0.1 被拦下」,而是**那一堆等价写法**也被拦下:
 * `2130706433`、`0177.0.0.1`、`::ffff:127.0.0.1` 全都是同一台机器。
 * 只按字符串比对的实现在这三行上会全绿地放行。
 */

/** 走一遍 `new URL()`,因为线上真正喂给 `ssrfRisk` 的就是它的输出 */
function risk(raw: string): string | null {
  return ssrfRisk(new URL(raw))
}

describe('isPrivateAddress · IPv4', () => {
  const PRIVATE = [
    '0.0.0.0',
    '0.1.2.3', // 0.0.0.0/8 整段
    '10.0.0.1',
    '10.255.255.255',
    '127.0.0.1',
    '127.1.2.3',
    '100.64.0.1', // 运营商级 NAT
    '100.127.255.255',
    '169.254.169.254', // ★ 云元数据端点 —— SSRF 最值钱的目标
    '169.254.0.1',
    '172.16.0.1',
    '172.31.255.255',
    '192.0.0.1',
    '192.168.1.1',
    '198.18.0.1',
    '198.19.255.255',
    '224.0.0.1', // 组播
    '255.255.255.255'
  ]
  for (const ip of PRIVATE) {
    it(`拦下 ${ip}`, () => {
      expect(isPrivateAddress(ip)).toBe(true)
    })
  }

  const PUBLIC = [
    '1.1.1.1',
    '8.8.8.8',
    '93.184.216.34',
    '11.0.0.1', // 10/8 的邻居,不是私网
    '100.63.255.255', // 100.64/10 的下边界外
    '100.128.0.1', // 上边界外
    '169.253.0.1', // 169.254/16 的邻居
    '172.15.255.255', // 172.16/12 的下边界外
    '172.32.0.1', // 上边界外
    '192.0.1.1', // 192.0.0/24 的邻居
    '192.167.1.1',
    '192.169.1.1',
    '198.17.0.1',
    '198.20.0.1',
    '223.255.255.255' // 组播段的下边界外
  ]
  for (const ip of PUBLIC) {
    it(`放行 ${ip}`, () => {
      expect(isPrivateAddress(ip)).toBe(false)
    })
  }
})

describe('isPrivateAddress · IPv6', () => {
  const PRIVATE = [
    '::',
    '::1',
    '0:0:0:0:0:0:0:1',
    '[::1]', // hostname 带方括号
    'fc00::1', // 唯一本地
    'fdff:ffff::1',
    'fe80::1', // 链路本地
    'febf::1',
    'ff02::1', // 组播
    '::ffff:127.0.0.1', // ★ IPv4 映射
    '::ffff:7f00:1', // 同上,URL 规范化后的形式
    '::ffff:169.254.169.254',
    '::ffff:a9fe:a9fe'
  ]
  for (const ip of PRIVATE) {
    it(`拦下 ${ip}`, () => {
      expect(isPrivateAddress(ip)).toBe(true)
    })
  }

  const PUBLIC = ['2001:4860:4860::8888', '2606:4700::1111', '::ffff:8.8.8.8', 'fec0::1']
  for (const ip of PUBLIC) {
    it(`放行 ${ip}`, () => {
      expect(isPrivateAddress(ip)).toBe(false)
    })
  }

  it('不是地址的东西一律返回 false —— 域名交给调用方另判', () => {
    for (const s of ['example.com', 'localhost', '', 'not:an:address:at:all:x:y:z', '1.2.3', '1.2.3.4.5', '256.1.1.1', 'g::1']) {
      expect(isPrivateAddress(s), s).toBe(false)
    }
  })
})

/**
 * ★ 这一组是全套里最容易被误删的:看起来它测的是 `URL` 的行为而不是我们的代码。
 * 但它测的恰恰是「我们凭什么可以只看 `url.hostname`」这个前提 ——
 * 前提哪天不成立了(换了 URL 实现、自己拼 host),这里会先红。
 */
describe('★ URL 会把 IPv4 的花式写法规范化掉,所以字面量筛查是够的', () => {
  const SAME: Array<[string, string]> = [
    ['http://2130706433/', '127.0.0.1'],
    ['http://0177.0.0.1/', '127.0.0.1'],
    ['http://0x7f.1/', '127.0.0.1'],
    ['http://127.1/', '127.0.0.1'],
    ['http://0x7f000001/', '127.0.0.1'],
    ['http://2852039166/', '169.254.169.254']
  ]
  for (const [raw, want] of SAME) {
    it(`${raw} 规范化成 ${want},并被拒`, () => {
      expect(new URL(raw).hostname).toBe(want)
      expect(risk(raw)).not.toBeNull()
    })
  }

  it('IPv4 映射的 IPv6 规范化成十六进制形式,仍然被拒', () => {
    expect(new URL('http://[::ffff:127.0.0.1]/').hostname).toBe('[::ffff:7f00:1]')
    expect(risk('http://[::ffff:127.0.0.1]/')).not.toBeNull()
  })
})

describe('ssrfRisk · 协议', () => {
  it('file:// 被拒,并指路到 Read', () => {
    const r = risk('file:///etc/passwd')
    expect(r).not.toBeNull()
    expect(r).toContain('Read')
  })

  for (const raw of ['ftp://example.com/x', 'gopher://example.com/', 'data:text/html,hi', 'javascript:alert(1)']) {
    it(`拒绝 ${raw.split(':')[0] ?? ''}:`, () => {
      expect(risk(raw)).not.toBeNull()
    })
  }

  it('http 和 https 通过', () => {
    expect(risk('http://example.com/')).toBeNull()
    expect(risk('https://example.com/')).toBeNull()
  })
})

describe('ssrfRisk · URL 里的凭证', () => {
  it('★ user:pass@ 被拒 —— 那是一条模型可以自己发起的外发通道', () => {
    const r = risk('https://user:pass@example.com/')
    expect(r).not.toBeNull()
    expect(r).toContain('user:pass@')
  })

  it('只有用户名也拒', () => {
    expect(risk('https://user@example.com/')).not.toBeNull()
  })

  it('★ 拒绝信息里不回显密码本身', () => {
    const r = risk('https://alice:hunter2@example.com/')
    expect(r).not.toBeNull()
    expect(r).not.toContain('hunter2')
  })
})

describe('ssrfRisk · 主机名', () => {
  const BLOCKED = [
    'http://localhost/',
    'http://LOCALHOST/',
    'http://api.localhost/',
    'http://printer.local/',
    'http://metadata.internal/',
    'http://wiki.intranet/',
    'http://x.home.arpa/',
    'http://nas.lan/',
    'http://router/', // ★ 单标签主机名 —— 只可能靠内网搜索域解析
    'http://nas/',
    'http://127.0.0.1:8080/',
    'http://[::1]:5432/',
    'http://192.168.1.1/',
    'http://169.254.169.254/latest/meta-data/iam/security-credentials/'
  ]
  for (const raw of BLOCKED) {
    it(`拒绝 ${raw}`, () => {
      expect(risk(raw), raw).not.toBeNull()
    })
  }

  const ALLOWED = [
    'https://example.com/',
    'https://api.github.com/repos/x/y',
    'https://sub.domain.example.co.uk/a?b=c#d',
    'https://8.8.8.8/',
    'https://xn--fsq.com/', // punycode
    'https://localhost.example.com/', // 以 localhost 开头但不是它
    'https://mylocal/x'.replace('mylocal', 'my.local.example.com')
  ]
  for (const raw of ALLOWED) {
    it(`放行 ${raw}`, () => {
      expect(risk(raw), raw).toBeNull()
    })
  }

  it('端口不影响判断 —— 公网域名的任意端口都放行', () => {
    expect(risk('https://example.com:8443/')).toBeNull()
  })

  it('★ 拒绝本机时要说清楚为什么,并给一条出路', () => {
    const r = risk('http://127.0.0.1:3000/')
    expect(r).not.toBeNull()
    expect(r).toContain('127.0.0.1')
    expect(r).toContain('private-network')
    expect(r).toContain('user')
  })
})

describe('resolvedAddressRisk · DNS 解析结果', () => {
  it('拒绝解析到环回或私网地址的公网域名', async () => {
    const lookup = async (): Promise<Array<{ address: string }>> => [
      { address: '93.184.216.34' },
      { address: '127.0.0.1' }
    ]

    const result = await resolvedAddressRisk('public.example.com', 100, lookup)

    expect(result).toContain('127.0.0.1')
    expect(result).toContain('private-network')
  })

  it('公网解析结果通过', async () => {
    const lookup = async (): Promise<Array<{ address: string }>> => [
      { address: '93.184.216.34' },
      { address: '2606:2800:220:1:248:1893:25c8:1946' }
    ]

    await expect(resolvedAddressRisk('example.com', 100, lookup)).resolves.toBeNull()
  })
})

/**
 * `allowPrivateAddresses` 是给 `browser.ts` / `web.ts` 用的开关(工作区所有者
 * 2026-09-20 明确要求关掉这两个工具的私网地址拦截)。这里要验证的不是「它能
 * 放行内网」——那是需求本身——而是「它只放行内网这一件事,协议白名单和凭证
 * 拦截不受影响」。少了这组测试,以后有人往 `allowPrivateAddresses` 分支里
 * 塞进别的豁免,不会有任何测试变红。
 */
describe('ssrfRisk · allowPrivateAddresses 选项', () => {
  it('放行本机和内网地址', () => {
    expect(risk('http://127.0.0.1:3000/')).not.toBeNull() // 默认仍然拦
    expect(ssrfRisk(new URL('http://127.0.0.1:3000/'), { allowPrivateAddresses: true })).toBeNull()
    expect(ssrfRisk(new URL('http://192.168.1.1/'), { allowPrivateAddresses: true })).toBeNull()
    expect(ssrfRisk(new URL('http://169.254.169.254/latest/meta-data/'), { allowPrivateAddresses: true })).toBeNull()
    expect(ssrfRisk(new URL('http://localhost/'), { allowPrivateAddresses: true })).toBeNull()
  })

  it('★ 协议白名单不受这个选项影响 —— file:// 仍然被拒', () => {
    const r = ssrfRisk(new URL('file:///etc/passwd'), { allowPrivateAddresses: true })
    expect(r).not.toBeNull()
    expect(r).toContain('Read')
  })

  it('★ URL 里的凭证不受这个选项影响 —— user:pass@ 仍然被拒', () => {
    const r = ssrfRisk(new URL('https://user:pass@127.0.0.1/'), { allowPrivateAddresses: true })
    expect(r).not.toBeNull()
    expect(r).toContain('user:pass@')
  })

  it('不传选项时行为和之前完全一样', () => {
    expect(ssrfRisk(new URL('http://127.0.0.1/'))).not.toBeNull()
  })
})

/**
 * `allowFileUrls` 是给 `browser.ts` 用的（工作区所有者 2026-09-22 要求本地
 * HTML 报告能进右侧工作台看）。要钉住的同样是「它只放行 file:// 这一件事」：
 * 其余协议、凭证拦截都不受影响。少了这组，以后有人往这个分支里塞别的豁免
 * 不会有任何测试变红 —— 理由同上面 allowPrivateAddresses 那组。
 */
describe('ssrfRisk · allowFileUrls 选项', () => {
  it('放行 file:// —— 浏览器工具要用它打开本地页面', () => {
    expect(ssrfRisk(new URL('file:///tmp/report.html'), { allowFileUrls: true })).toBeNull()
    expect(ssrfRisk(new URL('file:///tmp/report.html'), { allowPrivateAddresses: true, allowFileUrls: true })).toBeNull()
  })

  it('★ 不传时 file:// 仍被拒并指路到 Read —— 这个豁免只属于浏览器工具', () => {
    const r = ssrfRisk(new URL('file:///etc/passwd'), { allowPrivateAddresses: true })
    expect(r).not.toBeNull()
    expect(r).toContain('Read')
    expect(ssrfRisk(new URL('file:///etc/passwd'))).not.toBeNull()
  })

  it('★ 只多放行 file: —— 其余非 http(s) 协议即使传了选项也拒', () => {
    for (const raw of ['ftp://example.com/x', 'gopher://example.com/', 'data:text/html,hi', 'javascript:alert(1)']) {
      expect(ssrfRisk(new URL(raw), { allowFileUrls: true }), raw).not.toBeNull()
    }
  })

  it('★ 凭证拦截不受这个选项影响 —— 带 user:pass@ 的地址照样拒', () => {
    // 注意：`file://user:pass@…` 连 URL 构造都过不了（WHATWG 直接抛），
    // 所以凭证这条边界只能用 http(s) 地址来钉 —— 它和 allowFileUrls 是两道
    // 独立的闸，传了协议豁免也不能把凭证拦截一起带松。
    const r = ssrfRisk(new URL('https://user:pass@example.com/'), { allowFileUrls: true })
    expect(r).not.toBeNull()
    expect(r).toContain('user:pass@')
  })

  it('内网拦截也不受这个选项影响 —— 只传 allowFileUrls 时 http 内网地址仍拒', () => {
    expect(ssrfRisk(new URL('http://127.0.0.1/'), { allowFileUrls: true })).not.toBeNull()
  })
})
