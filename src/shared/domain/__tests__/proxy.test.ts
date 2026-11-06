/**
 * 代理那几个纯函数。
 *
 * 这一整块的失败模式只有一种,但它很糟:**界面显示「代理已开启」,请求却
 * 走了别的路**。规则串拼错、白名单把目标域名意外命中、半截配置静默退成系统代理 ——
 * 三种成因,同一个观察不到的后果。所以这里逐条钉。
 *
 * `migrateLegacyProxy` 的用例在 `settings.test.ts`(它的调用点在 `mergeSettings`,
 * 和合并语义绑在一起才看得出意义)。
 */
import { describe, expect, it } from 'vitest'
import type { ProxyEndpoint, ProxySettings } from '../proxy'
import {
  childProxyEnv,
  DEFAULT_PROXY,
  DIRECT_BYPASS,
  composeProxyUrl,
  isLocalNetworkHost,
  LOCAL_ONLY_BYPASS,
  normalizeBypassList,
  parseProxyEndpoint,
  parseResolvedProxy,
  proxyBypassRules,
  proxyConfigFor
} from '../proxy'

const p = (over: Partial<ProxySettings> = {}): ProxySettings => ({ ...DEFAULT_PROXY, ...over })

/** 一份填好了的手动代理,大多数用例在它上面改一个字段 */
const manual = (over: Partial<ProxySettings> = {}): ProxySettings =>
  p({ enabled: true, mode: 'manual', scheme: 'http', host: '127.0.0.1', port: 7890, ...over })

describe('composeProxyUrl', () => {
  it('三段拼成 url', () => {
    expect(composeProxyUrl({ scheme: 'socks5', host: '127.0.0.1', port: 1080 })).toBe(
      'socks5://127.0.0.1:1080'
    )
  })

  it('端口为 0 时省掉端口', () => {
    expect(composeProxyUrl({ scheme: 'http', host: 'proxy.corp', port: 0 })).toBe(
      'http://proxy.corp'
    )
  })

  /** 没填地址 = 没有代理,给空串让上层决定怎么处理,而不是拼出一个 `http://` */
  it('没填地址给空串', () => {
    expect(composeProxyUrl({ scheme: 'http', host: '', port: 7890 })).toBe('')
    expect(composeProxyUrl({ scheme: 'http', host: '   ', port: 7890 })).toBe('')
  })
})

describe('normalizeBypassList', () => {
  it('换行 / 逗号 / 分号混着填都认', () => {
    expect(normalizeBypassList('a.com\nb.com, c.com; d.com')).toEqual([
      'a.com',
      'b.com',
      'c.com',
      'd.com'
    ])
  })

  it('去空白、丢空行', () => {
    expect(normalizeBypassList('  a.com  \n\n\n  b.com  ')).toEqual(['a.com', 'b.com'])
    expect(normalizeBypassList('')).toEqual([])
    expect(normalizeBypassList('\n,;\n')).toEqual([])
  })

  /** 去重是为了让界面上那句「已添加 N 条」不骗人 */
  it('去重', () => {
    expect(normalizeBypassList('a.com\na.com\nb.com')).toEqual(['a.com', 'b.com'])
  })

  /** ★ 保序,不排序 —— 用户按自己的心智写的,重排会像「它把我写的弄乱了」 */
  it('保持用户写的顺序', () => {
    expect(normalizeBypassList('z.com\na.com\nm.com')).toEqual(['z.com', 'a.com', 'm.com'])
  })
})

describe('proxyBypassRules', () => {
  it('用户填的在前,内置表在后,分号连接', () => {
    const rules = proxyBypassRules(manual({ bypass: 'my.corp\ninternal.dev' })).split(';')
    expect(rules.slice(0, 2)).toEqual(['my.corp', 'internal.dev'])
    expect(rules.slice(2)).toEqual([...DIRECT_BYPASS])
  })

  it('用户没填时就是内置表本身', () => {
    expect(proxyBypassRules(manual()).split(';')).toEqual([...DIRECT_BYPASS])
  })

  /**
   * ★ 回环必须在表里。少了它,应用自己那个 `127.0.0.1:19836` 开放网关、
   * 以及本地起的 MCP http 服务器,都会被塞进代理绕一圈 —— 而多数代理
   * 根本不肯转发到回环地址,表现是「开了代理之后本地功能全坏了」。
   */
  it('内置表覆盖回环与私有网段', () => {
    for (const must of ['localhost', '127.0.0.1', '::1', '<local>', '192.168.0.0/16']) {
      expect(DIRECT_BYPASS).toContain(must)
    }
  })

  /** 界面上那句「已自动添加 N 个直连域名」的 N 从这里来,所以表不能有重复项 */
  it('内置表本身没有重复', () => {
    expect(new Set(DIRECT_BYPASS).size).toBe(DIRECT_BYPASS.length)
  })
})

describe('proxyConfigFor', () => {
  /**
   * ★★ **「关闭」= 跟随系统,不是强制直连。**
   *
   * `mode: 'direct'` 是一个主动要求(绕过操作系统那份设置),而 Chromium 在没人
   * 调 `setProxy` 时本来就跟随系统。以前这里返回 `direct`,等于把「用户没开这个
   * 开关」翻译成「用户要求无视系统代理」—— 开箱即用状态下每一个出站请求都绕过
   * 系统代理,实测撞出的是一个和代理毫无关联的 403 unsupported_country_region。
   */
  it('★★ 关掉 = 跟随系统（不是强制直连）', () => {
    expect(proxyConfigFor(p({ enabled: false }))).toEqual({ mode: 'system' })
  })

  /** ★ 关掉时必须显式给出配置而不是「什么都不给」—— 见 `applyProxy` 里的说明 */
  it('从手动改成关掉时不留下旧规则', () => {
    const cfg = proxyConfigFor(manual({ enabled: false }))
    expect(cfg).toEqual({ mode: 'system' })
    expect(cfg.proxyRules).toBeUndefined()
  })

  it('「开 + 跟随系统」和「关闭」同结果 —— 那个开关的含义是「我要自己指定代理」', () => {
    expect(proxyConfigFor(p({ enabled: true, mode: 'system' }))).toEqual(
      proxyConfigFor(p({ enabled: false }))
    )
  })

  it('跟随系统', () => {
    expect(proxyConfigFor(p({ enabled: true, mode: 'system' }))).toEqual({ mode: 'system' })
  })

  it('手动:一条规则对所有协议生效,并带上白名单', () => {
    const cfg = proxyConfigFor(manual({ bypass: 'my.corp' }))
    expect(cfg.proxyRules).toBe('http://127.0.0.1:7890')
    expect(cfg.proxyBypassRules?.startsWith('my.corp;')).toBe(true)
    expect(cfg.mode).toBeUndefined()
  })

  it('socks5 原样进规则串', () => {
    expect(proxyConfigFor(manual({ scheme: 'socks5', port: 1080 })).proxyRules).toBe(
      'socks5://127.0.0.1:1080'
    )
  })

  /**
   * ★ **「开了但地址没填完」走直连,不退回系统代理。**
   *
   * 注意这**不是**上面那条的反例:用户明确选了「手动」,此时静默退回系统代理,
   * 会让他在「我明明填了自定义代理」的同时走着另一条完全不同的线路,而界面上
   * 两种情况长得一模一样。半截配置就该是明显坏掉的,而不是悄悄换一条路。
   */
  it('开了但地址是空的 → 直连,而不是系统代理', () => {
    expect(proxyConfigFor(manual({ host: '' }))).toEqual({ mode: 'direct' })
  })
})

describe('parseProxyEndpoint', () => {
  /** ★ 用户十有八九只会打 `127.0.0.1:7890` —— 那正是各家代理软件显示的样子 */
  it('缺协议时补 http', () => {
    expect(parseProxyEndpoint('127.0.0.1:7890')).toEqual({
      scheme: 'http',
      host: '127.0.0.1',
      port: 7890
    })
  })

  it('带协议时照原样认', () => {
    expect(parseProxyEndpoint('socks5://10.0.0.1:1080')).toEqual({
      scheme: 'socks5',
      host: '10.0.0.1',
      port: 1080
    })
  })

  it('协议大小写不敏感', () => {
    expect(parseProxyEndpoint('SOCKS5://h:1')?.scheme).toBe('socks5')
  })

  it('不填端口就是 0,交给协议默认值', () => {
    expect(parseProxyEndpoint('proxy.corp')).toEqual({ scheme: 'http', host: 'proxy.corp', port: 0 })
  })

  it('认不出的协议直接判不合法', () => {
    expect(parseProxyEndpoint('ftp://h:21')).toBeNull()
    expect(parseProxyEndpoint('ws://h:80')).toBeNull()
  })

  /**
   * ★ 越界端口必须判不合法,**不能静默丢掉**。
   * `127.0.0.1:99999` 会被 `URL` 解析成「主机 127.0.0.1、没有端口」——
   * 于是用户填的 99999 消失了,代理连去了 80 端口,而界面上什么都没提示。
   */
  it('越界端口判不合法,而不是静默变成没有端口', () => {
    expect(parseProxyEndpoint('127.0.0.1:99999')).toBeNull()
    expect(parseProxyEndpoint('127.0.0.1:0')).toBeNull()
  })

  /**
   * ★ 协议默认端口**不能被当成「没写端口」丢掉**。
   *
   * `URL` 会把 `https://…:443` 规范化成 `port === ''`,和「越界端口被吞掉」
   * 长得一模一样。按后者处理的话,443/80 上的企业代理会被判成地址不合法,
   * 而用户填的完全正确 —— 这条用例就是那个 bug 的现场。
   */
  it('协议默认端口照样认', () => {
    expect(parseProxyEndpoint('https://a.b.c:443')).toEqual({
      scheme: 'https',
      host: 'a.b.c',
      port: 443
    })
    expect(parseProxyEndpoint('http://proxy.corp:80')?.port).toBe(80)
  })

  it('空串和纯空白是不合法', () => {
    expect(parseProxyEndpoint('')).toBeNull()
    expect(parseProxyEndpoint('   ')).toBeNull()
  })

  it('没有主机名的判不合法', () => {
    expect(parseProxyEndpoint('http://')).toBeNull()
  })

  /** IPv6 字面量的方括号要留着 —— 去掉之后拼回 url 就不合法了 */
  it('IPv6 字面量保留方括号', () => {
    const ep = parseProxyEndpoint('http://[::1]:8080')
    expect(ep?.host).toBe('[::1]')
    expect(ep?.port).toBe(8080)
    expect(composeProxyUrl({ scheme: 'http', host: ep!.host, port: ep!.port })).toBe(
      'http://[::1]:8080'
    )
  })

  /** 解析出来的三段拼回去,应当还是同一个地址 —— 表单「粘贴一行」那条路的往返 */
  it('解析再拼回去是同一个地址', () => {
    for (const raw of ['socks5://127.0.0.1:1080', 'http://proxy.corp:3128', 'https://a.b.c:443']) {
      expect(composeProxyUrl(parseProxyEndpoint(raw)!)).toBe(raw)
    }
  })
})

/**
 * Chromium `resolveProxy()` 的回程解析。
 *
 * 需求:ssh 子进程也要跟随同一份代理设置,而它只能靠这个回程知道该连哪儿
 * (`main/net/proxy.ts` 的 `resolveProxyForHost`)。这里的失败模式是**判成直连**:
 * 设置页一切正常,ssh 却绕过代理 —— 和文件头说的那个老 bug 是同一副面孔。
 */
describe('parseResolvedProxy', () => {
  it('DIRECT 就是直连', () => {
    expect(parseResolvedProxy('DIRECT')).toBeNull()
    expect(parseResolvedProxy('')).toBeNull()
  })

  /** PAC 的历史命名:`PROXY` 是 HTTP 代理,`SOCKS` 不带数字是 SOCKS4 */
  it('按 PAC 的命名认协议', () => {
    expect(parseResolvedProxy('PROXY 127.0.0.1:7890')).toEqual({ scheme: 'http', host: '127.0.0.1', port: 7890 })
    expect(parseResolvedProxy('HTTPS proxy.corp:443')).toEqual({ scheme: 'https', host: 'proxy.corp', port: 443 })
    expect(parseResolvedProxy('SOCKS5 127.0.0.1:1080')?.scheme).toBe('socks5')
    expect(parseResolvedProxy('SOCKS 127.0.0.1:1080')?.scheme).toBe('socks4')
  })

  /** 备选链只取第一个能用的:隧道一旦交给 ssh 就没有「换一个再来」的时机 */
  it('备选链取第一条,认不出的跳过', () => {
    expect(parseResolvedProxy('PROXY a.corp:3128;PROXY b.corp:3128;DIRECT')?.host).toBe('a.corp')
    expect(parseResolvedProxy('QUIC a.corp:443; PROXY b.corp:3128')?.host).toBe('b.corp')
  })

  /** PAC 脚本可以不写端口,而拨号必须有一个实数端口 */
  it('没写端口时按协议补默认端口', () => {
    expect(parseResolvedProxy('PROXY proxy.corp')?.port).toBe(80)
    expect(parseResolvedProxy('HTTPS proxy.corp')?.port).toBe(443)
    expect(parseResolvedProxy('SOCKS5 proxy.corp')?.port).toBe(1080)
  })
})

/**
 * `childProxyEnv` —— 代理端点翻成子进程环境变量(Agent 的 Bash / 后台 shell / 钩子用)。
 *
 * 失败模式和文件头说的是同一副面孔:**以为走了代理,其实没走**(变量拼错/被清空),
 * 以及反过来 **以为没动用户的配置,其实盖掉了**(父进程优先没守住)。
 */
describe('childProxyEnv', () => {
  const http = parseProxyEndpoint('http://127.0.0.1:7890') as ProxyEndpoint

  it('直连返回空对象 —— 不清空、不覆盖,维持继承来的现状', () => {
    expect(childProxyEnv(null, { HTTPS_PROXY: 'http://user-env:1' })).toEqual({})
  })

  it('http 代理补全大小写两套变量,NO_PROXY 默认是回环名单', () => {
    const env = childProxyEnv(http, {})
    expect(env).toEqual({
      HTTP_PROXY: 'http://127.0.0.1:7890',
      HTTPS_PROXY: 'http://127.0.0.1:7890',
      ALL_PROXY: 'http://127.0.0.1:7890',
      NO_PROXY: LOCAL_ONLY_BYPASS.join(','),
      http_proxy: 'http://127.0.0.1:7890',
      https_proxy: 'http://127.0.0.1:7890',
      all_proxy: 'http://127.0.0.1:7890',
      no_proxy: LOCAL_ONLY_BYPASS.join(',')
    })
  })

  /**
   * ★ socks 塞进 HTTP_PROXY 大多数工具不认,只会得到一句莫名其妙的报错;
   * ALL_PROXY 才是 socks 的正经载体,不认 socks 的工具自己会忽略它。
   */
  it('socks5 代理只发 ALL_PROXY 和 NO_PROXY,不发 HTTP_PROXY/HTTPS_PROXY', () => {
    const env = childProxyEnv(parseProxyEndpoint('socks5://127.0.0.1:1080') as ProxyEndpoint, {})
    expect(env).toEqual({
      ALL_PROXY: 'socks5://127.0.0.1:1080',
      NO_PROXY: LOCAL_ONLY_BYPASS.join(','),
      all_proxy: 'socks5://127.0.0.1:1080',
      no_proxy: LOCAL_ONLY_BYPASS.join(',')
    })
  })

  /**
   * ★★ 用户自己 export 过的永远赢 —— 那是他显式写的配置,盖掉它等于替他换了
   * 一条网络路径,而设置页上什么都看不出来。大小写不敏感:只定义了小写的
   * `https_proxy` 时,大写那一份也不发,免得两个变量打架(谁赢看工具心情)。
   */
  it('父进程已有的键不覆盖,大小写不敏感', () => {
    const env = childProxyEnv(http, { https_proxy: 'http://user-env:1', no_proxy: 'my.corp' })
    expect(env.HTTP_PROXY).toBe('http://127.0.0.1:7890')
    expect(env.HTTPS_PROXY).toBeUndefined()
    expect(env.https_proxy).toBeUndefined()
    expect(env.NO_PROXY).toBeUndefined()
    expect(env.no_proxy).toBeUndefined()
  })

  /** win32 环境变量不分大小写,小写副本会和大写撞名 */
  it('lowercase: false 时只发大写', () => {
    const env = childProxyEnv(http, {}, { lowercase: false })
    for (const name of Object.keys(env)) expect(name).toBe(name.toUpperCase())
  })

  it('显式 bypass 按序逗号连接(手动代理那条路把设置页的白名单原样带过来)', () => {
    const env = childProxyEnv(http, {}, { bypass: ['my.corp', ...DIRECT_BYPASS] })
    expect(env.NO_PROXY).toBe(['my.corp', ...DIRECT_BYPASS].join(','))
  })

  /** 端点缺端口时按协议默认值拼 url —— 与 composeProxyUrl 的约定一致 */
  it('端口为 0 的端点拼出无端口的 url', () => {
    const env = childProxyEnv({ scheme: 'http', host: 'proxy.corp', port: 0 }, {})
    expect(env.HTTP_PROXY).toBe('http://proxy.corp')
  })
})

/**
 * 需求:ssh 跟随系统代理之后,局域网里的机器必须仍然直连 —— 系统代理的排除列表
 * 默认不含私有网段,而代理软件对它们的 CONNECT 多半是拒绝的。
 */
describe('isLocalNetworkHost', () => {
  it('回环、私有网段、链路本地和 .local 都算本地', () => {
    for (const host of ['localhost', 'nas.local', '127.0.0.1', '10.1.2.3', '172.16.0.9', '172.31.255.1',
      '192.168.1.5', '169.254.1.1', '::1', '[fe80::1]', 'fd00::1']) {
      expect(isLocalNetworkHost(host), host).toBe(true)
    }
  })

  it('公网地址和普通域名不算', () => {
    for (const host of ['8.8.8.8', '172.32.0.1', '11.0.0.1', '192.169.1.1', 'example.com', 'my-alias']) {
      expect(isLocalNetworkHost(host), host).toBe(false)
    }
  })
})
